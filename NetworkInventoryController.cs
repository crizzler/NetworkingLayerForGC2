#if GC2_INVENTORY
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.Inventory;

namespace Arawn.GameCreator2.Networking.Inventory
{
    /// <summary>
    /// Server-authoritative inventory controller for GC2 Bag.
    /// Intercepts all inventory operations and routes through server validation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Purpose:</b>
    /// In competitive multiplayer, inventory operations MUST be server-authoritative
    /// to prevent item duplication, gold exploits, and illegal crafting.
    /// </para>
    /// <para>
    /// <b>Architecture:</b>
    /// - Clients send operation requests to server
    /// - Server validates and applies changes
    /// - Server broadcasts confirmed changes to all clients
    /// </para>
    /// </remarks>
    [RequireComponent(typeof(Bag))]
    [AddComponentMenu("Game Creator/Network/Inventory/Network Inventory Controller")]
    [DefaultExecutionOrder(ApplicationManager.EXECUTION_ORDER_DEFAULT + 5)]
    public partial class NetworkInventoryController : MonoBehaviour
    {
        // ════════════════════════════════════════════════════════════════════════════════════════
        // INSPECTOR
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        [Header("Network Settings")]
        [Tooltip("Apply changes optimistically before server confirmation (items only).")]
        [SerializeField] private bool m_OptimisticUpdates = false;
        
        [Tooltip("Rollback optimistic updates if server rejects.")]
        [SerializeField] private bool m_RollbackOnReject = true;

        [Tooltip("Optional stable network id for scene/world bags. Leave 0 to derive one from the scene hierarchy.")]
        [SerializeField] private uint m_StaticNetworkIdOverride = 0;
        
        [Header("Sync Settings")]
        [Tooltip("Send full state sync at this interval (seconds). 0 = never.")]
        [SerializeField] private float m_FullSyncInterval = 10f;
        
        [Tooltip("Send delta updates at this interval (seconds).")]
        [SerializeField] private float m_DeltaSyncInterval = 0.2f;
        
        [Header("Validation")]
        [Tooltip("Log rejected operations for debugging.")]
        [SerializeField] private bool m_LogRejections = false;
        
        [Header("Debug")]
        [SerializeField] private bool m_LogAllChanges = false;
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // EVENTS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        // Content events
        public event Action<NetworkContentAddRequest> OnContentAddRequested;
        public event Action<NetworkItemAddedBroadcast> OnItemAdded;
        public event Action<NetworkContentRemoveRequest> OnContentRemoveRequested;
        public event Action<NetworkItemRemovedBroadcast> OnItemRemoved;
        public event Action<NetworkContentMoveRequest> OnContentMoveRequested;
        public event Action<NetworkItemMovedBroadcast> OnItemMoved;
        public event Action<NetworkContentUseRequest> OnContentUseRequested;
        public event Action<NetworkItemUsedBroadcast> OnItemUsed;
        
        // Equipment events
        public event Action<NetworkEquipmentRequest> OnEquipmentRequested;
        public event Action<NetworkItemEquippedBroadcast> OnItemEquipped;
        public event Action<NetworkItemUnequippedBroadcast> OnItemUnequipped;
        
        // Socket events
        public event Action<NetworkSocketRequest> OnSocketRequested;
        public event Action<NetworkSocketChangeBroadcast> OnSocketChanged;
        
        // Wealth events
        public event Action<NetworkWealthRequest> OnWealthRequested;
        public event Action<NetworkWealthChangeBroadcast> OnWealthChanged;
        
        // Rejection event
        public event Action<InventoryRejectionReason, string> OnOperationRejected;
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // PRIVATE FIELDS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        private Bag m_Bag;
        private NetworkCharacter m_NetworkCharacter;
        private uint m_CachedStaticNetworkId;
        private bool m_IsApplyingNetworkState;
        
        // Network role
        private bool m_IsServer;
        private bool m_IsLocalClient;
        private bool m_IsRemoteClient;
        
        // Request tracking
        private ushort m_NextRequestId = 1;
        private ushort m_LastIssuedRequestId = 1;
        private static readonly List<ulong> s_SharedKeyBuffer = new(16);
        private static readonly List<long> s_SharedRuntimeIdBuffer = new(16);
        private readonly Dictionary<ulong, PendingContentAdd> m_PendingAdds = new(16);
        private readonly Dictionary<ulong, PendingContentRemove> m_PendingRemoves = new(16);
        private readonly Dictionary<ulong, PendingContentMove> m_PendingMoves = new(16);
        private readonly Dictionary<ulong, PendingEquipment> m_PendingEquipment = new(8);
        private readonly Dictionary<ulong, PendingWealth> m_PendingWealth = new(8);
        private readonly Dictionary<long, long> m_PendingPickupLocalRuntimeByServerRuntime = new(8);
        
        // State tracking for delta sync
        private readonly Dictionary<long, Vector2Int> m_LastSyncedPositions = new(32);
        private readonly Dictionary<int, int> m_LastSyncedWealth = new(8);
        private readonly Dictionary<int, long> m_LastSyncedEquipment = new(8);
        private float m_LastFullSync;
        private float m_LastDeltaSync;
        
        // RuntimeItem ID mapping (for server-assigned IDs)
        private readonly Dictionary<long, RuntimeItem> m_RuntimeItemMap = new(64);

        private static readonly List<NetworkInventoryController> s_Controllers = new(64);
        private static readonly List<PendingLocalRemoval> s_PendingLocalRemovals = new(32);
        private static readonly HashSet<long> s_LocalDropRuntimeIds = new();
        private static readonly Dictionary<long, DroppedItemInstance> s_DroppedItemInstances = new();
        private static readonly Dictionary<long, ServerDroppedWorldItem> s_ServerDroppedWorldItems = new();
        private static bool s_StaticHooksInstalled;
        private static NetworkInventoryController s_LocalPlayerController;
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // STRUCTS
        // ════════════════════════════════════════════════════════════════════════════════════════

        private struct PendingContentAdd : ITimedPendingRequest
        {
            public NetworkContentAddRequest Request;
            public float SentTime;
            public float PendingSentTime => SentTime;
        }
        
        private struct PendingContentRemove : ITimedPendingRequest
        {
            public NetworkContentRemoveRequest Request;
            public RuntimeItem RemovedItem; // For rollback
            public float SentTime;
            public float PendingSentTime => SentTime;
        }
        
        private struct PendingContentMove : ITimedPendingRequest
        {
            public NetworkContentMoveRequest Request;
            public float SentTime;
            public float PendingSentTime => SentTime;
        }
        
        private struct PendingEquipment : ITimedPendingRequest
        {
            public NetworkEquipmentRequest Request;
            public float SentTime;
            public float PendingSentTime => SentTime;
        }
        
        private struct PendingWealth : ITimedPendingRequest
        {
            public NetworkWealthRequest Request;
            public int OriginalValue;
            public float SentTime;
            public float PendingSentTime => SentTime;
        }

        private struct PendingLocalRemoval
        {
            public NetworkInventoryController SourceController;
            public NetworkRuntimeItem Item;
            public long RuntimeIdHash;
            public float Time;
        }

        private struct DroppedItemInstance
        {
            public GameObject Instance;
            public uint SourceBagNetworkId;
            public NetworkRuntimeItem Item;
            public Vector3 Position;
        }

        private struct ServerDroppedWorldItem
        {
            public uint SourceBagNetworkId;
            public NetworkRuntimeItem Item;
            public Vector3 Position;
            public float Time;
        }

        private static void LogPickupDebug(string message, UnityEngine.Object context = null)
        {
            if (context != null) Debug.Log($"[NetworkInventoryPickupDebug] {message}", context);
            else Debug.Log($"[NetworkInventoryPickupDebug] {message}");
        }

        private static void LogPickupWarning(string message, UnityEngine.Object context = null)
        {
            if (context != null) Debug.LogWarning($"[NetworkInventoryPickupDebug] {message}", context);
            else Debug.LogWarning($"[NetworkInventoryPickupDebug] {message}");
        }

        private static string DescribeRuntimeItem(RuntimeItem item)
        {
            if (item == null) return "null";
            return $"{item.ItemID.String} runtime={item.RuntimeID.String} hash={item.RuntimeID.Hash}";
        }

        private static string DescribeNetworkItem(NetworkRuntimeItem item)
        {
            return $"{item.ItemIdString} runtime={item.RuntimeIdString} hash={item.RuntimeIdHash} itemHash={item.ItemHash}";
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // PROPERTIES
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>The underlying GC2 Bag component.</summary>
        public Bag Bag => m_Bag;
        
        /// <summary>Network ID of this bag's owner.</summary>
        public uint NetworkId => m_NetworkCharacter != null
            ? m_NetworkCharacter.NetworkId
            : GetStaticNetworkId();

        /// <summary>Whether this inventory is backed by a spawned NetworkCharacter.</summary>
        public bool UsesNetworkCharacterId => m_NetworkCharacter != null;

        /// <summary>Whether this inventory is a scene/world bag such as a chest.</summary>
        public bool IsWorldInventory => m_NetworkCharacter == null;
        
        /// <summary>Whether this is running on the server.</summary>
        public bool IsServer => m_IsServer;
        
        /// <summary>Whether this is the local player's inventory.</summary>
        public bool IsLocalClient => m_IsLocalClient;

        public bool OptimisticUpdates => m_OptimisticUpdates;

        public bool RollbackOnReject => m_RollbackOnReject;
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // UNITY LIFECYCLE
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        private void Awake()
        {
            m_Bag = GetComponent<Bag>();
            m_NetworkCharacter = GetComponent<NetworkCharacter>();
        }
        
        private void Start()
        {
            // Subscribe to GC2 events for local change detection
            SubscribeToBagEvents();
        }
        
        private void OnDestroy()
        {
            UnsubscribeFromBagEvents();
        }
        
        private void Update()
        {
            if (!m_IsServer && !m_IsLocalClient) return;
            
            float currentTime = Time.time;
            
            // Server-side sync
            if (m_IsServer)
            {
                if (m_FullSyncInterval > 0 && currentTime - m_LastFullSync > m_FullSyncInterval)
                {
                    BroadcastFullState();
                    m_LastFullSync = currentTime;
                }
                
                if (m_DeltaSyncInterval > 0 && currentTime - m_LastDeltaSync > m_DeltaSyncInterval)
                {
                    BroadcastDeltaState();
                    m_LastDeltaSync = currentTime;
                }
            }
            
            CleanupPendingRequests();
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // INITIALIZATION
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Initialize the network inventory controller with role information.
        /// </summary>
        public void Initialize(bool isServer, bool isLocalClient)
        {
            m_IsServer = isServer;
            m_IsLocalClient = isLocalClient;
            m_IsRemoteClient = !isServer && !isLocalClient;

            if (m_IsLocalClient && UsesNetworkCharacterId && NetworkId != 0)
            {
                s_LocalPlayerController = this;
            }
            
            InitializeStateTracking();

            if (IsWorldInventory)
            {
                LogPickupDebug(
                    $"{name}: world inventory initialized networkId={NetworkId} path={BuildStableScenePath(transform)} server={m_IsServer} local={m_IsLocalClient} remote={m_IsRemoteClient} trackedItems={m_RuntimeItemMap.Count}",
                    this);
            }
            
            if (m_LogAllChanges)
            {
                string role = m_IsServer ? "Server" : (m_IsLocalClient ? "LocalClient" : "RemoteClient");
                Debug.Log($"[NetworkInventoryController] {gameObject.name} initialized as {role}");
            }
        }
        
        private void InitializeStateTracking()
        {
            // Build RuntimeItem map
            m_RuntimeItemMap.Clear();
            foreach (var cell in m_Bag.Content.CellList)
            {
                if (cell == null || cell.Available) continue;
                
                foreach (var runtimeIdEntry in cell.List)
                {
                    var runtimeItem = m_Bag.Content.GetRuntimeItem(runtimeIdEntry);
                    if (runtimeItem != null)
                    {
                        TrackRuntimeItemRecursive(runtimeItem);
                    }
                }
            }
            
            // Cache initial wealth
            foreach (var currencyId in m_Bag.Wealth.List)
            {
                m_LastSyncedWealth[currencyId.Hash] = m_Bag.Wealth.Get(currencyId);
            }

            CacheCurrentSyncState();
        }
        
        private void SubscribeToBagEvents()
        {
            if (!s_Controllers.Contains(this))
            {
                s_Controllers.Add(this);
            }

            InstallStaticInventoryHooks();

            if (m_Bag.Content != null)
            {
                m_Bag.Content.EventAdd += OnLocalItemAdded;
                m_Bag.Content.EventRemove += OnLocalItemRemoved;
                m_Bag.Content.EventUse += OnLocalItemUsed;
            }
            
            if (m_Bag.Equipment != null)
            {
                m_Bag.Equipment.EventEquip += OnLocalItemEquipped;
                m_Bag.Equipment.EventUnequip += OnLocalItemUnequipped;
            }
            
            if (m_Bag.Wealth != null)
            {
                m_Bag.Wealth.EventChange += OnLocalWealthChanged;
            }
        }
        
        private void UnsubscribeFromBagEvents()
        {
            s_Controllers.Remove(this);
            if (s_LocalPlayerController == this)
            {
                s_LocalPlayerController = null;
            }

            UninstallStaticInventoryHooksIfUnused();

            if (m_Bag != null)
            {
                if (m_Bag.Content != null)
                {
                    m_Bag.Content.EventAdd -= OnLocalItemAdded;
                    m_Bag.Content.EventRemove -= OnLocalItemRemoved;
                    m_Bag.Content.EventUse -= OnLocalItemUsed;
                }
                
                if (m_Bag.Equipment != null)
                {
                    m_Bag.Equipment.EventEquip -= OnLocalItemEquipped;
                    m_Bag.Equipment.EventUnequip -= OnLocalItemUnequipped;
                }
                
                if (m_Bag.Wealth != null)
                {
                    m_Bag.Wealth.EventChange -= OnLocalWealthChanged;
                }
            }
        }

        private ushort GetNextRequestId()
        {
            if (m_NextRequestId == 0)
            {
                m_NextRequestId = 1;
            }

            ushort requestId = m_NextRequestId;
            m_NextRequestId++;
            if (m_NextRequestId == 0)
            {
                m_NextRequestId = 1;
            }

            m_LastIssuedRequestId = requestId;
            return requestId;
        }

        private static ulong GetPendingKey(uint actorNetworkId, uint correlationId, ushort requestId)
        {
            uint pendingCorrelation = correlationId != 0 ? correlationId : requestId;
            return ((ulong)actorNetworkId << 32) | pendingCorrelation;
        }

        private uint GetStaticNetworkId()
        {
            if (m_StaticNetworkIdOverride != 0) return m_StaticNetworkIdOverride;
            if (m_CachedStaticNetworkId != 0) return m_CachedStaticNetworkId;

            string path = BuildStableScenePath(transform);
            uint hash = 2166136261u;
            for (int i = 0; i < path.Length; i++)
            {
                hash ^= path[i];
                hash *= 16777619u;
            }

            m_CachedStaticNetworkId = 0x80000000u | (hash & 0x7FFFFFFFu);
            if (m_CachedStaticNetworkId == 0) m_CachedStaticNetworkId = 0x80000001u;
            return m_CachedStaticNetworkId;
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
                    if (sibling != null && sibling.name == target.name)
                    {
                        sameNameIndex++;
                    }
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
                    if (root.name == target.name)
                    {
                        sameNameIndex++;
                    }
                }
            }

            return $"{target.name}[{sameNameIndex}]";
        }

        private static void InstallStaticInventoryHooks()
        {
            if (s_StaticHooksInstalled) return;

            RuntimeSockets.EventAttachRuntimeItem -= HandleGlobalSocketAttached;
            RuntimeSockets.EventAttachRuntimeItem += HandleGlobalSocketAttached;
            RuntimeSockets.EventDetachRuntimeItem -= HandleGlobalSocketDetached;
            RuntimeSockets.EventDetachRuntimeItem += HandleGlobalSocketDetached;
            Item.EventInstantiate -= HandleGlobalItemInstantiated;
            Item.EventInstantiate += HandleGlobalItemInstantiated;
            s_StaticHooksInstalled = true;
        }

        private static void UninstallStaticInventoryHooksIfUnused()
        {
            if (!s_StaticHooksInstalled || s_Controllers.Count > 0) return;

            RuntimeSockets.EventAttachRuntimeItem -= HandleGlobalSocketAttached;
            RuntimeSockets.EventDetachRuntimeItem -= HandleGlobalSocketDetached;
            Item.EventInstantiate -= HandleGlobalItemInstantiated;
            s_StaticHooksInstalled = false;
        }
    }
}
#endif
