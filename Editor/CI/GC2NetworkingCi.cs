using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using Arawn.EnemyMasses.Editor.Integration.GameCreator2.Patches;
using UnityEditor;
using UnityEditor.Build;
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
        private const string FusionCoreArchive =
            "Arawn/NetworkingLayerForGC2/Demo/Fusion/Packages/Core/Package.unitypackage";
        private const string PurrNetCoreArchive =
            "Arawn/NetworkingLayerForGC2/Demo/PurrNet/Packages/Core/Package.unitypackage";
        private const string FusionCoreInstallRoot =
            "Assets/Plugins/GameCreator/Installs/" +
            "GC2NetworkingLayerFusionTransport.CoreExamples@1.1.0";
        private const string PurrNetCoreInstallRoot =
            "Assets/Plugins/GameCreator/Installs/" +
            "GC2NetworkingLayerPurrNetTransport.CoreExamples@1.1.0";
        private const string FusionMalbersArchive =
            "Arawn/NetworkingLayerForGC2/Demo/Fusion/Packages/Malbers/Package.unitypackage";
        private const string PurrNetMalbersArchive =
            "Arawn/NetworkingLayerForGC2/Demo/PurrNet/Packages/Malbers/Package.unitypackage";
        private const string FusionMalbersInstallRoot =
            "Assets/Plugins/GameCreator/Installs/" +
            "GC2NetworkingLayerFusionTransport.MalbersExamples@1.0.0";
        private const string PurrNetMalbersInstallRoot =
            "Assets/Plugins/GameCreator/Installs/" +
            "GC2NetworkingLayerPurrNetTransport.MalbersExamples@1.0.0";

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

        /// <summary>
        /// Imports the checked-in Fusion and PurrNet Core Examples 1.1.0 archives so focused
        /// ragdoll scene/package tests do not depend on machine-local installed-example state.
        /// The builders are resolved by name to keep this shared editor assembly independent of
        /// both transport SDKs, and archive hashes must remain unchanged.
        /// </summary>
        public static void PrepareCoreRagdollExamples()
        {
            string[] archives =
            {
                FusionCoreArchive,
                PurrNetCoreArchive
            };
            Dictionary<string, string> archiveHashes = HashRequiredAssets(archives);

            try
            {
                InvokePublicStaticMethod(
                    "Arawn.GameCreator2.Networking.Transport.Fusion.Editor." +
                    "FusionCoreRagdollDemoBuilder, " +
                    "Arawn.GameCreator2.Networking.Transport.Fusion.Editor",
                    "BuildValidationFixture");
                InvokePublicStaticMethod(
                    "Arawn.GameCreator2.Networking.Transport.PurrNet.Editor." +
                    "PurrNetCoreRagdollDemoBuilder, " +
                    "Arawn.GameCreator2.Networking.Transport.PurrNet.Editor",
                    "BuildValidationFixture");
            }
            finally
            {
                AssertAssetHashesUnchanged(archiveHashes);
            }

            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            RequireGeneratedFolder(FusionCoreInstallRoot);
            RequireGeneratedFolder(PurrNetCoreInstallRoot);

            Debug.Log(
                "[GC2 Networking CI] Core ragdoll validation fixtures prepared " +
                "without modifying installer archives.");
        }

        /// <summary>
        /// Rebuilds the ignored Fusion and PurrNet Malbers install roots used by the focused
        /// optional-integration tests. The checked-in Malbers archives are hashed before and
        /// after preparation and must remain byte-identical. Malbers must already be installed.
        /// </summary>
        public static void PrepareMalbersExamples()
        {
            string[] archives =
            {
                FusionMalbersArchive,
                PurrNetMalbersArchive
            };
            Dictionary<string, string> archiveHashes = HashOptionalAssets(archives);

            try
            {
                InvokePublicStaticMethod(
                    "Arawn.GameCreator2.Networking.Transport.Fusion.Editor." +
                    "FusionMalbersDemoBuilder, " +
                    "Arawn.GameCreator2.Networking.Transport.Fusion.Editor",
                    "BuildValidationFixture");
                InvokePublicStaticMethod(
                    "Arawn.GameCreator2.Networking.Transport.PurrNet.Editor." +
                    "PurrNetMalbersDemoBuilder, " +
                    "Arawn.GameCreator2.Networking.Transport.PurrNet.Editor",
                    "BuildValidationFixture");
            }
            finally
            {
                AssertAssetHashesUnchanged(archiveHashes);
            }

            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            RequireGeneratedFolder(FusionMalbersInstallRoot);
            RequireGeneratedFolder(PurrNetMalbersInstallRoot);

            Debug.Log(
                "[GC2 Networking CI] Malbers validation fixtures prepared without modifying " +
                "installer archives.");
        }

        /// <summary>
        /// Rebuilds both transport Core Examples 1.1.0 installers, migrating an installed 1.0.2
        /// source fixture when necessary. This mutates installer archives and therefore belongs
        /// only in a disposable isolated project; copy reviewed outputs back afterward.
        /// </summary>
        public static void BuildCoreRagdollExamples()
        {
            InvokePublicStaticMethod(
                "Arawn.GameCreator2.Networking.Transport.Fusion.Editor." +
                "FusionCoreRagdollDemoBuilder, " +
                "Arawn.GameCreator2.Networking.Transport.Fusion.Editor",
                "BuildInstaller");
            InvokePublicStaticMethod(
                "Arawn.GameCreator2.Networking.Transport.PurrNet.Editor." +
                "PurrNetCoreRagdollDemoBuilder, " +
                "Arawn.GameCreator2.Networking.Transport.PurrNet.Editor",
                "BuildInstaller");

            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            RequireGeneratedFolder(FusionCoreInstallRoot);
            RequireGeneratedFolder(PurrNetCoreInstallRoot);
            Debug.Log("[GC2 Networking CI] Core ragdoll example installers rebuilt.");
        }

        /// <summary>
        /// Applies the Android player settings required by Fusion (INTERNET permission,
        /// ARM64, IL2CPP, minimum SDK) and synchronizes scripting defines for Android.
        /// Run once after Unity has selected the Android build target, then run
        /// <see cref="BuildAndroidPlayer"/> in a second process so the new defines are compiled.
        /// </summary>
        public static void ConfigureAndroidPlayer()
        {
            string report = NetworkAndroidPlatformSetup.ApplyAndroidSettings();
            GC2NetworkingDefineSymbols.RefreshNow(false);

            if (!NetworkAndroidPlatformSetup.TryValidateAndroidSettings(out string error))
            {
                throw new InvalidOperationException(
                    "Android player settings are invalid: " + error);
            }

            if (!NetworkAndroidPlatformSetup.TryEnsureAndroidJdk(out string jdkReport))
            {
                throw new InvalidOperationException(jdkReport);
            }

            string androidSymbols = PlayerSettings.GetScriptingDefineSymbols(
                NamedBuildTarget.Android);
            bool hasTransportIntegration = androidSymbols
                .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Any(symbol => string.Equals(
                    symbol.Trim(),
                    "ARAWN_GC2_TRANSPORT_INTEGRATION",
                    StringComparison.Ordinal));
            if (!hasTransportIntegration)
            {
                throw new InvalidOperationException(
                    "ARAWN_GC2_TRANSPORT_INTEGRATION is missing from the Android define set.");
            }

            Debug.Log("[GC2 Networking CI] Android player configured: " + report + "; " + jdkReport);
        }

        /// <summary>
        /// Builds a signed-development-style Android APK through the Unity CLI in an isolated
        /// project. The output path and optional scene override are read from
        /// GC2_ANDROID_BUILD_OUTPUT and GC2_ANDROID_BUILD_SCENE.
        /// </summary>
        public static void BuildAndroidPlayer()
        {
            if (!NetworkAndroidPlatformSetup.TryValidateAndroidSettings(out string error))
            {
                throw new InvalidOperationException(
                    "Android player settings are invalid: " + error);
            }

            if (!NetworkAndroidPlatformSetup.TryEnsureAndroidJdk(out string jdkReport))
            {
                throw new InvalidOperationException(jdkReport);
            }

            Debug.Log("[GC2 Networking CI] Android JDK: " + jdkReport);
            string outputPath = Environment.GetEnvironmentVariable("GC2_ANDROID_BUILD_OUTPUT");
            if (string.IsNullOrWhiteSpace(outputPath))
            {
                outputPath = "Build/Android/GC2Networking.apk";
            }

            string fullOutputPath = Path.GetFullPath(outputPath);
            string outputDirectory = Path.GetDirectoryName(fullOutputPath);
            if (!string.IsNullOrEmpty(outputDirectory))
            {
                Directory.CreateDirectory(outputDirectory);
            }

            string scene = ResolveAndroidBuildScene();
            var options = new BuildPlayerOptions
            {
                scenes = new[] { scene },
                locationPathName = fullOutputPath,
                target = BuildTarget.Android,
                targetGroup = BuildTargetGroup.Android,
                options = BuildOptions.None
            };

            UnityEditor.Build.Reporting.BuildReport report =
                BuildPipeline.BuildPlayer(options);
            if (report.summary.result !=
                UnityEditor.Build.Reporting.BuildResult.Succeeded)
            {
                throw new InvalidOperationException(
                    $"Android build failed: {report.summary.result} " +
                    $"({report.summary.totalErrors} errors).");
            }

            long bytes = File.Exists(fullOutputPath)
                ? new FileInfo(fullOutputPath).Length
                : 0L;
            Debug.Log(
                $"[GC2 Networking CI] Android build passed: {fullOutputPath} " +
                $"bytes={bytes} scene={scene} unity={Application.unityVersion}");
        }

        private const string FusionCoreVariablesScene =
            "Assets/Plugins/GameCreator/Installs/" +
            "GC2NetworkingLayerFusionTransport.CoreExamples@1.1.0/" +
            "Requires GC2 Core Demos - FusionCoreVariablesDemo.unity";

        /// <summary>
        /// Builds the Fusion Core demo as an Android development APK for device smoke tests.
        /// The runtime harness is activated by gc2-network-smoke-* intent extras, so this scene
        /// does not need a committed test component.
        /// </summary>
        public static void BuildAndroidSmokePlayer()
        {
            if (!NetworkAndroidPlatformSetup.TryValidateAndroidSettings(out string error))
            {
                throw new InvalidOperationException(
                    "Android player settings are invalid: " + error);
            }

            if (!NetworkAndroidPlatformSetup.TryEnsureAndroidJdk(out string jdkReport))
            {
                throw new InvalidOperationException(jdkReport);
            }

            string outputPath = Environment.GetEnvironmentVariable(
                "GC2_ANDROID_SMOKE_OUTPUT");
            if (string.IsNullOrWhiteSpace(outputPath))
            {
                outputPath = "Build/Android/GC2AndroidSmoke.apk";
            }
            string fullOutputPath = Path.GetFullPath(outputPath);
            Directory.CreateDirectory(
                Path.GetDirectoryName(fullOutputPath) ?? ".");

            string scene = ResolveCoreSmokeScene();
            if (string.IsNullOrWhiteSpace(scene))
            {
                throw new FileNotFoundException(
                    "The Fusion Core Variables demo scene is missing.");
            }

            var options = new BuildPlayerOptions
            {
                scenes = new[] { scene },
                locationPathName = fullOutputPath,
                target = BuildTarget.Android,
                targetGroup = BuildTargetGroup.Android,
                options = BuildOptions.Development
            };

            UnityEditor.Build.Reporting.BuildReport report =
                BuildPipeline.BuildPlayer(options);
            if (report.summary.result !=
                UnityEditor.Build.Reporting.BuildResult.Succeeded)
            {
                throw new InvalidOperationException(
                    $"Android smoke build failed: {report.summary.result} " +
                    $"({report.summary.totalErrors} errors).");
            }

            long bytes = File.Exists(fullOutputPath)
                ? new FileInfo(fullOutputPath).Length
                : 0L;
            Debug.Log(
                $"[GC2 Networking CI] Android smoke build passed: {fullOutputPath} " +
                $"bytes={bytes} scene={scene} jdk={jdkReport}");
        }

        private static string ResolveCoreSmokeScene()
        {
            string scene = Environment.GetEnvironmentVariable(
                "GC2_CORE_SMOKE_SCENE");
            if (string.IsNullOrWhiteSpace(scene))
            {
                scene = FusionCoreVariablesScene;
            }

            if (!File.Exists(scene))
            {
                string[] guids = AssetDatabase.FindAssets(
                    "FusionCoreVariablesDemo t:Scene");
                scene = guids.Length > 0
                    ? AssetDatabase.GUIDToAssetPath(guids[0])
                    : string.Empty;
            }

            return scene;
        }

        /// <summary>
        /// Builds the same Core Variables smoke scene as a Linux development player. This is
        /// the fallback second peer when an ARM64 Android emulator is unavailable on an x86_64
        /// host, or the desktop side of a phone-vs-desktop sync check.
        /// </summary>
        public static void BuildDesktopSmokePlayer()
        {
            string outputPath = Environment.GetEnvironmentVariable(
                "GC2_DESKTOP_SMOKE_OUTPUT");
            if (string.IsNullOrWhiteSpace(outputPath))
            {
                outputPath = "Build/Desktop/GC2CoreSmoke/GC2CoreSmoke.x86_64";
            }
            string fullOutputPath = Path.GetFullPath(outputPath);
            Directory.CreateDirectory(
                Path.GetDirectoryName(fullOutputPath) ?? ".");

            string scene = ResolveCoreSmokeScene();
            if (string.IsNullOrWhiteSpace(scene))
            {
                throw new FileNotFoundException(
                    "The Fusion Core Variables demo scene is missing.");
            }

            var options = new BuildPlayerOptions
            {
                scenes = new[] { scene },
                locationPathName = fullOutputPath,
                target = BuildTarget.StandaloneLinux64,
                targetGroup = BuildTargetGroup.Standalone,
                options = BuildOptions.Development
            };

            UnityEditor.Build.Reporting.BuildReport report =
                BuildPipeline.BuildPlayer(options);
            if (report.summary.result !=
                UnityEditor.Build.Reporting.BuildResult.Succeeded)
            {
                throw new InvalidOperationException(
                    $"Desktop smoke build failed: {report.summary.result} " +
                    $"({report.summary.totalErrors} errors).");
            }

            Debug.Log(
                $"[GC2 Networking CI] Desktop smoke build passed: {fullOutputPath} " +
                $"scene={scene}");
        }

        private static string ResolveAndroidBuildScene()
        {
            string configured = Environment.GetEnvironmentVariable("GC2_ANDROID_BUILD_SCENE");
            if (!string.IsNullOrWhiteSpace(configured))
            {
                string normalized = configured.Replace('\\', '/');
                if (!File.Exists(normalized))
                {
                    throw new InvalidOperationException(
                        $"GC2_ANDROID_BUILD_SCENE does not exist: {normalized}");
                }
                return normalized;
            }

            string enabledScene = EditorBuildSettings.scenes
                .Where(scene => scene.enabled)
                .Select(scene => scene.path)
                .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
            if (!string.IsNullOrWhiteSpace(enabledScene))
            {
                return enabledScene;
            }

            string[] sceneGuids = AssetDatabase.FindAssets(
                "t:Scene",
                new[] { "Assets/Plugins/GameCreator/Installs" });
            foreach (string guid in sceneGuids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (path.IndexOf("Fusion", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
                {
                    return path;
                }
            }

            const string generatedScene = "Assets/__GC2AndroidBuildSmoke.unity";
            UnityEngine.SceneManagement.Scene scene =
                UnityEditor.SceneManagement.EditorSceneManager.NewScene(
                    UnityEditor.SceneManagement.NewSceneSetup.EmptyScene,
                    UnityEditor.SceneManagement.NewSceneMode.Single);
            new GameObject("GC2 Android Build Smoke");
            UnityEditor.SceneManagement.EditorSceneManager.SaveScene(
                scene,
                generatedScene);
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            Debug.Log(
                $"[GC2 Networking CI] Created fallback Android build scene: {generatedScene}");
            return generatedScene;
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

        private static Dictionary<string, string> HashOptionalAssets(
            IEnumerable<string> assetRelativePaths)
        {
            var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string relativePath in assetRelativePaths)
            {
                string fullPath = Path.Combine(Application.dataPath, relativePath);
                if (File.Exists(fullPath)) hashes[relativePath] = ComputeSha256(fullPath);
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
                        $"Installer fixture preparation removed checked-in archive " +
                        $"Assets/{pair.Key}.",
                        fullPath);
                }

                string actualHash = ComputeSha256(fullPath);
                if (!string.Equals(pair.Value, actualHash, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Installer fixture preparation modified checked-in installer " +
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
                    $"Could not load required installer validation builder " +
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
                    $"{type.FullName}.{methodName} failed while preparing an isolated " +
                    "installer validation fixture.",
                    exception.InnerException ?? exception);
            }
        }

        private static void RequireGeneratedFolder(string assetPath)
        {
            if (!AssetDatabase.IsValidFolder(assetPath))
            {
                throw new DirectoryNotFoundException(
                    $"Installer validation builder did not create '{assetPath}'.");
            }
        }
    }
}
