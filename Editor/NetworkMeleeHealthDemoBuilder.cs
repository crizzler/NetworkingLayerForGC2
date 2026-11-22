using System;
using System.IO;
using System.Linq;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.VisualScripting;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Arawn.GameCreator2.Networking.Editor
{
    public static class NetworkMeleeHealthDemoBuilder
    {
        public const string PracticeNpcName = "Practice NPC";

        private const string StatsMeleeAssembly = "Arawn.GameCreator2.Networking.Stats.Melee";
        private const string MeleeControllerType = "Arawn.GameCreator2.Networking.Melee.NetworkMeleeController, Arawn.GameCreator2.Networking.Melee";

        public static string ScenePath(string transport) => NetworkTargetingDemoEditorUtility.Root(transport) +
            $"/Requires Melee & Stats Demos - {transport}MeleeHealthDemo.unity";

        [MenuItem("Game Creator/Networking Layer/Publisher/Export Signed Melee Health Installers")]
        public static void ExportInstallers()
        {
            string[] transports = { "Fusion", "PurrNet" };
            var exports = new NetworkPackageExportResult[transports.Length];
            for (int i = 0; i < transports.Length; i++)
            {
                string transport = transports[i];
                if (!File.Exists(ScenePath(transport)))
                    throw new InvalidOperationException($"Build the {transport} Melee Health scene before exporting.");

                string output = Path.Combine("Temp", "GC2MeleeHealthInstaller", transport, "Package.unitypackage");
                exports[i] = NetworkTargetingDemoEditorUtility.Export(transport, output, transport + " Melee Health installer");
            }

            for (int i = 0; i < transports.Length; i++)
                File.Copy(exports[i].PackagePath, NetworkTargetingDemoEditorUtility.PackagePath(transports[i]), true);

            AssetDatabase.Refresh();
        }

        public static Scene Build(string transport, Action<GameObject> configureNpc)
        {
            string root = NetworkTargetingDemoEditorUtility.Root(transport);
            string sourcePath = root + $"/Requires Melee & Stats Demos - {transport}MeleeStatsDemo.unity";
            string playerPath = root + $"/{transport}DemoPlayer-MeleeAndStats.prefab";
            GameObject player = AssetDatabase.LoadAssetAtPath<GameObject>(playerPath);
            if (player == null || !File.Exists(sourcePath))
                throw new FileNotFoundException($"Install the {transport} Melee Examples first.");

            Type damageBridgeType = RequireType(StatsMeleeAssembly + ".NetworkMeleeStatsDamageBridge, " + StatsMeleeAssembly);
            Type monitorType = RequireType(StatsMeleeAssembly + ".NetworkMeleeHealthDemoMonitor, " + StatsMeleeAssembly);
            Type meleeControllerType = RequireType(MeleeControllerType);
            Scene scene = EditorSceneManager.OpenScene(sourcePath, OpenSceneMode.Single);
            Component bridge = scene.GetRootGameObjects()
                .SelectMany(item => item.GetComponentsInChildren(damageBridgeType, true)).Single();
            Object health = new SerializedObject(bridge).FindProperty("m_HealthAttribute").objectReferenceValue;
            if (health == null) throw new InvalidOperationException("Install the GC2 Stats Classes demo to resolve the HP Attribute.");

            GameObject npc = (GameObject)PrefabUtility.InstantiatePrefab(player, scene);
            PrefabUtility.UnpackPrefabInstance(npc, PrefabUnpackMode.OutermostRoot, UnityEditor.InteractionMode.AutomatedAction);
            npc.name = PracticeNpcName;
            npc.transform.SetPositionAndRotation(Vector3.zero, Quaternion.Euler(0f, 180f, 0f));
            foreach (Trigger trigger in npc.GetComponentsInChildren<Trigger>(true))
            {
                var serialized = new SerializedObject(trigger);
                if (serialized.FindProperty("m_TriggerEvent").managedReferenceValue is not EventOnStart)
                    Object.DestroyImmediate(trigger);
            }

            NetworkCharacter network = npc.GetComponent<NetworkCharacter>();
            NetworkTargetingDemoEditorUtility.Set(network, "m_ActorType", (int)NetworkCharacterActorType.NPC);
            NetworkTargetingDemoEditorUtility.Set(network, "m_NPCMode", (int)NetworkCharacter.NPCSyncMode.ServerAuthoritative);
            NetworkTargetingDemoEditorUtility.Set(network, "m_PredictionBackend", (int)NetworkPredictionBackend.BuiltIn);
            NetworkTargetingDemoEditorUtility.SetUnit(npc.GetComponent<Character>(), "m_Driver", new UnitDriverNavmeshNetworkServer());
            NetworkTargetingDemoEditorUtility.Set(npc.GetComponent(meleeControllerType), "m_RecoverMissingCombatTarget", false);
            configureNpc(npc);

            var ui = new GameObject("Melee Health Monitor");
            SceneManager.MoveGameObjectToScene(ui, scene);
            NetworkTargetingDemoEditorUtility.Set(ui.AddComponent(monitorType), "m_Health", health);
            UpdateControls(scene, transport);

            string destination = ScenePath(transport);
            if (!EditorSceneManager.SaveScene(scene, destination))
                throw new IOException("Could not save Melee Health demo: " + destination);

            npc.SetActive(false);
            try
            {
                NetworkFreeFlowCombatDemoEditorUtility.BakeNavMesh(
                    scene, null, "Melee Health NavMesh", "NavMesh-MeleeHealth.asset", preserveAssetIdentity: true);
            }
            finally
            {
                npc.SetActive(true);
            }

            if (!EditorSceneManager.SaveScene(scene))
                throw new IOException("Could not save Melee Health demo NavMesh: " + destination);
            AssetDatabase.SaveAssets();
            return scene;
        }

        private static Type RequireType(string qualifiedName)
        {
            return Type.GetType(qualifiedName) ??
                throw new InvalidOperationException("Install and compile GC2 Melee and Stats first. Missing " + qualifiedName);
        }

        private static void UpdateControls(Scene scene, string transport)
        {
            GameObject session = scene.GetRootGameObjects().Single(item => item.name == transport + " Session");
            Transform[] elements = session.GetComponentsInChildren<Transform>(true);
            Transform controls = elements.Single(item => item.name == "Controls Panel");
            SetText(controls.Find("Title"), transport + " Melee Health Demo");
            SetText(controls.Find("Keys"), "WASD  Move\n\nLeft Mouse  Sword attack\n\nRight Mouse  Block\n\nLeft Shift  Dash");
        }

        private static void SetText(Transform element, string value)
        {
            foreach (MonoBehaviour behaviour in element.GetComponents<MonoBehaviour>())
            {
                if (behaviour == null) continue;
                var serialized = new SerializedObject(behaviour);
                SerializedProperty text = serialized.FindProperty("m_Text");
                if (text == null) continue;
                text.stringValue = value;
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }
        }
    }
}
