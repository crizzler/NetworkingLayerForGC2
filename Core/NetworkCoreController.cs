using System;
using System.Collections.Generic;
using UnityEngine;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Common;

namespace Arawn.GameCreator2.Networking
{
    /// <summary>
    /// Server-authoritative controller for GC2 Core character features:
    /// Ragdoll, Props, Invincibility, Poise, Busy, and Interaction.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This component works alongside GC2's character system without modifying it.
    /// On clients, it intercepts feature requests and sends them to the server.
    /// On the server, it validates and applies changes, then broadcasts to all clients.
    /// </para>
    /// </remarks>
    [AddComponentMenu("Game Creator/Network/Network Core Controller")]
    [DefaultExecutionOrder(ApplicationManager.EXECUTION_ORDER_DEFAULT)]
    public partial class NetworkCoreController : NetworkSingleton<NetworkCoreController>
    {
        // ════════════════════════════════════════════════════════════════════════════════════════
        // CONFIGURATION
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        protected override DuplicatePolicy OnDuplicatePolicy => DuplicatePolicy.WarnOnly;
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // INSPECTOR
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        [Header("Ragdoll Settings")]
        [Tooltip("Minimum time between ragdoll state changes.")]
        [SerializeField] private float m_RagdollCooldown = 0.5f;
        
        [Header("Props Settings")]
        [Tooltip("Maximum props per character.")]
        [SerializeField] private int m_MaxPropsPerCharacter = 10;
        
        [Header("Invincibility Settings")]
        [Tooltip("Maximum invincibility duration allowed.")]
        [SerializeField] private float m_MaxInvincibilityDuration = 30f;
        
        [Tooltip("Minimum time between invincibility activations.")]
        [SerializeField] private float m_InvincibilityCooldown = 5f;
        
        [Header("Poise Settings")]
        [Tooltip("Enable poise damage validation.")]
        [SerializeField] private bool m_ValidatePoiseDamage = true;
        
        [Header("Interaction Settings")]
        [Tooltip("Maximum interaction range for validation.")]
        [SerializeField] private float m_MaxInteractionRange = 5f;
        
        [Tooltip("Enable interaction cooldown per target.")]
        [SerializeField] private float m_InteractionCooldown = 0.5f;
        
        [Header("Debug")]
        [SerializeField] private bool m_DebugLog = false;

        [Header("Client Reliability")]
        [Tooltip("Seconds before unresolved client requests are timed out and dropped.")]
        [SerializeField] private float m_PendingRequestTimeoutSeconds = 8f;

        [Tooltip("How often pending client requests are scanned for timeouts.")]
        [SerializeField] private float m_PendingCleanupIntervalSeconds = 1f;
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // EVENTS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        // Ragdoll Events
        public event Action<NetworkRagdollRequest> OnRagdollRequestSent;
        public event Action<NetworkRagdollResponse> OnRagdollResponseReceived;
        public event Action<uint, NetworkRagdollRequest> OnRagdollRequestReceived;
        public event Action<NetworkRagdollBroadcast> OnRagdollBroadcastReceived;
        
        // Props Events
        public event Action<NetworkPropRequest> OnPropRequestSent;
        public event Action<NetworkPropResponse> OnPropResponseReceived;
        public event Action<uint, NetworkPropRequest> OnPropRequestReceived;
        public event Action<NetworkPropBroadcast> OnPropBroadcastReceived;
        
        // Invincibility Events
        public event Action<NetworkInvincibilityRequest> OnInvincibilityRequestSent;
        public event Action<NetworkInvincibilityResponse> OnInvincibilityResponseReceived;
        public event Action<uint, NetworkInvincibilityRequest> OnInvincibilityRequestReceived;
        public event Action<NetworkInvincibilityBroadcast> OnInvincibilityBroadcastReceived;
        
        // Poise Events
        public event Action<NetworkPoiseRequest> OnPoiseRequestSent;
        public event Action<NetworkPoiseResponse> OnPoiseResponseReceived;
        public event Action<uint, NetworkPoiseRequest> OnPoiseRequestReceived;
        public event Action<NetworkPoiseBroadcast> OnPoiseBroadcastReceived;
        
        // Busy Events
        public event Action<NetworkBusyRequest> OnBusyRequestSent;
        public event Action<NetworkBusyResponse> OnBusyResponseReceived;
        public event Action<uint, NetworkBusyRequest> OnBusyRequestReceived;
        public event Action<NetworkBusyBroadcast> OnBusyBroadcastReceived;
        
        // Interaction Events
        public event Action<NetworkInteractionRequest> OnInteractionRequestSent;
        public event Action<NetworkInteractionResponse> OnInteractionResponseReceived;
        public event Action<uint, NetworkInteractionRequest> OnInteractionRequestReceived;
        public event Action<NetworkInteractionBroadcast> OnInteractionBroadcastReceived;
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // DELEGATES (Network Integration Points)
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        // Ragdoll
        public Action<NetworkRagdollRequest> SendRagdollRequestToServer;
        public Action<uint, NetworkRagdollResponse> SendRagdollResponseToClient;
        public Action<NetworkRagdollBroadcast> BroadcastRagdollToClients;
        
        // Props
        public Action<NetworkPropRequest> SendPropRequestToServer;
        public Action<uint, NetworkPropResponse> SendPropResponseToClient;
        public Action<NetworkPropBroadcast> BroadcastPropToClients;
        
        // Invincibility
        public Action<NetworkInvincibilityRequest> SendInvincibilityRequestToServer;
        public Action<uint, NetworkInvincibilityResponse> SendInvincibilityResponseToClient;
        public Action<NetworkInvincibilityBroadcast> BroadcastInvincibilityToClients;
        
        // Poise
        public Action<NetworkPoiseRequest> SendPoiseRequestToServer;
        public Action<uint, NetworkPoiseResponse> SendPoiseResponseToClient;
        public Action<NetworkPoiseBroadcast> BroadcastPoiseToClients;
        
        // Busy
        public Action<NetworkBusyRequest> SendBusyRequestToServer;
        public Action<uint, NetworkBusyResponse> SendBusyResponseToClient;
        public Action<NetworkBusyBroadcast> BroadcastBusyToClients;
        
        // Interaction
        public Action<NetworkInteractionRequest> SendInteractionRequestToServer;
        public Action<uint, NetworkInteractionResponse> SendInteractionResponseToClient;
        public Action<NetworkInteractionBroadcast> BroadcastInteractionToClients;
        
        // Utility
        public Func<float> GetServerTime;
        public Func<uint, Character> GetCharacterByNetworkId;
        public Func<uint> GetLocalPlayerNetworkId;
        public Func<int, GameObject> GetPropPrefabByHash;
        public Func<int, Transform> GetBoneByHash;
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // PRIVATE FIELDS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        private bool m_IsServer;
        private bool m_IsClient;
        private float m_LastPendingCleanupTime;

        private static readonly List<ulong> s_SharedPendingRemovalBuffer = new(16);
        
        // Request tracking
        private ushort m_NextRequestId = 1;
        private ushort m_LastIssuedRequestId = 1;

        private ushort GetNextRequestId()
        {
            if (m_NextRequestId == 0)
            {
                m_NextRequestId = 1;
            }

            ushort requestId = m_NextRequestId;
            m_NextRequestId++;
            if (m_NextRequestId == 0)
            {
                m_NextRequestId = 1;
            }

            m_LastIssuedRequestId = requestId;
            return requestId;
        }

        private static ulong GetPendingKey(uint actorNetworkId, uint correlationId, ushort requestId)
        {
            uint pendingCorrelation = correlationId != 0 ? correlationId : requestId;
            return ((ulong)actorNetworkId << 32) | pendingCorrelation;
        }
        
        // Pending requests (client-side)
        private readonly Dictionary<ulong, PendingRagdollRequest> m_PendingRagdollRequests = new(16);
        private readonly Dictionary<ulong, PendingPropRequest> m_PendingPropRequests = new(16);
        private readonly Dictionary<ulong, PendingInvincibilityRequest> m_PendingInvincibilityRequests = new(16);
        private readonly Dictionary<ulong, PendingPoiseRequest> m_PendingPoiseRequests = new(16);
        private readonly Dictionary<ulong, PendingBusyRequest> m_PendingBusyRequests = new(16);
        private readonly Dictionary<ulong, PendingInteractionRequest> m_PendingInteractionRequests = new(16);
        
        // Cooldown tracking (server-side)
        private readonly Dictionary<uint, float> m_RagdollCooldowns = new(64);
        private readonly Dictionary<uint, float> m_InvincibilityCooldowns = new(64);
        private readonly Dictionary<(uint, uint), float> m_InteractionCooldowns = new(128);
        
        // Props tracking (server-side)
        private readonly Dictionary<uint, List<int>> m_CharacterProps = new(64);
        private int m_NextPropInstanceId = 1;
        
        // Statistics
        private NetworkCoreStats m_Stats;
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // PENDING REQUEST STRUCTS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        private struct PendingRagdollRequest : ITimeoutAwarePendingRequest
        {
            public NetworkRagdollRequest Request;
            public float SentTime;
            public Action<NetworkRagdollResponse> Callback;
            public float PendingSentTime => SentTime;

            public bool TryInvokeTimeout()
            {
                if (Callback == null) return false;

                Callback.Invoke(new NetworkRagdollResponse
                {
                    RequestId = Request.RequestId,
                    ActorNetworkId = Request.ActorNetworkId,
                    CorrelationId = Request.CorrelationId,
                    Approved = false,
                    RejectReason = RagdollRejectReason.Timeout
                });
                return true;
            }
        }
        
        private struct PendingPropRequest : ITimeoutAwarePendingRequest
        {
            public NetworkPropRequest Request;
            public float SentTime;
            public Action<NetworkPropResponse> Callback;
            public float PendingSentTime => SentTime;

            public bool TryInvokeTimeout()
            {
                if (Callback == null) return false;

                Callback.Invoke(new NetworkPropResponse
                {
                    RequestId = Request.RequestId,
                    ActorNetworkId = Request.ActorNetworkId,
                    CorrelationId = Request.CorrelationId,
                    Approved = false,
                    RejectReason = PropRejectReason.Timeout,
                    PropInstanceId = 0
                });
                return true;
            }
        }
        
        private struct PendingInvincibilityRequest : ITimeoutAwarePendingRequest
        {
            public NetworkInvincibilityRequest Request;
            public float SentTime;
            public Action<NetworkInvincibilityResponse> Callback;
            public float PendingSentTime => SentTime;

            public bool TryInvokeTimeout()
            {
                if (Callback == null) return false;

                Callback.Invoke(new NetworkInvincibilityResponse
                {
                    RequestId = Request.RequestId,
                    ActorNetworkId = Request.ActorNetworkId,
                    CorrelationId = Request.CorrelationId,
                    Approved = false,
                    RejectReason = InvincibilityRejectReason.Timeout,
                    ApprovedDuration = 0f
                });
                return true;
            }
        }
        
        private struct PendingPoiseRequest : ITimeoutAwarePendingRequest
        {
            public NetworkPoiseRequest Request;
            public float SentTime;
            public Action<NetworkPoiseResponse> Callback;
            public float PendingSentTime => SentTime;

            public bool TryInvokeTimeout()
            {
                if (Callback == null) return false;

                Callback.Invoke(new NetworkPoiseResponse
                {
                    RequestId = Request.RequestId,
                    ActorNetworkId = Request.ActorNetworkId,
                    CorrelationId = Request.CorrelationId,
                    Approved = false,
                    RejectReason = PoiseRejectReason.Timeout,
                    CurrentPoise = 0f,
                    IsBroken = false
                });
                return true;
            }
        }
        
        private struct PendingBusyRequest : ITimeoutAwarePendingRequest
        {
            public NetworkBusyRequest Request;
            public float SentTime;
            public Action<NetworkBusyResponse> Callback;
            public float PendingSentTime => SentTime;

            public bool TryInvokeTimeout()
            {
                if (Callback == null) return false;

                Callback.Invoke(new NetworkBusyResponse
                {
                    RequestId = Request.RequestId,
                    ActorNetworkId = Request.ActorNetworkId,
                    CorrelationId = Request.CorrelationId,
                    Approved = false,
                    RejectReason = BusyRejectReason.Timeout
                });
                return true;
            }
        }
        
        private struct PendingInteractionRequest : ITimeoutAwarePendingRequest
        {
            public NetworkInteractionRequest Request;
            public float SentTime;
            public Action<NetworkInteractionResponse> Callback;
            public float PendingSentTime => SentTime;

            public bool TryInvokeTimeout()
            {
                if (Callback == null) return false;

                Callback.Invoke(new NetworkInteractionResponse
                {
                    RequestId = Request.RequestId,
                    ActorNetworkId = Request.ActorNetworkId,
                    CorrelationId = Request.CorrelationId,
                    Approved = false,
                    RejectReason = InteractionRejectReason.Timeout,
                    ResultData = 0
                });
                return true;
            }
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // PROPERTIES
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        public bool IsServer => m_IsServer;
        public bool IsClient => m_IsClient;
        public NetworkCoreStats Stats => m_Stats;
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // UNITY LIFECYCLE
        // ════════════════════════════════════════════════════════════════════════════════════════

        private void Update()
        {
            if (!m_IsClient) return;

            float now = Time.time;
            float cleanupInterval = Mathf.Max(0.1f, m_PendingCleanupIntervalSeconds);
            if (now - m_LastPendingCleanupTime < cleanupInterval)
            {
                return;
            }

            m_LastPendingCleanupTime = now;
            CleanupTimedOutPendingRequests(now);
        }

        // ════════════════════════════════════════════════════════════════════════════════════════
        // INITIALIZATION
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        public void Initialize(bool isServer, bool isClient)
        {
            m_IsServer = isServer;
            m_IsClient = isClient;
            
            ClearPendingRequests();
            ClearCooldowns();
            m_Stats.Reset();
            m_LastPendingCleanupTime = Time.time;
            
            if (m_DebugLog)
            {
                Debug.Log($"[NetworkCore] Initialized - Server: {isServer}, Client: {isClient}");
            }
        }
        
        private void ClearPendingRequests()
        {
            m_PendingRagdollRequests.Clear();
            m_PendingPropRequests.Clear();
            m_PendingInvincibilityRequests.Clear();
            m_PendingPoiseRequests.Clear();
            m_PendingBusyRequests.Clear();
            m_PendingInteractionRequests.Clear();
            m_LastPendingCleanupTime = Time.time;
        }
        
        private void ClearCooldowns()
        {
            m_RagdollCooldowns.Clear();
            m_InvincibilityCooldowns.Clear();
            m_InteractionCooldowns.Clear();
        }

        private void CleanupTimedOutPendingRequests(float now)
        {
            float timeout = Mathf.Max(0.25f, m_PendingRequestTimeoutSeconds);
            CleanupPendingBucket(m_PendingRagdollRequests, now, timeout, "Ragdoll");
            CleanupPendingBucket(m_PendingPropRequests, now, timeout, "Prop");
            CleanupPendingBucket(m_PendingInvincibilityRequests, now, timeout, "Invincibility");
            CleanupPendingBucket(m_PendingPoiseRequests, now, timeout, "Poise");
            CleanupPendingBucket(m_PendingBusyRequests, now, timeout, "Busy");
            CleanupPendingBucket(m_PendingInteractionRequests, now, timeout, "Interaction");

            void CleanupPendingBucket<T>(
                Dictionary<ulong, T> pending,
                float currentTime,
                float timeoutSeconds,
                string requestName)
                where T : struct, ITimeoutAwarePendingRequest
            {
                if (pending.Count == 0) return;

                int removedCount = PendingRequestCleanup.RemoveTimedOut(
                    pending,
                    s_SharedPendingRemovalBuffer,
                    currentTime,
                    timeoutSeconds,
                    timedOut => timedOut.TryInvokeTimeout(),
                    exception =>
                        Debug.LogError($"[NetworkCore] Timeout callback for {requestName} threw an exception: {exception}"));

                if (removedCount > 0 && m_DebugLog)
                {
                    Debug.LogWarning(
                        $"[NetworkCore] Timed out {removedCount} pending {requestName} request(s) after {timeoutSeconds:F1}s.");
                }
            }
        }
        
    }
    
    // ════════════════════════════════════════════════════════════════════════════════════════════
    // HELPER COMPONENT
    // ════════════════════════════════════════════════════════════════════════════════════════════
    
    /// <summary>
    /// Tracks network prop instances for removal.
    /// </summary>
    public class NetworkPropTracker : MonoBehaviour
    {
        public int InstanceId;
        public int PropHash;
    }
}
