#if GC2_MELEE
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
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
        [SerializeField] private bool m_LogMeleeSync = false;
        [SerializeField] private bool m_LogSkillDiagnostics = false;

        [Header("Input Recovery")]
        [Tooltip("When a local melee input has no combat target, choose the nearest other NetworkCharacter before GC2 evaluates combo conditions.")]
        [SerializeField] private bool m_RecoverMissingCombatTarget = true;
        [SerializeField] private float m_RecoverTargetRadius = 20f;
#if UNITY_EDITOR
        [SerializeField] private bool m_DrawHitGizmos = false;
#endif
        
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

        /// <summary>Called when the locally equipped melee weapon changes.</summary>
        public event Action<NetworkMeleeWeaponState> OnWeaponStateChanged;
        
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
        private bool m_HasLastReactionBroadcast;
        private NetworkReactionBroadcast m_LastReactionBroadcast;
        private float m_LastReactionBroadcastTime;
        private const float OwnerHitPreReactionReconciliationSuppression = 0.25f;
        private const float OwnerReactionInitialReconciliationSuppression = 1.00f;
        private const float OwnerReactionRefreshReconciliationSuppression = 0.35f;
        private const float OwnerReactionExitReconciliationSuppression = 0.50f;
        
        // Block state tracking
        private bool m_IsBlockingLocally;
        private float m_BlockStartTime;
        private int m_CurrentShieldHash;
        private bool m_HasObservedGc2BlockState;
        private bool m_LastObservedGc2BlockState;
        private readonly Dictionary<ulong, PendingBlockRequest> m_PendingBlockRequests = new(4);
        
        // Charge state tracking
        private NetworkChargeState m_ChargeState;
        private readonly Dictionary<ulong, PendingChargeRequest> m_PendingChargeRequests = new(4);
        
        // Skill request tracking
        private readonly Dictionary<ulong, PendingSkillRequest> m_PendingSkillRequests = new(8);
        private float m_LastValidatedSkillRequestTime;
        private bool m_HasQueuedSkillInput;
        private MeleeKey m_QueuedSkillKey;
        private bool m_QueuedSkillIsChargeRelease;
        private float m_QueuedSkillChargeDuration;
        private float m_QueuedSkillTime;
        private int m_QueuedSkillFrame;
        private bool m_LoggedQueuedSkillWaitForWeapon;
        private bool m_LoggedQueuedSkillWaitForSkill;
        private float m_QueuedSkillOriginalTime;
        private int m_QueuedSkillOriginalFrame;
        private int m_QueuedSkillReplayCount;
        private bool m_QueuedSkillRetryQueued;
        private bool m_ReplayedSkillInputPending;
        private MeleeKey m_ReplayedSkillInputKey;
        private bool m_LoggedQueuedSkillWaitForDash;
        private bool m_LoggedQueuedSkillWaitForReplayConsume;
        private const float QueuedSkillResolutionTimeout = 0.75f;
        private const float QueuedSkillDashRecoveryWindow = 0.45f;
        private const int MaxQueuedSkillInputReplays = 2;

        // Input/focus diagnostics
        private bool m_HasWindowInputContextSample;
        private bool m_LastApplicationFocus;
        private CursorLockMode m_LastCursorLockState;
        private bool m_LastCursorVisible;
        private GameCreator.Runtime.Melee.Input m_SubscribedRawMeleeInput;
        private float m_LastDashStartTime = -100f;
        private float m_LastDashFinishTime = -100f;
        private int m_LastDashStartFrame = -1;
        private int m_LastDashFinishFrame = -1;
        private float m_LastAttackPhaseExitTime = -100f;
        private int m_LastAttackPhaseExitFrame = -1;
        private float m_LastRawExecuteTime = -100f;
        private int m_LastRawExecuteFrame = -1;
        private MeleeKey m_LastRawExecuteKey;
        private float m_LastConsumedExecuteTime = -100f;
        private int m_LastConsumedExecuteFrame = -1;
        private MeleeKey m_LastConsumedExecuteKey;
        private float m_LastSkillRequestSentTime = -100f;
        private ushort m_LastSkillRequestSentId;
        private int m_LastSkillRequestSentSkillHash;
        private float m_NextInputLockProbeTime;

        // Weapon state tracking
        private NetworkMeleeWeaponState m_LastWeaponState;
        
        // Lag compensation validator (server-only)
        private MeleeLagCompensationValidator m_Validator;
        
        // Server-side block tracking (for all characters)
        private readonly Dictionary<uint, ServerBlockState> m_ServerBlockStates = new(32);
        
        // Reflection cache for hooking into GC2
        private static readonly FieldInfo s_AttacksField;
        private static readonly MethodInfo s_PlaySkillDirectMethod;
        private static readonly MethodInfo s_PlayReactionDirectMethod;
        
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
            s_PlaySkillDirectMethod = typeof(MeleeStance).GetMethod(
                "PlaySkillDirect",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[] { typeof(MeleeWeapon), typeof(Skill), typeof(GameObject) },
                null);
            s_PlayReactionDirectMethod = typeof(MeleeStance).GetMethod(
                "PlayReactionDirect",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[] { typeof(GameObject), typeof(ReactionInput), typeof(IReaction), typeof(bool) },
                null);

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

        private bool ShouldLogMeleeSync => m_LogHits || m_LogMeleeSync;
        private bool ShouldLogSkillDiagnostics =>
            m_LogSkillDiagnostics ||
            ShouldLogMeleeSync ||
            NetworkMeleeDebug.ForceSkillDiagnostics;
        private bool ShouldLogInputLockDiagnostics =>
            ShouldLogSkillDiagnostics ||
            NetworkMeleeDebug.ForceInputLockDiagnostics;
        private bool ShouldLogReactionDiagnostics =>
            ShouldLogMeleeSync ||
            NetworkMeleeDebug.ForceReactionDiagnostics;

        private string DebugRole =>
            m_IsServer ? m_IsLocalClient ? "HostServerLocal" : "Server" :
            m_IsLocalClient ? "LocalClient" :
            m_IsRemoteClient ? "RemoteClient" : "Uninitialized";

        private void LogMeleeSync(string message)
        {
            if (!ShouldLogMeleeSync) return;
            Debug.Log($"[NetworkMeleeController] {name} netId={NetworkId} role={DebugRole} {message}", this);
        }

        private void LogMeleeSyncWarning(string message)
        {
            if (!ShouldLogMeleeSync) return;
            Debug.LogWarning($"[NetworkMeleeController] {name} netId={NetworkId} role={DebugRole} {message}", this);
        }

        private void LogSkillDiagnostics(string message)
        {
            if (!ShouldLogSkillDiagnostics) return;
            Debug.Log($"[NetworkMeleeSkillDebug][Controller] {name} netId={NetworkId} role={DebugRole} {message}", this);
        }

        private void LogSkillDiagnosticsWarning(string message)
        {
            if (!ShouldLogSkillDiagnostics) return;
            Debug.LogWarning($"[NetworkMeleeSkillDebug][Controller] {name} netId={NetworkId} role={DebugRole} {message}", this);
        }

        private void LogInputLockDiagnostics(string message)
        {
            if (!ShouldLogInputLockDiagnostics) return;
            Debug.Log($"[NetworkMeleeInputDebug][Controller] {name} netId={NetworkId} role={DebugRole} {message}", this);
        }

        private void LogInputLockWarning(string message)
        {
            if (!ShouldLogInputLockDiagnostics) return;
            Debug.LogWarning($"[NetworkMeleeInputDebug][Controller] {name} netId={NetworkId} role={DebugRole} {message}", this);
        }

        private void LogReactionDiagnostics(string message)
        {
            if (!ShouldLogReactionDiagnostics) return;
            Debug.Log($"[NetworkMeleeReactionDebug][Controller] {name} netId={NetworkId} role={DebugRole} {message}", this);
        }

        private void LogReactionWarning(string message)
        {
            if (!ShouldLogReactionDiagnostics) return;
            Debug.LogWarning($"[NetworkMeleeReactionDebug][Controller] {name} netId={NetworkId} role={DebugRole} {message}", this);
        }

        private static string FormatVector(Vector3 value)
        {
            return $"({value.x:F3},{value.y:F3},{value.z:F3})";
        }

        private static bool IsVerticalReactionDirection(Vector3 direction)
        {
            return direction.sqrMagnitude > 0.0001f && Mathf.Abs(direction.normalized.y) >= 0.45f;
        }

        private static string ReactionLabel(IReaction reaction)
        {
            if (reaction == null) return "null";
            return reaction is UnityEngine.Object unityObject ? unityObject.name : reaction.GetType().Name;
        }

        private static string ReactionItemLabel(IReaction reaction, ReactionItem item)
        {
            if (item == null) return "no-match";

            string clipName = item.AnimationClip != null ? item.AnimationClip.name : "nullClip";
            string rootMotion = reaction is Reaction reactionAsset
                ? reactionAsset.UseRootMotion.ToString()
                : "unknown";

            return $"{ReactionLabel(reaction)} clip={clipName} rootMotion={rootMotion} " +
                   $"gravity={item.Gravity:F3} cancel={item.CancelTime:F3}";
        }

        private string BuildReactionCandidateDebug(
            GameObject fromObject,
            ReactionInput input,
            IReaction explicitReaction)
        {
            if (m_Character == null) return "character=null";

            var args = new Args(fromObject, m_Character.gameObject);
            var sb = new StringBuilder(256);

            if (explicitReaction != null)
            {
                ReactionItem explicitItem = explicitReaction.CanRun(m_Character, args, input);
                sb.Append("explicit=").Append(ReactionItemLabel(explicitReaction, explicitItem));
                if (explicitItem != null) return sb.ToString();
                sb.Append(' ');
            }

            Weapon[] weapons = m_Character.Combat?.Weapons;
            if (weapons != null)
            {
                for (int i = 0; i < weapons.Length; i++)
                {
                    IWeapon weaponAsset = weapons[i]?.Asset;
                    IReaction reaction = weaponAsset?.HitReaction;
                    if (reaction == null) continue;

                    ReactionItem item = reaction.CanRun(m_Character, args, input);
                    sb.Append("weapon[").Append(i).Append("]=")
                        .Append(weaponAsset.GetName(args)).Append(' ')
                        .Append(ReactionItemLabel(reaction, item)).Append(' ');
                    if (item != null) return sb.ToString();
                }
            }

            Reaction defaultReaction = m_Character.Animim != null ? m_Character.Animim.Reaction : null;
            if (defaultReaction != null)
            {
                ReactionItem item = defaultReaction.CanRun(m_Character, args, input);
                sb.Append("default=").Append(ReactionItemLabel(defaultReaction, item));
                if (item != null) return sb.ToString();
            }

            return sb.Length > 0 ? sb.ToString() : "no reaction assets found";
        }

        private static string AgeLabel(float timestamp)
        {
            return timestamp <= -99f ? "never" : $"{Time.time - timestamp:F3}s";
        }

        private void RememberWindowInputContext(bool focused)
        {
            m_LastApplicationFocus = focused;
            m_LastCursorLockState = Cursor.lockState;
            m_LastCursorVisible = Cursor.visible;
            m_HasWindowInputContextSample = true;
        }

        private void ObserveWindowInputContext()
        {
            if (!m_IsLocalClient) return;

            bool focused = Application.isFocused;
            CursorLockMode cursorLockState = Cursor.lockState;
            bool cursorVisible = Cursor.visible;

            if (!m_HasWindowInputContextSample)
            {
                RememberWindowInputContext(focused);
                return;
            }

            if (focused == m_LastApplicationFocus &&
                cursorLockState == m_LastCursorLockState &&
                cursorVisible == m_LastCursorVisible)
            {
                return;
            }

            LogInputLockDiagnostics(
                $"window/input context changed focused={m_LastApplicationFocus}->{focused} " +
                $"cursorLock={m_LastCursorLockState}->{cursorLockState} " +
                $"cursorVisible={m_LastCursorVisible}->{cursorVisible} " +
                BuildSkillInputDebugContext(resolvedState: m_LastAttackState, hasResolvedState: true));
            RememberWindowInputContext(focused);
        }

        private string BuildSkillInputDebugContext(
            MeleeWeapon resolvedWeapon = null,
            NetworkAttackState resolvedState = default,
            bool hasResolvedState = false)
        {
            MeleeWeapon currentWeapon = resolvedWeapon ?? GetCurrentMeleeWeapon();
            string phase = m_MeleeStance != null ? m_MeleeStance.CurrentPhase.ToString() : "NoStance";
            bool busy = m_Character != null && m_Character.Busy.IsBusy;
            bool armsBusy = m_Character != null && m_Character.Busy.AreArmsBusy;
            bool legsBusy = m_Character != null && m_Character.Busy.AreLegsBusy;
            float queuedAge = m_HasQueuedSkillInput ? Time.time - m_QueuedSkillTime : 0f;
            int queuedFrameAge = m_HasQueuedSkillInput ? Time.frameCount - m_QueuedSkillFrame : 0;
            float characterTime = m_Character != null ? m_Character.Time.Time : -1f;

            var sb = new StringBuilder(512);
            sb.Append("ctx{");
            sb.Append("frame=").Append(Time.frameCount);
            sb.Append(" time=").Append(Time.time.ToString("F3"));
            sb.Append(" charTime=").Append(characterTime.ToString("F3"));
            sb.Append(" focused=").Append(Application.isFocused);
            sb.Append(" cursorLock=").Append(Cursor.lockState);
            sb.Append(" cursorVisible=").Append(Cursor.visible);
            sb.Append(" stance=").Append(m_MeleeStance != null);
            sb.Append(" phase=").Append(phase);
            sb.Append(" busy=").Append(busy);
            sb.Append(" armsBusy=").Append(armsBusy);
            sb.Append(" legsBusy=").Append(legsBusy);
            sb.Append(" queued=").Append(m_HasQueuedSkillInput);
            sb.Append(" queuedAge=").Append(queuedAge.ToString("F3"));
            sb.Append(" queuedFrameAge=").Append(queuedFrameAge);
            sb.Append(" pending=").Append(m_PendingSkillRequests.Count);
            sb.Append(" lastSkill=").Append(m_LastAttackState.SkillHash);
            sb.Append(" lastWeapon=").Append(m_LastAttackState.WeaponHash);
            sb.Append(" lastCombo=").Append(m_LastAttackState.ComboNodeId);

            if (hasResolvedState)
            {
                sb.Append(" resolvedSkill=").Append(resolvedState.SkillHash);
                sb.Append(" resolvedWeapon=").Append(resolvedState.WeaponHash);
                sb.Append(" resolvedCombo=").Append(resolvedState.ComboNodeId);
            }

            sb.Append(" currentWeapon=")
                .Append(currentWeapon != null ? currentWeapon.name : "null")
                .Append('#')
                .Append(currentWeapon != null ? currentWeapon.Id.Hash : 0);

            GameCreator.Runtime.Melee.Input meleeInput = null;
            int previousComboId = hasResolvedState ? resolvedState.ComboNodeId : m_LastAttackState.ComboNodeId;

            try
            {
                var attacks = s_AttacksField?.GetValue(m_MeleeStance) as Attacks;
                if (attacks != null)
                {
                    meleeInput = attacks.Input;
                    previousComboId = attacks.ComboId;

                    sb.Append(" attacksPhase=").Append(attacks.Phase);
                    sb.Append(" attacksWeapon=")
                        .Append(attacks.Weapon != null ? attacks.Weapon.name : "null")
                        .Append('#')
                        .Append(attacks.Weapon != null ? attacks.Weapon.Id.Hash : 0);
                    sb.Append(" attacksCombo=").Append(attacks.ComboId);
                    sb.Append(" attacksCharge=").Append(attacks.ChargeId);
                    sb.Append(" attacksComboSkill=")
                        .Append(attacks.ComboSkill != null ? attacks.ComboSkill.name : "null");
                    sb.Append(" attacksChargeSkill=")
                        .Append(attacks.ChargeSkill != null ? attacks.ChargeSkill.name : "null");

                    if (meleeInput != null)
                    {
                        sb.Append(" inputExecQueued=").Append(meleeInput.HasExecuteInQueue);
                        sb.Append(" inputChargeQueued=").Append(meleeInput.HasChargeInQueue);
                        sb.Append(" inputExecKey=").Append(meleeInput.ExecuteKey);
                        sb.Append(" inputChargeKey=").Append(meleeInput.ChargeKey);
                        sb.Append(" inputExecFrame=").Append(meleeInput.ExecuteActiveFrame);
                        sb.Append(" inputChargeFrame=").Append(meleeInput.ChargeActiveFrame);
                        sb.Append(" inputBetweenExec=").Append(meleeInput.TimeBetweenExecutions.ToString("F3"));
                        sb.Append(" inputBuffer=").Append(meleeInput.BufferWindow.ToString("F3"));
                    }
                }
                else
                {
                    sb.Append(" attacks=null");
                }
            }
            catch (Exception e)
            {
                sb.Append(" attacksReadError=").Append(e.GetType().Name).Append(':').Append(e.Message);
            }

            sb.Append(' ');
            sb.Append(BuildTargetDebug());
            sb.Append(' ');
            sb.Append(BuildCombatWeaponsDebug(meleeInput, previousComboId));
            sb.Append('}');
            return sb.ToString();
        }

        private string BuildTargetDebug()
        {
            GameObject target = m_Character != null ? m_Character.Combat.Targets.Primary : null;
            if (target == null) return $"target=null selfPos={transform.position}";

            var targetNetChar = target.GetComponent<NetworkCharacter>();
            float distance = Vector3.Distance(transform.position, target.transform.position);
            return $"target={target.name}#{(targetNetChar != null ? targetNetChar.NetworkId.ToString() : "noNet")} targetDistance={distance:F2} selfPos={transform.position}";
        }

        private bool IsUsableCombatTarget(GameObject target)
        {
            if (target == null) return false;
            if (target == gameObject) return false;
            if (target.transform.IsChildOf(transform)) return false;

            NetworkCharacter targetNetworkCharacter = target.GetComponentInParent<NetworkCharacter>();
            if (targetNetworkCharacter == null) return false;
            if (targetNetworkCharacter == m_NetworkCharacter) return false;
            if (m_NetworkCharacter != null &&
                targetNetworkCharacter.NetworkId != 0 &&
                targetNetworkCharacter.NetworkId == m_NetworkCharacter.NetworkId)
            {
                return false;
            }

            return true;
        }

        private bool TryRecoverMissingCombatTargetForInput(string reason)
        {
            if (!m_RecoverMissingCombatTarget) return false;
            if (!m_IsLocalClient || m_Character == null) return false;

            GameObject currentTarget = m_Character.Combat.Targets.Primary;
            if (IsUsableCombatTarget(currentTarget)) return false;

            NetworkCharacter target = FindNearestCombatTarget();
            if (target == null) return false;

            m_Character.Combat.Targets.Primary = target.gameObject;

            LogInputLockDiagnostics(
                $"recovered missing combat target before {reason}: " +
                $"target={target.name}#{target.NetworkId} distance={Vector3.Distance(transform.position, target.transform.position):F2} " +
                BuildSkillInputDebugContext(resolvedState: m_LastAttackState, hasResolvedState: true));
            return true;
        }

        private NetworkCharacter FindNearestCombatTarget()
        {
            NetworkCharacter[] candidates = FindObjectsByType<NetworkCharacter>(
                FindObjectsInactive.Exclude,
                FindObjectsSortMode.None);

            NetworkCharacter nearest = null;
            float maxSqrDistance = m_RecoverTargetRadius > 0f
                ? m_RecoverTargetRadius * m_RecoverTargetRadius
                : float.PositiveInfinity;
            float nearestSqrDistance = float.PositiveInfinity;

            for (int i = 0; i < candidates.Length; i++)
            {
                NetworkCharacter candidate = candidates[i];
                if (candidate == null) continue;
                if (candidate == m_NetworkCharacter) continue;
                if (candidate.gameObject == gameObject) continue;
                if (candidate.GetComponent<Character>() == null) continue;
                if (m_NetworkCharacter != null &&
                    candidate.NetworkId != 0 &&
                    candidate.NetworkId == m_NetworkCharacter.NetworkId)
                {
                    continue;
                }

                float sqrDistance = (candidate.transform.position - transform.position).sqrMagnitude;
                if (sqrDistance > maxSqrDistance) continue;
                if (sqrDistance >= nearestSqrDistance) continue;

                nearest = candidate;
                nearestSqrDistance = sqrDistance;
            }

            return nearest;
        }

        private string BuildCombatWeaponsDebug(GameCreator.Runtime.Melee.Input meleeInput, int previousComboId)
        {
            if (m_Character?.Combat?.Weapons == null) return "combatWeapons=null";

            int totalWeapons = 0;
            int meleeWeapons = 0;
            var sb = new StringBuilder(256);

            foreach (Weapon equippedWeapon in m_Character.Combat.Weapons)
            {
                totalWeapons++;
                if (equippedWeapon.Asset is not MeleeWeapon meleeWeapon) continue;

                if (meleeWeapons > 0) sb.Append('|');
                meleeWeapons++;
                sb.Append(meleeWeapon.name).Append('#').Append(meleeWeapon.Id.Hash);
                sb.Append(':').Append(DescribeComboCandidates(meleeWeapon, previousComboId, meleeInput));
            }

            return $"combatWeapons={totalWeapons} meleeWeapons={meleeWeapons} meleeCombos=[{sb}]";
        }

        private string DescribeComboCandidates(
            MeleeWeapon weapon,
            int previousComboId,
            GameCreator.Runtime.Melee.Input meleeInput)
        {
            if (weapon == null) return "weapon=null";
            if (weapon.Combo == null) return "combo=null";

            int[] roots = weapon.Combo.RootIds ?? Array.Empty<int>();
            List<int> candidates = previousComboId == ComboTree.NODE_INVALID
                ? new List<int>(roots)
                : weapon.Combo.Children(previousComboId);

            var sb = new StringBuilder(128);
            sb.Append("roots=").Append(roots.Length);
            sb.Append(",prev=").Append(previousComboId);
            sb.Append(",candidates=").Append(candidates.Count);
            sb.Append('{');

            int count = ShouldLogInputLockDiagnostics
                ? Mathf.Min(candidates.Count, 12)
                : Mathf.Min(candidates.Count, 6);
            for (int i = 0; i < count; i++)
            {
                if (i > 0) sb.Append(',');

                int candidateId = candidates[i];
                ComboItem item = weapon.Combo.Get(candidateId);
                sb.Append(candidateId).Append(':');

                if (item == null)
                {
                    sb.Append("null");
                    continue;
                }

                sb.Append(item.Key).Append('/').Append(item.Mode).Append('/').Append(item.When);
                sb.Append('/').Append(item.Skill != null ? item.Skill.name : "nullSkill");

                if (meleeInput != null)
                {
                    bool tapReady = false;
                    string conditionsReady = "skip";

                    try
                    {
                        tapReady = item.CheckConsumeExecuteTap(meleeInput, m_MeleeStance?.Args);
                        if (tapReady && m_MeleeStance?.Args != null)
                        {
                            conditionsReady = item.CheckConditions(m_MeleeStance.Args).ToString();
                        }
                    }
                    catch (Exception e)
                    {
                        conditionsReady = $"error:{e.GetType().Name}";
                    }

                    sb.Append("/tapReady=").Append(tapReady);
                    sb.Append("/conditions=").Append(conditionsReady);
                }
            }

            if (candidates.Count > count) sb.Append(",...");
            sb.Append('}');
            return sb.ToString();
        }

        private void SuppressLocalOwnerReconciliation(float duration, bool syncOwnerPose = false)
        {
            if (!m_IsLocalClient || m_IsServer) return;

            UnitDriverNetworkClient clientDriver = m_NetworkCharacter != null ? m_NetworkCharacter.ClientDriver : null;
            clientDriver ??= m_Character?.Driver as UnitDriverNetworkClient;
            if (clientDriver == null) return;

            clientDriver.SuppressReconciliation(duration);
            if (syncOwnerPose)
            {
                clientDriver.EnableOwnerAuthorityPoseSync(duration);
            }
        }

        private void RefreshLocalReactionReconciliationSuppression()
        {
            if (m_MeleeStance == null || m_MeleeStance.CurrentPhase != MeleePhase.Reaction) return;
            SuppressLocalOwnerReconciliation(OwnerReactionRefreshReconciliationSuppression, true);
        }

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

            if (m_Character != null)
            {
                m_Character.Combat.EventEquip += OnWeaponEquipped;
                m_Character.Combat.EventUnequip += OnWeaponUnequipped;
                m_Character.Dash.EventDashStart += OnDashStart;
                m_Character.Dash.EventDashFinish += OnDashFinish;
            }
        }
        
        private void Start()
        {
            // Try to get initial melee stance
            TryGetMeleeStance();
        }
        
        private void OnDestroy()
        {
            UnsubscribeFromMeleeStance();

            if (m_Character != null)
            {
                m_Character.Combat.EventEquip -= OnWeaponEquipped;
                m_Character.Combat.EventUnequip -= OnWeaponUnequipped;
                m_Character.Dash.EventDashStart -= OnDashStart;
                m_Character.Dash.EventDashFinish -= OnDashFinish;
            }
        }

        private void OnApplicationFocus(bool hasFocus)
        {
            if (!m_IsLocalClient) return;

            RememberWindowInputContext(hasFocus);
            LogInputLockDiagnostics(
                $"application focus callback hasFocus={hasFocus} " +
                BuildSkillInputDebugContext(resolvedState: m_LastAttackState, hasResolvedState: true));
        }

        private void OnApplicationPause(bool pauseStatus)
        {
            if (!m_IsLocalClient) return;

            LogInputLockDiagnostics(
                $"application pause callback paused={pauseStatus} " +
                BuildSkillInputDebugContext(resolvedState: m_LastAttackState, hasResolvedState: true));
        }

        private void OnDashStart()
        {
            m_LastDashStartTime = Time.time;
            m_LastDashStartFrame = Time.frameCount;
            if (!m_IsLocalClient) return;

            LogInputLockDiagnostics(
                "dash started during melee input context " +
                BuildSkillInputDebugContext(resolvedState: m_LastAttackState, hasResolvedState: true));
        }

        private void OnDashFinish()
        {
            m_LastDashFinishTime = Time.time;
            m_LastDashFinishFrame = Time.frameCount;
            if (!m_IsLocalClient) return;

            LogInputLockDiagnostics(
                "dash finished during melee input context " +
                BuildSkillInputDebugContext(resolvedState: m_LastAttackState, hasResolvedState: true));
        }
        
        private void Update()
        {
            if (!m_IsLocalClient && !m_IsServer) return;

            if (m_MeleeStance == null)
            {
                TryGetMeleeStance();
            }

            ObserveWindowInputContext();
            
            // Track attack state changes
            UpdateAttackState();
            RefreshLocalReactionReconciliationSuppression();
            UpdateWeaponState();
            FlushQueuedSkillInput();
            ProbePotentialSkillInputLock();
            ObserveGc2BlockState();
            
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

            LogMeleeSync($"initialized server={m_IsServer} local={m_IsLocalClient} remote={m_IsRemoteClient}");

            if (m_IsLocalClient)
            {
                PublishWeaponStateIfChanged(force: true);
            }
        }

        /// <summary>
        /// Registers the equipped GC2 melee weapon and combo skills for network hash playback.
        /// Transport integrations can call this after player spawn/equip without knowing GC2 assets.
        /// </summary>
        public void RegisterCurrentMeleeAssets()
        {
            if (m_MeleeStance == null)
            {
                TryGetMeleeStance();
            }

            RegisterWeaponAndSkills(GetCurrentMeleeWeapon());
        }

        private static void RegisterWeaponAndSkills(MeleeWeapon weapon)
        {
            if (weapon == null) return;

            NetworkMeleeManager.RegisterMeleeWeapon(weapon);
            RegisterComboSkills(weapon.Combo);
        }

        private static void RegisterComboSkills(ComboTree comboTree)
        {
            if (comboTree == null) return;

            var visited = new HashSet<int>();
            int[] rootIds = comboTree.RootIds;
            for (int i = 0; i < rootIds.Length; i++)
            {
                RegisterComboNode(comboTree, rootIds[i], visited);
            }
        }

        private static void RegisterComboNode(ComboTree comboTree, int nodeId, HashSet<int> visited)
        {
            if (nodeId == ComboTree.NODE_INVALID || !visited.Add(nodeId)) return;

            ComboItem comboItem = comboTree.Get(nodeId);
            if (comboItem?.Skill != null)
            {
                NetworkMeleeManager.RegisterSkill(comboItem.Skill);
            }

            List<int> children = comboTree.Children(nodeId);
            for (int i = 0; i < children.Count; i++)
            {
                RegisterComboNode(comboTree, children[i], visited);
            }
        }

        public void PublishCurrentWeaponState()
        {
            PublishWeaponStateIfChanged(force: true);
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
            SubscribeToRawMeleeInput();
        }
        
        private void UnsubscribeFromMeleeStance()
        {
            if (m_MeleeStance == null) return;
            
            m_MeleeStance.EventInputCharge -= OnInputCharge;
            m_MeleeStance.EventInputExecute -= OnInputExecute;
            UnsubscribeFromRawMeleeInput();
            
            m_MeleeStance = null;
        }

        private void SubscribeToRawMeleeInput()
        {
            GameCreator.Runtime.Melee.Input input = GetMeleeInputFromStance();
            if (input == null || ReferenceEquals(input, m_SubscribedRawMeleeInput)) return;

            UnsubscribeFromRawMeleeInput();
            m_SubscribedRawMeleeInput = input;
            m_SubscribedRawMeleeInput.EventInputCharge += OnRawInputCharge;
            m_SubscribedRawMeleeInput.EventInputExecute += OnRawInputExecute;
            LogSkillDiagnostics(
                "subscribed raw GC2 melee input " +
                BuildSkillInputDebugContext(resolvedState: m_LastAttackState, hasResolvedState: true));
        }

        private void UnsubscribeFromRawMeleeInput()
        {
            if (m_SubscribedRawMeleeInput == null) return;

            m_SubscribedRawMeleeInput.EventInputCharge -= OnRawInputCharge;
            m_SubscribedRawMeleeInput.EventInputExecute -= OnRawInputExecute;
            m_SubscribedRawMeleeInput = null;
            LogSkillDiagnostics("unsubscribed raw GC2 melee input");
        }

        private GameCreator.Runtime.Melee.Input GetMeleeInputFromStance()
        {
            try
            {
                var attacks = s_AttacksField?.GetValue(m_MeleeStance) as Attacks;
                return attacks?.Input;
            }
            catch (Exception e)
            {
                LogSkillDiagnosticsWarning($"failed to access raw GC2 melee input: {e.Message}");
                return null;
            }
        }

        private void OnWeaponEquipped(IWeapon weapon, GameObject instance)
        {
            if (weapon is not MeleeWeapon meleeWeapon)
            {
                PublishMeleeUnequipStateForNonMeleeEquip(weapon);
                return;
            }

            RegisterWeaponAndSkills(meleeWeapon);
            PublishWeaponStateIfChanged(force: true);
        }

        private void PublishMeleeUnequipStateForNonMeleeEquip(IWeapon weapon)
        {
            if (m_LastWeaponState.WeaponHash == 0 && GetCurrentMeleeWeapon() == null) return;

            m_LastWeaponState = NetworkMeleeWeaponState.None;
            if (!m_IsLocalClient) return;

            OnWeaponStateChanged?.Invoke(m_LastWeaponState);
            LogMeleeSync(
                $"non-melee weapon equipped ({weapon?.GetType().Name ?? "null"}). Clearing melee weapon state.");
        }

        private void OnWeaponUnequipped(IWeapon weapon, GameObject instance)
        {
            if (weapon is not MeleeWeapon) return;

            PublishWeaponStateIfChanged(force: true);
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // INPUT TRACKING
        // ════════════════════════════════════════════════════════════════════════════════════════

        private void OnRawInputCharge(MeleeKey key)
        {
            if (!m_IsLocalClient) return;

            LogInputLockDiagnostics(
                $"GC2 raw charge input queued key={key} " +
                BuildSkillInputDebugContext(resolvedState: m_LastAttackState, hasResolvedState: true));
        }

        private void OnRawInputExecute(MeleeKey key)
        {
            if (!m_IsLocalClient) return;

            m_LastRawExecuteTime = Time.time;
            m_LastRawExecuteFrame = Time.frameCount;
            m_LastRawExecuteKey = key;

            TryRecoverMissingCombatTargetForInput("raw execute input");

            LogInputLockDiagnostics(
                $"GC2 raw execute input queued key={key} " +
                BuildSkillInputDebugContext(resolvedState: m_LastAttackState, hasResolvedState: true));
        }
        
        private void OnInputCharge(MeleeKey key)
        {
            if (!m_IsLocalClient) return;
            
            // Get current weapon to determine charge skill
            var weapon = GetCurrentMeleeWeapon();
            if (weapon == null) return;
            RegisterWeaponAndSkills(weapon);
            
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
            if (!m_IsLocalClient)
            {
                LogInputLockDiagnostics($"ignored GC2 execute input key={key}: controller is not local client.");
                return;
            }

            TryRecoverMissingCombatTargetForInput("execute consume");

            bool replayedInput =
                m_ReplayedSkillInputPending &&
                m_ReplayedSkillInputKey == key;
            if (replayedInput)
            {
                m_ReplayedSkillInputPending = false;
            }
            else
            {
                m_QueuedSkillReplayCount = 0;
                m_QueuedSkillOriginalTime = Time.time;
                m_QueuedSkillOriginalFrame = Time.frameCount;
            }

            m_LastConsumedExecuteTime = Time.time;
            m_LastConsumedExecuteFrame = Time.frameCount;
            m_LastConsumedExecuteKey = key;

            // Check if this is a charge release
            bool isChargeRelease = m_ChargeState.IsCharging && m_ChargeState.InputKey == (byte)key;
            float chargeDuration = isChargeRelease ? (Time.time - m_ChargeState.ChargeStartTime) : 0f;

            // GC2 raises EventInputExecute while consuming the queued input, before
            // Attacks.ToSkill has populated ComboSkill. Defer one controller update so
            // the request carries the actual resolved SkillHash/ComboNodeId.
            m_HasQueuedSkillInput = true;
            m_QueuedSkillKey = key;
            m_QueuedSkillIsChargeRelease = isChargeRelease;
            m_QueuedSkillChargeDuration = chargeDuration;
            m_QueuedSkillTime = Time.time;
            m_QueuedSkillFrame = Time.frameCount;
            m_LoggedQueuedSkillWaitForWeapon = false;
            m_LoggedQueuedSkillWaitForSkill = false;
            m_QueuedSkillRetryQueued = false;
            m_LoggedQueuedSkillWaitForDash = false;
            m_LoggedQueuedSkillWaitForReplayConsume = false;
            if (m_QueuedSkillOriginalTime <= 0f)
            {
                m_QueuedSkillOriginalTime = Time.time;
                m_QueuedSkillOriginalFrame = Time.frameCount;
            }
            
            // Clear charge state if this was a charge release
            if (isChargeRelease)
            {
                m_ChargeState = NetworkChargeState.None;
            }
            
            if (m_LogHits)
            {
                Debug.Log($"[NetworkMeleeController] Skill request sent: Key={key}, ChargeRelease={isChargeRelease}");
            }

            LogMeleeSync(
                $"queued skill input key={key} chargeRelease={isChargeRelease} " +
                $"phase={m_MeleeStance?.CurrentPhase.ToString() ?? "NoStance"} busy={m_Character?.Busy.IsBusy}");
            LogInputLockDiagnostics(
                $"GC2 execute input queued key={key} chargeRelease={isChargeRelease} chargeDuration={chargeDuration:F3} " +
                $"replayed={replayedInput} replayCount={m_QueuedSkillReplayCount} " +
                $"phase={m_MeleeStance?.CurrentPhase.ToString() ?? "NoStance"} busy={m_Character?.Busy.IsBusy} " +
                $"lastSkill={m_LastAttackState.SkillHash} lastWeapon={m_LastAttackState.WeaponHash} " +
                $"lastCombo={m_LastAttackState.ComboNodeId} pending={m_PendingSkillRequests.Count} " +
                BuildSkillInputDebugContext(resolvedState: m_LastAttackState, hasResolvedState: true));
        }
        
        /// <summary>
        /// Get the currently equipped melee weapon.
        /// </summary>
        private MeleeWeapon GetCurrentMeleeWeapon(int requiredWeaponHash = 0)
        {
            MeleeWeapon weapon = null;

            if (s_AttacksField != null && m_MeleeStance != null)
            {
                try
                {
                    var attacks = s_AttacksField.GetValue(m_MeleeStance) as Attacks;
                    weapon = attacks?.Weapon;
                }
                catch
                {
                    weapon = null;
                }
            }

            if (weapon != null && (requiredWeaponHash == 0 || weapon.Id.Hash == requiredWeaponHash))
            {
                return weapon;
            }

            if (m_Character?.Combat?.Weapons == null) return requiredWeaponHash == 0 ? weapon : null;

            foreach (Weapon equippedWeapon in m_Character.Combat.Weapons)
            {
                if (equippedWeapon.Asset is not MeleeWeapon meleeWeapon) continue;
                if (requiredWeaponHash != 0 && meleeWeapon.Id.Hash != requiredWeaponHash) continue;
                return meleeWeapon;
            }

            return requiredWeaponHash == 0 ? weapon : null;
        }

        private void UpdateWeaponState()
        {
            if (!m_IsLocalClient) return;

            PublishWeaponStateIfChanged(force: false);
        }

        private void PublishWeaponStateIfChanged(bool force)
        {
            if (!m_IsLocalClient) return;

            NetworkMeleeWeaponState state = BuildWeaponState();
            if (!force &&
                state.WeaponHash == m_LastWeaponState.WeaponHash &&
                state.ShieldFlags == m_LastWeaponState.ShieldFlags &&
                state.BlockTiming == m_LastWeaponState.BlockTiming)
            {
                return;
            }

            m_LastWeaponState = state;
            OnWeaponStateChanged?.Invoke(state);
            LogMeleeSync($"weapon state changed weaponHash={state.WeaponHash} flags=0x{state.ShieldFlags:X2}");
        }

        private NetworkMeleeWeaponState BuildWeaponState()
        {
            MeleeWeapon weapon = GetCurrentMeleeWeapon();
            return new NetworkMeleeWeaponState
            {
                WeaponHash = weapon != null ? weapon.Id.Hash : 0,
                ShieldFlags = m_IsBlockingLocally ? NetworkMeleeWeaponState.SHIELD_RAISED : (byte)0,
                BlockTiming = 0
            };
        }

        public async void ApplyRemoteWeaponState(NetworkMeleeWeaponState state, MeleeWeapon weapon)
        {
            if (m_IsLocalClient) return;
            if (m_Character == null) return;

            if (state.WeaponHash == 0)
            {
                MeleeWeapon current = GetCurrentMeleeWeapon();
                if (current != null && m_Character.Combat.IsEquipped(current))
                {
                    await m_Character.Combat.Unequip(current, new Args(m_Character.gameObject));
                }

                m_LastWeaponState = state;
                return;
            }

            if (weapon == null) return;

            RegisterWeaponAndSkills(weapon);
            await UnequipNonMeleeWeapons();
            if (!m_Character.Combat.IsEquipped(weapon))
            {
                await m_Character.Combat.Equip(weapon, null, new Args(m_Character.gameObject));
            }

            m_LastWeaponState = state;
        }

        private async System.Threading.Tasks.Task UnequipNonMeleeWeapons()
        {
            if (m_Character?.Combat?.Weapons == null) return;

            Weapon[] equippedWeapons = m_Character.Combat.Weapons;
            for (int i = 0; i < equippedWeapons.Length; i++)
            {
                IWeapon asset = equippedWeapons[i]?.Asset;
                if (asset == null || asset is MeleeWeapon) continue;
                if (!m_Character.Combat.IsEquipped(asset)) continue;

                await m_Character.Combat.Unequip(asset, new Args(m_Character.gameObject));
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
                MeleePhase previousPhase = m_LastPhase;
                m_LastPhase = currentPhase;
                if (previousPhase != MeleePhase.None && currentPhase == MeleePhase.None)
                {
                    m_LastAttackPhaseExitTime = Time.time;
                    m_LastAttackPhaseExitFrame = Time.frameCount;
                }
                
                // Build new attack state
                m_LastAttackState = NetworkAttackState.FromPhase(currentPhase);
                
                // Get skill/weapon info if in active phase
                if (currentPhase != MeleePhase.None)
                {
                    TryGetCurrentSkillInfo(ref m_LastAttackState);
                }

                LogMeleeSync(
                    $"phase changed {previousPhase}->{currentPhase} skillHash={m_LastAttackState.SkillHash} " +
                    $"weaponHash={m_LastAttackState.WeaponHash} combo={m_LastAttackState.ComboNodeId}");
                LogSkillDiagnostics(
                    $"phase changed {previousPhase}->{currentPhase} skillHash={m_LastAttackState.SkillHash} " +
                    $"weaponHash={m_LastAttackState.WeaponHash} combo={m_LastAttackState.ComboNodeId} " +
                    $"queued={m_HasQueuedSkillInput} pending={m_PendingSkillRequests.Count} busy={m_Character?.Busy.IsBusy}");

                if (currentPhase == MeleePhase.Reaction)
                {
                    SuppressLocalOwnerReconciliation(OwnerReactionInitialReconciliationSuppression, true);
                    Vector3 reactionDirection = Vector3.zero;
                    float reactionPower = 0f;
                    IReaction reactionAsset = null;
                    GameObject fromObject = m_MeleeStance.Args?.Target;
                    var attacks = s_AttacksField?.GetValue(m_MeleeStance) as Attacks;
                    if (attacks != null)
                    {
                        reactionDirection = attacks.ReactionInput.Direction;
                        reactionPower = attacks.ReactionInput.Power;
                        reactionAsset = attacks.ReactionAsset;
                    }

                    LogReactionDiagnostics(
                        $"reaction phase entered previous={previousPhase} durationSuppress={OwnerReactionInitialReconciliationSuppression:F2}s " +
                        $"direction={FormatVector(reactionDirection)} power={reactionPower:F3} " +
                        $"candidate={BuildReactionCandidateDebug(fromObject, new ReactionInput(reactionDirection, reactionPower), reactionAsset)} " +
                        $"position={FormatVector(transform.position)} rootMotion={(m_Character != null ? m_Character.RootMotionPosition : 0f):F3} " +
                        $"gravityInfluence={(m_Character != null ? m_Character.Driver.GravityInfluence : 1f):F3}");
                    StartReactionMotionProbe("phase", reactionDirection);
                }
                else if (previousPhase == MeleePhase.Reaction)
                {
                    SuppressLocalOwnerReconciliation(OwnerReactionExitReconciliationSuppression, true);
                    LogReactionDiagnostics(
                        $"reaction phase exited current={currentPhase} grace={OwnerReactionExitReconciliationSuppression:F2}s " +
                        $"position={FormatVector(transform.position)} rootMotion={(m_Character != null ? m_Character.RootMotionPosition : 0f):F3} " +
                        $"gravityInfluence={(m_Character != null ? m_Character.Driver.GravityInfluence : 1f):F3}");
                }
                
                OnAttackStateChanged?.Invoke(m_LastAttackState);
                
                // Clear processed hits when entering new strike phase
                if (currentPhase == MeleePhase.Strike)
                {
                    m_ProcessedHits.Clear();
                }

                BroadcastServerReactionIfNeeded(currentPhase);
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

        private void BroadcastServerReactionIfNeeded(MeleePhase currentPhase)
        {
            if (!m_IsServer || currentPhase != MeleePhase.Reaction) return;
            if (NetworkMeleeManager.Instance == null)
            {
                LogMeleeSyncWarning("cannot broadcast reaction: no NetworkMeleeManager instance");
                return;
            }

            uint fromNetworkId = 0;
            Vector3 direction = -transform.forward;
            float power = 1f;
            IReaction reaction = null;
            GameObject fromObject = null;

            try
            {
                var attacks = s_AttacksField?.GetValue(m_MeleeStance) as Attacks;
                if (attacks != null)
                {
                    direction = attacks.ReactionInput.Direction;
                    power = attacks.ReactionInput.Power;
                    reaction = attacks.ReactionAsset;
                }

                fromObject = m_MeleeStance.Args?.Target;
                var fromNetworkCharacter = fromObject != null ? fromObject.GetComponent<NetworkCharacter>() : null;
                if (fromNetworkCharacter != null)
                {
                    fromNetworkId = fromNetworkCharacter.NetworkId;
                }
            }
            catch (Exception e)
            {
                LogMeleeSyncWarning($"failed to build reaction broadcast: {e.Message}");
            }

            LogReactionDiagnostics(
                $"server observed reaction phase target={NetworkId} from={fromNetworkId} " +
                $"fromObject={(fromObject != null ? fromObject.name : "null")} direction={FormatVector(direction)} " +
                $"power={power:F3} candidate={BuildReactionCandidateDebug(fromObject, new ReactionInput(direction, power), reaction)} " +
                $"position={FormatVector(transform.position)} rootMotion={(m_Character != null ? m_Character.RootMotionPosition : 0f):F3} " +
                $"gravityInfluence={(m_Character != null ? m_Character.Driver.GravityInfluence : 1f):F3}");
            LogMeleeSync(
                $"broadcasting reaction target={NetworkId} from={fromNetworkId} " +
                $"direction={direction} power={power:F3} reaction={(reaction != null ? reaction.GetType().Name : "null")}");
            NetworkMeleeManager.Instance.BroadcastReaction(
                CreateReactionBroadcast(NetworkId, fromNetworkId, direction, power, reaction));
        }

        private bool TryRecoverQueuedSkillInputAfterDashOrCancel(
            MeleeWeapon weapon,
            NetworkAttackState unresolvedState)
        {
            if (m_MeleeStance == null || weapon == null) return false;
            if (Time.time - m_QueuedSkillOriginalTime > QueuedSkillResolutionTimeout) return false;
            if (m_QueuedSkillReplayCount >= MaxQueuedSkillInputReplays) return false;
            if (!IsQueuedSkillInDashCancelRecoveryWindow()) return false;

            if (m_QueuedSkillRetryQueued)
            {
                if (!m_LoggedQueuedSkillWaitForReplayConsume)
                {
                    m_LoggedQueuedSkillWaitForReplayConsume = true;
                    LogInputLockDiagnostics(
                        $"queued skill input key={m_QueuedSkillKey} waiting for replay consume. " +
                        $"replayCount={m_QueuedSkillReplayCount} weapon={weapon.name} combo={unresolvedState.ComboNodeId} " +
                        BuildSkillInputDebugContext(weapon, unresolvedState, true));
                }

                return true;
            }

            bool dashOrLegsBusy =
                m_Character != null &&
                (m_Character.Dash.IsDashing || m_Character.Busy.AreLegsBusy);
            if (dashOrLegsBusy)
            {
                if (!m_LoggedQueuedSkillWaitForDash)
                {
                    m_LoggedQueuedSkillWaitForDash = true;
                    LogInputLockWarning(
                        $"queued skill input key={m_QueuedSkillKey} consumed during dash/cancel transition; " +
                        $"holding for replay. replayCount={m_QueuedSkillReplayCount} " +
                        $"weapon={weapon.name} combo={unresolvedState.ComboNodeId} " +
                        BuildSkillInputDebugContext(weapon, unresolvedState, true));
                }

                return true;
            }

            if (Time.frameCount <= m_QueuedSkillFrame) return true;

            m_QueuedSkillReplayCount++;
            m_QueuedSkillRetryQueued = true;
            m_ReplayedSkillInputPending = true;
            m_ReplayedSkillInputKey = m_QueuedSkillKey;
            m_QueuedSkillTime = Time.time;
            m_QueuedSkillFrame = Time.frameCount;
            m_LoggedQueuedSkillWaitForSkill = false;
            m_LoggedQueuedSkillWaitForDash = false;
            m_LoggedQueuedSkillWaitForReplayConsume = false;

            LogInputLockWarning(
                $"replaying queued skill input key={m_QueuedSkillKey} after dash/cancel transition resolved no ComboSkill. " +
                $"replayCount={m_QueuedSkillReplayCount}/{MaxQueuedSkillInputReplays} " +
                $"weapon={weapon.name} combo={unresolvedState.ComboNodeId} " +
                BuildSkillInputDebugContext(weapon, unresolvedState, true));

            m_MeleeStance.InputExecute(m_QueuedSkillKey);
            return true;
        }

        private bool IsQueuedSkillInDashCancelRecoveryWindow()
        {
            if (m_Character == null) return false;

            bool dashActive = m_Character.Dash.IsDashing || m_Character.Busy.AreLegsBusy;
            bool inputDuringDash =
                m_QueuedSkillOriginalTime >= m_LastDashStartTime &&
                m_QueuedSkillOriginalFrame >= m_LastDashStartFrame &&
                (m_LastDashFinishFrame < m_LastDashStartFrame ||
                 m_QueuedSkillOriginalTime <= m_LastDashFinishTime + QueuedSkillDashRecoveryWindow);
            bool inputAfterRecentDash =
                Time.time - m_LastDashFinishTime <= QueuedSkillDashRecoveryWindow;
            bool inputAfterRecentAttackExit =
                m_QueuedSkillOriginalFrame >= m_LastAttackPhaseExitFrame &&
                Time.time - m_LastAttackPhaseExitTime <= QueuedSkillDashRecoveryWindow;

            return dashActive || inputDuringDash || inputAfterRecentDash || inputAfterRecentAttackExit;
        }

        private void ClearQueuedSkillInputState()
        {
            m_HasQueuedSkillInput = false;
            m_QueuedSkillRetryQueued = false;
            m_ReplayedSkillInputPending = false;
            m_QueuedSkillReplayCount = 0;
            m_QueuedSkillOriginalTime = 0f;
            m_QueuedSkillOriginalFrame = 0;
            m_LoggedQueuedSkillWaitForDash = false;
            m_LoggedQueuedSkillWaitForSkill = false;
            m_LoggedQueuedSkillWaitForWeapon = false;
            m_LoggedQueuedSkillWaitForReplayConsume = false;
        }

        private void FlushQueuedSkillInput()
        {
            if (!m_HasQueuedSkillInput) return;
            if (!m_IsLocalClient) return;

            if (Time.time - m_QueuedSkillOriginalTime > QueuedSkillResolutionTimeout)
            {
                LogInputLockWarning(
                    $"dropped queued skill input key={m_QueuedSkillKey}: timed out after {Time.time - m_QueuedSkillOriginalTime:F3}s " +
                    $"phase={m_MeleeStance?.CurrentPhase.ToString() ?? "NoStance"} busy={m_Character?.Busy.IsBusy} " +
                    $"lastSkill={m_LastAttackState.SkillHash} lastWeapon={m_LastAttackState.WeaponHash} " +
                    $"combo={m_LastAttackState.ComboNodeId} " +
                    BuildSkillInputDebugContext(resolvedState: m_LastAttackState, hasResolvedState: true));
                LogMeleeSyncWarning(
                    $"dropped queued skill input key={m_QueuedSkillKey}: timed out waiting for GC2 skill resolution. " +
                    $"phase={m_MeleeStance?.CurrentPhase.ToString() ?? "NoStance"} lastSkill={m_LastAttackState.SkillHash} " +
                    $"lastWeapon={m_LastAttackState.WeaponHash} combo={m_LastAttackState.ComboNodeId}");
                ClearQueuedSkillInputState();
                return;
            }

            var weapon = GetCurrentMeleeWeapon();
            if (weapon == null)
            {
                if (!m_LoggedQueuedSkillWaitForWeapon)
                {
                    m_LoggedQueuedSkillWaitForWeapon = true;
                    LogInputLockWarning(
                        $"queued skill input key={m_QueuedSkillKey} waiting for equipped melee weapon. " +
                        $"phase={m_MeleeStance?.CurrentPhase.ToString() ?? "NoStance"} busy={m_Character?.Busy.IsBusy} " +
                        BuildSkillInputDebugContext(resolvedState: m_LastAttackState, hasResolvedState: true));
                    LogMeleeSyncWarning(
                        $"queued skill input key={m_QueuedSkillKey} is waiting for equipped melee weapon. " +
                        $"phase={m_MeleeStance?.CurrentPhase.ToString() ?? "NoStance"}");
                }

                return;
            }
            RegisterWeaponAndSkills(weapon);

            NetworkAttackState attackState = m_LastAttackState;
            TryGetCurrentSkillInfo(ref attackState);

            if (attackState.SkillHash == 0)
            {
                bool gc2ConsumedInputWithoutSkill =
                    Time.frameCount > m_QueuedSkillFrame &&
                    m_MeleeStance != null &&
                    m_MeleeStance.CurrentPhase == MeleePhase.None &&
                    m_Character != null &&
                    !m_Character.Busy.IsBusy;

                if (gc2ConsumedInputWithoutSkill)
                {
                    if (TryRecoverQueuedSkillInputAfterDashOrCancel(weapon, attackState))
                    {
                        return;
                    }

                    LogInputLockDiagnostics(
                        $"discarded queued skill input key={m_QueuedSkillKey}: GC2 consumed execute input but resolved no ComboSkill. " +
                        $"weapon={weapon.name} weaponHash={weapon.Id.Hash} combo={attackState.ComboNodeId} " +
                        BuildSkillInputDebugContext(weapon, attackState, true));
                    ClearQueuedSkillInputState();
                    return;
                }

                if (!m_LoggedQueuedSkillWaitForSkill)
                {
                    m_LoggedQueuedSkillWaitForSkill = true;
                    LogInputLockWarning(
                        $"queued skill input key={m_QueuedSkillKey} waiting for GC2 ComboSkill. " +
                        $"weapon={weapon.name} weaponHash={weapon.Id.Hash} " +
                        $"phase={m_MeleeStance?.CurrentPhase.ToString() ?? "NoStance"} busy={m_Character?.Busy.IsBusy} " +
                        $"stateSkill={attackState.SkillHash} stateWeapon={attackState.WeaponHash} combo={attackState.ComboNodeId} " +
                        BuildSkillInputDebugContext(weapon, attackState, true));
                    LogMeleeSyncWarning(
                        $"queued skill input key={m_QueuedSkillKey} is waiting for GC2 ComboSkill. " +
                        $"weapon={weapon.name} weaponHash={weapon.Id.Hash} phase={m_MeleeStance?.CurrentPhase.ToString() ?? "NoStance"} " +
                        $"combo={attackState.ComboNodeId}");
                }

                return;
            }

            if (attackState.WeaponHash == 0)
            {
                attackState.WeaponHash = weapon.Id.Hash;
            }

            uint targetNetworkId = 0;
            var target = m_Character.Combat.Targets.Primary;
            if (target != null)
            {
                var targetNetChar = target.GetComponent<NetworkCharacter>();
                if (targetNetChar != null) targetNetworkId = targetNetChar.NetworkId;
            }

            ushort requestId = GetNextRequestId();
            var request = new NetworkSkillRequest
            {
                RequestId = requestId,
                ActorNetworkId = NetworkId,
                CorrelationId = NetworkCorrelation.Compose(NetworkId, requestId),
                TargetNetworkId = targetNetworkId,
                SkillHash = attackState.SkillHash,
                WeaponHash = attackState.WeaponHash,
                ComboNodeId = attackState.ComboNodeId,
                InputKey = (byte)m_QueuedSkillKey,
                IsChargeRelease = m_QueuedSkillIsChargeRelease,
                ChargeDuration = m_QueuedSkillChargeDuration
            };

            ulong pendingKey = GetPendingKey(request.ActorNetworkId, request.CorrelationId);
            if (pendingKey == 0)
            {
                Debug.LogWarning("[NetworkMeleeController] Ignoring skill request with invalid actor/correlation context.");
                ClearQueuedSkillInputState();
                return;
            }

            m_PendingSkillRequests[pendingKey] = new PendingSkillRequest
            {
                Request = request,
                SentTime = Time.time
            };

            m_LastSkillRequestSentTime = Time.time;
            m_LastSkillRequestSentId = request.RequestId;
            m_LastSkillRequestSentSkillHash = request.SkillHash;

            m_LastAttackState = attackState;
            ClearQueuedSkillInputState();
            LogMeleeSync(
                $"sending skill request req={request.RequestId} corr={request.CorrelationId} " +
                $"skillHash={request.SkillHash} weaponHash={request.WeaponHash} combo={request.ComboNodeId} " +
                $"target={request.TargetNetworkId} phase={m_MeleeStance?.CurrentPhase.ToString() ?? "NoStance"}");
            int subscriberCount = OnSkillRequested?.GetInvocationList().Length ?? 0;
            LogInputLockDiagnostics(
                $"sending skill request req={request.RequestId} corr={request.CorrelationId} subscribers={subscriberCount} " +
                $"skillHash={request.SkillHash} weaponHash={request.WeaponHash} combo={request.ComboNodeId} " +
                $"target={request.TargetNetworkId} phase={m_MeleeStance?.CurrentPhase.ToString() ?? "NoStance"} " +
                $"busy={m_Character?.Busy.IsBusy} pending={m_PendingSkillRequests.Count} " +
                BuildSkillInputDebugContext(weapon, attackState, true));
            OnSkillRequested?.Invoke(request);
        }

        private void ProbePotentialSkillInputLock()
        {
            if (!ShouldLogInputLockDiagnostics) return;
            if (!m_IsLocalClient) return;
            if (Time.time < m_NextInputLockProbeTime) return;

            bool rawRecentlySeen = Time.time - m_LastRawExecuteTime <= 3f;
            bool consumedRecentlySeen = Time.time - m_LastConsumedExecuteTime <= 3f;
            bool rawStalled =
                rawRecentlySeen &&
                Time.time - m_LastRawExecuteTime >= 0.15f &&
                m_LastConsumedExecuteTime < m_LastRawExecuteTime;
            bool consumeStalled =
                consumedRecentlySeen &&
                Time.time - m_LastConsumedExecuteTime >= 0.15f &&
                m_LastSkillRequestSentTime < m_LastConsumedExecuteTime &&
                !m_HasQueuedSkillInput;
            bool queuedStalled =
                m_HasQueuedSkillInput &&
                Time.time - m_QueuedSkillTime >= 0.15f;
            bool interesting =
                rawStalled ||
                consumeStalled ||
                queuedStalled ||
                m_PendingSkillRequests.Count > 0 ||
                m_ReplayedSkillInputPending;

            if (!interesting) return;

            m_NextInputLockProbeTime = Time.time + 0.50f;
            NetworkMeleeManager manager = NetworkMeleeManager.Instance;
            int skillSubscribers = OnSkillRequested?.GetInvocationList().Length ?? 0;
            bool rawSubscribed = m_SubscribedRawMeleeInput != null;
            bool dashActive = m_Character != null && m_Character.Dash.IsDashing;
            bool legsBusy = m_Character != null && m_Character.Busy.AreLegsBusy;

            LogInputLockDiagnostics(
                "input-lock probe " +
                $"rawStalled={rawStalled} consumeStalled={consumeStalled} queuedStalled={queuedStalled} " +
                $"rawAge={AgeLabel(m_LastRawExecuteTime)} rawFrame={m_LastRawExecuteFrame} rawKey={m_LastRawExecuteKey} " +
                $"consumeAge={AgeLabel(m_LastConsumedExecuteTime)} consumeFrame={m_LastConsumedExecuteFrame} consumeKey={m_LastConsumedExecuteKey} " +
                $"lastReqAge={AgeLabel(m_LastSkillRequestSentTime)} lastReq={m_LastSkillRequestSentId} lastReqSkill={m_LastSkillRequestSentSkillHash} " +
                $"skillSubscribers={skillSubscribers} rawSubscribed={rawSubscribed} " +
                $"manager={(manager != null)} managerServer={manager?.IsServer ?? false} managerClient={manager?.IsClient ?? false} " +
                $"dash={dashActive} legsBusy={legsBusy} dashStartAge={AgeLabel(m_LastDashStartTime)} dashFinishAge={AgeLabel(m_LastDashFinishTime)} " +
                BuildSkillInputDebugContext(resolvedState: m_LastAttackState, hasResolvedState: true));
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
