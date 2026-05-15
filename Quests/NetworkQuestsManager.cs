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
                Debug.Log($"[NetworkQuestSync][Manager] send request {DescribeRequest(request)}", this);
            }

            OnSendQuestRequest?.Invoke(request);
        }

        public async Task ReceiveQuestRequest(NetworkQuestRequest request, ulong clientId)
        {
            LogNetwork($"receive request rawClient={clientId} {DescribeRequest(request)}");

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
                    LogNetwork($"reject request security sender={senderClientId} {DescribeRequest(request)}");
                    SendRejectedResponse(senderClientId, request, GetSecurityRejection(request.ActorNetworkId, request.CorrelationId));
                    return;
                }

                if (CustomQuestValidator != null)
                {
                    QuestRejectionReason customResult = CustomQuestValidator.Invoke(request, senderClientId);
                    if (customResult != QuestRejectionReason.None)
                    {
                        LogNetwork($"reject request custom reason={customResult} sender={senderClientId} {DescribeRequest(request)}");
                        SendRejectedResponse(senderClientId, request, customResult);
                        return;
                    }
                }

                if (!CheckAndIncrementPendingRequests(clientId))
                {
                    LogNetwork($"reject request rateLimit sender={senderClientId} rawClient={clientId} {DescribeRequest(request)}");
                    SendRejectedResponse(senderClientId, request, QuestRejectionReason.RateLimitExceeded);
                    return;
                }
                pendingIncremented = true;

                NetworkQuestsController controller = GetController(request.TargetNetworkId);
                if (controller == null)
                {
                    LogNetwork($"reject request targetNotFound sender={senderClientId} {DescribeRequest(request)} controllers={m_Controllers.Count}");
                    SendRejectedResponse(senderClientId, request, QuestRejectionReason.TargetNotFound);
                    return;
                }

                if (request.ShareMode == NetworkQuestShareMode.Personal &&
                    !ValidateTargetOwnership(senderClientId, request.ActorNetworkId, request.TargetNetworkId, nameof(NetworkQuestRequest)))
                {
                    LogNetwork($"reject request ownership sender={senderClientId} {DescribeRequest(request)}");
                    SendRejectedResponse(senderClientId, request, QuestRejectionReason.SecurityViolation);
                    return;
                }

                if (!controller.IsNetworkRequestAllowed(request, out QuestRejectionReason profileRejection, out string profileError))
                {
                    LogNetwork($"reject request profile reason={profileRejection} sender={senderClientId} error='{profileError}' {DescribeRequest(request)}");
                    SendRejectedResponse(senderClientId, request, profileRejection, profileError);
                    return;
                }

                NetworkQuestResponse response = await controller.ProcessQuestRequestAsync(request, senderClientId);
                response.ActorNetworkId = request.ActorNetworkId;
                response.CorrelationId = request.CorrelationId;
                LogNetwork($"send response targetClient={senderClientId} {DescribeResponse(response)}");
                OnSendQuestResponse?.Invoke(senderClientId, response);
            }
            catch (Exception exception)
            {
                Debug.LogError($"[NetworkQuestsManager] Failed to process quest request: {exception.Message}");
                LogNetwork($"reject request exception sender={senderClientId} exception={exception.Message} {DescribeRequest(request)}");
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
            LogNetwork($"route response targetNetworkId={targetNetworkId} actor={actorId} controller={(controller != null ? controller.name : "none")} {DescribeResponse(response)}");
            controller?.ReceiveQuestResponse(response);
        }

        public void BroadcastQuestChange(NetworkQuestBroadcast broadcast)
        {
            if (!m_IsServer) return;

            LogNetwork($"broadcast quest change {DescribeBroadcast(broadcast)}");

            if (broadcast.ShareMode != NetworkQuestShareMode.Personal)
            {
                MirrorSharedQuestChangeOnServer(broadcast);
            }

            OnBroadcastQuestChange?.Invoke(broadcast);
        }

        private void MirrorSharedQuestChangeOnServer(NetworkQuestBroadcast broadcast)
        {
            // Keep server-side shared journals aligned so any client can continue
            // a global or party quest that another actor started.
            foreach (KeyValuePair<uint, NetworkQuestsController> pair in m_Controllers)
            {
                if (pair.Key == broadcast.NetworkId) continue;

                LogNetwork($"mirror shared broadcast to server controller={pair.Key} name={(pair.Value != null ? pair.Value.name : "none")}");
                pair.Value?.ReceiveQuestChangeBroadcast(broadcast);
            }
        }

        public void ReceiveQuestChangeBroadcast(NetworkQuestBroadcast broadcast)
        {
            LogNetwork($"receive broadcast dispatch {DescribeBroadcast(broadcast)} controllers={m_Controllers.Count}");

            if (broadcast.ShareMode != NetworkQuestShareMode.Personal)
            {
                foreach (KeyValuePair<uint, NetworkQuestsController> pair in m_Controllers)
                {
                    LogNetwork($"dispatch shared broadcast to controller={pair.Key} name={(pair.Value != null ? pair.Value.name : "none")}");
                    pair.Value?.ReceiveQuestChangeBroadcast(broadcast);
                }

                return;
            }

            NetworkQuestsController controller = GetController(broadcast.NetworkId);
            LogNetwork($"dispatch personal broadcast to controller={broadcast.NetworkId} name={(controller != null ? controller.name : "none")}");
            controller?.ReceiveQuestChangeBroadcast(broadcast);
        }

        public void BroadcastFullSnapshot(NetworkQuestsSnapshot snapshot)
        {
            if (!m_IsServer) return;

            LogNetwork($"broadcast full snapshot {DescribeSnapshot(snapshot)}");

            OnBroadcastFullSnapshot?.Invoke(snapshot);
        }

        public void ReceiveFullSnapshot(NetworkQuestsSnapshot snapshot)
        {
            NetworkQuestsController controller = GetController(snapshot.NetworkId);
            LogNetwork($"route full snapshot controller={snapshot.NetworkId} name={(controller != null ? controller.name : "none")} {DescribeSnapshot(snapshot)}");
            controller?.ReceiveFullSnapshot(snapshot);
        }

        public void SendSnapshotToClient(ulong clientId, NetworkQuestsSnapshot snapshot)
        {
            if (!m_IsServer) return;
            LogNetwork($"send snapshot to client={clientId} {DescribeSnapshot(snapshot)}");
            OnSendSnapshotToClient?.Invoke(clientId, snapshot);
        }

        public void SendAllSnapshotsToClient(ulong clientId)
        {
            if (!m_IsServer) return;

            LogNetwork($"send all snapshots to client={clientId} controllers={m_Controllers.Count}");

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

        private void SendRejectedResponse(
            uint senderClientId,
            in NetworkQuestRequest request,
            QuestRejectionReason reason,
            string error = "")
        {
            LogNetwork($"send rejected response targetClient={senderClientId} reason={reason} error='{error}' {DescribeRequest(request)}");
            OnSendQuestResponse?.Invoke(senderClientId, new NetworkQuestResponse
            {
                RequestId = request.RequestId,
                ActorNetworkId = request.ActorNetworkId,
                CorrelationId = request.CorrelationId,
                ProfileHash = request.ProfileHash,
                ShareMode = request.ShareMode,
                ScopeId = request.ScopeId,
                Action = request.Action,
                Authorized = false,
                Applied = false,
                RejectionReason = reason,
                QuestHash = request.QuestHash,
                QuestIdString = request.QuestIdString,
                TaskId = request.TaskId,
                Error = error ?? string.Empty
            });
        }

        private void LogNetwork(string message)
        {
            if (!m_LogNetworkMessages) return;
            Debug.Log($"[NetworkQuestSync][Manager] {message}", this);
        }

        private static string DescribeQuestIdentity(string questIdString, int questHash)
        {
            return !string.IsNullOrEmpty(questIdString)
                ? $"quest='{questIdString}' hash={questHash}"
                : $"questHash={questHash}";
        }

        private static string DescribeRequest(in NetworkQuestRequest request)
        {
            return
                $"requestId={request.RequestId} actor={request.ActorNetworkId} target={request.TargetNetworkId} " +
                $"correlation={request.CorrelationId} action={request.Action} share={request.ShareMode} " +
                $"profile={request.ProfileHash} {DescribeQuestIdentity(request.QuestIdString, request.QuestHash)} task={request.TaskId}";
        }

        private static string DescribeResponse(in NetworkQuestResponse response)
        {
            return
                $"requestId={response.RequestId} actor={response.ActorNetworkId} correlation={response.CorrelationId} " +
                $"action={response.Action} authorized={response.Authorized} applied={response.Applied} rejection={response.RejectionReason} " +
                $"share={response.ShareMode} profile={response.ProfileHash} {DescribeQuestIdentity(response.QuestIdString, response.QuestHash)} " +
                $"task={response.TaskId} questState={response.QuestState} taskState={response.TaskState} tracking={response.IsTracking} error='{response.Error}'";
        }

        private static string DescribeBroadcast(in NetworkQuestBroadcast broadcast)
        {
            return
                $"networkId={broadcast.NetworkId} actor={broadcast.ActorNetworkId} correlation={broadcast.CorrelationId} " +
                $"action={broadcast.Action} share={broadcast.ShareMode} profile={broadcast.ProfileHash} " +
                $"{DescribeQuestIdentity(broadcast.QuestIdString, broadcast.QuestHash)} task={broadcast.TaskId} " +
                $"questState={broadcast.QuestState} taskState={broadcast.TaskState} tracking={broadcast.IsTracking}";
        }

        private static string DescribeSnapshot(in NetworkQuestsSnapshot snapshot)
        {
            int questCount = snapshot.QuestEntries?.Length ?? 0;
            int taskCount = snapshot.TaskEntries?.Length ?? 0;
            return $"networkId={snapshot.NetworkId} serverTime={snapshot.ServerTime:0.###} quests={questCount} tasks={taskCount}";
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
