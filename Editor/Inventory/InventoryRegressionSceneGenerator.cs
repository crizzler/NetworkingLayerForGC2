#if GC2_INVENTORY
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Arawn.GameCreator2.Networking.Editor.Inventory
{
    /// <summary>
    /// Owned, idempotent generator for the Inventory drop/re-pickup regression fixture.
    /// It builds the fixture scene from the shipped PurrNet Inventory demo scene so every real
    /// session, transport bridge, player, bag and pickup reference is preserved, then writes a
    /// machine-readable inspection of the actual GC2 pickup/drop interaction path so the fixture can
    /// be audited instead of trusted.
    /// Run: -executeMethod Arawn.GameCreator2.Networking.Editor.Inventory
    ///          .InventoryRegressionSceneGenerator.Generate
    /// </summary>
    public static class InventoryRegressionSceneGenerator
    {
        private const string SourceScene =
            "Assets/Plugins/GameCreator/Installs/" +
            "GC2NetworkingLayerPurrNetTransport.InventoryExamples@1.0.0/" +
            "Requires Inventory Demos - PurrNetInventoryDemo.unity";

        private const string OutputFolder =
            "Assets/Arawn/NetworkingLayerForGC2/Tests/Inventory/Regression";

        private const string OutputScene = OutputFolder + "/Scenes/InventoryDropPickupRegression.unity";
        private const string OutputInspection = OutputFolder + "/scene-fixture-inspection.json";

        private const string AddItemInstructionType =
            "GameCreator.Runtime.Inventory.InstructionInventoryAddItem";

        [MenuItem("Game Creator/Networking Layer/Tests/Generate Inventory Drop Pickup Regression Scene")]
        public static void Generate()
        {
            string projectRoot = Directory.GetParent(Application.dataPath)!.FullName;
            var report = new StringBuilder();
            var json = new StringBuilder();

            if (!File.Exists(Path.Combine(projectRoot, SourceScene)))
            {
                throw new FileNotFoundException(
                    $"The PurrNet Inventory demo scene is missing: {SourceScene}. " +
                    "Restore the Inventory examples dependency before generating the fixture.",
                    SourceScene);
            }

            Directory.CreateDirectory(Path.Combine(projectRoot, OutputFolder, "Scenes"));

            string guidBefore = AssetDatabase.AssetPathToGUID(OutputScene);
            if (!AssetDatabase.CopyAsset(SourceScene, OutputScene))
            {
                throw new InvalidOperationException(
                    $"Failed to copy '{SourceScene}' to '{OutputScene}'.");
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            string guidAfter = AssetDatabase.AssetPathToGUID(OutputScene);
            Scene scene = EditorSceneManager.OpenScene(OutputScene, OpenSceneMode.Single);
            if (!scene.IsValid() || !scene.isLoaded)
            {
                throw new InvalidOperationException($"Generated scene did not load: {OutputScene}");
            }

            List<PickupRecord> pickups = InspectScene(scene, out int addItemInstructionCount);
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);

            WriteInspection(json, scene, pickups, addItemInstructionCount, guidAfter);

            report.AppendLine("[Inventory Regression Fixture] Generated.");
            report.AppendLine($"  source        : {SourceScene}");
            report.AppendLine($"  scene         : {OutputScene}");
            report.AppendLine($"  scene guid    : {guidAfter}");
            report.AppendLine($"  guid stable   : {(guidBefore.Length == 0 || guidBefore == guidAfter)}");
            report.AppendLine($"  root objects  : {scene.rootCount}");
            report.AppendLine($"  add item instructions : {addItemInstructionCount}");
            report.AppendLine($"  converted pickup sources : {pickups.Count}");
            report.AppendLine($"  inspection    : {OutputInspection}");
            Debug.Log(report.ToString());
            Debug.Log($"[Inventory Regression Fixture] Complete. scene={OutputScene} guid={guidAfter}");
        }

        private static List<PickupRecord> InspectScene(Scene scene, out int addItemInstructionCount)
        {
            var records = new List<PickupRecord>();
            addItemInstructionCount = 0;

            foreach (GameObject root in scene.GetRootGameObjects())
            {
                foreach (MonoBehaviour behaviour in root.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (behaviour == null) continue;

                    if (!TryFindAddItemInstruction(behaviour, out UnityEngine.Object instruction))
                    {
                        continue;
                    }

                    addItemInstructionCount++;
                    var record = new PickupRecord
                    {
                        InstructionOwner = GetHierarchyPath(behaviour.transform),
                        InstructionOwnerName = behaviour.gameObject.name,
                        HasPickupSourceInParents = FindPickupSourceInParents(behaviour.gameObject) != null
                    };

                    UnityEngine.Object resolved = ResolveInstructionItem(instruction);
                    record.RequestedItem = resolved != null ? AssetDatabase.GetAssetPath(resolved) : string.Empty;
                    record.DropPrefab = resolved != null ? DescribeDropPrefab(resolved) : string.Empty;
                    record.DropPrefabHasPickupSource = DescribeDropPrefabHasSource(resolved);
                    records.Add(record);
                }
            }

            return records;
        }

        private static bool TryFindAddItemInstruction(MonoBehaviour behaviour, out UnityEngine.Object instruction)
        {
            instruction = null;
            SerializedObject serialized;
            try
            {
                serialized = new SerializedObject(behaviour);
            }
            catch
            {
                return false;
            }

            SerializedProperty iterator = serialized.GetIterator();
            bool enterChildren = true;
            while (iterator.Next(enterChildren))
            {
                enterChildren = true;
                if (iterator.propertyType != SerializedPropertyType.ManagedReference) continue;

                object value;
                try
                {
                    value = iterator.managedReferenceValue;
                }
                catch
                {
                    continue;
                }

                if (value == null || value.GetType().FullName != AddItemInstructionType) continue;
                instruction = value as UnityEngine.Object ?? behaviour;
                return true;
            }

            return false;
        }

        private static UnityEngine.Object ResolveInstructionItem(object instruction)
        {
            try
            {
                object wrapper = FindFieldValue(instruction?.GetType(), instruction, "m_Item");
                if (wrapper == null ||
                    wrapper.GetType().FullName != "GameCreator.Runtime.Inventory.PropertyGetItem")
                {
                    return null;
                }

                object property = FindFieldValue(wrapper.GetType(), wrapper, "m_Property");
                if (property == null ||
                    property.GetType().FullName != "GameCreator.Runtime.Inventory.GetItemInstance")
                {
                    return null;
                }

                return FindFieldValue(property.GetType(), property, "m_Item") as UnityEngine.Object;
            }
            catch
            {
                return null;
            }
        }

        private static object FindFieldValue(Type type, object target, string fieldName)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public |
                                       BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            for (Type cursor = type; cursor != null; cursor = cursor.BaseType)
            {
                FieldInfo field = cursor.GetField(fieldName, flags);
                if (field != null) return field.GetValue(target);
            }

            return null;
        }

        /// <summary>
        /// Reports the GameObject reference a resolved Item asset uses as its world drop prefab.
        /// The exact serialized field name is discovered rather than assumed so the inspection keeps
        /// working across GC2 Inventory revisions.
        /// </summary>
        private static string DescribeDropPrefab(UnityEngine.Object item)
        {
            var serialized = new SerializedObject(item);
            SerializedProperty iterator = serialized.GetIterator();
            bool enterChildren = true;
            while (iterator.Next(enterChildren))
            {
                enterChildren = true;
                if (iterator.propertyType != SerializedPropertyType.ObjectReference) continue;
                if (iterator.objectReferenceValue is not GameObject prefab) continue;

                string path = AssetDatabase.GetAssetPath(prefab);
                if (path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)) return path;
            }

            return string.Empty;
        }

        private static bool DescribeDropPrefabHasSource(UnityEngine.Object item)
        {
            string prefabPath = DescribeDropPrefab(item);
            if (string.IsNullOrEmpty(prefabPath)) return false;

            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            return prefab != null && FindPickupSourceInParents(prefab) != null;
        }

        private static Component FindPickupSourceInParents(GameObject value)
        {
            Type pickupType = Type.GetType(
                "Arawn.GameCreator2.Networking.Inventory.NetworkInventoryPickupSource, " +
                "Arawn.GameCreator2.Networking.Inventory");
            if (pickupType == null) return null;

            for (Transform cursor = value.transform; cursor != null; cursor = cursor.parent)
            {
                Component source = cursor.GetComponent(pickupType);
                if (source != null) return source;
            }

            return null;
        }

        private static string GetHierarchyPath(Transform value)
        {
            string path = value.name;
            while (value.parent != null)
            {
                value = value.parent;
                path = value.name + "/" + path;
            }

            return path;
        }

        private static void WriteInspection(
            StringBuilder json,
            Scene scene,
            List<PickupRecord> pickups,
            int addItemInstructionCount,
            string guid)
        {
            json.Append("{\n");
            json.Append("  \"generator\": \"InventoryRegressionSceneGenerator\",\n");
            json.Append($"  \"generatedScene\": \"{Escape(OutputScene)}\",\n");
            json.Append($"  \"generatedSceneGuid\": \"{guid}\",\n");
            json.Append($"  \"sourceScene\": \"{Escape(SourceScene)}\",\n");
            json.Append($"  \"sourceSceneGuid\": \"{AssetDatabase.AssetPathToGUID(SourceScene)}\",\n");
            json.Append($"  \"unityVersion\": \"{Escape(Application.unityVersion)}\",\n");
            json.Append($"  \"rootObjectCount\": {scene.rootCount},\n");
            json.Append($"  \"addItemInstructionCount\": {addItemInstructionCount},\n");
            json.Append($"  \"convertedPickupSourceCount\": {pickups.Count},\n");
            json.Append("  \"addItemSources\": [\n");
            for (int i = 0; i < pickups.Count; i++)
            {
                PickupRecord record = pickups[i];
                json.Append("    {");
                json.Append($"\"instructionOwner\": \"{Escape(record.InstructionOwner)}\", ");
                json.Append($"\"hasPickupSourceInParents\": {record.HasPickupSourceInParents.ToString().ToLowerInvariant()}, ");
                json.Append($"\"requestedItem\": \"{Escape(record.RequestedItem)}\", ");
                json.Append($"\"dropPrefab\": \"{Escape(record.DropPrefab)}\", ");
                json.Append($"\"dropPrefabHasPickupSource\": {record.DropPrefabHasPickupSource.ToString().ToLowerInvariant()}");
                json.Append(i == pickups.Count - 1 ? "}\n" : "},\n");
            }

            json.Append("  ]\n");
            json.Append("}\n");

            string absolute = Path.Combine(
                Directory.GetParent(Application.dataPath)!.FullName, OutputInspection);
            Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
            File.WriteAllText(absolute, json.ToString(), new UTF8Encoding(false));
            AssetDatabase.ImportAsset(OutputInspection, ImportAssetOptions.ForceUpdate);
        }

        private static string Escape(string value)
        {
            return (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private sealed class PickupRecord
        {
            public string InstructionOwner;
            public string InstructionOwnerName;
            public bool HasPickupSourceInParents;
            public string RequestedItem;
            public string DropPrefab;
            public bool DropPrefabHasPickupSource;
        }
    }
}
#endif
