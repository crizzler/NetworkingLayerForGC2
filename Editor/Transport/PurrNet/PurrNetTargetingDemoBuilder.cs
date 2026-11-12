using System.Collections.Generic;
using System.Linq;
using Arawn.GameCreator2.Networking.Editor;
using PurrNet;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Arawn.GameCreator2.Networking.Transport.PurrNet.Editor
{
    public static class PurrNetTargetingDemoBuilder
    {
        [MenuItem("Game Creator/Networking Layer/Demos/Rebuild PurrNet Closest Target Melee Installer", priority = 227)]
        public static void BuildInstaller() { BuildValidationFixture(); NetworkTargetingDemoEditorUtility.Export("PurrNet"); }

        public static void BuildValidationFixture()
        {
            NetworkTargetingDemoEditorUtility.Build("PurrNet", ConfigureNpc, ConfigureSession);
            Debug.Log("[TargetingDemo] PurrNet fixture prepared.");
        }

        public static void BuildNetworkSmokePlayer()
        {
            BuildValidationFixture();
            BuildInstalledNetworkSmokePlayer();
        }

        /// <summary>Builds the exact extracted installer scene without regenerating its payload.</summary>
        public static void BuildInstalledNetworkSmokePlayer()
        {
            NetworkEnemyShooterDemoEditorUtility.BuildStandaloneSmokePlayer(NetworkTargetingDemoEditorUtility.ScenePath("PurrNet"), "PurrNet Targeting");
        }

        private static void ConfigureNpc(GameObject root)
        {
            if (root.GetComponent<NetworkIdentity>() == null) root.AddComponent<NetworkIdentity>();
            if (root.GetComponent<PurrNetNetworkCharacterAuto>() == null) root.AddComponent<PurrNetNetworkCharacterAuto>();
        }

        private static void ConfigureSession(Scene scene, GameObject player)
        {
            PurrNetDemoPlayerSpawner spawner = scene.GetRootGameObjects().SelectMany(x => x.GetComponentsInChildren<PurrNetDemoPlayerSpawner>(true)).Single();
            NetworkTargetingDemoEditorUtility.Set(spawner, "m_PlayerPrefab", player);
            var serialized = new SerializedObject(spawner);
            serialized.FindProperty("m_PlayerPrefabs").arraySize = 0;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            string path = NetworkTargetingDemoEditorUtility.Root("PurrNet") + "/TargetingNetworkPrefabs.asset";
            NetworkPrefabs prefabs = AssetDatabase.LoadAssetAtPath<NetworkPrefabs>(path);
            if (prefabs == null) { prefabs = ScriptableObject.CreateInstance<NetworkPrefabs>(); AssetDatabase.CreateAsset(prefabs, path); }
            prefabs.autoGenerate = false;
            prefabs.searchAllIfNoFolder = false;
            prefabs.linkedNetworkPrefabs = new List<NetworkPrefabs>();
            prefabs.prefabs = new List<NetworkPrefabs.UserPrefabData>
            {
                new NetworkPrefabs.UserPrefabData { guid = AssetDatabase.AssetPathToGUID(NetworkTargetingDemoEditorUtility.PlayerPath("PurrNet")), prefab = player, pooled = false }
            };
            prefabs.Refresh();
            EditorUtility.SetDirty(prefabs);
            NetworkManager manager = scene.GetRootGameObjects().SelectMany(x => x.GetComponentsInChildren<NetworkManager>(true)).Single();
            NetworkTargetingDemoEditorUtility.Set(manager, "_networkPrefabs", prefabs);
            NetworkTargetingDemoEditorUtility.Set(manager, "_startServerFlags", 0);
            NetworkTargetingDemoEditorUtility.Set(manager, "_startClientFlags", 0);
        }
    }
}
