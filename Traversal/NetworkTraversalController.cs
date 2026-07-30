#if GC2_TRAVERSAL
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Characters.Animim;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.Traversal;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Traversal
{
    [RequireComponent(typeof(Character))]
    [RequireComponent(typeof(NetworkCharacter))]
    [AddComponentMenu("Game Creator/Network/Traversal/Network Traversal Controller")]
    [DefaultExecutionOrder(ApplicationManager.EXECUTION_ORDER_DEFAULT + 5)]
    public class NetworkTraversalController : MonoBehaviour
    {
        [Serializable]
        private struct PendingTraversalRequest
        {
            public NetworkTraversalRequest Request;
            public float SentTime;
        }

        private sealed class ServerStartAcknowledgement
        {
            public uint Sequence;
            public uint CorrelationId;
            public Traverse Target;
            public bool Acknowledged;
            public float CreatedAt;
        }

        private sealed class ClientAuthoritativeStateApply
        {
            public uint Sequence;
            public uint StateVersion;
            public uint CorrelationId;
            public bool IsTraversing;
            public int TraverseHash;
            public string TraverseIdString;
            public Task<bool> Completion;
        }

        private struct AuthoritativeMotionOperation
        {
            public uint Sequence;
            public uint CorrelationId;
            public uint StateVersion;
            public int ExpectedTraverseInstanceId;
            public float ExpiresAt;

            public bool IsArmed => Sequence != 0 && ExpectedTraverseInstanceId != 0;
        }

        private struct PendingUnresolvedBroadcast
        {
            public NetworkTraversalBroadcast Value;
            public float ReceivedAt;
        }

        [Header("Network Settings")]
        [SerializeField] private bool m_OptimisticUpdates;

        [Header("Sync Settings")]
        [SerializeField] private float m_FullSyncInterval = 5f;

        [Header("Validation")]
        [SerializeField] private bool m_LogRejections;

        [Header("Debug")]
        [SerializeField] private bool m_LogAllChanges;

        public event Action<NetworkTraversalRequest> OnTraversalRequested;
        public event Action<NetworkTraversalBroadcast> OnTraversalApplied;
        public event Action<TraversalRejectionReason, string> OnTraversalRejected;

        private Character m_Character;
        private NetworkCharacter m_NetworkCharacter;

        private bool m_IsServer;
        private bool m_IsLocalClient;
        private bool m_IsRemoteClient;

        private ushort m_NextRequestId = 1;
        private ushort m_LastIssuedRequestId = 1;

        private bool m_IsRegistered;
        private uint m_RegisteredNetworkId;

        private bool m_SuppressInterception;
        private float m_LastFullSync;

        private TraversalStance m_TraversalStance;
        private bool m_HasStanceSubscription;
        private UnitPlayerDirectionalNetwork m_NetworkDirectionalPlayer;
        private bool m_HasNetworkDirectionalJumpSubscription;
        private bool m_HasActiveAuthoritativeRequest;
        private NetworkTraversalRequest m_ActiveAuthoritativeRequest;
        private bool m_LastTryJumpStartedInteractiveConnection;
        private bool m_HasDeferredStartBroadcastRequest;
        private NetworkTraversalRequest m_DeferredStartBroadcastRequest;
        private AuthoritativeMotionOperation m_PendingAuthoritativeMotionEnter;
        private AuthoritativeMotionOperation m_PendingAuthoritativeMotionExit;
        private uint m_NextAuthoritativeMotionSequence;
        private float m_LastEdgeConnectionRequestTime = -100f;

        private uint m_ServerStateVersion;
        private uint m_LastAppliedStateVersion;
        private uint m_LatestTransientSnapshotVersion;
        private uint m_LastTraversalAppliedEventStateVersion;
        private uint m_LastTraversalAppliedEventCorrelationId;
        private bool m_LastAppliedIsTraversing;
        private int m_LastAppliedTraverseHash;
        private string m_LastAppliedTraverseIdString = string.Empty;
        private uint m_ClientApplySequence;
        private ClientAuthoritativeStateApply m_ClientAuthoritativeStateApply;
        private uint m_NextServerStartSequence;
        private uint m_ServerOwnerMotionOperationId;
        private bool m_HasAppliedAuthoritativeState;
        private bool m_ServerOwnerMotionWindowOpen;
        private bool m_ServerOwnerMotionUsesClientAuthority;
        private int m_ProtectedConnectionLinkInstanceId;
        private uint m_ProtectedConnectionLinkCorrelationId;
        private uint m_ProtectedConnectionLinkStateVersion;
        private float m_ProtectedConnectionLinkExpiresAt;

        private bool m_ClimbDiagnosticFocused;
        private float m_PullUpDiagnosticUntilRealtime;
        private ushort m_ClimbDiagnosticRequestId;
        private uint m_ClimbDiagnosticCorrelationId;
        private string m_ClimbDiagnosticAction = string.Empty;
        private float m_LastClimbAnimatorNormalizedTime = -1f;
        private int m_LastClimbAnimatorStateHash;
        private string m_LastClimbDominantClip = string.Empty;
        private float m_LastClimbDominantClipChangeRealtime = -100f;
        private bool m_HasClimbDiagnosticSnapshot;
        private Vector3 m_LastClimbDiagnosticSnapshotRelative;
        private uint m_LastClimbDiagnosticSnapshotVersion;
        private Animator m_ClimbDiagnosticAnimator;

        private bool m_HasStoredTraversalMotionValues;
        private float m_StoredTraversalLinearSpeed;
        private float m_StoredTraversalAngularSpeed;

        private bool m_HostLocalInteractiveStateStarted;
        private bool m_IsSnapshotRestoredTraversal;
        private int m_HostLocalInteractiveStateLayer = -1;
        private float m_HostLocalInteractiveStateTransitionOut;
        private TraverseInteractive m_LastEdgeConnectionSource;
        private Traverse m_LastEdgeConnectionTarget;
        private Vector3 m_LastEdgeConnectionLocalPosition;
        private Vector3 m_LastEdgeConnectionLocalDirection;
        private bool m_LastEdgeConnectionEdgeB;
        private float m_LastEdgeConnectionCandidateTime = -100f;

        private const float TRAVERSE_ID_POSITION_SCALE = 100f;
        private const float TRAVERSE_ID_ROTATION_SCALE = 10f;
        private const int TRAVERSE_RESOLVE_LOG_CANDIDATE_LIMIT = 12;

        private static readonly HashSet<string> s_LoggedTraverseResolutionFailures = new();

        private readonly Dictionary<ulong, PendingTraversalRequest> m_PendingRequests = new(16);
        private readonly Dictionary<uint, float> m_RecentlyAppliedCorrelations = new(16);
        private readonly List<ulong> m_PendingRemovalBuffer = new(8);
        private readonly List<uint> m_CorrelationRemovalBuffer = new(8);
        private readonly Dictionary<uint, ServerStartAcknowledgement> m_ServerStartAcknowledgements = new(4);
        private readonly List<uint> m_StartAcknowledgementRemovalBuffer = new(4);
        private readonly Dictionary<string, float> m_DiagnosticTimes = new(StringComparer.Ordinal);
        private readonly SemaphoreSlim m_ServerRequestGate = new(1, 1);
        private readonly List<PendingUnresolvedBroadcast> m_PendingUnresolvedBroadcasts = new(4);

        private bool m_HasPendingUnresolvedSnapshot;
        private NetworkTraversalSnapshot m_PendingUnresolvedSnapshot;
        private float m_NextUnresolvedStateRetryTime;

        private Coroutine m_PendingServerExitSnapshotCoroutine;
        private const float REQUEST_TIMEOUT_SECONDS = 8f;
        private const float SERVER_START_ACKNOWLEDGEMENT_SECONDS = 1f;
        private const float TRAVERSAL_CLEANUP_TIMEOUT_SECONDS = 1f;
        private const int MAX_NETWORK_IDENTITY_LENGTH = 512;
        private const float TRAVERSAL_POSE_AUTHORITY_REFRESH_SECONDS = 0.35f;
        private const float TRAVERSAL_POSE_AUTHORITY_EXIT_GRACE_SECONDS = 0.25f;
        private const float SERVER_OWNER_MOTION_WINDOW_SECONDS = 0.5f;
        private const float SERVER_OWNER_MOTION_EXIT_GRACE_SECONDS = 0.2f;
        private const float EDGE_CONNECTION_REQUEST_INTERVAL_SECONDS = 0.25f;
        private const float EDGE_CONNECTION_JUMP_MEMORY_SECONDS = 1.25f;
        private const float AUTHORITATIVE_CONNECTION_EXIT_SUPPRESSION_SECONDS = 2f;
        private const float UNRESOLVED_TRANSIENT_TTL_SECONDS = 2f;
        private const float UNRESOLVED_STATE_RETRY_INTERVAL_SECONDS = 0.1f;
        private const int MAX_PENDING_UNRESOLVED_BROADCASTS = 8;
        private const float DOWNWARD_JUMP_INPUT_THRESHOLD = -0.25f;
        private const float DOWNWARD_JUMP_VERTICAL_THRESHOLD = -0.1f;
        private const string INTERACTIVE_CONNECTION_ACTION_ID = "__network_interactive_connection";
        private const BindingFlags MOTION_INTERACTIVE_FIELD_FLAGS =
            BindingFlags.Instance | BindingFlags.NonPublic;

        private static readonly FieldInfo s_MotionInteractiveAnimationStateField =
            typeof(MotionInteractive).GetField("m_AnimationState", MOTION_INTERACTIVE_FIELD_FLAGS);

        private static readonly FieldInfo s_MotionInteractiveLayerField =
            typeof(MotionInteractive).GetField("m_Layer", MOTION_INTERACTIVE_FIELD_FLAGS);

        private static readonly FieldInfo s_MotionInteractiveAnimationSpeedField =
            typeof(MotionInteractive).GetField("m_AnimationSpeed", MOTION_INTERACTIVE_FIELD_FLAGS);

        private static readonly FieldInfo s_MotionInteractiveInputDirectionField =
            typeof(MotionInteractive).GetField("m_InputDirection", MOTION_INTERACTIVE_FIELD_FLAGS);

        private static readonly FieldInfo s_MotionInteractiveInputXField =
            typeof(MotionInteractive).GetField("m_InputX", MOTION_INTERACTIVE_FIELD_FLAGS);

        private static readonly FieldInfo s_MotionInteractiveInputYField =
            typeof(MotionInteractive).GetField("m_InputY", MOTION_INTERACTIVE_FIELD_FLAGS);

        private static readonly FieldInfo s_MotionInteractiveInputZField =
            typeof(MotionInteractive).GetField("m_InputZ", MOTION_INTERACTIVE_FIELD_FLAGS);

        private static readonly FieldInfo s_MotionLinkAnimationClipField =
            typeof(MotionLink).GetField("m_AnimationClip", MOTION_INTERACTIVE_FIELD_FLAGS);

        private static readonly FieldInfo s_MotionLinkMaskField =
            typeof(MotionLink).GetField("m_Mask", MOTION_INTERACTIVE_FIELD_FLAGS);

        private static readonly FieldInfo s_MotionLinkAnimationStateField =
            typeof(MotionLink).GetField("m_AnimationState", MOTION_INTERACTIVE_FIELD_FLAGS);

        private static readonly FieldInfo s_MotionLinkLayerField =
            typeof(MotionLink).GetField("m_Layer", MOTION_INTERACTIVE_FIELD_FLAGS);

        private static readonly FieldInfo s_MotionLinkAnimationSpeedField =
            typeof(MotionLink).GetField("m_AnimationSpeed", MOTION_INTERACTIVE_FIELD_FLAGS);

        private static readonly FieldInfo s_StatesOutputLayersField =
            typeof(StatesOutput).GetField("m_Layers", BindingFlags.Instance | BindingFlags.NonPublic);

        private static readonly PropertyInfo s_TraversalStanceRelativePositionProperty =
            typeof(TraversalStance).GetProperty(
                "RelativePosition",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        private static readonly PropertyInfo s_TraversalStanceAllowMovementProperty =
            typeof(TraversalStance).GetProperty(
                "AllowMovement",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        private static readonly PropertyInfo s_TraversalStanceInInteractiveTransitionProperty =
            typeof(TraversalStance).GetProperty(
                "InInteractiveTransition",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        private static readonly PropertyInfo s_TraversalStanceSnapshotTokenProperty =
            typeof(TraversalStance).GetProperty(
                "NetworkSnapshotToken",
                BindingFlags.Instance | BindingFlags.Public);

        private static readonly MethodInfo s_MotionInteractiveResumeSnapshotMethod =
            typeof(MotionInteractive).GetMethod(
                "NetworkResumeInteractiveSnapshot",
                BindingFlags.Instance | BindingFlags.Public,
                null,
                new[]
                {
                    typeof(TraverseInteractive),
                    typeof(Character),
                    typeof(TraversalToken)
                },
                null);

        private static readonly MethodInfo s_TraversalStanceRestoreSnapshotMethod =
            typeof(TraversalStance).GetMethod(
                "NetworkRestoreInteractiveSnapshot",
                BindingFlags.Instance | BindingFlags.Public,
                null,
                new[] { typeof(TraverseInteractive), typeof(Vector3) },
                null);

        private static readonly MethodInfo s_TraversalStanceClearSnapshotMethod =
            typeof(TraversalStance).GetMethod(
                "NetworkClearSnapshot",
                BindingFlags.Instance | BindingFlags.Public,
                null,
                Type.EmptyTypes,
                null);

        private static readonly MethodInfo s_TraversalStanceInvalidatePendingEnterMethod =
            typeof(TraversalStance).GetMethod(
                "NetworkInvalidatePendingEnter",
                BindingFlags.Instance | BindingFlags.Public,
                null,
                Type.EmptyTypes,
                null);

        public uint NetworkId => m_NetworkCharacter != null ? m_NetworkCharacter.NetworkId : 0;

        public bool IsServer => m_IsServer;
        public bool IsLocalClient => m_IsLocalClient;
        public bool IsRemoteClient => m_IsRemoteClient;
        internal bool IsApplyingAuthoritativeChange => m_SuppressInterception;
        private bool DiagnosticsEnabled =>
            m_LogAllChanges ||
            (NetworkTraversalManager.Instance != null && NetworkTraversalManager.Instance.DiagnosticsEnabled);
        public bool IsReadyForNetworkRouting =>
            isActiveAndEnabled &&
            m_NetworkCharacter != null &&
            m_NetworkCharacter.Role != NetworkCharacter.NetworkRole.None &&
            NetworkId != 0 &&
            ResolveTraversalStance() != null;

        internal bool CanAcceptPatchedRequest(out TraversalRouteStatus routeStatus)
        {
            RefreshRoutingRoleFromNetworkCharacter();

            if (!IsReadyForNetworkRouting)
            {
                routeStatus = TraversalRouteStatus.ControllerNotReady;
                return false;
            }

            if (m_IsRemoteClient)
            {
                routeStatus = TraversalRouteStatus.ControllerNotReady;
                return false;
            }

            // Traversal currently relies on the built-in driver's explicit owner/server motion
            // authority windows. Reject PurrDiction for every role, including a host-owned
            // character whose server role would otherwise bypass the owner-driver capability
            // check below.
            if (m_NetworkCharacter.PredictionBackend != NetworkPredictionBackend.BuiltIn)
            {
                routeStatus = TraversalRouteStatus.UnsupportedPredictionBackend;
                return false;
            }

            if (m_IsLocalClient && !HasOwnerMotionAuthorityForCurrentRole())
            {
                routeStatus = TraversalRouteStatus.UnsupportedPredictionBackend;
                return false;
            }

            if (m_IsServer && !m_IsLocalClient)
            {
                routeStatus = NetworkTraversalManager.Instance != null
                    ? TraversalRouteStatus.Ready
                    : TraversalRouteStatus.ManagerUnavailable;
                return routeStatus == TraversalRouteStatus.Ready;
            }

            NetworkTraversalManager manager = NetworkTraversalManager.Instance;
            routeStatus = manager != null
                ? manager.ResolveRequestRouteStatus(NetworkId)
                : TraversalRouteStatus.ManagerUnavailable;
            return routeStatus == TraversalRouteStatus.Ready;
        }

        /// <summary>
        /// Refreshes the controller's cached routing role from the already initialized
        /// NetworkCharacter. Patch interception can run in the same frame that ownership is
        /// assigned, before a transport bridge's periodic controller scan.
        /// </summary>
        internal void RefreshRoutingRoleFromNetworkCharacter()
        {
            if (m_NetworkCharacter == null)
            {
                m_NetworkCharacter = GetComponent<NetworkCharacter>();
            }

            if (m_NetworkCharacter == null ||
                m_NetworkCharacter.Role == NetworkCharacter.NetworkRole.None)
            {
                return;
            }

            bool isServer = m_NetworkCharacter.IsServerInstance;
            bool isLocalClient = m_NetworkCharacter.IsOwnerInstance;
            if (m_IsServer == isServer && m_IsLocalClient == isLocalClient)
            {
                return;
            }

            Initialize(isServer, isLocalClient);
        }

        private bool HasOwnerMotionAuthorityForCurrentRole()
        {
            if (!m_IsLocalClient) return true;

            if (m_NetworkCharacter?.OwnerMotionAuthority != null ||
                m_Character?.Driver is INetworkOwnerMotionAuthority)
            {
                return true;
            }

            return m_IsServer && m_Character?.Driver is INetworkServerOwnerMotionAuthority;
        }

        private void Awake()
        {
            m_Character = GetComponent<Character>();
            m_NetworkCharacter = GetComponent<NetworkCharacter>();
        }

        private void OnEnable()
        {
            EnsureTraversalStanceSubscription();
            EnsureNetworkDirectionalJumpSubscription();
        }

        private void OnDisable()
        {
            SetClimbDiagnosticFocus(false, "controller-disabled", null, null);
            ClearLedgeEdgeIntent();
            BeginClientApply();
            m_ClientAuthoritativeStateApply = null;
            ClearProtectedConnectionLink(null);
            CancelPendingServerExitSnapshot();
            CloseServerOwnerMotionWindow(0f);
            if (m_IsSnapshotRestoredTraversal)
            {
                bool previousSuppress = m_SuppressInterception;
                m_SuppressInterception = true;
                try
                {
                    TryClearSnapshotRestoredTraversal(m_TraversalStance ?? ResolveTraversalStance());
                }
                finally
                {
                    m_SuppressInterception = previousSuppress;
                }
            }
            StopHostLocalInteractiveMotionState();
            RestoreTraversalMotionValues("disable");
            m_PendingAuthoritativeMotionEnter = default;
            m_PendingAuthoritativeMotionExit = default;
            m_PendingUnresolvedBroadcasts.Clear();
            m_HasPendingUnresolvedSnapshot = false;
            m_PendingUnresolvedSnapshot = default;
            m_ServerStartAcknowledgements.Clear();
            RemoveTraversalStanceSubscription();
            RemoveNetworkDirectionalJumpSubscription();
            UnregisterFromManager();
        }

        private void Update()
        {
            EnsureRegisteredWithManager();
            EnsureTraversalStanceSubscription();
            EnsureNetworkDirectionalJumpSubscription();
            CleanupPendingRequests();
            CleanupServerStartAcknowledgements();
            CleanupAuthoritativeMotionOperations();
            RetryPendingUnresolvedAuthoritativeState();
            RefreshLocalTraversalPoseAuthority();
            RefreshServerOwnerMotionWindow();
            UpdateFocusedClimbDiagnostics();

            if (!m_IsServer) return;

            float now = Time.time;
            if (m_FullSyncInterval > 0f && now - m_LastFullSync >= m_FullSyncInterval)
            {
                NetworkTraversalManager.Instance?.BroadcastFullSnapshot(CaptureFullSnapshot());
                m_LastFullSync = now;
            }
        }

        public void Initialize(bool isServer, bool isLocalClient)
        {
            m_IsServer = isServer;
            m_IsLocalClient = isLocalClient;
            m_IsRemoteClient = !isServer && !isLocalClient;

            EnsureRegisteredWithManager();
            EnsureTraversalStanceSubscription();
            EnsureNetworkDirectionalJumpSubscription();

            if (DiagnosticsEnabled)
            {
                string role = m_IsServer ? "Server" : (m_IsLocalClient ? "LocalClient" : "RemoteClient");
                Debug.Log($"[NetworkTraversalController] {gameObject.name} initialized as {role}");
            }
        }

        public void RequestRunTraverseLink(TraverseLink link)
        {
            RequestTraversalAction(TraversalActionType.RunTraverseLink, link, default, default, null, alreadyAppliedLocally: false);
        }

        public void RequestEnterTraverseInteractive(TraverseInteractive interactive, InteractiveTransitionData transition = default)
        {
            RequestTraversalAction(TraversalActionType.EnterTraverseInteractive, interactive, default, default, null, alreadyAppliedLocally: false);
        }

        public void RequestTryCancel(Args args)
        {
            RequestTraversalAction(TraversalActionType.TryCancel, null, default, default, args, alreadyAppliedLocally: false);
        }

        public void RequestForceCancel()
        {
            RequestTraversalAction(TraversalActionType.ForceCancel, null, default, default, null, alreadyAppliedLocally: false);
        }

        public void RequestTryJump()
        {
            if (IsProtectedConnectionLinkActive(ResolveTraversalStance()?.Traverse))
            {
                LogTraversal(
                    $"ignored repeated TryJump while protected connection link is active " +
                    $"traverse='{FormatTraverse(ResolveTraversalStance()?.Traverse)}' " +
                    $"correlation={m_ProtectedConnectionLinkCorrelationId} " +
                    $"version={m_ProtectedConnectionLinkStateVersion}");
                return;
            }

            LogTraversal("direct RequestTryJump invoked");
            RequestTraversalAction(TraversalActionType.TryJump, null, default, default, null, alreadyAppliedLocally: false);
        }

        public void RequestTryAction(IdString actionId)
        {
            RequestTraversalAction(TraversalActionType.TryAction, null, actionId, default, null, alreadyAppliedLocally: false);
        }

        public void RequestTryStateEnter(IdString stateId)
        {
            RequestTraversalAction(TraversalActionType.TryStateEnter, null, default, stateId, null, alreadyAppliedLocally: false);
        }

        public void RequestTryStateExit()
        {
            RequestTraversalAction(TraversalActionType.TryStateExit, null, default, default, null, alreadyAppliedLocally: false);
        }

        internal void RequestRunTraverseLinkFromPatch(TraverseLink link, Character character)
        {
            if (!MatchesControlledCharacter(character)) return;
            RequestTraversalAction(TraversalActionType.RunTraverseLink, link, default, default, null, alreadyAppliedLocally: false);
        }

        internal void RequestEnterTraverseInteractiveFromPatch(TraverseInteractive interactive, Character character, InteractiveTransitionData transition)
        {
            if (!MatchesControlledCharacter(character)) return;

            TraversalStance stance = ResolveTraversalStance();
            Traverse currentTraverse = stance != null ? stance.Traverse : null;
            TraverseInteractive currentInteractive = currentTraverse as TraverseInteractive;
            bool sameInteractive = ReferenceEquals(currentInteractive, interactive);
            string validationReason = string.Empty;
            bool hasConfiguredConnection = currentInteractive != null &&
                !sameInteractive &&
                IsConfiguredInteractiveConnectionTarget(currentInteractive, interactive, out validationReason);

            if (currentInteractive == null)
            {
                validationReason = $"current traverse is '{FormatTraverse(currentTraverse)}'";
            }
            else if (sameInteractive)
            {
                validationReason = "target is already the current interactive";
            }

            LogTraversal(
                $"patched interactive enter intercepted target='{FormatTraverse(interactive)}' " +
                $"current='{FormatTraverse(currentTraverse)}' configuredConnection={hasConfiguredConnection} " +
                $"exitClip='{(transition.ExitAnimation != null ? transition.ExitAnimation.name : "null")}' " +
                $"exitLength={transition.ExitAnimationLength:F3} reason='{validationReason}'");

            if (sameInteractive)
            {
                LogTraversal(
                    $"ignored patched interactive enter because target is already active " +
                    $"target='{FormatTraverse(interactive)}' reason='{validationReason}'");
                return;
            }

            if (hasConfiguredConnection)
            {
                LogTraversal(
                    $"ignored direct configured interactive enter while traversing; " +
                    $"connections must use TryJump authority " +
                    $"from='{FormatTraverse(currentInteractive)}' to='{FormatTraverse(interactive)}' " +
                    $"exitClip='{(transition.ExitAnimation != null ? transition.ExitAnimation.name : "null")}' " +
                    $"reason='{validationReason}'");
                return;
            }

            LogTraversal(
                $"requesting regular interactive enter from patched Enter " +
                $"target='{FormatTraverse(interactive)}' reason='{validationReason}'");
            RequestEnterTraverseInteractive(interactive, transition);
        }

        internal void RequestTryCancelFromPatch(TraversalStance stance, Args args)
        {
            if (!MatchesControlledStance(stance)) return;
            RequestTryCancel(args);
        }

        internal void RequestForceCancelFromPatch(TraversalStance stance)
        {
            if (!MatchesControlledStance(stance)) return;
            RequestForceCancel();
        }

        internal void RequestTryJumpFromPatch(TraversalStance stance)
        {
            if (!MatchesControlledStance(stance)) return;

            LogTraversal(
                $"patched TryJump intercepted stanceTraverse='{FormatTraverse(stance.Traverse)}' " +
                $"character='{(stance.Character != null ? stance.Character.name : "null")}'");

            if (TryRequestInteractiveJumpConnectionFromPatch(stance))
            {
                LogTraversal("patched TryJump handled by traversal networking");
                return;
            }

            LogTraversal("patched TryJump forwarding as normal TryJump request");
            RequestTryJump();
        }

        internal void RequestTryActionFromPatch(TraversalStance stance, IdString actionId)
        {
            if (!MatchesControlledStance(stance)) return;
            RequestTryAction(actionId);
        }

        internal void RequestTryStateEnterFromPatch(TraversalStance stance, IdString stateId)
        {
            if (!MatchesControlledStance(stance)) return;
            RequestTryStateEnter(stateId);
        }

        internal void RequestTryStateExitFromPatch(TraversalStance stance)
        {
            if (!MatchesControlledStance(stance)) return;
            RequestTryStateExit();
        }

        internal Traverse ResolveInteractiveEdgeConnectionFromPatch(
            MotionInteractive motion,
            TraverseInteractive interactive,
            Character character,
            Vector3 currentLocalPosition,
            Vector3 localDirection,
            bool edgeB)
        {
            if (!MatchesControlledCharacter(character)) return null;
            if (motion == null || interactive == null) return null;
            if (m_IsRemoteClient) return null;

            string edge = edgeB ? "B" : "A";
            Args args = new Args(interactive.gameObject, character.gameObject);
            Traverse configuredContinue = edgeB ? interactive.ContinueB : interactive.ContinueA;

            if (configuredContinue != null)
            {
                if (configuredContinue.Motion == null || !configuredContinue.Motion.CanUse(args))
                {
                    LogTraversal(
                        $"edge {edge} authored continuation rejected by CanUse " +
                        $"from='{FormatTraverse(interactive)}' to='{FormatTraverse(configuredContinue)}'");
                    return null;
                }

                // A remote server proxy waits for the owning client's edge request. A host
                // owner uses the same manager/transport request route as a remote owner.
                if (m_IsServer && !m_IsLocalClient)
                {
                    return null;
                }

                float requestTime = Time.time;
                if (requestTime - m_LastEdgeConnectionRequestTime < EDGE_CONNECTION_REQUEST_INTERVAL_SECONDS)
                {
                    return null;
                }

                m_LastEdgeConnectionRequestTime = requestTime;
                LogTraversal(
                    $"edge {edge} routing authored continuation authoritatively " +
                    $"from='{FormatTraverse(interactive)}' to='{FormatTraverse(configuredContinue)}'");
                RequestTraversalAction(
                    configuredContinue is TraverseLink
                        ? TraversalActionType.RunTraverseLink
                        : TraversalActionType.EnterTraverseInteractive,
                    configuredContinue,
                    new IdString(INTERACTIVE_CONNECTION_ACTION_ID),
                    default,
                    null,
                    alreadyAppliedLocally: false);
                return null;
            }

            if (m_IsServer)
            {
                if (TrySelectInteractiveConnectionByLocalDirection(
                        interactive,
                        args,
                        currentLocalPosition,
                        localDirection,
                        out Traverse nextTraverse,
                        out string reason))
                {
                    StoreEdgeConnectionCandidate(interactive, nextTraverse, currentLocalPosition, localDirection, edgeB);
                    LogTraversal(
                        $"edge {edge} network connection candidate motion='{motion.name}' " +
                        $"from='{FormatTraverse(interactive)}' to='{FormatTraverse(nextTraverse)}' " +
                        $"reason='{reason}' autoTraverse=False waitForJump=True");
                    return null;
                }

                LogTraversal(
                    $"edge {edge} network connection candidate skipped motion='{motion.name}' " +
                    $"traverse='{FormatTraverse(interactive)}' reason='{reason}' autoTraverse=False waitForJump=True");
                return null;
            }

            if (!m_IsLocalClient)
            {
                return null;
            }

            float now = Time.time;
            if (now - m_LastEdgeConnectionRequestTime < EDGE_CONNECTION_REQUEST_INTERVAL_SECONDS)
            {
                return null;
            }

            m_LastEdgeConnectionRequestTime = now;

            if (TrySelectInteractiveConnectionByLocalDirection(
                    interactive,
                    args,
                    currentLocalPosition,
                    localDirection,
                    out Traverse requestedTraverse,
                    out string selectedReason))
            {
                StoreEdgeConnectionCandidate(interactive, requestedTraverse, currentLocalPosition, localDirection, edgeB);
                LogTraversal(
                    $"edge {edge} authoritative target candidate motion='{motion.name}' " +
                    $"from='{FormatTraverse(interactive)}' to='{FormatTraverse(requestedTraverse)}' " +
                    $"local={FormatVector(currentLocalPosition)} input={FormatVector(localDirection)} " +
                    $"reason='{selectedReason}' autoTraverse=False waitForJump=True");
                return null;
            }

            LogTraversal(
                $"edge {edge} has no authoritative target candidate motion='{motion.name}' " +
                $"traverse='{FormatTraverse(interactive)}' local={FormatVector(currentLocalPosition)} " +
                $"input={FormatVector(localDirection)} reason='{selectedReason}' autoTraverse=False waitForJump=True");
            return null;
        }

        internal bool ShouldSkipConnectionTransitionFromPatch(Traverse current, Traverse next, Character character)
        {
            if (!MatchesControlledCharacter(character)) return true;

            bool interactiveConnection = current is TraverseInteractive && next is TraverseInteractive;
            if (interactiveConnection)
            {
                LogTraversal(
                    $"interactive connection transition enabled from='{FormatTraverse(current)}' " +
                    $"to='{FormatTraverse(next)}'");
                return false;
            }

            return true;
        }

        private bool TryRequestInteractiveJumpConnectionFromPatch(TraversalStance stance)
        {
            if (stance == null)
            {
                LogTraversal("jump connection request skipped: stance is null");
                return false;
            }

            if (m_Character == null)
            {
                LogTraversal("jump connection request skipped: character is null");
                return false;
            }

            if (stance.Traverse is not TraverseInteractive interactive)
            {
                LogTraversal(
                    $"jump connection request skipped: active traverse is not interactive " +
                    $"traverse='{FormatTraverse(stance.Traverse)}'");
                return false;
            }

            Args args = new Args(interactive.gameObject, m_Character.gameObject);
            if (interactive.CanJump(args))
            {
                LogTraversal(
                    $"jump kept as traversal action because current interactive can jump " +
                    $"traverse='{FormatTraverse(interactive)}'");
                return false;
            }

            if (!TrySelectInteractiveJumpConnection(interactive, args, out Traverse requestedTraverse, out string reason))
            {
                if (HasDownwardInteractiveJumpInput(out string downwardInputReason))
                {
                    LogTraversal(
                        $"jump downward has no explicit interactive connection target; " +
                        $"requesting force cancel traverse='{FormatTraverse(interactive)}' " +
                        $"input='{downwardInputReason}' selectorReason='{reason}'");
                    RequestForceCancel();
                    return true;
                }

                LogTraversal(
                    $"jump has no explicit interactive connection target " +
                    $"traverse='{FormatTraverse(interactive)}' reason='{reason}'");
                return false;
            }

            if (requestedTraverse is not TraverseInteractive && requestedTraverse is not TraverseLink)
            {
                LogTraversal(
                    $"jump selected unsupported authoritative target " +
                    $"from='{FormatTraverse(interactive)}' to='{FormatTraverse(requestedTraverse)}' " +
                    $"reason='{reason}'");
                return false;
            }

            LogTraversal(
                $"jump requested authoritative target connection " +
                $"from='{FormatTraverse(interactive)}' to='{FormatTraverse(requestedTraverse)}' " +
                $"reason='{reason}'");

            RequestTraversalAction(
                requestedTraverse is TraverseLink
                    ? TraversalActionType.RunTraverseLink
                    : TraversalActionType.EnterTraverseInteractive,
                requestedTraverse,
                new IdString(INTERACTIVE_CONNECTION_ACTION_ID),
                default,
                null,
                alreadyAppliedLocally: false);
            return true;
        }

        private void RequestTraversalAction(
            TraversalActionType action,
            Traverse traverse,
            IdString actionId,
            IdString stateId,
            Args args,
            bool alreadyAppliedLocally)
        {
            RefreshRoutingRoleFromNetworkCharacter();

            if (m_IsRemoteClient)
            {
                LogTraversal($"request blocked on remote proxy action={action} traverse='{FormatTraverse(traverse)}'");
                if (m_LogRejections)
                {
                    Debug.LogWarning("[NetworkTraversalController] Cannot request traversal changes from a remote proxy");
                }

                return;
            }

            uint networkId = NetworkId;
            if (networkId == 0)
            {
                LogTraversal($"request blocked missing network id action={action} traverse='{FormatTraverse(traverse)}'");
                if (m_LogRejections)
                {
                    Debug.LogWarning("[NetworkTraversalController] Missing NetworkId; cannot send traversal request");
                }

                OnTraversalRejected?.Invoke(TraversalRejectionReason.TargetNotFound, "Missing NetworkId");
                return;
            }

            if (!IsReadyForNetworkRouting)
            {
                RejectLocalRequest(
                    TraversalRejectionReason.ControllerNotReady,
                    "NetworkTraversalController is waiting for its NetworkCharacter role and network id",
                    "controller-not-ready");
                return;
            }

            if (m_NetworkCharacter.PredictionBackend != NetworkPredictionBackend.BuiltIn)
            {
                RejectLocalRequest(
                    TraversalRejectionReason.UnsupportedPredictionBackend,
                    "Traversal currently supports only the built-in movement backend; PurrDiction Traversal is disabled to prevent partially synchronized motion",
                    "unsupported-prediction-backend");
                return;
            }

            if (m_IsLocalClient && !HasOwnerMotionAuthorityForCurrentRole())
            {
                RejectLocalRequest(
                    TraversalRejectionReason.UnsupportedPredictionBackend,
                    "The active owner movement backend does not implement INetworkOwnerMotionAuthority; traversal is disabled to prevent transform desynchronization",
                    "owner-motion-authority-missing");
                return;
            }

            if (RequiresTraverse(action) && traverse == null)
            {
                LogTraversal($"request blocked missing traverse reference action={action}");
                if (m_LogRejections)
                {
                    Debug.LogWarning($"[NetworkTraversalController] Action {action} requires a Traverse reference");
                }

                OnTraversalRejected?.Invoke(TraversalRejectionReason.InvalidAction, "Action requires Traverse reference");
                return;
            }

            int traverseHash = 0;
            string traverseId = string.Empty;
            if (traverse != null)
            {
                traverseId = BuildTraverseId(traverse);
                traverseHash = StableHashUtility.GetStableHash(traverseId);
            }

            uint argsSelfNetworkId = args != null ? ExtractNetworkId(args.Self) : networkId;
            uint argsTargetNetworkId = args != null ? ExtractNetworkId(args.Target) : networkId;

            if (ShouldSkipOutgoingTraversalRequest(action, traverse, traverseHash, traverseId, out string skipReason))
            {
                LogTraversal(
                    $"request skipped action={action} traverse='{traverseId}' hash={traverseHash} " +
                    $"reason='{skipReason}' pending={m_PendingRequests.Count}");
                return;
            }

            var request = new NetworkTraversalRequest
            {
                RequestId = GetNextRequestId(),
                ActorNetworkId = networkId,
                CorrelationId = NetworkCorrelation.Compose(networkId, m_LastIssuedRequestId),
                TargetNetworkId = networkId,
                Action = action,
                TraverseHash = traverseHash,
                TraverseIdString = traverseId,
                ActionIdHash = GetOptionalStableHash(actionId.String),
                ActionIdString = actionId.String,
                StateIdHash = GetOptionalStableHash(stateId.String),
                StateIdString = stateId.String,
                ArgsSelfNetworkId = argsSelfNetworkId,
                ArgsTargetNetworkId = argsTargetNetworkId
            };

            TrackFocusedTraversalRequest(request, traverse);

            LogTraversal(
                $"request built action={request.Action} requestId={request.RequestId} " +
                $"actor={request.ActorNetworkId} target={request.TargetNetworkId} " +
                $"correlation={request.CorrelationId} alreadyApplied={alreadyAppliedLocally} " +
                $"optimistic={m_OptimisticUpdates} traverse='{request.TraverseIdString}' " +
                $"hash={request.TraverseHash} actionId='{request.ActionIdString}' actionHash={request.ActionIdHash} " +
                $"stateId='{request.StateIdString}' stateHash={request.StateIdHash} " +
                $"self={request.ArgsSelfNetworkId} targetArg={request.ArgsTargetNetworkId}");
            LogTraversalPose(
                $"request-built action={request.Action} requestId={request.RequestId} " +
                $"correlation={request.CorrelationId} alreadyApplied={alreadyAppliedLocally}",
                traverse);

            NetworkTraversalManager manager = NetworkTraversalManager.Instance;
            bool trustedDedicatedServerRequest = m_IsServer && !m_IsLocalClient;
            if (!trustedDedicatedServerRequest && manager == null)
            {
                LogTraversal($"request blocked missing manager action={request.Action} requestId={request.RequestId}");
                if (m_LogRejections)
                {
                    Debug.LogWarning("[NetworkTraversalController] NetworkTraversalManager instance not found");
                }

                OnTraversalRejected?.Invoke(TraversalRejectionReason.TargetNotFound, "NetworkTraversalManager missing");
                return;
            }


            TraversalRouteStatus routeStatus = trustedDedicatedServerRequest
                ? TraversalRouteStatus.Ready
                : manager.ResolveRequestRouteStatus(request.ActorNetworkId);
            if (routeStatus != TraversalRouteStatus.Ready)
            {
                TraversalRejectionReason reason = routeStatus switch
                {
                    TraversalRouteStatus.PatchRequired => TraversalRejectionReason.PatchRequired,
                    TraversalRouteStatus.UnsupportedPredictionBackend => TraversalRejectionReason.UnsupportedPredictionBackend,
                    TraversalRouteStatus.ControllerNotReady => TraversalRejectionReason.ControllerNotReady,
                    _ => TraversalRejectionReason.RouteUnavailable
                };
                RejectLocalRequest(
                    reason,
                    $"Traversal request route is not ready: {routeStatus}",
                    $"route:{routeStatus}");
                return;
            }

            m_PendingRequests[GetPendingKey(request.ActorNetworkId, request.CorrelationId, request.RequestId)] =
                new PendingTraversalRequest
                {
                    Request = request,
                    SentTime = Time.time
                };

            OnTraversalRequested?.Invoke(request);

            if (trustedDedicatedServerRequest)
            {
                _ = ProcessLocalServerRequestAsync(request);
            }
            else
            {
                bool applyOptimistically = m_OptimisticUpdates &&
                    !alreadyAppliedLocally &&
                    !IsTraversalStartAction(request.Action);

                if (alreadyAppliedLocally || applyOptimistically)
                {
                    m_RecentlyAppliedCorrelations[request.CorrelationId] = Time.time;
                }

                if (m_OptimisticUpdates &&
                    !alreadyAppliedLocally &&
                    IsTraversalStartAction(request.Action))
                {
                    LogTraversal(
                        $"stateful optimistic traversal start deferred until confirmation " +
                        $"action={request.Action} correlation={request.CorrelationId} " +
                        $"traverse='{request.TraverseIdString}'");
                }

                if (applyOptimistically)
                {
                    _ = ApplyAuthoritativeActionAsync(
                        request.Action,
                        traverse,
                        request.ActionIdString,
                        request.StateIdString,
                        request.ArgsSelfNetworkId,
                        request.ArgsTargetNetworkId,
                        request.CorrelationId,
                        0);
                }

                if (!manager.TrySendTraversalRequest(request, out routeStatus))
                {
                    m_PendingRequests.Remove(GetPendingKey(
                        request.ActorNetworkId,
                        request.CorrelationId,
                        request.RequestId));
                    RejectLocalRequest(
                        TraversalRejectionReason.RouteUnavailable,
                        $"Traversal request route became unavailable before send: {routeStatus}",
                        $"route-send:{routeStatus}");
                }
            }
        }

        private bool ShouldSkipOutgoingTraversalRequest(
            TraversalActionType action,
            Traverse traverse,
            int traverseHash,
            string traverseId,
            out string reason)
        {
            reason = string.Empty;

            if (IsTraversalStartAction(action))
            {
                Traverse currentTraverse = ResolveTraversalStance()?.Traverse;
                if (ReferenceEquals(currentTraverse, traverse))
                {
                    reason = $"already traversing target '{FormatTraverse(traverse)}'";
                    return true;
                }

                if (TryFindPendingEquivalentStartRequest(action, traverseHash, traverseId, out NetworkTraversalRequest pending))
                {
                    reason =
                        $"equivalent start request pending requestId={pending.RequestId} " +
                        $"correlation={pending.CorrelationId}";
                    return true;
                }
            }
            else if (action == TraversalActionType.TryJump &&
                     TryFindPendingActionRequest(TraversalActionType.TryJump, out NetworkTraversalRequest pendingJump))
            {
                reason =
                    $"try jump request pending requestId={pendingJump.RequestId} " +
                    $"correlation={pendingJump.CorrelationId}";
                return true;
            }

            return false;
        }

        private bool TryFindPendingEquivalentStartRequest(
            TraversalActionType action,
            int traverseHash,
            string traverseId,
            out NetworkTraversalRequest pending)
        {
            foreach (PendingTraversalRequest candidate in m_PendingRequests.Values)
            {
                NetworkTraversalRequest request = candidate.Request;
                if (!PendingStartRequestMatches(request, action, traverseHash, traverseId)) continue;

                pending = request;
                return true;
            }

            pending = default;
            return false;
        }

        private bool TryFindPendingActionRequest(
            TraversalActionType action,
            out NetworkTraversalRequest pending)
        {
            foreach (PendingTraversalRequest candidate in m_PendingRequests.Values)
            {
                NetworkTraversalRequest request = candidate.Request;
                if (request.Action != action) continue;

                pending = request;
                return true;
            }

            pending = default;
            return false;
        }

        private int RemovePendingEquivalentStartRequests(
            TraversalActionType action,
            int traverseHash,
            string traverseId,
            uint keepCorrelationId,
            string reason)
        {
            m_PendingRemovalBuffer.Clear();

            foreach (KeyValuePair<ulong, PendingTraversalRequest> pair in m_PendingRequests)
            {
                NetworkTraversalRequest request = pair.Value.Request;
                if (!PendingStartRequestMatches(request, action, traverseHash, traverseId)) continue;
                if (keepCorrelationId != 0 && request.CorrelationId == keepCorrelationId) continue;

                m_PendingRemovalBuffer.Add(pair.Key);
            }

            for (int i = 0; i < m_PendingRemovalBuffer.Count; i++)
            {
                if (!m_PendingRequests.TryGetValue(m_PendingRemovalBuffer[i], out PendingTraversalRequest pending))
                {
                    continue;
                }

                LogTraversal(
                    $"removed pending equivalent start request reason='{reason}' " +
                    $"requestId={pending.Request.RequestId} correlation={pending.Request.CorrelationId} " +
                    $"action={pending.Request.Action} traverse='{pending.Request.TraverseIdString}'");

                m_PendingRequests.Remove(m_PendingRemovalBuffer[i]);
            }

            int removed = m_PendingRemovalBuffer.Count;
            m_PendingRemovalBuffer.Clear();
            return removed;
        }

        private static bool PendingStartRequestMatches(
            in NetworkTraversalRequest request,
            TraversalActionType action,
            int traverseHash,
            string traverseId)
        {
            return request.Action == action &&
                   request.TraverseHash == traverseHash &&
                   string.Equals(request.TraverseIdString, traverseId, StringComparison.Ordinal);
        }

        private bool ShouldRejectInvalidInteractiveConnection(
            in NetworkTraversalRequest request,
            Traverse traverse,
            out string reason)
        {
            reason = string.Empty;
            Traverse currentTraverse = ResolveTraversalStance()?.Traverse;

            if (IsInteractiveConnectionRequest(request.ActionIdString))
            {
                bool supportedConnectionAction =
                    (request.Action == TraversalActionType.EnterTraverseInteractive &&
                     traverse is TraverseInteractive) ||
                    (request.Action == TraversalActionType.RunTraverseLink &&
                     traverse is TraverseLink);
                if (!supportedConnectionAction)
                {
                    reason =
                        $"interactive connection marker is invalid for action={request.Action} " +
                        $"target='{FormatTraverse(traverse)}'";
                    return true;
                }

                if (currentTraverse is not TraverseInteractive currentInteractiveForConnection)
                {
                    reason =
                        $"interactive connection marker received while current traverse is " +
                        $"'{FormatTraverse(currentTraverse)}' target='{FormatTraverse(traverse)}'";
                    return true;
                }

                if (ReferenceEquals(currentInteractiveForConnection, traverse)) return false;

                if (!IsConfiguredInteractiveConnectionTarget(
                        currentInteractiveForConnection,
                        traverse,
                        out string validationReason))
                {
                    reason =
                        $"target is not a valid configured connection current='{FormatTraverse(currentInteractiveForConnection)}' " +
                        $"target='{FormatTraverse(traverse)}' reason='{validationReason}'";
                    return true;
                }

                return false;
            }

            if (request.Action != TraversalActionType.EnterTraverseInteractive) return false;
            if (traverse is not TraverseInteractive targetInteractive) return false;

            if (currentTraverse == null) return false;
            if (ReferenceEquals(currentTraverse, targetInteractive)) return false;
            if (currentTraverse is not TraverseInteractive currentInteractive) return false;

            reason =
                $"already traversing interactive current='{FormatTraverse(currentInteractive)}' " +
                $"target='{FormatTraverse(targetInteractive)}'. Direct trigger enter is stale; " +
                "interactive ledge-to-ledge movement must arrive through TryJump/connection authority.";
            return true;
        }

        private bool IsConfiguredInteractiveConnectionTarget(
            TraverseInteractive currentInteractive,
            Traverse targetTraverse,
            out string reason)
        {
            reason = string.Empty;

            if (currentInteractive == null || targetTraverse == null || m_Character == null)
            {
                reason = "missing current traverse, target traverse, or character";
                return false;
            }

            if (ReferenceEquals(currentInteractive.ContinueA, targetTraverse) ||
                ReferenceEquals(currentInteractive.ContinueB, targetTraverse))
            {
                Args continueArgs = new Args(currentInteractive.gameObject, m_Character.gameObject);
                if (targetTraverse.Motion == null || !targetTraverse.Motion.CanUse(continueArgs))
                {
                    reason = "authored edge continuation rejected by CanUse";
                    return false;
                }

                reason = ReferenceEquals(currentInteractive.ContinueB, targetTraverse)
                    ? "authored ContinueB target"
                    : "authored ContinueA target";
                return true;
            }

            if (currentInteractive.Connections == null || currentInteractive.Connections.Count == 0)
            {
                reason = "current traverse has no configured connections";
                return false;
            }

            Args args = new Args(currentInteractive.gameObject, m_Character.gameObject);
            Vector3 currentAnchor = currentInteractive.CalculateStartPosition(m_Character);

            for (int i = 0; i < currentInteractive.Connections.Count; i++)
            {
                Connection connection = currentInteractive.Connections[i];
                Traverse candidate = connection?.Traverse;
                if (candidate != targetTraverse) continue;

                if (candidate.Motion == null || !candidate.Motion.CanUse(args))
                {
                    reason = $"connection[{i}] target rejected by CanUse";
                    return false;
                }

                Vector3 candidateAnchor = candidate.CalculateStartPosition(m_Character);
                float distance = Vector3.Distance(currentAnchor, candidateAnchor);
                if (distance > connection.MaxDistance)
                {
                    reason =
                        $"connection[{i}] target too far distance={distance:F3} " +
                        $"max={connection.MaxDistance:F3} currentAnchor={FormatVector(currentAnchor)} " +
                        $"candidateAnchor={FormatVector(candidateAnchor)}";
                    return false;
                }

                reason =
                    $"connection[{i}] accepted distance={distance:F3} " +
                    $"max={connection.MaxDistance:F3} currentAnchor={FormatVector(currentAnchor)} " +
                    $"candidateAnchor={FormatVector(candidateAnchor)}";
                return true;
            }

            reason = $"target not listed in {currentInteractive.Connections.Count} configured connections";
            return false;
        }

        private async Task ProcessLocalServerRequestAsync(NetworkTraversalRequest request)
        {
            NetworkTraversalManager manager = NetworkTraversalManager.Instance;
            NetworkTraversalResponse response = manager != null
                ? await manager.ProcessTrustedServerRequestAsync(request)
                : CreateRejectedResponse(
                    request,
                    TraversalRejectionReason.RuntimeNotReady,
                    "NetworkTraversalManager is unavailable for trusted server traversal");
            response.ActorNetworkId = request.ActorNetworkId;
            response.CorrelationId = request.CorrelationId;
            ReceiveTraversalResponse(response);
        }

        public async Task<NetworkTraversalResponse> ProcessTraversalRequestAsync(NetworkTraversalRequest request, uint senderClientId)
        {
            await m_ServerRequestGate.WaitAsync();
            try
            {
                return await ProcessTraversalRequestSerializedAsync(request, senderClientId);
            }
            finally
            {
                m_ServerRequestGate.Release();
            }
        }

        private async Task<NetworkTraversalResponse> ProcessTraversalRequestSerializedAsync(
            NetworkTraversalRequest request,
            uint senderClientId)
        {
            LogTraversal(
                $"server processing request action={request.Action} requestId={request.RequestId} " +
                $"sender={senderClientId} actor={request.ActorNetworkId} target={request.TargetNetworkId} " +
                $"correlation={request.CorrelationId} traverse='{request.TraverseIdString}' hash={request.TraverseHash}");

            if (!m_IsServer)
            {
                return CreateRejectedResponse(
                    request,
                    TraversalRejectionReason.NotAuthorized,
                    "Traversal requests can only be processed by an initialized server controller");
            }

            // Keep the authoritative boundary self-contained. Trusted server/AI callers bypass
            // client routing checks, so the unsupported backend rule must be enforced here too.
            if (m_NetworkCharacter == null ||
                m_NetworkCharacter.PredictionBackend != NetworkPredictionBackend.BuiltIn)
            {
                return CreateRejectedResponse(
                    request,
                    TraversalRejectionReason.UnsupportedPredictionBackend,
                    "Traversal authoritative processing currently supports only the built-in movement backend");
            }

            TraversalStance serverStance = ResolveTraversalStance();
            if (m_Character == null || m_Character.Driver == null || serverStance == null)
            {
                return CreateRejectedResponse(
                    request,
                    TraversalRejectionReason.RuntimeNotReady,
                    "Character, driver, or traversal stance is not ready");
            }

            if (!Enum.IsDefined(typeof(TraversalActionType), request.Action))
            {
                LogTraversal($"server rejected requestId={request.RequestId}: unknown action={request.Action}");
                return CreateRejectedResponse(request, TraversalRejectionReason.InvalidAction, "Unknown traversal action");
            }

            if (!ValidateRequestIdentity(request, out string identityError))
            {
                LogTraversal($"server rejected requestId={request.RequestId}: identity mismatch {identityError}");
                return CreateRejectedResponse(request, TraversalRejectionReason.IdentityMismatch, identityError);
            }

            if (!TryResolveTraverseForRequest(request, out Traverse traverse, out TraversalRejectionReason resolutionError))
            {
                LogTraversal(
                    $"server rejected requestId={request.RequestId}: traverse resolution failed " +
                    $"reason={resolutionError} traverse='{request.TraverseIdString}' hash={request.TraverseHash}");
                return CreateRejectedResponse(request, resolutionError, "Traverse resolution failed");
            }

            TrackFocusedTraversalRequest(request, traverse);
            if (m_ClimbDiagnosticFocused)
            {
                FocusedClimbLog(
                    "ServerValidation",
                    $"sender={senderClientId} action={request.Action} traverse='{traverse?.name ?? "none"}' " +
                    $"stance='{serverStance.Traverse?.name ?? "none"}' motion='{traverse?.Motion?.name ?? "none"}' " +
                    $"resolved=true active={traverse?.isActiveAndEnabled ?? false} canUse=true");
            }

            if (!IsTraversalStartAction(request.Action) && serverStance.Traverse == null)
            {
                return CreateRejectedResponse(
                    request,
                    TraversalRejectionReason.InvalidState,
                    "Traversal stance has no active traverse for this action");
            }

            if (ShouldRejectInvalidInteractiveConnection(request, traverse, out string staleEnterError))
            {
                LogTraversal(
                    $"server rejected requestId={request.RequestId}: invalid interactive transition " +
                    $"reason='{staleEnterError}' traverse='{request.TraverseIdString}' hash={request.TraverseHash}");
                return CreateRejectedResponse(request, TraversalRejectionReason.InvalidState, staleEnterError);
            }

            if (request.Action == TraversalActionType.TryJump &&
                IsProtectedConnectionLinkActive(serverStance.Traverse))
            {
                string error =
                    $"TryJump is not valid while protected connection link " +
                    $"'{FormatTraverse(serverStance.Traverse)}' is active";
                LogTraversal($"server rejected requestId={request.RequestId}: {error}");
                return CreateRejectedResponse(
                    request,
                    TraversalRejectionReason.InvalidState,
                    error);
            }

            uint stateVersionBeforeApply = m_ServerStateVersion;
            ServerStartAcknowledgement startAcknowledgement = IsTraversalStartAction(request.Action)
                ? BeginServerStartAcknowledgement(request, traverse)
                : null;

            bool applied;
            try
            {
                applied = await ApplyRequestAuthoritativelyAsync(request, traverse);
            }
            catch (Exception exception)
            {
                RemoveServerStartAcknowledgement(request.CorrelationId, startAcknowledgement);
                LogTraversal($"server exception requestId={request.RequestId}: {exception.Message}");
                return CreateRejectedResponse(request, TraversalRejectionReason.Exception, exception.Message);
            }

            if (!applied)
            {
                RemoveServerStartAcknowledgement(request.CorrelationId, startAcknowledgement);
                if (m_HasDeferredStartBroadcastRequest &&
                    m_DeferredStartBroadcastRequest.CorrelationId == request.CorrelationId)
                {
                    m_HasDeferredStartBroadcastRequest = false;
                    m_DeferredStartBroadcastRequest = default;
                }
                LogTraversal($"server rejected requestId={request.RequestId}: runtime did not apply action={request.Action}");
                return CreateRejectedResponse(request, TraversalRejectionReason.InvalidState, "Traversal action rejected by runtime state");
            }

            startAcknowledgement ??= GetServerStartAcknowledgement(request.CorrelationId);
            if (startAcknowledgement != null && !startAcknowledgement.Acknowledged)
            {
                m_HasDeferredStartBroadcastRequest = true;
                m_DeferredStartBroadcastRequest = request;
            }

            if (startAcknowledgement != null &&
                !await WaitForServerStartAcknowledgementAsync(startAcknowledgement))
            {
                RejectUnacknowledgedServerStart(startAcknowledgement);
                LogTraversal(
                    $"server rejected requestId={request.RequestId}: traversal start was not acknowledged " +
                    $"within {SERVER_START_ACKNOWLEDGEMENT_SECONDS:F1}s target='{FormatTraverse(startAcknowledgement.Target)}'");
                return CreateStartTimeoutResponse(request);
            }

            RemoveServerStartAcknowledgement(request.CorrelationId, startAcknowledgement);
            if (m_HasDeferredStartBroadcastRequest &&
                m_DeferredStartBroadcastRequest.CorrelationId == request.CorrelationId)
            {
                m_HasDeferredStartBroadcastRequest = false;
                m_DeferredStartBroadcastRequest = default;
            }

            if (m_ServerStateVersion == stateVersionBeforeApply)
            {
                AdvanceServerStateVersion();
            }

            NetworkTraversalResponse response = BuildSuccessResponse(request);
            if (m_ClimbDiagnosticFocused)
            {
                FocusedClimbLog(
                    "ServerApplied",
                    $"authorized=true applied=true action={request.Action} stateVersion={response.StateVersion} " +
                    $"traversing={response.IsTraversing} pos={NetworkTraversalClimbDiagnostics.Vector(transform.position)}");
            }
            bool skipImmediateBroadcast = request.Action == TraversalActionType.TryJump &&
                m_LastTryJumpStartedInteractiveConnection;

            if (m_IsServer && !IsTraversalStartAction(request.Action) && !skipImmediateBroadcast)
            {
                NetworkTraversalBroadcast broadcast = BuildBroadcast(request);
                LogTraversal(
                    $"server broadcasting action={broadcast.Action} requestId={request.RequestId} " +
                    $"networkId={broadcast.NetworkId} traversing={broadcast.IsTraversing} " +
                    $"traverse='{broadcast.TraverseIdString}' correlation={broadcast.CorrelationId}");
                NetworkTraversalManager.Instance?.BroadcastTraversalChange(broadcast);
            }
            else if (m_IsServer && skipImmediateBroadcast)
            {
                LogTraversal(
                    $"server try jump started an interactive connection requestId={request.RequestId}; " +
                    "motion-enter broadcast will carry the target traverse");
            }
            else if (m_IsServer)
            {
                LogTraversal(
                    $"server start action accepted requestId={request.RequestId}; " +
                    "motion-enter broadcasts start and motion-exit broadcasts snapshot");
            }

            if (DiagnosticsEnabled)
            {
                Debug.Log($"[NetworkTraversalController] Applied {request.Action} sender={senderClientId}");
            }

            return response;
        }

        public void ReceiveTraversalResponse(NetworkTraversalResponse response)
        {
            if (m_ClimbDiagnosticFocused || ContainsDiagnosticName(response.ActionIdString, "PullUp"))
            {
                FocusedClimbLog(
                    "Response",
                    $"authorized={response.Authorized} applied={response.Applied} rejection={response.RejectionReason} " +
                    $"action={response.Action} responseVersion={response.StateVersion} traversing={response.IsTraversing} " +
                    $"error='{response.Error}'");
            }

            ulong key = GetPendingKey(response.ActorNetworkId, response.CorrelationId, response.RequestId);
            if (!m_PendingRequests.Remove(key))
            {
                LogTraversal(
                    $"client dropped response without pending requestId={response.RequestId} " +
                    $"actor={response.ActorNetworkId} correlation={response.CorrelationId} action={response.Action} " +
                    $"authorized={response.Authorized} applied={response.Applied} traversing={response.IsTraversing} " +
                    $"traverse='{response.TraverseIdString}'");
                return;
            }

            if (!response.Authorized || !response.Applied)
            {
                ClientAuthoritativeStateApply optimisticOperation = m_ClientAuthoritativeStateApply;
                if (optimisticOperation != null &&
                    optimisticOperation.CorrelationId != 0 &&
                    optimisticOperation.CorrelationId == response.CorrelationId)
                {
                    TraversalStance rejectedStance = ResolveTraversalStance();
                    uint rejectedApplySequence = BeginClientApply();
                    InvalidatePendingTraversalEnter(rejectedStance);
                    if (rejectedStance?.Traverse != null)
                    {
                        _ = ForceCancelAuthoritativeTraversalAsync(
                            rejectedStance,
                            response.CorrelationId,
                            response.StateVersion,
                            rejectedApplySequence);
                    }
                }

                ClearLedgeEdgeIntent();
                if (response.CorrelationId != 0 &&
                    response.CorrelationId == m_ProtectedConnectionLinkCorrelationId)
                {
                    ClearProtectedConnectionLink(null);
                }

                LogTraversal(
                    $"client received rejected response requestId={response.RequestId} " +
                    $"action={response.Action} reason={response.RejectionReason} error='{response.Error}'");

                if (m_LogRejections)
                {
                    Debug.LogWarning($"[NetworkTraversalController] Traversal request rejected: {response.RejectionReason} ({response.Error})");
                }

                OnTraversalRejected?.Invoke(response.RejectionReason, response.Error);
                return;
            }

            if (!m_IsServer)
            {
                bool alreadyApplied = response.CorrelationId != 0 &&
                    m_RecentlyAppliedCorrelations.ContainsKey(response.CorrelationId);

                LogTraversal(
                    $"client received accepted response requestId={response.RequestId} action={response.Action} " +
                    $"alreadyApplied={alreadyApplied} optimistic={m_OptimisticUpdates} " +
                    $"traversing={response.IsTraversing} traverse='{response.TraverseIdString}'");

                if (!CanAttemptAuthoritativeState(
                        response.StateVersion,
                        response.IsTraversing,
                        response.TraverseHash,
                        response.TraverseIdString))
                {
                    LogTraversal(
                        $"client ignored stale response requestId={response.RequestId} " +
                        $"version={response.StateVersion} last={m_LastAppliedStateVersion}");
                    return;
                }

                bool localStateMatches = LocalTraversalMatchesAuthoritativeState(
                    response.IsTraversing,
                    response.TraverseHash,
                    response.TraverseIdString);

                if ((m_OptimisticUpdates || alreadyApplied) && localStateMatches)
                {
                    if (response.CorrelationId != 0)
                    {
                        m_RecentlyAppliedCorrelations[response.CorrelationId] = Time.time;
                    }

                    MarkAuthoritativeStateApplied(response.StateVersion);
                    return;
                }

                if (m_OptimisticUpdates || alreadyApplied)
                {
                    LogTraversal(
                        $"client predicted response requires reconciliation requestId={response.RequestId} " +
                        $"authoritativeTraversing={response.IsTraversing} " +
                        $"authoritativeTraverse='{response.TraverseIdString}' " +
                        $"localTraverse='{FormatTraverse(ResolveTraversalStance()?.Traverse)}'");
                }

                _ = ApplyActionFromResponseAsync(response);
            }
        }

        public async void ReceiveTraversalChangeBroadcast(NetworkTraversalBroadcast broadcast)
        {
            if (broadcast.NetworkId != NetworkId) return;
            if (m_IsServer) return;

            if (ContainsDiagnosticName(broadcast.ActionIdString, "PullUp"))
            {
                m_ClimbDiagnosticRequestId = 0;
                m_ClimbDiagnosticCorrelationId = broadcast.CorrelationId;
                m_ClimbDiagnosticAction = broadcast.ActionIdString;
                m_PullUpDiagnosticUntilRealtime = Mathf.Max(
                    m_PullUpDiagnosticUntilRealtime,
                    Time.realtimeSinceStartup + 4f);
                SetClimbDiagnosticFocus(true, "pullup-broadcast", null, null);
            }

            if (m_ClimbDiagnosticFocused)
            {
                FocusedClimbLog(
                    "Broadcast",
                    $"action={broadcast.Action} actionId='{broadcast.ActionIdString}' " +
                    $"broadcastVersion={broadcast.StateVersion} traversing={broadcast.IsTraversing} " +
                    $"traverseHash={broadcast.TraverseHash} serverTime={broadcast.ServerTime:F3}");
            }

            if (!CanAttemptAuthoritativeState(
                    broadcast.StateVersion,
                    broadcast.IsTraversing,
                    broadcast.TraverseHash,
                    broadcast.TraverseIdString))
            {
                if (broadcast.StateVersion == m_LastAppliedStateVersion &&
                    MatchesLastAppliedAuthoritativeState(
                        broadcast.IsTraversing,
                        broadcast.TraverseHash,
                        broadcast.TraverseIdString))
                {
                    RaiseTraversalAppliedOnce(broadcast);
                }

                LogTraversal(
                    $"client ignored stale broadcast action={broadcast.Action} version={broadcast.StateVersion} " +
                    $"last={m_LastAppliedStateVersion} correlation={broadcast.CorrelationId}");
                return;
            }

            if (broadcast.CorrelationId != 0 &&
                m_RecentlyAppliedCorrelations.ContainsKey(broadcast.CorrelationId) &&
                LocalTraversalMatchesAuthoritativeState(
                    broadcast.IsTraversing,
                    broadcast.TraverseHash,
                    broadcast.TraverseIdString))
            {
                m_RecentlyAppliedCorrelations[broadcast.CorrelationId] = Time.time;
                LogTraversal(
                    $"client skipped predicted broadcast action={broadcast.Action} correlation={broadcast.CorrelationId} " +
                    $"traverse='{broadcast.TraverseIdString}'");
                MarkAuthoritativeStateApplied(broadcast.StateVersion);
                RaiseTraversalAppliedOnce(broadcast);
                return;
            }

            LogTraversal(
                $"client applying broadcast action={broadcast.Action} actor={broadcast.ActorNetworkId} " +
                $"correlation={broadcast.CorrelationId} traversing={broadcast.IsTraversing} " +
                $"traverse='{broadcast.TraverseIdString}'");

            Traverse authoritativeTraverse = null;
            if (broadcast.IsTraversing &&
                !TryResolveTraverseByIdentity(
                    broadcast.TraverseHash,
                    broadcast.TraverseIdString,
                    out authoritativeTraverse))
            {
                LogTraversal(
                    $"client broadcast queued: failed to resolve authoritative traverse action={broadcast.Action} " +
                    $"traverse='{broadcast.TraverseIdString}' hash={broadcast.TraverseHash}");

                if (m_LogRejections)
                {
                    Debug.LogWarning($"[NetworkTraversalController] Could not resolve traverse for broadcast action {broadcast.Action}");
                }

                QueuePendingUnresolvedBroadcast(broadcast);
                return;
            }

            bool hadPendingRequest = HasPendingRequestForCorrelation(
                broadcast.ActorNetworkId,
                broadcast.CorrelationId);

            if (IsTraversalStartAction(broadcast.Action))
            {
                RemovePendingEquivalentStartRequests(
                    broadcast.Action,
                    broadcast.TraverseHash,
                    broadcast.TraverseIdString,
                    broadcast.CorrelationId,
                    "broadcast-applied");
            }

            bool stateConverged = await BeginOrJoinClientAuthoritativeStateApply(
                broadcast.Action,
                broadcast.IsTraversing,
                broadcast.TraverseHash,
                broadcast.TraverseIdString,
                authoritativeTraverse,
                broadcast.ActionIdString,
                broadcast.StateIdString,
                broadcast.ArgsSelfNetworkId,
                broadcast.ArgsTargetNetworkId,
                broadcast.CorrelationId,
                broadcast.StateVersion);

            if (stateConverged)
            {
                if (hadPendingRequest && broadcast.CorrelationId != 0)
                {
                    m_RecentlyAppliedCorrelations[broadcast.CorrelationId] = Time.time;
                }
                RaiseTraversalAppliedOnce(broadcast);
            }
        }

        private void RaiseTraversalAppliedOnce(in NetworkTraversalBroadcast broadcast)
        {
            bool alreadyRaised = broadcast.StateVersion != 0
                ? m_LastTraversalAppliedEventStateVersion == broadcast.StateVersion
                : broadcast.CorrelationId != 0 &&
                  m_LastTraversalAppliedEventCorrelationId == broadcast.CorrelationId;
            if (alreadyRaised) return;

            m_LastTraversalAppliedEventStateVersion = broadcast.StateVersion;
            m_LastTraversalAppliedEventCorrelationId = broadcast.CorrelationId;
            OnTraversalApplied?.Invoke(broadcast);
        }

        public NetworkTraversalSnapshot CaptureFullSnapshot()
        {
            TraversalStance stance = ResolveTraversalStance();
            Traverse currentTraverse = stance?.Traverse;
            string traverseId = currentTraverse != null ? BuildTraverseId(currentTraverse) : string.Empty;
            TraversalSnapshotKind kind = currentTraverse switch
            {
                TraverseInteractive => TraversalSnapshotKind.ActiveInteractive,
                TraverseLink => TraversalSnapshotKind.ActiveLink,
                _ => TraversalSnapshotKind.None
            };

            bool hasRelativePose = false;
            Vector3 relativePosition = default;
            Quaternion relativeRotation = Quaternion.identity;
            if (currentTraverse is TraverseInteractive &&
                s_TraversalStanceRelativePositionProperty?.GetValue(stance) is Vector3 capturedRelative &&
                IsFinite(capturedRelative))
            {
                relativePosition = capturedRelative;
                relativeRotation = Quaternion.Inverse(currentTraverse.Transform.rotation) *
                                   m_Character.transform.rotation;
                hasRelativePose = IsFinite(relativeRotation);
            }

            LogTraversalPose(
                $"capture-full-snapshot traversing={currentTraverse != null} traverseId='{traverseId}'",
                currentTraverse);

            return new NetworkTraversalSnapshot
            {
                NetworkId = NetworkId,
                ServerTime = Time.time,
                IsTraversing = currentTraverse != null,
                TraverseHash = GetOptionalStableHash(traverseId),
                TraverseIdString = traverseId,
                StateVersion = m_ServerStateVersion,
                Kind = kind,
                HasRelativePose = hasRelativePose,
                RelativePosition = relativePosition,
                RelativeRotation = relativeRotation
            };
        }

        public void ReceiveFullSnapshot(NetworkTraversalSnapshot snapshot)
        {
            if (snapshot.NetworkId != NetworkId) return;
            if (m_IsServer) return;

            if (m_ClimbDiagnosticFocused)
            {
                m_HasClimbDiagnosticSnapshot = true;
                m_LastClimbDiagnosticSnapshotRelative = snapshot.RelativePosition;
                m_LastClimbDiagnosticSnapshotVersion = snapshot.StateVersion;
                FocusedClimbLog(
                    "Snapshot",
                    $"snapshotVersion={snapshot.StateVersion} kind={snapshot.Kind} traversing={snapshot.IsTraversing} " +
                    $"hasRelative={snapshot.HasRelativePose} relative={NetworkTraversalClimbDiagnostics.Vector(snapshot.RelativePosition)} " +
                    $"serverTime={snapshot.ServerTime:F3} clientPos={NetworkTraversalClimbDiagnostics.Vector(transform.position)}");
            }

            // A snapshot can arrive while the matching response/broadcast is still entering.
            // Join that operation before stale-version filtering so it can contribute its
            // presentation-safe relative pose without starting another traversal.
            if (TryJoinInFlightSnapshot(snapshot)) return;

            if (snapshot.StateVersion == m_LastAppliedStateVersion &&
                MatchesLastAppliedAuthoritativeState(
                    snapshot.IsTraversing,
                    snapshot.TraverseHash,
                    snapshot.TraverseIdString))
            {
                if (snapshot.Kind == TraversalSnapshotKind.ActiveInteractive &&
                    snapshot.IsTraversing &&
                    snapshot.HasRelativePose &&
                    TryResolveTraverseByIdentity(
                        snapshot.TraverseHash,
                        snapshot.TraverseIdString,
                        out Traverse repeatedTraverse))
                {
                    TraversalStance repeatedStance = ResolveTraversalStance();
                    if (repeatedStance != null &&
                        MatchesTraverseIdentity(
                            repeatedStance.Traverse,
                            snapshot.TraverseHash,
                            snapshot.TraverseIdString))
                    {
                        ApplySnapshotRelativePose(snapshot, repeatedStance, repeatedTraverse);
                    }
                }

                return;
            }

            if (!CanAttemptAuthoritativeState(
                    snapshot.StateVersion,
                    snapshot.IsTraversing,
                    snapshot.TraverseHash,
                    snapshot.TraverseIdString))
            {
                LogTraversal(
                    $"client ignored stale snapshot version={snapshot.StateVersion} " +
                    $"last={m_LastAppliedStateVersion} serverTime={snapshot.ServerTime:F3}");
                return;
            }

            TraversalStance stance = ResolveTraversalStance();
            if (stance == null)
            {
                QueuePendingUnresolvedSnapshot(snapshot);
                return;
            }

            bool currentlyTraversing = stance.Traverse != null;
            LogTraversal(
                $"client received snapshot serverTime={snapshot.ServerTime:F3} " +
                $"snapshotTraversing={snapshot.IsTraversing} localTraversing={currentlyTraversing} " +
                $"snapshotTraverse='{snapshot.TraverseIdString}' localTraverse='{FormatTraverse(stance.Traverse)}'");
            LogTraversalPose(
                $"receive-full-snapshot serverTime={snapshot.ServerTime:F3} " +
                $"snapshotTraversing={snapshot.IsTraversing} localTraversing={currentlyTraversing} " +
                $"snapshotTraverse='{snapshot.TraverseIdString}'",
                stance.Traverse);

            if (!snapshot.IsTraversing)
            {
                if (currentlyTraversing)
                {
                    LogTraversal("client forcing traversal cancel from non-traversing snapshot");
                }

                _ = BeginOrJoinClientAuthoritativeStateApply(
                    TraversalActionType.ForceCancel,
                    false,
                    0,
                    string.Empty,
                    null,
                    string.Empty,
                    string.Empty,
                    NetworkId,
                    NetworkId,
                    0,
                    snapshot.StateVersion,
                    true,
                    snapshot);
                return;
            }

            if (snapshot.Kind == TraversalSnapshotKind.ActiveLink)
            {
                // Link motion is transient. Record ordering and cancel a stale local traversal,
                // but never wait for or replay the link target on a late-joining client.
                uint linkApplySequence = BeginClientApply();
                ObserveTransientSnapshotVersion(snapshot.StateVersion);
                InvalidatePendingTraversalEnter(stance);
                bool cancelStaleTraversal = stance.Traverse != null &&
                    !MatchesTraverseIdentity(
                        stance.Traverse,
                        snapshot.TraverseHash,
                        snapshot.TraverseIdString);
                if (cancelStaleTraversal)
                {
                    ForceCancelAuthoritativeTraversal(stance, 0, snapshot.StateVersion);
                    _ = CompleteTransientLinkSnapshotCleanupAsync(snapshot, linkApplySequence);
                    return;
                }

                // Active links are transient. Do not consume their version here: a live
                // broadcast for the same version may still be in flight and owns playback.
                return;
            }

            if (!TryResolveTraverseByIdentity(snapshot.TraverseHash, snapshot.TraverseIdString, out Traverse traverse))
            {
                LogTraversal(
                    $"client active snapshot queued: failed to resolve traverse " +
                    $"traverse='{snapshot.TraverseIdString}' hash={snapshot.TraverseHash}");
                QueuePendingUnresolvedSnapshot(snapshot);
                return;
            }

            if (traverse is TraverseLink)
            {
                // TraverseLink motion is transient. A late snapshot records ordering but never replays it.
                uint linkApplySequence = BeginClientApply();
                ObserveTransientSnapshotVersion(snapshot.StateVersion);
                InvalidatePendingTraversalEnter(stance);
                bool cancelStaleTraversal = stance.Traverse != null &&
                    !MatchesTraverseIdentity(
                        stance.Traverse,
                        snapshot.TraverseHash,
                        snapshot.TraverseIdString);
                if (cancelStaleTraversal)
                {
                    ForceCancelAuthoritativeTraversal(stance, 0, snapshot.StateVersion);
                    _ = CompleteTransientLinkSnapshotCleanupAsync(snapshot, linkApplySequence);
                    return;
                }

                // See ActiveLink handling above. A snapshot never replays or claims a
                // finite link; the matching transient broadcast remains authoritative.
                return;
            }

            if (snapshot.Kind != TraversalSnapshotKind.ActiveInteractive ||
                traverse is not TraverseInteractive)
            {
                WarnRateLimited(
                    $"snapshot-kind:{snapshot.NetworkId}:{snapshot.TraverseHash}",
                    $"[NetworkTraversalController] Ignored traversal snapshot for NetworkId={snapshot.NetworkId}: " +
                    $"kind {snapshot.Kind} does not match '{snapshot.TraverseIdString}'.");
                return;
            }

            _ = BeginOrJoinClientAuthoritativeStateApply(
                TraversalActionType.EnterTraverseInteractive,
                true,
                snapshot.TraverseHash,
                snapshot.TraverseIdString,
                traverse,
                string.Empty,
                string.Empty,
                NetworkId,
                NetworkId,
                0,
                snapshot.StateVersion,
                true,
                snapshot);
        }

        private bool TryJoinInFlightSnapshot(in NetworkTraversalSnapshot snapshot)
        {
            ClientAuthoritativeStateApply operation = m_ClientAuthoritativeStateApply;
            if (operation == null || operation.Completion == null || operation.Completion.IsCompleted)
            {
                return false;
            }

            bool sameVersion = snapshot.StateVersion != 0 &&
                operation.StateVersion != 0 &&
                snapshot.StateVersion == operation.StateVersion;
            if (!sameVersion) return false;

            bool exactState = MatchesClientAuthoritativeState(
                operation,
                snapshot.IsTraversing,
                snapshot.TraverseHash,
                snapshot.TraverseIdString);
            if (!exactState)
            {
                WarnRateLimited(
                    $"client-snapshot-conflict:{snapshot.StateVersion}",
                    $"[NetworkTraversalController] Conflicting traversal snapshot and in-flight state " +
                    $"arrived with StateVersion={snapshot.StateVersion}. The snapshot was ignored.");
                return true;
            }

            _ = CompleteJoinedSnapshotAsync(snapshot, operation);
            return true;
        }

        private async Task CompleteJoinedSnapshotAsync(
            NetworkTraversalSnapshot snapshot,
            ClientAuthoritativeStateApply operation)
        {
            bool converged = await operation.Completion;
            if (!converged || !isActiveAndEnabled) return;

            if (snapshot.Kind != TraversalSnapshotKind.ActiveInteractive ||
                !snapshot.IsTraversing ||
                !snapshot.HasRelativePose)
            {
                return;
            }

            TraversalStance stance = ResolveTraversalStance();
            if (stance == null ||
                !TryResolveTraverseByIdentity(
                    snapshot.TraverseHash,
                    snapshot.TraverseIdString,
                    out Traverse traverse) ||
                !MatchesTraverseIdentity(
                    stance.Traverse,
                    snapshot.TraverseHash,
                    snapshot.TraverseIdString))
            {
                return;
            }

            ApplySnapshotRelativePose(snapshot, stance, traverse);
        }

        private async Task CompleteTransientLinkSnapshotCleanupAsync(
            NetworkTraversalSnapshot snapshot,
            uint applySequence)
        {
            bool converged = await WaitForLocalAuthoritativeStateAsync(
                false,
                0,
                string.Empty,
                applySequence);

            if (!IsCurrentClientApply(applySequence)) return;
            if (converged)
            {
                LogTraversal(
                    $"client cleared stale local traversal for transient link snapshot " +
                    $"version={snapshot.StateVersion} without replaying or consuming the link version");
                return;
            }

            LogTraversal(
                $"client could not clear stale local traversal for transient link snapshot " +
                $"version={snapshot.StateVersion} kind={snapshot.Kind} " +
                $"local='{FormatTraverse(ResolveTraversalStance()?.Traverse)}'");
        }

        private void QueuePendingUnresolvedSnapshot(in NetworkTraversalSnapshot snapshot)
        {
            if (!m_HasPendingUnresolvedSnapshot ||
                ShouldReplacePendingSnapshot(m_PendingUnresolvedSnapshot, snapshot))
            {
                m_HasPendingUnresolvedSnapshot = true;
                m_PendingUnresolvedSnapshot = snapshot;
            }
        }

        private void QueuePendingUnresolvedBroadcast(in NetworkTraversalBroadcast broadcast)
        {
            float now = Time.unscaledTime;
            RemoveExpiredUnresolvedBroadcasts(now);

            for (int i = 0; i < m_PendingUnresolvedBroadcasts.Count; i++)
            {
                NetworkTraversalBroadcast pending = m_PendingUnresolvedBroadcasts[i].Value;
                if (pending.StateVersion == broadcast.StateVersion &&
                    pending.CorrelationId == broadcast.CorrelationId &&
                    pending.Action == broadcast.Action &&
                    pending.TraverseHash == broadcast.TraverseHash &&
                    string.Equals(
                        pending.TraverseIdString,
                        broadcast.TraverseIdString,
                        StringComparison.Ordinal))
                {
                    return;
                }
            }

            while (m_PendingUnresolvedBroadcasts.Count >= MAX_PENDING_UNRESOLVED_BROADCASTS)
            {
                m_PendingUnresolvedBroadcasts.RemoveAt(0);
            }

            m_PendingUnresolvedBroadcasts.Add(new PendingUnresolvedBroadcast
            {
                Value = broadcast,
                ReceivedAt = now
            });
        }

        private void RetryPendingUnresolvedAuthoritativeState()
        {
            if (m_IsServer) return;

            float now = Time.unscaledTime;
            if (now < m_NextUnresolvedStateRetryTime) return;
            m_NextUnresolvedStateRetryTime = now + UNRESOLVED_STATE_RETRY_INTERVAL_SECONDS;

            if (m_HasPendingUnresolvedSnapshot)
            {
                NetworkTraversalSnapshot snapshot = m_PendingUnresolvedSnapshot;
                if (!CanAttemptAuthoritativeState(
                        snapshot.StateVersion,
                        snapshot.IsTraversing,
                        snapshot.TraverseHash,
                        snapshot.TraverseIdString))
                {
                    m_HasPendingUnresolvedSnapshot = false;
                    m_PendingUnresolvedSnapshot = default;
                }
                else if (ResolveTraversalStance() != null &&
                         (!snapshot.IsTraversing ||
                          snapshot.Kind == TraversalSnapshotKind.ActiveLink ||
                          TryResolveTraverseByIdentity(
                              snapshot.TraverseHash,
                              snapshot.TraverseIdString,
                              out _)))
                {
                    m_HasPendingUnresolvedSnapshot = false;
                    m_PendingUnresolvedSnapshot = default;
                    ReceiveFullSnapshot(snapshot);
                }
            }

            RemoveExpiredUnresolvedBroadcasts(now);
            for (int i = m_PendingUnresolvedBroadcasts.Count - 1; i >= 0; i--)
            {
                NetworkTraversalBroadcast broadcast = m_PendingUnresolvedBroadcasts[i].Value;
                if (!CanAttemptAuthoritativeState(
                        broadcast.StateVersion,
                        broadcast.IsTraversing,
                        broadcast.TraverseHash,
                        broadcast.TraverseIdString))
                {
                    m_PendingUnresolvedBroadcasts.RemoveAt(i);
                    continue;
                }

                if (RequiresTraverse(broadcast.Action) &&
                    !TryResolveTraverseByIdentity(
                        broadcast.TraverseHash,
                        broadcast.TraverseIdString,
                        out _))
                {
                    continue;
                }

                m_PendingUnresolvedBroadcasts.RemoveAt(i);
                ReceiveTraversalChangeBroadcast(broadcast);
            }
        }

        private void RemoveExpiredUnresolvedBroadcasts(float now)
        {
            for (int i = m_PendingUnresolvedBroadcasts.Count - 1; i >= 0; i--)
            {
                if (now - m_PendingUnresolvedBroadcasts[i].ReceivedAt <=
                    UNRESOLVED_TRANSIENT_TTL_SECONDS)
                {
                    continue;
                }

                LogTraversal(
                    $"expired unresolved traversal broadcast action=" +
                    $"{m_PendingUnresolvedBroadcasts[i].Value.Action} before its target became ready");
                m_PendingUnresolvedBroadcasts.RemoveAt(i);
            }
        }

        private static bool ShouldReplacePendingSnapshot(
            in NetworkTraversalSnapshot current,
            in NetworkTraversalSnapshot incoming)
        {
            if (incoming.StateVersion != 0 && current.StateVersion != 0)
            {
                if (incoming.StateVersion == current.StateVersion)
                {
                    return incoming.ServerTime >= current.ServerTime;
                }

                return NetworkTraversalVersion.IsNewer(
                    incoming.StateVersion,
                    current.StateVersion);
            }

            return incoming.ServerTime >= current.ServerTime;
        }

        private async Task ApplyActionFromResponseAsync(NetworkTraversalResponse response)
        {
            Traverse authoritativeTraverse = null;
            if (response.IsTraversing &&
                !TryResolveTraverseByIdentity(
                    response.TraverseHash,
                    response.TraverseIdString,
                    out authoritativeTraverse))
            {
                LogTraversal(
                    $"client response apply failed to resolve authoritative traverse requestId={response.RequestId} " +
                    $"action={response.Action} traverse='{response.TraverseIdString}' hash={response.TraverseHash}");
                return;
            }

            await BeginOrJoinClientAuthoritativeStateApply(
                response.Action,
                response.IsTraversing,
                response.TraverseHash,
                response.TraverseIdString,
                authoritativeTraverse,
                response.ActionIdString,
                response.StateIdString,
                response.ArgsSelfNetworkId,
                response.ArgsTargetNetworkId,
                response.CorrelationId,
                response.StateVersion);
        }

        private Task<bool> BeginOrJoinClientAuthoritativeStateApply(
            TraversalActionType action,
            bool isTraversing,
            int traverseHash,
            string traverseIdString,
            Traverse authoritativeTraverse,
            string actionIdString,
            string stateIdString,
            uint argsSelfNetworkId,
            uint argsTargetNetworkId,
            uint correlationId,
            uint stateVersion,
            bool presentationSafeSnapshot = false,
            NetworkTraversalSnapshot snapshot = default)
        {
            ClientAuthoritativeStateApply current = m_ClientAuthoritativeStateApply;
            if (current != null && current.Completion != null && !current.Completion.IsCompleted)
            {
                bool exactState = MatchesClientAuthoritativeState(
                    current,
                    isTraversing,
                    traverseHash,
                    traverseIdString);
                bool sameOperation = stateVersion != 0 && current.StateVersion != 0
                    ? stateVersion == current.StateVersion
                    : correlationId != 0 && correlationId == current.CorrelationId;

                if (sameOperation && exactState)
                {
                    if (current.StateVersion == 0 && stateVersion != 0)
                    {
                        current.StateVersion = stateVersion;
                    }

                    LogTraversal(
                        $"client joined in-flight authoritative state version={stateVersion} " +
                        $"correlation={correlationId} traverse='{traverseIdString}'");
                    return current.Completion;
                }

                if (stateVersion != 0 && current.StateVersion == stateVersion && !exactState)
                {
                    WarnRateLimited(
                        $"client-state-conflict:{stateVersion}",
                        $"[NetworkTraversalController] Conflicting authoritative traversal states " +
                        $"arrived with StateVersion={stateVersion}. Existing " +
                        $"traversing={current.IsTraversing} traverse='{current.TraverseIdString}', incoming " +
                        $"traversing={isTraversing} traverse='{traverseIdString}'. The incoming state was ignored.");
                    return Task.FromResult(false);
                }

                if (stateVersion != 0 && current.StateVersion != 0 &&
                    NetworkTraversalVersion.IsNewer(current.StateVersion, stateVersion))
                {
                    return Task.FromResult(false);
                }

                InvalidatePendingTraversalEnter(ResolveTraversalStance());
            }

            uint applySequence = BeginClientApply();
            var operation = new ClientAuthoritativeStateApply
            {
                Sequence = applySequence,
                StateVersion = stateVersion,
                CorrelationId = correlationId,
                IsTraversing = isTraversing,
                TraverseHash = traverseHash,
                TraverseIdString = traverseIdString ?? string.Empty
            };

            m_ClientAuthoritativeStateApply = operation;
            operation.Completion = RunClientAuthoritativeStateApplyAsync(
                operation,
                action,
                authoritativeTraverse,
                actionIdString,
                stateIdString,
                argsSelfNetworkId,
                argsTargetNetworkId,
                presentationSafeSnapshot,
                snapshot);
            return operation.Completion;
        }

        private async Task<bool> RunClientAuthoritativeStateApplyAsync(
            ClientAuthoritativeStateApply operation,
            TraversalActionType action,
            Traverse authoritativeTraverse,
            string actionIdString,
            string stateIdString,
            uint argsSelfNetworkId,
            uint argsTargetNetworkId,
            bool presentationSafeSnapshot,
            NetworkTraversalSnapshot snapshot)
        {
            try
            {
                bool localStateMatches = LocalTraversalMatchesAuthoritativeState(
                    operation.IsTraversing,
                    operation.TraverseHash,
                    operation.TraverseIdString);

                bool applied;
                if (presentationSafeSnapshot)
                {
                    applied = await ApplyAuthoritativeSnapshotStateAsync(
                        operation,
                        snapshot,
                        authoritativeTraverse);
                }
                else if (localStateMatches &&
                    (IsTraversalStartAction(action) ||
                     (!operation.IsTraversing && action == TraversalActionType.ForceCancel)))
                {
                    applied = true;
                }
                else if (!localStateMatches &&
                         operation.IsTraversing &&
                         !IsTraversalStartAction(action))
                {
                    applied = await ReconcileAuthoritativeTraversalStateAsync(
                        true,
                        authoritativeTraverse,
                        actionIdString,
                        stateIdString,
                        argsSelfNetworkId,
                        argsTargetNetworkId,
                        operation.CorrelationId,
                        operation.StateVersion);
                }
                else
                {
                    applied = await ApplyAuthoritativeActionAsync(
                        action,
                        RequiresTraverse(action) ? authoritativeTraverse : null,
                        actionIdString,
                        stateIdString,
                        argsSelfNetworkId,
                        argsTargetNetworkId,
                        operation.CorrelationId,
                        operation.StateVersion);
                }

                if (!applied && LocalTraversalMatchesAuthoritativeState(
                        operation.IsTraversing,
                        operation.TraverseHash,
                        operation.TraverseIdString))
                {
                    applied = true;
                }

                bool stateConverged = applied && await WaitForLocalAuthoritativeStateAsync(
                    operation.IsTraversing,
                    operation.TraverseHash,
                    operation.TraverseIdString,
                    operation.Sequence);

                if (stateConverged && IsCurrentClientApply(operation.Sequence))
                {
                    if (operation.CorrelationId != 0)
                    {
                        m_RecentlyAppliedCorrelations[operation.CorrelationId] = Time.time;
                    }

                    MarkAuthoritativeStateApplied(operation.StateVersion);
                    return true;
                }

                if (applied && IsCurrentClientApply(operation.Sequence))
                {
                    LogTraversal(
                        $"client left authoritative state retryable because it did not converge " +
                        $"version={operation.StateVersion} authoritative='{operation.TraverseIdString}' " +
                        $"local='{FormatTraverse(ResolveTraversalStance()?.Traverse)}'");
                }

                return false;
            }
            finally
            {
                if (ReferenceEquals(m_ClientAuthoritativeStateApply, operation))
                {
                    m_ClientAuthoritativeStateApply = null;
                }
            }
        }

        private async Task<bool> ApplyAuthoritativeSnapshotStateAsync(
            ClientAuthoritativeStateApply operation,
            NetworkTraversalSnapshot snapshot,
            Traverse authoritativeTraverse)
        {
            TraversalStance stance = ResolveTraversalStance();
            if (stance == null) return false;

            if (!operation.IsTraversing)
            {
                if (stance.Traverse == null)
                {
                    InvalidatePendingTraversalEnter(stance);
                    return true;
                }

                return await ForceCancelAuthoritativeTraversalAsync(
                    stance,
                    operation.CorrelationId,
                    operation.StateVersion,
                    operation.Sequence);
            }

            if (authoritativeTraverse is not TraverseInteractive interactive)
            {
                return false;
            }

            if (!ReferenceEquals(stance.Traverse, interactive))
            {
                if (stance.Traverse != null)
                {
                    ClearLedgeEdgeIntent();
                    bool drained = await CancelAndDrainPreviousTraversalAsync(
                        stance,
                        interactive,
                        operation.CorrelationId,
                        operation.StateVersion,
                        operation.Sequence);
                    if (!drained) return false;
                }

                if (!IsCurrentClientApply(operation.Sequence)) return false;
                if (!TryRestoreInteractiveSnapshot(snapshot, stance, interactive)) return false;
            }

            if (!IsCurrentClientApply(operation.Sequence) ||
                !ReferenceEquals(stance.Traverse, interactive))
            {
                return false;
            }

            ApplySnapshotRelativePose(snapshot, stance, interactive);
            return true;
        }

        private static bool MatchesClientAuthoritativeState(
            ClientAuthoritativeStateApply operation,
            bool isTraversing,
            int traverseHash,
            string traverseIdString)
        {
            if (operation == null || operation.IsTraversing != isTraversing) return false;
            if (!isTraversing) return true;

            return operation.TraverseHash == traverseHash &&
                   string.Equals(
                       operation.TraverseIdString ?? string.Empty,
                       traverseIdString ?? string.Empty,
                       StringComparison.Ordinal);
        }

        private async Task<bool> ReconcileAuthoritativeTraversalStateAsync(
            bool isTraversing,
            Traverse authoritativeTraverse,
            string actionIdString,
            string stateIdString,
            uint argsSelfNetworkId,
            uint argsTargetNetworkId,
            uint correlationId,
            uint stateVersion)
        {
            TraversalStance stance = ResolveTraversalStance();
            if (stance == null) return false;

            if (!isTraversing)
            {
                return await ForceCancelAuthoritativeTraversalAsync(
                    stance,
                    correlationId,
                    stateVersion,
                    m_ClientApplySequence);
            }

            if (authoritativeTraverse == null) return false;
            if (ReferenceEquals(stance.Traverse, authoritativeTraverse)) return true;

            TraversalActionType correctionAction = authoritativeTraverse is TraverseLink
                ? TraversalActionType.RunTraverseLink
                : TraversalActionType.EnterTraverseInteractive;
            return await ApplyAuthoritativeActionAsync(
                correctionAction,
                authoritativeTraverse,
                actionIdString,
                stateIdString,
                argsSelfNetworkId != 0 ? argsSelfNetworkId : NetworkId,
                argsTargetNetworkId != 0 ? argsTargetNetworkId : NetworkId,
                correlationId,
                stateVersion);
        }

        private async Task<bool> WaitForLocalAuthoritativeStateAsync(
            bool isTraversing,
            int traverseHash,
            string traverseIdString,
            uint applySequence)
        {
            if (LocalTraversalMatchesAuthoritativeState(isTraversing, traverseHash, traverseIdString))
            {
                return true;
            }

            float deadline = Time.realtimeSinceStartup + AUTHORITATIVE_CONNECTION_EXIT_SUPPRESSION_SECONDS;
            while (isActiveAndEnabled &&
                   IsCurrentClientApply(applySequence) &&
                   Time.realtimeSinceStartup < deadline)
            {
                await Task.Yield();
                if (LocalTraversalMatchesAuthoritativeState(isTraversing, traverseHash, traverseIdString))
                {
                    return true;
                }
            }

            return LocalTraversalMatchesAuthoritativeState(
                isTraversing,
                traverseHash,
                traverseIdString);
        }

        private bool CanAttemptAuthoritativeState(
            uint stateVersion,
            bool isTraversing,
            int traverseHash,
            string traverseIdString)
        {
            ClientAuthoritativeStateApply inFlight = m_ClientAuthoritativeStateApply;
            if (inFlight != null &&
                inFlight.Completion != null &&
                !inFlight.Completion.IsCompleted &&
                inFlight.StateVersion != 0 &&
                (stateVersion == 0 ||
                 NetworkTraversalVersion.IsNewer(inFlight.StateVersion, stateVersion)))
            {
                return false;
            }

            if (m_LatestTransientSnapshotVersion != 0 &&
                stateVersion != m_LatestTransientSnapshotVersion &&
                NetworkTraversalVersion.IsNewer(
                    m_LatestTransientSnapshotVersion,
                    stateVersion))
            {
                return false;
            }

            if (!m_HasAppliedAuthoritativeState) return true;
            if (stateVersion == m_LastAppliedStateVersion)
            {
                bool exactState = MatchesLastAppliedAuthoritativeState(
                    isTraversing,
                    traverseHash,
                    traverseIdString);
                if (!exactState)
                {
                    WarnRateLimited(
                        $"client-applied-state-conflict:{stateVersion}",
                        $"[NetworkTraversalController] Conflicting authoritative traversal states " +
                        $"arrived with the already-applied StateVersion={stateVersion}. Existing " +
                        $"traversing={m_LastAppliedIsTraversing} traverse='{m_LastAppliedTraverseIdString}', " +
                        $"incoming traversing={isTraversing} traverse='{traverseIdString}'. The incoming state was ignored.");
                }
                return false;
            }
            return NetworkTraversalVersion.IsNewer(stateVersion, m_LastAppliedStateVersion);
        }

        private void ObserveTransientSnapshotVersion(uint stateVersion)
        {
            if (stateVersion == 0) return;
            if (m_LatestTransientSnapshotVersion == 0 ||
                NetworkTraversalVersion.IsNewer(
                    stateVersion,
                    m_LatestTransientSnapshotVersion))
            {
                m_LatestTransientSnapshotVersion = stateVersion;
            }
        }

        private bool MatchesLastAppliedAuthoritativeState(
            bool isTraversing,
            int traverseHash,
            string traverseIdString)
        {
            return m_HasAppliedAuthoritativeState &&
                   m_LastAppliedIsTraversing == isTraversing &&
                   (!isTraversing ||
                    (m_LastAppliedTraverseHash == traverseHash &&
                     string.Equals(
                         m_LastAppliedTraverseIdString ?? string.Empty,
                         traverseIdString ?? string.Empty,
                         StringComparison.Ordinal)));
        }

        private uint BeginClientApply()
        {
            m_ClientApplySequence = unchecked(m_ClientApplySequence + 1u);
            if (m_ClientApplySequence == 0) m_ClientApplySequence = 1;
            m_ClientAuthoritativeStateApply = null;
            return m_ClientApplySequence;
        }

        private bool IsCurrentClientApply(uint sequence)
        {
            return sequence != 0 && sequence == m_ClientApplySequence;
        }

        private void MarkAuthoritativeStateApplied(uint stateVersion)
        {
            if (!m_HasAppliedAuthoritativeState ||
                stateVersion == m_LastAppliedStateVersion ||
                NetworkTraversalVersion.IsNewer(stateVersion, m_LastAppliedStateVersion))
            {
                Traverse current = ResolveTraversalStance()?.Traverse;
                string currentId = current != null ? BuildTraverseId(current) : string.Empty;
                m_HasAppliedAuthoritativeState = true;
                m_LastAppliedStateVersion = stateVersion;
                m_LastAppliedIsTraversing = current != null;
                m_LastAppliedTraverseIdString = currentId;
                m_LastAppliedTraverseHash = GetOptionalStableHash(currentId);
            }
        }

        private bool LocalTraversalMatchesAuthoritativeState(
            bool isTraversing,
            int traverseHash,
            string traverseIdString)
        {
            Traverse current = ResolveTraversalStance()?.Traverse;
            if (!isTraversing) return current == null;
            return MatchesTraverseIdentity(current, traverseHash, traverseIdString);
        }

        private void ForceCancelAuthoritativeTraversal(
            TraversalStance stance,
            uint correlationId = 0,
            uint stateVersion = 0)
        {
            if (stance == null) return;

            InvalidatePendingTraversalEnter(stance);
            SuppressNextClientMotionExitFromAuthoritativeConnection(
                TraversalActionType.ForceCancel,
                stance.Traverse,
                null,
                correlationId,
                stateVersion);

            bool previousSuppress = m_SuppressInterception;
            m_SuppressInterception = true;
            try
            {
                if (!TryClearSnapshotRestoredTraversal(stance))
                {
                    stance.ForceCancel();
                }
            }
            finally
            {
                m_SuppressInterception = previousSuppress;
            }

            if (m_IsServer)
            {
                CloseServerOwnerMotionWindow(SERVER_OWNER_MOTION_EXIT_GRACE_SECONDS);
            }
        }

        private async Task<bool> ForceCancelAuthoritativeTraversalAsync(
            TraversalStance stance,
            uint correlationId = 0,
            uint stateVersion = 0,
            uint clientApplySequence = 0)
        {
            if (stance == null) return false;

            Traverse previousTraverse = stance.Traverse;
            TraversalToken previousToken = s_TraversalStanceSnapshotTokenProperty?.GetValue(stance)
                as TraversalToken;

            ForceCancelAuthoritativeTraversal(stance, correlationId, stateVersion);
            return await WaitForTraversalCleanupAsync(
                stance,
                previousTraverse,
                previousToken,
                clientApplySequence);
        }

        private async Task<bool> CancelAndDrainPreviousTraversalAsync(
            TraversalStance stance,
            Traverse nextTraverse,
            uint correlationId,
            uint stateVersion,
            uint clientApplySequence)
        {
            if (stance == null) return false;

            Traverse previousTraverse = stance.Traverse;
            if (previousTraverse == null || ReferenceEquals(previousTraverse, nextTraverse))
            {
                return true;
            }

            TraversalToken previousToken = s_TraversalStanceSnapshotTokenProperty?.GetValue(stance)
                as TraversalToken;
            ForceCancelAuthoritativeTraversal(stance, correlationId, stateVersion);

            bool drained = await WaitForTraversalCleanupAsync(
                stance,
                previousTraverse,
                previousToken,
                clientApplySequence);
            if (!drained) return false;

            Traverse current = stance.Traverse;
            if (current != null && !ReferenceEquals(current, nextTraverse))
            {
                LogTraversal(
                    $"authoritative replacement was superseded while draining previous traversal " +
                    $"previous='{FormatTraverse(previousTraverse)}' next='{FormatTraverse(nextTraverse)}' " +
                    $"current='{FormatTraverse(current)}'");
                return false;
            }

            return true;
        }

        private async Task<bool> WaitForTraversalCleanupAsync(
            TraversalStance stance,
            Traverse previousTraverse,
            TraversalToken previousToken,
            uint clientApplySequence)
        {
            if (stance == null) return false;

            float deadline = Time.realtimeSinceStartup + TRAVERSAL_CLEANUP_TIMEOUT_SECONDS;
            while (isActiveAndEnabled && Time.realtimeSinceStartup < deadline)
            {
                if (clientApplySequence != 0 && !IsCurrentClientApply(clientApplySequence))
                {
                    return false;
                }

                Traverse currentTraverse = stance.Traverse;
                TraversalToken currentToken = s_TraversalStanceSnapshotTokenProperty?.GetValue(stance)
                    as TraversalToken;
                bool ownsPreviousTraverse = previousTraverse != null &&
                    ReferenceEquals(currentTraverse, previousTraverse);
                bool ownsPreviousToken = previousToken != null &&
                    ReferenceEquals(currentToken, previousToken);
                if (!ownsPreviousTraverse && !ownsPreviousToken)
                {
                    // GC2 performs MotionInteractive cleanup before OnTraverseExit and performs
                    // RefreshCollisions(false)/Traverse.OnExit synchronously immediately after it.
                    // One main-thread continuation ensures that complete native cleanup has drained.
                    await Task.Yield();
                    return isActiveAndEnabled &&
                           (clientApplySequence == 0 || IsCurrentClientApply(clientApplySequence));
                }

                await Task.Yield();
            }

            if (!isActiveAndEnabled ||
                (clientApplySequence != 0 && !IsCurrentClientApply(clientApplySequence)))
            {
                return false;
            }

            WarnRateLimited(
                $"traversal-cleanup-timeout:{NetworkId}:{previousTraverse?.GetInstanceID() ?? 0}",
                $"[NetworkTraversalController] Timed out waiting {TRAVERSAL_CLEANUP_TIMEOUT_SECONDS:F1}s " +
                $"for traversal cleanup on NetworkId={NetworkId}. Previous traversal " +
                $"'{FormatTraverse(previousTraverse)}' remains '{FormatTraverse(stance.Traverse)}'; " +
                $"the replacement was not started.");
            return false;
        }

        private void InvalidatePendingTraversalEnter(TraversalStance stance)
        {
            if (stance == null) return;

            TryInvalidatePendingTraversalEnter(stance);
            m_PendingAuthoritativeMotionEnter = default;
        }

        private bool TryInvalidatePendingTraversalEnter(TraversalStance stance)
        {
            if (stance == null) return false;

            if (s_TraversalStanceInvalidatePendingEnterMethod == null)
            {
                WarnRateLimited(
                    "pending-enter-invalidate-hook-missing",
                    "[NetworkTraversalController] Cannot invalidate a pending traversal enter " +
                    "because the required TraversalStance.NetworkInvalidatePendingEnter patch " +
                    "hook is missing. Apply the current GC2 Traversal server-authority patch.");
                return false;
            }

            try
            {
                s_TraversalStanceInvalidatePendingEnterMethod.Invoke(stance, null);
                return true;
            }
            catch (Exception exception)
            {
                WarnRateLimited(
                    "pending-enter-invalidate-hook-failed",
                    "[NetworkTraversalController] Failed to invalidate a pending traversal enter: " +
                    $"{exception.GetBaseException().Message}");
                return false;
            }
        }

        private bool TryRestoreInteractiveSnapshot(
            in NetworkTraversalSnapshot snapshot,
            TraversalStance stance,
            TraverseInteractive interactive)
        {
            if (stance == null || interactive == null || s_TraversalStanceRestoreSnapshotMethod == null)
            {
                WarnRateLimited(
                    "snapshot-restore-hook-missing",
                    "[NetworkTraversalController] Cannot restore an active interactive traversal snapshot " +
                    "because the required TraversalStance.NetworkRestoreInteractiveSnapshot patch hook is missing.");
                return false;
            }

            Vector3 relativePosition = snapshot.HasRelativePose && IsFinite(snapshot.RelativePosition)
                ? snapshot.RelativePosition
                : interactive.Transform.InverseTransformPoint(m_Character.transform.position);

            m_PendingAuthoritativeMotionEnter = CreateAuthoritativeMotionOperation(
                interactive,
                0,
                snapshot.StateVersion,
                AUTHORITATIVE_CONNECTION_EXIT_SUPPRESSION_SECONDS);

            try
            {
                bool restored = s_TraversalStanceRestoreSnapshotMethod.Invoke(
                    stance,
                    new object[] { interactive, relativePosition }) is true;
                if (!restored)
                {
                    m_PendingAuthoritativeMotionEnter = default;
                    return false;
                }

                m_IsSnapshotRestoredTraversal = true;
                bool resumeLocalOwner = m_IsLocalClient && !m_IsRemoteClient;
                if (s_TraversalStanceAllowMovementProperty?.CanWrite == true)
                {
                    s_TraversalStanceAllowMovementProperty.SetValue(stance, resumeLocalOwner);
                }
                StartHostLocalInteractiveMotionState(interactive, validateCanUse: false);
                if (resumeLocalOwner)
                {
                    _ = ResumeLocalOwnerInteractiveSnapshotAsync(stance, interactive);
                }
                return true;
            }
            catch (Exception exception)
            {
                m_PendingAuthoritativeMotionEnter = default;
                WarnRateLimited(
                    $"snapshot-restore:{interactive.GetInstanceID()}",
                    $"[NetworkTraversalController] Interactive traversal snapshot restore failed for " +
                    $"'{interactive.name}': {exception.GetBaseException().Message}");
                return false;
            }
        }

        private async Task ResumeLocalOwnerInteractiveSnapshotAsync(
            TraversalStance stance,
            TraverseInteractive interactive)
        {
            MotionInteractive motion = interactive != null ? interactive.MotionInteractive : null;
            if (stance == null || motion == null ||
                s_TraversalStanceSnapshotTokenProperty == null ||
                s_MotionInteractiveResumeSnapshotMethod == null)
            {
                WarnRateLimited(
                    "snapshot-owner-resume-hook-missing",
                    "[NetworkTraversalController] Cannot resume local-owner traversal movement from a snapshot " +
                    "because the Traversal 2.4 snapshot motion hooks are unavailable.");
                return;
            }

            TraversalToken token = s_TraversalStanceSnapshotTokenProperty.GetValue(stance) as TraversalToken;
            if (token == null) return;

            try
            {
                object invocation = s_MotionInteractiveResumeSnapshotMethod.Invoke(
                    motion,
                    new object[] { interactive, m_Character, token });
                if (invocation is not Task<bool> resumeTask)
                {
                    WarnRateLimited(
                        "snapshot-owner-resume-return",
                        "[NetworkTraversalController] Traversal snapshot motion hook returned an unexpected task type.");
                    return;
                }

                bool exitedLocally = await resumeTask;
                if (!exitedLocally ||
                    !m_IsSnapshotRestoredTraversal ||
                    !ReferenceEquals(stance.Traverse, interactive) ||
                    token.IsCancelled)
                {
                    return;
                }

                // NetworkClearSnapshot raises the normal local exit event, which sends the
                // authoritative cancel request without replaying Traverse/Motion instruction lists.
                TryClearSnapshotRestoredTraversal(stance);
            }
            catch (Exception exception)
            {
                WarnRateLimited(
                    $"snapshot-owner-resume:{interactive.GetInstanceID()}",
                    $"[NetworkTraversalController] Local-owner traversal snapshot resume failed for " +
                    $"'{interactive.name}': {exception.GetBaseException().Message}");
            }
        }

        private bool TryClearSnapshotRestoredTraversal(TraversalStance stance)
        {
            if (!m_IsSnapshotRestoredTraversal) return false;
            if (stance == null || s_TraversalStanceClearSnapshotMethod == null)
            {
                WarnRateLimited(
                    "snapshot-clear-hook-missing",
                    "[NetworkTraversalController] Cannot clear snapshot-restored traversal state because " +
                    "the required TraversalStance.NetworkClearSnapshot patch hook is missing.");
                return false;
            }

            try
            {
                bool cleared = s_TraversalStanceClearSnapshotMethod.Invoke(stance, null) is true;
                if (cleared) m_IsSnapshotRestoredTraversal = false;
                return cleared;
            }
            catch (Exception exception)
            {
                WarnRateLimited(
                    "snapshot-clear-failed",
                    $"[NetworkTraversalController] Snapshot-restored traversal clear failed: " +
                    exception.GetBaseException().Message);
                return false;
            }
        }

        private void ApplySnapshotRelativePose(
            in NetworkTraversalSnapshot snapshot,
            TraversalStance stance,
            Traverse traverse)
        {
            if (!snapshot.HasRelativePose ||
                stance == null ||
                traverse is not TraverseInteractive ||
                !IsFinite(snapshot.RelativePosition) ||
                !IsFinite(snapshot.RelativeRotation))
            {
                return;
            }

            if (s_TraversalStanceRelativePositionProperty == null ||
                !s_TraversalStanceRelativePositionProperty.CanWrite)
            {
                WarnRateLimited(
                    "snapshot-relative-position-property",
                    "[NetworkTraversalController] TraversalStance.RelativePosition is unavailable; " +
                    "an active interactive traversal snapshot cannot restore its relative pose.");
                return;
            }

            s_TraversalStanceRelativePositionProperty.SetValue(stance, snapshot.RelativePosition);
            if (m_Character != null)
            {
                Transform anchor = traverse.Transform;
                m_Character.transform.SetPositionAndRotation(
                    anchor.TransformPoint(snapshot.RelativePosition),
                    anchor.rotation * snapshot.RelativeRotation);
            }
        }

        private static bool MatchesTraverseIdentity(
            Traverse traverse,
            int expectedHash,
            string expectedId)
        {
            if (traverse == null) return false;
            string actualId = BuildTraverseId(traverse);
            if (!string.IsNullOrEmpty(expectedId))
            {
                return string.Equals(actualId, expectedId, StringComparison.Ordinal);
            }

            return expectedHash != 0 && StableHashUtility.GetStableHash(actualId) == expectedHash;
        }

        private static bool IsFinite(Vector3 value)
        {
            return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
        }

        private static bool IsFinite(Quaternion value)
        {
            return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z) && IsFinite(value.w);
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private async Task<bool> ApplyAuthoritativeActionAsync(
            TraversalActionType action,
            Traverse traverse,
            string actionIdString,
            string stateIdString,
            uint argsSelfNetworkId,
            uint argsTargetNetworkId,
            uint operationCorrelationId = 0,
            uint operationStateVersion = 0)
        {
            if (m_Character == null)
            {
                LogTraversal($"apply authoritative failed action={action}: missing character");
                return false;
            }

            TraversalStance stance = ResolveTraversalStance();
            if (stance == null)
            {
                LogTraversal($"apply authoritative failed action={action}: missing traversal stance");
                return false;
            }

            if (ContainsDiagnosticName(actionIdString, "PullUp") ||
                TryGetFocusedClimbMotion(traverse, out _, out _) ||
                m_ClimbDiagnosticFocused)
            {
                m_ClimbDiagnosticCorrelationId = operationCorrelationId;
                if (!string.IsNullOrEmpty(actionIdString)) m_ClimbDiagnosticAction = actionIdString;
                if (ContainsDiagnosticName(actionIdString, "PullUp"))
                {
                    m_PullUpDiagnosticUntilRealtime = Mathf.Max(
                        m_PullUpDiagnosticUntilRealtime,
                        Time.realtimeSinceStartup + 4f);
                }

                SetClimbDiagnosticFocus(true, "authoritative-apply", traverse, null);
                FocusedClimbLog(
                    "Apply",
                    $"action={action} actionId='{actionIdString}' operationVersion={operationStateVersion} " +
                    $"traverse='{traverse?.name ?? "none"}' stance='{stance.Traverse?.name ?? "none"}' " +
                    $"pos={NetworkTraversalClimbDiagnostics.Vector(transform.position)}");
            }

            uint clientApplySequence = m_IsServer ? 0u : m_ClientApplySequence;
            Traverse transitionSource = null;
            if (IsTraversalStartAction(action))
            {
                if (traverse == null) return false;
                if (ReferenceEquals(stance.Traverse, traverse)) return true;

                transitionSource = stance.Traverse;
                if (transitionSource != null)
                {
                    ClearLedgeEdgeIntent();
                    bool drained = await CancelAndDrainPreviousTraversalAsync(
                        stance,
                        traverse,
                        operationCorrelationId,
                        operationStateVersion,
                        clientApplySequence);
                    if (!drained) return false;
                }

                if (clientApplySequence != 0 && !IsCurrentClientApply(clientApplySequence))
                {
                    return false;
                }
            }

            if (action == TraversalActionType.TryCancel)
            {
                InvalidatePendingTraversalEnter(stance);
                bool canCancel = m_IsSnapshotRestoredTraversal;
                if (!canCancel && stance.Traverse != null)
                {
                    Args cancelArgs = BuildArgs(argsSelfNetworkId, argsTargetNetworkId);
                    canCancel = stance.Traverse.CanCancel(cancelArgs);
                }

                return canCancel && await ForceCancelAuthoritativeTraversalAsync(
                    stance,
                    operationCorrelationId,
                    operationStateVersion,
                    clientApplySequence);
            }

            if (action == TraversalActionType.ForceCancel)
            {
                bool hadTraversal = stance.Traverse != null || m_IsSnapshotRestoredTraversal;
                return hadTraversal && await ForceCancelAuthoritativeTraversalAsync(
                    stance,
                    operationCorrelationId,
                    operationStateVersion,
                    clientApplySequence);
            }

            if (action == TraversalActionType.TryJump)
            {
                if (IsProtectedConnectionLinkActive(stance.Traverse)) return false;

                if (await TryStartInteractiveJumpConnectionAsync(
                        stance,
                        operationCorrelationId,
                        operationStateVersion,
                        clientApplySequence))
                {
                    return true;
                }

                Traverse jumpTraverse = stance.Traverse;
                if (jumpTraverse == null) return false;
                Args jumpArgs = BuildArgs(argsSelfNetworkId, argsTargetNetworkId);
                if (!jumpTraverse.CanJump(jumpArgs)) return false;

                bool suppressBeforeJump = m_SuppressInterception;
                m_SuppressInterception = true;
                try
                {
                    stance.TryJump();
                    return true;
                }
                finally
                {
                    m_SuppressInterception = suppressBeforeJump;
                }
            }

            bool previousSuppress = m_SuppressInterception;
            m_SuppressInterception = true;

            try
            {
                LogTraversal(
                    $"apply authoritative begin action={action} traverse='{FormatTraverse(traverse)}' " +
                    $"stanceTraverse='{FormatTraverse(stance.Traverse)}' self={argsSelfNetworkId} target={argsTargetNetworkId} " +
                    $"actionId='{actionIdString}' position={FormatVector(m_Character.transform.position)}");
                LogTraversalPose(
                    $"apply-authoritative-begin action={action} self={argsSelfNetworkId} target={argsTargetNetworkId} " +
                    $"stanceTraverse='{FormatTraverse(stance.Traverse)}'",
                    traverse);

                switch (action)
                {
                    case TraversalActionType.RunTraverseLink:
                        if (traverse is not TraverseLink traverseLink)
                        {
                            LogTraversal($"apply authoritative failed action={action}: traverse is {FormatTraverse(traverse)}");
                            return false;
                        }

                        if (ReferenceEquals(stance.Traverse, traverseLink))
                        {
                            LogTraversal($"apply authoritative duplicate start ignored by runtime action={action} traverse='{FormatTraverse(traverseLink)}'");
                            return true;
                        }

                        SuppressNextClientMotionEnterFromAuthoritativeStart(
                            action,
                            traverseLink,
                            operationCorrelationId,
                            operationStateVersion);
                        if (IsInteractiveConnectionRequest(actionIdString))
                        {
                            ActivateProtectedConnectionLink(
                                traverseLink,
                                operationCorrelationId,
                                operationStateVersion);
                        }
                        Task linkTask = transitionSource != null
                            ? Traverse.ChangeTo(transitionSource, traverseLink, m_Character, false)
                            : traverseLink.Run(m_Character);
                        _ = ObserveAuthoritativeTraversalTask(
                            linkTask,
                            action,
                            traverseLink);
                        LogTraversal($"apply authoritative started async traversal action={action}");
                        LogTraversalPose($"apply-authoritative-task-started action={action}", traverseLink);
                        StartTraversalAnimationDiagnostics(action, traverseLink, "authoritative-link-start");
                        return true;

                    case TraversalActionType.EnterTraverseInteractive:
                        if (traverse is not TraverseInteractive traverseInteractive)
                        {
                            LogTraversal($"apply authoritative failed action={action}: traverse is {FormatTraverse(traverse)}");
                            return false;
                        }

                        if (ReferenceEquals(stance.Traverse, traverseInteractive))
                        {
                            LogTraversal($"apply authoritative duplicate start ignored by runtime action={action} traverse='{FormatTraverse(traverseInteractive)}'");
                            return true;
                        }

                        SuppressNextClientMotionEnterFromAuthoritativeStart(
                            action,
                            traverseInteractive,
                            operationCorrelationId,
                            operationStateVersion);
                        Task interactiveTask = transitionSource != null
                            ? Traverse.ChangeTo(transitionSource, traverseInteractive, m_Character, false)
                            : traverseInteractive.Enter(m_Character, InteractiveTransitionData.None);
                        _ = ObserveAuthoritativeTraversalTask(
                            interactiveTask,
                            action,
                            traverseInteractive);
                        LogTraversal(
                            $"apply authoritative started async traversal action={action} " +
                            $"from='{FormatTraverse(transitionSource)}' to='{FormatTraverse(traverseInteractive)}'");
                        LogTraversalPose($"apply-authoritative-task-started action={action}", traverseInteractive);
                        StartTraversalAnimationDiagnostics(action, traverseInteractive, "authoritative-interactive-start");
                        return true;

                    case TraversalActionType.TryAction:
                        if (string.IsNullOrEmpty(actionIdString)) return false;
                        stance.TryAction(new IdString(actionIdString));
                        return true;

                    case TraversalActionType.TryStateEnter:
                        if (string.IsNullOrEmpty(stateIdString)) return false;
                        stance.TryStateEnter(new IdString(stateIdString));
                        return true;

                    case TraversalActionType.TryStateExit:
                        stance.TryStateExit();
                        return true;

                    default:
                        return false;
                }
            }
            finally
            {
                m_SuppressInterception = previousSuppress;
            }
        }

        private void SuppressNextClientMotionEnterFromAuthoritativeStart(
            TraversalActionType action,
            Traverse traverse,
            uint correlationId,
            uint stateVersion)
        {
            if (m_IsServer || traverse == null) return;

            m_PendingAuthoritativeMotionEnter = CreateAuthoritativeMotionOperation(
                traverse,
                correlationId,
                stateVersion,
                AUTHORITATIVE_CONNECTION_EXIT_SUPPRESSION_SECONDS);
            LogTraversal(
                $"armed next client motion-enter suppression for authoritative start " +
                $"action={action} traverse='{FormatTraverse(traverse)}' " +
                $"sequence={m_PendingAuthoritativeMotionEnter.Sequence} " +
                $"correlation={correlationId} version={stateVersion}");
        }

        private void ActivateProtectedConnectionLink(
            TraverseLink link,
            uint correlationId,
            uint stateVersion)
        {
            if (link == null) return;

            m_ProtectedConnectionLinkInstanceId = link.GetInstanceID();
            m_ProtectedConnectionLinkCorrelationId = correlationId;
            m_ProtectedConnectionLinkStateVersion = stateVersion;
            m_ProtectedConnectionLinkExpiresAt =
                Time.realtimeSinceStartup + REQUEST_TIMEOUT_SECONDS;
            LogTraversal(
                $"protected interactive connection link activated " +
                $"traverse='{FormatTraverse(link)}' correlation={correlationId} version={stateVersion}");
        }

        private bool IsProtectedConnectionLinkActive(Traverse traverse)
        {
            if (m_ProtectedConnectionLinkInstanceId == 0) return false;

            if (Time.realtimeSinceStartup > m_ProtectedConnectionLinkExpiresAt)
            {
                ClearProtectedConnectionLink(null);
                return false;
            }

            if (traverse == null || traverse.GetInstanceID() != m_ProtectedConnectionLinkInstanceId)
            {
                TraversalStance stance = ResolveTraversalStance();
                if (stance?.Traverse == null ||
                    stance.Traverse.GetInstanceID() != m_ProtectedConnectionLinkInstanceId)
                {
                    ClearProtectedConnectionLink(null);
                }
                return false;
            }

            return true;
        }

        private void ClearProtectedConnectionLink(Traverse matchingTraverse)
        {
            if (m_ProtectedConnectionLinkInstanceId == 0) return;
            if (matchingTraverse != null &&
                matchingTraverse.GetInstanceID() != m_ProtectedConnectionLinkInstanceId)
            {
                return;
            }

            LogTraversal(
                $"protected interactive connection link cleared " +
                $"traverse='{FormatTraverse(matchingTraverse)}' " +
                $"correlation={m_ProtectedConnectionLinkCorrelationId} " +
                $"version={m_ProtectedConnectionLinkStateVersion}");
            m_ProtectedConnectionLinkInstanceId = 0;
            m_ProtectedConnectionLinkCorrelationId = 0;
            m_ProtectedConnectionLinkStateVersion = 0;
            m_ProtectedConnectionLinkExpiresAt = 0f;
        }

        private void SuppressNextClientMotionExitFromAuthoritativeConnection(
            TraversalActionType action,
            Traverse current,
            Traverse next,
            uint correlationId,
            uint stateVersion)
        {
            if (m_IsServer || current == null) return;

            m_PendingAuthoritativeMotionExit = CreateAuthoritativeMotionOperation(
                current,
                correlationId,
                stateVersion,
                AUTHORITATIVE_CONNECTION_EXIT_SUPPRESSION_SECONDS);

            LogTraversal(
                $"armed next client motion-exit suppression for authoritative connection " +
                $"action={action} from='{FormatTraverse(current)}' to='{FormatTraverse(next)}' " +
                $"sequence={m_PendingAuthoritativeMotionExit.Sequence} " +
                $"correlation={correlationId} version={stateVersion}");
        }

        private AuthoritativeMotionOperation CreateAuthoritativeMotionOperation(
            Traverse expectedTraverse,
            uint correlationId,
            uint stateVersion,
            float lifetime)
        {
            uint sequence = unchecked(++m_NextAuthoritativeMotionSequence);
            if (sequence == 0) sequence = unchecked(++m_NextAuthoritativeMotionSequence);
            return new AuthoritativeMotionOperation
            {
                Sequence = sequence,
                CorrelationId = correlationId,
                StateVersion = stateVersion,
                ExpectedTraverseInstanceId = expectedTraverse != null ? expectedTraverse.GetInstanceID() : 0,
                ExpiresAt = Time.realtimeSinceStartup + Mathf.Max(0.1f, lifetime)
            };
        }

        private bool TryConsumeAuthoritativeMotionEnter(Traverse enteringTraverse)
        {
            AuthoritativeMotionOperation operation = m_PendingAuthoritativeMotionEnter;
            if (!MatchesAuthoritativeMotionOperation(operation, enteringTraverse)) return false;

            m_PendingAuthoritativeMotionEnter = default;
            LogTraversal(
                $"consumed authoritative motion-enter operation sequence={operation.Sequence} " +
                $"correlation={operation.CorrelationId} version={operation.StateVersion} " +
                $"traverse='{FormatTraverse(enteringTraverse)}'");
            return true;
        }

        private bool TryConsumeNextAuthoritativeMotionExitSuppression(Traverse exitingTraverse)
        {
            AuthoritativeMotionOperation operation = m_PendingAuthoritativeMotionExit;
            if (!MatchesAuthoritativeMotionOperation(operation, exitingTraverse)) return false;

            m_PendingAuthoritativeMotionExit = default;
            LogTraversal(
                $"consumed authoritative motion-exit operation sequence={operation.Sequence} " +
                $"correlation={operation.CorrelationId} version={operation.StateVersion} " +
                $"exiting='{FormatTraverse(exitingTraverse)}'");
            return true;
        }

        private static bool MatchesAuthoritativeMotionOperation(
            in AuthoritativeMotionOperation operation,
            Traverse traverse)
        {
            return operation.IsArmed &&
                   traverse != null &&
                   operation.ExpectedTraverseInstanceId == traverse.GetInstanceID() &&
                   Time.realtimeSinceStartup <= operation.ExpiresAt;
        }

        private void CleanupAuthoritativeMotionOperations()
        {
            float now = Time.realtimeSinceStartup;
            if (m_PendingAuthoritativeMotionEnter.IsArmed &&
                now > m_PendingAuthoritativeMotionEnter.ExpiresAt)
            {
                m_PendingAuthoritativeMotionEnter = default;
            }

            if (m_PendingAuthoritativeMotionExit.IsArmed &&
                now > m_PendingAuthoritativeMotionExit.ExpiresAt)
            {
                m_PendingAuthoritativeMotionExit = default;
            }
        }

        private async Task<bool> ApplyRequestAuthoritativelyAsync(
            NetworkTraversalRequest request,
            Traverse traverse)
        {
            bool previousHasActiveRequest = m_HasActiveAuthoritativeRequest;
            NetworkTraversalRequest previousRequest = m_ActiveAuthoritativeRequest;

            m_HasActiveAuthoritativeRequest = true;
            m_ActiveAuthoritativeRequest = request;
            if (request.Action == TraversalActionType.TryJump)
            {
                m_LastTryJumpStartedInteractiveConnection = false;
            }

            try
            {
                return await ApplyAuthoritativeActionAsync(
                    request.Action,
                    traverse,
                    request.ActionIdString,
                    request.StateIdString,
                    request.ArgsSelfNetworkId,
                    request.ArgsTargetNetworkId,
                    request.CorrelationId,
                    m_ServerStateVersion);
            }
            finally
            {
                m_HasActiveAuthoritativeRequest = previousHasActiveRequest;
                m_ActiveAuthoritativeRequest = previousRequest;
            }
        }

        private ServerStartAcknowledgement BeginServerStartAcknowledgement(
            in NetworkTraversalRequest request,
            Traverse target)
        {
            if (!m_IsServer || request.CorrelationId == 0 || target == null) return null;

            uint sequence = unchecked(++m_NextServerStartSequence);
            if (sequence == 0) sequence = unchecked(++m_NextServerStartSequence);

            var acknowledgement = new ServerStartAcknowledgement
            {
                Sequence = sequence,
                CorrelationId = request.CorrelationId,
                Target = target,
                Acknowledged = ReferenceEquals(ResolveTraversalStance()?.Traverse, target),
                CreatedAt = Time.realtimeSinceStartup
            };

            m_ServerStartAcknowledgements[request.CorrelationId] = acknowledgement;
            return acknowledgement;
        }

        private ServerStartAcknowledgement GetServerStartAcknowledgement(uint correlationId)
        {
            return correlationId != 0 &&
                   m_ServerStartAcknowledgements.TryGetValue(correlationId, out ServerStartAcknowledgement acknowledgement)
                ? acknowledgement
                : null;
        }

        private async Task<bool> WaitForServerStartAcknowledgementAsync(
            ServerStartAcknowledgement acknowledgement)
        {
            if (acknowledgement == null) return true;

            float deadline = acknowledgement.CreatedAt + SERVER_START_ACKNOWLEDGEMENT_SECONDS;
            while (!acknowledgement.Acknowledged &&
                   isActiveAndEnabled &&
                   Time.realtimeSinceStartup < deadline)
            {
                await Task.Yield();
            }

            return acknowledgement.Acknowledged;
        }

        private void RemoveServerStartAcknowledgement(
            uint correlationId,
            ServerStartAcknowledgement expected)
        {
            if (correlationId == 0 || expected == null) return;
            if (m_ServerStartAcknowledgements.TryGetValue(
                    correlationId,
                    out ServerStartAcknowledgement current) &&
                ReferenceEquals(current, expected))
            {
                m_ServerStartAcknowledgements.Remove(correlationId);
            }
        }

        private void RejectUnacknowledgedServerStart(ServerStartAcknowledgement acknowledgement)
        {
            if (acknowledgement == null) return;

            CloseServerOwnerMotionWindow(0f);

            RemoveServerStartAcknowledgement(acknowledgement.CorrelationId, acknowledgement);
            if (m_HasDeferredStartBroadcastRequest &&
                m_DeferredStartBroadcastRequest.CorrelationId == acknowledgement.CorrelationId)
            {
                m_HasDeferredStartBroadcastRequest = false;
                m_DeferredStartBroadcastRequest = default;
            }

            Traverse target = acknowledgement.Target;
            // Invalidate the exact yielded GC2 enter generation. A later retry to the same
            // Traverse receives a new generation and must not be mistaken for this timeout.
            TraversalStance stance = ResolveTraversalStance();
            TryInvalidatePendingTraversalEnter(stance);
            if (stance == null || !ReferenceEquals(stance.Traverse, target)) return;

            bool previousSuppress = m_SuppressInterception;
            m_SuppressInterception = true;
            try
            {
                stance.ForceCancel();
            }
            finally
            {
                m_SuppressInterception = previousSuppress;
            }
        }

        private void CleanupServerStartAcknowledgements()
        {
            float now = Time.realtimeSinceStartup;

            m_StartAcknowledgementRemovalBuffer.Clear();
            foreach (KeyValuePair<uint, ServerStartAcknowledgement> pair in m_ServerStartAcknowledgements)
            {
                ServerStartAcknowledgement acknowledgement = pair.Value;
                if (acknowledgement == null ||
                    now - acknowledgement.CreatedAt > SERVER_START_ACKNOWLEDGEMENT_SECONDS * 2f)
                {
                    m_StartAcknowledgementRemovalBuffer.Add(pair.Key);
                }
            }

            for (int i = 0; i < m_StartAcknowledgementRemovalBuffer.Count; i++)
            {
                m_ServerStartAcknowledgements.Remove(m_StartAcknowledgementRemovalBuffer[i]);
            }

        }

        private async Task<bool> TryStartInteractiveJumpConnectionAsync(
            TraversalStance stance,
            uint correlationId,
            uint stateVersion,
            uint clientApplySequence)
        {
            if (stance == null)
            {
                LogTraversal("try jump connection start skipped: stance is null");
                return false;
            }

            if (stance.Traverse is not TraverseInteractive interactive)
            {
                LogTraversal(
                    $"try jump connection start skipped: active traverse is not interactive " +
                    $"traverse='{FormatTraverse(stance.Traverse)}'");
                return false;
            }

            Args args = new Args(interactive.gameObject, m_Character.gameObject);
            if (interactive.CanJump(args))
            {
                LogTraversal(
                    $"try jump connection start skipped: interactive CanJump returned true " +
                    $"traverse='{FormatTraverse(interactive)}'");
                return false;
            }

            if (!TrySelectInteractiveJumpConnection(interactive, args, out Traverse nextTraverse, out string reason))
            {
                LogTraversal(
                    $"try jump connection skipped traverse='{FormatTraverse(interactive)}' " +
                    $"reason='{reason}'");
                return false;
            }

            LogTraversal(
                $"try jump connection selected from='{FormatTraverse(interactive)}' " +
                $"to='{FormatTraverse(nextTraverse)}' reason='{reason}'");

            bool drained = await CancelAndDrainPreviousTraversalAsync(
                stance,
                nextTraverse,
                correlationId,
                stateVersion,
                clientApplySequence);
            if (!drained) return false;

            m_LastTryJumpStartedInteractiveConnection = true;
            if (m_HasActiveAuthoritativeRequest)
            {
                BeginServerStartAcknowledgement(m_ActiveAuthoritativeRequest, nextTraverse);
                m_HasDeferredStartBroadcastRequest = true;
                m_DeferredStartBroadcastRequest = m_ActiveAuthoritativeRequest;
            }

            if (nextTraverse is TraverseLink connectionLink)
            {
                ActivateProtectedConnectionLink(
                    connectionLink,
                    correlationId,
                    stateVersion);
            }

            SuppressNextClientMotionEnterFromAuthoritativeStart(
                TraversalActionType.TryJump,
                nextTraverse,
                correlationId,
                stateVersion);

            bool previousSuppress = m_SuppressInterception;
            m_SuppressInterception = true;
            try
            {
                _ = ObserveAuthoritativeTraversalTask(
                    Traverse.ChangeTo(interactive, nextTraverse, m_Character, false),
                    TraversalActionType.TryJump,
                    nextTraverse);
            }
            finally
            {
                m_SuppressInterception = previousSuppress;
            }
            return true;
        }

        private bool TrySelectInteractiveJumpConnection(
            TraverseInteractive interactive,
            Args args,
            out Traverse nextTraverse,
            out string reason)
        {
            nextTraverse = null;
            reason = string.Empty;

            if (interactive.Connections == null || interactive.Connections.Count == 0)
            {
                reason = "no configured connections";
                return false;
            }

            Vector3 currentAnchor = interactive.CalculateStartPosition(m_Character);
            bool hasDownwardInput = HasDownwardInteractiveJumpInput(out string downwardInputReason);

            if (TrySelectDownwardInteractiveJumpConnection(
                    interactive,
                    args,
                    currentAnchor,
                    out nextTraverse,
                    out reason))
            {
                return true;
            }

            if (hasDownwardInput)
            {
                reason =
                    $"downward input has no explicit configured connection " +
                    $"input='{downwardInputReason}' selector='{reason}'";
                return false;
            }

            if (TryConsumeStoredEdgeConnectionCandidate(interactive, args, out nextTraverse, out reason))
            {
                return true;
            }

            LogTraversal(
                $"try jump connection scan traverse='{FormatTraverse(interactive)}' " +
                $"connections={interactive.Connections.Count} currentAnchor={FormatVector(currentAnchor)} " +
                "selector=upward-preferred");

            float bestVertical = 0.1f;
            float bestDistance = Mathf.Infinity;

            for (int i = 0; i < interactive.Connections.Count; i++)
            {
                Connection connection = interactive.Connections[i];
                Traverse candidate = connection?.Traverse;
                if (candidate == null)
                {
                    LogTraversal($"try jump connection candidate[{i}] skipped: traverse is null");
                    continue;
                }

                if (candidate.Motion == null || !candidate.Motion.CanUse(args))
                {
                    LogTraversal(
                        $"try jump connection candidate[{i}] rejected by CanUse " +
                        $"candidate='{FormatTraverse(candidate)}'");
                    continue;
                }

                Vector3 candidateAnchor = candidate.CalculateStartPosition(m_Character);
                float distance = Vector3.Distance(currentAnchor, candidateAnchor);
                Vector3 localDelta = interactive.Transform.InverseTransformDirection(candidateAnchor - currentAnchor);

                if (distance > connection.MaxDistance)
                {
                    LogTraversal(
                        $"try jump connection candidate[{i}] rejected by distance " +
                        $"candidate='{FormatTraverse(candidate)}' distance={distance:F3} " +
                        $"max={connection.MaxDistance:F3} localDelta={FormatVector(localDelta)} " +
                        $"candidateAnchor={FormatVector(candidateAnchor)}");
                    continue;
                }

                if (localDelta.y < bestVertical)
                {
                    LogTraversal(
                        $"try jump connection candidate[{i}] rejected by vertical delta " +
                        $"candidate='{FormatTraverse(candidate)}' vertical={localDelta.y:F3} " +
                        $"best={bestVertical:F3} distance={distance:F3} " +
                        $"candidateAnchor={FormatVector(candidateAnchor)}");
                    continue;
                }

                if (Mathf.Approximately(localDelta.y, bestVertical) && distance >= bestDistance)
                {
                    LogTraversal(
                        $"try jump connection candidate[{i}] rejected by closer match " +
                        $"candidate='{FormatTraverse(candidate)}' vertical={localDelta.y:F3} " +
                        $"distance={distance:F3} bestDistance={bestDistance:F3}");
                    continue;
                }

                nextTraverse = candidate;
                bestVertical = localDelta.y;
                bestDistance = distance;

                LogTraversal(
                    $"try jump connection candidate[{i}] accepted " +
                    $"candidate='{FormatTraverse(candidate)}' vertical={localDelta.y:F3} " +
                    $"distance={distance:F3} max={connection.MaxDistance:F3} " +
                    $"localDelta={FormatVector(localDelta)} candidateAnchor={FormatVector(candidateAnchor)}");
            }

            if (nextTraverse == null)
            {
                reason =
                    $"no upward connection currentAnchor={FormatVector(currentAnchor)} " +
                    $"connections={interactive.Connections.Count}";
                return false;
            }

            reason =
                $"vertical={bestVertical:F3} distance={bestDistance:F3} " +
                $"currentAnchor={FormatVector(currentAnchor)}";
            return true;
        }

        private bool TrySelectDownwardInteractiveJumpConnection(
            TraverseInteractive interactive,
            Args args,
            Vector3 currentAnchor,
            out Traverse nextTraverse,
            out string reason)
        {
            nextTraverse = null;
            reason = string.Empty;

            if (!TryGetInteractiveJumpInput(out Vector2 direction, out string inputReason))
            {
                reason = inputReason;
                return false;
            }

            if (direction.y > DOWNWARD_JUMP_INPUT_THRESHOLD)
            {
                reason =
                    $"input is not downward direction={FormatVector2(direction)} " +
                    $"threshold={DOWNWARD_JUMP_INPUT_THRESHOLD:F3}";
                return false;
            }

            Camera camera = ShortcutMainCamera.Get<Camera>();
            if (camera == null)
            {
                reason = $"main camera not found for downward selector input='{inputReason}'";
                return false;
            }

            Traverse candidate = interactive.GetCandidateConnection(m_Character, camera, direction);
            if (candidate == null)
            {
                reason =
                    $"downward selector found no candidate direction={FormatVector2(direction)} " +
                    $"input='{inputReason}'";
                return false;
            }

            if (!IsConfiguredInteractiveConnectionTarget(
                    interactive,
                    candidate,
                    out string validationReason))
            {
                reason =
                    $"downward selector candidate failed validation " +
                    $"candidate='{FormatTraverse(candidate)}' validation='{validationReason}'";
                return false;
            }

            Vector3 candidateAnchor = candidate.CalculateStartPosition(m_Character);
            Vector3 localDelta = interactive.Transform.InverseTransformDirection(candidateAnchor - currentAnchor);

            if (localDelta.y > DOWNWARD_JUMP_VERTICAL_THRESHOLD)
            {
                reason =
                    $"downward selector candidate is not below current ledge " +
                    $"candidate='{FormatTraverse(candidate)}' localDelta={FormatVector(localDelta)} " +
                    $"threshold={DOWNWARD_JUMP_VERTICAL_THRESHOLD:F3} validation='{validationReason}'";
                LogTraversal(reason);
                return false;
            }

            nextTraverse = candidate;
            reason =
                $"downward selector direction={FormatVector2(direction)} input='{inputReason}' " +
                $"localDelta={FormatVector(localDelta)} currentAnchor={FormatVector(currentAnchor)} " +
                $"candidateAnchor={FormatVector(candidateAnchor)} validation='{validationReason}'";

            LogTraversal(
                $"try jump using downward directional connection " +
                $"from='{FormatTraverse(interactive)}' to='{FormatTraverse(nextTraverse)}' " +
                $"reason='{reason}'");

            return true;
        }

        private bool TryGetInteractiveJumpInput(out Vector2 direction, out string reason)
        {
            direction = Vector2.zero;
            reason = string.Empty;

            if (m_NetworkDirectionalPlayer != null)
            {
                Vector2 rawInput = m_NetworkDirectionalPlayer.RawInput;
                if (rawInput.sqrMagnitude > 0.0001f)
                {
                    direction = rawInput.normalized;
                    reason = $"network-raw={FormatVector2(rawInput)}";
                    return true;
                }
            }

            Vector3 localInput = m_Character?.Player?.LocalInputDirection ?? Vector3.zero;
            Vector2 localDirection = new Vector2(localInput.x, localInput.z);
            if (localDirection.sqrMagnitude > 0.0001f)
            {
                direction = localDirection.normalized;
                reason = $"character-local={FormatVector(localInput)}";
                return true;
            }

            reason = "no jump direction input";
            return false;
        }

        private bool HasDownwardInteractiveJumpInput(out string reason)
        {
            if (!TryGetInteractiveJumpInput(out Vector2 direction, out string inputReason))
            {
                reason = inputReason;
                return false;
            }

            reason =
                $"{inputReason} direction={FormatVector2(direction)} " +
                $"threshold={DOWNWARD_JUMP_INPUT_THRESHOLD:F3}";
            return direction.y <= DOWNWARD_JUMP_INPUT_THRESHOLD;
        }

        private void StoreEdgeConnectionCandidate(
            TraverseInteractive source,
            Traverse target,
            Vector3 localPosition,
            Vector3 localDirection,
            bool edgeB)
        {
            m_LastEdgeConnectionSource = source;
            m_LastEdgeConnectionTarget = target;
            m_LastEdgeConnectionLocalPosition = localPosition;
            m_LastEdgeConnectionLocalDirection = localDirection;
            m_LastEdgeConnectionEdgeB = edgeB;
            m_LastEdgeConnectionCandidateTime = Time.time;

            LogTraversal(
                $"stored edge connection candidate edge={(edgeB ? "B" : "A")} " +
                $"from='{FormatTraverse(source)}' to='{FormatTraverse(target)}' " +
                $"local={FormatVector(localPosition)} input={FormatVector(localDirection)}");
        }

        private bool TryConsumeStoredEdgeConnectionCandidate(
            TraverseInteractive interactive,
            Args args,
            out Traverse nextTraverse,
            out string reason)
        {
            nextTraverse = null;
            reason = string.Empty;

            if (m_LastEdgeConnectionTarget == null)
            {
                reason = "no stored edge target";
                return false;
            }

            float age = Time.time - m_LastEdgeConnectionCandidateTime;
            if (age > EDGE_CONNECTION_JUMP_MEMORY_SECONDS)
            {
                reason =
                    $"stored edge target expired age={age:F3} " +
                    $"limit={EDGE_CONNECTION_JUMP_MEMORY_SECONDS:F3}";
                return false;
            }

            if (!ReferenceEquals(m_LastEdgeConnectionSource, interactive))
            {
                reason =
                    $"stored edge target belongs to another source " +
                    $"stored='{FormatTraverse(m_LastEdgeConnectionSource)}' current='{FormatTraverse(interactive)}'";
                return false;
            }

            if (!IsConfiguredInteractiveConnectionTarget(
                    interactive,
                    m_LastEdgeConnectionTarget,
                    out string validationReason))
            {
                reason = $"stored edge target failed validation reason='{validationReason}'";
                return false;
            }

            nextTraverse = m_LastEdgeConnectionTarget;
            reason =
                $"stored edge {(m_LastEdgeConnectionEdgeB ? "B" : "A")} target age={age:F3} " +
                $"local={FormatVector(m_LastEdgeConnectionLocalPosition)} " +
                $"input={FormatVector(m_LastEdgeConnectionLocalDirection)} " +
                $"validation='{validationReason}'";

            LogTraversal(
                $"try jump using stored edge connection target " +
                $"from='{FormatTraverse(interactive)}' to='{FormatTraverse(nextTraverse)}' " +
                $"reason='{reason}'");

            m_LastEdgeConnectionTarget = null;
            m_LastEdgeConnectionSource = null;
            return true;
        }

        private bool TrySelectInteractiveConnectionByLocalDirection(
            TraverseInteractive interactive,
            Args args,
            Vector3 currentLocalPosition,
            Vector3 localDirection,
            out Traverse nextTraverse,
            out string reason)
        {
            nextTraverse = null;
            reason = string.Empty;

            if (interactive.Connections == null || interactive.Connections.Count == 0)
            {
                reason = "no configured connections";
                return false;
            }

            if (localDirection.sqrMagnitude <= 0.0001f)
            {
                reason = $"input too small input={FormatVector(localDirection)}";
                return false;
            }

            Vector3 normalizedDirection = localDirection.normalized;
            Vector3 currentAnchor = interactive.Transform.TransformPoint(currentLocalPosition);
            float bestDot = 0.1f;
            float bestDistance = Mathf.Infinity;

            for (int i = 0; i < interactive.Connections.Count; i++)
            {
                Connection connection = interactive.Connections[i];
                Traverse candidate = connection?.Traverse;
                if (candidate == null)
                {
                    LogTraversal($"edge connection candidate[{i}] skipped: traverse is null");
                    continue;
                }

                if (candidate.Motion == null || !candidate.Motion.CanUse(args))
                {
                    LogTraversal(
                        $"edge connection candidate[{i}] rejected by CanUse " +
                        $"candidate='{FormatTraverse(candidate)}'");
                    continue;
                }

                Vector3 candidateAnchor = candidate.CalculateStartPosition(m_Character);
                float distance = Vector3.Distance(currentAnchor, candidateAnchor);
                Vector3 localDelta = interactive.Transform.InverseTransformDirection(candidateAnchor - currentAnchor);
                float dot = localDelta.sqrMagnitude > 0.0001f
                    ? Vector3.Dot(normalizedDirection, localDelta.normalized)
                    : -1f;

                if (distance > connection.MaxDistance)
                {
                    LogTraversal(
                        $"edge connection candidate[{i}] rejected by distance " +
                        $"candidate='{FormatTraverse(candidate)}' distance={distance:F3} " +
                        $"max={connection.MaxDistance:F3} localDelta={FormatVector(localDelta)} " +
                        $"dot={dot:F3} candidateAnchor={FormatVector(candidateAnchor)}");
                    continue;
                }

                if (dot < bestDot)
                {
                    LogTraversal(
                        $"edge connection candidate[{i}] rejected by direction " +
                        $"candidate='{FormatTraverse(candidate)}' dot={dot:F3} best={bestDot:F3} " +
                        $"distance={distance:F3} localDelta={FormatVector(localDelta)} " +
                        $"candidateAnchor={FormatVector(candidateAnchor)}");
                    continue;
                }

                if (Mathf.Approximately(dot, bestDot) && distance >= bestDistance)
                {
                    LogTraversal(
                        $"edge connection candidate[{i}] rejected by closer match " +
                        $"candidate='{FormatTraverse(candidate)}' dot={dot:F3} " +
                        $"distance={distance:F3} bestDistance={bestDistance:F3}");
                    continue;
                }

                nextTraverse = candidate;
                bestDot = dot;
                bestDistance = distance;

                LogTraversal(
                    $"edge connection candidate[{i}] accepted " +
                    $"candidate='{FormatTraverse(candidate)}' dot={dot:F3} distance={distance:F3} " +
                    $"max={connection.MaxDistance:F3} localDelta={FormatVector(localDelta)} " +
                    $"candidateAnchor={FormatVector(candidateAnchor)}");
            }

            if (nextTraverse == null)
            {
                reason =
                    $"no matching connection currentAnchor={FormatVector(currentAnchor)} " +
                    $"input={FormatVector(localDirection)} connections={interactive.Connections.Count}";
                return false;
            }

            reason =
                $"dot={bestDot:F3} distance={bestDistance:F3} " +
                $"currentAnchor={FormatVector(currentAnchor)} input={FormatVector(localDirection)}";
            return true;
        }

        private async Task ObserveAuthoritativeTraversalTask(
            Task traversalTask,
            TraversalActionType action,
            Traverse traverse)
        {
            float startTime = Time.time;
            Vector3 startPosition = m_Character != null ? m_Character.transform.position : transform.position;
            string traverseName = FormatTraverse(traverse);
            LogTraversalPose($"authoritative-task-observe-start action={action}", traverse);

            try
            {
                await traversalTask;
                LogTraversal(
                    $"authoritative traversal task completed action={action} traverse='{traverseName}' " +
                    $"duration={(Time.time - startTime):F3} start={FormatVector(startPosition)} " +
                    $"end={FormatVector(m_Character != null ? m_Character.transform.position : transform.position)}");
                LogTraversalPose(
                    $"authoritative-task-complete action={action} duration={(Time.time - startTime):F3} " +
                    $"start={FormatVector(startPosition)}",
                    traverse);
            }
            catch (Exception exception)
            {
                Debug.LogError(
                    $"[NetworkTraversalDebug][Controller] {name} netId={NetworkId} " +
                    $"authoritative traversal task failed action={action} traverse='{traverseName}': {exception}",
                    this);
            }
        }

        private NetworkTraversalResponse BuildSuccessResponse(in NetworkTraversalRequest request)
        {
            Traverse currentTraverse = ResolveTraversalStance()?.Traverse;
            string currentTraverseId = currentTraverse != null ? BuildTraverseId(currentTraverse) : string.Empty;
            int currentTraverseHash = GetOptionalStableHash(currentTraverseId);

            return new NetworkTraversalResponse
            {
                RequestId = request.RequestId,
                ActorNetworkId = request.ActorNetworkId,
                CorrelationId = request.CorrelationId,
                Action = request.Action,
                Authorized = true,
                Applied = true,
                RejectionReason = TraversalRejectionReason.None,
                TraverseHash = currentTraverseHash,
                TraverseIdString = currentTraverseId,
                ActionIdHash = request.ActionIdHash,
                ActionIdString = request.ActionIdString,
                StateIdHash = request.StateIdHash,
                StateIdString = request.StateIdString,
                ArgsSelfNetworkId = request.ArgsSelfNetworkId,
                ArgsTargetNetworkId = request.ArgsTargetNetworkId,
                IsTraversing = currentTraverse != null,
                StateVersion = m_ServerStateVersion,
                Error = string.Empty
            };
        }

        private uint AdvanceServerStateVersion()
        {
            m_ServerStateVersion = unchecked(m_ServerStateVersion + 1u);
            if (m_ServerStateVersion == 0) m_ServerStateVersion = 1;
            return m_ServerStateVersion;
        }

        private NetworkTraversalBroadcast BuildBroadcast(in NetworkTraversalRequest request)
        {
            Traverse currentTraverse = ResolveTraversalStance()?.Traverse;
            string currentTraverseId = currentTraverse != null ? BuildTraverseId(currentTraverse) : string.Empty;
            int currentTraverseHash = GetOptionalStableHash(currentTraverseId);

            return new NetworkTraversalBroadcast
            {
                NetworkId = NetworkId,
                ActorNetworkId = request.ActorNetworkId,
                CorrelationId = request.CorrelationId,
                Action = request.Action,
                TraverseHash = currentTraverseHash,
                TraverseIdString = currentTraverseId,
                ActionIdHash = request.ActionIdHash,
                ActionIdString = request.ActionIdString,
                StateIdHash = request.StateIdHash,
                StateIdString = request.StateIdString,
                ArgsSelfNetworkId = request.ArgsSelfNetworkId,
                ArgsTargetNetworkId = request.ArgsTargetNetworkId,
                IsTraversing = currentTraverse != null,
                StateVersion = m_ServerStateVersion,
                ServerTime = Time.time
            };
        }

        private static NetworkTraversalResponse CreateRejectedResponse(
            in NetworkTraversalRequest request,
            TraversalRejectionReason reason,
            string error)
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
                Error = error
            };
        }

        private static NetworkTraversalResponse CreateStartTimeoutResponse(
            in NetworkTraversalRequest request)
        {
            return CreateRejectedResponse(
                request,
                TraversalRejectionReason.StartTimeout,
                "Traversal runtime did not enter the requested target before the server acknowledgement deadline");
        }

        private bool ValidateRequestIdentity(in NetworkTraversalRequest request, out string error)
        {
            error = string.Empty;

            if (request.RequestId == 0 || request.CorrelationId == 0)
            {
                error = "Request and correlation identifiers must be non-zero";
                return false;
            }

            if (request.ActorNetworkId == 0 ||
                request.TargetNetworkId == 0 ||
                request.ActorNetworkId != request.TargetNetworkId ||
                request.TargetNetworkId != NetworkId)
            {
                error = "Actor and target identifiers must match the routed traversal controller";
                return false;
            }

            if ((request.TraverseIdString?.Length ?? 0) > MAX_NETWORK_IDENTITY_LENGTH ||
                (request.ActionIdString?.Length ?? 0) > MAX_NETWORK_IDENTITY_LENGTH ||
                (request.StateIdString?.Length ?? 0) > MAX_NETWORK_IDENTITY_LENGTH)
            {
                error = $"Traversal identity strings may not exceed {MAX_NETWORK_IDENTITY_LENGTH} characters";
                return false;
            }

            if (!MatchesStableHash(request.ActionIdHash, request.ActionIdString, allowEmpty: true))
            {
                error = "Action hash does not match action id string";
                return false;
            }

            if (!MatchesStableHash(request.StateIdHash, request.StateIdString, allowEmpty: true))
            {
                error = "State hash does not match state id string";
                return false;
            }

            if (!MatchesStableHash(request.TraverseHash, request.TraverseIdString, allowEmpty: true))
            {
                error = "Traverse hash does not match traverse id string";
                return false;
            }

            if (RequiresTraverse(request.Action))
            {
                if (string.IsNullOrEmpty(request.TraverseIdString) || request.TraverseHash == 0)
                {
                    error = "Traversal start actions require a stable traverse identity";
                    return false;
                }
            }
            else if (!string.IsNullOrEmpty(request.TraverseIdString) || request.TraverseHash != 0)
            {
                error = "This traversal action must not include a traverse identity";
                return false;
            }

            bool hasActionIdentity = !string.IsNullOrEmpty(request.ActionIdString) || request.ActionIdHash != 0;
            if (request.Action == TraversalActionType.TryAction)
            {
                if (!hasActionIdentity)
                {
                    error = "TryAction requires an action identity";
                    return false;
                }
            }
            else if (hasActionIdentity &&
                     !((request.Action == TraversalActionType.EnterTraverseInteractive ||
                        request.Action == TraversalActionType.RunTraverseLink) &&
                       IsInteractiveConnectionRequest(request.ActionIdString)))
            {
                error = "This traversal action must not include an action identity";
                return false;
            }

            bool hasStateIdentity = !string.IsNullOrEmpty(request.StateIdString) || request.StateIdHash != 0;
            if (request.Action == TraversalActionType.TryStateEnter)
            {
                if (!hasStateIdentity)
                {
                    error = "TryStateEnter requires a state identity";
                    return false;
                }
            }
            else if (hasStateIdentity)
            {
                error = "This traversal action must not include a state identity";
                return false;
            }

            return true;
        }

        private bool TryResolveTraverseForRequest(in NetworkTraversalRequest request, out Traverse traverse, out TraversalRejectionReason error)
        {
            traverse = null;
            error = TraversalRejectionReason.None;

            if (!RequiresTraverse(request.Action))
            {
                return true;
            }

            if (!TryResolveTraverseByIdentity(request.TraverseHash, request.TraverseIdString, out traverse))
            {
                error = TraversalRejectionReason.TargetNotFound;
                return false;
            }

            if (!traverse.isActiveAndEnabled || !traverse.gameObject.activeInHierarchy)
            {
                error = TraversalRejectionReason.RuntimeNotReady;
                return false;
            }

            if (traverse.Motion == null)
            {
                error = TraversalRejectionReason.UnresolvedMotion;
                return false;
            }

            try
            {
                Args args = new Args(traverse.gameObject, m_Character.gameObject);
                if (!traverse.Motion.CanUse(args))
                {
                    error = TraversalRejectionReason.UnusableMotion;
                    return false;
                }
            }
            catch (Exception exception)
            {
                WarnRateLimited(
                    $"motion-can-use:{request.TraverseHash}",
                    $"[NetworkTraversalController] Traversal motion validation threw for " +
                    $"'{request.TraverseIdString}': {exception.Message}");
                error = TraversalRejectionReason.UnusableMotion;
                return false;
            }

            if (request.Action == TraversalActionType.RunTraverseLink && traverse is not TraverseLink)
            {
                error = TraversalRejectionReason.InvalidAction;
                return false;
            }

            if (request.Action == TraversalActionType.EnterTraverseInteractive && traverse is not TraverseInteractive)
            {
                error = TraversalRejectionReason.InvalidAction;
                return false;
            }

            return true;
        }

        private static int GetOptionalStableHash(string value)
        {
            return string.IsNullOrEmpty(value) ? 0 : StableHashUtility.GetStableHash(value);
        }

        private static bool MatchesId(int hash, string value, bool allowEmpty = false)
        {
            if (string.IsNullOrEmpty(value))
            {
                return allowEmpty || hash == 0;
            }

            return new IdString(value).Hash == hash;
        }

        private static bool MatchesStableHash(int hash, string value, bool allowEmpty = false)
        {
            if (string.IsNullOrEmpty(value))
            {
                return allowEmpty || hash == 0;
            }

            return StableHashUtility.GetStableHash(value) == hash;
        }

        private static bool RequiresTraverse(TraversalActionType action)
        {
            return action == TraversalActionType.RunTraverseLink ||
                   action == TraversalActionType.EnterTraverseInteractive;
        }

        private static bool IsTraversalStartAction(TraversalActionType action)
        {
            return action == TraversalActionType.RunTraverseLink ||
                   action == TraversalActionType.EnterTraverseInteractive;
        }

        private static bool IsInteractiveConnectionRequest(string actionIdString)
        {
            return string.Equals(
                actionIdString,
                INTERACTIVE_CONNECTION_ACTION_ID,
                StringComparison.Ordinal);
        }

        private static ulong GetPendingKey(uint actorNetworkId, uint correlationId, ushort requestId)
        {
            uint pendingCorrelation = correlationId != 0 ? correlationId : requestId;
            return ((ulong)actorNetworkId << 32) | pendingCorrelation;
        }

        private bool HasPendingRequestForCorrelation(uint actorNetworkId, uint correlationId)
        {
            if (correlationId == 0) return false;
            return m_PendingRequests.ContainsKey(GetPendingKey(actorNetworkId, correlationId, 0));
        }

        private void CleanupPendingRequests()
        {
            float now = Time.time;

            PendingRequestCleanup.RemoveTimedOut(
                m_PendingRequests,
                m_PendingRemovalBuffer,
                now,
                REQUEST_TIMEOUT_SECONDS,
                pending => pending.SentTime,
                pending => OnTraversalRejected?.Invoke(
                    TraversalRejectionReason.Exception,
                    $"Traversal request timed out: {pending.Request.Action}"));

            m_CorrelationRemovalBuffer.Clear();
            foreach (KeyValuePair<uint, float> pair in m_RecentlyAppliedCorrelations)
            {
                if (now - pair.Value <= REQUEST_TIMEOUT_SECONDS) continue;
                m_CorrelationRemovalBuffer.Add(pair.Key);
            }

            for (int i = 0; i < m_CorrelationRemovalBuffer.Count; i++)
            {
                m_RecentlyAppliedCorrelations.Remove(m_CorrelationRemovalBuffer[i]);
            }
        }

        private void EnsureRegisteredWithManager()
        {
            NetworkTraversalManager manager = NetworkTraversalManager.Instance;
            if (manager == null)
            {
                m_IsRegistered = false;
                m_RegisteredNetworkId = 0;
                return;
            }

            uint networkId = NetworkId;
            if (!IsReadyForNetworkRouting)
            {
                UnregisterFromManager();
                return;
            }

            if (m_IsRegistered && m_RegisteredNetworkId == networkId)
            {
                return;
            }

            if (m_IsRegistered)
            {
                manager.UnregisterController(m_RegisteredNetworkId);
            }

            manager.RegisterController(networkId, this);
            m_IsRegistered = ReferenceEquals(manager.GetController(networkId), this);
            m_RegisteredNetworkId = m_IsRegistered ? networkId : 0;
        }

        private void UnregisterFromManager()
        {
            if (!m_IsRegistered) return;

            NetworkTraversalManager.Instance?.UnregisterController(m_RegisteredNetworkId);
            m_IsRegistered = false;
            m_RegisteredNetworkId = 0;
        }

        private void EnsureTraversalStanceSubscription()
        {
            TraversalStance stance = ResolveTraversalStance();
            if (ReferenceEquals(stance, m_TraversalStance) && m_HasStanceSubscription)
            {
                return;
            }

            RemoveTraversalStanceSubscription();

            m_TraversalStance = stance;
            if (m_TraversalStance == null)
            {
                return;
            }

            m_TraversalStance.EventMotionEnter += OnLocalTraversalMotionEnter;
            m_TraversalStance.EventMotionExit += OnLocalTraversalMotionExit;
            m_HasStanceSubscription = true;
        }

        private void RemoveTraversalStanceSubscription()
        {
            if (!m_HasStanceSubscription || m_TraversalStance == null)
            {
                m_HasStanceSubscription = false;
                m_TraversalStance = null;
                return;
            }

            m_TraversalStance.EventMotionEnter -= OnLocalTraversalMotionEnter;
            m_TraversalStance.EventMotionExit -= OnLocalTraversalMotionExit;
            m_HasStanceSubscription = false;
            m_TraversalStance = null;
        }

        private void EnsureNetworkDirectionalJumpSubscription()
        {
            if (m_Character == null) return;
            if (!m_IsServer && !m_IsLocalClient) return;

            UnitPlayerDirectionalNetwork player = m_Character.Player as UnitPlayerDirectionalNetwork;
            if (ReferenceEquals(player, m_NetworkDirectionalPlayer) && m_HasNetworkDirectionalJumpSubscription)
            {
                return;
            }

            RemoveNetworkDirectionalJumpSubscription();

            m_NetworkDirectionalPlayer = player;
            if (m_NetworkDirectionalPlayer == null)
            {
                return;
            }

            m_NetworkDirectionalPlayer.EventTryConsumeJump += TryConsumeNetworkDirectionalJump;
            m_HasNetworkDirectionalJumpSubscription = true;
            LogTraversal("subscribed network directional jump input for traversal");
        }

        private void RemoveNetworkDirectionalJumpSubscription()
        {
            if (m_HasNetworkDirectionalJumpSubscription && m_NetworkDirectionalPlayer != null)
            {
                m_NetworkDirectionalPlayer.EventTryConsumeJump -= TryConsumeNetworkDirectionalJump;
            }

            m_HasNetworkDirectionalJumpSubscription = false;
            m_NetworkDirectionalPlayer = null;
        }

        private bool TryConsumeNetworkDirectionalJump()
        {
            if (m_IsRemoteClient) return false;

            TraversalStance stance = ResolveTraversalStance();
            if (stance == null || stance.Traverse == null)
            {
                return false;
            }

            LogTraversal(
                $"network directional jump consumed by traversal " +
                $"traverse='{FormatTraverse(stance.Traverse)}'");

            stance.TryJump();
            return true;
        }

        private void CaptureTraversalMotionValues()
        {
            if (m_HasStoredTraversalMotionValues || m_Character == null)
            {
                return;
            }

            m_StoredTraversalLinearSpeed = m_Character.Motion.LinearSpeed;
            m_StoredTraversalAngularSpeed = m_Character.Motion.AngularSpeed;
            m_HasStoredTraversalMotionValues = true;

            LogTraversal(
                $"stored traversal motion values linear={m_StoredTraversalLinearSpeed:F3} " +
                $"angular={m_StoredTraversalAngularSpeed:F3}");
        }

        private void RestoreTraversalMotionValues(string reason)
        {
            if (!m_HasStoredTraversalMotionValues || m_Character == null)
            {
                return;
            }

            LogTraversal(
                $"restore traversal motion values reason='{reason}' " +
                $"linear {m_Character.Motion.LinearSpeed:F3}->{m_StoredTraversalLinearSpeed:F3} " +
                $"angular {m_Character.Motion.AngularSpeed:F3}->{m_StoredTraversalAngularSpeed:F3}");

            m_Character.Motion.LinearSpeed = m_StoredTraversalLinearSpeed;
            m_Character.Motion.AngularSpeed = m_StoredTraversalAngularSpeed;
            m_HasStoredTraversalMotionValues = false;
        }

        private void StopHostLocalInteractiveMotionState()
        {
            if (!m_HostLocalInteractiveStateStarted || m_Character == null)
            {
                return;
            }

            LogTraversalAnimation(
                $"host-local stop prestarted interactive state layer={m_HostLocalInteractiveStateLayer} " +
                $"transitionOut={m_HostLocalInteractiveStateTransitionOut:F3} " +
                $"position={FormatVector(m_Character.transform.position)}");

            m_Character.States.Stop(
                m_HostLocalInteractiveStateLayer,
                0f,
                m_HostLocalInteractiveStateTransitionOut);

            m_HostLocalInteractiveStateStarted = false;
            m_HostLocalInteractiveStateLayer = -1;
            m_HostLocalInteractiveStateTransitionOut = 0f;
        }

        private void StartHostLocalInteractiveMotionState(
            TraverseInteractive interactive,
            bool validateCanUse = true)
        {
            if (interactive == null || m_Character == null)
            {
                return;
            }

            MotionInteractive motion = interactive.MotionInteractive;
            if (motion == null)
            {
                LogTraversalAnimation(
                    $"host-local prestart skipped: interactive motion is null traverse='{FormatTraverse(interactive)}'");
                return;
            }

            Args args = new Args(interactive.gameObject, m_Character.gameObject);
            if (validateCanUse && !motion.CanUse(args))
            {
                LogTraversalAnimation(
                    $"host-local prestart skipped: motion CanUse returned false motion='{motion.name}' " +
                    $"traverse='{FormatTraverse(interactive)}'");
                return;
            }

            State state = s_MotionInteractiveAnimationStateField?.GetValue(motion) as State;
            if (state == null)
            {
                LogTraversalAnimation(
                    $"host-local prestart skipped: motion animation state is null motion='{motion.name}' " +
                    $"traverse='{FormatTraverse(interactive)}'");
                return;
            }

            int stateLayer = 1;
            if (s_MotionInteractiveLayerField?.GetValue(motion) is PropertyGetInteger layerProperty)
            {
                stateLayer = (int)layerProperty.Get(args);
            }

            float speed = 1f;
            if (s_MotionInteractiveAnimationSpeedField?.GetValue(motion) is PropertyGetDecimal speedProperty)
            {
                speed = Mathf.Max(0.01f, (float)speedProperty.Get(args));
            }

            ConfigState stateConfig = new ConfigState(
                0f,
                speed,
                1f,
                motion.TransitionIn,
                motion.TransitionOut);

            try
            {
                m_NetworkCharacter?.AnimimController?.RegisterState(state);

                LogTraversalAnimation(
                    $"host-local prestart state motion='{motion.name}' state='{state.name}' " +
                    $"layer={stateLayer} speed={speed:F3} transitionIn={motion.TransitionIn:F3} " +
                    $"transitionOut={motion.TransitionOut:F3} traverse='{FormatTraverse(interactive)}' " +
                    $"position={FormatVector(m_Character.transform.position)}");

                m_HostLocalInteractiveStateStarted = true;
                m_HostLocalInteractiveStateLayer = stateLayer;
                m_HostLocalInteractiveStateTransitionOut = motion.TransitionOut;

                _ = ObserveHostLocalInteractiveMotionStateTask(
                    m_Character.States.SetState(state, stateLayer, BlendMode.Blend, stateConfig),
                    motion.name,
                    state.name,
                    stateLayer);
            }
            catch (Exception exception)
            {
                Debug.LogError(
                    $"[TraversalAnimDebug][Controller] {name} netId={NetworkId} role={FormatRole()} " +
                    $"host-local prestart state threw motion='{motion.name}' state='{state.name}' " +
                    $"layer={stateLayer}: {exception.Message}\n{exception.StackTrace}",
                    this);
            }
        }

        private void StartHostLocalLinkMotionAnimation(TraverseLink link)
        {
            if (link == null || m_Character == null)
            {
                return;
            }

            MotionLink motion = link.MotionLink;
            if (motion == null)
            {
                LogTraversalAnimation(
                    $"host-local link prestart skipped: link motion is null traverse='{FormatTraverse(link)}'");
                return;
            }

            Args args = new Args(link.gameObject, m_Character.gameObject);
            if (!motion.CanUse(args))
            {
                LogTraversalAnimation(
                    $"host-local link prestart skipped: motion CanUse returned false motion='{motion.name}' " +
                    $"traverse='{FormatTraverse(link)}'");
                return;
            }

            float speed = 1f;
            if (s_MotionLinkAnimationSpeedField?.GetValue(motion) is PropertyGetDecimal speedProperty)
            {
                speed = Mathf.Max(0.01f, (float)speedProperty.Get(args));
            }

            try
            {
                switch (motion.AnimationMode)
                {
                    case MotionLink.Mode.AnimationClip:
                    {
                        AnimationClip clip = s_MotionLinkAnimationClipField?.GetValue(motion) as AnimationClip;
                        AvatarMask mask = s_MotionLinkMaskField?.GetValue(motion) as AvatarMask;
                        if (clip == null)
                        {
                            LogTraversalAnimation(
                                $"host-local link prestart skipped: motion clip is null motion='{motion.name}' " +
                                $"traverse='{FormatTraverse(link)}'");
                            return;
                        }

                        ConfigGesture gestureConfig = new ConfigGesture(
                            0f,
                            clip.length,
                            speed,
                            true,
                            motion.TransitionIn,
                            motion.TransitionOut);

                        m_NetworkCharacter?.AnimimController?.RegisterClip(clip);

                        LogTraversalAnimation(
                            $"host-local link prestart gesture motion='{motion.name}' clip='{clip.name}' " +
                            $"clipLength={clip.length:F3} speed={speed:F3} transitionIn={motion.TransitionIn:F3} " +
                            $"transitionOut={motion.TransitionOut:F3} mask='{(mask != null ? mask.name : "none")}' " +
                            $"traverse='{FormatTraverse(link)}' position={FormatVector(m_Character.transform.position)}");

                        _ = ObserveHostLocalLinkMotionTask(
                            m_Character.Gestures.CrossFade(clip, mask, BlendMode.Blend, gestureConfig, true),
                            motion.name,
                            "gesture",
                            clip.name,
                            -1);
                        break;
                    }

                    case MotionLink.Mode.AnimationState:
                    {
                        State state = s_MotionLinkAnimationStateField?.GetValue(motion) as State;
                        if (state == null)
                        {
                            LogTraversalAnimation(
                                $"host-local link prestart skipped: motion state is null motion='{motion.name}' " +
                                $"traverse='{FormatTraverse(link)}'");
                            return;
                        }

                        int stateLayer = 1;
                        if (s_MotionLinkLayerField?.GetValue(motion) is PropertyGetInteger layerProperty)
                        {
                            stateLayer = (int)layerProperty.Get(args);
                        }

                        ConfigState stateConfig = new ConfigState(
                            0f,
                            speed,
                            1f,
                            motion.TransitionIn,
                            motion.TransitionOut);

                        m_NetworkCharacter?.AnimimController?.RegisterState(state);

                        LogTraversalAnimation(
                            $"host-local link prestart state motion='{motion.name}' state='{state.name}' " +
                            $"layer={stateLayer} speed={speed:F3} transitionIn={motion.TransitionIn:F3} " +
                            $"transitionOut={motion.TransitionOut:F3} traverse='{FormatTraverse(link)}' " +
                            $"position={FormatVector(m_Character.transform.position)}");

                        _ = ObserveHostLocalLinkMotionTask(
                            m_Character.States.SetState(state, stateLayer, BlendMode.Blend, stateConfig),
                            motion.name,
                            "state",
                            state.name,
                            stateLayer);
                        break;
                    }

                    default:
                        LogTraversalAnimation(
                            $"host-local link prestart skipped: unsupported mode={motion.AnimationMode} " +
                            $"motion='{motion.name}' traverse='{FormatTraverse(link)}'");
                        break;
                }
            }
            catch (Exception exception)
            {
                Debug.LogError(
                    $"[TraversalAnimDebug][Controller] {name} netId={NetworkId} role={FormatRole()} " +
                    $"host-local link prestart threw motion='{motion.name}' mode={motion.AnimationMode} " +
                    $"traverse='{FormatTraverse(link)}': {exception.Message}\n{exception.StackTrace}",
                    this);
            }
        }

        private async Task ObserveHostLocalLinkMotionTask(
            Task task,
            string motionName,
            string animationType,
            string animationName,
            int layer)
        {
            try
            {
                await task;
                string layerText = layer >= 0 ? $" layer={layer}" : string.Empty;
                LogTraversalAnimation(
                    $"host-local link {animationType} completed motion='{motionName}' " +
                    $"{animationType}='{animationName}'{layerText} " +
                    $"position={FormatVector(m_Character != null ? m_Character.transform.position : transform.position)}");
            }
            catch (Exception exception)
            {
                string layerText = layer >= 0 ? $" layer={layer}" : string.Empty;
                Debug.LogError(
                    $"[TraversalAnimDebug][Controller] {name} netId={NetworkId} role={FormatRole()} " +
                    $"host-local link {animationType} failed motion='{motionName}' " +
                    $"{animationType}='{animationName}'{layerText}: {exception.Message}\n{exception.StackTrace}",
                    this);
            }
        }

        private async Task ObserveHostLocalInteractiveMotionStateTask(
            Task task,
            string motionName,
            string stateName,
            int stateLayer)
        {
            try
            {
                await task;
                LogTraversalAnimation(
                    $"host-local state completed motion='{motionName}' state='{stateName}' " +
                    $"layer={stateLayer} position={FormatVector(m_Character != null ? m_Character.transform.position : transform.position)}");
            }
            catch (Exception exception)
            {
                Debug.LogError(
                    $"[TraversalAnimDebug][Controller] {name} netId={NetworkId} role={FormatRole()} " +
                    $"host-local state failed motion='{motionName}' state='{stateName}' " +
                    $"layer={stateLayer}: {exception.Message}\n{exception.StackTrace}",
                    this);
            }
        }

        private void StartTraversalAnimationDiagnostics(
            TraversalActionType action,
            Traverse traverse,
            string reason)
        {
            if (!DiagnosticsEnabled) return;
            if (!IsTraversalStartAction(action)) return;

            LogTraversalAnimationSnapshot($"{reason}-immediate", action, traverse);

            if (!isActiveAndEnabled) return;
            StartCoroutine(ObserveTraversalAnimationDiagnosticsCoroutine(action, traverse, reason));
        }

        private IEnumerator ObserveTraversalAnimationDiagnosticsCoroutine(
            TraversalActionType action,
            Traverse traverse,
            string reason)
        {
            yield return null;
            LogTraversalAnimationSnapshot($"{reason}-next-frame", action, traverse);

            yield return new WaitForSeconds(0.15f);
            LogTraversalAnimationSnapshot($"{reason}-0.15s", action, traverse);

            yield return new WaitForSeconds(0.35f);
            LogTraversalAnimationSnapshot($"{reason}-0.50s", action, traverse);

            yield return new WaitForSeconds(1.00f);
            LogTraversalAnimationSnapshot($"{reason}-1.50s", action, traverse);
        }

        private void LogTraversalAnimationSnapshot(
            string label,
            TraversalActionType action,
            Traverse traverse)
        {
            LogTraversalAnimation(
                $"anim-snapshot label='{label}' action={action} traverse='{FormatTraverse(traverse)}' " +
                $"stance='{FormatTraverse(m_TraversalStance != null ? m_TraversalStance.Traverse : null)}' " +
                $"suppress={m_SuppressInterception} server={m_IsServer} local={m_IsLocalClient} remote={m_IsRemoteClient} " +
                $"position={FormatVector(m_Character != null ? m_Character.transform.position : transform.position)} " +
                $"{FormatAnimimNetworkControllerSnapshot()} {FormatAnimimStatesSnapshot()} " +
                $"{FormatAnimimGesturesSnapshot()} {FormatAnimatorSnapshot()}");
        }

        private string FormatAnimimNetworkControllerSnapshot()
        {
            UnitAnimimNetworkController controller =
                m_NetworkCharacter != null ? m_NetworkCharacter.AnimimController : null;

            if (controller == null)
            {
                controller = GetComponent<UnitAnimimNetworkController>();
            }

            return controller == null
                ? "animimNet=null"
                : $"animimNet=enabled={controller.enabled} active={controller.gameObject.activeInHierarchy} " +
                  $"initialized={controller.IsInitialized} local={controller.IsLocalPlayer} sync={controller.IsSyncEnabled}";
        }

        private string FormatAnimimStatesSnapshot()
        {
            if (m_Character?.States == null)
            {
                return "animimStates=null";
            }

            if (s_StatesOutputLayersField == null)
            {
                return "animimStates=layers-field-unavailable";
            }

            try
            {
                if (s_StatesOutputLayersField.GetValue(m_Character.States) is not
                    SortedList<int, List<StatePlayableBehaviour>> layers)
                {
                    return "animimStates=layers-value-unavailable";
                }

                int activeCount = 0;
                var builder = new StringBuilder("animimStates active=");
                int activeCountOffset = builder.Length;
                builder.Append("0 layers=[");

                bool firstLayer = true;
                foreach (KeyValuePair<int, List<StatePlayableBehaviour>> entry in layers)
                {
                    List<StatePlayableBehaviour> behaviours = entry.Value;
                    int layerCount = behaviours?.Count ?? 0;
                    activeCount += layerCount;

                    if (!firstLayer) builder.Append("; ");
                    firstLayer = false;

                    builder
                        .Append("layer=").Append(entry.Key)
                        .Append(" count=").Append(layerCount);

                    if (behaviours == null || layerCount <= 0) continue;

                    StatePlayableBehaviour latest = behaviours[layerCount - 1];
                    builder
                        .Append(" latest=")
                        .Append(latest.State != null ? latest.State.name : "clip/controller")
                        .Append(" exiting=").Append(latest.IsExiting)
                        .Append(" complete=").Append(latest.IsComplete)
                        .Append(" weight=").Append(latest.CurrentWeight.ToString("F3"));
                }

                builder.Append(']');
                builder.Remove(activeCountOffset, 1);
                builder.Insert(activeCountOffset, activeCount.ToString());
                return builder.ToString();
            }
            catch (Exception exception)
            {
                return $"animimStates=error:{exception.GetType().Name}:{exception.Message}";
            }
        }

        private string FormatAnimimGesturesSnapshot()
        {
            if (m_Character?.Gestures == null)
            {
                return "animimGestures=null";
            }

            try
            {
                return
                    $"animimGestures playing={m_Character.Gestures.IsPlaying} " +
                    $"weight={m_Character.Gestures.CurrentWeight:F3}";
            }
            catch (Exception exception)
            {
                return $"animimGestures=error:{exception.GetType().Name}:{exception.Message}";
            }
        }

        private string FormatAnimatorSnapshot()
        {
            Animator[] animators = m_Character != null
                ? m_Character.GetComponentsInChildren<Animator>(true)
                : GetComponentsInChildren<Animator>(true);

            if (animators == null || animators.Length == 0)
            {
                return "animators=0";
            }

            var builder = new StringBuilder("animators=count=");
            builder.Append(animators.Length).Append(" [");

            int appended = 0;
            for (int i = 0; i < animators.Length && appended < 4; i++)
            {
                Animator animator = animators[i];
                if (animator == null) continue;

                if (appended > 0) builder.Append("; ");
                AppendAnimatorSnapshot(builder, animator);
                appended++;
            }

            if (animators.Length > appended)
            {
                builder.Append("; more=").Append(animators.Length - appended);
            }

            builder.Append(']');
            return builder.ToString();
        }

        private static void AppendAnimatorSnapshot(StringBuilder builder, Animator animator)
        {
            RuntimeAnimatorController controller = animator.runtimeAnimatorController;
            Avatar avatar = animator.avatar;

            builder
                .Append("animator='").Append(animator.name)
                .Append("' enabled=").Append(animator.enabled)
                .Append(" active=").Append(animator.gameObject.activeInHierarchy)
                .Append(" initialized=").Append(animator.isInitialized)
                .Append(" culling=").Append(animator.cullingMode)
                .Append(" update=").Append(animator.updateMode)
                .Append(" speed=").Append(animator.speed.ToString("F3"))
                .Append(" applyRootMotion=").Append(animator.applyRootMotion)
                .Append(" controller='").Append(controller != null ? controller.name : "null")
                .Append("' avatar='").Append(avatar != null ? avatar.name : "null")
                .Append("' avatarValid=").Append(avatar != null && avatar.isValid)
                .Append(" avatarHuman=").Append(avatar != null && avatar.isHuman)
                .Append(" layers=").Append(animator.layerCount);

            int layerLimit = Mathf.Min(animator.layerCount, 4);
            for (int layer = 0; layer < layerLimit; layer++)
            {
                AppendAnimatorLayerSnapshot(builder, animator, layer);
            }

            if (animator.layerCount > layerLimit)
            {
                builder.Append(" layerMore=").Append(animator.layerCount - layerLimit);
            }
        }

        private static void AppendAnimatorLayerSnapshot(StringBuilder builder, Animator animator, int layer)
        {
            try
            {
                AnimatorStateInfo current = animator.GetCurrentAnimatorStateInfo(layer);
                bool inTransition = animator.IsInTransition(layer);

                builder
                    .Append(" layer").Append(layer).Append('{')
                    .Append("weight=").Append(animator.GetLayerWeight(layer).ToString("F3"))
                    .Append(" current=").Append(FormatAnimatorStateInfo(current))
                    .Append(" inTransition=").Append(inTransition);

                if (inTransition)
                {
                    AnimatorTransitionInfo transition = animator.GetAnimatorTransitionInfo(layer);
                    AnimatorStateInfo next = animator.GetNextAnimatorStateInfo(layer);
                    builder
                        .Append(" transitionHash=").Append(transition.fullPathHash)
                        .Append(" transitionNormalized=").Append(transition.normalizedTime.ToString("F3"))
                        .Append(" transitionDuration=").Append(transition.duration.ToString("F3"))
                        .Append(" next=").Append(FormatAnimatorStateInfo(next));
                }

                AppendAnimatorClipSnapshot(builder, animator, layer);
                builder.Append('}');
            }
            catch (Exception exception)
            {
                builder
                    .Append(" layer").Append(layer).Append("{error=")
                    .Append(exception.GetType().Name).Append(':')
                    .Append(exception.Message).Append('}');
            }
        }

        private static string FormatAnimatorStateInfo(AnimatorStateInfo state)
        {
            return
                $"short={state.shortNameHash}/full={state.fullPathHash}/tag={state.tagHash}/norm={state.normalizedTime:F3}";
        }

        private static void AppendAnimatorClipSnapshot(StringBuilder builder, Animator animator, int layer)
        {
            AnimatorClipInfo[] clips = animator.GetCurrentAnimatorClipInfo(layer);
            builder.Append(" clips=[");

            if (clips == null || clips.Length == 0)
            {
                builder.Append(']');
                return;
            }

            int clipLimit = Mathf.Min(clips.Length, 3);
            for (int i = 0; i < clipLimit; i++)
            {
                if (i > 0) builder.Append(',');

                AnimationClip clip = clips[i].clip;
                builder
                    .Append(clip != null ? clip.name : "null")
                    .Append('@')
                    .Append(clips[i].weight.ToString("F3"));
            }

            if (clips.Length > clipLimit)
            {
                builder.Append(",more=").Append(clips.Length - clipLimit);
            }

            builder.Append(']');
        }

        private void OnLocalTraversalMotionEnter()
        {
            TraversalStance stance = m_TraversalStance;
            Traverse traverse = stance != null ? stance.Traverse : null;
            if (traverse == null) return;

            ClearLedgeEdgeIntent();

            if (m_ProtectedConnectionLinkInstanceId != 0 &&
                traverse.GetInstanceID() != m_ProtectedConnectionLinkInstanceId)
            {
                ClearProtectedConnectionLink(null);
            }

            if (TryGetFocusedClimbMotion(traverse, out _, out MotionInteractive focusedMotion) ||
                m_ClimbDiagnosticFocused)
            {
                SetClimbDiagnosticFocus(true, "motion-enter", traverse, focusedMotion);
                FocusedClimbLog(
                    "MotionEnter",
                    $"traverse='{traverse.name}' motion='{focusedMotion?.name ?? "none"}' " +
                    $"suppress={m_SuppressInterception} pos={NetworkTraversalClimbDiagnostics.Vector(transform.position)}");
            }

            if (m_IsServer)
            {
                foreach (ServerStartAcknowledgement acknowledgement in m_ServerStartAcknowledgements.Values)
                {
                    if (acknowledgement != null && ReferenceEquals(acknowledgement.Target, traverse))
                    {
                        acknowledgement.Acknowledged = true;
                    }
                }

                AdvanceServerStateVersion();
                uint ownerMotionOperationId = m_HasActiveAuthoritativeRequest
                    ? m_ActiveAuthoritativeRequest.CorrelationId
                    : (m_HasDeferredStartBroadcastRequest
                        ? m_DeferredStartBroadcastRequest.CorrelationId
                        : m_ServerStateVersion);
                OpenServerOwnerMotionWindow(ownerMotionOperationId);
            }

            CancelPendingServerExitSnapshot();
            CaptureTraversalMotionValues();
            LogTraversalPose("motion-enter-event", traverse);

            if (m_IsServer && m_IsLocalClient && !m_IsRemoteClient &&
                traverse is TraverseInteractive hostInteractive)
            {
                StartHostLocalInteractiveMotionState(hostInteractive);
            }
            else if (m_IsServer && m_IsLocalClient && !m_IsRemoteClient &&
                     traverse is TraverseLink hostLink)
            {
                StartHostLocalLinkMotionAnimation(hostLink);
            }

            if (m_IsServer)
            {
                NetworkTraversalManager manager = NetworkTraversalManager.Instance;
                if (manager != null)
                {
                    bool hasBroadcastRequest = m_HasActiveAuthoritativeRequest || m_HasDeferredStartBroadcastRequest;
                    NetworkTraversalRequest broadcastRequest = m_HasActiveAuthoritativeRequest
                        ? m_ActiveAuthoritativeRequest
                        : m_DeferredStartBroadcastRequest;
                    uint actorNetworkId = hasBroadcastRequest
                        ? broadcastRequest.ActorNetworkId
                        : NetworkId;
                    uint correlationId = hasBroadcastRequest
                        ? broadcastRequest.CorrelationId
                        : 0;
                    uint argsSelfNetworkId = hasBroadcastRequest
                        ? broadcastRequest.ArgsSelfNetworkId
                        : NetworkId;
                    uint argsTargetNetworkId = hasBroadcastRequest
                        ? broadcastRequest.ArgsTargetNetworkId
                        : NetworkId;
                    string traverseId = BuildTraverseId(traverse);

                    manager.BroadcastTraversalChange(new NetworkTraversalBroadcast
                    {
                        NetworkId = NetworkId,
                        ActorNetworkId = actorNetworkId,
                        CorrelationId = correlationId,
                        Action = traverse is TraverseLink ? TraversalActionType.RunTraverseLink : TraversalActionType.EnterTraverseInteractive,
                        TraverseHash = StableHashUtility.GetStableHash(traverseId),
                        TraverseIdString = traverseId,
                        ActionIdHash = hasBroadcastRequest ? broadcastRequest.ActionIdHash : 0,
                        ActionIdString = hasBroadcastRequest ? broadcastRequest.ActionIdString : string.Empty,
                        StateIdHash = hasBroadcastRequest ? broadcastRequest.StateIdHash : 0,
                        StateIdString = hasBroadcastRequest ? broadcastRequest.StateIdString : string.Empty,
                        ArgsSelfNetworkId = argsSelfNetworkId,
                        ArgsTargetNetworkId = argsTargetNetworkId,
                        IsTraversing = true,
                        StateVersion = m_ServerStateVersion,
                        ServerTime = Time.time
                    });

                    if (!m_HasActiveAuthoritativeRequest && m_HasDeferredStartBroadcastRequest)
                    {
                        m_HasDeferredStartBroadcastRequest = false;
                        m_DeferredStartBroadcastRequest = default;
                    }
                }

                return;
            }

            bool consumedAuthoritativeEnter = TryConsumeAuthoritativeMotionEnter(traverse);
            if (m_SuppressInterception || consumedAuthoritativeEnter)
            {
                LogTraversal(
                    $"client motion enter suppressed traverse='{FormatTraverse(traverse)}' " +
                    $"operationToken={consumedAuthoritativeEnter}");
                if (m_IsLocalClient && !m_IsRemoteClient)
                {
                    ActivateLocalTraversalPoseAuthority(TRAVERSAL_POSE_AUTHORITY_REFRESH_SECONDS);
                }
                return;
            }

            if (!m_IsLocalClient || m_IsRemoteClient) return;

            ActivateLocalTraversalPoseAuthority(TRAVERSAL_POSE_AUTHORITY_REFRESH_SECONDS);

            LogTraversal(
                $"client local motion enter request traverse='{FormatTraverse(traverse)}' " +
                $"position={FormatVector(m_Character != null ? m_Character.transform.position : transform.position)}");

            if (traverse is TraverseLink link)
            {
                RequestTraversalAction(TraversalActionType.RunTraverseLink, link, default, default, null, alreadyAppliedLocally: true);
            }
            else if (traverse is TraverseInteractive interactive)
            {
                RequestTraversalAction(TraversalActionType.EnterTraverseInteractive, interactive, default, default, null, alreadyAppliedLocally: true);
            }
        }

        private void OnLocalTraversalMotionExit()
        {
            TraversalStance stance = m_TraversalStance;
            Traverse exitingTraverse = stance != null ? stance.Traverse : null;
            ClearLedgeEdgeIntent();
            ClearProtectedConnectionLink(exitingTraverse);
            if (exitingTraverse != null)
            {
                LogTraversalPose("motion-exit-event", exitingTraverse);
            }

            if (m_ClimbDiagnosticFocused)
            {
                m_PullUpDiagnosticUntilRealtime = Mathf.Max(
                    m_PullUpDiagnosticUntilRealtime,
                    Time.realtimeSinceStartup + 1.5f);
                FocusedClimbLog(
                    "MotionExit",
                    $"traverse='{exitingTraverse?.name ?? "none"}' suppress={m_SuppressInterception} " +
                    $"pos={NetworkTraversalClimbDiagnostics.Vector(transform.position)} grounded={m_Character?.Driver?.IsGrounded ?? false}");
                StartCoroutine(CaptureFocusedExitCheckpoints(exitingTraverse != null ? exitingTraverse.name : "none"));
            }

            StopHostLocalInteractiveMotionState();
            RestoreTraversalMotionValues("motion-exit");

            if (m_IsServer)
            {
                CloseServerOwnerMotionWindow(SERVER_OWNER_MOTION_EXIT_GRACE_SECONDS);
                AdvanceServerStateVersion();
                string exitingTraverseId = exitingTraverse != null ? BuildTraverseId(exitingTraverse) : string.Empty;
                ScheduleServerTraversalExitSnapshot(exitingTraverseId);
                return;
            }

            if (m_SuppressInterception)
            {
                LogTraversal("client motion exit suppressed");
                TryConsumeNextAuthoritativeMotionExitSuppression(exitingTraverse);
            }
            else if (TryConsumeNextAuthoritativeMotionExitSuppression(exitingTraverse))
            {
                LogTraversal("client motion exit consumed by authoritative connection suppression");
            }
            else if (m_IsLocalClient && !m_IsRemoteClient && exitingTraverse != null)
            {
                LogTraversal(
                    $"client local motion exit request force cancel " +
                    $"exiting='{FormatTraverse(exitingTraverse)}' " +
                    $"position={FormatVector(m_Character != null ? m_Character.transform.position : transform.position)}");

                RequestTraversalAction(
                    TraversalActionType.ForceCancel,
                    null,
                    default,
                    default,
                    null,
                    alreadyAppliedLocally: true);
            }

            ActivateLocalTraversalPoseAuthority(TRAVERSAL_POSE_AUTHORITY_EXIT_GRACE_SECONDS);
        }

        private void ScheduleServerTraversalExitSnapshot(string exitingTraverseId)
        {
            CancelPendingServerExitSnapshot();
            m_PendingServerExitSnapshotCoroutine = StartCoroutine(BroadcastServerTraversalExitSnapshotNextFrame(exitingTraverseId));
        }

        private IEnumerator BroadcastServerTraversalExitSnapshotNextFrame(string exitingTraverseId)
        {
            yield return null;

            m_PendingServerExitSnapshotCoroutine = null;
            if (!m_IsServer || !isActiveAndEnabled) yield break;

            TraversalStance stance = m_TraversalStance;
            if (stance != null && stance.Traverse != null)
            {
                LogTraversal(
                    $"skip server exit snapshot: active traverse='{FormatTraverse(stance.Traverse)}' " +
                    $"exiting='{exitingTraverseId}'");
                yield break;
            }

            NetworkTraversalSnapshot snapshot = CaptureFullSnapshot();
            if (m_ClimbDiagnosticFocused)
            {
                FocusedClimbLog(
                    "ExitSnapshot",
                    $"traversing={snapshot.IsTraversing} snapshotVersion={snapshot.StateVersion} " +
                    $"exiting='{exitingTraverseId}' serverPos={NetworkTraversalClimbDiagnostics.Vector(transform.position)} " +
                    $"grounded={m_Character?.Driver?.IsGrounded ?? false}");
            }
            NetworkTraversalManager.Instance?.BroadcastFullSnapshot(snapshot);
        }

        private IEnumerator CaptureFocusedExitCheckpoints(string exitingTraverse)
        {
            float[] delays = { 0f, 0.1f, 0.35f, 0.9f };
            for (int i = 0; i < delays.Length; i++)
            {
                if (delays[i] <= 0f) yield return null;
                else yield return new WaitForSecondsRealtime(delays[i]);
                if (!isActiveAndEnabled) yield break;

                TraversalStance stance = m_TraversalStance ?? ResolveTraversalStance();
                float ownerGrace = m_Character?.Driver is UnitDriverNetworkClient clientDriver
                    ? clientDriver.OwnerMotionAuthorityRemaining
                    : 0f;
                FocusedClimbLog(
                    "PullUpCheckpoint",
                    $"index={i} exiting='{exitingTraverse}' active='{stance?.Traverse?.name ?? "none"}' " +
                    $"pos={NetworkTraversalClimbDiagnostics.Vector(transform.position)} grounded={m_Character?.Driver?.IsGrounded ?? false} " +
                    $"serverWindow={m_ServerOwnerMotionWindowOpen} operation={m_ServerOwnerMotionOperationId} " +
                    $"ownerGrace={ownerGrace:F3} hasSnapshot={m_HasClimbDiagnosticSnapshot} " +
                    $"snapshotVersion={m_LastClimbDiagnosticSnapshotVersion} " +
                    $"snapshotRelative={NetworkTraversalClimbDiagnostics.Vector(m_LastClimbDiagnosticSnapshotRelative)}");
            }
        }

        private void CancelPendingServerExitSnapshot()
        {
            if (m_PendingServerExitSnapshotCoroutine == null) return;

            StopCoroutine(m_PendingServerExitSnapshotCoroutine);
            m_PendingServerExitSnapshotCoroutine = null;
        }

        private void RefreshLocalTraversalPoseAuthority()
        {
            if (m_IsServer || !m_IsLocalClient || m_IsRemoteClient) return;

            TraversalStance stance = m_TraversalStance ?? ResolveTraversalStance();
            if (stance?.Traverse == null) return;

            ActivateLocalTraversalPoseAuthority(TRAVERSAL_POSE_AUTHORITY_REFRESH_SECONDS);
        }

        private void ActivateLocalTraversalPoseAuthority(float duration)
        {
            if (duration <= 0f) return;

            INetworkOwnerMotionAuthority motionAuthority = m_NetworkCharacter?.OwnerMotionAuthority;
            motionAuthority ??= m_Character?.Driver as INetworkOwnerMotionAuthority;
            if (motionAuthority == null)
            {
                WarnRateLimited(
                    "owner-motion-authority-refresh",
                    "[NetworkTraversalController] Traversal could not open an owner-motion window because " +
                    "the active movement backend does not implement INetworkOwnerMotionAuthority.");
                return;
            }

            motionAuthority.OpenOwnerMotionWindow(duration);
            if (m_ClimbDiagnosticFocused)
            {
                FocusedClimbLog(
                    "OwnerWindow",
                    $"side=client operation=refresh duration={duration:F3} " +
                    $"pos={NetworkTraversalClimbDiagnostics.Vector(transform.position)}",
                    $"owner-window-refresh:{GetInstanceID()}");
            }
        }

        private void OpenServerOwnerMotionWindow(uint operationId)
        {
            if (!m_IsServer) return;

            INetworkServerOwnerMotionAuthority authority =
                m_Character?.Driver as INetworkServerOwnerMotionAuthority;
            m_ServerOwnerMotionOperationId = operationId != 0
                ? operationId
                : (m_ServerStateVersion != 0 ? m_ServerStateVersion : 1u);

            if (authority != null)
            {
                m_ServerOwnerMotionWindowOpen = true;
                m_ServerOwnerMotionUsesClientAuthority = false;
                authority.OpenServerOwnerMotionWindow(
                    SERVER_OWNER_MOTION_WINDOW_SECONDS,
                    m_ServerOwnerMotionOperationId);
                if (m_ClimbDiagnosticFocused)
                {
                    FocusedClimbLog(
                        "OwnerWindow",
                        $"side=server operation=open id={m_ServerOwnerMotionOperationId} " +
                        $"duration={SERVER_OWNER_MOTION_WINDOW_SECONDS:F3} driver=server");
                }
                return;
            }

            // A host-owned character may intentionally use UnitDriverNetworkClient for local
            // prediction. In that configuration there is no server driver on this instance;
            // the owner-side authority window is the correct reconciliation/pose path.
            if (m_IsLocalClient &&
                (m_NetworkCharacter?.OwnerMotionAuthority ??
                 m_Character?.Driver as INetworkOwnerMotionAuthority) is INetworkOwnerMotionAuthority ownerAuthority)
            {
                m_ServerOwnerMotionWindowOpen = true;
                m_ServerOwnerMotionUsesClientAuthority = true;
                ownerAuthority.OpenOwnerMotionWindow(SERVER_OWNER_MOTION_WINDOW_SECONDS);
                if (m_ClimbDiagnosticFocused)
                {
                    FocusedClimbLog(
                        "OwnerWindow",
                        $"side=host operation=open id={m_ServerOwnerMotionOperationId} " +
                        $"duration={SERVER_OWNER_MOTION_WINDOW_SECONDS:F3} driver=client");
                }
                return;
            }

            m_ServerOwnerMotionOperationId = 0;
            WarnRateLimited(
                "server-owner-motion-authority",
                "[NetworkTraversalController] Server traversal cannot open a motion-authority window " +
                "because the active driver implements neither INetworkServerOwnerMotionAuthority nor, " +
                "for a host-owned client-predicted character, INetworkOwnerMotionAuthority.");
        }

        private void RefreshServerOwnerMotionWindow()
        {
            if (!m_IsServer || !m_ServerOwnerMotionWindowOpen) return;

            TraversalStance stance = m_TraversalStance ?? ResolveTraversalStance();
            if (stance?.Traverse == null)
            {
                CloseServerOwnerMotionWindow(SERVER_OWNER_MOTION_EXIT_GRACE_SECONDS);
                return;
            }

            if (m_ServerOwnerMotionUsesClientAuthority)
            {
                ActivateLocalTraversalPoseAuthority(SERVER_OWNER_MOTION_WINDOW_SECONDS);
                return;
            }

            if (m_Character?.Driver is INetworkServerOwnerMotionAuthority authority)
            {
                authority.OpenServerOwnerMotionWindow(
                    SERVER_OWNER_MOTION_WINDOW_SECONDS,
                    m_ServerOwnerMotionOperationId);
            }
        }

        private void CloseServerOwnerMotionWindow(float graceSeconds)
        {
            bool wasOpen = m_ServerOwnerMotionWindowOpen;
            uint operationId = m_ServerOwnerMotionOperationId;
            if (m_Character?.Driver is INetworkServerOwnerMotionAuthority authority)
            {
                authority.CloseServerOwnerMotionWindow(Mathf.Max(0f, graceSeconds));
            }
            else if (m_ServerOwnerMotionUsesClientAuthority && graceSeconds > 0f)
            {
                INetworkOwnerMotionAuthority ownerAuthority = m_NetworkCharacter?.OwnerMotionAuthority;
                ownerAuthority ??= m_Character?.Driver as INetworkOwnerMotionAuthority;
                ownerAuthority?.OpenOwnerMotionWindow(Mathf.Max(0f, graceSeconds));
            }

            m_ServerOwnerMotionWindowOpen = false;
            m_ServerOwnerMotionUsesClientAuthority = false;
            m_ServerOwnerMotionOperationId = 0;

            if (wasOpen && m_ClimbDiagnosticFocused)
            {
                FocusedClimbLog(
                    "OwnerWindow",
                    $"side=server operation=close id={operationId} grace={graceSeconds:F3} " +
                    $"pos={NetworkTraversalClimbDiagnostics.Vector(transform.position)}");
            }
        }

        private void TrackFocusedTraversalRequest(NetworkTraversalRequest request, Traverse traverse)
        {
            bool isPullUp = ContainsDiagnosticName(request.ActionIdString, "PullUp") ||
                            ContainsDiagnosticName(request.StateIdString, "PullUp") ||
                            ContainsDiagnosticName(traverse != null ? traverse.name : string.Empty, "PullUp");
            bool isClimb = TryGetFocusedClimbMotion(traverse, out _, out _);
            if (!isPullUp && !isClimb && !m_ClimbDiagnosticFocused) return;

            m_ClimbDiagnosticRequestId = request.RequestId;
            m_ClimbDiagnosticCorrelationId = request.CorrelationId;
            m_ClimbDiagnosticAction = !string.IsNullOrEmpty(request.ActionIdString)
                ? request.ActionIdString
                : request.Action.ToString();

            if (isPullUp)
            {
                m_PullUpDiagnosticUntilRealtime = Mathf.Max(
                    m_PullUpDiagnosticUntilRealtime,
                    Time.realtimeSinceStartup + 4f);
            }

            SetClimbDiagnosticFocus(true, "request", traverse, null);
            FocusedClimbLog(
                "Request",
                $"action={request.Action} actionId='{request.ActionIdString}' request={request.RequestId} " +
                $"corr={request.CorrelationId} actor={request.ActorNetworkId} target={request.TargetNetworkId} " +
                $"traverse='{traverse?.name ?? "none"}' traverseHash={request.TraverseHash} " +
                $"serverVersion={m_ServerStateVersion} appliedVersion={m_LastAppliedStateVersion}");
        }

        private void UpdateFocusedClimbDiagnostics()
        {
            if (!NetworkTraversalClimbDiagnostics.Enabled || m_Character == null) return;

            TraversalStance stance = m_TraversalStance ?? ResolveTraversalStance();
            Traverse traverse = stance != null ? stance.Traverse : null;
            bool hasClimb = TryGetFocusedClimbMotion(traverse, out TraverseInteractive interactive, out MotionInteractive motion);
            bool pullUpGrace = Time.realtimeSinceStartup < m_PullUpDiagnosticUntilRealtime;
            bool shouldFocus = hasClimb || pullUpGrace;

            SetClimbDiagnosticFocus(
                shouldFocus,
                hasClimb ? "climb-active" : pullUpGrace ? "pullup-checkpoint" : "climb-ended",
                traverse,
                motion);
            if (!shouldFocus) return;

            Vector2 rawInput = m_NetworkDirectionalPlayer != null
                ? m_NetworkDirectionalPlayer.RawInput
                : Vector2.zero;
            Vector3 worldInput = m_Character.Player?.InputDirection ?? Vector3.zero;
            Vector3 localInput = m_Character.Player?.LocalInputDirection ?? Vector3.zero;
            Vector3 motionDirection = m_Character.Motion?.MoveDirection ?? Vector3.zero;
            Vector3 driverWorld = m_Character.Driver?.WorldMoveDirection ?? Vector3.zero;
            Vector3 driverLocal = m_Character.Driver?.LocalMoveDirection ?? Vector3.zero;
            Vector3 relativePosition = ReadTraversalVector(s_TraversalStanceRelativePositionProperty, stance);
            bool allowMovement = ReadTraversalBool(s_TraversalStanceAllowMovementProperty, stance);
            bool inTransition = ReadTraversalBool(s_TraversalStanceInInteractiveTransitionProperty, stance);

            Vector3 mappedInput = localInput;
            string inputSource = "player-local";
            string mapX = "unknown";
            string mapY = "unknown";
            string mapZ = "unknown";
            int stateLayer = 0;

            if (motion != null && interactive != null)
            {
                try
                {
                    var args = new Args(interactive.gameObject, m_Character.gameObject);
                    if (s_MotionInteractiveInputDirectionField?.GetValue(motion) is PropertyGetDirection input)
                    {
                        mappedInput = input.Get(args);
                        inputSource = input.ToString();
                    }

                    mapX = s_MotionInteractiveInputXField?.GetValue(motion)?.ToString() ?? "unknown";
                    mapY = s_MotionInteractiveInputYField?.GetValue(motion)?.ToString() ?? "unknown";
                    mapZ = s_MotionInteractiveInputZField?.GetValue(motion)?.ToString() ?? "unknown";
                    if (s_MotionInteractiveLayerField?.GetValue(motion) is PropertyGetInteger layer)
                    {
                        stateLayer = (int)layer.Get(args);
                    }
                }
                catch (Exception exception)
                {
                    inputSource = $"error:{exception.GetType().Name}";
                }
            }

            Animator animator = FindFocusedAnimator();
            int stateHash = 0;
            float normalizedTime = 0f;
            bool animatorTransition = false;
            string clipName = "none";
            string currentClipBlend = "none";
            int nextStateHash = 0;
            float nextNormalizedTime = 0f;
            string nextClipName = "none";
            string nextClipBlend = "none";
            Vector3 speed = Vector3.zero;
            Vector3 intent = Vector3.zero;
            if (animator != null && animator.layerCount > 0)
            {
                int layer = Mathf.Clamp(stateLayer, 0, animator.layerCount - 1);
                AnimatorStateInfo state = animator.GetCurrentAnimatorStateInfo(layer);
                stateHash = state.fullPathHash;
                normalizedTime = state.normalizedTime;
                animatorTransition = animator.IsInTransition(layer);
                currentClipBlend = FormatFocusedClipBlend(
                    animator.GetCurrentAnimatorClipInfo(layer),
                    out clipName);
                if (animatorTransition)
                {
                    AnimatorStateInfo nextState = animator.GetNextAnimatorStateInfo(layer);
                    nextStateHash = nextState.fullPathHash;
                    nextNormalizedTime = nextState.normalizedTime;
                    nextClipBlend = FormatFocusedClipBlend(
                        animator.GetNextAnimatorClipInfo(layer),
                        out nextClipName);
                }

                speed = new Vector3(
                    animator.GetFloat(Animator.StringToHash("Speed-X")),
                    animator.GetFloat(Animator.StringToHash("Speed-Y")),
                    animator.GetFloat(Animator.StringToHash("Speed-Z")));
                intent = new Vector3(
                    animator.GetFloat(Animator.StringToHash("Intent-X")),
                    animator.GetFloat(Animator.StringToHash("Intent-Y")),
                    animator.GetFloat(Animator.StringToHash("Intent-Z")));
            }

            string axisSign = $"{Sign(rawInput.x)},{Sign(rawInput.y)}|{Sign(speed.x)},{Sign(speed.y)},{Sign(speed.z)}|{Sign(intent.x)},{Sign(intent.y)},{Sign(intent.z)}";
            string changeValue =
                $"{traverse?.GetInstanceID() ?? 0}:{motion?.GetInstanceID() ?? 0}:{stateHash}:{clipName}:" +
                $"{axisSign}:{allowMovement}:{inTransition}:{animatorTransition}";
            string changeKey = $"controller-state:{GetInstanceID()}";
            bool changed = NetworkTraversalClimbDiagnostics.HasChanged(changeKey, changeValue);

            if (!string.IsNullOrEmpty(m_LastClimbDominantClip) &&
                !string.Equals(m_LastClimbDominantClip, clipName, StringComparison.Ordinal))
            {
                float sincePreviousSwitch =
                    Time.realtimeSinceStartup - m_LastClimbDominantClipChangeRealtime;
                FocusedClimbLog(
                    "ClipSwitch",
                    $"from='{m_LastClimbDominantClip}' to='{clipName}' after={sincePreviousSwitch:F3}s " +
                    $"raw={NetworkTraversalClimbDiagnostics.Vector(rawInput)} " +
                    $"mapped={NetworkTraversalClimbDiagnostics.Vector(mappedInput)} " +
                    $"motionMove={NetworkTraversalClimbDiagnostics.Vector(motionDirection)} " +
                    $"driverLocal={NetworkTraversalClimbDiagnostics.Vector(driverLocal)} " +
                    $"speed={NetworkTraversalClimbDiagnostics.Vector(speed)} " +
                    $"intent={NetworkTraversalClimbDiagnostics.Vector(intent)} " +
                    $"currentBlend={currentClipBlend} nextBlend={nextClipBlend}");
                m_LastClimbDominantClipChangeRealtime = Time.realtimeSinceStartup;
            }
            else if (string.IsNullOrEmpty(m_LastClimbDominantClip))
            {
                m_LastClimbDominantClipChangeRealtime = Time.realtimeSinceStartup;
            }

            m_LastClimbDominantClip = clipName;

            if (stateHash != 0 && stateHash == m_LastClimbAnimatorStateHash &&
                m_LastClimbAnimatorNormalizedTime >= 0f &&
                normalizedTime + 0.2f < m_LastClimbAnimatorNormalizedTime)
            {
                FocusedClimbLog(
                    "AnimRestart",
                    $"state={stateHash} clip='{clipName}' normBefore={m_LastClimbAnimatorNormalizedTime:F3} " +
                    $"normAfter={normalizedTime:F3} transition={animatorTransition}");
            }

            m_LastClimbAnimatorStateHash = stateHash;
            m_LastClimbAnimatorNormalizedTime = normalizedTime;

            string message =
                $"actor={NetworkId} role={FormatRole()} serverVersion={m_ServerStateVersion} " +
                $"appliedVersion={m_LastAppliedStateVersion} request={m_ClimbDiagnosticRequestId} " +
                $"corr={m_ClimbDiagnosticCorrelationId} action='{m_ClimbDiagnosticAction}' " +
                $"traverse='{traverse?.name ?? "none"}' motion='{motion?.name ?? "none"}' " +
                $"pos={NetworkTraversalClimbDiagnostics.Vector(m_Character.transform.position)} " +
                $"raw={NetworkTraversalClimbDiagnostics.Vector(rawInput)} worldInput={NetworkTraversalClimbDiagnostics.Vector(worldInput)} " +
                $"localInput={NetworkTraversalClimbDiagnostics.Vector(localInput)} mapped={NetworkTraversalClimbDiagnostics.Vector(mappedInput)} " +
                $"inputSource='{inputSource}' map={mapX}/{mapY}/{mapZ} " +
                $"motionMove={NetworkTraversalClimbDiagnostics.Vector(motionDirection)} " +
                $"driverWorld={NetworkTraversalClimbDiagnostics.Vector(driverWorld)} driverLocal={NetworkTraversalClimbDiagnostics.Vector(driverLocal)} " +
                $"relative={NetworkTraversalClimbDiagnostics.Vector(relativePosition)} allowMove={allowMovement} transition={inTransition} " +
                $"speed={NetworkTraversalClimbDiagnostics.Vector(speed)} intent={NetworkTraversalClimbDiagnostics.Vector(intent)} " +
                $"state={stateHash} clip='{clipName}' blend={currentClipBlend} norm={normalizedTime:F3} " +
                $"animatorTransition={animatorTransition} nextState={nextStateHash} nextClip='{nextClipName}' " +
                $"nextBlend={nextClipBlend} nextNorm={nextNormalizedTime:F3} " +
                $"playables={FormatFocusedStatePlayables(stateLayer)} " +
                $"updateKinematics={m_Character.Driver?.UpdateKinematics ?? true} grounded={m_Character.Driver?.IsGrounded ?? false}";

            NetworkTraversalClimbDiagnostics.Log(
                changed ? "ClimbChange" : "Climb",
                message,
                this,
                changed ? null : $"controller-sample:{GetInstanceID()}");
        }

        private void SetClimbDiagnosticFocus(
            bool focused,
            string reason,
            Traverse traverse,
            MotionInteractive motion)
        {
            if (m_ClimbDiagnosticFocused == focused) return;

            m_ClimbDiagnosticFocused = focused;
            NetworkTraversalClimbDiagnostics.SetCharacterFocus(gameObject, NetworkId, focused);
            if (!focused)
            {
                m_LastClimbAnimatorNormalizedTime = -1f;
                m_LastClimbAnimatorStateHash = 0;
                m_LastClimbDominantClip = string.Empty;
                m_LastClimbDominantClipChangeRealtime = -100f;
                m_ClimbDiagnosticAnimator = null;
            }

            FocusedClimbLog(
                "Focus",
                $"active={focused} reason='{reason}' traverse='{traverse?.name ?? "none"}' " +
                $"motion='{motion?.name ?? "none"}' pos={NetworkTraversalClimbDiagnostics.Vector(transform.position)}");
        }

        private void FocusedClimbLog(string stage, string message, string sampleKey = null)
        {
            NetworkTraversalClimbDiagnostics.Log(
                stage,
                $"actor={NetworkId} role={FormatRole()} serverVersion={m_ServerStateVersion} " +
                $"appliedVersion={m_LastAppliedStateVersion} request={m_ClimbDiagnosticRequestId} " +
                $"corr={m_ClimbDiagnosticCorrelationId} {message}",
                this,
                sampleKey);
        }

        private static bool TryGetFocusedClimbMotion(
            Traverse traverse,
            out TraverseInteractive interactive,
            out MotionInteractive motion)
        {
            interactive = traverse as TraverseInteractive;
            motion = interactive != null ? interactive.MotionInteractive : null;
            if (motion == null) return false;

            return ContainsDiagnosticName(motion.name, "Motion_Free_Climb") ||
                   ContainsDiagnosticName(motion.name, "Motion_Ledge_Climb") ||
                   ContainsDiagnosticName(motion.name, "Free_Climb") ||
                   ContainsDiagnosticName(motion.name, "Ledge_Climb");
        }

        private static bool ContainsDiagnosticName(string value, string expected)
        {
            return !string.IsNullOrEmpty(value) &&
                   value.IndexOf(expected, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static Vector3 ReadTraversalVector(PropertyInfo property, TraversalStance stance)
        {
            return stance != null && property?.GetValue(stance) is Vector3 value
                ? value
                : Vector3.zero;
        }

        private static bool ReadTraversalBool(PropertyInfo property, TraversalStance stance)
        {
            return stance != null && property?.GetValue(stance) is bool value && value;
        }

        private Animator FindFocusedAnimator()
        {
            if (m_ClimbDiagnosticAnimator != null &&
                m_ClimbDiagnosticAnimator.runtimeAnimatorController != null)
            {
                return m_ClimbDiagnosticAnimator;
            }

            Animator[] animators = m_Character != null
                ? m_Character.GetComponentsInChildren<Animator>(true)
                : GetComponentsInChildren<Animator>(true);
            for (int i = 0; i < animators.Length; i++)
            {
                if (animators[i] != null && animators[i].runtimeAnimatorController != null)
                {
                    m_ClimbDiagnosticAnimator = animators[i];
                    return m_ClimbDiagnosticAnimator;
                }
            }

            return null;
        }

        private static string FormatFocusedClipBlend(
            AnimatorClipInfo[] clips,
            out string dominantClip)
        {
            dominantClip = "none";
            if (clips == null || clips.Length == 0) return "none";

            var builder = new StringBuilder("[");
            float dominantWeight = float.MinValue;
            int appended = 0;
            for (int i = 0; i < clips.Length && appended < 6; i++)
            {
                AnimationClip clip = clips[i].clip;
                if (clip == null) continue;

                if (appended > 0) builder.Append(',');
                builder.Append(clip.name).Append(':').Append(clips[i].weight.ToString("F3"));
                appended++;

                if (clips[i].weight > dominantWeight)
                {
                    dominantWeight = clips[i].weight;
                    dominantClip = clip.name;
                }
            }

            if (clips.Length > appended)
            {
                builder.Append(",more=").Append(clips.Length - appended);
            }

            builder.Append(']');
            return builder.ToString();
        }

        private string FormatFocusedStatePlayables(int stateLayer)
        {
            if (m_Character?.States == null || s_StatesOutputLayersField == null)
            {
                return "unavailable";
            }

            try
            {
                if (s_StatesOutputLayersField.GetValue(m_Character.States) is not
                    SortedList<int, List<StatePlayableBehaviour>> layers ||
                    !layers.TryGetValue(stateLayer, out List<StatePlayableBehaviour> behaviours) ||
                    behaviours == null || behaviours.Count == 0)
                {
                    return $"layer={stateLayer}:none";
                }

                var builder = new StringBuilder();
                builder.Append("layer=").Append(stateLayer).Append('[');
                int start = Mathf.Max(0, behaviours.Count - 4);
                for (int i = start; i < behaviours.Count; i++)
                {
                    if (i > start) builder.Append(',');
                    StatePlayableBehaviour behaviour = behaviours[i];
                    builder
                        .Append(behaviour.State != null ? behaviour.State.name : "clip/controller")
                        .Append(":weight=").Append(behaviour.CurrentWeight.ToString("F3"))
                        .Append(":exit=").Append(behaviour.IsExiting)
                        .Append(":complete=").Append(behaviour.IsComplete);
                }

                builder.Append(']');
                return builder.ToString();
            }
            catch (Exception exception)
            {
                return $"error:{exception.GetType().Name}";
            }
        }

        private static int Sign(float value)
        {
            return value > 0.05f ? 1 : value < -0.05f ? -1 : 0;
        }

        private TraversalStance ResolveTraversalStance()
        {
            if (m_Character == null || m_Character.Combat == null)
            {
                return null;
            }

            return m_Character.Combat.RequestStance<TraversalStance>();
        }

        private bool TryResolveTraverseByIdentity(int traverseHash, string traverseIdString, out Traverse traverse)
        {
            Traverse[] traverses = FindObjectsByType<Traverse>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);

            if (!string.IsNullOrEmpty(traverseIdString))
            {
                if (TryResolveTraverseByString(traverses, traverseIdString, out traverse))
                {
                    return true;
                }

                if (TryResolveTraverseByPose(traverses, traverseIdString, out traverse))
                {
                    LogTraversal(
                        $"resolved traverse by pose fallback requested='{traverseIdString}' " +
                        $"resolved='{BuildTraverseId(traverse)}'");
                    return true;
                }
            }

            if (traverseHash != 0)
            {
                for (int i = 0; i < traverses.Length; i++)
                {
                    Traverse candidate = traverses[i];
                    if (candidate == null) continue;

                    int candidateHash = StableHashUtility.GetStableHash(BuildTraverseId(candidate));
                    int legacyCandidateHash = StableHashUtility.GetStableHash(BuildLegacyIndexedTraverseId(candidate));
                    if (candidateHash != traverseHash && legacyCandidateHash != traverseHash) continue;

                    traverse = candidate;
                    return true;
                }
            }

            LogTraverseResolutionFailure(traverseHash, traverseIdString, traverses);

            traverse = null;
            return false;
        }

        private static bool TryResolveTraverseByString(
            IReadOnlyList<Traverse> traverses,
            string traverseIdString,
            out Traverse traverse)
        {
            for (int i = 0; i < traverses.Count; i++)
            {
                Traverse candidate = traverses[i];
                if (candidate == null) continue;

                string candidateId = BuildTraverseId(candidate);
                if (string.Equals(candidateId, traverseIdString, StringComparison.Ordinal))
                {
                    traverse = candidate;
                    return true;
                }

                string legacyCandidateId = BuildLegacyIndexedTraverseId(candidate);
                if (!string.Equals(legacyCandidateId, traverseIdString, StringComparison.Ordinal)) continue;

                traverse = candidate;
                return true;
            }

            traverse = null;
            return false;
        }

        private static bool TryResolveTraverseByPose(
            IReadOnlyList<Traverse> traverses,
            string traverseIdString,
            out Traverse traverse)
        {
            traverse = null;

            if (!TryReadQuantizedTraversePose(
                    traverseIdString,
                    out int expectedX,
                    out int expectedY,
                    out int expectedZ,
                    out int expectedRotX,
                    out int expectedRotY,
                    out int expectedRotZ))
            {
                return false;
            }

            string expectedType = ExtractRequestedTraverseType(traverseIdString);
            Traverse match = null;
            int matchCount = 0;

            for (int i = 0; i < traverses.Count; i++)
            {
                Traverse candidate = traverses[i];
                if (candidate == null || candidate.transform == null) continue;
                if (!string.IsNullOrEmpty(expectedType) &&
                    !string.Equals(candidate.GetType().FullName, expectedType, StringComparison.Ordinal))
                {
                    continue;
                }

                QuantizeTraversePose(
                    candidate.transform,
                    out int candidateX,
                    out int candidateY,
                    out int candidateZ,
                    out int candidateRotX,
                    out int candidateRotY,
                    out int candidateRotZ);

                if (candidateX != expectedX ||
                    candidateY != expectedY ||
                    candidateZ != expectedZ ||
                    candidateRotX != expectedRotX ||
                    candidateRotY != expectedRotY ||
                    candidateRotZ != expectedRotZ)
                {
                    continue;
                }

                match = candidate;
                matchCount++;
                if (matchCount > 1) break;
            }

            if (matchCount != 1) return false;

            traverse = match;
            return true;
        }

        private static string BuildTraverseId(Traverse traverse)
        {
            if (traverse == null)
            {
                return string.Empty;
            }

            Transform transform = traverse.transform;
            if (transform == null)
            {
                return traverse.GetType().FullName;
            }

            var builder = new StringBuilder(128);
            builder.Append(transform.gameObject.scene.path);
            builder.Append('|');

            var chain = new Stack<string>(8);
            Transform current = transform;
            while (current != null)
            {
                chain.Push(current.name);
                current = current.parent;
            }

            while (chain.Count > 0)
            {
                if (builder[builder.Length - 1] != '|') builder.Append('/');
                builder.Append(chain.Pop());
            }

            builder.Append('|');
            builder.Append(traverse.GetType().FullName);
            AppendQuantizedTraversePose(builder, transform);

            return builder.ToString();
        }

        private static string BuildLegacyIndexedTraverseId(Traverse traverse)
        {
            if (traverse == null)
            {
                return string.Empty;
            }

            Transform transform = traverse.transform;
            if (transform == null)
            {
                return traverse.GetType().FullName;
            }

            var builder = new StringBuilder(128);
            builder.Append(transform.gameObject.scene.path);
            builder.Append('|');

            var chain = new Stack<string>(8);
            Transform current = transform;
            while (current != null)
            {
                chain.Push($"{current.name}[{current.GetSiblingIndex()}]");
                current = current.parent;
            }

            while (chain.Count > 0)
            {
                if (builder[builder.Length - 1] != '|') builder.Append('/');
                builder.Append(chain.Pop());
            }

            builder.Append('|');
            builder.Append(traverse.GetType().FullName);

            return builder.ToString();
        }

        private static void AppendQuantizedTraversePose(StringBuilder builder, Transform transform)
        {
            QuantizeTraversePose(
                transform,
                out int x,
                out int y,
                out int z,
                out int rotX,
                out int rotY,
                out int rotZ);

            builder.Append("|pos=");
            builder.Append(x);
            builder.Append(',');
            builder.Append(y);
            builder.Append(',');
            builder.Append(z);
            builder.Append("|rot=");
            builder.Append(rotX);
            builder.Append(',');
            builder.Append(rotY);
            builder.Append(',');
            builder.Append(rotZ);
        }

        private static void QuantizeTraversePose(
            Transform transform,
            out int x,
            out int y,
            out int z,
            out int rotX,
            out int rotY,
            out int rotZ)
        {
            Vector3 position = transform.position;
            Vector3 euler = transform.rotation.eulerAngles;

            x = Mathf.RoundToInt(position.x * TRAVERSE_ID_POSITION_SCALE);
            y = Mathf.RoundToInt(position.y * TRAVERSE_ID_POSITION_SCALE);
            z = Mathf.RoundToInt(position.z * TRAVERSE_ID_POSITION_SCALE);
            rotX = Mathf.RoundToInt(NormalizeAngle(euler.x) * TRAVERSE_ID_ROTATION_SCALE);
            rotY = Mathf.RoundToInt(NormalizeAngle(euler.y) * TRAVERSE_ID_ROTATION_SCALE);
            rotZ = Mathf.RoundToInt(NormalizeAngle(euler.z) * TRAVERSE_ID_ROTATION_SCALE);
        }

        private static float NormalizeAngle(float angle)
        {
            angle = Mathf.Repeat(angle + 180f, 360f) - 180f;
            return Mathf.Abs(angle) <= 0.0001f ? 0f : angle;
        }

        private static bool TryReadQuantizedTraversePose(
            string traverseIdString,
            out int x,
            out int y,
            out int z,
            out int rotX,
            out int rotY,
            out int rotZ)
        {
            x = 0;
            y = 0;
            z = 0;
            rotX = 0;
            rotY = 0;
            rotZ = 0;

            if (string.IsNullOrEmpty(traverseIdString)) return false;

            int posStart = traverseIdString.IndexOf("|pos=", StringComparison.Ordinal);
            int rotStart = traverseIdString.IndexOf("|rot=", StringComparison.Ordinal);
            if (posStart < 0 || rotStart < 0 || rotStart <= posStart) return false;

            string positionText = traverseIdString.Substring(posStart + 5, rotStart - posStart - 5);
            string rotationText = traverseIdString.Substring(rotStart + 5);
            int nextSeparator = rotationText.IndexOf('|');
            if (nextSeparator >= 0)
            {
                rotationText = rotationText.Substring(0, nextSeparator);
            }

            return TryReadInt3(positionText, out x, out y, out z) &&
                   TryReadInt3(rotationText, out rotX, out rotY, out rotZ);
        }

        private static bool TryReadInt3(string value, out int x, out int y, out int z)
        {
            x = 0;
            y = 0;
            z = 0;

            string[] parts = value.Split(',');
            return parts.Length == 3 &&
                   int.TryParse(parts[0], out x) &&
                   int.TryParse(parts[1], out y) &&
                   int.TryParse(parts[2], out z);
        }

        private void LogTraverseResolutionFailure(
            int traverseHash,
            string traverseIdString,
            IReadOnlyList<Traverse> traverses)
        {
            if (!DiagnosticsEnabled && !m_LogRejections) return;

            string logKey = $"{traverseHash}:{traverseIdString}";
            if (!s_LoggedTraverseResolutionFailures.Add(logKey)) return;

            string requestedType = ExtractRequestedTraverseType(traverseIdString);
            string requestedLeaf = ExtractRequestedTraverseLeafName(traverseIdString);
            bool hasPose = TryReadQuantizedTraversePose(
                traverseIdString,
                out int expectedX,
                out int expectedY,
                out int expectedZ,
                out int expectedRotX,
                out int expectedRotY,
                out int expectedRotZ);

            var candidates = new StringBuilder(1024);
            int shown = 0;
            int matchingNameOrType = 0;

            for (int i = 0; i < traverses.Count; i++)
            {
                Traverse candidate = traverses[i];
                if (candidate == null) continue;

                bool typeMatches = string.IsNullOrEmpty(requestedType) ||
                    string.Equals(candidate.GetType().FullName, requestedType, StringComparison.Ordinal);
                bool nameMatches = string.IsNullOrEmpty(requestedLeaf) ||
                    string.Equals(candidate.name, requestedLeaf, StringComparison.Ordinal);

                if (!typeMatches && !nameMatches) continue;

                matchingNameOrType++;
                if (shown >= TRAVERSE_RESOLVE_LOG_CANDIDATE_LIMIT) continue;

                if (candidates.Length > 0) candidates.Append(" || ");
                candidates.Append(BuildTraverseId(candidate));
                shown++;
            }

            string expectedPose = hasPose
                ? $"pos={expectedX},{expectedY},{expectedZ} rot={expectedRotX},{expectedRotY},{expectedRotZ}"
                : "none";

            Debug.LogWarning(
                $"[TraversalResolveDebug][Controller] {name} netId={NetworkId} role={FormatRole()} " +
                $"failed to resolve traverse hash={traverseHash} id='{traverseIdString}' " +
                $"requestedType='{requestedType}' requestedLeaf='{requestedLeaf}' requestedPose={expectedPose} " +
                $"allTraverses={traverses.Count} matchingNameOrType={matchingNameOrType} " +
                $"shown={shown} candidates='{candidates}'",
                this);
        }

        private static string ExtractRequestedTraverseType(string traverseIdString)
        {
            if (string.IsNullOrEmpty(traverseIdString)) return string.Empty;

            string[] parts = traverseIdString.Split('|');
            return parts.Length >= 3 ? parts[2] : string.Empty;
        }

        private static string ExtractRequestedTraverseLeafName(string traverseIdString)
        {
            if (string.IsNullOrEmpty(traverseIdString)) return string.Empty;

            string[] parts = traverseIdString.Split('|');
            if (parts.Length < 2) return string.Empty;

            string path = parts[1];
            int slash = path.LastIndexOf('/');
            string leaf = slash >= 0 ? path.Substring(slash + 1) : path;
            int indexStart = leaf.LastIndexOf('[');
            return indexStart > 0 ? leaf.Substring(0, indexStart) : leaf;
        }

        private Args BuildArgs(uint selfNetworkId, uint targetNetworkId)
        {
            GameObject self = ResolveGameObject(selfNetworkId);
            if (self == null)
            {
                Character actorCharacter = ResolveCharacter(NetworkId);
                if (actorCharacter != null)
                {
                    self = actorCharacter.gameObject;
                }
            }

            GameObject target = ResolveGameObject(targetNetworkId);
            if (self == null) self = gameObject;
            if (target == null) target = self;

            return new Args(self, target);
        }

        private static Character ResolveCharacter(uint networkId)
        {
            if (networkId == 0) return null;

            NetworkTransportBridge bridge = NetworkTransportBridge.Active;
            return bridge != null ? bridge.ResolveCharacter(networkId) : null;
        }

        private static GameObject ResolveGameObject(uint networkId)
        {
            Character character = ResolveCharacter(networkId);
            return character != null ? character.gameObject : null;
        }

        private bool MatchesControlledCharacter(Character character)
        {
            return character != null && m_Character != null && ReferenceEquals(character, m_Character);
        }

        private bool MatchesControlledStance(TraversalStance stance)
        {
            return stance != null && m_Character != null && ReferenceEquals(stance.Character, m_Character);
        }

        private static uint ExtractNetworkId(GameObject gameObject)
        {
            if (gameObject == null) return 0;

            NetworkCharacter networkCharacter = gameObject.GetComponent<NetworkCharacter>();
            return networkCharacter != null ? networkCharacter.NetworkId : 0;
        }

        private void RejectLocalRequest(
            TraversalRejectionReason reason,
            string error,
            string diagnosticKey)
        {
            ClearLedgeEdgeIntent();
            WarnRateLimited(
                $"request-rejected:{diagnosticKey}",
                $"[NetworkTraversalController] Rejected local traversal request on '{name}': " +
                $"{reason} ({error})");
            OnTraversalRejected?.Invoke(reason, error);
        }

        private void ClearLedgeEdgeIntent()
        {
            NetworkTraversalManager.Instance?.ClearLedgeEdgeIntent(m_Character);
        }

        private void WarnRateLimited(string key, string message, float interval = 5f)
        {
            float now = Time.realtimeSinceStartup;
            if (m_DiagnosticTimes.TryGetValue(key, out float previous) && now - previous < interval)
            {
                return;
            }

            m_DiagnosticTimes[key] = now;
            Debug.LogWarning(message, this);
        }

        private void LogTraversal(string message)
        {
            if (!DiagnosticsEnabled) return;

            Debug.Log(
                $"[TraversalTrace][Controller] {name} netId={NetworkId} role={FormatRole()} " +
                $"pos={FormatVector(m_Character != null ? m_Character.transform.position : transform.position)} " +
                $"stance='{FormatTraverse(m_TraversalStance != null ? m_TraversalStance.Traverse : null)}' " +
                $"suppress={m_SuppressInterception} pending={m_PendingRequests.Count} recent={m_RecentlyAppliedCorrelations.Count} " +
                $"{message}",
                this);
        }

        private void LogTraversalPose(string message, Traverse traverse)
        {
            if (!DiagnosticsEnabled) return;

            Debug.Log(
                $"[TraversalPoseDebug][Controller] {name} netId={NetworkId} role={FormatRole()} " +
                $"{FormatCharacterPose()} {FormatTraversePose(traverse, m_Character)} {message}",
                this);
        }

        private void LogTraversalAnimation(string message)
        {
            if (!DiagnosticsEnabled) return;

            Debug.Log(
                $"[TraversalAnimDebug][Controller] {name} netId={NetworkId} role={FormatRole()} {message}",
                this);
        }

        private string FormatCharacterPose()
        {
            Transform characterTransform = m_Character != null ? m_Character.transform : transform;
            return
                $"characterPos={FormatVector(characterTransform.position)} " +
                $"characterRot={FormatQuaternion(characterTransform.rotation)} " +
                $"characterForward={FormatVector(characterTransform.forward)}";
        }

        private static string FormatTraversePose(Traverse traverse, Character character)
        {
            if (traverse == null)
            {
                return "traverse=none";
            }

            Transform traverseTransform = traverse.transform;
            Transform characterTransform = character != null ? character.transform : null;

            string localCharacterPosition = characterTransform != null
                ? FormatVector(traverseTransform.InverseTransformPoint(characterTransform.position))
                : "n/a";

            string startPosition = "n/a";
            string localStartPosition = "n/a";
            if (character != null)
            {
                try
                {
                    Vector3 start = traverse.CalculateStartPosition(character);
                    startPosition = FormatVector(start);
                    localStartPosition = FormatVector(traverseTransform.InverseTransformPoint(start));
                }
                catch (Exception exception)
                {
                    startPosition = $"error:{exception.GetType().Name}";
                }
            }

            string extra = string.Empty;
            if (traverse is TraverseInteractive interactive)
            {
                extra =
                    $" interactiveBoundsA={interactive.PositionA:F3} interactiveBoundsB={interactive.PositionB:F3} " +
                    $"interactiveWidth={interactive.Width:F3} rotationMode={interactive.RotationMode} " +
                    $"rotationIdle={interactive.RotationIdle} rotationValue={FormatVector(interactive.RotationValue)}";
            }
            else if (traverse is TraverseLink link && link.Type != null && character != null)
            {
                try
                {
                    TraverseLinkData data = link.Type.ToTraverseLinkData(character, link);
                    Vector3 worldA = traverseTransform.TransformPoint(data.positionA);
                    Vector3 worldB = traverseTransform.TransformPoint(data.positionB);
                    Quaternion worldRotA = traverseTransform.rotation * data.rotationA;
                    Quaternion worldRotB = traverseTransform.rotation * data.rotationB;
                    extra =
                        $" linkA={FormatVector(worldA)} linkB={FormatVector(worldB)} " +
                        $"linkLocalA={FormatVector(data.positionA)} linkLocalB={FormatVector(data.positionB)} " +
                        $"linkRotA={FormatQuaternion(worldRotA)} linkRotB={FormatQuaternion(worldRotB)}";
                }
                catch (Exception exception)
                {
                    extra = $" linkDataError={exception.GetType().Name}:{exception.Message}";
                }
            }

            return
                $"traverse='{FormatTraverse(traverse)}' traversePos={FormatVector(traverseTransform.position)} " +
                $"traverseRot={FormatQuaternion(traverseTransform.rotation)} " +
                $"traverseForward={FormatVector(traverseTransform.forward)} " +
                $"traverseRight={FormatVector(traverseTransform.right)} " +
                $"traverseUp={FormatVector(traverseTransform.up)} " +
                $"characterLocalOnTraverse={localCharacterPosition} calculatedStart={startPosition} " +
                $"calculatedLocalStart={localStartPosition} {FormatFacingAlignment(characterTransform, traverseTransform)}" +
                extra;
        }

        private static string FormatFacingAlignment(Transform characterTransform, Transform traverseTransform)
        {
            if (characterTransform == null || traverseTransform == null)
            {
                return "facingAlignment=n/a";
            }

            Vector3 characterForward = characterTransform.forward.normalized;
            Vector3 traverseForward = traverseTransform.forward.normalized;
            Vector3 traverseRight = traverseTransform.right.normalized;
            Vector3 traverseUp = traverseTransform.up.normalized;

            return
                $"dotForward={Vector3.Dot(characterForward, traverseForward):F3} " +
                $"dotBack={Vector3.Dot(characterForward, -traverseForward):F3} " +
                $"dotRight={Vector3.Dot(characterForward, traverseRight):F3} " +
                $"dotLeft={Vector3.Dot(characterForward, -traverseRight):F3} " +
                $"dotUp={Vector3.Dot(characterForward, traverseUp):F3} " +
                $"dotDown={Vector3.Dot(characterForward, -traverseUp):F3} " +
                $"horizontalYawToForward={HorizontalYawDelta(characterForward, traverseForward):F2} " +
                $"horizontalYawToBack={HorizontalYawDelta(characterForward, -traverseForward):F2} " +
                $"horizontalYawToUp={HorizontalYawDelta(characterForward, traverseUp):F2} " +
                $"horizontalYawToDown={HorizontalYawDelta(characterForward, -traverseUp):F2}";
        }

        private static float HorizontalYawDelta(Vector3 from, Vector3 to)
        {
            from.y = 0f;
            to.y = 0f;
            if (from.sqrMagnitude <= 0.000001f || to.sqrMagnitude <= 0.000001f) return 0f;
            return Vector3.SignedAngle(from.normalized, to.normalized, Vector3.up);
        }

        private string FormatRole()
        {
            if (m_IsServer && m_IsLocalClient) return "HostServerLocal";
            if (m_IsServer) return "Server";
            if (m_IsLocalClient) return "LocalClient";
            if (m_IsRemoteClient) return "RemoteClient";
            return "Uninitialized";
        }

        private static string FormatTraverse(Traverse traverse)
        {
            if (traverse == null) return "none";
            return $"{traverse.name}:{traverse.GetType().Name}";
        }

        private static string FormatVector(Vector3 value)
        {
            return $"({value.x:F3},{value.y:F3},{value.z:F3})";
        }

        private static string FormatVector2(Vector2 value)
        {
            return $"({value.x:F3},{value.y:F3})";
        }

        private static string FormatQuaternion(Quaternion value)
        {
            Vector3 euler = value.eulerAngles;
            return $"euler({euler.x:F2},{euler.y:F2},{euler.z:F2})";
        }

        private ushort GetNextRequestId()
        {
            if (m_NextRequestId == 0)
            {
                m_NextRequestId = 1;
            }

            m_LastIssuedRequestId = m_NextRequestId;
            m_NextRequestId++;
            if (m_NextRequestId == 0)
            {
                m_NextRequestId = 1;
            }

            return m_LastIssuedRequestId;
        }
    }
}
#endif
