#if GC2_MELEE
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.Melee;
using Arawn.NetworkingCore.LagCompensation;
using Arawn.GameCreator2.Networking.Combat;

namespace Arawn.GameCreator2.Networking.Melee
{
    /// <summary>
    /// Server-authoritative melee combat controller for GC2.
    /// Intercepts melee hit detection and routes through server validation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>How it works:</b>
    /// This component hooks into GC2's MeleeStance events to intercept hits before damage
    /// is applied. On clients, it sends hit requests to the server. On the server, it validates
    /// hits using lag compensation and applies damage authoritatively.
    /// </para>
    /// <para>
    /// <b>Setup:</b>
    /// 1. Add this component to Characters that use melee combat
    /// 2. Ensure NetworkCharacter is also present (with Combat Mode = Disabled)
    /// 3. Add NetworkMeleeManager to your scene for global coordination
    /// </para>
    /// </remarks>
    [RequireComponent(typeof(Character))]
    [AddComponentMenu("Game Creator/Network/Melee/Network Melee Controller")]
    [DefaultExecutionOrder(ApplicationManager.EXECUTION_ORDER_DEFAULT + 10)]
    public partial class NetworkMeleeController : MonoBehaviour
    {
        // ════════════════════════════════════════════════════════════════════════════════════════
        // INSPECTOR
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        [Header("Network Settings")]
        [Tooltip("Show optimistic hit effects before server confirmation.")]
        [SerializeField] private bool m_OptimisticEffects = true;
        
        [Tooltip("Maximum number of hits to buffer before flush.")]
        [SerializeField] private int m_MaxHitBuffer = 8;
        
        [Header("Lag Compensation")]
        [Tooltip("Configuration for lag compensation validation.")]
        [SerializeField] private MeleeValidationConfig m_ValidationConfig = new();
        
        [Header("Debug")]
        [SerializeField] private bool m_LogHits = false;
        [SerializeField] private bool m_DrawHitGizmos = false;
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // EVENTS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>Called when a hit is detected locally (before server validation).</summary>
        public event Action<NetworkMeleeHitRequest> OnHitDetected;
        
        /// <summary>Called when a hit is confirmed by server.</summary>
        public event Action<NetworkMeleeHitBroadcast> OnHitConfirmed;
        
        /// <summary>Called when a hit is rejected by server.</summary>
        public event Action<NetworkMeleeHitResponse> OnHitRejected;
        
        /// <summary>Called when attack state changes (for sync).</summary>
        public event Action<NetworkAttackState> OnAttackStateChanged;
        
        /// <summary>Called when block is requested (for network layer).</summary>
        public event Action<NetworkBlockRequest> OnBlockRequested;
        
        /// <summary>Called when block state changes.</summary>
        public event Action<NetworkBlockBroadcast> OnBlockStateChanged;
        
        /// <summary>Called when skill execution is requested (for network layer).</summary>
        public event Action<NetworkSkillRequest> OnSkillRequested;
        
        /// <summary>Called when skill is executed (broadcast received).</summary>
        public event Action<NetworkSkillBroadcast> OnSkillExecuted;
        
        /// <summary>Called when charge is requested (for network layer).</summary>
        public event Action<NetworkChargeRequest> OnChargeRequested;
        
        /// <summary>Called when charge state changes.</summary>
        public event Action<NetworkChargeBroadcast> OnChargeStateChanged;
        
        /// <summary>Called when a reaction should be played (broadcast received).</summary>
        public event Action<NetworkReactionBroadcast> OnReactionReceived;
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // PRIVATE FIELDS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        private Character m_Character;
        private NetworkCharacter m_NetworkCharacter;
        private MeleeStance m_MeleeStance;
        
        // Network role
        private bool m_IsServer;
        private bool m_IsLocalClient;
        private bool m_IsRemoteClient;
        
        // Hit interception state
        private static readonly List<ulong> s_SharedPendingRemovalBuffer = new(16);
        private readonly Dictionary<ulong, PendingHit> m_PendingHits = new(8);
        private readonly HashSet<int> m_ProcessedHits = new(32);
        private ushort m_NextRequestId = 1;
        private ushort m_LastIssuedRequestId = 1;
        
        // Attack state tracking
        private NetworkAttackState m_LastAttackState;
        private MeleePhase m_LastPhase;
        
        // Block state tracking
        private bool m_IsBlockingLocally;
        private float m_BlockStartTime;
        private int m_CurrentShieldHash;
        private readonly Dictionary<ulong, PendingBlockRequest> m_PendingBlockRequests = new(4);
        
        // Charge state tracking
        private NetworkChargeState m_ChargeState;
        private readonly Dictionary<ulong, PendingChargeRequest> m_PendingChargeRequests = new(4);
        
        // Skill request tracking
        private readonly Dictionary<ulong, PendingSkillRequest> m_PendingSkillRequests = new(8);
        private float m_LastValidatedSkillRequestTime;
        
        // Lag compensation validator (server-only)
        private MeleeLagCompensationValidator m_Validator;
        
        // Server-side block tracking (for all characters)
        private readonly Dictionary<uint, ServerBlockState> m_ServerBlockStates = new(32);
        
        // Reflection cache for hooking into GC2
        private static readonly FieldInfo s_AttacksField;
        private static readonly MethodInfo s_GetStrikersMethod;
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // STRUCTS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        private struct PendingHit : ITimedPendingRequest
        {
            public NetworkMeleeHitRequest Request;
            public float SentTime;
            public bool OptimisticPlayed;
            public float PendingSentTime => SentTime;
        }
        
        private struct PendingBlockRequest : ITimedPendingRequest
        {
            public NetworkBlockRequest Request;
            public float SentTime;
            public float PendingSentTime => SentTime;
        }
        
        private struct PendingChargeRequest : ITimedPendingRequest
        {
            public NetworkChargeRequest Request;
            public float SentTime;
            public float PendingSentTime => SentTime;
        }
        
        private struct PendingSkillRequest : ITimedPendingRequest
        {
            public NetworkSkillRequest Request;
            public float SentTime;
            public float PendingSentTime => SentTime;
        }
        
        /// <summary>Server-side block state for a character.</summary>
        private struct ServerBlockState
        {
            public bool IsBlocking;
            public float BlockStartTime;
            public int ShieldHash;
            public float ParryWindowEnd;
            public float CurrentDefense;
            public float MaxDefense;
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // STATIC CONSTRUCTOR
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        static NetworkMeleeController()
        {
            // Cache reflection for accessing internal GC2 fields
            // This allows us to hook without modifying GC2 source
            s_AttacksField = typeof(MeleeStance).GetField("m_Attacks", 
                BindingFlags.NonPublic | BindingFlags.Instance);

            if (s_AttacksField == null)
            {
                Debug.LogWarning(
                    "[NetworkMeleeController] Could not resolve MeleeStance.m_Attacks via reflection. " +
                    "Melee sync will degrade until patch signatures are updated.");
            }
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // PROPERTIES
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>The underlying GC2 Character.</summary>
        public Character Character => m_Character;
        
        /// <summary>Current attack state for network sync.</summary>
        public NetworkAttackState AttackState => m_LastAttackState;
        
        /// <summary>Whether optimistic effects are enabled.</summary>
        public bool OptimisticEffects
        {
            get => m_OptimisticEffects;
            set => m_OptimisticEffects = value;
        }
        
        /// <summary>Whether this is running on the server.</summary>
        public bool IsServer => m_IsServer;
        
        /// <summary>Whether this is the local player's character.</summary>
        public bool IsLocalClient => m_IsLocalClient;
        
        /// <summary>Shorthand for the character's network ID.</summary>
        private uint NetworkId => m_NetworkCharacter != null ? m_NetworkCharacter.NetworkId : 0;

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

        private ulong GetPendingKey(uint actorNetworkId, uint correlationId)
        {
            uint resolvedActorNetworkId = actorNetworkId != 0 ? actorNetworkId : NetworkId;
            if (resolvedActorNetworkId == 0 || correlationId == 0) return 0;
            return ((ulong)resolvedActorNetworkId << 32) | correlationId;
        }

        private bool TryTakePending<T>(
            Dictionary<ulong, T> pendingRequests,
            uint actorNetworkId,
            uint correlationId,
            out T pending)
        {
            ulong key = GetPendingKey(actorNetworkId, correlationId);
            if (key != 0 && pendingRequests.TryGetValue(key, out pending))
            {
                pendingRequests.Remove(key);
                return true;
            }

            pending = default;
            return false;
        }

        // ════════════════════════════════════════════════════════════════════════════════════════
        // UNITY LIFECYCLE
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        private void Awake()
        {
            m_Character = GetComponent<Character>();
            m_NetworkCharacter = GetComponent<NetworkCharacter>();
        }
        
        private void Start()
        {
            // Try to get initial melee stance
            TryGetMeleeStance();
        }
        
        private void OnDestroy()
        {
            UnsubscribeFromMeleeStance();
        }
        
        private void Update()
        {
            if (!m_IsLocalClient && !m_IsServer) return;

            if (m_MeleeStance == null)
            {
                TryGetMeleeStance();
            }
            
            // Track attack state changes
            UpdateAttackState();
            
            // Clean up old pending hits
            CleanupPendingHits();
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // INITIALIZATION
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Initialize the network melee controller with role information.
        /// Called by NetworkCharacter when network role is determined.
        /// </summary>
        public void Initialize(bool isServer, bool isLocalClient)
        {
            m_IsServer = isServer;
            m_IsLocalClient = isLocalClient;
            m_IsRemoteClient = !isServer && !isLocalClient;
            
            if (m_LogHits)
            {
                string role = m_IsServer ? "Server" : (m_IsLocalClient ? "LocalClient" : "RemoteClient");
                Debug.Log($"[NetworkMeleeController] {gameObject.name} initialized as {role}");
            }
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // STANCE TRACKING
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        private void OnStanceChanged(int stanceId)
        {
            if (stanceId == MeleeStance.ID)
            {
                TryGetMeleeStance();
            }
            else
            {
                UnsubscribeFromMeleeStance();
            }
        }
        
        private void TryGetMeleeStance()
        {
            m_MeleeStance = m_Character.Combat.RequestStance<MeleeStance>();
            
            if (m_MeleeStance != null)
            {
                SubscribeToMeleeStance();
            }
        }
        
        private void SubscribeToMeleeStance()
        {
            if (m_MeleeStance == null) return;
            
            // Subscribe to input events to track attacks
            m_MeleeStance.EventInputCharge += OnInputCharge;
            m_MeleeStance.EventInputExecute += OnInputExecute;
        }
        
        private void UnsubscribeFromMeleeStance()
        {
            if (m_MeleeStance == null) return;
            
            m_MeleeStance.EventInputCharge -= OnInputCharge;
            m_MeleeStance.EventInputExecute -= OnInputExecute;
            
            m_MeleeStance = null;
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // INPUT TRACKING
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        private void OnInputCharge(MeleeKey key)
        {
            if (!m_IsLocalClient) return;
            
            // Get current weapon to determine charge skill
            var weapon = GetCurrentMeleeWeapon();
            if (weapon == null) return;
            
            // Send charge request to server
            var request = new NetworkChargeRequest
            {
                RequestId = GetNextRequestId(),
                ActorNetworkId = NetworkId,
                CorrelationId = NetworkCorrelation.Compose(NetworkId, m_LastIssuedRequestId),
                ClientTimestamp = Time.time,
                InputKey = (byte)key,
                WeaponHash = weapon.Id.Hash
            };

            ulong pendingKey = GetPendingKey(request.ActorNetworkId, request.CorrelationId);
            if (pendingKey == 0)
            {
                Debug.LogWarning("[NetworkMeleeController] Ignoring charge request with invalid actor/correlation context.");
                return;
            }

            m_PendingChargeRequests[pendingKey] = new PendingChargeRequest
            {
                Request = request,
                SentTime = Time.time
            };
            
            OnChargeRequested?.Invoke(request);
            
            if (m_LogHits)
            {
                Debug.Log($"[NetworkMeleeController] Charge request sent: Key={key}, Weapon={weapon.name}");
            }
        }
        
        private void OnInputExecute(MeleeKey key)
        {
            if (!m_IsLocalClient) return;
            
            // Get current weapon and determine skill
            var weapon = GetCurrentMeleeWeapon();
            if (weapon == null) return;
            
            // Check if this is a charge release
            bool isChargeRelease = m_ChargeState.IsCharging && m_ChargeState.InputKey == (byte)key;
            float chargeDuration = isChargeRelease ? (Time.time - m_ChargeState.ChargeStartTime) : 0f;
            
            // Get target if any
            uint targetNetworkId = 0;
            var target = m_Character.Combat.Targets.Primary;
            if (target != null)
            {
                var targetNetChar = target.GetComponent<NetworkCharacter>();
                if (targetNetChar != null) targetNetworkId = targetNetChar.NetworkId;
            }
            
            // Build skill request
            ushort requestId = GetNextRequestId();
            var request = new NetworkSkillRequest
            {
                RequestId = requestId,
                ActorNetworkId = NetworkId,
                CorrelationId = NetworkCorrelation.Compose(NetworkId, requestId),
                TargetNetworkId = targetNetworkId,
                SkillHash = m_LastAttackState.SkillHash, // Will be updated by server
                WeaponHash = weapon.Id.Hash,
                ComboNodeId = m_LastAttackState.ComboNodeId,
                InputKey = (byte)key,
                IsChargeRelease = isChargeRelease,
                ChargeDuration = chargeDuration
            };

            ulong pendingKey = GetPendingKey(request.ActorNetworkId, request.CorrelationId);
            if (pendingKey == 0)
            {
                Debug.LogWarning("[NetworkMeleeController] Ignoring skill request with invalid actor/correlation context.");
                return;
            }

            m_PendingSkillRequests[pendingKey] = new PendingSkillRequest
            {
                Request = request,
                SentTime = Time.time
            };
            
            OnSkillRequested?.Invoke(request);
            
            // Clear charge state if this was a charge release
            if (isChargeRelease)
            {
                m_ChargeState = NetworkChargeState.None;
            }
            
            if (m_LogHits)
            {
                Debug.Log($"[NetworkMeleeController] Skill request sent: Key={key}, ChargeRelease={isChargeRelease}");
            }
        }
        
        /// <summary>
        /// Get the currently equipped melee weapon.
        /// </summary>
        private MeleeWeapon GetCurrentMeleeWeapon()
        {
            if (s_AttacksField == null || m_MeleeStance == null) return null;
            
            try
            {
                var attacks = s_AttacksField.GetValue(m_MeleeStance) as Attacks;
                return attacks?.Weapon;
            }
            catch
            {
                return null;
            }
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // ATTACK STATE SYNC
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        private void UpdateAttackState()
        {
            if (m_MeleeStance == null) return;
            
            MeleePhase currentPhase = m_MeleeStance.CurrentPhase;
            
            if (currentPhase != m_LastPhase)
            {
                m_LastPhase = currentPhase;
                
                // Build new attack state
                m_LastAttackState = NetworkAttackState.FromPhase(currentPhase);
                
                // Get skill/weapon info if in active phase
                if (currentPhase != MeleePhase.None)
                {
                    TryGetCurrentSkillInfo(ref m_LastAttackState);
                }
                
                OnAttackStateChanged?.Invoke(m_LastAttackState);
                
                // Clear processed hits when entering new strike phase
                if (currentPhase == MeleePhase.Strike)
                {
                    m_ProcessedHits.Clear();
                }
            }
        }
        
        private void TryGetCurrentSkillInfo(ref NetworkAttackState state)
        {
            // Use reflection to get current skill from Attacks state machine
            if (s_AttacksField == null || m_MeleeStance == null) return;
            
            try
            {
                var attacks = s_AttacksField.GetValue(m_MeleeStance) as Attacks;
                if (attacks != null)
                {
                    if (attacks.ComboSkill != null)
                    {
                        state.SkillHash = StableHashUtility.GetStableHash(attacks.ComboSkill.name);
                    }
                    
                    if (attacks.Weapon != null)
                    {
                        state.WeaponHash = attacks.Weapon.Id.Hash;
                    }
                    
                    state.ComboNodeId = (short)attacks.ComboId;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[NetworkMeleeController] Failed to get skill info: {e.Message}");
            }
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // VALIDATION HELPERS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Maps CombatValidationRejectionReason to MeleeHitRejectionReason.
        /// </summary>
        private static MeleeHitRejectionReason MapValidationRejection(CombatValidationRejectionReason reason)
        {
            return reason switch
            {
                CombatValidationRejectionReason.None => MeleeHitRejectionReason.None,
                CombatValidationRejectionReason.AttackerNotFound => MeleeHitRejectionReason.AttackerNotFound,
                CombatValidationRejectionReason.TargetNotFound => MeleeHitRejectionReason.TargetNotFound,
                CombatValidationRejectionReason.AttackerNotRegistered => MeleeHitRejectionReason.AttackerNotFound,
                CombatValidationRejectionReason.TargetNotRegistered => MeleeHitRejectionReason.TargetNotFound,
                CombatValidationRejectionReason.TargetNotActive => MeleeHitRejectionReason.TargetNotFound,
                CombatValidationRejectionReason.TargetInvincible => MeleeHitRejectionReason.TargetInvincible,
                CombatValidationRejectionReason.TargetDodging => MeleeHitRejectionReason.TargetDodged,
                CombatValidationRejectionReason.TargetDead => MeleeHitRejectionReason.TargetNotFound,
                CombatValidationRejectionReason.TimestampTooOld => MeleeHitRejectionReason.TimestampTooOld,
                CombatValidationRejectionReason.TimestampInFuture => MeleeHitRejectionReason.CheatSuspected,
                CombatValidationRejectionReason.NoHistoryAvailable => MeleeHitRejectionReason.TimestampTooOld,
                CombatValidationRejectionReason.OutOfMeleeRange => MeleeHitRejectionReason.OutOfRange,
                CombatValidationRejectionReason.OutsideAttackArc => MeleeHitRejectionReason.OutOfRange,
                CombatValidationRejectionReason.InvalidAttackPhase => MeleeHitRejectionReason.InvalidPhase,
                CombatValidationRejectionReason.WeaponMismatch => MeleeHitRejectionReason.WeaponMismatch,
                CombatValidationRejectionReason.SkillMismatch => MeleeHitRejectionReason.SkillMismatch,
                CombatValidationRejectionReason.AlreadyHitThisSwing => MeleeHitRejectionReason.AlreadyHit,
                CombatValidationRejectionReason.CheatSuspected => MeleeHitRejectionReason.CheatSuspected,
                _ => MeleeHitRejectionReason.CheatSuspected
            };
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // CLEANUP
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        private void CleanupPendingHits()
        {
            float timeout = 2f; // 2 second timeout
            float currentTime = Time.time;

            PendingRequestCleanup.RemoveTimedOut(
                m_PendingHits,
                s_SharedPendingRemovalBuffer,
                currentTime,
                timeout,
                timedOut =>
                {
                    if (m_LogHits)
                    {
                        Debug.LogWarning($"[NetworkMeleeController] Hit request timed out: {timedOut.Request.RequestId}");
                    }
                });

            PendingRequestCleanup.RemoveTimedOut(
                m_PendingBlockRequests,
                s_SharedPendingRemovalBuffer,
                currentTime,
                timeout);
            PendingRequestCleanup.RemoveTimedOut(
                m_PendingChargeRequests,
                s_SharedPendingRemovalBuffer,
                currentTime,
                timeout);
            PendingRequestCleanup.RemoveTimedOut(
                m_PendingSkillRequests,
                s_SharedPendingRemovalBuffer,
                currentTime,
                timeout);
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // DEBUG
        // ════════════════════════════════════════════════════════════════════════════════════════
        
#if UNITY_EDITOR
        private void OnDrawGizmos()
        {
            if (!m_DrawHitGizmos) return;
            
            // Draw pending hits
            Gizmos.color = Color.yellow;
            foreach (var pending in m_PendingHits.Values)
            {
                Gizmos.DrawWireSphere(pending.Request.HitPoint, 0.1f);
            }
        }
#endif
    }
}
#endif
