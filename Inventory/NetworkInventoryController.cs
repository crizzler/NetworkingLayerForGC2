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
    public class NetworkInventoryController : MonoBehaviour
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
        private readonly Dictionary<ushort, PendingContentAdd> m_PendingAdds = new(16);
        private readonly Dictionary<ushort, PendingContentRemove> m_PendingRemoves = new(16);
        private readonly Dictionary<ushort, PendingContentMove> m_PendingMoves = new(16);
        private readonly Dictionary<ushort, PendingEquipment> m_PendingEquipment = new(8);
        private readonly Dictionary<ushort, PendingWealth> m_PendingWealth = new(8);
        
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
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // CLIENT-SIDE: REQUEST OPERATIONS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        #region Content Requests
        
        /// <summary>
        /// Request to add an item type to the bag.
        /// </summary>
        public void RequestAddItem(Item item, Vector2Int position, bool allowStack,
            InventoryModificationSource source = InventoryModificationSource.Direct, int sourceHash = 0)
        {
            if (m_IsRemoteClient)
            {
                Debug.LogWarning("[NetworkInventoryController] Cannot modify inventory on remote client");
                return;
            }
            
            var request = new NetworkContentAddRequest
            {
                RequestId = m_NextRequestId++,
                TargetBagNetworkId = NetworkId,
                ItemHash = item.ID.Hash,
                Position = position,
                AllowStack = allowStack,
                Source = source,
                SourceHash = sourceHash
            };
            
            m_PendingAdds[request.RequestId] = new PendingContentAdd
            {
                Request = request,
                SentTime = Time.time
            };
            
            OnContentAddRequested?.Invoke(request);
            
            if (m_IsServer)
            {
                var response = ProcessContentAddRequest(request, NetworkId);
                ReceiveContentAddResponse(response);
            }
            else
            {
                NetworkInventoryManager.Instance?.SendContentAddRequest(request);
            }
        }
        
        /// <summary>
        /// Request to add an existing RuntimeItem to the bag.
        /// </summary>
        public void RequestAddRuntimeItem(NetworkRuntimeItem runtimeItem, Vector2Int position, bool allowStack,
            InventoryModificationSource source = InventoryModificationSource.Direct, int sourceHash = 0)
        {
            if (m_IsRemoteClient) return;
            
            var request = new NetworkContentAddRequest
            {
                RequestId = m_NextRequestId++,
                TargetBagNetworkId = NetworkId,
                ItemHash = 0, // Using RuntimeItem instead
                RuntimeItem = runtimeItem,
                Position = position,
                AllowStack = allowStack,
                Source = source,
                SourceHash = sourceHash
            };
            
            m_PendingAdds[request.RequestId] = new PendingContentAdd
            {
                Request = request,
                SentTime = Time.time
            };
            
            OnContentAddRequested?.Invoke(request);
            
            if (m_IsServer)
            {
                var response = ProcessContentAddRequest(request, NetworkId);
                ReceiveContentAddResponse(response);
            }
            else
            {
                NetworkInventoryManager.Instance?.SendContentAddRequest(request);
            }
        }
        
        /// <summary>
        /// Request to remove an item from the bag.
        /// </summary>
        public void RequestRemoveItem(RuntimeItem runtimeItem,
            InventoryModificationSource source = InventoryModificationSource.Direct)
        {
            if (m_IsRemoteClient) return;
            if (runtimeItem == null) return;
            
            var request = new NetworkContentRemoveRequest
            {
                RequestId = m_NextRequestId++,
                TargetBagNetworkId = NetworkId,
                RuntimeIdHash = runtimeItem.RuntimeID.Hash,
                UsePosition = false,
                Source = source
            };
            
            m_PendingRemoves[request.RequestId] = new PendingContentRemove
            {
                Request = request,
                RemovedItem = runtimeItem,
                SentTime = Time.time
            };
            
            OnContentRemoveRequested?.Invoke(request);
            
            if (m_IsServer)
            {
                var response = ProcessContentRemoveRequest(request, NetworkId);
                ReceiveContentRemoveResponse(response);
            }
            else
            {
                NetworkInventoryManager.Instance?.SendContentRemoveRequest(request);
            }
        }
        
        /// <summary>
        /// Request to remove item at position.
        /// </summary>
        public void RequestRemoveAtPosition(Vector2Int position,
            InventoryModificationSource source = InventoryModificationSource.Direct)
        {
            if (m_IsRemoteClient) return;
            
            var request = new NetworkContentRemoveRequest
            {
                RequestId = m_NextRequestId++,
                TargetBagNetworkId = NetworkId,
                Position = position,
                UsePosition = true,
                Source = source
            };
            
            m_PendingRemoves[request.RequestId] = new PendingContentRemove
            {
                Request = request,
                SentTime = Time.time
            };
            
            OnContentRemoveRequested?.Invoke(request);
            
            if (m_IsServer)
            {
                var response = ProcessContentRemoveRequest(request, NetworkId);
                ReceiveContentRemoveResponse(response);
            }
            else
            {
                NetworkInventoryManager.Instance?.SendContentRemoveRequest(request);
            }
        }
        
        /// <summary>
        /// Request to move item within bag.
        /// </summary>
        public void RequestMoveItem(Vector2Int fromPosition, Vector2Int toPosition, bool allowStack)
        {
            if (m_IsRemoteClient) return;
            
            var request = new NetworkContentMoveRequest
            {
                RequestId = m_NextRequestId++,
                TargetBagNetworkId = NetworkId,
                FromPosition = fromPosition,
                ToPosition = toPosition,
                AllowStack = allowStack
            };
            
            m_PendingMoves[request.RequestId] = new PendingContentMove
            {
                Request = request,
                SentTime = Time.time
            };
            
            OnContentMoveRequested?.Invoke(request);
            
            if (m_IsServer)
            {
                var response = ProcessContentMoveRequest(request, NetworkId);
                ReceiveContentMoveResponse(response);
            }
            else
            {
                NetworkInventoryManager.Instance?.SendContentMoveRequest(request);
            }
        }
        
        /// <summary>
        /// Request to use an item.
        /// </summary>
        public void RequestUseItem(RuntimeItem runtimeItem)
        {
            if (m_IsRemoteClient) return;
            if (runtimeItem == null) return;
            
            var request = new NetworkContentUseRequest
            {
                RequestId = m_NextRequestId++,
                TargetBagNetworkId = NetworkId,
                RuntimeIdHash = runtimeItem.RuntimeID.Hash,
                UsePosition = false
            };
            
            OnContentUseRequested?.Invoke(request);
            
            if (m_IsServer)
            {
                var response = ProcessContentUseRequest(request, NetworkId);
                ReceiveContentUseResponse(response);
            }
            else
            {
                NetworkInventoryManager.Instance?.SendContentUseRequest(request);
            }
        }
        
        /// <summary>
        /// Request to drop an item.
        /// </summary>
        public void RequestDropItem(RuntimeItem runtimeItem, Vector3 dropPosition, int maxAmount = 1)
        {
            if (m_IsRemoteClient) return;
            if (runtimeItem == null) return;
            
            var request = new NetworkContentDropRequest
            {
                RequestId = m_NextRequestId++,
                TargetBagNetworkId = NetworkId,
                RuntimeIdHash = runtimeItem.RuntimeID.Hash,
                DropPosition = dropPosition,
                MaxAmount = maxAmount
            };
            
            if (m_IsServer)
            {
                var response = ProcessContentDropRequest(request, NetworkId);
                ReceiveContentDropResponse(response);
            }
            else
            {
                NetworkInventoryManager.Instance?.SendContentDropRequest(request);
            }
        }
        
        #endregion
        
        #region Equipment Requests
        
        /// <summary>
        /// Request to equip an item.
        /// </summary>
        public void RequestEquip(RuntimeItem runtimeItem, int slot = -1)
        {
            if (m_IsRemoteClient) return;
            if (runtimeItem == null) return;
            
            var request = new NetworkEquipmentRequest
            {
                RequestId = m_NextRequestId++,
                TargetBagNetworkId = NetworkId,
                RuntimeIdHash = runtimeItem.RuntimeID.Hash,
                Action = slot >= 0 ? EquipmentAction.EquipToSlot : EquipmentAction.Equip,
                SlotOrIndex = slot
            };
            
            m_PendingEquipment[request.RequestId] = new PendingEquipment
            {
                Request = request,
                SentTime = Time.time
            };
            
            OnEquipmentRequested?.Invoke(request);
            
            if (m_IsServer)
            {
                var response = ProcessEquipmentRequest(request, NetworkId);
                ReceiveEquipmentResponse(response);
            }
            else
            {
                NetworkInventoryManager.Instance?.SendEquipmentRequest(request);
            }
        }
        
        /// <summary>
        /// Request to unequip an item.
        /// </summary>
        public void RequestUnequip(RuntimeItem runtimeItem)
        {
            if (m_IsRemoteClient) return;
            if (runtimeItem == null) return;
            
            var request = new NetworkEquipmentRequest
            {
                RequestId = m_NextRequestId++,
                TargetBagNetworkId = NetworkId,
                RuntimeIdHash = runtimeItem.RuntimeID.Hash,
                Action = EquipmentAction.Unequip,
                SlotOrIndex = -1
            };
            
            m_PendingEquipment[request.RequestId] = new PendingEquipment
            {
                Request = request,
                SentTime = Time.time
            };
            
            OnEquipmentRequested?.Invoke(request);
            
            if (m_IsServer)
            {
                var response = ProcessEquipmentRequest(request, NetworkId);
                ReceiveEquipmentResponse(response);
            }
            else
            {
                NetworkInventoryManager.Instance?.SendEquipmentRequest(request);
            }
        }
        
        /// <summary>
        /// Request to unequip from specific index.
        /// </summary>
        public void RequestUnequipFromIndex(int index)
        {
            if (m_IsRemoteClient) return;
            
            var request = new NetworkEquipmentRequest
            {
                RequestId = m_NextRequestId++,
                TargetBagNetworkId = NetworkId,
                RuntimeIdHash = 0,
                Action = EquipmentAction.UnequipFromIndex,
                SlotOrIndex = index
            };
            
            m_PendingEquipment[request.RequestId] = new PendingEquipment
            {
                Request = request,
                SentTime = Time.time
            };
            
            OnEquipmentRequested?.Invoke(request);
            
            if (m_IsServer)
            {
                var response = ProcessEquipmentRequest(request, NetworkId);
                ReceiveEquipmentResponse(response);
            }
            else
            {
                NetworkInventoryManager.Instance?.SendEquipmentRequest(request);
            }
        }
        
        #endregion
        
        #region Socket Requests
        
        /// <summary>
        /// Request to attach item to socket.
        /// </summary>
        public void RequestAttachToSocket(RuntimeItem parent, RuntimeItem attachment, IdString socketId = default)
        {
            if (m_IsRemoteClient) return;
            if (parent == null || attachment == null) return;
            
            var request = new NetworkSocketRequest
            {
                RequestId = m_NextRequestId++,
                TargetBagNetworkId = NetworkId,
                ParentRuntimeIdHash = parent.RuntimeID.Hash,
                AttachmentRuntimeIdHash = attachment.RuntimeID.Hash,
                SocketHash = socketId.Hash,
                Action = socketId.Hash != 0 ? SocketAction.AttachToSocket : SocketAction.Attach
            };
            
            OnSocketRequested?.Invoke(request);
            
            if (m_IsServer)
            {
                var response = ProcessSocketRequest(request, NetworkId);
                ReceiveSocketResponse(response);
            }
            else
            {
                NetworkInventoryManager.Instance?.SendSocketRequest(request);
            }
        }
        
        /// <summary>
        /// Request to detach from socket.
        /// </summary>
        public void RequestDetachFromSocket(RuntimeItem parent, IdString socketId)
        {
            if (m_IsRemoteClient) return;
            if (parent == null) return;
            
            var request = new NetworkSocketRequest
            {
                RequestId = m_NextRequestId++,
                TargetBagNetworkId = NetworkId,
                ParentRuntimeIdHash = parent.RuntimeID.Hash,
                SocketHash = socketId.Hash,
                Action = SocketAction.DetachFromSocket
            };
            
            OnSocketRequested?.Invoke(request);
            
            if (m_IsServer)
            {
                var response = ProcessSocketRequest(request, NetworkId);
                ReceiveSocketResponse(response);
            }
            else
            {
                NetworkInventoryManager.Instance?.SendSocketRequest(request);
            }
        }
        
        #endregion
        
        #region Wealth Requests
        
        /// <summary>
        /// Request to modify wealth.
        /// </summary>
        public void RequestWealthModify(Currency currency, int value, WealthAction action,
            InventoryModificationSource source = InventoryModificationSource.Direct, int sourceHash = 0)
        {
            if (m_IsRemoteClient) return;
            if (currency == null) return;
            
            var request = new NetworkWealthRequest
            {
                RequestId = m_NextRequestId++,
                TargetBagNetworkId = NetworkId,
                CurrencyHash = currency.ID.Hash,
                Value = value,
                Action = action,
                Source = source,
                SourceHash = sourceHash
            };
            
            int originalValue = m_Bag.Wealth.Get(currency);
            
            m_PendingWealth[request.RequestId] = new PendingWealth
            {
                Request = request,
                OriginalValue = originalValue,
                SentTime = Time.time
            };
            
            OnWealthRequested?.Invoke(request);
            
            if (m_IsServer)
            {
                var response = ProcessWealthRequest(request, NetworkId);
                ReceiveWealthResponse(response);
            }
            else
            {
                NetworkInventoryManager.Instance?.SendWealthRequest(request);
            }
        }
        
        #endregion
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // SERVER-SIDE: PROCESS REQUESTS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        #region Server Processing
        
        /// <summary>
        /// [Server] Process content add request.
        /// </summary>
        public NetworkContentAddResponse ProcessContentAddRequest(NetworkContentAddRequest request, uint clientNetworkId)
        {
            if (!m_IsServer)
            {
                return new NetworkContentAddResponse
                {
                    RequestId = request.RequestId,
                    Authorized = false,
                    RejectionReason = InventoryRejectionReason.NotAuthorized
                };
            }
            
            // Get or create the RuntimeItem
            RuntimeItem runtimeItem;
            if (request.ItemHash != 0)
            {
                // Create new from Item
                var item = GetItemFromHash(request.ItemHash);
                if (item == null)
                {
                    return new NetworkContentAddResponse
                    {
                        RequestId = request.RequestId,
                        Authorized = false,
                        RejectionReason = InventoryRejectionReason.ItemNotFound
                    };
                }
                
                runtimeItem = new RuntimeItem(item);
            }
            else
            {
                // Reconstruct from network data
                runtimeItem = ReconstructRuntimeItem(request.RuntimeItem);
                if (runtimeItem == null)
                {
                    return new NetworkContentAddResponse
                    {
                        RequestId = request.RequestId,
                        Authorized = false,
                        RejectionReason = InventoryRejectionReason.RuntimeItemNotFound
                    };
                }
            }
            
            // Try to add
            Vector2Int resultPosition;
            if (request.Position.x >= 0 && request.Position.y >= 0)
            {
                bool success = m_Bag.Content.Add(runtimeItem, request.Position, request.AllowStack);
                resultPosition = success ? request.Position : TBagContent.INVALID;
            }
            else
            {
                resultPosition = m_Bag.Content.Add(runtimeItem, request.AllowStack);
            }
            
            if (resultPosition == TBagContent.INVALID)
            {
                return new NetworkContentAddResponse
                {
                    RequestId = request.RequestId,
                    Authorized = false,
                    RejectionReason = InventoryRejectionReason.InsufficientSpace
                };
            }
            
            // Update map
            m_RuntimeItemMap[runtimeItem.RuntimeID.Hash] = runtimeItem;
            
            // Broadcast
            var broadcast = new NetworkItemAddedBroadcast
            {
                BagNetworkId = NetworkId,
                Item = ConvertToNetworkItem(runtimeItem),
                Position = resultPosition,
                StackCount = m_Bag.Content.GetContent(resultPosition)?.Count ?? 1
            };
            
            NetworkInventoryManager.Instance?.BroadcastItemAdded(broadcast);
            OnItemAdded?.Invoke(broadcast);
            
            return new NetworkContentAddResponse
            {
                RequestId = request.RequestId,
                Authorized = true,
                RejectionReason = InventoryRejectionReason.None,
                ResultPosition = resultPosition,
                AssignedRuntimeId = runtimeItem.RuntimeID.Hash,
                AssignedRuntimeIdString = runtimeItem.RuntimeID.String
            };
        }
        
        /// <summary>
        /// [Server] Process content remove request.
        /// </summary>
        public NetworkContentRemoveResponse ProcessContentRemoveRequest(NetworkContentRemoveRequest request, uint clientNetworkId)
        {
            if (!m_IsServer)
            {
                return new NetworkContentRemoveResponse
                {
                    RequestId = request.RequestId,
                    Authorized = false,
                    RejectionReason = InventoryRejectionReason.NotAuthorized
                };
            }
            
            RuntimeItem removed;
            Vector2Int position;
            
            if (request.UsePosition)
            {
                position = request.Position;
                removed = m_Bag.Content.Remove(position);
            }
            else
            {
                if (!m_RuntimeItemMap.TryGetValue(request.RuntimeIdHash, out var runtimeItem))
                {
                    return new NetworkContentRemoveResponse
                    {
                        RequestId = request.RequestId,
                        Authorized = false,
                        RejectionReason = InventoryRejectionReason.RuntimeItemNotFound
                    };
                }
                
                position = m_Bag.Content.FindPosition(runtimeItem.RuntimeID);
                removed = m_Bag.Content.Remove(runtimeItem);
            }
            
            if (removed == null)
            {
                return new NetworkContentRemoveResponse
                {
                    RequestId = request.RequestId,
                    Authorized = false,
                    RejectionReason = InventoryRejectionReason.RuntimeItemNotFound
                };
            }
            
            m_RuntimeItemMap.Remove(removed.RuntimeID.Hash);
            
            // Broadcast
            var cell = m_Bag.Content.GetContent(position);
            var broadcast = new NetworkItemRemovedBroadcast
            {
                BagNetworkId = NetworkId,
                RuntimeIdHash = removed.RuntimeID.Hash,
                Position = position,
                RemainingStackCount = cell?.Count ?? 0
            };
            
            NetworkInventoryManager.Instance?.BroadcastItemRemoved(broadcast);
            OnItemRemoved?.Invoke(broadcast);
            
            return new NetworkContentRemoveResponse
            {
                RequestId = request.RequestId,
                Authorized = true,
                RejectionReason = InventoryRejectionReason.None,
                RemovedItem = ConvertToNetworkItem(removed)
            };
        }
        
        /// <summary>
        /// [Server] Process content move request.
        /// </summary>
        public NetworkContentMoveResponse ProcessContentMoveRequest(NetworkContentMoveRequest request, uint clientNetworkId)
        {
            if (!m_IsServer)
            {
                return new NetworkContentMoveResponse
                {
                    RequestId = request.RequestId,
                    Authorized = false,
                    RejectionReason = InventoryRejectionReason.NotAuthorized
                };
            }
            
            if (!m_Bag.Content.CanMove(request.FromPosition, request.ToPosition, request.AllowStack))
            {
                return new NetworkContentMoveResponse
                {
                    RequestId = request.RequestId,
                    Authorized = false,
                    RejectionReason = InventoryRejectionReason.InvalidPosition
                };
            }
            
            var cell = m_Bag.Content.GetContent(request.FromPosition);
            long runtimeIdHash = cell?.RootRuntimeItemID.Hash ?? 0;
            
            bool success = m_Bag.Content.Move(request.FromPosition, request.ToPosition, request.AllowStack);
            
            if (!success)
            {
                return new NetworkContentMoveResponse
                {
                    RequestId = request.RequestId,
                    Authorized = false,
                    RejectionReason = InventoryRejectionReason.InvalidOperation
                };
            }
            
            // Broadcast
            var broadcast = new NetworkItemMovedBroadcast
            {
                BagNetworkId = NetworkId,
                RuntimeIdHash = runtimeIdHash,
                FromPosition = request.FromPosition,
                ToPosition = request.ToPosition
            };
            
            NetworkInventoryManager.Instance?.BroadcastItemMoved(broadcast);
            OnItemMoved?.Invoke(broadcast);
            
            return new NetworkContentMoveResponse
            {
                RequestId = request.RequestId,
                Authorized = true,
                RejectionReason = InventoryRejectionReason.None,
                FinalPosition = request.ToPosition
            };
        }
        
        /// <summary>
        /// [Server] Process content use request.
        /// </summary>
        public NetworkContentUseResponse ProcessContentUseRequest(NetworkContentUseRequest request, uint clientNetworkId)
        {
            if (!m_IsServer)
            {
                return new NetworkContentUseResponse
                {
                    RequestId = request.RequestId,
                    Authorized = false,
                    RejectionReason = InventoryRejectionReason.NotAuthorized
                };
            }
            
            bool success;
            bool wasConsumed = false;
            
            if (request.UsePosition)
            {
                var cell = m_Bag.Content.GetContent(request.Position);
                if (cell == null || cell.Available)
                {
                    return new NetworkContentUseResponse
                    {
                        RequestId = request.RequestId,
                        Authorized = false,
                        RejectionReason = InventoryRejectionReason.RuntimeItemNotFound
                    };
                }
                
                var runtimeItem = cell.RootRuntimeItem;
                wasConsumed = runtimeItem.Item.Usage.ConsumeWhenUse;
                success = m_Bag.Content.Use(request.Position);
            }
            else
            {
                if (!m_RuntimeItemMap.TryGetValue(request.RuntimeIdHash, out var runtimeItem))
                {
                    return new NetworkContentUseResponse
                    {
                        RequestId = request.RequestId,
                        Authorized = false,
                        RejectionReason = InventoryRejectionReason.RuntimeItemNotFound
                    };
                }
                
                wasConsumed = runtimeItem.Item.Usage.ConsumeWhenUse;
                success = m_Bag.Content.Use(runtimeItem);
                
                if (success && wasConsumed)
                {
                    m_RuntimeItemMap.Remove(request.RuntimeIdHash);
                }
            }
            
            if (!success)
            {
                return new NetworkContentUseResponse
                {
                    RequestId = request.RequestId,
                    Authorized = false,
                    RejectionReason = InventoryRejectionReason.CannotUse
                };
            }
            
            // Broadcast
            var broadcast = new NetworkItemUsedBroadcast
            {
                BagNetworkId = NetworkId,
                RuntimeIdHash = request.RuntimeIdHash,
                WasConsumed = wasConsumed
            };
            
            NetworkInventoryManager.Instance?.BroadcastItemUsed(broadcast);
            OnItemUsed?.Invoke(broadcast);
            
            return new NetworkContentUseResponse
            {
                RequestId = request.RequestId,
                Authorized = true,
                RejectionReason = InventoryRejectionReason.None,
                WasConsumed = wasConsumed
            };
        }
        
        /// <summary>
        /// [Server] Process content drop request.
        /// </summary>
        public NetworkContentDropResponse ProcessContentDropRequest(NetworkContentDropRequest request, uint clientNetworkId)
        {
            if (!m_IsServer)
            {
                return new NetworkContentDropResponse
                {
                    RequestId = request.RequestId,
                    Authorized = false,
                    RejectionReason = InventoryRejectionReason.NotAuthorized
                };
            }
            
            if (!m_RuntimeItemMap.TryGetValue(request.RuntimeIdHash, out var runtimeItem))
            {
                return new NetworkContentDropResponse
                {
                    RequestId = request.RequestId,
                    Authorized = false,
                    RejectionReason = InventoryRejectionReason.RuntimeItemNotFound
                };
            }
            
            if (!runtimeItem.Item.CanDrop)
            {
                return new NetworkContentDropResponse
                {
                    RequestId = request.RequestId,
                    Authorized = false,
                    RejectionReason = InventoryRejectionReason.CannotDrop
                };
            }
            
            var dropped = m_Bag.Content.Drop(runtimeItem, request.DropPosition);
            
            if (dropped == null)
            {
                return new NetworkContentDropResponse
                {
                    RequestId = request.RequestId,
                    Authorized = false,
                    RejectionReason = InventoryRejectionReason.CannotDrop
                };
            }
            
            m_RuntimeItemMap.Remove(request.RuntimeIdHash);
            
            // Note: The actual Prop NetworkObject spawning should be handled by the transport layer
            
            return new NetworkContentDropResponse
            {
                RequestId = request.RequestId,
                Authorized = true,
                RejectionReason = InventoryRejectionReason.None,
                DroppedCount = 1
            };
        }
        
        /// <summary>
        /// [Server] Process equipment request.
        /// </summary>
        public async Task<NetworkEquipmentResponse> ProcessEquipmentRequest(NetworkEquipmentRequest request, uint clientNetworkId)
        {
            if (!m_IsServer)
            {
                return new NetworkEquipmentResponse
                {
                    RequestId = request.RequestId,
                    Authorized = false,
                    RejectionReason = InventoryRejectionReason.NotAuthorized
                };
            }
            
            bool success = false;
            int equippedIndex = -1;
            
            switch (request.Action)
            {
                case EquipmentAction.Equip:
                case EquipmentAction.EquipToSlot:
                case EquipmentAction.EquipToIndex:
                {
                    if (!m_RuntimeItemMap.TryGetValue(request.RuntimeIdHash, out var runtimeItem))
                    {
                        return new NetworkEquipmentResponse
                        {
                            RequestId = request.RequestId,
                            Authorized = false,
                            RejectionReason = InventoryRejectionReason.RuntimeItemNotFound
                        };
                    }
                    
                    if (request.Action == EquipmentAction.EquipToIndex)
                    {
                        success = await m_Bag.Equipment.EquipToIndex(runtimeItem, request.SlotOrIndex);
                        equippedIndex = request.SlotOrIndex;
                    }
                    else if (request.Action == EquipmentAction.EquipToSlot)
                    {
                        success = await m_Bag.Equipment.Equip(runtimeItem, request.SlotOrIndex);
                        equippedIndex = m_Bag.Equipment.GetEquippedIndex(runtimeItem);
                    }
                    else
                    {
                        success = await m_Bag.Equipment.Equip(runtimeItem);
                        equippedIndex = m_Bag.Equipment.GetEquippedIndex(runtimeItem);
                    }
                    
                    if (success)
                    {
                        var broadcast = new NetworkItemEquippedBroadcast
                        {
                            BagNetworkId = NetworkId,
                            RuntimeIdHash = request.RuntimeIdHash,
                            EquipmentIndex = equippedIndex
                        };
                        NetworkInventoryManager.Instance?.BroadcastItemEquipped(broadcast);
                        OnItemEquipped?.Invoke(broadcast);
                    }
                    break;
                }
                
                case EquipmentAction.Unequip:
                {
                    if (!m_RuntimeItemMap.TryGetValue(request.RuntimeIdHash, out var runtimeItem))
                    {
                        return new NetworkEquipmentResponse
                        {
                            RequestId = request.RequestId,
                            Authorized = false,
                            RejectionReason = InventoryRejectionReason.RuntimeItemNotFound
                        };
                    }
                    
                    equippedIndex = m_Bag.Equipment.GetEquippedIndex(runtimeItem);
                    success = await m_Bag.Equipment.Unequip(runtimeItem);
                    
                    if (success)
                    {
                        var broadcast = new NetworkItemUnequippedBroadcast
                        {
                            BagNetworkId = NetworkId,
                            RuntimeIdHash = request.RuntimeIdHash,
                            EquipmentIndex = equippedIndex
                        };
                        NetworkInventoryManager.Instance?.BroadcastItemUnequipped(broadcast);
                        OnItemUnequipped?.Invoke(broadcast);
                    }
                    break;
                }
                
                case EquipmentAction.UnequipFromIndex:
                {
                    var slotId = m_Bag.Equipment.GetSlotRootRuntimeItemID(request.SlotOrIndex);
                    long runtimeIdHash = slotId.Hash;
                    
                    success = await m_Bag.Equipment.UnequipFromIndex(request.SlotOrIndex);
                    equippedIndex = request.SlotOrIndex;
                    
                    if (success)
                    {
                        var broadcast = new NetworkItemUnequippedBroadcast
                        {
                            BagNetworkId = NetworkId,
                            RuntimeIdHash = runtimeIdHash,
                            EquipmentIndex = equippedIndex
                        };
                        NetworkInventoryManager.Instance?.BroadcastItemUnequipped(broadcast);
                        OnItemUnequipped?.Invoke(broadcast);
                    }
                    break;
                }
            }
            
            return new NetworkEquipmentResponse
            {
                RequestId = request.RequestId,
                Authorized = success,
                RejectionReason = success ? InventoryRejectionReason.None : InventoryRejectionReason.CannotEquip,
                EquippedIndex = equippedIndex
            };
        }
        
        /// <summary>
        /// [Server] Process socket request.
        /// </summary>
        public NetworkSocketResponse ProcessSocketRequest(NetworkSocketRequest request, uint clientNetworkId)
        {
            if (!m_IsServer)
            {
                return new NetworkSocketResponse
                {
                    RequestId = request.RequestId,
                    Authorized = false,
                    RejectionReason = InventoryRejectionReason.NotAuthorized
                };
            }
            
            if (!m_RuntimeItemMap.TryGetValue(request.ParentRuntimeIdHash, out var parent))
            {
                return new NetworkSocketResponse
                {
                    RequestId = request.RequestId,
                    Authorized = false,
                    RejectionReason = InventoryRejectionReason.RuntimeItemNotFound
                };
            }
            
            bool success = false;
            NetworkRuntimeItem detachedItem = default;
            int usedSocketHash = request.SocketHash;
            
            switch (request.Action)
            {
                case SocketAction.Attach:
                case SocketAction.AttachToSocket:
                {
                    if (!m_RuntimeItemMap.TryGetValue(request.AttachmentRuntimeIdHash, out var attachment))
                    {
                        return new NetworkSocketResponse
                        {
                            RequestId = request.RequestId,
                            Authorized = false,
                            RejectionReason = InventoryRejectionReason.RuntimeItemNotFound
                        };
                    }
                    
                    if (request.Action == SocketAction.AttachToSocket)
                    {
                        var socketId = new IdString(request.SocketHash.ToString()); // Note: Would need proper reconstruction
                        success = m_Bag.Equipment.AttachTo(parent, attachment, socketId);
                    }
                    else
                    {
                        success = m_Bag.Equipment.AttachTo(parent, attachment);
                    }
                    break;
                }
                
                case SocketAction.Detach:
                case SocketAction.DetachFromSocket:
                {
                    RuntimeItem detached;
                    if (request.Action == SocketAction.DetachFromSocket)
                    {
                        var socketId = new IdString(request.SocketHash.ToString());
                        detached = m_Bag.Equipment.DetachFrom(parent, socketId);
                    }
                    else
                    {
                        if (!m_RuntimeItemMap.TryGetValue(request.AttachmentRuntimeIdHash, out var attachment))
                        {
                            return new NetworkSocketResponse
                            {
                                RequestId = request.RequestId,
                                Authorized = false,
                                RejectionReason = InventoryRejectionReason.RuntimeItemNotFound
                            };
                        }
                        detached = m_Bag.Equipment.DetachFrom(parent, attachment);
                    }
                    
                    success = detached != null;
                    if (success)
                    {
                        detachedItem = ConvertToNetworkItem(detached);
                    }
                    break;
                }
            }
            
            if (success)
            {
                var broadcast = new NetworkSocketChangeBroadcast
                {
                    BagNetworkId = NetworkId,
                    ParentRuntimeIdHash = request.ParentRuntimeIdHash,
                    SocketHash = usedSocketHash,
                    HasAttachment = request.Action == SocketAction.Attach || request.Action == SocketAction.AttachToSocket
                };
                NetworkInventoryManager.Instance?.BroadcastSocketChange(broadcast);
                OnSocketChanged?.Invoke(broadcast);
            }
            
            return new NetworkSocketResponse
            {
                RequestId = request.RequestId,
                Authorized = success,
                RejectionReason = success ? InventoryRejectionReason.None : InventoryRejectionReason.CannotAttach,
                UsedSocketHash = usedSocketHash,
                DetachedItem = detachedItem
            };
        }
        
        /// <summary>
        /// [Server] Process wealth request.
        /// </summary>
        public NetworkWealthResponse ProcessWealthRequest(NetworkWealthRequest request, uint clientNetworkId)
        {
            if (!m_IsServer)
            {
                return new NetworkWealthResponse
                {
                    RequestId = request.RequestId,
                    Authorized = false,
                    RejectionReason = InventoryRejectionReason.NotAuthorized
                };
            }
            
            var currencyId = new IdString(request.CurrencyHash.ToString()); // Note: Would need proper reconstruction
            int oldValue = m_Bag.Wealth.Get(currencyId);
            int newValue;
            
            switch (request.Action)
            {
                case WealthAction.Set:
                    m_Bag.Wealth.Set(currencyId, request.Value);
                    newValue = request.Value;
                    break;
                    
                case WealthAction.Add:
                    m_Bag.Wealth.Add(currencyId, request.Value);
                    newValue = oldValue + request.Value;
                    break;
                    
                case WealthAction.Subtract:
                    if (oldValue < request.Value)
                    {
                        return new NetworkWealthResponse
                        {
                            RequestId = request.RequestId,
                            Authorized = false,
                            RejectionReason = InventoryRejectionReason.InsufficientFunds
                        };
                    }
                    m_Bag.Wealth.Subtract(currencyId, request.Value);
                    newValue = oldValue - request.Value;
                    break;
                    
                default:
                    return new NetworkWealthResponse
                    {
                        RequestId = request.RequestId,
                        Authorized = false,
                        RejectionReason = InventoryRejectionReason.InvalidOperation
                    };
            }
            
            // Broadcast
            var broadcast = new NetworkWealthChangeBroadcast
            {
                BagNetworkId = NetworkId,
                CurrencyHash = request.CurrencyHash,
                NewValue = newValue,
                Change = newValue - oldValue
            };
            NetworkInventoryManager.Instance?.BroadcastWealthChange(broadcast);
            OnWealthChanged?.Invoke(broadcast);
            
            return new NetworkWealthResponse
            {
                RequestId = request.RequestId,
                Authorized = true,
                RejectionReason = InventoryRejectionReason.None,
                NewValue = newValue,
                OldValue = oldValue
            };
        }
        
        #endregion
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // CLIENT-SIDE: RECEIVE RESPONSES
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        #region Client Response Handlers
        
        public void ReceiveContentAddResponse(NetworkContentAddResponse response)
        {
            if (!m_PendingAdds.TryGetValue(response.RequestId, out var pending))
                return;
            
            m_PendingAdds.Remove(response.RequestId);
            
            if (!response.Authorized)
            {
                if (m_LogRejections)
                    Debug.LogWarning($"[NetworkInventoryController] Add rejected: {response.RejectionReason}");
                OnOperationRejected?.Invoke(response.RejectionReason, "Add item");
            }
        }
        
        public void ReceiveContentRemoveResponse(NetworkContentRemoveResponse response)
        {
            if (!m_PendingRemoves.TryGetValue(response.RequestId, out var pending))
                return;
            
            m_PendingRemoves.Remove(response.RequestId);
            
            if (!response.Authorized)
            {
                if (m_LogRejections)
                    Debug.LogWarning($"[NetworkInventoryController] Remove rejected: {response.RejectionReason}");
                OnOperationRejected?.Invoke(response.RejectionReason, "Remove item");
            }
        }
        
        public void ReceiveContentMoveResponse(NetworkContentMoveResponse response)
        {
            if (!m_PendingMoves.TryGetValue(response.RequestId, out var pending))
                return;
            
            m_PendingMoves.Remove(response.RequestId);
            
            if (!response.Authorized)
            {
                if (m_LogRejections)
                    Debug.LogWarning($"[NetworkInventoryController] Move rejected: {response.RejectionReason}");
                OnOperationRejected?.Invoke(response.RejectionReason, "Move item");
            }
        }
        
        public void ReceiveContentUseResponse(NetworkContentUseResponse response)
        {
            if (!response.Authorized)
            {
                if (m_LogRejections)
                    Debug.LogWarning($"[NetworkInventoryController] Use rejected: {response.RejectionReason}");
                OnOperationRejected?.Invoke(response.RejectionReason, "Use item");
            }
        }
        
        public void ReceiveContentDropResponse(NetworkContentDropResponse response)
        {
            if (!response.Authorized)
            {
                if (m_LogRejections)
                    Debug.LogWarning($"[NetworkInventoryController] Drop rejected: {response.RejectionReason}");
                OnOperationRejected?.Invoke(response.RejectionReason, "Drop item");
            }
        }
        
        public void ReceiveEquipmentResponse(NetworkEquipmentResponse response)
        {
            if (!m_PendingEquipment.TryGetValue(response.RequestId, out var pending))
                return;
            
            m_PendingEquipment.Remove(response.RequestId);
            
            if (!response.Authorized)
            {
                if (m_LogRejections)
                    Debug.LogWarning($"[NetworkInventoryController] Equipment rejected: {response.RejectionReason}");
                OnOperationRejected?.Invoke(response.RejectionReason, "Equipment operation");
            }
        }
        
        public void ReceiveSocketResponse(NetworkSocketResponse response)
        {
            if (!response.Authorized)
            {
                if (m_LogRejections)
                    Debug.LogWarning($"[NetworkInventoryController] Socket rejected: {response.RejectionReason}");
                OnOperationRejected?.Invoke(response.RejectionReason, "Socket operation");
            }
        }
        
        public void ReceiveWealthResponse(NetworkWealthResponse response)
        {
            if (!m_PendingWealth.TryGetValue(response.RequestId, out var pending))
                return;
            
            m_PendingWealth.Remove(response.RequestId);
            
            if (!response.Authorized)
            {
                if (m_LogRejections)
                    Debug.LogWarning($"[NetworkInventoryController] Wealth rejected: {response.RejectionReason}");
                OnOperationRejected?.Invoke(response.RejectionReason, "Wealth operation");
            }
        }
        
        #endregion
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // BROADCAST RECEIVERS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        #region Broadcast Receivers
        
        public void ReceiveItemAddedBroadcast(NetworkItemAddedBroadcast broadcast)
        {
            if (m_IsServer) return;
            
            // Reconstruct and apply
            var runtimeItem = ReconstructRuntimeItem(broadcast.Item);
            if (runtimeItem != null)
            {
                m_Bag.Content.Add(runtimeItem, broadcast.Position, true);
                m_RuntimeItemMap[runtimeItem.RuntimeID.Hash] = runtimeItem;
            }
            
            OnItemAdded?.Invoke(broadcast);
        }
        
        public void ReceiveItemRemovedBroadcast(NetworkItemRemovedBroadcast broadcast)
        {
            if (m_IsServer) return;
            
            if (m_RuntimeItemMap.TryGetValue(broadcast.RuntimeIdHash, out var runtimeItem))
            {
                m_Bag.Content.Remove(runtimeItem);
                m_RuntimeItemMap.Remove(broadcast.RuntimeIdHash);
            }
            
            OnItemRemoved?.Invoke(broadcast);
        }
        
        public void ReceiveItemMovedBroadcast(NetworkItemMovedBroadcast broadcast)
        {
            if (m_IsServer) return;
            
            m_Bag.Content.Move(broadcast.FromPosition, broadcast.ToPosition, true);
            OnItemMoved?.Invoke(broadcast);
        }
        
        public void ReceiveItemUsedBroadcast(NetworkItemUsedBroadcast broadcast)
        {
            if (m_IsServer) return;
            
            if (broadcast.WasConsumed && m_RuntimeItemMap.TryGetValue(broadcast.RuntimeIdHash, out var runtimeItem))
            {
                m_Bag.Content.Remove(runtimeItem);
                m_RuntimeItemMap.Remove(broadcast.RuntimeIdHash);
            }
            
            OnItemUsed?.Invoke(broadcast);
        }
        
        public void ReceiveItemEquippedBroadcast(NetworkItemEquippedBroadcast broadcast)
        {
            if (m_IsServer) return;
            
            if (m_RuntimeItemMap.TryGetValue(broadcast.RuntimeIdHash, out var runtimeItem))
            {
                _ = m_Bag.Equipment.EquipToIndex(runtimeItem, broadcast.EquipmentIndex);
            }
            
            OnItemEquipped?.Invoke(broadcast);
        }
        
        public void ReceiveItemUnequippedBroadcast(NetworkItemUnequippedBroadcast broadcast)
        {
            if (m_IsServer) return;
            
            _ = m_Bag.Equipment.UnequipFromIndex(broadcast.EquipmentIndex);
            OnItemUnequipped?.Invoke(broadcast);
        }
        
        public void ReceiveSocketChangeBroadcast(NetworkSocketChangeBroadcast broadcast)
        {
            if (m_IsServer) return;
            
            // Socket changes would need to be reconstructed
            OnSocketChanged?.Invoke(broadcast);
        }
        
        public void ReceiveWealthChangeBroadcast(NetworkWealthChangeBroadcast broadcast)
        {
            if (m_IsServer) return;
            
            var currencyId = new IdString(broadcast.CurrencyHash.ToString());
            m_Bag.Wealth.Set(currencyId, broadcast.NewValue);
            
            OnWealthChanged?.Invoke(broadcast);
        }
        
        public void ReceiveFullSnapshot(NetworkInventorySnapshot snapshot)
        {
            if (m_IsServer) return;
            
            // This would require clearing and rebuilding the entire inventory
            // Implementation would depend on how GC2 handles bulk operations
            
            if (m_LogAllChanges)
            {
                Debug.Log($"[NetworkInventoryController] Received full snapshot: {snapshot.Cells?.Length ?? 0} cells");
            }
        }
        
        #endregion
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // SERVER BROADCASTING
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        private void BroadcastFullState()
        {
            var snapshot = GetFullSnapshot();
            NetworkInventoryManager.Instance?.BroadcastFullSnapshot(snapshot);
        }
        
        private void BroadcastDeltaState()
        {
            // Implement delta tracking and broadcast
            // This is complex due to the nature of inventory changes
        }
        
        /// <summary>
        /// Get full inventory snapshot for initial sync.
        /// </summary>
        public NetworkInventorySnapshot GetFullSnapshot()
        {
            var cells = new List<NetworkCell>();
            var equipment = new List<NetworkEquipmentSlot>();
            var wealth = new List<NetworkWealthEntry>();
            
            // Collect cells
            foreach (var cell in m_Bag.Content.CellList)
            {
                if (cell == null || cell.Available) continue;
                
                var position = m_Bag.Content.FindPosition(cell.RootRuntimeItemID);
                
                cells.Add(new NetworkCell
                {
                    Position = position,
                    ItemHash = cell.Item.ID.Hash,
                    StackCount = cell.Count,
                    RootItem = ConvertToNetworkItem(cell.RootRuntimeItem),
                    StackedRuntimeIds = GetStackedIds(cell)
                });
            }
            
            // Collect equipment
            for (int i = 0; i < m_Bag.Equipment.Count; i++)
            {
                var slotId = m_Bag.Equipment.GetSlotRootRuntimeItemID(i);
                var baseId = m_Bag.Equipment.GetSlotBaseID(i);
                
                equipment.Add(new NetworkEquipmentSlot
                {
                    SlotIndex = i,
                    BaseItemHash = baseId.Hash,
                    IsOccupied = !string.IsNullOrEmpty(slotId.String),
                    EquippedRuntimeIdHash = slotId.Hash
                });
            }
            
            // Collect wealth
            foreach (var currencyId in m_Bag.Wealth.List)
            {
                wealth.Add(new NetworkWealthEntry
                {
                    CurrencyHash = currencyId.Hash,
                    Amount = m_Bag.Wealth.Get(currencyId)
                });
            }
            
            return new NetworkInventorySnapshot
            {
                BagNetworkId = NetworkId,
                Timestamp = Time.time,
                Cells = cells.ToArray(),
                Equipment = equipment.ToArray(),
                Wealth = wealth.ToArray()
            };
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // HELPER METHODS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        private void CleanupPendingRequests()
        {
            float timeout = 5f;
            float currentTime = Time.time;
            
            CleanupPending(m_PendingAdds, currentTime, timeout);
            CleanupPending(m_PendingRemoves, currentTime, timeout);
            CleanupPending(m_PendingMoves, currentTime, timeout);
            CleanupPending(m_PendingEquipment, currentTime, timeout);
            CleanupPending(m_PendingWealth, currentTime, timeout);
        }
        
        private void CleanupPending<T>(Dictionary<ushort, T> pending, float currentTime, float timeout) where T : struct
        {
            var keysToRemove = new List<ushort>();
            foreach (var kvp in pending)
            {
                // Using reflection to get SentTime - in production, use interface
                var sentTimeField = typeof(T).GetField("SentTime");
                if (sentTimeField != null)
                {
                    float sentTime = (float)sentTimeField.GetValue(kvp.Value);
                    if (currentTime - sentTime > timeout)
                        keysToRemove.Add(kvp.Key);
                }
            }
            foreach (var key in keysToRemove)
                pending.Remove(key);
        }
        
        private Item GetItemFromHash(int hash)
        {
            // Would need to look up from InventoryRepository
            var inventory = Settings.From<InventoryRepository>();
            // Note: GC2's Items.Get takes IdString, not hash - would need proper lookup
            return null; // Placeholder
        }
        
        private NetworkRuntimeItem ConvertToNetworkItem(RuntimeItem runtimeItem)
        {
            if (runtimeItem == null) return default;
            
            var properties = new List<NetworkRuntimeProperty>();
            foreach (var prop in runtimeItem.Properties)
            {
                properties.Add(new NetworkRuntimeProperty
                {
                    PropertyHash = prop.Key.Hash,
                    Number = prop.Value.Number,
                    Text = prop.Value.Text
                });
            }
            
            var sockets = new List<NetworkRuntimeSocket>();
            foreach (var socket in runtimeItem.Sockets)
            {
                sockets.Add(new NetworkRuntimeSocket
                {
                    SocketHash = socket.Key.Hash,
                    HasAttachment = socket.Value.HasAttachment,
                    Attachment = socket.Value.HasAttachment ? ConvertToNetworkItem(socket.Value.Attachment) : default
                });
            }
            
            return new NetworkRuntimeItem
            {
                ItemHash = runtimeItem.ItemID.Hash,
                RuntimeIdHash = runtimeItem.RuntimeID.Hash,
                RuntimeIdString = runtimeItem.RuntimeID.String,
                Properties = properties.ToArray(),
                Sockets = sockets.ToArray()
            };
        }
        
        private RuntimeItem ReconstructRuntimeItem(NetworkRuntimeItem networkItem)
        {
            if (networkItem.ItemHash == 0) return null;
            
            var item = GetItemFromHash(networkItem.ItemHash);
            if (item == null) return null;
            
            var runtimeItem = new RuntimeItem(item);
            // Would need to set properties and sockets from network data
            // This is complex due to GC2's internal structure
            
            return runtimeItem;
        }
        
        private long[] GetStackedIds(Cell cell)
        {
            var ids = new List<long>();
            foreach (var id in cell.List)
            {
                if (id.Hash != cell.RootRuntimeItemID.Hash)
                    ids.Add(id.Hash);
            }
            return ids.ToArray();
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // LOCAL CHANGE DETECTION
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        private void OnLocalItemAdded(RuntimeItem item)
        {
            if (m_LogAllChanges && !m_IsServer)
                Debug.Log($"[NetworkInventoryController] Local item added: {item?.ItemID.String}");
        }
        
        private void OnLocalItemRemoved(RuntimeItem item)
        {
            if (m_LogAllChanges && !m_IsServer)
                Debug.Log($"[NetworkInventoryController] Local item removed: {item?.ItemID.String}");
        }
        
        private void OnLocalItemUsed(RuntimeItem item)
        {
            if (m_LogAllChanges && !m_IsServer)
                Debug.Log($"[NetworkInventoryController] Local item used: {item?.ItemID.String}");
        }
        
        private void OnLocalItemEquipped(RuntimeItem item, int index)
        {
            if (m_LogAllChanges && !m_IsServer)
                Debug.Log($"[NetworkInventoryController] Local item equipped: {item?.ItemID.String} at {index}");
        }
        
        private void OnLocalItemUnequipped(RuntimeItem item, int index)
        {
            if (m_LogAllChanges && !m_IsServer)
                Debug.Log($"[NetworkInventoryController] Local item unequipped: {item?.ItemID.String} from {index}");
        }
        
        private void OnLocalWealthChanged(IdString currencyId, int oldValue, int newValue)
        {
            if (m_LogAllChanges && !m_IsServer)
                Debug.Log($"[NetworkInventoryController] Local wealth changed: {currencyId.String} {oldValue} -> {newValue}");
        }
    }
}
#endif
