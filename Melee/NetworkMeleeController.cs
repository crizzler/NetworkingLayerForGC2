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

#if UNITY_NETCODE
using Unity.Netcode;
#endif

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
    public class NetworkMeleeController : MonoBehaviour
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
        private readonly List<PendingHit> m_PendingHits = new(8);
        private readonly HashSet<int> m_ProcessedHits = new(32);
        private ushort m_NextRequestId = 1;
        
        // Attack state tracking
        private NetworkAttackState m_LastAttackState;
        private MeleePhase m_LastPhase;
        
        // Block state tracking
        private bool m_IsBlockingLocally;
        private float m_BlockStartTime;
        private int m_CurrentShieldHash;
        private readonly List<PendingBlockRequest> m_PendingBlockRequests = new(4);
        
        // Charge state tracking
        private NetworkChargeState m_ChargeState;
        private readonly List<PendingChargeRequest> m_PendingChargeRequests = new(4);
        
        // Skill request tracking
        private readonly List<PendingSkillRequest> m_PendingSkillRequests = new(8);
        
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
        
        private struct PendingHit
        {
            public NetworkMeleeHitRequest Request;
            public float SentTime;
            public bool OptimisticPlayed;
        }
        
        private struct PendingBlockRequest
        {
            public NetworkBlockRequest Request;
            public float SentTime;
        }
        
        private struct PendingChargeRequest
        {
            public NetworkChargeRequest Request;
            public float SentTime;
        }
        
        private struct PendingSkillRequest
        {
            public NetworkSkillRequest Request;
            public ushort RequestId;
            public float SentTime;
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
            // Subscribe to combat stance changes
            m_Character.Combat.EventChangeStance += OnStanceChanged;
            
            // Try to get initial melee stance
            TryGetMeleeStance();
        }
        
        private void OnDestroy()
        {
            if (m_Character != null)
            {
                m_Character.Combat.EventChangeStance -= OnStanceChanged;
            }
            
            UnsubscribeFromMeleeStance();
        }
        
        private void Update()
        {
            if (!m_IsLocalClient && !m_IsServer) return;
            
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
                RequestId = m_NextRequestId++,
                ClientTimestamp = Time.time,
                InputKey = (byte)key,
                WeaponHash = weapon.Id.Hash
            };
            
            m_PendingChargeRequests.Add(new PendingChargeRequest
            {
                Request = request,
                SentTime = Time.time
            });
            
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
            var request = new NetworkSkillRequest
            {
                TargetNetworkId = targetNetworkId,
                SkillHash = m_LastAttackState.SkillHash, // Will be updated by server
                WeaponHash = weapon.Id.Hash,
                InputKey = (byte)key,
                IsChargeRelease = isChargeRelease,
                ChargeDuration = chargeDuration
            };
            
            m_PendingSkillRequests.Add(new PendingSkillRequest
            {
                Request = request,
                RequestId = m_NextRequestId++,
                SentTime = Time.time
            });
            
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
                        state.SkillHash = attacks.ComboSkill.name.GetHashCode();
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
        // HIT INTERCEPTION (Called by NetworkStriker or custom integrations)
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Intercept a hit detected by a striker.
        /// </summary>
        /// <param name="target">The hit target.</param>
        /// <param name="hitPoint">World position of hit.</param>
        /// <param name="direction">Strike direction in target's local space.</param>
        /// <param name="skill">The skill being used.</param>
        /// <returns>True if hit should be processed locally (server or optimistic), false otherwise.</returns>
        public bool InterceptHit(GameObject target, Vector3 hitPoint, Vector3 direction, Skill skill)
        {
            if (target == null) return false;
            
            int targetId = target.GetInstanceID();
            
            // Don't process same target twice in one strike
            if (m_ProcessedHits.Contains(targetId)) return false;
            m_ProcessedHits.Add(targetId);
            
            // Server processes hits directly
            if (m_IsServer)
            {
                return true;
            }
            
            // Remote clients don't process hits - they receive broadcasts
            if (m_IsRemoteClient)
            {
                return false;
            }
            
            // Local client - send to server
            var targetNetworkChar = target.GetComponent<NetworkCharacter>();
            uint targetNetworkId = targetNetworkChar != null ? targetNetworkChar.NetworkId : 0;
            uint attackerNetworkId = m_NetworkCharacter != null ? m_NetworkCharacter.NetworkId : 0;
            
            var request = new NetworkMeleeHitRequest
            {
                RequestId = m_NextRequestId++,
                ClientTimestamp = Time.time, // TODO: Use network time
                AttackerNetworkId = attackerNetworkId,
                TargetNetworkId = targetNetworkId,
                HitPoint = hitPoint,
                StrikeDirection = direction,
                SkillHash = skill != null ? skill.name.GetHashCode() : 0,
                WeaponHash = m_LastAttackState.WeaponHash,
                ComboNodeId = m_LastAttackState.ComboNodeId,
                AttackPhase = m_LastAttackState.Phase
            };
            
            m_PendingHits.Add(new PendingHit
            {
                Request = request,
                SentTime = Time.time,
                OptimisticPlayed = false
            });
            
            // Raise event for network layer to send
            OnHitDetected?.Invoke(request);
            
            if (m_LogHits)
            {
                Debug.Log($"[NetworkMeleeController] Hit request sent: {target.name} at {hitPoint}");
            }
            
            // Return optimistic setting
            return m_OptimisticEffects;
        }
        
        /// <summary>
        /// Intercept a StrikeOutput from GC2's striker system.
        /// </summary>
        public bool InterceptStrikeOutput(StrikeOutput output, Skill skill)
        {
            return InterceptHit(output.GameObject, output.Point, output.Direction, skill);
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // SERVER VALIDATION
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// [Server] Process a hit request from a client.
        /// </summary>
        public NetworkMeleeHitResponse ProcessHitRequest(NetworkMeleeHitRequest request, uint clientNetworkId)
        {
            if (!m_IsServer)
            {
                Debug.LogWarning("[NetworkMeleeController] ProcessHitRequest called on non-server");
                return new NetworkMeleeHitResponse
                {
                    RequestId = request.RequestId,
                    Validated = false,
                    RejectionReason = MeleeHitRejectionReason.CheatSuspected
                };
            }
            
            // Find target character
            var targetNetworkChar = NetworkMeleeManager.Instance?.GetCharacterByNetworkId(request.TargetNetworkId);
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
            if (targetCharacter == null)
            {
                return new NetworkMeleeHitResponse
                {
                    RequestId = request.RequestId,
                    Validated = false,
                    RejectionReason = MeleeHitRejectionReason.TargetNotFound
                };
            }
            
            // Check if target is invincible
            if (targetCharacter.Combat.Invincibility.IsInvincible)
            {
                return new NetworkMeleeHitResponse
                {
                    RequestId = request.RequestId,
                    Validated = false,
                    RejectionReason = MeleeHitRejectionReason.TargetInvincible
                };
            }
            
            // Check if target dodged
            if (targetCharacter.Dash != null && targetCharacter.Dash.IsDodge)
            {
                return new NetworkMeleeHitResponse
                {
                    RequestId = request.RequestId,
                    Validated = false,
                    RejectionReason = MeleeHitRejectionReason.TargetDodged
                };
            }
            
            // ═══════════════════════════════════════════════════════════════════════════════════
            // LAG COMPENSATION VALIDATION
            // ═══════════════════════════════════════════════════════════════════════════════════
            
            // Ensure validator is initialized
            if (m_Validator == null)
            {
                m_Validator = new MeleeLagCompensationValidator(m_ValidationConfig);
            }
            
            // Perform lag-compensated validation
            var validationResult = m_Validator.ValidateMeleeHit(
                request,
                m_Character,
                skill: null,  // TODO: Look up skill from hash
                weapon: null  // TODO: Look up weapon from hash
            );
            
            if (!validationResult.IsValid)
            {
                if (m_LogHits)
                {
                    Debug.Log($"[NetworkMeleeController] Hit rejected: {validationResult}");
                }
                
                return new NetworkMeleeHitResponse
                {
                    RequestId = request.RequestId,
                    Validated = false,
                    RejectionReason = MapValidationRejection(validationResult.RejectionReason),
                    Damage = 0f,
                    PoiseBroken = false
                };
            }
            
            // Hit validated with lag compensation!
            if (m_LogHits)
            {
                Debug.Log($"[NetworkMeleeController] Hit validated: {validationResult}");
            }
            
            return new NetworkMeleeHitResponse
            {
                RequestId = request.RequestId,
                Validated = true,
                RejectionReason = MeleeHitRejectionReason.None,
                Damage = validationResult.FinalDamage,
                PoiseBroken = false // TODO: Calculate from poise system
            };
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // RECEIVING RESPONSES & BROADCASTS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// [Client] Called when server responds to our hit request.
        /// </summary>
        public void ReceiveHitResponse(NetworkMeleeHitResponse response)
        {
            // Find pending request
            int index = m_PendingHits.FindIndex(p => p.Request.RequestId == response.RequestId);
            if (index < 0)
            {
                if (m_LogHits)
                {
                    Debug.LogWarning($"[NetworkMeleeController] Response for unknown request: {response.RequestId}");
                }
                return;
            }
            
            var pending = m_PendingHits[index];
            m_PendingHits.RemoveAt(index);
            
            if (response.Validated)
            {
                // Hit was confirmed - if we didn't play optimistic effects, play now
                if (!pending.OptimisticPlayed && !m_OptimisticEffects)
                {
                    // Effects will be played by broadcast
                }
            }
            else
            {
                // Hit was rejected
                OnHitRejected?.Invoke(response);
                
                if (m_LogHits)
                {
                    Debug.Log($"[NetworkMeleeController] Hit rejected: {response.RejectionReason}");
                }
            }
        }
        
        /// <summary>
        /// [All] Called when server broadcasts a confirmed hit.
        /// </summary>
        public void ReceiveHitBroadcast(NetworkMeleeHitBroadcast broadcast)
        {
            OnHitConfirmed?.Invoke(broadcast);
            
            // Play effects if this is a remote client or non-optimistic local
            bool shouldPlayEffects = m_IsRemoteClient || 
                (m_IsLocalClient && !m_OptimisticEffects);
            
            if (shouldPlayEffects)
            {
                PlayHitEffects(broadcast);
            }
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // EFFECTS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        private void PlayHitEffects(NetworkMeleeHitBroadcast broadcast)
        {
            // TODO: Look up skill by hash and play effects
            // This includes: particles, sounds, hit pause
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // BLOCK/SHIELD NETWORKING
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// [Client] Request to raise block/guard.
        /// </summary>
        public void RequestBlockStart()
        {
            if (!m_IsLocalClient) return;
            if (m_IsBlockingLocally) return;
            
            // Get shield from current weapon
            var weapon = GetCurrentMeleeWeapon();
            int shieldHash = weapon?.Shield != null ? weapon.Shield.name.GetHashCode() : 0;
            
            var request = new NetworkBlockRequest
            {
                RequestId = m_NextRequestId++,
                ClientTimestamp = Time.time,
                Action = NetworkBlockAction.Raise,
                ShieldHash = shieldHash
            };
            
            m_PendingBlockRequests.Add(new PendingBlockRequest
            {
                Request = request,
                SentTime = Time.time
            });
            
            // Optimistically start blocking locally for responsiveness
            m_IsBlockingLocally = true;
            m_BlockStartTime = Time.time;
            m_CurrentShieldHash = shieldHash;
            
            OnBlockRequested?.Invoke(request);
            
            if (m_LogHits)
            {
                Debug.Log($"[NetworkMeleeController] Block start requested");
            }
        }
        
        /// <summary>
        /// [Client] Request to lower block/guard.
        /// </summary>
        public void RequestBlockStop()
        {
            if (!m_IsLocalClient) return;
            if (!m_IsBlockingLocally) return;
            
            var request = new NetworkBlockRequest
            {
                RequestId = m_NextRequestId++,
                ClientTimestamp = Time.time,
                Action = NetworkBlockAction.Lower,
                ShieldHash = m_CurrentShieldHash
            };
            
            m_PendingBlockRequests.Add(new PendingBlockRequest
            {
                Request = request,
                SentTime = Time.time
            });
            
            // Optimistically stop blocking locally
            m_IsBlockingLocally = false;
            
            OnBlockRequested?.Invoke(request);
            
            if (m_LogHits)
            {
                Debug.Log($"[NetworkMeleeController] Block stop requested");
            }
        }
        
        /// <summary>
        /// [Server] Process a block request from client.
        /// </summary>
        public NetworkBlockResponse ProcessBlockRequest(NetworkBlockRequest request, uint clientNetworkId)
        {
            if (!m_IsServer)
            {
                return new NetworkBlockResponse
                {
                    RequestId = request.RequestId,
                    Validated = false,
                    RejectionReason = BlockRejectionReason.CheatSuspected
                };
            }
            
            // Check if character is busy (attacking, reacting, etc.)
            if (m_Character.Busy.IsBusy && request.Action == NetworkBlockAction.Raise)
            {
                return new NetworkBlockResponse
                {
                    RequestId = request.RequestId,
                    Validated = false,
                    RejectionReason = BlockRejectionReason.CharacterBusy
                };
            }
            
            // Check shield is equipped
            var weapon = GetCurrentMeleeWeapon();
            if (weapon?.Shield == null && request.Action == NetworkBlockAction.Raise)
            {
                return new NetworkBlockResponse
                {
                    RequestId = request.RequestId,
                    Validated = false,
                    RejectionReason = BlockRejectionReason.NoShieldEquipped
                };
            }
            
            uint charNetworkId = m_NetworkCharacter?.NetworkId ?? 0;
            float serverTime = Time.time;
            
            if (request.Action == NetworkBlockAction.Raise)
            {
                // Get shield properties
                var shield = weapon.Shield;
                var args = new Args(m_Character.gameObject);
                float defense = shield.GetDefense(args);
                float parryTime = 0.25f; // Default, could get from shield
                
                // Update server block state
                m_ServerBlockStates[charNetworkId] = new ServerBlockState
                {
                    IsBlocking = true,
                    BlockStartTime = serverTime,
                    ShieldHash = request.ShieldHash,
                    ParryWindowEnd = serverTime + parryTime,
                    CurrentDefense = defense,
                    MaxDefense = defense
                };
                
                // Actually raise guard on server
                m_Character.Combat.Block.RaiseGuard();
            }
            else
            {
                // Lower guard
                if (m_ServerBlockStates.ContainsKey(charNetworkId))
                {
                    var state = m_ServerBlockStates[charNetworkId];
                    state.IsBlocking = false;
                    m_ServerBlockStates[charNetworkId] = state;
                }
                
                m_Character.Combat.Block.LowerGuard();
            }
            
            return new NetworkBlockResponse
            {
                RequestId = request.RequestId,
                Validated = true,
                RejectionReason = BlockRejectionReason.None,
                ServerBlockStartTime = serverTime
            };
        }
        
        /// <summary>
        /// [Client] Called when server responds to block request.
        /// </summary>
        public void ReceiveBlockResponse(NetworkBlockResponse response)
        {
            // Find and remove pending request
            int index = m_PendingBlockRequests.FindIndex(p => p.Request.RequestId == response.RequestId);
            if (index >= 0)
            {
                m_PendingBlockRequests.RemoveAt(index);
            }
            
            if (!response.Validated)
            {
                // Revert optimistic block state
                m_IsBlockingLocally = !m_IsBlockingLocally;
                
                if (m_LogHits)
                {
                    Debug.Log($"[NetworkMeleeController] Block rejected: {response.RejectionReason}");
                }
            }
            else
            {
                // Sync block start time with server for accurate parry window
                m_BlockStartTime = response.ServerBlockStartTime;
            }
        }
        
        /// <summary>
        /// [All] Called when server broadcasts block state change.
        /// </summary>
        public void ReceiveBlockBroadcast(NetworkBlockBroadcast broadcast)
        {
            OnBlockStateChanged?.Invoke(broadcast);
            
            // If this is for our character, sync state
            uint ourNetworkId = m_NetworkCharacter?.NetworkId ?? 0;
            if (broadcast.CharacterNetworkId == ourNetworkId && m_IsRemoteClient)
            {
                // Apply block state from server
                if (broadcast.Action == NetworkBlockAction.Raise)
                {
                    m_Character.Combat.Block.RaiseGuard();
                }
                else
                {
                    m_Character.Combat.Block.LowerGuard();
                }
            }
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // SKILL EXECUTION NETWORKING
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// [Server] Process a skill execution request.
        /// </summary>
        public NetworkSkillResponse ProcessSkillRequest(NetworkSkillRequest request, uint clientNetworkId)
        {
            if (!m_IsServer)
            {
                return new NetworkSkillResponse
                {
                    RequestId = (ushort)request.InputKey,
                    Validated = false,
                    RejectionReason = SkillRejectionReason.CheatSuspected
                };
            }
            
            // Check if character is in valid state for skill
            if (m_Character.Busy.IsBusy)
            {
                // Allow during certain phases (recovery allows combo transitions)
                MeleePhase phase = m_MeleeStance?.CurrentPhase ?? MeleePhase.None;
                if (phase != MeleePhase.Recovery && phase != MeleePhase.None)
                {
                    return new NetworkSkillResponse
                    {
                        RequestId = (ushort)request.InputKey,
                        Validated = false,
                        RejectionReason = SkillRejectionReason.CharacterBusy
                    };
                }
            }
            
            // Validate weapon is equipped
            var weapon = GetCurrentMeleeWeapon();
            if (weapon == null || weapon.Id.Hash != request.WeaponHash)
            {
                return new NetworkSkillResponse
                {
                    RequestId = (ushort)request.InputKey,
                    Validated = false,
                    RejectionReason = SkillRejectionReason.WeaponNotEquipped
                };
            }
            
            // Validate charge if this is a charge release
            if (request.IsChargeRelease)
            {
                uint charNetworkId = m_NetworkCharacter?.NetworkId ?? 0;
                if (!m_ServerBlockStates.ContainsKey(charNetworkId))
                {
                    // Check charge state tracking (separate from block states)
                    // For now, trust client charge duration within limits
                    if (request.ChargeDuration < 0.1f || request.ChargeDuration > 10f)
                    {
                        return new NetworkSkillResponse
                        {
                            RequestId = (ushort)request.InputKey,
                            Validated = false,
                            RejectionReason = SkillRejectionReason.ChargeNotValid
                        };
                    }
                }
            }
            
            // TODO: Validate combo transition is legal
            // TODO: Check cooldowns, resources, etc.
            
            // Skill validated - execute on server
            // The actual skill execution happens through normal GC2 flow,
            // we just validate it was legal
            
            return new NetworkSkillResponse
            {
                RequestId = (ushort)request.InputKey,
                Validated = true,
                RejectionReason = SkillRejectionReason.None,
                ComboNodeId = m_LastAttackState.ComboNodeId
            };
        }
        
        /// <summary>
        /// [Client] Called when server responds to skill request.
        /// </summary>
        public void ReceiveSkillResponse(NetworkSkillResponse response)
        {
            // Find and remove pending request
            int index = m_PendingSkillRequests.FindIndex(p => p.RequestId == response.RequestId);
            if (index >= 0)
            {
                m_PendingSkillRequests.RemoveAt(index);
            }
            
            if (!response.Validated)
            {
                // Could cancel the optimistic skill execution
                // For now just log - in practice you might want to interrupt
                if (m_LogHits)
                {
                    Debug.Log($"[NetworkMeleeController] Skill rejected: {response.RejectionReason}");
                }
            }
        }
        
        /// <summary>
        /// [All] Called when server broadcasts skill execution.
        /// </summary>
        public void ReceiveSkillBroadcast(NetworkSkillBroadcast broadcast)
        {
            OnSkillExecuted?.Invoke(broadcast);
            
            // Remote clients need to play the skill
            uint ourNetworkId = m_NetworkCharacter?.NetworkId ?? 0;
            if (broadcast.CharacterNetworkId == ourNetworkId && m_IsRemoteClient)
            {
                // TODO: Trigger skill playback from hash lookup
                // This would involve finding the Skill asset and calling PlaySkill
            }
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // CHARGE ATTACK NETWORKING
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// [Server] Process a charge start request.
        /// </summary>
        public NetworkChargeResponse ProcessChargeRequest(NetworkChargeRequest request, uint clientNetworkId)
        {
            if (!m_IsServer)
            {
                return new NetworkChargeResponse
                {
                    RequestId = request.RequestId,
                    Validated = false
                };
            }
            
            // Validate weapon
            var weapon = GetCurrentMeleeWeapon();
            if (weapon == null || weapon.Id.Hash != request.WeaponHash)
            {
                return new NetworkChargeResponse
                {
                    RequestId = request.RequestId,
                    Validated = false
                };
            }
            
            // TODO: Look up charge skill from weapon's combo tree
            int chargeSkillHash = 0; // Would be determined by weapon + input key
            
            float serverTime = Time.time;
            
            // Track charge state on server
            m_ChargeState = new NetworkChargeState
            {
                IsCharging = true,
                InputKey = request.InputKey,
                ChargeSkillHash = chargeSkillHash,
                ChargeStartTime = serverTime,
                ChargeComboNodeId = -1
            };
            
            return new NetworkChargeResponse
            {
                RequestId = request.RequestId,
                Validated = true,
                ServerChargeStartTime = serverTime,
                ChargeSkillHash = chargeSkillHash
            };
        }
        
        /// <summary>
        /// [Client] Called when server responds to charge request.
        /// </summary>
        public void ReceiveChargeResponse(NetworkChargeResponse response)
        {
            // Find and remove pending request
            int index = m_PendingChargeRequests.FindIndex(p => p.Request.RequestId == response.RequestId);
            if (index >= 0)
            {
                m_PendingChargeRequests.RemoveAt(index);
            }
            
            if (response.Validated)
            {
                // Sync charge start time with server
                m_ChargeState = new NetworkChargeState
                {
                    IsCharging = true,
                    InputKey = m_ChargeState.InputKey,
                    ChargeSkillHash = response.ChargeSkillHash,
                    ChargeStartTime = response.ServerChargeStartTime,
                    ChargeComboNodeId = -1
                };
            }
            else
            {
                // Clear optimistic charge
                m_ChargeState = NetworkChargeState.None;
            }
        }
        
        /// <summary>
        /// [All] Called when server broadcasts charge state change.
        /// </summary>
        public void ReceiveChargeBroadcast(NetworkChargeBroadcast broadcast)
        {
            OnChargeStateChanged?.Invoke(broadcast);
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // REACTION NETWORKING
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// [Server] Broadcast a reaction to all clients.
        /// </summary>
        public NetworkReactionBroadcast CreateReactionBroadcast(
            uint targetNetworkId, 
            uint attackerNetworkId, 
            Vector3 direction, 
            float power, 
            IReaction reaction)
        {
            return new NetworkReactionBroadcast
            {
                CharacterNetworkId = targetNetworkId,
                FromNetworkId = attackerNetworkId,
                ReactionHash = reaction != null ? reaction.GetHashCode() : 0,
                Direction = NetworkReactionBroadcast.CompressDirection(direction),
                Power = NetworkReactionBroadcast.CompressPower(power)
            };
        }
        
        /// <summary>
        /// [All] Called when server broadcasts a reaction.
        /// </summary>
        public void ReceiveReactionBroadcast(NetworkReactionBroadcast broadcast)
        {
            OnReactionReceived?.Invoke(broadcast);
            
            // Play reaction on this character if it's the target
            uint ourNetworkId = m_NetworkCharacter?.NetworkId ?? 0;
            if (broadcast.CharacterNetworkId == ourNetworkId)
            {
                PlayReactionFromBroadcast(broadcast);
            }
        }
        
        private void PlayReactionFromBroadcast(NetworkReactionBroadcast broadcast)
        {
            if (m_MeleeStance == null) return;
            
            // Get attacker GameObject
            GameObject fromObject = null;
            if (broadcast.FromNetworkId != 0)
            {
                var attackerNetChar = NetworkMeleeManager.Instance?.GetCharacterByNetworkId(broadcast.FromNetworkId);
                if (attackerNetChar != null) fromObject = attackerNetChar.gameObject;
            }
            
            // Build reaction input
            Vector3 direction = broadcast.GetDirection();
            float power = broadcast.GetPower();
            var reactionInput = new ReactionInput(direction, power);
            
            // Play the reaction
            // TODO: Look up reaction by hash for specific reaction
            m_MeleeStance.PlayReaction(fromObject, reactionInput, null, true);
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // BLOCK RESULT CALCULATION (Server-side)
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// [Server] Evaluate if target is blocking and determine block result.
        /// </summary>
        public BlockEvaluationResult EvaluateBlock(
            uint targetNetworkId, 
            Vector3 attackDirection, 
            float attackPower,
            int skillHash)
        {
            if (!m_IsServer) return BlockEvaluationResult.NoBlock;
            
            // Get target's block state
            if (!m_ServerBlockStates.TryGetValue(targetNetworkId, out var blockState))
            {
                return BlockEvaluationResult.NoBlock;
            }
            
            if (!blockState.IsBlocking)
            {
                return BlockEvaluationResult.NoBlock;
            }
            
            // Get target character for angle check
            var targetNetChar = NetworkMeleeManager.Instance?.GetCharacterByNetworkId(targetNetworkId);
            if (targetNetChar == null) return BlockEvaluationResult.NoBlock;
            
            var targetCharacter = targetNetChar.GetComponent<Character>();
            if (targetCharacter == null) return BlockEvaluationResult.NoBlock;
            
            // Check attack angle vs block direction (default 180 degree coverage)
            Vector3 targetForward = targetCharacter.transform.forward;
            Vector3 flatAttackDir = new Vector3(attackDirection.x, 0f, attackDirection.z).normalized;
            float angle = Vector3.Angle(-flatAttackDir, targetForward);
            
            const float DefaultBlockAngle = 90f; // Half of 180 degree coverage
            if (angle > DefaultBlockAngle)
            {
                // Attack came from outside block arc
                return BlockEvaluationResult.NoBlock;
            }
            
            float serverTime = Time.time;
            
            // Check for parry (within parry window)
            if (serverTime <= blockState.ParryWindowEnd)
            {
                if (m_LogHits)
                {
                    Debug.Log($"[NetworkMeleeController] Attack PARRIED by {targetNetworkId}");
                }
                return BlockEvaluationResult.Parried;
            }
            
            // Check for block break
            float newDefense = blockState.CurrentDefense - attackPower;
            
            if (newDefense <= 0f)
            {
                // Block broken!
                var updatedState = blockState;
                updatedState.IsBlocking = false;
                updatedState.CurrentDefense = 0f;
                m_ServerBlockStates[targetNetworkId] = updatedState;
                
                // Force lower guard
                targetCharacter.Combat.Block.LowerGuard();
                
                if (m_LogHits)
                {
                    Debug.Log($"[NetworkMeleeController] Block BROKEN for {targetNetworkId}");
                }
                return BlockEvaluationResult.BlockBroken;
            }
            
            // Normal block - reduce defense
            var state = blockState;
            state.CurrentDefense = newDefense;
            m_ServerBlockStates[targetNetworkId] = state;
            
            if (m_LogHits)
            {
                Debug.Log($"[NetworkMeleeController] Attack BLOCKED by {targetNetworkId}, defense remaining: {newDefense}");
            }
            return BlockEvaluationResult.Blocked(newDefense);
        }
        
        /// <summary>
        /// [Server] Reset block defense (e.g., after cooldown).
        /// </summary>
        public void ResetBlockDefense(uint networkId, float defense)
        {
            if (!m_IsServer) return;
            
            if (m_ServerBlockStates.TryGetValue(networkId, out var state))
            {
                state.CurrentDefense = defense;
                state.MaxDefense = defense;
                m_ServerBlockStates[networkId] = state;
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
            
            // Cleanup pending hits
            for (int i = m_PendingHits.Count - 1; i >= 0; i--)
            {
                if (currentTime - m_PendingHits[i].SentTime > timeout)
                {
                    if (m_LogHits)
                    {
                        Debug.LogWarning($"[NetworkMeleeController] Hit request timed out: {m_PendingHits[i].Request.RequestId}");
                    }
                    m_PendingHits.RemoveAt(i);
                }
            }
            
            // Cleanup pending block requests
            for (int i = m_PendingBlockRequests.Count - 1; i >= 0; i--)
            {
                if (currentTime - m_PendingBlockRequests[i].SentTime > timeout)
                {
                    m_PendingBlockRequests.RemoveAt(i);
                }
            }
            
            // Cleanup pending charge requests
            for (int i = m_PendingChargeRequests.Count - 1; i >= 0; i--)
            {
                if (currentTime - m_PendingChargeRequests[i].SentTime > timeout)
                {
                    m_PendingChargeRequests.RemoveAt(i);
                }
            }
            
            // Cleanup pending skill requests
            for (int i = m_PendingSkillRequests.Count - 1; i >= 0; i--)
            {
                if (currentTime - m_PendingSkillRequests[i].SentTime > timeout)
                {
                    m_PendingSkillRequests.RemoveAt(i);
                }
            }
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
            foreach (var pending in m_PendingHits)
            {
                Gizmos.DrawWireSphere(pending.Request.HitPoint, 0.1f);
            }
        }
#endif
    }
}
#endif
