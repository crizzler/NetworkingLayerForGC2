using System;
using System.Collections.Generic;
using UnityEngine;
using DaimahouGames.Runtime.Abilities;
using DaimahouGames.Runtime.Pawns;
using DaimahouGames.Runtime.Core.Common;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Common;

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
    public class NetworkAbilitiesController : MonoBehaviour
    {
        // ════════════════════════════════════════════════════════════════════════════════════════
        // SINGLETON
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        private static NetworkAbilitiesController s_Instance;
        
        public static NetworkAbilitiesController Instance => s_Instance;
        public static bool HasInstance => s_Instance != null;
        
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
        private readonly Dictionary<ushort, PendingCastRequest> m_PendingCastRequests = new(16);
        private readonly Dictionary<ushort, PendingLearnRequest> m_PendingLearnRequests = new(16);
        private readonly Dictionary<ushort, PendingCooldownRequest> m_PendingCooldownRequests = new(16);
        private readonly Dictionary<ushort, PendingCancelRequest> m_PendingCancelRequests = new(16);
        
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
        
        private void Awake()
        {
            if (s_Instance != null && s_Instance != this)
            {
                Debug.LogWarning("[NetworkAbilitiesController] Duplicate instance destroyed.");
                Destroy(this);
                return;
            }
            
            s_Instance = this;
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
            if (!m_IsServer) return;
            
            // Periodic cleanup
            float currentTime = GetServerTime?.Invoke() ?? Time.time;
            if (currentTime - m_LastCleanupTime > m_ProjectileCleanupInterval)
            {
                m_LastCleanupTime = currentTime;
                CleanupExpiredEntries(currentTime);
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
            
            ClearAllState();
            
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
            
            ClearAllState();
            
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
            
            ClearAllState();
            
            if (m_DebugLog)
            {
                Debug.Log("[NetworkAbilitiesController] Initialized as Host");
            }
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
        // CLIENT-SIDE: REQUEST ABILITY CAST
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Request to cast an ability. Called by client.
        /// </summary>
        /// <param name="casterNetworkId">Network ID of the caster.</param>
        /// <param name="ability">The ability to cast.</param>
        /// <param name="target">Optional target.</param>
        /// <param name="callback">Optional callback when response received.</param>
        public void RequestCastAbility(
            uint casterNetworkId,
            Ability ability,
            Target target = default,
            Action<NetworkAbilityCastResponse> callback = null)
        {
            if (!m_IsClient && !m_IsServer)
            {
                Debug.LogWarning("[NetworkAbilitiesController] Not initialized.");
                return;
            }
            
            if (ability == null)
            {
                Debug.LogWarning("[NetworkAbilitiesController] Cannot cast null ability.");
                return;
            }
            
            ushort requestId = m_NextRequestId++;
            
            var request = new NetworkAbilityCastRequest
            {
                RequestId = requestId,
                CasterNetworkId = casterNetworkId,
                AbilityIdHash = ability.ID.Hash,
                ClientTime = Time.time,
                TargetType = GetTargetType(target),
                TargetPosition = target.Position,
                TargetNetworkId = GetTargetNetworkId(target),
                AutoConfirm = false
            };
            
            if (m_IsClient && !m_IsServer)
            {
                // Client-only: store pending and send to server
                m_PendingCastRequests[requestId] = new PendingCastRequest
                {
                    Request = request,
                    SentTime = Time.time,
                    Callback = callback,
                    Ability = ability
                };
                
                SendCastRequestToServer?.Invoke(request);
                OnCastRequestSent?.Invoke(request);
                
                m_Stats.TotalCastRequests++;
                
                if (m_DebugLog)
                {
                    Debug.Log($"[NetworkAbilitiesController] Cast request sent: {ability.name} (ID: {requestId})");
                }
            }
            else if (m_IsServer)
            {
                // Server or Host: process directly
                ProcessCastRequest(casterNetworkId, request);
            }
        }
        
        /// <summary>
        /// Request to cast ability with auto-confirm (for AI/instructions).
        /// </summary>
        public void RequestCastAbilityAutoConfirm(
            uint casterNetworkId,
            Ability ability,
            Vector3 targetPosition,
            uint targetNetworkId = 0,
            Action<NetworkAbilityCastResponse> callback = null)
        {
            if (!m_IsClient && !m_IsServer)
            {
                Debug.LogWarning("[NetworkAbilitiesController] Not initialized.");
                return;
            }
            
            ushort requestId = m_NextRequestId++;
            
            byte targetType = (byte)(targetNetworkId != 0 ? 2 : 1);
            
            var request = new NetworkAbilityCastRequest
            {
                RequestId = requestId,
                CasterNetworkId = casterNetworkId,
                AbilityIdHash = ability.ID.Hash,
                ClientTime = Time.time,
                TargetType = targetType,
                TargetPosition = targetPosition,
                TargetNetworkId = targetNetworkId,
                AutoConfirm = true
            };
            
            if (m_IsClient && !m_IsServer)
            {
                m_PendingCastRequests[requestId] = new PendingCastRequest
                {
                    Request = request,
                    SentTime = Time.time,
                    Callback = callback,
                    Ability = ability
                };
                
                SendCastRequestToServer?.Invoke(request);
                OnCastRequestSent?.Invoke(request);
                m_Stats.TotalCastRequests++;
            }
            else if (m_IsServer)
            {
                ProcessCastRequest(casterNetworkId, request);
            }
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // SERVER-SIDE: PROCESS CAST REQUEST
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Process incoming cast request from a client.
        /// Called by network manager when server receives cast request.
        /// </summary>
        public void ProcessCastRequest(uint clientId, NetworkAbilityCastRequest request)
        {
            if (!m_IsServer)
            {
                Debug.LogWarning("[NetworkAbilitiesController] ProcessCastRequest called on non-server.");
                return;
            }
            
            OnCastRequestReceived?.Invoke(clientId, request);
            m_Stats.TotalCastRequests++;
            
            // Validate caster exists
            Pawn casterPawn = GetPawnByNetworkId?.Invoke(request.CasterNetworkId);
            if (casterPawn == null)
            {
                SendCastRejection(clientId, request.RequestId, AbilityCastRejectReason.CasterNotFound);
                return;
            }
            
            // Get or create caster state
            if (!m_CasterStates.TryGetValue(request.CasterNetworkId, out var casterState))
            {
                casterState = new CasterState
                {
                    NetworkId = request.CasterNetworkId,
                    Pawn = casterPawn,
                    Caster = casterPawn.GetFeature<Caster>()
                };
                m_CasterStates[request.CasterNetworkId] = casterState;
            }
            
            if (casterState.Caster == null)
            {
                SendCastRejection(clientId, request.RequestId, AbilityCastRejectReason.CasterNotFound);
                return;
            }
            
            // Validate ability
            Ability ability = GetAbilityByHash?.Invoke(request.AbilityIdHash);
            if (ability == null)
            {
                SendCastRejection(clientId, request.RequestId, AbilityCastRejectReason.AbilityNotKnown);
                return;
            }
            
            // Check if ability is known (in a slot)
            bool abilityKnown = false;
            for (int i = 0; i < 10; i++) // Check up to 10 slots
            {
                var slottedAbility = casterState.Caster.GetSlottedAbility(i);
                if (slottedAbility != null && slottedAbility.ID.Hash == request.AbilityIdHash)
                {
                    abilityKnown = true;
                    break;
                }
            }
            
            if (!abilityKnown)
            {
                SendCastRejection(clientId, request.RequestId, AbilityCastRejectReason.AbilityNotKnown);
                m_Stats.RejectedRequirements++;
                return;
            }
            
            // Check cooldown
            var cooldownKey = (request.CasterNetworkId, request.AbilityIdHash);
            if (m_Cooldowns.TryGetValue(cooldownKey, out var cooldownData))
            {
                float serverTime = GetServerTime?.Invoke() ?? Time.time;
                if (serverTime < cooldownData.EndTime)
                {
                    SendCastRejection(clientId, request.RequestId, AbilityCastRejectReason.OnCooldown);
                    m_Stats.RejectedOnCooldown++;
                    return;
                }
            }
            
            // Check cast queue
            if (casterState.CastQueueCount >= m_MaxCastQueueSize)
            {
                SendCastRejection(clientId, request.RequestId, AbilityCastRejectReason.AlreadyCasting);
                m_Stats.RejectedAlreadyCasting++;
                return;
            }
            
            // Validate requirements using runtime ability
            RuntimeAbility runtimeAbility = casterState.Caster.GetRuntimeAbility(ability);
            var args = new ExtendedArgs(casterState.Pawn.gameObject);
            args.Set(runtimeAbility);
            
            if (!runtimeAbility.CanUse(args, out var failedRequirement))
            {
                if (m_DebugLog)
                {
                    Debug.Log($"[NetworkAbilitiesController] Cast rejected: requirement not met - {failedRequirement?.Title}");
                }
                SendCastRejection(clientId, request.RequestId, AbilityCastRejectReason.RequirementNotMet);
                m_Stats.RejectedRequirements++;
                return;
            }
            
            // Validate target if needed
            if (request.TargetType > 0)
            {
                Target target = ReconstructTarget(request);
                if (IsValidTarget(target))
                {
                    args.Set(target);
                }
                
                // Range check
                if (runtimeAbility.Targeting.ShouldCheckRange)
                {
                    float range = (float)runtimeAbility.GetRange(args) + m_RangeValidationGrace;
                    float distance = Vector3.Distance(casterState.Pawn.Position, request.TargetPosition);
                    
                    if (distance > range)
                    {
                        SendCastRejection(clientId, request.RequestId, AbilityCastRejectReason.OutOfRange);
                        m_Stats.RejectedOutOfRange++;
                        return;
                    }
                }
            }
            
            // === APPROVED ===
            
            uint castInstanceId = m_NextCastInstanceId++;
            float serverTime2 = GetServerTime?.Invoke() ?? Time.time;
            
            // Create active cast tracking
            var activeCast = new ActiveCast
            {
                CastInstanceId = castInstanceId,
                CasterNetworkId = request.CasterNetworkId,
                AbilityIdHash = request.AbilityIdHash,
                Ability = ability,
                RuntimeAbility = runtimeAbility,
                State = AbilityCastState.Started,
                StartTime = serverTime2,
                TargetType = request.TargetType,
                TargetPosition = request.TargetPosition,
                TargetNetworkId = request.TargetNetworkId
            };
            
            m_ActiveCasts[castInstanceId] = activeCast;
            casterState.ActiveCastIds.Add(castInstanceId);
            casterState.CastQueueCount++;
            
            // Send approval
            var response = new NetworkAbilityCastResponse
            {
                RequestId = request.RequestId,
                CastInstanceId = castInstanceId,
                Approved = true,
                RejectReason = AbilityCastRejectReason.None,
                CooldownEndTime = 0 // Will be set after cast completes
            };
            
            SendCastResponseToClient?.Invoke(clientId, response);
            m_Stats.ApprovedCasts++;
            
            // Broadcast cast start to all clients
            var broadcast = new NetworkAbilityCastBroadcast
            {
                CasterNetworkId = request.CasterNetworkId,
                CastInstanceId = castInstanceId,
                AbilityIdHash = request.AbilityIdHash,
                ServerTime = serverTime2,
                TargetType = request.TargetType,
                TargetPosition = request.TargetPosition,
                TargetNetworkId = request.TargetNetworkId,
                CastState = AbilityCastState.Started
            };
            
            BroadcastCastToClients?.Invoke(broadcast);
            
            if (m_DebugLog)
            {
                Debug.Log($"[NetworkAbilitiesController] Cast approved: {ability.name} (CastID: {castInstanceId})");
            }
            
            // Execute the cast on server
            ExecuteServerCast(activeCast, casterState);
        }
        
        private void SendCastRejection(uint clientId, ushort requestId, AbilityCastRejectReason reason)
        {
            var response = new NetworkAbilityCastResponse
            {
                RequestId = requestId,
                CastInstanceId = 0,
                Approved = false,
                RejectReason = reason,
                CooldownEndTime = 0
            };
            
            SendCastResponseToClient?.Invoke(clientId, response);
            m_Stats.RejectedCasts++;
            
            if (m_DebugLog)
            {
                Debug.Log($"[NetworkAbilitiesController] Cast rejected: {reason} (ReqID: {requestId})");
            }
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // SERVER-SIDE: EXECUTE CAST
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        private async void ExecuteServerCast(ActiveCast activeCast, CasterState casterState)
        {
            try
            {
                RuntimeAbility runtimeAbility = activeCast.RuntimeAbility;
                runtimeAbility.Reset();
                
                var args = new ExtendedArgs(casterState.Pawn.gameObject);
                args.Set(runtimeAbility);
                args.Set(new AbiltySource(casterState.Pawn.gameObject));
                
                // Set target
                if (activeCast.TargetType > 0)
                {
                    Target target = ReconstructTarget(activeCast);
                    if (IsValidTarget(target))
                    {
                        args.Set(target);
                    }
                }
                
                // Mark as auto-confirm for server execution
                args.Set(new AutoConfirmInput());
                
                // Subscribe to trigger events to broadcast effects
                var triggerReceipt = runtimeAbility.OnTrigger.Subscribe(triggerArgs =>
                {
                    OnAbilityTriggered(activeCast, triggerArgs);
                });
                
                // Execute via Caster.Cast (this handles state machine)
                bool success = await casterState.Caster.Cast(activeCast.Ability, args);
                
                triggerReceipt.Dispose();
                
                // Update cast state
                activeCast.State = runtimeAbility.IsCanceled ? AbilityCastState.Canceled : AbilityCastState.Completed;
                
                // Broadcast completion/cancellation
                BroadcastCastStateChange(activeCast);
                
                // Handle cooldown on success
                if (success && !runtimeAbility.IsCanceled)
                {
                    ApplyServerCooldown(casterState.NetworkId, activeCast.AbilityIdHash, args);
                }
                
                // Cleanup
                casterState.ActiveCastIds.Remove(activeCast.CastInstanceId);
                casterState.CastQueueCount = Mathf.Max(0, casterState.CastQueueCount - 1);
                m_ActiveCasts.Remove(activeCast.CastInstanceId);
                
                if (m_DebugLog)
                {
                    Debug.Log($"[NetworkAbilitiesController] Cast completed: {activeCast.Ability.name} " +
                              $"(Success: {success}, Canceled: {runtimeAbility.IsCanceled})");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[NetworkAbilitiesController] Cast execution error: {ex.Message}\n{ex.StackTrace}");
                
                // Cleanup on error
                activeCast.State = AbilityCastState.Canceled;
                BroadcastCastStateChange(activeCast);
                
                casterState.ActiveCastIds.Remove(activeCast.CastInstanceId);
                casterState.CastQueueCount = Mathf.Max(0, casterState.CastQueueCount - 1);
                m_ActiveCasts.Remove(activeCast.CastInstanceId);
            }
        }
        
        private void OnAbilityTriggered(ActiveCast activeCast, ExtendedArgs args)
        {
            activeCast.State = AbilityCastState.Triggered;
            
            // Broadcast trigger to clients
            BroadcastCastStateChange(activeCast);
            
            if (m_DebugLog)
            {
                Debug.Log($"[NetworkAbilitiesController] Ability triggered: {activeCast.Ability.name}");
            }
        }
        
        private void BroadcastCastStateChange(ActiveCast activeCast)
        {
            var broadcast = new NetworkAbilityCastBroadcast
            {
                CasterNetworkId = activeCast.CasterNetworkId,
                CastInstanceId = activeCast.CastInstanceId,
                AbilityIdHash = activeCast.AbilityIdHash,
                ServerTime = GetServerTime?.Invoke() ?? Time.time,
                TargetType = activeCast.TargetType,
                TargetPosition = activeCast.TargetPosition,
                TargetNetworkId = activeCast.TargetNetworkId,
                CastState = activeCast.State
            };
            
            BroadcastCastToClients?.Invoke(broadcast);
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // SERVER-SIDE: COOLDOWN MANAGEMENT
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        private void ApplyServerCooldown(uint casterNetworkId, int abilityIdHash, ExtendedArgs args)
        {
            RuntimeAbility runtimeAbility = args.Get<RuntimeAbility>();
            if (runtimeAbility == null) return;
            
            // Find cooldown requirement
            float cooldownDuration = 0f;
            foreach (var requirement in runtimeAbility.Requirements)
            {
                if (requirement is AbilityRequirementCooldown cooldownReq)
                {
                    // Commit the requirement to apply the cooldown locally
                    cooldownReq.Commit(args);
                    
                    // Get the cooldown duration (hacky but works)
                    var caster = runtimeAbility.Caster;
                    if (caster != null)
                    {
                        var cooldowns = caster.Get<Cooldowns>();
                        if (cooldowns != null)
                        {
                            float endTime = cooldowns.GetCooldown(runtimeAbility.ID);
                            cooldownDuration = endTime - Time.time;
                        }
                    }
                    break;
                }
            }
            
            if (cooldownDuration <= 0) return;
            
            // Add server-side buffer
            cooldownDuration += m_CooldownBuffer;
            
            float serverTime = GetServerTime?.Invoke() ?? Time.time;
            float cooldownEndTime = serverTime + cooldownDuration;
            
            var cooldownKey = (casterNetworkId, abilityIdHash);
            m_Cooldowns[cooldownKey] = new CooldownData
            {
                EndTime = cooldownEndTime,
                TotalDuration = cooldownDuration
            };
            
            // Broadcast cooldown to clients
            var broadcast = new NetworkCooldownBroadcast
            {
                CharacterNetworkId = casterNetworkId,
                AbilityIdHash = abilityIdHash,
                CooldownEndTime = cooldownEndTime,
                TotalDuration = cooldownDuration,
                Reason = CooldownChangeReason.AbilityCast
            };
            
            BroadcastCooldownToClients?.Invoke(broadcast);
            m_Stats.CooldownsSet++;
            
            if (m_DebugLog)
            {
                Debug.Log($"[NetworkAbilitiesController] Cooldown set: hash={abilityIdHash}, duration={cooldownDuration}s");
            }
        }
        
        /// <summary>
        /// Server method to reset a cooldown.
        /// </summary>
        public void ServerResetCooldown(uint casterNetworkId, int abilityIdHash)
        {
            if (!m_IsServer) return;
            
            var cooldownKey = (casterNetworkId, abilityIdHash);
            m_Cooldowns.Remove(cooldownKey);
            
            // Broadcast cleared cooldown
            var broadcast = new NetworkCooldownBroadcast
            {
                CharacterNetworkId = casterNetworkId,
                AbilityIdHash = abilityIdHash,
                CooldownEndTime = 0,
                TotalDuration = 0,
                Reason = CooldownChangeReason.ServerReset
            };
            
            BroadcastCooldownToClients?.Invoke(broadcast);
            m_Stats.CooldownsCleared++;
        }
        
        /// <summary>
        /// Server method to modify a cooldown duration.
        /// </summary>
        public void ServerModifyCooldown(uint casterNetworkId, int abilityIdHash, float newDuration, bool extend)
        {
            if (!m_IsServer) return;
            
            var cooldownKey = (casterNetworkId, abilityIdHash);
            float serverTime = GetServerTime?.Invoke() ?? Time.time;
            
            float newEndTime = serverTime + newDuration;
            
            m_Cooldowns[cooldownKey] = new CooldownData
            {
                EndTime = newEndTime,
                TotalDuration = newDuration
            };
            
            var broadcast = new NetworkCooldownBroadcast
            {
                CharacterNetworkId = casterNetworkId,
                AbilityIdHash = abilityIdHash,
                CooldownEndTime = newEndTime,
                TotalDuration = newDuration,
                Reason = extend ? CooldownChangeReason.ServerExtended : CooldownChangeReason.ServerReduced
            };
            
            BroadcastCooldownToClients?.Invoke(broadcast);
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // SERVER-SIDE: PROJECTILE SPAWNING
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Server spawns a projectile and broadcasts to clients.
        /// Call this from ability effects instead of direct Projectile.Get().
        /// </summary>
        public RuntimeProjectile ServerSpawnProjectile(
            uint castInstanceId,
            Projectile projectileSO,
            Vector3 spawnPosition,
            Vector3 direction,
            Vector3 targetPosition,
            uint targetNetworkId = 0,
            ExtendedArgs args = null)
        {
            if (!m_IsServer) return null;
            
            // Check limit per caster
            if (m_ActiveCasts.TryGetValue(castInstanceId, out var activeCast))
            {
                if (m_CasterStates.TryGetValue(activeCast.CasterNetworkId, out var casterState))
                {
                    if (casterState.ActiveProjectileIds.Count >= m_MaxProjectilesPerCharacter)
                    {
                        if (m_DebugLog)
                        {
                            Debug.LogWarning($"[NetworkAbilitiesController] Max projectiles reached for caster {casterState.NetworkId}");
                        }
                        return null;
                    }
                }
            }
            
            uint projectileId = m_NextProjectileId++;
            float serverTime = GetServerTime?.Invoke() ?? Time.time;
            
            // Spawn the projectile on server
            RuntimeProjectile instance = projectileSO.Get(
                args ?? new Args((GameObject)null),
                spawnPosition,
                Quaternion.LookRotation(direction)
            );
            
            if (instance == null) return null;
            
            // Track it
            var activeProjectile = new ActiveProjectile
            {
                ProjectileId = projectileId,
                CastInstanceId = castInstanceId,
                OwnerNetworkId = activeCast?.CasterNetworkId ?? 0,
                ProjectileHash = projectileSO.GetHashCode(),
                Instance = instance,
                SpawnTime = serverTime,
                PierceCount = 0
            };
            
            m_ActiveProjectiles[projectileId] = activeProjectile;
            
            if (m_CasterStates.TryGetValue(activeProjectile.OwnerNetworkId, out var ownerState))
            {
                ownerState.ActiveProjectileIds.Add(projectileId);
            }
            
            // Broadcast spawn
            var broadcast = new NetworkProjectileSpawnBroadcast
            {
                ProjectileId = projectileId,
                CastInstanceId = castInstanceId,
                ProjectileHash = projectileSO.GetHashCode(),
                SpawnPosition = spawnPosition,
                Direction = direction,
                TargetPosition = targetPosition,
                TargetNetworkId = targetNetworkId,
                ServerTime = serverTime
            };
            
            BroadcastProjectileSpawnToClients?.Invoke(broadcast);
            m_Stats.ProjectilesSpawned++;
            
            if (m_DebugLog)
            {
                Debug.Log($"[NetworkAbilitiesController] Projectile spawned: {projectileSO.name} (ID: {projectileId})");
            }
            
            return instance;
        }
        
        /// <summary>
        /// Server reports projectile hit.
        /// </summary>
        public void ServerReportProjectileHit(uint projectileId, Vector3 position, uint hitTargetNetworkId = 0)
        {
            if (!m_IsServer) return;
            if (!m_ActiveProjectiles.TryGetValue(projectileId, out var projectile)) return;
            
            var broadcast = new NetworkProjectileEventBroadcast
            {
                ProjectileId = projectileId,
                EventType = ProjectileEventType.Hit,
                Position = position,
                HitTargetNetworkId = hitTargetNetworkId,
                ServerTime = GetServerTime?.Invoke() ?? Time.time
            };
            
            BroadcastProjectileEventToClients?.Invoke(broadcast);
            m_Stats.ProjectileHits++;
        }
        
        /// <summary>
        /// Server destroys a projectile.
        /// </summary>
        public void ServerDestroyProjectile(uint projectileId, ProjectileEventType reason = ProjectileEventType.Destroyed)
        {
            if (!m_IsServer) return;
            if (!m_ActiveProjectiles.TryGetValue(projectileId, out var projectile)) return;
            
            Vector3 position = projectile.Instance != null ? projectile.Instance.transform.position : Vector3.zero;
            
            var broadcast = new NetworkProjectileEventBroadcast
            {
                ProjectileId = projectileId,
                EventType = reason,
                Position = position,
                HitTargetNetworkId = 0,
                ServerTime = GetServerTime?.Invoke() ?? Time.time
            };
            
            BroadcastProjectileEventToClients?.Invoke(broadcast);
            
            // Cleanup
            if (m_CasterStates.TryGetValue(projectile.OwnerNetworkId, out var ownerState))
            {
                ownerState.ActiveProjectileIds.Remove(projectileId);
            }
            
            if (projectile.Instance != null)
            {
                projectile.Instance.gameObject.SetActive(false);
            }
            
            m_ActiveProjectiles.Remove(projectileId);
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // SERVER-SIDE: IMPACT SPAWNING
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Server spawns an impact and broadcasts to clients.
        /// </summary>
        public RuntimeImpact ServerSpawnImpact(
            uint castInstanceId,
            Impact impactSO,
            Vector3 position,
            Quaternion rotation,
            ExtendedArgs args = null)
        {
            if (!m_IsServer) return null;
            
            if (m_ActiveImpacts.Count >= m_MaxConcurrentImpacts)
            {
                if (m_DebugLog)
                {
                    Debug.LogWarning("[NetworkAbilitiesController] Max concurrent impacts reached.");
                }
                return null;
            }
            
            uint impactId = m_NextImpactId++;
            float serverTime = GetServerTime?.Invoke() ?? Time.time;
            
            // Spawn impact on server
            RuntimeImpact instance = impactSO.Get(
                args ?? new Args((GameObject)null),
                position,
                rotation
            );
            
            if (instance == null) return null;
            
            // Track it
            var activeImpact = new ActiveImpact
            {
                ImpactId = impactId,
                CastInstanceId = castInstanceId,
                ImpactHash = impactSO.GetHashCode(),
                Instance = instance,
                SpawnTime = serverTime
            };
            
            m_ActiveImpacts[impactId] = activeImpact;
            
            // Broadcast spawn
            var broadcast = new NetworkImpactSpawnBroadcast
            {
                ImpactId = impactId,
                CastInstanceId = castInstanceId,
                ImpactHash = impactSO.GetHashCode(),
                Position = position,
                Rotation = rotation,
                ServerTime = serverTime
            };
            
            BroadcastImpactSpawnToClients?.Invoke(broadcast);
            m_Stats.ImpactsSpawned++;
            
            if (m_DebugLog)
            {
                Debug.Log($"[NetworkAbilitiesController] Impact spawned: {impactSO.name} (ID: {impactId})");
            }
            
            return instance;
        }
        
        /// <summary>
        /// Server reports impact hits.
        /// </summary>
        public void ServerReportImpactHits(uint impactId, uint[] hitTargetNetworkIds)
        {
            if (!m_IsServer) return;
            if (!m_ActiveImpacts.ContainsKey(impactId)) return;
            
            var broadcast = new NetworkImpactHitBroadcast
            {
                ImpactId = impactId,
                ServerTime = GetServerTime?.Invoke() ?? Time.time,
                TargetCount = (byte)Mathf.Min(hitTargetNetworkIds.Length, 16)
            };
            
            // Fill in targets
            if (hitTargetNetworkIds.Length > 0) broadcast.Target0 = hitTargetNetworkIds[0];
            if (hitTargetNetworkIds.Length > 1) broadcast.Target1 = hitTargetNetworkIds[1];
            if (hitTargetNetworkIds.Length > 2) broadcast.Target2 = hitTargetNetworkIds[2];
            if (hitTargetNetworkIds.Length > 3) broadcast.Target3 = hitTargetNetworkIds[3];
            if (hitTargetNetworkIds.Length > 4) broadcast.Target4 = hitTargetNetworkIds[4];
            if (hitTargetNetworkIds.Length > 5) broadcast.Target5 = hitTargetNetworkIds[5];
            if (hitTargetNetworkIds.Length > 6) broadcast.Target6 = hitTargetNetworkIds[6];
            if (hitTargetNetworkIds.Length > 7) broadcast.Target7 = hitTargetNetworkIds[7];
            if (hitTargetNetworkIds.Length > 8) broadcast.Target8 = hitTargetNetworkIds[8];
            if (hitTargetNetworkIds.Length > 9) broadcast.Target9 = hitTargetNetworkIds[9];
            if (hitTargetNetworkIds.Length > 10) broadcast.Target10 = hitTargetNetworkIds[10];
            if (hitTargetNetworkIds.Length > 11) broadcast.Target11 = hitTargetNetworkIds[11];
            if (hitTargetNetworkIds.Length > 12) broadcast.Target12 = hitTargetNetworkIds[12];
            if (hitTargetNetworkIds.Length > 13) broadcast.Target13 = hitTargetNetworkIds[13];
            if (hitTargetNetworkIds.Length > 14) broadcast.Target14 = hitTargetNetworkIds[14];
            if (hitTargetNetworkIds.Length > 15) broadcast.Target15 = hitTargetNetworkIds[15];
            
            BroadcastImpactHitToClients?.Invoke(broadcast);
            m_Stats.ImpactTargetsHit += hitTargetNetworkIds.Length;
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // CLIENT-SIDE: ABILITY LEARNING
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Request to learn an ability into a slot.
        /// </summary>
        public void RequestLearnAbility(
            uint characterNetworkId,
            Ability ability,
            int slot,
            Action<NetworkAbilityLearnResponse> callback = null)
        {
            if (!m_IsClient && !m_IsServer) return;
            if (ability == null) return;
            
            ushort requestId = m_NextRequestId++;
            
            var request = new NetworkAbilityLearnRequest
            {
                RequestId = requestId,
                CharacterNetworkId = characterNetworkId,
                AbilityIdHash = ability.ID.Hash,
                Slot = (sbyte)slot,
                IsLearning = true
            };
            
            if (m_IsClient && !m_IsServer)
            {
                m_PendingLearnRequests[requestId] = new PendingLearnRequest
                {
                    Request = request,
                    SentTime = Time.time,
                    Callback = callback
                };
                
                SendLearnRequestToServer?.Invoke(request);
                OnLearnRequestSent?.Invoke(request);
            }
            else if (m_IsServer)
            {
                ProcessLearnRequest(characterNetworkId, request);
            }
        }
        
        /// <summary>
        /// Request to unlearn an ability from a slot.
        /// </summary>
        public void RequestUnlearnAbility(
            uint characterNetworkId,
            int slot,
            Action<NetworkAbilityLearnResponse> callback = null)
        {
            if (!m_IsClient && !m_IsServer) return;
            
            ushort requestId = m_NextRequestId++;
            
            var request = new NetworkAbilityLearnRequest
            {
                RequestId = requestId,
                CharacterNetworkId = characterNetworkId,
                AbilityIdHash = 0,
                Slot = (sbyte)slot,
                IsLearning = false
            };
            
            if (m_IsClient && !m_IsServer)
            {
                m_PendingLearnRequests[requestId] = new PendingLearnRequest
                {
                    Request = request,
                    SentTime = Time.time,
                    Callback = callback
                };
                
                SendLearnRequestToServer?.Invoke(request);
            }
            else if (m_IsServer)
            {
                ProcessLearnRequest(characterNetworkId, request);
            }
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // SERVER-SIDE: PROCESS LEARN REQUEST
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Process learn request on server.
        /// </summary>
        public void ProcessLearnRequest(uint clientId, NetworkAbilityLearnRequest request)
        {
            if (!m_IsServer) return;
            
            OnLearnRequestReceived?.Invoke(clientId, request);
            
            // Validate character
            Pawn pawn = GetPawnByNetworkId?.Invoke(request.CharacterNetworkId);
            if (pawn == null)
            {
                SendLearnRejection(clientId, request.RequestId, AbilityLearnRejectReason.CharacterNotFound);
                return;
            }
            
            Caster caster = pawn.GetFeature<Caster>();
            if (caster == null)
            {
                SendLearnRejection(clientId, request.RequestId, AbilityLearnRejectReason.CharacterNotFound);
                return;
            }
            
            if (request.IsLearning)
            {
                // Learn ability
                Ability ability = GetAbilityByHash?.Invoke(request.AbilityIdHash);
                if (ability == null)
                {
                    SendLearnRejection(clientId, request.RequestId, AbilityLearnRejectReason.AbilityNotFound);
                    return;
                }
                
                if (request.Slot < 0)
                {
                    SendLearnRejection(clientId, request.RequestId, AbilityLearnRejectReason.SlotInvalid);
                    return;
                }
                
                // Apply
                caster.Learn(ability, request.Slot);
                m_Stats.AbilitiesLearned++;
                
                // Send response
                var response = new NetworkAbilityLearnResponse
                {
                    RequestId = request.RequestId,
                    Approved = true,
                    RejectReason = AbilityLearnRejectReason.None
                };
                SendLearnResponseToClient?.Invoke(clientId, response);
                
                // Broadcast
                var broadcast = new NetworkAbilityLearnBroadcast
                {
                    CharacterNetworkId = request.CharacterNetworkId,
                    AbilityIdHash = request.AbilityIdHash,
                    Slot = request.Slot,
                    IsLearned = true
                };
                BroadcastLearnToClients?.Invoke(broadcast);
            }
            else
            {
                // Unlearn ability
                if (request.Slot < 0)
                {
                    SendLearnRejection(clientId, request.RequestId, AbilityLearnRejectReason.SlotInvalid);
                    return;
                }
                
                Ability slottedAbility = caster.GetSlottedAbility(request.Slot);
                
                caster.UnLearn(request.Slot);
                m_Stats.AbilitiesUnlearned++;
                
                var response = new NetworkAbilityLearnResponse
                {
                    RequestId = request.RequestId,
                    Approved = true,
                    RejectReason = AbilityLearnRejectReason.None
                };
                SendLearnResponseToClient?.Invoke(clientId, response);
                
                var broadcast = new NetworkAbilityLearnBroadcast
                {
                    CharacterNetworkId = request.CharacterNetworkId,
                    AbilityIdHash = slottedAbility?.ID.Hash ?? 0,
                    Slot = request.Slot,
                    IsLearned = false
                };
                BroadcastLearnToClients?.Invoke(broadcast);
            }
        }
        
        private void SendLearnRejection(uint clientId, ushort requestId, AbilityLearnRejectReason reason)
        {
            var response = new NetworkAbilityLearnResponse
            {
                RequestId = requestId,
                Approved = false,
                RejectReason = reason
            };
            SendLearnResponseToClient?.Invoke(clientId, response);
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // CLIENT-SIDE: RECEIVE RESPONSES AND BROADCASTS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Client receives cast response from server.
        /// </summary>
        public void ReceiveCastResponse(NetworkAbilityCastResponse response)
        {
            if (!m_IsClient) return;
            
            OnCastResponseReceived?.Invoke(response);
            
            if (m_PendingCastRequests.TryGetValue(response.RequestId, out var pending))
            {
                m_PendingCastRequests.Remove(response.RequestId);
                pending.Callback?.Invoke(response);
                
                if (m_DebugLog)
                {
                    Debug.Log($"[NetworkAbilitiesController] Cast response received: " +
                              $"Approved={response.Approved}, Reason={response.RejectReason}");
                }
            }
        }
        
        /// <summary>
        /// Client receives cast broadcast (for other players' casts).
        /// </summary>
        public void ReceiveCastBroadcast(NetworkAbilityCastBroadcast broadcast)
        {
            if (!m_IsClient) return;
            
            OnCastBroadcastReceived?.Invoke(broadcast);
            
            // If this is for a remote player, play animations/VFX
            uint localId = GetLocalPlayerNetworkId?.Invoke() ?? 0;
            if (broadcast.CasterNetworkId != localId)
            {
                // Remote player casting - trigger visual representation
                HandleRemoteCast(broadcast);
            }
        }
        
        private void HandleRemoteCast(NetworkAbilityCastBroadcast broadcast)
        {
            // Get the pawn/character
            Pawn pawn = GetPawnByNetworkId?.Invoke(broadcast.CasterNetworkId);
            if (pawn == null) return;
            
            Caster caster = pawn.GetFeature<Caster>();
            if (caster == null) return;
            
            Ability ability = GetAbilityByHash?.Invoke(broadcast.AbilityIdHash);
            if (ability == null) return;
            
            switch (broadcast.CastState)
            {
                case AbilityCastState.Started:
                    // Play cast start animation/effects on remote
                    if (m_DebugLog)
                    {
                        Debug.Log($"[NetworkAbilitiesController] Remote cast started: {ability.name}");
                    }
                    break;
                    
                case AbilityCastState.Triggered:
                    // Effects triggered
                    break;
                    
                case AbilityCastState.Completed:
                case AbilityCastState.Canceled:
                    // Cleanup
                    break;
            }
        }
        
        /// <summary>
        /// Client receives cooldown broadcast.
        /// </summary>
        public void ReceiveCooldownBroadcast(NetworkCooldownBroadcast broadcast)
        {
            if (!m_IsClient) return;
            
            OnCooldownBroadcastReceived?.Invoke(broadcast);
            
            // Sync cooldown locally
            Pawn pawn = GetPawnByNetworkId?.Invoke(broadcast.CharacterNetworkId);
            if (pawn == null) return;
            
            Caster caster = pawn.GetFeature<Caster>();
            if (caster == null) return;
            
            Cooldowns cooldowns = caster.Get<Cooldowns>();
            if (cooldowns == null) return;
            
            Ability ability = GetAbilityByHash?.Invoke(broadcast.AbilityIdHash);
            if (ability == null) return;
            
            // Apply the server's cooldown
            if (broadcast.CooldownEndTime > 0)
            {
                float remainingDuration = broadcast.CooldownEndTime - (GetServerTime?.Invoke() ?? Time.time);
                if (remainingDuration > 0)
                {
                    cooldowns.AddCooldown(ability.ID, remainingDuration);
                }
            }
        }
        
        /// <summary>
        /// Client receives learn response.
        /// </summary>
        public void ReceiveLearnResponse(NetworkAbilityLearnResponse response)
        {
            if (!m_IsClient) return;
            
            OnLearnResponseReceived?.Invoke(response);
            
            if (m_PendingLearnRequests.TryGetValue(response.RequestId, out var pending))
            {
                m_PendingLearnRequests.Remove(response.RequestId);
                pending.Callback?.Invoke(response);
            }
        }
        
        /// <summary>
        /// Client receives learn broadcast.
        /// </summary>
        public void ReceiveLearnBroadcast(NetworkAbilityLearnBroadcast broadcast)
        {
            if (!m_IsClient) return;
            
            OnLearnBroadcastReceived?.Invoke(broadcast);
            
            // Sync locally
            Pawn pawn = GetPawnByNetworkId?.Invoke(broadcast.CharacterNetworkId);
            if (pawn == null) return;
            
            Caster caster = pawn.GetFeature<Caster>();
            if (caster == null) return;
            
            if (broadcast.IsLearned)
            {
                Ability ability = GetAbilityByHash?.Invoke(broadcast.AbilityIdHash);
                if (ability != null)
                {
                    caster.Learn(ability, broadcast.Slot);
                }
            }
            else
            {
                caster.UnLearn(broadcast.Slot);
            }
        }
        
        /// <summary>
        /// Client receives projectile spawn broadcast.
        /// </summary>
        public void ReceiveProjectileSpawnBroadcast(NetworkProjectileSpawnBroadcast broadcast)
        {
            if (!m_IsClient) return;
            
            OnProjectileSpawnReceived?.Invoke(broadcast);
            
            // Spawn visual projectile on client
            Projectile projectileSO = GetProjectileByHash?.Invoke(broadcast.ProjectileHash);
            if (projectileSO == null) return;
            
            // Calculate lag-compensated position
            float timeSinceSpawn = (GetServerTime?.Invoke() ?? Time.time) - broadcast.ServerTime;
            Vector3 compensatedPosition = broadcast.SpawnPosition + broadcast.Direction * timeSinceSpawn * 5f; // Estimate speed
            
            RuntimeProjectile instance = projectileSO.Get(
                new Args((GameObject)null),
                compensatedPosition,
                Quaternion.LookRotation(broadcast.Direction)
            );
            
            if (instance != null)
            {
                // Initialize with broadcast data
                var extendedArgs = new ExtendedArgs(instance.gameObject);
                
                if (broadcast.TargetNetworkId != 0)
                {
                    Pawn targetPawn = GetPawnByNetworkId?.Invoke(broadcast.TargetNetworkId);
                    if (targetPawn != null)
                    {
                        extendedArgs.Set(new Target(targetPawn));
                    }
                }
                else
                {
                    extendedArgs.Set(new Target(broadcast.TargetPosition));
                }
                
                instance.Initialize(extendedArgs, broadcast.Direction);
            }
        }
        
        /// <summary>
        /// Client receives impact spawn broadcast.
        /// </summary>
        public void ReceiveImpactSpawnBroadcast(NetworkImpactSpawnBroadcast broadcast)
        {
            if (!m_IsClient) return;
            
            OnImpactSpawnReceived?.Invoke(broadcast);
            
            Impact impactSO = GetImpactByHash?.Invoke(broadcast.ImpactHash);
            if (impactSO == null) return;
            
            RuntimeImpact instance = impactSO.Get(
                new Args((GameObject)null),
                broadcast.Position,
                broadcast.Rotation
            );
            
            // Client-side impacts are visual only - server handles actual effects
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // HELPER METHODS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        private byte GetTargetType(Target target)
        {
            if (!IsValidTarget(target)) return 0;
            
            // Check if target has a Pawn (character target)
            if (target.Pawn != null) return 2;
            
            // Check if target has a GameObject (object target)
            if (target.GameObject != null) return 3;
            
            // Position target
            return 1;
        }
        
        private uint GetTargetNetworkId(Target target)
        {
            if (!IsValidTarget(target)) return 0;
            
            Pawn pawn = target.Pawn;
            if (pawn != null)
            {
                return GetNetworkIdForPawn?.Invoke(pawn) ?? 0;
            }
            
            return 0;
        }
        
        private Target ReconstructTarget(NetworkAbilityCastRequest request)
        {
            return ReconstructTargetFromData(request.TargetType, request.TargetPosition, request.TargetNetworkId);
        }
        
        private Target ReconstructTarget(ActiveCast cast)
        {
            return ReconstructTargetFromData(cast.TargetType, cast.TargetPosition, cast.TargetNetworkId);
        }
        
        private Target ReconstructTargetFromData(byte targetType, Vector3 position, uint networkId)
        {
            switch (targetType)
            {
                case 0:
                    return default;
                    
                case 1: // Position
                    return new Target(position);
                    
                case 2: // Character
                    Pawn pawn = GetPawnByNetworkId?.Invoke(networkId);
                    return pawn != null ? new Target(pawn) : new Target(position);
                    
                case 3: // Object
                    // For now, treat as position
                    return new Target(position);
                    
                default:
                    return default;
            }
        }
        
        /// <summary>
        /// Checks if a Target struct has a valid target (either GameObject or explicit position).
        /// </summary>
        private bool IsValidTarget(Target target)
        {
            return target.GameObject != null || target.HasPosition;
        }
        
        private void CleanupExpiredEntries(float currentTime)
        {
            // Cleanup old cooldowns
            var expiredCooldowns = new List<(uint, int)>();
            foreach (var kvp in m_Cooldowns)
            {
                if (currentTime >= kvp.Value.EndTime)
                {
                    expiredCooldowns.Add(kvp.Key);
                }
            }
            
            foreach (var key in expiredCooldowns)
            {
                m_Cooldowns.Remove(key);
            }
            
            // Cleanup stale projectiles (should be destroyed by game logic, but safety)
            var staleProjectiles = new List<uint>();
            foreach (var kvp in m_ActiveProjectiles)
            {
                if (currentTime - kvp.Value.SpawnTime > 30f) // 30 second max lifetime
                {
                    staleProjectiles.Add(kvp.Key);
                }
            }
            
            foreach (var id in staleProjectiles)
            {
                ServerDestroyProjectile(id, ProjectileEventType.Destroyed);
            }
            
            // Cleanup stale impacts
            var staleImpacts = new List<uint>();
            foreach (var kvp in m_ActiveImpacts)
            {
                if (currentTime - kvp.Value.SpawnTime > 10f) // 10 second max
                {
                    staleImpacts.Add(kvp.Key);
                }
            }
            
            foreach (var id in staleImpacts)
            {
                m_ActiveImpacts.Remove(id);
            }
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
