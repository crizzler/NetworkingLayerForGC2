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
    /// <summary>
    /// Rebuilds the optional PurrNet Free Flow Combat example from the installed Melee
    /// example. The generated installer owns only its player, server NPC, scene, NavMesh,
    /// and exact two-prefab PurrNet registry; licensed dependencies remain external.
    /// </summary>
    public static class PurrNetFreeFlowCombatDemoBuilder
    {
        private const string SourceRoot =
            "Assets/Plugins/GameCreator/Installs/GC2NetworkingLayerPurrNetTransport.MeleeExamples@1.0.0";
        private const string InstallRoot =
            "Assets/Plugins/GameCreator/Installs/GC2NetworkingLayerPurrNetTransport.FreeFlowCombatExamples@1.0.0";

        private const string SourcePlayerName = "PurrNetDemoPlayer-MeleeAndStats.prefab";
        private const string SourceSceneName =
            "Requires Melee & Stats Demos - PurrNetMeleeStatsDemo.unity";
        private const string PlayerName = "PurrNetDemoPlayer-FreeFlowCombat.prefab";
        private const string EnemyName = "PurrNetDemoEnemy-FreeFlowCombat.prefab";
        private const string SceneName =
            "Requires Melee & Free Flow Combat - PurrNetFreeFlowCombatDemo.unity";

        private const string SourcePlayer = SourceRoot + "/" + SourcePlayerName;
        private const string CopiedPlayer = InstallRoot + "/" + SourcePlayerName;
        private const string CopiedScene = InstallRoot + "/" + SourceSceneName;
        private const string PlayerPrefab = InstallRoot + "/" + PlayerName;
        private const string EnemyPrefab = InstallRoot + "/" + EnemyName;
        private const string DemoScene = InstallRoot + "/" + SceneName;
        private const string Profiles = InstallRoot + "/Profiles";
        private const string NetworkPrefabsPath =
            Profiles + "/PurrNetFreeFlowCombatNetworkPrefabs.asset";
        private const string Package =
            "Assets/Arawn/NetworkingLayerForGC2/Demo/PurrNet/Packages/FreeFlowCombat/Package.unitypackage";

        [MenuItem(
            "Game Creator/Networking Layer/Demos/Rebuild PurrNet Free Flow Combat Installer",
            priority = 223)]
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
                "PurrNet Free Flow Combat");
        }

        private static void Build(bool exportInstaller)
        {
            UnityEngine.Object freeFlowWeapon =
                NetworkFreeFlowCombatDemoEditorUtility.RequireFreeFlowDependencies();

            NetworkFreeFlowCombatDemoEditorUtility.RecreateInstallRoot(
                SourceRoot,
                InstallRoot);
            NetworkFreeFlowCombatDemoEditorUtility.RenameGeneratedAsset(
                CopiedPlayer,
                PlayerPrefab);
            NetworkFreeFlowCombatDemoEditorUtility.RenameGeneratedAsset(
                CopiedScene,
                DemoScene);
            RecreateProfilesFolder();

            GameObject player = NetworkFreeFlowCombatDemoEditorUtility.PreparePlayerPrefab(
                PlayerPrefab,
                freeFlowWeapon);
            GameObject enemy = NetworkFreeFlowCombatDemoEditorUtility.BuildEnemyPrefab(
                PlayerPrefab,
                EnemyPrefab,
                ConfigureEnemyTransport);
            NetworkPrefabs networkPrefabs = CreateNetworkPrefabs(player, enemy);

            Scene scene = EditorSceneManager.OpenScene(DemoScene, OpenSceneMode.Single);
            if (!scene.IsValid() || !scene.isLoaded)
            {
                throw new InvalidOperationException(
                    $"Could not open the generated PurrNet Free Flow scene '{DemoScene}'.");
            }

            // Opening/importing the copied scene can unload otherwise unreferenced editor
            // objects. Resolve the generated assets again by stable path before authoring the
            // scene instead of retaining fake-null UnityEngine.Object handles.
            player = RequireGeneratedAsset<GameObject>(PlayerPrefab);
            enemy = RequireGeneratedAsset<GameObject>(EnemyPrefab);
            networkPrefabs = RequireGeneratedAsset<NetworkPrefabs>(NetworkPrefabsPath);
            freeFlowWeapon = AssetDatabase.LoadMainAssetAtPath(
                NetworkFreeFlowCombatDemoEditorUtility.FreeFlowWeaponPath);
            if (freeFlowWeapon == null)
            {
                throw new FileNotFoundException(
                    "The canonical Free Flow weapon was unloaded and could not be resolved again.",
                    NetworkFreeFlowCombatDemoEditorUtility.FreeFlowWeaponPath);
            }

            NetworkFreeFlowCombatDemoEditorUtility.ConfigureScene(
                scene,
                SourceRoot,
                InstallRoot,
                enemy,
                freeFlowWeapon);
            ConfigureTransportScene(scene, player, networkPrefabs);
            EditorSceneManager.SaveScene(scene);

            PurrNetDemoPlayerSpawner spawner = FindSceneComponent<PurrNetDemoPlayerSpawner>(scene);
            NetworkFreeFlowCombatDemoEditorUtility.BakeNavMesh(
                scene,
                spawner != null && spawner.transform.parent != null
                    ? spawner.transform.parent.gameObject
                    : null);

            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            if (exportInstaller)
            {
                NetworkFreeFlowCombatDemoEditorUtility.ExportExactInstallRoot(
                    InstallRoot,
                    Package);
            }

            Debug.Log(
                exportInstaller
                    ? "[PurrNetFreeFlowCombatDemoBuilder] Built exact 1.0.0 installer payload " +
                      "with one PlayerOwned prefab, one server-authoritative NPC prefab, and " +
                      "an exact two-prefab PurrNet registry."
                    : "[PurrNetFreeFlowCombatDemoBuilder] Rebuilt the PurrNet Free Flow " +
                      "Combat Examples 1.0.0 validation fixture without exporting its archive.");
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
            GameObject enemy)
        {
            if (player == null) throw new ArgumentNullException(nameof(player));
            if (enemy == null) throw new ArgumentNullException(nameof(enemy));
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
                CreateRegistryEntry(EnemyPrefab, enemy)
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

        private static void ConfigureEnemyTransport(
            GameObject root,
            List<string> changes)
        {
            Ensure<NetworkIdentity>(root, changes);
            Ensure<PurrNetNetworkCharacterAuto>(root, changes);

            MonoBehaviour[] behaviours = root.GetComponentsInChildren<MonoBehaviour>(true);
            for (int i = 0; i < behaviours.Length; i++)
            {
                MonoBehaviour behaviour = behaviours[i];
                if (behaviour == null) continue;
                string typeName = behaviour.GetType().FullName ?? string.Empty;
                if (typeName.IndexOf("PurrDiction", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                string componentName = behaviour.GetType().Name;
                UnityEngine.Object.DestroyImmediate(behaviour, true);
                changes.Add($"removed {componentName}");
            }
        }

        private static void ConfigureTransportScene(
            Scene scene,
            GameObject player,
            NetworkPrefabs networkPrefabs)
        {
            NetworkManager manager = FindSceneComponent<NetworkManager>(scene);
            PurrNetDemoPlayerSpawner spawner =
                FindSceneComponent<PurrNetDemoPlayerSpawner>(scene);
            if (manager == null || spawner == null)
            {
                throw new InvalidOperationException(
                    "The copied PurrNet Melee fixture is missing its NetworkManager or " +
                    "PurrNetDemoPlayerSpawner.");
            }

            ReplaceSourcePlayerReferences(scene, player);
            RemoveBotSlotCoordinators(scene);

            var spawnerObject = new SerializedObject(spawner);
            SetRequiredObject(spawnerObject, "m_PlayerPrefab", player);
            SetRequiredObjectArray(spawnerObject, "m_PlayerPrefabs", player);
            SetRequiredObject(spawnerObject, "m_CharacterSelection", null);
            SetRequiredBool(spawnerObject, "m_WaitForCharacterSelection", false);
            SetRequiredObject(spawnerObject, "m_BotSlotCoordinator", null);
            spawnerObject.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(spawner);

            var managerObject = new SerializedObject(manager);
            SetRequiredObject(managerObject, "_networkPrefabs", networkPrefabs);
            managerObject.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(manager);

            AssertNoSourcePlayerReferences(scene);
            EditorSceneManager.MarkSceneDirty(scene);
        }

        private static void ReplaceSourcePlayerReferences(Scene scene, GameObject player)
        {
            GameObject source = AssetDatabase.LoadAssetAtPath<GameObject>(SourcePlayer);
            if (source == null)
            {
                throw new FileNotFoundException(
                    "The installed PurrNet Melee player fixture is missing.",
                    SourcePlayer);
            }

            foreach (Component component in EnumerateSceneComponents(scene))
            {
                if (component == null || component is Transform) continue;
                var serialized = new SerializedObject(component);
                SerializedProperty property = serialized.GetIterator();
                bool enterChildren = true;
                bool changed = false;
                while (property.Next(enterChildren))
                {
                    enterChildren = true;
                    if (property.propertyType != SerializedPropertyType.ObjectReference)
                        continue;

                    UnityEngine.Object current = property.objectReferenceValue;
                    if (current == null ||
                        !string.Equals(
                            AssetDatabase.GetAssetPath(current),
                            SourcePlayer,
                            StringComparison.Ordinal))
                    {
                        continue;
                    }

                    UnityEngine.Object replacement = ResolvePlayerEquivalent(
                        current,
                        source,
                        player);
                    if (replacement == null)
                    {
                        throw new InvalidOperationException(
                            $"Could not remap source-player reference " +
                            $"'{component.name}.{property.propertyPath}'.");
                    }

                    property.objectReferenceValue = replacement;
                    changed = true;
                }

                if (changed) serialized.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        private static UnityEngine.Object ResolvePlayerEquivalent(
            UnityEngine.Object sourceObject,
            GameObject sourcePlayer,
            GameObject destinationPlayer)
        {
            if (sourceObject is GameObject) return destinationPlayer;
            if (sourceObject is not Component sourceComponent) return null;

            string transformPath = AnimationUtility.CalculateTransformPath(
                sourceComponent.transform,
                sourcePlayer.transform);
            Transform destinationTransform = string.IsNullOrEmpty(transformPath)
                ? destinationPlayer.transform
                : destinationPlayer.transform.Find(transformPath);
            if (destinationTransform == null) return null;

            Type type = sourceComponent.GetType();
            Component[] sourceComponents = sourceComponent.GetComponents(type);
            Component[] destinationComponents = destinationTransform.GetComponents(type);
            int index = Array.IndexOf(sourceComponents, sourceComponent);
            return index >= 0 && index < destinationComponents.Length
                ? destinationComponents[index]
                : null;
        }

        private static void RemoveBotSlotCoordinators(Scene scene)
        {
            PurrNetBotSlotCoordinator[] coordinators =
                UnityObjectSearch.FindAll<PurrNetBotSlotCoordinator>(
                    FindObjectsInactive.Include);
            for (int i = 0; i < coordinators.Length; i++)
            {
                PurrNetBotSlotCoordinator coordinator = coordinators[i];
                if (coordinator == null || coordinator.gameObject.scene != scene) continue;
                UnityEngine.Object.DestroyImmediate(coordinator);
            }
        }

        private static void AssertNoSourcePlayerReferences(Scene scene)
        {
            foreach (Component component in EnumerateSceneComponents(scene))
            {
                if (component == null || component is Transform) continue;
                var serialized = new SerializedObject(component);
                SerializedProperty property = serialized.GetIterator();
                bool enterChildren = true;
                while (property.Next(enterChildren))
                {
                    enterChildren = true;
                    if (property.propertyType != SerializedPropertyType.ObjectReference ||
                        property.objectReferenceValue == null)
                    {
                        continue;
                    }

                    if (string.Equals(
                            AssetDatabase.GetAssetPath(property.objectReferenceValue),
                            SourcePlayer,
                            StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            $"Generated scene retains source-player reference " +
                            $"'{component.name}.{property.propertyPath}'.");
                    }
                }
            }
        }

        private static IEnumerable<Component> EnumerateSceneComponents(Scene scene)
        {
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                Component[] components = root.GetComponentsInChildren<Component>(true);
                for (int i = 0; i < components.Length; i++)
                {
                    if (components[i] != null) yield return components[i];
                }
            }
        }

        private static T FindSceneComponent<T>(Scene scene) where T : Component
        {
            return UnityObjectSearch.FindAll<T>(FindObjectsInactive.Include)
                .FirstOrDefault(item => item != null && item.gameObject.scene == scene);
        }

        private static T Ensure<T>(GameObject root, ICollection<string> changes)
            where T : Component
        {
            T component = root.GetComponent<T>();
            if (component != null) return component;
            component = root.AddComponent<T>();
            changes?.Add(typeof(T).Name);
            return component;
        }

        private static T RequireGeneratedAsset<T>(string path)
            where T : UnityEngine.Object
        {
            return AssetDatabase.LoadAssetAtPath<T>(path) ??
                throw new FileNotFoundException("Generated asset is not importable.", path);
        }

        private static void SetRequiredObject(
            SerializedObject serialized,
            string propertyName,
            UnityEngine.Object value)
        {
            SerializedProperty property = serialized.FindProperty(propertyName);
            if (property == null ||
                property.propertyType != SerializedPropertyType.ObjectReference)
            {
                throw new MissingFieldException(
                    serialized.targetObject.GetType().FullName,
                    propertyName);
            }
            property.objectReferenceValue = value;
        }

        private static void SetRequiredObjectArray(
            SerializedObject serialized,
            string propertyName,
            UnityEngine.Object value)
        {
            SerializedProperty property = serialized.FindProperty(propertyName);
            if (property == null || !property.isArray)
            {
                throw new MissingFieldException(
                    serialized.targetObject.GetType().FullName,
                    propertyName);
            }

            property.arraySize = 1;
            property.GetArrayElementAtIndex(0).objectReferenceValue = value;
        }

        private static void SetRequiredBool(
            SerializedObject serialized,
            string propertyName,
            bool value)
        {
            SerializedProperty property = serialized.FindProperty(propertyName);
            if (property == null ||
                property.propertyType != SerializedPropertyType.Boolean)
            {
                throw new MissingFieldException(
                    serialized.targetObject.GetType().FullName,
                    propertyName);
            }
            property.boolValue = value;
        }
    }
}
