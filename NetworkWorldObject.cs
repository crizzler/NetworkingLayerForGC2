#if GC2_INVENTORY
using System.Collections.Generic;
using GameCreator.Runtime.Inventory;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Inventory
{
    public enum NetworkWorldObjectKind
    {
        Generic = 0,
        PickupItem = 1,
        // For future expansion
        Door = 2,
        Lever = 3,
        Trap = 4,
        Portal = 5,
        Shrine = 6
    }

    /// <summary>
    /// Stable network identity for scene-authored world objects. Pickup behavior is the first
    /// implemented use; future object interactions can reuse the same identity and registry.
    /// </summary>
    [AddComponentMenu("Game Creator/Network/Inventory/Network World Object")]
    [DisallowMultipleComponent]
    public sealed class NetworkWorldObject : MonoBehaviour
    {
        // [LOCAL-EDIT] #PILFER-INVENTORY-WORLD-OBJECT
        [Header("Network Id")]
        [SerializeField] private bool m_UseAutomaticNetworkId = true;
        [SerializeField] private uint m_ManualNetworkId;
        [SerializeField] private string m_NetworkIdSalt = string.Empty;

        [Header("World Object")]
        [SerializeField] private NetworkWorldObjectKind m_Kind = NetworkWorldObjectKind.PickupItem;

        [Header("Pickup")]
        [SerializeField] private bool m_AllowPickup = true;
        [SerializeField] private Item m_Item;
        [SerializeField] private float m_PickupRadius = 2f;
        [SerializeField] private bool m_DisableOnPickup = true;
        [SerializeField] private bool m_DestroyOnPickup;

        [Header("Debug")]
        [SerializeField] private bool m_LogDiagnostics;

        private uint m_CachedNetworkId;
        private bool m_IsConsumed;

        public uint NetworkId => ResolveNetworkId();
        public NetworkWorldObjectKind Kind => m_Kind;
        public bool AllowPickup => m_AllowPickup;
        public Item Item => m_Item;
        public float PickupRadius => Mathf.Max(0f, m_PickupRadius);
        public bool IsConsumed => m_IsConsumed;

        private void OnEnable()
        {
            NetworkWorldObjectRegistry.Register(this);
        }

        private void OnDisable()
        {
            NetworkWorldObjectRegistry.Unregister(this);
        }

        public bool CanPickupFrom(Vector3 pickerPosition)
        {
            if (!m_AllowPickup || m_IsConsumed || m_Item == null) return false;
            if (m_Kind != NetworkWorldObjectKind.PickupItem) return false;

            float radius = PickupRadius;
            if (radius <= 0f) return true;

            // [LOCAL-EDIT] #PILFER-INVENTORY-WORLD-OBJECT-RANGE-DIAGNOSTICS
            return GetHorizontalDistanceTo(pickerPosition) <= radius;
        }

        public float GetDistanceTo(Vector3 pickerPosition)
        {
            // [LOCAL-EDIT] #PILFER-INVENTORY-WORLD-OBJECT-RANGE-DIAGNOSTICS
            return Vector3.Distance(transform.position, pickerPosition);
        }

        public float GetHorizontalDistanceTo(Vector3 pickerPosition)
        {
            // [LOCAL-EDIT] #PILFER-INVENTORY-WORLD-OBJECT-RANGE-DIAGNOSTICS
            Vector3 position = transform.position;
            float deltaX = position.x - pickerPosition.x;
            float deltaZ = position.z - pickerPosition.z;
            return Mathf.Sqrt(deltaX * deltaX + deltaZ * deltaZ);
        }

        public RuntimeItem CreatePickupRuntimeItem()
        {
            return m_Item != null ? new RuntimeItem(m_Item) : null;
        }

        public void MarkPickedUp()
        {
            if (m_IsConsumed) return;
            m_IsConsumed = true;
            NetworkWorldObjectRegistry.MarkConsumed(NetworkId);

            if (m_LogDiagnostics)
            {
                Debug.Log(
                    $"[NetworkWorldObject] picked up object={name} networkId={NetworkId} item={m_Item?.ID.String}",
                    this);
            }

            if (m_DestroyOnPickup)
            {
                Destroy(gameObject);
                return;
            }

            if (m_DisableOnPickup)
            {
                gameObject.SetActive(false);
            }
        }

        private uint ResolveNetworkId()
        {
            if (!m_UseAutomaticNetworkId && m_ManualNetworkId != 0) return m_ManualNetworkId;
            if (m_CachedNetworkId != 0) return m_CachedNetworkId;

            string path = BuildStableScenePath(transform);
            if (!string.IsNullOrEmpty(m_NetworkIdSalt)) path = $"{path}:{m_NetworkIdSalt}";

            uint hash = 2166136261u;
            for (int i = 0; i < path.Length; i++)
            {
                hash ^= path[i];
                hash *= 16777619u;
            }

            m_CachedNetworkId = hash != 0 ? hash : 1u;
            return m_CachedNetworkId;
        }

        private static string BuildStableScenePath(Transform target)
        {
            if (target == null) return string.Empty;

            string scenePath = target.gameObject.scene.path;
            if (string.IsNullOrEmpty(scenePath)) scenePath = target.gameObject.scene.name;

            string path = BuildStableScenePathSegment(target);
            Transform current = target;
            while (current.parent != null)
            {
                current = current.parent;
                path = $"{BuildStableScenePathSegment(current)}/{path}";
            }

            return $"{scenePath}:{path}";
        }

        private static string BuildStableScenePathSegment(Transform target)
        {
            int sameNameIndex = 0;
            Transform parent = target.parent;
            if (parent != null)
            {
                for (int i = 0; i < parent.childCount; i++)
                {
                    Transform sibling = parent.GetChild(i);
                    if (sibling == target) break;
                    if (sibling != null && sibling.name == target.name) sameNameIndex++;
                }
            }
            else if (target.gameObject.scene.IsValid())
            {
                GameObject[] roots = target.gameObject.scene.GetRootGameObjects();
                for (int i = 0; i < roots.Length; i++)
                {
                    GameObject root = roots[i];
                    if (root == null) continue;
                    if (root.transform == target) break;
                    if (root.name == target.name) sameNameIndex++;
                }
            }

            return $"{target.name}[{sameNameIndex}]";
        }
    }

    public static class NetworkWorldObjectRegistry
    {
        // [LOCAL-EDIT] #PILFER-INVENTORY-WORLD-OBJECT
        private static readonly Dictionary<uint, NetworkWorldObject> s_Objects = new(128);
        // [LOCAL-EDIT] #PILFER-INVENTORY-WORLD-OBJECT-CONSUMED-REGISTRY
        private static readonly HashSet<uint> s_ConsumedObjectIds = new(128);

        public static void Register(NetworkWorldObject worldObject)
        {
            if (worldObject == null || worldObject.NetworkId == 0) return;
            s_Objects[worldObject.NetworkId] = worldObject;
        }

        public static void Unregister(NetworkWorldObject worldObject)
        {
            if (worldObject == null || worldObject.NetworkId == 0) return;
            if (!s_Objects.TryGetValue(worldObject.NetworkId, out NetworkWorldObject existing)) return;
            if (existing == worldObject) s_Objects.Remove(worldObject.NetworkId);
        }

        public static void MarkConsumed(uint networkId)
        {
            // [LOCAL-EDIT] #PILFER-INVENTORY-WORLD-OBJECT-CONSUMED-REGISTRY
            if (networkId != 0) s_ConsumedObjectIds.Add(networkId);
        }

        public static bool IsConsumed(uint networkId)
        {
            // [LOCAL-EDIT] #PILFER-INVENTORY-WORLD-OBJECT-CONSUMED-REGISTRY
            return networkId != 0 && s_ConsumedObjectIds.Contains(networkId);
        }

        public static bool TryGet(uint networkId, out NetworkWorldObject worldObject)
        {
            worldObject = null;
            return networkId != 0 && s_Objects.TryGetValue(networkId, out worldObject) && worldObject != null;
        }
    }
}
#endif
