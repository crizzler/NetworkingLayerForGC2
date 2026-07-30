#if GC2_INVENTORY
using System;
using System.Threading.Tasks;
using GameCreator.Runtime.Inventory;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Inventory
{
    /// <summary>
    /// Authoritative identity for a scene or runtime pickup. The Item is resolved on the server;
    /// clients only submit the stable pickup id and their destination bag.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Game Creator/Network/Inventory/Network Inventory Pickup Source")]
    public sealed class NetworkInventoryPickupSource : MonoBehaviour
    {
        [SerializeField] private uint m_PickupId;
        [SerializeField] private Item m_Item;
        [SerializeField, Min(0.1f)] private float m_MaxPickupDistance = 4f;
        [SerializeField] private bool m_RequireLineOfSight;
        [SerializeField] private LayerMask m_LineOfSightMask = ~0;
        [SerializeField] private MonoBehaviour m_RuntimeIdentity;
        [SerializeField] private bool m_HideWhenConsumed = true;

        private bool m_Consumed;
        private uint m_ConsumedBy;
        private uint m_StateVersion;
        private bool m_Reserved;
        private Collider[] m_Colliders;
        private Renderer[] m_Renderers;

        public uint PickupId => m_PickupId != 0 ? m_PickupId : ComputeStableId();
        public Item Item => m_Item;
        public bool IsConsumed => m_Consumed;
        public uint StateVersion => m_StateVersion;
        public INetworkInventoryRuntimePickupIdentity RuntimeIdentity =>
            m_RuntimeIdentity as INetworkInventoryRuntimePickupIdentity;

        private void Awake()
        {
            ResolveRuntimeIdentity();
            m_Colliders = GetComponentsInChildren<Collider>(true);
            m_Renderers = GetComponentsInChildren<Renderer>(true);
        }

        private void OnEnable()
        {
            if (RuntimeIdentity == null)
                NetworkInventoryManager.Instance?.RegisterPickupSource(this);
        }

        private void Start()
        {
            if (RuntimeIdentity == null)
                NetworkInventoryManager.Instance?.RegisterPickupSource(this);
        }

        private void OnDestroy()
        {
            if (RuntimeIdentity == null)
                NetworkInventoryManager.Instance?.UnregisterPickupSource(this);
        }

        public Task<NetworkInventoryInterceptResult> RequestPickupAsync(
            NetworkInventoryController picker)
        {
            return RequestPickupInternalAsync(picker);
        }

        private async Task<NetworkInventoryInterceptResult> RequestPickupInternalAsync(
            NetworkInventoryController picker)
        {
            NetworkInventoryManager manager = NetworkInventoryManager.Instance;
            if (manager == null || picker == null)
                return NetworkInventoryInterceptResult.HandledFailure;

            NetworkPickupResponse response = await manager.RequestPickupSourceAsync(this, picker);
            return response.Authorized
                ? NetworkInventoryInterceptResult.HandledSuccess
                : NetworkInventoryInterceptResult.HandledFailure;
        }

        internal bool TryReserve(
            NetworkInventoryController picker,
            uint actorNetworkId,
            out InventoryRejectionReason reason)
        {
            reason = InventoryRejectionReason.None;
            if (m_Consumed || m_Reserved)
            {
                reason = InventoryRejectionReason.InvalidOperation;
                return false;
            }
            if (m_Item == null)
            {
                reason = InventoryRejectionReason.ItemNotFound;
                return false;
            }
            if (picker == null || picker.Bag == null)
            {
                reason = InventoryRejectionReason.BagNotFound;
                return false;
            }

            float maxDistance = Mathf.Max(0.1f, m_MaxPickupDistance);
            Vector3 origin = picker.transform.position;
            Vector3 target = transform.position;
            if ((origin - target).sqrMagnitude > maxDistance * maxDistance)
            {
                reason = InventoryRejectionReason.NotAuthorized;
                return false;
            }

            if (m_RequireLineOfSight)
            {
                Vector3 delta = target - origin;
                if (Physics.Raycast(origin, delta.normalized, out RaycastHit hit,
                        delta.magnitude, m_LineOfSightMask, QueryTriggerInteraction.Ignore) &&
                    !hit.transform.IsChildOf(transform))
                {
                    reason = InventoryRejectionReason.NotAuthorized;
                    return false;
                }
            }

            if (!picker.Bag.Content.CanAddType(m_Item, true))
            {
                reason = InventoryRejectionReason.InsufficientSpace;
                return false;
            }

            m_Reserved = true;
            return true;
        }

        internal void ReleaseReservation()
        {
            m_Reserved = false;
        }

        internal NetworkPickupState Commit(uint actorNetworkId)
        {
            m_Reserved = false;
            m_Consumed = true;
            m_ConsumedBy = actorNetworkId;
            m_StateVersion = Math.Max(1u, m_StateVersion + 1u);
            ApplyPresentationState();
            return GetState();
        }

        internal bool CommitRuntime(uint actorNetworkId, INetworkInventoryRuntimePickupIdentity identity)
        {
            if (identity == null || !identity.IsSpawned || !identity.TryServerConsume())
            {
                m_Reserved = false;
                return false;
            }
            m_Reserved = false;
            m_Consumed = true;
            m_ConsumedBy = actorNetworkId;
            ApplyPresentationState();
            return true;
        }

        internal NetworkPickupState GetState()
        {
            return new NetworkPickupState
            {
                PickupId = PickupId,
                Consumed = m_Consumed,
                ConsumedByActorNetworkId = m_ConsumedBy,
                StateVersion = m_StateVersion
            };
        }

        internal void ApplyState(NetworkPickupState state)
        {
            if (state.PickupId != PickupId || state.StateVersion < m_StateVersion) return;
            m_StateVersion = state.StateVersion;
            m_Consumed = state.Consumed;
            m_ConsumedBy = state.ConsumedByActorNetworkId;
            m_Reserved = false;
            ApplyPresentationState();
        }

        private void ApplyPresentationState()
        {
            if (!m_HideWhenConsumed) return;
            bool enabledState = !m_Consumed;
            if (m_Colliders != null)
            {
                for (int i = 0; i < m_Colliders.Length; i++)
                    if (m_Colliders[i] != null) m_Colliders[i].enabled = enabledState;
            }
            if (m_Renderers != null)
            {
                for (int i = 0; i < m_Renderers.Length; i++)
                    if (m_Renderers[i] != null) m_Renderers[i].enabled = enabledState;
            }
        }

        private uint ComputeStableId()
        {
            string path = gameObject.scene.path + ":" + BuildPath(transform);
            uint hash = 2166136261u;
            for (int i = 0; i < path.Length; i++)
            {
                hash ^= path[i];
                hash *= 16777619u;
            }
            return hash == 0 ? 1u : hash;
        }

        private static string BuildPath(Transform value)
        {
            if (value == null) return string.Empty;
            string path = value.name + "[" + value.GetSiblingIndex() + "]";
            while (value.parent != null)
            {
                value = value.parent;
                path = value.name + "[" + value.GetSiblingIndex() + "]/" + path;
            }
            return path;
        }

        private void ResolveRuntimeIdentity()
        {
            if (m_RuntimeIdentity is INetworkInventoryRuntimePickupIdentity) return;
            MonoBehaviour[] behaviours = GetComponents<MonoBehaviour>();
            for (int i = 0; i < behaviours.Length; i++)
            {
                if (behaviours[i] is not INetworkInventoryRuntimePickupIdentity) continue;
                m_RuntimeIdentity = behaviours[i];
                return;
            }
            m_RuntimeIdentity = null;
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            ResolveRuntimeIdentity();
        }

        internal void EditorConfigure(uint pickupId, Item item)
        {
            m_PickupId = pickupId;
            m_Item = item;
            UnityEditor.EditorUtility.SetDirty(this);
        }
#endif
    }
}
#endif
