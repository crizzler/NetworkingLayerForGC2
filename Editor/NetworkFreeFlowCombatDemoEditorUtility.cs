using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.VisualScripting;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Arawn.GameCreator2.Networking.Editor
{
    /// <summary>
    /// Deterministic, transport-neutral authoring helpers for the optional Free Flow Combat
    /// networking examples. Optional Free Flow types are resolved by name because they are
    /// compiled into Assembly-CSharp and must not become a dependency of the editor assembly.
    /// </summary>
    public static class NetworkFreeFlowCombatDemoEditorUtility
    {
        public const string FreeFlowDemoScene = "Assets/Arawn/FreeFlowCombat/Demo.unity";
        public const string FreeFlowWeaponPath =
            "Assets/Arawn/FreeFlowCombat/Assets/BrawlBaseline/FreeFlow_Brawl_FreeFlowWeapon.asset";
        public const string AuthorityAiRootName = "Authority Only Free Flow AI";
        public const string PersistentEnemiesRootName = "Free Flow Network Enemies";
        public const string InputRootName = "Free Flow Combat Inputs";

        private const string SwordWeaponGuid = "b9043bd74bc74466f88cc0f9ebe31a42";
        private const string AdapterTypeName =
            "Arawn.GameCreator2.Networking.Melee.NetworkFreeFlowCombatAdapter, " +
            "Arawn.GameCreator2.Networking.Melee";
        private const string FreeFlowWeaponTypeName =
            "Arawn.FreeFlowCombat.FreeFlowMeleeWeapon, Assembly-CSharp";
        private const string FreeFlowAttackTypeName =
            "Arawn.FreeFlowCombat.VisualScripting.InstructionFreeFlowAttack";
        private const string FreeFlowCounterTypeName =
            "Arawn.FreeFlowCombat.VisualScripting.InstructionFreeFlowCounter";
        private const string MeleeInputTypeName =
            "GameCreator.Runtime.Melee.InstructionMeleeInputExecute";
        private const string MeleeEquipTypeName =
            "GameCreator.Runtime.Melee.InstructionMeleeEquipWeapon";
        private static readonly Vector3[] s_EnemyPositions =
        {
            new Vector3(-3f, 1f, -3.68f),
            new Vector3(-7f, 1f, 4f),
            new Vector3(2.0073771f, 1f, 8.081082f),
            new Vector3(3f, 1f, 3.68f),
            new Vector3(4f, 1f, -3.68f)
        };

        /// <summary>Validates every optional input before a generated install root is replaced.</summary>
        public static UnityEngine.Object RequireFreeFlowDependencies()
        {
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(FreeFlowDemoScene) == null)
            {
                throw new FileNotFoundException(
                    "Free Flow Combat Demo.unity is missing. Install Free Flow Combat 1.0.1 " +
                    "before rebuilding the networking example.",
                    FreeFlowDemoScene);
            }

            Type weaponType = Type.GetType(FreeFlowWeaponTypeName, false);
            if (weaponType == null)
            {
                throw new InvalidOperationException(
                    "Free Flow Combat is present but its FreeFlowMeleeWeapon runtime type is " +
                    "not compiled. Resolve project compilation errors before rebuilding the demo.");
            }

            Type adapterType = RequireAdapterType();
            if (!typeof(Component).IsAssignableFrom(adapterType))
            {
                throw new InvalidOperationException(
                    $"'{AdapterTypeName}' does not resolve to a Unity Component.");
            }

            if (ResolveType(FreeFlowAttackTypeName) == null ||
                ResolveType(FreeFlowCounterTypeName) == null)
            {
                throw new InvalidOperationException(
                    "Free Flow Combat attack/counter Instructions are not compiled. Resolve " +
                    "project compilation errors before rebuilding the demo.");
            }

            UnityEngine.Object weapon = AssetDatabase.LoadMainAssetAtPath(FreeFlowWeaponPath);
            if (weapon == null || !weaponType.IsInstanceOfType(weapon))
            {
                throw new FileNotFoundException(
                    "The canonical BrawlBaseline Free Flow weapon is missing or has the wrong type.",
                    FreeFlowWeaponPath);
            }

            string swordPath = AssetDatabase.GUIDToAssetPath(SwordWeaponGuid);
            if (string.IsNullOrEmpty(swordPath) ||
                AssetDatabase.LoadMainAssetAtPath(swordPath) == null)
            {
                throw new FileNotFoundException(
                    "The Melee example Sword fixture is missing. Reinstall the Melee examples " +
                    "before rebuilding the Free Flow networking example.");
            }

            return weapon;
        }

        /// <summary>
        /// Recreates a generated installer root from an installed Melee example. The source is
        /// never moved or modified. Deletion is restricted to the exact generated install root.
        /// </summary>
        public static void RecreateInstallRoot(string sourceRoot, string destinationRoot)
        {
            const string installsPrefix = "Assets/Plugins/GameCreator/Installs/";
            if (string.IsNullOrWhiteSpace(sourceRoot) ||
                !sourceRoot.StartsWith(installsPrefix, StringComparison.Ordinal) ||
                !AssetDatabase.IsValidFolder(sourceRoot))
            {
                throw new DirectoryNotFoundException(
                    $"Installed Melee example source is missing: '{sourceRoot}'.");
            }

            if (string.IsNullOrWhiteSpace(destinationRoot) ||
                !destinationRoot.StartsWith(installsPrefix, StringComparison.Ordinal) ||
                destinationRoot.IndexOf("FreeFlowCombatExamples@", StringComparison.Ordinal) < 0 ||
                string.Equals(sourceRoot, destinationRoot, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Refusing to replace unsafe Free Flow installer destination '{destinationRoot}'.");
            }

            if (AssetDatabase.IsValidFolder(destinationRoot) &&
                !AssetDatabase.DeleteAsset(destinationRoot))
            {
                throw new IOException(
                    $"Could not remove the generated installer staging root '{destinationRoot}'.");
            }

            if (!AssetDatabase.CopyAsset(sourceRoot, destinationRoot))
            {
                // Some Unity editor versions do not copy folder assets through CopyAsset.
                // Fall back to an asset-aware recursive copy so every generated asset receives
                // a fresh GUID while source assets and their metas stay untouched.
                if (AssetDatabase.IsValidFolder(destinationRoot))
                    AssetDatabase.DeleteAsset(destinationRoot);
                CopyFolderRecursively(sourceRoot, destinationRoot);
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        }

        public static void RenameGeneratedAsset(string sourcePath, string destinationPath)
        {
            if (AssetDatabase.LoadMainAssetAtPath(destinationPath) != null)
            {
                if (AssetDatabase.LoadMainAssetAtPath(sourcePath) == null) return;
                if (!AssetDatabase.DeleteAsset(destinationPath))
                {
                    throw new IOException(
                        $"Could not remove generated destination asset '{destinationPath}'.");
                }
            }

            if (AssetDatabase.LoadMainAssetAtPath(sourcePath) == null)
            {
                throw new FileNotFoundException("Generated source asset is missing.", sourcePath);
            }

            string error = AssetDatabase.MoveAsset(sourcePath, destinationPath);
            if (!string.IsNullOrEmpty(error)) throw new InvalidOperationException(error);
            AssetDatabase.ImportAsset(destinationPath, ImportAssetOptions.ForceSynchronousImport);
        }

        public static GameObject PreparePlayerPrefab(
            string playerPrefabPath,
            UnityEngine.Object freeFlowWeapon)
        {
            RequirePrefab(playerPrefabPath);
            if (freeFlowWeapon == null) throw new ArgumentNullException(nameof(freeFlowWeapon));

            UnityEngine.Object sword = AssetDatabase.LoadMainAssetAtPath(
                AssetDatabase.GUIDToAssetPath(SwordWeaponGuid));
            GameObject root = PrefabUtility.LoadPrefabContents(playerPrefabPath);
            int replacedWeapons = 0;
            int removedInputs = 0;
            try
            {
                root.name = Path.GetFileNameWithoutExtension(playerPrefabPath);
                Character character = root.GetComponent<Character>();
                NetworkCharacter networkCharacter = root.GetComponent<NetworkCharacter>();
                if (character == null || networkCharacter == null)
                {
                    throw new InvalidOperationException(
                        $"'{playerPrefabPath}' must contain root Character and NetworkCharacter components.");
                }

                // PlayerOwned is a network classification. Keep the prefab-authored GC2 flag
                // off; NetworkCharacter enables it only for the authenticated local owner.
                if (character.IsPlayer)
                {
                    character.IsPlayer = false;
                    EditorUtility.SetDirty(character);
                }

                SetNetworkActorType(networkCharacter, NetworkCharacterActorType.PlayerOwned);
                EnsureOptionalComponent(root, RequireAdapterType());
                replacedWeapons = ReplaceObjectReferences(root, sword, freeFlowWeapon);
                removedInputs = RemoveComponentsContaining(root, MeleeInputTypeName);

                if (replacedWeapons == 0)
                {
                    throw new InvalidOperationException(
                        $"'{playerPrefabPath}' contains no reference to the canonical Melee " +
                        "example Sword. The source fixture may have changed.");
                }
                if (removedInputs == 0)
                {
                    throw new InvalidOperationException(
                        $"'{playerPrefabPath}' contains no local InstructionMeleeInputExecute " +
                        "Trigger. Refusing to produce a demo that may execute duplicate attacks.");
                }

                PrefabUtility.SaveAsPrefabAsset(root, playerPrefabPath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }

            AssetDatabase.ImportAsset(playerPrefabPath, ImportAssetOptions.ForceSynchronousImport);
            GameObject result = AssetDatabase.LoadAssetAtPath<GameObject>(playerPrefabPath);
            if (result == null) throw new InvalidOperationException("Generated player prefab is not importable.");
            Debug.Log(
                $"[GC2 Free Flow Demo Builder] Prepared PlayerOwned '{playerPrefabPath}'; " +
                $"weaponReferences={replacedWeapons}, removedLocalAttackTriggers={removedInputs}.");
            return result;
        }

        public static GameObject BuildEnemyPrefab(
            string playerPrefabPath,
            string enemyPrefabPath,
            Action<GameObject, List<string>> configureTransport)
        {
            RequirePrefab(playerPrefabPath);
            if (AssetDatabase.LoadMainAssetAtPath(enemyPrefabPath) != null &&
                !AssetDatabase.DeleteAsset(enemyPrefabPath))
            {
                throw new IOException($"Could not remove generated enemy '{enemyPrefabPath}'.");
            }
            if (!AssetDatabase.CopyAsset(playerPrefabPath, enemyPrefabPath))
            {
                throw new IOException(
                    $"Could not copy '{playerPrefabPath}' to '{enemyPrefabPath}'.");
            }
            AssetDatabase.ImportAsset(enemyPrefabPath, ImportAssetOptions.ForceSynchronousImport);

            GameObject root = PrefabUtility.LoadPrefabContents(enemyPrefabPath);
            var changes = new List<string>();
            try
            {
                root.name = Path.GetFileNameWithoutExtension(enemyPrefabPath);
                Transform previousAi = root.transform.Find(AuthorityAiRootName);
                if (previousAi != null) UnityEngine.Object.DestroyImmediate(previousAi.gameObject);

                var authorityRoot = new GameObject(AuthorityAiRootName);
                authorityRoot.transform.SetParent(root.transform, false);
                int equipTriggers = PrepareReplicaEquipTriggers(root);
                if (equipTriggers == 0)
                {
                    throw new InvalidOperationException(
                        "The copied Melee player contains no enabled top-level Free Flow " +
                        "equip Instruction. " +
                        "The Melee example prefab structure may have changed.");
                }
                changes.Add($"{equipTriggers} replica-safe Free Flow equip Trigger(s)");

                var errors = new List<string>();
                NetworkNpcSetupEditorUtility.ConfigureServerNpc(
                    root,
                    NetworkPredictionBackend.BuiltIn,
                    new[] { AuthorityAiRootName },
                    new[] { RequireAdapterType() },
                    changes,
                    errors);
                if (errors.Count > 0)
                {
                    throw new InvalidOperationException(string.Join("\n", errors));
                }

                NetworkNpcTargetSelector selector = root.GetComponent<NetworkNpcTargetSelector>();
                if (selector == null)
                {
                    throw new InvalidOperationException("Server NPC preparation did not add its target selector.");
                }
                var serializedSelector = new SerializedObject(selector);
                SetBool(serializedSelector, "m_FollowSelectedTarget", false);
                serializedSelector.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(selector);
                changes.Add("Free Flow-owned movement (generic target following disabled)");

                configureTransport?.Invoke(root, changes);
                authorityRoot.SetActive(true);
                PrefabUtility.SaveAsPrefabAsset(root, enemyPrefabPath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }

            AssetDatabase.ImportAsset(enemyPrefabPath, ImportAssetOptions.ForceSynchronousImport);
            GameObject result = AssetDatabase.LoadAssetAtPath<GameObject>(enemyPrefabPath);
            if (result == null) throw new InvalidOperationException("Generated enemy prefab is not importable.");
            Debug.Log(
                $"[GC2 Free Flow Demo Builder] Prepared NPC '{enemyPrefabPath}': " +
                string.Join(", ", changes));
            return result;
        }

        /// <summary>Applies the common Free Flow content to a copied network Melee scene.</summary>
        public static void ConfigureScene(
            Scene scene,
            string sourceInstallRoot,
            string destinationInstallRoot,
            GameObject enemyPrefab,
            UnityEngine.Object freeFlowWeapon)
        {
            if (!scene.IsValid() || !scene.isLoaded) throw new ArgumentException("Scene is not loaded.");
            if (enemyPrefab == null) throw new ArgumentNullException(nameof(enemyPrefab));
            if (freeFlowWeapon == null) throw new ArgumentNullException(nameof(freeFlowWeapon));

            RemapCopiedInstallReferences(scene, sourceInstallRoot, destinationInstallRoot);
            RemoveLegacyMeleeInputs(scene);
            CloneFreeFlowInputRoot(scene);
            ConfigureMeleeWeaponRegistration(scene, freeFlowWeapon);
            CreatePersistentEnemies(scene, enemyPrefab);
            UpdateControlsText(scene);
            EditorSceneManager.MarkSceneDirty(scene);
        }

        public static string BakeNavMesh(Scene scene, GameObject parent)
        {
            Type surfaceType = Type.GetType("Unity.AI.Navigation.NavMeshSurface, Unity.AI.Navigation");
            if (surfaceType == null)
            {
                throw new InvalidOperationException(
                    "Unity AI Navigation is required to bake the Free Flow Combat demo NavMesh.");
            }

            DestroyNamedObjects(scene, "Free Flow Combat NavMesh Surface");
            var surfaceObject = new GameObject("Free Flow Combat NavMesh Surface");
            SceneManager.MoveGameObjectToScene(surfaceObject, scene);
            if (parent != null) surfaceObject.transform.SetParent(parent.transform, false);
            Component surface = surfaceObject.AddComponent(surfaceType);

            GameObject enemies = FindGameObject(scene, PersistentEnemiesRootName);
            bool enemiesWereActive = enemies != null && enemies.activeSelf;
            if (enemiesWereActive) enemies.SetActive(false);
            try
            {
                MethodInfo build = surfaceType.GetMethod(
                    "BuildNavMesh",
                    BindingFlags.Instance | BindingFlags.Public,
                    null,
                    Type.EmptyTypes,
                    null);
                if (build == null) throw new MissingMethodException(surfaceType.FullName, "BuildNavMesh");
                build.Invoke(surface, null);
            }
            finally
            {
                if (enemiesWereActive && enemies != null) enemies.SetActive(true);
            }

            PropertyInfo dataProperty = surfaceType.GetProperty(
                "navMeshData",
                BindingFlags.Instance | BindingFlags.Public);
            UnityEngine.Object data = dataProperty?.GetValue(surface) as UnityEngine.Object;
            if (data == null)
            {
                throw new InvalidOperationException("The Free Flow Combat NavMesh bake produced no data.");
            }

            string sceneDataFolder = Path.ChangeExtension(scene.path, null)?.Replace('\\', '/');
            if (string.IsNullOrEmpty(sceneDataFolder))
            {
                throw new InvalidOperationException("The generated Free Flow scene has no asset path.");
            }
            EnsureAssetFolder(sceneDataFolder);
            string destination = sceneDataFolder + "/NavMesh-FreeFlowCombat.asset";
            string current = AssetDatabase.GetAssetPath(data);
            if (string.IsNullOrEmpty(current))
            {
                if (AssetDatabase.LoadMainAssetAtPath(destination) != null)
                    AssetDatabase.DeleteAsset(destination);
                AssetDatabase.CreateAsset(data, destination);
            }
            else if (!string.Equals(current, destination, StringComparison.Ordinal))
            {
                if (AssetDatabase.LoadMainAssetAtPath(destination) != null &&
                    !AssetDatabase.DeleteAsset(destination))
                {
                    throw new IOException($"Could not replace '{destination}'.");
                }
                string error = AssetDatabase.MoveAsset(current, destination);
                if (!string.IsNullOrEmpty(error)) throw new InvalidOperationException(error);
            }

            EditorUtility.SetDirty(surface);
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();
            return destination;
        }

        public static void ExportExactInstallRoot(string installRoot, string packagePath)
        {
            if (!AssetDatabase.IsValidFolder(installRoot))
                throw new DirectoryNotFoundException($"Generated install root is missing: '{installRoot}'.");

            var payload = new HashSet<string>(StringComparer.Ordinal) { installRoot };
            string[] guids = AssetDatabase.FindAssets(string.Empty, new[] { installRoot });
            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                if (IsPathInside(path, installRoot)) payload.Add(path);
            }
            CollectFolders(installRoot, payload);
            NetworkEnemyShooterDemoEditorUtility.ExportExactPackage(
                payload.OrderBy(path => path, StringComparer.Ordinal).ToArray(),
                packagePath);
        }

        private static int PrepareReplicaEquipTriggers(GameObject root)
        {
            Character character = root.GetComponent<Character>();
            if (character == null) throw new InvalidOperationException("NPC prefab has no Character.");

            Trigger[] rootTriggers = root.GetComponents<Trigger>();
            Trigger equipTrigger = null;
            Instruction equipInstruction = null;
            for (int i = 0; i < rootTriggers.Length; i++)
            {
                Trigger trigger = rootTriggers[i];
                if (trigger == null) continue;

                SerializedProperty instructions = GetTopLevelInstructions(trigger);
                for (int instructionIndex = 0;
                     instructionIndex < instructions.arraySize;
                     instructionIndex++)
                {
                    object candidateValue = instructions
                        .GetArrayElementAtIndex(instructionIndex)
                        .managedReferenceValue;
                    if (candidateValue == null || !string.Equals(
                            candidateValue.GetType().FullName,
                            MeleeEquipTypeName,
                            StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (candidateValue is not Instruction candidate)
                    {
                        throw new InvalidOperationException(
                            $"'{MeleeEquipTypeName}' no longer derives from GC2 Instruction.");
                    }
                    if (!candidate.IsEnabled) continue;

                    if (equipInstruction != null)
                    {
                        throw new InvalidOperationException(
                            "The copied Melee player contains multiple enabled top-level Melee " +
                            "equip Instructions. The Free Flow demo builder cannot safely choose " +
                            "which equipment bootstrap should run on every replica.");
                    }

                    equipTrigger = trigger;
                    equipInstruction = candidate;
                }
            }

            if (equipTrigger == null || equipInstruction == null) return 0;

            // Replica equipment is initialization rather than AI simulation. Normalize the
            // copied player setup to one deterministic On Start action so an unrelated source
            // event or instruction can never execute on observer NPCs.
            equipTrigger.enabled = true;
            Trigger.Reconfigure(
                equipTrigger,
                new EventOnStart(),
                new InstructionList(equipInstruction));
            RewriteSelfTargets(equipTrigger, character);
            EditorUtility.SetDirty(equipTrigger);

            for (int i = 0; i < rootTriggers.Length; i++)
            {
                Trigger trigger = rootTriggers[i];
                if (trigger != null && trigger != equipTrigger)
                {
                    UnityEngine.Object.DestroyImmediate(trigger, true);
                }
            }

            // The NPC is scene-persistent. Network despawn tears down its equipment, so copied
            // player-only root Triggers (including OnDisable unequip) are intentionally removed.
            return 1;
        }

        private static SerializedProperty GetTopLevelInstructions(Trigger trigger)
        {
            var serialized = new SerializedObject(trigger);
            SerializedProperty wrapper = serialized.FindProperty("m_Instructions");
            SerializedProperty instructions = wrapper?.FindPropertyRelative("m_Instructions");
            if (instructions == null || !instructions.isArray)
            {
                throw new MissingFieldException(
                    trigger.GetType().FullName,
                    "m_Instructions.m_Instructions");
            }

            return instructions;
        }

        private static void RewriteSelfTargets(Component component, Character character)
        {
            var serialized = new SerializedObject(component);
            SerializedProperty property = serialized.GetIterator();
            bool enterChildren = true;
            bool changed = false;
            while (property.Next(enterChildren))
            {
                enterChildren = true;
                if (property.propertyType != SerializedPropertyType.ManagedReference ||
                    property.managedReferenceValue is not GetGameObjectSelf)
                {
                    continue;
                }

                // Bind replica initialization to the exact NPC Character instead of depending on
                // the Trigger's authored Self context. GetGameObjectCharacterTarget is
                // deliberately not used here: it resolves the Character's current combat target
                // and would equip the selected player instead of the NPC.
                property.managedReferenceValue =
                    new GetGameObjectCharactersInstance(character);
                changed = true;
            }
            if (changed) serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void RemoveLegacyMeleeInputs(Scene scene)
        {
            DestroyNamedObjects(
                scene,
                "Trigger_Release_Attack",
                "Trigger_Press_Defend",
                "Trigger_Release_Defend",
                InputRootName);

            if (SceneContainsManagedReference(scene, MeleeInputTypeName))
            {
                throw new InvalidOperationException(
                    "The copied scene still contains InstructionMeleeInputExecute after removing " +
                    "the known legacy input roots. Update the demo builder for the new fixture layout.");
            }
        }

        private static void CloneFreeFlowInputRoot(Scene destinationScene)
        {
            Scene previousActive = SceneManager.GetActiveScene();
            Scene sourceScene = EditorSceneManager.OpenScene(FreeFlowDemoScene, OpenSceneMode.Additive);
            try
            {
                GameObject source = sourceScene.GetRootGameObjects()
                    .SingleOrDefault(item => item != null && item.name == "Trigger");
                if (source == null)
                {
                    throw new InvalidOperationException(
                        "Free Flow Combat Demo.unity must contain one root named 'Trigger'.");
                }
                if (!ContainsManagedReference(source, FreeFlowAttackTypeName) ||
                    !ContainsManagedReference(source, FreeFlowCounterTypeName))
                {
                    throw new InvalidOperationException(
                        "The Free Flow Demo Trigger no longer contains both attack and counter Instructions.");
                }

                GameObject clone = UnityEngine.Object.Instantiate(source);
                clone.name = InputRootName;
                SceneManager.MoveGameObjectToScene(clone, destinationScene);
                GameObject session = FindSessionRoot(destinationScene);
                if (session != null) clone.transform.SetParent(session.transform, false);
                RejectExternalSceneReferences(clone, sourceScene);
            }
            finally
            {
                EditorSceneManager.CloseScene(sourceScene, true);
                if (previousActive.IsValid() && previousActive.isLoaded)
                    SceneManager.SetActiveScene(previousActive);
                else
                    SceneManager.SetActiveScene(destinationScene);
            }

            if (!SceneContainsManagedReference(destinationScene, FreeFlowAttackTypeName) ||
                !SceneContainsManagedReference(destinationScene, FreeFlowCounterTypeName))
            {
                throw new InvalidOperationException("The cloned Free Flow input root is incomplete.");
            }
        }

        private static void ConfigureMeleeWeaponRegistration(
            Scene scene,
            UnityEngine.Object freeFlowWeapon)
        {
            int configured = 0;
            foreach (Component component in EnumerateComponents(scene))
            {
                if (component is not MonoBehaviour behaviour || behaviour == null) continue;
                var serialized = new SerializedObject(behaviour);
                SerializedProperty weapons = serialized.FindProperty("m_RegisterWeapons");
                if (weapons == null || !weapons.isArray) continue;

                weapons.arraySize = 1;
                weapons.GetArrayElementAtIndex(0).objectReferenceValue = freeFlowWeapon;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(behaviour);
                configured++;
            }

            if (configured == 0)
            {
                throw new InvalidOperationException(
                    "The copied Melee scene has no transport bridge m_RegisterWeapons list.");
            }
        }

        private static void CreatePersistentEnemies(Scene scene, GameObject enemyPrefab)
        {
            DestroyNamedObjects(scene, PersistentEnemiesRootName);
            var root = new GameObject(PersistentEnemiesRootName);
            SceneManager.MoveGameObjectToScene(root, scene);

            // These are transport-owned scene objects, not children of the persistent session
            // bootstrap. Fusion moves the session root to DontDestroyOnLoad before its scene
            // manager collects scene NetworkObjects. Parenting the enemies below that root
            // therefore removes them from the scene just before Fusion can spawn/admit them.
            // PurrNet likewise needs its scene identities to remain in the loaded scene while
            // the manager establishes the server and client halves of a Host. Keep this
            // container as a scene root for both transports.
            root.transform.SetParent(null, false);

            for (int i = 0; i < s_EnemyPositions.Length; i++)
            {
                GameObject enemy = PrefabUtility.InstantiatePrefab(enemyPrefab, scene) as GameObject;
                if (enemy == null)
                    throw new InvalidOperationException($"Could not instantiate persistent enemy {i + 1}.");
                enemy.name = $"Free Flow Network Enemy {i + 1}";
                enemy.transform.SetParent(root.transform, false);
                enemy.transform.position = s_EnemyPositions[i];
                Vector3 look = new Vector3(-enemy.transform.position.x, 0f, -enemy.transform.position.z);
                if (look.sqrMagnitude > 0.0001f)
                    enemy.transform.rotation = Quaternion.LookRotation(look.normalized, Vector3.up);
            }
        }

        private static void UpdateControlsText(Scene scene)
        {
            const string replacement =
                "Left Mouse  Free Flow attack\n\n" +
                "Right Mouse  Counter\n\n" +
                "Middle Mouse  Block attack\n\n" +
                "Space  Dash\n\n" +
                "Dash Animation has to be registered in Network Character Component";

            foreach (Component component in EnumerateComponents(scene))
            {
                if (component is not MonoBehaviour behaviour || behaviour == null) continue;
                var serialized = new SerializedObject(behaviour);
                SerializedProperty text = serialized.FindProperty("m_Text");
                if (text == null || text.propertyType != SerializedPropertyType.String ||
                    string.IsNullOrEmpty(text.stringValue) ||
                    text.stringValue.IndexOf("Sword attack", StringComparison.Ordinal) < 0)
                {
                    continue;
                }

                text.stringValue = replacement;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(behaviour);
            }
        }

        private static void RemapCopiedInstallReferences(
            Scene scene,
            string sourceRoot,
            string destinationRoot)
        {
            foreach (Component component in EnumerateComponents(scene))
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
                    if (current == null) continue;
                    string currentPath = AssetDatabase.GetAssetPath(current);
                    if (!IsPathInside(currentPath, sourceRoot)) continue;

                    string candidate = destinationRoot + currentPath.Substring(sourceRoot.Length);
                    UnityEngine.Object replacement = LoadEquivalentObject(candidate, current);
                    if (replacement == null || replacement == current) continue;
                    property.objectReferenceValue = replacement;
                    changed = true;
                }
                if (changed) serialized.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        private static UnityEngine.Object LoadEquivalentObject(
            string assetPath,
            UnityEngine.Object source)
        {
            if (source is Component sourceComponent)
            {
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
                return prefab != null ? prefab.GetComponent(sourceComponent.GetType()) : null;
            }
            return AssetDatabase.LoadAssetAtPath(assetPath, source.GetType());
        }

        private static int ReplaceObjectReferences(
            GameObject root,
            UnityEngine.Object source,
            UnityEngine.Object replacement)
        {
            int count = 0;
            Component[] components = root.GetComponentsInChildren<Component>(true);
            for (int i = 0; i < components.Length; i++)
            {
                Component component = components[i];
                if (component == null || component is Transform) continue;
                var serialized = new SerializedObject(component);
                SerializedProperty property = serialized.GetIterator();
                bool enterChildren = true;
                bool changed = false;
                while (property.Next(enterChildren))
                {
                    enterChildren = true;
                    if (property.propertyType != SerializedPropertyType.ObjectReference ||
                        property.objectReferenceValue != source)
                    {
                        continue;
                    }
                    property.objectReferenceValue = replacement;
                    changed = true;
                    count++;
                }
                if (changed) serialized.ApplyModifiedPropertiesWithoutUndo();
            }
            return count;
        }

        private static int RemoveComponentsContaining(GameObject root, string managedTypeName)
        {
            MonoBehaviour[] behaviours = root.GetComponentsInChildren<MonoBehaviour>(true);
            int count = 0;
            for (int i = 0; i < behaviours.Length; i++)
            {
                MonoBehaviour behaviour = behaviours[i];
                if (behaviour == null || !ContainsManagedReference(behaviour, managedTypeName)) continue;
                UnityEngine.Object.DestroyImmediate(behaviour, true);
                count++;
            }
            return count;
        }

        private static bool ContainsManagedReference(GameObject root, string fullTypeName)
        {
            Component[] components = root.GetComponentsInChildren<Component>(true);
            for (int i = 0; i < components.Length; i++)
            {
                if (components[i] != null && ContainsManagedReference(components[i], fullTypeName))
                    return true;
            }
            return false;
        }

        private static bool ContainsManagedReference(Component component, string fullTypeName)
        {
            if (component == null) return false;
            var serialized = new SerializedObject(component);
            SerializedProperty property = serialized.GetIterator();
            bool enterChildren = true;
            while (property.Next(enterChildren))
            {
                enterChildren = true;
                if (property.propertyType != SerializedPropertyType.ManagedReference) continue;
                string serializedType = property.managedReferenceFullTypename ?? string.Empty;
                if (serializedType.EndsWith(" " + fullTypeName, StringComparison.Ordinal)) return true;
                object value = property.managedReferenceValue;
                if (string.Equals(value?.GetType().FullName, fullTypeName, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        private static bool SceneContainsManagedReference(Scene scene, string fullTypeName)
        {
            foreach (Component component in EnumerateComponents(scene))
            {
                if (ContainsManagedReference(component, fullTypeName)) return true;
            }
            return false;
        }

        private static void RejectExternalSceneReferences(GameObject root, Scene sourceScene)
        {
            Component[] components = root.GetComponentsInChildren<Component>(true);
            for (int i = 0; i < components.Length; i++)
            {
                Component component = components[i];
                if (component == null || component is Transform) continue;
                var serialized = new SerializedObject(component);
                SerializedProperty property = serialized.GetIterator();
                bool enterChildren = true;
                while (property.Next(enterChildren))
                {
                    enterChildren = true;
                    if (property.propertyType != SerializedPropertyType.ObjectReference) continue;
                    UnityEngine.Object value = property.objectReferenceValue;
                    GameObject referenced = value switch
                    {
                        GameObject gameObject => gameObject,
                        Component referencedComponent => referencedComponent.gameObject,
                        _ => null
                    };
                    if (referenced != null && referenced.scene == sourceScene)
                    {
                        throw new InvalidOperationException(
                            $"Free Flow input clone retains an external scene reference at " +
                            $"'{component.name}.{property.propertyPath}'.");
                    }
                }
            }
        }

        private static void SetNetworkActorType(
            NetworkCharacter networkCharacter,
            NetworkCharacterActorType actorType)
        {
            var serialized = new SerializedObject(networkCharacter);
            SerializedProperty property = serialized.FindProperty("m_ActorType");
            if (property == null)
                throw new MissingFieldException(typeof(NetworkCharacter).FullName, "m_ActorType");
            property.enumValueIndex = (int)actorType;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(networkCharacter);
        }

        private static Component EnsureOptionalComponent(GameObject root, Type type)
        {
            Component component = root.GetComponent(type);
            return component != null ? component : root.AddComponent(type);
        }

        private static Type RequireAdapterType()
        {
            return Type.GetType(AdapterTypeName, false) ??
                throw new InvalidOperationException(
                    "NetworkFreeFlowCombatAdapter is not compiled. Ensure the Networking Layer " +
                    "Melee integration and its dependencies compile before rebuilding the demo.");
        }

        private static Type ResolveType(string fullName)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type type = assembly.GetType(fullName, false);
                if (type != null) return type;
            }
            return null;
        }

        private static GameObject RequirePrefab(string path)
        {
            return AssetDatabase.LoadAssetAtPath<GameObject>(path) ??
                throw new FileNotFoundException("Prefab is missing.", path);
        }

        private static void SetBool(SerializedObject serialized, string name, bool value)
        {
            SerializedProperty property = serialized.FindProperty(name);
            if (property == null)
                throw new MissingFieldException(serialized.targetObject.GetType().FullName, name);
            property.boolValue = value;
        }

        private static GameObject FindSessionRoot(Scene scene)
        {
            return scene.GetRootGameObjects().FirstOrDefault(item =>
                item != null && item.name.EndsWith(" Session", StringComparison.Ordinal));
        }

        private static GameObject FindGameObject(Scene scene, string name)
        {
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                Transform[] transforms = root.GetComponentsInChildren<Transform>(true);
                for (int i = 0; i < transforms.Length; i++)
                {
                    if (transforms[i] != null && transforms[i].name == name)
                        return transforms[i].gameObject;
                }
            }
            return null;
        }

        private static void DestroyNamedObjects(Scene scene, params string[] names)
        {
            var wanted = new HashSet<string>(names, StringComparer.Ordinal);
            var destroy = new List<GameObject>();
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                Transform[] transforms = root.GetComponentsInChildren<Transform>(true);
                for (int i = 0; i < transforms.Length; i++)
                {
                    Transform transform = transforms[i];
                    if (transform != null && wanted.Contains(transform.name))
                        destroy.Add(transform.gameObject);
                }
            }

            // Remove deepest objects first so a selected parent cannot invalidate traversal.
            foreach (GameObject item in destroy
                         .Where(item => item != null)
                         .OrderByDescending(item => GetDepth(item.transform)))
            {
                if (item != null) UnityEngine.Object.DestroyImmediate(item);
            }
        }

        private static int GetDepth(Transform transform)
        {
            int depth = 0;
            while (transform != null)
            {
                depth++;
                transform = transform.parent;
            }
            return depth;
        }

        private static IEnumerable<Component> EnumerateComponents(Scene scene)
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

        private static bool IsPathInside(string path, string root)
        {
            return !string.IsNullOrEmpty(path) &&
                !string.IsNullOrEmpty(root) &&
                (string.Equals(path, root, StringComparison.Ordinal) ||
                 path.StartsWith(root + "/", StringComparison.Ordinal));
        }

        private static void EnsureAssetFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder)) return;
            string parent = Path.GetDirectoryName(folder)?.Replace('\\', '/');
            string name = Path.GetFileName(folder);
            if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(name))
                throw new InvalidOperationException($"Invalid asset folder '{folder}'.");
            EnsureAssetFolder(parent);
            AssetDatabase.CreateFolder(parent, name);
        }

        private static void CopyFolderRecursively(string source, string destination)
        {
            EnsureAssetFolder(destination);

            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
            if (string.IsNullOrEmpty(projectRoot))
            {
                throw new DirectoryNotFoundException(
                    "Could not resolve the Unity project root while copying the installer.");
            }

            string sourceAbsolute = Path.Combine(
                projectRoot,
                source.Replace('/', Path.DirectorySeparatorChar));
            foreach (string file in Directory.EnumerateFiles(
                         sourceAbsolute,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                if (file.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) continue;
                string assetPath = source + "/" + Path.GetFileName(file);
                string destinationPath = destination + "/" + Path.GetFileName(file);
                if (!AssetDatabase.CopyAsset(assetPath, destinationPath))
                {
                    throw new IOException(
                        $"Could not copy generated installer asset '{assetPath}' to " +
                        $"'{destinationPath}'.");
                }
            }

            string[] subfolders = AssetDatabase.GetSubFolders(source);
            for (int i = 0; i < subfolders.Length; i++)
            {
                string child = subfolders[i];
                CopyFolderRecursively(
                    child,
                    destination + "/" + Path.GetFileName(child));
            }
        }

        private static void CollectFolders(string root, ISet<string> payload)
        {
            string[] children = AssetDatabase.GetSubFolders(root);
            for (int i = 0; i < children.Length; i++)
            {
                payload.Add(children[i]);
                CollectFolders(children[i], payload);
            }
        }
    }
}
