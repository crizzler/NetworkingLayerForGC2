#if GC2_INVENTORY
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Inventory
{
    /// <summary>
    /// Global manager for inventory network communication.
    /// Transport-agnostic - wire up delegates to your networking solution.
    /// </summary>
    [AddComponentMenu("Game Creator/Network/Inventory/Network Inventory Manager")]
    public class NetworkInventoryManager : MonoBehaviour
    {
        // ════════════════════════════════════════════════════════════════════════════════════════
        // SINGLETON
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        private static NetworkInventoryManager s_Instance;
        
        public static NetworkInventoryManager Instance
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
        
        [Header("Debug")]
        [SerializeField] private bool m_LogNetworkMessages = false;
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // PRIVATE FIELDS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        private readonly Dictionary<uint, NetworkInventoryController> m_Controllers = new(32);
        private readonly Dictionary<ulong, int> m_PendingRequestCounts = new(32);
        
        // Merchant controllers (separate from player bags)
        private readonly Dictionary<uint, NetworkMerchantController> m_MerchantControllers = new(8);
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // PROPERTIES
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        public bool IsServer
        {
            get => m_IsServer;
            set => m_IsServer = value;
        }
        
        public int ControllerCount => m_Controllers.Count;
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // UNITY LIFECYCLE
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        private void Awake()
        {
            if (s_Instance != null && s_Instance != this)
            {
                Debug.LogWarning("[NetworkInventoryManager] Duplicate instance found, destroying.");
                Destroy(gameObject);
                return;
            }
            s_Instance = this;
        }
        
        private void OnDestroy()
        {
            if (s_Instance == this)
                s_Instance = null;
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // REGISTRATION
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        public void RegisterController(uint networkId, NetworkInventoryController controller)
        {
            if (controller == null) return;
            m_Controllers[networkId] = controller;
            
            if (m_LogNetworkMessages)
                Debug.Log($"[NetworkInventoryManager] Registered inventory controller: NetworkId={networkId}");
        }
        
        public void UnregisterController(uint networkId)
        {
            if (m_Controllers.Remove(networkId) && m_LogNetworkMessages)
                Debug.Log($"[NetworkInventoryManager] Unregistered inventory controller: NetworkId={networkId}");
        }
        
        public NetworkInventoryController GetController(uint networkId)
        {
            return m_Controllers.TryGetValue(networkId, out var controller) ? controller : null;
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
                Debug.Log($"[NetworkInventoryManager] Sending pickup request: RequestId={request.RequestId}");
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
        
        public void ReceiveContentAddRequest(NetworkContentAddRequest request, ulong clientId)
        {
            if (!m_IsServer) return;
            if (!CheckRateLimit(clientId)) return;
            
            var controller = GetController(request.TargetBagNetworkId);
            if (controller == null)
            {
                SendContentAddResponse(request.TargetBagNetworkId, new NetworkContentAddResponse
                {
                    RequestId = request.RequestId,
                    Authorized = false,
                    RejectionReason = InventoryRejectionReason.BagNotFound
                });
                return;
            }
            
            var response = controller.ProcessContentAddRequest(request, request.TargetBagNetworkId);
            SendContentAddResponse(request.TargetBagNetworkId, response);
        }
        
        public void ReceiveContentRemoveRequest(NetworkContentRemoveRequest request, ulong clientId)
        {
            if (!m_IsServer) return;
            if (!CheckRateLimit(clientId)) return;
            
            var controller = GetController(request.TargetBagNetworkId);
            if (controller == null)
            {
                SendContentRemoveResponse(request.TargetBagNetworkId, new NetworkContentRemoveResponse
                {
                    RequestId = request.RequestId,
                    Authorized = false,
                    RejectionReason = InventoryRejectionReason.BagNotFound
                });
                return;
            }
            
            var response = controller.ProcessContentRemoveRequest(request, request.TargetBagNetworkId);
            SendContentRemoveResponse(request.TargetBagNetworkId, response);
        }
        
        public void ReceiveContentMoveRequest(NetworkContentMoveRequest request, ulong clientId)
        {
            if (!m_IsServer) return;
            if (!CheckRateLimit(clientId)) return;
            
            var controller = GetController(request.TargetBagNetworkId);
            if (controller == null)
            {
                SendContentMoveResponse(request.TargetBagNetworkId, new NetworkContentMoveResponse
                {
                    RequestId = request.RequestId,
                    Authorized = false,
                    RejectionReason = InventoryRejectionReason.BagNotFound
                });
                return;
            }
            
            var response = controller.ProcessContentMoveRequest(request, request.TargetBagNetworkId);
            SendContentMoveResponse(request.TargetBagNetworkId, response);
        }
        
        public void ReceiveContentUseRequest(NetworkContentUseRequest request, ulong clientId)
        {
            if (!m_IsServer) return;
            if (!CheckRateLimit(clientId)) return;
            
            var controller = GetController(request.TargetBagNetworkId);
            if (controller == null)
            {
                SendContentUseResponse(request.TargetBagNetworkId, new NetworkContentUseResponse
                {
                    RequestId = request.RequestId,
                    Authorized = false,
                    RejectionReason = InventoryRejectionReason.BagNotFound
                });
                return;
            }
            
            var response = controller.ProcessContentUseRequest(request, request.TargetBagNetworkId);
            SendContentUseResponse(request.TargetBagNetworkId, response);
        }
        
        public void ReceiveContentDropRequest(NetworkContentDropRequest request, ulong clientId)
        {
            if (!m_IsServer) return;
            if (!CheckRateLimit(clientId)) return;
            
            var controller = GetController(request.TargetBagNetworkId);
            if (controller == null)
            {
                SendContentDropResponse(request.TargetBagNetworkId, new NetworkContentDropResponse
                {
                    RequestId = request.RequestId,
                    Authorized = false,
                    RejectionReason = InventoryRejectionReason.BagNotFound
                });
                return;
            }
            
            var response = controller.ProcessContentDropRequest(request, request.TargetBagNetworkId);
            SendContentDropResponse(request.TargetBagNetworkId, response);
        }
        
        public async void ReceiveEquipmentRequest(NetworkEquipmentRequest request, ulong clientId)
        {
            if (!m_IsServer) return;
            if (!CheckRateLimit(clientId)) return;
            
            var controller = GetController(request.TargetBagNetworkId);
            if (controller == null)
            {
                SendEquipmentResponse(request.TargetBagNetworkId, new NetworkEquipmentResponse
                {
                    RequestId = request.RequestId,
                    Authorized = false,
                    RejectionReason = InventoryRejectionReason.BagNotFound
                });
                return;
            }
            
            var response = await controller.ProcessEquipmentRequest(request, request.TargetBagNetworkId);
            SendEquipmentResponse(request.TargetBagNetworkId, response);
        }
        
        public void ReceiveSocketRequest(NetworkSocketRequest request, ulong clientId)
        {
            if (!m_IsServer) return;
            if (!CheckRateLimit(clientId)) return;
            
            var controller = GetController(request.TargetBagNetworkId);
            if (controller == null)
            {
                SendSocketResponse(request.TargetBagNetworkId, new NetworkSocketResponse
                {
                    RequestId = request.RequestId,
                    Authorized = false,
                    RejectionReason = InventoryRejectionReason.BagNotFound
                });
                return;
            }
            
            var response = controller.ProcessSocketRequest(request, request.TargetBagNetworkId);
            SendSocketResponse(request.TargetBagNetworkId, response);
        }
        
        public void ReceiveWealthRequest(NetworkWealthRequest request, ulong clientId)
        {
            if (!m_IsServer) return;
            if (!CheckRateLimit(clientId)) return;
            
            var controller = GetController(request.TargetBagNetworkId);
            if (controller == null)
            {
                SendWealthResponse(request.TargetBagNetworkId, new NetworkWealthResponse
                {
                    RequestId = request.RequestId,
                    Authorized = false,
                    RejectionReason = InventoryRejectionReason.BagNotFound
                });
                return;
            }
            
            var response = controller.ProcessWealthRequest(request, request.TargetBagNetworkId);
            SendWealthResponse(request.TargetBagNetworkId, response);
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
            controller?.ReceiveItemAddedBroadcast(broadcast);
        }
        
        public void ReceiveItemRemovedBroadcast(NetworkItemRemovedBroadcast broadcast)
        {
            var controller = GetController(broadcast.BagNetworkId);
            controller?.ReceiveItemRemovedBroadcast(broadcast);
        }
        
        public void ReceiveItemMovedBroadcast(NetworkItemMovedBroadcast broadcast)
        {
            var controller = GetController(broadcast.BagNetworkId);
            controller?.ReceiveItemMovedBroadcast(broadcast);
        }
        
        public void ReceiveItemUsedBroadcast(NetworkItemUsedBroadcast broadcast)
        {
            var controller = GetController(broadcast.BagNetworkId);
            controller?.ReceiveItemUsedBroadcast(broadcast);
        }
        
        public void ReceiveItemEquippedBroadcast(NetworkItemEquippedBroadcast broadcast)
        {
            var controller = GetController(broadcast.BagNetworkId);
            controller?.ReceiveItemEquippedBroadcast(broadcast);
        }
        
        public void ReceiveItemUnequippedBroadcast(NetworkItemUnequippedBroadcast broadcast)
        {
            var controller = GetController(broadcast.BagNetworkId);
            controller?.ReceiveItemUnequippedBroadcast(broadcast);
        }
        
        public void ReceiveSocketChangeBroadcast(NetworkSocketChangeBroadcast broadcast)
        {
            var controller = GetController(broadcast.BagNetworkId);
            controller?.ReceiveSocketChangeBroadcast(broadcast);
        }
        
        public void ReceiveWealthChangeBroadcast(NetworkWealthChangeBroadcast broadcast)
        {
            var controller = GetController(broadcast.BagNetworkId);
            controller?.ReceiveWealthChangeBroadcast(broadcast);
        }
        
        public void ReceiveFullSnapshot(NetworkInventorySnapshot snapshot)
        {
            var controller = GetController(snapshot.BagNetworkId);
            controller?.ReceiveFullSnapshot(snapshot);
        }
        
        #endregion
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // CLIENT: RECEIVING RESPONSES
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        #region Receive Responses (Client)
        
        public void ReceiveContentAddResponse(NetworkContentAddResponse response, uint targetNetworkId)
        {
            var controller = GetController(targetNetworkId);
            controller?.ReceiveContentAddResponse(response);
        }
        
        public void ReceiveContentRemoveResponse(NetworkContentRemoveResponse response, uint targetNetworkId)
        {
            var controller = GetController(targetNetworkId);
            controller?.ReceiveContentRemoveResponse(response);
        }
        
        public void ReceiveContentMoveResponse(NetworkContentMoveResponse response, uint targetNetworkId)
        {
            var controller = GetController(targetNetworkId);
            controller?.ReceiveContentMoveResponse(response);
        }
        
        public void ReceiveContentUseResponse(NetworkContentUseResponse response, uint targetNetworkId)
        {
            var controller = GetController(targetNetworkId);
            controller?.ReceiveContentUseResponse(response);
        }
        
        public void ReceiveContentDropResponse(NetworkContentDropResponse response, uint targetNetworkId)
        {
            var controller = GetController(targetNetworkId);
            controller?.ReceiveContentDropResponse(response);
        }
        
        public void ReceiveEquipmentResponse(NetworkEquipmentResponse response, uint targetNetworkId)
        {
            var controller = GetController(targetNetworkId);
            controller?.ReceiveEquipmentResponse(response);
        }
        
        public void ReceiveSocketResponse(NetworkSocketResponse response, uint targetNetworkId)
        {
            var controller = GetController(targetNetworkId);
            controller?.ReceiveSocketResponse(response);
        }
        
        public void ReceiveWealthResponse(NetworkWealthResponse response, uint targetNetworkId)
        {
            var controller = GetController(targetNetworkId);
            controller?.ReceiveWealthResponse(response);
        }
        
        #endregion
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // CUSTOM VALIDATION EXTENSION POINTS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>Custom validator for add operations.</summary>
        public Func<NetworkContentAddRequest, uint, (bool allowed, InventoryRejectionReason reason)> CustomAddValidator;
        
        /// <summary>Custom validator for remove operations.</summary>
        public Func<NetworkContentRemoveRequest, uint, (bool allowed, InventoryRejectionReason reason)> CustomRemoveValidator;
        
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
        
        public IEnumerable<uint> GetRegisteredNetworkIds() => m_Controllers.Keys;
        
        public void SendInitialState(ulong clientId)
        {
            if (!m_IsServer) return;
            foreach (var kvp in m_Controllers)
            {
                var snapshot = kvp.Value.GetFullSnapshot();
                SendSnapshotToClient(clientId, snapshot);
            }
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
            m_Controllers.Clear();
            m_MerchantControllers.Clear();
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
