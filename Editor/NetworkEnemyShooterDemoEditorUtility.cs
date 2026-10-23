using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using GameCreator.Runtime.Characters;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace Arawn.GameCreator2.Networking.Editor
{
    /// <summary>Deterministic rebuild helpers shared by the Fusion and PurrNet enemy demos.</summary>
    public static class NetworkEnemyShooterDemoEditorUtility
    {
        public const string EnemyAiScene =
            "Assets/Plugins/GameCreator/Installs/Shooter.Examples@1.2.4/19_Enemy_AI.unity";
        public const string AuthorityAiRootName = "Authority Only AI";

        public static void MoveInstallRoot(string currentRoot, string nextRoot)
        {
            if (!AssetDatabase.IsValidFolder(currentRoot))
                throw new DirectoryNotFoundException($"Missing installed demo source: {currentRoot}");
            if (AssetDatabase.IsValidFolder(nextRoot))
                throw new InvalidOperationException(
                    $"Refusing to overwrite existing installer staging root '{nextRoot}'.");

            string error = AssetDatabase.MoveAsset(currentRoot, nextRoot);
            if (!string.IsNullOrEmpty(error)) throw new InvalidOperationException(error);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        }

        public static GameObject BuildEnemyPrefab(
            string playerPrefabPath,
            string enemyPrefabPath,
            string demoWeaponPath,
            Action<GameObject, List<string>> configureTransport)
        {
            if (!AssetDatabase.CopyAsset(playerPrefabPath, enemyPrefabPath))
                throw new InvalidOperationException(
                    $"Could not copy '{playerPrefabPath}' to '{enemyPrefabPath}'.");
            AssetDatabase.ImportAsset(enemyPrefabPath, ImportAssetOptions.ForceSynchronousImport);

            UnityEngine.Object demoWeapon = AssetDatabase.LoadMainAssetAtPath(demoWeaponPath);
            if (demoWeapon == null) throw new FileNotFoundException("Demo weapon is missing.", demoWeaponPath);

            Scene sampleScene = EditorSceneManager.OpenScene(EnemyAiScene, OpenSceneMode.Additive);
            GameObject sourceEnemy = sampleScene.GetRootGameObjects()
                .FirstOrDefault(item => item != null && item.name == "Enemy");
            if (sourceEnemy == null)
            {
                EditorSceneManager.CloseScene(sampleScene, true);
                throw new InvalidOperationException("Shooter 19_Enemy_AI has no Enemy root.");
            }

            Character sourceCharacter = sourceEnemy.GetComponentInChildren<Character>(true);
            GameObject prefabRoot = PrefabUtility.LoadPrefabContents(enemyPrefabPath);
            var changes = new List<string>();
            try
            {
                prefabRoot.name = Path.GetFileNameWithoutExtension(enemyPrefabPath);
                Transform existingAi = prefabRoot.transform.Find(AuthorityAiRootName);
                if (existingAi != null) UnityEngine.Object.DestroyImmediate(existingAi.gameObject);

                var aiRoot = new GameObject(AuthorityAiRootName);
                aiRoot.transform.SetParent(prefabRoot.transform, false);
                foreach (Transform child in sourceEnemy.transform)
                {
                    if (child == null ||
                        (child.name != "Trigger_Start" && child.name != "Trigger_AI"))
                        continue;
                    GameObject clone = UnityEngine.Object.Instantiate(child.gameObject);
                    clone.name = child.name;
                    clone.transform.SetParent(aiRoot.transform, false);
                }
                changes.Add("GC2 19_Enemy_AI start/update Trigger structure");

                ReplaceAiObjectReferences(
                    aiRoot,
                    sourceEnemy,
                    sourceCharacter,
                    prefabRoot,
                    demoWeapon);

                var errors = new List<string>();
                NetworkNpcSetupEditorUtility.ConfigureServerNpc(
                    prefabRoot,
                    NetworkPredictionBackend.BuiltIn,
                    new[] { AuthorityAiRootName },
                    Array.Empty<Type>(),
                    changes,
                    errors);
                if (errors.Count > 0)
                    throw new InvalidOperationException(string.Join("\n", errors));

                configureTransport?.Invoke(prefabRoot, changes);
                // The authority gate captures the authored active state in Awake and immediately
                // disables this root until the network role resolves. Keep the prefab-authored
                // state enabled so authority can restore and execute the copied GC2 AI.
                aiRoot.SetActive(true);
                PrefabUtility.SaveAsPrefabAsset(prefabRoot, enemyPrefabPath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(prefabRoot);
                EditorSceneManager.CloseScene(sampleScene, true);
            }

            AssetDatabase.ImportAsset(enemyPrefabPath, ImportAssetOptions.ForceSynchronousImport);
            GameObject result = AssetDatabase.LoadAssetAtPath<GameObject>(enemyPrefabPath);
            if (result == null) throw new InvalidOperationException("Generated enemy prefab is not importable.");
            Debug.Log(
                $"[GC2 Networking Demo Builder] Prepared '{enemyPrefabPath}': " +
                string.Join(", ", changes));
            return result;
        }

        public static Scene CopyAndOpenScene(string sourcePath, string destinationPath)
        {
            if (!AssetDatabase.CopyAsset(sourcePath, destinationPath))
                throw new InvalidOperationException(
                    $"Could not copy '{sourcePath}' to '{destinationPath}'.");
            AssetDatabase.ImportAsset(destinationPath, ImportAssetOptions.ForceSynchronousImport);
            return EditorSceneManager.OpenScene(destinationPath, OpenSceneMode.Single);
        }

        public static string BakeNavMesh(Scene scene, GameObject parent)
        {
            Type surfaceType = Type.GetType(
                "Unity.AI.Navigation.NavMeshSurface, Unity.AI.Navigation");
            if (surfaceType == null)
                throw new InvalidOperationException(
                    "Unity AI Navigation is required to bake the Enemy Shooter demo NavMesh.");

            GameObject surfaceObject = new GameObject("Enemy Demo NavMesh Surface");
            if (parent != null) surfaceObject.transform.SetParent(parent.transform, false);
            Component surface = surfaceObject.AddComponent(surfaceType);
            MethodInfo build = surfaceType.GetMethod(
                "BuildNavMesh",
                BindingFlags.Instance | BindingFlags.Public,
                null,
                Type.EmptyTypes,
                null);
            if (build == null) throw new MissingMethodException(surfaceType.FullName, "BuildNavMesh");
            build.Invoke(surface, null);
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();

            PropertyInfo dataProperty = surfaceType.GetProperty(
                "navMeshData",
                BindingFlags.Instance | BindingFlags.Public);
            UnityEngine.Object data = dataProperty?.GetValue(surface) as UnityEngine.Object;
            string path = data != null ? AssetDatabase.GetAssetPath(data) : string.Empty;
            if (data == null)
                throw new InvalidOperationException("The Enemy Shooter demo NavMesh bake produced no data.");
            if (string.IsNullOrEmpty(path))
            {
                string sceneFolder = Path.ChangeExtension(scene.path, null)?.Replace('\\', '/');
                if (string.IsNullOrEmpty(sceneFolder))
                    throw new InvalidOperationException("The Enemy Shooter demo scene has no asset path.");
                string parentFolder = Path.GetDirectoryName(sceneFolder)?.Replace('\\', '/');
                string folderName = Path.GetFileName(sceneFolder);
                if (!AssetDatabase.IsValidFolder(sceneFolder))
                    AssetDatabase.CreateFolder(parentFolder, folderName);
                path = sceneFolder + "/NavMesh-EnemyDemo.asset";
                AssetDatabase.CreateAsset(data, path);
                EditorUtility.SetDirty(surface);
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
                AssetDatabase.SaveAssets();
            }
            return path;
        }

        public static List<Transform> CreateSlotAnchors(
            GameObject coordinator,
            int count)
        {
            var root = new GameObject("Slot Anchors");
            root.transform.SetParent(coordinator.transform, false);
            var anchors = new List<Transform>(count);
            for (int i = 0; i < count; i++)
            {
                var anchor = new GameObject($"Combatant Slot {i + 1}");
                anchor.transform.SetParent(root.transform, false);
                float angle = (Mathf.PI * 2f * i) / count;
                anchor.transform.localPosition =
                    new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * 5f;
                anchor.transform.localRotation = Quaternion.LookRotation(
                    -anchor.transform.localPosition.normalized,
                    Vector3.up);
                anchors.Add(anchor.transform);
            }
            return anchors;
        }

        public static void ConfigureSlots(
            Component coordinator,
            IReadOnlyList<Transform> anchors,
            GameObject enemyPrefab,
            string idPrefix)
        {
            var serialized = new SerializedObject(coordinator);
            SerializedProperty slots = serialized.FindProperty("m_Slots");
            if (slots == null || !slots.isArray)
                throw new InvalidOperationException("Bot-slot coordinator has no slot definitions.");
            slots.arraySize = anchors.Count;
            for (int i = 0; i < anchors.Count; i++)
            {
                SerializedProperty slot = slots.GetArrayElementAtIndex(i);
                slot.FindPropertyRelative("m_StableSlotId").stringValue = $"{idPrefix}-{i + 1}";
                slot.FindPropertyRelative("m_Anchor").objectReferenceValue = anchors[i];
                slot.FindPropertyRelative("m_BotPrefab").objectReferenceValue = enemyPrefab;
            }
            SerializedProperty fill = serialized.FindProperty("m_FillVacantSlotsWithBots");
            if (fill != null) fill.boolValue = true;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(coordinator);
        }

        public static void ExportExactPackage(
            IReadOnlyList<string> assetPaths,
            string packagePath)
        {
            string[] distinct = assetPaths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            for (int i = 0; i < distinct.Length; i++)
            {
                if (AssetDatabase.LoadMainAssetAtPath(distinct[i]) == null &&
                    !AssetDatabase.IsValidFolder(distinct[i]))
                    throw new FileNotFoundException("Installer payload entry is missing.", distinct[i]);
            }

            string fullOutput = Path.GetFullPath(packagePath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullOutput) ?? ".");
#if UNITY_6000_6_OR_NEWER
            // Unity 6000.6 obsoletes AssetDatabase.ExportPackage; nested installers stay unsigned.
            UnityEditor.AssetPackage.Package.Export(
                new UnityEditor.AssetPackage.ExportPackageParameters(
                    distinct,
                    fullOutput,
                    string.Empty,
                    ExportPackageOptions.Default));
#else
            AssetDatabase.ExportPackage(distinct, fullOutput, ExportPackageOptions.Default);
#endif
            if (!File.Exists(fullOutput))
                throw new InvalidOperationException($"Unity did not write '{fullOutput}'.");
        }

        public static void BuildStandaloneSmokePlayer(string scenePath, string transportName)
        {
            BuildStandaloneSmokePlayer(scenePath, transportName, replaceMaterialsForSmoke: false);
        }

        /// <summary>
        /// Builds a smoke player. Malbers demos pass <paramref name="replaceMaterialsForSmoke"/>
        /// so the Shader Graph Malbers materials are replaced in the disposable fixture before
        /// the player build; their variant space otherwise dominates an unattended smoke build.
        /// </summary>
        public static void BuildStandaloneSmokePlayer(
            string scenePath,
            string transportName,
            bool replaceMaterialsForSmoke)
        {
            string output = Environment.GetEnvironmentVariable(
                "GC2_NETWORK_SMOKE_BUILD_PATH");
            if (string.IsNullOrWhiteSpace(output))
            {
                throw new InvalidOperationException(
                    "GC2_NETWORK_SMOKE_BUILD_PATH must identify the standalone player output.");
            }
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(scenePath) == null)
                throw new FileNotFoundException("Enemy smoke scene is missing.", scenePath);

            string buildScenePath = replaceMaterialsForSmoke
                ? PrepareSmokeScene(scenePath)
                : scenePath;

            string fullOutput = Path.GetFullPath(output);
            Directory.CreateDirectory(Path.GetDirectoryName(fullOutput) ?? ".");
            var options = new BuildPlayerOptions
            {
                scenes = new[] { buildScenePath },
                locationPathName = fullOutput,
                target = BuildTarget.StandaloneLinux64,
                options = BuildOptions.Development
            };
            string previousStripper = Environment.GetEnvironmentVariable(
                "GC2_NETWORK_SMOKE_STRIP_SHADERS");
            RenderPipelineAsset previousDefaultPipeline = GraphicsSettings.defaultRenderPipeline;
            int previousQualityLevel = QualitySettings.GetQualityLevel();
            int qualityCount = QualitySettings.names.Length;
            var previousQualityPipelines = new RenderPipelineAsset[qualityCount];
            if (replaceMaterialsForSmoke)
            {
                // A stripped smoke player has no visual requirement. Removing the render
                // pipeline keeps URP's Lit variant space, which the Malbers shader graph
                // amplifies, out of the build entirely.
                for (int i = 0; i < qualityCount; i++)
                {
                    QualitySettings.SetQualityLevel(i, false);
                    previousQualityPipelines[i] = QualitySettings.renderPipeline;
                    QualitySettings.renderPipeline = null;
                }

                QualitySettings.SetQualityLevel(previousQualityLevel, false);
                GraphicsSettings.defaultRenderPipeline = null;
                Environment.SetEnvironmentVariable("GC2_NETWORK_SMOKE_STRIP_SHADERS", "1");
            }

            BuildReport report;
            try
            {
                report = BuildPipeline.BuildPlayer(options);
            }
            finally
            {
                Environment.SetEnvironmentVariable(
                    "GC2_NETWORK_SMOKE_STRIP_SHADERS",
                    previousStripper);
                if (replaceMaterialsForSmoke)
                {
                    GraphicsSettings.defaultRenderPipeline = previousDefaultPipeline;
                    for (int i = 0; i < qualityCount; i++)
                    {
                        QualitySettings.SetQualityLevel(i, false);
                        QualitySettings.renderPipeline = previousQualityPipelines[i];
                    }

                    QualitySettings.SetQualityLevel(previousQualityLevel, false);
                }
            }

            if (report.summary.result != BuildResult.Succeeded)
            {
                throw new InvalidOperationException(
                    $"{transportName} network smoke player failed to build: " +
                    $"{report.summary.result}, errors={report.summary.totalErrors}.");
            }

            Debug.Log(
                $"[GC2 Network Smoke CI] Built {transportName} player at '{fullOutput}'.");
        }

        private static string PrepareSmokeScene(string scenePath)
        {
            const string generatedFolder = "Assets/Arawn/NetworkingLayerForGC2/Generated/MalbersSmoke";
            EnsureFolder(generatedFolder);

            string materialPath = generatedFolder + "/MalbersSmokeUnlit.mat";
            Material material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            Shader shader = Shader.Find("Unlit/Color") ??
                            Shader.Find("Sprites/Default") ??
                            Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null)
                throw new InvalidOperationException("No fallback smoke shader is available.");

            if (material == null)
            {
                material = new Material(shader) { name = "MalbersSmokeUnlit" };
                AssetDatabase.CreateAsset(material, materialPath);
            }
            else if (material.shader != shader)
            {
                material.shader = shader;
                EditorUtility.SetDirty(material);
            }

            string installRoot = Path.GetDirectoryName(scenePath)?.Replace('\\', '/');
            if (string.IsNullOrEmpty(installRoot))
                throw new InvalidOperationException("The smoke scene has no install root.");

            // Nested Malbers prefab variants keep their base materials even when a scene or
            // outer prefab overrides renderers. Retarget the licensed materials themselves in
            // this disposable fixture so the Shader Graph variant space is not built at all.
            ReplaceMaterialShaders(
                material.shader,
                "Assets/Malbers Animations",
                installRoot);

            string[] prefabGuids = AssetDatabase.FindAssets("t:Prefab", new[] { installRoot });
            for (int i = 0; i < prefabGuids.Length; i++)
            {
                string prefabPath = AssetDatabase.GUIDToAssetPath(prefabGuids[i]);
                GameObject root = PrefabUtility.LoadPrefabContents(prefabPath);
                try
                {
                    ReplaceRendererMaterials(root, material);
                    PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
                }
                finally
                {
                    PrefabUtility.UnloadPrefabContents(root);
                }
            }

            string smokeScenePath = generatedFolder + "/MalbersSmokeScene.unity";
            Scene scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
            ReplaceRendererMaterials(scene, material);
            EnableMalbersSmokeDiagnostics(scene);
            EditorSceneManager.SaveScene(scene, smokeScenePath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            Debug.Log(
                $"[GC2 Network Smoke CI] Prepared stripped smoke scene '{smokeScenePath}'.");
            return smokeScenePath;
        }

        /// <summary>
        /// Turns on the Malbers replication diagnostics in the disposable smoke scene only. A
        /// scene-authored manager is used by the transport, so both peers log send/apply drops.
        /// </summary>
        private static void EnableMalbersSmokeDiagnostics(Scene scene)
        {
            if (!scene.IsValid() || !scene.isLoaded) return;

            GameObject host = scene.GetRootGameObjects()
                .FirstOrDefault(root => root != null && root.name == "Malbers Smoke Diagnostics");
            if (host == null)
            {
                host = new GameObject("Malbers Smoke Diagnostics");
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(host, scene);
            }

            NetworkMalbersStateManager manager = host.GetComponent<NetworkMalbersStateManager>();
            if (manager == null) manager = host.AddComponent<NetworkMalbersStateManager>();
            EnableSerializedFlag(manager, "m_LogDiagnostics");

            string[] bridgeTypeNames =
            {
                "Arawn.GameCreator2.Networking.Malbers.NetworkMalbersStateBridge"
            };
            for (int i = 0; i < bridgeTypeNames.Length; i++)
            {
                Type bridgeType = NetworkMalbersIntegrationPolicy.FindLoadedType(bridgeTypeNames[i]);
                if (bridgeType == null) continue;

                foreach (GameObject root in scene.GetRootGameObjects())
                {
                    MonoBehaviour[] behaviours = root.GetComponentsInChildren(bridgeType, true)
                        .OfType<MonoBehaviour>()
                        .ToArray();
                    for (int b = 0; b < behaviours.Length; b++)
                    {
                        EnableSerializedFlag(behaviours[b], "m_LogDiagnostics");
                    }
                }
            }

            EditorSceneManager.MarkSceneDirty(scene);
        }

        private static void EnableSerializedFlag(UnityEngine.Object target, string fieldName)
        {
            if (target == null) return;

            var serialized = new SerializedObject(target);
            SerializedProperty property = serialized.FindProperty(fieldName);
            if (property == null || property.propertyType != SerializedPropertyType.Boolean) return;
            if (property.boolValue) return;

            property.boolValue = true;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(target);
        }

        private static void ReplaceRendererMaterials(GameObject root, Material material)
        {
            if (root == null || material == null) return;
            foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                ReplaceRendererMaterials(renderer, material);
            }
        }

        private static void ReplaceRendererMaterials(Scene scene, Material material)
        {
            if (!scene.IsValid() || !scene.isLoaded || material == null) return;
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                ReplaceRendererMaterials(root, material);
            }
        }

        private static void ReplaceRendererMaterials(Renderer renderer, Material material)
        {
            if (renderer == null) return;
            int count = Mathf.Max(1, renderer.sharedMaterials.Length);
            var materials = new Material[count];
            for (int i = 0; i < count; i++) materials[i] = material;
            renderer.sharedMaterials = materials;
        }

        private static void ReplaceMaterialShaders(Shader shader, params string[] roots)
        {
            if (shader == null || roots == null || roots.Length == 0) return;

            for (int rootIndex = 0; rootIndex < roots.Length; rootIndex++)
            {
                string root = roots[rootIndex];
                if (string.IsNullOrEmpty(root) || !AssetDatabase.IsValidFolder(root)) continue;

                string[] guids = AssetDatabase.FindAssets("t:Material", new[] { root });
                for (int i = 0; i < guids.Length; i++)
                {
                    string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                    Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
                    if (material == null || material.shader == shader) continue;
                    material.shader = shader;
                    EditorUtility.SetDirty(material);
                }
            }

            AssetDatabase.SaveAssets();
        }

        private static void EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder)) return;
            string parent = Path.GetDirectoryName(folder)?.Replace('\\', '/');
            if (string.IsNullOrEmpty(parent))
                throw new InvalidOperationException($"Cannot create folder '{folder}'.");
            if (!AssetDatabase.IsValidFolder(parent)) EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));
        }

        private static void ReplaceAiObjectReferences(
            GameObject aiRoot,
            GameObject sourceEnemy,
            Character sourceCharacter,
            GameObject destinationCharacter,
            UnityEngine.Object demoWeapon)
        {
            Component[] components = aiRoot.GetComponentsInChildren<Component>(true);
            for (int componentIndex = 0; componentIndex < components.Length; componentIndex++)
            {
                Component component = components[componentIndex];
                if (component == null || component is Transform) continue;
                var serialized = new SerializedObject(component);
                SerializedProperty property = serialized.GetIterator();
                bool enterChildren = true;
                bool changed = false;
                while (property.Next(enterChildren))
                {
                    enterChildren = true;
                    if (property.propertyType == SerializedPropertyType.ManagedReference &&
                        property.managedReferenceValue is GetGameObjectPlayer)
                    {
                        property.managedReferenceValue =
                            CreateCharacterTargetGetter(
                                destinationCharacter.GetComponent<Character>());
                        changed = true;
                        continue;
                    }
                    if (property.propertyType != SerializedPropertyType.ObjectReference) continue;
                    UnityEngine.Object current = property.objectReferenceValue;
                    if (current == null) continue;

                    UnityEngine.Object replacement = null;
                    if (current.GetType().Name == "ShooterWeapon")
                    {
                        replacement = demoWeapon;
                    }
                    else if (IsBelow(current, sourceEnemy.transform))
                    {
                        replacement = MapEnemyReference(
                            current,
                            sourceCharacter,
                            destinationCharacter);
                    }

                    if (replacement == null || replacement == current) continue;
                    property.objectReferenceValue = replacement;
                    changed = true;
                }
                if (changed) serialized.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        private static GetGameObjectCharacterTarget CreateCharacterTargetGetter(
            Character character)
        {
            var getter = new GetGameObjectCharacterTarget();
            FieldInfo from = typeof(GetGameObjectCharacterTarget).GetField(
                "m_From",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (from == null)
                throw new MissingFieldException(typeof(GetGameObjectCharacterTarget).FullName, "m_From");
            from.SetValue(getter, GetGameObjectCharactersInstance.CreateWith(character));
            return getter;
        }

        private static bool IsBelow(UnityEngine.Object value, Transform ancestor)
        {
            Transform transform = value switch
            {
                GameObject gameObject => gameObject.transform,
                Component component => component.transform,
                _ => null
            };
            return transform != null &&
                (transform == ancestor || transform.IsChildOf(ancestor));
        }

        private static UnityEngine.Object MapEnemyReference(
            UnityEngine.Object value,
            Character sourceCharacter,
            GameObject destination)
        {
            if (value is Character) return destination.GetComponent<Character>();
            if (value is GameObject gameObject &&
                sourceCharacter != null &&
                gameObject == sourceCharacter.gameObject)
                return destination;
            if (value is Transform transform &&
                sourceCharacter != null &&
                transform == sourceCharacter.transform)
                return destination.transform;
            if (value is Component component)
                return destination.GetComponent(component.GetType());
            return value is GameObject ? destination : destination.transform;
        }
    }
}
