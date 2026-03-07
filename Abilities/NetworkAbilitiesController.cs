using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using DaimahouGames.Runtime.Abilities;
using DaimahouGames.Runtime.Pawns;
using DaimahouGames.Runtime.Core.Common;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Common;
using Arawn.GameCreator2.Networking.Security;

// Suppress warnings for unused events and fields that are part of public API hooks
#pragma warning disable CS0067 // Event is never used
#pragma warning disable CS0414 // Field is assigned but never used

namespace Arawn.GameCreator2.Networking
{
    /// <summary>
    /// Server-authoritative controller for DaimahouGames Abilities system.
    /// Handles ability casting, cooldowns, projectiles, and impacts with full server validation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This component works alongside the Abilities system without modifying it.
    /// On clients, it intercepts cast requests and sends them to the server.
    /// On the server, it validates and applies changes, then broadcasts to all clients.
    /// </para>
    /// <para>
    /// Key features:
    /// - Server-authoritative ability casting with requirement validation
    /// - Server-managed cooldowns (prevents client-side cooldown manipulation)
    /// - Server-spawned projectiles and impacts
    /// - Target validation and range checking
    /// - Support for channeled and single-activation abilities
    /// </para>
    /// </remarks>
    [AddComponentMenu("Game Creator/Network/Network Abilities Controller")]
    [DefaultExecutionOrder(ApplicationManager.EXECUTION_ORDER_DEFAULT)]
    public partial class NetworkAbilitiesController : NetworkSingleton<NetworkAbilitiesController>
    {
        // ════════════════════════════════════════════════════════════════════════════════════════
        // CONFIGURATION
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        protected override DuplicatePolicy OnDuplicatePolicy => DuplicatePolicy.DestroyComponent;
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // INSPECTOR
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        [Header("Cast Settings")]
        [Tooltip("Allow clients to predict cast start before server response (recommended for responsiveness).")]
        [SerializeField] private bool m_AllowClientPrediction = true;
        
        [Tooltip("Maximum cast queue size per character.")]
        [SerializeField] private int m_MaxCastQueueSize = 2;
        
        [Tooltip("Grace period for range validation (accounts for latency).")]
        [SerializeField] private float m_RangeValidationGrace = 0.5f;
        
        [Header("Cooldown Settings")]
        [Tooltip("Add server-side buffer to cooldowns (anti-cheat).")]
        [SerializeField] private float m_CooldownBuffer = 0.05f;
        
        [Header("Projectile Settings")]
        [Tooltip("Maximum active projectiles per character.")]
        [SerializeField] private int m_MaxProjectilesPerCharacter = 20;
        
        [Tooltip("Projectile cleanup interval in seconds.")]
        [SerializeField] private float m_ProjectileCleanupInterval = 5f;
        
        [Header("Impact Settings")]
        [Tooltip("Maximum concurrent impacts.")]
        [SerializeField] private int m_MaxConcurrentImpacts = 50;
        
        [Header("Debug")]
        [SerializeField] private bool m_DebugLog = false;
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // EVENTS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        // Cast Events
        public event Action<NetworkAbilityCastRequest> OnCastRequestSent;
        public event Action<NetworkAbilityCastResponse> OnCastResponseReceived;
        public event Action<uint, NetworkAbilityCastRequest> OnCastRequestReceived;
        public event Action<NetworkAbilityCastBroadcast> OnCastBroadcastReceived;
        
        // Effect Events
        public event Action<NetworkAbilityEffectBroadcast> OnEffectBroadcastReceived;
        
        // Projectile Events
        public event Action<NetworkProjectileSpawnBroadcast> OnProjectileSpawnReceived;
        public event Action<NetworkProjectileEventBroadcast> OnProjectileEventReceived;
        
        // Impact Events
        public event Action<NetworkImpactSpawnBroadcast> OnImpactSpawnReceived;
        public event Action<NetworkImpactHitBroadcast> OnImpactHitReceived;
        
        // Cooldown Events
        public event Action<NetworkCooldownBroadcast> OnCooldownBroadcastReceived;
        
        // Learn Events
        public event Action<NetworkAbilityLearnRequest> OnLearnRequestSent;
        public event Action<NetworkAbilityLearnResponse> OnLearnResponseReceived;
        public event Action<uint, NetworkAbilityLearnRequest> OnLearnRequestReceived;
        public event Action<NetworkAbilityLearnBroadcast> OnLearnBroadcastReceived;
        
        // Cancel Events
        public event Action<NetworkCastCancelRequest> OnCancelRequestSent;
        public event Action<NetworkCastCancelResponse> OnCancelResponseReceived;
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // DELEGATES (Network Integration Points)
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        // Cast
        public Action<NetworkAbilityCastRequest> SendCastRequestToServer;
        public Action<uint, NetworkAbilityCastResponse> SendCastResponseToClient;
        public Action<NetworkAbilityCastBroadcast> BroadcastCastToClients;
        
        // Effects
        public Action<NetworkAbilityEffectBroadcast> BroadcastEffectToClients;
        
        // Projectiles
        public Action<NetworkProjectileSpawnBroadcast> BroadcastProjectileSpawnToClients;
        public Action<NetworkProjectileEventBroadcast> BroadcastProjectileEventToClients;
        
        // Impacts
        public Action<NetworkImpactSpawnBroadcast> BroadcastImpactSpawnToClients;
        public Action<NetworkImpactHitBroadcast> BroadcastImpactHitToClients;
        
        // Cooldowns
        public Action<NetworkCooldownBroadcast> BroadcastCooldownToClients;
        public Action<uint, NetworkCooldownResponse> SendCooldownResponseToClient;
        
        // Learning
        public Action<NetworkAbilityLearnRequest> SendLearnRequestToServer;
        public Action<uint, NetworkAbilityLearnResponse> SendLearnResponseToClient;
        public Action<NetworkAbilityLearnBroadcast> BroadcastLearnToClients;
        
        // Cancel
        public Action<NetworkCastCancelRequest> SendCancelRequestToServer;
        public Action<uint, NetworkCastCancelResponse> SendCancelResponseToClient;
        
        // State Sync
        public Action<uint, NetworkAbilityStateResponse, NetworkAbilitySlotEntry[], NetworkCooldownEntry[]> SendStateToClient;
        
        // Utility
        public Func<float> GetServerTime;
        public Func<uint, Pawn> GetPawnByNetworkId;
        public Func<uint, Character> GetCharacterByNetworkId;
        public Func<uint> GetLocalPlayerNetworkId;
        public Func<int, Ability> GetAbilityByHash;
        public Func<int, Projectile> GetProjectileByHash;
        public Func<int, Impact> GetImpactByHash;
        public Func<Pawn, uint> GetNetworkIdForPawn;
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // PRIVATE FIELDS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        private bool m_IsServer;
        private bool m_IsClient;
        
        // Request tracking
        private ushort m_NextRequestId = 1;
        private uint m_NextCastInstanceId = 1;
        private uint m_NextProjectileId = 1;
        private uint m_NextImpactId = 1;
        
        // Pending requests (client-side)
        private readonly Dictionary<uint, PendingCastRequest> m_PendingCastRequests = new(16);
        private readonly Dictionary<uint, PendingLearnRequest> m_PendingLearnRequests = new(16);
        private readonly Dictionary<uint, PendingCooldownRequest> m_PendingCooldownRequests = new(16);
        private readonly Dictionary<uint, PendingCancelRequest> m_PendingCancelRequests = new(16);
        
        // Server-side tracking
        private readonly Dictionary<uint, CasterState> m_CasterStates = new(64);
        private readonly Dictionary<uint, ActiveCast> m_ActiveCasts = new(32);
        private readonly Dictionary<uint, ActiveProjectile> m_ActiveProjectiles = new(128);
        private readonly Dictionary<uint, ActiveImpact> m_ActiveImpacts = new(64);
        
        // Cooldown tracking (server-authoritative)
        private readonly Dictionary<(uint, int), CooldownData> m_Cooldowns = new(256);
        
        // Statistics
        private NetworkAbilitiesStats m_Stats;
        
        // Cleanup timer
        private float m_LastCleanupTime;
        private NetworkAbilitiesPatchHooks m_PatchHooks;
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // TRACKING STRUCTS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        private struct PendingCastRequest
        {
            public NetworkAbilityCastRequest Request;
            public float SentTime;
            public Action<NetworkAbilityCastResponse> Callback;
            public Ability Ability;
        }
        
        private struct PendingLearnRequest
        {
            public NetworkAbilityLearnRequest Request;
            public float SentTime;
            public Action<NetworkAbilityLearnResponse> Callback;
        }
        
        private struct PendingCooldownRequest
        {
            public NetworkCooldownRequest Request;
            public float SentTime;
            public Action<NetworkCooldownResponse> Callback;
        }
        
        private struct PendingCancelRequest
        {
            public NetworkCastCancelRequest Request;
            public float SentTime;
            public Action<NetworkCastCancelResponse> Callback;
        }
        
        private class CasterState
        {
            public uint NetworkId;
            public Pawn Pawn;
            public Caster Caster;
            public List<uint> ActiveCastIds = new(4);
            public List<uint> ActiveProjectileIds = new(20);
            public int CastQueueCount;
        }
        
        private class ActiveCast
        {
            public uint CastInstanceId;
            public uint CasterNetworkId;
            public int AbilityIdHash;
            public Ability Ability;
            public RuntimeAbility RuntimeAbility;
            public AbilityCastState State;
            public float StartTime;
            public byte TargetType;
            public Vector3 TargetPosition;
            public uint TargetNetworkId;
        }
        
        private class ActiveProjectile
        {
            public uint ProjectileId;
            public uint CastInstanceId;
            public uint OwnerNetworkId;
            public int ProjectileHash;
            public RuntimeProjectile Instance;
            public float SpawnTime;
            public int PierceCount;
        }
        
        private class ActiveImpact
        {
            public uint ImpactId;
            public uint CastInstanceId;
            public int ImpactHash;
            public RuntimeImpact Instance;
            public float SpawnTime;
        }
        
        private struct CooldownData
        {
            public float EndTime;
            public float TotalDuration;
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // LIFECYCLE
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        private void Update()
        {
            if (!m_IsServer) return;
            
            // Periodic cleanup
            float currentTime = GetServerTime?.Invoke() ?? Time.time;
            if (currentTime - m_LastCleanupTime > m_ProjectileCleanupInterval)
            {
                m_LastCleanupTime = currentTime;
                CleanupExpiredEntries(currentTime);
            }
        }

        protected override void OnSingletonCleanup()
        {
            SecurityIntegration.SetModuleServerContext("Abilities", false);

            if (m_PatchHooks != null)
            {
                UnwirePatchHooks(m_PatchHooks);
                m_PatchHooks.Initialize(false, false);
            }
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // INITIALIZATION
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Initialize the controller for server mode.
        /// </summary>
        public void InitializeAsServer()
        {
            m_IsServer = true;
            m_IsClient = false;
            SecurityIntegration.SetModuleServerContext("Abilities", true);
            SecurityIntegration.EnsureSecurityManagerInitialized(true, () => GetServerTime?.Invoke() ?? Time.time);
            
            ClearAllState();
            SyncPatchHooks();
            
            if (m_DebugLog)
            {
                Debug.Log("[NetworkAbilitiesController] Initialized as Server");
            }
        }
        
        /// <summary>
        /// Initialize the controller for client mode.
        /// </summary>
        public void InitializeAsClient()
        {
            m_IsServer = false;
            m_IsClient = true;
            SecurityIntegration.SetModuleServerContext("Abilities", false);
            SecurityIntegration.EnsureSecurityManagerInitialized(false, () => GetServerTime?.Invoke() ?? Time.time);
            
            ClearAllState();
            SyncPatchHooks();
            
            if (m_DebugLog)
            {
                Debug.Log("[NetworkAbilitiesController] Initialized as Client");
            }
        }
        
        /// <summary>
        /// Initialize the controller for host mode (server + client).
        /// </summary>
        public void InitializeAsHost()
        {
            m_IsServer = true;
            m_IsClient = true;
            SecurityIntegration.SetModuleServerContext("Abilities", true);
            SecurityIntegration.EnsureSecurityManagerInitialized(true, () => GetServerTime?.Invoke() ?? Time.time);
            
            ClearAllState();
            SyncPatchHooks();
            
            if (m_DebugLog)
            {
                Debug.Log("[NetworkAbilitiesController] Initialized as Host");
            }
        }

        private void SyncPatchHooks()
        {
            bool enableHooks = m_IsServer || m_IsClient;
            if (!enableHooks)
            {
                if (m_PatchHooks != null)
                {
                    UnwirePatchHooks(m_PatchHooks);
                    m_PatchHooks.Initialize(false, false);
                }

                return;
            }

            if (m_PatchHooks == null)
            {
                m_PatchHooks = GetComponent<NetworkAbilitiesPatchHooks>();
                if (m_PatchHooks == null)
                {
                    m_PatchHooks = gameObject.AddComponent<NetworkAbilitiesPatchHooks>();
                }
            }

            WirePatchHooks(m_PatchHooks);
            m_PatchHooks.Initialize(m_IsServer, m_IsClient);
        }

        private void WirePatchHooks(NetworkAbilitiesPatchHooks patchHooks)
        {
            if (patchHooks == null) return;

            patchHooks.OnCastValidation = ValidatePatchedCastRequest;
            patchHooks.OnLearnValidation = ValidatePatchedLearnRequest;
            patchHooks.OnUnLearnValidation = ValidatePatchedUnlearnRequest;
            patchHooks.OnCastCompleted = HandlePatchedCastCompleted;
        }

        private static void UnwirePatchHooks(NetworkAbilitiesPatchHooks patchHooks)
        {
            if (patchHooks == null) return;

            patchHooks.OnCastValidation = null;
            patchHooks.OnLearnValidation = null;
            patchHooks.OnUnLearnValidation = null;
            patchHooks.OnCastCompleted = null;
        }

        private bool ValidatePatchedCastRequest(Caster caster, Ability ability, ExtendedArgs args)
        {
            if (m_IsServer && args != null && args.Get<AutoConfirmInput>() != null)
            {
                return true;
            }

            if (!m_IsClient)
            {
                return m_IsServer;
            }

            uint casterNetworkId = ResolveCasterNetworkId(caster);
            if (casterNetworkId == 0 || ability == null)
            {
                return m_IsServer;
            }

            Target target = args != null ? args.Get<Target>() : default;
            RequestCastAbility(casterNetworkId, ability, target);
            return false;
        }

        private bool ValidatePatchedLearnRequest(Caster caster, Ability ability, int slot)
        {
            if (!m_IsClient)
            {
                return m_IsServer;
            }

            uint casterNetworkId = ResolveCasterNetworkId(caster);
            if (casterNetworkId == 0 || ability == null)
            {
                return m_IsServer;
            }

            RequestLearnAbility(casterNetworkId, ability, slot);
            return false;
        }

        private bool ValidatePatchedUnlearnRequest(Caster caster, Ability ability)
        {
            if (!m_IsClient)
            {
                return m_IsServer;
            }

            uint casterNetworkId = ResolveCasterNetworkId(caster);
            if (casterNetworkId == 0 || ability == null)
            {
                return m_IsServer;
            }

            int slot = caster.GetSlotFromAbility(ability);
            if (slot < 0)
            {
                return m_IsServer;
            }

            RequestUnlearnAbility(casterNetworkId, slot);
            return false;
        }

        private void HandlePatchedCastCompleted(Caster caster, Ability ability, bool success)
        {
            if (!m_DebugLog || caster == null || ability == null) return;

            uint casterNetworkId = ResolveCasterNetworkId(caster);
            Debug.Log($"[NetworkAbilitiesController] Patched cast completed (caster={casterNetworkId}, ability={ability.name}, success={success})");
        }

        private uint ResolveCasterNetworkId(Caster caster)
        {
            if (caster == null) return 0;

            Pawn pawn = caster.Pawn;
            if (pawn == null) return 0;

            if (GetNetworkIdForPawn != null)
            {
                uint networkId = GetNetworkIdForPawn(pawn);
                if (networkId != 0) return networkId;
            }

            var networkCharacter = pawn.GetComponent<NetworkCharacter>();
            return networkCharacter != null ? networkCharacter.NetworkId : 0;
        }
        
        private void ClearAllState()
        {
            m_PendingCastRequests.Clear();
            m_PendingLearnRequests.Clear();
            m_PendingCooldownRequests.Clear();
            m_PendingCancelRequests.Clear();
            m_CasterStates.Clear();
            m_ActiveCasts.Clear();
            m_ActiveProjectiles.Clear();
            m_ActiveImpacts.Clear();
            m_Cooldowns.Clear();
            m_Stats = default;
        }
        
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // PUBLIC ACCESSORS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        public bool IsServer => m_IsServer;
        public bool IsClient => m_IsClient;
        public NetworkAbilitiesStats Stats => m_Stats;
        
        /// <summary>
        /// Check if an ability is on cooldown (server-authoritative).
        /// </summary>
        public bool IsOnCooldown(uint casterNetworkId, int abilityIdHash)
        {
            var key = (casterNetworkId, abilityIdHash);
            if (m_Cooldowns.TryGetValue(key, out var data))
            {
                float serverTime = GetServerTime?.Invoke() ?? Time.time;
                return serverTime < data.EndTime;
            }
            return false;
        }
        
        /// <summary>
        /// Get cooldown remaining time.
        /// </summary>
        public float GetCooldownRemaining(uint casterNetworkId, int abilityIdHash)
        {
            var key = (casterNetworkId, abilityIdHash);
            if (m_Cooldowns.TryGetValue(key, out var data))
            {
                float serverTime = GetServerTime?.Invoke() ?? Time.time;
                return Mathf.Max(0, data.EndTime - serverTime);
            }
            return 0f;
        }
        
        /// <summary>
        /// Get current state snapshot.
        /// </summary>
        public NetworkAbilitiesState GetState()
        {
            return new NetworkAbilitiesState
            {
                ActiveCasters = m_CasterStates.Count,
                OngoingCasts = m_ActiveCasts.Count,
                ActiveProjectiles = m_ActiveProjectiles.Count,
                ActiveImpacts = m_ActiveImpacts.Count,
                TrackedCooldowns = m_Cooldowns.Count,
                PendingRequests = m_PendingCastRequests.Count + m_PendingLearnRequests.Count
            };
        }
    }
    
    /// <summary>
    /// Marker class for auto-confirm input (server/AI casts).
    /// </summary>
    public class AutoConfirmInput { }
    
    /// <summary>
    /// Marker class for tracking ability source.
    /// </summary>
    public class AbiltySource
    {
        public GameObject Source { get; }
        public AbiltySource(GameObject source) => Source = source;
    }
}

#pragma warning restore CS0067
#pragma warning restore CS0414
