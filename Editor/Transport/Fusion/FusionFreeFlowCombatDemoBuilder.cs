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
    /// <summary>
    /// Rebuilds the optional Fusion Free Flow Combat installer from the installed Fusion
    /// Melee example without modifying either licensed dependency fixture.
    /// </summary>
    public static class FusionFreeFlowCombatDemoBuilder
    {
        private const string SourceInstallRoot =
            "Assets/Plugins/GameCreator/Installs/" +
            "GC2NetworkingLayerFusionTransport.MeleeExamples@1.0.0";
        private const string InstallRoot =
            "Assets/Plugins/GameCreator/Installs/" +
            "GC2NetworkingLayerFusionTransport.FreeFlowCombatExamples@1.0.0";

        private const string SourcePlayerPrefab =
            SourceInstallRoot + "/FusionDemoPlayer-MeleeAndStats.prefab";
        private const string CopiedPlayerPrefab =
            InstallRoot + "/FusionDemoPlayer-MeleeAndStats.prefab";
        private const string PlayerPrefab =
            InstallRoot + "/FusionDemoPlayer-FreeFlowCombat.prefab";
        private const string EnemyPrefab =
            InstallRoot + "/FusionDemoEnemy-FreeFlowCombat.prefab";

        private const string CopiedScene =
            InstallRoot + "/Requires Melee & Stats Demos - FusionMeleeStatsDemo.unity";
        private const string DemoScene =
            InstallRoot +
            "/Requires Melee & Free Flow Combat - FusionFreeFlowCombatDemo.unity";
        private const string PackagePath =
            "Assets/Arawn/NetworkingLayerForGC2/Demo/Fusion/Packages/" +
            "FreeFlowCombat/Package.unitypackage";

        [MenuItem(
            "Game Creator/Networking Layer/Demos/Rebuild Fusion Free Flow Combat Installer",
            priority = 222)]
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
                "Fusion Free Flow Combat");
        }

        private static void Build(bool exportInstaller)
        {
            UnityEngine.Object freeFlowWeapon =
                NetworkFreeFlowCombatDemoEditorUtility.RequireFreeFlowDependencies();

            NetworkFreeFlowCombatDemoEditorUtility.RecreateInstallRoot(
                SourceInstallRoot,
                InstallRoot);
            NetworkFreeFlowCombatDemoEditorUtility.RenameGeneratedAsset(
                CopiedPlayerPrefab,
                PlayerPrefab);
            NetworkFreeFlowCombatDemoEditorUtility.RenameGeneratedAsset(
                CopiedScene,
                DemoScene);

            GameObject player = NetworkFreeFlowCombatDemoEditorUtility.PreparePlayerPrefab(
                PlayerPrefab,
                freeFlowWeapon);
            ValidatePlayerTransport(player);
            AddFusionPrefabLabel(player);

            GameObject enemy = NetworkFreeFlowCombatDemoEditorUtility.BuildEnemyPrefab(
                PlayerPrefab,
                EnemyPrefab,
                ConfigureEnemyTransport);
            AddFusionPrefabLabel(enemy);

            Scene scene = EditorSceneManager.OpenScene(DemoScene, OpenSceneMode.Single);
            if (!scene.IsValid() || !scene.isLoaded)
            {
                throw new InvalidOperationException(
                    $"Could not open the generated Fusion Free Flow scene '{DemoScene}'.");
            }


            // Scene import may unload otherwise unreferenced editor objects. Re-resolve by
            // stable asset path so subsequent authoring never receives fake-null handles.
            player = RequireGeneratedAsset<GameObject>(PlayerPrefab);
            enemy = RequireGeneratedAsset<GameObject>(EnemyPrefab);
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
                SourceInstallRoot,
                InstallRoot,
                enemy,
                freeFlowWeapon);
            ReplaceSourcePlayerReferences(scene, player);
            FusionPlayerSpawner spawner = ConfigurePlayerSpawner(scene, player);

            NetworkFreeFlowCombatDemoEditorUtility.BakeNavMesh(
                scene,
                spawner.transform.parent != null
                    ? spawner.transform.parent.gameObject
                    : spawner.gameObject);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();

            NetworkProjectConfigUtilities.RebuildPrefabTable();
            AssetDatabase.SaveAssets();
            if (exportInstaller)
            {
                NetworkFreeFlowCombatDemoEditorUtility.ExportExactInstallRoot(
                    InstallRoot,
                    PackagePath);
            }

            Debug.Log(
                exportInstaller
                    ? "[FusionFreeFlowCombatDemoBuilder] Built the exact Fusion Free Flow " +
                      "Combat Examples 1.0.0 installer payload."
                    : "[FusionFreeFlowCombatDemoBuilder] Rebuilt the Fusion Free Flow " +
                      "Combat Examples 1.0.0 validation fixture without exporting its archive.");
        }

        private static void ValidatePlayerTransport(GameObject player)
        {
            NetworkCharacter networkCharacter =
                player != null ? player.GetComponent<NetworkCharacter>() : null;
            if (player == null ||
                networkCharacter == null ||
                networkCharacter.ActorType != NetworkCharacterActorType.PlayerOwned ||
                player.GetComponent<NetworkObject>() == null ||
                player.GetComponent<FusionNetworkIdentity>() == null ||
                player.GetComponent<FusionNetworkCharacterAuto>() == null)
            {
                throw new InvalidOperationException(
                    "The generated Fusion player is not explicitly PlayerOwned or is missing " +
                    "NetworkObject, FusionNetworkIdentity, or FusionNetworkCharacterAuto. " +
                    "Rebuild the source Melee example before generating the Free Flow Combat " +
                    "installer.");
            }
        }

        private static void ConfigureEnemyTransport(GameObject root, List<string> changes)
        {
            NetworkObject networkObject = root.GetComponent<NetworkObject>();
            if (networkObject == null)
            {
                networkObject = root.AddComponent<NetworkObject>();
                changes.Add(nameof(NetworkObject));
            }

            NetworkObjectFlags flags = networkObject.Flags |
                                       NetworkObjectFlags.MasterClientObject;
            flags &= ~NetworkObjectFlags.AllowStateAuthorityOverride;
            flags &= ~NetworkObjectFlags.DestroyWhenStateAuthorityLeaves;
            flags &= ~NetworkObjectFlags.HasMainNetworkTRSP;
            if (networkObject.Flags != flags)
            {
                networkObject.Flags = flags;
                changes.Add("master-owned Built-in authority flags");
            }

            if (!networkObject.EnableInterpolation)
            {
                networkObject.EnableInterpolation = true;
                changes.Add("Fusion render interpolation");
            }

            Ensure<FusionNetworkIdentity>(root, changes);
            Ensure<FusionNetworkCharacterAuto>(root, changes);

            FusionNativeNetworkCharacterMotor[] nativeMotors =
                root.GetComponentsInChildren<FusionNativeNetworkCharacterMotor>(true);
            for (int i = 0; i < nativeMotors.Length; i++)
            {
                if (nativeMotors[i] != null)
                    UnityEngine.Object.DestroyImmediate(nativeMotors[i], true);
            }
            if (nativeMotors.Length > 0)
                changes.Add($"removed {nativeMotors.Length} Fusion Native motor(s)");

            MonoBehaviour[] behaviours = root.GetComponentsInChildren<MonoBehaviour>(true);
            int removedKcc = 0;
            for (int i = 0; i < behaviours.Length; i++)
            {
                MonoBehaviour behaviour = behaviours[i];
                if (behaviour == null || behaviour is FusionNativeNetworkCharacterMotor) continue;
                string fullName = behaviour.GetType().FullName ?? string.Empty;
                if (fullName.IndexOf("Fusion", StringComparison.OrdinalIgnoreCase) < 0 ||
                    fullName.IndexOf("Kcc", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                UnityEngine.Object.DestroyImmediate(behaviour, true);
                removedKcc++;
            }
            if (removedKcc > 0)
                changes.Add($"removed {removedKcc} unsupported Fusion KCC component(s)");
        }

        private static FusionPlayerSpawner ConfigurePlayerSpawner(
            Scene scene,
            GameObject player)
        {
            FusionPlayerSpawner spawner = FindSceneComponent<FusionPlayerSpawner>(scene);
            if (spawner == null)
            {
                throw new InvalidOperationException(
                    "The copied Fusion Melee scene has no FusionPlayerSpawner.");
            }

            if (FindSceneComponents<FusionBotSlotCoordinator>(scene).Any() ||
                FindSceneComponents<FusionBotSlotStateReplicator>(scene).Any())
            {
                throw new InvalidOperationException(
                    "The Fusion Melee source scene unexpectedly contains bot-slot components. " +
                    "The Free Flow Combat demo intentionally uses persistent enemies instead.");
            }

            NetworkObject playerNetworkObject = player.GetComponent<NetworkObject>();
            if (playerNetworkObject == null)
                throw new InvalidOperationException("The generated Fusion player has no NetworkObject.");

            var serialized = new SerializedObject(spawner);
            SetObject(serialized, "m_PlayerPrefab", playerNetworkObject);
            SetObjectArray(serialized, "m_PlayerPrefabs", playerNetworkObject);
            SetObject(serialized, "m_CharacterSelection", null);
            SetBool(serialized, "m_WaitForCharacterSelection", false);
            SetObject(serialized, "m_BotSlotCoordinator", null);
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(spawner);
            EditorSceneManager.MarkSceneDirty(scene);
            return spawner;
        }

        private static void ReplaceSourcePlayerReferences(Scene scene, GameObject replacementRoot)
        {
            GameObject sourceRoot = AssetDatabase.LoadAssetAtPath<GameObject>(SourcePlayerPrefab);
            if (sourceRoot == null)
            {
                throw new FileNotFoundException(
                    "The installed Fusion Melee player fixture is missing.",
                    SourcePlayerPrefab);
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
                    if (property.propertyType != SerializedPropertyType.ObjectReference) continue;

                    UnityEngine.Object current = property.objectReferenceValue;
                    if (current == null ||
                        !string.Equals(
                            AssetDatabase.GetAssetPath(current),
                            SourcePlayerPrefab,
                            StringComparison.Ordinal))
                    {
                        continue;
                    }

                    UnityEngine.Object replacement = FindEquivalentPrefabObject(
                        current,
                        sourceRoot,
                        replacementRoot);
                    if (replacement == null)
                    {
                        throw new InvalidOperationException(
                            $"Could not remap source-player reference " +
                            $"'{component.name}.{property.propertyPath}' to the generated " +
                            "Free Flow player.");
                    }

                    property.objectReferenceValue = replacement;
                    changed = true;
                }

                if (changed) serialized.ApplyModifiedPropertiesWithoutUndo();
            }

            foreach (Component component in EnumerateSceneComponents(scene))
            {
                if (component == null || component is Transform) continue;
                var serialized = new SerializedObject(component);
                SerializedProperty property = serialized.GetIterator();
                bool enterChildren = true;
                while (property.Next(enterChildren))
                {
                    enterChildren = true;
                    if (property.propertyType != SerializedPropertyType.ObjectReference) continue;
                    UnityEngine.Object value = property.objectReferenceValue;
                    if (value != null &&
                        string.Equals(
                            AssetDatabase.GetAssetPath(value),
                            SourcePlayerPrefab,
                            StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            $"The generated scene still references the source Melee player at " +
                            $"'{component.name}.{property.propertyPath}'.");
                    }
                }
            }
        }

        private static UnityEngine.Object FindEquivalentPrefabObject(
            UnityEngine.Object source,
            GameObject sourceRoot,
            GameObject replacementRoot)
        {
            Transform sourceTransform = source switch
            {
                GameObject gameObject => gameObject.transform,
                Component component => component.transform,
                _ => null
            };
            if (sourceTransform == null) return null;

            string path = AnimationUtility.CalculateTransformPath(
                sourceTransform,
                sourceRoot.transform);
            Transform replacementTransform = string.IsNullOrEmpty(path)
                ? replacementRoot.transform
                : replacementRoot.transform.Find(path);
            if (replacementTransform == null) return null;

            if (source is GameObject) return replacementTransform.gameObject;
            if (source is not Component sourceComponent) return null;

            Component[] sourceComponents = sourceTransform.GetComponents(sourceComponent.GetType());
            Component[] replacements = replacementTransform.GetComponents(sourceComponent.GetType());
            int index = Array.IndexOf(sourceComponents, sourceComponent);
            return index >= 0 && index < replacements.Length ? replacements[index] : null;
        }

        private static void AddFusionPrefabLabel(GameObject prefab)
        {
            string[] labels = AssetDatabase.GetLabels(prefab);
            if (labels.Contains("FusionPrefab", StringComparer.Ordinal)) return;
            AssetDatabase.SetLabels(prefab, labels.Append("FusionPrefab").ToArray());
            EditorUtility.SetDirty(prefab);
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

        private static T FindSceneComponent<T>(Scene scene) where T : Component
        {
            return FindSceneComponents<T>(scene).FirstOrDefault();
        }

        private static IEnumerable<T> FindSceneComponents<T>(Scene scene) where T : Component
        {
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                T[] components = root.GetComponentsInChildren<T>(true);
                for (int i = 0; i < components.Length; i++)
                {
                    if (components[i] != null) yield return components[i];
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

        private static void SetObject(
            SerializedObject serialized,
            string propertyName,
            UnityEngine.Object value)
        {
            SerializedProperty property = serialized.FindProperty(propertyName);
            if (property == null)
            {
                throw new MissingFieldException(
                    serialized.targetObject.GetType().FullName,
                    propertyName);
            }
            property.objectReferenceValue = value;
        }

        private static void SetObjectArray(
            SerializedObject serialized,
            string propertyName,
            params UnityEngine.Object[] values)
        {
            SerializedProperty property = serialized.FindProperty(propertyName);
            if (property == null || !property.isArray)
            {
                throw new MissingFieldException(
                    serialized.targetObject.GetType().FullName,
                    propertyName);
            }

            property.arraySize = values?.Length ?? 0;
            for (int i = 0; i < property.arraySize; i++)
                property.GetArrayElementAtIndex(i).objectReferenceValue = values[i];
        }

        private static void SetBool(
            SerializedObject serialized,
            string propertyName,
            bool value)
        {
            SerializedProperty property = serialized.FindProperty(propertyName);
            if (property == null)
            {
                throw new MissingFieldException(
                    serialized.targetObject.GetType().FullName,
                    propertyName);
            }
            property.boolValue = value;
        }
    }
}
