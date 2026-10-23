using System;
using System.Collections.Generic;
using System.IO;
using Arawn.GameCreator2.Networking.Editor;
using PurrNet;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Arawn.GameCreator2.Networking.Transport.PurrNet.Editor
{
    /// <summary>
    /// Builds the optional PurrNet Malbers Animal Controller demo from the installed PurrNet
    /// Core example. The generated scene references the licensed Malbers package and owns
    /// only its player/NPC prefabs, scene, HUD, exact two-prefab registry, and installer payload.
    /// </summary>
    public static class PurrNetMalbersDemoBuilder
    {
        private const string SourceRoot =
            "Assets/Plugins/GameCreator/Installs/" +
            "GC2NetworkingLayerPurrNetTransport.CoreExamples@1.1.0";
        private const string InstallRoot =
            "Assets/Plugins/GameCreator/Installs/" +
            "GC2NetworkingLayerPurrNetTransport.MalbersExamples@1.0.0";

        private const string SourcePlayerName = "PurrNetDemoPlayer-CoreAndVariables 3.prefab";
        private const string SourceSceneName =
            "Requires GC2 Core Demos - PurrNetCoreVariablesDemo.unity";
        private const string PlayerName = "PurrNetDemoPlayer-Malbers.prefab";
        private const string NpcName = "PurrNetDemoNpc-Malbers.prefab";
        private const string SceneName =
            "Requires Malbers Animal Controller - PurrNetMalbersDemo.unity";

        private const string SourcePlayer = SourceRoot + "/" + SourcePlayerName;
        private const string CopiedPlayer = InstallRoot + "/" + SourcePlayerName;
        private const string CopiedScene = InstallRoot + "/" + SourceSceneName;
        private const string PlayerPrefab = InstallRoot + "/" + PlayerName;
        private const string NpcPrefab = InstallRoot + "/" + NpcName;
        private const string DemoScene = InstallRoot + "/" + SceneName;
        private const string Profiles = InstallRoot + "/Profiles";
        private const string NetworkPrefabsPath =
            Profiles + "/PurrNetMalbersNetworkPrefabs.asset";
        private const string Package =
            "Assets/Arawn/NetworkingLayerForGC2/Demo/PurrNet/Packages/Malbers/Package.unitypackage";

        private const string SpawnerTypeName =
            "Arawn.GameCreator2.Networking.Transport.PurrNet.PurrNetDemoPlayerSpawner";

        [MenuItem(
            "Game Creator/Networking Layer/Demos/Rebuild PurrNet Malbers Installer",
            priority = 226)]
        public static void BuildInstaller()
        {
            Build(exportInstaller: true);
        }

        /// <summary>
        /// Rebuilds the generated install root for isolated CI validation without exporting or
        /// otherwise modifying the checked-in installer archive.
        /// </summary>
        public static void BuildValidationFixture()
        {
            Build(exportInstaller: false);
        }

        /// <summary>Rebuilds the fixture and produces the single-scene standalone smoke player.</summary>
        public static void BuildNetworkSmokePlayer()
        {
            BuildValidationFixture();
            NetworkEnemyShooterDemoEditorUtility.BuildStandaloneSmokePlayer(
                DemoScene,
                "PurrNet Malbers",
                replaceMaterialsForSmoke: true);
        }

        private static void Build(bool exportInstaller)
        {
            RequireDependencies();

            NetworkFreeFlowCombatDemoEditorUtility.RecreateInstallRoot(
                SourceRoot,
                InstallRoot,
                "MalbersExamples@");
            NetworkFreeFlowCombatDemoEditorUtility.RenameGeneratedAsset(CopiedScene, DemoScene);
            RecreateProfilesFolder();

            GameObject player = NetworkMalbersDemoEditorUtility.SaveMalbersPlayerPrefab(
                PlayerPrefab,
                configureAsNpc: false,
                CopiedPlayer);
            GameObject npc = NetworkMalbersDemoEditorUtility.CopyNpcPrefab(PlayerPrefab, NpcPrefab);
            NetworkPrefabs networkPrefabs = CreateNetworkPrefabs(player, npc);

            Scene scene = EditorSceneManager.OpenScene(DemoScene, OpenSceneMode.Single);
            if (!scene.IsValid() || !scene.isLoaded)
            {
                throw new InvalidOperationException(
                    $"Could not open the generated PurrNet Malbers scene '{DemoScene}'.");
            }

            player = RequireGeneratedAsset<GameObject>(PlayerPrefab);
            npc = RequireGeneratedAsset<GameObject>(NpcPrefab);
            networkPrefabs = RequireGeneratedAsset<NetworkPrefabs>(NetworkPrefabsPath);
            NetworkMalbersDemoEditorUtility.ReplacePlayerReferences(scene, SourcePlayer, player);
            NetworkMalbersDemoEditorUtility.ConfigureSpawner(scene, player, SpawnerTypeName);
            ConfigureNetworkManager(scene, networkPrefabs);
            NetworkMalbersDemoEditorUtility.CreateNpcInstances(
                scene,
                npc,
                "Malbers Network NPCs");
            NetworkMalbersDemoEditorUtility.RemoveCoreVariableDemoObjects(scene);
            NetworkMalbersDemoEditorUtility.AddHud(scene);
            NetworkMalbersDemoEditorUtility.ConfigureFootstepDetectors(scene);
            EditorSceneManager.SaveScene(scene);

            NetworkMalbersDemoEditorUtility.RemoveGeneratedSourcePlayer(
                InstallRoot,
                SourcePlayerName);
            NetworkMalbersDemoEditorUtility.PruneInstallRoot(
                InstallRoot,
                DemoScene,
                PlayerPrefab,
                NpcPrefab,
                NetworkPrefabsPath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

            if (exportInstaller)
            {
                EnsurePackageFolder();
                NetworkFreeFlowCombatDemoEditorUtility.ExportExactInstallRoot(
                    InstallRoot,
                    Package);
            }

            NetworkMalbersDemoEditorUtility.CreateInstallerAsset(
                "Assets/Arawn/NetworkingLayerForGC2/Demo/PurrNet/Packages/Malbers/" +
                "GC2NetworkingLayerPurrNetTransport.MalbersExamples.asset",
                "Malbers Examples",
                "GC2 Networking Layer PurrNet Transport",
                "PurrNet Malbers Animal Controller state replication example.",
                "GC2NetworkingLayerPurrNetTransport.Assets");

            Debug.Log(
                exportInstaller
                    ? "[PurrNetMalbersDemoBuilder] Built the Malbers installer payload with one " +
                      "PlayerOwned prefab, one server NPC prefab, a scene, the exact two-prefab " +
                      "registry, and the Malbers HUD."
                    : "[PurrNetMalbersDemoBuilder] Rebuilt the Malbers validation fixture without " +
                      "exporting its archive.");
        }

        private static void RequireDependencies()
        {
            if (AssetDatabase.LoadMainAssetAtPath(NetworkMalbersDemoEditorUtility.MalbersPlayerScene) == null)
            {
                throw new FileNotFoundException(
                    "The Malbers GC2 integration scene is missing. Install Malbers Animal " +
                    "Controller and its Game Creator 2 integration before building the demo.",
                    NetworkMalbersDemoEditorUtility.MalbersPlayerScene);
            }

            if (AssetDatabase.LoadMainAssetAtPath(SourcePlayer) == null ||
                AssetDatabase.LoadMainAssetAtPath(SourceRoot + "/" + SourceSceneName) == null)
            {
                throw new FileNotFoundException(
                    "The installed PurrNet Core example is required as the demo base.",
                    SourcePlayer);
            }
        }

        private static void RecreateProfilesFolder()
        {
            if (AssetDatabase.IsValidFolder(Profiles) &&
                !AssetDatabase.DeleteAsset(Profiles))
            {
                throw new IOException(
                    $"Could not replace the generated profiles folder '{Profiles}'.");
            }

            string guid = AssetDatabase.CreateFolder(InstallRoot, "Profiles");
            if (string.IsNullOrEmpty(guid) || !AssetDatabase.IsValidFolder(Profiles))
            {
                throw new IOException(
                    $"Could not create the generated profiles folder '{Profiles}'.");
            }
        }

        private static NetworkPrefabs CreateNetworkPrefabs(
            GameObject player,
            GameObject npc)
        {
            if (player == null) throw new ArgumentNullException(nameof(player));
            if (npc == null) throw new ArgumentNullException(nameof(npc));
            if (AssetDatabase.LoadMainAssetAtPath(NetworkPrefabsPath) != null &&
                !AssetDatabase.DeleteAsset(NetworkPrefabsPath))
            {
                throw new IOException(
                    $"Could not replace the generated PurrNet registry '{NetworkPrefabsPath}'.");
            }

            NetworkPrefabs prefabs = ScriptableObject.CreateInstance<NetworkPrefabs>();
            prefabs.name = Path.GetFileNameWithoutExtension(NetworkPrefabsPath);
            prefabs.autoGenerate = false;
            prefabs.networkOnly = true;
            prefabs.poolByDefault = false;
            prefabs.folder = null;
            prefabs.searchAllIfNoFolder = false;
            prefabs.linkedNetworkPrefabs = new List<NetworkPrefabs>();
            prefabs.prefabs = new List<NetworkPrefabs.UserPrefabData>
            {
                CreateRegistryEntry(PlayerPrefab, player),
                CreateRegistryEntry(NpcPrefab, npc)
            };

            AssetDatabase.CreateAsset(prefabs, NetworkPrefabsPath);
            prefabs.Refresh();
            EditorUtility.SetDirty(prefabs);
            AssetDatabase.SaveAssets();
            return prefabs;
        }

        private static NetworkPrefabs.UserPrefabData CreateRegistryEntry(
            string prefabPath,
            GameObject prefab)
        {
            string guid = AssetDatabase.AssetPathToGUID(prefabPath);
            if (string.IsNullOrEmpty(guid) || prefab == null)
            {
                throw new InvalidOperationException(
                    $"The generated network prefab is not importable: '{prefabPath}'.");
            }

            return new NetworkPrefabs.UserPrefabData
            {
                guid = guid,
                prefab = prefab,
                pooled = false,
                warmupCount = 0
            };
        }

        private static void ConfigureNetworkManager(Scene scene, NetworkPrefabs networkPrefabs)
        {
            NetworkManager manager = FindSceneComponent<NetworkManager>(scene);
            if (manager == null)
            {
                throw new InvalidOperationException(
                    "The copied PurrNet Core scene has no NetworkManager.");
            }

            var serialized = new SerializedObject(manager);
            SerializedProperty property = serialized.FindProperty("_networkPrefabs");
            if (property == null)
                throw new MissingFieldException(typeof(NetworkManager).FullName, "_networkPrefabs");

            property.objectReferenceValue = networkPrefabs;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(manager);
        }

        private static T FindSceneComponent<T>(Scene scene) where T : Component
        {
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                T component = root.GetComponentInChildren<T>(true);
                if (component != null) return component;
            }

            return null;
        }

        private static void EnsurePackageFolder()
        {
            string folder = Path.GetDirectoryName(Package)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(folder))
                NetworkMalbersDemoEditorUtility.EnsureAssetFolder(folder);
        }

        private static T RequireGeneratedAsset<T>(string path) where T : UnityEngine.Object
        {
            T asset = AssetDatabase.LoadAssetAtPath<T>(path);
            if (asset == null)
                throw new FileNotFoundException($"Generated asset is not importable: '{path}'.", path);
            return asset;
        }
    }
}
