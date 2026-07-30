using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Editor
{
    /// <summary>
    /// Transport-neutral editor validation and migration for authoritative Inventory scene pickups.
    /// Reflection keeps the base editor assembly loadable when the optional Inventory module is absent.
    /// </summary>
    public static class InventorySceneSetupTools
    {
        private const string PICKUP_SOURCE_TYPE =
            "Arawn.GameCreator2.Networking.Inventory.NetworkInventoryPickupSource, Arawn.GameCreator2.Networking.Inventory";
        private const string INVENTORY_MANAGER_TYPE =
            "Arawn.GameCreator2.Networking.Inventory.NetworkInventoryManager, Arawn.GameCreator2.Networking.Inventory";
        private const string ADD_ITEM_INSTRUCTION_TYPE =
            "GameCreator.Runtime.Inventory.InstructionInventoryAddItem";
        private const string PROPERTY_GET_ITEM_TYPE =
            "GameCreator.Runtime.Inventory.PropertyGetItem";
        private const string GET_ITEM_INSTANCE_TYPE =
            "GameCreator.Runtime.Inventory.GetItemInstance";

        private static bool s_HasCachedValidation;
        private static double s_NextValidationTime;
        private static ValidationSummary s_CachedValidation;

        public readonly struct ValidationSummary
        {
            public readonly int PickupSourceCount;
            public readonly int DuplicateIdCount;
            public readonly int UnresolvedItemCount;
            public readonly int ImplicitIdCount;
            public readonly int LegacyStockPickupCount;
            public readonly int UnsafeManagerCount;

            public bool HasErrors => DuplicateIdCount > 0 || UnresolvedItemCount > 0;
            public bool HasWarnings => HasErrors || ImplicitIdCount > 0 ||
                                       LegacyStockPickupCount > 0 || UnsafeManagerCount > 0;

            public ValidationSummary(
                int pickupSourceCount,
                int duplicateIdCount,
                int unresolvedItemCount,
                int implicitIdCount,
                int legacyStockPickupCount,
                int unsafeManagerCount)
            {
                PickupSourceCount = pickupSourceCount;
                DuplicateIdCount = duplicateIdCount;
                UnresolvedItemCount = unresolvedItemCount;
                ImplicitIdCount = implicitIdCount;
                LegacyStockPickupCount = legacyStockPickupCount;
                UnsafeManagerCount = unsafeManagerCount;
            }

            public string ToReport()
            {
                return
                    $"Inventory scene validation: {PickupSourceCount} pickup source(s), " +
                    $"{DuplicateIdCount} duplicate ID(s), {UnresolvedItemCount} unresolved Item(s), " +
                    $"{ImplicitIdCount} implicit ID(s), {LegacyStockPickupCount} unconverted stock pickup(s), " +
                    $"{UnsafeManagerCount} manager(s) allowing unvalidated client adds.";
            }
        }

        private readonly struct AddItemSource
        {
            public readonly GameObject InstructionOwner;
            public readonly GameObject PickupHost;
            public readonly UnityEngine.Object Item;

            public AddItemSource(
                GameObject instructionOwner,
                GameObject pickupHost,
                UnityEngine.Object item)
            {
                InstructionOwner = instructionOwner;
                PickupHost = pickupHost;
                Item = item;
            }
        }

        [MenuItem("Game Creator/Networking Layer/Inventory/Validate Open Scenes", priority = 80)]
        public static void ValidateOpenScenesMenu()
        {
            ValidationSummary summary = ValidateOpenScenes(true);
            if (summary.HasErrors) Debug.LogError($"[InventorySceneSetup] {summary.ToReport()}");
            else if (summary.HasWarnings) Debug.LogWarning($"[InventorySceneSetup] {summary.ToReport()}");
            else Debug.Log($"[InventorySceneSetup] {summary.ToReport()}");

            EditorUtility.DisplayDialog(
                "Inventory Scene Validation",
                summary.ToReport(),
                "OK");
        }

        [MenuItem("Game Creator/Networking Layer/Inventory/Convert Stock Scene Pickups", priority = 81)]
        public static void ConvertStockScenePickupsMenu()
        {
            ConvertStockScenePickups(true);
        }

        public static ValidationSummary ValidateOpenScenes(bool force = false)
        {
            if (!force && s_HasCachedValidation &&
                EditorApplication.timeSinceStartup < s_NextValidationTime)
            {
                return s_CachedValidation;
            }

            Type pickupType = Type.GetType(PICKUP_SOURCE_TYPE);
            Type managerType = Type.GetType(INVENTORY_MANAGER_TYPE);
            int sourceCount = 0;
            int duplicateCount = 0;
            int unresolvedCount = 0;
            int implicitCount = 0;
            int unsafeManagerCount = 0;
            var ids = new Dictionary<uint, Component>();

            if (pickupType != null)
            {
                foreach (Component source in FindSceneComponents(pickupType))
                {
                    sourceCount++;
                    var serialized = new SerializedObject(source);
                    SerializedProperty idProperty = serialized.FindProperty("m_PickupId");
                    SerializedProperty itemProperty = serialized.FindProperty("m_Item");
                    uint explicitId = idProperty?.uintValue ?? 0u;
                    uint effectiveId = explicitId != 0u
                        ? explicitId
                        : ComputeStableId(source.gameObject);

                    if (explicitId == 0u) implicitCount++;
                    if (itemProperty == null || itemProperty.objectReferenceValue == null)
                    {
                        unresolvedCount++;
                    }

                    if (ids.TryGetValue(effectiveId, out Component existing) && existing != source)
                    {
                        duplicateCount++;
                    }
                    else
                    {
                        ids[effectiveId] = source;
                    }
                }
            }

            if (managerType != null)
            {
                foreach (Component manager in FindSceneComponents(managerType))
                {
                    var serialized = new SerializedObject(manager);
                    SerializedProperty unsafeAdds = serialized.FindProperty(
                        "m_AllowUnvalidatedOwnedClientAdds");
                    if (unsafeAdds?.boolValue == true) unsafeManagerCount++;
                }
            }

            int legacyCount = 0;
            foreach (AddItemSource source in FindStockAddItemSources())
            {
                if (pickupType == null || FindPickupSourceInParents(source.InstructionOwner, pickupType) == null)
                {
                    legacyCount++;
                }
            }

            s_CachedValidation = new ValidationSummary(
                sourceCount,
                duplicateCount,
                unresolvedCount,
                implicitCount,
                legacyCount,
                unsafeManagerCount);
            s_HasCachedValidation = true;
            s_NextValidationTime = EditorApplication.timeSinceStartup + 0.5d;
            return s_CachedValidation;
        }

        /// <summary>
        /// Converts only stock pickup-shaped Add Item triggers. Generic rewards, quests, debug
        /// grants, and other Add Item instructions are deliberately left untouched.
        /// </summary>
        public static int ConvertStockScenePickups(bool showSummary)
        {
            Type pickupType = Type.GetType(PICKUP_SOURCE_TYPE);
            if (pickupType == null)
            {
                if (showSummary)
                {
                    EditorUtility.DisplayDialog(
                        "Convert Inventory Pickups",
                        "NetworkInventoryPickupSource is unavailable. Install/compile the Networking Layer Inventory module first.",
                        "OK");
                }
                return 0;
            }

            List<AddItemSource> candidates = FindStockAddItemSources();
            var usedIds = new HashSet<uint>();
            foreach (Component existing in FindSceneComponents(pickupType))
            {
                var serialized = new SerializedObject(existing);
                uint id = serialized.FindProperty("m_PickupId")?.uintValue ?? 0u;
                usedIds.Add(id != 0u ? id : ComputeStableId(existing.gameObject));
            }

            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("Convert Network Inventory Pickups");
            int undoGroup = Undo.GetCurrentGroup();
            int converted = 0;
            int skipped = 0;
            var handledHosts = new HashSet<GameObject>();

            foreach (AddItemSource candidate in candidates)
            {
                GameObject host = candidate.PickupHost;
                if (host == null || !handledHosts.Add(host)) continue;
                if (candidate.Item == null)
                {
                    skipped++;
                    Debug.LogWarning(
                        $"[InventorySceneSetup] Skipped '{GetHierarchyPath(host.transform)}': " +
                        "its Add Item instruction does not resolve to a fixed Item asset.",
                        host);
                    continue;
                }

                Component source = FindPickupSourceInParents(candidate.InstructionOwner, pickupType);
                bool created = source == null;
                if (created) source = Undo.AddComponent(host, pickupType);
                if (source == null)
                {
                    skipped++;
                    continue;
                }

                var serialized = new SerializedObject(source);
                SerializedProperty idProperty = serialized.FindProperty("m_PickupId");
                SerializedProperty itemProperty = serialized.FindProperty("m_Item");
                if (idProperty == null || itemProperty == null)
                {
                    skipped++;
                    continue;
                }

                uint currentId = idProperty.uintValue;
                if (currentId == 0u)
                {
                    uint id = ComputeStableId(source.gameObject);
                    while (usedIds.Contains(id)) id = id == uint.MaxValue ? 1u : id + 1u;
                    idProperty.uintValue = id;
                    usedIds.Add(id);
                }

                itemProperty.objectReferenceValue = candidate.Item;

                serialized.ApplyModifiedProperties();
                EditorUtility.SetDirty(source);
                EditorSceneManager.MarkSceneDirty(source.gameObject.scene);
                converted++;

                Debug.Log(
                    $"[InventorySceneSetup] {(created ? "Converted" : "Updated")} pickup " +
                    $"'{GetHierarchyPath(host.transform)}' with a stable authoritative identity.",
                    source);
            }

            Undo.CollapseUndoOperations(undoGroup);
            s_HasCachedValidation = false;
            if (showSummary)
            {
                EditorUtility.DisplayDialog(
                    "Convert Inventory Pickups",
                    $"Converted or updated {converted} stock scene pickup(s)." +
                    (skipped > 0 ? $"\n\nSkipped {skipped}; see the Console for details." : string.Empty),
                    "OK");
            }
            return converted;
        }

        private static List<AddItemSource> FindStockAddItemSources()
        {
            var results = new List<AddItemSource>();
            var seenOwners = new HashSet<GameObject>();
            foreach (MonoBehaviour behaviour in FindSceneMonoBehaviours())
            {
                if (behaviour == null || !IsStockPickupObject(behaviour.gameObject)) continue;
                if (!TryFindFixedAddItem(behaviour, out UnityEngine.Object item)) continue;

                GameObject owner = behaviour.gameObject;
                if (!seenOwners.Add(owner)) continue;
                results.Add(new AddItemSource(owner, ResolvePickupHost(owner), item));
            }
            return results;
        }

        private static bool TryFindFixedAddItem(
            MonoBehaviour behaviour,
            out UnityEngine.Object item)
        {
            item = null;
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

                if (value == null || value.GetType().FullName != ADD_ITEM_INSTRUCTION_TYPE) continue;
                item = ResolveInstructionItem(value);
                return true;
            }
            return false;
        }

        private static UnityEngine.Object ResolveInstructionItem(object instruction)
        {
            try
            {
                FieldInfo wrapperField = FindInstanceField(instruction?.GetType(), "m_Item");
                object wrapper = wrapperField?.GetValue(instruction);
                if (wrapper == null ||
                    wrapper.GetType().FullName != PROPERTY_GET_ITEM_TYPE)
                {
                    return null;
                }

                // PropertyGetItem.EditorValue is not suitable here: GetItemInstance does not
                // override the base editor value and therefore reports null. Unwrap only the
                // fixed Item getter. Dynamic Item properties must remain unconverted because the
                // server cannot derive one authoritative asset from them at edit time.
                FieldInfo propertyField = FindInstanceField(wrapper.GetType(), "m_Property");
                object property = propertyField?.GetValue(wrapper);
                if (property == null ||
                    property.GetType().FullName != GET_ITEM_INSTANCE_TYPE)
                {
                    return null;
                }

                FieldInfo itemField = FindInstanceField(property.GetType(), "m_Item");
                return itemField?.GetValue(property) as UnityEngine.Object;
            }
            catch
            {
                return null;
            }
        }

        private static FieldInfo FindInstanceField(Type type, string fieldName)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public |
                                       BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            for (Type cursor = type; cursor != null; cursor = cursor.BaseType)
            {
                FieldInfo field = cursor.GetField(fieldName, flags);
                if (field != null) return field;
            }

            return null;
        }

        private static bool IsStockPickupObject(GameObject value)
        {
            for (Transform cursor = value.transform; cursor != null; cursor = cursor.parent)
            {
                if (cursor.name.IndexOf("pickup", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }

                UnityEngine.Object source = PrefabUtility.GetCorrespondingObjectFromSource(cursor.gameObject);
                string path = source != null ? AssetDatabase.GetAssetPath(source) : string.Empty;
                if (path.IndexOf("_Template_Pickup_Item", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
            return false;
        }

        private static GameObject ResolvePickupHost(GameObject instructionOwner)
        {
            GameObject fallback = instructionOwner;
            for (Transform cursor = instructionOwner.transform; cursor != null; cursor = cursor.parent)
            {
                if (cursor.name.IndexOf("pickup", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    fallback = cursor.gameObject;
                }

                UnityEngine.Object source = PrefabUtility.GetCorrespondingObjectFromSource(cursor.gameObject);
                string path = source != null ? AssetDatabase.GetAssetPath(source) : string.Empty;
                if (path.IndexOf("_Template_Pickup_Item", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    GameObject root = PrefabUtility.GetNearestPrefabInstanceRoot(cursor.gameObject);
                    return root != null ? root : cursor.gameObject;
                }
            }
            return fallback;
        }

        private static Component FindPickupSourceInParents(GameObject value, Type pickupType)
        {
            for (Transform cursor = value.transform; cursor != null; cursor = cursor.parent)
            {
                Component source = cursor.GetComponent(pickupType);
                if (source != null) return source;
            }
            return null;
        }

        private static MonoBehaviour[] FindSceneMonoBehaviours()
        {
#if UNITY_2023_1_OR_NEWER
            MonoBehaviour[] values = UnityEngine.Object.FindObjectsByType<MonoBehaviour>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);
#else
            MonoBehaviour[] values = UnityEngine.Object.FindObjectsOfType<MonoBehaviour>(true);
#endif
            var result = new List<MonoBehaviour>(values?.Length ?? 0);
            if (values == null) return result.ToArray();
            foreach (MonoBehaviour value in values)
            {
                if (value != null && value.gameObject.scene.IsValid() && value.gameObject.scene.isLoaded)
                {
                    result.Add(value);
                }
            }
            return result.ToArray();
        }

        private static Component[] FindSceneComponents(Type type)
        {
            if (type == null) return Array.Empty<Component>();
#if UNITY_2023_1_OR_NEWER
            UnityEngine.Object[] values = UnityEngine.Object.FindObjectsByType(
                type,
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);
#else
            UnityEngine.Object[] values = UnityEngine.Object.FindObjectsOfType(type, true);
#endif
            var result = new List<Component>(values?.Length ?? 0);
            if (values == null) return result.ToArray();
            foreach (UnityEngine.Object value in values)
            {
                if (value is Component component && component.gameObject.scene.IsValid() &&
                    component.gameObject.scene.isLoaded)
                {
                    result.Add(component);
                }
            }
            return result.ToArray();
        }

        private static uint ComputeStableId(GameObject value)
        {
            string identity = value.scene.path + ":" + GetStableTransformPath(value.transform);
            uint hash = 2166136261u;
            for (int i = 0; i < identity.Length; i++)
            {
                hash ^= identity[i];
                hash *= 16777619u;
            }
            return hash == 0u ? 1u : hash;
        }

        private static string GetStableTransformPath(Transform value)
        {
            string path = value.name + "[" + value.GetSiblingIndex() + "]";
            while (value.parent != null)
            {
                value = value.parent;
                path = value.name + "[" + value.GetSiblingIndex() + "]/" + path;
            }
            return path;
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
    }
}
