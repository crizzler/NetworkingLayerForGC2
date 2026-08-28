using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using Arawn.EnemyMasses.Editor.Integration.GameCreator2.Patches;
using UnityEditor;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Editor.CI
{
    /// <summary>
    /// Unity CLI entry points for validation that requires imported licensed dependencies.
    /// Always run these methods in an isolated project copy.
    /// </summary>
    public static class GC2NetworkingCi
    {
        private const string FusionFreeFlowArchive =
            "Arawn/NetworkingLayerForGC2/Demo/Fusion/Packages/" +
            "FreeFlowCombat/Package.unitypackage";
        private const string PurrNetFreeFlowArchive =
            "Arawn/NetworkingLayerForGC2/Demo/PurrNet/Packages/" +
            "FreeFlowCombat/Package.unitypackage";
        private const string FusionFreeFlowInstallRoot =
            "Assets/Plugins/GameCreator/Installs/" +
            "GC2NetworkingLayerFusionTransport.FreeFlowCombatExamples@1.0.0";
        private const string PurrNetFreeFlowInstallRoot =
            "Assets/Plugins/GameCreator/Installs/" +
            "GC2NetworkingLayerPurrNetTransport.FreeFlowCombatExamples@1.0.0";

        private static readonly string[] DefaultGameCreatorPatchers =
        {
            "Core",
            "Stats",
            "Inventory",
            "Quests",
            "Dialogue",
            "Traversal",
            "Melee",
            "ShooterSight",
            "Shooter"
        };

        /// <summary>
        /// Applies every requested patch to pristine installed GC2 sources, verifies it, then
        /// restores and byte-compares every input. Override the default list with the
        /// GC2_NETWORK_CI_PATCHERS environment variable (comma-separated registry names).
        /// </summary>
        public static void RunPatchRoundTrip()
        {
            string[] patcherNames = ResolvePatcherNames();
            if (patcherNames.Length == 0)
            {
                throw new InvalidOperationException("No GC2 patchers were selected for validation.");
            }

            for (int i = 0; i < patcherNames.Length; i++)
            {
                ValidatePatcherRoundTrip(patcherNames[i]);
            }

            Debug.Log(
                $"[GC2 Networking CI] Patch round-trip passed for: " +
                string.Join(", ", patcherNames));
        }

        /// <summary>
        /// Applies and verifies the current patches without restoring them. This entry point is
        /// used only while preparing an isolated test project. Pristine inputs are patched
        /// transactionally; structurally current older markers can be promoted without rewriting
        /// their verified hook bodies.
        /// </summary>
        public static void ApplyCurrentPatches()
        {
            string[] patcherNames = ResolvePatcherNames();
            if (patcherNames.Length == 0)
            {
                throw new InvalidOperationException("No GC2 patchers were selected for validation.");
            }

            for (int i = 0; i < patcherNames.Length; i++)
            {
                string patcherName = patcherNames[i];
                GC2PatcherBase patcher = GC2PatchManager.GetPatcher(patcherName);
                if (patcher == null)
                {
                    throw new InvalidOperationException(
                        $"Patcher '{patcherName}' is not registered with GC2PatchManager.");
                }

                if (!patcher.TryValidateVersionCompatibility(out string compatibilityMessage))
                {
                    throw new InvalidOperationException(
                        $"Patcher '{patcherName}' rejected the installed dependency version: " +
                        compatibilityMessage);
                }

                if (!patcher.IsPatched())
                {
                    patcher.TryPromoteVerifiedPatchMarkers();
                }

                if (!patcher.IsPatched() && !patcher.ApplyPatch())
                {
                    throw new InvalidOperationException(
                        $"Patcher '{patcherName}' failed while preparing the isolated test project.");
                }

                if (!patcher.IsPatched())
                {
                    throw new InvalidOperationException(
                        $"Patcher '{patcherName}' did not pass post-apply verification.");
                }

                Debug.Log(
                    $"[GC2 Networking CI] Applied {patcherName} {patcher.PatchVersion}.");
            }

            // Patch-gated assemblies use scripting define constraints. Synchronize them before
            // reporting success so the next batch process discovers the complete test/build
            // graph immediately rather than one editor launch late.
            Arawn.GameCreator2.Networking.Editor.GC2NetworkingDefineSymbols.RefreshNow(false);
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            Debug.Log(
                $"[GC2 Networking CI] Current patches applied for: " +
                string.Join(", ", patcherNames));
        }

        /// <summary>
        /// Rebuilds the ignored Fusion and PurrNet Free Flow Combat install roots used by the
        /// installer tests. Transport editor assemblies are resolved by name so this base editor
        /// assembly does not acquire transport-specific references. The checked-in archives are
        /// hashed before and after preparation and must remain byte-identical.
        /// </summary>
        public static void PrepareFreeFlowCombatExamples()
        {
            string[] archives =
            {
                FusionFreeFlowArchive,
                PurrNetFreeFlowArchive
            };
            Dictionary<string, string> archiveHashes = HashRequiredAssets(archives);

            try
            {
                InvokePublicStaticMethod(
                    "Arawn.GameCreator2.Networking.Transport.Fusion.Editor." +
                    "FusionFreeFlowCombatDemoBuilder, " +
                    "Arawn.GameCreator2.Networking.Transport.Fusion.Editor",
                    "BuildValidationFixture");
                InvokePublicStaticMethod(
                    "Arawn.GameCreator2.Networking.Transport.PurrNet.Editor." +
                    "PurrNetFreeFlowCombatDemoBuilder, " +
                    "Arawn.GameCreator2.Networking.Transport.PurrNet.Editor",
                    "BuildValidationFixture");
            }
            finally
            {
                AssertAssetHashesUnchanged(archiveHashes);
            }

            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            RequireGeneratedFolder(FusionFreeFlowInstallRoot);
            RequireGeneratedFolder(PurrNetFreeFlowInstallRoot);

            Debug.Log(
                "[GC2 Networking CI] Free Flow Combat validation fixtures prepared " +
                "without modifying installer archives.");
        }

        private static string[] ResolvePatcherNames()
        {
            string configured = Environment.GetEnvironmentVariable("GC2_NETWORK_CI_PATCHERS");
            if (string.IsNullOrWhiteSpace(configured))
            {
                return DefaultGameCreatorPatchers;
            }

            return configured
                .Split(',')
                .Select(value => value.Trim())
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }

        private static void ValidatePatcherRoundTrip(string patcherName)
        {
            GC2PatcherBase patcher = GC2PatchManager.GetPatcher(patcherName);
            if (patcher == null)
            {
                throw new InvalidOperationException(
                    $"Patcher '{patcherName}' is not registered with GC2PatchManager.");
            }

            if (!patcher.ValidateFilesExist())
            {
                throw new FileNotFoundException(
                    $"Required pristine inputs for patcher '{patcherName}' are not installed.");
            }

            if (!patcher.TryValidateVersionCompatibility(out string compatibilityMessage))
            {
                throw new InvalidOperationException(
                    $"Patcher '{patcherName}' rejected the installed dependency version: " +
                    compatibilityMessage);
            }

            Dictionary<string, string> pristineHashes = HashTargets(patcher);
            if (!patcher.ApplyPatch())
            {
                throw new InvalidOperationException(
                    $"Patcher '{patcherName}' failed while applying to pristine source.");
            }

            if (!patcher.IsPatched())
            {
                throw new InvalidOperationException(
                    $"Patcher '{patcherName}' did not pass its post-apply verification.");
            }

            Dictionary<string, string> patchedHashes = HashTargets(patcher);
            if (!patcher.ApplyPatch())
            {
                throw new InvalidOperationException(
                    $"Patcher '{patcherName}' failed its idempotent second apply.");
            }

            if (patcher.LastApplyOutcome != PatchApplyOutcome.AlreadyApplied)
            {
                throw new InvalidOperationException(
                    $"Patcher '{patcherName}' rewrote or reclassified an already current patch. " +
                    $"Outcome was {patcher.LastApplyOutcome}.");
            }

            Dictionary<string, string> reappliedHashes = HashTargets(patcher);
            foreach (KeyValuePair<string, string> pair in patchedHashes)
            {
                if (!reappliedHashes.TryGetValue(pair.Key, out string reappliedHash) ||
                    !string.Equals(pair.Value, reappliedHash, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Patcher '{patcherName}' modified {pair.Key} during an idempotent reapply.");
                }
            }

            if (!patcher.RemovePatch())
            {
                throw new InvalidOperationException(
                    $"Patcher '{patcherName}' failed to restore its pristine source.");
            }

            Dictionary<string, string> restoredHashes = HashTargets(patcher);
            foreach (KeyValuePair<string, string> pair in pristineHashes)
            {
                if (!restoredHashes.TryGetValue(pair.Key, out string restoredHash) ||
                    !string.Equals(pair.Value, restoredHash, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Patcher '{patcherName}' did not byte-identically restore {pair.Key}.");
                }
            }

            Debug.Log(
                $"[GC2 Networking CI] {patcherName} {patcher.PatchVersion}: " +
                $"apply, byte-identical idempotent reapply, verification, and " +
                $"byte-identical restore passed.");
        }

        private static Dictionary<string, string> HashTargets(GC2PatcherBase patcher)
        {
            var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
            using SHA256 sha256 = SHA256.Create();

            for (int i = 0; i < patcher.TargetFiles.Count; i++)
            {
                string relativePath = patcher.TargetFiles[i];
                string fullPath = Path.Combine(Application.dataPath, relativePath);
                if (!File.Exists(fullPath))
                {
                    throw new FileNotFoundException(
                        $"Patcher input disappeared during validation: {relativePath}", fullPath);
                }

                byte[] hash = sha256.ComputeHash(File.ReadAllBytes(fullPath));
                hashes[relativePath] = BitConverter.ToString(hash).Replace("-", string.Empty);
            }

            return hashes;
        }

        private static Dictionary<string, string> HashRequiredAssets(
            IEnumerable<string> assetRelativePaths)
        {
            var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string relativePath in assetRelativePaths)
            {
                string fullPath = Path.Combine(Application.dataPath, relativePath);
                if (!File.Exists(fullPath))
                {
                    throw new FileNotFoundException(
                        $"Required checked-in installer archive is missing: " +
                        $"Assets/{relativePath}",
                        fullPath);
                }

                hashes[relativePath] = ComputeSha256(fullPath);
            }

            return hashes;
        }

        private static void AssertAssetHashesUnchanged(
            IReadOnlyDictionary<string, string> expectedHashes)
        {
            foreach (KeyValuePair<string, string> pair in expectedHashes)
            {
                string fullPath = Path.Combine(Application.dataPath, pair.Key);
                if (!File.Exists(fullPath))
                {
                    throw new FileNotFoundException(
                        $"Free Flow Combat fixture preparation removed installer archive " +
                        $"Assets/{pair.Key}.",
                        fullPath);
                }

                string actualHash = ComputeSha256(fullPath);
                if (!string.Equals(pair.Value, actualHash, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Free Flow Combat fixture preparation modified checked-in installer " +
                        $"archive Assets/{pair.Key}. Validation builders must not export " +
                        "packages.");
                }
            }
        }

        private static string ComputeSha256(string fullPath)
        {
            using SHA256 sha256 = SHA256.Create();
            byte[] hash = sha256.ComputeHash(File.ReadAllBytes(fullPath));
            return BitConverter.ToString(hash).Replace("-", string.Empty);
        }

        private static void InvokePublicStaticMethod(
            string assemblyQualifiedTypeName,
            string methodName)
        {
            Type type = Type.GetType(assemblyQualifiedTypeName, throwOnError: false);
            if (type == null)
            {
                throw new InvalidOperationException(
                    $"Could not load required Free Flow Combat builder " +
                    $"'{assemblyQualifiedTypeName}'. Restore and compile its transport " +
                    "dependencies before preparing validation fixtures.");
            }

            MethodInfo method = type.GetMethod(
                methodName,
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                types: Type.EmptyTypes,
                modifiers: null);
            if (method == null)
            {
                throw new MissingMethodException(type.FullName, methodName);
            }

            try
            {
                method.Invoke(null, null);
            }
            catch (TargetInvocationException exception)
            {
                throw new InvalidOperationException(
                    $"{type.FullName}.{methodName} failed while preparing the isolated " +
                    "Free Flow Combat validation fixture.",
                    exception.InnerException ?? exception);
            }
        }

        private static void RequireGeneratedFolder(string assetPath)
        {
            if (!AssetDatabase.IsValidFolder(assetPath))
            {
                throw new DirectoryNotFoundException(
                    $"Free Flow Combat validation builder did not create '{assetPath}'.");
            }
        }
    }
}
