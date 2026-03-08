#if GC2_QUESTS
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using Arawn.GameCreator2.Networking.Security;

namespace Arawn.GameCreator2.Networking.Quests
{
    [AddComponentMenu("Game Creator/Network/Quests/Network Quests Manager")]
    public class NetworkQuestsManager : NetworkSingleton<NetworkQuestsManager>
    {
        public static class MessageTypes
        {
            public const byte QuestRequest = 195;
            public const byte QuestResponse = 196;
            public const byte QuestBroadcast = 197;
            public const byte QuestSnapshot = 198;
        }

        public new static NetworkQuestsManager Instance
        {
            get
            {
                if (s_Instance == null)
                {
                    s_Instance = FindFirstObjectByType<NetworkQuestsManager>();
                }

                return s_Instance;
            }
        }

        // CLIENT -> SERVER
        public Action<NetworkQuestRequest> OnSendQuestRequest;

        // SERVER -> CLIENT
        public Action<uint, NetworkQuestResponse> OnSendQuestResponse;
        public Action<NetworkQuestBroadcast> OnBroadcastQuestChange;
        public Action<NetworkQuestsSnapshot> OnBroadcastFullSnapshot;
        public Action<ulong, NetworkQuestsSnapshot> OnSendSnapshotToClient;

        [Header("Settings")]
        [SerializeField] private bool m_IsServer;

        [Header("Validation")]
        [SerializeField] private int m_MaxPendingRequestsPerPlayer = 50;

        [Header("Debug")]
        [SerializeField] private bool m_LogNetworkMessages;

        private readonly Dictionary<uint, NetworkQuestsController> m_Controllers = new(32);
        private readonly Dictionary<ulong, int> m_PendingRequestCounts = new(32);
        private NetworkQuestsPatchHooks m_PatchHooks;

        public Func<NetworkQuestRequest, uint, QuestRejectionReason> CustomQuestValidator;

        public bool IsServer
        {
            get => m_IsServer;
            set
            {
                m_IsServer = value;
                SecurityIntegration.SetModuleServerContext("Quests", m_IsServer);
                SecurityIntegration.EnsureSecurityManagerInitialized(m_IsServer, ResolveSecurityTimeProvider);
                SyncPatchHooks();
                if (m_IsServer) RefreshOwnedEntityMappings();
            }
        }

        private void OnEnable()
        {
            SecurityIntegration.SetModuleServerContext("Quests", m_IsServer);
            SecurityIntegration.EnsureSecurityManagerInitialized(m_IsServer, ResolveSecurityTimeProvider);
            SyncPatchHooks();
        }

        private void OnDisable()
        {
            SecurityIntegration.SetModuleServerContext("Quests", false);
            if (m_PatchHooks != null)
            {
                m_PatchHooks.Initialize(false, false);
            }
        }

        public void RegisterController(uint networkId, NetworkQuestsController controller)
        {
            if (controller == null || networkId == 0) return;

            m_Controllers[networkId] = controller;
            RegisterOwnedEntityMapping(networkId);

            if (m_LogNetworkMessages)
            {
                Debug.Log($"[NetworkQuestsManager] Registered controller for NetworkId={networkId}");
            }
        }

        public void UnregisterController(uint networkId)
        {
            if (!m_Controllers.Remove(networkId)) return;

            SecurityIntegration.UnregisterEntity(networkId);
            if (m_LogNetworkMessages)
            {
                Debug.Log($"[NetworkQuestsManager] Unregistered controller for NetworkId={networkId}");
            }
        }

        public NetworkQuestsController GetController(uint networkId)
        {
            return m_Controllers.TryGetValue(networkId, out NetworkQuestsController controller)
                ? controller
                : null;
        }

        public void SendQuestRequest(NetworkQuestRequest request)
        {
            if (m_LogNetworkMessages)
            {
                Debug.Log($"[NetworkQuestsManager] Sending quest request: Action={request.Action}, RequestId={request.RequestId}");
            }

            OnSendQuestRequest?.Invoke(request);
        }

        public async Task ReceiveQuestRequest(NetworkQuestRequest request, ulong clientId)
        {
            if (!m_IsServer)
            {
                Debug.LogWarning("[NetworkQuestsManager] Non-server received quest request");
                return;
            }

            uint senderClientId = GetSenderClientId(clientId);
            bool pendingIncremented = false;
            try
            {
                if (!SecurityIntegration.ValidateModuleRequest(
                        senderClientId,
                        BuildContext(request.ActorNetworkId, request.CorrelationId),
                        "Quests",
                        nameof(NetworkQuestRequest)))
                {
                    SendRejectedResponse(senderClientId, request, GetSecurityRejection(request.ActorNetworkId, request.CorrelationId));
                    return;
                }

                if (!ValidateTargetOwnership(senderClientId, request.ActorNetworkId, request.TargetNetworkId, nameof(NetworkQuestRequest)))
                {
                    SendRejectedResponse(senderClientId, request, QuestRejectionReason.SecurityViolation);
                    return;
                }

                if (CustomQuestValidator != null)
                {
                    QuestRejectionReason customResult = CustomQuestValidator.Invoke(request, senderClientId);
                    if (customResult != QuestRejectionReason.None)
                    {
                        SendRejectedResponse(senderClientId, request, customResult);
                        return;
                    }
                }

                if (!CheckAndIncrementPendingRequests(clientId))
                {
                    SendRejectedResponse(senderClientId, request, QuestRejectionReason.RateLimitExceeded);
                    return;
                }
                pendingIncremented = true;

                NetworkQuestsController controller = GetController(request.TargetNetworkId);
                if (controller == null)
                {
                    SendRejectedResponse(senderClientId, request, QuestRejectionReason.TargetNotFound);
                    return;
                }

                NetworkQuestResponse response = await controller.ProcessQuestRequestAsync(request, senderClientId);
                response.ActorNetworkId = request.ActorNetworkId;
                response.CorrelationId = request.CorrelationId;
                OnSendQuestResponse?.Invoke(senderClientId, response);
            }
            catch (Exception exception)
            {
                Debug.LogError($"[NetworkQuestsManager] Failed to process quest request: {exception.Message}");
                SendRejectedResponse(senderClientId, request, QuestRejectionReason.Exception);
            }
            finally
            {
                if (pendingIncremented)
                {
                    DecrementPendingRequests(clientId);
                }
            }
        }

        public void ReceiveQuestResponse(NetworkQuestResponse response, uint targetNetworkId)
        {
            uint actorId = response.ActorNetworkId != 0 ? response.ActorNetworkId : targetNetworkId;
            NetworkQuestsController controller = GetController(actorId);
            controller?.ReceiveQuestResponse(response);
        }

        public void BroadcastQuestChange(NetworkQuestBroadcast broadcast)
        {
            if (!m_IsServer) return;

            if (m_LogNetworkMessages)
            {
                Debug.Log($"[NetworkQuestsManager] Broadcasting quest change: NetworkId={broadcast.NetworkId}, Action={broadcast.Action}");
            }

            OnBroadcastQuestChange?.Invoke(broadcast);
        }

        public void ReceiveQuestChangeBroadcast(NetworkQuestBroadcast broadcast)
        {
            NetworkQuestsController controller = GetController(broadcast.NetworkId);
            controller?.ReceiveQuestChangeBroadcast(broadcast);
        }

        public void BroadcastFullSnapshot(NetworkQuestsSnapshot snapshot)
        {
            if (!m_IsServer) return;

            if (m_LogNetworkMessages)
            {
                Debug.Log($"[NetworkQuestsManager] Broadcasting full snapshot: NetworkId={snapshot.NetworkId}, Quests={snapshot.QuestEntries?.Length ?? 0}");
            }

            OnBroadcastFullSnapshot?.Invoke(snapshot);
        }

        public void ReceiveFullSnapshot(NetworkQuestsSnapshot snapshot)
        {
            NetworkQuestsController controller = GetController(snapshot.NetworkId);
            controller?.ReceiveFullSnapshot(snapshot);
        }

        public void SendSnapshotToClient(ulong clientId, NetworkQuestsSnapshot snapshot)
        {
            if (!m_IsServer) return;
            OnSendSnapshotToClient?.Invoke(clientId, snapshot);
        }

        public void SendAllSnapshotsToClient(ulong clientId)
        {
            if (!m_IsServer) return;

            foreach (KeyValuePair<uint, NetworkQuestsController> pair in m_Controllers)
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

        private static QuestRejectionReason GetSecurityRejection(uint actorNetworkId, uint correlationId)
        {
            return SecurityIntegration.IsProtocolContextMismatch(actorNetworkId, correlationId)
                ? QuestRejectionReason.ProtocolMismatch
                : QuestRejectionReason.SecurityViolation;
        }

        private void SendRejectedResponse(uint senderClientId, in NetworkQuestRequest request, QuestRejectionReason reason)
        {
            OnSendQuestResponse?.Invoke(senderClientId, new NetworkQuestResponse
            {
                RequestId = request.RequestId,
                ActorNetworkId = request.ActorNetworkId,
                CorrelationId = request.CorrelationId,
                Action = request.Action,
                Authorized = false,
                Applied = false,
                RejectionReason = reason,
                QuestHash = request.QuestHash,
                QuestIdString = request.QuestIdString,
                TaskId = request.TaskId
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
            foreach (KeyValuePair<uint, NetworkQuestsController> pair in m_Controllers)
            {
                RegisterOwnedEntityMapping(pair.Key);
            }
        }

        private static bool ValidateTargetOwnership(uint senderClientId, uint actorNetworkId, uint targetNetworkId, string requestType)
        {
            return SecurityIntegration.ValidateTargetEntityOwnership(
                senderClientId,
                actorNetworkId,
                targetNetworkId,
                "Quests",
                requestType);
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
                m_PatchHooks = GetComponent<NetworkQuestsPatchHooks>();
                if (m_PatchHooks == null)
                {
                    m_PatchHooks = gameObject.AddComponent<NetworkQuestsPatchHooks>();
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
                    Debug.LogWarning($"[NetworkQuestsManager] Rate limit exceeded for client {clientId}");
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
