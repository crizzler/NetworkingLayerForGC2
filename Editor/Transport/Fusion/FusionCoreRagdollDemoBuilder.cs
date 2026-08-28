using System;
using System.IO;
using System.Linq;
using Arawn.GameCreator2.Networking.Editor;
using Arawn.GameCreator2.Networking.Security;
using Fusion;
using Fusion.Editor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Arawn.GameCreator2.Networking.Transport.Fusion.Editor
{
    /// <summary>Builds the Fusion Core Examples 1.1.0 ragdoll fixture and installer.</summary>
    public static class FusionCoreRagdollDemoBuilder
    {
        private const string PreviousRoot =
            "Assets/Plugins/GameCreator/Installs/" +
            "GC2NetworkingLayerFusionTransport.CoreExamples@1.0.2";
        private const string InstallRoot =
            "Assets/Plugins/GameCreator/Installs/" +
            "GC2NetworkingLayerFusionTransport.CoreExamples@1.1.0";

        private const string PreviousPlayerPrefab =
            PreviousRoot + "/FusionDemoPlayer-CoreAndVariables 3.prefab";
        private const string PlayerPrefab =
            InstallRoot + "/FusionDemoPlayer-CoreAndVariables 3.prefab";
        private const string SourceScene =
            InstallRoot + "/Requires GC2 Core Demos - FusionCoreVariablesDemo.unity";
        private const string RagdollScene =
            InstallRoot + "/Requires GC2 Core Demos - FusionRagdollDemo.unity";
        private const string PackagePath =
            "Assets/Arawn/NetworkingLayerForGC2/Demo/Fusion/Packages/Core/" +
            "Package.unitypackage";

        [MenuItem(
            "Game Creator/Networking Layer/Demos/Rebuild Fusion Core Ragdoll Installer",
            priority = 221)]
        public static void BuildInstaller()
        {
            Build(exportInstaller: true, importCheckedInInstaller: false);
        }

        /// <summary>
        /// Reimports and validates the checked-in installer without resaving its payload or
        /// exporting or otherwise rewriting the archive.
        /// </summary>
        public static void BuildValidationFixture()
        {
            Build(exportInstaller: false, importCheckedInInstaller: true);
        }

        private static void Build(bool exportInstaller, bool importCheckedInInstaller)
        {
            string originalGuid;
            string[] originalLabels;
            if (importCheckedInInstaller)
            {
                NetworkRagdollDemoEditorUtility.RebuildValidationInstallRoot(
                    PackagePath,
                    PreviousRoot,
                    InstallRoot);
                CapturePlayerIdentity(PlayerPrefab, out originalGuid, out originalLabels);

                GameObject importedPlayer =
                    AssetDatabase.LoadAssetAtPath<GameObject>(PlayerPrefab);
                ValidatePlayerIdentity(importedPlayer, originalGuid, originalLabels);
                Scene importedScene = EditorSceneManager.OpenScene(
                    RagdollScene,
                    OpenSceneMode.Single);
                ValidateScene(importedScene, importedPlayer);

                Debug.Log(
                    "[FusionCoreRagdollDemoBuilder] Imported and validated the Fusion Core " +
                    "Examples 1.1.0 fixture without exporting its archive.");
                return;
            }
            else
            {
                string originalPrefabPath = ResolveExistingPlayerPrefab();
                CapturePlayerIdentity(
                    originalPrefabPath,
                    out originalGuid,
                    out originalLabels);
                NetworkRagdollDemoEditorUtility.EnsureInstallRoot(
                    PreviousRoot,
                    InstallRoot);
            }

            GameObject originalPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(PlayerPrefab);
            if (originalPrefab == null)
                throw new FileNotFoundException("The migrated Fusion Core player is missing.", PlayerPrefab);
            if (!originalLabels.Contains("FusionPrefab", StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    "The existing Fusion Core player lost its FusionPrefab label. Restore the " +
                    "1.0.2/1.1.0 fixture before rebuilding the installer.");
            }

            GameObject player = NetworkRagdollDemoEditorUtility.ConfigurePlayerPrefab(PlayerPrefab);
            ValidatePlayerIdentity(player, originalGuid, originalLabels);

            Scene scene = NetworkRagdollDemoEditorUtility.CopyAndConfigureRagdollScene(
                SourceScene,
                RagdollScene,
                "Fusion Core + Network Ragdoll Demo");
            ValidateScene(scene, player);

            AssetDatabase.SaveAssets();
            NetworkProjectConfigUtilities.RebuildPrefabTable();
            AssetDatabase.SaveAssets();

            if (exportInstaller)
            {
                NetworkRagdollDemoEditorUtility.ExportExactInstallRoot(
                    InstallRoot,
                    PackagePath);
            }

            Debug.Log(
                "[FusionCoreRagdollDemoBuilder] Built the exact Fusion Core Examples " +
                "1.1.0 installer payload.");
        }

        private static string ResolveExistingPlayerPrefab()
        {
            if (AssetDatabase.LoadAssetAtPath<GameObject>(PlayerPrefab) != null)
                return PlayerPrefab;
            if (AssetDatabase.LoadAssetAtPath<GameObject>(PreviousPlayerPrefab) != null)
                return PreviousPlayerPrefab;
            throw new FileNotFoundException(
                "Neither the Fusion Core Examples 1.0.2 nor 1.1.0 player prefab exists.",
                PlayerPrefab);
        }

        private static void CapturePlayerIdentity(
            string prefabPath,
            out string guid,
            out string[] labels)
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null)
                throw new FileNotFoundException("The Fusion Core player is missing.", prefabPath);

            guid = AssetDatabase.AssetPathToGUID(prefabPath);
            labels = AssetDatabase.GetLabels(prefab)
                .OrderBy(label => label, StringComparer.Ordinal)
                .ToArray();
            if (string.IsNullOrEmpty(guid))
                throw new InvalidOperationException("The Fusion Core player has no stable asset GUID.");
        }

        private static void ValidatePlayerIdentity(
            GameObject player,
            string expectedGuid,
            string[] expectedLabels)
        {
            if (player == null ||
                player.GetComponent<NetworkObject>() == null ||
                player.GetComponent<FusionNetworkIdentity>() == null ||
                player.GetComponent<FusionNetworkCharacterAuto>() == null)
            {
                throw new InvalidOperationException(
                    "The existing Fusion Core player must retain NetworkObject, " +
                    "FusionNetworkIdentity, and FusionNetworkCharacterAuto.");
            }

            string actualGuid = AssetDatabase.AssetPathToGUID(PlayerPrefab);
            string[] actualLabels = AssetDatabase.GetLabels(player)
                .OrderBy(label => label, StringComparer.Ordinal)
                .ToArray();
            if (!string.Equals(actualGuid, expectedGuid, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The Fusion Core player GUID changed while configuring ragdoll support.");
            }
            if (!actualLabels.SequenceEqual(expectedLabels, StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    "The Fusion Core player labels changed while configuring ragdoll support.");
            }
        }

        private static void ValidateScene(Scene scene, GameObject player)
        {
            if (!scene.IsValid() || !scene.isLoaded || scene.path != RagdollScene)
                throw new InvalidOperationException("The generated Fusion ragdoll scene is not loaded.");

            FusionPlayerSpawner spawner = RequireSingle<FusionPlayerSpawner>(scene);
            if (spawner.PlayerPrefab == null ||
                !string.Equals(
                    AssetDatabase.GetAssetPath(spawner.PlayerPrefab),
                    PlayerPrefab,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The generated Fusion ragdoll scene no longer references the existing " +
                    "Core player prefab from its player spawner.");
            }
            if (spawner.PlayerPrefab.gameObject != player)
            {
                throw new InvalidOperationException(
                    "The Fusion player spawner did not retain the configured prefab object.");
            }

            RequireSingle<FusionTransportBridge>(scene);
            RequireSingle<FusionSessionBootstrap>(scene);
            RequireSingle<FusionCoreTransportBridge>(scene);
            RequireSingle<FusionAnimationMotionTransportBridge>(scene);
            RequireSingle<NetworkCoreManager>(scene);
            RequireSingle<NetworkMotionManager>(scene);
            RequireSingle<NetworkAnimationManager>(scene);
            RequireSingle<NetworkSecurityManager>(scene);
            RequireSingle<NetworkRagdollDemoUI>(scene);

            if (FindByName(scene, "Main Camera") == null ||
                FindByName(scene, "Floor") == null)
            {
                throw new InvalidOperationException(
                    "The generated Fusion ragdoll scene lost its camera or floor infrastructure.");
            }

            string[] removedNames =
            {
                "Scene Network Variables",
                "Variable Values UI",
                "Network Actions Door Demo",
                "Network Actions Door Demo UI",
                "Network Action Manager",
                "Network Variable Manager",
                "Fusion Network Actions Bridge",
                "Fusion Variable Bridge"
            };
            for (int i = 0; i < removedNames.Length; i++)
            {
                if (FindByName(scene, removedNames[i]) != null)
                {
                    throw new InvalidOperationException(
                        $"The generated ragdoll scene retained demo-only root " +
                        $"'{removedNames[i]}'.");
                }
            }
        }

        private static T RequireSingle<T>(Scene scene) where T : Component
        {
            T[] matches = scene.GetRootGameObjects()
                .SelectMany(root => root.GetComponentsInChildren<T>(true))
                .ToArray();
            if (matches.Length != 1)
            {
                throw new InvalidOperationException(
                    $"The generated Fusion ragdoll scene requires exactly one " +
                    $"{typeof(T).Name}; found {matches.Length}.");
            }
            return matches[0];
        }

        private static GameObject FindByName(Scene scene, string name)
        {
            return scene.GetRootGameObjects()
                .SelectMany(root => root.GetComponentsInChildren<Transform>(true))
                .FirstOrDefault(candidate =>
                    candidate != null &&
                    string.Equals(candidate.name, name, StringComparison.Ordinal))
                ?.gameObject;
        }
    }
}
