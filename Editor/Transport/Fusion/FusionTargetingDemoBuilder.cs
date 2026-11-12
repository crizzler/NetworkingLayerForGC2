using System.Linq;
using Arawn.GameCreator2.Networking.Editor;
using Fusion;
using Fusion.Editor;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Arawn.GameCreator2.Networking.Transport.Fusion.Editor
{
    public static class FusionTargetingDemoBuilder
    {
        [MenuItem("Game Creator/Networking Layer/Demos/Rebuild Fusion Closest Target Melee Installer", priority = 226)]
        public static void BuildInstaller() { BuildValidationFixture(); NetworkTargetingDemoEditorUtility.Export("Fusion"); }

        public static void BuildValidationFixture()
        {
            NetworkTargetingDemoEditorUtility.Build("Fusion", ConfigureNpc, ConfigureSession);
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(NetworkTargetingDemoEditorUtility.PlayerPath("Fusion"));
            AssetDatabase.SetLabels(prefab, AssetDatabase.GetLabels(prefab).Concat(new[] { "FusionPrefab" }).Distinct().ToArray());
            NetworkProjectConfigUtilities.RebuildPrefabTable();
            AssetDatabase.SaveAssets();
            Debug.Log("[TargetingDemo] Fusion fixture prepared.");
        }

        public static void BuildNetworkSmokePlayer()
        {
            BuildValidationFixture();
            BuildInstalledNetworkSmokePlayer();
        }

        /// <summary>Builds the exact extracted installer scene without regenerating its payload.</summary>
        public static void BuildInstalledNetworkSmokePlayer()
        {
            NetworkEnemyShooterDemoEditorUtility.BuildStandaloneSmokePlayer(NetworkTargetingDemoEditorUtility.ScenePath("Fusion"), "Fusion Targeting");
        }

        private static void ConfigureNpc(GameObject root)
        {
            NetworkObject obj = root.GetComponent<NetworkObject>() ?? root.AddComponent<NetworkObject>();
            obj.Flags = (obj.Flags | NetworkObjectFlags.MasterClientObject) &
                ~NetworkObjectFlags.AllowStateAuthorityOverride & ~NetworkObjectFlags.DestroyWhenStateAuthorityLeaves;
            if (root.GetComponent<FusionNetworkIdentity>() == null) root.AddComponent<FusionNetworkIdentity>();
            if (root.GetComponent<FusionNetworkCharacterAuto>() == null) root.AddComponent<FusionNetworkCharacterAuto>();
        }

        private static void ConfigureSession(Scene scene, GameObject player)
        {
            FusionPlayerSpawner spawner = scene.GetRootGameObjects().SelectMany(x => x.GetComponentsInChildren<FusionPlayerSpawner>(true)).Single();
            NetworkTargetingDemoEditorUtility.Set(spawner, "m_PlayerPrefab", player.GetComponent<NetworkObject>());
            var serialized = new SerializedObject(spawner);
            serialized.FindProperty("m_PlayerPrefabs").arraySize = 0;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            FusionSessionBootstrap bootstrap = scene.GetRootGameObjects().SelectMany(x => x.GetComponentsInChildren<FusionSessionBootstrap>(true)).Single();
        }
    }
}
