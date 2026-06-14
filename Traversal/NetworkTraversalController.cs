#if GC2_TRAVERSAL
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
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
        private bool m_SuppressNextAuthoritativeMotionEnter;
        private bool m_SuppressNextAuthoritativeMotionExit;
        private float m_SuppressNextAuthoritativeMotionExitTime = -100f;
        private float m_LastEdgeConnectionRequestTime = -100f;

        private bool m_HasStoredTraversalMotionValues;
        private float m_StoredTraversalLinearSpeed;
        private float m_StoredTraversalAngularSpeed;

        private bool m_HostLocalInteractiveStateStarted;
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

        private Coroutine m_PendingServerExitSnapshotCoroutine;
        private string m_LastCompletedTraverseId = string.Empty;
        private int m_LastCompletedTraverseHash;
        private float m_LastCompletedTraversalTime = -1f;
        private Vector3 m_LastCompletedTraversalPosition;

        private const float REQUEST_TIMEOUT_SECONDS = 8f;
        private const float TRAVERSAL_POSE_AUTHORITY_REFRESH_SECONDS = 0.35f;
        private const float TRAVERSAL_POSE_AUTHORITY_EXIT_GRACE_SECONDS = 0.25f;
        private const float ACTIVE_SNAPSHOT_REPLAY_GUARD_SECONDS = 2f;
        private const float EDGE_CONNECTION_REQUEST_INTERVAL_SECONDS = 0.25f;
        private const float EDGE_CONNECTION_JUMP_MEMORY_SECONDS = 1.25f;
        private const float AUTHORITATIVE_CONNECTION_EXIT_SUPPRESSION_SECONDS = 2f;
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

        public uint NetworkId => m_NetworkCharacter != null ? m_NetworkCharacter.NetworkId : 0;

        public bool IsServer => m_IsServer;
        public bool IsLocalClient => m_IsLocalClient;
        public bool IsRemoteClient => m_IsRemoteClient;
        internal bool IsApplyingAuthoritativeChange => m_SuppressInterception;

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
            CancelPendingServerExitSnapshot();
            StopHostLocalInteractiveMotionState();
            RestoreTraversalMotionValues("disable");
            m_SuppressNextAuthoritativeMotionEnter = false;
            m_SuppressNextAuthoritativeMotionExit = false;
            m_SuppressNextAuthoritativeMotionExitTime = -100f;
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
            RefreshLocalTraversalPoseAuthority();

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

            if (m_LogAllChanges)
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
            if (!m_IsServer && manager == null)
            {
                LogTraversal($"request blocked missing manager action={request.Action} requestId={request.RequestId}");
                if (m_LogRejections)
                {
                    Debug.LogWarning("[NetworkTraversalController] NetworkTraversalManager instance not found");
                }

                OnTraversalRejected?.Invoke(TraversalRejectionReason.TargetNotFound, "NetworkTraversalManager missing");
                return;
            }

            m_PendingRequests[GetPendingKey(request.ActorNetworkId, request.CorrelationId, request.RequestId)] =
                new PendingTraversalRequest
                {
                    Request = request,
                    SentTime = Time.time
                };

            OnTraversalRequested?.Invoke(request);

            if (m_IsServer)
            {
                _ = ProcessLocalServerRequestAsync(request);
            }
            else
            {
                if (alreadyAppliedLocally || m_OptimisticUpdates)
                {
                    m_RecentlyAppliedCorrelations[request.CorrelationId] = Time.time;
                }

                if (m_OptimisticUpdates && !alreadyAppliedLocally)
                {
                    _ = ApplyAuthoritativeActionAsync(
                        request.Action,
                        traverse,
                        request.ActionIdString,
                        request.StateIdString,
                        request.ArgsSelfNetworkId,
                        request.ArgsTargetNetworkId);
                }

                manager.SendTraversalRequest(request);
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

        private bool ShouldRejectStaleDirectInteractiveEnter(
            in NetworkTraversalRequest request,
            Traverse traverse,
            out string reason)
        {
            reason = string.Empty;
            if (request.Action != TraversalActionType.EnterTraverseInteractive) return false;
            if (traverse is not TraverseInteractive targetInteractive) return false;

            Traverse currentTraverse = ResolveTraversalStance()?.Traverse;

            if (IsInteractiveConnectionRequest(request.ActionIdString))
            {
                if (currentTraverse is not TraverseInteractive currentInteractiveForConnection)
                {
                    reason =
                        $"interactive connection marker received while current traverse is " +
                        $"'{FormatTraverse(currentTraverse)}' target='{FormatTraverse(targetInteractive)}'";
                    return true;
                }

                if (ReferenceEquals(currentInteractiveForConnection, targetInteractive)) return false;

                if (!IsConfiguredInteractiveConnectionTarget(
                        currentInteractiveForConnection,
                        targetInteractive,
                        out string validationReason))
                {
                    reason =
                        $"target is not a valid configured connection current='{FormatTraverse(currentInteractiveForConnection)}' " +
                        $"target='{FormatTraverse(targetInteractive)}' reason='{validationReason}'";
                    return true;
                }

                return false;
            }

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
            NetworkTraversalResponse response = await ProcessTraversalRequestAsync(request, request.ActorNetworkId);
            response.ActorNetworkId = request.ActorNetworkId;
            response.CorrelationId = request.CorrelationId;
            ReceiveTraversalResponse(response);
        }

        public async Task<NetworkTraversalResponse> ProcessTraversalRequestAsync(NetworkTraversalRequest request, uint senderClientId)
        {
            LogTraversal(
                $"server processing request action={request.Action} requestId={request.RequestId} " +
                $"sender={senderClientId} actor={request.ActorNetworkId} target={request.TargetNetworkId} " +
                $"correlation={request.CorrelationId} traverse='{request.TraverseIdString}' hash={request.TraverseHash}");

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

            if (ShouldRejectStaleDirectInteractiveEnter(request, traverse, out string staleEnterError))
            {
                LogTraversal(
                    $"server rejected requestId={request.RequestId}: stale direct interactive enter " +
                    $"reason='{staleEnterError}' traverse='{request.TraverseIdString}' hash={request.TraverseHash}");
                return CreateRejectedResponse(request, TraversalRejectionReason.InvalidState, staleEnterError);
            }

            bool applied;
            try
            {
                applied = await ApplyRequestAuthoritativelyAsync(request, traverse);
            }
            catch (Exception exception)
            {
                LogTraversal($"server exception requestId={request.RequestId}: {exception.Message}");
                return CreateRejectedResponse(request, TraversalRejectionReason.Exception, exception.Message);
            }

            if (!applied)
            {
                LogTraversal($"server rejected requestId={request.RequestId}: runtime did not apply action={request.Action}");
                return CreateRejectedResponse(request, TraversalRejectionReason.InvalidState, "Traversal action rejected by runtime state");
            }

            NetworkTraversalResponse response = BuildSuccessResponse(request);
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

            if (m_LogAllChanges)
            {
                Debug.Log($"[NetworkTraversalController] Applied {request.Action} sender={senderClientId}");
            }

            return response;
        }

        public void ReceiveTraversalResponse(NetworkTraversalResponse response)
        {
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

                if (response.CorrelationId != 0)
                {
                    m_RecentlyAppliedCorrelations[response.CorrelationId] = Time.time;
                }

                if (!m_OptimisticUpdates && !alreadyApplied)
                {
                    _ = ApplyActionFromResponseAsync(response);
                }
            }
        }

        public async void ReceiveTraversalChangeBroadcast(NetworkTraversalBroadcast broadcast)
        {
            if (broadcast.NetworkId != NetworkId) return;
            if (m_IsServer) return;

            if (broadcast.CorrelationId != 0 &&
                m_RecentlyAppliedCorrelations.ContainsKey(broadcast.CorrelationId))
            {
                m_RecentlyAppliedCorrelations[broadcast.CorrelationId] = Time.time;
                LogTraversal(
                    $"client skipped predicted broadcast action={broadcast.Action} correlation={broadcast.CorrelationId} " +
                    $"traverse='{broadcast.TraverseIdString}'");
                return;
            }

            if (HasPendingRequestForCorrelation(broadcast.ActorNetworkId, broadcast.CorrelationId))
            {
                m_RecentlyAppliedCorrelations[broadcast.CorrelationId] = Time.time;
            }

            if (IsTraversalStartAction(broadcast.Action))
            {
                RemovePendingEquivalentStartRequests(
                    broadcast.Action,
                    broadcast.TraverseHash,
                    broadcast.TraverseIdString,
                    broadcast.CorrelationId,
                    "broadcast-applied");
            }

            LogTraversal(
                $"client applying broadcast action={broadcast.Action} actor={broadcast.ActorNetworkId} " +
                $"correlation={broadcast.CorrelationId} traversing={broadcast.IsTraversing} " +
                $"traverse='{broadcast.TraverseIdString}'");

            if (!broadcast.IsTraversing && IsTraversalStartAction(broadcast.Action))
            {
                LogTraversal(
                    $"client ignored non-traversing start broadcast action={broadcast.Action} " +
                    $"correlation={broadcast.CorrelationId}");
                return;
            }

            Traverse traverse = null;
            if (RequiresTraverse(broadcast.Action) &&
                !TryResolveTraverseByIdentity(broadcast.TraverseHash, broadcast.TraverseIdString, out traverse))
            {
                LogTraversal(
                    $"client broadcast ignored: failed to resolve traverse action={broadcast.Action} " +
                    $"traverse='{broadcast.TraverseIdString}' hash={broadcast.TraverseHash}");

                if (m_LogRejections)
                {
                    Debug.LogWarning($"[NetworkTraversalController] Could not resolve traverse for broadcast action {broadcast.Action}");
                }

                return;
            }

            await ApplyAuthoritativeActionAsync(
                broadcast.Action,
                traverse,
                broadcast.ActionIdString,
                broadcast.StateIdString,
                broadcast.ArgsSelfNetworkId,
                broadcast.ArgsTargetNetworkId);

            OnTraversalApplied?.Invoke(broadcast);
        }

        public NetworkTraversalSnapshot CaptureFullSnapshot()
        {
            Traverse currentTraverse = ResolveTraversalStance()?.Traverse;
            string traverseId = currentTraverse != null ? BuildTraverseId(currentTraverse) : string.Empty;
            LogTraversalPose(
                $"capture-full-snapshot traversing={currentTraverse != null} traverseId='{traverseId}'",
                currentTraverse);

            return new NetworkTraversalSnapshot
            {
                NetworkId = NetworkId,
                ServerTime = Time.time,
                IsTraversing = currentTraverse != null,
                TraverseHash = StableHashUtility.GetStableHash(traverseId),
                TraverseIdString = traverseId
            };
        }

        public async void ReceiveFullSnapshot(NetworkTraversalSnapshot snapshot)
        {
            if (snapshot.NetworkId != NetworkId) return;
            if (m_IsServer) return;

            TraversalStance stance = ResolveTraversalStance();
            if (stance == null) return;

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

            if (currentlyTraversing == snapshot.IsTraversing)
            {
                return;
            }

            if (!snapshot.IsTraversing)
            {
                LogTraversal("client forcing traversal cancel from non-traversing snapshot");
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

                return;
            }

            if (!TryResolveTraverseByIdentity(snapshot.TraverseHash, snapshot.TraverseIdString, out Traverse traverse))
            {
                LogTraversal(
                    $"client active snapshot ignored: failed to resolve traverse " +
                    $"traverse='{snapshot.TraverseIdString}' hash={snapshot.TraverseHash}");
                return;
            }

            if (traverse is TraverseLink &&
                IsRecentlyCompletedTraversalSnapshot(snapshot, out float completedAge))
            {
                LogTraversal(
                    $"client ignored active TraverseLink snapshot for recently completed traverse " +
                    $"age={completedAge:F3}s guard={ACTIVE_SNAPSHOT_REPLAY_GUARD_SECONDS:F3}s " +
                    $"snapshotTraverse='{snapshot.TraverseIdString}' lastCompleted='{m_LastCompletedTraverseId}' " +
                    $"lastCompletedPosition={FormatVector(m_LastCompletedTraversalPosition)} " +
                    $"currentPosition={FormatVector(m_Character != null ? m_Character.transform.position : transform.position)}");
                return;
            }

            await ApplyAuthoritativeActionAsync(
                traverse is TraverseLink ? TraversalActionType.RunTraverseLink : TraversalActionType.EnterTraverseInteractive,
                traverse,
                string.Empty,
                string.Empty,
                NetworkId,
                NetworkId);
        }

        private async Task ApplyActionFromResponseAsync(NetworkTraversalResponse response)
        {
            Traverse traverse = null;
            if (RequiresTraverse(response.Action) &&
                !TryResolveTraverseByIdentity(response.TraverseHash, response.TraverseIdString, out traverse))
            {
                LogTraversal(
                    $"client response apply failed to resolve traverse requestId={response.RequestId} " +
                    $"action={response.Action} traverse='{response.TraverseIdString}' hash={response.TraverseHash}");
                return;
            }

            await ApplyAuthoritativeActionAsync(
                response.Action,
                traverse,
                response.ActionIdString,
                response.StateIdString,
                response.ArgsSelfNetworkId,
                response.ArgsTargetNetworkId);
        }

        private Task<bool> ApplyAuthoritativeActionAsync(
            TraversalActionType action,
            Traverse traverse,
            string actionIdString,
            string stateIdString,
            uint argsSelfNetworkId,
            uint argsTargetNetworkId)
        {
            if (m_Character == null)
            {
                LogTraversal($"apply authoritative failed action={action}: missing character");
                return Task.FromResult(false);
            }

            TraversalStance stance = ResolveTraversalStance();
            if (stance == null)
            {
                LogTraversal($"apply authoritative failed action={action}: missing traversal stance");
                return Task.FromResult(false);
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
                            return Task.FromResult(false);
                        }

                        if (ReferenceEquals(stance.Traverse, traverseLink))
                        {
                            LogTraversal($"apply authoritative duplicate start ignored by runtime action={action} traverse='{FormatTraverse(traverseLink)}'");
                            return Task.FromResult(true);
                        }

                        SuppressNextClientMotionEnterFromAuthoritativeStart(action, traverseLink);
                        _ = ObserveAuthoritativeTraversalTask(
                            traverseLink.Run(m_Character),
                            action,
                            traverseLink);
                        LogTraversal($"apply authoritative started async traversal action={action}");
                        LogTraversalPose($"apply-authoritative-task-started action={action}", traverseLink);
                        StartTraversalAnimationDiagnostics(action, traverseLink, "authoritative-link-start");
                        return Task.FromResult(true);

                    case TraversalActionType.EnterTraverseInteractive:
                        if (traverse is not TraverseInteractive traverseInteractive)
                        {
                            LogTraversal($"apply authoritative failed action={action}: traverse is {FormatTraverse(traverse)}");
                            return Task.FromResult(false);
                        }

                        if (ReferenceEquals(stance.Traverse, traverseInteractive))
                        {
                            LogTraversal($"apply authoritative duplicate start ignored by runtime action={action} traverse='{FormatTraverse(traverseInteractive)}'");
                            return Task.FromResult(true);
                        }

                        if (stance.Traverse is TraverseInteractive currentInteractive)
                        {
                            SuppressNextClientMotionExitFromAuthoritativeConnection(
                                action,
                                currentInteractive,
                                traverseInteractive);
                            SuppressNextClientMotionEnterFromAuthoritativeStart(action, traverseInteractive);
                            _ = ObserveAuthoritativeTraversalTask(
                                Traverse.ChangeTo(currentInteractive, traverseInteractive, m_Character, false),
                                action,
                                traverseInteractive);
                            LogTraversal(
                                $"apply authoritative started interactive connection change-to action={action} " +
                                $"from='{FormatTraverse(currentInteractive)}' to='{FormatTraverse(traverseInteractive)}' " +
                                $"actionId='{actionIdString}'");
                            LogTraversalPose($"apply-authoritative-task-started action={action}", traverseInteractive);
                            StartTraversalAnimationDiagnostics(action, traverseInteractive, "authoritative-interactive-connection-start");
                            return Task.FromResult(true);
                        }

                        SuppressNextClientMotionEnterFromAuthoritativeStart(action, traverseInteractive);
                        _ = ObserveAuthoritativeTraversalTask(
                            traverseInteractive.Enter(m_Character, InteractiveTransitionData.None),
                            action,
                            traverseInteractive);
                        LogTraversal($"apply authoritative started async traversal action={action}");
                        LogTraversalPose($"apply-authoritative-task-started action={action}", traverseInteractive);
                        StartTraversalAnimationDiagnostics(action, traverseInteractive, "authoritative-interactive-start");
                        return Task.FromResult(true);

                    case TraversalActionType.TryCancel:
                        stance.TryCancel(BuildArgs(argsSelfNetworkId, argsTargetNetworkId));
                        return Task.FromResult(true);

                    case TraversalActionType.ForceCancel:
                        return Task.FromResult(stance.ForceCancel());

                    case TraversalActionType.TryJump:
                        if (TryStartInteractiveJumpConnection(stance))
                        {
                            return Task.FromResult(true);
                        }

                        stance.TryJump();
                        return Task.FromResult(true);

                    case TraversalActionType.TryAction:
                        if (string.IsNullOrEmpty(actionIdString)) return Task.FromResult(false);
                        stance.TryAction(new IdString(actionIdString));
                        return Task.FromResult(true);

                    case TraversalActionType.TryStateEnter:
                        if (string.IsNullOrEmpty(stateIdString)) return Task.FromResult(false);
                        stance.TryStateEnter(new IdString(stateIdString));
                        return Task.FromResult(true);

                    case TraversalActionType.TryStateExit:
                        stance.TryStateExit();
                        return Task.FromResult(true);

                    default:
                        return Task.FromResult(false);
                }
            }
            finally
            {
                m_SuppressInterception = previousSuppress;
            }
        }

        private void SuppressNextClientMotionEnterFromAuthoritativeStart(
            TraversalActionType action,
            Traverse traverse)
        {
            if (m_IsServer) return;

            m_SuppressNextAuthoritativeMotionEnter = true;
            LogTraversal(
                $"armed next client motion-enter suppression for authoritative start " +
                $"action={action} traverse='{FormatTraverse(traverse)}'");
        }

        private void SuppressNextClientMotionExitFromAuthoritativeConnection(
            TraversalActionType action,
            Traverse current,
            Traverse next)
        {
            if (m_IsServer) return;

            m_SuppressNextAuthoritativeMotionExit = true;
            m_SuppressNextAuthoritativeMotionExitTime = Time.time;

            LogTraversal(
                $"armed next client motion-exit suppression for authoritative connection " +
                $"action={action} from='{FormatTraverse(current)}' to='{FormatTraverse(next)}'");
        }

        private bool TryConsumeNextAuthoritativeMotionExitSuppression(Traverse exitingTraverse)
        {
            if (!m_SuppressNextAuthoritativeMotionExit)
            {
                return false;
            }

            float age = Time.time - m_SuppressNextAuthoritativeMotionExitTime;
            m_SuppressNextAuthoritativeMotionExit = false;
            m_SuppressNextAuthoritativeMotionExitTime = -100f;

            if (age <= AUTHORITATIVE_CONNECTION_EXIT_SUPPRESSION_SECONDS)
            {
                LogTraversal(
                    $"client motion exit suppressed for authoritative connection " +
                    $"exiting='{FormatTraverse(exitingTraverse)}' age={age:F3}");
                return true;
            }

            LogTraversal(
                $"stale authoritative motion-exit suppression ignored " +
                $"exiting='{FormatTraverse(exitingTraverse)}' age={age:F3}");
            return false;
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
                    request.ArgsTargetNetworkId);
            }
            finally
            {
                m_HasActiveAuthoritativeRequest = previousHasActiveRequest;
                m_ActiveAuthoritativeRequest = previousRequest;
            }
        }

        private bool TryStartInteractiveJumpConnection(TraversalStance stance)
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

            m_LastTryJumpStartedInteractiveConnection = true;
            if (m_HasActiveAuthoritativeRequest)
            {
                m_HasDeferredStartBroadcastRequest = true;
                m_DeferredStartBroadcastRequest = m_ActiveAuthoritativeRequest;
            }

            _ = ObserveAuthoritativeTraversalTask(
                Traverse.ChangeTo(interactive, nextTraverse, m_Character, false),
                TraversalActionType.TryJump,
                nextTraverse);
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
            string currentTraverseId = currentTraverse != null ? BuildTraverseId(currentTraverse) : request.TraverseIdString;
            int currentTraverseHash = StableHashUtility.GetStableHash(currentTraverseId);

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
                Error = string.Empty
            };
        }

        private NetworkTraversalBroadcast BuildBroadcast(in NetworkTraversalRequest request)
        {
            Traverse currentTraverse = ResolveTraversalStance()?.Traverse;
            string currentTraverseId = currentTraverse != null ? BuildTraverseId(currentTraverse) : request.TraverseIdString;
            int currentTraverseHash = StableHashUtility.GetStableHash(currentTraverseId);

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

        private bool ValidateRequestIdentity(in NetworkTraversalRequest request, out string error)
        {
            error = string.Empty;

            if (!MatchesStableHash(request.ActionIdHash, request.ActionIdString, allowEmpty: true))
            {
                LogTraversal(
                    $"server accepted action id string despite hash mismatch " +
                    $"actionId='{request.ActionIdString}' hash={request.ActionIdHash} " +
                    $"expected={GetOptionalStableHash(request.ActionIdString)}");
            }

            if (!MatchesStableHash(request.StateIdHash, request.StateIdString, allowEmpty: true))
            {
                LogTraversal(
                    $"server accepted state id string despite hash mismatch " +
                    $"stateId='{request.StateIdString}' hash={request.StateIdHash} " +
                    $"expected={GetOptionalStableHash(request.StateIdString)}");
            }

            if (!MatchesStableHash(request.TraverseHash, request.TraverseIdString, allowEmpty: true))
            {
                error = "Traverse hash does not match traverse id string";
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
            if (manager == null) return;

            uint networkId = NetworkId;
            if (networkId == 0) return;

            if (m_IsRegistered && m_RegisteredNetworkId == networkId)
            {
                return;
            }

            if (m_IsRegistered)
            {
                manager.UnregisterController(m_RegisteredNetworkId);
            }

            manager.RegisterController(networkId, this);
            m_IsRegistered = true;
            m_RegisteredNetworkId = networkId;
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

            Debug.Log(
                $"[TraversalAnimDebug][Controller] {name} netId={NetworkId} role={FormatRole()} " +
                $"host-local stop prestarted interactive state layer={m_HostLocalInteractiveStateLayer} " +
                $"transitionOut={m_HostLocalInteractiveStateTransitionOut:F3} " +
                $"position={FormatVector(m_Character.transform.position)}",
                this);

            m_Character.States.Stop(
                m_HostLocalInteractiveStateLayer,
                0f,
                m_HostLocalInteractiveStateTransitionOut);

            m_HostLocalInteractiveStateStarted = false;
            m_HostLocalInteractiveStateLayer = -1;
            m_HostLocalInteractiveStateTransitionOut = 0f;
        }

        private void StartHostLocalInteractiveMotionState(TraverseInteractive interactive)
        {
            if (interactive == null || m_Character == null)
            {
                return;
            }

            MotionInteractive motion = interactive.MotionInteractive;
            if (motion == null)
            {
                Debug.Log(
                    $"[TraversalAnimDebug][Controller] {name} netId={NetworkId} role={FormatRole()} " +
                    $"host-local prestart skipped: interactive motion is null traverse='{FormatTraverse(interactive)}'",
                    this);
                return;
            }

            Args args = new Args(interactive.gameObject, m_Character.gameObject);
            if (!motion.CanUse(args))
            {
                Debug.Log(
                    $"[TraversalAnimDebug][Controller] {name} netId={NetworkId} role={FormatRole()} " +
                    $"host-local prestart skipped: motion CanUse returned false motion='{motion.name}' " +
                    $"traverse='{FormatTraverse(interactive)}'",
                    this);
                return;
            }

            State state = s_MotionInteractiveAnimationStateField?.GetValue(motion) as State;
            if (state == null)
            {
                Debug.Log(
                    $"[TraversalAnimDebug][Controller] {name} netId={NetworkId} role={FormatRole()} " +
                    $"host-local prestart skipped: motion animation state is null motion='{motion.name}' " +
                    $"traverse='{FormatTraverse(interactive)}'",
                    this);
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

                Debug.Log(
                    $"[TraversalAnimDebug][Controller] {name} netId={NetworkId} role={FormatRole()} " +
                    $"host-local prestart state motion='{motion.name}' state='{state.name}' " +
                    $"layer={stateLayer} speed={speed:F3} transitionIn={motion.TransitionIn:F3} " +
                    $"transitionOut={motion.TransitionOut:F3} traverse='{FormatTraverse(interactive)}' " +
                    $"position={FormatVector(m_Character.transform.position)}",
                    this);

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
                Debug.Log(
                    $"[TraversalAnimDebug][Controller] {name} netId={NetworkId} role={FormatRole()} " +
                    $"host-local link prestart skipped: link motion is null traverse='{FormatTraverse(link)}'",
                    this);
                return;
            }

            Args args = new Args(link.gameObject, m_Character.gameObject);
            if (!motion.CanUse(args))
            {
                Debug.Log(
                    $"[TraversalAnimDebug][Controller] {name} netId={NetworkId} role={FormatRole()} " +
                    $"host-local link prestart skipped: motion CanUse returned false motion='{motion.name}' " +
                    $"traverse='{FormatTraverse(link)}'",
                    this);
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
                            Debug.Log(
                                $"[TraversalAnimDebug][Controller] {name} netId={NetworkId} role={FormatRole()} " +
                                $"host-local link prestart skipped: motion clip is null motion='{motion.name}' " +
                                $"traverse='{FormatTraverse(link)}'",
                                this);
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

                        Debug.Log(
                            $"[TraversalAnimDebug][Controller] {name} netId={NetworkId} role={FormatRole()} " +
                            $"host-local link prestart gesture motion='{motion.name}' clip='{clip.name}' " +
                            $"clipLength={clip.length:F3} speed={speed:F3} transitionIn={motion.TransitionIn:F3} " +
                            $"transitionOut={motion.TransitionOut:F3} mask='{(mask != null ? mask.name : "none")}' " +
                            $"traverse='{FormatTraverse(link)}' position={FormatVector(m_Character.transform.position)}",
                            this);

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
                            Debug.Log(
                                $"[TraversalAnimDebug][Controller] {name} netId={NetworkId} role={FormatRole()} " +
                                $"host-local link prestart skipped: motion state is null motion='{motion.name}' " +
                                $"traverse='{FormatTraverse(link)}'",
                                this);
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

                        Debug.Log(
                            $"[TraversalAnimDebug][Controller] {name} netId={NetworkId} role={FormatRole()} " +
                            $"host-local link prestart state motion='{motion.name}' state='{state.name}' " +
                            $"layer={stateLayer} speed={speed:F3} transitionIn={motion.TransitionIn:F3} " +
                            $"transitionOut={motion.TransitionOut:F3} traverse='{FormatTraverse(link)}' " +
                            $"position={FormatVector(m_Character.transform.position)}",
                            this);

                        _ = ObserveHostLocalLinkMotionTask(
                            m_Character.States.SetState(state, stateLayer, BlendMode.Blend, stateConfig),
                            motion.name,
                            "state",
                            state.name,
                            stateLayer);
                        break;
                    }

                    default:
                        Debug.Log(
                            $"[TraversalAnimDebug][Controller] {name} netId={NetworkId} role={FormatRole()} " +
                            $"host-local link prestart skipped: unsupported mode={motion.AnimationMode} " +
                            $"motion='{motion.name}' traverse='{FormatTraverse(link)}'",
                            this);
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
                Debug.Log(
                    $"[TraversalAnimDebug][Controller] {name} netId={NetworkId} role={FormatRole()} " +
                    $"host-local link {animationType} completed motion='{motionName}' " +
                    $"{animationType}='{animationName}'{layerText} " +
                    $"position={FormatVector(m_Character != null ? m_Character.transform.position : transform.position)}",
                    this);
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
                Debug.Log(
                    $"[TraversalAnimDebug][Controller] {name} netId={NetworkId} role={FormatRole()} " +
                    $"host-local state completed motion='{motionName}' state='{stateName}' " +
                    $"layer={stateLayer} position={FormatVector(m_Character != null ? m_Character.transform.position : transform.position)}",
                    this);
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
            Debug.Log(
                $"[TraversalAnimDebug][Controller] {name} netId={NetworkId} role={FormatRole()} " +
                $"anim-snapshot label='{label}' action={action} traverse='{FormatTraverse(traverse)}' " +
                $"stance='{FormatTraverse(m_TraversalStance != null ? m_TraversalStance.Traverse : null)}' " +
                $"suppress={m_SuppressInterception} server={m_IsServer} local={m_IsLocalClient} remote={m_IsRemoteClient} " +
                $"position={FormatVector(m_Character != null ? m_Character.transform.position : transform.position)} " +
                $"{FormatAnimimNetworkControllerSnapshot()} {FormatAnimimStatesSnapshot()} " +
                $"{FormatAnimimGesturesSnapshot()} {FormatAnimatorSnapshot()}",
                this);
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

            if (m_SuppressInterception || m_SuppressNextAuthoritativeMotionEnter)
            {
                bool deferredSuppression = m_SuppressNextAuthoritativeMotionEnter && !m_SuppressInterception;
                m_SuppressNextAuthoritativeMotionEnter = false;

                LogTraversal(
                    $"client motion enter suppressed traverse='{FormatTraverse(traverse)}' " +
                    $"deferred={deferredSuppression}");
                ActivateLocalTraversalPoseAuthority(TRAVERSAL_POSE_AUTHORITY_REFRESH_SECONDS);
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
            if (exitingTraverse != null)
            {
                LogTraversalPose("motion-exit-event", exitingTraverse);
                RememberCompletedTraversal(exitingTraverse);
            }

            StopHostLocalInteractiveMotionState();
            RestoreTraversalMotionValues("motion-exit");

            if (m_IsServer)
            {
                string exitingTraverseId = exitingTraverse != null ? BuildTraverseId(exitingTraverse) : string.Empty;
                ScheduleServerTraversalExitSnapshot(exitingTraverseId);
                return;
            }

            if (m_SuppressInterception)
            {
                LogTraversal("client motion exit suppressed");
                if (m_SuppressNextAuthoritativeMotionExit)
                {
                    m_SuppressNextAuthoritativeMotionExit = false;
                    m_SuppressNextAuthoritativeMotionExitTime = -100f;
                    LogTraversal("cleared pending authoritative motion-exit suppression");
                }
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
            NetworkTraversalManager.Instance?.BroadcastFullSnapshot(snapshot);
        }

        private void CancelPendingServerExitSnapshot()
        {
            if (m_PendingServerExitSnapshotCoroutine == null) return;

            StopCoroutine(m_PendingServerExitSnapshotCoroutine);
            m_PendingServerExitSnapshotCoroutine = null;
        }

        private void RememberCompletedTraversal(Traverse traverse)
        {
            m_LastCompletedTraverseId = BuildTraverseId(traverse);
            m_LastCompletedTraverseHash = StableHashUtility.GetStableHash(m_LastCompletedTraverseId);
            m_LastCompletedTraversalTime = Time.time;
            m_LastCompletedTraversalPosition = m_Character != null
                ? m_Character.transform.position
                : transform.position;
        }

        private bool IsRecentlyCompletedTraversalSnapshot(
            in NetworkTraversalSnapshot snapshot,
            out float completedAge)
        {
            completedAge = m_LastCompletedTraversalTime >= 0f
                ? Time.time - m_LastCompletedTraversalTime
                : float.PositiveInfinity;

            if (completedAge > ACTIVE_SNAPSHOT_REPLAY_GUARD_SECONDS)
            {
                return false;
            }

            if (!string.IsNullOrEmpty(snapshot.TraverseIdString) &&
                string.Equals(snapshot.TraverseIdString, m_LastCompletedTraverseId, StringComparison.Ordinal))
            {
                return true;
            }

            return snapshot.TraverseHash != 0 &&
                   snapshot.TraverseHash == m_LastCompletedTraverseHash;
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

            UnitDriverNetworkClient clientDriver = m_NetworkCharacter != null
                ? m_NetworkCharacter.ClientDriver
                : null;
            clientDriver ??= m_Character?.Driver as UnitDriverNetworkClient;
            if (clientDriver == null) return;

            clientDriver.SuppressReconciliation(duration);
            clientDriver.EnableOwnerAuthorityPoseSync(duration);
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

        private void LogTraversal(string message)
        {
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
            Debug.Log(
                $"[TraversalPoseDebug][Controller] {name} netId={NetworkId} role={FormatRole()} " +
                $"{FormatCharacterPose()} {FormatTraversePose(traverse, m_Character)} {message}",
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
