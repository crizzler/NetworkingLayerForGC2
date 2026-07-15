#if GC2_SHOOTER
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
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

        [Header("Client Presentation Recovery")]
        [Tooltip("Briefly retain cosmetic broadcasts while their character controller is still spawning.")]
        [Min(0.05f)]
        [SerializeField] private float m_TransientBroadcastLifetime = 0.75f;

        [Tooltip("Maximum transient cosmetic broadcasts retained for spawn-order recovery.")]
        [Min(8)]
        [SerializeField] private int m_MaxPendingTransientBroadcasts = 128;

        [Tooltip("How long a validated shot reference remains valid for hit binding.")]
        [SerializeField] private float m_ValidatedShotLifetime = 2f;

        [Tooltip("Maximum accepted hit confirmations per projectile for one validated shot.")]
        [Min(1)]
        [SerializeField] private int m_MaxValidatedHitsPerProjectile = 4;
        
        [Tooltip("Maximum time in the past for shot validation (seconds).")]
        [SerializeField] private float m_MaxRewindTime = 0.5f;

        [Header("Server Damage Safety")]
        [Tooltip("Prevents the built-in fallback hit reaction from moving server-authoritative NavMesh characters. " +
                 "Use a custom ApplyDamageFunc if a project needs authored hit reactions for these targets.")]
        [SerializeField] private bool m_SuppressFallbackHitReactionsForServerNavMesh = true;

        [Tooltip("If fallback hit reactions are allowed for server NavMesh targets, remove vertical hit direction before choosing the reaction.")]
        [SerializeField] private bool m_FlattenServerNavMeshHitReactionDirection = true;

        [Tooltip("If fallback hit reactions are allowed for server NavMesh targets, snap the character back to the NavMesh after the reaction request.")]
        [SerializeField] private bool m_ResnapServerNavMeshAfterHitReaction = true;

        [Tooltip("Maximum NavMesh sample distance used when resnapping a server NavMesh target after a hit reaction.")]
        [Min(0.25f)]
        [SerializeField] private float m_ServerNavMeshHitReactionSnapDistance = 2f;
        
        [Header("Debug")]
        [Tooltip("Logs Shooter manager/controller registration and missing transport delegate wiring.")]
        [SerializeField] private bool m_LogDiagnostics = true;
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

        /// <summary>
        /// Optional client-side resolver that maps the authoritative material hash and
        /// locally detected collider object to the object used by GC2 MaterialSounds.
        /// Return the fallback object when no custom variant is needed.
        /// </summary>
        public Func<int, GameObject, GameObject> ResolveMaterialTargetFunc;

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
        private readonly List<PendingShotBroadcast> m_PendingShotBroadcasts = new(16);
        private readonly List<PendingHitBroadcast> m_PendingHitBroadcasts = new(16);
        private readonly List<PendingImpactMotion> m_PendingImpactMotions = new(8);

        private struct PendingShotBroadcast
        {
            public NetworkShotBroadcast Broadcast;
            public float ReceivedTime;
        }

        private struct PendingHitBroadcast
        {
            public NetworkShooterHitBroadcast Broadcast;
            public float ReceivedTime;
            public bool NeedsShooter;
            public bool NeedsTarget;
        }

        private struct PendingImpactMotion
        {
            public NetworkShooterImpactMotion Motion;
            public float ReceivedTime;
        }
        
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
            public GameObject ModelPrefab;
            public Handle Handle;
            public string Name;
        }
        
        private static readonly Dictionary<int, ShooterWeaponRegistryEntry> s_WeaponRegistry = new(32);
        
        /// <summary>
        /// Register a ShooterWeapon for network hash-to-asset lookup.
        /// Uses <c>weapon.Id.Hash</c> as the key.
        /// </summary>
        public static void RegisterShooterWeapon(
            ShooterWeapon weapon,
            GameObject modelPrefab = null,
            Handle handle = null)
        {
            if (weapon == null) return;
            int hash = weapon.Id.Hash;
            s_WeaponRegistry.TryGetValue(hash, out ShooterWeaponRegistryEntry existing);
            s_WeaponRegistry[hash] = new ShooterWeaponRegistryEntry
            {
                Hash = hash,
                Weapon = weapon,
                ModelPrefab = modelPrefab != null ? modelPrefab : existing.ModelPrefab,
                Handle = handle != null ? handle : existing.Handle,
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
        /// Get the full ShooterWeapon registry entry by hash.
        /// </summary>
        public static bool TryGetShooterWeaponRegistryEntry(int hash, out ShooterWeaponRegistryEntry entry)
        {
            return s_WeaponRegistry.TryGetValue(hash, out entry);
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
            public bool TrustedServerOrigin;
        }
        
        private struct QueuedHitRequest
        {
            public uint ClientNetworkId;
            public NetworkShooterHitRequest Request;
            public float ReceivedTime;
            public bool TrustedServerOrigin;
            public bool NativeDamageWillApply;
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
        public float NetworkTime => GetNetworkTimeFunc?.Invoke() ?? Time.time;
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // UNITY LIFECYCLE
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        private void Update()
        {
            if (m_IsClient)
            {
                FlushPendingTransientBroadcasts();
            }

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
            if (!m_IsServer)
            {
                LogDiagnosticsWarning(
                    $"dropped inbound shot request because manager is not server client={clientNetworkId} " +
                    $"actor={request.ActorNetworkId} req={request.RequestId}");
                return;
            }

            LogDiagnostics(
                $"received shot request client={clientNetworkId} actor={request.ActorNetworkId} " +
                $"shooter={request.ShooterNetworkId} req={request.RequestId} corr={request.CorrelationId} " +
                $"weaponHash={request.WeaponHash} muzzle={request.MuzzlePosition} dir={request.ShotDirection}");

            if (!ValidateShooterRequest(clientNetworkId, request.ActorNetworkId, request.CorrelationId, nameof(NetworkShotRequest)))
            {
                LogDiagnosticsWarning(
                    $"shot request failed security validation client={clientNetworkId} actor={request.ActorNetworkId} " +
                    $"req={request.RequestId} corr={request.CorrelationId}");
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
                LogDiagnosticsWarning(
                    $"shot request failed actor binding client={clientNetworkId} actor={request.ActorNetworkId} " +
                    $"shooter={request.ShooterNetworkId} req={request.RequestId}");
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
                LogDiagnosticsWarning(
                    $"shot request rejected because queue is full client={clientNetworkId} actor={request.ActorNetworkId} " +
                    $"req={request.RequestId} queue={m_ServerShotQueue.Count}");
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
                ReceivedTime = Time.time,
                TrustedServerOrigin = false
            });

            LogDiagnostics(
                $"queued shot request client={clientNetworkId} actor={request.ActorNetworkId} " +
                $"req={request.RequestId} queue={m_ServerShotQueue.Count}");
        }

        /// <summary>
        /// [Server] Queue a shot that the authoritative GC2 simulation has already fired. The
        /// trusted path broadcasts presentation and establishes manager-side hit bookkeeping
        /// without replaying the shot or consuming ammunition a second time.
        /// </summary>
        public bool TryServerQueueTrustedShot(NetworkShotRequest request)
        {
            if (!m_IsServer ||
                request.ActorNetworkId == 0 ||
                request.ActorNetworkId != request.ShooterNetworkId)
            {
                return false;
            }

            int queueLimit = Mathf.Max(1, m_MaxShotQueueLength);
            if (m_ServerShotQueue.Count >= queueLimit)
            {
                LogDiagnosticsWarning(
                    $"trusted server shot dropped because queue is full actor={request.ActorNetworkId} " +
                    $"req={request.RequestId} queue={m_ServerShotQueue.Count}/{queueLimit}");
                return false;
            }

            m_ServerShotQueue.Enqueue(new QueuedShotRequest
            {
                ClientNetworkId = 0,
                Request = request,
                ReceivedTime = Time.time,
                TrustedServerOrigin = true
            });
            m_Stats.ShotRequestsReceived++;
            return true;
        }
        
        /// <summary>
        /// [Server] Called when a hit request is received from a client.
        /// </summary>
        public void ReceiveHitRequest(uint clientNetworkId, NetworkShooterHitRequest request)
        {
            if (!m_IsServer)
            {
                LogDiagnosticsWarning(
                    $"dropped inbound hit request because manager is not server client={clientNetworkId} " +
                    $"actor={request.ActorNetworkId} req={request.RequestId}");
                return;
            }

            LogDiagnostics(
                $"received hit request client={clientNetworkId} actor={request.ActorNetworkId} " +
                $"shooter={request.ShooterNetworkId} target={request.TargetNetworkId} req={request.RequestId} " +
                $"sourceShot={request.SourceShotRequestId} weaponHash={request.WeaponHash} point={request.HitPoint}");

            if (!ValidateShooterRequest(clientNetworkId, request.ActorNetworkId, request.CorrelationId, nameof(NetworkShooterHitRequest)))
            {
                LogDiagnosticsWarning(
                    $"hit request failed security validation client={clientNetworkId} actor={request.ActorNetworkId} " +
                    $"req={request.RequestId} corr={request.CorrelationId}");
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
                LogDiagnosticsWarning(
                    $"hit request failed actor binding client={clientNetworkId} actor={request.ActorNetworkId} " +
                    $"shooter={request.ShooterNetworkId} req={request.RequestId}");
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
                LogDiagnosticsWarning(
                    $"hit request rejected because queue is full client={clientNetworkId} actor={request.ActorNetworkId} " +
                    $"req={request.RequestId} queue={m_ServerHitQueue.Count}");
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
                ReceivedTime = Time.time,
                TrustedServerOrigin = false,
                NativeDamageWillApply = false
            });

            LogDiagnostics(
                $"queued hit request client={clientNetworkId} actor={request.ActorNetworkId} " +
                $"req={request.RequestId} queue={m_ServerHitQueue.Count}");
        }

        /// <summary>
        /// [Server] Queue a hit observed by a server-owned shooter. Client ownership, replay, and
        /// source-shot binding checks are intentionally skipped because the collision was produced
        /// by the authoritative simulation itself. All ordinary target validation and broadcast
        /// processing still occurs.
        /// </summary>
        public bool TryServerQueueTrustedHit(
            NetworkShooterHitRequest request,
            bool nativeDamageWillApply = false)
        {
            if (!m_IsServer ||
                request.ActorNetworkId == 0 ||
                request.ActorNetworkId != request.ShooterNetworkId)
            {
                return false;
            }

            int queueLimit = Mathf.Max(1, m_MaxHitQueueLength);
            if (m_ServerHitQueue.Count >= queueLimit)
            {
                LogDiagnosticsWarning(
                    $"trusted server hit dropped because queue is full actor={request.ActorNetworkId} " +
                    $"req={request.RequestId} queue={m_ServerHitQueue.Count}/{queueLimit}");
                return false;
            }

            m_ServerHitQueue.Enqueue(new QueuedHitRequest
            {
                ClientNetworkId = 0,
                Request = request,
                ReceivedTime = Time.time,
                TrustedServerOrigin = true,
                NativeDamageWillApply = nativeDamageWillApply
            });
            m_Stats.HitRequestsReceived++;
            return true;
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
            LogDiagnostics(
                $"processing shot request client={queued.ClientNetworkId} actor={request.ActorNetworkId} " +
                $"req={request.RequestId} corr={request.CorrelationId} weaponHash={request.WeaponHash}");
            
            NetworkShotResponse response;
            if (request.ActorNetworkId == 0 || request.ActorNetworkId != request.ShooterNetworkId)
            {
                LogDiagnosticsWarning(
                    $"shot rejected before controller lookup req={request.RequestId}: actor/shooter mismatch " +
                    $"actor={request.ActorNetworkId} shooter={request.ShooterNetworkId}");
                response = new NetworkShotResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Validated = false,
                    RejectionReason = ShotRejectionReason.CheatSuspected
                };
                if (!queued.TrustedServerOrigin)
                {
                    SendShotResponseToClient?.Invoke(queued.ClientNetworkId, response);
                }
                return;
            }

            NetworkShooterController controller;
            bool hasController = queued.TrustedServerOrigin
                ? m_Controllers.TryGetValue(request.ActorNetworkId, out controller) && controller != null
                : TryGetActorController(
                    queued.ClientNetworkId,
                    request.ActorNetworkId,
                    nameof(NetworkShotRequest),
                    out controller);
            if (!hasController)
            {
                LogDiagnosticsWarning(
                    $"shot rejected before controller lookup req={request.RequestId}: actor controller not found " +
                    $"actor={request.ActorNetworkId} client={queued.ClientNetworkId}");
                response = new NetworkShotResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Validated = false,
                    RejectionReason = ShotRejectionReason.ShooterNotFound
                };

                if (!queued.TrustedServerOrigin)
                {
                    SendShotResponseToClient?.Invoke(queued.ClientNetworkId, response);
                }
                return;
            }

            response = queued.TrustedServerOrigin
                ? ValidateShotRequest(request)
                : controller.ProcessShotRequest(request, queued.ClientNetworkId);
            response.ActorNetworkId = request.ActorNetworkId;
            response.CorrelationId = request.CorrelationId;
            
            if (!queued.TrustedServerOrigin)
            {
                SendShotResponseToClient?.Invoke(queued.ClientNetworkId, response);
                LogDiagnostics(
                    $"shot response sent client={queued.ClientNetworkId} actor={request.ActorNetworkId} " +
                    $"req={request.RequestId} validated={response.Validated} reason={response.RejectionReason}");
            }
            
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
                LogDiagnostics(
                    $"broadcasting shot actor={request.ActorNetworkId} weaponHash={request.WeaponHash} " +
                    $"muzzle={request.MuzzlePosition} end={broadcast.HitPoint}");
                
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
            LogDiagnostics(
                $"processing hit request client={queued.ClientNetworkId} actor={request.ActorNetworkId} " +
                $"req={request.RequestId} sourceShot={request.SourceShotRequestId} target={request.TargetNetworkId} " +
                $"impactProp={request.ImpactPropNetworkId}");
            
            NetworkShooterHitResponse response;
            ulong sourceShotKey = 0;
            if (request.ActorNetworkId == 0 || request.ActorNetworkId != request.ShooterNetworkId)
            {
                LogDiagnosticsWarning(
                    $"hit rejected before controller lookup req={request.RequestId}: actor/shooter mismatch " +
                    $"actor={request.ActorNetworkId} shooter={request.ShooterNetworkId}");
                response = new NetworkShooterHitResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Validated = false,
                    RejectionReason = HitRejectionReason.CheatSuspected
                };
                if (!queued.TrustedServerOrigin)
                {
                    SendHitResponseToClient?.Invoke(queued.ClientNetworkId, response);
                }
                return;
            }

            if (queued.TrustedServerOrigin)
            {
                if (!m_Controllers.TryGetValue(request.ActorNetworkId, out var trustedController) ||
                    trustedController == null)
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
                    // The server generated this collision, so current authoritative target state
                    // is sufficient and no client shot-claim binding is required.
                    response = ValidateHitRequest(request);
                }
            }
            else if (!ValidateHitSourceShot(request, out sourceShotKey, out HitRejectionReason shotBindingRejection))
            {
                LogDiagnosticsWarning(
                    $"hit rejected before controller lookup req={request.RequestId}: source shot invalid " +
                    $"sourceShot={request.SourceShotRequestId} reason={shotBindingRejection}");
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
                LogDiagnosticsWarning(
                    $"hit rejected before controller lookup req={request.RequestId}: actor controller not found " +
                    $"actor={request.ActorNetworkId} client={queued.ClientNetworkId}");
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
            
            if (!queued.TrustedServerOrigin)
            {
                SendHitResponseToClient?.Invoke(queued.ClientNetworkId, response);
                LogDiagnostics(
                    $"hit response sent client={queued.ClientNetworkId} actor={request.ActorNetworkId} " +
                    $"req={request.RequestId} validated={response.Validated} reason={response.RejectionReason} damage={response.Damage:F2}");
            }
            
            if (response.Validated)
            {
                m_Stats.HitsValidated++;

                // Consume one authoritative hit claim from this validated shot.
                RecordValidatedHitClaim(sourceShotKey, request);
                
                // Apply damage on server
                if (!queued.NativeDamageWillApply)
                {
                    ApplyDamageOnServer(request, response.Damage);
                }
                
                bool hasImpactMotion = TryBuildImpactMotion(request, out NetworkShooterImpactMotion impactMotion);

                // Broadcast to all clients
                var broadcast = new NetworkShooterHitBroadcast
                {
                    ShooterNetworkId = request.ActorNetworkId,
                    TargetNetworkId = request.TargetNetworkId,
                    HitPoint = request.HitPoint,
                    HitNormal = request.HitNormal,
                    WeaponHash = request.WeaponHash,
                    BlockResult = (byte)response.BlockResult,
                    MaterialHash = ResolveMaterialHashFunc?.Invoke(request) ?? 0,
                    HasImpactMotion = hasImpactMotion,
                    ImpactMotion = impactMotion
                };
                
                BroadcastHitToAllClients?.Invoke(broadcast);
                m_Stats.HitBroadcastsSent++;
                LogDiagnostics(
                    $"broadcasting hit actor={request.ActorNetworkId} target={request.TargetNetworkId} " +
                    $"weaponHash={request.WeaponHash} point={request.HitPoint} material={broadcast.MaterialHash} " +
                    $"impactProp={request.ImpactPropNetworkId} impactMotion={hasImpactMotion}");
                
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

        private bool TryBuildImpactMotion(
            NetworkShooterHitRequest request,
            out NetworkShooterImpactMotion motion)
        {
            motion = default;

            if (request.IsCharacterHit || request.ImpactPropNetworkId == 0) return false;

            ShooterWeapon weapon = GetShooterWeaponByHash(request.WeaponHash);
            if (weapon == null || !weapon.Fire.ForceEnabled) return false;

            if (!NetworkShooterImpactProp.TryFindExisting(
                    request.ImpactPropNetworkId,
                    out NetworkShooterImpactProp prop))
            {
                LogDiagnosticsWarning(
                    $"validated environment hit has no impact prop prop={request.ImpactPropNetworkId} " +
                    $"actor={request.ActorNetworkId} req={request.RequestId}");
                return false;
            }

            float networkTime = GetNetworkTimeFunc?.Invoke() ?? Time.time;
            if (!prop.TryBuildImpactMotion(
                    request.HitPoint,
                    request.HitNormal,
                    weapon.Fire.Force,
                    networkTime,
                    out motion))
            {
                LogDiagnosticsWarning(
                    $"impact prop rejected motion prop={request.ImpactPropNetworkId} actor={request.ActorNetworkId} " +
                    $"req={request.RequestId} point={request.HitPoint}");
                return false;
            }

            prop.ApplyImpactMotion(motion);
            return true;
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
            bool targetUsesServerNavMesh = UsesServerAuthoritativeNavMesh(targetCharacter, targetNetworkChar);

            if (targetUsesServerNavMesh && m_SuppressFallbackHitReactionsForServerNavMesh)
            {
                if (m_LogHitRequests || NetworkShooterDebug.ForceDiagnostics)
                {
                    Debug.Log(
                        $"[NetworkShooterManager] Suppressed fallback hit reaction for server NavMesh target " +
                        $"target={targetCharacter.name} damage={damage:F2} hitPoint={request.HitPoint} hitNormal={request.HitNormal}",
                        targetCharacter);
                }

                return;
            }

            Vector3 beforeReactionPosition = targetCharacter.transform.position;
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

            if (targetUsesServerNavMesh && m_FlattenServerNavMeshHitReactionDirection)
            {
                incomingDirection = FlattenHitDirectionForNavMesh(
                    incomingDirection,
                    targetCharacter,
                    attackerNetworkChar);
            }

            Vector3 localDirection = targetCharacter.transform.InverseTransformDirection(incomingDirection).normalized;
            if (localDirection.sqrMagnitude < 0.0001f)
            {
                localDirection = Vector3.forward;
            }

            var reactionInput = new ReactionInput(localDirection, Mathf.Max(0f, damage));
            var args = new Args(attackerNetworkChar != null ? attackerNetworkChar.gameObject : null, targetCharacter.gameObject);
            _ = targetCharacter.Combat.GetHitReaction(reactionInput, args, null);

            if (targetUsesServerNavMesh && m_ResnapServerNavMeshAfterHitReaction)
            {
                ResnapServerNavMeshTargetAfterHitReaction(targetCharacter, beforeReactionPosition, request);
            }

            if (m_LogHitRequests)
            {
                Debug.Log(
                    $"[NetworkShooterManager] Applied built-in server reaction damage={damage:F2} target={targetCharacter.name}");
            }
        }

        private static bool UsesServerAuthoritativeNavMesh(
            Character targetCharacter,
            NetworkCharacter targetNetworkChar)
        {
            if (targetCharacter?.Driver is UnitDriverNavmeshNetworkServer) return true;
            if (targetNetworkChar == null || !targetNetworkChar.IsServerInstance) return false;
            if (!targetNetworkChar.IsServerAuthoritativeNPC) return false;
            if (targetCharacter.GetComponent<NavMeshAgent>() == null) return false;

            string driverName = targetCharacter.Driver?.GetType().Name;
            return targetCharacter.Driver is UnitDriverNetworkServer ||
                   (!string.IsNullOrEmpty(driverName) &&
                    driverName.IndexOf("Navmesh", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static Vector3 FlattenHitDirectionForNavMesh(
            Vector3 incomingDirection,
            Character targetCharacter,
            NetworkCharacter attackerNetworkChar)
        {
            Vector3 flattened = Vector3.ProjectOnPlane(incomingDirection, Vector3.up);
            if (flattened.sqrMagnitude > 0.0001f) return flattened.normalized;

            if (targetCharacter != null && attackerNetworkChar != null)
            {
                flattened = Vector3.ProjectOnPlane(
                    targetCharacter.transform.position - attackerNetworkChar.transform.position,
                    Vector3.up);

                if (flattened.sqrMagnitude > 0.0001f) return flattened.normalized;
            }

            return targetCharacter != null ? targetCharacter.transform.forward : Vector3.forward;
        }

        private void ResnapServerNavMeshTargetAfterHitReaction(
            Character targetCharacter,
            Vector3 beforeReactionPosition,
            NetworkShooterHitRequest request)
        {
            if (targetCharacter == null) return;

            NavMeshAgent agent = targetCharacter.GetComponent<NavMeshAgent>();
            if (agent == null) return;

            Vector3 currentPosition = targetCharacter.transform.position;
            float halfHeight = targetCharacter.Motion != null
                ? targetCharacter.Motion.Height * 0.5f
                : 0f;

            Vector3 sampleOrigin = currentPosition - Vector3.up * halfHeight;
            float sampleDistance = Mathf.Max(0.25f, m_ServerNavMeshHitReactionSnapDistance);

            if (!NavMesh.SamplePosition(sampleOrigin, out NavMeshHit hit, sampleDistance, NavMesh.AllAreas))
            {
                return;
            }

            Vector3 snappedRoot = hit.position + Vector3.up * halfHeight;
            float verticalDrift = Mathf.Abs(currentPosition.y - snappedRoot.y);
            bool needsSnap = !agent.isOnNavMesh || verticalDrift > 0.05f;

            if (!needsSnap) return;

            agent.Warp(snappedRoot);

            if (m_LogHitRequests || NetworkShooterDebug.ForceDiagnostics)
            {
                Debug.Log(
                    $"[NetworkShooterManager] Resnapped server NavMesh target after hit reaction " +
                    $"target={targetCharacter.name} before={beforeReactionPosition} current={currentPosition} " +
                    $"snapped={snappedRoot} hitPoint={request.HitPoint}",
                    targetCharacter);
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
                LogDiagnostics(
                    $"routing shot response actor={response.ActorNetworkId} req={response.RequestId} " +
                    $"validated={response.Validated} reason={response.RejectionReason}");
                controller.ReceiveShotResponse(response);
                return;
            }

            LogDiagnosticsWarning(
                $"dropped shot response because controller was not found actor={response.ActorNetworkId} " +
                $"req={response.RequestId} validated={response.Validated}");
        }
        
        /// <summary>
        /// [Client] Called when server sends a hit response.
        /// </summary>
        public void ReceiveHitResponse(NetworkShooterHitResponse response)
        {
            if (response.ActorNetworkId != 0 && m_Controllers.TryGetValue(response.ActorNetworkId, out var controller))
            {
                LogDiagnostics(
                    $"routing hit response actor={response.ActorNetworkId} req={response.RequestId} " +
                    $"validated={response.Validated} reason={response.RejectionReason}");
                controller.ReceiveHitResponse(response);
                return;
            }

            LogDiagnosticsWarning(
                $"dropped hit response because controller was not found actor={response.ActorNetworkId} " +
                $"req={response.RequestId} validated={response.Validated}");
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
                LogDiagnostics(
                    $"routing shot broadcast shooter={broadcast.ShooterNetworkId} weaponHash={broadcast.WeaponHash}");
                controller.ReceiveShotBroadcast(broadcast);
                return;
            }

            EnqueuePendingShotBroadcast(broadcast);
            LogDiagnostics(
                $"queued shot broadcast while shooter controller spawns shooter={broadcast.ShooterNetworkId} " +
                $"weaponHash={broadcast.WeaponHash}");
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

            if (broadcast.HasImpactMotion &&
                !NetworkShooterImpactProp.TryApplyImpactMotion(broadcast.ImpactMotion))
            {
                TrimPendingTransientCapacity();
                m_PendingImpactMotions.Add(new PendingImpactMotion
                {
                    Motion = broadcast.ImpactMotion,
                    ReceivedTime = Time.unscaledTime
                });
                LogDiagnostics(
                    $"queued impact motion while prop registers prop={broadcast.ImpactMotion.PropNetworkId} " +
                    $"shooter={broadcast.ShooterNetworkId} target={broadcast.TargetNetworkId}");
            }
            
            RouteHitBroadcast(broadcast, out bool needsShooter, out bool needsTarget);
            if (needsShooter || needsTarget)
            {
                EnqueuePendingHitBroadcast(broadcast, needsShooter, needsTarget);
            }
        }

        private void RouteHitBroadcast(
            NetworkShooterHitBroadcast broadcast,
            out bool needsShooter,
            out bool needsTarget)
        {
            needsShooter = false;
            needsTarget = false;

            if (broadcast.ShooterNetworkId != 0 &&
                broadcast.ShooterNetworkId == broadcast.TargetNetworkId)
            {
                if (m_Controllers.TryGetValue(broadcast.ShooterNetworkId, out var selfController) &&
                    selfController != null)
                {
                    selfController.ReceiveSelfHitBroadcast(broadcast);
                }
                else
                {
                    needsShooter = true;
                }

                return;
            }

            if (m_Controllers.TryGetValue(broadcast.ShooterNetworkId, out var shooterController) &&
                shooterController != null)
            {
                // The attacker owns shared impact VFX and optimistic reconciliation.
                shooterController.ReceiveHitBroadcast(broadcast);
            }
            else
            {
                needsShooter = broadcast.ShooterNetworkId != 0;
            }

            if (broadcast.TargetNetworkId == 0) return;

            if (m_Controllers.TryGetValue(broadcast.TargetNetworkId, out var targetController) &&
                targetController != null)
            {
                // The target path only owns hit reaction/state notification.
                targetController.ReceiveTargetHitBroadcast(broadcast);
            }
            else
            {
                needsTarget = true;
            }
        }

        private void EnqueuePendingShotBroadcast(NetworkShotBroadcast broadcast)
        {
            TrimPendingTransientCapacity();
            m_PendingShotBroadcasts.Add(new PendingShotBroadcast
            {
                Broadcast = broadcast,
                ReceivedTime = Time.unscaledTime
            });
        }

        private void EnqueuePendingHitBroadcast(
            NetworkShooterHitBroadcast broadcast,
            bool needsShooter,
            bool needsTarget)
        {
            TrimPendingTransientCapacity();
            m_PendingHitBroadcasts.Add(new PendingHitBroadcast
            {
                Broadcast = broadcast,
                ReceivedTime = Time.unscaledTime,
                NeedsShooter = needsShooter,
                NeedsTarget = needsTarget
            });
        }

        private void TrimPendingTransientCapacity()
        {
            int maximum = Mathf.Max(8, m_MaxPendingTransientBroadcasts);
            while (m_PendingShotBroadcasts.Count + m_PendingHitBroadcasts.Count +
                   m_PendingImpactMotions.Count >= maximum)
            {
                float oldestShot = m_PendingShotBroadcasts.Count > 0
                    ? m_PendingShotBroadcasts[0].ReceivedTime
                    : float.MaxValue;
                float oldestHit = m_PendingHitBroadcasts.Count > 0
                    ? m_PendingHitBroadcasts[0].ReceivedTime
                    : float.MaxValue;
                float oldestImpact = m_PendingImpactMotions.Count > 0
                    ? m_PendingImpactMotions[0].ReceivedTime
                    : float.MaxValue;

                if (oldestShot <= oldestHit && oldestShot <= oldestImpact)
                {
                    m_PendingShotBroadcasts.RemoveAt(0);
                }
                else if (oldestHit <= oldestImpact)
                {
                    m_PendingHitBroadcasts.RemoveAt(0);
                }
                else
                {
                    m_PendingImpactMotions.RemoveAt(0);
                }
            }
        }

        private void FlushPendingTransientBroadcasts()
        {
            float now = Time.unscaledTime;
            float lifetime = Mathf.Max(0.05f, m_TransientBroadcastLifetime);

            for (int i = m_PendingShotBroadcasts.Count - 1; i >= 0; i--)
            {
                PendingShotBroadcast pending = m_PendingShotBroadcasts[i];
                if (now - pending.ReceivedTime > lifetime)
                {
                    m_PendingShotBroadcasts.RemoveAt(i);
                    continue;
                }

                if (!m_Controllers.TryGetValue(pending.Broadcast.ShooterNetworkId, out var controller) ||
                    controller == null) continue;

                controller.ReceiveShotBroadcast(pending.Broadcast);
                m_PendingShotBroadcasts.RemoveAt(i);
            }

            for (int i = m_PendingHitBroadcasts.Count - 1; i >= 0; i--)
            {
                PendingHitBroadcast pending = m_PendingHitBroadcasts[i];
                if (now - pending.ReceivedTime > lifetime)
                {
                    m_PendingHitBroadcasts.RemoveAt(i);
                    continue;
                }

                if (pending.Broadcast.ShooterNetworkId == pending.Broadcast.TargetNetworkId)
                {
                    if (!m_Controllers.TryGetValue(pending.Broadcast.ShooterNetworkId, out var selfController) ||
                        selfController == null) continue;

                    selfController.ReceiveSelfHitBroadcast(pending.Broadcast);
                    m_PendingHitBroadcasts.RemoveAt(i);
                    continue;
                }

                if (pending.NeedsShooter &&
                    m_Controllers.TryGetValue(pending.Broadcast.ShooterNetworkId, out var shooterController) &&
                    shooterController != null)
                {
                    shooterController.ReceiveHitBroadcast(pending.Broadcast);
                    pending.NeedsShooter = false;
                }

                if (pending.NeedsTarget &&
                    m_Controllers.TryGetValue(pending.Broadcast.TargetNetworkId, out var targetController) &&
                    targetController != null)
                {
                    targetController.ReceiveTargetHitBroadcast(pending.Broadcast);
                    pending.NeedsTarget = false;
                }

                if (!pending.NeedsShooter && !pending.NeedsTarget)
                {
                    m_PendingHitBroadcasts.RemoveAt(i);
                }
                else
                {
                    m_PendingHitBroadcasts[i] = pending;
                }
            }

            for (int i = m_PendingImpactMotions.Count - 1; i >= 0; i--)
            {
                PendingImpactMotion pending = m_PendingImpactMotions[i];
                if (now - pending.ReceivedTime > lifetime ||
                    NetworkShooterImpactProp.TryApplyImpactMotion(pending.Motion))
                {
                    m_PendingImpactMotions.RemoveAt(i);
                }
            }
        }
    }
}
#endif
