using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.VisualScripting;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Arawn.GameCreator2.Networking.Editor
{
    /// <summary>
    /// Deterministic authoring helpers shared by the transport-specific Core ragdoll demos.
    /// The helpers deliberately operate only on exact GC2 Networking Layer install roots and
    /// generated demo objects.
    /// </summary>
    public static class NetworkRagdollDemoEditorUtility
    {
        public const string SkeletonPath =
            "Assets/Plugins/GameCreator/Packages/Core/Runtime/Characters/Assets/3D/" +
            "Skeleton.asset";
        public const string RecoverFaceDownPath =
            "Assets/Plugins/GameCreator/Packages/Core/Runtime/Characters/Assets/3D/" +
            "Animations/Actions/Human@Action_StandFaceDown.anim";
        public const string RecoverFaceUpPath =
            "Assets/Plugins/GameCreator/Packages/Core/Runtime/Characters/Assets/3D/" +
            "Animations/Actions/Human@Action_StandFaceUp.anim";

        public const string DemoRootName = "Network Ragdoll Demo";
        public const string StartActionsName = "Start Ragdoll Actions";
        public const string RecoverActionsName = "Recover Ragdoll Actions";

        private const string InstallsPrefix = "Assets/Plugins/GameCreator/Installs/";

        private static readonly string[] DemoOnlyObjectNames =
        {
            "Scene Network Variables",
            "Variable Values UI",
            "Network Actions Door Demo",
            "Network Actions Door Demo UI",
            "Network Action Manager",
            "Network Variable Manager",
            "Fusion Network Actions Bridge",
            "Fusion Variable Bridge",
            "PurrNet Network Actions Bridge",
            "PurrNet Variable Bridge"
        };

        /// <summary>
        /// Migrates the exact prior install root when necessary and removes other stale versions
        /// of the same installer. If the requested root already exists, it is retained in place.
        /// </summary>
        public static void EnsureInstallRoot(string previousRoot, string installRoot)
        {
            ValidateInstallRootPair(previousRoot, installRoot, out string installId);

            if (!AssetDatabase.IsValidFolder(installRoot))
            {
                if (!AssetDatabase.IsValidFolder(previousRoot))
                {
                    throw new DirectoryNotFoundException(
                        $"Neither the previous Core Examples root '{previousRoot}' nor the " +
                        $"current root '{installRoot}' exists.");
                }

                string error = AssetDatabase.MoveAsset(previousRoot, installRoot);
                if (!string.IsNullOrEmpty(error))
                {
                    throw new InvalidOperationException(
                        $"Could not migrate '{previousRoot}' to '{installRoot}': {error}");
                }
            }

            RemoveStaleInstallRoots(installId, installRoot);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

            if (!AssetDatabase.IsValidFolder(installRoot))
            {
                throw new DirectoryNotFoundException(
                    $"Core Examples migration did not produce '{installRoot}'.");
            }
        }

        /// <summary>
        /// Reimports the checked-in installer into a clean fixture root. This method never
        /// exports or rewrites the source archive.
        /// </summary>
        public static void RebuildValidationInstallRoot(
            string packagePath,
            string previousRoot,
            string installRoot)
        {
            ValidateInstallRootPair(previousRoot, installRoot, out string installId);
            string fullPackagePath = ProjectPath(packagePath);
            if (!File.Exists(fullPackagePath))
            {
                throw new FileNotFoundException(
                    "The checked-in Core Examples installer archive is missing.",
                    packagePath);
            }

            RemoveAllInstallRoots(installId);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

            // AssetDatabase.ImportPackage is asynchronous/no-op in some Unity batch-mode
            // sessions, especially when the archive itself lives below Assets. Extract the
            // simple Unity-package TAR payload directly so the fixture exists before this
            // executeMethod returns. Every pathname is constrained to the exact installer ID.
            ExtractPackageInstallRoot(
                fullPackagePath,
                previousRoot,
                installRoot);
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

            EnsureInstallRoot(previousRoot, installRoot);
        }

        /// <summary>
        /// Configures the existing transport player prefab in place so its GUID, labels, and all
        /// scene/spawner references remain stable.
        /// </summary>
        public static GameObject ConfigurePlayerPrefab(string prefabPath)
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null)
                throw new FileNotFoundException("The Core demo player prefab is missing.", prefabPath);

            Skeleton skeleton = RequireAsset<Skeleton>(SkeletonPath);
            AnimationClip faceDown = RequireAsset<AnimationClip>(RecoverFaceDownPath);
            AnimationClip faceUp = RequireAsset<AnimationClip>(RecoverFaceUpPath);

            GameObject contents = PrefabUtility.LoadPrefabContents(prefabPath);
            try
            {
                Character character = contents.GetComponent<Character>();
                NetworkCharacter networkCharacter = contents.GetComponent<NetworkCharacter>();
                if (character == null || networkCharacter == null)
                {
                    throw new InvalidOperationException(
                        $"'{prefabPath}' must have Character and NetworkCharacter on its root.");
                }

                ConfigureGc2Ragdoll(character, skeleton, faceDown, faceUp);
                ConfigureNetworkCharacter(networkCharacter);

                EditorUtility.SetDirty(character);
                EditorUtility.SetDirty(networkCharacter);
                if (PrefabUtility.SaveAsPrefabAsset(contents, prefabPath) == null)
                {
                    throw new InvalidOperationException(
                        $"Unity could not save the configured Core demo player '{prefabPath}'.");
                }
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }

            AssetDatabase.ImportAsset(prefabPath, ImportAssetOptions.ForceSynchronousImport);
            prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null)
                throw new InvalidOperationException("The configured player prefab is not importable.");

            ValidateConfiguredPlayer(prefab, skeleton, faceDown, faceUp);
            return prefab;
        }

        /// <summary>
        /// Copies the existing Core/Variables scene once and then idempotently authors the
        /// dedicated ragdoll controls in the generated scene.
        /// </summary>
        public static Scene CopyAndConfigureRagdollScene(
            string sourceScenePath,
            string ragdollScenePath,
            string title)
        {
            if (string.Equals(sourceScenePath, ragdollScenePath, StringComparison.Ordinal))
                throw new InvalidOperationException("The source and ragdoll scene paths must differ.");
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(sourceScenePath) == null)
                throw new FileNotFoundException("The Core/Variables source scene is missing.", sourceScenePath);

            SceneAsset generated = AssetDatabase.LoadAssetAtPath<SceneAsset>(ragdollScenePath);
            if (generated == null)
            {
                if (AssetDatabase.LoadMainAssetAtPath(ragdollScenePath) != null)
                {
                    throw new InvalidOperationException(
                        $"The ragdoll scene destination '{ragdollScenePath}' is not a scene.");
                }
                if (!AssetDatabase.CopyAsset(sourceScenePath, ragdollScenePath))
                {
                    throw new InvalidOperationException(
                        $"Could not copy '{sourceScenePath}' to '{ragdollScenePath}'.");
                }
                AssetDatabase.ImportAsset(
                    ragdollScenePath,
                    ImportAssetOptions.ForceSynchronousImport);
            }

            Scene scene = EditorSceneManager.OpenScene(ragdollScenePath, OpenSceneMode.Single);
            if (!scene.IsValid() || !scene.isLoaded)
            {
                throw new InvalidOperationException(
                    $"Could not open the generated ragdoll scene '{ragdollScenePath}'.");
            }

            RemoveVariableAndDoorObjects(scene);
            GameObject demoRoot = RequireSingleDemoRoot(scene);
            Actions startActions = ConfigureActions<InstructionNetworkCoreStartRagdoll>(
                demoRoot,
                StartActionsName);
            Actions recoverActions = ConfigureActions<InstructionNetworkCoreRecoverRagdoll>(
                demoRoot,
                RecoverActionsName);
            ConfigureDemoUi(demoRoot, startActions, recoverActions, title);
            TryUpdateVisibleLegend(scene, title);

            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene, ragdollScenePath))
            {
                throw new InvalidOperationException(
                    $"Unity could not save the generated ragdoll scene '{ragdollScenePath}'.");
            }
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(
                ragdollScenePath,
                ImportAssetOptions.ForceSynchronousImport);
            return scene;
        }

        /// <summary>Exports exactly one complete Core Examples install root.</summary>
        public static void ExportExactInstallRoot(string installRoot, string packagePath)
        {
            ValidateSingleInstallRoot(installRoot, out _);
            if (!AssetDatabase.IsValidFolder(installRoot))
                throw new DirectoryNotFoundException($"Missing installer root '{installRoot}'.");

            string fullPackagePath = ProjectPath(packagePath);
            string outputDirectory = Path.GetDirectoryName(fullPackagePath);
            if (string.IsNullOrEmpty(outputDirectory))
                throw new InvalidOperationException($"Invalid package output path '{packagePath}'.");
            Directory.CreateDirectory(outputDirectory);

            AssetDatabase.ExportPackage(
                new[] { installRoot },
                fullPackagePath,
                ExportPackageOptions.Recurse);
            if (!File.Exists(fullPackagePath))
                throw new InvalidOperationException($"Unity did not write '{fullPackagePath}'.");
        }

        private static void ConfigureGc2Ragdoll(
            Character character,
            Skeleton skeleton,
            AnimationClip faceDown,
            AnimationClip faceUp)
        {
            var serialized = new SerializedObject(character);
            SerializedProperty ragdollContainer = RequireProperty(serialized, "m_Ragdoll");
            SerializedProperty ragdoll = RequireRelative(ragdollContainer, "m_Ragdoll");
            ragdoll.managedReferenceValue = new RagdollDefault();
            serialized.ApplyModifiedPropertiesWithoutUndo();
            serialized.Update();

            ragdollContainer = RequireProperty(serialized, "m_Ragdoll");
            ragdoll = RequireRelative(ragdollContainer, "m_Ragdoll");
            SerializedProperty boneRack = RequireRelative(ragdoll, "m_BoneRack");
            RequireRelative(boneRack, "m_Skeleton").objectReferenceValue = skeleton;
            RequireRelative(ragdoll, "m_TransitionDuration").floatValue = 0.2f;
            RequireRelative(ragdoll, "m_RecoverFaceDown").objectReferenceValue = faceDown;
            RequireRelative(ragdoll, "m_RecoverFaceUp").objectReferenceValue = faceUp;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void ConfigureNetworkCharacter(NetworkCharacter networkCharacter)
        {
            var serialized = new SerializedObject(networkCharacter);
            RequireProperty(serialized, "m_ActorType").enumValueIndex =
                (int)NetworkCharacterActorType.PlayerOwned;
            RequireProperty(serialized, "m_RagdollMode").enumValueIndex =
                (int)NetworkCharacter.RemoteSystemMode.Synchronized;
            RequireProperty(serialized, "m_UseCoreNetworking").boolValue = true;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void ValidateConfiguredPlayer(
            GameObject prefab,
            Skeleton skeleton,
            AnimationClip faceDown,
            AnimationClip faceUp)
        {
            Character character = prefab.GetComponent<Character>();
            NetworkCharacter networkCharacter = prefab.GetComponent<NetworkCharacter>();
            RagdollDefault ragdoll = character != null
                ? character.Ragdoll.Get<RagdollDefault>()
                : null;
            if (character == null ||
                networkCharacter == null ||
                networkCharacter.ActorType != NetworkCharacterActorType.PlayerOwned ||
                networkCharacter.RagdollMode != NetworkCharacter.RemoteSystemMode.Synchronized ||
                !networkCharacter.UseCoreNetworking ||
                ragdoll == null)
            {
                throw new InvalidOperationException(
                    "The Core demo player did not retain its explicit PlayerOwned, Core, and " +
                    "synchronized RagdollDefault configuration.");
            }

            var serialized = new SerializedObject(character);
            SerializedProperty ragdollProperty = RequireRelative(
                RequireProperty(serialized, "m_Ragdoll"),
                "m_Ragdoll");
            UnityEngine.Object configuredSkeleton = RequireRelative(
                RequireRelative(ragdollProperty, "m_BoneRack"),
                "m_Skeleton").objectReferenceValue;
            UnityEngine.Object configuredFaceDown = RequireRelative(
                ragdollProperty,
                "m_RecoverFaceDown").objectReferenceValue;
            UnityEngine.Object configuredFaceUp = RequireRelative(
                ragdollProperty,
                "m_RecoverFaceUp").objectReferenceValue;
            if (configuredSkeleton != skeleton ||
                configuredFaceDown != faceDown ||
                configuredFaceUp != faceUp)
            {
                throw new InvalidOperationException(
                    "The Core demo player does not reference the canonical GC2 Skeleton and " +
                    "face-down/face-up recovery clips.");
            }
        }

        private static void RemoveVariableAndDoorObjects(Scene scene)
        {
            var matches = new List<GameObject>();
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                Transform[] transforms = root.GetComponentsInChildren<Transform>(true);
                for (int i = 0; i < transforms.Length; i++)
                {
                    Transform candidate = transforms[i];
                    if (candidate == null ||
                        !DemoOnlyObjectNames.Contains(candidate.name, StringComparer.Ordinal))
                    {
                        continue;
                    }

                    // Once a named ancestor is selected, its descendants are removed with it.
                    if (matches.Any(item => candidate.IsChildOf(item.transform))) continue;
                    matches.Add(candidate.gameObject);
                }
            }

            for (int i = 0; i < matches.Count; i++)
                if (matches[i] != null) UnityEngine.Object.DestroyImmediate(matches[i]);
        }

        private static GameObject RequireSingleDemoRoot(Scene scene)
        {
            GameObject[] existing = scene.GetRootGameObjects()
                .Where(candidate => candidate != null && candidate.name == DemoRootName)
                .ToArray();
            if (existing.Length > 1)
            {
                throw new InvalidOperationException(
                    $"The generated scene contains {existing.Length} '{DemoRootName}' roots.");
            }

            GameObject root = existing.Length == 1
                ? existing[0]
                : new GameObject(DemoRootName);
            if (root.scene != scene) SceneManager.MoveGameObjectToScene(root, scene);

            // This generated root is owned by the builder. Keep only its two stable action
            // children so reruns cannot accumulate stale controls.
            for (int i = root.transform.childCount - 1; i >= 0; i--)
            {
                Transform child = root.transform.GetChild(i);
                if (child.name == StartActionsName || child.name == RecoverActionsName) continue;
                UnityEngine.Object.DestroyImmediate(child.gameObject);
            }

            Actions[] rootActions = root.GetComponents<Actions>();
            for (int i = 0; i < rootActions.Length; i++)
                UnityEngine.Object.DestroyImmediate(rootActions[i]);
            return root;
        }

        private static Actions ConfigureActions<TInstruction>(
            GameObject demoRoot,
            string childName)
            where TInstruction : Instruction, new()
        {
            Transform[] matches = Enumerable.Range(0, demoRoot.transform.childCount)
                .Select(demoRoot.transform.GetChild)
                .Where(child => child != null && child.name == childName)
                .ToArray();
            if (matches.Length > 1)
            {
                for (int i = 1; i < matches.Length; i++)
                    UnityEngine.Object.DestroyImmediate(matches[i].gameObject);
            }

            GameObject actionObject;
            if (matches.Length == 0)
            {
                actionObject = new GameObject(childName);
                actionObject.transform.SetParent(demoRoot.transform, false);
            }
            else
            {
                actionObject = matches[0].gameObject;
            }

            Actions[] actions = actionObject.GetComponents<Actions>();
            Actions result = actions.Length > 0 ? actions[0] : actionObject.AddComponent<Actions>();
            for (int i = 1; i < actions.Length; i++)
                UnityEngine.Object.DestroyImmediate(actions[i]);

            var serialized = new SerializedObject(result);
            SerializedProperty wrapper = RequireProperty(serialized, "m_Instructions");
            SerializedProperty instructions = RequireRelative(wrapper, "m_Instructions");
            if (!instructions.isArray)
                throw new InvalidOperationException("GC2 Actions instructions are no longer an array.");
            instructions.arraySize = 1;
            instructions.GetArrayElementAtIndex(0).managedReferenceValue = new TInstruction();
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(result);
            return result;
        }

        private static void ConfigureDemoUi(
            GameObject demoRoot,
            Actions startActions,
            Actions recoverActions,
            string title)
        {
            NetworkRagdollDemoUI[] components = demoRoot.GetComponents<NetworkRagdollDemoUI>();
            NetworkRagdollDemoUI ui = components.Length > 0
                ? components[0]
                : demoRoot.AddComponent<NetworkRagdollDemoUI>();
            for (int i = 1; i < components.Length; i++)
                UnityEngine.Object.DestroyImmediate(components[i]);

            var serialized = new SerializedObject(ui);
            RequireProperty(serialized, "m_StartActions").objectReferenceValue = startActions;
            RequireProperty(serialized, "m_RecoverActions").objectReferenceValue = recoverActions;
            SerializedProperty titleProperty = serialized.FindProperty("m_Title");
            if (titleProperty != null &&
                titleProperty.propertyType == SerializedPropertyType.String &&
                !string.IsNullOrWhiteSpace(title))
            {
                titleProperty.stringValue = title;
            }
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(ui);
        }

        private static void TryUpdateVisibleLegend(Scene scene, string title)
        {
            GameObject[] controlsRoots = scene.GetRootGameObjects()
                .SelectMany(root => root.GetComponentsInChildren<Transform>(true))
                .Where(candidate =>
                    candidate != null &&
                    candidate.name.EndsWith(" Demo Controls UI", StringComparison.Ordinal))
                .Select(candidate => candidate.gameObject)
                .Distinct()
                .ToArray();
            if (controlsRoots.Length != 1) return;

            Transform titleObject = controlsRoots[0].transform.Find("Controls Panel/Title");
            Transform keysObject = controlsRoots[0].transform.Find("Controls Panel/Keys");
            TrySetLegacyText(titleObject, string.IsNullOrWhiteSpace(title)
                ? "Network Ragdoll Demo"
                : title);
            TrySetLegacyText(
                keysObject,
                "Use the Network Ragdoll Demo panel to start and recover the local player");
        }

        private static void TrySetLegacyText(Transform transform, string value)
        {
            if (transform == null) return;
            MonoBehaviour text = transform.GetComponents<MonoBehaviour>()
                .FirstOrDefault(component =>
                    component != null &&
                    string.Equals(
                        component.GetType().FullName,
                        "UnityEngine.UI.Text",
                        StringComparison.Ordinal));
            if (text == null) return;

            var serialized = new SerializedObject(text);
            SerializedProperty property = serialized.FindProperty("m_Text");
            if (property == null || property.propertyType != SerializedPropertyType.String) return;
            property.stringValue = value;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(text);
        }

        private static T RequireAsset<T>(string path) where T : UnityEngine.Object
        {
            return AssetDatabase.LoadAssetAtPath<T>(path) ??
                throw new FileNotFoundException(
                    $"Required GC2 {typeof(T).Name} asset is missing.",
                    path);
        }

        private static SerializedProperty RequireProperty(
            SerializedObject serialized,
            string propertyName)
        {
            return serialized.FindProperty(propertyName) ??
                throw new MissingFieldException(
                    serialized.targetObject.GetType().FullName,
                    propertyName);
        }

        private static SerializedProperty RequireRelative(
            SerializedProperty parent,
            string propertyName)
        {
            return parent.FindPropertyRelative(propertyName) ??
                throw new MissingFieldException(parent.propertyPath, propertyName);
        }

        private static void ValidateInstallRootPair(
            string previousRoot,
            string installRoot,
            out string installId)
        {
            ValidateSingleInstallRoot(previousRoot, out string previousId);
            ValidateSingleInstallRoot(installRoot, out installId);
            if (!string.Equals(previousId, installId, StringComparison.Ordinal) ||
                string.Equals(previousRoot, installRoot, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Core Examples migration roots must be different versions of the same " +
                    "installer ID.");
            }
        }

        private static void ValidateSingleInstallRoot(string root, out string installId)
        {
            if (string.IsNullOrWhiteSpace(root) ||
                !root.StartsWith(InstallsPrefix, StringComparison.Ordinal) ||
                root.IndexOf("..", StringComparison.Ordinal) >= 0)
            {
                throw new InvalidOperationException(
                    $"Refusing to operate on unsafe installer root '{root}'.");
            }

            string leaf = root.Substring(InstallsPrefix.Length);
            int separator = leaf.LastIndexOf('@');
            if (separator <= 0 ||
                leaf.IndexOf("/", StringComparison.Ordinal) >= 0 ||
                leaf.IndexOf("CoreExamples", StringComparison.Ordinal) < 0)
            {
                throw new InvalidOperationException(
                    $"'{root}' is not an exact Core Examples version root.");
            }
            installId = leaf.Substring(0, separator);
        }

        private static void RemoveAllInstallRoots(string installId)
        {
            foreach (string root in EnumerateInstallRoots(installId))
            {
                if (!AssetDatabase.DeleteAsset(root))
                    throw new IOException($"Could not remove stale installer fixture '{root}'.");
            }
        }

        private static void ExtractPackageInstallRoot(
            string fullPackagePath,
            string previousRoot,
            string installRoot)
        {
            var pathnames = new Dictionary<string, string>(StringComparer.Ordinal);
            var assets = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            var metas = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            var header = new byte[512];

            using (FileStream file = File.OpenRead(fullPackagePath))
            using (var gzip = new GZipStream(file, CompressionMode.Decompress))
            {
                while (true)
                {
                    int headerBytes = ReadFully(gzip, header, header.Length);
                    if (headerBytes == 0 || header.All(value => value == 0)) break;
                    if (headerBytes != header.Length)
                        throw new InvalidDataException(
                            "The Core Examples Unity package has an incomplete TAR header.");

                    string entryName = Encoding.UTF8.GetString(header, 0, 100)
                        .TrimEnd('\0')
                        .TrimStart('.', '/');
                    string sizeText = Encoding.ASCII.GetString(header, 124, 12)
                        .Trim('\0', ' ');
                    long size = string.IsNullOrEmpty(sizeText)
                        ? 0L
                        : Convert.ToInt64(sizeText, 8);
                    string[] segments = entryName.Split('/');
                    bool capture = segments.Length == 2 &&
                                   (segments[1] == "pathname" ||
                                    segments[1] == "asset" ||
                                    segments[1] == "asset.meta");

                    if (capture)
                    {
                        if (size > int.MaxValue)
                            throw new InvalidDataException(
                                $"Unity package entry '{entryName}' is too large.");

                        var content = new byte[(int)size];
                        if (ReadFully(gzip, content, content.Length) != content.Length)
                            throw new EndOfStreamException(
                                $"Unity package entry '{entryName}' is incomplete.");

                        switch (segments[1])
                        {
                            case "pathname":
                                pathnames[segments[0]] = Encoding.UTF8.GetString(content)
                                    .TrimEnd('\0', '\r', '\n');
                                break;
                            case "asset":
                                assets[segments[0]] = content;
                                break;
                            case "asset.meta":
                                metas[segments[0]] = content;
                                break;
                        }
                    }
                    else
                    {
                        Skip(gzip, size);
                    }

                    Skip(gzip, (512L - size % 512L) % 512L);
                }
            }

            if (pathnames.Count == 0)
                throw new InvalidDataException(
                    "The Core Examples Unity package contains no pathname entries.");

            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
            if (string.IsNullOrEmpty(projectRoot))
                throw new InvalidOperationException("Could not resolve the Unity project root.");
            string normalizedProjectRoot = Path.GetFullPath(projectRoot) +
                                           Path.DirectorySeparatorChar;

            foreach (KeyValuePair<string, string> entry in pathnames)
            {
                string assetPath = entry.Value.Replace('\\', '/');
                bool allowed = IsPathWithinRoot(assetPath, previousRoot) ||
                               IsPathWithinRoot(assetPath, installRoot);
                if (!allowed || assetPath.IndexOf("..", StringComparison.Ordinal) >= 0)
                {
                    throw new InvalidDataException(
                        $"Unity package pathname '{assetPath}' is outside the exact Core " +
                        "Examples installer root.");
                }

                string destination = Path.GetFullPath(Path.Combine(
                    projectRoot,
                    assetPath.Replace('/', Path.DirectorySeparatorChar)));
                if (!destination.StartsWith(normalizedProjectRoot, StringComparison.Ordinal))
                    throw new InvalidDataException(
                        $"Unity package pathname '{assetPath}' escapes the Unity project.");

                if (assets.TryGetValue(entry.Key, out byte[] assetBytes))
                {
                    string parent = Path.GetDirectoryName(destination);
                    if (string.IsNullOrEmpty(parent))
                        throw new InvalidDataException(
                            $"Unity package pathname '{assetPath}' has no parent folder.");
                    Directory.CreateDirectory(parent);
                    File.WriteAllBytes(destination, assetBytes);
                }
                else
                {
                    Directory.CreateDirectory(destination);
                }

                if (metas.TryGetValue(entry.Key, out byte[] metaBytes))
                {
                    File.WriteAllBytes(destination + ".meta", metaBytes);
                }
            }
        }

        private static bool IsPathWithinRoot(string assetPath, string root)
        {
            return string.Equals(assetPath, root, StringComparison.Ordinal) ||
                   assetPath.StartsWith(root + "/", StringComparison.Ordinal);
        }

        private static int ReadFully(Stream stream, byte[] buffer, int count)
        {
            int total = 0;
            while (total < count)
            {
                int read = stream.Read(buffer, total, count - total);
                if (read <= 0) break;
                total += read;
            }
            return total;
        }

        private static void Skip(Stream stream, long count)
        {
            var buffer = new byte[8192];
            while (count > 0)
            {
                int read = stream.Read(
                    buffer,
                    0,
                    (int)Math.Min(buffer.Length, count));
                if (read <= 0)
                    throw new EndOfStreamException("Unity package TAR entry is incomplete.");
                count -= read;
            }
        }

        private static void RemoveStaleInstallRoots(string installId, string installRoot)
        {
            foreach (string root in EnumerateInstallRoots(installId))
            {
                if (string.Equals(root, installRoot, StringComparison.Ordinal)) continue;
                if (!AssetDatabase.DeleteAsset(root))
                    throw new IOException($"Could not remove stale installer root '{root}'.");
            }
        }

        private static IEnumerable<string> EnumerateInstallRoots(string installId)
        {
            string fullInstallsPath = ProjectPath(InstallsPrefix.TrimEnd('/'));
            if (!Directory.Exists(fullInstallsPath)) yield break;

            string[] directories = Directory.GetDirectories(
                fullInstallsPath,
                installId + "@*",
                SearchOption.TopDirectoryOnly);
            Array.Sort(directories, StringComparer.Ordinal);
            for (int i = 0; i < directories.Length; i++)
            {
                string leaf = Path.GetFileName(directories[i]);
                if (string.IsNullOrEmpty(leaf)) continue;
                yield return InstallsPrefix + leaf;
            }
        }

        private static string ProjectPath(string assetPath)
        {
            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
            if (string.IsNullOrEmpty(projectRoot))
                throw new InvalidOperationException("Could not resolve the Unity project root.");
            return Path.GetFullPath(Path.Combine(
                projectRoot,
                assetPath.Replace('/', Path.DirectorySeparatorChar)));
        }
    }
}
