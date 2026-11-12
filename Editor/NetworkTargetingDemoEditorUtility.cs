using System;
using System.IO;
using System.Linq;
using GameCreator.Runtime.Characters;
using GameCreator.Editor.Installs;
using GameCreator.Runtime.VisualScripting;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Arawn.GameCreator2.Networking.Editor
{
    /// <summary>Authors the transport twins from the licensed closest-target arena, preserving the original examples.</summary>
    public static class NetworkTargetingDemoEditorUtility
    {
        public const string SourceScene = "Assets/Plugins/GameCreator/Installs/Melee.Examples@1.2.8/3 - Targeting/10_Target_Closest/10_Target_Closest.unity";
        public static string Root(string transport) => $"Assets/Plugins/GameCreator/Installs/GC2NetworkingLayer{transport}Transport.MeleeExamples@1.0.0";
        public static string ScenePath(string transport) => Root(transport) + $"/Requires Melee Examples - {transport}TargetClosestDemo.unity";
        public static string PlayerPath(string transport) => Root(transport) + $"/{transport}DemoPlayer-TargetClosest.prefab";

        public static Scene Build(string transport, Action<GameObject> configureNpc, Action<Scene, GameObject> configureSession)
        {
            string root = Root(transport);
            string playerPath = PlayerPath(transport);
            string originalPlayer = root + $"/{transport}DemoPlayer-MeleeAndStats.prefab";
            if (!File.Exists(SourceScene) || !File.Exists(originalPlayer))
                throw new FileNotFoundException("Install the Melee 1.2.8 and transport Melee examples first.");
            if (!File.Exists(playerPath) && !AssetDatabase.CopyAsset(originalPlayer, playerPath))
                throw new IOException("Could not create targeting player.");
            GameObject prefab = PrefabUtility.LoadPrefabContents(playerPath);
            try
            {
                foreach (Trigger trigger in prefab.GetComponentsInChildren<Trigger>(true))
                    UnityEngine.Object.DestroyImmediate(trigger);
                // The combat showcase's automatic recovery targeter would override an explicit clear.
                foreach (MonoBehaviour component in prefab.GetComponents<MonoBehaviour>())
                    if (component != null && component.GetType().Name == "PurrNetDemoCombatTargeter")
                        UnityEngine.Object.DestroyImmediate(component);
                Character character = prefab.GetComponent<Character>();
                SetUnit(character, "m_Facing", new UnitFacingNetworkTarget());
                Set(prefab.GetComponent<NetworkCharacter>(), "m_ActorType", (int)NetworkCharacterActorType.PlayerOwned);
                if (prefab.GetComponent<NetworkCombatTarget>() == null) prefab.AddComponent<NetworkCombatTarget>();
                PrefabUtility.SaveAsPrefabAsset(prefab, playerPath);
            }
            finally { PrefabUtility.UnloadPrefabContents(prefab); }

            string scenePath = ScenePath(transport);
            if (!File.Exists(scenePath) && !AssetDatabase.CopyAsset(SourceScene, scenePath))
                throw new IOException("Could not copy the closest-target arena.");
            // Always reconstruct from the source so retries cannot duplicate networking roots.
            if (File.Exists(scenePath)) File.Copy(SourceScene, scenePath, true);
            AssetDatabase.ImportAsset(scenePath, ImportAssetOptions.ForceSynchronousImport);
            Scene scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
            Vector3 playerPosition = Vector3.zero;
            foreach (Character character in scene.GetRootGameObjects().SelectMany(x => x.GetComponentsInChildren<Character>(true)).ToArray())
            {
                if (character.IsPlayer)
                {
                    playerPosition = character.transform.position;
                    UnityEngine.Object.DestroyImmediate(character.gameObject);
                    continue;
                }
                foreach (Trigger trigger in character.GetComponentsInChildren<Trigger>(true))
                    UnityEngine.Object.DestroyImmediate(trigger);
                SetUnit(character, "m_Player", new UnitPlayerDirectionalNetwork());
                SetUnit(character, "m_Motion", new UnitMotionNetworkController());
                SetUnit(character, "m_Driver", new UnitDriverNavmeshNetworkServer());
                SetUnit(character, "m_Facing", new UnitFacingNetworkPivot());
                NetworkCharacter network = character.gameObject.GetComponent<NetworkCharacter>() ?? character.gameObject.AddComponent<NetworkCharacter>();
                Set(network, "m_ActorType", (int)NetworkCharacterActorType.NPC);
                configureNpc(character.gameObject);
            }

            Scene networkScene = EditorSceneManager.OpenScene(root + $"/Requires Melee & Stats Demos - {transport}MeleeStatsDemo.unity", OpenSceneMode.Additive);
            GameObject session = networkScene.GetRootGameObjects().Single(x => x.name == transport + " Session");
            SceneManager.MoveGameObjectToScene(session, scene);
            EditorSceneManager.CloseScene(networkScene, true);
            SceneManager.SetActiveScene(scene);
            GameObject player = AssetDatabase.LoadAssetAtPath<GameObject>(playerPath);
            configureSession(scene, player);
            // Give both humans a distinct initial position near the stock arena's authored player.
            int point = 0;
            foreach (Transform item in session.GetComponentsInChildren<Transform>(true))
                if (item.name.StartsWith("Spawn Point", StringComparison.Ordinal))
                    item.position = playerPosition + new Vector3((point++ % 2) * 2f, 0f, (point / 2) * 2f);
            GameObject ui = new GameObject("Network Closest Target Demo");
            ui.AddComponent<NetworkTargetingDemoUI>();
            foreach (MonoBehaviour component in session.GetComponentsInChildren<MonoBehaviour>(true))
                if (component != null && component.GetType().Name.Contains("DemoControls")) component.enabled = false;
            foreach (Transform item in session.GetComponentsInChildren<Transform>(true))
                if (item.name == transport + " Demo Controls UI") item.gameObject.SetActive(false);
            EditorSceneManager.SaveScene(scene);
            NetworkFreeFlowCombatDemoEditorUtility.BakeNavMesh(scene, session, "Network Targeting NavMesh Surface", "NavMesh-Targeting.asset", preserveAssetIdentity: true);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();
            return scene;
        }

        public static NetworkPackageExportResult Export(string transport)
        {
            string descriptorPath = $"Assets/Arawn/NetworkingLayerForGC2/Demo/{transport}/Packages/Melee/GC2NetworkingLayer{transport}Transport.MeleeExamples.asset";
            Installer descriptor = AssetDatabase.LoadAssetAtPath<Installer>(descriptorPath);
            if (descriptor == null) throw new FileNotFoundException("Melee installer descriptor is missing.", descriptorPath);
            string root = InstallManager.GetInstallPath(descriptor.Data.ID, descriptor.Data.Version).Replace('\\', '/').TrimEnd('/');
            if (root != Root(transport)) throw new InvalidOperationException("Installer ID/version no longer matches the targeting fixture root: " + root);
            string package = Path.GetDirectoryName(descriptorPath) + "/Package.unitypackage";
            string[] payload = AssetDatabase.FindAssets(string.Empty, new[] { root })
                .Select(AssetDatabase.GUIDToAssetPath).Append(root)
                .Concat(Directory.GetDirectories(root, "*", SearchOption.AllDirectories).Select(x => x.Replace('\\', '/')))
                .Where(x => x == root || x.StartsWith(root + "/", StringComparison.Ordinal)).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToArray();
            return NetworkPackageSigning.ExportSigned(payload, Path.GetFullPath(package), ExportPackageOptions.Default,
                transport + " closest-target Melee installer");
        }

        public static void SetUnit(Character character, string name, object unit)
        {
            var serialized = new SerializedObject(character);
            serialized.FindProperty("m_Kernel").FindPropertyRelative(name).managedReferenceValue = unit;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        public static void Set(UnityEngine.Object target, string name, object value)
        {
            var serialized = new SerializedObject(target);
            SerializedProperty property = serialized.FindProperty(name) ?? throw new MissingFieldException(target.GetType().Name, name);
            if (value is int number) property.intValue = number;
            else if (value is bool flag) property.boolValue = flag;
            else property.objectReferenceValue = value as UnityEngine.Object;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}
