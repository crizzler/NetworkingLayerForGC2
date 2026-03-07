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
        
        // Network role
        private bool m_IsServer;
        private bool m_IsLocalClient;
        private bool m_IsRemoteClient;
        
        // Request tracking
        private ushort m_NextRequestId = 1;
        private static readonly List<uint> s_SharedKeyBuffer = new(16);
        private readonly Dictionary<uint, PendingContentAdd> m_PendingAdds = new(16);
        private readonly Dictionary<uint, PendingContentRemove> m_PendingRemoves = new(16);
        private readonly Dictionary<uint, PendingContentMove> m_PendingMoves = new(16);
        private readonly Dictionary<uint, PendingEquipment> m_PendingEquipment = new(8);
        private readonly Dictionary<uint, PendingWealth> m_PendingWealth = new(8);
        
        // State tracking for delta sync
        private readonly Dictionary<long, Vector2Int> m_LastSyncedPositions = new(32);
        private readonly Dictionary<int, int> m_LastSyncedWealth = new(8);
        private readonly Dictionary<int, long> m_LastSyncedEquipment = new(8);
        private float m_LastFullSync;
        private float m_LastDeltaSync;
        
        // RuntimeItem ID mapping (for server-assigned IDs)
        private readonly Dictionary<long, RuntimeItem> m_RuntimeItemMap = new(64);
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // STRUCTS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        private struct PendingContentAdd
        {
            public NetworkContentAddRequest Request;
            public float SentTime;
        }
        
        private struct PendingContentRemove
        {
            public NetworkContentRemoveRequest Request;
            public RuntimeItem RemovedItem; // For rollback
            public float SentTime;
        }
        
        private struct PendingContentMove
        {
            public NetworkContentMoveRequest Request;
            public float SentTime;
        }
        
        private struct PendingEquipment
        {
            public NetworkEquipmentRequest Request;
            public float SentTime;
        }
        
        private struct PendingWealth
        {
            public NetworkWealthRequest Request;
            public int OriginalValue;
            public float SentTime;
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // PROPERTIES
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>The underlying GC2 Bag component.</summary>
        public Bag Bag => m_Bag;
        
        /// <summary>Network ID of this bag's owner.</summary>
        public uint NetworkId => m_NetworkCharacter != null ? m_NetworkCharacter.NetworkId : 0;
        
        /// <summary>Whether this is running on the server.</summary>
        public bool IsServer => m_IsServer;
        
        /// <summary>Whether this is the local player's inventory.</summary>
        public bool IsLocalClient => m_IsLocalClient;
        
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
            
            InitializeStateTracking();
            
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
                        m_RuntimeItemMap[runtimeItem.RuntimeID.Hash] = runtimeItem;
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

        private static uint GetPendingKey(uint correlationId, ushort requestId)
        {
            return correlationId != 0 ? correlationId : requestId;
        }
    }
}
#endif
