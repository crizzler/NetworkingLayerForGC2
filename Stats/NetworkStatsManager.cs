#if GC2_STATS
using System;
using System.Collections.Generic;
using UnityEngine;
using GameCreator.Runtime.Common;
using Arawn.GameCreator2.Networking;
using Arawn.GameCreator2.Networking.Security;

namespace Arawn.GameCreator2.Networking.Stats
{
    /// <summary>
    /// Global manager for stats network communication.
    /// Transport-agnostic - wire up delegates to your networking solution.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Purpose:</b>
    /// Provides centralized routing of stat-related network messages.
    /// Works with any transport layer (NGO, FishNet, Mirror, custom, etc.).
    /// </para>
    /// <para>
    /// <b>Usage:</b>
    /// 1. Register controllers via RegisterController()
    /// 2. Wire up network delegates to your transport
    /// 3. Call Send* methods when controllers need to communicate
    /// 4. Call Receive* methods when network messages arrive
    /// </para>
    /// </remarks>
    [AddComponentMenu("Game Creator/Network/Stats/Network Stats Manager")]
    public class NetworkStatsManager : NetworkSingleton<NetworkStatsManager>
    {
        // ════════════════════════════════════════════════════════════════════════════════════════
        // SINGLETON (lazy-find override)
        // ════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>Singleton instance. Falls back to FindFirstObjectByType if not yet assigned.</summary>
        public new static NetworkStatsManager Instance
        {
            get
            {
                if (s_Instance == null)
                    s_Instance = FindFirstObjectByType<NetworkStatsManager>();
                return s_Instance;
            }
        }

        // ════════════════════════════════════════════════════════════════════════════════════════
        // TRANSPORT DELEGATES - Wire to your networking solution
        // ════════════════════════════════════════════════════════════════════════════════════════

        // ─────────────────────────────────────────────────────────────────────────────────────────
        // CLIENT → SERVER
        // ─────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Send stat modify request to server.</summary>
        public Action<NetworkStatModifyRequest> OnSendStatModifyRequest;

        /// <summary>Send attribute modify request to server.</summary>
        public Action<NetworkAttributeModifyRequest> OnSendAttributeModifyRequest;

        /// <summary>Send status effect request to server.</summary>
        public Action<NetworkStatusEffectRequest> OnSendStatusEffectRequest;

        /// <summary>Send stat modifier request to server.</summary>
        public Action<NetworkStatModifierRequest> OnSendStatModifierRequest;

        /// <summary>Send clear status effects request to server.</summary>
        public Action<NetworkClearStatusEffectsRequest> OnSendClearStatusEffectsRequest;

        // ─────────────────────────────────────────────────────────────────────────────────────────
        // SERVER → CLIENT (Single target)
        // ─────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Send stat modify response to requesting client. (networkId, response)</summary>
        public Action<uint, NetworkStatModifyResponse> OnSendStatModifyResponse;

        /// <summary>Send attribute modify response to requesting client. (networkId, response)</summary>
        public Action<uint, NetworkAttributeModifyResponse> OnSendAttributeModifyResponse;

        /// <summary>Send status effect response to requesting client. (networkId, response)</summary>
        public Action<uint, NetworkStatusEffectResponse> OnSendStatusEffectResponse;

        /// <summary>Send stat modifier response to requesting client. (networkId, response)</summary>
        public Action<uint, NetworkStatModifierResponse> OnSendStatModifierResponse;

        /// <summary>Send clear status effects response to requesting client. (networkId, response)</summary>
        public Action<uint, NetworkClearStatusEffectsResponse> OnSendClearStatusEffectsResponse;

        // ─────────────────────────────────────────────────────────────────────────────────────────
        // SERVER → ALL CLIENTS (Broadcast)
        // ─────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Broadcast stat change to all clients.</summary>
        public Action<NetworkStatChangeBroadcast> OnBroadcastStatChange;

        /// <summary>Broadcast attribute change to all clients.</summary>
        public Action<NetworkAttributeChangeBroadcast> OnBroadcastAttributeChange;

        /// <summary>Broadcast status effect change to all clients.</summary>
        public Action<NetworkStatusEffectBroadcast> OnBroadcastStatusEffectChange;

        /// <summary>Broadcast stat modifier change to all clients.</summary>
        public Action<NetworkStatModifierBroadcast> OnBroadcastStatModifierChange;

        /// <summary>Broadcast full stats snapshot to all clients.</summary>
        public Action<NetworkStatsSnapshot> OnBroadcastFullSnapshot;

        /// <summary>Broadcast delta state to all clients.</summary>
        public Action<NetworkStatsDelta> OnBroadcastDelta;

        // ─────────────────────────────────────────────────────────────────────────────────────────
        // SERVER → SINGLE CLIENT (Targeted)
        // ─────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Send full snapshot to specific client. (clientId, snapshot)</summary>
        public Action<ulong, NetworkStatsSnapshot> OnSendSnapshotToClient;

        // ════════════════════════════════════════════════════════════════════════════════════════
        // INSPECTOR
        // ════════════════════════════════════════════════════════════════════════════════════════

        [Header("Settings")]
        [Tooltip("Whether this instance is on the server.")]
        [SerializeField] private bool m_IsServer;

        [Header("Validation")]
        [Tooltip("Maximum pending requests per player before rate limiting.")]
        [SerializeField] private int m_MaxPendingRequestsPerPlayer = 50;

        [Tooltip("Request timeout in seconds.")]
        [SerializeField] private float m_RequestTimeout = 5f;

        [Header("Debug")]
        [SerializeField] private bool m_LogNetworkMessages = false;

        // ════════════════════════════════════════════════════════════════════════════════════════
        // PRIVATE FIELDS
        // ════════════════════════════════════════════════════════════════════════════════════════

        private readonly Dictionary<uint, NetworkStatsController> m_Controllers = new(32);
        private readonly Dictionary<ulong, int> m_PendingRequestCounts = new(32);
        private NetworkStatsPatchHooks m_PatchHooks;

        // ════════════════════════════════════════════════════════════════════════════════════════
        // PROPERTIES
        // ════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>Whether this manager is running on server.</summary>
        public bool IsServer
        {
            get => m_IsServer;
            set
            {
                m_IsServer = value;
                SecurityIntegration.SetModuleServerContext("Stats", m_IsServer);
                SecurityIntegration.EnsureSecurityManagerInitialized(m_IsServer, ResolveSecurityTimeProvider);
                SyncPatchHooks();
                if (m_IsServer) RefreshOwnedEntityMappings();
            }
        }

        /// <summary>Number of registered controllers.</summary>
        public int ControllerCount => m_Controllers.Count;

        /// <summary>Configured timeout for client-side requests (seconds).</summary>
        public float RequestTimeoutSeconds => m_RequestTimeout;

        // ════════════════════════════════════════════════════════════════════════════════════════
        // UNITY LIFECYCLE
        // ════════════════════════════════════════════════════════════════════════════════════════
        private void OnEnable()
        {
            SecurityIntegration.SetModuleServerContext("Stats", m_IsServer);
            SecurityIntegration.EnsureSecurityManagerInitialized(m_IsServer, ResolveSecurityTimeProvider);
            SyncPatchHooks();
        }

        private void OnDisable()
        {
            SecurityIntegration.SetModuleServerContext("Stats", false);
            if (m_PatchHooks != null)
            {
                m_PatchHooks.Initialize(false);
            }
        }


        // ════════════════════════════════════════════════════════════════════════════════════════
        // REGISTRATION
        // ════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Register a stats controller for network communication.
        /// </summary>
        public void RegisterController(uint networkId, NetworkStatsController controller)
        {
            if (controller == null)
            {
                Debug.LogWarning("[NetworkStatsManager] Cannot register null controller");
                return;
            }

            m_Controllers[networkId] = controller;
            RegisterOwnedEntityMapping(networkId);

            if (m_LogNetworkMessages)
            {
                Debug.Log($"[NetworkStatsManager] Registered controller for NetworkId={networkId}");
            }
        }

        /// <summary>
        /// Unregister a stats controller.
        /// </summary>
        public void UnregisterController(uint networkId)
        {
            bool removed = m_Controllers.Remove(networkId);
            if (removed)
            {
                SecurityIntegration.UnregisterEntity(networkId);
            }

            if (removed && m_LogNetworkMessages)
            {
                Debug.Log($"[NetworkStatsManager] Unregistered controller for NetworkId={networkId}");
            }
        }

        /// <summary>
        /// Get a registered controller by network ID.
        /// </summary>
        public NetworkStatsController GetController(uint networkId)
        {
            return m_Controllers.TryGetValue(networkId, out var controller) ? controller : null;
        }

        // ════════════════════════════════════════════════════════════════════════════════════════
        // CLIENT → SERVER: SENDING REQUESTS
        // ════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// [Client] Send stat modification request to server.
        /// </summary>
        public void SendStatModifyRequest(NetworkStatModifyRequest request)
        {
            if (m_LogNetworkMessages)
            {
                Debug.Log($"[NetworkStatsManager] Sending stat modify request: RequestId={request.RequestId}, StatHash={request.StatHash}");
            }

            OnSendStatModifyRequest?.Invoke(request);
        }

        /// <summary>
        /// [Client] Send attribute modification request to server.
        /// </summary>
        public void SendAttributeModifyRequest(NetworkAttributeModifyRequest request)
        {
            if (m_LogNetworkMessages)
            {
                Debug.Log($"[NetworkStatsManager] Sending attribute modify request: RequestId={request.RequestId}, AttrHash={request.AttributeHash}");
            }

            OnSendAttributeModifyRequest?.Invoke(request);
        }

        /// <summary>
        /// [Client] Send status effect request to server.
        /// </summary>
        public void SendStatusEffectRequest(NetworkStatusEffectRequest request)
        {
            if (m_LogNetworkMessages)
            {
                Debug.Log($"[NetworkStatsManager] Sending status effect request: RequestId={request.RequestId}, Action={request.Action}");
            }

            OnSendStatusEffectRequest?.Invoke(request);
        }

        /// <summary>
        /// [Client] Send stat modifier request to server.
        /// </summary>
        public void SendStatModifierRequest(NetworkStatModifierRequest request)
        {
            if (m_LogNetworkMessages)
            {
                Debug.Log($"[NetworkStatsManager] Sending stat modifier request: RequestId={request.RequestId}, Action={request.Action}");
            }

            OnSendStatModifierRequest?.Invoke(request);
        }

        /// <summary>
        /// [Client] Send clear status effects request to server.
        /// </summary>
        public void SendClearStatusEffectsRequest(NetworkClearStatusEffectsRequest request)
        {
            if (m_LogNetworkMessages)
            {
                Debug.Log($"[NetworkStatsManager] Sending clear status effects request: RequestId={request.RequestId}, TypeMask={request.TypeMask}");
            }

            OnSendClearStatusEffectsRequest?.Invoke(request);
        }

        // ════════════════════════════════════════════════════════════════════════════════════════
        // SERVER: RECEIVING & PROCESSING REQUESTS
        // ════════════════════════════════════════════════════════════════════════════════════════

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

        private static StatRejectionReason GetSecurityRejection(uint actorNetworkId, uint correlationId)
        {
            return SecurityIntegration.IsProtocolContextMismatch(actorNetworkId, correlationId)
                ? StatRejectionReason.ProtocolMismatch
                : StatRejectionReason.SecurityViolation;
        }

        private void RegisterOwnedEntityMapping(uint entityNetworkId)
        {
            if (!m_IsServer || entityNetworkId == 0) return;

            SecurityIntegration.RegisterEntityActor(entityNetworkId, entityNetworkId);

            var bridge = NetworkTransportBridge.Active;
            if (bridge != null &&
                bridge.TryGetCharacterOwner(entityNetworkId, out uint ownerClientId) &&
                NetworkTransportBridge.IsValidClientId(ownerClientId))
            {
                SecurityIntegration.RegisterEntityOwner(entityNetworkId, ownerClientId);
            }
        }

        private void RefreshOwnedEntityMappings()
        {
            foreach (var kvp in m_Controllers)
            {
                RegisterOwnedEntityMapping(kvp.Key);
            }
        }

        private static bool ValidateTargetOwnership(uint senderClientId, uint actorNetworkId, uint targetNetworkId, string requestType)
        {
            return SecurityIntegration.ValidateTargetEntityOwnership(
                senderClientId,
                actorNetworkId,
                targetNetworkId,
                "Stats",
                requestType);
        }

        private static float ResolveSecurityTimeProvider()
        {
            var bridge = NetworkTransportBridge.Active;
            return bridge != null && bridge.IsServer ? bridge.ServerTime : Time.time;
        }

        private void SyncPatchHooks()
        {
            if (!m_IsServer)
            {
                if (m_PatchHooks != null) m_PatchHooks.Initialize(false);
                return;
            }

            if (m_PatchHooks == null)
            {
                m_PatchHooks = GetComponent<NetworkStatsPatchHooks>();
                if (m_PatchHooks == null)
                {
                    m_PatchHooks = gameObject.AddComponent<NetworkStatsPatchHooks>();
                }
            }

            m_PatchHooks.Initialize(true);
        }

        /// <summary>
        /// [Server] Process incoming stat modify request from client.
        /// </summary>
        public void ReceiveStatModifyRequest(NetworkStatModifyRequest request, ulong clientId)
        {
            if (!m_IsServer)
            {
                Debug.LogWarning("[NetworkStatsManager] Non-server received server request");
                return;
            }

            uint senderClientId = GetSenderClientId(clientId);
            if (!SecurityIntegration.ValidateModuleRequest(
                    senderClientId,
                    BuildContext(request.ActorNetworkId, request.CorrelationId),
                    "Stats",
                    nameof(NetworkStatModifyRequest)))
            {
                OnSendStatModifyResponse?.Invoke(senderClientId, new NetworkStatModifyResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Authorized = false,
                    RejectionReason = GetSecurityRejection(request.ActorNetworkId, request.CorrelationId)
                });
                return;
            }
            if (!ValidateTargetOwnership(senderClientId, request.ActorNetworkId, request.TargetNetworkId, nameof(NetworkStatModifyRequest)))
            {
                OnSendStatModifyResponse?.Invoke(senderClientId, new NetworkStatModifyResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Authorized = false,
                    RejectionReason = StatRejectionReason.SecurityViolation
                });
                return;
            }

            // Rate limit check
            if (!CheckAndIncrementPendingRequests(clientId))
            {
                var rejectResponse = new NetworkStatModifyResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Authorized = false,
                    RejectionReason = StatRejectionReason.RateLimitExceeded
                };
                OnSendStatModifyResponse?.Invoke(senderClientId, rejectResponse);
                return;
            }

            if (m_LogNetworkMessages)
            {
                Debug.Log($"[NetworkStatsManager] Server received stat modify request from client {clientId}: RequestId={request.RequestId}");
            }

            try
            {
                // Find target controller
                var controller = GetController(request.TargetNetworkId);
                if (controller == null)
                {
                    var response = new NetworkStatModifyResponse
                    {
                        RequestId = request.RequestId,
                        ActorNetworkId = request.ActorNetworkId,
                        CorrelationId = request.CorrelationId,
                        Authorized = false,
                        RejectionReason = StatRejectionReason.TargetNotFound
                    };
                    OnSendStatModifyResponse?.Invoke(senderClientId, response);
                    return;
                }

                // Process request
                var result = controller.ProcessStatModifyRequest(request, senderClientId);
                result.ActorNetworkId = request.ActorNetworkId;
                result.CorrelationId = request.CorrelationId;

                // Send response to client
                OnSendStatModifyResponse?.Invoke(senderClientId, result);
            }
            finally
            {
                DecrementPendingRequests(clientId);
            }
        }

        /// <summary>
        /// [Server] Process incoming attribute modify request from client.
        /// </summary>
        public void ReceiveAttributeModifyRequest(NetworkAttributeModifyRequest request, ulong clientId)
        {
            if (!m_IsServer)
            {
                Debug.LogWarning("[NetworkStatsManager] Non-server received server request");
                return;
            }

            uint senderClientId = GetSenderClientId(clientId);
            if (!SecurityIntegration.ValidateModuleRequest(
                    senderClientId,
                    BuildContext(request.ActorNetworkId, request.CorrelationId),
                    "Stats",
                    nameof(NetworkAttributeModifyRequest)))
            {
                OnSendAttributeModifyResponse?.Invoke(senderClientId, new NetworkAttributeModifyResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Authorized = false,
                    RejectionReason = GetSecurityRejection(request.ActorNetworkId, request.CorrelationId)
                });
                return;
            }
            if (!ValidateTargetOwnership(senderClientId, request.ActorNetworkId, request.TargetNetworkId, nameof(NetworkAttributeModifyRequest)))
            {
                OnSendAttributeModifyResponse?.Invoke(senderClientId, new NetworkAttributeModifyResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Authorized = false,
                    RejectionReason = StatRejectionReason.SecurityViolation
                });
                return;
            }

            if (!CheckAndIncrementPendingRequests(clientId))
            {
                var rejectResponse = new NetworkAttributeModifyResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Authorized = false,
                    RejectionReason = StatRejectionReason.RateLimitExceeded
                };
                OnSendAttributeModifyResponse?.Invoke(senderClientId, rejectResponse);
                return;
            }

            if (m_LogNetworkMessages)
            {
                Debug.Log($"[NetworkStatsManager] Server received attribute modify request from client {clientId}: RequestId={request.RequestId}");
            }

            try
            {
                var controller = GetController(request.TargetNetworkId);
                if (controller == null)
                {
                    var response = new NetworkAttributeModifyResponse
                    {
                        RequestId = request.RequestId,
                        ActorNetworkId = request.ActorNetworkId,
                        CorrelationId = request.CorrelationId,
                        Authorized = false,
                        RejectionReason = StatRejectionReason.TargetNotFound
                    };
                    OnSendAttributeModifyResponse?.Invoke(senderClientId, response);
                    return;
                }

                var result = controller.ProcessAttributeModifyRequest(request, senderClientId);
                result.ActorNetworkId = request.ActorNetworkId;
                result.CorrelationId = request.CorrelationId;
                OnSendAttributeModifyResponse?.Invoke(senderClientId, result);
            }
            finally
            {
                DecrementPendingRequests(clientId);
            }
        }

        /// <summary>
        /// [Server] Process incoming status effect request from client.
        /// </summary>
        public void ReceiveStatusEffectRequest(NetworkStatusEffectRequest request, ulong clientId)
        {
            if (!m_IsServer)
            {
                Debug.LogWarning("[NetworkStatsManager] Non-server received server request");
                return;
            }

            uint senderClientId = GetSenderClientId(clientId);
            if (!SecurityIntegration.ValidateModuleRequest(
                    senderClientId,
                    BuildContext(request.ActorNetworkId, request.CorrelationId),
                    "Stats",
                    nameof(NetworkStatusEffectRequest)))
            {
                OnSendStatusEffectResponse?.Invoke(senderClientId, new NetworkStatusEffectResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Authorized = false,
                    RejectionReason = GetSecurityRejection(request.ActorNetworkId, request.CorrelationId)
                });
                return;
            }
            if (!ValidateTargetOwnership(senderClientId, request.ActorNetworkId, request.TargetNetworkId, nameof(NetworkStatusEffectRequest)))
            {
                OnSendStatusEffectResponse?.Invoke(senderClientId, new NetworkStatusEffectResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Authorized = false,
                    RejectionReason = StatRejectionReason.SecurityViolation
                });
                return;
            }

            if (!CheckAndIncrementPendingRequests(clientId))
            {
                var rejectResponse = new NetworkStatusEffectResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Authorized = false,
                    RejectionReason = StatRejectionReason.RateLimitExceeded
                };
                OnSendStatusEffectResponse?.Invoke(senderClientId, rejectResponse);
                return;
            }

            if (m_LogNetworkMessages)
            {
                Debug.Log($"[NetworkStatsManager] Server received status effect request from client {clientId}: RequestId={request.RequestId}");
            }

            try
            {
                var controller = GetController(request.TargetNetworkId);
                if (controller == null)
                {
                    var response = new NetworkStatusEffectResponse
                    {
                        RequestId = request.RequestId,
                        ActorNetworkId = request.ActorNetworkId,
                        CorrelationId = request.CorrelationId,
                        Authorized = false,
                        RejectionReason = StatRejectionReason.TargetNotFound
                    };
                    OnSendStatusEffectResponse?.Invoke(senderClientId, response);
                    return;
                }

                var result = controller.ProcessStatusEffectRequest(request, senderClientId);
                result.ActorNetworkId = request.ActorNetworkId;
                result.CorrelationId = request.CorrelationId;
                OnSendStatusEffectResponse?.Invoke(senderClientId, result);
            }
            finally
            {
                DecrementPendingRequests(clientId);
            }
        }

        /// <summary>
        /// [Server] Process incoming stat modifier request from client.
        /// </summary>
        public void ReceiveStatModifierRequest(NetworkStatModifierRequest request, ulong clientId)
        {
            if (!m_IsServer)
            {
                Debug.LogWarning("[NetworkStatsManager] Non-server received server request");
                return;
            }

            uint senderClientId = GetSenderClientId(clientId);
            if (!SecurityIntegration.ValidateModuleRequest(
                    senderClientId,
                    BuildContext(request.ActorNetworkId, request.CorrelationId),
                    "Stats",
                    nameof(NetworkStatModifierRequest)))
            {
                OnSendStatModifierResponse?.Invoke(senderClientId, new NetworkStatModifierResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Authorized = false,
                    RejectionReason = GetSecurityRejection(request.ActorNetworkId, request.CorrelationId)
                });
                return;
            }
            if (!ValidateTargetOwnership(senderClientId, request.ActorNetworkId, request.TargetNetworkId, nameof(NetworkStatModifierRequest)))
            {
                OnSendStatModifierResponse?.Invoke(senderClientId, new NetworkStatModifierResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Authorized = false,
                    RejectionReason = StatRejectionReason.SecurityViolation
                });
                return;
            }

            if (!CheckAndIncrementPendingRequests(clientId))
            {
                var rejectResponse = new NetworkStatModifierResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Authorized = false,
                    RejectionReason = StatRejectionReason.RateLimitExceeded
                };
                OnSendStatModifierResponse?.Invoke(senderClientId, rejectResponse);
                return;
            }

            if (m_LogNetworkMessages)
            {
                Debug.Log($"[NetworkStatsManager] Server received stat modifier request from client {clientId}: RequestId={request.RequestId}");
            }

            try
            {
                var controller = GetController(request.TargetNetworkId);
                if (controller == null)
                {
                    var response = new NetworkStatModifierResponse
                    {
                        RequestId = request.RequestId,
                        ActorNetworkId = request.ActorNetworkId,
                        CorrelationId = request.CorrelationId,
                        Authorized = false,
                        RejectionReason = StatRejectionReason.TargetNotFound
                    };
                    OnSendStatModifierResponse?.Invoke(senderClientId, response);
                    return;
                }

                var result = controller.ProcessStatModifierRequest(request, senderClientId);
                result.ActorNetworkId = request.ActorNetworkId;
                result.CorrelationId = request.CorrelationId;
                OnSendStatModifierResponse?.Invoke(senderClientId, result);
            }
            finally
            {
                DecrementPendingRequests(clientId);
            }
        }

        /// <summary>
        /// [Server] Process incoming clear status effects request from client.
        /// </summary>
        public void ReceiveClearStatusEffectsRequest(NetworkClearStatusEffectsRequest request, ulong clientId)
        {
            if (!m_IsServer)
            {
                Debug.LogWarning("[NetworkStatsManager] Non-server received server request");
                return;
            }

            uint senderClientId = GetSenderClientId(clientId);
            if (!SecurityIntegration.ValidateModuleRequest(
                    senderClientId,
                    BuildContext(request.ActorNetworkId, request.CorrelationId),
                    "Stats",
                    nameof(NetworkClearStatusEffectsRequest)))
            {
                OnSendClearStatusEffectsResponse?.Invoke(senderClientId, new NetworkClearStatusEffectsResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Authorized = false,
                    RejectionReason = GetSecurityRejection(request.ActorNetworkId, request.CorrelationId)
                });
                return;
            }
            if (!ValidateTargetOwnership(senderClientId, request.ActorNetworkId, request.TargetNetworkId, nameof(NetworkClearStatusEffectsRequest)))
            {
                OnSendClearStatusEffectsResponse?.Invoke(senderClientId, new NetworkClearStatusEffectsResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Authorized = false,
                    RejectionReason = StatRejectionReason.SecurityViolation
                });
                return;
            }

            if (!CheckAndIncrementPendingRequests(clientId))
            {
                var rejectResponse = new NetworkClearStatusEffectsResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Authorized = false,
                    RejectionReason = StatRejectionReason.RateLimitExceeded
                };
                OnSendClearStatusEffectsResponse?.Invoke(senderClientId, rejectResponse);
                return;
            }

            if (m_LogNetworkMessages)
            {
                Debug.Log($"[NetworkStatsManager] Server received clear status effects request from client {clientId}: RequestId={request.RequestId}");
            }

            try
            {
                var controller = GetController(request.TargetNetworkId);
                if (controller == null)
                {
                    var response = new NetworkClearStatusEffectsResponse
                    {
                        RequestId = request.RequestId,
                        ActorNetworkId = request.ActorNetworkId,
                        CorrelationId = request.CorrelationId,
                        Authorized = false,
                        RejectionReason = StatRejectionReason.TargetNotFound
                    };
                    OnSendClearStatusEffectsResponse?.Invoke(senderClientId, response);
                    return;
                }

                var result = controller.ProcessClearStatusEffectsRequest(request, senderClientId);
                result.ActorNetworkId = request.ActorNetworkId;
                result.CorrelationId = request.CorrelationId;
                OnSendClearStatusEffectsResponse?.Invoke(senderClientId, result);
            }
            finally
            {
                DecrementPendingRequests(clientId);
            }
        }

        // ════════════════════════════════════════════════════════════════════════════════════════
        // CLIENT: RECEIVING RESPONSES
        // ════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// [Client] Process stat modify response from server.
        /// </summary>
        public void ReceiveStatModifyResponse(NetworkStatModifyResponse response, uint targetNetworkId)
        {
            if (m_LogNetworkMessages)
            {
                Debug.Log($"[NetworkStatsManager] Client received stat modify response: RequestId={response.RequestId}, Authorized={response.Authorized}");
            }

            uint actorId = response.ActorNetworkId != 0 ? response.ActorNetworkId : targetNetworkId;
            var controller = GetController(actorId);
            controller?.ReceiveStatModifyResponse(response);
        }

        /// <summary>
        /// [Client] Process attribute modify response from server.
        /// </summary>
        public void ReceiveAttributeModifyResponse(NetworkAttributeModifyResponse response, uint targetNetworkId)
        {
            if (m_LogNetworkMessages)
            {
                Debug.Log($"[NetworkStatsManager] Client received attribute modify response: RequestId={response.RequestId}, Authorized={response.Authorized}");
            }

            uint actorId = response.ActorNetworkId != 0 ? response.ActorNetworkId : targetNetworkId;
            var controller = GetController(actorId);
            controller?.ReceiveAttributeModifyResponse(response);
        }

        /// <summary>
        /// [Client] Process status effect response from server.
        /// </summary>
        public void ReceiveStatusEffectResponse(NetworkStatusEffectResponse response, uint targetNetworkId)
        {
            if (m_LogNetworkMessages)
            {
                Debug.Log($"[NetworkStatsManager] Client received status effect response: RequestId={response.RequestId}, Authorized={response.Authorized}");
            }

            uint actorId = response.ActorNetworkId != 0 ? response.ActorNetworkId : targetNetworkId;
            var controller = GetController(actorId);
            controller?.ReceiveStatusEffectResponse(response);
        }

        /// <summary>
        /// [Client] Process stat modifier response from server.
        /// </summary>
        public void ReceiveStatModifierResponse(NetworkStatModifierResponse response, uint targetNetworkId)
        {
            if (m_LogNetworkMessages)
            {
                Debug.Log($"[NetworkStatsManager] Client received stat modifier response: RequestId={response.RequestId}, Authorized={response.Authorized}");
            }

            uint actorId = response.ActorNetworkId != 0 ? response.ActorNetworkId : targetNetworkId;
            var controller = GetController(actorId);
            controller?.ReceiveStatModifierResponse(response);
        }

        /// <summary>
        /// [Client] Process clear status effects response from server.
        /// </summary>
        public void ReceiveClearStatusEffectsResponse(NetworkClearStatusEffectsResponse response, uint targetNetworkId)
        {
            if (m_LogNetworkMessages)
            {
                Debug.Log($"[NetworkStatsManager] Client received clear status effects response: RequestId={response.RequestId}, Authorized={response.Authorized}");
            }

            uint actorId = response.ActorNetworkId != 0 ? response.ActorNetworkId : targetNetworkId;
            var controller = GetController(actorId);
            controller?.ReceiveClearStatusEffectsResponse(response);
        }

        // ════════════════════════════════════════════════════════════════════════════════════════
        // SERVER: BROADCASTING
        // ════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// [Server] Broadcast stat change to all clients.
        /// </summary>
        public void BroadcastStatChange(NetworkStatChangeBroadcast broadcast)
        {
            if (!m_IsServer) return;

            if (m_LogNetworkMessages)
            {
                Debug.Log($"[NetworkStatsManager] Broadcasting stat change: NetworkId={broadcast.NetworkId}, StatHash={broadcast.StatHash}");
            }

            OnBroadcastStatChange?.Invoke(broadcast);
        }

        /// <summary>
        /// [Server] Broadcast attribute change to all clients.
        /// </summary>
        public void BroadcastAttributeChange(NetworkAttributeChangeBroadcast broadcast)
        {
            if (!m_IsServer) return;

            if (m_LogNetworkMessages)
            {
                Debug.Log($"[NetworkStatsManager] Broadcasting attribute change: NetworkId={broadcast.NetworkId}, AttrHash={broadcast.AttributeHash}");
            }

            OnBroadcastAttributeChange?.Invoke(broadcast);
        }

        /// <summary>
        /// [Server] Broadcast status effect change to all clients.
        /// </summary>
        public void BroadcastStatusEffectChange(NetworkStatusEffectBroadcast broadcast)
        {
            if (!m_IsServer) return;

            if (m_LogNetworkMessages)
            {
                Debug.Log($"[NetworkStatsManager] Broadcasting status effect change: NetworkId={broadcast.NetworkId}, Action={broadcast.Action}");
            }

            OnBroadcastStatusEffectChange?.Invoke(broadcast);
        }

        /// <summary>
        /// [Server] Broadcast stat modifier change to all clients.
        /// </summary>
        public void BroadcastStatModifierChange(NetworkStatModifierBroadcast broadcast)
        {
            if (!m_IsServer) return;

            if (m_LogNetworkMessages)
            {
                Debug.Log($"[NetworkStatsManager] Broadcasting stat modifier change: NetworkId={broadcast.NetworkId}");
            }

            OnBroadcastStatModifierChange?.Invoke(broadcast);
        }

        /// <summary>
        /// [Server] Broadcast full stats snapshot to all clients.
        /// </summary>
        public void BroadcastFullSnapshot(NetworkStatsSnapshot snapshot)
        {
            if (!m_IsServer) return;

            if (m_LogNetworkMessages)
            {
                Debug.Log($"[NetworkStatsManager] Broadcasting full snapshot: NetworkId={snapshot.NetworkId}, Stats={snapshot.Stats?.Length ?? 0}");
            }

            OnBroadcastFullSnapshot?.Invoke(snapshot);
        }

        /// <summary>
        /// [Server] Broadcast delta state to all clients.
        /// </summary>
        public void BroadcastDelta(NetworkStatsDelta delta)
        {
            if (!m_IsServer) return;

            if (m_LogNetworkMessages)
            {
                Debug.Log($"[NetworkStatsManager] Broadcasting delta: NetworkId={delta.NetworkId}, ChangedStats={delta.ChangedStats?.Length ?? 0}");
            }

            OnBroadcastDelta?.Invoke(delta);
        }

        /// <summary>
        /// [Server] Send full snapshot to a specific joining client.
        /// </summary>
        public void SendSnapshotToClient(ulong clientId, NetworkStatsSnapshot snapshot)
        {
            if (!m_IsServer) return;

            if (m_LogNetworkMessages)
            {
                Debug.Log($"[NetworkStatsManager] Sending snapshot to client {clientId}: NetworkId={snapshot.NetworkId}");
            }

            OnSendSnapshotToClient?.Invoke(clientId, snapshot);
        }

        // ════════════════════════════════════════════════════════════════════════════════════════
        // CLIENT: RECEIVING BROADCASTS
        // ════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// [Client] Process stat change broadcast from server.
        /// </summary>
        public void ReceiveStatChangeBroadcast(NetworkStatChangeBroadcast broadcast)
        {
            if (m_LogNetworkMessages)
            {
                Debug.Log($"[NetworkStatsManager] Received stat change broadcast: NetworkId={broadcast.NetworkId}");
            }

            var controller = GetController(broadcast.NetworkId);
            controller?.ReceiveStatChangeBroadcast(broadcast);
        }

        /// <summary>
        /// [Client] Process attribute change broadcast from server.
        /// </summary>
        public void ReceiveAttributeChangeBroadcast(NetworkAttributeChangeBroadcast broadcast)
        {
            if (m_LogNetworkMessages)
            {
                Debug.Log($"[NetworkStatsManager] Received attribute change broadcast: NetworkId={broadcast.NetworkId}");
            }

            var controller = GetController(broadcast.NetworkId);
            controller?.ReceiveAttributeChangeBroadcast(broadcast);
        }

        /// <summary>
        /// [Client] Process status effect change broadcast from server.
        /// </summary>
        public void ReceiveStatusEffectBroadcast(NetworkStatusEffectBroadcast broadcast)
        {
            if (m_LogNetworkMessages)
            {
                Debug.Log($"[NetworkStatsManager] Received status effect broadcast: NetworkId={broadcast.NetworkId}");
            }

            var controller = GetController(broadcast.NetworkId);
            controller?.ReceiveStatusEffectBroadcast(broadcast);
        }

        /// <summary>
        /// [Client] Process stat modifier change broadcast from server.
        /// </summary>
        public void ReceiveStatModifierBroadcast(NetworkStatModifierBroadcast broadcast)
        {
            if (m_LogNetworkMessages)
            {
                Debug.Log($"[NetworkStatsManager] Received stat modifier broadcast: NetworkId={broadcast.NetworkId}");
            }

            var controller = GetController(broadcast.NetworkId);
            controller?.ReceiveStatModifierBroadcast(broadcast);
        }

        /// <summary>
        /// [Client] Process full snapshot from server.
        /// </summary>
        public void ReceiveFullSnapshot(NetworkStatsSnapshot snapshot)
        {
            if (m_LogNetworkMessages)
            {
                Debug.Log($"[NetworkStatsManager] Received full snapshot: NetworkId={snapshot.NetworkId}");
            }

            var controller = GetController(snapshot.NetworkId);
            controller?.ReceiveFullSnapshot(snapshot);
        }

        /// <summary>
        /// [Client] Process delta update from server.
        /// </summary>
        public void ReceiveDelta(NetworkStatsDelta delta)
        {
            if (m_LogNetworkMessages)
            {
                Debug.Log($"[NetworkStatsManager] Received delta: NetworkId={delta.NetworkId}");
            }

            var controller = GetController(delta.NetworkId);
            if (controller == null) return;

            // Apply changed stats
            if (delta.ChangedStats != null)
            {
                foreach (var statValue in delta.ChangedStats)
                {
                    var broadcast = new NetworkStatChangeBroadcast
                    {
                        NetworkId = delta.NetworkId,
                        StatHash = statValue.StatHash,
                        NewBaseValue = statValue.BaseValue,
                        NewComputedValue = statValue.ComputedValue
                    };
                    controller.ReceiveStatChangeBroadcast(broadcast);
                }
            }

            // Apply changed attributes
            if (delta.ChangedAttributes != null)
            {
                foreach (var attrValue in delta.ChangedAttributes)
                {
                    var broadcast = new NetworkAttributeChangeBroadcast
                    {
                        NetworkId = delta.NetworkId,
                        AttributeHash = attrValue.AttributeHash,
                        NewValue = attrValue.CurrentValue,
                        MaxValue = attrValue.MaxValue,
                        Change = 0f
                    };
                    controller.ReceiveAttributeChangeBroadcast(broadcast);
                }
            }
        }

        // ════════════════════════════════════════════════════════════════════════════════════════
        // SERVER VALIDATION EXTENSION POINTS
        // ════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>Custom stat modification validator. Return false to reject.</summary>
        public Func<NetworkStatModifyRequest, uint, (bool allowed, StatRejectionReason reason)> CustomStatValidator;

        /// <summary>Custom attribute modification validator. Return false to reject.</summary>
        public Func<NetworkAttributeModifyRequest, uint, (bool allowed, StatRejectionReason reason)> CustomAttributeValidator;

        /// <summary>Custom status effect validator. Return false to reject.</summary>
        public Func<NetworkStatusEffectRequest, uint, (bool allowed, StatRejectionReason reason)> CustomStatusEffectValidator;

        // ════════════════════════════════════════════════════════════════════════════════════════
        // HELPERS
        // ════════════════════════════════════════════════════════════════════════════════════════

        private bool CheckAndIncrementPendingRequests(ulong clientId)
        {
            if (!m_PendingRequestCounts.TryGetValue(clientId, out int count))
                count = 0;

            if (count >= m_MaxPendingRequestsPerPlayer)
            {
                Debug.LogWarning($"[NetworkStatsManager] Client {clientId} exceeded max pending requests");
                return false;
            }

            m_PendingRequestCounts[clientId] = count + 1;
            return true;
        }

        private void DecrementPendingRequests(ulong clientId)
        {
            if (m_PendingRequestCounts.TryGetValue(clientId, out int count))
            {
                m_PendingRequestCounts[clientId] = Math.Max(0, count - 1);
            }
        }

        // ════════════════════════════════════════════════════════════════════════════════════════
        // UTILITY METHODS
        // ════════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Get all registered controller IDs.
        /// </summary>
        public IEnumerable<uint> GetRegisteredNetworkIds()
        {
            return m_Controllers.Keys;
        }

        /// <summary>
        /// Send initial snapshots to a newly connected client.
        /// </summary>
        public void SendInitialState(ulong clientId)
        {
            if (!m_IsServer) return;

            foreach (var kvp in m_Controllers)
            {
                var snapshot = kvp.Value.GetFullSnapshot();
                SendSnapshotToClient(clientId, snapshot);
            }
        }

        /// <summary>
        /// Force a full sync of all stats to all clients.
        /// </summary>
        public void ForceFullSync()
        {
            if (!m_IsServer) return;

            foreach (var kvp in m_Controllers)
            {
                var snapshot = kvp.Value.GetFullSnapshot();
                BroadcastFullSnapshot(snapshot);
            }
        }

        /// <summary>
        /// Clear all registered controllers (e.g., on scene change).
        /// </summary>
        public void ClearControllers()
        {
            m_Controllers.Clear();

            if (m_LogNetworkMessages)
            {
                Debug.Log("[NetworkStatsManager] All controllers cleared");
            }
        }
    }
}
#endif
