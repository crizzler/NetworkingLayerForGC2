#if GC2_INVENTORY
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using Arawn.GameCreator2.Networking;
using Arawn.GameCreator2.Networking.Security;

namespace Arawn.GameCreator2.Networking.Inventory
{
    /// <summary>
    /// Global manager for inventory network communication.
    /// Transport-agnostic - wire up delegates to your networking solution.
    /// </summary>
    [AddComponentMenu("Game Creator/Network/Inventory/Network Inventory Manager")]
    public partial class NetworkInventoryManager : NetworkSingleton<NetworkInventoryManager>
    {
        // ════════════════════════════════════════════════════════════════════════════════════════
        // SINGLETON (lazy-find override)
        // ════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>Singleton instance. Falls back to FindFirstObjectByType if not yet assigned.</summary>
        public new static NetworkInventoryManager Instance
        {
            get
            {
                if (s_Instance == null)
                    s_Instance = FindFirstObjectByType<NetworkInventoryManager>();
                return s_Instance;
            }
        }

        // ════════════════════════════════════════════════════════════════════════════════════════
        // TRANSPORT DELEGATES - Wire to your networking solution
        // ════════════════════════════════════════════════════════════════════════════════════════

        // ─────────────────────────────────────────────────────────────────────────────────────────
        // CLIENT → SERVER: Content Operations
        // ─────────────────────────────────────────────────────────────────────────────────────────

        public Action<NetworkContentAddRequest> OnSendContentAddRequest;
        public Action<NetworkContentRemoveRequest> OnSendContentRemoveRequest;
        public Action<NetworkContentMoveRequest> OnSendContentMoveRequest;
        public Action<NetworkContentUseRequest> OnSendContentUseRequest;
        public Action<NetworkContentDropRequest> OnSendContentDropRequest;

        // ─────────────────────────────────────────────────────────────────────────────────────────
        // CLIENT → SERVER: Equipment Operations
        // ─────────────────────────────────────────────────────────────────────────────────────────

        public Action<NetworkEquipmentRequest> OnSendEquipmentRequest;

        // ─────────────────────────────────────────────────────────────────────────────────────────
        // CLIENT → SERVER: Socket Operations
        // ─────────────────────────────────────────────────────────────────────────────────────────

        public Action<NetworkSocketRequest> OnSendSocketRequest;

        // ─────────────────────────────────────────────────────────────────────────────────────────
        // CLIENT → SERVER: Wealth Operations
        // ─────────────────────────────────────────────────────────────────────────────────────────

        public Action<NetworkWealthRequest> OnSendWealthRequest;

        // ─────────────────────────────────────────────────────────────────────────────────────────
        // CLIENT → SERVER: Merchant Operations
        // ─────────────────────────────────────────────────────────────────────────────────────────

        public Action<NetworkMerchantRequest> OnSendMerchantRequest;

        // ─────────────────────────────────────────────────────────────────────────────────────────
        // CLIENT → SERVER: Crafting Operations
        // ─────────────────────────────────────────────────────────────────────────────────────────

        public Action<NetworkCraftingRequest> OnSendCraftingRequest;

        // ─────────────────────────────────────────────────────────────────────────────────────────
        // CLIENT → SERVER: Transfer Operations
        // ─────────────────────────────────────────────────────────────────────────────────────────

        public Action<NetworkTransferRequest> OnSendTransferRequest;
        public Action<NetworkPickupRequest> OnSendPickupRequest;
        public Action<NetworkCombineRequest> OnSendCombineRequest;

        // ─────────────────────────────────────────────────────────────────────────────────────────
        // SERVER → CLIENT: Responses (Single target)
        // ─────────────────────────────────────────────────────────────────────────────────────────

        public Action<uint, NetworkContentAddResponse> OnSendContentAddResponse;
        public Action<uint, NetworkContentRemoveResponse> OnSendContentRemoveResponse;
        public Action<uint, NetworkContentMoveResponse> OnSendContentMoveResponse;
        public Action<uint, NetworkContentUseResponse> OnSendContentUseResponse;
        public Action<uint, NetworkContentDropResponse> OnSendContentDropResponse;
        public Action<uint, NetworkEquipmentResponse> OnSendEquipmentResponse;
        public Action<uint, NetworkSocketResponse> OnSendSocketResponse;
        public Action<uint, NetworkWealthResponse> OnSendWealthResponse;
        public Action<uint, NetworkMerchantResponse> OnSendMerchantResponse;
        public Action<uint, NetworkCraftingResponse> OnSendCraftingResponse;
        public Action<uint, NetworkTransferResponse> OnSendTransferResponse;
        public Action<uint, NetworkPickupResponse> OnSendPickupResponse;
        public Action<uint, NetworkCombineResponse> OnSendCombineResponse;

        // ─────────────────────────────────────────────────────────────────────────────────────────
        // SERVER → ALL CLIENTS: Broadcasts
        // ─────────────────────────────────────────────────────────────────────────────────────────

        public Action<NetworkItemAddedBroadcast> OnBroadcastItemAdded;
        public Action<NetworkItemRemovedBroadcast> OnBroadcastItemRemoved;
        public Action<NetworkItemDroppedBroadcast> OnBroadcastItemDropped;
        public Action<NetworkDroppedItemRemovedBroadcast> OnBroadcastDroppedItemRemoved;
        public Action<NetworkItemMovedBroadcast> OnBroadcastItemMoved;
        public Action<NetworkItemUsedBroadcast> OnBroadcastItemUsed;
        public Action<NetworkItemEquippedBroadcast> OnBroadcastItemEquipped;
        public Action<NetworkItemUnequippedBroadcast> OnBroadcastItemUnequipped;
        public Action<NetworkSocketChangeBroadcast> OnBroadcastSocketChange;
        public Action<NetworkWealthChangeBroadcast> OnBroadcastWealthChange;
        public Action<NetworkPropertyChangeBroadcast> OnBroadcastPropertyChange;
        public Action<NetworkInventorySnapshot> OnBroadcastFullSnapshot;
        public Action<NetworkInventoryDelta> OnBroadcastDelta;

        // ─────────────────────────────────────────────────────────────────────────────────────────
        // SERVER → SINGLE CLIENT: Targeted
        // ─────────────────────────────────────────────────────────────────────────────────────────

        public Action<ulong, NetworkInventorySnapshot> OnSendSnapshotToClient;

        // ════════════════════════════════════════════════════════════════════════════════════════
        // INSPECTOR
        // ════════════════════════════════════════════════════════════════════════════════════════

        [Header("Settings")]
        [SerializeField] private bool m_IsServer;

        [Header("Validation")]
        [SerializeField] private int m_MaxPendingRequestsPerPlayer = 50;
        [SerializeField] private float m_RequestTimeout = 5f;

        [Tooltip("UNSAFE compatibility mode. Allows an owning client to create registered Item types without a project validator. Leave disabled for server-authoritative games.")]
        [SerializeField] private bool m_AllowUnvalidatedOwnedClientAdds = false;

        [Tooltip("Maximum distance between the requesting character and a scene/world inventory. This also protects legacy dropped-item pickups.")]
        [SerializeField, Min(0.1f)] private float m_MaxWorldInteractionDistance = 5f;

        [Tooltip("Maximum number of bags whose persistent state may wait for a controller to register.")]
        [SerializeField] private int m_MaxPendingPersistentBags = 128;

        [Tooltip("Maximum ordered persistent messages retained per unregistered bag.")]
        [SerializeField] private int m_MaxPendingPersistentMessagesPerBag = 64;

        [Header("Debug")]
        [SerializeField] private bool m_LogNetworkMessages = false;

        // ════════════════════════════════════════════════════════════════════════════════════════
        // PRIVATE FIELDS
        // ════════════════════════════════════════════════════════════════════════════════════════

        private readonly Dictionary<uint, NetworkInventoryController> m_Controllers = new(32);
        private readonly Dictionary<ulong, int> m_PendingRequestCounts = new(32);
        private readonly Dictionary<uint, List<PendingPersistentState>> m_PendingPersistentState = new(16);
        private readonly HashSet<uint> m_PendingPersistentOverflow = new();
        private NetworkInventoryPatchHooks m_PatchHooks;

        private const float PENDING_TRANSIENT_USE_TTL_SECONDS = 2f;

        // Merchant controllers (separate from player bags)
        private readonly Dictionary<uint, NetworkMerchantController> m_MerchantControllers = new(8);

        private enum PendingPersistentStateKind : byte
        {
            ItemAdded,
            ItemRemoved,
            ItemMoved,
            ItemUsed,
            ItemEquipped,
            ItemUnequipped,
            SocketChanged,
            WealthChanged,
            PropertyChanged,
            Snapshot,
            Delta
        }

        private readonly struct PendingPersistentState
        {
            public readonly PendingPersistentStateKind Kind;
            public readonly object Payload;
            public readonly uint StateVersion;
            public readonly float QueuedRealtime;

            public PendingPersistentState(
                PendingPersistentStateKind kind,
                object payload,
                uint stateVersion,
                float queuedRealtime = 0f)
            {
                Kind = kind;
                Payload = payload;
                StateVersion = stateVersion;
                QueuedRealtime = queuedRealtime;
            }
        }

        // ════════════════════════════════════════════════════════════════════════════════════════
        // PROPERTIES
        // ════════════════════════════════════════════════════════════════════════════════════════

        public bool IsServer
        {
            get => m_IsServer;
            set
            {
                m_IsServer = value;
                SecurityIntegration.SetModuleServerContext("Inventory", m_IsServer);
                SecurityIntegration.EnsureSecurityManagerInitialized(m_IsServer, ResolveSecurityTimeProvider);
                SyncPatchHooks();
                if (m_IsServer) RefreshOwnedEntityMappings();
            }
        }

        public int ControllerCount => m_Controllers.Count;

        public float RequestTimeoutSeconds => m_RequestTimeout;

        /// <summary>Whether optional Inventory transport and reconciliation diagnostics are enabled.</summary>
        public bool DiagnosticsEnabled => m_LogNetworkMessages;

        /// <summary>
        /// Unsafe trusted-co-op compatibility option for client-originated generic Add Item calls.
        /// Runtime-item payloads remain server-only even when this option is enabled.
        /// </summary>
        public bool AllowUnvalidatedOwnedClientAdds
        {
            get => m_AllowUnvalidatedOwnedClientAdds;
            set => m_AllowUnvalidatedOwnedClientAdds = value;
        }

        /// <summary>Maximum server-authorized interaction distance for world bags and legacy drops.</summary>
        public float MaxWorldInteractionDistance => Mathf.Max(0.1f, m_MaxWorldInteractionDistance);

        // ════════════════════════════════════════════════════════════════════════════════════════
        // UNITY LIFECYCLE
        // ════════════════════════════════════════════════════════════════════════════════════════
        private void OnEnable()
        {
            SecurityIntegration.SetModuleServerContext("Inventory", m_IsServer);
            SecurityIntegration.EnsureSecurityManagerInitialized(m_IsServer, ResolveSecurityTimeProvider);
            SyncPatchHooks();
        }

        private void OnDisable()
        {
            SecurityIntegration.SetModuleServerContext("Inventory", false);
            CancelPendingSemanticTransactions();
            m_PendingPersistentState.Clear();
            m_PendingPersistentOverflow.Clear();
            if (m_PatchHooks != null)
            {
                m_PatchHooks.Shutdown();
            }
        }


        // ════════════════════════════════════════════════════════════════════════════════════════
        // REGISTRATION
        // ════════════════════════════════════════════════════════════════════════════════════════

        public void RegisterController(uint networkId, NetworkInventoryController controller)
        {
            if (controller == null) return;
            m_Controllers[networkId] = controller;
            RegisterOwnedEntityMapping(networkId);
            FlushPendingPersistentState(networkId, controller);

            if (m_LogNetworkMessages)
                Debug.Log($"[NetworkInventoryManager] Registered inventory controller: NetworkId={networkId}");
        }

        public void UnregisterController(uint networkId)
        {
            bool removed = m_Controllers.Remove(networkId);
            if (removed)
            {
                SecurityIntegration.UnregisterEntity(networkId);
            }

            if (removed && m_LogNetworkMessages)
                Debug.Log($"[NetworkInventoryManager] Unregistered inventory controller: NetworkId={networkId}");
        }

        public NetworkInventoryController GetController(uint networkId)
        {
            return m_Controllers.TryGetValue(networkId, out var controller) ? controller : null;
        }

        private NetworkInventoryController GetControllerOrFallback(uint networkId, string operation)
        {
            NetworkInventoryController controller = GetController(networkId);
            if (controller != null) return controller;

            foreach (var entry in m_Controllers)
            {
                if (entry.Value == null) continue;

                Debug.LogWarning(
                    $"[NetworkInventoryPickupDebug][Manager] {operation} using fallback controller because bag={networkId} is not registered locally. fallbackBag={entry.Key}");
                return entry.Value;
            }

            Debug.LogWarning(
                $"[NetworkInventoryPickupDebug][Manager] {operation} ignored because bag={networkId} is not registered locally and no fallback controller exists");
            return null;
        }

        public void RegisterMerchantController(uint networkId, NetworkMerchantController controller)
        {
            if (controller == null) return;
            m_MerchantControllers[networkId] = controller;
        }

        public void UnregisterMerchantController(uint networkId)
        {
            m_MerchantControllers.Remove(networkId);
        }

        public NetworkMerchantController GetMerchantController(uint networkId)
        {
            return m_MerchantControllers.TryGetValue(networkId, out var controller) ? controller : null;
        }

        // ════════════════════════════════════════════════════════════════════════════════════════
        // CLIENT → SERVER: SENDING REQUESTS
        // ════════════════════════════════════════════════════════════════════════════════════════

        #region Send Requests

        public void SendContentAddRequest(NetworkContentAddRequest request)
        {
            if (m_LogNetworkMessages)
                Debug.Log($"[NetworkInventoryManager] Sending add request: RequestId={request.RequestId}");
            OnSendContentAddRequest?.Invoke(request);
        }

        public void SendContentRemoveRequest(NetworkContentRemoveRequest request)
        {
            if (m_LogNetworkMessages)
                Debug.Log($"[NetworkInventoryManager] Sending remove request: RequestId={request.RequestId}");
            OnSendContentRemoveRequest?.Invoke(request);
        }

        public void SendContentMoveRequest(NetworkContentMoveRequest request)
        {
            if (m_LogNetworkMessages)
                Debug.Log($"[NetworkInventoryManager] Sending move request: RequestId={request.RequestId}");
            OnSendContentMoveRequest?.Invoke(request);
        }

        public void SendContentUseRequest(NetworkContentUseRequest request)
        {
            if (m_LogNetworkMessages)
                Debug.Log($"[NetworkInventoryManager] Sending use request: RequestId={request.RequestId}");
            OnSendContentUseRequest?.Invoke(request);
        }

        public void SendContentDropRequest(NetworkContentDropRequest request)
        {
            if (m_LogNetworkMessages)
                Debug.Log($"[NetworkInventoryManager] Sending drop request: RequestId={request.RequestId}");
            OnSendContentDropRequest?.Invoke(request);
        }

        public void SendEquipmentRequest(NetworkEquipmentRequest request)
        {
            if (m_LogNetworkMessages)
                Debug.Log($"[NetworkInventoryManager] Sending equipment request: RequestId={request.RequestId}, Action={request.Action}");
            OnSendEquipmentRequest?.Invoke(request);
        }

        public void SendSocketRequest(NetworkSocketRequest request)
        {
            if (m_LogNetworkMessages)
                Debug.Log($"[NetworkInventoryManager] Sending socket request: RequestId={request.RequestId}, Action={request.Action}");
            OnSendSocketRequest?.Invoke(request);
        }

        public void SendWealthRequest(NetworkWealthRequest request)
        {
            if (m_LogNetworkMessages)
                Debug.Log($"[NetworkInventoryManager] Sending wealth request: RequestId={request.RequestId}, Action={request.Action}");
            OnSendWealthRequest?.Invoke(request);
        }

        public void SendMerchantRequest(NetworkMerchantRequest request)
        {
            if (m_LogNetworkMessages)
                Debug.Log($"[NetworkInventoryManager] Sending merchant request: RequestId={request.RequestId}, Action={request.Action}");
            OnSendMerchantRequest?.Invoke(request);
        }

        public void SendCraftingRequest(NetworkCraftingRequest request)
        {
            if (m_LogNetworkMessages)
                Debug.Log($"[NetworkInventoryManager] Sending crafting request: RequestId={request.RequestId}, Action={request.Action}");
            OnSendCraftingRequest?.Invoke(request);
        }

        public void SendTransferRequest(NetworkTransferRequest request)
        {
            if (m_LogNetworkMessages)
                Debug.Log($"[NetworkInventoryManager] Sending transfer request: RequestId={request.RequestId}");
            OnSendTransferRequest?.Invoke(request);
        }

        public void SendPickupRequest(NetworkPickupRequest request)
        {
            if (m_LogNetworkMessages)
            {
                Debug.Log(
                    $"[NetworkInventoryPickupDebug][Manager] send pickup request req={request.RequestId} actor={request.ActorNetworkId} pickerBag={request.PickerBagNetworkId} sourceBag={request.SourceBagNetworkId} runtime={request.RuntimeIdHash} destination={request.DestinationPosition}");
            }
            OnSendPickupRequest?.Invoke(request);
        }

        public void SendCombineRequest(NetworkCombineRequest request)
        {
            if (m_LogNetworkMessages)
                Debug.Log($"[NetworkInventoryManager] Sending combine request: RequestId={request.RequestId}");
            OnSendCombineRequest?.Invoke(request);
        }

        #endregion

        // ════════════════════════════════════════════════════════════════════════════════════════
        // SERVER: RECEIVING REQUESTS
        // ════════════════════════════════════════════════════════════════════════════════════════

        #region Receive Requests (Server)

        private static uint GetSenderClientId(ulong clientId)
        {
            return NetworkTransportBridge.TryConvertSenderClientId(clientId, out uint senderClientId)
                ? senderClientId
                : NetworkTransportBridge.InvalidClientId;
        }

        private static NetworkRequestContext BuildContext(uint actorNetworkId, uint correlationId)
        {
            return NetworkRequestContext.Create(actorNetworkId, correlationId);
        }

        private static InventoryRejectionReason GetSecurityRejection(uint actorNetworkId, uint correlationId)
        {
            return SecurityIntegration.IsProtocolContextMismatch(actorNetworkId, correlationId)
                ? InventoryRejectionReason.ProtocolMismatch
                : InventoryRejectionReason.SecurityViolation;
        }

        private void RegisterOwnedEntityMapping(uint entityNetworkId)
        {
            if (!m_IsServer || entityNetworkId == 0) return;

            SecurityIntegration.RegisterEntityActor(entityNetworkId, entityNetworkId);

            var bridge = NetworkTransportBridge.Active;
            if (bridge != null &&
                bridge.TryGetCharacterOwner(entityNetworkId, out uint ownerClientId) &&
                NetworkTransportBridge.IsValidClientId(ownerClientId))
            {
                SecurityIntegration.RegisterEntityOwner(entityNetworkId, ownerClientId);
            }
        }

        private void RefreshOwnedEntityMappings()
        {
            foreach (var kvp in m_Controllers)
            {
                RegisterOwnedEntityMapping(kvp.Key);
            }
        }

        private bool ValidateTargetOwnership(uint senderClientId, uint actorNetworkId, uint targetBagNetworkId, string requestType)
        {
            NetworkInventoryController targetController = GetController(targetBagNetworkId);
            if (targetController != null && targetController.IsWorldInventory)
            {
                NetworkInventoryController actorController = GetController(actorNetworkId);
                if (actorController == null || !actorController.UsesNetworkCharacterId)
                {
                    return false;
                }

                return IsWithinWorldInteractionRange(actorController, targetController.transform.position);
            }

            return SecurityIntegration.ValidateTargetEntityOwnership(
                senderClientId,
                actorNetworkId,
                targetBagNetworkId,
                "Inventory",
                requestType);
        }

        internal bool IsWithinWorldInteractionRange(
            NetworkInventoryController actorController,
            Vector3 worldPosition)
        {
            if (actorController == null || !actorController.UsesNetworkCharacterId) return false;
            float maxDistance = MaxWorldInteractionDistance;
            return (actorController.transform.position - worldPosition).sqrMagnitude <=
                   maxDistance * maxDistance;
        }

        private static float ResolveSecurityTimeProvider()
        {
            var bridge = NetworkTransportBridge.Active;
            return bridge != null && bridge.IsServer ? bridge.ServerTime : Time.time;
        }

        private void SyncPatchHooks()
        {
            if (m_PatchHooks == null)
            {
                m_PatchHooks = GetComponent<NetworkInventoryPatchHooks>();
                if (m_PatchHooks == null)
                {
                    m_PatchHooks = gameObject.AddComponent<NetworkInventoryPatchHooks>();
                }
            }

            // Semantic hooks must exist on every peer. The resolved bag/controller role decides
            // whether a call executes locally, becomes a request, or is rejected as a proxy write.
            m_PatchHooks.Initialize(m_IsServer);
        }

        public void ReceiveContentAddRequest(NetworkContentAddRequest request, ulong clientId)
        {
            if (!m_IsServer) return;
            uint senderClientId = GetSenderClientId(clientId);
            if (!SecurityIntegration.ValidateModuleRequest(
                    senderClientId,
                    BuildContext(request.ActorNetworkId, request.CorrelationId),
                    "Inventory",
                    nameof(NetworkContentAddRequest)))
            {
                SendContentAddResponse(senderClientId, new NetworkContentAddResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Authorized = false,
                    RejectionReason = GetSecurityRejection(request.ActorNetworkId, request.CorrelationId)
                });
                return;
            }
            if (!ValidateTargetOwnership(senderClientId, request.ActorNetworkId, request.TargetBagNetworkId, nameof(NetworkContentAddRequest)))
            {
                SendContentAddResponse(senderClientId, new NetworkContentAddResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Authorized = false,
                    RejectionReason = InventoryRejectionReason.SecurityViolation
                });
                return;
            }
            if (!CheckRateLimit(clientId))
            {
                SendContentAddResponse(senderClientId, new NetworkContentAddResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Authorized = false,
                    RejectionReason = InventoryRejectionReason.RateLimitExceeded
                });
                return;
            }

            try
            {
                var controller = GetController(request.TargetBagNetworkId);
                if (controller == null)
                {
                    SendContentAddResponse(senderClientId, new NetworkContentAddResponse
                    {
                        RequestId = request.RequestId,
                        ActorNetworkId = request.ActorNetworkId,
                        CorrelationId = request.CorrelationId,
                        Authorized = false,
                        RejectionReason = InventoryRejectionReason.BagNotFound
                    });
                    return;
                }

                if (request.RuntimeItem.ItemHash != 0 || request.ItemHash == 0)
                {
                    SendContentAddResponse(senderClientId, new NetworkContentAddResponse
                    {
                        RequestId = request.RequestId,
                        ActorNetworkId = request.ActorNetworkId,
                        CorrelationId = request.CorrelationId,
                        Authorized = false,
                        RejectionReason = InventoryRejectionReason.SecurityViolation
                    });
                    return;
                }

                if (CustomAddValidator != null)
                {
                    var validation = CustomAddValidator(request, senderClientId);
                    if (!validation.allowed)
                    {
                        SendContentAddResponse(senderClientId, new NetworkContentAddResponse
                        {
                            RequestId = request.RequestId,
                            ActorNetworkId = request.ActorNetworkId,
                            CorrelationId = request.CorrelationId,
                            Authorized = false,
                            RejectionReason = validation.reason
                        });
                        return;
                    }
                }
                else if (!m_AllowUnvalidatedOwnedClientAdds)
                {
                    SendContentAddResponse(senderClientId, new NetworkContentAddResponse
                    {
                        RequestId = request.RequestId,
                        ActorNetworkId = request.ActorNetworkId,
                        CorrelationId = request.CorrelationId,
                        Authorized = false,
                        RejectionReason = InventoryRejectionReason.NotAuthorized
                    });
                    return;
                }

                var response = controller.ProcessContentAddRequest(request, senderClientId);
                response.ActorNetworkId = request.ActorNetworkId;
                response.CorrelationId = request.CorrelationId;
                SendContentAddResponse(senderClientId, response);
            }
            finally
            {
                DecrementPendingRequests(clientId);
            }
        }

        public void ReceiveContentRemoveRequest(NetworkContentRemoveRequest request, ulong clientId)
        {
            if (!m_IsServer) return;
            uint senderClientId = GetSenderClientId(clientId);
            if (!SecurityIntegration.ValidateModuleRequest(
                    senderClientId,
                    BuildContext(request.ActorNetworkId, request.CorrelationId),
                    "Inventory",
                    nameof(NetworkContentRemoveRequest)))
            {
                SendContentRemoveResponse(senderClientId, new NetworkContentRemoveResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Authorized = false,
                    RejectionReason = GetSecurityRejection(request.ActorNetworkId, request.CorrelationId)
                });
                return;
            }
            if (!ValidateTargetOwnership(senderClientId, request.ActorNetworkId, request.TargetBagNetworkId, nameof(NetworkContentRemoveRequest)))
            {
                SendContentRemoveResponse(senderClientId, new NetworkContentRemoveResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Authorized = false,
                    RejectionReason = InventoryRejectionReason.SecurityViolation
                });
                return;
            }
            if (!CheckRateLimit(clientId))
            {
                SendContentRemoveResponse(senderClientId, new NetworkContentRemoveResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Authorized = false,
                    RejectionReason = InventoryRejectionReason.RateLimitExceeded
                });
                return;
            }

            try
            {
                var controller = GetController(request.TargetBagNetworkId);
                if (controller == null)
                {
                    SendContentRemoveResponse(senderClientId, new NetworkContentRemoveResponse
                    {
                        RequestId = request.RequestId,
                        ActorNetworkId = request.ActorNetworkId,
                        CorrelationId = request.CorrelationId,
                        Authorized = false,
                        RejectionReason = InventoryRejectionReason.BagNotFound
                    });
                    return;
                }

                if (CustomRemoveValidator != null)
                {
                    var validation = CustomRemoveValidator(request, senderClientId);
                    if (!validation.allowed)
                    {
                        SendContentRemoveResponse(senderClientId, new NetworkContentRemoveResponse
                        {
                            RequestId = request.RequestId,
                            ActorNetworkId = request.ActorNetworkId,
                            CorrelationId = request.CorrelationId,
                            Authorized = false,
                            RejectionReason = validation.reason
                        });
                        return;
                    }
                }

                var response = controller.ProcessContentRemoveRequest(request, senderClientId);
                response.ActorNetworkId = request.ActorNetworkId;
                response.CorrelationId = request.CorrelationId;
                SendContentRemoveResponse(senderClientId, response);
            }
            finally
            {
                DecrementPendingRequests(clientId);
            }
        }

        public void ReceiveContentMoveRequest(NetworkContentMoveRequest request, ulong clientId)
        {
            if (!m_IsServer) return;
            uint senderClientId = GetSenderClientId(clientId);
            if (!SecurityIntegration.ValidateModuleRequest(
                    senderClientId,
                    BuildContext(request.ActorNetworkId, request.CorrelationId),
                    "Inventory",
                    nameof(NetworkContentMoveRequest)))
            {
                SendContentMoveResponse(senderClientId, new NetworkContentMoveResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Authorized = false,
                    RejectionReason = GetSecurityRejection(request.ActorNetworkId, request.CorrelationId)
                });
                return;
            }
            if (!ValidateTargetOwnership(senderClientId, request.ActorNetworkId, request.TargetBagNetworkId, nameof(NetworkContentMoveRequest)))
            {
                SendContentMoveResponse(senderClientId, new NetworkContentMoveResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Authorized = false,
                    RejectionReason = InventoryRejectionReason.SecurityViolation
                });
                return;
            }
            if (!CheckRateLimit(clientId))
            {
                SendContentMoveResponse(senderClientId, new NetworkContentMoveResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Authorized = false,
                    RejectionReason = InventoryRejectionReason.RateLimitExceeded
                });
                return;
            }

            try
            {
                var controller = GetController(request.TargetBagNetworkId);
                if (controller == null)
                {
                    SendContentMoveResponse(senderClientId, new NetworkContentMoveResponse
                    {
                        RequestId = request.RequestId,
                        ActorNetworkId = request.ActorNetworkId,
                        CorrelationId = request.CorrelationId,
                        Authorized = false,
                        RejectionReason = InventoryRejectionReason.BagNotFound
                    });
                    return;
                }

                var response = controller.ProcessContentMoveRequest(request, senderClientId);
                response.ActorNetworkId = request.ActorNetworkId;
                response.CorrelationId = request.CorrelationId;
                SendContentMoveResponse(senderClientId, response);
            }
            finally
            {
                DecrementPendingRequests(clientId);
            }
        }

        public void ReceiveContentUseRequest(NetworkContentUseRequest request, ulong clientId)
        {
            if (!m_IsServer) return;
            uint senderClientId = GetSenderClientId(clientId);
            if (!SecurityIntegration.ValidateModuleRequest(
                    senderClientId,
                    BuildContext(request.ActorNetworkId, request.CorrelationId),
                    "Inventory",
                    nameof(NetworkContentUseRequest)))
            {
                SendContentUseResponse(senderClientId, new NetworkContentUseResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Authorized = false,
                    RejectionReason = GetSecurityRejection(request.ActorNetworkId, request.CorrelationId)
                });
                return;
            }
            if (!ValidateTargetOwnership(senderClientId, request.ActorNetworkId, request.TargetBagNetworkId, nameof(NetworkContentUseRequest)))
            {
                SendContentUseResponse(senderClientId, new NetworkContentUseResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Authorized = false,
                    RejectionReason = InventoryRejectionReason.SecurityViolation
                });
                return;
            }
            if (!CheckRateLimit(clientId))
            {
                SendContentUseResponse(senderClientId, new NetworkContentUseResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Authorized = false,
                    RejectionReason = InventoryRejectionReason.RateLimitExceeded
                });
                return;
            }

            try
            {
                var controller = GetController(request.TargetBagNetworkId);
                if (controller == null)
                {
                    SendContentUseResponse(senderClientId, new NetworkContentUseResponse
                    {
                        RequestId = request.RequestId,
                        ActorNetworkId = request.ActorNetworkId,
                        CorrelationId = request.CorrelationId,
                        Authorized = false,
                        RejectionReason = InventoryRejectionReason.BagNotFound
                    });
                    return;
                }

                var response = controller.ProcessContentUseRequest(request, senderClientId);
                response.ActorNetworkId = request.ActorNetworkId;
                response.CorrelationId = request.CorrelationId;
                SendContentUseResponse(senderClientId, response);
            }
            finally
            {
                DecrementPendingRequests(clientId);
            }
        }

        public void ReceiveContentDropRequest(NetworkContentDropRequest request, ulong clientId)
        {
            if (!m_IsServer) return;
            uint senderClientId = GetSenderClientId(clientId);
            if (!SecurityIntegration.ValidateModuleRequest(
                    senderClientId,
                    BuildContext(request.ActorNetworkId, request.CorrelationId),
                    "Inventory",
                    nameof(NetworkContentDropRequest)))
            {
                SendContentDropResponse(senderClientId, new NetworkContentDropResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Authorized = false,
                    RejectionReason = GetSecurityRejection(request.ActorNetworkId, request.CorrelationId)
                });
                return;
            }
            if (!ValidateTargetOwnership(senderClientId, request.ActorNetworkId, request.TargetBagNetworkId, nameof(NetworkContentDropRequest)))
            {
                SendContentDropResponse(senderClientId, new NetworkContentDropResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Authorized = false,
                    RejectionReason = InventoryRejectionReason.SecurityViolation
                });
                return;
            }
            if (!CheckRateLimit(clientId))
            {
                SendContentDropResponse(senderClientId, new NetworkContentDropResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Authorized = false,
                    RejectionReason = InventoryRejectionReason.RateLimitExceeded
                });
                return;
            }

            try
            {
                var controller = GetController(request.TargetBagNetworkId);
                if (controller == null)
                {
                    SendContentDropResponse(senderClientId, new NetworkContentDropResponse
                    {
                        RequestId = request.RequestId,
                        ActorNetworkId = request.ActorNetworkId,
                        CorrelationId = request.CorrelationId,
                        Authorized = false,
                        RejectionReason = InventoryRejectionReason.BagNotFound
                    });
                    return;
                }

                var response = controller.ProcessContentDropRequest(request, senderClientId);
                response.ActorNetworkId = request.ActorNetworkId;
                response.CorrelationId = request.CorrelationId;
                SendContentDropResponse(senderClientId, response);
            }
            finally
            {
                DecrementPendingRequests(clientId);
            }
        }

        public async Task ReceiveEquipmentRequest(NetworkEquipmentRequest request, ulong clientId)
        {
            if (!m_IsServer) return;
            uint senderClientId = GetSenderClientId(clientId);
            if (!SecurityIntegration.ValidateModuleRequest(
                    senderClientId,
                    BuildContext(request.ActorNetworkId, request.CorrelationId),
                    "Inventory",
                    nameof(NetworkEquipmentRequest)))
            {
                SendEquipmentResponse(senderClientId, new NetworkEquipmentResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Authorized = false,
                    RejectionReason = GetSecurityRejection(request.ActorNetworkId, request.CorrelationId)
                });
                return;
            }
            if (!ValidateTargetOwnership(senderClientId, request.ActorNetworkId, request.TargetBagNetworkId, nameof(NetworkEquipmentRequest)))
            {
                SendEquipmentResponse(senderClientId, new NetworkEquipmentResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Authorized = false,
                    RejectionReason = InventoryRejectionReason.SecurityViolation
                });
                return;
            }
            if (!CheckRateLimit(clientId))
            {
                SendEquipmentResponse(senderClientId, new NetworkEquipmentResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Authorized = false,
                    RejectionReason = InventoryRejectionReason.RateLimitExceeded
                });
                return;
            }

            try
            {
                var controller = GetController(request.TargetBagNetworkId);
                if (controller == null)
                {
                    SendEquipmentResponse(senderClientId, new NetworkEquipmentResponse
                    {
                        RequestId = request.RequestId,
                        ActorNetworkId = request.ActorNetworkId,
                        CorrelationId = request.CorrelationId,
                        Authorized = false,
                        RejectionReason = InventoryRejectionReason.BagNotFound
                    });
                    return;
                }

                try
                {
                    var response = await controller.ProcessEquipmentRequest(request, senderClientId);
                    response.ActorNetworkId = request.ActorNetworkId;
                    response.CorrelationId = request.CorrelationId;
                    SendEquipmentResponse(senderClientId, response);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[NetworkInventory] ReceiveEquipmentRequest failed: {ex.Message}\n{ex.StackTrace}");
                    SendEquipmentResponse(senderClientId, new NetworkEquipmentResponse
                    {
                        RequestId = request.RequestId,
                        ActorNetworkId = request.ActorNetworkId,
                        CorrelationId = request.CorrelationId,
                        Authorized = false,
                        RejectionReason = InventoryRejectionReason.InternalError
                    });
                }
            }
            finally
            {
                DecrementPendingRequests(clientId);
            }
        }

        public void ReceiveSocketRequest(NetworkSocketRequest request, ulong clientId)
        {
            if (!m_IsServer) return;
            uint senderClientId = GetSenderClientId(clientId);
            if (!SecurityIntegration.ValidateModuleRequest(
                    senderClientId,
                    BuildContext(request.ActorNetworkId, request.CorrelationId),
                    "Inventory",
                    nameof(NetworkSocketRequest)))
            {
                SendSocketResponse(senderClientId, new NetworkSocketResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Authorized = false,
                    RejectionReason = GetSecurityRejection(request.ActorNetworkId, request.CorrelationId)
                });
                return;
            }
            if (!ValidateTargetOwnership(senderClientId, request.ActorNetworkId, request.TargetBagNetworkId, nameof(NetworkSocketRequest)))
            {
                SendSocketResponse(senderClientId, new NetworkSocketResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Authorized = false,
                    RejectionReason = InventoryRejectionReason.SecurityViolation
                });
                return;
            }
            if (!CheckRateLimit(clientId))
            {
                SendSocketResponse(senderClientId, new NetworkSocketResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Authorized = false,
                    RejectionReason = InventoryRejectionReason.RateLimitExceeded
                });
                return;
            }

            try
            {
                var controller = GetController(request.TargetBagNetworkId);
                if (controller == null)
                {
                    SendSocketResponse(senderClientId, new NetworkSocketResponse
                    {
                        RequestId = request.RequestId,
                        ActorNetworkId = request.ActorNetworkId,
                        CorrelationId = request.CorrelationId,
                        Authorized = false,
                        RejectionReason = InventoryRejectionReason.BagNotFound
                    });
                    return;
                }

                var response = controller.ProcessSocketRequest(request, senderClientId);
                response.ActorNetworkId = request.ActorNetworkId;
                response.CorrelationId = request.CorrelationId;
                SendSocketResponse(senderClientId, response);
            }
            finally
            {
                DecrementPendingRequests(clientId);
            }
        }

        public void ReceiveWealthRequest(NetworkWealthRequest request, ulong clientId)
        {
            if (!m_IsServer) return;
            uint senderClientId = GetSenderClientId(clientId);
            if (!SecurityIntegration.ValidateModuleRequest(
                    senderClientId,
                    BuildContext(request.ActorNetworkId, request.CorrelationId),
                    "Inventory",
                    nameof(NetworkWealthRequest)))
            {
                SendWealthResponse(senderClientId, new NetworkWealthResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Authorized = false,
                    RejectionReason = GetSecurityRejection(request.ActorNetworkId, request.CorrelationId)
                });
                return;
            }
            if (!ValidateTargetOwnership(senderClientId, request.ActorNetworkId, request.TargetBagNetworkId, nameof(NetworkWealthRequest)))
            {
                SendWealthResponse(senderClientId, new NetworkWealthResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Authorized = false,
                    RejectionReason = InventoryRejectionReason.SecurityViolation
                });
                return;
            }

            bool validAction = request.Action == WealthAction.Set ||
                               request.Action == WealthAction.Add ||
                               request.Action == WealthAction.Subtract;
            bool validSource = Enum.IsDefined(typeof(InventoryModificationSource), request.Source);
            bool validOperand = request.Action == WealthAction.Set
                ? request.Value >= 0
                : request.Value > 0;
            if (!validAction || !validSource || !validOperand)
            {
                SendWealthResponse(senderClientId, new NetworkWealthResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Authorized = false,
                    RejectionReason = InventoryRejectionReason.InvalidOperation
                });
                return;
            }

            if (!CheckRateLimit(clientId))
            {
                SendWealthResponse(senderClientId, new NetworkWealthResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Authorized = false,
                    RejectionReason = InventoryRejectionReason.RateLimitExceeded
                });
                return;
            }

            try
            {
                // A generic client wealth mutation is a grant/spend authority boundary. Native
                // merchant/crafting operations execute inside trusted server mutation scopes and
                // never reach this endpoint. Projects that expose other client-originated wealth
                // operations must explicitly validate their source and amount here.
                if (CustomWealthValidator == null)
                {
                    SendWealthResponse(senderClientId, new NetworkWealthResponse
                    {
                        RequestId = request.RequestId,
                        ActorNetworkId = request.ActorNetworkId,
                        CorrelationId = request.CorrelationId,
                        Authorized = false,
                        RejectionReason = InventoryRejectionReason.NotAuthorized
                    });
                    return;
                }

                var validation = CustomWealthValidator(request, senderClientId);
                if (!validation.allowed)
                {
                    SendWealthResponse(senderClientId, new NetworkWealthResponse
                    {
                        RequestId = request.RequestId,
                        ActorNetworkId = request.ActorNetworkId,
                        CorrelationId = request.CorrelationId,
                        Authorized = false,
                        RejectionReason = validation.reason == InventoryRejectionReason.None
                            ? InventoryRejectionReason.NotAuthorized
                            : validation.reason
                    });
                    return;
                }

                var controller = GetController(request.TargetBagNetworkId);
                if (controller == null)
                {
                    SendWealthResponse(senderClientId, new NetworkWealthResponse
                    {
                        RequestId = request.RequestId,
                        ActorNetworkId = request.ActorNetworkId,
                        CorrelationId = request.CorrelationId,
                        Authorized = false,
                        RejectionReason = InventoryRejectionReason.BagNotFound
                    });
                    return;
                }

                var response = controller.ProcessWealthRequest(request, senderClientId);
                response.ActorNetworkId = request.ActorNetworkId;
                response.CorrelationId = request.CorrelationId;
                SendWealthResponse(senderClientId, response);
            }
            finally
            {
                DecrementPendingRequests(clientId);
            }
        }

        public void ReceiveTransferRequest(NetworkTransferRequest request, ulong clientId)
        {
            if (!m_IsServer) return;
            uint senderClientId = GetSenderClientId(clientId);
            NetworkInventoryController sourceController = GetController(request.SourceBagNetworkId);
            NetworkInventoryController destinationController = GetController(request.DestinationBagNetworkId);
            if (m_LogNetworkMessages)
            {
                Debug.Log(
                    $"[NetworkInventoryPickupDebug][Manager] receive transfer request req={request.RequestId} senderConnection={clientId} senderClient={senderClientId} actor={request.ActorNetworkId} sourceBag={request.SourceBagNetworkId} sourceFound={sourceController != null} sourceWorld={(sourceController != null && sourceController.IsWorldInventory)} destinationBag={request.DestinationBagNetworkId} destinationFound={destinationController != null} destinationWorld={(destinationController != null && destinationController.IsWorldInventory)} runtime={request.RuntimeIdHash} destination={request.DestinationPosition}");
            }

            if (!SecurityIntegration.ValidateModuleRequest(
                    senderClientId,
                    BuildContext(request.ActorNetworkId, request.CorrelationId),
                    "Inventory",
                    nameof(NetworkTransferRequest)))
            {
                SendTransferResponse(senderClientId, new NetworkTransferResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Authorized = false,
                    RejectionReason = GetSecurityRejection(request.ActorNetworkId, request.CorrelationId)
                });
                return;
            }

            bool sourceAuthorized = ValidateTargetOwnership(
                senderClientId,
                request.ActorNetworkId,
                request.SourceBagNetworkId,
                nameof(NetworkTransferRequest));

            bool destinationAuthorized = ValidateTargetOwnership(
                senderClientId,
                request.ActorNetworkId,
                request.DestinationBagNetworkId,
                nameof(NetworkTransferRequest));

            if (!sourceAuthorized || !destinationAuthorized)
            {
                Debug.LogWarning(
                    $"[NetworkInventoryPickupDebug][Manager] transfer rejected by ownership req={request.RequestId} senderClient={senderClientId} actor={request.ActorNetworkId} sourceBag={request.SourceBagNetworkId} sourceAuthorized={sourceAuthorized} destinationBag={request.DestinationBagNetworkId} destinationAuthorized={destinationAuthorized}");

                SendTransferResponse(senderClientId, new NetworkTransferResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Authorized = false,
                    RejectionReason = InventoryRejectionReason.SecurityViolation
                });
                return;
            }

            if (!CheckRateLimit(clientId))
            {
                SendTransferResponse(senderClientId, new NetworkTransferResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Authorized = false,
                    RejectionReason = InventoryRejectionReason.RateLimitExceeded
                });
                return;
            }

            try
            {
                NetworkInventoryController source = GetController(request.SourceBagNetworkId);
                NetworkInventoryController destination = GetController(request.DestinationBagNetworkId);
                if (source == null || destination == null)
                {
                    Debug.LogWarning(
                        $"[NetworkInventoryPickupDebug][Manager] transfer rejected bag not found req={request.RequestId} sourceBag={request.SourceBagNetworkId} sourceFound={source != null} destinationBag={request.DestinationBagNetworkId} destinationFound={destination != null}");

                    SendTransferResponse(senderClientId, new NetworkTransferResponse
                    {
                        RequestId = request.RequestId,
                        ActorNetworkId = request.ActorNetworkId,
                        CorrelationId = request.CorrelationId,
                        Authorized = false,
                        RejectionReason = InventoryRejectionReason.BagNotFound
                    });
                    return;
                }

                NetworkTransferResponse response = source.ProcessTransferRequest(request, destination, senderClientId);
                response.ActorNetworkId = request.ActorNetworkId;
                response.CorrelationId = request.CorrelationId;
                SendTransferResponse(senderClientId, response);
            }
            finally
            {
                DecrementPendingRequests(clientId);
            }
        }

        public void ReceivePickupRequest(NetworkPickupRequest request, ulong clientId)
        {
            if (!m_IsServer) return;
            uint senderClientId = GetSenderClientId(clientId);
            if (m_LogNetworkMessages)
            {
                Debug.Log(
                    $"[NetworkInventoryPickupDebug][Manager] receive pickup request req={request.RequestId} senderConnection={clientId} senderClient={senderClientId} actor={request.ActorNetworkId} pickerBag={request.PickerBagNetworkId} sourceBag={request.SourceBagNetworkId} runtime={request.RuntimeIdHash}");
            }
            if (!SecurityIntegration.ValidateModuleRequest(
                    senderClientId,
                    BuildContext(request.ActorNetworkId, request.CorrelationId),
                    "Inventory",
                    nameof(NetworkPickupRequest)))
            {
                Debug.LogWarning(
                    $"[NetworkInventoryPickupDebug][Manager] pickup rejected by security req={request.RequestId} senderClient={senderClientId} actor={request.ActorNetworkId} reason={GetSecurityRejection(request.ActorNetworkId, request.CorrelationId)}");
                SendPickupResponse(senderClientId, new NetworkPickupResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Authorized = false,
                    RejectionReason = GetSecurityRejection(request.ActorNetworkId, request.CorrelationId)
                });
                return;
            }

            if (!ValidateTargetOwnership(senderClientId, request.ActorNetworkId, request.PickerBagNetworkId, nameof(NetworkPickupRequest)))
            {
                Debug.LogWarning(
                    $"[NetworkInventoryPickupDebug][Manager] pickup rejected by ownership req={request.RequestId} senderClient={senderClientId} actor={request.ActorNetworkId} pickerBag={request.PickerBagNetworkId}");
                SendPickupResponse(senderClientId, new NetworkPickupResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Authorized = false,
                    RejectionReason = InventoryRejectionReason.SecurityViolation
                });
                return;
            }

            if (!CheckRateLimit(clientId))
            {
                Debug.LogWarning(
                    $"[NetworkInventoryPickupDebug][Manager] pickup rejected by rate limit req={request.RequestId} senderConnection={clientId}");
                SendPickupResponse(senderClientId, new NetworkPickupResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Authorized = false,
                    RejectionReason = InventoryRejectionReason.RateLimitExceeded
                });
                return;
            }

            try
            {
                NetworkInventoryController picker = GetController(request.PickerBagNetworkId);
                if (picker == null)
                {
                    Debug.LogWarning(
                        $"[NetworkInventoryPickupDebug][Manager] pickup rejected picker bag not found req={request.RequestId} pickerBag={request.PickerBagNetworkId}");
                    SendPickupResponse(senderClientId, new NetworkPickupResponse
                    {
                        RequestId = request.RequestId,
                        ActorNetworkId = request.ActorNetworkId,
                        CorrelationId = request.CorrelationId,
                        Authorized = false,
                        RejectionReason = InventoryRejectionReason.BagNotFound
                    });
                    return;
                }

                if (TryProcessRegisteredPickup(request, senderClientId, out NetworkPickupResponse registeredResponse))
                {
                    SendPickupResponse(senderClientId, registeredResponse);
                    return;
                }

                NetworkPickupResponse response = picker.ProcessPickupRequest(request, senderClientId);
                response.ActorNetworkId = request.ActorNetworkId;
                response.CorrelationId = request.CorrelationId;
                if (m_LogNetworkMessages)
                {
                    Debug.Log(
                        $"[NetworkInventoryPickupDebug][Manager] pickup processed req={request.RequestId} authorized={response.Authorized} reason={response.RejectionReason} senderClient={senderClientId} placed={response.PlacedPosition}");
                }
                SendPickupResponse(senderClientId, response);
            }
            finally
            {
                DecrementPendingRequests(clientId);
            }
        }

        #endregion

        // ════════════════════════════════════════════════════════════════════════════════════════
        // SERVER: SEND RESPONSES
        // ════════════════════════════════════════════════════════════════════════════════════════

        #region Send Responses (Server)

        private void SendContentAddResponse(uint targetNetworkId, NetworkContentAddResponse response)
        {
            if (m_LogNetworkMessages)
                Debug.Log($"[NetworkInventoryManager] Sending add response: RequestId={response.RequestId}, Authorized={response.Authorized}");
            OnSendContentAddResponse?.Invoke(targetNetworkId, response);
        }

        private void SendContentRemoveResponse(uint targetNetworkId, NetworkContentRemoveResponse response)
        {
            if (m_LogNetworkMessages)
                Debug.Log($"[NetworkInventoryManager] Sending remove response: RequestId={response.RequestId}, Authorized={response.Authorized}");
            OnSendContentRemoveResponse?.Invoke(targetNetworkId, response);
        }

        private void SendContentMoveResponse(uint targetNetworkId, NetworkContentMoveResponse response)
        {
            if (m_LogNetworkMessages)
                Debug.Log($"[NetworkInventoryManager] Sending move response: RequestId={response.RequestId}, Authorized={response.Authorized}");
            OnSendContentMoveResponse?.Invoke(targetNetworkId, response);
        }

        private void SendContentUseResponse(uint targetNetworkId, NetworkContentUseResponse response)
        {
            if (m_LogNetworkMessages)
                Debug.Log($"[NetworkInventoryManager] Sending use response: RequestId={response.RequestId}, Authorized={response.Authorized}");
            OnSendContentUseResponse?.Invoke(targetNetworkId, response);
        }

        private void SendContentDropResponse(uint targetNetworkId, NetworkContentDropResponse response)
        {
            if (m_LogNetworkMessages)
                Debug.Log($"[NetworkInventoryManager] Sending drop response: RequestId={response.RequestId}, Authorized={response.Authorized}");
            OnSendContentDropResponse?.Invoke(targetNetworkId, response);
        }

        private void SendEquipmentResponse(uint targetNetworkId, NetworkEquipmentResponse response)
        {
            if (m_LogNetworkMessages)
                Debug.Log($"[NetworkInventoryManager] Sending equipment response: RequestId={response.RequestId}, Authorized={response.Authorized}");
            OnSendEquipmentResponse?.Invoke(targetNetworkId, response);
        }

        private void SendSocketResponse(uint targetNetworkId, NetworkSocketResponse response)
        {
            if (m_LogNetworkMessages)
                Debug.Log($"[NetworkInventoryManager] Sending socket response: RequestId={response.RequestId}, Authorized={response.Authorized}");
            OnSendSocketResponse?.Invoke(targetNetworkId, response);
        }

        private void SendWealthResponse(uint targetNetworkId, NetworkWealthResponse response)
        {
            if (m_LogNetworkMessages)
                Debug.Log($"[NetworkInventoryManager] Sending wealth response: RequestId={response.RequestId}, Authorized={response.Authorized}");
            OnSendWealthResponse?.Invoke(targetNetworkId, response);
        }

        private void SendTransferResponse(uint targetNetworkId, NetworkTransferResponse response)
        {
            if (m_LogNetworkMessages)
                Debug.Log($"[NetworkInventoryManager] Sending transfer response: RequestId={response.RequestId}, Authorized={response.Authorized}");
            OnSendTransferResponse?.Invoke(targetNetworkId, response);
        }

        private void SendPickupResponse(uint targetNetworkId, NetworkPickupResponse response)
        {
            if (m_LogNetworkMessages)
            {
                Debug.Log(
                    $"[NetworkInventoryPickupDebug][Manager] send pickup response req={response.RequestId} target={targetNetworkId} authorized={response.Authorized} reason={response.RejectionReason} placed={response.PlacedPosition}");
            }
            OnSendPickupResponse?.Invoke(targetNetworkId, response);
        }

        #endregion

        // ════════════════════════════════════════════════════════════════════════════════════════
        // SERVER: BROADCASTING
        // ════════════════════════════════════════════════════════════════════════════════════════

        #region Broadcasting (Server)

        public void BroadcastItemAdded(NetworkItemAddedBroadcast broadcast)
        {
            if (!m_IsServer) return;
            if (m_LogNetworkMessages)
                Debug.Log($"[NetworkInventoryManager] Broadcasting item added: BagId={broadcast.BagNetworkId}");
            OnBroadcastItemAdded?.Invoke(broadcast);
        }

        public void BroadcastItemRemoved(NetworkItemRemovedBroadcast broadcast)
        {
            if (!m_IsServer) return;
            if (m_LogNetworkMessages)
                Debug.Log($"[NetworkInventoryManager] Broadcasting item removed: BagId={broadcast.BagNetworkId}");
            OnBroadcastItemRemoved?.Invoke(broadcast);
        }

        public void BroadcastItemDropped(NetworkItemDroppedBroadcast broadcast)
        {
            if (!m_IsServer) return;
            if (m_LogNetworkMessages)
                Debug.Log($"[NetworkInventoryManager] Broadcasting item dropped: BagId={broadcast.SourceBagNetworkId}");
            OnBroadcastItemDropped?.Invoke(broadcast);
        }

        public void BroadcastDroppedItemRemoved(NetworkDroppedItemRemovedBroadcast broadcast)
        {
            if (!m_IsServer) return;
            if (m_LogNetworkMessages)
            {
                Debug.Log(
                    $"[NetworkInventoryPickupDebug][Manager] broadcast dropped item removed sourceBag={broadcast.SourceBagNetworkId} runtime={broadcast.RuntimeIdHash} position={broadcast.Position}");
            }
            OnBroadcastDroppedItemRemoved?.Invoke(broadcast);
        }

        public void BroadcastItemMoved(NetworkItemMovedBroadcast broadcast)
        {
            if (!m_IsServer) return;
            if (m_LogNetworkMessages)
                Debug.Log($"[NetworkInventoryManager] Broadcasting item moved: BagId={broadcast.BagNetworkId}");
            OnBroadcastItemMoved?.Invoke(broadcast);
        }

        public void BroadcastItemUsed(NetworkItemUsedBroadcast broadcast)
        {
            if (!m_IsServer) return;
            if (m_LogNetworkMessages)
                Debug.Log($"[NetworkInventoryManager] Broadcasting item used: BagId={broadcast.BagNetworkId}");
            OnBroadcastItemUsed?.Invoke(broadcast);
        }

        public void BroadcastItemEquipped(NetworkItemEquippedBroadcast broadcast)
        {
            if (!m_IsServer) return;
            if (m_LogNetworkMessages)
                Debug.Log($"[NetworkInventoryManager] Broadcasting item equipped: BagId={broadcast.BagNetworkId}, Index={broadcast.EquipmentIndex}");
            OnBroadcastItemEquipped?.Invoke(broadcast);
        }

        public void BroadcastItemUnequipped(NetworkItemUnequippedBroadcast broadcast)
        {
            if (!m_IsServer) return;
            if (m_LogNetworkMessages)
                Debug.Log($"[NetworkInventoryManager] Broadcasting item unequipped: BagId={broadcast.BagNetworkId}, Index={broadcast.EquipmentIndex}");
            OnBroadcastItemUnequipped?.Invoke(broadcast);
        }

        public void BroadcastSocketChange(NetworkSocketChangeBroadcast broadcast)
        {
            if (!m_IsServer) return;
            if (m_LogNetworkMessages)
                Debug.Log($"[NetworkInventoryManager] Broadcasting socket change: BagId={broadcast.BagNetworkId}");
            OnBroadcastSocketChange?.Invoke(broadcast);
        }

        public void BroadcastWealthChange(NetworkWealthChangeBroadcast broadcast)
        {
            if (!m_IsServer) return;
            if (m_LogNetworkMessages)
                Debug.Log($"[NetworkInventoryManager] Broadcasting wealth change: BagId={broadcast.BagNetworkId}, Change={broadcast.Change}");
            OnBroadcastWealthChange?.Invoke(broadcast);
        }

        public void BroadcastPropertyChange(NetworkPropertyChangeBroadcast broadcast)
        {
            if (!m_IsServer) return;
            if (m_LogNetworkMessages)
                Debug.Log($"[NetworkInventoryManager] Broadcasting property change: BagId={broadcast.BagNetworkId}");
            OnBroadcastPropertyChange?.Invoke(broadcast);
        }

        public void BroadcastFullSnapshot(NetworkInventorySnapshot snapshot)
        {
            if (!m_IsServer) return;
            if (m_LogNetworkMessages)
                Debug.Log($"[NetworkInventoryManager] Broadcasting full snapshot: BagId={snapshot.BagNetworkId}, Cells={snapshot.Cells?.Length ?? 0}");
            OnBroadcastFullSnapshot?.Invoke(snapshot);
        }

        public void BroadcastDelta(NetworkInventoryDelta delta)
        {
            if (!m_IsServer) return;
            if (m_LogNetworkMessages)
                Debug.Log($"[NetworkInventoryManager] Broadcasting delta: BagId={delta.BagNetworkId}");
            OnBroadcastDelta?.Invoke(delta);
        }

        public void SendSnapshotToClient(ulong clientId, NetworkInventorySnapshot snapshot)
        {
            if (!m_IsServer) return;
            if (m_LogNetworkMessages)
                Debug.Log($"[NetworkInventoryManager] Sending snapshot to client {clientId}: BagId={snapshot.BagNetworkId}");
            OnSendSnapshotToClient?.Invoke(clientId, snapshot);
        }

        #endregion

        // ════════════════════════════════════════════════════════════════════════════════════════
        // CLIENT: RECEIVING BROADCASTS
        // ════════════════════════════════════════════════════════════════════════════════════════

        #region Receive Broadcasts (Client)

        public void ReceiveItemAddedBroadcast(NetworkItemAddedBroadcast broadcast)
        {
            var controller = GetController(broadcast.BagNetworkId);
            if (controller != null) controller.ReceiveItemAddedBroadcast(broadcast);
            else QueuePendingPersistentState(broadcast.BagNetworkId, PendingPersistentStateKind.ItemAdded, broadcast);
        }

        public void ReceiveItemRemovedBroadcast(NetworkItemRemovedBroadcast broadcast)
        {
            var controller = GetController(broadcast.BagNetworkId);
            if (controller != null) controller.ReceiveItemRemovedBroadcast(broadcast);
            else QueuePendingPersistentState(broadcast.BagNetworkId, PendingPersistentStateKind.ItemRemoved, broadcast);
        }

        public void ReceiveItemDroppedBroadcast(NetworkItemDroppedBroadcast broadcast)
        {
            var controller = GetControllerOrFallback(broadcast.SourceBagNetworkId, "receive dropped item broadcast");
            controller?.ReceiveItemDroppedBroadcast(broadcast);
        }

        public void ReceiveDroppedItemRemovedBroadcast(NetworkDroppedItemRemovedBroadcast broadcast)
        {
            var controller = GetControllerOrFallback(broadcast.SourceBagNetworkId, "receive dropped item removed broadcast");
            controller?.ReceiveDroppedItemRemovedBroadcast(broadcast);
        }

        public void ReceiveItemMovedBroadcast(NetworkItemMovedBroadcast broadcast)
        {
            var controller = GetController(broadcast.BagNetworkId);
            if (controller != null) controller.ReceiveItemMovedBroadcast(broadcast);
            else QueuePendingPersistentState(broadcast.BagNetworkId, PendingPersistentStateKind.ItemMoved, broadcast);
        }

        public void ReceiveItemUsedBroadcast(NetworkItemUsedBroadcast broadcast)
        {
            var controller = GetController(broadcast.BagNetworkId);
            if (controller != null) controller.ReceiveItemUsedBroadcast(broadcast);
            else QueuePendingPersistentState(broadcast.BagNetworkId, PendingPersistentStateKind.ItemUsed, broadcast);
        }

        public void ReceiveItemEquippedBroadcast(NetworkItemEquippedBroadcast broadcast)
        {
            var controller = GetController(broadcast.BagNetworkId);
            if (controller != null) controller.ReceiveItemEquippedBroadcast(broadcast);
            else QueuePendingPersistentState(broadcast.BagNetworkId, PendingPersistentStateKind.ItemEquipped, broadcast);
        }

        public void ReceiveItemUnequippedBroadcast(NetworkItemUnequippedBroadcast broadcast)
        {
            var controller = GetController(broadcast.BagNetworkId);
            if (controller != null) controller.ReceiveItemUnequippedBroadcast(broadcast);
            else QueuePendingPersistentState(broadcast.BagNetworkId, PendingPersistentStateKind.ItemUnequipped, broadcast);
        }

        public void ReceiveSocketChangeBroadcast(NetworkSocketChangeBroadcast broadcast)
        {
            var controller = GetController(broadcast.BagNetworkId);
            if (controller != null) controller.ReceiveSocketChangeBroadcast(broadcast);
            else QueuePendingPersistentState(broadcast.BagNetworkId, PendingPersistentStateKind.SocketChanged, broadcast);
        }

        public void ReceiveWealthChangeBroadcast(NetworkWealthChangeBroadcast broadcast)
        {
            var controller = GetController(broadcast.BagNetworkId);
            if (controller != null) controller.ReceiveWealthChangeBroadcast(broadcast);
            else QueuePendingPersistentState(broadcast.BagNetworkId, PendingPersistentStateKind.WealthChanged, broadcast);
        }

        public void ReceivePropertyChangeBroadcast(NetworkPropertyChangeBroadcast broadcast)
        {
            var controller = GetController(broadcast.BagNetworkId);
            if (controller != null) controller.ReceivePropertyChangeBroadcast(broadcast);
            else QueuePendingPersistentState(broadcast.BagNetworkId, PendingPersistentStateKind.PropertyChanged, broadcast);
        }

        public void ReceiveFullSnapshot(NetworkInventorySnapshot snapshot)
        {
            var controller = GetController(snapshot.BagNetworkId);
            if (controller == null)
            {
                QueuePendingPersistentState(snapshot.BagNetworkId, PendingPersistentStateKind.Snapshot, snapshot);
                return;
            }

            controller.ReceiveFullSnapshot(snapshot);
        }

        public void ReceiveDelta(NetworkInventoryDelta delta)
        {
            var controller = GetController(delta.BagNetworkId);
            if (controller != null) controller.ReceiveDelta(delta);
            else QueuePendingPersistentState(delta.BagNetworkId, PendingPersistentStateKind.Delta, delta);
        }

        private void QueuePendingPersistentState(
            uint bagNetworkId,
            PendingPersistentStateKind kind,
            object payload)
        {
            if (bagNetworkId == 0 || payload == null) return;
            if (kind == PendingPersistentStateKind.ItemUsed &&
                payload is NetworkItemUsedBroadcast transientUse &&
                !transientUse.WasConsumed)
            {
                QueuePendingTransientUse(bagNetworkId, transientUse);
                return;
            }

            uint stateVersion = GetPendingPersistentStateVersion(kind, payload);

            if (kind == PendingPersistentStateKind.Snapshot)
            {
                // A targeted snapshot is a replacement baseline. Never let an older snapshot
                // erase newer ordered mutations which arrived while the controller was absent.
                if (stateVersion != 0 &&
                    m_PendingPersistentState.TryGetValue(bagNetworkId, out List<PendingPersistentState> existing))
                {
                    for (int i = 0; i < existing.Count; i++)
                    {
                        PendingPersistentState queued = existing[i];
                        if (queued.Kind != PendingPersistentStateKind.Snapshot ||
                            queued.StateVersion == 0) continue;
                        if (!IsNewerStateVersion(stateVersion, queued.StateVersion)) return;
                    }
                }

                m_PendingPersistentOverflow.Remove(bagNetworkId);
                if (!m_PendingPersistentState.TryGetValue(bagNetworkId, out List<PendingPersistentState> replacement))
                {
                    EnsurePendingPersistentBagCapacity(bagNetworkId);
                    replacement = new List<PendingPersistentState>(8);
                    m_PendingPersistentState[bagNetworkId] = replacement;
                }
                RemoveExpiredPendingTransientUses(replacement);

                if (stateVersion == 0)
                {
                    // Replace only persistent state. A recent non-consuming Use is a transient
                    // event and must survive snapshot baseline replacement.
                    for (int i = replacement.Count - 1; i >= 0; i--)
                    {
                        if (!IsPendingTransientUse(replacement[i])) replacement.RemoveAt(i);
                    }
                    replacement.Insert(0, new PendingPersistentState(kind, payload, 0));
                    return;
                }

                for (int i = replacement.Count - 1; i >= 0; i--)
                {
                    uint queuedVersion = replacement[i].StateVersion;
                    if (queuedVersion == 0)
                    {
                        if (!IsPendingTransientUse(replacement[i])) replacement.RemoveAt(i);
                        continue;
                    }
                    if (!IsNewerStateVersion(queuedVersion, stateVersion))
                        replacement.RemoveAt(i);
                }
                replacement.Insert(0, new PendingPersistentState(kind, payload, stateVersion));
                return;
            }

            if (m_PendingPersistentOverflow.Contains(bagNetworkId)) return;

            if (!m_PendingPersistentState.TryGetValue(bagNetworkId, out List<PendingPersistentState> pending))
            {
                EnsurePendingPersistentBagCapacity(bagNetworkId);
                pending = new List<PendingPersistentState>(8);
                m_PendingPersistentState[bagNetworkId] = pending;
            }

            if (stateVersion != 0)
            {
                int insertionIndex = pending.Count;
                for (int i = 0; i < pending.Count; i++)
                {
                    PendingPersistentState queued = pending[i];
                    if (queued.StateVersion == 0) continue;

                    if (queued.StateVersion == stateVersion)
                    {
                        // A snapshot is already complete for this revision. A delta is the next
                        // most complete representation and may replace a primitive mutation.
                        if (queued.Kind == PendingPersistentStateKind.Snapshot) return;
                        if (kind == PendingPersistentStateKind.Delta &&
                            queued.Kind != PendingPersistentStateKind.Delta)
                        {
                            pending[i] = new PendingPersistentState(kind, payload, stateVersion);
                        }
                        return;
                    }

                    if (queued.Kind == PendingPersistentStateKind.Snapshot &&
                        !IsNewerStateVersion(stateVersion, queued.StateVersion))
                    {
                        return;
                    }

                    if (IsNewerStateVersion(queued.StateVersion, stateVersion))
                    {
                        insertionIndex = i;
                        break;
                    }
                }

                int versionedMaximum = Mathf.Max(4, m_MaxPendingPersistentMessagesPerBag);
                while (pending.Count >= versionedMaximum &&
                       RemoveOldestPendingTransientUse(pending))
                {
                }
                if (pending.Count >= versionedMaximum)
                {
                    pending.Clear();
                    m_PendingPersistentOverflow.Add(bagNetworkId);
                    Debug.LogWarning(
                        $"[NetworkInventoryManager] Persistent state queue overflowed for bag={bagNetworkId}. " +
                        "A targeted resync will be requested when its controller registers.");
                    return;
                }

                pending.Insert(insertionIndex,
                    new PendingPersistentState(kind, payload, stateVersion));
                return;
            }

            int maximum = Mathf.Max(4, m_MaxPendingPersistentMessagesPerBag);
            while (pending.Count >= maximum && RemoveOldestPendingTransientUse(pending))
            {
            }
            if (pending.Count >= maximum)
            {
                pending.Clear();
                m_PendingPersistentOverflow.Add(bagNetworkId);
                Debug.LogWarning(
                    $"[NetworkInventoryManager] Persistent state queue overflowed for bag={bagNetworkId}. " +
                    "A targeted resync will be requested when its controller registers.");
                return;
            }

            pending.Add(new PendingPersistentState(kind, payload, 0));
        }

        private void QueuePendingTransientUse(
            uint bagNetworkId,
            NetworkItemUsedBroadcast broadcast)
        {
            // A transient Use must never displace a persistent bag revision or turn a cosmetic
            // queue overflow into a state resync. It is retained briefly only for spawn-order races.
            if (m_PendingPersistentOverflow.Contains(bagNetworkId)) return;

            if (!m_PendingPersistentState.TryGetValue(
                    bagNetworkId,
                    out List<PendingPersistentState> pending))
            {
                EnsurePendingPersistentBagCapacity(bagNetworkId);
                pending = new List<PendingPersistentState>(8);
                m_PendingPersistentState[bagNetworkId] = pending;
            }
            RemoveExpiredPendingTransientUses(pending);

            int oldestTransientIndex = -1;
            float oldestTransientTime = float.MaxValue;
            for (int i = 0; i < pending.Count; i++)
            {
                PendingPersistentState queued = pending[i];
                if (queued.Kind != PendingPersistentStateKind.ItemUsed ||
                    queued.Payload is not NetworkItemUsedBroadcast queuedUse ||
                    queuedUse.WasConsumed)
                {
                    continue;
                }

                if (queuedUse.RuntimeIdHash == broadcast.RuntimeIdHash &&
                    queuedUse.StateVersion == broadcast.StateVersion)
                {
                    return;
                }

                if (queued.QueuedRealtime < oldestTransientTime)
                {
                    oldestTransientIndex = i;
                    oldestTransientTime = queued.QueuedRealtime;
                }
            }

            int maximum = Mathf.Max(4, m_MaxPendingPersistentMessagesPerBag);
            if (pending.Count >= maximum)
            {
                if (oldestTransientIndex < 0) return;
                pending.RemoveAt(oldestTransientIndex);
            }

            pending.Add(new PendingPersistentState(
                PendingPersistentStateKind.ItemUsed,
                broadcast,
                0,
                Time.realtimeSinceStartup));
        }

        private static bool IsPendingTransientUse(PendingPersistentState state)
        {
            return state.Kind == PendingPersistentStateKind.ItemUsed &&
                   state.Payload is NetworkItemUsedBroadcast use &&
                   !use.WasConsumed;
        }

        private static void RemoveExpiredPendingTransientUses(
            List<PendingPersistentState> pending)
        {
            float now = Time.realtimeSinceStartup;
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                PendingPersistentState state = pending[i];
                if (!IsPendingTransientUse(state) || state.QueuedRealtime <= 0f) continue;
                if (now - state.QueuedRealtime > PENDING_TRANSIENT_USE_TTL_SECONDS)
                {
                    pending.RemoveAt(i);
                }
            }
        }

        private static bool RemoveOldestPendingTransientUse(
            List<PendingPersistentState> pending)
        {
            int oldestIndex = -1;
            float oldestTime = float.MaxValue;
            for (int i = 0; i < pending.Count; i++)
            {
                PendingPersistentState state = pending[i];
                if (!IsPendingTransientUse(state)) continue;
                if (state.QueuedRealtime >= oldestTime) continue;
                oldestIndex = i;
                oldestTime = state.QueuedRealtime;
            }

            if (oldestIndex < 0) return false;
            pending.RemoveAt(oldestIndex);
            return true;
        }

        private static bool IsNewerStateVersion(uint candidate, uint baseline)
        {
            return candidate != baseline && unchecked((int)(candidate - baseline)) > 0;
        }

        private static uint GetPendingPersistentStateVersion(
            PendingPersistentStateKind kind,
            object payload)
        {
            return kind switch
            {
                PendingPersistentStateKind.ItemAdded => ((NetworkItemAddedBroadcast)payload).StateVersion,
                PendingPersistentStateKind.ItemRemoved => ((NetworkItemRemovedBroadcast)payload).StateVersion,
                PendingPersistentStateKind.ItemMoved => ((NetworkItemMovedBroadcast)payload).StateVersion,
                PendingPersistentStateKind.ItemUsed =>
                    ((NetworkItemUsedBroadcast)payload).WasConsumed
                        ? ((NetworkItemUsedBroadcast)payload).StateVersion
                        : 0u,
                PendingPersistentStateKind.ItemEquipped => ((NetworkItemEquippedBroadcast)payload).StateVersion,
                PendingPersistentStateKind.ItemUnequipped => ((NetworkItemUnequippedBroadcast)payload).StateVersion,
                PendingPersistentStateKind.SocketChanged => ((NetworkSocketChangeBroadcast)payload).StateVersion,
                PendingPersistentStateKind.WealthChanged => ((NetworkWealthChangeBroadcast)payload).StateVersion,
                PendingPersistentStateKind.PropertyChanged => ((NetworkPropertyChangeBroadcast)payload).StateVersion,
                PendingPersistentStateKind.Snapshot => ((NetworkInventorySnapshot)payload).StateVersion,
                PendingPersistentStateKind.Delta => ((NetworkInventoryDelta)payload).StateVersion,
                _ => 0
            };
        }

        private void EnsurePendingPersistentBagCapacity(uint incomingBagNetworkId)
        {
            int maximum = Mathf.Max(4, m_MaxPendingPersistentBags);
            if (m_PendingPersistentState.ContainsKey(incomingBagNetworkId) ||
                m_PendingPersistentState.Count < maximum)
            {
                return;
            }

            uint evicted = 0;
            foreach (uint networkId in m_PendingPersistentState.Keys)
            {
                evicted = networkId;
                break;
            }

            if (evicted == 0) return;
            m_PendingPersistentState.Remove(evicted);
            m_PendingPersistentOverflow.Add(evicted);
            Debug.LogWarning(
                $"[NetworkInventoryManager] Evicted queued persistent state for bag={evicted} " +
                $"while reserving capacity for bag={incomingBagNetworkId}. A resync will be " +
                "requested if that bag registers.");
        }

        private void FlushPendingPersistentState(uint bagNetworkId, NetworkInventoryController controller)
        {
            if (controller == null) return;
            bool requiresResync = m_PendingPersistentOverflow.Remove(bagNetworkId);
            if (!m_PendingPersistentState.TryGetValue(bagNetworkId, out List<PendingPersistentState> pending))
            {
                if (requiresResync) RequestPendingPersistentResync(bagNetworkId);
                return;
            }

            m_PendingPersistentState.Remove(bagNetworkId);
            if (requiresResync)
            {
                RequestPendingPersistentResync(bagNetworkId);
                return;
            }
            if (pending.Count == 0) return;

            for (int i = 0; i < pending.Count; i++)
            {
                PendingPersistentState state = pending[i];
                switch (state.Kind)
                {
                    case PendingPersistentStateKind.ItemAdded:
                        controller.ReceiveItemAddedBroadcast((NetworkItemAddedBroadcast)state.Payload);
                        break;
                    case PendingPersistentStateKind.ItemRemoved:
                        controller.ReceiveItemRemovedBroadcast((NetworkItemRemovedBroadcast)state.Payload);
                        break;
                    case PendingPersistentStateKind.ItemMoved:
                        controller.ReceiveItemMovedBroadcast((NetworkItemMovedBroadcast)state.Payload);
                        break;
                    case PendingPersistentStateKind.ItemUsed:
                        NetworkItemUsedBroadcast useBroadcast =
                            (NetworkItemUsedBroadcast)state.Payload;
                        if (useBroadcast.WasConsumed ||
                            state.QueuedRealtime <= 0f ||
                            Time.realtimeSinceStartup - state.QueuedRealtime <=
                            PENDING_TRANSIENT_USE_TTL_SECONDS)
                        {
                            controller.ReceiveItemUsedBroadcast(useBroadcast);
                        }
                        break;
                    case PendingPersistentStateKind.ItemEquipped:
                        controller.ReceiveItemEquippedBroadcast((NetworkItemEquippedBroadcast)state.Payload);
                        break;
                    case PendingPersistentStateKind.ItemUnequipped:
                        controller.ReceiveItemUnequippedBroadcast((NetworkItemUnequippedBroadcast)state.Payload);
                        break;
                    case PendingPersistentStateKind.SocketChanged:
                        controller.ReceiveSocketChangeBroadcast((NetworkSocketChangeBroadcast)state.Payload);
                        break;
                    case PendingPersistentStateKind.WealthChanged:
                        controller.ReceiveWealthChangeBroadcast((NetworkWealthChangeBroadcast)state.Payload);
                        break;
                    case PendingPersistentStateKind.PropertyChanged:
                        controller.ReceivePropertyChangeBroadcast((NetworkPropertyChangeBroadcast)state.Payload);
                        break;
                    case PendingPersistentStateKind.Snapshot:
                        controller.ReceiveFullSnapshot((NetworkInventorySnapshot)state.Payload);
                        break;
                    case PendingPersistentStateKind.Delta:
                        controller.ReceiveDelta((NetworkInventoryDelta)state.Payload);
                        break;
                }
            }
        }

        private void RequestPendingPersistentResync(uint bagNetworkId)
        {
            if (m_IsServer) return;
            if (OnSendResyncRequest == null)
            {
                Debug.LogWarning(
                    $"[NetworkInventoryManager] Inventory state for bag={bagNetworkId} requires " +
                    "a resync, but no Inventory resync transport route is installed.");
                return;
            }

            uint actorNetworkId = bagNetworkId;
            NetworkInventoryController bagController = GetController(bagNetworkId);
            if (bagController == null || !bagController.IsLocalClient ||
                !bagController.UsesNetworkCharacterId)
            {
                foreach (NetworkInventoryController candidate in m_Controllers.Values)
                {
                    if (candidate == null || !candidate.IsLocalClient ||
                        !candidate.UsesNetworkCharacterId || candidate.NetworkId == 0) continue;
                    actorNetworkId = candidate.NetworkId;
                    break;
                }
            }

            ushort requestId = NextSemanticRequestId();
            SendResyncRequest(new NetworkInventoryResyncRequest
            {
                ActorNetworkId = actorNetworkId,
                CorrelationId = NetworkCorrelation.Compose(actorNetworkId, requestId),
                BagNetworkId = bagNetworkId,
                LastAppliedStateVersion = 0
            });
        }

        #endregion

        // ════════════════════════════════════════════════════════════════════════════════════════
        // CLIENT: RECEIVING RESPONSES
        // ════════════════════════════════════════════════════════════════════════════════════════

        #region Receive Responses (Client)

        public void ReceiveContentAddResponse(NetworkContentAddResponse response, uint targetNetworkId)
        {
            uint actorId = response.ActorNetworkId != 0 ? response.ActorNetworkId : targetNetworkId;
            var controller = GetController(actorId);
            controller?.ReceiveContentAddResponse(response);
        }

        public void ReceiveContentRemoveResponse(NetworkContentRemoveResponse response, uint targetNetworkId)
        {
            uint actorId = response.ActorNetworkId != 0 ? response.ActorNetworkId : targetNetworkId;
            var controller = GetController(actorId);
            controller?.ReceiveContentRemoveResponse(response);
        }

        public void ReceiveContentMoveResponse(NetworkContentMoveResponse response, uint targetNetworkId)
        {
            uint actorId = response.ActorNetworkId != 0 ? response.ActorNetworkId : targetNetworkId;
            var controller = GetController(actorId);
            controller?.ReceiveContentMoveResponse(response);
        }

        public void ReceiveContentUseResponse(NetworkContentUseResponse response, uint targetNetworkId)
        {
            uint actorId = response.ActorNetworkId != 0 ? response.ActorNetworkId : targetNetworkId;
            var controller = GetController(actorId);
            controller?.ReceiveContentUseResponse(response);
        }

        public void ReceiveContentDropResponse(NetworkContentDropResponse response, uint targetNetworkId)
        {
            uint actorId = response.ActorNetworkId != 0 ? response.ActorNetworkId : targetNetworkId;
            var controller = GetController(actorId);
            controller?.ReceiveContentDropResponse(response);
        }

        public void ReceiveEquipmentResponse(NetworkEquipmentResponse response, uint targetNetworkId)
        {
            uint actorId = response.ActorNetworkId != 0 ? response.ActorNetworkId : targetNetworkId;
            var controller = GetController(actorId);
            controller?.ReceiveEquipmentResponse(response);
        }

        public void ReceiveSocketResponse(NetworkSocketResponse response, uint targetNetworkId)
        {
            uint actorId = response.ActorNetworkId != 0 ? response.ActorNetworkId : targetNetworkId;
            var controller = GetController(actorId);
            controller?.ReceiveSocketResponse(response);
        }

        public void ReceiveWealthResponse(NetworkWealthResponse response, uint targetNetworkId)
        {
            uint actorId = response.ActorNetworkId != 0 ? response.ActorNetworkId : targetNetworkId;
            var controller = GetController(actorId);
            controller?.ReceiveWealthResponse(response);
        }

        public void ReceiveTransferResponse(NetworkTransferResponse response, uint targetNetworkId)
        {
            uint actorId = response.ActorNetworkId != 0 ? response.ActorNetworkId : targetNetworkId;
            GetController(actorId)?.ReceiveTransactionResponse(
                response.Authorized, response.RejectionReason, "Transfer item",
                response.StateVersion);
            if (!response.Authorized && m_LogNetworkMessages)
            {
                Debug.LogWarning($"[NetworkInventoryManager] Transfer rejected: {response.RejectionReason}");
            }
        }

        public void ReceivePickupResponse(NetworkPickupResponse response, uint targetNetworkId)
        {
            CompletePickupResponse(response);
            if (m_LogNetworkMessages)
            {
                Debug.Log(
                    $"[NetworkInventoryPickupDebug][Manager] receive pickup response target={targetNetworkId} req={response.RequestId} authorized={response.Authorized} reason={response.RejectionReason} placed={response.PlacedPosition}");
            }

            if (!response.Authorized)
            {
                Debug.LogWarning($"[NetworkInventoryManager] Pickup rejected: {response.RejectionReason}");
            }
        }

        #endregion

        // ════════════════════════════════════════════════════════════════════════════════════════
        // CUSTOM VALIDATION EXTENSION POINTS
        // ════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>Custom validator for add operations.</summary>
        public Func<NetworkContentAddRequest, uint, (bool allowed, InventoryRejectionReason reason)> CustomAddValidator;

        /// <summary>Custom validator for remove operations.</summary>
        public Func<NetworkContentRemoveRequest, uint, (bool allowed, InventoryRejectionReason reason)> CustomRemoveValidator;

        /// <summary>
        /// Required validator for generic client-originated wealth changes. Merchant, crafting,
        /// and trusted server mutations do not use this endpoint.
        /// </summary>
        public Func<NetworkWealthRequest, uint, (bool allowed, InventoryRejectionReason reason)> CustomWealthValidator;

        /// <summary>Custom validator for merchant operations.</summary>
        public Func<NetworkMerchantRequest, uint, (bool allowed, InventoryRejectionReason reason)> CustomMerchantValidator;

        /// <summary>Custom validator for crafting operations.</summary>
        public Func<NetworkCraftingRequest, uint, (bool allowed, InventoryRejectionReason reason)> CustomCraftingValidator;

        // ════════════════════════════════════════════════════════════════════════════════════════
        // HELPERS
        // ════════════════════════════════════════════════════════════════════════════════════════

        private bool CheckRateLimit(ulong clientId)
        {
            if (!m_PendingRequestCounts.TryGetValue(clientId, out int count))
                count = 0;

            if (count >= m_MaxPendingRequestsPerPlayer)
            {
                Debug.LogWarning($"[NetworkInventoryManager] Client {clientId} exceeded rate limit");
                return false;
            }

            m_PendingRequestCounts[clientId] = count + 1;
            return true;
        }

        private void DecrementPendingRequests(ulong clientId)
        {
            if (m_PendingRequestCounts.TryGetValue(clientId, out int count))
            {
                m_PendingRequestCounts[clientId] = Math.Max(0, count - 1);
            }
        }

        public IEnumerable<uint> GetRegisteredNetworkIds() => m_Controllers.Keys;

        public void SendInitialState(ulong clientId)
        {
            if (!m_IsServer) return;
            foreach (var kvp in m_Controllers)
            {
                var snapshot = kvp.Value.GetFullSnapshot();
                SendSnapshotToClient(clientId, snapshot);
            }

            SendPickupStateSnapshot(clientId);
        }

        public void ForceFullSync()
        {
            if (!m_IsServer) return;
            foreach (var kvp in m_Controllers)
            {
                var snapshot = kvp.Value.GetFullSnapshot();
                BroadcastFullSnapshot(snapshot);
            }
        }

        public void ClearControllers()
        {
            CancelPendingSemanticTransactions();
            m_Controllers.Clear();
            m_MerchantControllers.Clear();
            m_PendingPersistentState.Clear();
            m_PendingPersistentOverflow.Clear();
            if (m_LogNetworkMessages)
                Debug.Log("[NetworkInventoryManager] All controllers cleared");
        }
    }

    /// <summary>
    /// Placeholder for merchant-specific network controller.
    /// </summary>
    public class NetworkMerchantController : MonoBehaviour
    {
        // Would contain merchant-specific networking logic
        // Similar to NetworkInventoryController but for merchant operations
    }
}
#endif
