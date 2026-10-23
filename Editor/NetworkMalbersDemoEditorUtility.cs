using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GameCreator.Editor.Installs;
using GameCreator.Runtime.Characters;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Arawn.GameCreator2.Networking.Editor
{
    /// <summary>
    /// Shared authoring helpers for the optional Malbers Animal Controller demos.
    /// The generated assets reference the licensed Malbers package; they never copy its
    /// scripts, models, materials, or animations into the Networking Layer package.
    /// </summary>
    public static class NetworkMalbersDemoEditorUtility
    {
        public const string MalbersPlayerScene =
            "Assets/Malbers Animations/Integrations/GameCreator2/Scenes/Wolf - Forest.unity";

        public const string StateBridgeTypeName =
            "Arawn.GameCreator2.Networking.Malbers.NetworkMalbersStateBridge";

        public const string DemoHudTypeName =
            "Arawn.GameCreator2.Networking.Malbers.NetworkMalbersDemoHud";

        private static readonly string[] s_CopiedComponentTypeNames =
        {
            "Arawn.GameCreator2.Networking.NetworkCharacter",
            "Arawn.GameCreator2.Networking.UnitAnimimNetworkController",
            "Arawn.GameCreator2.Networking.Transport.Fusion.FusionNetworkIdentity",
            "Arawn.GameCreator2.Networking.Transport.Fusion.FusionNativeNetworkCharacterMotor",
            "Arawn.GameCreator2.Networking.Transport.Fusion.FusionNetworkCharacterAuto",
            "Arawn.GameCreator2.Networking.Transport.PurrNet.PurrNetNetworkCharacterAuto"
        };

        public static Character RequireMalbersPlayer()
        {
            if (!File.Exists(Path.Combine(
                    Path.GetFullPath(Path.Combine(Application.dataPath, "..")),
                    MalbersPlayerScene)))
            {
                throw new FileNotFoundException(
                    "The Malbers GC2 integration Wolf scene is required for the demo.",
                    MalbersPlayerScene);
            }

            Scene scene = EditorSceneManager.OpenScene(MalbersPlayerScene, OpenSceneMode.Single);
            Character player = scene.GetRootGameObjects()
                .SelectMany(root => root.GetComponentsInChildren<Character>(true))
                .FirstOrDefault(character => character != null && character.IsPlayer);
            if (player == null)
            {
                throw new InvalidOperationException(
                    $"The Malbers Wolf scene has no player Character: '{MalbersPlayerScene}'.");
            }

            return player;
        }

        public static GameObject SaveMalbersPlayerPrefab(
            string destinationPath,
            bool configureAsNpc,
            string networkSourcePrefabPath)
        {
            if (string.IsNullOrEmpty(destinationPath))
                throw new ArgumentException("Destination prefab path is required.", nameof(destinationPath));
            if (AssetDatabase.LoadMainAssetAtPath(destinationPath) != null &&
                !AssetDatabase.DeleteAsset(destinationPath))
            {
                throw new IOException($"Could not replace '{destinationPath}'.");
            }

            Character player = RequireMalbersPlayer();
            GameObject root = player.gameObject;
            if (root.transform.parent != null)
            {
                // The Malbers player may be nested under a demo scene root. Promote a copy so the
                // saved prefab owns its complete hierarchy.
                root = UnityEngine.Object.Instantiate(root);
                root.name = Path.GetFileNameWithoutExtension(destinationPath);
                SceneManager.MoveGameObjectToScene(root, EditorSceneManager.GetActiveScene());
            }

            root.name = Path.GetFileNameWithoutExtension(destinationPath);
            Character character = root.GetComponent<Character>();
            if (character == null)
                throw new InvalidOperationException("The extracted Malbers player has no Character.");

            // NetworkCharacter enables the GC2 player flag only for the authenticated owner.
            character.IsPlayer = false;
            EditorUtility.SetDirty(character);
            UseEventDrivenFootstepDetector(character);

            CopyNetworkComponents(networkSourcePrefabPath, root);
            ConfigureActorType(root, configureAsNpc);
            if (configureAsNpc)
            {
                RemovePresentationOnlyComponents(root);
            }

            EnsureComponentByTypeName(root, StateBridgeTypeName);

            GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, destinationPath);
            AssetDatabase.ImportAsset(destinationPath, ImportAssetOptions.ForceSynchronousImport);
            if (prefab == null)
                throw new InvalidOperationException($"Could not save Malbers prefab '{destinationPath}'.");

            Debug.Log(
                $"[MalbersDemo] Saved {(configureAsNpc ? "NPC" : "player")} prefab " +
                $"'{destinationPath}' from '{MalbersPlayerScene}'.");
            return prefab;
        }

        /// <summary>
        /// Malbers animals use generic, non-humanoid rigs. Every GC2 footstep detector resolves
        /// its feet through <c>Animator.GetBoneTransform</c>, which throws on a generic avatar on
        /// every frame. Clearing the humanoid foot list makes the detector a no-op, which keeps
        /// the demo log and frame time clean; Malbers owns its own footstep audio.
        /// </summary>
        public static void UseEventDrivenFootstepDetector(Character character)
        {
            if (character == null) return;
            if (character.Animim.Animator != null && character.Animim.Animator.isHuman) return;

            var serialized = new SerializedObject(character);
            SerializedProperty footsteps = serialized.FindProperty("m_Footsteps");
            SerializedProperty feet = footsteps?.FindPropertyRelative("m_Feet");
            if (feet == null || !feet.isArray) return;

            bool changed = false;
            if (feet.arraySize > 0)
            {
                feet.arraySize = 0;
                changed = true;
            }

            SerializedProperty detector = footsteps.FindPropertyRelative("m_FootstepDetector");
            if (detector != null &&
                detector.propertyType == SerializedPropertyType.ManagedReference &&
                detector.managedReferenceValue is not FootstepDetectorFulcrum)
            {
                detector.managedReferenceValue = new FootstepDetectorFulcrum();
                changed = true;
            }

            if (!changed) return;

            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(character);
        }

        /// <summary>Applies the rig-agnostic footstep detector to every Character in a scene.</summary>
        public static void ConfigureFootstepDetectors(Scene scene)
        {
            if (!scene.IsValid() || !scene.isLoaded) return;

            Character[] characters = scene.GetRootGameObjects()
                .SelectMany(root => root.GetComponentsInChildren<Character>(true))
                .ToArray();
            for (int i = 0; i < characters.Length; i++)
            {
                UseEventDrivenFootstepDetector(characters[i]);
            }
        }

        public static GameObject CopyNpcPrefab(string playerPrefabPath, string npcPrefabPath)
        {
            if (AssetDatabase.LoadMainAssetAtPath(npcPrefabPath) != null &&
                !AssetDatabase.DeleteAsset(npcPrefabPath))
            {
                throw new IOException($"Could not replace '{npcPrefabPath}'.");
            }
            if (!AssetDatabase.CopyAsset(playerPrefabPath, npcPrefabPath))
                throw new IOException($"Could not copy '{playerPrefabPath}' to '{npcPrefabPath}'.");

            AssetDatabase.ImportAsset(npcPrefabPath, ImportAssetOptions.ForceSynchronousImport);
            GameObject root = PrefabUtility.LoadPrefabContents(npcPrefabPath);
            try
            {
                root.name = Path.GetFileNameWithoutExtension(npcPrefabPath);
                Character character = root.GetComponent<Character>();
                if (character != null)
                {
                    character.IsPlayer = false;
                    EditorUtility.SetDirty(character);
                    UseEventDrivenFootstepDetector(character);
                }

                ConfigureActorType(root, isNpc: true);
                RemovePresentationOnlyComponents(root);
                PrefabUtility.SaveAsPrefabAsset(root, npcPrefabPath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }

            AssetDatabase.ImportAsset(npcPrefabPath, ImportAssetOptions.ForceSynchronousImport);
            GameObject result = AssetDatabase.LoadAssetAtPath<GameObject>(npcPrefabPath);
            if (result == null)
                throw new InvalidOperationException($"Generated NPC prefab is not importable: '{npcPrefabPath}'.");
            return result;
        }

        public static void ReplacePlayerReferences(
            Scene scene,
            string sourcePlayerPath,
            GameObject destinationPlayer)
        {
            if (destinationPlayer == null) throw new ArgumentNullException(nameof(destinationPlayer));

            GameObject source = AssetDatabase.LoadAssetAtPath<GameObject>(sourcePlayerPath);
            if (source == null)
                throw new FileNotFoundException("The source Core player prefab is missing.", sourcePlayerPath);

            int replaced = 0;
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                foreach (Component component in root.GetComponentsInChildren<Component>(true))
                {
                    if (component == null || component is Transform) continue;
                    var serialized = new SerializedObject(component);
                    SerializedProperty property = serialized.GetIterator();
                    bool changed = false;
                    bool enterChildren = true;
                    while (property.Next(enterChildren))
                    {
                        enterChildren = true;
                        if (property.propertyType != SerializedPropertyType.ObjectReference)
                            continue;

                        UnityEngine.Object current = property.objectReferenceValue;
                        if (current == null ||
                            !string.Equals(
                                AssetDatabase.GetAssetPath(current),
                                sourcePlayerPath,
                                StringComparison.Ordinal))
                        {
                            continue;
                        }

                        property.objectReferenceValue = ResolveEquivalent(
                            current,
                            source,
                            destinationPlayer);
                        if (property.objectReferenceValue == null)
                        {
                            throw new InvalidOperationException(
                                $"Could not remap source-player reference " +
                                $"'{component.name}.{property.propertyPath}'.");
                        }

                        changed = true;
                        replaced++;
                    }

                    if (changed) serialized.ApplyModifiedPropertiesWithoutUndo();
                }
            }

            if (replaced == 0)
            {
                throw new InvalidOperationException(
                    $"The copied scene contains no reference to '{sourcePlayerPath}'.");
            }

            EditorSceneManager.MarkSceneDirty(scene);
            Debug.Log($"[MalbersDemo] Remapped {replaced} Core-player reference(s) to the Malbers prefab.");
        }

        public static int CreateNpcInstances(Scene scene, GameObject npcPrefab, string rootName)
        {
            if (npcPrefab == null) throw new ArgumentNullException(nameof(npcPrefab));

            GameObject existing = scene.GetRootGameObjects()
                .FirstOrDefault(root => root != null && root.name == rootName);
            if (existing != null) UnityEngine.Object.DestroyImmediate(existing);

            var container = new GameObject(rootName);
            SceneManager.MoveGameObjectToScene(container, scene);
            container.transform.SetParent(null, false);

            Vector3[] positions =
            {
                new(4f, 0f, 4f),
                new(-4f, 0f, 4f),
                new(0f, 0f, 7f)
            };

            for (int i = 0; i < positions.Length; i++)
            {
                GameObject npc = PrefabUtility.InstantiatePrefab(npcPrefab, scene) as GameObject;
                if (npc == null)
                    throw new InvalidOperationException($"Could not instantiate Malbers NPC {i + 1}.");

                npc.name = $"Malbers Network NPC {i + 1}";
                npc.transform.SetParent(container.transform, false);
                npc.transform.position = positions[i];
                Vector3 look = -new Vector3(positions[i].x, 0f, positions[i].z);
                if (look.sqrMagnitude > 0.0001f)
                    npc.transform.rotation = Quaternion.LookRotation(look.normalized, Vector3.up);
            }

            EditorSceneManager.MarkSceneDirty(scene);
            Debug.Log($"[MalbersDemo] Instantiated {positions.Length} persistent Malbers NPC(s).");
            return positions.Length;
        }

        public static void AddHud(Scene scene)
        {
            GameObject existing = scene.GetRootGameObjects()
                .FirstOrDefault(root => root != null && root.name == "Malbers Demo HUD");
            if (existing != null) UnityEngine.Object.DestroyImmediate(existing);

            var hud = new GameObject("Malbers Demo HUD");
            SceneManager.MoveGameObjectToScene(hud, scene);
            EnsureComponentByTypeName(hud, DemoHudTypeName);
            EditorSceneManager.MarkSceneDirty(scene);
        }

        public static void RemoveCoreVariableDemoObjects(Scene scene)
        {
            // Only the demo presentation is removed. The Variables module bridges stay in the
            // scene: the Fusion authority readiness gate requires an active Variables
            // participant and shuts the session down when it is missing.
            DestroySceneObjects(
                scene,
                "Door Frame Left",
                "Door Frame Right",
                "Door Frame Top",
                "Door Panel",
                "Keys",
                "Network Actions Door Demo",
                "Network Actions Door Demo UI",
                "Replicated Door Endpoint",
                "Variable Values UI",
                "Variable Label",
                "Values Text");
        }

        public static int DestroySceneObjects(Scene scene, params string[] names)
        {
            if (names == null || names.Length == 0) return 0;

            var wanted = new HashSet<string>(names, StringComparer.Ordinal);
            var matches = new List<GameObject>();
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                foreach (Transform transform in root.GetComponentsInChildren<Transform>(true))
                {
                    if (transform == null || !wanted.Contains(transform.gameObject.name)) continue;

                    // Never remove a transport/session manager or module bridge through a
                    // name collision: those objects carry live replication for the demo.
                    MonoBehaviour behaviour = transform.GetComponent<MonoBehaviour>();
                    if (behaviour != null && IsProtectedDemoObject(behaviour.GetType().Name))
                    {
                        continue;
                    }

                    matches.Add(transform.gameObject);
                }
            }

            foreach (GameObject match in matches)
            {
                if (match != null) UnityEngine.Object.DestroyImmediate(match);
            }

            if (matches.Count > 0) EditorSceneManager.MarkSceneDirty(scene);
            return matches.Count;
        }

        /// <summary>
        /// Protects objects that carry live networking behaviour from a demo-content name
        /// collision. Managers, transport bridges, and network identities are never pruned.
        /// </summary>
        private static bool IsProtectedDemoObject(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return false;

            return typeName.IndexOf("Manager", StringComparison.Ordinal) >= 0 ||
                   typeName.IndexOf("Bridge", StringComparison.Ordinal) >= 0 ||
                   typeName.IndexOf("Transport", StringComparison.Ordinal) >= 0 ||
                   typeName.IndexOf("NetworkIdentity", StringComparison.Ordinal) >= 0;
        }

        public static int PruneInstallRoot(string installRoot, params string[] keepPaths)
        {
            if (!AssetDatabase.IsValidFolder(installRoot)) return 0;

            var keep = new HashSet<string>(StringComparer.Ordinal);
            foreach (string path in keepPaths ?? Array.Empty<string>())
            {
                if (string.IsNullOrEmpty(path)) continue;
                keep.Add(path);
                keep.Add(path + ".meta");
            }

            keep.Add(installRoot + ".meta");
            keep.Add(installRoot + "/Profiles.meta");

            int removed = 0;
            string[] guids = AssetDatabase.FindAssets(string.Empty, new[] { installRoot });
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path) ||
                    AssetDatabase.IsValidFolder(path) ||
                    keep.Contains(path) ||
                    path.StartsWith(installRoot + "/Profiles/", StringComparison.Ordinal))
                {
                    continue;
                }

                if (AssetDatabase.DeleteAsset(path)) removed++;
            }

            if (removed > 0)
            {
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            }

            return removed;
        }

        public static void RemoveGeneratedSourcePlayer(string installRoot, string sourcePlayerName)
        {
            string path = installRoot + "/" + sourcePlayerName;
            if (AssetDatabase.LoadMainAssetAtPath(path) == null) return;
            if (!AssetDatabase.DeleteAsset(path))
                throw new IOException($"Could not remove unused Core player '{path}'.");
            Debug.Log($"[MalbersDemo] Removed unused Core player asset '{path}'.");
        }

        public static void ConfigureSpawner(
            Scene scene,
            GameObject playerPrefab,
            string spawnerTypeName)
        {
            MonoBehaviour spawner = FindComponentByTypeName(scene, spawnerTypeName);
            if (spawner == null)
                throw new InvalidOperationException($"Scene has no spawner '{spawnerTypeName}'.");

            var serialized = new SerializedObject(spawner);
            SerializedProperty prefab = serialized.FindProperty("m_PlayerPrefab");
            if (prefab != null) AssignPrefabReference(prefab, playerPrefab);

            SerializedProperty prefabs = serialized.FindProperty("m_PlayerPrefabs");
            if (prefabs != null && prefabs.isArray)
            {
                prefabs.arraySize = 1;
                AssignPrefabReference(prefabs.GetArrayElementAtIndex(0), playerPrefab);
            }

            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(spawner);
        }

        private static void AssignPrefabReference(
            SerializedProperty property,
            GameObject playerPrefab)
        {
            if (property == null || playerPrefab == null) return;

            if (property.objectReferenceValue is GameObject)
            {
                property.objectReferenceValue = playerPrefab;
                return;
            }

            Type expected = ResolveSerializedReferenceType(property);
            if (expected == null || expected == typeof(GameObject))
            {
                property.objectReferenceValue = playerPrefab;
                return;
            }

            Component component = playerPrefab.GetComponent(expected);
            if (component == null)
            {
                throw new InvalidOperationException(
                    $"The Malbers player prefab has no '{expected.FullName}' component required " +
                    $"by spawner field '{property.propertyPath}'.");
            }

            property.objectReferenceValue = component;
        }

        private static Type ResolveSerializedReferenceType(SerializedProperty property)
        {
            if (property.objectReferenceValue != null)
                return property.objectReferenceValue.GetType();

            string typeName = property.type;
            const string prefix = "PPtr<$";
            if (typeName != null && typeName.StartsWith(prefix, StringComparison.Ordinal) &&
                typeName.EndsWith(">", StringComparison.Ordinal))
            {
                string shortName = typeName.Substring(
                    prefix.Length,
                    typeName.Length - prefix.Length - 1);
                foreach (Type type in typeof(Component).Assembly.GetTypes())
                {
                    if (string.Equals(type.Name, shortName, StringComparison.Ordinal))
                        return type;
                }

#pragma warning disable UAC0005 // Optional Fusion/PurrNet component type lookup by serialized short name.
                foreach (System.Reflection.Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
#pragma warning restore UAC0005
                {
                    Type[] types;
                    try
                    {
                        types = assembly.GetTypes();
                    }
                    catch (System.Reflection.ReflectionTypeLoadException exception)
                    {
                        types = exception.Types;
                    }

                    foreach (Type type in types)
                    {
                        if (type != null &&
                            string.Equals(type.Name, shortName, StringComparison.Ordinal))
                        {
                            return type;
                        }
                    }
                }
            }

            return null;
        }

        public static MonoBehaviour FindComponentByTypeName(Scene scene, string typeName)
        {
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                foreach (MonoBehaviour behaviour in root.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (behaviour != null && string.Equals(
                            behaviour.GetType().FullName,
                            typeName,
                            StringComparison.Ordinal))
                    {
                        return behaviour;
                    }
                }
            }

            return null;
        }

        public static void EnsureAssetFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder)) return;
            string parent = Path.GetDirectoryName(folder)?.Replace('\\', '/');
            if (string.IsNullOrEmpty(parent))
                throw new InvalidOperationException($"Cannot create asset folder '{folder}'.");
            if (!AssetDatabase.IsValidFolder(parent)) EnsureAssetFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));
        }

        public static void CreateInstallerAsset(
            string path,
            string displayName,
            string module,
            string description,
            params string[] dependencyIds)
        {
            string folder = Path.GetDirectoryName(path)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(folder)) EnsureAssetFolder(folder);
            if (AssetDatabase.LoadMainAssetAtPath(path) != null &&
                !AssetDatabase.DeleteAsset(path))
            {
                throw new IOException($"Could not replace installer asset '{path}'.");
            }

            Installer installer = ScriptableObject.CreateInstance<Installer>();
            AssetDatabase.CreateAsset(installer, path);
            var serialized = new SerializedObject(installer);
            serialized.FindProperty("m_Install.m_Name").stringValue = displayName;
            serialized.FindProperty("m_Install.m_Module").stringValue = module;
            serialized.FindProperty("m_Install.m_Description").stringValue = description;
            serialized.FindProperty("m_Install.m_Author").stringValue = "Arawn";

            SerializedProperty version = serialized.FindProperty("m_Install.m_Version");
            version.FindPropertyRelative("major").intValue = 1;
            version.FindPropertyRelative("minor").intValue = 0;
            version.FindPropertyRelative("patch").intValue = 0;
            serialized.FindProperty("m_Install.m_Complexity").intValue = (int)Install.ComplexityType.Intermediate;

            SerializedProperty dependencies = serialized.FindProperty("m_Install.m_Dependencies");
            dependencyIds ??= Array.Empty<string>();
            dependencies.arraySize = dependencyIds.Length;
            for (int i = 0; i < dependencyIds.Length; i++)
            {
                SerializedProperty dependency = dependencies.GetArrayElementAtIndex(i);
                dependency.FindPropertyRelative("m_ID").stringValue = dependencyIds[i];
                SerializedProperty minimum = dependency.FindPropertyRelative("m_MinVersion");
                minimum.FindPropertyRelative("major").intValue = 1;
                minimum.FindPropertyRelative("minor").intValue = 0;
                minimum.FindPropertyRelative("patch").intValue = 0;
            }

            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(installer);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            Debug.Log($"[MalbersDemo] Created installer asset '{path}'.");
        }

        private static void CopyNetworkComponents(string sourcePrefabPath, GameObject destination)
        {
            if (string.IsNullOrEmpty(sourcePrefabPath)) return;
            GameObject source = AssetDatabase.LoadAssetAtPath<GameObject>(sourcePrefabPath);
            if (source == null) return;

            int copied = 0;
            foreach (Component sourceComponent in source.GetComponents<Component>())
            {
                if (sourceComponent == null) continue;
                Type type = sourceComponent.GetType();
                if (!ShouldCopy(type)) continue;

                Component destinationComponent = destination.GetComponent(type);
                if (destinationComponent == null)
                {
                    destinationComponent = destination.AddComponent(type);
                }

                if (destinationComponent == null)
                {
                    Debug.LogWarning(
                        $"[MalbersDemo] Unity refused to add '{type.FullName}' to the Malbers " +
                        "player prefab; skipping that transport component.");
                    continue;
                }

                EditorUtility.CopySerialized(sourceComponent, destinationComponent);
                SanitizeCopiedReferences(sourceComponent, destinationComponent, source, destination);
                copied++;
            }

            Debug.Log($"[MalbersDemo] Copied {copied} transport component(s) from '{sourcePrefabPath}'.");
        }

        private static bool ShouldCopy(Type type)
        {
            string fullName = type.FullName ?? string.Empty;
            if (fullName == "UnityEngine.Character" || fullName == "GameCreator.Runtime.Characters.Character")
                return false;
            if (fullName.StartsWith("GameCreator.", StringComparison.Ordinal)) return false;
            if (s_CopiedComponentTypeNames.Contains(fullName, StringComparer.Ordinal)) return true;

            string ns = type.Namespace ?? string.Empty;
            return ns == "Fusion" || ns.StartsWith("Fusion.", StringComparison.Ordinal) ||
                   ns == "PurrNet" || ns.StartsWith("PurrNet.", StringComparison.Ordinal);
        }

        private static void SanitizeCopiedReferences(
            Component sourceComponent,
            Component destinationComponent,
            GameObject sourceRoot,
            GameObject destinationRoot)
        {
            if (destinationComponent == null) return;

            var serialized = new SerializedObject(destinationComponent);
            SerializedProperty property = serialized.GetIterator();
            bool enterChildren = true;
            bool changed = false;
            while (property.Next(enterChildren))
            {
                enterChildren = true;
                if (property.propertyType != SerializedPropertyType.ObjectReference) continue;

                UnityEngine.Object reference = property.objectReferenceValue;
                if (reference == null || IsInside(reference, destinationRoot.transform)) continue;

                string propertyPath = property.propertyPath;
                if (propertyPath == "m_Script" ||
                    propertyPath == "m_GameObject" ||
                    propertyPath == "m_CorrespondingSourceObject" ||
                    propertyPath == "m_PrefabInstance" ||
                    propertyPath == "m_PrefabAsset")
                {
                    continue;
                }

                // External assets (ScriptableObjects, materials, prefabs) are legitimate
                // references and must survive the copy. Only Core-demo hierarchy objects must
                // be remapped or cleared.
                if (!IsInside(reference, sourceRoot.transform)) continue;

                {
                    Transform sourceTransform = reference is GameObject go
                        ? go.transform
                        : reference is Component component ? component.transform : null;
                    if (sourceTransform != null)
                    {
                        string path = AnimationUtility.CalculateTransformPath(
                            sourceTransform,
                            sourceRoot.transform);
                        Transform destinationTransform = string.IsNullOrEmpty(path)
                            ? destinationRoot.transform
                            : destinationRoot.transform.Find(path);
                        if (destinationTransform != null)
                        {
                            UnityEngine.Object remapped = reference is GameObject
                                ? destinationTransform.gameObject
                                : ResolveEquivalent(reference, sourceRoot, destinationRoot);
                            if (remapped != null)
                            {
                                property.objectReferenceValue = remapped;
                                changed = true;
                                continue;
                            }
                        }
                    }
                }

                // A Core demo child reference has no meaning on the Malbers hierarchy.
                property.objectReferenceValue = null;
                changed = true;
            }

            if (changed) serialized.ApplyModifiedPropertiesWithoutUndo();

            var visualRootSerialized = new SerializedObject(destinationComponent);
            SerializedProperty visualRoot = visualRootSerialized.FindProperty(
                "m_ListenHostPresentationVisualRoot");
            if (visualRoot != null && visualRoot.objectReferenceValue == null)
            {
                Animator animator = destinationRoot.GetComponentInChildren<Animator>(true);
                if (animator != null)
                {
                    visualRoot.objectReferenceValue = animator.transform;
                    visualRootSerialized.ApplyModifiedPropertiesWithoutUndo();
                }
            }
        }

        private static bool IsInside(UnityEngine.Object value, Transform root)
        {
            if (value is GameObject gameObject)
                return gameObject.transform == root || gameObject.transform.IsChildOf(root);
            if (value is Component component)
                return component.transform == root || component.transform.IsChildOf(root);
            return false;
        }

        private static void ConfigureActorType(GameObject root, bool isNpc)
        {
            NetworkCharacter networkCharacter = root.GetComponent<NetworkCharacter>();
            if (networkCharacter == null)
            {
                throw new InvalidOperationException(
                    "The Malbers demo prefab has no NetworkCharacter. Verify the transport Core " +
                    "player prefab path is correct.");
            }

            var serialized = new SerializedObject(networkCharacter);
            SerializedProperty actorType = serialized.FindProperty("m_ActorType");
            if (actorType == null)
                throw new MissingFieldException(networkCharacter.GetType().FullName, "m_ActorType");

            actorType.intValue = (int)(isNpc
                ? NetworkCharacterActorType.NPC
                : NetworkCharacterActorType.PlayerOwned);
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(networkCharacter);
        }

        private static void RemovePresentationOnlyComponents(GameObject root)
        {
            foreach (Camera camera in root.GetComponentsInChildren<Camera>(true))
            {
                if (camera == null) continue;
                // Keep the authored hierarchy so GC2 Character camera references remain valid.
                camera.enabled = false;
            }

            foreach (AudioListener listener in root.GetComponentsInChildren<AudioListener>(true))
            {
                if (listener != null) listener.enabled = false;
            }

            foreach (MonoBehaviour behaviour in root.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (behaviour == null) continue;
                string typeName = behaviour.GetType().Name;
                string fullName = behaviour.GetType().FullName ?? string.Empty;
                if (typeName == "MAnimalAIControl" || typeName == "MAnimalBrain")
                {
                    behaviour.enabled = false;
                    continue;
                }

                if (fullName.StartsWith("MalbersAnimations", StringComparison.Ordinal) &&
                    typeName.IndexOf("Input", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    behaviour.enabled = false;
                }
            }
        }

        private static Component EnsureComponentByTypeName(
            GameObject root,
            string fullName,
            bool allowMissing = false)
        {
            Type type = ResolveType(fullName);
            if (type == null)
            {
                if (allowMissing) return null;
                throw new InvalidOperationException(
                    $"Required optional-integration type was not compiled: '{fullName}'. " +
                    "Install Malbers and re-run the GC2 compatibility bootstrap so " +
                    "ARAWN_GC2_MALBERS is defined.");
            }

            Component existing = root.GetComponent(type);
            if (existing != null) return existing;
            return root.AddComponent(type);
        }

        private static Type ResolveType(string fullName)
        {
            Type type = Type.GetType(fullName + ", Assembly-CSharp");
            if (type != null) return type;

#pragma warning disable UAC0005 // Optional Assembly-CSharp lookup must stay cross-version safe.
            foreach (System.Reflection.Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
#pragma warning restore UAC0005
            {
                type = assembly.GetType(fullName);
                if (type != null) return type;
            }

            return null;
        }

        private static UnityEngine.Object ResolveEquivalent(
            UnityEngine.Object sourceObject,
            GameObject sourcePlayer,
            GameObject destinationPlayer)
        {
            if (sourceObject is GameObject) return destinationPlayer;
            if (sourceObject is not Component sourceComponent) return null;

            string path = AnimationUtility.CalculateTransformPath(
                sourceComponent.transform,
                sourcePlayer.transform);
            Transform destinationTransform = string.IsNullOrEmpty(path)
                ? destinationPlayer.transform
                : destinationPlayer.transform.Find(path);
            if (destinationTransform == null) return null;

            Type type = sourceComponent.GetType();
            Component[] sourceComponents = sourceComponent.GetComponents(type);
            Component[] destinationComponents = destinationTransform.GetComponents(type);
            int index = Array.IndexOf(sourceComponents, sourceComponent);
            return index >= 0 && index < destinationComponents.Length
                ? destinationComponents[index]
                : null;
        }
    }
}
