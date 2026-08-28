using System;
using System.IO;
using System.Linq;
using Arawn.GameCreator2.Networking.Editor;
using Arawn.GameCreator2.Networking.Security;
using PurrNet;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Arawn.GameCreator2.Networking.Transport.PurrNet.Editor
{
    /// <summary>
    /// Upgrades the existing PurrNet Core Examples installer in place and adds the focused
    /// server-authoritative ragdoll lifecycle demo without replacing its player or prefab
    /// registry assets.
    /// </summary>
    public static class PurrNetCoreRagdollDemoBuilder
    {
        private const string PreviousRoot =
            "Assets/Plugins/GameCreator/Installs/" +
            "GC2NetworkingLayerPurrNetTransport.CoreExamples@1.0.2";
        private const string InstallRoot =
            "Assets/Plugins/GameCreator/Installs/" +
            "GC2NetworkingLayerPurrNetTransport.CoreExamples@1.1.0";

        private const string PreviousPlayerPrefab =
            PreviousRoot + "/PurrNetDemoPlayer-CoreAndVariables 3.prefab";
        private const string PlayerPrefab =
            InstallRoot + "/PurrNetDemoPlayer-CoreAndVariables 3.prefab";
        private const string SourceScene =
            InstallRoot + "/Requires GC2 Core Demos - PurrNetCoreVariablesDemo.unity";
        private const string RagdollScene =
            InstallRoot + "/Requires GC2 Core Demos - PurrNetRagdollDemo.unity";
        private const string NetworkPrefabsPath =
            InstallRoot + "/Profiles/PurrNetDemoNetworkPrefabs 35.asset";
        private const string PreviousNetworkPrefabsPath =
            PreviousRoot + "/Profiles/PurrNetDemoNetworkPrefabs 35.asset";
        private const string PackagePath =
            "Assets/Arawn/NetworkingLayerForGC2/Demo/PurrNet/Packages/Core/" +
            "Package.unitypackage";

        [MenuItem(
            "Game Creator/Networking Layer/Demos/Rebuild PurrNet Core Ragdoll Installer",
            priority = 225)]
        public static void BuildInstaller()
        {
            Build(exportInstaller: true, importCheckedInInstaller: false);
        }

        /// <summary>
        /// Imports the checked-in archive into a clean install root for isolated validation.
        /// It validates the imported payload without resaving, exporting, or otherwise rewriting
        /// the checked-in archive.
        /// </summary>
        public static void BuildValidationFixture()
        {
            Build(exportInstaller: false, importCheckedInInstaller: true);
        }

        private static void Build(bool exportInstaller, bool importCheckedInInstaller)
        {
            string originalPlayerGuid;
            string originalRegistryGuid;
            string[] originalPlayerLabels;
            if (importCheckedInInstaller)
            {
                NetworkRagdollDemoEditorUtility.RebuildValidationInstallRoot(
                    PackagePath,
                    PreviousRoot,
                    InstallRoot);
                CaptureAssetIdentity(
                    PlayerPrefab,
                    NetworkPrefabsPath,
                    out originalPlayerGuid,
                    out originalRegistryGuid,
                    out originalPlayerLabels);

                Scene importedScene = EditorSceneManager.OpenScene(
                    RagdollScene,
                    OpenSceneMode.Single);
                ValidateTransportFixture(
                    importedScene,
                    originalPlayerGuid,
                    originalRegistryGuid,
                    originalPlayerLabels);

                Debug.Log(
                    "[PurrNetCoreRagdollDemoBuilder] Imported and validated the PurrNet " +
                    "Core Examples 1.1.0 fixture without exporting its archive.");
                return;
            }
            else
            {
                string existingPlayer = ResolveExistingPlayerPrefab();
                string existingRegistry = string.Equals(
                    existingPlayer,
                    PreviousPlayerPrefab,
                    StringComparison.Ordinal)
                    ? PreviousNetworkPrefabsPath
                    : NetworkPrefabsPath;
                CaptureAssetIdentity(
                    existingPlayer,
                    existingRegistry,
                    out originalPlayerGuid,
                    out originalRegistryGuid,
                    out originalPlayerLabels);
                NetworkRagdollDemoEditorUtility.EnsureInstallRoot(
                    PreviousRoot,
                    InstallRoot);
            }

            NetworkRagdollDemoEditorUtility.ConfigurePlayerPrefab(PlayerPrefab);
            Scene scene = NetworkRagdollDemoEditorUtility.CopyAndConfigureRagdollScene(
                SourceScene,
                RagdollScene,
                "PurrNet Core + Network Ragdoll Demo");
            ValidateTransportFixture(
                scene,
                originalPlayerGuid,
                originalRegistryGuid,
                originalPlayerLabels);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

            if (exportInstaller)
            {
                NetworkRagdollDemoEditorUtility.ExportExactInstallRoot(
                    InstallRoot,
                    PackagePath);
            }

            Debug.Log(
                "[PurrNetCoreRagdollDemoBuilder] Built the exact PurrNet Core Examples " +
                "1.1.0 installer payload.");
        }

        private static string ResolveExistingPlayerPrefab()
        {
            if (AssetDatabase.LoadAssetAtPath<GameObject>(PlayerPrefab) != null)
                return PlayerPrefab;
            if (AssetDatabase.LoadAssetAtPath<GameObject>(PreviousPlayerPrefab) != null)
                return PreviousPlayerPrefab;
            throw new FileNotFoundException(
                "Neither the PurrNet Core Examples 1.0.2 nor 1.1.0 player prefab exists.",
                PlayerPrefab);
        }

        private static void CaptureAssetIdentity(
            string playerPath,
            string registryPath,
            out string playerGuid,
            out string registryGuid,
            out string[] playerLabels)
        {
            GameObject player = AssetDatabase.LoadAssetAtPath<GameObject>(playerPath);
            NetworkPrefabs registry = AssetDatabase.LoadAssetAtPath<NetworkPrefabs>(registryPath);
            if (player == null)
                throw new FileNotFoundException("The PurrNet Core player is missing.", playerPath);
            if (registry == null)
                throw new FileNotFoundException(
                    "The PurrNet Core prefab registry is missing.",
                    registryPath);

            playerGuid = AssetDatabase.AssetPathToGUID(playerPath);
            registryGuid = AssetDatabase.AssetPathToGUID(registryPath);
            playerLabels = AssetDatabase.GetLabels(player)
                .OrderBy(label => label, StringComparer.Ordinal)
                .ToArray();
            if (string.IsNullOrEmpty(playerGuid) || string.IsNullOrEmpty(registryGuid))
            {
                throw new InvalidOperationException(
                    "The existing PurrNet Core player or prefab registry has no stable GUID.");
            }
        }

        private static void ValidateTransportFixture(
            Scene scene,
            string expectedPlayerGuid,
            string expectedRegistryGuid,
            string[] expectedPlayerLabels)
        {
            if (!scene.IsValid() || !scene.isLoaded || scene.path != RagdollScene)
                throw new InvalidOperationException(
                    "The generated PurrNet ragdoll scene is not loaded.");

            GameObject player = AssetDatabase.LoadAssetAtPath<GameObject>(PlayerPrefab);
            if (player == null)
                throw new FileNotFoundException(
                    "The PurrNet Core player prefab is missing.",
                    PlayerPrefab);
            if (player.GetComponent<NetworkIdentity>() == null ||
                player.GetComponent<PurrNetNetworkCharacterAuto>() == null)
            {
                throw new InvalidOperationException(
                    "The PurrNet Core player must retain NetworkIdentity and " +
                    "PurrNetNetworkCharacterAuto on its root.");
            }

            string actualPlayerGuid = AssetDatabase.AssetPathToGUID(PlayerPrefab);
            string[] actualPlayerLabels = AssetDatabase.GetLabels(player)
                .OrderBy(label => label, StringComparer.Ordinal)
                .ToArray();
            if (!string.Equals(actualPlayerGuid, expectedPlayerGuid, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "The PurrNet Core player GUID changed while configuring ragdoll support.");
            if (!actualPlayerLabels.SequenceEqual(
                    expectedPlayerLabels,
                    StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    "The PurrNet Core player labels changed while configuring ragdoll support.");
            }

            NetworkPrefabs networkPrefabs =
                AssetDatabase.LoadAssetAtPath<NetworkPrefabs>(NetworkPrefabsPath);
            if (networkPrefabs == null)
                throw new FileNotFoundException(
                    "The PurrNet Core prefab registry is missing.",
                    NetworkPrefabsPath);

            string actualRegistryGuid = AssetDatabase.AssetPathToGUID(NetworkPrefabsPath);
            if (!string.Equals(
                    actualRegistryGuid,
                    expectedRegistryGuid,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The PurrNet Core prefab registry GUID changed while configuring the " +
                    "ragdoll demo.");
            }

            string playerGuid = AssetDatabase.AssetPathToGUID(PlayerPrefab);
            int matchingEntries = networkPrefabs.prefabs?.Count(entry =>
                entry.prefab == player &&
                string.Equals(entry.guid, playerGuid, StringComparison.Ordinal)) ?? 0;
            if (matchingEntries != 1 || networkPrefabs.prefabs.Count != 1)
            {
                throw new InvalidOperationException(
                    "The PurrNet Core prefab registry must retain exactly one entry for the " +
                    "existing Core player prefab.");
            }

            NetworkManager[] managers = FindSceneComponents<NetworkManager>(scene);
            PurrNetTransportBridge[] transports =
                FindSceneComponents<PurrNetTransportBridge>(scene);
            PurrNetDemoPlayerSpawner[] spawners =
                FindSceneComponents<PurrNetDemoPlayerSpawner>(scene);
            PurrNetAnimationMotionTransportBridge[] motionBridges =
                FindSceneComponents<PurrNetAnimationMotionTransportBridge>(scene);
            if (managers.Length != 1 ||
                transports.Length != 1 ||
                spawners.Length != 1 ||
                motionBridges.Length != 1)
            {
                throw new InvalidOperationException(
                    "The PurrNet ragdoll scene must retain exactly one NetworkManager, " +
                    "PurrNetTransportBridge, PurrNetAnimationMotionTransportBridge, and " +
                    "PurrNetDemoPlayerSpawner.");
            }

            var managerObject = new SerializedObject(managers[0]);
            SerializedProperty managerPrefabs = managerObject.FindProperty("_networkPrefabs");
            if (managerPrefabs == null || managerPrefabs.objectReferenceValue != networkPrefabs)
            {
                throw new InvalidOperationException(
                    "The PurrNet NetworkManager no longer references the existing Core " +
                    "prefab registry.");
            }

            var spawnerObject = new SerializedObject(spawners[0]);
            SerializedProperty playerProperty = spawnerObject.FindProperty("m_PlayerPrefab");
            if (playerProperty == null || playerProperty.objectReferenceValue != player)
            {
                throw new InvalidOperationException(
                    "The PurrNet demo spawner no longer references the existing Core player " +
                    "prefab.");
            }

            RequireSingle<NetworkCoreManager>(scene);
            RequireSingle<NetworkMotionManager>(scene);
            RequireSingle<NetworkAnimationManager>(scene);
            RequireSingle<NetworkSecurityManager>(scene);
            RequireSingle<NetworkRagdollDemoUI>(scene);

            if (FindByName(scene, "Main Camera") == null ||
                FindByName(scene, "Floor") == null)
            {
                throw new InvalidOperationException(
                    "The generated PurrNet ragdoll scene lost its camera or floor " +
                    "infrastructure.");
            }

            string[] removedNames =
            {
                "Scene Network Variables",
                "Variable Values UI",
                "Network Actions Door Demo",
                "Network Actions Door Demo UI",
                "Network Action Manager",
                "Network Variable Manager",
                "PurrNet Network Actions Bridge",
                "PurrNet Variable Bridge"
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

        private static T[] FindSceneComponents<T>(Scene scene) where T : Component
        {
            return scene.GetRootGameObjects()
                .SelectMany(root => root.GetComponentsInChildren<T>(true))
                .ToArray();
        }

        private static T RequireSingle<T>(Scene scene) where T : Component
        {
            T[] matches = FindSceneComponents<T>(scene);
            if (matches.Length != 1)
            {
                throw new InvalidOperationException(
                    $"The generated PurrNet ragdoll scene requires exactly one " +
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
