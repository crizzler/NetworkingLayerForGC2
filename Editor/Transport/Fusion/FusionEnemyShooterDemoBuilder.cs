using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Arawn.GameCreator2.Networking.Editor;
using Fusion;
using Fusion.Editor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Arawn.GameCreator2.Networking.Transport.Fusion.Editor
{
    public static class FusionEnemyShooterDemoBuilder
    {
        private const string CurrentRoot =
            "Assets/Plugins/GameCreator/Installs/GC2NetworkingLayerFusionTransport.ShooterExamples@1.0.2";
        private const string InstallRoot =
            "Assets/Plugins/GameCreator/Installs/GC2NetworkingLayerFusionTransport.ShooterExamples@1.1.0";
        private const string PlayerPrefab = InstallRoot + "/FusionDemoPlayer-ShooterAndStats.prefab";
        private const string EnemyPrefab = InstallRoot + "/FusionDemoEnemy-ShooterAndStats.prefab";
        private const string PvpScene = InstallRoot + "/Requires Shooter Demos - FusionShooterStatsDemo.unity";
        private const string EnemyScene = InstallRoot + "/Requires Shooter Demos - FusionEnemyShooterDemo.unity";
        private const string Weapon = InstallRoot + "/Weapons/FusionDemo_AK_Weapon.asset";
        private const string Monitor = InstallRoot + "/FusionShooterStatsDemoMonitor.cs";
        private const string Package =
            "Assets/Arawn/NetworkingLayerForGC2/Demo/Fusion/Packages/Shooter/Package.unitypackage";

        [MenuItem("Game Creator/Networking Layer/Demos/Rebuild Fusion Enemy Shooter Installer", priority = 220)]
        public static void BuildInstaller()
        {
            NetworkEnemyShooterDemoEditorUtility.MoveInstallRoot(CurrentRoot, InstallRoot);
            GameObject enemy = NetworkEnemyShooterDemoEditorUtility.BuildEnemyPrefab(
                PlayerPrefab,
                EnemyPrefab,
                Weapon,
                ConfigureEnemyTransport);

            AddFusionPrefabLabel(enemy);
            Scene scene = NetworkEnemyShooterDemoEditorUtility.CopyAndOpenScene(PvpScene, EnemyScene);
            ConfigureScene(scene, enemy);
            string navData = NetworkEnemyShooterDemoEditorUtility.BakeNavMesh(
                scene,
                FindSceneComponent<FusionPlayerSpawner>()?.transform.parent?.gameObject);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();
            NetworkProjectConfigUtilities.RebuildPrefabTable();

            string navFolder = Path.GetDirectoryName(navData)?.Replace('\\', '/');
            NetworkEnemyShooterDemoEditorUtility.ExportExactPackage(
                new[]
                {
                    InstallRoot,
                    PvpScene,
                    PlayerPrefab,
                    Monitor,
                    InstallRoot + "/Weapons",
                    Weapon,
                    EnemyPrefab,
                    EnemyScene,
                    navFolder,
                    navData
                },
                Package);
            Debug.Log("[FusionEnemyShooterDemoBuilder] Built exact 1.1.0 installer payload.");
        }

        public static void BuildNetworkSmokePlayer()
        {
            if (AssetDatabase.IsValidFolder(CurrentRoot)) BuildInstaller();
            else if (!AssetDatabase.IsValidFolder(InstallRoot))
                throw new DirectoryNotFoundException(
                    "Neither the 1.0.2 source fixture nor generated 1.1.0 Fusion demo exists.");
            NetworkEnemyShooterDemoEditorUtility.BuildStandaloneSmokePlayer(
                EnemyScene,
                "Fusion");
        }

        private static void ConfigureEnemyTransport(GameObject root, List<string> changes)
        {
            NetworkObject networkObject = root.GetComponent<NetworkObject>();
            if (networkObject == null)
            {
                networkObject = root.AddComponent<NetworkObject>();
                changes.Add("NetworkObject");
            }
            NetworkObjectFlags flags = networkObject.Flags |
                                       NetworkObjectFlags.MasterClientObject;
            flags &= ~NetworkObjectFlags.AllowStateAuthorityOverride;
            flags &= ~NetworkObjectFlags.DestroyWhenStateAuthorityLeaves;
            flags &= ~NetworkObjectFlags.HasMainNetworkTRSP;
            networkObject.Flags = flags;
            networkObject.EnableInterpolation = true;

            Ensure<FusionNetworkIdentity>(root, changes);
            Ensure<FusionNetworkCharacterAuto>(root, changes);
            FusionNativeNetworkCharacterMotor[] native =
                root.GetComponentsInChildren<FusionNativeNetworkCharacterMotor>(true);
            for (int i = 0; i < native.Length; i++)
                if (native[i] != null) UnityEngine.Object.DestroyImmediate(native[i], true);
            if (native.Length > 0) changes.Add($"removed {native.Length} player-native motor(s)");

            MonoBehaviour[] behaviours = root.GetComponentsInChildren<MonoBehaviour>(true);
            for (int i = 0; i < behaviours.Length; i++)
            {
                MonoBehaviour behaviour = behaviours[i];
                if (behaviour == null) continue;
                string name = behaviour.GetType().FullName ?? string.Empty;
                if (!name.Contains("FusionKcc", StringComparison.OrdinalIgnoreCase)) continue;
                UnityEngine.Object.DestroyImmediate(behaviour, true);
                changes.Add($"removed {behaviour.GetType().Name}");
            }
        }

        private static void ConfigureScene(Scene scene, GameObject enemy)
        {
            FusionTransportBridge transport = FindSceneComponent<FusionTransportBridge>();
            FusionAuthoritySpawnRegistry registry =
                FindSceneComponent<FusionAuthoritySpawnRegistry>();
            FusionPlayerSpawner spawner = FindSceneComponent<FusionPlayerSpawner>();
            if (transport == null || registry == null || spawner == null)
                throw new InvalidOperationException(
                    "The Fusion Shooter fixture is missing its transport, spawn registry, or player spawner.");

            var slotsObject = new GameObject("Fusion Enemy Bot Slots");
            NetworkObject networkObject = slotsObject.AddComponent<NetworkObject>();
            NetworkObjectFlags flags = networkObject.Flags |
                                       NetworkObjectFlags.MasterClientObject;
            flags &= ~NetworkObjectFlags.AllowStateAuthorityOverride;
            flags &= ~NetworkObjectFlags.DestroyWhenStateAuthorityLeaves;
            networkObject.Flags = flags;
            FusionBotSlotStateReplicator replicator =
                slotsObject.AddComponent<FusionBotSlotStateReplicator>();
            FusionBotSlotCoordinator coordinator =
                slotsObject.AddComponent<FusionBotSlotCoordinator>();
            if (spawner.transform.parent != null)
                slotsObject.transform.SetParent(spawner.transform.parent, false);

            List<Transform> anchors =
                NetworkEnemyShooterDemoEditorUtility.CreateSlotAnchors(slotsObject, 3);
            NetworkEnemyShooterDemoEditorUtility.ConfigureSlots(
                coordinator,
                anchors,
                enemy,
                "fusion-enemy-slot");

            var coordinatorObject = new SerializedObject(coordinator);
            SetObject(coordinatorObject, "m_TransportBridge", transport);
            SetObject(coordinatorObject, "m_SpawnRegistry", registry);
            SetObject(coordinatorObject, "m_StateReplicator", replicator);
            coordinatorObject.ApplyModifiedPropertiesWithoutUndo();

            var replicatorObject = new SerializedObject(replicator);
            SetObject(replicatorObject, "m_Coordinator", coordinator);
            replicatorObject.ApplyModifiedPropertiesWithoutUndo();

            var spawnerObject = new SerializedObject(spawner);
            SetObject(spawnerObject, "m_BotSlotCoordinator", coordinator);
            spawnerObject.ApplyModifiedPropertiesWithoutUndo();
            EditorSceneManager.MarkSceneDirty(scene);
        }

        private static void AddFusionPrefabLabel(GameObject prefab)
        {
            string[] current = AssetDatabase.GetLabels(prefab);
            if (current.Contains("FusionPrefab", StringComparer.Ordinal)) return;
            AssetDatabase.SetLabels(prefab, current.Append("FusionPrefab").ToArray());
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
