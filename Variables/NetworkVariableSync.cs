using System;
using System.Collections.Generic;
using UnityEngine;
using GameCreator.Runtime.Variables;

namespace Arawn.GameCreator2.Networking
{
    /// <summary>
    /// Synchronizes GC2 Local Name Variables and Local List Variables over the network.
    /// Network-agnostic: raises events that your networking solution relays to peers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Attach this to any GameObject that has <see cref="LocalNameVariables"/> or
    /// <see cref="LocalListVariables"/> components that need network sync.
    /// </para>
    /// <para>
    /// <b>Owner</b> changes are detected via GC2's Register() callbacks and broadcast
    /// to peers. Non-owners apply received changes to their local copy.
    /// </para>
    /// <para>
    /// Values are serialized as type-prefixed strings for transport:
    /// <c>"float:3.14"</c>, <c>"int:42"</c>, <c>"bool:True"</c>, <c>"string:hello"</c>,
    /// <c>"vector3:1.0,2.0,3.0"</c>, <c>"color:1.0,0.5,0.0,1.0"</c>.
    /// </para>
    /// </remarks>
    [AddComponentMenu("Game Creator/Network/Variable Sync")]
    [DisallowMultipleComponent]
    public class NetworkVariableSync : MonoBehaviour
    {
        // ════════════════════════════════════════════════════════════════════
        // NESTED TYPES
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Payload for a Name Variable change. Serialize and send over your network.
        /// </summary>
        public struct NameVariableChange
        {
            public string VariableName;
            public string SerializedValue;
        }

        /// <summary>
        /// Payload for a List Variable change. Serialize and send over your network.
        /// </summary>
        public struct ListVariableChange
        {
            public enum ChangeType : byte
            {
                Set = 0,
                Push = 1,
                Remove = 2,
                Clear = 3,
                Insert = 4,
                Move = 5
            }

            public ChangeType Type;
            public int Index;
            public int IndexTo; // for Move
            public string SerializedValue;
        }

        // ════════════════════════════════════════════════════════════════════
        // INSPECTOR
        // ════════════════════════════════════════════════════════════════════

        [Header("Variable Components")]
        [Tooltip("GC2 Local Name Variables component to sync (optional)")]
        [SerializeField] private LocalNameVariables m_NameVariables;

        [Tooltip("GC2 Local List Variables component to sync (optional)")]
        [SerializeField] private LocalListVariables m_ListVariables;

        [Header("Settings")]
        [SerializeField] private bool m_IsOwner;

        [Tooltip("If true, sends a full state snapshot on ownership change")]
        [SerializeField] private bool m_SyncOnOwnershipChange = true;

        [Header("Debug")]
        [SerializeField] private bool m_DebugMode;

        // ════════════════════════════════════════════════════════════════════
        // FIELDS
        // ════════════════════════════════════════════════════════════════════

        private bool m_IsApplyingNetworkChange;
        private Action<string> m_NameChangeCallback;
        private Action<ListVariableRuntime.Change, int> m_ListChangeCallback;

        // ════════════════════════════════════════════════════════════════════
        // EVENTS
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Raised when a Name Variable changes on the owner.
        /// Hook this to your networking solution's RPC / message system.
        /// </summary>
        public event Action<NameVariableChange> OnNameVariableBroadcast;

        /// <summary>
        /// Raised when a List Variable changes on the owner.
        /// Hook this to your networking solution's RPC / message system.
        /// </summary>
        public event Action<ListVariableChange> OnListVariableBroadcast;

        /// <summary>
        /// Raised when a full snapshot is requested (e.g., for late joiners).
        /// Returns all name variable key-value pairs.
        /// </summary>
        public event Action<NameVariableChange[]> OnNameSnapshotRequested;

        /// <summary>
        /// Raised when a full snapshot is requested (e.g., for late joiners).
        /// Returns all list variable values in order.
        /// </summary>
        public event Action<string[]> OnListSnapshotRequested;

        // ════════════════════════════════════════════════════════════════════
        // PROPERTIES
        // ════════════════════════════════════════════════════════════════════

        public bool IsOwner
        {
            get => m_IsOwner;
            set
            {
                bool wasOwner = m_IsOwner;
                m_IsOwner = value;

                if (value && !wasOwner && m_SyncOnOwnershipChange)
                    BroadcastFullSnapshot();
            }
        }

        public LocalNameVariables NameVariables => m_NameVariables;
        public LocalListVariables ListVariables => m_ListVariables;

        // ════════════════════════════════════════════════════════════════════
        // LIFECYCLE
        // ════════════════════════════════════════════════════════════════════

        private void Awake()
        {
            // Auto-find components on same GameObject if not assigned
            if (m_NameVariables == null)
                m_NameVariables = GetComponent<LocalNameVariables>();
            if (m_ListVariables == null)
                m_ListVariables = GetComponent<LocalListVariables>();

            m_NameChangeCallback = OnNameChanged;
            m_ListChangeCallback = OnListChanged;
        }

        private void OnEnable()
        {
            if (m_NameVariables != null)
                m_NameVariables.Register(m_NameChangeCallback);

            if (m_ListVariables != null)
                m_ListVariables.Register(m_ListChangeCallback);
        }

        private void OnDisable()
        {
            if (m_NameVariables != null)
                m_NameVariables.Unregister(m_NameChangeCallback);

            if (m_ListVariables != null)
                m_ListVariables.Unregister(m_ListChangeCallback);
        }

        // ════════════════════════════════════════════════════════════════════
        // CHANGE DETECTION (owner side)
        // ════════════════════════════════════════════════════════════════════

        private void OnNameChanged(string variableName)
        {
            if (m_IsApplyingNetworkChange) return;
            if (!m_IsOwner) return;

            object value = m_NameVariables.Get(variableName);
            string serialized = SerializeValue(value);

            if (m_DebugMode)
                Debug.Log($"[VariableSync] Name '{variableName}' = {serialized}");

            OnNameVariableBroadcast?.Invoke(new NameVariableChange
            {
                VariableName = variableName,
                SerializedValue = serialized
            });
        }

        private void OnListChanged(ListVariableRuntime.Change change, int index)
        {
            if (m_IsApplyingNetworkChange) return;
            if (!m_IsOwner) return;

            ListVariableChange.ChangeType changeType;
            string serialized = null;

            switch (change)
            {
                case ListVariableRuntime.Change.Set:
                    changeType = ListVariableChange.ChangeType.Set;
                    serialized = SerializeValue(m_ListVariables.Get(index));
                    break;
                case ListVariableRuntime.Change.Insert:
                    changeType = ListVariableChange.ChangeType.Insert;
                    serialized = SerializeValue(m_ListVariables.Get(index));
                    break;
                case ListVariableRuntime.Change.Remove:
                    changeType = ListVariableChange.ChangeType.Remove;
                    break;
                default:
                    // Unknown change type, skip
                    return;
            }

            if (m_DebugMode)
                Debug.Log($"[VariableSync] List [{index}] {changeType} = {serialized ?? "N/A"}");

            OnListVariableBroadcast?.Invoke(new ListVariableChange
            {
                Type = changeType,
                Index = index,
                SerializedValue = serialized
            });
        }

        // ════════════════════════════════════════════════════════════════════
        // NETWORK RECEIVE (non-owner clients)
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Apply a Name Variable change received from the network.
        /// Call this from your networking solution's RPC handler.
        /// </summary>
        public void ApplyNameChange(NameVariableChange change)
        {
            if (m_NameVariables == null) return;

            object value = DeserializeValue(change.SerializedValue);
            if (value == null) return;

            m_IsApplyingNetworkChange = true;
            try
            {
                m_NameVariables.Set(change.VariableName, value);

                if (m_DebugMode)
                    Debug.Log($"[VariableSync] Applied name '{change.VariableName}' = {change.SerializedValue}");
            }
            finally
            {
                m_IsApplyingNetworkChange = false;
            }
        }

        /// <summary>
        /// Apply a List Variable change received from the network.
        /// Call this from your networking solution's RPC handler.
        /// </summary>
        public void ApplyListChange(ListVariableChange change)
        {
            if (m_ListVariables == null) return;

            m_IsApplyingNetworkChange = true;
            try
            {
                object value = null;
                if (change.SerializedValue != null)
                    value = DeserializeValue(change.SerializedValue);

                switch (change.Type)
                {
                    case ListVariableChange.ChangeType.Set:
                        if (value != null) m_ListVariables.Set(change.Index, value);
                        break;
                    case ListVariableChange.ChangeType.Push:
                        if (value != null) m_ListVariables.Push(value);
                        break;
                    case ListVariableChange.ChangeType.Remove:
                        m_ListVariables.Remove(change.Index);
                        break;
                    case ListVariableChange.ChangeType.Clear:
                        m_ListVariables.Clear();
                        break;
                    case ListVariableChange.ChangeType.Insert:
                        if (value != null) m_ListVariables.Insert(change.Index, value);
                        break;
                    case ListVariableChange.ChangeType.Move:
                        m_ListVariables.Move(change.Index, change.IndexTo);
                        break;
                }

                if (m_DebugMode)
                    Debug.Log($"[VariableSync] Applied list [{change.Index}] {change.Type}");
            }
            finally
            {
                m_IsApplyingNetworkChange = false;
            }
        }

        // ════════════════════════════════════════════════════════════════════
        // SNAPSHOT (for late joiners)
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Broadcast a full snapshot of all variables.
        /// Useful for late joiners or ownership transfer.
        /// </summary>
        public void BroadcastFullSnapshot()
        {
            BroadcastNameSnapshot();
            BroadcastListSnapshot();
        }

        /// <summary>
        /// Get a full snapshot of Name Variables for sending to a late joiner.
        /// </summary>
        public NameVariableChange[] GetNameSnapshot()
        {
            if (m_NameVariables == null) return Array.Empty<NameVariableChange>();

            // Use reflection to get all variable names from the NameVariableRuntime
            var runtime = GetNameRuntime();
            if (runtime == null) return Array.Empty<NameVariableChange>();

            var names = GetNameVariableNames(runtime);
            if (names == null || names.Length == 0) return Array.Empty<NameVariableChange>();

            var snapshot = new NameVariableChange[names.Length];
            for (int i = 0; i < names.Length; i++)
            {
                object val = m_NameVariables.Get(names[i]);
                snapshot[i] = new NameVariableChange
                {
                    VariableName = names[i],
                    SerializedValue = SerializeValue(val)
                };
            }

            return snapshot;
        }

        /// <summary>
        /// Get a full snapshot of List Variables for sending to a late joiner.
        /// </summary>
        public string[] GetListSnapshot()
        {
            if (m_ListVariables == null) return Array.Empty<string>();

            int count = m_ListVariables.Count;
            var snapshot = new string[count];
            for (int i = 0; i < count; i++)
            {
                snapshot[i] = SerializeValue(m_ListVariables.Get(i));
            }

            return snapshot;
        }

        /// <summary>
        /// Apply a full Name Variables snapshot (e.g., received from server on late join).
        /// </summary>
        public void ApplyNameSnapshot(NameVariableChange[] snapshot)
        {
            if (snapshot == null) return;

            m_IsApplyingNetworkChange = true;
            try
            {
                foreach (var change in snapshot)
                {
                    object value = DeserializeValue(change.SerializedValue);
                    if (value != null)
                        m_NameVariables.Set(change.VariableName, value);
                }

                if (m_DebugMode)
                    Debug.Log($"[VariableSync] Applied name snapshot ({snapshot.Length} vars)");
            }
            finally
            {
                m_IsApplyingNetworkChange = false;
            }
        }

        /// <summary>
        /// Apply a full List Variables snapshot (e.g., received from server on late join).
        /// </summary>
        public void ApplyListSnapshot(string[] snapshot)
        {
            if (snapshot == null) return;

            m_IsApplyingNetworkChange = true;
            try
            {
                // Clear and rebuild
                m_ListVariables.Clear();
                foreach (string serialized in snapshot)
                {
                    object value = DeserializeValue(serialized);
                    if (value != null)
                        m_ListVariables.Push(value);
                }

                if (m_DebugMode)
                    Debug.Log($"[VariableSync] Applied list snapshot ({snapshot.Length} items)");
            }
            finally
            {
                m_IsApplyingNetworkChange = false;
            }
        }

        // ════════════════════════════════════════════════════════════════════
        // BROADCAST HELPERS
        // ════════════════════════════════════════════════════════════════════

        private void BroadcastNameSnapshot()
        {
            var snapshot = GetNameSnapshot();
            if (snapshot.Length > 0)
                OnNameSnapshotRequested?.Invoke(snapshot);
        }

        private void BroadcastListSnapshot()
        {
            var listSnapshot = GetListSnapshot();
            if (listSnapshot.Length > 0)
                OnListSnapshotRequested?.Invoke(listSnapshot);
        }

        // ════════════════════════════════════════════════════════════════════
        // SERIALIZATION
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Serialize a value to a type-prefixed string for network transport.
        /// Format: "type:value" (e.g., "float:3.14", "int:42", "bool:True")
        /// </summary>
        public static string SerializeValue(object value)
        {
            if (value == null) return "null:";

            switch (value)
            {
                case float f:
                    return $"float:{f.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
                case int i:
                    return $"int:{i}";
                case bool b:
                    return $"bool:{b}";
                case string s:
                    return $"string:{s}";
                case double d:
                    return $"double:{d.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
                case Vector2 v2:
                    return $"vector2:{v2.x.ToString(System.Globalization.CultureInfo.InvariantCulture)},{v2.y.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
                case Vector3 v3:
                    return $"vector3:{v3.x.ToString(System.Globalization.CultureInfo.InvariantCulture)},{v3.y.ToString(System.Globalization.CultureInfo.InvariantCulture)},{v3.z.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
                case Quaternion q:
                    return $"quaternion:{q.x.ToString(System.Globalization.CultureInfo.InvariantCulture)},{q.y.ToString(System.Globalization.CultureInfo.InvariantCulture)},{q.z.ToString(System.Globalization.CultureInfo.InvariantCulture)},{q.w.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
                case Color c:
                    return $"color:{c.r.ToString(System.Globalization.CultureInfo.InvariantCulture)},{c.g.ToString(System.Globalization.CultureInfo.InvariantCulture)},{c.b.ToString(System.Globalization.CultureInfo.InvariantCulture)},{c.a.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
                default:
                    Debug.LogWarning($"[VariableSync] Unsupported type: {value.GetType().Name}");
                    return $"string:{value}";
            }
        }

        /// <summary>
        /// Deserialize a type-prefixed string back to a value.
        /// </summary>
        public static object DeserializeValue(string serialized)
        {
            if (string.IsNullOrEmpty(serialized) || serialized == "null:") return null;

            int colonIndex = serialized.IndexOf(':');
            if (colonIndex < 0) return serialized;

            string type = serialized.Substring(0, colonIndex);
            string data = serialized.Substring(colonIndex + 1);

            var culture = System.Globalization.CultureInfo.InvariantCulture;

            switch (type)
            {
                case "float":
                    return float.TryParse(data, System.Globalization.NumberStyles.Float, culture, out float f) ? f : 0f;
                case "int":
                    return int.TryParse(data, out int i) ? i : 0;
                case "bool":
                    return bool.TryParse(data, out bool b) ? b : false;
                case "string":
                    return data;
                case "double":
                    return double.TryParse(data, System.Globalization.NumberStyles.Float, culture, out double d) ? d : 0.0;
                case "vector2":
                    return ParseVector2(data, culture);
                case "vector3":
                    return ParseVector3(data, culture);
                case "quaternion":
                    return ParseQuaternion(data, culture);
                case "color":
                    return ParseColor(data, culture);
                default:
                    Debug.LogWarning($"[VariableSync] Unknown type prefix: {type}");
                    return data;
            }
        }

        // ════════════════════════════════════════════════════════════════════
        // PARSE HELPERS
        // ════════════════════════════════════════════════════════════════════

        private static Vector2 ParseVector2(string data, System.Globalization.CultureInfo culture)
        {
            string[] parts = data.Split(',');
            if (parts.Length != 2) return Vector2.zero;
            float.TryParse(parts[0], System.Globalization.NumberStyles.Float, culture, out float x);
            float.TryParse(parts[1], System.Globalization.NumberStyles.Float, culture, out float y);
            return new Vector2(x, y);
        }

        private static Vector3 ParseVector3(string data, System.Globalization.CultureInfo culture)
        {
            string[] parts = data.Split(',');
            if (parts.Length != 3) return Vector3.zero;
            float.TryParse(parts[0], System.Globalization.NumberStyles.Float, culture, out float x);
            float.TryParse(parts[1], System.Globalization.NumberStyles.Float, culture, out float y);
            float.TryParse(parts[2], System.Globalization.NumberStyles.Float, culture, out float z);
            return new Vector3(x, y, z);
        }

        private static Quaternion ParseQuaternion(string data, System.Globalization.CultureInfo culture)
        {
            string[] parts = data.Split(',');
            if (parts.Length != 4) return Quaternion.identity;
            float.TryParse(parts[0], System.Globalization.NumberStyles.Float, culture, out float x);
            float.TryParse(parts[1], System.Globalization.NumberStyles.Float, culture, out float y);
            float.TryParse(parts[2], System.Globalization.NumberStyles.Float, culture, out float z);
            float.TryParse(parts[3], System.Globalization.NumberStyles.Float, culture, out float w);
            return new Quaternion(x, y, z, w);
        }

        private static Color ParseColor(string data, System.Globalization.CultureInfo culture)
        {
            string[] parts = data.Split(',');
            if (parts.Length != 4) return Color.white;
            float.TryParse(parts[0], System.Globalization.NumberStyles.Float, culture, out float r);
            float.TryParse(parts[1], System.Globalization.NumberStyles.Float, culture, out float g);
            float.TryParse(parts[2], System.Globalization.NumberStyles.Float, culture, out float b);
            float.TryParse(parts[3], System.Globalization.NumberStyles.Float, culture, out float a);
            return new Color(r, g, b, a);
        }

        // ════════════════════════════════════════════════════════════════════
        // REFLECTION HELPERS
        // ════════════════════════════════════════════════════════════════════

        private object GetNameRuntime()
        {
            if (m_NameVariables == null) return null;

            var field = typeof(LocalNameVariables).GetField(
                "m_Runtime",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance
            );
            return field?.GetValue(m_NameVariables);
        }

        private string[] GetNameVariableNames(object runtime)
        {
            if (runtime == null) return null;

            // NameVariableRuntime stores data in a Dictionary<string, NameVariable>
            var dataField = runtime.GetType().GetField(
                "m_Map",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance
            );

            if (dataField == null)
            {
                // Try alternative field name
                dataField = runtime.GetType().GetField(
                    "m_Data",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance
                );
            }

            if (dataField == null) return null;

            var dict = dataField.GetValue(runtime);
            if (dict == null) return null;

            // Get keys from the dictionary
            var keysProperty = dict.GetType().GetProperty("Keys");
            if (keysProperty == null) return null;

            var keys = keysProperty.GetValue(dict) as System.Collections.ICollection;
            if (keys == null) return null;

            var nameList = new List<string>();
            foreach (var key in keys)
            {
                if (key is string name)
                    nameList.Add(name);
            }

            return nameList.ToArray();
        }
    }
}
