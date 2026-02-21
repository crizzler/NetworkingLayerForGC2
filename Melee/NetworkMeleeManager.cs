#if GC2_MELEE
using System;
using System.Collections.Generic;
using UnityEngine;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Common;

#if UNITY_NETCODE
using Unity.Netcode;
#endif

namespace Arawn.GameCreator2.Networking.Melee
{
    /// <summary>
    /// Global manager for network melee combat coordination.
    /// Handles routing messages between clients and server.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Add this component to a NetworkManager or persistent object in your scene.
    /// It manages the routing of hit requests, responses, and broadcasts.
    /// </para>
    /// <para>
    /// <b>Integration:</b>
    /// Hook up the delegate actions to your network transport:
    /// - SendHitRequestToServer: Sends hit request RPC to server
    /// - SendHitResponseToClient: Sends response RPC to specific client
    /// - BroadcastHitToAllClients: Sends broadcast RPC to all clients
    /// </para>
    /// </remarks>
    [AddComponentMenu("Game Creator/Network/Melee/Network Melee Manager")]
    [DefaultExecutionOrder(ApplicationManager.EXECUTION_ORDER_DEFAULT - 10)]
    public class NetworkMeleeManager : MonoBehaviour
    {
        // ════════════════════════════════════════════════════════════════════════════════════════
        // SINGLETON
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        private static NetworkMeleeManager s_Instance;
        
        /// <summary>Global manager instance.</summary>
        public static NetworkMeleeManager Instance => s_Instance;
        
        /// <summary>Whether an instance exists.</summary>
        public static bool HasInstance => s_Instance != null;
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // INSPECTOR
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        [Header("Processing Settings")]
        [Tooltip("Maximum hit requests to process per frame on server.")]
        [SerializeField] private int m_MaxHitsPerFrame = 10;
        
        [Tooltip("Maximum time in the past for hit validation (seconds).")]
        [SerializeField] private float m_MaxRewindTime = 0.5f;
        
        [Tooltip("Extra tolerance for hit validation (meters).")]
        [SerializeField] private float m_HitTolerance = 0.3f;
        
        [Header("Debug")]
        [SerializeField] private bool m_LogHitRequests = false;
        [SerializeField] private bool m_LogHitBroadcasts = false;
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // NETWORK DELEGATES (Connect to your transport)
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// [Client] Assign to send hit requests to server.
        /// </summary>
        public Action<NetworkMeleeHitRequest> SendHitRequestToServer;
        
        /// <summary>
        /// [Server] Assign to send hit responses to a specific client.
        /// uint parameter is the client's network ID.
        /// </summary>
        public Action<uint, NetworkMeleeHitResponse> SendHitResponseToClient;
        
        /// <summary>
        /// [Server] Assign to broadcast hit to all clients.
        /// </summary>
        public Action<NetworkMeleeHitBroadcast> BroadcastHitToAllClients;
        
        /// <summary>
        /// [Client] Assign to send block requests to server.
        /// </summary>
        public Action<NetworkBlockRequest> SendBlockRequestToServer;
        
        /// <summary>
        /// [Server] Assign to send block responses to client.
        /// </summary>
        public Action<uint, NetworkBlockResponse> SendBlockResponseToClient;
        
        /// <summary>
        /// [Server] Assign to broadcast block state to all clients.
        /// </summary>
        public Action<NetworkBlockBroadcast> BroadcastBlockToAllClients;
        
        /// <summary>
        /// [Client] Assign to send skill requests to server.
        /// </summary>
        public Action<NetworkSkillRequest> SendSkillRequestToServer;
        
        /// <summary>
        /// [Server] Assign to send skill responses to client.
        /// </summary>
        public Action<uint, NetworkSkillResponse> SendSkillResponseToClient;
        
        /// <summary>
        /// [Server] Assign to broadcast skill execution to all clients.
        /// </summary>
        public Action<NetworkSkillBroadcast> BroadcastSkillToAllClients;
        
        /// <summary>
        /// [Client] Assign to send charge requests to server.
        /// </summary>
        public Action<NetworkChargeRequest> SendChargeRequestToServer;
        
        /// <summary>
        /// [Server] Assign to send charge responses to client.
        /// </summary>
        public Action<uint, NetworkChargeResponse> SendChargeResponseToClient;
        
        /// <summary>
        /// [Server] Assign to broadcast charge state to all clients.
        /// </summary>
        public Action<NetworkChargeBroadcast> BroadcastChargeToAllClients;
        
        /// <summary>
        /// [Server] Assign to broadcast reaction to all clients.
        /// </summary>
        public Action<NetworkReactionBroadcast> BroadcastReactionToAllClients;
        
        /// <summary>
        /// Assign to get a NetworkCharacter by network ID.
        /// </summary>
        public Func<uint, NetworkCharacter> GetCharacterByNetworkIdFunc;
        
        /// <summary>
        /// Assign to get current network time.
        /// </summary>
        public Func<float> GetNetworkTimeFunc;
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // EVENTS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>Called when any hit request is sent (for logging/analytics).</summary>
        public event Action<NetworkMeleeHitRequest> OnHitRequestSent;
        
        /// <summary>Called when any hit is validated on server.</summary>
        public event Action<NetworkMeleeHitBroadcast> OnHitValidated;
        
        /// <summary>Called when any hit is rejected on server.</summary>
        public event Action<NetworkMeleeHitRequest, MeleeHitRejectionReason> OnHitRejected;
        
        /// <summary>Called when block request is sent.</summary>
        public event Action<NetworkBlockRequest> OnBlockRequestSent;
        
        /// <summary>Called when block is validated.</summary>
        public event Action<NetworkBlockBroadcast> OnBlockValidated;
        
        /// <summary>Called when skill request is sent.</summary>
        public event Action<NetworkSkillRequest> OnSkillRequestSent;
        
        /// <summary>Called when skill is validated.</summary>
        public event Action<NetworkSkillBroadcast> OnSkillValidated;
        
        /// <summary>Called when reaction is broadcast.</summary>
        public event Action<NetworkReactionBroadcast> OnReactionBroadcast;
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // PRIVATE FIELDS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        private bool m_IsServer;
        private bool m_IsClient;
        
        // Controller registry
        private readonly Dictionary<uint, NetworkMeleeController> m_Controllers = new(32);
        
        // Server hit queue
        private readonly Queue<QueuedHitRequest> m_ServerHitQueue = new(64);
        
        // Server block queue
        private readonly Queue<QueuedBlockRequest> m_ServerBlockQueue = new(32);
        
        // Server skill queue
        private readonly Queue<QueuedSkillRequest> m_ServerSkillQueue = new(32);
        
        // Server charge queue
        private readonly Queue<QueuedChargeRequest> m_ServerChargeQueue = new(16);
        
        // Statistics
        private MeleeNetworkStats m_Stats;
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // STRUCTS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        private struct QueuedHitRequest
        {
            public uint ClientNetworkId;
            public NetworkMeleeHitRequest Request;
            public float ReceivedTime;
        }
        
        private struct QueuedBlockRequest
        {
            public uint ClientNetworkId;
            public NetworkBlockRequest Request;
            public float ReceivedTime;
        }
        
        private struct QueuedSkillRequest
        {
            public uint ClientNetworkId;
            public NetworkSkillRequest Request;
            public float ReceivedTime;
        }
        
        private struct QueuedChargeRequest
        {
            public uint ClientNetworkId;
            public NetworkChargeRequest Request;
            public float ReceivedTime;
        }
        
        /// <summary>Network statistics.</summary>
        [Serializable]
        public struct MeleeNetworkStats
        {
            public int HitRequestsSent;
            public int HitRequestsReceived;
            public int HitsValidated;
            public int HitsRejected;
            public int HitBroadcastsSent;
            public int BlockRequestsReceived;
            public int BlocksValidated;
            public int SkillRequestsReceived;
            public int SkillsValidated;
            public int ReactionsBroadcast;
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // PROPERTIES
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        public bool IsServer => m_IsServer;
        public bool IsClient => m_IsClient;
        public MeleeNetworkStats Stats => m_Stats;
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // UNITY LIFECYCLE
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        private void Awake()
        {
            if (s_Instance == null)
            {
                s_Instance = this;
            }
            else if (s_Instance != this)
            {
                Debug.LogWarning("[NetworkMeleeManager] Multiple instances detected. Using first.");
                Destroy(this);
                return;
            }
        }
        
        private void OnDestroy()
        {
            if (s_Instance == this)
            {
                s_Instance = null;
            }
        }
        
        private void Update()
        {
            if (m_IsServer)
            {
                ProcessServerHitQueue();
                ProcessServerBlockQueue();
                ProcessServerSkillQueue();
                ProcessServerChargeQueue();
            }
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // INITIALIZATION
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Initialize the manager with network role.
        /// </summary>
        public void Initialize(bool isServer, bool isClient)
        {
            m_IsServer = isServer;
            m_IsClient = isClient;
            
            Debug.Log($"[NetworkMeleeManager] Initialized - Server: {isServer}, Client: {isClient}");
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // CONTROLLER REGISTRATION
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Register a NetworkMeleeController for a character.
        /// </summary>
        public void RegisterController(uint networkId, NetworkMeleeController controller)
        {
            if (controller == null) return;
            
            m_Controllers[networkId] = controller;
            
            // Subscribe to controller events
            controller.OnHitDetected += OnControllerHitDetected;
            controller.OnBlockRequested += OnControllerBlockRequested;
            controller.OnSkillRequested += OnControllerSkillRequested;
            controller.OnChargeRequested += OnControllerChargeRequested;
        }
        
        /// <summary>
        /// Unregister a controller.
        /// </summary>
        public void UnregisterController(uint networkId)
        {
            if (m_Controllers.TryGetValue(networkId, out var controller))
            {
                controller.OnHitDetected -= OnControllerHitDetected;
                controller.OnBlockRequested -= OnControllerBlockRequested;
                controller.OnSkillRequested -= OnControllerSkillRequested;
                controller.OnChargeRequested -= OnControllerChargeRequested;
                m_Controllers.Remove(networkId);
            }
        }
        
        /// <summary>
        /// Get a NetworkCharacter by network ID.
        /// </summary>
        public NetworkCharacter GetCharacterByNetworkId(uint networkId)
        {
            if (GetCharacterByNetworkIdFunc != null)
            {
                return GetCharacterByNetworkIdFunc(networkId);
            }
            
            // Fallback: search in registered controllers
            if (m_Controllers.TryGetValue(networkId, out var controller))
            {
                return controller.GetComponent<NetworkCharacter>();
            }
            
            return null;
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // CLIENT-SIDE: SENDING REQUESTS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        private void OnControllerHitDetected(NetworkMeleeHitRequest request)
        {
            if (!m_IsClient) return;
            
            if (m_LogHitRequests)
            {
                Debug.Log($"[NetworkMeleeManager] Hit request: Attacker={request.AttackerNetworkId}, " +
                         $"Target={request.TargetNetworkId}, Point={request.HitPoint}");
            }
            
            // Send to server
            SendHitRequestToServer?.Invoke(request);
            m_Stats.HitRequestsSent++;
            
            OnHitRequestSent?.Invoke(request);
        }
        
        private void OnControllerBlockRequested(NetworkBlockRequest request)
        {
            if (!m_IsClient) return;
            
            SendBlockRequestToServer?.Invoke(request);
            OnBlockRequestSent?.Invoke(request);
        }
        
        private void OnControllerSkillRequested(NetworkSkillRequest request)
        {
            if (!m_IsClient) return;
            
            SendSkillRequestToServer?.Invoke(request);
            OnSkillRequestSent?.Invoke(request);
        }
        
        private void OnControllerChargeRequested(NetworkChargeRequest request)
        {
            if (!m_IsClient) return;
            
            SendChargeRequestToServer?.Invoke(request);
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // SERVER-SIDE: RECEIVING & PROCESSING REQUESTS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// [Server] Called when a hit request is received from a client.
        /// </summary>
        public void ReceiveHitRequest(uint clientNetworkId, NetworkMeleeHitRequest request)
        {
            if (!m_IsServer)
            {
                Debug.LogWarning("[NetworkMeleeManager] ReceiveHitRequest called on non-server");
                return;
            }
            
            m_Stats.HitRequestsReceived++;
            
            // Queue for processing
            m_ServerHitQueue.Enqueue(new QueuedHitRequest
            {
                ClientNetworkId = clientNetworkId,
                Request = request,
                ReceivedTime = Time.time
            });
        }
        
        private void ProcessServerHitQueue()
        {
            int processed = 0;
            
            while (m_ServerHitQueue.Count > 0 && processed < m_MaxHitsPerFrame)
            {
                var queued = m_ServerHitQueue.Dequeue();
                ProcessHitRequest(queued);
                processed++;
            }
        }
        
        private void ProcessHitRequest(QueuedHitRequest queued)
        {
            var request = queued.Request;
            
            // Get attacker controller
            NetworkMeleeController attackerController = null;
            if (m_Controllers.TryGetValue(request.AttackerNetworkId, out var ctrl))
            {
                attackerController = ctrl;
            }
            
            NetworkMeleeHitResponse response;
            
            if (attackerController != null)
            {
                // Use controller's validation logic
                response = attackerController.ProcessHitRequest(request, queued.ClientNetworkId);
            }
            else
            {
                // Fallback validation
                response = ValidateHitRequest(request);
            }
            
            // Send response to client
            SendHitResponseToClient?.Invoke(queued.ClientNetworkId, response);
            
            if (response.Validated)
            {
                m_Stats.HitsValidated++;
                
                // ═══════════════════════════════════════════════════════════════════════════════
                // EVALUATE BLOCK ON TARGET
                // ═══════════════════════════════════════════════════════════════════════════════
                
                BlockEvaluationResult blockResult = BlockEvaluationResult.NoBlock;
                
                // Get target controller to evaluate block
                if (m_Controllers.TryGetValue(request.TargetNetworkId, out var targetCtrl))
                {
                    // Calculate attack power (from skill or default)
                    float attackPower = response.Damage; // Use damage as proxy for power
                    
                    blockResult = targetCtrl.EvaluateBlock(
                        request.TargetNetworkId,
                        request.StrikeDirection,
                        attackPower,
                        request.SkillHash
                    );
                }
                
                // Determine final damage based on block result
                float finalDamage = response.Damage;
                switch (blockResult.Result)
                {
                    case NetworkBlockResult.Parried:
                        finalDamage = 0f; // No damage on parry
                        break;
                    case NetworkBlockResult.Blocked:
                        finalDamage = 0f; // No damage on block (shield absorbed it)
                        break;
                    case NetworkBlockResult.BlockBroken:
                        finalDamage *= 0.5f; // Partial damage on break
                        break;
                }
                
                // Apply damage on server (only if not fully blocked)
                if (finalDamage > 0f)
                {
                    ApplyDamageOnServer(request, finalDamage);
                }
                
                // ═══════════════════════════════════════════════════════════════════════════════
                // BROADCAST HIT RESULT
                // ═══════════════════════════════════════════════════════════════════════════════
                
                var broadcast = new NetworkMeleeHitBroadcast
                {
                    AttackerNetworkId = request.AttackerNetworkId,
                    TargetNetworkId = request.TargetNetworkId,
                    HitPoint = request.HitPoint,
                    StrikeDirection = request.StrikeDirection,
                    SkillHash = request.SkillHash,
                    BlockResult = (byte)blockResult.Result,
                    PoiseBroken = response.PoiseBroken
                };
                
                BroadcastHitToAllClients?.Invoke(broadcast);
                m_Stats.HitBroadcastsSent++;
                
                // ═══════════════════════════════════════════════════════════════════════════════
                // BROADCAST REACTION IF NEEDED
                // ═══════════════════════════════════════════════════════════════════════════════
                
                if (blockResult.TriggerReaction && attackerController != null)
                {
                    // Create and broadcast reaction
                    var reactionBroadcast = attackerController.CreateReactionBroadcast(
                        request.TargetNetworkId,
                        request.AttackerNetworkId,
                        request.StrikeDirection,
                        response.Damage / 10f, // Normalize power
                        null // Let target pick appropriate reaction
                    );
                    
                    BroadcastReaction(reactionBroadcast);
                }
                
                if (m_LogHitBroadcasts)
                {
                    string blockStr = blockResult.Result != NetworkBlockResult.None 
                        ? $" (Block: {blockResult.Result})" 
                        : "";
                    Debug.Log($"[NetworkMeleeManager] Hit broadcast: {request.AttackerNetworkId} -> {request.TargetNetworkId}{blockStr}");
                }
                
                OnHitValidated?.Invoke(broadcast);
            }
            else
            {
                m_Stats.HitsRejected++;
                
                if (m_LogHitRequests)
                {
                    Debug.Log($"[NetworkMeleeManager] Hit rejected: {response.RejectionReason}");
                }
                
                OnHitRejected?.Invoke(request, response.RejectionReason);
            }
        }
        
        private NetworkMeleeHitResponse ValidateHitRequest(NetworkMeleeHitRequest request)
        {
            // Basic validation without controller
            
            // Check timestamp
            float networkTime = GetNetworkTimeFunc?.Invoke() ?? Time.time;
            float age = networkTime - request.ClientTimestamp;
            
            if (age > m_MaxRewindTime)
            {
                return new NetworkMeleeHitResponse
                {
                    RequestId = request.RequestId,
                    Validated = false,
                    RejectionReason = MeleeHitRejectionReason.TimestampTooOld
                };
            }
            
            // Check target exists
            var targetNetworkChar = GetCharacterByNetworkId(request.TargetNetworkId);
            if (targetNetworkChar == null)
            {
                return new NetworkMeleeHitResponse
                {
                    RequestId = request.RequestId,
                    Validated = false,
                    RejectionReason = MeleeHitRejectionReason.TargetNotFound
                };
            }
            
            var targetCharacter = targetNetworkChar.GetComponent<Character>();
            if (targetCharacter == null || targetCharacter.IsDead)
            {
                return new NetworkMeleeHitResponse
                {
                    RequestId = request.RequestId,
                    Validated = false,
                    RejectionReason = MeleeHitRejectionReason.TargetNotFound
                };
            }
            
            // Check invincibility
            if (targetCharacter.Combat.Invincibility.IsInvincible)
            {
                return new NetworkMeleeHitResponse
                {
                    RequestId = request.RequestId,
                    Validated = false,
                    RejectionReason = MeleeHitRejectionReason.TargetInvincible
                };
            }
            
            // Check dodge
            if (targetCharacter.Dash != null && targetCharacter.Dash.IsDodge)
            {
                return new NetworkMeleeHitResponse
                {
                    RequestId = request.RequestId,
                    Validated = false,
                    RejectionReason = MeleeHitRejectionReason.TargetDodged
                };
            }
            
            // TODO: Range/position validation with lag compensation
            
            // Valid hit
            return new NetworkMeleeHitResponse
            {
                RequestId = request.RequestId,
                Validated = true,
                RejectionReason = MeleeHitRejectionReason.None,
                Damage = 10f, // TODO: Calculate from skill
                PoiseBroken = false
            };
        }
        
        private void ApplyDamageOnServer(NetworkMeleeHitRequest request, float damage)
        {
            // Get target character
            var targetNetworkChar = GetCharacterByNetworkId(request.TargetNetworkId);
            if (targetNetworkChar == null) return;
            
            var targetCharacter = targetNetworkChar.GetComponent<Character>();
            if (targetCharacter == null) return;
            
            // TODO: Apply actual damage using GC2 stats system
            // This depends on your stats integration
            Debug.Log($"[NetworkMeleeManager] Server applying {damage} damage to {targetCharacter.name}");
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // SERVER-SIDE: BLOCK REQUEST PROCESSING
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// [Server] Called when a block request is received from a client.
        /// </summary>
        public void ReceiveBlockRequest(uint clientNetworkId, NetworkBlockRequest request)
        {
            if (!m_IsServer) return;
            
            m_Stats.BlockRequestsReceived++;
            
            m_ServerBlockQueue.Enqueue(new QueuedBlockRequest
            {
                ClientNetworkId = clientNetworkId,
                Request = request,
                ReceivedTime = Time.time
            });
        }
        
        private void ProcessServerBlockQueue()
        {
            while (m_ServerBlockQueue.Count > 0)
            {
                var queued = m_ServerBlockQueue.Dequeue();
                ProcessBlockRequest(queued);
            }
        }
        
        private void ProcessBlockRequest(QueuedBlockRequest queued)
        {
            var request = queued.Request;
            
            // Find character's controller
            if (!m_Controllers.TryGetValue(queued.ClientNetworkId, out var controller))
            {
                SendBlockResponseToClient?.Invoke(queued.ClientNetworkId, new NetworkBlockResponse
                {
                    RequestId = request.RequestId,
                    Validated = false,
                    RejectionReason = BlockRejectionReason.InvalidState
                });
                return;
            }
            
            // Process request
            var response = controller.ProcessBlockRequest(request, queued.ClientNetworkId);
            
            // Send response to client
            SendBlockResponseToClient?.Invoke(queued.ClientNetworkId, response);
            
            if (response.Validated)
            {
                m_Stats.BlocksValidated++;
                
                // Broadcast block state to all clients
                var broadcast = new NetworkBlockBroadcast
                {
                    CharacterNetworkId = queued.ClientNetworkId,
                    Action = request.Action,
                    ServerTimestamp = response.ServerBlockStartTime,
                    ShieldHash = request.ShieldHash
                };
                
                BroadcastBlockToAllClients?.Invoke(broadcast);
                OnBlockValidated?.Invoke(broadcast);
            }
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // SERVER-SIDE: SKILL REQUEST PROCESSING
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// [Server] Called when a skill request is received from a client.
        /// </summary>
        public void ReceiveSkillRequest(uint clientNetworkId, NetworkSkillRequest request)
        {
            if (!m_IsServer) return;
            
            m_Stats.SkillRequestsReceived++;
            
            m_ServerSkillQueue.Enqueue(new QueuedSkillRequest
            {
                ClientNetworkId = clientNetworkId,
                Request = request,
                ReceivedTime = Time.time
            });
        }
        
        private void ProcessServerSkillQueue()
        {
            while (m_ServerSkillQueue.Count > 0)
            {
                var queued = m_ServerSkillQueue.Dequeue();
                ProcessSkillRequest(queued);
            }
        }
        
        private void ProcessSkillRequest(QueuedSkillRequest queued)
        {
            var request = queued.Request;
            
            // Find character's controller
            if (!m_Controllers.TryGetValue(queued.ClientNetworkId, out var controller))
            {
                SendSkillResponseToClient?.Invoke(queued.ClientNetworkId, new NetworkSkillResponse
                {
                    RequestId = (ushort)request.InputKey,
                    Validated = false,
                    RejectionReason = SkillRejectionReason.CheatSuspected
                });
                return;
            }
            
            // Process request
            var response = controller.ProcessSkillRequest(request, queued.ClientNetworkId);
            
            // Send response to client
            SendSkillResponseToClient?.Invoke(queued.ClientNetworkId, response);
            
            if (response.Validated)
            {
                m_Stats.SkillsValidated++;
                
                // Broadcast skill execution to all clients
                var broadcast = new NetworkSkillBroadcast
                {
                    CharacterNetworkId = queued.ClientNetworkId,
                    TargetNetworkId = request.TargetNetworkId,
                    SkillHash = request.SkillHash,
                    WeaponHash = request.WeaponHash,
                    ComboNodeId = response.ComboNodeId,
                    ServerTimestamp = Time.time,
                    IsCharged = request.IsChargeRelease,
                    ChargeLevel = request.IsChargeRelease 
                        ? (byte)Mathf.Clamp(Mathf.RoundToInt(request.ChargeDuration / 3f * 255f), 0, 255) 
                        : (byte)0
                };
                
                BroadcastSkillToAllClients?.Invoke(broadcast);
                OnSkillValidated?.Invoke(broadcast);
            }
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // SERVER-SIDE: CHARGE REQUEST PROCESSING
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// [Server] Called when a charge request is received from a client.
        /// </summary>
        public void ReceiveChargeRequest(uint clientNetworkId, NetworkChargeRequest request)
        {
            if (!m_IsServer) return;
            
            m_ServerChargeQueue.Enqueue(new QueuedChargeRequest
            {
                ClientNetworkId = clientNetworkId,
                Request = request,
                ReceivedTime = Time.time
            });
        }
        
        private void ProcessServerChargeQueue()
        {
            while (m_ServerChargeQueue.Count > 0)
            {
                var queued = m_ServerChargeQueue.Dequeue();
                ProcessChargeRequest(queued);
            }
        }
        
        private void ProcessChargeRequest(QueuedChargeRequest queued)
        {
            var request = queued.Request;
            
            // Find character's controller
            if (!m_Controllers.TryGetValue(queued.ClientNetworkId, out var controller))
            {
                SendChargeResponseToClient?.Invoke(queued.ClientNetworkId, new NetworkChargeResponse
                {
                    RequestId = request.RequestId,
                    Validated = false
                });
                return;
            }
            
            // Process request
            var response = controller.ProcessChargeRequest(request, queued.ClientNetworkId);
            
            // Send response to client
            SendChargeResponseToClient?.Invoke(queued.ClientNetworkId, response);
            
            if (response.Validated)
            {
                // Broadcast charge start to all clients
                var broadcast = new NetworkChargeBroadcast
                {
                    CharacterNetworkId = queued.ClientNetworkId,
                    ChargeStarted = true,
                    ChargeSkillHash = response.ChargeSkillHash,
                    ServerTimestamp = response.ServerChargeStartTime
                };
                
                BroadcastChargeToAllClients?.Invoke(broadcast);
            }
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // SERVER-SIDE: REACTION BROADCASTING
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// [Server] Broadcast a reaction to all clients.
        /// </summary>
        public void BroadcastReaction(NetworkReactionBroadcast broadcast)
        {
            if (!m_IsServer) return;
            
            m_Stats.ReactionsBroadcast++;
            
            BroadcastReactionToAllClients?.Invoke(broadcast);
            OnReactionBroadcast?.Invoke(broadcast);
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // CLIENT-SIDE: RECEIVING RESPONSES & BROADCASTS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// [Client] Called when server sends a hit response.
        /// </summary>
        public void ReceiveHitResponse(NetworkMeleeHitResponse response)
        {
            // Find the attacker's controller and forward the response
            // We need to know which controller sent the original request
            // For now, broadcast to all controllers with matching pending request
            foreach (var kvp in m_Controllers)
            {
                kvp.Value.ReceiveHitResponse(response);
            }
        }
        
        /// <summary>
        /// [Client] Called when server broadcasts a confirmed hit.
        /// </summary>
        public void ReceiveHitBroadcast(NetworkMeleeHitBroadcast broadcast)
        {
            if (m_LogHitBroadcasts)
            {
                Debug.Log($"[NetworkMeleeManager] Received hit broadcast: {broadcast.AttackerNetworkId} -> {broadcast.TargetNetworkId}");
            }
            
            // Forward to relevant controllers
            if (m_Controllers.TryGetValue(broadcast.AttackerNetworkId, out var attackerCtrl))
            {
                attackerCtrl.ReceiveHitBroadcast(broadcast);
            }
            
            if (m_Controllers.TryGetValue(broadcast.TargetNetworkId, out var targetCtrl))
            {
                targetCtrl.ReceiveHitBroadcast(broadcast);
            }
        }
        
        /// <summary>
        /// [Client] Called when server sends a block response.
        /// </summary>
        public void ReceiveBlockResponse(NetworkBlockResponse response)
        {
            foreach (var kvp in m_Controllers)
            {
                kvp.Value.ReceiveBlockResponse(response);
            }
        }
        
        /// <summary>
        /// [Client] Called when server broadcasts block state.
        /// </summary>
        public void ReceiveBlockBroadcast(NetworkBlockBroadcast broadcast)
        {
            if (m_Controllers.TryGetValue(broadcast.CharacterNetworkId, out var ctrl))
            {
                ctrl.ReceiveBlockBroadcast(broadcast);
            }
        }
        
        /// <summary>
        /// [Client] Called when server sends a skill response.
        /// </summary>
        public void ReceiveSkillResponse(NetworkSkillResponse response)
        {
            foreach (var kvp in m_Controllers)
            {
                kvp.Value.ReceiveSkillResponse(response);
            }
        }
        
        /// <summary>
        /// [Client] Called when server broadcasts skill execution.
        /// </summary>
        public void ReceiveSkillBroadcast(NetworkSkillBroadcast broadcast)
        {
            if (m_Controllers.TryGetValue(broadcast.CharacterNetworkId, out var ctrl))
            {
                ctrl.ReceiveSkillBroadcast(broadcast);
            }
        }
        
        /// <summary>
        /// [Client] Called when server sends a charge response.
        /// </summary>
        public void ReceiveChargeResponse(NetworkChargeResponse response)
        {
            foreach (var kvp in m_Controllers)
            {
                kvp.Value.ReceiveChargeResponse(response);
            }
        }
        
        /// <summary>
        /// [Client] Called when server broadcasts charge state.
        /// </summary>
        public void ReceiveChargeBroadcast(NetworkChargeBroadcast broadcast)
        {
            if (m_Controllers.TryGetValue(broadcast.CharacterNetworkId, out var ctrl))
            {
                ctrl.ReceiveChargeBroadcast(broadcast);
            }
        }
        
        /// <summary>
        /// [Client] Called when server broadcasts a reaction.
        /// </summary>
        public void ReceiveReactionBroadcast(NetworkReactionBroadcast broadcast)
        {
            if (m_Controllers.TryGetValue(broadcast.CharacterNetworkId, out var ctrl))
            {
                ctrl.ReceiveReactionBroadcast(broadcast);
            }
        }
    }
}
#endif
