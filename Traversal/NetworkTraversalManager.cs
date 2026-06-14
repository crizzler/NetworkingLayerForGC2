#if GC2_TRAVERSAL
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Arawn.GameCreator2.Networking.Security;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Traversal;
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

        private const BindingFlags TRAVERSAL_STANCE_FIELD_FLAGS =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static readonly PropertyInfo s_TraversalStanceRelativePositionProperty =
            typeof(TraversalStance).GetProperty("RelativePosition", TRAVERSAL_STANCE_FIELD_FLAGS);

        private static readonly PropertyInfo s_TraversalStanceInInteractiveTransitionProperty =
            typeof(TraversalStance).GetProperty("InInteractiveTransition", TRAVERSAL_STANCE_FIELD_FLAGS);

        private static float s_LastOwnerAuthorityPoseSyncLogRealtime = -100f;
        private static bool s_LoggedMissingTransitionProperty;

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
            InstallOwnerAuthorityPoseSyncHook();
        }

        private void OnDisable()
        {
            SecurityIntegration.SetModuleServerContext("Traversal", false);
            UninstallOwnerAuthorityPoseSyncHook();
            if (m_PatchHooks != null)
            {
                m_PatchHooks.Initialize(false, false);
            }
        }

        private static void InstallOwnerAuthorityPoseSyncHook()
        {
            UnitDriverNetworkServer.OwnerAuthorityPositionAccepted -= SyncTraversalRelativePositionFromOwnerAuthority;
            UnitDriverNetworkServer.OwnerAuthorityPositionAccepted += SyncTraversalRelativePositionFromOwnerAuthority;
            UnitDriverNetworkServer.OwnerAuthorityPositionRejectionRequested -= RejectOwnerAuthorityPoseDuringInteractiveTransition;
            UnitDriverNetworkServer.OwnerAuthorityPositionRejectionRequested += RejectOwnerAuthorityPoseDuringInteractiveTransition;
            UnitDriverNetworkServer.ExternalRootPositionWriteAllowanceRequested -= AllowInteractiveTraversalRootWrite;
            UnitDriverNetworkServer.ExternalRootPositionWriteAllowanceRequested += AllowInteractiveTraversalRootWrite;
        }

        private static void UninstallOwnerAuthorityPoseSyncHook()
        {
            UnitDriverNetworkServer.OwnerAuthorityPositionAccepted -= SyncTraversalRelativePositionFromOwnerAuthority;
            UnitDriverNetworkServer.OwnerAuthorityPositionRejectionRequested -= RejectOwnerAuthorityPoseDuringInteractiveTransition;
            UnitDriverNetworkServer.ExternalRootPositionWriteAllowanceRequested -= AllowInteractiveTraversalRootWrite;
        }

        private static string RejectOwnerAuthorityPoseDuringInteractiveTransition(Character character, Vector3 ownerAuthorityPosition)
        {
            if (!TryGetActiveInteractiveTraversal(character, out TraversalStance stance, out TraverseInteractive interactive))
            {
                return string.Empty;
            }

            if (!TryGetInInteractiveTransition(character, stance, out bool inTransition) || !inTransition)
            {
                return string.Empty;
            }

            return $"traversal-interactive-transition:{interactive.name}";
        }

        private static string AllowInteractiveTraversalRootWrite(Character character, Vector3 rootPosition)
        {
            if (!TryGetActiveInteractiveTraversal(character, out TraversalStance stance, out TraverseInteractive interactive))
            {
                return string.Empty;
            }

            return TryGetInInteractiveTransition(character, stance, out bool inTransition) && inTransition
                ? $"traversal-interactive-transition:{interactive.name}"
                : $"traversal-interactive:{interactive.name}";
        }

        private static void SyncTraversalRelativePositionFromOwnerAuthority(Character character, Vector3 ownerAuthorityPosition)
        {
            if (!TryGetActiveInteractiveTraversal(character, out TraversalStance stance, out TraverseInteractive interactive))
            {
                return;
            }

            if (interactive.MotionInteractive == null) return;

            if (s_TraversalStanceRelativePositionProperty == null)
            {
                Debug.LogError(
                    $"[TraversalPoseDebug][Manager] {character.name} failed to sync owner-authority traversal pose: " +
                    "TraversalStance.RelativePosition property was not found",
                    character);
                return;
            }

            Vector3 anchorPosition = interactive.MotionInteractive.CharacterPosition(character);
            Vector3 localPosition = interactive.Transform.InverseTransformPoint(anchorPosition);
            Vector3 previousRelative = s_TraversalStanceRelativePositionProperty.GetValue(stance) is Vector3 previous
                ? previous
                : default;

            float halfWidth = interactive.Width * 0.5f;
            localPosition.x = Mathf.Clamp(localPosition.x, -halfWidth, halfWidth);
            localPosition.y = 0f;
            localPosition.z = Mathf.Clamp(localPosition.z, interactive.PositionA, interactive.PositionB);

            s_TraversalStanceRelativePositionProperty.SetValue(stance, localPosition);

            float now = Time.realtimeSinceStartup;
            if ((localPosition - previousRelative).sqrMagnitude <= 0.0001f &&
                now - s_LastOwnerAuthorityPoseSyncLogRealtime < 0.25f)
            {
                return;
            }

            Debug.Log(
                $"[TraversalPoseDebug][Manager] synced owner-authority traversal relative position " +
                $"character='{character.name}' traverse='{interactive.name}:{interactive.GetType().Name}' " +
                $"ownerRoot={FormatVector(ownerAuthorityPosition)} anchor={FormatVector(anchorPosition)} " +
                $"previousRelative={FormatVector(previousRelative)} relative={FormatVector(localPosition)} " +
                $"boundsA={interactive.PositionA:F3} boundsB={interactive.PositionB:F3} width={interactive.Width:F3}",
                character);
            s_LastOwnerAuthorityPoseSyncLogRealtime = now;
        }

        private static bool TryGetActiveInteractiveTraversal(
            Character character,
            out TraversalStance stance,
            out TraverseInteractive interactive)
        {
            stance = null;
            interactive = null;

            if (character == null || character.Combat == null) return false;

            stance = character.Combat.RequestStance<TraversalStance>();
            if (stance == null) return false;

            interactive = stance.Traverse as TraverseInteractive;
            return interactive != null;
        }

        private static bool TryGetInInteractiveTransition(
            Character character,
            TraversalStance stance,
            out bool inTransition)
        {
            inTransition = false;
            if (stance == null) return false;

            if (s_TraversalStanceInInteractiveTransitionProperty == null)
            {
                if (!s_LoggedMissingTransitionProperty)
                {
                    Debug.LogError(
                        $"[TraversalPoseDebug][Manager] {(character != null ? character.name : "Character")} " +
                        "failed to inspect traversal transition state: " +
                        "TraversalStance.InInteractiveTransition property was not found",
                        character);
                    s_LoggedMissingTransitionProperty = true;
                }

                return false;
            }

            if (s_TraversalStanceInInteractiveTransitionProperty.GetValue(stance) is not bool value)
            {
                return false;
            }

            inTransition = value;
            return true;
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

            TraceTraversal(
                $"receive request rawClient={clientId} sender={senderClientId} requestId={request.RequestId} " +
                $"actor={request.ActorNetworkId} target={request.TargetNetworkId} correlation={request.CorrelationId} " +
                $"action={request.Action} traverse='{request.TraverseIdString}' hash={request.TraverseHash}");

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
                    TraceTraversal(
                        $"reject no controller requestId={request.RequestId} target={request.TargetNetworkId} " +
                        $"registered={m_Controllers.Count}");
                    SendRejectedResponse(senderClientId, request, TraversalRejectionReason.TargetNotFound);
                    return;
                }

                TraceTraversal(
                    $"validated request requestId={request.RequestId} sender={senderClientId} " +
                    $"target={request.TargetNetworkId} controller='{controller.name}'");

                NetworkTraversalResponse response = await controller.ProcessTraversalRequestAsync(request, senderClientId);
                response.ActorNetworkId = request.ActorNetworkId;
                response.CorrelationId = request.CorrelationId;
                TraceTraversal(
                    $"send response requestId={response.RequestId} sender={senderClientId} " +
                    $"authorized={response.Authorized} applied={response.Applied} rejection={response.RejectionReason} " +
                    $"traversing={response.IsTraversing} traverse='{response.TraverseIdString}' error='{response.Error}'");
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
            TraceTraversal(
                $"reject requestId={request.RequestId} sender={senderClientId} actor={request.ActorNetworkId} " +
                $"target={request.TargetNetworkId} correlation={request.CorrelationId} action={request.Action} " +
                $"reason={reason} traverse='{request.TraverseIdString}' hash={request.TraverseHash}");

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

        private void TraceTraversal(string message)
        {
            Debug.Log($"[TraversalTrace][Manager] server={m_IsServer} controllers={m_Controllers.Count} {message}", this);
        }

        private static string FormatVector(Vector3 value)
        {
            return $"({value.x:F3},{value.y:F3},{value.z:F3})";
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
