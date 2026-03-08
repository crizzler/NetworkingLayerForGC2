#if GC2_DIALOGUE
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Arawn.GameCreator2.Networking.Security;
using GameCreator.Runtime.Dialogue;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Dialogue
{
    [AddComponentMenu("Game Creator/Network/Dialogue/Network Dialogue Manager")]
    public class NetworkDialogueManager : NetworkSingleton<NetworkDialogueManager>
    {
        public static class MessageTypes
        {
            public const byte DialogueRequest = 230;
            public const byte DialogueResponse = 231;
            public const byte DialogueBroadcast = 232;
            public const byte DialogueSnapshot = 233;
        }

        public new static NetworkDialogueManager Instance
        {
            get
            {
                if (s_Instance == null)
                {
                    s_Instance = FindFirstObjectByType<NetworkDialogueManager>();
                }

                return s_Instance;
            }
        }

        // CLIENT -> SERVER
        public Action<NetworkDialogueRequest> OnSendDialogueRequest;

        // SERVER -> CLIENT
        public Action<uint, NetworkDialogueResponse> OnSendDialogueResponse;
        public Action<NetworkDialogueBroadcast> OnBroadcastDialogueChange;
        public Action<NetworkDialogueSnapshot> OnBroadcastFullSnapshot;
        public Action<ulong, NetworkDialogueSnapshot> OnSendSnapshotToClient;

        [Header("Settings")]
        [SerializeField] private bool m_IsServer;

        [Header("Validation")]
        [SerializeField] private int m_MaxPendingRequestsPerPlayer = 50;

        [Header("Debug")]
        [SerializeField] private bool m_LogNetworkMessages;

        private readonly Dictionary<uint, NetworkDialogueController> m_Controllers = new(32);
        private readonly Dictionary<ulong, int> m_PendingRequestCounts = new(32);
        private NetworkDialoguePatchHooks m_PatchHooks;

        public Func<NetworkDialogueRequest, uint, DialogueRejectionReason> CustomDialogueValidator;

        public bool IsServer
        {
            get => m_IsServer;
            set
            {
                m_IsServer = value;
                SecurityIntegration.SetModuleServerContext("Dialogue", m_IsServer);
                SecurityIntegration.EnsureSecurityManagerInitialized(m_IsServer, ResolveSecurityTimeProvider);
                SyncPatchHooks();
                if (m_IsServer) RefreshOwnedEntityMappings();
            }
        }

        public bool IsPatchModeActive => m_PatchHooks != null && m_PatchHooks.IsPatchActive;

        private void OnEnable()
        {
            SecurityIntegration.SetModuleServerContext("Dialogue", m_IsServer);
            SecurityIntegration.EnsureSecurityManagerInitialized(m_IsServer, ResolveSecurityTimeProvider);
            SyncPatchHooks();
        }

        private void OnDisable()
        {
            SecurityIntegration.SetModuleServerContext("Dialogue", false);
            if (m_PatchHooks != null)
            {
                m_PatchHooks.Initialize(false, false);
            }
        }

        public void RegisterController(uint networkId, NetworkDialogueController controller)
        {
            if (controller == null || networkId == 0) return;

            m_Controllers[networkId] = controller;
            RegisterOwnedEntityMapping(networkId);

            if (m_LogNetworkMessages)
            {
                Debug.Log($"[NetworkDialogueManager] Registered controller for NetworkId={networkId}");
            }
        }

        public void UnregisterController(uint networkId)
        {
            if (!m_Controllers.Remove(networkId)) return;

            SecurityIntegration.UnregisterEntity(networkId);
            if (m_LogNetworkMessages)
            {
                Debug.Log($"[NetworkDialogueManager] Unregistered controller for NetworkId={networkId}");
            }
        }

        public NetworkDialogueController GetController(uint networkId)
        {
            return m_Controllers.TryGetValue(networkId, out NetworkDialogueController controller)
                ? controller
                : null;
        }

        public void SendDialogueRequest(NetworkDialogueRequest request)
        {
            if (m_LogNetworkMessages)
            {
                Debug.Log($"[NetworkDialogueManager] Sending dialogue request: Action={request.Action}, RequestId={request.RequestId}");
            }

            OnSendDialogueRequest?.Invoke(request);
        }

        public async Task ReceiveDialogueRequest(NetworkDialogueRequest request, ulong clientId)
        {
            if (!m_IsServer)
            {
                Debug.LogWarning("[NetworkDialogueManager] Non-server received dialogue request");
                return;
            }

            uint senderClientId = GetSenderClientId(clientId);
            bool pendingIncremented = false;

            try
            {
                if (!SecurityIntegration.ValidateModuleRequest(
                        senderClientId,
                        BuildContext(request.ActorNetworkId, request.CorrelationId),
                        "Dialogue",
                        nameof(NetworkDialogueRequest)))
                {
                    SendRejectedResponse(senderClientId, request, GetSecurityRejection(request.ActorNetworkId, request.CorrelationId));
                    return;
                }

                if (request.TargetNetworkId == 0)
                {
                    SendRejectedResponse(senderClientId, request, DialogueRejectionReason.TargetNotFound);
                    return;
                }

                if (!ValidateTargetOwnership(senderClientId, request.ActorNetworkId, request.TargetNetworkId, nameof(NetworkDialogueRequest)))
                {
                    SendRejectedResponse(senderClientId, request, DialogueRejectionReason.SecurityViolation);
                    return;
                }

                if (CustomDialogueValidator != null)
                {
                    DialogueRejectionReason customResult = CustomDialogueValidator.Invoke(request, senderClientId);
                    if (customResult != DialogueRejectionReason.None)
                    {
                        SendRejectedResponse(senderClientId, request, customResult);
                        return;
                    }
                }

                if (!CheckAndIncrementPendingRequests(clientId))
                {
                    SendRejectedResponse(senderClientId, request, DialogueRejectionReason.RateLimitExceeded);
                    return;
                }

                pendingIncremented = true;

                NetworkDialogueController controller = GetController(request.TargetNetworkId);
                if (controller == null)
                {
                    SendRejectedResponse(senderClientId, request, DialogueRejectionReason.TargetNotFound);
                    return;
                }

                NetworkDialogueResponse response = await controller.ProcessDialogueRequestAsync(request, senderClientId);
                response.ActorNetworkId = request.ActorNetworkId;
                response.CorrelationId = request.CorrelationId;
                OnSendDialogueResponse?.Invoke(senderClientId, response);
            }
            catch (Exception exception)
            {
                Debug.LogError($"[NetworkDialogueManager] Failed to process dialogue request: {exception.Message}");
                SendRejectedResponse(senderClientId, request, DialogueRejectionReason.Exception);
            }
            finally
            {
                if (pendingIncremented)
                {
                    DecrementPendingRequests(clientId);
                }
            }
        }

        public void ReceiveDialogueResponse(NetworkDialogueResponse response, uint targetNetworkId)
        {
            uint actorId = response.ActorNetworkId != 0 ? response.ActorNetworkId : targetNetworkId;
            NetworkDialogueController controller = GetController(actorId);
            controller?.ReceiveDialogueResponse(response);
        }

        public void BroadcastDialogueChange(NetworkDialogueBroadcast broadcast)
        {
            if (!m_IsServer) return;

            if (m_LogNetworkMessages)
            {
                Debug.Log($"[NetworkDialogueManager] Broadcasting dialogue change: NetworkId={broadcast.NetworkId}, Action={broadcast.Action}");
            }

            OnBroadcastDialogueChange?.Invoke(broadcast);
        }

        public void ReceiveDialogueChangeBroadcast(NetworkDialogueBroadcast broadcast)
        {
            NetworkDialogueController controller = GetController(broadcast.NetworkId);
            controller?.ReceiveDialogueChangeBroadcast(broadcast);
        }

        public void BroadcastFullSnapshot(NetworkDialogueSnapshot snapshot)
        {
            if (!m_IsServer) return;

            if (m_LogNetworkMessages)
            {
                Debug.Log($"[NetworkDialogueManager] Broadcasting full dialogue snapshot: NetworkId={snapshot.NetworkId}");
            }

            OnBroadcastFullSnapshot?.Invoke(snapshot);
        }

        public void ReceiveFullSnapshot(NetworkDialogueSnapshot snapshot)
        {
            NetworkDialogueController controller = GetController(snapshot.NetworkId);
            controller?.ReceiveFullSnapshot(snapshot);
        }

        public void SendSnapshotToClient(ulong clientId, NetworkDialogueSnapshot snapshot)
        {
            if (!m_IsServer) return;
            OnSendSnapshotToClient?.Invoke(clientId, snapshot);
        }

        public void SendAllSnapshotsToClient(ulong clientId)
        {
            if (!m_IsServer) return;

            foreach (KeyValuePair<uint, NetworkDialogueController> pair in m_Controllers)
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

        private static DialogueRejectionReason GetSecurityRejection(uint actorNetworkId, uint correlationId)
        {
            return SecurityIntegration.IsProtocolContextMismatch(actorNetworkId, correlationId)
                ? DialogueRejectionReason.ProtocolMismatch
                : DialogueRejectionReason.SecurityViolation;
        }

        private static bool ValidateTargetOwnership(uint senderClientId, uint actorNetworkId, uint targetNetworkId, string requestType)
        {
            return SecurityIntegration.ValidateTargetEntityOwnership(
                senderClientId,
                actorNetworkId,
                targetNetworkId,
                "Dialogue",
                requestType);
        }

        private void SendRejectedResponse(uint senderClientId, in NetworkDialogueRequest request, DialogueRejectionReason reason)
        {
            OnSendDialogueResponse?.Invoke(senderClientId, new NetworkDialogueResponse
            {
                RequestId = request.RequestId,
                ActorNetworkId = request.ActorNetworkId,
                CorrelationId = request.CorrelationId,
                Action = request.Action,
                Authorized = false,
                Applied = false,
                RejectionReason = reason,
                DialogueHash = request.DialogueHash,
                DialogueIdString = request.DialogueIdString,
                CurrentNodeId = Content.NODE_INVALID,
                IsPlaying = false,
                IsVisited = false
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
            foreach (KeyValuePair<uint, NetworkDialogueController> pair in m_Controllers)
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
                m_PatchHooks = GetComponent<NetworkDialoguePatchHooks>();
                if (m_PatchHooks == null)
                {
                    m_PatchHooks = gameObject.AddComponent<NetworkDialoguePatchHooks>();
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
                    Debug.LogWarning($"[NetworkDialogueManager] Rate limit exceeded for client {clientId}");
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
