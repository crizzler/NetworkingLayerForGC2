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
    public class NetworkCoreController : NetworkSingleton<NetworkCoreController>
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
        
        // Request tracking
        private ushort m_NextRequestId = 1;

        private static uint GetPendingKey(uint correlationId, ushort requestId)
        {
            return correlationId != 0 ? correlationId : requestId;
        }
        
        // Pending requests (client-side)
        private readonly Dictionary<uint, PendingRagdollRequest> m_PendingRagdollRequests = new(16);
        private readonly Dictionary<uint, PendingPropRequest> m_PendingPropRequests = new(16);
        private readonly Dictionary<uint, PendingInvincibilityRequest> m_PendingInvincibilityRequests = new(16);
        private readonly Dictionary<uint, PendingPoiseRequest> m_PendingPoiseRequests = new(16);
        private readonly Dictionary<uint, PendingBusyRequest> m_PendingBusyRequests = new(16);
        private readonly Dictionary<uint, PendingInteractionRequest> m_PendingInteractionRequests = new(16);
        
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
        
        private struct PendingRagdollRequest
        {
            public NetworkRagdollRequest Request;
            public float SentTime;
            public Action<NetworkRagdollResponse> Callback;
        }
        
        private struct PendingPropRequest
        {
            public NetworkPropRequest Request;
            public float SentTime;
            public Action<NetworkPropResponse> Callback;
        }
        
        private struct PendingInvincibilityRequest
        {
            public NetworkInvincibilityRequest Request;
            public float SentTime;
            public Action<NetworkInvincibilityResponse> Callback;
        }
        
        private struct PendingPoiseRequest
        {
            public NetworkPoiseRequest Request;
            public float SentTime;
            public Action<NetworkPoiseResponse> Callback;
        }
        
        private struct PendingBusyRequest
        {
            public NetworkBusyRequest Request;
            public float SentTime;
            public Action<NetworkBusyResponse> Callback;
        }
        
        private struct PendingInteractionRequest
        {
            public NetworkInteractionRequest Request;
            public float SentTime;
            public Action<NetworkInteractionResponse> Callback;
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
        }
        
        private void ClearCooldowns()
        {
            m_RagdollCooldowns.Clear();
            m_InvincibilityCooldowns.Clear();
            m_InteractionCooldowns.Clear();
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // RAGDOLL - CLIENT METHODS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// [Client] Request to start ragdoll on a character.
        /// </summary>
        public void RequestStartRagdoll(uint characterNetworkId, Vector3 force = default, 
            Vector3 forcePoint = default, Action<NetworkRagdollResponse> callback = null)
        {
            if (!m_IsClient) return;
            
            var request = new NetworkRagdollRequest
            {
                RequestId = m_NextRequestId++,
                ActorNetworkId = characterNetworkId,
                CorrelationId = NetworkCorrelation.Compose(characterNetworkId, (ushort)(m_NextRequestId - 1)),
                CharacterNetworkId = characterNetworkId,
                ClientTime = GetServerTime?.Invoke() ?? Time.time,
                ActionType = force != default ? RagdollActionType.StartRagdollWithForce : RagdollActionType.StartRagdoll,
                Force = force,
                ForcePoint = forcePoint
            };
            
            m_PendingRagdollRequests[GetPendingKey(request.CorrelationId, request.RequestId)] = new PendingRagdollRequest
            {
                Request = request,
                SentTime = Time.time,
                Callback = callback
            };
            
            m_Stats.RagdollRequestsSent++;
            SendRagdollRequestToServer?.Invoke(request);
            OnRagdollRequestSent?.Invoke(request);
        }
        
        /// <summary>
        /// [Client] Request to start recovery from ragdoll.
        /// </summary>
        public void RequestStartRecover(uint characterNetworkId, bool instant = false,
            Action<NetworkRagdollResponse> callback = null)
        {
            if (!m_IsClient) return;
            
            var request = new NetworkRagdollRequest
            {
                RequestId = m_NextRequestId++,
                ActorNetworkId = characterNetworkId,
                CorrelationId = NetworkCorrelation.Compose(characterNetworkId, (ushort)(m_NextRequestId - 1)),
                CharacterNetworkId = characterNetworkId,
                ClientTime = GetServerTime?.Invoke() ?? Time.time,
                ActionType = instant ? RagdollActionType.InstantRecover : RagdollActionType.StartRecover
            };
            
            m_PendingRagdollRequests[GetPendingKey(request.CorrelationId, request.RequestId)] = new PendingRagdollRequest
            {
                Request = request,
                SentTime = Time.time,
                Callback = callback
            };
            
            m_Stats.RagdollRequestsSent++;
            SendRagdollRequestToServer?.Invoke(request);
            OnRagdollRequestSent?.Invoke(request);
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // RAGDOLL - SERVER METHODS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// [Server] Process ragdoll request from client.
        /// </summary>
        public void ProcessRagdollRequest(uint senderNetworkId, NetworkRagdollRequest request)
        {
            if (!m_IsServer) return;
            
            m_Stats.RagdollRequestsReceived++;
            OnRagdollRequestReceived?.Invoke(senderNetworkId, request);
            
            var character = GetCharacterByNetworkId?.Invoke(request.CharacterNetworkId);
            if (character == null)
            {
                SendRagdollResponse(senderNetworkId, request.RequestId, false, RagdollRejectReason.CharacterNotFound, request.ActorNetworkId, request.CorrelationId);
                return;
            }
            
            // Check cooldown
            float currentTime = GetServerTime?.Invoke() ?? Time.time;
            if (m_RagdollCooldowns.TryGetValue(request.CharacterNetworkId, out float cooldownEnd) && currentTime < cooldownEnd)
            {
                SendRagdollResponse(senderNetworkId, request.RequestId, false, RagdollRejectReason.Cooldown, request.ActorNetworkId, request.CorrelationId);
                return;
            }
            
            // Validate based on action type
            bool canPerform = false;
            RagdollRejectReason rejectReason = RagdollRejectReason.None;
            
            switch (request.ActionType)
            {
                case RagdollActionType.StartRagdoll:
                case RagdollActionType.StartRagdollWithForce:
                    canPerform = !character.Ragdoll.IsRagdoll;
                    rejectReason = canPerform ? RagdollRejectReason.None : RagdollRejectReason.AlreadyRagdoll;
                    break;
                    
                case RagdollActionType.StartRecover:
                case RagdollActionType.InstantRecover:
                    canPerform = character.Ragdoll.IsRagdoll;
                    rejectReason = canPerform ? RagdollRejectReason.None : RagdollRejectReason.NotRagdoll;
                    break;
            }
            
            if (!canPerform)
            {
                SendRagdollResponse(senderNetworkId, request.RequestId, false, rejectReason, request.ActorNetworkId, request.CorrelationId);
                m_Stats.RagdollRejected++;
                return;
            }
            
            // Apply ragdoll action
            ApplyRagdollAction(character, request);
            
            // Update cooldown
            m_RagdollCooldowns[request.CharacterNetworkId] = currentTime + m_RagdollCooldown;
            
            // Send response
            SendRagdollResponse(senderNetworkId, request.RequestId, true, RagdollRejectReason.None, request.ActorNetworkId, request.CorrelationId);
            m_Stats.RagdollApproved++;
            
            // Broadcast to all clients
            var broadcast = new NetworkRagdollBroadcast
            {
                CharacterNetworkId = request.CharacterNetworkId,
                ActionType = request.ActionType,
                ServerTime = currentTime,
                Force = request.Force,
                ForcePoint = request.ForcePoint
            };
            
            BroadcastRagdollToClients?.Invoke(broadcast);
        }
        
        private void ApplyRagdollAction(Character character, NetworkRagdollRequest request)
        {
            switch (request.ActionType)
            {
                case RagdollActionType.StartRagdoll:
                    _ = character.Ragdoll.StartRagdoll(); // Fire-and-forget async
                    break;
                    
                case RagdollActionType.StartRagdollWithForce:
                    _ = ApplyRagdollWithForceAsync(character, request.Force, request.ForcePoint);
                    break;
                    
                case RagdollActionType.StartRecover:
                    _ = character.Ragdoll.StartRecover(); // Fire-and-forget async
                    break;
                    
                case RagdollActionType.InstantRecover:
                    // Force immediate recovery
                    _ = character.Ragdoll.StartRecover(); // Fire-and-forget async
                    break;
            }
        }
        
        private async System.Threading.Tasks.Task ApplyRagdollWithForceAsync(Character character, Vector3 force, Vector3 forcePoint)
        {
            await character.Ragdoll.StartRagdoll();
            
            // Apply force after ragdoll starts
            if (force != Vector3.zero)
            {
                // Small delay to let ragdoll physics activate
                await System.Threading.Tasks.Task.Yield();
                
                var rigidbodies = character.GetComponentsInChildren<Rigidbody>();
                foreach (var rb in rigidbodies)
                {
                    rb.AddForceAtPosition(force, forcePoint, ForceMode.Impulse);
                }
            }
        }
        
        private void SendRagdollResponse(uint clientId, ushort requestId, bool approved, RagdollRejectReason reason,
            uint actorNetworkId = 0, uint correlationId = 0)
        {
            var response = new NetworkRagdollResponse
            {
                RequestId = requestId,
                ActorNetworkId = actorNetworkId,
                CorrelationId = correlationId,
                Approved = approved,
                RejectReason = reason
            };
            
            SendRagdollResponseToClient?.Invoke(clientId, response);
        }
        
        /// <summary>
        /// [Client] Handle ragdoll response from server.
        /// </summary>
        public void ReceiveRagdollResponse(NetworkRagdollResponse response)
        {
            if (!m_IsClient) return;
            
            uint pendingKey = GetPendingKey(response.CorrelationId, response.RequestId);
            if (m_PendingRagdollRequests.TryGetValue(pendingKey, out var pending))
            {
                m_PendingRagdollRequests.Remove(pendingKey);
                pending.Callback?.Invoke(response);
            }
            
            OnRagdollResponseReceived?.Invoke(response);
        }
        
        /// <summary>
        /// [Client] Handle ragdoll broadcast from server.
        /// </summary>
        public void ReceiveRagdollBroadcast(NetworkRagdollBroadcast broadcast)
        {
            var character = GetCharacterByNetworkId?.Invoke(broadcast.CharacterNetworkId);
            if (character == null) return;
            
            // Skip if we're the server (already applied)
            if (m_IsServer) return;
            
            var request = new NetworkRagdollRequest
            {
                ActionType = broadcast.ActionType,
                Force = broadcast.Force,
                ForcePoint = broadcast.ForcePoint
            };
            
            ApplyRagdollAction(character, request);
            OnRagdollBroadcastReceived?.Invoke(broadcast);
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // PROPS - CLIENT METHODS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// [Client] Request to attach a prop prefab.
        /// </summary>
        public void RequestAttachProp(uint characterNetworkId, int propHash, int boneHash,
            Vector3 localPosition = default, Quaternion localRotation = default,
            Action<NetworkPropResponse> callback = null)
        {
            if (!m_IsClient) return;
            
            var request = new NetworkPropRequest
            {
                RequestId = m_NextRequestId++,
                ActorNetworkId = characterNetworkId,
                CorrelationId = NetworkCorrelation.Compose(characterNetworkId, (ushort)(m_NextRequestId - 1)),
                CharacterNetworkId = characterNetworkId,
                ActionType = PropActionType.AttachPrefab,
                PropHash = propHash,
                BoneHash = boneHash,
                LocalPosition = localPosition
            };
            request.SetLocalRotation(localRotation == default ? Quaternion.identity : localRotation);
            
            m_PendingPropRequests[GetPendingKey(request.CorrelationId, request.RequestId)] = new PendingPropRequest
            {
                Request = request,
                SentTime = Time.time,
                Callback = callback
            };
            
            m_Stats.PropRequestsSent++;
            SendPropRequestToServer?.Invoke(request);
            OnPropRequestSent?.Invoke(request);
        }
        
        /// <summary>
        /// [Client] Request to detach a prop.
        /// </summary>
        public void RequestDetachProp(uint characterNetworkId, int propHash,
            Action<NetworkPropResponse> callback = null)
        {
            if (!m_IsClient) return;
            
            var request = new NetworkPropRequest
            {
                RequestId = m_NextRequestId++,
                ActorNetworkId = characterNetworkId,
                CorrelationId = NetworkCorrelation.Compose(characterNetworkId, (ushort)(m_NextRequestId - 1)),
                CharacterNetworkId = characterNetworkId,
                ActionType = PropActionType.DetachPrefab,
                PropHash = propHash
            };
            
            m_PendingPropRequests[GetPendingKey(request.CorrelationId, request.RequestId)] = new PendingPropRequest
            {
                Request = request,
                SentTime = Time.time,
                Callback = callback
            };
            
            m_Stats.PropRequestsSent++;
            SendPropRequestToServer?.Invoke(request);
            OnPropRequestSent?.Invoke(request);
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // PROPS - SERVER METHODS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// [Server] Process prop request from client.
        /// </summary>
        public void ProcessPropRequest(uint senderNetworkId, NetworkPropRequest request)
        {
            if (!m_IsServer) return;
            
            m_Stats.PropRequestsReceived++;
            OnPropRequestReceived?.Invoke(senderNetworkId, request);
            
            var character = GetCharacterByNetworkId?.Invoke(request.CharacterNetworkId);
            if (character == null)
            {
                SendPropResponse(senderNetworkId, request.RequestId, false, PropRejectReason.CharacterNotFound, 0, request.ActorNetworkId, request.CorrelationId);
                return;
            }
            
            // Check max props
            if (!m_CharacterProps.TryGetValue(request.CharacterNetworkId, out var propList))
            {
                propList = new List<int>();
                m_CharacterProps[request.CharacterNetworkId] = propList;
            }
            
            PropRejectReason rejectReason = PropRejectReason.None;
            int propInstanceId = 0;
            
            switch (request.ActionType)
            {
                case PropActionType.AttachPrefab:
                case PropActionType.AttachInstance:
                    if (propList.Count >= m_MaxPropsPerCharacter)
                    {
                        rejectReason = PropRejectReason.MaxPropsReached;
                    }
                    else
                    {
                        propInstanceId = m_NextPropInstanceId++;
                        propList.Add(propInstanceId);
                        
                        // Apply prop attachment
                        ApplyPropAttach(character, request, propInstanceId);
                    }
                    break;
                    
                case PropActionType.DetachPrefab:
                case PropActionType.DetachInstance:
                    // Find and remove prop
                    // For simplicity, using propHash as lookup
                    ApplyPropDetach(character, request);
                    break;
                    
                case PropActionType.DetachAll:
                    propList.Clear();
                    // Detach all props from character
                    break;
            }
            
            if (rejectReason != PropRejectReason.None)
            {
                SendPropResponse(senderNetworkId, request.RequestId, false, rejectReason, 0, request.ActorNetworkId, request.CorrelationId);
                m_Stats.PropRejected++;
                return;
            }
            
            // Send response
            SendPropResponse(senderNetworkId, request.RequestId, true, PropRejectReason.None, propInstanceId, request.ActorNetworkId, request.CorrelationId);
            m_Stats.PropApproved++;
            
            // Broadcast
            var broadcast = new NetworkPropBroadcast
            {
                CharacterNetworkId = request.CharacterNetworkId,
                ActionType = request.ActionType,
                PropHash = request.PropHash,
                BoneHash = request.BoneHash,
                PropInstanceId = propInstanceId,
                LocalPosition = request.LocalPosition,
                RotationX = request.RotationX,
                RotationY = request.RotationY,
                RotationZ = request.RotationZ
            };
            
            BroadcastPropToClients?.Invoke(broadcast);
        }
        
        private void ApplyPropAttach(Character character, NetworkPropRequest request, int instanceId)
        {
            var prefab = GetPropPrefabByHash?.Invoke(request.PropHash);
            if (prefab == null) return;
            
            // GC2 Props system uses bone names, we need to resolve bone
            // This is a simplified implementation - full impl would use GC2's Props.AttachPrefab
            var bone = GetBoneByHash?.Invoke(request.BoneHash);
            if (bone == null) bone = character.transform;
            
            var instance = Instantiate(prefab, bone);
            instance.transform.localPosition = request.LocalPosition;
            instance.transform.localRotation = request.GetLocalRotation();
            
            // Tag with instance ID for later removal
            var tracker = instance.AddComponent<NetworkPropTracker>();
            tracker.InstanceId = instanceId;
            tracker.PropHash = request.PropHash;
        }
        
        private void ApplyPropDetach(Character character, NetworkPropRequest request)
        {
            var trackers = character.GetComponentsInChildren<NetworkPropTracker>();
            foreach (var tracker in trackers)
            {
                if (tracker.PropHash == request.PropHash)
                {
                    Destroy(tracker.gameObject);
                    break;
                }
            }
        }
        
        private void SendPropResponse(uint clientId, ushort requestId, bool approved, PropRejectReason reason, int instanceId,
            uint actorNetworkId = 0, uint correlationId = 0)
        {
            var response = new NetworkPropResponse
            {
                RequestId = requestId,
                ActorNetworkId = actorNetworkId,
                CorrelationId = correlationId,
                Approved = approved,
                RejectReason = reason,
                PropInstanceId = instanceId
            };
            
            SendPropResponseToClient?.Invoke(clientId, response);
        }
        
        /// <summary>
        /// [Client] Handle prop response from server.
        /// </summary>
        public void ReceivePropResponse(NetworkPropResponse response)
        {
            if (!m_IsClient) return;
            
            uint pendingKey = GetPendingKey(response.CorrelationId, response.RequestId);
            if (m_PendingPropRequests.TryGetValue(pendingKey, out var pending))
            {
                m_PendingPropRequests.Remove(pendingKey);
                pending.Callback?.Invoke(response);
            }
            
            OnPropResponseReceived?.Invoke(response);
        }
        
        /// <summary>
        /// [Client] Handle prop broadcast from server.
        /// </summary>
        public void ReceivePropBroadcast(NetworkPropBroadcast broadcast)
        {
            if (m_IsServer) return;
            
            var character = GetCharacterByNetworkId?.Invoke(broadcast.CharacterNetworkId);
            if (character == null) return;
            
            var request = new NetworkPropRequest
            {
                ActionType = broadcast.ActionType,
                PropHash = broadcast.PropHash,
                BoneHash = broadcast.BoneHash,
                LocalPosition = broadcast.LocalPosition,
                RotationX = broadcast.RotationX,
                RotationY = broadcast.RotationY,
                RotationZ = broadcast.RotationZ
            };
            
            switch (broadcast.ActionType)
            {
                case PropActionType.AttachPrefab:
                case PropActionType.AttachInstance:
                    ApplyPropAttach(character, request, broadcast.PropInstanceId);
                    break;
                    
                case PropActionType.DetachPrefab:
                case PropActionType.DetachInstance:
                    ApplyPropDetach(character, request);
                    break;
            }
            
            OnPropBroadcastReceived?.Invoke(broadcast);
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // INVINCIBILITY - CLIENT METHODS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// [Client] Request to set invincibility.
        /// </summary>
        public void RequestSetInvincibility(uint characterNetworkId, float duration,
            Action<NetworkInvincibilityResponse> callback = null)
        {
            if (!m_IsClient) return;
            
            var request = new NetworkInvincibilityRequest
            {
                RequestId = m_NextRequestId++,
                ActorNetworkId = characterNetworkId,
                CorrelationId = NetworkCorrelation.Compose(characterNetworkId, (ushort)(m_NextRequestId - 1)),
                CharacterNetworkId = characterNetworkId,
                Duration = duration,
                ClientTime = GetServerTime?.Invoke() ?? Time.time
            };
            
            m_PendingInvincibilityRequests[GetPendingKey(request.CorrelationId, request.RequestId)] = new PendingInvincibilityRequest
            {
                Request = request,
                SentTime = Time.time,
                Callback = callback
            };
            
            m_Stats.InvincibilityRequestsSent++;
            SendInvincibilityRequestToServer?.Invoke(request);
            OnInvincibilityRequestSent?.Invoke(request);
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // INVINCIBILITY - SERVER METHODS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// [Server] Process invincibility request from client.
        /// </summary>
        public void ProcessInvincibilityRequest(uint senderNetworkId, NetworkInvincibilityRequest request)
        {
            if (!m_IsServer) return;
            
            m_Stats.InvincibilityRequestsReceived++;
            OnInvincibilityRequestReceived?.Invoke(senderNetworkId, request);
            
            var character = GetCharacterByNetworkId?.Invoke(request.CharacterNetworkId);
            if (character == null)
            {
                SendInvincibilityResponse(senderNetworkId, request.RequestId, false, 
                    InvincibilityRejectReason.CharacterNotFound, 0, request.ActorNetworkId, request.CorrelationId);
                return;
            }
            
            float currentTime = GetServerTime?.Invoke() ?? Time.time;
            
            // Check cooldown
            if (m_InvincibilityCooldowns.TryGetValue(request.CharacterNetworkId, out float cooldownEnd) 
                && currentTime < cooldownEnd)
            {
                SendInvincibilityResponse(senderNetworkId, request.RequestId, false,
                    InvincibilityRejectReason.OnCooldown, 0, request.ActorNetworkId, request.CorrelationId);
                m_Stats.InvincibilityRejected++;
                return;
            }
            
            // Validate duration
            float approvedDuration = Mathf.Clamp(request.Duration, 0, m_MaxInvincibilityDuration);
            if (approvedDuration <= 0)
            {
                // Cancelling invincibility
                if (!character.Combat.Invincibility.IsInvincible)
                {
                    SendInvincibilityResponse(senderNetworkId, request.RequestId, false,
                        InvincibilityRejectReason.NotInvincible, 0, request.ActorNetworkId, request.CorrelationId);
                    m_Stats.InvincibilityRejected++;
                    return;
                }
            }
            
            // Apply invincibility
            character.Combat.Invincibility.Set(approvedDuration);
            
            // Update cooldown
            m_InvincibilityCooldowns[request.CharacterNetworkId] = currentTime + approvedDuration + m_InvincibilityCooldown;
            
            // Send response
            SendInvincibilityResponse(senderNetworkId, request.RequestId, true,
                InvincibilityRejectReason.None, approvedDuration, request.ActorNetworkId, request.CorrelationId);
            m_Stats.InvincibilityApproved++;
            
            // Broadcast
            var broadcast = new NetworkInvincibilityBroadcast
            {
                CharacterNetworkId = request.CharacterNetworkId,
                IsInvincible = approvedDuration > 0,
                StartTime = currentTime,
                Duration = approvedDuration
            };
            
            BroadcastInvincibilityToClients?.Invoke(broadcast);
        }
        
        private void SendInvincibilityResponse(uint clientId, ushort requestId, bool approved,
            InvincibilityRejectReason reason, float duration, uint actorNetworkId = 0, uint correlationId = 0)
        {
            var response = new NetworkInvincibilityResponse
            {
                RequestId = requestId,
                ActorNetworkId = actorNetworkId,
                CorrelationId = correlationId,
                Approved = approved,
                RejectReason = reason,
                ApprovedDuration = duration
            };
            
            SendInvincibilityResponseToClient?.Invoke(clientId, response);
        }
        
        /// <summary>
        /// [Client] Handle invincibility response from server.
        /// </summary>
        public void ReceiveInvincibilityResponse(NetworkInvincibilityResponse response)
        {
            if (!m_IsClient) return;
            
            uint pendingKey = GetPendingKey(response.CorrelationId, response.RequestId);
            if (m_PendingInvincibilityRequests.TryGetValue(pendingKey, out var pending))
            {
                m_PendingInvincibilityRequests.Remove(pendingKey);
                pending.Callback?.Invoke(response);
            }
            
            OnInvincibilityResponseReceived?.Invoke(response);
        }
        
        /// <summary>
        /// [Client] Handle invincibility broadcast from server.
        /// </summary>
        public void ReceiveInvincibilityBroadcast(NetworkInvincibilityBroadcast broadcast)
        {
            if (m_IsServer) return;
            
            var character = GetCharacterByNetworkId?.Invoke(broadcast.CharacterNetworkId);
            if (character == null) return;
            
            character.Combat.Invincibility.Set(broadcast.Duration);
            OnInvincibilityBroadcastReceived?.Invoke(broadcast);
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // POISE - CLIENT METHODS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// [Client] Request to damage poise.
        /// </summary>
        public void RequestPoiseDamage(uint characterNetworkId, float damage,
            Action<NetworkPoiseResponse> callback = null)
        {
            if (!m_IsClient) return;
            
            var request = new NetworkPoiseRequest
            {
                RequestId = m_NextRequestId++,
                ActorNetworkId = characterNetworkId,
                CorrelationId = NetworkCorrelation.Compose(characterNetworkId, (ushort)(m_NextRequestId - 1)),
                CharacterNetworkId = characterNetworkId,
                ActionType = PoiseActionType.Damage,
                Value = damage,
                ClientTime = GetServerTime?.Invoke() ?? Time.time
            };
            
            m_PendingPoiseRequests[GetPendingKey(request.CorrelationId, request.RequestId)] = new PendingPoiseRequest
            {
                Request = request,
                SentTime = Time.time,
                Callback = callback
            };
            
            m_Stats.PoiseRequestsSent++;
            SendPoiseRequestToServer?.Invoke(request);
            OnPoiseRequestSent?.Invoke(request);
        }
        
        /// <summary>
        /// [Client] Request to reset poise.
        /// </summary>
        public void RequestPoiseReset(uint characterNetworkId, float value = -1,
            Action<NetworkPoiseResponse> callback = null)
        {
            if (!m_IsClient) return;
            
            var request = new NetworkPoiseRequest
            {
                RequestId = m_NextRequestId++,
                ActorNetworkId = characterNetworkId,
                CorrelationId = NetworkCorrelation.Compose(characterNetworkId, (ushort)(m_NextRequestId - 1)),
                CharacterNetworkId = characterNetworkId,
                ActionType = value < 0 ? PoiseActionType.Reset : PoiseActionType.Set,
                Value = value,
                ClientTime = GetServerTime?.Invoke() ?? Time.time
            };
            
            m_PendingPoiseRequests[GetPendingKey(request.CorrelationId, request.RequestId)] = new PendingPoiseRequest
            {
                Request = request,
                SentTime = Time.time,
                Callback = callback
            };
            
            m_Stats.PoiseRequestsSent++;
            SendPoiseRequestToServer?.Invoke(request);
            OnPoiseRequestSent?.Invoke(request);
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // POISE - SERVER METHODS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// [Server] Process poise request from client.
        /// </summary>
        public void ProcessPoiseRequest(uint senderNetworkId, NetworkPoiseRequest request)
        {
            if (!m_IsServer) return;
            
            m_Stats.PoiseRequestsReceived++;
            OnPoiseRequestReceived?.Invoke(senderNetworkId, request);
            
            var character = GetCharacterByNetworkId?.Invoke(request.CharacterNetworkId);
            if (character == null)
            {
                SendPoiseResponse(senderNetworkId, request.RequestId, false,
                    PoiseRejectReason.CharacterNotFound, 0, false, request.ActorNetworkId, request.CorrelationId);
                return;
            }
            
            var poise = character.Combat.Poise;
            float currentTime = GetServerTime?.Invoke() ?? Time.time;
            
            // Apply poise action
            switch (request.ActionType)
            {
                case PoiseActionType.Damage:
                    if (m_ValidatePoiseDamage && request.Value < 0)
                    {
                        SendPoiseResponse(senderNetworkId, request.RequestId, false,
                            PoiseRejectReason.InvalidValue, poise.Current, poise.IsBroken, request.ActorNetworkId, request.CorrelationId);
                        m_Stats.PoiseRejected++;
                        return;
                    }
                    poise.Damage(request.Value);
                    break;
                    
                case PoiseActionType.Set:
                    poise.Set(request.Value);
                    break;
                    
                case PoiseActionType.Reset:
                    poise.Reset(poise.Maximum);
                    break;
                    
                case PoiseActionType.Add:
                    poise.Set(poise.Current + request.Value);
                    break;
            }
            
            // Send response
            SendPoiseResponse(senderNetworkId, request.RequestId, true,
                PoiseRejectReason.None, poise.Current, poise.IsBroken, request.ActorNetworkId, request.CorrelationId);
            m_Stats.PoiseApproved++;
            
            // Broadcast
            var broadcast = new NetworkPoiseBroadcast
            {
                CharacterNetworkId = request.CharacterNetworkId,
                CurrentPoise = poise.Current,
                MaximumPoise = poise.Maximum,
                IsBroken = poise.IsBroken,
                ServerTime = currentTime
            };
            
            BroadcastPoiseToClients?.Invoke(broadcast);
        }
        
        private void SendPoiseResponse(uint clientId, ushort requestId, bool approved,
            PoiseRejectReason reason, float currentPoise, bool isBroken, uint actorNetworkId = 0, uint correlationId = 0)
        {
            var response = new NetworkPoiseResponse
            {
                RequestId = requestId,
                ActorNetworkId = actorNetworkId,
                CorrelationId = correlationId,
                Approved = approved,
                RejectReason = reason,
                CurrentPoise = currentPoise,
                IsBroken = isBroken
            };
            
            SendPoiseResponseToClient?.Invoke(clientId, response);
        }
        
        /// <summary>
        /// [Client] Handle poise response from server.
        /// </summary>
        public void ReceivePoiseResponse(NetworkPoiseResponse response)
        {
            if (!m_IsClient) return;
            
            uint pendingKey = GetPendingKey(response.CorrelationId, response.RequestId);
            if (m_PendingPoiseRequests.TryGetValue(pendingKey, out var pending))
            {
                m_PendingPoiseRequests.Remove(pendingKey);
                pending.Callback?.Invoke(response);
            }
            
            OnPoiseResponseReceived?.Invoke(response);
        }
        
        /// <summary>
        /// [Client] Handle poise broadcast from server.
        /// </summary>
        public void ReceivePoiseBroadcast(NetworkPoiseBroadcast broadcast)
        {
            if (m_IsServer) return;
            
            var character = GetCharacterByNetworkId?.Invoke(broadcast.CharacterNetworkId);
            if (character == null) return;
            
            var poise = character.Combat.Poise;
            poise.Set(broadcast.CurrentPoise);
            
            OnPoiseBroadcastReceived?.Invoke(broadcast);
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // BUSY - CLIENT METHODS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// [Client] Request to set busy state on limbs.
        /// </summary>
        public void RequestSetBusy(uint characterNetworkId, BusyLimbs limbs, bool setBusy,
            float timeout = 0, Action<NetworkBusyResponse> callback = null)
        {
            if (!m_IsClient) return;
            
            var request = new NetworkBusyRequest
            {
                RequestId = m_NextRequestId++,
                ActorNetworkId = characterNetworkId,
                CorrelationId = NetworkCorrelation.Compose(characterNetworkId, (ushort)(m_NextRequestId - 1)),
                CharacterNetworkId = characterNetworkId,
                Limbs = limbs,
                SetBusy = setBusy,
                Timeout = timeout
            };
            
            m_PendingBusyRequests[GetPendingKey(request.CorrelationId, request.RequestId)] = new PendingBusyRequest
            {
                Request = request,
                SentTime = Time.time,
                Callback = callback
            };
            
            SendBusyRequestToServer?.Invoke(request);
            OnBusyRequestSent?.Invoke(request);
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // BUSY - SERVER METHODS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// [Server] Process busy request from client.
        /// </summary>
        public void ProcessBusyRequest(uint senderNetworkId, NetworkBusyRequest request)
        {
            if (!m_IsServer) return;
            
            OnBusyRequestReceived?.Invoke(senderNetworkId, request);
            
            var character = GetCharacterByNetworkId?.Invoke(request.CharacterNetworkId);
            if (character == null)
            {
                SendBusyResponse(senderNetworkId, request.RequestId, false, BusyRejectReason.CharacterNotFound, request.ActorNetworkId, request.CorrelationId);
                return;
            }
            
            var busy = character.Busy;
            
            // Apply busy state using GC2's Busy system
            // Convert our BusyLimbs to GC2's Busy.Limb enum
            GameCreator.Runtime.Characters.Busy.Limb gc2Limbs = 0;
            if ((request.Limbs & BusyLimbs.ArmLeft) != 0) 
                gc2Limbs |= GameCreator.Runtime.Characters.Busy.Limb.ArmLeft;
            if ((request.Limbs & BusyLimbs.ArmRight) != 0) 
                gc2Limbs |= GameCreator.Runtime.Characters.Busy.Limb.ArmRight;
            if ((request.Limbs & BusyLimbs.LegLeft) != 0) 
                gc2Limbs |= GameCreator.Runtime.Characters.Busy.Limb.LegLeft;
            if ((request.Limbs & BusyLimbs.LegRight) != 0) 
                gc2Limbs |= GameCreator.Runtime.Characters.Busy.Limb.LegRight;
            
            if (request.SetBusy)
            {
                if (request.Timeout > 0)
                {
                    _ = busy.Timeout(gc2Limbs, request.Timeout); // Fire-and-forget async
                }
                else
                {
                    busy.AddState(gc2Limbs);
                }
            }
            else
            {
                busy.RemoveState(gc2Limbs);
            }
            
            // Send response
            SendBusyResponse(senderNetworkId, request.RequestId, true, BusyRejectReason.None, request.ActorNetworkId, request.CorrelationId);
            
            // Broadcast
            BusyLimbs currentBusy = 0;
            if (busy.AreArmsBusy) currentBusy |= BusyLimbs.Arms;
            if (busy.AreLegsBusy) currentBusy |= BusyLimbs.Legs;
            
            var broadcast = new NetworkBusyBroadcast
            {
                CharacterNetworkId = request.CharacterNetworkId,
                CurrentBusyLimbs = currentBusy,
                ServerTime = GetServerTime?.Invoke() ?? Time.time
            };
            
            BroadcastBusyToClients?.Invoke(broadcast);
        }
        
        private void SendBusyResponse(uint clientId, ushort requestId, bool approved, BusyRejectReason reason,
            uint actorNetworkId = 0, uint correlationId = 0)
        {
            var response = new NetworkBusyResponse
            {
                RequestId = requestId,
                ActorNetworkId = actorNetworkId,
                CorrelationId = correlationId,
                Approved = approved,
                RejectReason = reason
            };
            
            SendBusyResponseToClient?.Invoke(clientId, response);
        }
        
        /// <summary>
        /// [Client] Handle busy response from server.
        /// </summary>
        public void ReceiveBusyResponse(NetworkBusyResponse response)
        {
            if (!m_IsClient) return;
            
            uint pendingKey = GetPendingKey(response.CorrelationId, response.RequestId);
            if (m_PendingBusyRequests.TryGetValue(pendingKey, out var pending))
            {
                m_PendingBusyRequests.Remove(pendingKey);
                pending.Callback?.Invoke(response);
            }
            
            OnBusyResponseReceived?.Invoke(response);
        }
        
        /// <summary>
        /// [Client] Handle busy broadcast from server.
        /// </summary>
        public void ReceiveBusyBroadcast(NetworkBusyBroadcast broadcast)
        {
            if (m_IsServer) return;
            
            var character = GetCharacterByNetworkId?.Invoke(broadcast.CharacterNetworkId);
            if (character == null) return;
            
            var busy = character.Busy;
            
            // Sync busy state
            GameCreator.Runtime.Characters.Busy.Limb gc2Limbs = 0;
            if ((broadcast.CurrentBusyLimbs & BusyLimbs.ArmLeft) != 0) 
                gc2Limbs |= GameCreator.Runtime.Characters.Busy.Limb.ArmLeft;
            if ((broadcast.CurrentBusyLimbs & BusyLimbs.ArmRight) != 0) 
                gc2Limbs |= GameCreator.Runtime.Characters.Busy.Limb.ArmRight;
            if ((broadcast.CurrentBusyLimbs & BusyLimbs.LegLeft) != 0) 
                gc2Limbs |= GameCreator.Runtime.Characters.Busy.Limb.LegLeft;
            if ((broadcast.CurrentBusyLimbs & BusyLimbs.LegRight) != 0) 
                gc2Limbs |= GameCreator.Runtime.Characters.Busy.Limb.LegRight;
            
            // Clear all then set current
            busy.RemoveState(GameCreator.Runtime.Characters.Busy.Limb.Every);
            if (gc2Limbs != 0)
            {
                busy.AddState(gc2Limbs);
            }
            
            OnBusyBroadcastReceived?.Invoke(broadcast);
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // INTERACTION - CLIENT METHODS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// [Client] Request to interact with a target.
        /// </summary>
        public void RequestInteraction(uint characterNetworkId, uint targetNetworkId, int targetHash,
            Vector3 interactionPosition, Action<NetworkInteractionResponse> callback = null)
        {
            if (!m_IsClient) return;
            
            var request = new NetworkInteractionRequest
            {
                RequestId = m_NextRequestId++,
                ActorNetworkId = characterNetworkId,
                CorrelationId = NetworkCorrelation.Compose(characterNetworkId, (ushort)(m_NextRequestId - 1)),
                CharacterNetworkId = characterNetworkId,
                TargetNetworkId = targetNetworkId,
                TargetHash = targetHash,
                InteractionPosition = interactionPosition,
                ClientTime = GetServerTime?.Invoke() ?? Time.time
            };
            
            m_PendingInteractionRequests[GetPendingKey(request.CorrelationId, request.RequestId)] = new PendingInteractionRequest
            {
                Request = request,
                SentTime = Time.time,
                Callback = callback
            };
            
            m_Stats.InteractionRequestsSent++;
            SendInteractionRequestToServer?.Invoke(request);
            OnInteractionRequestSent?.Invoke(request);
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // INTERACTION - SERVER METHODS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// [Server] Process interaction request from client.
        /// </summary>
        public void ProcessInteractionRequest(uint senderNetworkId, NetworkInteractionRequest request)
        {
            if (!m_IsServer) return;
            
            m_Stats.InteractionRequestsReceived++;
            OnInteractionRequestReceived?.Invoke(senderNetworkId, request);
            
            var character = GetCharacterByNetworkId?.Invoke(request.CharacterNetworkId);
            if (character == null)
            {
                SendInteractionResponse(senderNetworkId, request.RequestId, false,
                    InteractionRejectReason.CharacterNotFound, 0, request.ActorNetworkId, request.CorrelationId);
                return;
            }
            
            float currentTime = GetServerTime?.Invoke() ?? Time.time;
            
            // Check interaction cooldown
            var cooldownKey = (request.CharacterNetworkId, request.TargetNetworkId);
            if (m_InteractionCooldowns.TryGetValue(cooldownKey, out float cooldownEnd) && currentTime < cooldownEnd)
            {
                SendInteractionResponse(senderNetworkId, request.RequestId, false,
                    InteractionRejectReason.OnCooldown, 0, request.ActorNetworkId, request.CorrelationId);
                m_Stats.InteractionRejected++;
                return;
            }
            
            // Validate range
            float distance = Vector3.Distance(character.transform.position, request.InteractionPosition);
            if (distance > m_MaxInteractionRange)
            {
                SendInteractionResponse(senderNetworkId, request.RequestId, false,
                    InteractionRejectReason.OutOfRange, 0, request.ActorNetworkId, request.CorrelationId);
                m_Stats.InteractionRejected++;
                return;
            }
            
            // Check if character can interact
            if (!character.Interaction.CanInteract)
            {
                SendInteractionResponse(senderNetworkId, request.RequestId, false,
                    InteractionRejectReason.CharacterBusy, 0, request.ActorNetworkId, request.CorrelationId);
                m_Stats.InteractionRejected++;
                return;
            }
            
            // Perform interaction
            // Note: Full implementation would resolve target and call character.Interaction.Interact()
            // This is simplified - actual interaction target resolution depends on game implementation
            
            // Update cooldown
            m_InteractionCooldowns[cooldownKey] = currentTime + m_InteractionCooldown;
            
            // Send response
            SendInteractionResponse(senderNetworkId, request.RequestId, true,
                InteractionRejectReason.None, 0, request.ActorNetworkId, request.CorrelationId);
            m_Stats.InteractionApproved++;
            
            // Broadcast
            var broadcast = new NetworkInteractionBroadcast
            {
                CharacterNetworkId = request.CharacterNetworkId,
                TargetNetworkId = request.TargetNetworkId,
                TargetHash = request.TargetHash,
                InteractionType = InteractionType.Generic,
                ServerTime = currentTime
            };
            
            BroadcastInteractionToClients?.Invoke(broadcast);
        }
        
        private void SendInteractionResponse(uint clientId, ushort requestId, bool approved,
            InteractionRejectReason reason, int resultData, uint actorNetworkId = 0, uint correlationId = 0)
        {
            var response = new NetworkInteractionResponse
            {
                RequestId = requestId,
                ActorNetworkId = actorNetworkId,
                CorrelationId = correlationId,
                Approved = approved,
                RejectReason = reason,
                ResultData = resultData
            };
            
            SendInteractionResponseToClient?.Invoke(clientId, response);
        }
        
        /// <summary>
        /// [Client] Handle interaction response from server.
        /// </summary>
        public void ReceiveInteractionResponse(NetworkInteractionResponse response)
        {
            if (!m_IsClient) return;
            
            uint pendingKey = GetPendingKey(response.CorrelationId, response.RequestId);
            if (m_PendingInteractionRequests.TryGetValue(pendingKey, out var pending))
            {
                m_PendingInteractionRequests.Remove(pendingKey);
                pending.Callback?.Invoke(response);
            }
            
            OnInteractionResponseReceived?.Invoke(response);
        }
        
        /// <summary>
        /// [Client] Handle interaction broadcast from server.
        /// </summary>
        public void ReceiveInteractionBroadcast(NetworkInteractionBroadcast broadcast)
        {
            if (m_IsServer) return;
            
            // Interaction effects/animations can be triggered here
            OnInteractionBroadcastReceived?.Invoke(broadcast);
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // SERVER-AUTHORITATIVE DIRECT METHODS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// [Server] Directly start ragdoll and broadcast (bypasses client request).
        /// </summary>
        public void ServerStartRagdoll(uint characterNetworkId, Vector3 force = default, Vector3 forcePoint = default)
        {
            if (!m_IsServer) return;
            
            var character = GetCharacterByNetworkId?.Invoke(characterNetworkId);
            if (character == null) return;
            
            var actionType = force != default ? RagdollActionType.StartRagdollWithForce : RagdollActionType.StartRagdoll;
            
            var request = new NetworkRagdollRequest
            {
                CharacterNetworkId = characterNetworkId,
                ActionType = actionType,
                Force = force,
                ForcePoint = forcePoint
            };
            
            ApplyRagdollAction(character, request);
            
            var broadcast = new NetworkRagdollBroadcast
            {
                CharacterNetworkId = characterNetworkId,
                ActionType = actionType,
                ServerTime = GetServerTime?.Invoke() ?? Time.time,
                Force = force,
                ForcePoint = forcePoint
            };
            
            BroadcastRagdollToClients?.Invoke(broadcast);
        }
        
        /// <summary>
        /// [Server] Directly set invincibility and broadcast.
        /// </summary>
        public void ServerSetInvincibility(uint characterNetworkId, float duration)
        {
            if (!m_IsServer) return;
            
            var character = GetCharacterByNetworkId?.Invoke(characterNetworkId);
            if (character == null) return;
            
            character.Combat.Invincibility.Set(duration);
            
            var broadcast = new NetworkInvincibilityBroadcast
            {
                CharacterNetworkId = characterNetworkId,
                IsInvincible = duration > 0,
                StartTime = GetServerTime?.Invoke() ?? Time.time,
                Duration = duration
            };
            
            BroadcastInvincibilityToClients?.Invoke(broadcast);
        }
        
        /// <summary>
        /// [Server] Directly damage poise and broadcast.
        /// </summary>
        public void ServerDamagePoise(uint characterNetworkId, float damage)
        {
            if (!m_IsServer) return;
            
            var character = GetCharacterByNetworkId?.Invoke(characterNetworkId);
            if (character == null) return;
            
            var poise = character.Combat.Poise;
            poise.Damage(damage);
            
            var broadcast = new NetworkPoiseBroadcast
            {
                CharacterNetworkId = characterNetworkId,
                CurrentPoise = poise.Current,
                MaximumPoise = poise.Maximum,
                IsBroken = poise.IsBroken,
                ServerTime = GetServerTime?.Invoke() ?? Time.time
            };
            
            BroadcastPoiseToClients?.Invoke(broadcast);
        }
        
        /// <summary>
        /// [Server] Directly reset poise and broadcast.
        /// </summary>
        public void ServerResetPoise(uint characterNetworkId)
        {
            if (!m_IsServer) return;
            
            var character = GetCharacterByNetworkId?.Invoke(characterNetworkId);
            if (character == null) return;
            
            var poise = character.Combat.Poise;
            poise.Reset(poise.Maximum);
            
            var broadcast = new NetworkPoiseBroadcast
            {
                CharacterNetworkId = characterNetworkId,
                CurrentPoise = poise.Current,
                MaximumPoise = poise.Maximum,
                IsBroken = poise.IsBroken,
                ServerTime = GetServerTime?.Invoke() ?? Time.time
            };
            
            BroadcastPoiseToClients?.Invoke(broadcast);
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
