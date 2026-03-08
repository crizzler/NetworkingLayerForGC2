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
                    return false;
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

                // Create backups first
                CreateBackups();
                
                // Apply patches to each file
                for (int i = 0; i < FilesToPatch.Length; i++)
                {
                    string relativePath = FilesToPatch[i];

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

                int endIndex = content.IndexOf(sectionEnd, startIndex, StringComparison.Ordinal);
                if (endIndex < 0)
                {
                    failureReason = "Found start patch marker without matching end marker.";
                    return false;
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
