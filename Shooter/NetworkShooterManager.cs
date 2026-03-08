#if GC2_SHOOTER
using System;
using System.Collections.Generic;
using UnityEngine;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.Shooter;

using Arawn.GameCreator2.Networking;
using Arawn.GameCreator2.Networking.Security;

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
    public partial class NetworkShooterManager : NetworkSingleton<NetworkShooterManager>
    {
        // ════════════════════════════════════════════════════════════════════════════════════════
        // CONFIGURATION
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        protected override DuplicatePolicy OnDuplicatePolicy => DuplicatePolicy.DestroyComponent;
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // INSPECTOR
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        [Header("Processing Settings")]
        [Tooltip("Maximum shot requests to process per frame on server.")]
        [SerializeField] private int m_MaxShotsPerFrame = 20;
        
        [Tooltip("Maximum hit requests to process per frame on server.")]
        [SerializeField] private int m_MaxHitsPerFrame = 40;

        [Tooltip("Maximum queued shot requests awaiting processing.")]
        [SerializeField] private int m_MaxShotQueueLength = 512;

        [Tooltip("Maximum queued hit requests awaiting processing.")]
        [SerializeField] private int m_MaxHitQueueLength = 1024;

        [Tooltip("Maximum queued reload requests awaiting processing.")]
        [SerializeField] private int m_MaxReloadQueueLength = 128;

        [Tooltip("Maximum queued fix-jam requests awaiting processing.")]
        [SerializeField] private int m_MaxFixJamQueueLength = 128;

        [Tooltip("Maximum queued charge requests awaiting processing.")]
        [SerializeField] private int m_MaxChargeQueueLength = 128;

        [Tooltip("Maximum queued sight-switch requests awaiting processing.")]
        [SerializeField] private int m_MaxSightSwitchQueueLength = 128;

        [Tooltip("Drop queued requests older than this many seconds.")]
        [SerializeField] private float m_MaxQueueAgeSeconds = 1.5f;

        [Tooltip("How long a validated shot reference remains valid for hit binding.")]
        [SerializeField] private float m_ValidatedShotLifetime = 2f;

        [Tooltip("Maximum accepted hit confirmations per projectile for one validated shot.")]
        [Min(1)]
        [SerializeField] private int m_MaxValidatedHitsPerProjectile = 4;
        
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

        /// <summary>Optional server-side damage calculation hook.</summary>
        public Func<NetworkShooterHitRequest, float> ComputeDamageFunc;

        /// <summary>Optional server-side material hash resolver for hit effects.</summary>
        public Func<NetworkShooterHitRequest, int> ResolveMaterialHashFunc;

        /// <summary>Optional server-side damage application hook.</summary>
        public Action<NetworkShooterHitRequest, float> ApplyDamageFunc;
        
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
        private static readonly List<ulong> s_SharedValidatedShotKeyBuffer = new(32);
        
        // Controller registry
        private readonly Dictionary<uint, NetworkShooterController> m_Controllers = new(32);
        
        // Server request queues
        private readonly Queue<QueuedShotRequest> m_ServerShotQueue = new(64);
        private readonly Queue<QueuedHitRequest> m_ServerHitQueue = new(128);
        private readonly Queue<QueuedReloadRequest> m_ServerReloadQueue = new(16);
        private readonly Queue<QueuedFixJamRequest> m_ServerFixJamQueue = new(16);
        private readonly Queue<QueuedChargeRequest> m_ServerChargeQueue = new(16);
        private readonly Queue<QueuedSightSwitchRequest> m_ServerSightSwitchQueue = new(16);

        private struct ValidatedShotReference
        {
            public float ValidatedTime;
            public int WeaponHash;
            public int MaxAcceptedHits;
            public int AcceptedHitCount;
            public HashSet<uint> AcceptedCharacterTargets;
        }

        private readonly Dictionary<ulong, ValidatedShotReference> m_ValidatedShotReferences = new(128);
        
        // Statistics
        private ShooterNetworkStats m_Stats;
        private NetworkShooterPatchHooks m_PatchHooks;
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // WEAPON REGISTRY
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Registry entry for a ShooterWeapon asset.
        /// </summary>
        public struct ShooterWeaponRegistryEntry
        {
            public int Hash;
            public ShooterWeapon Weapon;
            public string Name;
        }
        
        private static readonly Dictionary<int, ShooterWeaponRegistryEntry> s_WeaponRegistry = new(32);
        
        /// <summary>
        /// Register a ShooterWeapon for network hash-to-asset lookup.
        /// Uses <c>weapon.Id.Hash</c> as the key.
        /// </summary>
        public static void RegisterShooterWeapon(ShooterWeapon weapon)
        {
            if (weapon == null) return;
            int hash = weapon.Id.Hash;
            s_WeaponRegistry[hash] = new ShooterWeaponRegistryEntry
            {
                Hash = hash,
                Weapon = weapon,
                Name = weapon.name
            };
        }
        
        /// <summary>
        /// Unregister a ShooterWeapon.
        /// </summary>
        public static void UnregisterShooterWeapon(ShooterWeapon weapon)
        {
            if (weapon == null) return;
            s_WeaponRegistry.Remove(weapon.Id.Hash);
        }
        
        /// <summary>
        /// Get a ShooterWeapon by its <see cref="IdString"/> hash.
        /// </summary>
        /// <returns>The weapon, or <c>null</c> if not registered.</returns>
        public static ShooterWeapon GetShooterWeaponByHash(int hash)
        {
            return s_WeaponRegistry.TryGetValue(hash, out var entry) ? entry.Weapon : null;
        }
        
        /// <summary>
        /// Check if a ShooterWeapon is registered.
        /// </summary>
        public static bool IsShooterWeaponRegistered(ShooterWeapon weapon)
        {
            return weapon != null && s_WeaponRegistry.ContainsKey(weapon.Id.Hash);
        }
        
        /// <summary>
        /// Clear all weapon registries.
        /// Call on scene unload or session end.
        /// </summary>
        public static void ClearRegistries()
        {
            s_WeaponRegistry.Clear();
        }
        
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
        
        private void Update()
        {
            if (m_IsServer)
            {
                CleanupStaleValidatedShotReferences();
                ProcessServerShotQueue();
                ProcessServerHitQueue();
                ProcessServerReloadQueue();
                ProcessServerFixJamQueue();
                ProcessServerChargeQueue();
                ProcessServerSightSwitchQueue();
            }
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
            if (!ValidateShooterRequest(clientNetworkId, request.ActorNetworkId, request.CorrelationId, nameof(NetworkShotRequest)))
            {
                SendShotResponseToClient?.Invoke(clientNetworkId, new NetworkShotResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Validated = false,
                    RejectionReason = ShotRejectionReason.CheatSuspected
                });
                return;
            }
            if (!ValidateActorBinding(clientNetworkId, request.ActorNetworkId, request.ShooterNetworkId, nameof(NetworkShotRequest), nameof(request.ShooterNetworkId)))
            {
                SendShotResponseToClient?.Invoke(clientNetworkId, new NetworkShotResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Validated = false,
                    RejectionReason = ShotRejectionReason.CheatSuspected
                });
                return;
            }
            
            m_Stats.ShotRequestsReceived++;

            if (IsQueueAtCapacity(
                    m_ServerShotQueue,
                    m_MaxShotQueueLength,
                    clientNetworkId,
                    request.ActorNetworkId,
                    nameof(NetworkShotRequest)))
            {
                SendShotResponseToClient?.Invoke(clientNetworkId, new NetworkShotResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Validated = false,
                    RejectionReason = ShotRejectionReason.RateLimitExceeded
                });
                return;
            }
            
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
            if (!ValidateShooterRequest(clientNetworkId, request.ActorNetworkId, request.CorrelationId, nameof(NetworkShooterHitRequest)))
            {
                SendHitResponseToClient?.Invoke(clientNetworkId, new NetworkShooterHitResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Validated = false,
                    RejectionReason = HitRejectionReason.CheatSuspected
                });
                return;
            }
            if (!ValidateActorBinding(clientNetworkId, request.ActorNetworkId, request.ShooterNetworkId, nameof(NetworkShooterHitRequest), nameof(request.ShooterNetworkId)))
            {
                SendHitResponseToClient?.Invoke(clientNetworkId, new NetworkShooterHitResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Validated = false,
                    RejectionReason = HitRejectionReason.CheatSuspected
                });
                return;
            }
            
            m_Stats.HitRequestsReceived++;

            if (IsQueueAtCapacity(
                    m_ServerHitQueue,
                    m_MaxHitQueueLength,
                    clientNetworkId,
                    request.ActorNetworkId,
                    nameof(NetworkShooterHitRequest)))
            {
                SendHitResponseToClient?.Invoke(clientNetworkId, new NetworkShooterHitResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Validated = false,
                    RejectionReason = HitRejectionReason.CheatSuspected
                });
                return;
            }
            
            m_ServerHitQueue.Enqueue(new QueuedHitRequest
            {
                ClientNetworkId = clientNetworkId,
                Request = request,
                ReceivedTime = Time.time
            });
        }
        
        private void ProcessServerShotQueue()
        {
            int staleDropped = DropStaleRequests(m_ServerShotQueue, m_MaxQueueAgeSeconds, queued => queued.ReceivedTime);
            if (staleDropped > 0 && (m_LogShotRequests || m_LogBroadcasts))
            {
                Debug.LogWarning($"[NetworkShooterManager] Dropped {staleDropped} stale shot requests");
            }

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
            int staleDropped = DropStaleRequests(m_ServerHitQueue, m_MaxQueueAgeSeconds, queued => queued.ReceivedTime);
            if (staleDropped > 0 && (m_LogHitRequests || m_LogBroadcasts))
            {
                Debug.LogWarning($"[NetworkShooterManager] Dropped {staleDropped} stale hit requests");
            }

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
            if (request.ActorNetworkId == 0 || request.ActorNetworkId != request.ShooterNetworkId)
            {
                response = new NetworkShotResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Validated = false,
                    RejectionReason = ShotRejectionReason.CheatSuspected
                };
                SendShotResponseToClient?.Invoke(queued.ClientNetworkId, response);
                return;
            }
            
            if (!TryGetActorController(
                    queued.ClientNetworkId,
                    request.ActorNetworkId,
                    nameof(NetworkShotRequest),
                    out var controller))
            {
                response = new NetworkShotResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Validated = false,
                    RejectionReason = ShotRejectionReason.ShooterNotFound
                };

                SendShotResponseToClient?.Invoke(queued.ClientNetworkId, response);
                return;
            }

            response = controller.ProcessShotRequest(request, queued.ClientNetworkId);
            response.ActorNetworkId = request.ActorNetworkId;
            response.CorrelationId = request.CorrelationId;
            
            SendShotResponseToClient?.Invoke(queued.ClientNetworkId, response);
            
            if (response.Validated)
            {
                m_Stats.ShotsValidated++;
                RecordValidatedShot(request);
                
                // Broadcast to all clients
                var broadcast = new NetworkShotBroadcast
                {
                    ShooterNetworkId = request.ActorNetworkId,
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
                    Debug.Log($"[NetworkShooterManager] Shot broadcast: {request.ActorNetworkId}");
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
            ulong sourceShotKey = 0;
            if (request.ActorNetworkId == 0 || request.ActorNetworkId != request.ShooterNetworkId)
            {
                response = new NetworkShooterHitResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Validated = false,
                    RejectionReason = HitRejectionReason.CheatSuspected
                };
                SendHitResponseToClient?.Invoke(queued.ClientNetworkId, response);
                return;
            }

            if (!ValidateHitSourceShot(request, out sourceShotKey, out HitRejectionReason shotBindingRejection))
            {
                response = new NetworkShooterHitResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Validated = false,
                    RejectionReason = shotBindingRejection
                };
            }
            else if (!TryGetActorController(
                         queued.ClientNetworkId,
                         request.ActorNetworkId,
                         nameof(NetworkShooterHitRequest),
                         out var controller))
            {
                response = new NetworkShooterHitResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Validated = false,
                    RejectionReason = HitRejectionReason.ShooterNotFound
                };
            }
            else
            {
                response = controller.ProcessHitRequest(request, queued.ClientNetworkId);
            }

            response.ActorNetworkId = request.ActorNetworkId;
            response.CorrelationId = request.CorrelationId;
            
            SendHitResponseToClient?.Invoke(queued.ClientNetworkId, response);
            
            if (response.Validated)
            {
                m_Stats.HitsValidated++;

                // Consume one authoritative hit claim from this validated shot.
                RecordValidatedHitClaim(sourceShotKey, request);
                
                // Apply damage on server
                ApplyDamageOnServer(request, response.Damage);
                
                // Broadcast to all clients
                var broadcast = new NetworkShooterHitBroadcast
                {
                    ShooterNetworkId = request.ActorNetworkId,
                    TargetNetworkId = request.TargetNetworkId,
                    HitPoint = request.HitPoint,
                    HitNormal = request.HitNormal,
                    WeaponHash = request.WeaponHash,
                    BlockResult = (byte)response.BlockResult,
                    MaterialHash = ResolveMaterialHashFunc?.Invoke(request) ?? 0
                };
                
                BroadcastHitToAllClients?.Invoke(broadcast);
                m_Stats.HitBroadcastsSent++;
                
                if (m_LogBroadcasts)
                {
                    Debug.Log($"[NetworkShooterManager] Hit broadcast: {request.ActorNetworkId} -> {request.TargetNetworkId}");
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
            uint shooterNetworkId = request.ActorNetworkId != 0 ? request.ActorNetworkId : request.ShooterNetworkId;
            
            if (age > m_MaxRewindTime)
            {
                return new NetworkShotResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Validated = false,
                    RejectionReason = ShotRejectionReason.TimestampTooOld
                };
            }
            
            var shooterNetworkChar = GetCharacterByNetworkId(shooterNetworkId);
            if (shooterNetworkChar == null)
            {
                return new NetworkShotResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Validated = false,
                    RejectionReason = ShotRejectionReason.ShooterNotFound
                };
            }
            
            return new NetworkShotResponse
            {
                RequestId = request.RequestId,
                ActorNetworkId = request.ActorNetworkId,
                CorrelationId = request.CorrelationId,
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
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
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
                        ActorNetworkId = request.ActorNetworkId,
                        CorrelationId = request.CorrelationId,
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
                            ActorNetworkId = request.ActorNetworkId,
                            CorrelationId = request.CorrelationId,
                            Validated = false,
                            RejectionReason = HitRejectionReason.TargetInvincible
                        };
                    }
                    
                    if (targetCharacter.Dash != null && targetCharacter.Dash.IsDodge)
                    {
                        return new NetworkShooterHitResponse
                        {
                            RequestId = request.RequestId,
                            ActorNetworkId = request.ActorNetworkId,
                            CorrelationId = request.CorrelationId,
                            Validated = false,
                            RejectionReason = HitRejectionReason.TargetDodged
                        };
                    }
                }
            }
            
            return new NetworkShooterHitResponse
            {
                RequestId = request.RequestId,
                ActorNetworkId = request.ActorNetworkId,
                CorrelationId = request.CorrelationId,
                Validated = true,
                RejectionReason = HitRejectionReason.None,
                Damage = Mathf.Max(0f, ComputeDamageFunc?.Invoke(request) ?? 10f),
                BlockResult = NetworkBlockResult.None
            };
        }
        
        private void ApplyDamageOnServer(NetworkShooterHitRequest request, float damage)
        {
            if (!request.IsCharacterHit || request.TargetNetworkId == 0) return;
            if (float.IsNaN(damage) || float.IsInfinity(damage) || damage <= 0f) return;
            
            var targetNetworkChar = GetCharacterByNetworkId(request.TargetNetworkId);
            if (targetNetworkChar == null) return;
            
            var targetCharacter = targetNetworkChar.GetComponent<Character>();
            if (targetCharacter == null) return;
            
            if (ApplyDamageFunc != null)
            {
                try
                {
                    ApplyDamageFunc.Invoke(request, damage);
                    return;
                }
                catch (Exception ex)
                {
                    Debug.LogError(
                        $"[NetworkShooterManager] ApplyDamageFunc threw an exception. " +
                        $"Falling back to built-in reaction damage path.\n{ex.Message}");
                }
            }

            uint shooterNetworkId = request.ActorNetworkId != 0 ? request.ActorNetworkId : request.ShooterNetworkId;
            var attackerNetworkChar = GetCharacterByNetworkId(shooterNetworkId);
            Vector3 incomingDirection;

            if (request.HitNormal.sqrMagnitude > 0.0001f)
            {
                incomingDirection = -request.HitNormal.normalized;
            }
            else if (attackerNetworkChar != null)
            {
                incomingDirection = (targetCharacter.transform.position - attackerNetworkChar.transform.position).normalized;
            }
            else
            {
                incomingDirection = targetCharacter.transform.forward;
            }

            Vector3 localDirection = targetCharacter.transform.InverseTransformDirection(incomingDirection).normalized;
            if (localDirection.sqrMagnitude < 0.0001f)
            {
                localDirection = Vector3.forward;
            }

            var reactionInput = new ReactionInput(localDirection, Mathf.Max(0f, damage));
            var args = new Args(attackerNetworkChar != null ? attackerNetworkChar.gameObject : null, targetCharacter.gameObject);
            _ = targetCharacter.Combat.GetHitReaction(reactionInput, args, null);

            if (m_LogHitRequests)
            {
                Debug.Log(
                    $"[NetworkShooterManager] Applied built-in server reaction damage={damage:F2} target={targetCharacter.name}");
            }
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // CLIENT-SIDE: RECEIVING RESPONSES & BROADCASTS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// [Client] Called when server sends a shot response.
        /// </summary>
        public void ReceiveShotResponse(NetworkShotResponse response)
        {
            if (response.ActorNetworkId != 0 && m_Controllers.TryGetValue(response.ActorNetworkId, out var controller))
            {
                controller.ReceiveShotResponse(response);
            }
        }
        
        /// <summary>
        /// [Client] Called when server sends a hit response.
        /// </summary>
        public void ReceiveHitResponse(NetworkShooterHitResponse response)
        {
            if (response.ActorNetworkId != 0 && m_Controllers.TryGetValue(response.ActorNetworkId, out var controller))
            {
                controller.ReceiveHitResponse(response);
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
    }
}
#endif
