using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Arawn.GameCreator2.Networking.Editor;
using PurrNet;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Arawn.GameCreator2.Networking.Transport.PurrNet.Editor
{
    public static class PurrNetEnemyShooterDemoBuilder
    {
        private const string CurrentRoot =
            "Assets/Plugins/GameCreator/Installs/GC2NetworkingLayerPurrNetTransport.ShooterExamples@1.0.2";
        private const string InstallRoot =
            "Assets/Plugins/GameCreator/Installs/GC2NetworkingLayerPurrNetTransport.ShooterExamples@1.1.0";
        private const string PlayerPrefab = InstallRoot + "/PurrNetDemoPlayer-ShooterAndStats.prefab";
        private const string EnemyPrefab = InstallRoot + "/PurrNetDemoEnemy-ShooterAndStats.prefab";
        private const string PvpScene = InstallRoot + "/Requires Shooter Demos - PurrNetShooterStatsDemo.unity";
        private const string EnemyScene = InstallRoot + "/Requires Shooter Demos - PurrNetEnemyShooterDemo.unity";
        private const string Weapon = InstallRoot + "/Weapons/PurrNetDemo_AK_Weapon.asset";
        private const string Monitor = InstallRoot + "/PurrNetShooterStatsDemoMonitor.cs";
        private const string Profiles = InstallRoot + "/Profiles";
        private const string NetworkPrefabsPath = Profiles + "/PurrNetDemoNetworkPrefabs 37.asset";
        private const string VariableProfile = Profiles + "/PurrNetDemoNetworkVariableProfile 21.asset";
        private const string SceneVariableProfile = Profiles + "/PurrNetDemoSceneNetworkVariableProfile 20.asset";
        private const string Package =
            "Assets/Arawn/NetworkingLayerForGC2/Demo/PurrNet/Packages/Shooter/Package.unitypackage";

        [MenuItem("Game Creator/Networking Layer/Demos/Rebuild PurrNet Enemy Shooter Installer", priority = 221)]
        public static void BuildInstaller()
        {
            NetworkEnemyShooterDemoEditorUtility.MoveInstallRoot(CurrentRoot, InstallRoot);
            GameObject enemy = NetworkEnemyShooterDemoEditorUtility.BuildEnemyPrefab(
                PlayerPrefab,
                EnemyPrefab,
                Weapon,
                ConfigureEnemyTransport);
            RegisterEnemyPrefab(enemy);

            Scene scene = NetworkEnemyShooterDemoEditorUtility.CopyAndOpenScene(PvpScene, EnemyScene);
            ConfigureScene(scene, enemy);
            string navData = NetworkEnemyShooterDemoEditorUtility.BakeNavMesh(
                scene,
                FindSceneComponent<PurrNetDemoPlayerSpawner>()?.transform.parent?.gameObject);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();

            string navFolder = Path.GetDirectoryName(navData)?.Replace('\\', '/');
            NetworkEnemyShooterDemoEditorUtility.ExportExactPackage(
                new[]
                {
                    InstallRoot,
                    Profiles,
                    NetworkPrefabsPath,
                    VariableProfile,
                    SceneVariableProfile,
                    PlayerPrefab,
                    Monitor,
                    PvpScene,
                    InstallRoot + "/Weapons",
                    Weapon,
                    EnemyPrefab,
                    EnemyScene,
                    navFolder,
                    navData
                },
                Package);
            Debug.Log("[PurrNetEnemyShooterDemoBuilder] Built exact 1.1.0 installer payload.");
        }

        public static void BuildNetworkSmokePlayer()
        {
            if (AssetDatabase.IsValidFolder(CurrentRoot)) BuildInstaller();
            else if (!AssetDatabase.IsValidFolder(InstallRoot))
                throw new DirectoryNotFoundException(
                    "Neither the 1.0.2 source fixture nor generated 1.1.0 PurrNet demo exists.");
            NetworkEnemyShooterDemoEditorUtility.BuildStandaloneSmokePlayer(
                EnemyScene,
                "PurrNet");
        }

        private static void ConfigureEnemyTransport(GameObject root, List<string> changes)
        {
            Ensure<NetworkIdentity>(root, changes);
            Ensure<PurrNetNetworkCharacterAuto>(root, changes);
            MonoBehaviour[] behaviours = root.GetComponentsInChildren<MonoBehaviour>(true);
            for (int i = 0; i < behaviours.Length; i++)
            {
                MonoBehaviour behaviour = behaviours[i];
                if (behaviour == null) continue;
                string name = behaviour.GetType().FullName ?? string.Empty;
                if (!name.Contains("PurrDiction", StringComparison.Ordinal)) continue;
                UnityEngine.Object.DestroyImmediate(behaviour, true);
                changes.Add($"removed {behaviour.GetType().Name}");
            }
        }

        private static void RegisterEnemyPrefab(GameObject enemy)
        {
            NetworkPrefabs prefabs = AssetDatabase.LoadAssetAtPath<NetworkPrefabs>(
                NetworkPrefabsPath);
            if (prefabs == null) throw new FileNotFoundException("PurrNet prefab registry is missing.");
            if (prefabs.prefabs.Any(entry => entry.prefab == enemy)) return;
            prefabs.prefabs.Add(new NetworkPrefabs.UserPrefabData
            {
                guid = AssetDatabase.AssetPathToGUID(EnemyPrefab),
                prefab = enemy,
                pooled = false,
                warmupCount = 0
            });
            prefabs.Refresh();
            EditorUtility.SetDirty(prefabs);
        }

        private static void ConfigureScene(Scene scene, GameObject enemy)
        {
            NetworkManager manager = FindSceneComponent<NetworkManager>();
            PurrNetDemoPlayerSpawner spawner = FindSceneComponent<PurrNetDemoPlayerSpawner>();
            if (manager == null || spawner == null)
                throw new InvalidOperationException(
                    "The PurrNet Shooter fixture is missing its NetworkManager or player spawner.");

            var slotsObject = new GameObject("PurrNet Enemy Bot Slots");
            if (spawner.transform.parent != null)
                slotsObject.transform.SetParent(spawner.transform.parent, false);
            PurrNetBotSlotCoordinator coordinator =
                slotsObject.AddComponent<PurrNetBotSlotCoordinator>();
            List<Transform> anchors =
                NetworkEnemyShooterDemoEditorUtility.CreateSlotAnchors(slotsObject, 3);
            NetworkEnemyShooterDemoEditorUtility.ConfigureSlots(
                coordinator,
                anchors,
                enemy,
                "purrnet-enemy-slot");

            var coordinatorObject = new SerializedObject(coordinator);
            SetObject(coordinatorObject, "m_NetworkManager", manager);
            coordinatorObject.ApplyModifiedPropertiesWithoutUndo();

            var spawnerObject = new SerializedObject(spawner);
            SetObject(spawnerObject, "m_BotSlotCoordinator", coordinator);
            spawnerObject.ApplyModifiedPropertiesWithoutUndo();
            EditorSceneManager.MarkSceneDirty(scene);
        }

        private static T FindSceneComponent<T>() where T : Component
        {
            return UnityObjectSearch.FindAll<T>(FindObjectsInactive.Include)
                .FirstOrDefault(item => item.gameObject.scene == SceneManager.GetActiveScene());
        }

        private static T Ensure<T>(GameObject root, ICollection<string> changes)
            where T : Component
        {
            T component = root.GetComponent<T>();
            if (component != null) return component;
            component = root.AddComponent<T>();
            changes.Add(typeof(T).Name);
            return component;
        }

        private static void SetObject(
            SerializedObject serialized,
            string propertyName,
            UnityEngine.Object value)
        {
            SerializedProperty property = serialized.FindProperty(propertyName);
            if (property != null) property.objectReferenceValue = value;
        }
    }
}
