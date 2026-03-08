#if GC2_TRAVERSAL
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Arawn.GameCreator2.Networking.Security;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Traversal
{
    [AddComponentMenu("Game Creator/Network/Traversal/Network Traversal Manager")]
    public class NetworkTraversalManager : NetworkSingleton<NetworkTraversalManager>
    {
        public static class MessageTypes
        {
            public const byte TraversalRequest = 250;
            public const byte TraversalResponse = 251;
            public const byte TraversalBroadcast = 252;
            public const byte TraversalSnapshot = 253;
        }

        public new static NetworkTraversalManager Instance
        {
            get
            {
                if (s_Instance == null)
                {
                    s_Instance = FindFirstObjectByType<NetworkTraversalManager>();
                }

                return s_Instance;
            }
        }

        public Action<NetworkTraversalRequest> OnSendTraversalRequest;
        public Action<uint, NetworkTraversalResponse> OnSendTraversalResponse;
        public Action<NetworkTraversalBroadcast> OnBroadcastTraversalChange;
        public Action<NetworkTraversalSnapshot> OnBroadcastFullSnapshot;
        public Action<ulong, NetworkTraversalSnapshot> OnSendSnapshotToClient;

        [Header("Settings")]
        [SerializeField] private bool m_IsServer;

        [Header("Validation")]
        [SerializeField] private int m_MaxPendingRequestsPerPlayer = 50;

        [Header("Debug")]
        [SerializeField] private bool m_LogNetworkMessages;

        private readonly Dictionary<uint, NetworkTraversalController> m_Controllers = new(32);
        private readonly Dictionary<ulong, int> m_PendingRequestCounts = new(32);
        private NetworkTraversalPatchHooks m_PatchHooks;

        public Func<NetworkTraversalRequest, uint, TraversalRejectionReason> CustomTraversalValidator;

        public bool IsServer
        {
            get => m_IsServer;
            set
            {
                m_IsServer = value;
                SecurityIntegration.SetModuleServerContext("Traversal", m_IsServer);
                SecurityIntegration.EnsureSecurityManagerInitialized(m_IsServer, ResolveSecurityTimeProvider);
                SyncPatchHooks();
                if (m_IsServer) RefreshOwnedEntityMappings();
            }
        }

        public bool IsPatchModeActive => m_PatchHooks != null && m_PatchHooks.IsPatchActive;

        private void OnEnable()
        {
            SecurityIntegration.SetModuleServerContext("Traversal", m_IsServer);
            SecurityIntegration.EnsureSecurityManagerInitialized(m_IsServer, ResolveSecurityTimeProvider);
            SyncPatchHooks();
        }

        private void OnDisable()
        {
            SecurityIntegration.SetModuleServerContext("Traversal", false);
            if (m_PatchHooks != null)
            {
                m_PatchHooks.Initialize(false, false);
            }
        }

        public void RegisterController(uint networkId, NetworkTraversalController controller)
        {
            if (controller == null || networkId == 0) return;

            m_Controllers[networkId] = controller;
            RegisterOwnedEntityMapping(networkId);

            if (m_LogNetworkMessages)
            {
                Debug.Log($"[NetworkTraversalManager] Registered controller for NetworkId={networkId}");
            }
        }

        public void UnregisterController(uint networkId)
        {
            if (!m_Controllers.Remove(networkId)) return;

            SecurityIntegration.UnregisterEntity(networkId);
            if (m_LogNetworkMessages)
            {
                Debug.Log($"[NetworkTraversalManager] Unregistered controller for NetworkId={networkId}");
            }
        }

        public NetworkTraversalController GetController(uint networkId)
        {
            return m_Controllers.TryGetValue(networkId, out NetworkTraversalController controller)
                ? controller
                : null;
        }

        public void SendTraversalRequest(NetworkTraversalRequest request)
        {
            if (m_LogNetworkMessages)
            {
                Debug.Log($"[NetworkTraversalManager] Sending traversal request: Action={request.Action}, RequestId={request.RequestId}");
            }

            OnSendTraversalRequest?.Invoke(request);
        }

        public async Task ReceiveTraversalRequest(NetworkTraversalRequest request, ulong clientId)
        {
            if (!m_IsServer)
            {
                Debug.LogWarning("[NetworkTraversalManager] Non-server received traversal request");
                return;
            }

            uint senderClientId = GetSenderClientId(clientId);
            bool pendingIncremented = false;

            try
            {
                if (!SecurityIntegration.ValidateModuleRequest(
                        senderClientId,
                        BuildContext(request.ActorNetworkId, request.CorrelationId),
                        "Traversal",
                        nameof(NetworkTraversalRequest)))
                {
                    SendRejectedResponse(senderClientId, request, GetSecurityRejection(request.ActorNetworkId, request.CorrelationId));
                    return;
                }

                if (request.TargetNetworkId == 0)
                {
                    SendRejectedResponse(senderClientId, request, TraversalRejectionReason.TargetNotFound);
                    return;
                }

                if (!ValidateTargetOwnership(senderClientId, request.ActorNetworkId, request.TargetNetworkId, nameof(NetworkTraversalRequest)))
                {
                    SendRejectedResponse(senderClientId, request, TraversalRejectionReason.SecurityViolation);
                    return;
                }

                if (CustomTraversalValidator != null)
                {
                    TraversalRejectionReason customResult = CustomTraversalValidator.Invoke(request, senderClientId);
                    if (customResult != TraversalRejectionReason.None)
                    {
                        SendRejectedResponse(senderClientId, request, customResult);
                        return;
                    }
                }

                if (!CheckAndIncrementPendingRequests(clientId))
                {
                    SendRejectedResponse(senderClientId, request, TraversalRejectionReason.RateLimitExceeded);
                    return;
                }

                pendingIncremented = true;

                NetworkTraversalController controller = GetController(request.TargetNetworkId);
                if (controller == null)
                {
                    SendRejectedResponse(senderClientId, request, TraversalRejectionReason.TargetNotFound);
                    return;
                }

                NetworkTraversalResponse response = await controller.ProcessTraversalRequestAsync(request, senderClientId);
                response.ActorNetworkId = request.ActorNetworkId;
                response.CorrelationId = request.CorrelationId;
                OnSendTraversalResponse?.Invoke(senderClientId, response);
            }
            catch (Exception exception)
            {
                Debug.LogError($"[NetworkTraversalManager] Failed to process traversal request: {exception.Message}");
                SendRejectedResponse(senderClientId, request, TraversalRejectionReason.Exception);
            }
            finally
            {
                if (pendingIncremented)
                {
                    DecrementPendingRequests(clientId);
                }
            }
        }

        public void ReceiveTraversalResponse(NetworkTraversalResponse response, uint targetNetworkId)
        {
            uint actorId = response.ActorNetworkId != 0 ? response.ActorNetworkId : targetNetworkId;
            NetworkTraversalController controller = GetController(actorId);
            controller?.ReceiveTraversalResponse(response);
        }

        public void BroadcastTraversalChange(NetworkTraversalBroadcast broadcast)
        {
            if (!m_IsServer) return;

            if (m_LogNetworkMessages)
            {
                Debug.Log($"[NetworkTraversalManager] Broadcasting traversal change: NetworkId={broadcast.NetworkId}, Action={broadcast.Action}");
            }

            OnBroadcastTraversalChange?.Invoke(broadcast);
        }

        public void ReceiveTraversalChangeBroadcast(NetworkTraversalBroadcast broadcast)
        {
            NetworkTraversalController controller = GetController(broadcast.NetworkId);
            controller?.ReceiveTraversalChangeBroadcast(broadcast);
        }

        public void BroadcastFullSnapshot(NetworkTraversalSnapshot snapshot)
        {
            if (!m_IsServer) return;

            if (m_LogNetworkMessages)
            {
                Debug.Log($"[NetworkTraversalManager] Broadcasting traversal snapshot: NetworkId={snapshot.NetworkId}");
            }

            OnBroadcastFullSnapshot?.Invoke(snapshot);
        }

        public void ReceiveFullSnapshot(NetworkTraversalSnapshot snapshot)
        {
            NetworkTraversalController controller = GetController(snapshot.NetworkId);
            controller?.ReceiveFullSnapshot(snapshot);
        }

        public void SendSnapshotToClient(ulong clientId, NetworkTraversalSnapshot snapshot)
        {
            if (!m_IsServer) return;
            OnSendSnapshotToClient?.Invoke(clientId, snapshot);
        }

        public void SendAllSnapshotsToClient(ulong clientId)
        {
            if (!m_IsServer) return;

            foreach (KeyValuePair<uint, NetworkTraversalController> pair in m_Controllers)
            {
                if (pair.Value == null) continue;
                SendSnapshotToClient(clientId, pair.Value.CaptureFullSnapshot());
            }
        }

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

        private static TraversalRejectionReason GetSecurityRejection(uint actorNetworkId, uint correlationId)
        {
            return SecurityIntegration.IsProtocolContextMismatch(actorNetworkId, correlationId)
                ? TraversalRejectionReason.ProtocolMismatch
                : TraversalRejectionReason.SecurityViolation;
        }

        private static bool ValidateTargetOwnership(uint senderClientId, uint actorNetworkId, uint targetNetworkId, string requestType)
        {
            return SecurityIntegration.ValidateTargetEntityOwnership(
                senderClientId,
                actorNetworkId,
                targetNetworkId,
                "Traversal",
                requestType);
        }

        private void SendRejectedResponse(uint senderClientId, in NetworkTraversalRequest request, TraversalRejectionReason reason)
        {
            OnSendTraversalResponse?.Invoke(senderClientId, new NetworkTraversalResponse
            {
                RequestId = request.RequestId,
                ActorNetworkId = request.ActorNetworkId,
                CorrelationId = request.CorrelationId,
                Action = request.Action,
                Authorized = false,
                Applied = false,
                RejectionReason = reason,
                TraverseHash = request.TraverseHash,
                TraverseIdString = request.TraverseIdString,
                ActionIdHash = request.ActionIdHash,
                ActionIdString = request.ActionIdString,
                StateIdHash = request.StateIdHash,
                StateIdString = request.StateIdString,
                ArgsSelfNetworkId = request.ArgsSelfNetworkId,
                ArgsTargetNetworkId = request.ArgsTargetNetworkId,
                IsTraversing = false,
                Error = reason.ToString()
            });
        }

        private void RegisterOwnedEntityMapping(uint entityNetworkId)
        {
            if (!m_IsServer || entityNetworkId == 0) return;

            SecurityIntegration.RegisterEntityActor(entityNetworkId, entityNetworkId);

            NetworkTransportBridge bridge = NetworkTransportBridge.Active;
            if (bridge != null &&
                bridge.TryGetCharacterOwner(entityNetworkId, out uint ownerClientId) &&
                NetworkTransportBridge.IsValidClientId(ownerClientId))
            {
                SecurityIntegration.RegisterEntityOwner(entityNetworkId, ownerClientId);
            }
        }

        private void RefreshOwnedEntityMappings()
        {
            foreach (KeyValuePair<uint, NetworkTraversalController> pair in m_Controllers)
            {
                RegisterOwnedEntityMapping(pair.Key);
            }
        }

        private static float ResolveSecurityTimeProvider()
        {
            NetworkTransportBridge bridge = NetworkTransportBridge.Active;
            return bridge != null && bridge.IsServer ? bridge.ServerTime : Time.time;
        }

        private void SyncPatchHooks()
        {
            if (m_PatchHooks == null)
            {
                m_PatchHooks = GetComponent<NetworkTraversalPatchHooks>();
                if (m_PatchHooks == null)
                {
                    m_PatchHooks = gameObject.AddComponent<NetworkTraversalPatchHooks>();
                }
            }

            m_PatchHooks.Initialize(m_IsServer, true);
        }

        private bool CheckAndIncrementPendingRequests(ulong clientId)
        {
            if (!m_PendingRequestCounts.TryGetValue(clientId, out int count))
            {
                count = 0;
            }

            if (count >= m_MaxPendingRequestsPerPlayer)
            {
                if (m_LogNetworkMessages)
                {
                    Debug.LogWarning($"[NetworkTraversalManager] Rate limit exceeded for client {clientId}");
                }

                return false;
            }

            m_PendingRequestCounts[clientId] = count + 1;
            return true;
        }

        private void DecrementPendingRequests(ulong clientId)
        {
            if (!m_PendingRequestCounts.TryGetValue(clientId, out int count)) return;

            count--;
            if (count <= 0)
            {
                m_PendingRequestCounts.Remove(clientId);
            }
            else
            {
                m_PendingRequestCounts[clientId] = count;
            }
        }
    }
}
#endif
