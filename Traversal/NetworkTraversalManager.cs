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
        public Func<uint, TraversalRouteStatus> OnResolveRequestRouteStatusForActor;

        [Obsolete("Use OnResolveRequestRouteStatusForActor so the transport validates the exact requesting actor.")]
        public Func<TraversalRouteStatus> OnResolveRequestRouteStatus;

        [Header("Settings")]
        [SerializeField] private bool m_IsServer;

        [Header("Validation")]
        [SerializeField] private int m_MaxPendingRequestsPerPlayer = 50;

        [Header("Spawn Readiness")]
        [Min(0.1f)]
        [SerializeField] private float m_TransientStateTtl = 2f;

        [Min(1)]
        [SerializeField] private int m_MaxPendingTransientStatesPerCharacter = 16;

        [Min(8)]
        [SerializeField] private int m_MaxPendingCharacterStates = 128;

        [Header("Debug")]
        [Tooltip("Writes verbose traversal manager, controller, patch, and transport messages.")]
        [SerializeField] private bool m_LogNetworkMessages;

        [Tooltip("Enables only the rate-limited focused climb, PullUp, and Zipline handoff diagnostics without verbose network logging.")]
        [SerializeField] private bool m_LogFocusedClimbDiagnostics;

        private readonly Dictionary<uint, NetworkTraversalController> m_Controllers = new(32);
        private readonly Dictionary<ulong, int> m_PendingRequestCounts = new(32);
        private readonly Dictionary<uint, PendingSnapshot> m_PendingSnapshots = new(32);
        private readonly Dictionary<uint, List<PendingBroadcast>> m_PendingBroadcasts = new(32);
        private readonly Dictionary<uint, List<PendingResponse>> m_PendingResponses = new(8);
        private readonly List<uint> m_PendingStateRemovalBuffer = new(32);
        private readonly Dictionary<string, float> m_DiagnosticTimes = new(StringComparer.Ordinal);
        private NetworkTraversalPatchHooks m_PatchHooks;
        private bool m_ClimbDiagnosticsActive;

        private struct PendingSnapshot
        {
            public NetworkTraversalSnapshot Value;
            public float ReceivedAt;
        }

        private struct PendingBroadcast
        {
            public NetworkTraversalBroadcast Value;
            public float ReceivedAt;
        }

        private struct PendingResponse
        {
            public NetworkTraversalResponse Value;
            public float ReceivedAt;
        }

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
        public bool DiagnosticsEnabled => m_LogNetworkMessages;
        public bool FocusedClimbDiagnosticsEnabled =>
            m_LogNetworkMessages ||
            m_LogFocusedClimbDiagnostics ||
            NetworkTraversalDebug.ForceClimbDiagnostics;

        private void OnEnable()
        {
            SetClimbDiagnosticsActive(FocusedClimbDiagnosticsEnabled);
            SecurityIntegration.SetModuleServerContext("Traversal", m_IsServer);
            SecurityIntegration.EnsureSecurityManagerInitialized(m_IsServer, ResolveSecurityTimeProvider);
            SyncPatchHooks();
            InstallOwnerAuthorityPoseSyncHook();
        }

        private void Update()
        {
            SetClimbDiagnosticsActive(FocusedClimbDiagnosticsEnabled);
            CleanupExpiredPendingState();
        }

        private void OnDisable()
        {
            SetClimbDiagnosticsActive(false);
            SecurityIntegration.SetModuleServerContext("Traversal", false);
            UninstallOwnerAuthorityPoseSyncHook();
            if (m_PatchHooks != null)
            {
                m_PatchHooks.Initialize(false, false, DiagnosticsEnabled);
            }

            m_PendingSnapshots.Clear();
            m_PendingBroadcasts.Clear();
            m_PendingResponses.Clear();
            m_PendingStateRemovalBuffer.Clear();
        }

        private void SetClimbDiagnosticsActive(bool active)
        {
            if (m_ClimbDiagnosticsActive == active) return;
            m_ClimbDiagnosticsActive = active;
            NetworkTraversalClimbDiagnostics.SetManagerActive(active);

            if (active)
            {
                NetworkTraversalClimbDiagnostics.Log(
                    "Manager",
                    $"enabled manager='{name}' server={m_IsServer} focusedHz=10",
                    this);
            }
        }

        private static void InstallOwnerAuthorityPoseSyncHook()
        {
            NetworkOwnerMotionAuthorityHooks.PositionAccepted -= SyncTraversalRelativePositionFromOwnerAuthority;
            NetworkOwnerMotionAuthorityHooks.PositionAccepted += SyncTraversalRelativePositionFromOwnerAuthority;
            NetworkOwnerMotionAuthorityHooks.PositionRejectionRequested -= RejectOwnerAuthorityPoseDuringInteractiveTransition;
            NetworkOwnerMotionAuthorityHooks.PositionRejectionRequested += RejectOwnerAuthorityPoseDuringInteractiveTransition;
            NetworkOwnerMotionAuthorityHooks.ExternalRootWriteAllowanceRequested -= AllowInteractiveTraversalRootWrite;
            NetworkOwnerMotionAuthorityHooks.ExternalRootWriteAllowanceRequested += AllowInteractiveTraversalRootWrite;
            NetworkOwnerMotionAuthorityHooks.ContinuousOwnerPoseRequested -= IsContinuousInteractiveOwnerPose;
            NetworkOwnerMotionAuthorityHooks.ContinuousOwnerPoseRequested += IsContinuousInteractiveOwnerPose;
        }

        private static void UninstallOwnerAuthorityPoseSyncHook()
        {
            NetworkOwnerMotionAuthorityHooks.PositionAccepted -= SyncTraversalRelativePositionFromOwnerAuthority;
            NetworkOwnerMotionAuthorityHooks.PositionRejectionRequested -= RejectOwnerAuthorityPoseDuringInteractiveTransition;
            NetworkOwnerMotionAuthorityHooks.ExternalRootWriteAllowanceRequested -= AllowInteractiveTraversalRootWrite;
            NetworkOwnerMotionAuthorityHooks.ContinuousOwnerPoseRequested -= IsContinuousInteractiveOwnerPose;
        }

        private static bool IsContinuousInteractiveOwnerPose(Character character)
        {
            if (!TryGetActiveInteractiveTraversal(
                    character,
                    out TraversalStance stance,
                    out _))
            {
                return false;
            }

            // Entry/exit transition clips are finite operations and retain reliable ordered
            // delivery. Once MotionInteractive enters its update loop, each world pose replaces
            // the preceding pose and belongs on the continuous prediction stream.
            return !TryGetInInteractiveTransition(character, stance, out bool inTransition) ||
                   !inTransition;
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

            string reason = $"traversal-interactive-transition:{interactive.name}";
            if (NetworkTraversalClimbDiagnostics.IsFocused(character.gameObject))
            {
                NetworkCharacter networkCharacter = character.GetComponent<NetworkCharacter>();
                NetworkTraversalClimbDiagnostics.Log(
                    "OwnerPoseHook",
                    $"actor={networkCharacter?.NetworkId ?? 0} role={networkCharacter?.CurrentRole.ToString() ?? "none"} " +
                    $"result=rejected reason='{reason}' requested={NetworkTraversalClimbDiagnostics.Vector(ownerAuthorityPosition)} " +
                    $"current={NetworkTraversalClimbDiagnostics.Vector(character.transform.position)} " +
                    $"traverse='{interactive.name}' transition={inTransition}",
                    character);
            }

            return reason;
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

            // Rebuild the traversal anchor from the accepted network root using the exact
            // inverse of MotionInteractive's Driver.SetPosition conversion. CharacterPosition
            // is intended for initial placement and subtracts Driver.SkinWidth; using it for
            // every accepted owner pose makes the server write that skin-width offset back on
            // its next interactive update, producing a vertical owner/server feedback loop.
            float halfHeight = character.Motion.Height * 0.5f;
            Vector3 anchorOffset = interactive.MotionInteractive.Anchor switch
            {
                Anchor.Crown => Vector3.up * halfHeight,
                Anchor.Center => Vector3.zero,
                Anchor.Feet => Vector3.down * halfHeight,
                _ => throw new ArgumentOutOfRangeException()
            };
            Vector3 anchorPosition = ownerAuthorityPosition + anchorOffset;
            Vector3 localPosition = interactive.Transform.InverseTransformPoint(anchorPosition);
            Vector3 previousRelative = s_TraversalStanceRelativePositionProperty.GetValue(stance) is Vector3 previous
                ? previous
                : default;

            float halfWidth = interactive.Width * 0.5f;
            localPosition.x = Mathf.Clamp(localPosition.x, -halfWidth, halfWidth);
            localPosition.y = 0f;
            localPosition.z = Mathf.Clamp(localPosition.z, interactive.PositionA, interactive.PositionB);

            s_TraversalStanceRelativePositionProperty.SetValue(stance, localPosition);

            if (NetworkTraversalClimbDiagnostics.IsFocused(character.gameObject))
            {
                NetworkCharacter networkCharacter = character.GetComponent<NetworkCharacter>();
                NetworkTraversalClimbDiagnostics.Log(
                    "RelativePose",
                    $"actor={networkCharacter?.NetworkId ?? 0} role={networkCharacter?.CurrentRole.ToString() ?? "none"} " +
                    $"traverse='{interactive.name}' owner={NetworkTraversalClimbDiagnostics.Vector(ownerAuthorityPosition)} " +
                    $"anchor={NetworkTraversalClimbDiagnostics.Vector(anchorPosition)} " +
                    $"before={NetworkTraversalClimbDiagnostics.Vector(previousRelative)} " +
                    $"after={NetworkTraversalClimbDiagnostics.Vector(localPosition)} " +
                    $"bounds={interactive.PositionA:F3}/{interactive.PositionB:F3} width={interactive.Width:F3}",
                    character,
                    $"relative-pose:{character.GetInstanceID()}");
            }

            float now = Time.realtimeSinceStartup;
            if ((localPosition - previousRelative).sqrMagnitude <= 0.0001f &&
                now - s_LastOwnerAuthorityPoseSyncLogRealtime < 0.25f)
            {
                return;
            }

            NetworkTraversalManager manager = Instance;
            if (manager != null && manager.m_LogNetworkMessages)
            {
                Debug.Log(
                    $"[TraversalPoseDebug][Manager] synced owner-authority traversal relative position " +
                    $"character='{character.name}' traverse='{interactive.name}:{interactive.GetType().Name}' " +
                    $"ownerRoot={FormatVector(ownerAuthorityPosition)} anchor={FormatVector(anchorPosition)} " +
                    $"previousRelative={FormatVector(previousRelative)} relative={FormatVector(localPosition)} " +
                    $"boundsA={interactive.PositionA:F3} boundsB={interactive.PositionB:F3} width={interactive.Width:F3}",
                    character);
                s_LastOwnerAuthorityPoseSyncLogRealtime = now;
            }
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
            if (controller == null || networkId == 0 || !controller.IsReadyForNetworkRouting) return;

            if (m_Controllers.TryGetValue(
                    networkId,
                    out NetworkTraversalController existing) &&
                ReferenceEquals(existing, controller))
            {
                return;
            }

            m_Controllers[networkId] = controller;
            RegisterOwnedEntityMapping(networkId);
            FlushPendingState(networkId, controller);

            if (m_LogNetworkMessages)
            {
                Debug.Log($"[NetworkTraversalManager] Registered controller for NetworkId={networkId}");
            }
        }

        public void UnregisterController(uint networkId)
        {
            if (!m_Controllers.TryGetValue(networkId, out NetworkTraversalController controller)) return;

            m_Controllers.Remove(networkId);
            if (controller != null)
            {
                m_PatchHooks?.ClearLedgeEdgeIntent(controller.GetComponent<Character>());
                NetworkTraversalClimbDiagnostics.SetCharacterFocus(
                    controller.gameObject,
                    networkId,
                    false);
            }

            SecurityIntegration.UnregisterEntity(networkId);
            if (m_LogNetworkMessages)
            {
                Debug.Log($"[NetworkTraversalManager] Unregistered controller for NetworkId={networkId}");
            }
        }

        public NetworkTraversalController GetController(uint networkId)
        {
            if (!m_Controllers.TryGetValue(networkId, out NetworkTraversalController controller) ||
                controller == null ||
                !controller.IsReadyForNetworkRouting)
            {
                return null;
            }

            return controller;
        }

        internal void ClearLedgeEdgeIntent(Character character)
        {
            m_PatchHooks?.ClearLedgeEdgeIntent(character);
        }

        public void SendTraversalRequest(NetworkTraversalRequest request)
        {
            if (TrySendTraversalRequest(request, out _)) return;
        }

        public bool TrySendTraversalRequest(
            NetworkTraversalRequest request,
            out TraversalRouteStatus routeStatus)
        {
            routeStatus = ResolveRequestRouteStatus(request.ActorNetworkId);
            if (routeStatus != TraversalRouteStatus.Ready)
            {
                WarnRateLimited(
                    $"request-route:{routeStatus}",
                    $"[NetworkTraversalManager] Traversal request failed closed because the route is {routeStatus}. " +
                    "The request was not queued or applied locally.");
                return false;
            }

            if (m_LogNetworkMessages)
            {
                Debug.Log($"[NetworkTraversalManager] Sending traversal request: Action={request.Action}, RequestId={request.RequestId}");
            }

            try
            {
                OnSendTraversalRequest.Invoke(request);
                return true;
            }
            catch (Exception exception)
            {
                routeStatus = TraversalRouteStatus.TransportUnavailable;
                WarnRateLimited(
                    "request-send-exception",
                    $"[NetworkTraversalManager] Traversal request transport failed: {exception.Message}");
                return false;
            }
        }

        public TraversalRouteStatus ResolveRequestRouteStatus(uint actorNetworkId)
        {
            if (OnSendTraversalRequest == null)
            {
                return TraversalRouteStatus.TransportUnavailable;
            }

            TraversalRouteStatus status;
            if (OnResolveRequestRouteStatusForActor != null)
            {
                status = OnResolveRequestRouteStatusForActor.Invoke(actorNetworkId);
            }
            else
            {
#pragma warning disable CS0618 // Compatibility path for transports compiled before actor-aware routing.
                status = OnResolveRequestRouteStatus?.Invoke() ?? TraversalRouteStatus.Ready;
#pragma warning restore CS0618
            }

            return status == TraversalRouteStatus.Unknown
                ? TraversalRouteStatus.TransportUnavailable
                : status;
        }

        [Obsolete("Use ResolveRequestRouteStatus(uint actorNetworkId) to validate the exact requesting actor.")]
        public TraversalRouteStatus ResolveRequestRouteStatus()
        {
            if (OnSendTraversalRequest == null)
            {
                return TraversalRouteStatus.TransportUnavailable;
            }

#pragma warning disable CS0618 // Compatibility path for callers that cannot yet supply an actor id.
            TraversalRouteStatus status = OnResolveRequestRouteStatus != null
                ? OnResolveRequestRouteStatus.Invoke()
                : OnResolveRequestRouteStatusForActor?.Invoke(0) ?? TraversalRouteStatus.Ready;
#pragma warning restore CS0618
            return status == TraversalRouteStatus.Unknown
                ? TraversalRouteStatus.TransportUnavailable
                : status;
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
                if (request.RequestId == 0 ||
                    request.ActorNetworkId == 0 ||
                    request.TargetNetworkId == 0 ||
                    request.TargetNetworkId != request.ActorNetworkId)
                {
                    SendRejectedResponse(senderClientId, request, TraversalRejectionReason.IdentityMismatch);
                    return;
                }

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
                    SendRejectedResponse(senderClientId, request, TraversalRejectionReason.ControllerNotReady);
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

        /// <summary>
        /// Applies a request originating from trusted server gameplay code. This deliberately bypasses
        /// client ownership/rate checks, but retains controller identity, action, and runtime validation.
        /// Host-player input must use the normal request route so it receives the same validation as a client.
        /// </summary>
        public async Task<NetworkTraversalResponse> ProcessTrustedServerRequestAsync(
            NetworkTraversalRequest request)
        {
            if (!m_IsServer)
            {
                return CreateRejectedResponse(request, TraversalRejectionReason.NotAuthorized);
            }

            if (request.ActorNetworkId == 0 ||
                request.TargetNetworkId == 0 ||
                request.ActorNetworkId != request.TargetNetworkId)
            {
                return CreateRejectedResponse(request, TraversalRejectionReason.IdentityMismatch);
            }

            NetworkTraversalController controller = GetController(request.TargetNetworkId);
            if (controller == null)
            {
                return CreateRejectedResponse(request, TraversalRejectionReason.ControllerNotReady);
            }

            try
            {
                NetworkTraversalResponse response = await controller.ProcessTraversalRequestAsync(
                    request,
                    NetworkTransportBridge.InvalidClientId);
                response.ActorNetworkId = request.ActorNetworkId;
                response.CorrelationId = request.CorrelationId;
                return response;
            }
            catch (Exception exception)
            {
                WarnRateLimited(
                    $"trusted-request:{request.TargetNetworkId}",
                    $"[NetworkTraversalManager] Trusted server traversal request failed: {exception.Message}");
                return CreateRejectedResponse(request, TraversalRejectionReason.Exception);
            }
        }

        public void ReceiveTraversalResponse(NetworkTraversalResponse response, uint targetNetworkId)
        {
            uint actorId = response.ActorNetworkId != 0 ? response.ActorNetworkId : targetNetworkId;
            NetworkTraversalController controller = GetController(actorId);
            if (controller != null)
            {
                controller.ReceiveTraversalResponse(response);
                return;
            }

            CachePendingResponse(actorId, response);
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
            if (controller != null)
            {
                controller.ReceiveTraversalChangeBroadcast(broadcast);
                return;
            }

            CachePendingBroadcast(broadcast);
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
            if (controller != null)
            {
                controller.ReceiveFullSnapshot(snapshot);
                return;
            }

            CachePendingSnapshot(snapshot);
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

            OnSendTraversalResponse?.Invoke(senderClientId, CreateRejectedResponse(request, reason));
        }

        private static NetworkTraversalResponse CreateRejectedResponse(
            in NetworkTraversalRequest request,
            TraversalRejectionReason reason)
        {
            return new NetworkTraversalResponse
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
                StateVersion = 0,
                Error = reason.ToString()
            };
        }

        private void CachePendingSnapshot(in NetworkTraversalSnapshot snapshot)
        {
            if (snapshot.NetworkId == 0) return;
            EnsurePendingCharacterCapacity(snapshot.NetworkId);

            if (m_PendingSnapshots.TryGetValue(snapshot.NetworkId, out PendingSnapshot existing) &&
                !ShouldReplaceSnapshot(existing.Value, snapshot))
            {
                return;
            }

            m_PendingSnapshots[snapshot.NetworkId] = new PendingSnapshot
            {
                Value = snapshot,
                ReceivedAt = Time.unscaledTime
            };

            TraceTraversal(
                $"cached latest traversal snapshot for NetworkId={snapshot.NetworkId} " +
                "until its controller becomes ready");
        }

        private void CachePendingBroadcast(in NetworkTraversalBroadcast broadcast)
        {
            if (broadcast.NetworkId == 0) return;
            EnsurePendingCharacterCapacity(broadcast.NetworkId);

            if (!m_PendingBroadcasts.TryGetValue(broadcast.NetworkId, out List<PendingBroadcast> pending))
            {
                pending = new List<PendingBroadcast>(4);
                m_PendingBroadcasts[broadcast.NetworkId] = pending;
            }

            RemoveExpiredBroadcasts(pending, Time.unscaledTime);
            int maxEntries = Mathf.Max(1, m_MaxPendingTransientStatesPerCharacter);
            while (pending.Count >= maxEntries)
            {
                pending.RemoveAt(0);
            }

            pending.Add(new PendingBroadcast
            {
                Value = broadcast,
                ReceivedAt = Time.unscaledTime
            });

            TraceTraversal(
                $"temporarily cached traversal events for NetworkId={broadcast.NetworkId} " +
                $"while its controller is not ready (TTL={Mathf.Max(0.1f, m_TransientStateTtl):F1}s)");
        }

        private void CachePendingResponse(uint actorNetworkId, in NetworkTraversalResponse response)
        {
            if (actorNetworkId == 0) return;
            EnsurePendingCharacterCapacity(actorNetworkId);

            if (!m_PendingResponses.TryGetValue(actorNetworkId, out List<PendingResponse> pending))
            {
                pending = new List<PendingResponse>(2);
                m_PendingResponses[actorNetworkId] = pending;
            }

            RemoveExpiredResponses(pending, Time.unscaledTime);
            int maxEntries = Mathf.Max(1, m_MaxPendingTransientStatesPerCharacter);
            while (pending.Count >= maxEntries)
            {
                pending.RemoveAt(0);
            }

            pending.Add(new PendingResponse
            {
                Value = response,
                ReceivedAt = Time.unscaledTime
            });

            TraceTraversal(
                $"temporarily cached a traversal response for NetworkId={actorNetworkId} " +
                "while its controller is not ready");
        }

        private void FlushPendingState(uint networkId, NetworkTraversalController controller)
        {
            if (controller == null || !controller.IsReadyForNetworkRouting) return;

            float now = Time.unscaledTime;
            if (m_PendingResponses.TryGetValue(networkId, out List<PendingResponse> responses))
            {
                RemoveExpiredResponses(responses, now);
                for (int i = 0; i < responses.Count; i++)
                {
                    controller.ReceiveTraversalResponse(responses[i].Value);
                }

                m_PendingResponses.Remove(networkId);
            }

            uint snapshotVersion = 0;
            if (m_PendingSnapshots.TryGetValue(networkId, out PendingSnapshot pendingSnapshot))
            {
                snapshotVersion = pendingSnapshot.Value.StateVersion;
                controller.ReceiveFullSnapshot(pendingSnapshot.Value);
                m_PendingSnapshots.Remove(networkId);
            }

            if (m_PendingBroadcasts.TryGetValue(networkId, out List<PendingBroadcast> broadcasts))
            {
                RemoveExpiredBroadcasts(broadcasts, now);
                broadcasts.Sort(ComparePendingBroadcasts);

                for (int i = 0; i < broadcasts.Count; i++)
                {
                    NetworkTraversalBroadcast value = broadcasts[i].Value;
                    if (snapshotVersion != 0 &&
                        value.StateVersion != 0 &&
                        !NetworkTraversalVersion.IsNewer(value.StateVersion, snapshotVersion))
                    {
                        continue;
                    }

                    controller.ReceiveTraversalChangeBroadcast(value);
                }

                m_PendingBroadcasts.Remove(networkId);
            }
        }

        private void CleanupExpiredPendingState()
        {
            float now = Time.unscaledTime;
            m_PendingStateRemovalBuffer.Clear();

            foreach (KeyValuePair<uint, List<PendingBroadcast>> pair in m_PendingBroadcasts)
            {
                RemoveExpiredBroadcasts(pair.Value, now);
                if (pair.Value.Count == 0) m_PendingStateRemovalBuffer.Add(pair.Key);
            }

            for (int i = 0; i < m_PendingStateRemovalBuffer.Count; i++)
            {
                uint networkId = m_PendingStateRemovalBuffer[i];
                m_PendingBroadcasts.Remove(networkId);
                TraceTraversal(
                    $"expired queued traversal events for NetworkId={networkId} " +
                    "before its controller became ready");
            }

            m_PendingStateRemovalBuffer.Clear();
            foreach (KeyValuePair<uint, List<PendingResponse>> pair in m_PendingResponses)
            {
                RemoveExpiredResponses(pair.Value, now);
                if (pair.Value.Count == 0) m_PendingStateRemovalBuffer.Add(pair.Key);
            }

            for (int i = 0; i < m_PendingStateRemovalBuffer.Count; i++)
            {
                uint networkId = m_PendingStateRemovalBuffer[i];
                m_PendingResponses.Remove(networkId);
                TraceTraversal(
                    $"expired a queued traversal response for NetworkId={networkId} " +
                    "before its controller became ready");
            }

            m_PendingStateRemovalBuffer.Clear();
        }

        private void RemoveExpiredBroadcasts(List<PendingBroadcast> pending, float now)
        {
            float ttl = Mathf.Max(0.1f, m_TransientStateTtl);
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                if (now - pending[i].ReceivedAt > ttl) pending.RemoveAt(i);
            }
        }

        private void RemoveExpiredResponses(List<PendingResponse> pending, float now)
        {
            float ttl = Mathf.Max(0.1f, m_TransientStateTtl);
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                if (now - pending[i].ReceivedAt > ttl) pending.RemoveAt(i);
            }
        }

        private void EnsurePendingCharacterCapacity(uint incomingNetworkId)
        {
            if (m_PendingSnapshots.ContainsKey(incomingNetworkId) ||
                m_PendingBroadcasts.ContainsKey(incomingNetworkId) ||
                m_PendingResponses.ContainsKey(incomingNetworkId))
            {
                return;
            }

            int maxCharacters = Mathf.Max(8, m_MaxPendingCharacterStates);
            var knownIds = new HashSet<uint>(m_PendingSnapshots.Keys);
            knownIds.UnionWith(m_PendingBroadcasts.Keys);
            knownIds.UnionWith(m_PendingResponses.Keys);
            if (knownIds.Count < maxCharacters) return;

            uint oldestId = 0;
            float oldestTime = float.PositiveInfinity;
            foreach (uint networkId in knownIds)
            {
                float receivedAt = GetOldestPendingTime(networkId);
                if (receivedAt >= oldestTime) continue;
                oldestTime = receivedAt;
                oldestId = networkId;
            }

            if (oldestId == 0) return;
            m_PendingSnapshots.Remove(oldestId);
            m_PendingBroadcasts.Remove(oldestId);
            m_PendingResponses.Remove(oldestId);
            WarnRateLimited(
                "pending-capacity",
                $"[NetworkTraversalManager] Evicted pending traversal state for NetworkId={oldestId}; " +
                $"the bounded readiness cache reached {maxCharacters} characters.");
        }

        private float GetOldestPendingTime(uint networkId)
        {
            float oldest = float.PositiveInfinity;
            if (m_PendingSnapshots.TryGetValue(networkId, out PendingSnapshot snapshot))
            {
                oldest = Mathf.Min(oldest, snapshot.ReceivedAt);
            }

            if (m_PendingBroadcasts.TryGetValue(networkId, out List<PendingBroadcast> broadcasts) &&
                broadcasts.Count > 0)
            {
                oldest = Mathf.Min(oldest, broadcasts[0].ReceivedAt);
            }

            if (m_PendingResponses.TryGetValue(networkId, out List<PendingResponse> responses) &&
                responses.Count > 0)
            {
                oldest = Mathf.Min(oldest, responses[0].ReceivedAt);
            }

            return oldest;
        }

        private static bool ShouldReplaceSnapshot(
            in NetworkTraversalSnapshot current,
            in NetworkTraversalSnapshot incoming)
        {
            if (incoming.StateVersion != 0 && current.StateVersion != 0)
            {
                if (incoming.StateVersion == current.StateVersion)
                {
                    return incoming.ServerTime >= current.ServerTime;
                }

                return NetworkTraversalVersion.IsNewer(incoming.StateVersion, current.StateVersion);
            }

            return incoming.ServerTime >= current.ServerTime;
        }

        private static int ComparePendingBroadcasts(PendingBroadcast left, PendingBroadcast right)
        {
            uint leftVersion = left.Value.StateVersion;
            uint rightVersion = right.Value.StateVersion;
            if (leftVersion != 0 && rightVersion != 0 && leftVersion != rightVersion)
            {
                return NetworkTraversalVersion.IsNewer(leftVersion, rightVersion) ? 1 : -1;
            }

            int timeComparison = left.Value.ServerTime.CompareTo(right.Value.ServerTime);
            return timeComparison != 0 ? timeComparison : left.ReceivedAt.CompareTo(right.ReceivedAt);
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

            m_PatchHooks.Initialize(m_IsServer, true, DiagnosticsEnabled);
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
            if (!m_LogNetworkMessages) return;

            Debug.Log($"[TraversalTrace][Manager] server={m_IsServer} controllers={m_Controllers.Count} {message}", this);
        }

        private void WarnRateLimited(string key, string message, float interval = 5f)
        {
            float now = Time.unscaledTime;
            if (m_DiagnosticTimes.TryGetValue(key, out float lastTime) &&
                now - lastTime < Mathf.Max(0.1f, interval))
            {
                return;
            }

            m_DiagnosticTimes[key] = now;
            Debug.LogWarning(message, this);
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
