#if GC2_MELEE
using System;
using System.Collections.Generic;
using UnityEngine;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.Melee;

using Arawn.GameCreator2.Networking;
using Arawn.GameCreator2.Networking.Security;

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
    public class NetworkMeleeManager : NetworkSingleton<NetworkMeleeManager>
    {
        // ════════════════════════════════════════════════════════════════════════════════════════
        // CONFIGURATION
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        protected override DuplicatePolicy OnDuplicatePolicy => DuplicatePolicy.DestroyComponent;
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // INSPECTOR
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        [Header("Processing Settings")]
        [Tooltip("Maximum hit requests to process per frame on server.")]
        [SerializeField] private int m_MaxHitsPerFrame = 10;

        [Tooltip("Maximum queued hit requests awaiting processing.")]
        [SerializeField] private int m_MaxHitQueueLength = 512;

        [Tooltip("Maximum queued block requests awaiting processing.")]
        [SerializeField] private int m_MaxBlockQueueLength = 256;

        [Tooltip("Maximum queued skill requests awaiting processing.")]
        [SerializeField] private int m_MaxSkillQueueLength = 256;

        [Tooltip("Maximum queued charge requests awaiting processing.")]
        [SerializeField] private int m_MaxChargeQueueLength = 256;

        [Tooltip("Drop queued requests older than this many seconds.")]
        [SerializeField] private float m_MaxQueueAgeSeconds = 1.5f;
        
        [Tooltip("Maximum time in the past for hit validation (seconds).")]
        [SerializeField] private float m_MaxRewindTime = 0.5f;
        
        [Tooltip("Extra tolerance for hit validation (meters).")]
        [SerializeField] private float m_HitTolerance = 0.3f;

        [Tooltip("Default server melee range used when request data does not provide a range.")]
        [SerializeField] private float m_DefaultMeleeRange = 3f;

        [Header("Hit Presentation")]
        [Tooltip("Presentation-only effects instantiated locally after a hit is confirmed. No Skill gameplay callbacks are replayed.")]
        [SerializeField] private MeleeHitEffectRegistration[] m_HitEffects = Array.Empty<MeleeHitEffectRegistration>();

        [Min(0.5f)]
        [Tooltip("Minimum interval between repeated diagnostics for the same missing hit presentation mapping.")]
        [SerializeField] private float m_MissingPresentationWarningInterval = 10f;

        [Header("Persistent State")]
        [Min(1)]
        [Tooltip("Maximum number of character snapshots retained while their controllers are still spawning.")]
        [SerializeField] private int m_MaxPendingPersistentStates = 128;
        
        [Header("Debug")]
        [SerializeField] private bool m_LogHitRequests = false;
        [SerializeField] private bool m_LogHitBroadcasts = false;
        [SerializeField] private bool m_LogMeleeFlow = false;
        [SerializeField] private bool m_LogSkillFlowDiagnostics = false;
        
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

        /// <summary>Optional server-side damage calculation hook.</summary>
        public Func<NetworkMeleeHitRequest, float> ComputeDamageFunc;

        /// <summary>
        /// Optional server-side damage application hook. Return true when damage was applied.
        /// Returning false lets the built-in melee reaction fallback run.
        /// </summary>
        public Func<NetworkMeleeHitRequest, float, bool> TryApplyDamageFunc;

        /// <summary>Optional server-side damage application hook.</summary>
        public Action<NetworkMeleeHitRequest, float> ApplyDamageFunc;
        
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

        /// <summary>
        /// Raised before the default presentation-only melee effect is instantiated. Set
        /// <see cref="NetworkMeleeHitPresentationContext.Handled"/> to use custom presentation.
        /// </summary>
        public event Action<NetworkMeleeHitPresentationContext> OnHitPresentationRequested;
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // PRIVATE FIELDS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        private bool m_IsServer;
        private bool m_IsClient;
        
        // Controller registry
        private readonly Dictionary<uint, NetworkMeleeController> m_Controllers = new(32);

        // Presentation registry and rate-limited diagnostics
        private readonly Dictionary<int, MeleeHitEffectRegistration> m_HitEffectRegistry = new(32);
        private readonly Dictionary<int, float> m_NextPresentationWarningTime = new(32);

        // Server-owned latest state and receiver-side spawn-order cache
        private readonly Dictionary<uint, NetworkMeleeCharacterSnapshot> m_LatestCharacterStates = new(32);
        private readonly Dictionary<uint, NetworkMeleeCharacterSnapshot> m_PendingCharacterStates = new(32);
        private readonly List<uint> m_PersistentStateKeyBuffer = new(32);
        private float m_NextPersistentStateRetryTime;
        
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
        private NetworkMeleePatchHooks m_PatchHooks;
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // WEAPON / SKILL REGISTRY
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Registry entry for a MeleeWeapon asset.
        /// </summary>
        public struct MeleeWeaponRegistryEntry
        {
            public int Hash;
            public MeleeWeapon Weapon;
            public string Name;
        }
        
        /// <summary>
        /// Registry entry for a Skill asset.
        /// </summary>
        public struct SkillRegistryEntry
        {
            public int Hash;
            public Skill Skill;
            public string Name;
        }
        
        private static readonly Dictionary<int, MeleeWeaponRegistryEntry> s_WeaponRegistry = new(32);
        private static readonly Dictionary<int, SkillRegistryEntry> s_SkillRegistry = new(64);
        
        /// <summary>
        /// Register a MeleeWeapon for network hash-to-asset lookup.
        /// Uses <c>weapon.Id.Hash</c> as the key.
        /// </summary>
        public static void RegisterMeleeWeapon(MeleeWeapon weapon)
        {
            if (weapon == null) return;
            int hash = weapon.Id.Hash;
            s_WeaponRegistry[hash] = new MeleeWeaponRegistryEntry
            {
                Hash = hash,
                Weapon = weapon,
                Name = weapon.name
            };
        }
        
        /// <summary>
        /// Unregister a MeleeWeapon.
        /// </summary>
        public static void UnregisterMeleeWeapon(MeleeWeapon weapon)
        {
            if (weapon == null) return;
            s_WeaponRegistry.Remove(weapon.Id.Hash);
        }
        
        /// <summary>
        /// Get a MeleeWeapon by its <see cref="IdString"/> hash.
        /// </summary>
        /// <returns>The weapon, or <c>null</c> if not registered.</returns>
        public static MeleeWeapon GetMeleeWeaponByHash(int hash)
        {
            return s_WeaponRegistry.TryGetValue(hash, out var entry) ? entry.Weapon : null;
        }
        
        /// <summary>
        /// Check if a MeleeWeapon is registered.
        /// </summary>
        public static bool IsMeleeWeaponRegistered(MeleeWeapon weapon)
        {
            return weapon != null && s_WeaponRegistry.ContainsKey(weapon.Id.Hash);
        }
        
        /// <summary>
        /// Register a Skill for network hash-to-asset lookup.
        /// Uses <see cref="StableHashUtility.GetStableHash(string)"/> on the skill name.
        /// </summary>
        public static void RegisterSkill(Skill skill)
        {
            if (skill == null) return;
            int hash = StableHashUtility.GetStableHash(skill.name);
            s_SkillRegistry[hash] = new SkillRegistryEntry
            {
                Hash = hash,
                Skill = skill,
                Name = skill.name
            };
        }
        
        /// <summary>
        /// Unregister a Skill.
        /// </summary>
        public static void UnregisterSkill(Skill skill)
        {
            if (skill == null) return;
            s_SkillRegistry.Remove(StableHashUtility.GetStableHash(skill.name));
        }
        
        /// <summary>
        /// Get a Skill by its stable hash.
        /// </summary>
        /// <returns>The skill, or <c>null</c> if not registered.</returns>
        public static Skill GetSkillByHash(int hash)
        {
            return s_SkillRegistry.TryGetValue(hash, out var entry) ? entry.Skill : null;
        }
        
        /// <summary>
        /// Check if a Skill is registered.
        /// </summary>
        public static bool IsSkillRegistered(Skill skill)
        {
            return skill != null && s_SkillRegistry.ContainsKey(StableHashUtility.GetStableHash(skill.name));
        }
        
        /// <summary>
        /// Clear all weapon and skill registries.
        /// Call on scene unload or session end.
        /// </summary>
        public static void ClearRegistries()
        {
            s_WeaponRegistry.Clear();
            s_SkillRegistry.Clear();
        }

        /// <summary>Rebuild the inspector-authored, presentation-only hit effect lookup.</summary>
        public void RebuildHitEffectRegistry()
        {
            m_HitEffectRegistry.Clear();
            if (m_HitEffects == null) return;

            for (int i = 0; i < m_HitEffects.Length; i++)
            {
                MeleeHitEffectRegistration registration = m_HitEffects[i];
                if (registration?.Skill == null) continue;

                int skillHash = registration.SkillHash;
                if (skillHash == 0) continue;

                if (m_HitEffectRegistry.ContainsKey(skillHash))
                {
                    Debug.LogWarning(
                        $"[NetworkMeleeManager] Duplicate hit presentation registration for " +
                        $"Skill '{registration.Skill.name}' ({skillHash}). The last entry is used.",
                        this);
                }

                RegisterSkill(registration.Skill);
                m_HitEffectRegistry[skillHash] = registration;
            }
        }

        /// <summary>
        /// Play one presentation-only hit effect on this client. This never invokes Skill.OnHit
        /// or any other damage/gameplay callback.
        /// </summary>
        internal void RequestHitPresentation(NetworkMeleeHitBroadcast broadcast)
        {
            if (!m_IsClient) return;

            Skill skill = GetSkillByHash(broadcast.SkillHash);
            Character attacker = GetCharacterByNetworkId(broadcast.AttackerNetworkId)?.GetComponent<Character>();
            Character target = GetCharacterByNetworkId(broadcast.TargetNetworkId)?.GetComponent<Character>();

            var context = new NetworkMeleeHitPresentationContext(broadcast, skill, attacker, target);
            InvokeHitPresentationSubscribers(context);
            if (context.Handled) return;

            if (!m_HitEffectRegistry.TryGetValue(broadcast.SkillHash, out MeleeHitEffectRegistration registration))
            {
                WarnMissingPresentation(
                    broadcast.SkillHash,
                    (NetworkBlockResult)broadcast.BlockResult,
                    skill == null ? "Skill and hit effect are not registered" : "hit effect is not registered");
                return;
            }

            NetworkBlockResult result = Enum.IsDefined(typeof(NetworkBlockResult), broadcast.BlockResult)
                ? (NetworkBlockResult)broadcast.BlockResult
                : NetworkBlockResult.None;

            GameObject attackerObject = attacker != null ? attacker.gameObject : null;
            GameObject targetObject = target != null ? target.gameObject : null;
            Args args = new Args(attackerObject, targetObject);

            Vector3 worldStrikeDirection = broadcast.StrikeDirection;
            if (target != null && worldStrikeDirection.sqrMagnitude > 0.0001f)
            {
                worldStrikeDirection = target.transform.TransformDirection(worldStrikeDirection);
            }

            Quaternion rotation = worldStrikeDirection.sqrMagnitude > 0.0001f
                ? Quaternion.LookRotation(-worldStrikeDirection.normalized)
                : Quaternion.identity;

            PropertyGetInstantiate effect = registration.GetEffect(result);
            GameObject instance = TryInstantiatePresentation(effect, args, broadcast.HitPoint, rotation);

            // Unconfigured outcome variants fall back to the Default entry.
            if (instance == null && !ReferenceEquals(effect, registration.DefaultEffect))
            {
                instance = TryInstantiatePresentation(
                    registration.DefaultEffect,
                    args,
                    broadcast.HitPoint,
                    rotation);
            }

            if (instance == null)
            {
                WarnMissingPresentation(
                    broadcast.SkillHash,
                    result,
                    $"registered effect for Skill '{registration.Skill.name}' resolved no prefab");
            }
        }

        private void InvokeHitPresentationSubscribers(NetworkMeleeHitPresentationContext context)
        {
            Delegate[] subscribers = OnHitPresentationRequested?.GetInvocationList();
            if (subscribers == null) return;

            for (int i = 0; i < subscribers.Length; i++)
            {
                try
                {
                    ((Action<NetworkMeleeHitPresentationContext>)subscribers[i]).Invoke(context);
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception, this);
                }
            }
        }

        private static GameObject TryInstantiatePresentation(
            PropertyGetInstantiate effect,
            Args args,
            Vector3 position,
            Quaternion rotation)
        {
            if (effect == null) return null;

            try
            {
                return effect.Get(args, position, rotation);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                return null;
            }
        }

        private void WarnMissingPresentation(int skillHash, NetworkBlockResult result, string reason)
        {
            int warningKey = unchecked((skillHash * 397) ^ (int)result);
            float now = Time.unscaledTime;
            if (m_NextPresentationWarningTime.TryGetValue(warningKey, out float nextTime) && now < nextTime)
            {
                return;
            }

            m_NextPresentationWarningTime[warningKey] = now + Mathf.Max(0.5f, m_MissingPresentationWarningInterval);
            Debug.LogWarning(
                $"[NetworkMeleeManager] Cannot present confirmed melee hit skillHash={skillHash} " +
                $"result={result}: {reason}. Gameplay/reaction processing continues.",
                this);
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // STRUCTS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        private struct QueuedHitRequest
        {
            public uint ClientNetworkId;
            public NetworkMeleeHitRequest Request;
            public float ReceivedTime;
            public bool TrustedServerOrigin;
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

        private static NetworkRequestContext BuildContext(uint actorNetworkId, uint correlationId)
        {
            return NetworkRequestContext.Create(actorNetworkId, correlationId);
        }

        private bool ValidateMeleeRequest(uint senderClientId, uint actorNetworkId, uint correlationId, string requestType)
        {
            return SecurityIntegration.ValidateModuleRequest(
                senderClientId,
                BuildContext(actorNetworkId, correlationId),
                "Melee",
                requestType);
        }

        private bool ValidateActorBinding(
            uint senderClientId,
            uint actorNetworkId,
            uint claimedNetworkId,
            string requestType,
            string claimedFieldName)
        {
            if (actorNetworkId == 0 || claimedNetworkId == 0)
            {
                SecurityIntegration.RecordViolation(
                    senderClientId,
                    actorNetworkId,
                    SecurityViolationType.InvalidRequest,
                    "Melee",
                    $"{requestType} missing actor binding values actor={actorNetworkId}, {claimedFieldName}={claimedNetworkId}");
                return false;
            }

            if (actorNetworkId == claimedNetworkId)
            {
                return true;
            }

            SecurityIntegration.RecordViolation(
                senderClientId,
                actorNetworkId,
                SecurityViolationType.ProtocolMismatch,
                "Melee",
                $"{requestType} actor mismatch actor={actorNetworkId}, {claimedFieldName}={claimedNetworkId}");
            return false;
        }

        private bool TryGetActorController(
            uint senderClientId,
            uint actorNetworkId,
            string requestType,
            out NetworkMeleeController controller)
        {
            if (m_Controllers.TryGetValue(actorNetworkId, out controller))
            {
                return true;
            }

            SecurityIntegration.RecordViolation(
                senderClientId,
                actorNetworkId,
                SecurityViolationType.InvalidTarget,
                "Melee",
                $"{requestType} rejected: no registered controller for actor {actorNetworkId}");

            if (m_LogHitRequests || m_LogHitBroadcasts)
            {
                Debug.LogWarning(
                    $"[NetworkMeleeManager] {requestType} rejected: missing controller for actor {actorNetworkId}");
            }

            return false;
        }

        private bool IsQueueAtCapacity<T>(Queue<T> queue, int maxQueueLength, uint senderClientId, uint actorNetworkId, string requestType)
        {
            int safeLimit = Mathf.Max(1, maxQueueLength);
            if (queue.Count < safeLimit) return false;

            SecurityIntegration.RecordViolation(
                senderClientId,
                actorNetworkId,
                SecurityViolationType.RateLimitExceeded,
                "Melee",
                $"{requestType} queue capacity reached ({queue.Count}/{safeLimit})");

            if (m_LogHitRequests || m_LogHitBroadcasts)
            {
                Debug.LogWarning($"[NetworkMeleeManager] Dropped {requestType}: queue full ({queue.Count}/{safeLimit})");
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

        private bool ShouldLogMeleeFlow => m_LogMeleeFlow || m_LogHitRequests || m_LogHitBroadcasts;
        private bool ShouldLogSkillFlow =>
            m_LogSkillFlowDiagnostics ||
            ShouldLogMeleeFlow ||
            NetworkMeleeDebug.ForcePacketDiagnostics;

        private void LogMeleeFlow(string message)
        {
            if (!ShouldLogMeleeFlow) return;
            Debug.Log($"[NetworkMeleeManager] {message}", this);
        }

        private void LogMeleeFlowWarning(string message)
        {
            if (!ShouldLogMeleeFlow) return;
            Debug.LogWarning($"[NetworkMeleeManager] {message}", this);
        }

        private void LogSkillFlow(string message)
        {
            if (!ShouldLogSkillFlow) return;
            Debug.Log($"[NetworkMeleeSkillDebug][Manager] {message}", this);
        }

        private void LogSkillFlowWarning(string message)
        {
            if (!ShouldLogSkillFlow) return;
            Debug.LogWarning($"[NetworkMeleeSkillDebug][Manager] {message}", this);
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // UNITY LIFECYCLE
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        private void Update()
        {
            if (m_IsServer)
            {
                ProcessServerHitQueue();
                ProcessServerBlockQueue();
                ProcessServerSkillQueue();
                ProcessServerChargeQueue();
            }

            if (m_PendingCharacterStates.Count > 0 &&
                Time.unscaledTime >= m_NextPersistentStateRetryTime)
            {
                m_NextPersistentStateRetryTime = Time.unscaledTime + 0.25f;
                FlushPendingCharacterStates();
            }
        }

        private void OnDisable()
        {
            SecurityIntegration.SetModuleServerContext("Melee", false);
            if (m_PatchHooks != null)
            {
                m_PatchHooks.Initialize(false);
            }

            m_PendingCharacterStates.Clear();
            m_LatestCharacterStates.Clear();
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
            RebuildHitEffectRegistry();
            SecurityIntegration.SetModuleServerContext("Melee", isServer);
            SecurityIntegration.EnsureSecurityManagerInitialized(isServer, () => GetNetworkTimeFunc?.Invoke() ?? Time.time);
            SyncPatchHooks();
            
            Debug.Log($"[NetworkMeleeManager] Initialized - Server: {isServer}, Client: {isClient}");
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
                m_PatchHooks = GetComponent<NetworkMeleePatchHooks>();
                if (m_PatchHooks == null)
                {
                    m_PatchHooks = gameObject.AddComponent<NetworkMeleePatchHooks>();
                }
            }

            m_PatchHooks.Initialize(true);
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

            if (m_Controllers.TryGetValue(networkId, out NetworkMeleeController previous) &&
                previous != null &&
                previous != controller)
            {
                // A same-id controller replacement is a registry refresh, not an authoritative
                // despawn. Detach the old subscriptions while preserving the latest snapshot for
                // the replacement that is about to register.
                previous.OnHitDetected -= OnControllerHitDetected;
                previous.OnBlockRequested -= OnControllerBlockRequested;
                previous.OnSkillRequested -= OnControllerSkillRequested;
                previous.OnChargeRequested -= OnControllerChargeRequested;
            }
            
            m_Controllers[networkId] = controller;
            
            // Subscribe to controller events
            controller.OnHitDetected -= OnControllerHitDetected;
            controller.OnHitDetected += OnControllerHitDetected;
            controller.OnBlockRequested -= OnControllerBlockRequested;
            controller.OnBlockRequested += OnControllerBlockRequested;
            controller.OnSkillRequested -= OnControllerSkillRequested;
            controller.OnSkillRequested += OnControllerSkillRequested;
            controller.OnChargeRequested -= OnControllerChargeRequested;
            controller.OnChargeRequested += OnControllerChargeRequested;

            if (m_PendingCharacterStates.TryGetValue(networkId, out NetworkMeleeCharacterSnapshot pendingState) &&
                ApplyCharacterState(controller, pendingState))
            {
                m_PendingCharacterStates.Remove(networkId);
            }

            LogSkillFlow(
                $"registered controller netId={networkId} name={controller.name} " +
                $"server={controller.IsServer} local={controller.IsLocalClient} " +
                $"registeredCount={m_Controllers.Count}");
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
                LogSkillFlow(
                    $"unregistered controller netId={networkId} name={(controller != null ? controller.name : "null")} " +
                    $"registeredCount={m_Controllers.Count}");
            }

            // This method represents an authoritative character removal. Persistent latest-value
            // state must not survive ID reuse and appear in a later player's join snapshot.
            m_LatestCharacterStates.Remove(networkId);
            m_PendingCharacterStates.Remove(networkId);
        }

        /// <summary>Cache a validated weapon state as the server's latest persistent state.</summary>
        public void RecordAuthoritativeWeaponState(uint characterNetworkId, NetworkMeleeWeaponState state)
        {
            if (!m_IsServer || characterNetworkId == 0) return;

            NetworkMeleeCharacterSnapshot snapshot = GetOrCreateLatestState(characterNetworkId);
            snapshot.HasWeaponState = true;
            snapshot.WeaponState = state;
            m_LatestCharacterStates[characterNetworkId] = snapshot;
        }

        /// <summary>
        /// Apply a replicated weapon state now, or retain only the latest value until its
        /// character controller and registered weapon asset are ready.
        /// </summary>
        public void ReceiveWeaponState(uint characterNetworkId, NetworkMeleeWeaponState state)
        {
            if (characterNetworkId == 0) return;

            NetworkMeleeCharacterSnapshot update = NetworkMeleeCharacterSnapshot.Create(characterNetworkId);
            update.HasWeaponState = true;
            update.WeaponState = state;
            ApplyOrQueueCharacterState(update);
        }

        /// <summary>Apply a targeted late-join snapshot with latest-value semantics.</summary>
        public void ReceiveCharacterSnapshot(NetworkMeleeCharacterSnapshot snapshot)
        {
            if (snapshot.CharacterNetworkId == 0) return;

            // Targeted snapshots use full-replacement semantics. An omitted field therefore
            // means the authoritative default, rather than "leave whatever this peer happened
            // to apply before the snapshot arrived".
            if (!snapshot.HasWeaponState)
            {
                snapshot.HasWeaponState = true;
                snapshot.WeaponState = NetworkMeleeWeaponState.None;
            }

            if (!snapshot.HasBlockState)
            {
                snapshot.HasBlockState = true;
                snapshot.BlockState = new NetworkBlockBroadcast
                {
                    CharacterNetworkId = snapshot.CharacterNetworkId,
                    Action = NetworkBlockAction.Lower
                };
            }
            else
            {
                snapshot.BlockState.CharacterNetworkId = snapshot.CharacterNetworkId;
            }

            // A targeted snapshot is a complete point-in-time replacement. Do not merge it
            // with an older spawn-order entry or a cleared weapon/block state could survive a
            // repeated snapshot. Incremental weapon/block broadcasts still use merge semantics.
            ApplyOrQueueCharacterState(snapshot, true);
        }

        /// <summary>Capture all latest server-owned character presentation states.</summary>
        public NetworkMeleeCharacterSnapshot[] CaptureCharacterSnapshots()
        {
            if (!m_IsServer) return Array.Empty<NetworkMeleeCharacterSnapshot>();

            // Ensure server-local characters are represented even before their first change event.
            foreach (var pair in m_Controllers)
            {
                uint networkId = pair.Key;
                NetworkMeleeController controller = pair.Value;
                if (networkId == 0 || controller == null) continue;

                NetworkMeleeCharacterSnapshot snapshot = GetOrCreateLatestState(networkId);
                if (!snapshot.HasWeaponState)
                {
                    snapshot.HasWeaponState = true;
                    snapshot.WeaponState = controller.CurrentWeaponState;
                }

                if (!snapshot.HasBlockState)
                {
                    snapshot.HasBlockState = true;
                    snapshot.BlockState = new NetworkBlockBroadcast
                    {
                        CharacterNetworkId = networkId,
                        Action = NetworkBlockAction.Lower
                    };
                }

                m_LatestCharacterStates[networkId] = snapshot;
            }

            var snapshots = new NetworkMeleeCharacterSnapshot[m_LatestCharacterStates.Count];
            int index = 0;
            foreach (NetworkMeleeCharacterSnapshot snapshot in m_LatestCharacterStates.Values)
            {
                snapshots[index++] = snapshot;
            }

            return snapshots;
        }

        private NetworkMeleeCharacterSnapshot GetOrCreateLatestState(uint characterNetworkId)
        {
            return m_LatestCharacterStates.TryGetValue(characterNetworkId, out NetworkMeleeCharacterSnapshot snapshot)
                ? snapshot
                : NetworkMeleeCharacterSnapshot.Create(characterNetworkId);
        }

        private void RecordAuthoritativeBlockState(NetworkBlockBroadcast blockState)
        {
            if (!m_IsServer || blockState.CharacterNetworkId == 0) return;

            NetworkMeleeCharacterSnapshot snapshot = GetOrCreateLatestState(blockState.CharacterNetworkId);
            snapshot.HasBlockState = true;
            snapshot.BlockState = blockState;
            m_LatestCharacterStates[blockState.CharacterNetworkId] = snapshot;
        }

        private void ApplyOrQueueCharacterState(
            NetworkMeleeCharacterSnapshot update,
            bool fullReplacement = false)
        {
            uint networkId = update.CharacterNetworkId;
            NetworkMeleeCharacterSnapshot pending = !fullReplacement &&
                                                     m_PendingCharacterStates.TryGetValue(
                                                         networkId,
                                                         out NetworkMeleeCharacterSnapshot existing)
                ? existing
                : NetworkMeleeCharacterSnapshot.Create(networkId);

            if (update.HasWeaponState)
            {
                pending.HasWeaponState = true;
                pending.WeaponState = update.WeaponState;
            }

            if (update.HasBlockState)
            {
                pending.HasBlockState = true;
                pending.BlockState = update.BlockState;
                pending.BlockState.CharacterNetworkId = networkId;
            }

            if (m_Controllers.TryGetValue(networkId, out NetworkMeleeController controller) &&
                controller != null &&
                ApplyCharacterState(controller, pending))
            {
                m_PendingCharacterStates.Remove(networkId);
                return;
            }

            StorePendingCharacterState(networkId, pending);
        }

        private bool ApplyCharacterState(
            NetworkMeleeController controller,
            NetworkMeleeCharacterSnapshot snapshot)
        {
            if (controller == null) return false;

            if (snapshot.HasWeaponState)
            {
                MeleeWeapon weapon = snapshot.WeaponState.WeaponHash != 0
                    ? GetMeleeWeaponByHash(snapshot.WeaponState.WeaponHash)
                    : null;

                if (snapshot.WeaponState.WeaponHash != 0 && weapon == null)
                {
                    WarnMissingPersistentWeapon(snapshot.CharacterNetworkId, snapshot.WeaponState.WeaponHash);
                    return false;
                }

                controller.ApplyRemoteWeaponState(snapshot.WeaponState, weapon);
            }

            if (snapshot.HasBlockState)
            {
                NetworkBlockBroadcast blockState = snapshot.BlockState;
                blockState.CharacterNetworkId = snapshot.CharacterNetworkId;
                controller.ReceiveBlockBroadcast(blockState);
            }

            return true;
        }

        private void StorePendingCharacterState(uint networkId, NetworkMeleeCharacterSnapshot state)
        {
            int limit = Mathf.Max(1, m_MaxPendingPersistentStates);
            if (!m_PendingCharacterStates.ContainsKey(networkId) && m_PendingCharacterStates.Count >= limit)
            {
                // Persistent state is latest-value data. Evict one oldest insertion rather than
                // allowing an unbounded spawn-order cache.
                uint evictedId = 0;
                foreach (uint candidateId in m_PendingCharacterStates.Keys)
                {
                    evictedId = candidateId;
                    break;
                }

                if (evictedId != 0)
                {
                    m_PendingCharacterStates.Remove(evictedId);
                    Debug.LogWarning(
                        $"[NetworkMeleeManager] Pending melee state cache reached {limit}; " +
                        $"evicted character {evictedId}.",
                        this);
                }
            }

            m_PendingCharacterStates[networkId] = state;
        }

        private void FlushPendingCharacterStates()
        {
            m_PersistentStateKeyBuffer.Clear();
            foreach (uint networkId in m_PendingCharacterStates.Keys)
            {
                m_PersistentStateKeyBuffer.Add(networkId);
            }

            for (int i = 0; i < m_PersistentStateKeyBuffer.Count; i++)
            {
                uint networkId = m_PersistentStateKeyBuffer[i];
                if (!m_Controllers.TryGetValue(networkId, out NetworkMeleeController controller) ||
                    controller == null ||
                    !m_PendingCharacterStates.TryGetValue(networkId, out NetworkMeleeCharacterSnapshot state))
                {
                    continue;
                }

                if (ApplyCharacterState(controller, state))
                {
                    m_PendingCharacterStates.Remove(networkId);
                }
            }
        }

        private void WarnMissingPersistentWeapon(uint networkId, int weaponHash)
        {
            int warningKey = unchecked((weaponHash * 397) ^ 0x4D575354);
            float now = Time.unscaledTime;
            if (m_NextPresentationWarningTime.TryGetValue(warningKey, out float nextTime) && now < nextTime)
            {
                return;
            }

            m_NextPresentationWarningTime[warningKey] = now + Mathf.Max(0.5f, m_MissingPresentationWarningInterval);
            Debug.LogWarning(
                $"[NetworkMeleeManager] Deferring melee weapon state for character {networkId}: " +
                $"weapon hash {weaponHash} is not registered yet.",
                this);
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
            if (!m_IsClient)
            {
                LogMeleeFlowWarning(
                    $"dropped local block request because manager is not client. actor={request.ActorNetworkId} " +
                    $"action={request.Action} shieldHash={request.ShieldHash}");
                return;
            }
            
            LogMeleeFlow(
                $"sending block request to server actor={request.ActorNetworkId} req={request.RequestId} " +
                $"corr={request.CorrelationId} action={request.Action} shieldHash={request.ShieldHash}");
            SendBlockRequestToServer?.Invoke(request);
            OnBlockRequestSent?.Invoke(request);
        }
        
        private void OnControllerSkillRequested(NetworkSkillRequest request)
        {
            if (!m_IsClient)
            {
                LogSkillFlowWarning(
                    $"dropped local skill request because manager is not client actor={request.ActorNetworkId} " +
                    $"req={request.RequestId} corr={request.CorrelationId} skillHash={request.SkillHash} weaponHash={request.WeaponHash}");
                LogMeleeFlowWarning(
                    $"dropped local skill request because manager is not client. actor={request.ActorNetworkId} " +
                    $"skillHash={request.SkillHash} weaponHash={request.WeaponHash}");
                return;
            }
            
            LogSkillFlow(
                $"sending skill request to server actor={request.ActorNetworkId} req={request.RequestId} corr={request.CorrelationId} " +
                $"skillHash={request.SkillHash} weaponHash={request.WeaponHash} combo={request.ComboNodeId} target={request.TargetNetworkId} " +
                $"sendDelegate={(SendSkillRequestToServer != null)}");
            LogMeleeFlow(
                $"sending skill request to server actor={request.ActorNetworkId} req={request.RequestId} corr={request.CorrelationId} " +
                $"skillHash={request.SkillHash} weaponHash={request.WeaponHash} combo={request.ComboNodeId}");
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
            if (!ValidateMeleeRequest(clientNetworkId, request.ActorNetworkId, request.CorrelationId, nameof(NetworkMeleeHitRequest)))
            {
                SendHitResponseToClient?.Invoke(clientNetworkId, new NetworkMeleeHitResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Validated = false,
                    RejectionReason = MeleeHitRejectionReason.CheatSuspected
                });
                return;
            }
            if (!ValidateActorBinding(clientNetworkId, request.ActorNetworkId, request.AttackerNetworkId, nameof(NetworkMeleeHitRequest), nameof(request.AttackerNetworkId)))
            {
                SendHitResponseToClient?.Invoke(clientNetworkId, new NetworkMeleeHitResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Validated = false,
                    RejectionReason = MeleeHitRejectionReason.CheatSuspected
                });
                return;
            }
            
            m_Stats.HitRequestsReceived++;

            if (IsQueueAtCapacity(
                    m_ServerHitQueue,
                    m_MaxHitQueueLength,
                    clientNetworkId,
                    request.ActorNetworkId,
                    nameof(NetworkMeleeHitRequest)))
            {
                SendHitResponseToClient?.Invoke(clientNetworkId, new NetworkMeleeHitResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Validated = false,
                    RejectionReason = MeleeHitRejectionReason.CheatSuspected
                });
                return;
            }
            
            // Queue for processing
            m_ServerHitQueue.Enqueue(new QueuedHitRequest
            {
                ClientNetworkId = clientNetworkId,
                Request = request,
                ReceivedTime = Time.time,
                TrustedServerOrigin = false
            });
        }

        /// <summary>
        /// [Server] Queue a hit observed by a server-owned actor. This deliberately bypasses
        /// client ownership and request-security checks because there is no remote sender, while
        /// retaining the ordinary server validation, damage, block, reaction, and broadcast path.
        /// </summary>
        public bool TryServerQueueTrustedHit(NetworkMeleeHitRequest request)
        {
            if (!m_IsServer ||
                request.ActorNetworkId == 0 ||
                request.ActorNetworkId != request.AttackerNetworkId)
            {
                return false;
            }

            int queueLimit = Mathf.Max(1, m_MaxHitQueueLength);
            if (m_ServerHitQueue.Count >= queueLimit)
            {
                LogMeleeFlowWarning(
                    $"trusted server hit dropped because queue is full actor={request.ActorNetworkId} " +
                    $"req={request.RequestId} queue={m_ServerHitQueue.Count}/{queueLimit}");
                return false;
            }

            m_ServerHitQueue.Enqueue(new QueuedHitRequest
            {
                ClientNetworkId = 0,
                Request = request,
                ReceivedTime = Time.time,
                TrustedServerOrigin = true
            });
            m_Stats.HitRequestsReceived++;
            return true;
        }
        
        private void ProcessServerHitQueue()
        {
            int staleDropped = DropStaleRequests(m_ServerHitQueue, m_MaxQueueAgeSeconds, queued => queued.ReceivedTime);
            if (staleDropped > 0 && (m_LogHitRequests || m_LogHitBroadcasts))
            {
                Debug.LogWarning($"[NetworkMeleeManager] Dropped {staleDropped} stale hit requests");
            }

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
            if (request.ActorNetworkId == 0 || request.ActorNetworkId != request.AttackerNetworkId)
            {
                if (!queued.TrustedServerOrigin)
                {
                    SendHitResponseToClient?.Invoke(queued.ClientNetworkId, new NetworkMeleeHitResponse
                    {
                        RequestId = request.RequestId,
                        ActorNetworkId = request.ActorNetworkId,
                        CorrelationId = request.CorrelationId,
                        Validated = false,
                        RejectionReason = MeleeHitRejectionReason.CheatSuspected
                    });
                }
                return;
            }

            NetworkMeleeController attackerController;
            bool hasAttackerController = queued.TrustedServerOrigin
                ? m_Controllers.TryGetValue(request.ActorNetworkId, out attackerController) &&
                  attackerController != null
                : TryGetActorController(
                    queued.ClientNetworkId,
                    request.ActorNetworkId,
                    nameof(NetworkMeleeHitRequest),
                    out attackerController);
            if (!hasAttackerController)
            {
                if (!queued.TrustedServerOrigin)
                {
                    SendHitResponseToClient?.Invoke(queued.ClientNetworkId, new NetworkMeleeHitResponse
                    {
                        RequestId = request.RequestId,
                        ActorNetworkId = request.ActorNetworkId,
                        CorrelationId = request.CorrelationId,
                        Validated = false,
                        RejectionReason = MeleeHitRejectionReason.AttackerNotFound
                    });
                }
                return;
            }

            NetworkMeleeHitResponse response = attackerController.ProcessHitRequest(request, queued.ClientNetworkId);
            response.ActorNetworkId = request.ActorNetworkId;
            response.CorrelationId = request.CorrelationId;
            
            // Trusted server observations have no remote requester to answer.
            if (!queued.TrustedServerOrigin)
            {
                SendHitResponseToClient?.Invoke(queued.ClientNetworkId, response);
            }
            
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
                    AttackerNetworkId = request.ActorNetworkId,
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
                        request.ActorNetworkId,
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
                    Debug.Log($"[NetworkMeleeManager] Hit broadcast: {request.ActorNetworkId} -> {request.TargetNetworkId}{blockStr}");
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
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
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
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
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
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
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
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
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
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Validated = false,
                    RejectionReason = MeleeHitRejectionReason.TargetDodged
                };
            }
            
            uint attackerNetworkId = request.ActorNetworkId != 0 ? request.ActorNetworkId : request.AttackerNetworkId;
            var attackerNetworkChar = GetCharacterByNetworkId(attackerNetworkId);
            if (attackerNetworkChar == null)
            {
                return new NetworkMeleeHitResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Validated = false,
                    RejectionReason = MeleeHitRejectionReason.AttackerNotFound
                };
            }

            var attackerCharacter = attackerNetworkChar.GetComponent<Character>();
            if (attackerCharacter == null || attackerCharacter.IsDead)
            {
                return new NetworkMeleeHitResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Validated = false,
                    RejectionReason = MeleeHitRejectionReason.AttackerNotFound
                };
            }

            // Validate range using current authoritative positions.
            float maxRange = Mathf.Max(0.1f, m_DefaultMeleeRange) + m_HitTolerance;
            float distance = Vector3.Distance(attackerCharacter.transform.position, targetCharacter.transform.position);
            if (distance > maxRange)
            {
                return new NetworkMeleeHitResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Validated = false,
                    RejectionReason = MeleeHitRejectionReason.OutOfRange
                };
            }

            if (request.StrikeDirection.sqrMagnitude < 0.01f)
            {
                return new NetworkMeleeHitResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Validated = false,
                    RejectionReason = MeleeHitRejectionReason.InvalidPhase
                };
            }
            
            // Valid hit
            return new NetworkMeleeHitResponse
            {
                RequestId = request.RequestId,
                ActorNetworkId = request.ActorNetworkId,
                CorrelationId = request.CorrelationId,
                Validated = true,
                RejectionReason = MeleeHitRejectionReason.None,
                Damage = Mathf.Max(0f, ComputeDamageFunc?.Invoke(request) ?? 10f),
                PoiseBroken = false
            };
        }
        
        private void ApplyDamageOnServer(NetworkMeleeHitRequest request, float damage)
        {
            if (float.IsNaN(damage) || float.IsInfinity(damage) || damage <= 0f) return;

            // Get target character
            var targetNetworkChar = GetCharacterByNetworkId(request.TargetNetworkId);
            if (targetNetworkChar == null) return;
            
            var targetCharacter = targetNetworkChar.GetComponent<Character>();
            if (targetCharacter == null) return;
            
            if (TryApplyDamageFunc != null)
            {
                try
                {
                    if (TryApplyDamageFunc.Invoke(request, damage)) return;
                }
                catch (Exception ex)
                {
                    Debug.LogError(
                        $"[NetworkMeleeManager] TryApplyDamageFunc threw an exception. " +
                        $"Falling back to built-in reaction damage path.\n{ex.Message}");
                }
            }

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
                        $"[NetworkMeleeManager] ApplyDamageFunc threw an exception. " +
                        $"Falling back to built-in reaction damage path.\n{ex.Message}");
                }
            }

            uint attackerNetworkId = request.ActorNetworkId != 0 ? request.ActorNetworkId : request.AttackerNetworkId;
            var attackerNetworkChar = GetCharacterByNetworkId(attackerNetworkId);
            Vector3 localDirection = request.StrikeDirection.sqrMagnitude > 0.0001f
                ? request.StrikeDirection.normalized
                : attackerNetworkChar != null
                    ? targetCharacter.transform.InverseTransformDirection(
                        (targetCharacter.transform.position - attackerNetworkChar.transform.position).normalized)
                    : Vector3.forward;
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
                    $"[NetworkMeleeManager] Applied built-in server reaction damage={damage:F2} target={targetCharacter.name}");
            }
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // SERVER-SIDE: BLOCK REQUEST PROCESSING
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// [Server] Called when a block request is received from a client.
        /// </summary>
        public void ReceiveBlockRequest(uint clientNetworkId, NetworkBlockRequest request)
        {
            if (!m_IsServer)
            {
                LogMeleeFlowWarning(
                    $"dropped block request on non-server manager client={clientNetworkId} actor={request.ActorNetworkId} " +
                    $"action={request.Action}");
                return;
            }

            LogMeleeFlow(
                $"received block request client={clientNetworkId} actor={request.ActorNetworkId} req={request.RequestId} " +
                $"corr={request.CorrelationId} action={request.Action} shieldHash={request.ShieldHash}");

            if (!ValidateMeleeRequest(clientNetworkId, request.ActorNetworkId, request.CorrelationId, nameof(NetworkBlockRequest)))
            {
                LogMeleeFlowWarning(
                    $"rejected block request in security validation client={clientNetworkId} actor={request.ActorNetworkId} " +
                    $"req={request.RequestId} corr={request.CorrelationId} action={request.Action}");
                SendBlockResponseToClient?.Invoke(clientNetworkId, new NetworkBlockResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Validated = false,
                    RejectionReason = BlockRejectionReason.CheatSuspected
                });
                return;
            }
            
            m_Stats.BlockRequestsReceived++;

            if (IsQueueAtCapacity(
                    m_ServerBlockQueue,
                    m_MaxBlockQueueLength,
                    clientNetworkId,
                    request.ActorNetworkId,
                    nameof(NetworkBlockRequest)))
            {
                LogMeleeFlowWarning(
                    $"rejected block request because queue is full client={clientNetworkId} actor={request.ActorNetworkId}");
                SendBlockResponseToClient?.Invoke(clientNetworkId, new NetworkBlockResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Validated = false,
                    RejectionReason = BlockRejectionReason.CheatSuspected
                });
                return;
            }
            
            m_ServerBlockQueue.Enqueue(new QueuedBlockRequest
            {
                ClientNetworkId = clientNetworkId,
                Request = request,
                ReceivedTime = Time.time
            });
            LogMeleeFlow($"queued block request actor={request.ActorNetworkId} queueCount={m_ServerBlockQueue.Count}");
        }
        
        private void ProcessServerBlockQueue()
        {
            int staleDropped = DropStaleRequests(m_ServerBlockQueue, m_MaxQueueAgeSeconds, queued => queued.ReceivedTime);
            if (staleDropped > 0 && (m_LogHitRequests || m_LogHitBroadcasts))
            {
                Debug.LogWarning($"[NetworkMeleeManager] Dropped {staleDropped} stale block requests");
            }

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
            if (!m_Controllers.TryGetValue(request.ActorNetworkId, out var controller))
            {
                LogMeleeFlowWarning(
                    $"rejected block request: no controller for actor={request.ActorNetworkId} req={request.RequestId}");
                SendBlockResponseToClient?.Invoke(queued.ClientNetworkId, new NetworkBlockResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Validated = false,
                    RejectionReason = BlockRejectionReason.InvalidState
                });
                return;
            }
            
            // Process request
            var response = controller.ProcessBlockRequest(request, queued.ClientNetworkId);
            response.ActorNetworkId = request.ActorNetworkId;
            response.CorrelationId = request.CorrelationId;
            LogMeleeFlow(
                $"processed block request actor={request.ActorNetworkId} req={request.RequestId} " +
                $"action={request.Action} validated={response.Validated} reason={response.RejectionReason}");
            
            // Send response to client
            SendBlockResponseToClient?.Invoke(queued.ClientNetworkId, response);
            
            if (response.Validated)
            {
                m_Stats.BlocksValidated++;
                
                // Broadcast block state to all clients
                var broadcast = new NetworkBlockBroadcast
                {
                    CharacterNetworkId = request.ActorNetworkId,
                    Action = request.Action,
                    ServerTimestamp = response.ServerBlockStartTime,
                    ShieldHash = request.ShieldHash
                };
                
                LogMeleeFlow(
                    $"broadcasting block actor={broadcast.CharacterNetworkId} action={broadcast.Action} " +
                    $"shieldHash={broadcast.ShieldHash} serverTime={broadcast.ServerTimestamp:F3}");
                RecordAuthoritativeBlockState(broadcast);
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
            if (!m_IsServer)
            {
                LogSkillFlowWarning(
                    $"dropped skill request on non-server manager client={clientNetworkId} actor={request.ActorNetworkId} " +
                    $"req={request.RequestId} corr={request.CorrelationId}");
                LogMeleeFlowWarning(
                    $"dropped skill request on non-server manager client={clientNetworkId} actor={request.ActorNetworkId}");
                return;
            }
            LogSkillFlow(
                $"received skill request client={clientNetworkId} actor={request.ActorNetworkId} req={request.RequestId} " +
                $"corr={request.CorrelationId} skillHash={request.SkillHash} weaponHash={request.WeaponHash} combo={request.ComboNodeId} " +
                $"queueCount={m_ServerSkillQueue.Count}");
            LogMeleeFlow(
                $"received skill request client={clientNetworkId} actor={request.ActorNetworkId} req={request.RequestId} " +
                $"corr={request.CorrelationId} skillHash={request.SkillHash} weaponHash={request.WeaponHash} combo={request.ComboNodeId}");
            if (!ValidateMeleeRequest(clientNetworkId, request.ActorNetworkId, request.CorrelationId, nameof(NetworkSkillRequest)))
            {
                LogSkillFlowWarning(
                    $"rejected skill request in security validation client={clientNetworkId} actor={request.ActorNetworkId} " +
                    $"req={request.RequestId} corr={request.CorrelationId}");
                LogMeleeFlowWarning(
                    $"rejected skill request in security validation client={clientNetworkId} actor={request.ActorNetworkId} " +
                    $"req={request.RequestId} corr={request.CorrelationId}");
                SendSkillResponseToClient?.Invoke(clientNetworkId, new NetworkSkillResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Validated = false,
                    RejectionReason = SkillRejectionReason.CheatSuspected
                });
                return;
            }
            
            m_Stats.SkillRequestsReceived++;

            if (IsQueueAtCapacity(
                    m_ServerSkillQueue,
                    m_MaxSkillQueueLength,
                    clientNetworkId,
                    request.ActorNetworkId,
                    nameof(NetworkSkillRequest)))
            {
                LogSkillFlowWarning(
                    $"rejected skill request because queue is full client={clientNetworkId} actor={request.ActorNetworkId} " +
                    $"queueCount={m_ServerSkillQueue.Count} max={m_MaxSkillQueueLength}");
                LogMeleeFlowWarning(
                    $"rejected skill request because queue is full client={clientNetworkId} actor={request.ActorNetworkId}");
                SendSkillResponseToClient?.Invoke(clientNetworkId, new NetworkSkillResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Validated = false,
                    RejectionReason = SkillRejectionReason.CheatSuspected
                });
                return;
            }
            
            m_ServerSkillQueue.Enqueue(new QueuedSkillRequest
            {
                ClientNetworkId = clientNetworkId,
                Request = request,
                ReceivedTime = Time.time
            });
            LogSkillFlow($"queued skill request actor={request.ActorNetworkId} queueCount={m_ServerSkillQueue.Count}");
            LogMeleeFlow($"queued skill request actor={request.ActorNetworkId} queueCount={m_ServerSkillQueue.Count}");
        }
        
        private void ProcessServerSkillQueue()
        {
            int staleDropped = DropStaleRequests(m_ServerSkillQueue, m_MaxQueueAgeSeconds, queued => queued.ReceivedTime);
            if (staleDropped > 0 && (m_LogHitRequests || m_LogHitBroadcasts))
            {
                Debug.LogWarning($"[NetworkMeleeManager] Dropped {staleDropped} stale skill requests");
            }

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
            if (!m_Controllers.TryGetValue(request.ActorNetworkId, out var controller))
            {
                LogSkillFlowWarning(
                    $"rejected skill request: no controller for actor={request.ActorNetworkId} req={request.RequestId} " +
                    $"registeredControllers={m_Controllers.Count}");
                LogMeleeFlowWarning(
                    $"rejected skill request: no controller for actor={request.ActorNetworkId} req={request.RequestId}");
                SendSkillResponseToClient?.Invoke(queued.ClientNetworkId, new NetworkSkillResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Validated = false,
                    RejectionReason = SkillRejectionReason.CheatSuspected
                });
                return;
            }
            
            // Process request
            var response = controller.ProcessSkillRequest(request, queued.ClientNetworkId);
            response.ActorNetworkId = request.ActorNetworkId;
            response.CorrelationId = request.CorrelationId;
            LogMeleeFlow(
                $"processed skill request actor={request.ActorNetworkId} req={request.RequestId} " +
                $"validated={response.Validated} reason={response.RejectionReason}");
            LogSkillFlow(
                $"processed skill request actor={request.ActorNetworkId} req={request.RequestId} " +
                $"validated={response.Validated} reason={response.RejectionReason} sendResponse={(SendSkillResponseToClient != null)}");
            
            // Send response to client
            SendSkillResponseToClient?.Invoke(queued.ClientNetworkId, response);
            
            if (response.Validated)
            {
                m_Stats.SkillsValidated++;
                
                // Broadcast skill execution to all clients
                var broadcast = new NetworkSkillBroadcast
                {
                    CharacterNetworkId = request.ActorNetworkId,
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
                
                LogMeleeFlow(
                    $"broadcasting skill actor={broadcast.CharacterNetworkId} skillHash={broadcast.SkillHash} " +
                    $"weaponHash={broadcast.WeaponHash} combo={broadcast.ComboNodeId}");
                LogSkillFlow(
                    $"broadcasting skill actor={broadcast.CharacterNetworkId} skillHash={broadcast.SkillHash} " +
                    $"weaponHash={broadcast.WeaponHash} combo={broadcast.ComboNodeId} broadcastDelegate={(BroadcastSkillToAllClients != null)}");
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
            if (!ValidateMeleeRequest(clientNetworkId, request.ActorNetworkId, request.CorrelationId, nameof(NetworkChargeRequest)))
            {
                SendChargeResponseToClient?.Invoke(clientNetworkId, new NetworkChargeResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Validated = false
                });
                return;
            }
            
            if (IsQueueAtCapacity(
                    m_ServerChargeQueue,
                    m_MaxChargeQueueLength,
                    clientNetworkId,
                    request.ActorNetworkId,
                    nameof(NetworkChargeRequest)))
            {
                SendChargeResponseToClient?.Invoke(clientNetworkId, new NetworkChargeResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Validated = false
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
            if (staleDropped > 0 && (m_LogHitRequests || m_LogHitBroadcasts))
            {
                Debug.LogWarning($"[NetworkMeleeManager] Dropped {staleDropped} stale charge requests");
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
            
            // Find character's controller
            if (!m_Controllers.TryGetValue(request.ActorNetworkId, out var controller))
            {
                SendChargeResponseToClient?.Invoke(queued.ClientNetworkId, new NetworkChargeResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Validated = false
                });
                return;
            }
            
            // Process request
            var response = controller.ProcessChargeRequest(request, queued.ClientNetworkId);
            response.ActorNetworkId = request.ActorNetworkId;
            response.CorrelationId = request.CorrelationId;
            
            // Send response to client
            SendChargeResponseToClient?.Invoke(queued.ClientNetworkId, response);
            
            if (response.Validated)
            {
                // Broadcast charge start to all clients
                var broadcast = new NetworkChargeBroadcast
                {
                    CharacterNetworkId = request.ActorNetworkId,
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
            if (response.ActorNetworkId != 0 && m_Controllers.TryGetValue(response.ActorNetworkId, out var controller))
            {
                controller.ReceiveHitResponse(response);
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
            
            m_Controllers.TryGetValue(broadcast.AttackerNetworkId, out NetworkMeleeController attackerCtrl);
            m_Controllers.TryGetValue(broadcast.TargetNetworkId, out NetworkMeleeController targetCtrl);

            // The attacker owns shared impact presentation and optimistic reconciliation. The
            // target owns reaction/reconciliation notification only. This guarantees one local
            // presentation even when both controllers exist on the same peer.
            if (attackerCtrl != null)
            {
                bool alsoTarget = targetCtrl != null && ReferenceEquals(attackerCtrl, targetCtrl);
                attackerCtrl.ReceiveHitBroadcastAsAttacker(broadcast, alsoTarget);
            }
            if (targetCtrl != null && !ReferenceEquals(attackerCtrl, targetCtrl))
            {
                targetCtrl.ReceiveHitBroadcastAsTarget(broadcast);
            }
        }
        
        /// <summary>
        /// [Client] Called when server sends a block response.
        /// </summary>
        public void ReceiveBlockResponse(NetworkBlockResponse response)
        {
            LogMeleeFlow(
                $"received block response actor={response.ActorNetworkId} req={response.RequestId} " +
                $"validated={response.Validated} reason={response.RejectionReason} " +
                $"hasController={m_Controllers.ContainsKey(response.ActorNetworkId)}");
            if (response.ActorNetworkId != 0 && m_Controllers.TryGetValue(response.ActorNetworkId, out var controller))
            {
                controller.ReceiveBlockResponse(response);
            }
        }
        
        /// <summary>
        /// [Client] Called when server broadcasts block state.
        /// </summary>
        public void ReceiveBlockBroadcast(NetworkBlockBroadcast broadcast)
        {
            LogMeleeFlow(
                $"received block broadcast actor={broadcast.CharacterNetworkId} action={broadcast.Action} " +
                $"shieldHash={broadcast.ShieldHash} hasController={m_Controllers.ContainsKey(broadcast.CharacterNetworkId)}");

            NetworkMeleeCharacterSnapshot update = NetworkMeleeCharacterSnapshot.Create(
                broadcast.CharacterNetworkId);
            update.HasBlockState = true;
            update.BlockState = broadcast;
            ApplyOrQueueCharacterState(update);
        }
        
        /// <summary>
        /// [Client] Called when server sends a skill response.
        /// </summary>
        public void ReceiveSkillResponse(NetworkSkillResponse response)
        {
            LogSkillFlow(
                $"received skill response actor={response.ActorNetworkId} req={response.RequestId} corr={response.CorrelationId} " +
                $"validated={response.Validated} reason={response.RejectionReason} " +
                $"hasController={m_Controllers.ContainsKey(response.ActorNetworkId)}");
            if (response.ActorNetworkId != 0 && m_Controllers.TryGetValue(response.ActorNetworkId, out var controller))
            {
                controller.ReceiveSkillResponse(response);
            }
        }
        
        /// <summary>
        /// [Client] Called when server broadcasts skill execution.
        /// </summary>
        public void ReceiveSkillBroadcast(NetworkSkillBroadcast broadcast)
        {
            LogSkillFlow(
                $"received skill broadcast actor={broadcast.CharacterNetworkId} skillHash={broadcast.SkillHash} " +
                $"weaponHash={broadcast.WeaponHash} combo={broadcast.ComboNodeId} " +
                $"hasController={m_Controllers.ContainsKey(broadcast.CharacterNetworkId)}");
            LogMeleeFlow(
                $"received skill broadcast actor={broadcast.CharacterNetworkId} skillHash={broadcast.SkillHash} " +
                $"weaponHash={broadcast.WeaponHash} hasController={m_Controllers.ContainsKey(broadcast.CharacterNetworkId)}");
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
            if (response.ActorNetworkId != 0 && m_Controllers.TryGetValue(response.ActorNetworkId, out var controller))
            {
                controller.ReceiveChargeResponse(response);
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
            LogMeleeFlow(
                $"received reaction broadcast target={broadcast.CharacterNetworkId} from={broadcast.FromNetworkId} " +
                $"hasController={m_Controllers.ContainsKey(broadcast.CharacterNetworkId)}");
            if (m_Controllers.TryGetValue(broadcast.CharacterNetworkId, out var ctrl))
            {
                ctrl.ReceiveReactionBroadcast(broadcast);
            }
        }
    }
}
#endif
