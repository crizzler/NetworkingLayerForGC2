#if GC2_TRAVERSAL
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using GameCreator.Runtime.Characters;
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
        private bool m_HasActiveAuthoritativeRequest;
        private NetworkTraversalRequest m_ActiveAuthoritativeRequest;

        private readonly Dictionary<ulong, PendingTraversalRequest> m_PendingRequests = new(16);
        private readonly Dictionary<uint, float> m_RecentlyAppliedCorrelations = new(16);
        private readonly List<ulong> m_PendingRemovalBuffer = new(8);
        private readonly List<uint> m_CorrelationRemovalBuffer = new(8);

        private const float REQUEST_TIMEOUT_SECONDS = 8f;

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
        }

        private void OnDisable()
        {
            RemoveTraversalStanceSubscription();
            UnregisterFromManager();
        }

        private void Update()
        {
            EnsureRegisteredWithManager();
            EnsureTraversalStanceSubscription();
            CleanupPendingRequests();

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
                if (m_LogRejections)
                {
                    Debug.LogWarning("[NetworkTraversalController] Cannot request traversal changes from a remote proxy");
                }

                return;
            }

            uint networkId = NetworkId;
            if (networkId == 0)
            {
                if (m_LogRejections)
                {
                    Debug.LogWarning("[NetworkTraversalController] Missing NetworkId; cannot send traversal request");
                }

                OnTraversalRejected?.Invoke(TraversalRejectionReason.TargetNotFound, "Missing NetworkId");
                return;
            }

            if (RequiresTraverse(action) && traverse == null)
            {
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

            NetworkTraversalManager manager = NetworkTraversalManager.Instance;
            if (!m_IsServer && manager == null)
            {
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

            if (m_IsServer && !IsTraversalStartAction(request.Action))
            {
                NetworkTraversalBroadcast broadcast = BuildBroadcast(request);
                LogTraversal(
                    $"server broadcasting action={broadcast.Action} requestId={request.RequestId} " +
                    $"networkId={broadcast.NetworkId} traversing={broadcast.IsTraversing} " +
                    $"traverse='{broadcast.TraverseIdString}' correlation={broadcast.CorrelationId}");
                NetworkTraversalManager.Instance?.BroadcastTraversalChange(broadcast);
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

            if (TryResolveTraverseByIdentity(snapshot.TraverseHash, snapshot.TraverseIdString, out Traverse traverse))
            {
                await ApplyAuthoritativeActionAsync(
                    traverse is TraverseLink ? TraversalActionType.RunTraverseLink : TraversalActionType.EnterTraverseInteractive,
                    traverse,
                    string.Empty,
                    string.Empty,
                    NetworkId,
                    NetworkId);
            }
        }

        private async Task ApplyActionFromResponseAsync(NetworkTraversalResponse response)
        {
            Traverse traverse = null;
            if (RequiresTraverse(response.Action) &&
                !TryResolveTraverseByIdentity(response.TraverseHash, response.TraverseIdString, out traverse))
            {
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
            if (m_Character == null) return Task.FromResult(false);

            TraversalStance stance = ResolveTraversalStance();
            if (stance == null) return Task.FromResult(false);

            bool previousSuppress = m_SuppressInterception;
            m_SuppressInterception = true;

            try
            {
                LogTraversal(
                    $"apply authoritative begin action={action} traverse='{FormatTraverse(traverse)}' " +
                    $"stanceTraverse='{FormatTraverse(stance.Traverse)}' self={argsSelfNetworkId} target={argsTargetNetworkId} " +
                    $"position={FormatVector(m_Character.transform.position)}");

                switch (action)
                {
                    case TraversalActionType.RunTraverseLink:
                        if (traverse is not TraverseLink traverseLink) return Task.FromResult(false);
                        _ = ObserveAuthoritativeTraversalTask(
                            traverseLink.Run(m_Character),
                            action,
                            FormatTraverse(traverseLink));
                        LogTraversal($"apply authoritative started async traversal action={action}");
                        return Task.FromResult(true);

                    case TraversalActionType.EnterTraverseInteractive:
                        if (traverse is not TraverseInteractive traverseInteractive) return Task.FromResult(false);
                        _ = ObserveAuthoritativeTraversalTask(
                            traverseInteractive.Enter(m_Character, InteractiveTransitionData.None),
                            action,
                            FormatTraverse(traverseInteractive));
                        LogTraversal($"apply authoritative started async traversal action={action}");
                        return Task.FromResult(true);

                    case TraversalActionType.TryCancel:
                        stance.TryCancel(BuildArgs(argsSelfNetworkId, argsTargetNetworkId));
                        return Task.FromResult(true);

                    case TraversalActionType.ForceCancel:
                        return Task.FromResult(stance.ForceCancel());

                    case TraversalActionType.TryJump:
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

        private async Task<bool> ApplyRequestAuthoritativelyAsync(
            NetworkTraversalRequest request,
            Traverse traverse)
        {
            bool previousHasActiveRequest = m_HasActiveAuthoritativeRequest;
            NetworkTraversalRequest previousRequest = m_ActiveAuthoritativeRequest;

            m_HasActiveAuthoritativeRequest = true;
            m_ActiveAuthoritativeRequest = request;

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

        private async Task ObserveAuthoritativeTraversalTask(
            Task traversalTask,
            TraversalActionType action,
            string traverseName)
        {
            try
            {
                await traversalTask;
                LogTraversal(
                    $"authoritative traversal task completed action={action} traverse='{traverseName}' " +
                    $"position={FormatVector(m_Character != null ? m_Character.transform.position : transform.position)}");
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

        private void OnLocalTraversalMotionEnter()
        {
            TraversalStance stance = m_TraversalStance;
            Traverse traverse = stance != null ? stance.Traverse : null;
            if (traverse == null) return;

            if (m_IsServer)
            {
                NetworkTraversalManager manager = NetworkTraversalManager.Instance;
                if (manager != null)
                {
                    uint actorNetworkId = m_HasActiveAuthoritativeRequest
                        ? m_ActiveAuthoritativeRequest.ActorNetworkId
                        : NetworkId;
                    uint correlationId = m_HasActiveAuthoritativeRequest
                        ? m_ActiveAuthoritativeRequest.CorrelationId
                        : 0;
                    uint argsSelfNetworkId = m_HasActiveAuthoritativeRequest
                        ? m_ActiveAuthoritativeRequest.ArgsSelfNetworkId
                        : NetworkId;
                    uint argsTargetNetworkId = m_HasActiveAuthoritativeRequest
                        ? m_ActiveAuthoritativeRequest.ArgsTargetNetworkId
                        : NetworkId;
                    string traverseId = BuildTraverseId(traverse);

                    LogTraversal(
                        $"server motion enter broadcast traverse='{traverseId}' actor={actorNetworkId} " +
                        $"correlation={correlationId} suppress={m_SuppressInterception} " +
                        $"position={FormatVector(m_Character != null ? m_Character.transform.position : transform.position)}");

                    manager.BroadcastTraversalChange(new NetworkTraversalBroadcast
                    {
                        NetworkId = NetworkId,
                        ActorNetworkId = actorNetworkId,
                        CorrelationId = correlationId,
                        Action = traverse is TraverseLink ? TraversalActionType.RunTraverseLink : TraversalActionType.EnterTraverseInteractive,
                        TraverseHash = StableHashUtility.GetStableHash(traverseId),
                        TraverseIdString = traverseId,
                        ActionIdHash = 0,
                        ActionIdString = string.Empty,
                        StateIdHash = 0,
                        StateIdString = string.Empty,
                        ArgsSelfNetworkId = argsSelfNetworkId,
                        ArgsTargetNetworkId = argsTargetNetworkId,
                        IsTraversing = true,
                        ServerTime = Time.time
                    });
                }

                return;
            }

            if (m_SuppressInterception)
            {
                LogTraversal($"client motion enter suppressed traverse='{FormatTraverse(traverse)}'");
                return;
            }

            if (!m_IsLocalClient || m_IsRemoteClient) return;

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
            if (m_IsServer)
            {
                NetworkTraversalSnapshot snapshot = CaptureFullSnapshot();
                LogTraversal(
                    $"server motion exit snapshot traverse='{snapshot.TraverseIdString}' " +
                    $"traversing={snapshot.IsTraversing} suppress={m_SuppressInterception} " +
                    $"position={FormatVector(m_Character != null ? m_Character.transform.position : transform.position)}");
                NetworkTraversalManager.Instance?.BroadcastFullSnapshot(snapshot);
                return;
            }

            if (m_SuppressInterception)
            {
                LogTraversal("client motion exit suppressed");
            }
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
                for (int i = 0; i < traverses.Length; i++)
                {
                    Traverse candidate = traverses[i];
                    if (candidate == null) continue;

                    string candidateId = BuildTraverseId(candidate);
                    if (!string.Equals(candidateId, traverseIdString, StringComparison.Ordinal)) continue;

                    traverse = candidate;
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
                    if (candidateHash != traverseHash) continue;

                    traverse = candidate;
                    return true;
                }
            }

            traverse = null;
            return false;
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

            return builder.ToString();
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
                $"[NetworkTraversalDebug][Controller] {name} netId={NetworkId} role={FormatRole()} {message}",
                this);
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
            return $"({value.x:F2},{value.y:F2},{value.z:F2})";
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
