using System;
using System.Collections.Generic;
using Arawn.GameCreator2.Networking.Security;
using GameCreator.Runtime.Characters;
using UnityEngine;

namespace Arawn.GameCreator2.Networking
{
    [AddComponentMenu("Game Creator/Network/Actions/Network Action Manager")]
    public sealed class NetworkActionManager : NetworkSingleton<NetworkActionManager>
    {
        public const int MaxActionIdCharacters = 96;
        public const int MaxPayloadStringCharacters = 512;

        private const string Module = "Actions";
        private const int MaxProcessedRequests = 2048;
        private const int MaxDeferredPersistentStates = 256;

        private readonly struct ActionKey : IEquatable<ActionKey>
        {
            public readonly uint TargetNetworkId;
            public readonly int EndpointHash;
            public readonly int ActionHash;
            public readonly string ActionId;

            public ActionKey(
                uint targetNetworkId,
                int endpointHash,
                int actionHash,
                string actionId)
            {
                TargetNetworkId = targetNetworkId;
                EndpointHash = endpointHash;
                ActionHash = actionHash;
                ActionId = actionId ?? string.Empty;
            }

            public bool Equals(ActionKey other) =>
                TargetNetworkId == other.TargetNetworkId &&
                EndpointHash == other.EndpointHash &&
                ActionHash == other.ActionHash &&
                string.Equals(ActionId, other.ActionId, StringComparison.Ordinal);

            public override bool Equals(object obj) => obj is ActionKey other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = (int)TargetNetworkId;
                    hash = (hash * 397) ^ EndpointHash;
                    hash = (hash * 397) ^ ActionHash;
                    hash = (hash * 397) ^ StringComparer.Ordinal.GetHashCode(ActionId);
                    return hash;
                }
            }
        }

        private readonly struct RequestKey : IEquatable<RequestKey>
        {
            public readonly uint SenderClientId;
            public readonly uint ActorNetworkId;
            public readonly uint CorrelationId;

            public RequestKey(uint senderClientId, uint actorNetworkId, uint correlationId)
            {
                SenderClientId = senderClientId;
                ActorNetworkId = actorNetworkId;
                CorrelationId = correlationId;
            }

            public bool Equals(RequestKey other) =>
                SenderClientId == other.SenderClientId &&
                ActorNetworkId == other.ActorNetworkId &&
                CorrelationId == other.CorrelationId;
            public override bool Equals(object obj) => obj is RequestKey other && Equals(other);
            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = (int)SenderClientId;
                    hash = (hash * 397) ^ (int)ActorNetworkId;
                    hash = (hash * 397) ^ (int)CorrelationId;
                    return hash;
                }
            }
        }

        private readonly struct CooldownKey : IEquatable<CooldownKey>
        {
            public readonly uint ActorNetworkId;
            public readonly ActionKey Action;

            public CooldownKey(uint actorNetworkId, ActionKey action)
            {
                ActorNetworkId = actorNetworkId;
                Action = action;
            }

            public bool Equals(CooldownKey other) =>
                ActorNetworkId == other.ActorNetworkId && Action.Equals(other.Action);
            public override bool Equals(object obj) => obj is CooldownKey other && Equals(other);
            public override int GetHashCode() => ((int)ActorNetworkId * 397) ^ Action.GetHashCode();
        }

        private sealed class PendingRequest
        {
            public NetworkActionRequest Request;
            public float Deadline;
            public Action<NetworkActionResponse> Callback;
        }

        private struct ProcessedRequest
        {
            public NetworkActionResponse Response;
        }

        [Header("Runtime")]
        [SerializeField] private float m_DefaultRequestTimeout = 5f;

        [Header("Debug")]
        [SerializeField] private bool m_LogNetworkMessages;

        private bool m_IsServer;
        private uint m_AuthorityEpoch;
        private readonly Dictionary<NetworkActionEndpointKey, NetworkActionEndpoint> m_Endpoints =
            new(64);
        private readonly Dictionary<ActionKey, NetworkActionBroadcast> m_PersistentStates = new(64);
        private readonly Dictionary<ActionKey, NetworkActionBroadcast> m_DeferredPersistentStates =
            new(32);
        private readonly Dictionary<RequestKey, ProcessedRequest> m_ProcessedRequests = new(256);
        private readonly Queue<RequestKey> m_ProcessedOrder = new(256);
        private readonly Dictionary<CooldownKey, float> m_Cooldowns = new(128);
        private readonly Dictionary<uint, ushort> m_RequestCounters = new(32);
        private readonly Dictionary<ulong, PendingRequest> m_PendingRequests = new(64);
        private readonly List<ulong> m_PendingTimeoutScratch = new(16);

        public Action<NetworkActionRequest> OnSendActionRequest;
        public Action<uint, NetworkActionResponse> OnSendActionResponse;
        public Action<NetworkActionBroadcast> OnBroadcastAction;
        public Action<uint, NetworkActionBroadcast> OnSendActionToClient;
        public Action<uint, NetworkActionSnapshot> OnSendSnapshotToClient;
        public Action<NetworkActionSnapshot> OnBroadcastSnapshot;

        public new static NetworkActionManager Instance
        {
            get
            {
                if (s_Instance == null)
                {
                    s_Instance = FindFirstObjectByType<NetworkActionManager>(
                        FindObjectsInactive.Include);
                }

                return s_Instance;
            }
        }

        public bool IsServer
        {
            get => m_IsServer;
            set
            {
                bool promoted = value && !m_IsServer;
                m_IsServer = value;
                SecurityIntegration.SetModuleServerContext(Module, value);
                SecurityIntegration.EnsureSecurityManagerInitialized(value, GetServerTime);
                if (!promoted) return;

                // A newly promoted Shared authority must never reuse the former authority's
                // request-dedupe/cooldown history. Replicated persistent state is retained and
                // rebased when SetAuthorityEpoch receives the transport migration epoch.
                m_ProcessedRequests.Clear();
                m_ProcessedOrder.Clear();
                m_Cooldowns.Clear();

                if (m_AuthorityEpoch == uint.MaxValue) m_AuthorityEpoch = 1;
                else m_AuthorityEpoch++;
                InitializeAllPersistentStates();
            }
        }

        public uint AuthorityEpoch => m_AuthorityEpoch;
        protected override DuplicatePolicy OnDuplicatePolicy => DuplicatePolicy.WarnOnly;

        protected override void OnSingletonAwake()
        {
            SecurityIntegration.SetModuleServerContext(Module, m_IsServer);
        }

        protected override void OnSingletonCleanup()
        {
            SecurityIntegration.SetModuleServerContext(Module, false);
            m_Endpoints.Clear();
            m_PersistentStates.Clear();
            m_DeferredPersistentStates.Clear();
            m_ProcessedRequests.Clear();
            m_ProcessedOrder.Clear();
            m_Cooldowns.Clear();
            m_RequestCounters.Clear();
            m_PendingRequests.Clear();
            ClearTransportDelegates();
        }

        private void OnEnable()
        {
            SecurityIntegration.SetModuleServerContext(Module, m_IsServer);
            SecurityIntegration.EnsureSecurityManagerInitialized(m_IsServer, GetServerTime);
        }

        private void OnDisable()
        {
            SecurityIntegration.SetModuleServerContext(Module, false);
        }

        private void Update()
        {
            if (m_PendingRequests.Count == 0) return;
            float now = Time.unscaledTime;
            m_PendingTimeoutScratch.Clear();
            foreach (KeyValuePair<ulong, PendingRequest> pair in m_PendingRequests)
            {
                if (now >= pair.Value.Deadline) m_PendingTimeoutScratch.Add(pair.Key);
            }

            for (int i = 0; i < m_PendingTimeoutScratch.Count; i++)
            {
                ulong key = m_PendingTimeoutScratch[i];
                if (!m_PendingRequests.Remove(key, out PendingRequest pending)) continue;
                NetworkActionResponse response = BuildRejectedResponse(
                    pending.Request, NetworkActionRejectReason.Timeout);
                DispatchResponseLocally(response, pending.Callback);
            }
        }

        public void SetAuthorityEpoch(uint authorityEpoch)
        {
            if (authorityEpoch == 0) return;
            m_AuthorityEpoch = authorityEpoch;
            if (!m_IsServer || m_PersistentStates.Count == 0) return;

            // Promotion may initialize authored state before the transport reports its current
            // migration epoch. Rebase those authority-owned entries so late-join snapshots and
            // subsequent deltas use one coherent epoch.
            var keys = new List<ActionKey>(m_PersistentStates.Keys);
            for (int i = 0; i < keys.Count; i++)
            {
                NetworkActionBroadcast state = m_PersistentStates[keys[i]];
                state.AuthorityEpoch = authorityEpoch;
                m_PersistentStates[keys[i]] = state;
            }
        }

        /// <summary>
        /// Clears all session-scoped state while retaining registered scene endpoints and
        /// transport delegate wiring. Adapters call this when their runner/session shuts down.
        /// </summary>
        public void ResetSessionState(
            NetworkActionRejectReason pendingReason = NetworkActionRejectReason.NotRunning)
        {
            if (pendingReason == NetworkActionRejectReason.None)
                pendingReason = NetworkActionRejectReason.NotRunning;

            if (m_PendingRequests.Count > 0)
            {
                var pending = new List<PendingRequest>(m_PendingRequests.Values);
                m_PendingRequests.Clear();
                for (int i = 0; i < pending.Count; i++)
                {
                    NetworkActionResponse response = BuildRejectedResponse(
                        pending[i].Request, pendingReason);
                    DispatchResponseLocally(response, pending[i].Callback);
                }
            }

            m_PersistentStates.Clear();
            m_DeferredPersistentStates.Clear();
            m_ProcessedRequests.Clear();
            m_ProcessedOrder.Clear();
            m_Cooldowns.Clear();
            m_RequestCounters.Clear();
            m_AuthorityEpoch = 0;
            m_IsServer = false;
            SecurityIntegration.SetModuleServerContext(Module, false);
        }

        public bool RegisterEndpoint(uint networkId, NetworkActionEndpoint endpoint)
        {
            if (networkId == 0 || endpoint == null || endpoint.EndpointHash == 0) return false;
            NetworkActionEndpointKey key = new(networkId, endpoint.EndpointHash);
            if (m_Endpoints.TryGetValue(key, out NetworkActionEndpoint existing))
            {
                if (existing == endpoint) return true;
                if (existing != null)
                {
                    Debug.LogWarning(
                        $"[NetworkActionManager] Endpoint {key} is already registered by " +
                        $"'{existing.name}'; endpoint '{endpoint.name}' was ignored.", endpoint);
                    return false;
                }
            }

            m_Endpoints[key] = endpoint;
            // Actor ownership is registered by the transport/Core character lifecycle. Action
            // endpoints may share that Network ID, so this module must not own or later erase
            // the global ownership record.
            bool appliedDeferred = false;
            if (m_IsServer)
            {
                InitializePersistentStates(endpoint);
                PublishEndpointSnapshot(endpoint);
            }
            else appliedDeferred = ApplyDeferredPersistentStates(endpoint);

            // Registration can follow a dynamic spawn, scene streaming, or a temporary
            // component disable. Re-apply the latest absolute state to this endpoint so its
            // presentation does not wait for a future mutation. A newly deferred snapshot was
            // already applied by ApplyDeferredPersistentStates and must not execute twice.
            if (!appliedDeferred) ApplyCurrentPersistentStates(endpoint);
            return true;
        }

        public void UnregisterEndpoint(
            uint networkId,
            NetworkActionEndpoint endpoint,
            bool terminalLifetime = false)
        {
            if (networkId == 0 || endpoint == null || endpoint.EndpointHash == 0) return;
            NetworkActionEndpointKey key = new(networkId, endpoint.EndpointHash);
            if (!m_Endpoints.TryGetValue(key, out var existing)) return;
            if (endpoint != null && existing != endpoint) return;
            m_Endpoints.Remove(key);
            if (terminalLifetime) PurgeEndpointGeneration(networkId, existing.EndpointHash);
        }

        /// <summary>
        /// Removes an endpoint by its transport generation key. Transport adapters use this
        /// overload after a confirmed despawn or Unity destruction, where the retained component
        /// reference can already compare equal to null and cannot identify its old generation
        /// through Unity's overloaded object equality.
        /// </summary>
        public void UnregisterEndpoint(
            NetworkActionEndpointKey key,
            bool terminalLifetime)
        {
            if (!key.IsValid) return;
            m_Endpoints.Remove(key);
            if (terminalLifetime) PurgeEndpointGeneration(key.NetworkId, key.EndpointHash);
        }

        public bool IsRegistered(uint networkId, NetworkActionEndpoint endpoint) =>
            networkId != 0 && endpoint != null &&
            m_Endpoints.TryGetValue(
                new NetworkActionEndpointKey(networkId, endpoint.EndpointHash),
                out var existing) && existing == endpoint;

        public bool TryGetEndpoint(uint networkId, out NetworkActionEndpoint endpoint)
        {
            endpoint = null;
            foreach (KeyValuePair<NetworkActionEndpointKey, NetworkActionEndpoint> pair in
                     m_Endpoints)
            {
                if (pair.Key.NetworkId != networkId || pair.Value == null) continue;
                if (endpoint != null) return false; // Ambiguous: caller must provide endpoint hash.
                endpoint = pair.Value;
            }
            if (endpoint != null) return true;
            return false;
        }

        public bool TryGetEndpoint(
            uint networkId,
            int endpointHash,
            out NetworkActionEndpoint endpoint)
        {
            if (m_Endpoints.TryGetValue(
                    new NetworkActionEndpointKey(networkId, endpointHash), out endpoint) &&
                endpoint != null) return true;
            endpoint = null;
            return false;
        }

        public bool RequestAction(
            uint actorNetworkId,
            NetworkActionEndpoint endpoint,
            NetworkActionDefinition definition,
            NetworkActionPayload payload,
            uint expectedRevision = 0,
            Action<NetworkActionResponse> callback = null,
            float timeout = -1f)
        {
            if (actorNetworkId == 0 || endpoint == null || endpoint.NetworkId == 0 ||
                endpoint.EndpointHash == 0 ||
                definition == null)
            {
                return false;
            }

            if (!definition.TryValidatePayload(payload, out _)) return false;

            ushort requestId = NextRequestId(actorNetworkId);
            uint correlationId = NetworkCorrelation.Compose(actorNetworkId, requestId);
            var request = new NetworkActionRequest
            {
                RequestId = requestId,
                ActorNetworkId = actorNetworkId,
                CorrelationId = correlationId,
                TargetNetworkId = endpoint.NetworkId,
                EndpointHash = endpoint.EndpointHash,
                ActionHash = definition.ActionHash,
                ActionId = definition.ActionId,
                SchemaVersion = definition.SchemaVersion,
                Payload = definition.NormalizePayload(payload),
                ExpectedRevision = expectedRevision,
                ClientTime = GetServerTime()
            };

            if (callback != null)
            {
                float duration = timeout >= 0f ? timeout : m_DefaultRequestTimeout;
                m_PendingRequests[PendingKey(actorNetworkId, correlationId)] = new PendingRequest
                {
                    Request = request,
                    Deadline = Time.unscaledTime + Mathf.Max(0.1f, duration),
                    Callback = callback
                };
            }

            // Logical authority executes its own request through the exact same validation and
            // commit path, but with an authority-local sender. Routing it through a Host/Shared
            // loopback would misclassify AuthorityOnly actions as remote client requests.
            if (m_IsServer)
            {
                ReceiveActionRequest(request, NetworkTransportBridge.InvalidClientId);
                return true;
            }

            if (OnSendActionRequest != null)
            {
                OnSendActionRequest.Invoke(request);
                return true;
            }

            m_PendingRequests.Remove(PendingKey(actorNetworkId, correlationId));
            NetworkActionResponse rejected = BuildRejectedResponse(
                request, NetworkActionRejectReason.TransportUnavailable);
            DispatchResponseLocally(rejected, callback);
            return false;
        }

        public void ReceiveActionRequest(NetworkActionRequest request, uint senderClientId)
        {
            RequestKey requestKey = new(senderClientId, request.ActorNetworkId, request.CorrelationId);
            if (m_ProcessedRequests.TryGetValue(requestKey, out ProcessedRequest processed))
            {
                SendResponse(senderClientId, processed.Response);
                return;
            }

            NetworkActionResponse response = BuildRejectedResponse(
                request, NetworkActionRejectReason.None);
            if (!m_IsServer)
            {
                Reject(requestKey, senderClientId, ref response,
                    NetworkActionRejectReason.NotAuthority);
                return;
            }

            if (!ValidateRequestEnvelope(request))
            {
                Reject(requestKey, senderClientId, ref response,
                    NetworkActionRejectReason.InvalidPayload);
                return;
            }

            bool authorityLocal = !NetworkTransportBridge.IsValidClientId(senderClientId);
            if (!authorityLocal && !SecurityIntegration.ValidateModuleRequest(
                    senderClientId,
                    NetworkRequestContext.Create(request.ActorNetworkId, request.CorrelationId),
                    Module,
                    nameof(NetworkActionRequest)))
            {
                Reject(requestKey, senderClientId, ref response,
                    NetworkActionRejectReason.SecurityViolation);
                return;
            }

            if (!TryGetEndpoint(
                    request.TargetNetworkId,
                    request.EndpointHash,
                    out NetworkActionEndpoint endpoint))
            {
                Reject(requestKey, senderClientId, ref response,
                    NetworkActionRejectReason.TargetNotFound);
                return;
            }

            if (!endpoint.TryGetBinding(
                    request.ActionHash,
                    request.ActionId,
                    request.SchemaVersion,
                    out NetworkActionBinding binding,
                    out NetworkActionRejectReason bindingReject))
            {
                Reject(requestKey, senderClientId, ref response, bindingReject);
                return;
            }

            NetworkActionDefinition definition = binding.Definition;
            if (!definition.TryValidatePayload(request.Payload, out NetworkActionRejectReason payloadReject))
            {
                Reject(requestKey, senderClientId, ref response, payloadReject);
                return;
            }

            // Reconstruct the discriminated union before any authored Condition or custom
            // authority handler observes it. Inactive wire fields are never trusted inputs.
            request.Payload = definition.NormalizePayload(request.Payload);

            if (!ValidateAuthorityPolicy(
                    authorityLocal, senderClientId, endpoint, definition, out var authorityReject))
            {
                Reject(requestKey, senderClientId, ref response, authorityReject);
                return;
            }

            GameObject actorObject = ResolveActorObject(request.ActorNetworkId);
            if (actorObject == null)
            {
                Reject(requestKey, senderClientId, ref response,
                    NetworkActionRejectReason.ActorNotFound);
                return;
            }

            if (!ValidateRangeAndLineOfSight(actorObject, endpoint, definition, out var spatialReject))
            {
                Reject(requestKey, senderClientId, ref response, spatialReject);
                return;
            }

            ActionKey actionKey = new(
                request.TargetNetworkId,
                request.EndpointHash,
                request.ActionHash,
                request.ActionId);
            uint currentRevision = m_PersistentStates.TryGetValue(
                actionKey, out NetworkActionBroadcast currentState)
                ? currentState.Revision
                : 0u;
            if (request.ExpectedRevision != 0 && request.ExpectedRevision != currentRevision)
            {
                response.Revision = currentRevision;
                response.CanonicalPayload = currentState.Payload;
                Reject(requestKey, senderClientId, ref response,
                    NetworkActionRejectReason.RevisionConflict);
                return;
            }

            float serverTime = GetServerTime();
            CooldownKey cooldownKey = new(request.ActorNetworkId, actionKey);
            if (definition.Cooldown > 0f &&
                m_Cooldowns.TryGetValue(cooldownKey, out float nextAllowed) &&
                serverTime < nextAllowed)
            {
                Reject(requestKey, senderClientId, ref response,
                    NetworkActionRejectReason.Cooldown);
                return;
            }

            NetworkActionBroadcast broadcast = new()
            {
                RequestId = request.RequestId,
                ActorNetworkId = request.ActorNetworkId,
                CorrelationId = request.CorrelationId,
                TargetNetworkId = request.TargetNetworkId,
                EndpointHash = request.EndpointHash,
                ActionHash = request.ActionHash,
                ActionId = request.ActionId,
                SchemaVersion = request.SchemaVersion,
                EffectKind = definition.EffectKind,
                RecipientPolicy = definition.RecipientPolicy,
                Reliable = definition.Reliable,
                Payload = request.Payload,
                Revision = definition.EffectKind == NetworkActionEffectKind.PersistentState
                    ? NextRevision(currentRevision)
                    : 0u,
                AuthorityEpoch = m_AuthorityEpoch,
                ServerTime = serverTime,
                IsSnapshot = false
            };
            NetworkActionResponse approvedPreview = response;
            approvedPreview.Authorized = true;
            approvedPreview.CanonicalPayload = broadcast.Payload;
            approvedPreview.Revision = broadcast.Revision;
            approvedPreview.ServerTime = serverTime;
            NetworkActionExecutionContext validationContext = new(
                NetworkActionContextPhase.AuthorityCommit,
                in request,
                in approvedPreview,
                in broadcast,
                actorObject,
                endpoint.gameObject);
            if (!endpoint.CheckAuthority(binding, in validationContext))
            {
                Reject(requestKey, senderClientId, ref response,
                    NetworkActionRejectReason.ConditionsFailed);
                return;
            }

            NetworkActionPayload canonicalPayload = request.Payload;
            if (!endpoint.TryValidateWithHandlers(
                    definition, in request, ref canonicalPayload, out var handlerReject) ||
                !definition.TryValidatePayload(canonicalPayload, out payloadReject))
            {
                Reject(requestKey, senderClientId, ref response,
                    handlerReject != NetworkActionRejectReason.None
                        ? handlerReject
                        : payloadReject);
                return;
            }

            broadcast.Payload = definition.NormalizePayload(canonicalPayload);
            if (definition.EffectKind == NetworkActionEffectKind.PersistentState)
                m_PersistentStates[actionKey] = broadcast;
            if (definition.Cooldown > 0f)
                m_Cooldowns[cooldownKey] = serverTime + definition.Cooldown;

            response.Authorized = true;
            response.RejectReason = NetworkActionRejectReason.None;
            response.CanonicalPayload = broadcast.Payload;
            response.Revision = broadcast.Revision;
            response.ServerTime = serverTime;
            CacheProcessed(requestKey, response);
            SendResponse(senderClientId, response);

            endpoint.NotifyAuthorityHandlers(definition, in broadcast);
            NetworkActionExecutionContext authorityContext =
                NetworkActionExecutionContext.FromBroadcast(
                    in broadcast, actorObject, endpoint.gameObject, true);
            _ = endpoint.ExecuteAuthorityAsync(binding, authorityContext);

            NetworkActionExecutionContext appliedContext =
                NetworkActionExecutionContext.FromBroadcast(
                    in broadcast, actorObject, endpoint.gameObject);
            _ = ApplyAndRaiseAsync(endpoint, binding, appliedContext);
            RouteBroadcast(senderClientId, endpoint, definition, broadcast);
            Log($"approved action={request.ActionId} actor={request.ActorNetworkId} " +
                $"target={request.TargetNetworkId} revision={broadcast.Revision}");
        }

        /// <summary>
        /// Completes a locally-created request when a transport cannot enqueue it. Adapters use
        /// this instead of leaving GC2 Instructions waiting for an avoidable timeout.
        /// </summary>
        public void RejectLocalActionRequest(
            in NetworkActionRequest request,
            NetworkActionRejectReason reason)
        {
            if (reason == NetworkActionRejectReason.None)
                reason = NetworkActionRejectReason.TransportUnavailable;
            NetworkActionResponse response = BuildRejectedResponse(request, reason);
            response.ServerTime = GetServerTime();
            ReceiveActionResponse(response);
        }

        public void ReceiveActionResponse(NetworkActionResponse response)
        {
            ulong key = PendingKey(response.ActorNetworkId, response.CorrelationId);
            Action<NetworkActionResponse> callback = null;
            if (m_PendingRequests.Remove(key, out PendingRequest pending))
                callback = pending.Callback;
            DispatchResponseLocally(response, callback);
        }

        public void ReceiveActionBroadcast(NetworkActionBroadcast broadcast)
        {
            if (m_IsServer || !ValidateBroadcastEnvelope(broadcast)) return;
            ApplyBroadcast(broadcast);
        }

        public void ReceiveActionSnapshot(NetworkActionSnapshot snapshot)
        {
            if (m_IsServer) return;
            if (snapshot.AuthorityEpoch != 0)
                m_AuthorityEpoch = snapshot.AuthorityEpoch;
            NetworkActionBroadcast[] entries = snapshot.Entries ??
                                               Array.Empty<NetworkActionBroadcast>();
            for (int i = 0; i < entries.Length; i++)
            {
                NetworkActionBroadcast entry = entries[i];
                entry.IsSnapshot = true;
                if (ValidateBroadcastEnvelope(entry)) ApplyBroadcast(entry);
            }
        }

        public NetworkActionSnapshot BuildSnapshot(float serverTime)
        {
            var entries = new List<NetworkActionBroadcast>(m_PersistentStates.Count);
            foreach (KeyValuePair<ActionKey, NetworkActionBroadcast> pair in m_PersistentStates)
            {
                NetworkActionBroadcast entry = pair.Value;
                if (!TryGetEndpoint(
                        entry.TargetNetworkId,
                        entry.EndpointHash,
                        out NetworkActionEndpoint endpoint) ||
                    !endpoint.TryGetBinding(
                        entry.ActionHash,
                        entry.ActionId,
                        entry.SchemaVersion,
                        out NetworkActionBinding binding,
                        out _) ||
                    binding.Definition == null ||
                    !binding.Definition.IncludeInLateJoinSnapshot)
                {
                    continue;
                }

                entry.IsSnapshot = true;
                entry.ServerTime = serverTime;
                entry.AuthorityEpoch = m_AuthorityEpoch;
                entries.Add(entry);
            }

            return new NetworkActionSnapshot
            {
                Entries = entries.ToArray(),
                AuthorityEpoch = m_AuthorityEpoch,
                ServerTime = serverTime
            };
        }

        public void SendInitialState(uint clientId)
        {
            if (!m_IsServer) return;
            NetworkActionSnapshot snapshot = BuildSnapshot(GetServerTime());
            if (OnSendSnapshotToClient != null)
                OnSendSnapshotToClient.Invoke(clientId, snapshot);
            else
                OnBroadcastSnapshot?.Invoke(snapshot);
        }

        public bool BroadcastFullSnapshot()
        {
            if (!m_IsServer || OnBroadcastSnapshot == null) return false;
            OnBroadcastSnapshot.Invoke(BuildSnapshot(GetServerTime()));
            return true;
        }

        public bool TryGetPersistentState(
            NetworkActionEndpoint endpoint,
            NetworkActionDefinition definition,
            out NetworkActionPayload payload,
            out uint revision)
        {
            payload = default;
            revision = 0;
            if (endpoint == null || endpoint.NetworkId == 0 || endpoint.EndpointHash == 0 ||
                definition == null) return false;
            ActionKey key = new(
                endpoint.NetworkId,
                endpoint.EndpointHash,
                definition.ActionHash,
                definition.ActionId);
            if (!m_PersistentStates.TryGetValue(key, out NetworkActionBroadcast state)) return false;
            payload = state.Payload;
            revision = state.Revision;
            return true;
        }

        private void ApplyBroadcast(NetworkActionBroadcast broadcast)
        {
            if (!TryGetEndpoint(
                    broadcast.TargetNetworkId,
                    broadcast.EndpointHash,
                    out NetworkActionEndpoint endpoint))
            {
                CacheDeferredPersistentState(broadcast);
                return;
            }

            if (!endpoint.TryGetBinding(
                    broadcast.ActionHash,
                    broadcast.ActionId,
                    broadcast.SchemaVersion,
                    out NetworkActionBinding binding,
                    out _))
            {
                return;
            }

            // The sender is authority, but wire data must still satisfy the locally authored
            // definition before it can enter persistent state or a GC2 execution context.
            if (binding.Definition == null ||
                broadcast.EffectKind != binding.Definition.EffectKind ||
                broadcast.RecipientPolicy != binding.Definition.RecipientPolicy ||
                broadcast.Reliable != binding.Definition.Reliable ||
                (broadcast.EffectKind == NetworkActionEffectKind.PersistentState &&
                 !broadcast.Reliable) ||
                !binding.Definition.TryValidatePayload(
                    broadcast.Payload, out NetworkActionRejectReason _)) return;

            ActionKey key = new(
                broadcast.TargetNetworkId,
                broadcast.EndpointHash,
                broadcast.ActionHash,
                broadcast.ActionId);
            if (broadcast.EffectKind == NetworkActionEffectKind.PersistentState)
            {
                if (m_PersistentStates.TryGetValue(key, out NetworkActionBroadcast current) &&
                    !IsNewerState(broadcast, current)) return;
                m_PersistentStates[key] = broadcast;
            }

            GameObject actor = ResolveActorObject(broadcast.ActorNetworkId);
            NetworkActionExecutionContext context =
                NetworkActionExecutionContext.FromBroadcast(
                    in broadcast, actor, endpoint.gameObject);
            _ = ApplyAndRaiseAsync(endpoint, binding, context);
        }

        private bool ValidateAuthorityPolicy(
            bool authorityLocal,
            uint senderClientId,
            NetworkActionEndpoint endpoint,
            NetworkActionDefinition definition,
            out NetworkActionRejectReason rejectReason)
        {
            rejectReason = NetworkActionRejectReason.None;
            if (authorityLocal) return true;

            if (definition.AuthorityPolicy == NetworkActionAuthorityPolicy.AuthorityOnly)
            {
                rejectReason = NetworkActionRejectReason.NotAuthorized;
                return false;
            }

            if (definition.AuthorityPolicy == NetworkActionAuthorityPolicy.OwnerRequest)
            {
                // ValidateModuleRequest authenticates the sender and verifies ownership of
                // request.ActorNetworkId. The endpoint itself may intentionally be an unowned
                // scene object (door, lever, chest), so requiring target ownership here would
                // make the safe default unusable for world interactions.
                return true;
            }

            return true;
        }

        private static bool ValidateRangeAndLineOfSight(
            GameObject actor,
            NetworkActionEndpoint endpoint,
            NetworkActionDefinition definition,
            out NetworkActionRejectReason rejectReason)
        {
            rejectReason = NetworkActionRejectReason.None;
            Vector3 actorPosition = actor.transform.position;
            Vector3 targetPosition = endpoint.transform.position;
            if (definition.MaximumDistance > 0f &&
                Vector3.Distance(actorPosition, targetPosition) > definition.MaximumDistance)
            {
                rejectReason = NetworkActionRejectReason.OutOfRange;
                return false;
            }

            if (!definition.RequireLineOfSight) return true;
            Vector3 from = actorPosition + Vector3.up;
            Vector3 to = targetPosition + Vector3.up;
            Vector3 delta = to - from;
            float distance = delta.magnitude;
            if (distance <= 0.0001f) return true;

            RaycastHit[] hits = Physics.RaycastAll(
                from,
                delta / distance,
                distance,
                definition.LineOfSightMask,
                QueryTriggerInteraction.Ignore);
            if (hits == null || hits.Length == 0) return true;
            Array.Sort(hits, static (left, right) => left.distance.CompareTo(right.distance));
            for (int i = 0; i < hits.Length; i++)
            {
                Transform hitTransform = hits[i].transform;
                if (hitTransform == null || hitTransform.IsChildOf(endpoint.transform) ||
                    hitTransform.IsChildOf(actor.transform)) continue;
                rejectReason = NetworkActionRejectReason.LineOfSightBlocked;
                return false;
            }
            return true;
        }

        private void RouteBroadcast(
            uint requesterClientId,
            NetworkActionEndpoint endpoint,
            NetworkActionDefinition definition,
            NetworkActionBroadcast broadcast)
        {
            switch (definition.RecipientPolicy)
            {
                case NetworkActionRecipientPolicy.Requester:
                    if (NetworkTransportBridge.IsValidClientId(requesterClientId))
                        OnSendActionToClient?.Invoke(requesterClientId, broadcast);
                    break;
                case NetworkActionRecipientPolicy.TargetOwner:
                    if (endpoint.TryGetOwnerClientId(out uint ownerClientId))
                        OnSendActionToClient?.Invoke(ownerClientId, broadcast);
                    break;
                default:
                    OnBroadcastAction?.Invoke(broadcast);
                    break;
            }
        }

        private void SendResponse(uint clientId, NetworkActionResponse response)
        {
            if (NetworkTransportBridge.IsValidClientId(clientId) && OnSendActionResponse != null)
            {
                OnSendActionResponse.Invoke(clientId, response);
                return;
            }

            // Authority-local requests can still have a pending callback. Route through the
            // normal receive path so the pending entry is removed exactly once.
            ReceiveActionResponse(response);
        }

        private void DispatchResponseLocally(
            NetworkActionResponse response,
            Action<NetworkActionResponse> callback)
        {
            GameObject actor = ResolveActorObject(response.ActorNetworkId);
            GameObject target = TryGetEndpoint(
                                    response.TargetNetworkId,
                                    response.EndpointHash,
                                    out var endpoint)
                ? endpoint.gameObject
                : null;
            NetworkActionExecutionContext context =
                NetworkActionExecutionContext.FromResponse(in response, actor, target);
            if (callback != null)
            {
                try
                {
                    callback.Invoke(response);
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception, target != null ? target : this);
                }
            }
            NetworkActionEvents.RaiseResponse(in context);
        }

        private void Reject(
            RequestKey requestKey,
            uint senderClientId,
            ref NetworkActionResponse response,
            NetworkActionRejectReason reason)
        {
            response.Authorized = false;
            response.RejectReason = reason;
            response.ServerTime = GetServerTime();
            CacheProcessed(requestKey, response);
            SendResponse(senderClientId, response);
            Log($"rejected action={response.ActionId} actor={response.ActorNetworkId} " +
                $"target={response.TargetNetworkId} reason={reason}");
        }

        private void CacheProcessed(RequestKey requestKey, NetworkActionResponse response)
        {
            m_ProcessedRequests[requestKey] = new ProcessedRequest { Response = response };
            m_ProcessedOrder.Enqueue(requestKey);
            while (m_ProcessedOrder.Count > MaxProcessedRequests)
            {
                RequestKey oldest = m_ProcessedOrder.Dequeue();
                m_ProcessedRequests.Remove(oldest);
            }
        }

        private void InitializeAllPersistentStates()
        {
            foreach (NetworkActionEndpoint endpoint in m_Endpoints.Values)
                if (endpoint != null) InitializePersistentStates(endpoint);
        }

        private void InitializePersistentStates(NetworkActionEndpoint endpoint)
        {
            if (endpoint == null || endpoint.NetworkId == 0) return;
            IReadOnlyList<NetworkActionBinding> actions = endpoint.Actions;
            for (int i = 0; i < actions.Count; i++)
            {
                NetworkActionDefinition definition = actions[i]?.Definition;
                if (definition == null ||
                    definition.EffectKind != NetworkActionEffectKind.PersistentState) continue;
                ActionKey key = new(
                    endpoint.NetworkId,
                    endpoint.EndpointHash,
                    definition.ActionHash,
                    definition.ActionId);
                if (m_PersistentStates.ContainsKey(key)) continue;
                m_PersistentStates[key] = new NetworkActionBroadcast
                {
                    TargetNetworkId = endpoint.NetworkId,
                    EndpointHash = endpoint.EndpointHash,
                    ActionHash = definition.ActionHash,
                    ActionId = definition.ActionId,
                    SchemaVersion = definition.SchemaVersion,
                    EffectKind = NetworkActionEffectKind.PersistentState,
                    RecipientPolicy = definition.RecipientPolicy,
                    Reliable = true,
                    Payload = definition.InitialPayload,
                    Revision = 1,
                    AuthorityEpoch = m_AuthorityEpoch,
                    ServerTime = GetServerTime(),
                    IsSnapshot = true
                };
            }
        }

        private void CacheDeferredPersistentState(in NetworkActionBroadcast broadcast)
        {
            if (broadcast.EffectKind != NetworkActionEffectKind.PersistentState ||
                broadcast.EndpointHash == 0 || broadcast.Revision == 0) return;

            ActionKey key = new(
                broadcast.TargetNetworkId,
                broadcast.EndpointHash,
                broadcast.ActionHash,
                broadcast.ActionId);
            if (m_DeferredPersistentStates.TryGetValue(key, out NetworkActionBroadcast current) &&
                !IsNewerState(broadcast, current)) return;

            if (m_DeferredPersistentStates.Count >= MaxDeferredPersistentStates &&
                !m_DeferredPersistentStates.ContainsKey(key))
            {
                // Deferred state is only a short admission-order buffer. Drop the oldest
                // authority payload rather than allowing an unbounded pre-registration queue.
                ActionKey oldestKey = default;
                float oldestTime = float.PositiveInfinity;
                foreach (KeyValuePair<ActionKey, NetworkActionBroadcast> pair in
                         m_DeferredPersistentStates)
                {
                    if (pair.Value.ServerTime >= oldestTime) continue;
                    oldestTime = pair.Value.ServerTime;
                    oldestKey = pair.Key;
                }
                m_DeferredPersistentStates.Remove(oldestKey);
            }

            m_DeferredPersistentStates[key] = broadcast;
        }

        private bool ApplyDeferredPersistentStates(NetworkActionEndpoint endpoint)
        {
            if (endpoint == null || endpoint.NetworkId == 0 || endpoint.EndpointHash == 0 ||
                m_DeferredPersistentStates.Count == 0) return false;

            var matches = new List<NetworkActionBroadcast>();
            var keys = new List<ActionKey>();
            foreach (KeyValuePair<ActionKey, NetworkActionBroadcast> pair in
                     m_DeferredPersistentStates)
            {
                if (pair.Key.TargetNetworkId != endpoint.NetworkId ||
                    pair.Key.EndpointHash != endpoint.EndpointHash) continue;
                keys.Add(pair.Key);
                matches.Add(pair.Value);
            }

            for (int i = 0; i < keys.Count; i++)
                m_DeferredPersistentStates.Remove(keys[i]);
            for (int i = 0; i < matches.Count; i++) ApplyBroadcast(matches[i]);
            return matches.Count > 0;
        }

        private void ApplyCurrentPersistentStates(NetworkActionEndpoint endpoint)
        {
            if (endpoint == null || endpoint.NetworkId == 0 || endpoint.EndpointHash == 0) return;
            var states = new List<NetworkActionBroadcast>();
            foreach (KeyValuePair<ActionKey, NetworkActionBroadcast> pair in m_PersistentStates)
            {
                if (pair.Key.TargetNetworkId != endpoint.NetworkId ||
                    pair.Key.EndpointHash != endpoint.EndpointHash) continue;
                states.Add(pair.Value);
            }

            for (int i = 0; i < states.Count; i++)
            {
                NetworkActionBroadcast state = states[i];
                state.IsSnapshot = true;
                if (!endpoint.TryGetBinding(
                        state.ActionHash,
                        state.ActionId,
                        state.SchemaVersion,
                        out NetworkActionBinding binding,
                        out _) || binding.Definition == null) continue;
                GameObject actor = ResolveActorObject(state.ActorNetworkId);
                NetworkActionExecutionContext context =
                    NetworkActionExecutionContext.FromBroadcast(
                        in state, actor, endpoint.gameObject);
                _ = ApplyAndRaiseAsync(endpoint, binding, context);
            }
        }

        private static async System.Threading.Tasks.Task ApplyAndRaiseAsync(
            NetworkActionEndpoint endpoint,
            NetworkActionBinding binding,
            NetworkActionExecutionContext context)
        {
            if (endpoint == null || binding == null) return;
            await endpoint.ExecuteAppliedAsync(binding, context);
            if (endpoint == null) return;
            NetworkActionEvents.RaiseApplied(in context);
        }

        private void PublishEndpointSnapshot(NetworkActionEndpoint endpoint)
        {
            if (!m_IsServer || endpoint == null || OnBroadcastSnapshot == null) return;
            float serverTime = GetServerTime();
            var entries = new List<NetworkActionBroadcast>();
            foreach (KeyValuePair<ActionKey, NetworkActionBroadcast> pair in m_PersistentStates)
            {
                ActionKey key = pair.Key;
                if (key.TargetNetworkId != endpoint.NetworkId ||
                    key.EndpointHash != endpoint.EndpointHash) continue;
                NetworkActionBroadcast entry = pair.Value;
                if (!endpoint.TryGetBinding(
                        entry.ActionHash,
                        entry.ActionId,
                        entry.SchemaVersion,
                        out NetworkActionBinding binding,
                        out _) || binding.Definition == null ||
                    !binding.Definition.IncludeInLateJoinSnapshot) continue;
                entry.IsSnapshot = true;
                entry.AuthorityEpoch = m_AuthorityEpoch;
                entry.ServerTime = serverTime;
                entries.Add(entry);
            }

            if (entries.Count == 0) return;
            OnBroadcastSnapshot.Invoke(new NetworkActionSnapshot
            {
                Entries = entries.ToArray(),
                AuthorityEpoch = m_AuthorityEpoch,
                ServerTime = serverTime
            });
        }

        private void PurgeEndpointGeneration(uint networkId, int endpointHash)
        {
            if (networkId == 0 || endpointHash == 0) return;

            var stateKeys = new List<ActionKey>();
            foreach (ActionKey key in m_PersistentStates.Keys)
                if (key.TargetNetworkId == networkId && key.EndpointHash == endpointHash)
                    stateKeys.Add(key);
            for (int i = 0; i < stateKeys.Count; i++) m_PersistentStates.Remove(stateKeys[i]);

            stateKeys.Clear();
            foreach (ActionKey key in m_DeferredPersistentStates.Keys)
                if (key.TargetNetworkId == networkId && key.EndpointHash == endpointHash)
                    stateKeys.Add(key);
            for (int i = 0; i < stateKeys.Count; i++)
                m_DeferredPersistentStates.Remove(stateKeys[i]);

            var cooldownKeys = new List<CooldownKey>();
            foreach (CooldownKey key in m_Cooldowns.Keys)
                if (key.Action.TargetNetworkId == networkId &&
                    key.Action.EndpointHash == endpointHash) cooldownKeys.Add(key);
            for (int i = 0; i < cooldownKeys.Count; i++) m_Cooldowns.Remove(cooldownKeys[i]);

            var pendingKeys = new List<ulong>();
            foreach (KeyValuePair<ulong, PendingRequest> pair in m_PendingRequests)
                if (pair.Value.Request.TargetNetworkId == networkId &&
                    pair.Value.Request.EndpointHash == endpointHash) pendingKeys.Add(pair.Key);
            for (int i = 0; i < pendingKeys.Count; i++)
            {
                if (!m_PendingRequests.Remove(pendingKeys[i], out PendingRequest pending)) continue;
                NetworkActionResponse response = BuildRejectedResponse(
                    pending.Request, NetworkActionRejectReason.TargetNotFound);
                DispatchResponseLocally(response, pending.Callback);
            }

            var processedKeys = new List<RequestKey>();
            foreach (KeyValuePair<RequestKey, ProcessedRequest> pair in m_ProcessedRequests)
                if (pair.Value.Response.TargetNetworkId == networkId &&
                    pair.Value.Response.EndpointHash == endpointHash) processedKeys.Add(pair.Key);
            for (int i = 0; i < processedKeys.Count; i++)
                m_ProcessedRequests.Remove(processedKeys[i]);
        }

        private bool ValidateRequestEnvelope(in NetworkActionRequest request)
        {
            return request.RequestId != 0 && request.ActorNetworkId != 0 &&
                   request.CorrelationId != 0 && request.TargetNetworkId != 0 &&
                   request.EndpointHash != 0 &&
                   request.ActionHash != 0 &&
                   !string.IsNullOrWhiteSpace(request.ActionId) &&
                   request.ActionId.Length <= MaxActionIdCharacters &&
                   request.SchemaVersion != 0 &&
                   IsFinite(request.ClientTime) &&
                   NetworkCorrelation.MatchesActor(
                       request.CorrelationId, request.ActorNetworkId);
        }

        private static bool ValidateBroadcastEnvelope(in NetworkActionBroadcast broadcast)
        {
            if (broadcast.TargetNetworkId == 0 || broadcast.EndpointHash == 0 ||
                broadcast.ActionHash == 0 ||
                string.IsNullOrWhiteSpace(broadcast.ActionId) ||
                broadcast.ActionId.Length > MaxActionIdCharacters ||
                broadcast.SchemaVersion == 0 || !IsFinite(broadcast.ServerTime)) return false;
            if (broadcast.EffectKind == NetworkActionEffectKind.PersistentState &&
                broadcast.Revision == 0) return false;
            return true;
        }

        private static bool IsNewerState(
            in NetworkActionBroadcast candidate,
            in NetworkActionBroadcast current)
        {
            if (candidate.AuthorityEpoch != current.AuthorityEpoch)
                return candidate.AuthorityEpoch > current.AuthorityEpoch;
            return candidate.Revision > current.Revision;
        }

        private static uint NextRevision(uint current) =>
            current == uint.MaxValue ? 1u : current + 1u;

        private ushort NextRequestId(uint actorNetworkId)
        {
            m_RequestCounters.TryGetValue(actorNetworkId, out ushort counter);
            counter++;
            if (counter == 0) counter = 1;
            m_RequestCounters[actorNetworkId] = counter;
            return counter;
        }

        private GameObject ResolveActorObject(uint actorNetworkId)
        {
            if (actorNetworkId == 0) return null;
            NetworkTransportBridge bridge = NetworkTransportBridge.Active;
            Character character = bridge != null ? bridge.ResolveCharacter(actorNetworkId) : null;
            if (character != null) return character.gameObject;
            foreach (KeyValuePair<NetworkActionEndpointKey, NetworkActionEndpoint> pair in
                     m_Endpoints)
                if (pair.Key.NetworkId == actorNetworkId && pair.Value != null)
                    return pair.Value.gameObject;
            return null;
        }

        private float GetServerTime()
        {
            NetworkTransportBridge bridge = NetworkTransportBridge.Active;
            return bridge != null && bridge.IsRunning ? bridge.ServerTime : Time.time;
        }

        private static NetworkActionResponse BuildRejectedResponse(
            in NetworkActionRequest request,
            NetworkActionRejectReason reason)
        {
            return new NetworkActionResponse
            {
                RequestId = request.RequestId,
                ActorNetworkId = request.ActorNetworkId,
                CorrelationId = request.CorrelationId,
                TargetNetworkId = request.TargetNetworkId,
                EndpointHash = request.EndpointHash,
                ActionHash = request.ActionHash,
                ActionId = request.ActionId,
                Authorized = false,
                RejectReason = reason,
                CanonicalPayload = request.Payload
            };
        }

        private static ulong PendingKey(uint actorNetworkId, uint correlationId) =>
            ((ulong)actorNetworkId << 32) | correlationId;

        private static bool IsFinite(float value) =>
            !float.IsNaN(value) && !float.IsInfinity(value);

        private void ClearTransportDelegates()
        {
            OnSendActionRequest = null;
            OnSendActionResponse = null;
            OnBroadcastAction = null;
            OnSendActionToClient = null;
            OnSendSnapshotToClient = null;
            OnBroadcastSnapshot = null;
        }

        private void Log(string message)
        {
            if (m_LogNetworkMessages) Debug.Log($"[NetworkActionManager] {message}", this);
        }
    }
}
