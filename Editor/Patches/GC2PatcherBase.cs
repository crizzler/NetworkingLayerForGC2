using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace Arawn.EnemyMasses.Editor.Integration.GameCreator2.Patches
{
    /// <summary>
    /// Base class for all GC2 module patchers.
    /// Provides common functionality for patching, unpatching, and backup management.
    /// </summary>
    public abstract class GC2PatcherBase
    {
        // ════════════════════════════════════════════════════════════════════════════════════════
        // CONSTANTS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        protected const string PATCH_MARKER_PREFIX = "// [GC2_NETWORK_PATCH_";
        protected const string BACKUP_BASE_FOLDER = "../Library/GameCreator2NetworkingLayer/Patches/Backups";
        protected const string LEGACY_BACKUP_BASE_FOLDER = "Plugins/EnemyMasses/Editor/GameCreator2/Patches/Backups";
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // ABSTRACT MEMBERS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>Module name (e.g., "Core", "Stats", "Inventory").</summary>
        public abstract string ModuleName { get; }
        
        /// <summary>Patch version string.</summary>
        public abstract string PatchVersion { get; }
        
        /// <summary>Relative path from Assets folder to the main file to patch.</summary>
        protected abstract string[] FilesToPatch { get; }
        
        /// <summary>Display name for menu and dialogs.</summary>
        public abstract string DisplayName { get; }
        
        /// <summary>Description of what the patch does.</summary>
        public abstract string PatchDescription { get; }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // PROPERTIES
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        private string PatchVersionToken => BuildPatchVersionToken(PatchVersion);
        protected string PatchMarker => $"{PATCH_MARKER_PREFIX}{ModuleName}_{PatchVersionToken}]";
        protected string LegacyPatchMarker => $"{PATCH_MARKER_PREFIX}{ModuleName}_v1]";
        protected string BackupFolder => Path.Combine(BACKUP_BASE_FOLDER, ModuleName);
        protected string LegacyBackupFolder => Path.Combine(LEGACY_BACKUP_BASE_FOLDER, ModuleName);
        protected virtual bool AllowLegacyBackupRestore => true;

        protected enum ExistingPatchState
        {
            Continue,
            SkipAlreadyPatched,
            Failed
        }

        private readonly struct TextLineSpan
        {
            public readonly int Start;
            public readonly int Length;

            public TextLineSpan(int start, int length)
            {
                Start = start;
                Length = length;
            }
        }

        protected readonly struct SourceBlockSpan
        {
            public readonly int DeclarationStart;
            public readonly int OpenBrace;
            public readonly int BodyStart;
            public readonly int BodyEnd;
            public readonly int CloseBrace;

            public int BodyLength => BodyEnd - BodyStart;

            public SourceBlockSpan(
                int declarationStart,
                int openBrace,
                int bodyStart,
                int bodyEnd,
                int closeBrace)
            {
                DeclarationStart = declarationStart;
                OpenBrace = openBrace;
                BodyStart = bodyStart;
                BodyEnd = bodyEnd;
                CloseBrace = closeBrace;
            }
        }

        protected readonly struct VersionCompatibilityRequirement
        {
            public readonly string VersionFileRelativePath;
            public readonly string[] SupportedVersionPatterns;

            public VersionCompatibilityRequirement(string versionFileRelativePath, params string[] supportedVersionPatterns)
            {
                VersionFileRelativePath = versionFileRelativePath;
                SupportedVersionPatterns = supportedVersionPatterns ?? Array.Empty<string>();
            }
        }

        protected static VersionCompatibilityRequirement VersionRequirement(
            string versionFileRelativePath,
            params string[] supportedVersionPatterns)
        {
            return new VersionCompatibilityRequirement(versionFileRelativePath, supportedVersionPatterns);
        }

        protected static bool TryReplaceWithFlexibleWhitespace(ref string content, string originalSnippet, string replacementSnippet)
        {
            if (string.IsNullOrEmpty(originalSnippet)) return false;

            if (content.Contains(originalSnippet))
            {
                content = content.Replace(originalSnippet, replacementSnippet);
                return true;
            }

            string pattern = BuildFlexibleSnippetRegex(NormalizeLineEndings(originalSnippet));

            try
            {
                Match match = Regex.Match(
                    content,
                    pattern,
                    RegexOptions.CultureInvariant | RegexOptions.Singleline,
                    TimeSpan.FromMilliseconds(250));

                if (!match.Success)
                {
                    return TryReplaceWithFuzzyLineWindow(ref content, originalSnippet, replacementSnippet);
                }

                content = content.Remove(match.Index, match.Length).Insert(match.Index, replacementSnippet);
                return true;
            }
            catch (RegexMatchTimeoutException)
            {
                Debug.LogWarning(
                    "[GC2 Networking] Flexible whitespace matcher timed out. " +
                    "Skipping this replacement to avoid editor stall.");
                return false;
            }
            catch (ArgumentException ex)
            {
                Debug.LogError($"[GC2 Networking] Invalid flexible-match regex generated: {ex.Message}");
                return false;
            }
        }

        protected bool ContainsPatchMarker(string content)
        {
            if (string.IsNullOrEmpty(content)) return false;
            if (content.Contains(PatchMarker, StringComparison.Ordinal) ||
                content.Contains(LegacyPatchMarker, StringComparison.Ordinal))
            {
                return true;
            }

            // Accept any versioned marker for this module so older patched projects
            // are still recognized when patch version strings evolve.
            string moduleMarkerPrefix = $"{PATCH_MARKER_PREFIX}{ModuleName}_";
            return content.Contains(moduleMarkerPrefix, StringComparison.Ordinal);
        }

        protected bool TryReplaceRequired(
            ref string content,
            string originalSnippet,
            string replacementSnippet,
            string errorMessage)
        {
            if (TryReplaceWithFlexibleWhitespace(ref content, originalSnippet, replacementSnippet))
            {
                return true;
            }

            Debug.LogError(errorMessage);
            return false;
        }

        protected ExistingPatchState PrepareContentForPatch(string relativePath, ref string content)
        {
            if (!ContainsPatchMarker(content))
            {
                return ExistingPatchState.Continue;
            }

            if (VerifyPatchedFile(relativePath, content, out _))
            {
                Debug.LogWarning($"[GC2 Networking] {relativePath} is already patched and valid.");
                return ExistingPatchState.SkipAlreadyPatched;
            }

            Debug.LogWarning(
                $"[GC2 Networking] {relativePath} contains stale/legacy patch markers. " +
                "Attempting in-place migration cleanup before patching.");

            if (!TryStripLegacyPatchArtifacts(ref content, out string failureReason))
            {
                Debug.LogError(
                    $"[GC2 Networking] Could not migrate stale patch content in {relativePath}: {failureReason}");
                return ExistingPatchState.Failed;
            }

            if (ContainsPatchMarker(content))
            {
                Debug.LogError(
                    $"[GC2 Networking] Migration cleanup for {relativePath} left patch markers behind.");
                return ExistingPatchState.Failed;
            }

            return ExistingPatchState.Continue;
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // PUBLIC API
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Check if the module files exist.
        /// </summary>
        public bool ValidateFilesExist()
        {
            foreach (var relativePath in FilesToPatch)
            {
                string fullPath = Path.Combine(Application.dataPath, relativePath);
                if (!File.Exists(fullPath))
                {
                    return false;
                }
            }
            return true;
        }

        public bool TryValidateVersionCompatibility(out string compatibilityMessage)
        {
            compatibilityMessage = null;
            VersionCompatibilityRequirement[] requirements = GetVersionCompatibilityRequirements();
            if (requirements == null || requirements.Length == 0)
            {
                return true;
            }

            for (int i = 0; i < requirements.Length; i++)
            {
                VersionCompatibilityRequirement requirement = requirements[i];
                if (string.IsNullOrWhiteSpace(requirement.VersionFileRelativePath))
                {
                    continue;
                }

                string fullPath = Path.Combine(Application.dataPath, requirement.VersionFileRelativePath);
                if (!File.Exists(fullPath))
                {
                    compatibilityMessage =
                        $"Expected version file missing: {requirement.VersionFileRelativePath}";
                    return false;
                }

                string version = File.ReadAllText(fullPath).Trim();
                if (version.Length == 0)
                {
                    compatibilityMessage =
                        $"Could not read version from {requirement.VersionFileRelativePath}";
                    return false;
                }

                if (!IsVersionSupported(version, requirement.SupportedVersionPatterns))
                {
                    compatibilityMessage =
                        $"Detected version {version} at {requirement.VersionFileRelativePath}, " +
                        $"but supported versions are: {string.Join(", ", requirement.SupportedVersionPatterns)}";
                    return false;
                }
            }

            return true;
        }
        
        /// <summary>
        /// Check if the module is already patched.
        /// </summary>
        public bool IsPatched()
        {
            if (FilesToPatch.Length == 0) return false;

            bool hasRequiredPatchedFile = false;

            foreach (var relativePath in FilesToPatch)
            {
                if (!ShouldRequirePatchMarker(relativePath)) continue;

                string fullPath = Path.Combine(Application.dataPath, relativePath);
                if (!File.Exists(fullPath)) return false;

                string content = NormalizeLineEndings(File.ReadAllText(fullPath));
                if (!ContainsPatchMarker(content))
                {
                    return false;
                }

                hasRequiredPatchedFile = true;
                if (!VerifyPatchedFile(relativePath, content, out _))
                {
                    return false;
                }
            }

            return hasRequiredPatchedFile;
        }
        
        /// <summary>
        /// Check if backups exist.
        /// </summary>
        public bool HasBackups()
        {
            foreach (string backupFolder in EnumerateBackupFoldersForRestore())
            {
                if (HasAllBackups(backupFolder))
                {
                    return true;
                }
            }

            return false;
        }
        
        /// <summary>
        /// Apply the patch.
        /// </summary>
        public bool ApplyPatch()
        {
            bool assetEditingStarted = false;
            bool reloadAssembliesLocked = false;
            try
            {
                EditorApplication.LockReloadAssemblies();
                reloadAssembliesLocked = true;

                AssetDatabase.StartAssetEditing();
                assetEditingStarted = true;

                EditorUtility.DisplayProgressBar(
                    "GC2 Networking Patch",
                    $"Backing up {DisplayName}...",
                    0f);

                // Create backups first
                CreateBackups();
                
                // Apply patches to each file
                for (int i = 0; i < FilesToPatch.Length; i++)
                {
                    string relativePath = FilesToPatch[i];
                    float progress = FilesToPatch.Length > 0 ? (float) i / FilesToPatch.Length : 1f;

                    if (EditorUtility.DisplayCancelableProgressBar(
                            "GC2 Networking Patch",
                            $"Patching {DisplayName}: {relativePath}",
                            progress))
                    {
                        Debug.LogWarning($"[GC2 Networking] {ModuleName} patch cancelled by user.");
                        RestoreFromBackups();
                        return false;
                    }

                    if (!PatchFile(relativePath))
                    {
                        // Rollback on failure
                        RestoreFromBackups();
                        return false;
                    }

                    if (!VerifyPatchedOutput(relativePath, out string verifyError))
                    {
                        Debug.LogError($"[GC2 Networking] Patch verification failed for {relativePath}: {verifyError}");
                        RestoreFromBackups();
                        return false;
                    }
                }
                
                // Save patch info
                SavePatchInfo();
                
                Debug.Log($"[GC2 Networking] {ModuleName} patch applied successfully.");
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"[GC2 Networking] Failed to patch {ModuleName}: {e}");
                
                // Try to rollback
                try { RestoreFromBackups(); } catch { }
                
                return false;
            }
            finally
            {
                if (assetEditingStarted)
                {
                    AssetDatabase.StopAssetEditing();
                }

                if (reloadAssembliesLocked)
                {
                    EditorApplication.UnlockReloadAssemblies();
                }

                EditorUtility.ClearProgressBar();
                AssetDatabase.Refresh();
            }
        }
        
        /// <summary>
        /// Remove the patch and restore original files.
        /// </summary>
        public bool RemovePatch()
        {
            bool assetEditingStarted = false;
            bool reloadAssembliesLocked = false;
            try
            {
                EditorApplication.LockReloadAssemblies();
                reloadAssembliesLocked = true;

                AssetDatabase.StartAssetEditing();
                assetEditingStarted = true;

                EditorUtility.DisplayProgressBar(
                    "GC2 Networking Patch",
                    $"Restoring {DisplayName} from backups...",
                    0.5f);

                return RestoreFromBackups();
            }
            catch (Exception e)
            {
                Debug.LogError($"[GC2 Networking] Failed to unpatch {ModuleName}: {e}");
                return false;
            }
            finally
            {
                if (assetEditingStarted)
                {
                    AssetDatabase.StopAssetEditing();
                }

                if (reloadAssembliesLocked)
                {
                    EditorApplication.UnlockReloadAssemblies();
                }

                EditorUtility.ClearProgressBar();
                AssetDatabase.Refresh();
            }
        }
        
        /// <summary>
        /// Get patch status information.
        /// </summary>
        public PatchStatus GetStatus()
        {
            return new PatchStatus
            {
                ModuleName = ModuleName,
                DisplayName = DisplayName,
                IsInstalled = ValidateFilesExist(),
                IsPatched = IsPatched(),
                HasBackups = HasBackups(),
                PatchVersion = PatchVersion
            };
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // ABSTRACT PATCH IMPLEMENTATION
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Apply patch to a specific file. Override to implement patching logic.
        /// </summary>
        protected abstract bool PatchFile(string relativePath);

        /// <summary>
        /// Runtime/editor files can opt out of strict marker requirements.
        /// </summary>
        protected virtual bool ShouldRequirePatchMarker(string relativePath)
        {
            return !relativePath.Contains("/Editor/");
        }

        protected virtual VersionCompatibilityRequirement[] GetVersionCompatibilityRequirements()
        {
            return Array.Empty<VersionCompatibilityRequirement>();
        }

        /// <summary>
        /// Returns required tokens that must exist in a patched file.
        /// Override in module patchers for hook-point verification.
        /// </summary>
        protected virtual string[] GetRequiredPatchTokens(string relativePath)
        {
            return Array.Empty<string>();
        }

        /// <summary>
        /// Returns required token occurrence counts for structural hook verification.
        /// Key is token substring, value is minimum required count.
        /// </summary>
        protected virtual Dictionary<string, int> GetRequiredPatchTokenCounts(string relativePath)
        {
            return null;
        }

        /// <summary>
        /// Returns required regex occurrence counts for method-scoped hook verification.
        /// Key is regex pattern, value is minimum required match count.
        /// </summary>
        protected virtual Dictionary<string, int> GetRequiredPatchRegexTokenCounts(string relativePath)
        {
            return null;
        }

        /// <summary>
        /// Verifies a patched file contains required markers and balanced patch sections.
        /// Override for module-specific hook-point verification.
        /// </summary>
        protected virtual bool VerifyPatchedFile(string relativePath, string content, out string failureReason)
        {
            if (ShouldRequirePatchMarker(relativePath) && !ContainsPatchMarker(content))
            {
                failureReason = "Patch marker missing.";
                return false;
            }

            const string sectionStart = "// [GC2_NETWORK_PATCH]";
            const string sectionEnd = "// [GC2_NETWORK_PATCH_END]";
            int startCount = CountOccurrences(content, sectionStart);
            int endCount = CountOccurrences(content, sectionEnd);

            if (startCount != endCount)
            {
                failureReason = "Patch hook markers are unbalanced.";
                return false;
            }

            if (ShouldRequirePatchMarker(relativePath) && startCount == 0)
            {
                failureReason = "No patch hook sections were found.";
                return false;
            }

            string[] requiredTokens = GetRequiredPatchTokens(relativePath);
            for (int i = 0; i < requiredTokens.Length; i++)
            {
                string token = requiredTokens[i];
                if (string.IsNullOrEmpty(token)) continue;

                if (!content.Contains(token))
                {
                    failureReason = $"Required patch token missing: '{token}'";
                    return false;
                }
            }

            Dictionary<string, int> requiredTokenCounts = GetRequiredPatchTokenCounts(relativePath);
            if (requiredTokenCounts != null)
            {
                foreach (KeyValuePair<string, int> rule in requiredTokenCounts)
                {
                    string token = rule.Key;
                    if (string.IsNullOrEmpty(token)) continue;

                    int minCount = Mathf.Max(1, rule.Value);
                    int actualCount = CountOccurrences(content, token);
                    if (actualCount < minCount)
                    {
                        failureReason =
                            $"Required patch token '{token}' count {actualCount} is below minimum {minCount}.";
                        return false;
                    }
                }
            }

            Dictionary<string, int> requiredRegexTokenCounts = GetRequiredPatchRegexTokenCounts(relativePath);
            if (requiredRegexTokenCounts != null)
            {
                foreach (KeyValuePair<string, int> rule in requiredRegexTokenCounts)
                {
                    string pattern = rule.Key;
                    if (string.IsNullOrEmpty(pattern)) continue;

                    int minCount = Mathf.Max(1, rule.Value);
                    int actualCount = CountRegexMatches(content, pattern);
                    if (actualCount < minCount)
                    {
                        failureReason =
                            $"Required patch regex '{pattern}' matched {actualCount}, below minimum {minCount}.";
                        return false;
                    }
                }
            }

            failureReason = null;
            return true;
        }

        private static int CountOccurrences(string content, string value)
        {
            if (string.IsNullOrEmpty(content) || string.IsNullOrEmpty(value)) return 0;

            int count = 0;
            int index = 0;
            while (index < content.Length)
            {
                int next = content.IndexOf(value, index, StringComparison.Ordinal);
                if (next < 0) break;

                count++;
                index = next + value.Length;
            }

            return count;
        }

        private static int CountRegexMatches(string content, string pattern)
        {
            if (string.IsNullOrEmpty(content) || string.IsNullOrEmpty(pattern)) return 0;

            try
            {
                Regex regex = new Regex(
                    pattern,
                    RegexOptions.Multiline | RegexOptions.Singleline,
                    TimeSpan.FromSeconds(1));

                return regex.Matches(content).Count;
            }
            catch (RegexMatchTimeoutException)
            {
                Debug.LogWarning(
                    $"[GC2 Networking] Regex verification timed out for pattern: {pattern}. " +
                    "Treating as non-match to fail safely.");
                return 0;
            }
            catch (ArgumentException ex)
            {
                Debug.LogError($"[GC2 Networking] Invalid regex pattern '{pattern}': {ex.Message}");
                return 0;
            }
        }

        private static string BuildFlexibleSnippetRegex(string snippet)
        {
            if (string.IsNullOrEmpty(snippet))
            {
                return string.Empty;
            }

            var pattern = new System.Text.StringBuilder(snippet.Length * 2);
            bool previousWasWhitespace = false;

            for (int i = 0; i < snippet.Length; i++)
            {
                char character = snippet[i];
                if (char.IsWhiteSpace(character))
                {
                    if (previousWasWhitespace) continue;
                    pattern.Append(@"\s+");
                    previousWasWhitespace = true;
                    continue;
                }

                pattern.Append(Regex.Escape(character.ToString()));
                previousWasWhitespace = false;
            }

            return pattern.ToString();
        }

        private static bool TryReplaceWithFuzzyLineWindow(
            ref string content,
            string originalSnippet,
            string replacementSnippet)
        {
            const double minSimilarity = 0.94d;
            const int maxContentLength = 750_000;
            const int maxSnippetLength = 12_000;

            if (string.IsNullOrEmpty(content) || string.IsNullOrEmpty(originalSnippet))
            {
                return false;
            }

            if (content.Length > maxContentLength || originalSnippet.Length > maxSnippetLength)
            {
                return false;
            }

            string normalizedSnippet = NormalizeForSimilarity(originalSnippet);
            if (normalizedSnippet.Length < 80)
            {
                return false;
            }

            List<TextLineSpan> contentLines = GetLineSpans(content);
            int snippetLineCount = CountLogicalLines(originalSnippet);
            if (contentLines.Count == 0 || snippetLineCount <= 0)
            {
                return false;
            }

            double bestScore = 0d;
            int bestStart = -1;
            int bestLength = 0;

            int minWindowLines = Math.Max(1, snippetLineCount - 1);
            int maxWindowLines = Math.Min(contentLines.Count, snippetLineCount + 1);

            for (int windowLines = minWindowLines; windowLines <= maxWindowLines; windowLines++)
            {
                for (int lineIndex = 0; lineIndex + windowLines <= contentLines.Count; lineIndex++)
                {
                    int start = contentLines[lineIndex].Start;
                    TextLineSpan endLine = contentLines[lineIndex + windowLines - 1];
                    int length = endLine.Start + endLine.Length - start;

                    string candidate = content.Substring(start, length);
                    string normalizedCandidate = NormalizeForSimilarity(candidate);
                    double similarity = CalculateBoundedSimilarity(
                        normalizedSnippet,
                        normalizedCandidate,
                        minSimilarity);

                    if (similarity <= bestScore)
                    {
                        continue;
                    }

                    bestScore = similarity;
                    bestStart = start;
                    bestLength = length;
                }
            }

            if (bestStart < 0 || bestScore < minSimilarity)
            {
                return false;
            }

            content = content.Remove(bestStart, bestLength).Insert(bestStart, replacementSnippet);
            return true;
        }

        private static List<TextLineSpan> GetLineSpans(string content)
        {
            var spans = new List<TextLineSpan>();
            int start = 0;

            for (int i = 0; i < content.Length; i++)
            {
                if (content[i] != '\n') continue;

                spans.Add(new TextLineSpan(start, i - start + 1));
                start = i + 1;
            }

            if (start < content.Length)
            {
                spans.Add(new TextLineSpan(start, content.Length - start));
            }

            return spans;
        }

        private static int CountLogicalLines(string content)
        {
            string normalized = NormalizeLineEndings(content).Trim('\n');
            if (normalized.Length == 0)
            {
                return 0;
            }

            int count = 1;
            for (int i = 0; i < normalized.Length; i++)
            {
                if (normalized[i] == '\n')
                {
                    count++;
                }
            }

            return count;
        }

        private static string NormalizeForSimilarity(string content)
        {
            content = NormalizeLineEndings(content);
            var builder = new System.Text.StringBuilder(content.Length);
            bool previousWasWhitespace = true;

            for (int i = 0; i < content.Length; i++)
            {
                char ch = content[i];
                if (char.IsWhiteSpace(ch))
                {
                    if (!previousWasWhitespace)
                    {
                        builder.Append(' ');
                        previousWasWhitespace = true;
                    }

                    continue;
                }

                builder.Append(ch);
                previousWasWhitespace = false;
            }

            return builder.ToString().Trim();
        }

        private static double CalculateBoundedSimilarity(string source, string target, double minimumSimilarity)
        {
            if (string.Equals(source, target, StringComparison.Ordinal))
            {
                return 1d;
            }

            int maxLength = Math.Max(source.Length, target.Length);
            if (maxLength == 0)
            {
                return 1d;
            }

            int maxDistance = Math.Max(1, (int) Math.Ceiling(maxLength * (1d - minimumSimilarity)));
            int distance = CalculateBoundedLevenshteinDistance(source, target, maxDistance);
            if (distance > maxDistance)
            {
                return 0d;
            }

            return 1d - ((double) distance / maxLength);
        }

        private static int CalculateBoundedLevenshteinDistance(string source, string target, int maxDistance)
        {
            int sourceLength = source.Length;
            int targetLength = target.Length;

            if (Math.Abs(sourceLength - targetLength) > maxDistance)
            {
                return maxDistance + 1;
            }

            if (sourceLength == 0)
            {
                return targetLength;
            }

            if (targetLength == 0)
            {
                return sourceLength;
            }

            int[] previous = new int[targetLength + 1];
            int[] current = new int[targetLength + 1];

            for (int j = 0; j <= targetLength; j++)
            {
                previous[j] = j;
            }

            for (int i = 1; i <= sourceLength; i++)
            {
                current[0] = i;
                int rowMinimum = current[0];

                for (int j = 1; j <= targetLength; j++)
                {
                    int cost = source[i - 1] == target[j - 1] ? 0 : 1;
                    int deletion = previous[j] + 1;
                    int insertion = current[j - 1] + 1;
                    int substitution = previous[j - 1] + cost;

                    int value = Math.Min(Math.Min(deletion, insertion), substitution);
                    current[j] = value;
                    if (value < rowMinimum)
                    {
                        rowMinimum = value;
                    }
                }

                if (rowMinimum > maxDistance)
                {
                    return maxDistance + 1;
                }

                int[] swap = previous;
                previous = current;
                current = swap;
            }

            return previous[targetLength];
        }

        private static bool TryStripLegacyPatchArtifacts(ref string content, out string failureReason)
        {
            content = NormalizeLineEndings(content);

            try
            {
                // Remove module/version marker header lines.
                content = Regex.Replace(
                    content,
                    @"(?m)^[ \t]*//\s*\[GC2_NETWORK_PATCH_[^\]]*\]\s*\n?",
                    string.Empty,
                    RegexOptions.CultureInvariant,
                    TimeSpan.FromMilliseconds(250));

                // Remove standard patch header comments.
                content = Regex.Replace(
                    content,
                    @"(?m)^[ \t]*// This file has been patched for GC2 Networking server authority\.\s*\n?",
                    string.Empty,
                    RegexOptions.CultureInvariant,
                    TimeSpan.FromMilliseconds(250));
                content = Regex.Replace(
                    content,
                    @"(?m)^[ \t]*// Do not modify the patched sections manually\.\s*\n?",
                    string.Empty,
                    RegexOptions.CultureInvariant,
                    TimeSpan.FromMilliseconds(250));
                content = Regex.Replace(
                    content,
                    @"(?m)^[ \t]*// Use (?:Tools > Game Creator 2 Networking|Game Creator > Networking Layer) > Patches > .*? > Unpatch to restore\.\s*\n?",
                    string.Empty,
                    RegexOptions.CultureInvariant,
                    TimeSpan.FromMilliseconds(250));
            }
            catch (Exception ex)
            {
                failureReason = $"Regex cleanup failed: {ex.Message}";
                return false;
            }

            const string sectionStart = "// [GC2_NETWORK_PATCH]";
            const string sectionEnd = "// [GC2_NETWORK_PATCH_END]";
            int guard = 0;
            while (true)
            {
                int startIndex = content.IndexOf(sectionStart, StringComparison.Ordinal);
                if (startIndex < 0) break;
                if (++guard > 512)
                {
                    failureReason = "Exceeded cleanup iteration guard while stripping patch sections.";
                    return false;
                }

                int nextStartIndex = content.IndexOf(
                    sectionStart,
                    startIndex + sectionStart.Length,
                    StringComparison.Ordinal);
                int endIndex = content.IndexOf(sectionEnd, startIndex, StringComparison.Ordinal);
                if (endIndex < 0 || (nextStartIndex >= 0 && nextStartIndex < endIndex))
                {
                    Debug.LogWarning(
                        "[GC2 Networking] Found a legacy patch section start without a clean matching end marker. " +
                        "Attempting line-based cleanup of the orphaned patch block.");

                    if (!TryStripOrphanLegacyPatchSection(ref content, startIndex, out failureReason))
                    {
                        return false;
                    }

                    continue;
                }

                int removeEnd = endIndex + sectionEnd.Length;
                while (removeEnd < content.Length &&
                       (content[removeEnd] == ' ' || content[removeEnd] == '\t'))
                {
                    removeEnd++;
                }

                if (removeEnd < content.Length && content[removeEnd] == '\n')
                {
                    removeEnd++;
                }

                content = content.Remove(startIndex, removeEnd - startIndex);
            }

            // If any loose section end markers remain, strip the line to avoid false-positive marker detection.
            content = Regex.Replace(
                content,
                @"(?m)^[ \t]*//\s*\[GC2_NETWORK_PATCH_END\]\s*\n?",
                string.Empty,
                RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(250));

            // Keep formatting sane after block removals.
            content = Regex.Replace(
                content,
                @"\n{3,}",
                "\n\n",
                RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(250));

            failureReason = null;
            return true;
        }

        private static bool TryStripOrphanLegacyPatchSection(
            ref string content,
            int markerIndex,
            out string failureReason)
        {
            failureReason = null;

            if (markerIndex < 0 || markerIndex >= content.Length)
            {
                failureReason = "Invalid orphan patch marker index.";
                return false;
            }

            int removeStart = FindLineStart(content, markerIndex);
            int cursor = FindNextLineStart(content, markerIndex);
            int removeEnd = cursor;
            int braceDepth = 0;
            int parenDepth = 0;
            bool consumedPatchCode = false;
            int scannedLines = 0;

            while (cursor < content.Length && scannedLines++ < 96)
            {
                int lineEnd = content.IndexOf('\n', cursor);
                if (lineEnd < 0) lineEnd = content.Length;

                string line = content.Substring(cursor, lineEnd - cursor);
                string trimmed = line.Trim();

                bool includeLine;
                if (trimmed.Length == 0)
                {
                    includeLine = !consumedPatchCode ||
                        braceDepth > 0 ||
                        parenDepth > 0 ||
                        IsNextNonEmptyLikelyLegacyPatchLine(
                            content,
                            lineEnd + 1,
                            consumedPatchCode,
                            braceDepth,
                            parenDepth);
                }
                else
                {
                    includeLine = IsLikelyLegacyPatchLine(
                        trimmed,
                        consumedPatchCode,
                        braceDepth,
                        parenDepth);
                }

                if (!includeLine)
                {
                    break;
                }

                if (trimmed.Length > 0 && !trimmed.StartsWith("// [GC2_NETWORK_PATCH", StringComparison.Ordinal))
                {
                    consumedPatchCode = true;
                }

                braceDepth += CountPatchLineBraceDelta(trimmed);
                if (braceDepth < 0) braceDepth = 0;

                parenDepth += CountPatchLineParenDelta(trimmed);
                if (parenDepth < 0) parenDepth = 0;

                removeEnd = lineEnd < content.Length ? lineEnd + 1 : lineEnd;
                cursor = removeEnd;
            }

            if (removeEnd <= removeStart)
            {
                failureReason = "Could not determine orphan patch section removal range.";
                return false;
            }

            content = content.Remove(removeStart, removeEnd - removeStart);
            return true;
        }

        private static int FindLineStart(string content, int index)
        {
            int lineStart = content.LastIndexOf('\n', Math.Max(0, index));
            return lineStart < 0 ? 0 : lineStart + 1;
        }

        private static int FindNextLineStart(string content, int index)
        {
            int lineEnd = content.IndexOf('\n', Math.Max(0, index));
            return lineEnd < 0 ? content.Length : lineEnd + 1;
        }

        private static bool IsNextNonEmptyLikelyLegacyPatchLine(
            string content,
            int index,
            bool consumedPatchCode,
            int braceDepth,
            int parenDepth)
        {
            int cursor = Math.Max(0, index);
            while (cursor < content.Length)
            {
                int lineEnd = content.IndexOf('\n', cursor);
                if (lineEnd < 0) lineEnd = content.Length;

                string trimmed = content.Substring(cursor, lineEnd - cursor).Trim();
                if (trimmed.Length > 0)
                {
                    return IsLikelyLegacyPatchLine(trimmed, consumedPatchCode, braceDepth, parenDepth);
                }

                cursor = lineEnd < content.Length ? lineEnd + 1 : lineEnd;
            }

            return false;
        }

        private static bool IsLikelyLegacyPatchLine(
            string trimmed,
            bool consumedPatchCode,
            int braceDepth,
            int parenDepth)
        {
            if (trimmed.StartsWith("// [GC2_NETWORK_PATCH", StringComparison.Ordinal))
            {
                return true;
            }

            if (trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                return ContainsOrdinalIgnoreCase(trimmed, "network") ||
                    ContainsOrdinalIgnoreCase(trimmed, "server authority") ||
                    ContainsOrdinalIgnoreCase(trimmed, "remote");
            }

            if (trimmed.Contains("Network", StringComparison.Ordinal) ||
                trimmed.Contains("networkConnection", StringComparison.Ordinal) ||
                trimmed.Contains("skipTransition", StringComparison.Ordinal))
            {
                return true;
            }

            if (consumedPatchCode && (braceDepth > 0 || parenDepth > 0))
            {
                return true;
            }

            if (trimmed.Contains("token != this.m_CurrentToken", StringComparison.Ordinal) ||
                trimmed.Contains("traverse != this.Traverse", StringComparison.Ordinal))
            {
                return true;
            }

            if (trimmed == "{" || trimmed == "}")
            {
                return consumedPatchCode || braceDepth > 0;
            }

            if ((trimmed == "return;" || trimmed == "return false;") &&
                (consumedPatchCode || braceDepth > 0))
            {
                return true;
            }

            if (trimmed.StartsWith("_ = Traverse.ChangeTo", StringComparison.Ordinal) &&
                trimmed.Contains("skipTransition", StringComparison.Ordinal))
            {
                return true;
            }

            return false;
        }

        private static int CountPatchLineBraceDelta(string trimmed)
        {
            int delta = 0;
            for (int i = 0; i < trimmed.Length; i++)
            {
                if (trimmed[i] == '{') delta++;
                else if (trimmed[i] == '}') delta--;
            }

            return delta;
        }

        private static int CountPatchLineParenDelta(string trimmed)
        {
            int delta = 0;
            bool inString = false;
            bool inChar = false;
            bool escaped = false;

            for (int i = 0; i < trimmed.Length; i++)
            {
                char ch = trimmed[i];

                if (inString)
                {
                    if (escaped)
                    {
                        escaped = false;
                        continue;
                    }

                    if (ch == '\\')
                    {
                        escaped = true;
                        continue;
                    }

                    if (ch == '"') inString = false;
                    continue;
                }

                if (inChar)
                {
                    if (escaped)
                    {
                        escaped = false;
                        continue;
                    }

                    if (ch == '\\')
                    {
                        escaped = true;
                        continue;
                    }

                    if (ch == '\'') inChar = false;
                    continue;
                }

                if (ch == '/' && i + 1 < trimmed.Length && trimmed[i + 1] == '/')
                {
                    break;
                }

                if (ch == '"')
                {
                    inString = true;
                    continue;
                }

                if (ch == '\'')
                {
                    inChar = true;
                    continue;
                }

                if (ch == '(') delta++;
                else if (ch == ')') delta--;
            }

            return delta;
        }

        private static bool ContainsOrdinalIgnoreCase(string value, string search)
        {
            return value?.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsVersionSupported(string version, string[] supportedPatterns)
        {
            if (supportedPatterns == null || supportedPatterns.Length == 0)
            {
                return true;
            }

            for (int i = 0; i < supportedPatterns.Length; i++)
            {
                string pattern = supportedPatterns[i];
                if (string.IsNullOrWhiteSpace(pattern)) continue;

                string normalizedPattern = pattern.Trim();
                if (normalizedPattern.EndsWith("*", StringComparison.Ordinal))
                {
                    string prefix = normalizedPattern.Substring(0, normalizedPattern.Length - 1);
                    if (version.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
                else if (string.Equals(version, normalizedPattern, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // BACKUP MANAGEMENT
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        protected void CreateBackups()
        {
            string backupFolder = Path.Combine(Application.dataPath, BackupFolder);
            Directory.CreateDirectory(backupFolder);
            
            foreach (var relativePath in FilesToPatch)
            {
                string fullPath = Path.Combine(Application.dataPath, relativePath);
                string backupPath = GetNestedBackupPath(backupFolder, relativePath);
                
                if (File.Exists(fullPath))
                {
                    string directory = Path.GetDirectoryName(backupPath);
                    if (!string.IsNullOrEmpty(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

                    File.Copy(fullPath, backupPath, overwrite: true);
                    Debug.Log($"[GC2 Networking] Backed up {relativePath}");
                }
            }
        }
        
        protected bool RestoreFromBackups()
        {
            string backupFolder = ResolveRestoreBackupFolder();
            if (string.IsNullOrEmpty(backupFolder)) return false;
            
            bool anyRestored = false;
            
            string primaryBackupFolder = Path.Combine(Application.dataPath, BackupFolder);
            if (!string.Equals(backupFolder, primaryBackupFolder, StringComparison.OrdinalIgnoreCase))
            {
                Debug.Log($"[GC2 Networking] Using legacy backup folder for restore: {backupFolder}");
            }

            foreach (var relativePath in FilesToPatch)
            {
                string fullPath = Path.Combine(Application.dataPath, relativePath);
                string restoredFromPath = null;
                foreach (string candidatePath in EnumerateBackupPathCandidates(backupFolder, relativePath))
                {
                    if (!File.Exists(candidatePath)) continue;
                    restoredFromPath = candidatePath;
                    break;
                }

                if (!string.IsNullOrEmpty(restoredFromPath))
                {
                    File.Copy(restoredFromPath, fullPath, overwrite: true);
                    Debug.Log($"[GC2 Networking] Restored {relativePath} from backup");
                    anyRestored = true;
                }
            }
            
            return anyRestored;
        }

        private IEnumerable<string> EnumerateBackupFoldersForRestore()
        {
            string primary = Path.Combine(Application.dataPath, BackupFolder);
            yield return primary;

            if (!AllowLegacyBackupRestore)
            {
                yield break;
            }

            string legacy = Path.Combine(Application.dataPath, LegacyBackupFolder);
            if (!string.Equals(primary, legacy, StringComparison.OrdinalIgnoreCase))
            {
                yield return legacy;
            }
        }

        private bool HasAllBackups(string backupFolder)
        {
            if (!Directory.Exists(backupFolder)) return false;

            foreach (var relativePath in FilesToPatch)
            {
                bool hasBackup = false;
                foreach (string candidatePath in EnumerateBackupPathCandidates(backupFolder, relativePath))
                {
                    if (!File.Exists(candidatePath)) continue;
                    hasBackup = true;
                    break;
                }

                if (!hasBackup) return false;
            }

            return true;
        }

        private static string NormalizeRelativePath(string relativePath)
        {
            return relativePath?.Replace('\\', '/');
        }

        private static string GetNestedBackupPath(string backupFolder, string relativePath)
        {
            string normalized = NormalizeRelativePath(relativePath) ?? string.Empty;
            string backupRelativePath = normalized + ".backup";
            return Path.Combine(backupFolder, backupRelativePath);
        }

        private static IEnumerable<string> EnumerateBackupPathCandidates(string backupFolder, string relativePath)
        {
            yield return GetNestedBackupPath(backupFolder, relativePath);

            // Legacy fallback: backups keyed only by file name.
            string fileName = Path.GetFileName(relativePath);
            if (!string.IsNullOrEmpty(fileName))
            {
                yield return Path.Combine(backupFolder, fileName + ".backup");
            }
        }

        private string ResolveRestoreBackupFolder()
        {
            foreach (string backupFolder in EnumerateBackupFoldersForRestore())
            {
                if (HasAllBackups(backupFolder))
                {
                    return backupFolder;
                }
            }

            return null;
        }
        
        protected void SavePatchInfo()
        {
            string backupFolder = Path.Combine(Application.dataPath, BackupFolder);
            Directory.CreateDirectory(backupFolder);
            
            string patchInfoPath = Path.Combine(backupFolder, "patch_info.txt");
            File.WriteAllText(patchInfoPath,
                $"Module: {ModuleName}\n" +
                $"Patch Version: {PatchVersion}\n" +
                $"Patch Marker: {PatchMarker}\n" +
                $"Project Path: {Path.GetFullPath(Path.Combine(Application.dataPath, ".."))}\n" +
                $"Patch Date: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
                $"Unity Version: {Application.unityVersion}\n");
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // HELPER METHODS
        // ════════════════════════════════════════════════════════════════════════════════════════

        protected bool EnsurePatchMarkerBeforeNamespace(ref string content, string namespaceName)
        {
            if (ContainsPatchMarker(content))
            {
                return true;
            }

            string pattern = $@"(?m)^namespace\s+{Regex.Escape(namespaceName)}\s*\{{";
            Match namespaceMatch = Regex.Match(content, pattern, RegexOptions.CultureInvariant);
            if (!namespaceMatch.Success)
            {
                Debug.LogError(
                    $"[GC2 Networking] Could not find namespace '{namespaceName}' while adding patch marker.");
                return false;
            }

            string header =
                $"{PatchMarker}\n" +
                "// This file has been patched for GC2 Networking server authority.\n" +
                "// Do not modify the patched sections manually.\n" +
                $"// Use Game Creator > Networking Layer > Patches > {ModuleName} > Unpatch to restore.\n\n";

            content = content.Insert(namespaceMatch.Index, header);
            return true;
        }

        protected static bool TryInsertAfterTypeOpeningBrace(
            ref string content,
            string typeName,
            string requiredToken,
            string insertion,
            out string failureReason)
        {
            failureReason = null;

            if (!string.IsNullOrEmpty(requiredToken) &&
                content.Contains(requiredToken, StringComparison.Ordinal))
            {
                return true;
            }

            if (!TryFindTypeBodySpan(content, typeName, out SourceBlockSpan typeSpan))
            {
                failureReason = $"Could not find type body for '{typeName}'.";
                return false;
            }

            content = content.Insert(typeSpan.BodyStart, insertion);
            return true;
        }

        protected static bool TryInsertAtMethodStart(
            ref string content,
            string methodName,
            string requiredToken,
            string insertion,
            out string failureReason)
        {
            failureReason = null;

            if (!string.IsNullOrEmpty(requiredToken) &&
                content.Contains(requiredToken, StringComparison.Ordinal))
            {
                return true;
            }

            if (!TryFindMethodBodySpan(content, methodName, out SourceBlockSpan methodSpan))
            {
                failureReason = $"Could not find method body for '{methodName}'.";
                return false;
            }

            content = content.Insert(methodSpan.BodyStart, insertion);
            return true;
        }

        protected static bool TryInsertAfterMethodAnchors(
            ref string content,
            string methodName,
            string requiredToken,
            string insertion,
            out string failureReason,
            params string[] anchorRegexes)
        {
            failureReason = null;

            if (!string.IsNullOrEmpty(requiredToken) &&
                content.Contains(requiredToken, StringComparison.Ordinal))
            {
                return true;
            }

            if (!TryFindMethodBodySpan(content, methodName, out SourceBlockSpan methodSpan))
            {
                failureReason = $"Could not find method body for '{methodName}'.";
                return false;
            }

            if (anchorRegexes == null || anchorRegexes.Length == 0)
            {
                content = content.Insert(methodSpan.BodyStart, insertion);
                return true;
            }

            string body = content.Substring(methodSpan.BodyStart, methodSpan.BodyLength);
            int relativeSearchStart = 0;
            int relativeInsertionIndex = -1;

            for (int i = 0; i < anchorRegexes.Length; i++)
            {
                string anchorRegex = anchorRegexes[i];
                if (string.IsNullOrWhiteSpace(anchorRegex)) continue;

                Match match = Regex.Match(
                    body.Substring(relativeSearchStart),
                    anchorRegex,
                    RegexOptions.CultureInvariant | RegexOptions.Multiline,
                    TimeSpan.FromMilliseconds(250));

                if (!match.Success)
                {
                    failureReason =
                        $"Could not find anchor {i + 1} in method '{methodName}': {anchorRegex}";
                    return false;
                }

                relativeInsertionIndex = relativeSearchStart + match.Index + match.Length;
                relativeSearchStart = relativeInsertionIndex;
            }

            if (relativeInsertionIndex < 0)
            {
                failureReason = $"Could not resolve insertion point in method '{methodName}'.";
                return false;
            }

            content = content.Insert(methodSpan.BodyStart + relativeInsertionIndex, insertion);
            return true;
        }

        protected static bool TryReplaceRegexInMethod(
            ref string content,
            string methodName,
            string requiredToken,
            string matchRegex,
            string replacement,
            out string failureReason)
        {
            failureReason = null;

            if (!string.IsNullOrEmpty(requiredToken) &&
                content.Contains(requiredToken, StringComparison.Ordinal))
            {
                return true;
            }

            if (!TryFindMethodBodySpan(content, methodName, out SourceBlockSpan methodSpan))
            {
                failureReason = $"Could not find method body for '{methodName}'.";
                return false;
            }

            string body = content.Substring(methodSpan.BodyStart, methodSpan.BodyLength);
            Match match = Regex.Match(
                body,
                matchRegex,
                RegexOptions.CultureInvariant | RegexOptions.Multiline | RegexOptions.Singleline,
                TimeSpan.FromMilliseconds(250));

            if (!match.Success)
            {
                failureReason = $"Could not find replacement target in method '{methodName}': {matchRegex}";
                return false;
            }

            int index = methodSpan.BodyStart + match.Index;
            content = content.Remove(index, match.Length).Insert(index, replacement);
            return true;
        }

        protected static bool TryInsertAfterRegexInMethod(
            ref string content,
            string methodName,
            string requiredToken,
            string anchorRegex,
            string insertion,
            out string failureReason)
        {
            failureReason = null;

            if (!string.IsNullOrEmpty(requiredToken) &&
                content.Contains(requiredToken, StringComparison.Ordinal))
            {
                return true;
            }

            if (!TryFindMethodBodySpan(content, methodName, out SourceBlockSpan methodSpan))
            {
                failureReason = $"Could not find method body for '{methodName}'.";
                return false;
            }

            string body = content.Substring(methodSpan.BodyStart, methodSpan.BodyLength);
            Match match = Regex.Match(
                body,
                anchorRegex,
                RegexOptions.CultureInvariant | RegexOptions.Multiline | RegexOptions.Singleline,
                TimeSpan.FromMilliseconds(250));

            if (!match.Success)
            {
                failureReason = $"Could not find anchor in method '{methodName}': {anchorRegex}";
                return false;
            }

            content = content.Insert(methodSpan.BodyStart + match.Index + match.Length, insertion);
            return true;
        }

        protected static bool TryFindTypeBodySpan(
            string content,
            string typeName,
            out SourceBlockSpan span)
        {
            span = default;

            string pattern =
                $@"(?m)^[ \t]*(?:\[[^\]\n]+\]\s*)*" +
                $@"(?:public|private|internal|protected)?\s*" +
                $@"(?:(?:new|sealed|abstract|static|partial)\s+)*" +
                $@"(?:class|struct|interface)\s+{Regex.Escape(typeName)}\b[^{{;]*\{{";

            Match match = Regex.Match(content, pattern, RegexOptions.CultureInvariant);
            return TryBuildSourceBlockSpan(content, match, out span);
        }

        protected static bool TryFindMethodBodySpan(
            string content,
            string methodName,
            out SourceBlockSpan span)
        {
            span = default;

            string pattern =
                $@"(?m)^[ \t]*(?:\[[^\]\n]+\]\s*)*" +
                $@"(?:public|private|internal|protected)\s+" +
                $@"(?:(?:new|static|virtual|override|sealed|async|extern|partial)\s+)*" +
                $@"[\w<>\[\].?, \t]+\s+" +
                $@"{Regex.Escape(methodName)}\s*(?:<[^>\n]+>)?\s*" +
                $@"\([^;{{}}]*\)\s*(?:where[^\{{]+)?\{{";

            Match match = Regex.Match(content, pattern, RegexOptions.CultureInvariant);
            return TryBuildSourceBlockSpan(content, match, out span);
        }

        private static bool TryBuildSourceBlockSpan(
            string content,
            Match declarationMatch,
            out SourceBlockSpan span)
        {
            span = default;
            if (!declarationMatch.Success) return false;

            int openBrace = content.IndexOf('{', declarationMatch.Index);
            if (openBrace < 0 || openBrace >= declarationMatch.Index + declarationMatch.Length)
            {
                return false;
            }

            if (!TryFindMatchingBrace(content, openBrace, out int closeBrace))
            {
                return false;
            }

            span = new SourceBlockSpan(
                declarationMatch.Index,
                openBrace,
                openBrace + 1,
                closeBrace,
                closeBrace);
            return true;
        }

        protected static bool TryFindMatchingBrace(
            string content,
            int openBrace,
            out int closeBrace)
        {
            closeBrace = -1;
            if (string.IsNullOrEmpty(content) ||
                openBrace < 0 ||
                openBrace >= content.Length ||
                content[openBrace] != '{')
            {
                return false;
            }

            int depth = 0;
            bool inLineComment = false;
            bool inBlockComment = false;
            bool inString = false;
            bool inVerbatimString = false;
            bool inChar = false;

            for (int i = openBrace; i < content.Length; i++)
            {
                char ch = content[i];
                char next = i + 1 < content.Length ? content[i + 1] : '\0';

                if (inLineComment)
                {
                    if (ch == '\n') inLineComment = false;
                    continue;
                }

                if (inBlockComment)
                {
                    if (ch == '*' && next == '/')
                    {
                        inBlockComment = false;
                        i++;
                    }
                    continue;
                }

                if (inString)
                {
                    if (inVerbatimString)
                    {
                        if (ch == '"' && next == '"')
                        {
                            i++;
                            continue;
                        }

                        if (ch == '"')
                        {
                            inString = false;
                            inVerbatimString = false;
                        }

                        continue;
                    }

                    if (ch == '\\')
                    {
                        i++;
                        continue;
                    }

                    if (ch == '"')
                    {
                        inString = false;
                    }

                    continue;
                }

                if (inChar)
                {
                    if (ch == '\\')
                    {
                        i++;
                        continue;
                    }

                    if (ch == '\'')
                    {
                        inChar = false;
                    }

                    continue;
                }

                if (ch == '/' && next == '/')
                {
                    inLineComment = true;
                    i++;
                    continue;
                }

                if (ch == '/' && next == '*')
                {
                    inBlockComment = true;
                    i++;
                    continue;
                }

                if (ch == '"')
                {
                    inString = true;
                    inVerbatimString =
                        (i > 0 && content[i - 1] == '@') ||
                        (i > 1 && content[i - 2] == '@' && content[i - 1] == '$');
                    continue;
                }

                if (ch == '\'')
                {
                    inChar = true;
                    continue;
                }

                if (ch == '{')
                {
                    depth++;
                    continue;
                }

                if (ch == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        closeBrace = i;
                        return true;
                    }
                }
            }

            return false;
        }

        protected string ReadFile(string relativePath)
        {
            string fullPath = Path.Combine(Application.dataPath, relativePath);
            string content = File.ReadAllText(fullPath);
            return NormalizeLineEndings(content);
        }

        private bool VerifyPatchedOutput(string relativePath, out string failureReason)
        {
            if (!ShouldRequirePatchMarker(relativePath))
            {
                failureReason = null;
                return true;
            }

            string fullPath = Path.Combine(Application.dataPath, relativePath);
            if (!File.Exists(fullPath))
            {
                failureReason = "Patched file missing after write.";
                return false;
            }

            string content = NormalizeLineEndings(File.ReadAllText(fullPath));
            return VerifyPatchedFile(relativePath, content, out failureReason);
        }
        
        protected void WriteFile(string relativePath, string content)
        {
            string fullPath = Path.Combine(Application.dataPath, relativePath);
            File.WriteAllText(fullPath, content);
        }
        
        /// <summary>
        /// Normalizes line endings to Unix style (LF) and removes BOM.
        /// This ensures pattern matching works regardless of file encoding.
        /// </summary>
        protected static string NormalizeLineEndings(string content)
        {
            // Remove BOM if present
            if (content.Length > 0 && content[0] == '\uFEFF')
            {
                content = content.Substring(1);
            }
            
            // Normalize CRLF to LF
            return content.Replace("\r\n", "\n").Replace("\r", "\n");
        }
        
        /// <summary>
        /// Add the patch marker comment to content.
        /// </summary>
        protected string AddPatchMarker(string content, string insertAfterPattern)
        {
            int insertIndex = content.IndexOf(insertAfterPattern);
            if (insertIndex < 0) return content;
            
            insertIndex += insertAfterPattern.Length;
            
            string markerComment = $"\n\n{PatchMarker}\n// This file has been patched for GC2 Networking server authority.\n// Do not modify the patched sections manually.\n// Use Game Creator > Networking Layer > Patches > {ModuleName} > Unpatch to restore.\n";
            
            return content.Insert(insertIndex, markerComment);
        }

        private static string BuildPatchVersionToken(string patchVersion)
        {
            if (string.IsNullOrWhiteSpace(patchVersion))
            {
                return "v1";
            }

            string version = patchVersion.Trim();
            if (version.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            {
                version = version.Substring(1);
            }

            if (version.Length == 0)
            {
                return "v1";
            }

            var token = new System.Text.StringBuilder(version.Length + 1);
            token.Append('v');
            for (int i = 0; i < version.Length; i++)
            {
                char ch = version[i];
                if (char.IsLetterOrDigit(ch))
                {
                    token.Append(char.ToLowerInvariant(ch));
                }
                else
                {
                    token.Append('_');
                }
            }

            return token.ToString();
        }
    }
    
    /// <summary>
    /// Patch status information.
    /// </summary>
    public struct PatchStatus
    {
        public string ModuleName;
        public string DisplayName;
        public bool IsInstalled;
        public bool IsPatched;
        public bool HasBackups;
        public string PatchVersion;
    }
}
