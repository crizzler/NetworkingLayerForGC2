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

        private static string TrainingFolder(string transport) => NetworkTargetingDemoEditorUtility.Root(transport) + "/Parry Formula";

        [MenuItem("Game Creator/Networking Layer/Publisher/Export Signed Melee Health Installers")]
        public static void ExportInstallers()
        {
            string[] transports = { "Fusion", "PurrNet" };
            var exports = new NetworkPackageExportResult[transports.Length];
            for (int i = 0; i < transports.Length; i++)
            {
                string transport = transports[i];
                if (!File.Exists(ScenePath(transport)) ||
                    AssetDatabase.LoadMainAssetAtPath(TrainingFolder(transport) + "/Training Sword.asset") == null)
                    throw new InvalidOperationException($"Build the {transport} Melee Parry and Formula scene before exporting.");

                string output = Path.Combine("Temp", "GC2MeleeHealthInstaller", transport, "Package.unitypackage");
                exports[i] = NetworkTargetingDemoEditorUtility.Export(transport, output, transport + " Melee Health installer");
            }

            if (exports.Any(export => !export.Signed))
                throw new InvalidOperationException("Unsigned candidates were left in Temp. Neither demo installer was replaced.");

            for (int i = 0; i < transports.Length; i++)
                File.Copy(exports[i].PackagePath, NetworkTargetingDemoEditorUtility.PackagePath(transports[i]), true);

            AssetDatabase.Refresh();
        }

        [MenuItem("Game Creator/Networking Layer/Demos/Build Melee Health, Parry and Formula Scenes")]
        public static void BuildBoth() => CI.GC2NetworkingCi.BuildMeleeHealthScenes();

        public static Scene Build(string transport, Action<GameObject> configureNpc, Action<Scene, GameObject> configurePlayer)
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

            Object sword = AssetDatabase.LoadMainAssetAtPath("Assets/Plugins/GameCreator/Installs/Melee.Sword@1.1.3/Sword_Weapon.asset");
            Type assetBuilder = RequireType(StatsMeleeAssembly + ".NetworkMeleeFormulaDemoAssets, " + StatsMeleeAssembly);
            var trainingSword = (Object)assetBuilder.GetMethod("Build").Invoke(null, new object[] { root, transport });
            string variantPath = TrainingFolder(transport) + $"/{transport}DemoPlayer-ParryFormula.prefab";
            GameObject variant = BuildPlayerVariant(player, variantPath, sword, trainingSword, meleeControllerType);
            foreach (GameObject item in scene.GetRootGameObjects()) ReplaceWeaponReferences(item, sword, trainingSword);
            configurePlayer(scene, variant);

            GameObject npc = (GameObject)PrefabUtility.InstantiatePrefab(variant, scene);
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
            Component monitor = ui.AddComponent(monitorType);
            NetworkTargetingDemoEditorUtility.Set(monitor, "m_Health", health);
            NetworkTargetingDemoEditorUtility.Set(monitor, "m_ShowParryAndFormula", true);
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

        private static GameObject BuildPlayerVariant(GameObject player, string path, Object stock, Object training, Type meleeController)
        {
            Scene preview = EditorSceneManager.NewPreviewScene();
            try
            {
                var instance = (GameObject)PrefabUtility.InstantiatePrefab(player, preview);
                instance.name = Path.GetFileNameWithoutExtension(path);
                if (ReplaceWeaponReferences(instance, stock, training) == 0)
                    throw new InvalidOperationException("The Melee & Stats player no longer equips the expected stock Sword.");
                var data = new SerializedObject(instance.GetComponent(meleeController));
                var config = data.FindProperty("m_ValidationConfig");
                config.FindPropertyRelative("HeadDamageMultiplier").floatValue = 1f;
                config.FindPropertyRelative("LegsDamageMultiplier").floatValue = 1f;
                data.ApplyModifiedPropertiesWithoutUndo();
                return PrefabUtility.SaveAsPrefabAsset(instance, path) ?? throw new IOException("Could not save " + path);
            }
            finally { EditorSceneManager.ClosePreviewScene(preview); }
        }

        private static int ReplaceWeaponReferences(GameObject root, Object stock, Object training)
        {
            if (stock == null || training == null) throw new InvalidOperationException("Both weapons must exist.");
            int replaced = 0;
            foreach (MonoBehaviour component in root.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (component == null) continue;
                var data = new SerializedObject(component);
                var property = data.GetIterator();
                bool changed = false;
                while (property.Next(true))
                {
                    if (property.propertyType != SerializedPropertyType.ObjectReference || property.objectReferenceValue != stock) continue;
                    property.objectReferenceValue = training;
                    replaced++;
                    changed = true;
                }
                if (changed) data.ApplyModifiedPropertiesWithoutUndo();
            }
            return replaced;
        }

        private static void UpdateControls(Scene scene, string transport)
        {
            GameObject session = scene.GetRootGameObjects().Single(item => item.name == transport + " Session");
            Transform[] elements = session.GetComponentsInChildren<Transform>(true);
            Transform controls = elements.Single(item => item.name == "Controls Panel");
            SetText(controls.Find("Title"), transport + " Melee: Parry & Formula");
            SetText(controls.Find("Keys"), "WASD  Move\n\nLeft Mouse  Slash / Riposte\n\nRight Mouse  Guard / Parry\n\nLeft Shift  Dash");
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
