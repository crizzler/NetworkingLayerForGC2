#if GC2_SHOOTER
using System;
using System.Collections.Generic;
using UnityEngine;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.Shooter;

using Arawn.GameCreator2.Networking;
using Arawn.GameCreator2.Networking.Security;

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
    public class NetworkShooterManager : NetworkSingleton<NetworkShooterManager>
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

        private static NetworkRequestContext BuildContext(uint actorNetworkId, uint correlationId)
        {
            return NetworkRequestContext.Create(actorNetworkId, correlationId);
        }

        private bool ValidateShooterRequest(uint senderClientId, uint actorNetworkId, uint correlationId, string requestType)
        {
            return SecurityIntegration.ValidateModuleRequest(
                senderClientId,
                BuildContext(actorNetworkId, correlationId),
                "Shooter",
                requestType);
        }

        private static ulong ComposeShotKey(uint shooterNetworkId, ushort shotRequestId)
        {
            return ((ulong)shooterNetworkId << 16) | shotRequestId;
        }

        private bool IsQueueAtCapacity<T>(Queue<T> queue, int maxQueueLength, uint senderClientId, uint actorNetworkId, string requestType)
        {
            int safeLimit = Mathf.Max(1, maxQueueLength);
            if (queue.Count < safeLimit) return false;

            SecurityIntegration.RecordViolation(
                senderClientId,
                actorNetworkId,
                SecurityViolationType.RateLimitExceeded,
                "Shooter",
                $"{requestType} queue capacity reached ({queue.Count}/{safeLimit})");

            if (m_LogShotRequests || m_LogHitRequests || m_LogBroadcasts)
            {
                Debug.LogWarning(
                    $"[NetworkShooterManager] Dropped {requestType}: queue full ({queue.Count}/{safeLimit})");
            }

            return true;
        }

        private static int DropStaleRequests<T>(Queue<T> queue, float maxAgeSeconds, Func<T, float> getReceivedTime)
        {
            if (queue.Count == 0) return 0;

            float now = Time.time;
            int dropped = 0;
            while (queue.Count > 0)
            {
                T queued = queue.Peek();
                if (now - getReceivedTime(queued) <= maxAgeSeconds) break;

                queue.Dequeue();
                dropped++;
            }

            return dropped;
        }

        private bool ValidateHitSourceShot(NetworkShooterHitRequest request)
        {
            if (request.SourceShotRequestId == 0) return false;

            ulong shotKey = ComposeShotKey(request.ShooterNetworkId, request.SourceShotRequestId);
            if (!m_ValidatedShotReferences.TryGetValue(shotKey, out var shotReference))
            {
                return false;
            }

            if (shotReference.WeaponHash != 0 && shotReference.WeaponHash != request.WeaponHash)
            {
                return false;
            }

            if (Time.time - shotReference.ValidatedTime > m_ValidatedShotLifetime)
            {
                m_ValidatedShotReferences.Remove(shotKey);
                return false;
            }

            return true;
        }

        private void RecordValidatedShot(NetworkShotRequest request)
        {
            ulong shotKey = ComposeShotKey(request.ShooterNetworkId, request.RequestId);
            m_ValidatedShotReferences[shotKey] = new ValidatedShotReference
            {
                ValidatedTime = Time.time,
                WeaponHash = request.WeaponHash
            };
        }

        private void CleanupStaleValidatedShotReferences()
        {
            if (m_ValidatedShotReferences.Count == 0) return;

            float now = Time.time;
            s_SharedValidatedShotKeyBuffer.Clear();
            foreach (var entry in m_ValidatedShotReferences)
            {
                if (now - entry.Value.ValidatedTime > m_ValidatedShotLifetime)
                {
                    s_SharedValidatedShotKeyBuffer.Add(entry.Key);
                }
            }

            foreach (ulong key in s_SharedValidatedShotKeyBuffer)
            {
                m_ValidatedShotReferences.Remove(key);
            }
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

        private void OnDisable()
        {
            m_ValidatedShotReferences.Clear();

            if (m_PatchHooks != null)
            {
                m_PatchHooks.Initialize(false);
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
            SyncPatchHooks();
            
            Debug.Log($"[NetworkShooterManager] Initialized - Server: {isServer}, Client: {isClient}");
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
                m_PatchHooks = GetComponent<NetworkShooterPatchHooks>();
                if (m_PatchHooks == null)
                {
                    m_PatchHooks = gameObject.AddComponent<NetworkShooterPatchHooks>();
                }
            }

            m_PatchHooks.Initialize(true);
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
            
            if (m_Controllers.TryGetValue(request.ShooterNetworkId, out var controller))
            {
                response = controller.ProcessShotRequest(request, queued.ClientNetworkId);
            }
            else
            {
                response = ValidateShotRequest(request);
            }

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

            if (!ValidateHitSourceShot(request))
            {
                response = new NetworkShooterHitResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Validated = false,
                    RejectionReason = HitRejectionReason.ShotNotValidated
                };
            }
            else if (m_Controllers.TryGetValue(request.ShooterNetworkId, out var controller))
            {
                response = controller.ProcessHitRequest(request, queued.ClientNetworkId);
            }
            else
            {
                response = ValidateHitRequest(request);
            }

            response.ActorNetworkId = request.ActorNetworkId;
            response.CorrelationId = request.CorrelationId;
            
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
                    MaterialHash = ResolveMaterialHashFunc?.Invoke(request) ?? 0
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
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
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
            
            var targetNetworkChar = GetCharacterByNetworkId(request.TargetNetworkId);
            if (targetNetworkChar == null) return;
            
            var targetCharacter = targetNetworkChar.GetComponent<Character>();
            if (targetCharacter == null) return;
            
            if (ApplyDamageFunc != null)
            {
                ApplyDamageFunc.Invoke(request, damage);
                return;
            }

            var attackerNetworkChar = GetCharacterByNetworkId(request.ShooterNetworkId);
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

            Debug.LogWarning(
                $"[NetworkShooterManager] ApplyDamageFunc not set. " +
                $"Applied fallback hit reaction damage={damage:F2} target={targetCharacter.name}");
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
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // RELOAD NETWORKING
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// [Server] Called when a reload request is received from a client.
        /// </summary>
        public void ReceiveReloadRequest(uint clientNetworkId, NetworkReloadRequest request)
        {
            if (!m_IsServer) return;
            if (!ValidateShooterRequest(clientNetworkId, request.ActorNetworkId, request.CorrelationId, nameof(NetworkReloadRequest)))
            {
                SendReloadResponseToClient?.Invoke(clientNetworkId, new NetworkReloadResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Validated = false,
                    RejectionReason = ReloadRejectionReason.CheatSuspected
                });
                return;
            }
            
            m_Stats.ReloadRequestsReceived++;

            if (IsQueueAtCapacity(
                    m_ServerReloadQueue,
                    m_MaxReloadQueueLength,
                    clientNetworkId,
                    request.ActorNetworkId,
                    nameof(NetworkReloadRequest)))
            {
                SendReloadResponseToClient?.Invoke(clientNetworkId, new NetworkReloadResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Validated = false,
                    RejectionReason = ReloadRejectionReason.RateLimitExceeded
                });
                return;
            }
            
            m_ServerReloadQueue.Enqueue(new QueuedReloadRequest
            {
                ClientNetworkId = clientNetworkId,
                Request = request,
                ReceivedTime = Time.time
            });
        }
        
        private void ProcessServerReloadQueue()
        {
            int staleDropped = DropStaleRequests(m_ServerReloadQueue, m_MaxQueueAgeSeconds, queued => queued.ReceivedTime);
            if (staleDropped > 0 && (m_LogShotRequests || m_LogBroadcasts))
            {
                Debug.LogWarning($"[NetworkShooterManager] Dropped {staleDropped} stale reload requests");
            }

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

            response.ActorNetworkId = request.ActorNetworkId;
            response.CorrelationId = request.CorrelationId;
            
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
            if (response.ActorNetworkId != 0 && m_Controllers.TryGetValue(response.ActorNetworkId, out var controller))
            {
                controller.ReceiveReloadResponse(response);
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
            if (!ValidateShooterRequest(clientNetworkId, request.ActorNetworkId, request.CorrelationId, nameof(NetworkFixJamRequest)))
            {
                SendFixJamResponseToClient?.Invoke(clientNetworkId, new NetworkFixJamResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Validated = false,
                    RejectionReason = FixJamRejectionReason.CheatSuspected
                });
                return;
            }
            
            m_Stats.FixJamRequestsReceived++;

            if (IsQueueAtCapacity(
                    m_ServerFixJamQueue,
                    m_MaxFixJamQueueLength,
                    clientNetworkId,
                    request.ActorNetworkId,
                    nameof(NetworkFixJamRequest)))
            {
                SendFixJamResponseToClient?.Invoke(clientNetworkId, new NetworkFixJamResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Validated = false,
                    RejectionReason = FixJamRejectionReason.RateLimitExceeded
                });
                return;
            }
            
            m_ServerFixJamQueue.Enqueue(new QueuedFixJamRequest
            {
                ClientNetworkId = clientNetworkId,
                Request = request,
                ReceivedTime = Time.time
            });
        }
        
        private void ProcessServerFixJamQueue()
        {
            int staleDropped = DropStaleRequests(m_ServerFixJamQueue, m_MaxQueueAgeSeconds, queued => queued.ReceivedTime);
            if (staleDropped > 0 && (m_LogShotRequests || m_LogBroadcasts))
            {
                Debug.LogWarning($"[NetworkShooterManager] Dropped {staleDropped} stale fix-jam requests");
            }

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

            response.ActorNetworkId = request.ActorNetworkId;
            response.CorrelationId = request.CorrelationId;
            
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
            if (response.ActorNetworkId != 0 && m_Controllers.TryGetValue(response.ActorNetworkId, out var controller))
            {
                controller.ReceiveFixJamResponse(response);
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
            if (!ValidateShooterRequest(clientNetworkId, request.ActorNetworkId, request.CorrelationId, nameof(NetworkChargeStartRequest)))
            {
                SendChargeStartResponseToClient?.Invoke(clientNetworkId, new NetworkChargeStartResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Validated = false,
                    RejectionReason = ChargeRejectionReason.CheatSuspected
                });
                return;
            }
            
            m_Stats.ChargeRequestsReceived++;

            if (IsQueueAtCapacity(
                    m_ServerChargeQueue,
                    m_MaxChargeQueueLength,
                    clientNetworkId,
                    request.ActorNetworkId,
                    nameof(NetworkChargeStartRequest)))
            {
                SendChargeStartResponseToClient?.Invoke(clientNetworkId, new NetworkChargeStartResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Validated = false,
                    RejectionReason = ChargeRejectionReason.InvalidState
                });
                return;
            }
            
            m_ServerChargeQueue.Enqueue(new QueuedChargeRequest
            {
                ClientNetworkId = clientNetworkId,
                Request = request,
                ReceivedTime = Time.time
            });
        }
        
        private void ProcessServerChargeQueue()
        {
            int staleDropped = DropStaleRequests(m_ServerChargeQueue, m_MaxQueueAgeSeconds, queued => queued.ReceivedTime);
            if (staleDropped > 0 && (m_LogShotRequests || m_LogBroadcasts))
            {
                Debug.LogWarning($"[NetworkShooterManager] Dropped {staleDropped} stale charge requests");
            }

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

            response.ActorNetworkId = request.ActorNetworkId;
            response.CorrelationId = request.CorrelationId;
            
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
            if (response.ActorNetworkId != 0 && m_Controllers.TryGetValue(response.ActorNetworkId, out var controller))
            {
                controller.ReceiveChargeStartResponse(response);
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
            if (!ValidateShooterRequest(clientNetworkId, request.ActorNetworkId, request.CorrelationId, nameof(NetworkSightSwitchRequest)))
            {
                SendSightSwitchResponseToClient?.Invoke(clientNetworkId, new NetworkSightSwitchResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Validated = false,
                    RejectionReason = SightSwitchRejectionReason.CheatSuspected
                });
                return;
            }
            
            m_Stats.SightSwitchRequestsReceived++;

            if (IsQueueAtCapacity(
                    m_ServerSightSwitchQueue,
                    m_MaxSightSwitchQueueLength,
                    clientNetworkId,
                    request.ActorNetworkId,
                    nameof(NetworkSightSwitchRequest)))
            {
                SendSightSwitchResponseToClient?.Invoke(clientNetworkId, new NetworkSightSwitchResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Validated = false,
                    RejectionReason = SightSwitchRejectionReason.RateLimitExceeded
                });
                return;
            }
            
            m_ServerSightSwitchQueue.Enqueue(new QueuedSightSwitchRequest
            {
                ClientNetworkId = clientNetworkId,
                Request = request,
                ReceivedTime = Time.time
            });
        }
        
        private void ProcessServerSightSwitchQueue()
        {
            int staleDropped = DropStaleRequests(m_ServerSightSwitchQueue, m_MaxQueueAgeSeconds, queued => queued.ReceivedTime);
            if (staleDropped > 0 && (m_LogShotRequests || m_LogBroadcasts))
            {
                Debug.LogWarning($"[NetworkShooterManager] Dropped {staleDropped} stale sight-switch requests");
            }

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

            response.ActorNetworkId = request.ActorNetworkId;
            response.CorrelationId = request.CorrelationId;
            
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
            if (response.ActorNetworkId != 0 && m_Controllers.TryGetValue(response.ActorNetworkId, out var controller))
            {
                controller.ReceiveSightSwitchResponse(response);
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
