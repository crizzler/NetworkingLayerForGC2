#if GC2_SHOOTER
using System;
using System.Collections.Generic;
using UnityEngine;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Common;

#if UNITY_NETCODE
using Unity.Netcode;
#endif

namespace Arawn.GameCreator2.Networking.Shooter
{
    /// <summary>
    /// Global manager for network shooter combat coordination.
    /// Handles routing messages between clients and server.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Add this component to a NetworkManager or persistent object in your scene.
    /// It manages the routing of shot requests, hit requests, and broadcasts.
    /// </para>
    /// <para>
    /// <b>Integration:</b>
    /// Hook up the delegate actions to your network transport.
    /// </para>
    /// </remarks>
    [AddComponentMenu("Game Creator/Network/Shooter/Network Shooter Manager")]
    [DefaultExecutionOrder(ApplicationManager.EXECUTION_ORDER_DEFAULT - 10)]
    public class NetworkShooterManager : MonoBehaviour
    {
        // ════════════════════════════════════════════════════════════════════════════════════════
        // SINGLETON
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        private static NetworkShooterManager s_Instance;
        
        /// <summary>Global manager instance.</summary>
        public static NetworkShooterManager Instance => s_Instance;
        
        /// <summary>Whether an instance exists.</summary>
        public static bool HasInstance => s_Instance != null;
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // INSPECTOR
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        [Header("Processing Settings")]
        [Tooltip("Maximum shot requests to process per frame on server.")]
        [SerializeField] private int m_MaxShotsPerFrame = 20;
        
        [Tooltip("Maximum hit requests to process per frame on server.")]
        [SerializeField] private int m_MaxHitsPerFrame = 40;
        
        [Tooltip("Maximum time in the past for shot validation (seconds).")]
        [SerializeField] private float m_MaxRewindTime = 0.5f;
        
        [Header("Debug")]
        [SerializeField] private bool m_LogShotRequests = false;
        [SerializeField] private bool m_LogHitRequests = false;
        [SerializeField] private bool m_LogBroadcasts = false;
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // NETWORK DELEGATES (Connect to your transport)
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        // --- Shot/Hit Delegates ---
        
        /// <summary>[Client] Send shot request to server.</summary>
        public Action<NetworkShotRequest> SendShotRequestToServer;
        
        /// <summary>[Client] Send hit request to server.</summary>
        public Action<NetworkShooterHitRequest> SendHitRequestToServer;
        
        /// <summary>[Server] Send shot response to a specific client.</summary>
        public Action<uint, NetworkShotResponse> SendShotResponseToClient;
        
        /// <summary>[Server] Send hit response to a specific client.</summary>
        public Action<uint, NetworkShooterHitResponse> SendHitResponseToClient;
        
        /// <summary>[Server] Broadcast shot to all clients.</summary>
        public Action<NetworkShotBroadcast> BroadcastShotToAllClients;
        
        /// <summary>[Server] Broadcast hit to all clients.</summary>
        public Action<NetworkShooterHitBroadcast> BroadcastHitToAllClients;
        
        // --- Reload Delegates ---
        
        /// <summary>[Client] Send reload request to server.</summary>
        public Action<NetworkReloadRequest> SendReloadRequestToServer;
        
        /// <summary>[Client] Send quick reload request to server.</summary>
        public Action<NetworkQuickReloadRequest> SendQuickReloadRequestToServer;
        
        /// <summary>[Server] Send reload response to a specific client.</summary>
        public Action<uint, NetworkReloadResponse> SendReloadResponseToClient;
        
        /// <summary>[Server] Broadcast reload event to all clients.</summary>
        public Action<NetworkReloadBroadcast> BroadcastReloadToAllClients;
        
        // --- Jam/Fix Delegates ---
        
        /// <summary>[Client] Send fix jam request to server.</summary>
        public Action<NetworkFixJamRequest> SendFixJamRequestToServer;
        
        /// <summary>[Server] Send fix jam response to a specific client.</summary>
        public Action<uint, NetworkFixJamResponse> SendFixJamResponseToClient;
        
        /// <summary>[Server] Broadcast weapon jam to all clients.</summary>
        public Action<NetworkJamBroadcast> BroadcastJamToAllClients;
        
        /// <summary>[Server] Broadcast jam fix to all clients.</summary>
        public Action<NetworkFixJamBroadcast> BroadcastFixJamToAllClients;
        
        // --- Charge Delegates ---
        
        /// <summary>[Client] Send charge start request to server.</summary>
        public Action<NetworkChargeStartRequest> SendChargeStartRequestToServer;
        
        /// <summary>[Client] Send charge cancel request to server.</summary>
        public Action<NetworkChargeCancelRequest> SendChargeCancelRequestToServer;
        
        /// <summary>[Server] Send charge start response to a specific client.</summary>
        public Action<uint, NetworkChargeStartResponse> SendChargeStartResponseToClient;
        
        /// <summary>[Server] Broadcast charge state to all clients.</summary>
        public Action<NetworkChargeBroadcast> BroadcastChargeToAllClients;
        
        // --- Sight Switch Delegates ---
        
        /// <summary>[Client] Send sight switch request to server.</summary>
        public Action<NetworkSightSwitchRequest> SendSightSwitchRequestToServer;
        
        /// <summary>[Server] Send sight switch response to a specific client.</summary>
        public Action<uint, NetworkSightSwitchResponse> SendSightSwitchResponseToClient;
        
        /// <summary>[Server] Broadcast sight switch to all clients.</summary>
        public Action<NetworkSightSwitchBroadcast> BroadcastSightSwitchToAllClients;
        
        // --- Utility Delegates ---
        
        /// <summary>Get a NetworkCharacter by network ID.</summary>
        public Func<uint, NetworkCharacter> GetCharacterByNetworkIdFunc;
        
        /// <summary>Get current network time.</summary>
        public Func<float> GetNetworkTimeFunc;
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // EVENTS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>Called when any shot request is sent.</summary>
        public event Action<NetworkShotRequest> OnShotRequestSent;
        
        /// <summary>Called when any shot is validated on server.</summary>
        public event Action<NetworkShotBroadcast> OnShotValidated;
        
        /// <summary>Called when any shot is rejected on server.</summary>
        public event Action<NetworkShotRequest, ShotRejectionReason> OnShotRejected;
        
        /// <summary>Called when any hit is validated on server.</summary>
        public event Action<NetworkShooterHitBroadcast> OnHitValidated;
        
        /// <summary>Called when any hit is rejected on server.</summary>
        public event Action<NetworkShooterHitRequest, HitRejectionReason> OnHitRejected;
        
        /// <summary>Called when any reload is validated on server.</summary>
        public event Action<NetworkReloadBroadcast> OnReloadValidated;
        
        /// <summary>Called when a weapon jams on server.</summary>
        public event Action<NetworkJamBroadcast> OnWeaponJammed;
        
        /// <summary>Called when a weapon jam is fixed on server.</summary>
        public event Action<NetworkFixJamBroadcast> OnJamFixed;
        
        /// <summary>Called when charge state changes on server.</summary>
        public event Action<NetworkChargeBroadcast> OnChargeStateChanged;
        
        /// <summary>Called when sight is switched on server.</summary>
        public event Action<NetworkSightSwitchBroadcast> OnSightSwitched;
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // PRIVATE FIELDS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        private bool m_IsServer;
        private bool m_IsClient;
        
        // Controller registry
        private readonly Dictionary<uint, NetworkShooterController> m_Controllers = new(32);
        
        // Server request queues
        private readonly Queue<QueuedShotRequest> m_ServerShotQueue = new(64);
        private readonly Queue<QueuedHitRequest> m_ServerHitQueue = new(128);
        private readonly Queue<QueuedReloadRequest> m_ServerReloadQueue = new(16);
        private readonly Queue<QueuedFixJamRequest> m_ServerFixJamQueue = new(16);
        private readonly Queue<QueuedChargeRequest> m_ServerChargeQueue = new(16);
        private readonly Queue<QueuedSightSwitchRequest> m_ServerSightSwitchQueue = new(16);
        
        // Statistics
        private ShooterNetworkStats m_Stats;
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // STRUCTS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        private struct QueuedShotRequest
        {
            public uint ClientNetworkId;
            public NetworkShotRequest Request;
            public float ReceivedTime;
        }
        
        private struct QueuedHitRequest
        {
            public uint ClientNetworkId;
            public NetworkShooterHitRequest Request;
            public float ReceivedTime;
        }
        
        private struct QueuedReloadRequest
        {
            public uint ClientNetworkId;
            public NetworkReloadRequest Request;
            public float ReceivedTime;
        }
        
        private struct QueuedFixJamRequest
        {
            public uint ClientNetworkId;
            public NetworkFixJamRequest Request;
            public float ReceivedTime;
        }
        
        private struct QueuedChargeRequest
        {
            public uint ClientNetworkId;
            public NetworkChargeStartRequest Request;
            public float ReceivedTime;
        }
        
        private struct QueuedSightSwitchRequest
        {
            public uint ClientNetworkId;
            public NetworkSightSwitchRequest Request;
            public float ReceivedTime;
        }
        
        /// <summary>Network statistics.</summary>
        [Serializable]
        public struct ShooterNetworkStats
        {
            public int ShotRequestsSent;
            public int ShotRequestsReceived;
            public int ShotsValidated;
            public int ShotsRejected;
            public int HitRequestsReceived;
            public int HitsValidated;
            public int HitsRejected;
            public int ShotBroadcastsSent;
            public int HitBroadcastsSent;
            public int ReloadRequestsReceived;
            public int ReloadsValidated;
            public int FixJamRequestsReceived;
            public int FixJamsValidated;
            public int ChargeRequestsReceived;
            public int ChargesValidated;
            public int SightSwitchRequestsReceived;
            public int SightSwitchesValidated;
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // PROPERTIES
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        public bool IsServer => m_IsServer;
        public bool IsClient => m_IsClient;
        public ShooterNetworkStats Stats => m_Stats;
        
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
                Debug.LogWarning("[NetworkShooterManager] Multiple instances detected. Using first.");
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
                ProcessServerShotQueue();
                ProcessServerHitQueue();
                ProcessServerReloadQueue();
                ProcessServerFixJamQueue();
                ProcessServerChargeQueue();
                ProcessServerSightSwitchQueue();
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
            
            Debug.Log($"[NetworkShooterManager] Initialized - Server: {isServer}, Client: {isClient}");
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // CONTROLLER REGISTRATION
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Register a NetworkShooterController for a character.
        /// </summary>
        public void RegisterController(uint networkId, NetworkShooterController controller)
        {
            if (controller == null) return;
            
            m_Controllers[networkId] = controller;
            
            // Subscribe to controller events
            controller.OnShotRequestSent += OnControllerShotRequestSent;
            controller.OnHitDetected += OnControllerHitDetected;
        }
        
        /// <summary>
        /// Unregister a controller.
        /// </summary>
        public void UnregisterController(uint networkId)
        {
            if (m_Controllers.TryGetValue(networkId, out var controller))
            {
                controller.OnShotRequestSent -= OnControllerShotRequestSent;
                controller.OnHitDetected -= OnControllerHitDetected;
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
            
            if (m_Controllers.TryGetValue(networkId, out var controller))
            {
                return controller.GetComponent<NetworkCharacter>();
            }
            
            return null;
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // CLIENT-SIDE: SENDING REQUESTS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        private void OnControllerShotRequestSent(NetworkShotRequest request)
        {
            if (!m_IsClient) return;
            
            if (m_LogShotRequests)
            {
                Debug.Log($"[NetworkShooterManager] Shot request: Shooter={request.ShooterNetworkId}, " +
                         $"Pos={request.MuzzlePosition}, Dir={request.ShotDirection}");
            }
            
            SendShotRequestToServer?.Invoke(request);
            m_Stats.ShotRequestsSent++;
            
            OnShotRequestSent?.Invoke(request);
        }
        
        private void OnControllerHitDetected(NetworkShooterHitRequest request)
        {
            if (!m_IsClient) return;
            
            if (m_LogHitRequests)
            {
                Debug.Log($"[NetworkShooterManager] Hit request: Target={request.TargetNetworkId}, " +
                         $"Point={request.HitPoint}");
            }
            
            SendHitRequestToServer?.Invoke(request);
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // SERVER-SIDE: RECEIVING & PROCESSING REQUESTS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// [Server] Called when a shot request is received from a client.
        /// </summary>
        public void ReceiveShotRequest(uint clientNetworkId, NetworkShotRequest request)
        {
            if (!m_IsServer) return;
            
            m_Stats.ShotRequestsReceived++;
            
            m_ServerShotQueue.Enqueue(new QueuedShotRequest
            {
                ClientNetworkId = clientNetworkId,
                Request = request,
                ReceivedTime = Time.time
            });
        }
        
        /// <summary>
        /// [Server] Called when a hit request is received from a client.
        /// </summary>
        public void ReceiveHitRequest(uint clientNetworkId, NetworkShooterHitRequest request)
        {
            if (!m_IsServer) return;
            
            m_Stats.HitRequestsReceived++;
            
            m_ServerHitQueue.Enqueue(new QueuedHitRequest
            {
                ClientNetworkId = clientNetworkId,
                Request = request,
                ReceivedTime = Time.time
            });
        }
        
        private void ProcessServerShotQueue()
        {
            int processed = 0;
            
            while (m_ServerShotQueue.Count > 0 && processed < m_MaxShotsPerFrame)
            {
                var queued = m_ServerShotQueue.Dequeue();
                ProcessShotRequest(queued);
                processed++;
            }
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
        
        private void ProcessShotRequest(QueuedShotRequest queued)
        {
            var request = queued.Request;
            
            NetworkShotResponse response;
            
            if (m_Controllers.TryGetValue(request.ShooterNetworkId, out var controller))
            {
                response = controller.ProcessShotRequest(request, queued.ClientNetworkId);
            }
            else
            {
                response = ValidateShotRequest(request);
            }
            
            SendShotResponseToClient?.Invoke(queued.ClientNetworkId, response);
            
            if (response.Validated)
            {
                m_Stats.ShotsValidated++;
                
                // Broadcast to all clients
                var broadcast = new NetworkShotBroadcast
                {
                    ShooterNetworkId = request.ShooterNetworkId,
                    MuzzlePosition = request.MuzzlePosition,
                    ShotDirection = request.ShotDirection,
                    WeaponHash = request.WeaponHash,
                    SightHash = request.SightHash,
                    HitPoint = request.MuzzlePosition + request.ShotDirection * 100f, // Default far point
                    DidHit = false
                };
                
                BroadcastShotToAllClients?.Invoke(broadcast);
                m_Stats.ShotBroadcastsSent++;
                
                if (m_LogBroadcasts)
                {
                    Debug.Log($"[NetworkShooterManager] Shot broadcast: {request.ShooterNetworkId}");
                }
                
                OnShotValidated?.Invoke(broadcast);
            }
            else
            {
                m_Stats.ShotsRejected++;
                
                if (m_LogShotRequests)
                {
                    Debug.Log($"[NetworkShooterManager] Shot rejected: {response.RejectionReason}");
                }
                
                OnShotRejected?.Invoke(request, response.RejectionReason);
            }
        }
        
        private void ProcessHitRequest(QueuedHitRequest queued)
        {
            var request = queued.Request;
            
            NetworkShooterHitResponse response;
            
            if (m_Controllers.TryGetValue(request.ShooterNetworkId, out var controller))
            {
                response = controller.ProcessHitRequest(request, queued.ClientNetworkId);
            }
            else
            {
                response = ValidateHitRequest(request);
            }
            
            SendHitResponseToClient?.Invoke(queued.ClientNetworkId, response);
            
            if (response.Validated)
            {
                m_Stats.HitsValidated++;
                
                // Apply damage on server
                ApplyDamageOnServer(request, response.Damage);
                
                // Broadcast to all clients
                var broadcast = new NetworkShooterHitBroadcast
                {
                    ShooterNetworkId = request.ShooterNetworkId,
                    TargetNetworkId = request.TargetNetworkId,
                    HitPoint = request.HitPoint,
                    HitNormal = request.HitNormal,
                    WeaponHash = request.WeaponHash,
                    BlockResult = (byte)response.BlockResult,
                    MaterialHash = 0 // TODO: Get from hit
                };
                
                BroadcastHitToAllClients?.Invoke(broadcast);
                m_Stats.HitBroadcastsSent++;
                
                if (m_LogBroadcasts)
                {
                    Debug.Log($"[NetworkShooterManager] Hit broadcast: {request.ShooterNetworkId} -> {request.TargetNetworkId}");
                }
                
                OnHitValidated?.Invoke(broadcast);
            }
            else
            {
                m_Stats.HitsRejected++;
                
                if (m_LogHitRequests)
                {
                    Debug.Log($"[NetworkShooterManager] Hit rejected: {response.RejectionReason}");
                }
                
                OnHitRejected?.Invoke(request, response.RejectionReason);
            }
        }
        
        private NetworkShotResponse ValidateShotRequest(NetworkShotRequest request)
        {
            // Basic validation without controller
            float networkTime = GetNetworkTimeFunc?.Invoke() ?? Time.time;
            float age = networkTime - request.ClientTimestamp;
            
            if (age > m_MaxRewindTime)
            {
                return new NetworkShotResponse
                {
                    RequestId = request.RequestId,
                    Validated = false,
                    RejectionReason = ShotRejectionReason.TimestampTooOld
                };
            }
            
            var shooterNetworkChar = GetCharacterByNetworkId(request.ShooterNetworkId);
            if (shooterNetworkChar == null)
            {
                return new NetworkShotResponse
                {
                    RequestId = request.RequestId,
                    Validated = false,
                    RejectionReason = ShotRejectionReason.ShooterNotFound
                };
            }
            
            return new NetworkShotResponse
            {
                RequestId = request.RequestId,
                Validated = true,
                RejectionReason = ShotRejectionReason.None,
                AmmoRemaining = 0
            };
        }
        
        private NetworkShooterHitResponse ValidateHitRequest(NetworkShooterHitRequest request)
        {
            float networkTime = GetNetworkTimeFunc?.Invoke() ?? Time.time;
            float age = networkTime - request.ClientTimestamp;
            
            if (age > m_MaxRewindTime)
            {
                return new NetworkShooterHitResponse
                {
                    RequestId = request.RequestId,
                    Validated = false,
                    RejectionReason = HitRejectionReason.TimestampTooOld
                };
            }
            
            if (request.IsCharacterHit && request.TargetNetworkId != 0)
            {
                var targetNetworkChar = GetCharacterByNetworkId(request.TargetNetworkId);
                if (targetNetworkChar == null)
                {
                    return new NetworkShooterHitResponse
                    {
                        RequestId = request.RequestId,
                        Validated = false,
                        RejectionReason = HitRejectionReason.TargetNotFound
                    };
                }
                
                var targetCharacter = targetNetworkChar.GetComponent<Character>();
                if (targetCharacter != null)
                {
                    if (targetCharacter.Combat.Invincibility.IsInvincible)
                    {
                        return new NetworkShooterHitResponse
                        {
                            RequestId = request.RequestId,
                            Validated = false,
                            RejectionReason = HitRejectionReason.TargetInvincible
                        };
                    }
                    
                    if (targetCharacter.Dash != null && targetCharacter.Dash.IsDodge)
                    {
                        return new NetworkShooterHitResponse
                        {
                            RequestId = request.RequestId,
                            Validated = false,
                            RejectionReason = HitRejectionReason.TargetDodged
                        };
                    }
                }
            }
            
            return new NetworkShooterHitResponse
            {
                RequestId = request.RequestId,
                Validated = true,
                RejectionReason = HitRejectionReason.None,
                Damage = 10f,
                BlockResult = NetworkBlockResult.None
            };
        }
        
        private void ApplyDamageOnServer(NetworkShooterHitRequest request, float damage)
        {
            if (!request.IsCharacterHit || request.TargetNetworkId == 0) return;
            
            var targetNetworkChar = GetCharacterByNetworkId(request.TargetNetworkId);
            if (targetNetworkChar == null) return;
            
            var targetCharacter = targetNetworkChar.GetComponent<Character>();
            if (targetCharacter == null) return;
            
            // TODO: Apply actual damage using GC2 stats system
            Debug.Log($"[NetworkShooterManager] Server applying {damage} damage to {targetCharacter.name}");
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // CLIENT-SIDE: RECEIVING RESPONSES & BROADCASTS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// [Client] Called when server sends a shot response.
        /// </summary>
        public void ReceiveShotResponse(NetworkShotResponse response)
        {
            foreach (var kvp in m_Controllers)
            {
                kvp.Value.ReceiveShotResponse(response);
            }
        }
        
        /// <summary>
        /// [Client] Called when server sends a hit response.
        /// </summary>
        public void ReceiveHitResponse(NetworkShooterHitResponse response)
        {
            foreach (var kvp in m_Controllers)
            {
                kvp.Value.ReceiveHitResponse(response);
            }
        }
        
        /// <summary>
        /// [Client] Called when server broadcasts a confirmed shot.
        /// </summary>
        public void ReceiveShotBroadcast(NetworkShotBroadcast broadcast)
        {
            if (m_LogBroadcasts)
            {
                Debug.Log($"[NetworkShooterManager] Received shot broadcast: {broadcast.ShooterNetworkId}");
            }
            
            if (m_Controllers.TryGetValue(broadcast.ShooterNetworkId, out var controller))
            {
                controller.ReceiveShotBroadcast(broadcast);
            }
        }
        
        /// <summary>
        /// [Client] Called when server broadcasts a confirmed hit.
        /// </summary>
        public void ReceiveHitBroadcast(NetworkShooterHitBroadcast broadcast)
        {
            if (m_LogBroadcasts)
            {
                Debug.Log($"[NetworkShooterManager] Received hit broadcast: {broadcast.ShooterNetworkId} -> {broadcast.TargetNetworkId}");
            }
            
            if (m_Controllers.TryGetValue(broadcast.ShooterNetworkId, out var shooterCtrl))
            {
                shooterCtrl.ReceiveHitBroadcast(broadcast);
            }
            
            if (m_Controllers.TryGetValue(broadcast.TargetNetworkId, out var targetCtrl))
            {
                targetCtrl.ReceiveHitBroadcast(broadcast);
            }
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // RELOAD NETWORKING
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// [Server] Called when a reload request is received from a client.
        /// </summary>
        public void ReceiveReloadRequest(uint clientNetworkId, NetworkReloadRequest request)
        {
            if (!m_IsServer) return;
            
            m_Stats.ReloadRequestsReceived++;
            
            m_ServerReloadQueue.Enqueue(new QueuedReloadRequest
            {
                ClientNetworkId = clientNetworkId,
                Request = request,
                ReceivedTime = Time.time
            });
        }
        
        private void ProcessServerReloadQueue()
        {
            while (m_ServerReloadQueue.Count > 0)
            {
                var queued = m_ServerReloadQueue.Dequeue();
                ProcessReloadRequest(queued);
            }
        }
        
        private void ProcessReloadRequest(QueuedReloadRequest queued)
        {
            var request = queued.Request;
            NetworkReloadResponse response;
            
            if (m_Controllers.TryGetValue(request.CharacterNetworkId, out var controller))
            {
                response = controller.ProcessReloadRequest(request, queued.ClientNetworkId);
            }
            else
            {
                response = new NetworkReloadResponse
                {
                    RequestId = request.RequestId,
                    Validated = false,
                    RejectionReason = ReloadRejectionReason.CharacterNotFound
                };
            }
            
            SendReloadResponseToClient?.Invoke(queued.ClientNetworkId, response);
            
            if (response.Validated)
            {
                m_Stats.ReloadsValidated++;
                
                // Broadcast reload started
                var broadcast = new NetworkReloadBroadcast
                {
                    CharacterNetworkId = request.CharacterNetworkId,
                    WeaponHash = request.WeaponHash,
                    NewAmmoCount = 0,
                    EventType = ReloadEventType.Started
                };
                
                BroadcastReloadToAllClients?.Invoke(broadcast);
                OnReloadValidated?.Invoke(broadcast);
                
                if (m_LogBroadcasts)
                {
                    Debug.Log($"[NetworkShooterManager] Reload broadcast: {request.CharacterNetworkId}");
                }
            }
        }
        
        /// <summary>
        /// [Client] Called when server sends a reload response.
        /// </summary>
        public void ReceiveReloadResponse(NetworkReloadResponse response)
        {
            foreach (var kvp in m_Controllers)
            {
                kvp.Value.ReceiveReloadResponse(response);
            }
        }
        
        /// <summary>
        /// [Client] Called when server broadcasts a reload event.
        /// </summary>
        public void ReceiveReloadBroadcast(NetworkReloadBroadcast broadcast)
        {
            if (m_LogBroadcasts)
            {
                Debug.Log($"[NetworkShooterManager] Received reload broadcast: {broadcast.CharacterNetworkId}, Event: {broadcast.EventType}");
            }
            
            if (m_Controllers.TryGetValue(broadcast.CharacterNetworkId, out var controller))
            {
                controller.ReceiveReloadBroadcast(broadcast);
            }
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // JAM / FIX NETWORKING
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// [Server] Called when a fix jam request is received from a client.
        /// </summary>
        public void ReceiveFixJamRequest(uint clientNetworkId, NetworkFixJamRequest request)
        {
            if (!m_IsServer) return;
            
            m_Stats.FixJamRequestsReceived++;
            
            m_ServerFixJamQueue.Enqueue(new QueuedFixJamRequest
            {
                ClientNetworkId = clientNetworkId,
                Request = request,
                ReceivedTime = Time.time
            });
        }
        
        private void ProcessServerFixJamQueue()
        {
            while (m_ServerFixJamQueue.Count > 0)
            {
                var queued = m_ServerFixJamQueue.Dequeue();
                ProcessFixJamRequest(queued);
            }
        }
        
        private void ProcessFixJamRequest(QueuedFixJamRequest queued)
        {
            var request = queued.Request;
            NetworkFixJamResponse response;
            
            if (m_Controllers.TryGetValue(request.CharacterNetworkId, out var controller))
            {
                response = controller.ProcessFixJamRequest(request, queued.ClientNetworkId);
            }
            else
            {
                response = new NetworkFixJamResponse
                {
                    RequestId = request.RequestId,
                    Validated = false,
                    RejectionReason = FixJamRejectionReason.CharacterNotFound
                };
            }
            
            SendFixJamResponseToClient?.Invoke(queued.ClientNetworkId, response);
            
            if (response.Validated)
            {
                m_Stats.FixJamsValidated++;
                
                if (m_LogBroadcasts)
                {
                    Debug.Log($"[NetworkShooterManager] Fix jam validated: {request.CharacterNetworkId}");
                }
            }
        }
        
        /// <summary>
        /// [Server] Broadcast that a weapon has jammed.
        /// Call this when server determines a jam occurs (e.g., during shot processing).
        /// </summary>
        public void BroadcastJam(uint characterNetworkId, int weaponHash)
        {
            if (!m_IsServer) return;
            
            var broadcast = new NetworkJamBroadcast
            {
                CharacterNetworkId = characterNetworkId,
                WeaponHash = weaponHash
            };
            
            BroadcastJamToAllClients?.Invoke(broadcast);
            OnWeaponJammed?.Invoke(broadcast);
            
            if (m_LogBroadcasts)
            {
                Debug.Log($"[NetworkShooterManager] Jam broadcast: {characterNetworkId}");
            }
        }
        
        /// <summary>
        /// [Server] Broadcast that a jam fix has completed.
        /// </summary>
        public void BroadcastFixJamComplete(uint characterNetworkId, int weaponHash, bool success)
        {
            if (!m_IsServer) return;
            
            var broadcast = new NetworkFixJamBroadcast
            {
                CharacterNetworkId = characterNetworkId,
                WeaponHash = weaponHash,
                Success = success
            };
            
            BroadcastFixJamToAllClients?.Invoke(broadcast);
            OnJamFixed?.Invoke(broadcast);
            
            if (m_LogBroadcasts)
            {
                Debug.Log($"[NetworkShooterManager] Fix jam complete broadcast: {characterNetworkId}, Success: {success}");
            }
        }
        
        /// <summary>
        /// [Client] Called when server sends a fix jam response.
        /// </summary>
        public void ReceiveFixJamResponse(NetworkFixJamResponse response)
        {
            foreach (var kvp in m_Controllers)
            {
                kvp.Value.ReceiveFixJamResponse(response);
            }
        }
        
        /// <summary>
        /// [Client] Called when server broadcasts a weapon jam.
        /// </summary>
        public void ReceiveJamBroadcast(NetworkJamBroadcast broadcast)
        {
            if (m_LogBroadcasts)
            {
                Debug.Log($"[NetworkShooterManager] Received jam broadcast: {broadcast.CharacterNetworkId}");
            }
            
            if (m_Controllers.TryGetValue(broadcast.CharacterNetworkId, out var controller))
            {
                controller.ReceiveJamBroadcast(broadcast);
            }
        }
        
        /// <summary>
        /// [Client] Called when server broadcasts a jam fix complete.
        /// </summary>
        public void ReceiveFixJamBroadcast(NetworkFixJamBroadcast broadcast)
        {
            if (m_LogBroadcasts)
            {
                Debug.Log($"[NetworkShooterManager] Received fix jam broadcast: {broadcast.CharacterNetworkId}, Success: {broadcast.Success}");
            }
            
            if (m_Controllers.TryGetValue(broadcast.CharacterNetworkId, out var controller))
            {
                controller.ReceiveFixJamBroadcast(broadcast);
            }
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // CHARGE NETWORKING
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// [Server] Called when a charge start request is received from a client.
        /// </summary>
        public void ReceiveChargeStartRequest(uint clientNetworkId, NetworkChargeStartRequest request)
        {
            if (!m_IsServer) return;
            
            m_Stats.ChargeRequestsReceived++;
            
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
            NetworkChargeStartResponse response;
            
            if (m_Controllers.TryGetValue(request.CharacterNetworkId, out var controller))
            {
                response = controller.ProcessChargeStartRequest(request, queued.ClientNetworkId);
            }
            else
            {
                response = new NetworkChargeStartResponse
                {
                    RequestId = request.RequestId,
                    Validated = false,
                    RejectionReason = ChargeRejectionReason.CharacterNotFound
                };
            }
            
            SendChargeStartResponseToClient?.Invoke(queued.ClientNetworkId, response);
            
            if (response.Validated)
            {
                m_Stats.ChargesValidated++;
                
                // Broadcast charge started
                var broadcast = new NetworkChargeBroadcast
                {
                    CharacterNetworkId = request.CharacterNetworkId,
                    WeaponHash = request.WeaponHash,
                    ChargeRatio = 0,
                    EventType = ChargeEventType.Started
                };
                
                BroadcastChargeToAllClients?.Invoke(broadcast);
                OnChargeStateChanged?.Invoke(broadcast);
                
                if (m_LogBroadcasts)
                {
                    Debug.Log($"[NetworkShooterManager] Charge start broadcast: {request.CharacterNetworkId}");
                }
            }
        }
        
        /// <summary>
        /// [Server] Broadcast charge state update.
        /// Call this periodically while a character is charging.
        /// </summary>
        public void BroadcastChargeState(uint characterNetworkId, int weaponHash, float chargeRatio, ChargeEventType eventType)
        {
            if (!m_IsServer) return;
            
            var broadcast = new NetworkChargeBroadcast
            {
                CharacterNetworkId = characterNetworkId,
                WeaponHash = weaponHash,
                ChargeRatio = (byte)(chargeRatio * 255f),
                EventType = eventType
            };
            
            BroadcastChargeToAllClients?.Invoke(broadcast);
            OnChargeStateChanged?.Invoke(broadcast);
        }
        
        /// <summary>
        /// [Client] Called when server sends a charge start response.
        /// </summary>
        public void ReceiveChargeStartResponse(NetworkChargeStartResponse response)
        {
            foreach (var kvp in m_Controllers)
            {
                kvp.Value.ReceiveChargeStartResponse(response);
            }
        }
        
        /// <summary>
        /// [Client] Called when server broadcasts a charge state.
        /// </summary>
        public void ReceiveChargeBroadcast(NetworkChargeBroadcast broadcast)
        {
            if (m_Controllers.TryGetValue(broadcast.CharacterNetworkId, out var controller))
            {
                controller.ReceiveChargeBroadcast(broadcast);
            }
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // SIGHT SWITCH NETWORKING
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// [Server] Called when a sight switch request is received from a client.
        /// </summary>
        public void ReceiveSightSwitchRequest(uint clientNetworkId, NetworkSightSwitchRequest request)
        {
            if (!m_IsServer) return;
            
            m_Stats.SightSwitchRequestsReceived++;
            
            m_ServerSightSwitchQueue.Enqueue(new QueuedSightSwitchRequest
            {
                ClientNetworkId = clientNetworkId,
                Request = request,
                ReceivedTime = Time.time
            });
        }
        
        private void ProcessServerSightSwitchQueue()
        {
            while (m_ServerSightSwitchQueue.Count > 0)
            {
                var queued = m_ServerSightSwitchQueue.Dequeue();
                ProcessSightSwitchRequest(queued);
            }
        }
        
        private void ProcessSightSwitchRequest(QueuedSightSwitchRequest queued)
        {
            var request = queued.Request;
            NetworkSightSwitchResponse response;
            
            if (m_Controllers.TryGetValue(request.CharacterNetworkId, out var controller))
            {
                response = controller.ProcessSightSwitchRequest(request, queued.ClientNetworkId);
            }
            else
            {
                response = new NetworkSightSwitchResponse
                {
                    RequestId = request.RequestId,
                    Validated = false,
                    RejectionReason = SightSwitchRejectionReason.CharacterNotFound
                };
            }
            
            SendSightSwitchResponseToClient?.Invoke(queued.ClientNetworkId, response);
            
            if (response.Validated)
            {
                m_Stats.SightSwitchesValidated++;
                
                // Broadcast sight switch
                var broadcast = new NetworkSightSwitchBroadcast
                {
                    CharacterNetworkId = request.CharacterNetworkId,
                    WeaponHash = request.WeaponHash,
                    NewSightHash = request.NewSightHash
                };
                
                BroadcastSightSwitchToAllClients?.Invoke(broadcast);
                OnSightSwitched?.Invoke(broadcast);
                
                if (m_LogBroadcasts)
                {
                    Debug.Log($"[NetworkShooterManager] Sight switch broadcast: {request.CharacterNetworkId}");
                }
            }
        }
        
        /// <summary>
        /// [Client] Called when server sends a sight switch response.
        /// </summary>
        public void ReceiveSightSwitchResponse(NetworkSightSwitchResponse response)
        {
            foreach (var kvp in m_Controllers)
            {
                kvp.Value.ReceiveSightSwitchResponse(response);
            }
        }
        
        /// <summary>
        /// [Client] Called when server broadcasts a sight switch.
        /// </summary>
        public void ReceiveSightSwitchBroadcast(NetworkSightSwitchBroadcast broadcast)
        {
            if (m_LogBroadcasts)
            {
                Debug.Log($"[NetworkShooterManager] Received sight switch broadcast: {broadcast.CharacterNetworkId}");
            }
            
            if (m_Controllers.TryGetValue(broadcast.CharacterNetworkId, out var controller))
            {
                controller.ReceiveSightSwitchBroadcast(broadcast);
            }
        }
    }
}
#endif
