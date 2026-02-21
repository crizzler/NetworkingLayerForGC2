using System;
using UnityEngine;

#if GC2_MELEE
using GameCreator.Runtime.Melee;
#endif

namespace Arawn.GameCreator2.Networking.Melee
{
    /// <summary>
    /// Network-optimized data types for GC2 Melee synchronization.
    /// </summary>
    /// 
    // ════════════════════════════════════════════════════════════════════════════════════════════
    // NETWORK MELEE HIT REQUEST
    // ════════════════════════════════════════════════════════════════════════════════════════════
    
    /// <summary>
    /// Compact hit request sent from client to server. (~30 bytes)
    /// </summary>
    [Serializable]
    public struct NetworkMeleeHitRequest
    {
        /// <summary>Unique request ID for response matching.</summary>
        public ushort RequestId;
        
        /// <summary>Client timestamp when hit was detected.</summary>
        public float ClientTimestamp;
        
        /// <summary>Network ID of the attacker.</summary>
        public uint AttackerNetworkId;
        
        /// <summary>Network ID of the target.</summary>
        public uint TargetNetworkId;
        
        /// <summary>Hit point (compressed world position).</summary>
        public Vector3 HitPoint;
        
        /// <summary>Strike direction (compressed normal).</summary>
        public Vector3 StrikeDirection;
        
        /// <summary>Hash of the skill being used.</summary>
        public int SkillHash;
        
        /// <summary>Hash of the weapon being used.</summary>
        public int WeaponHash;
        
        /// <summary>Combo node ID (for combo validation).</summary>
        public int ComboNodeId;
        
        /// <summary>Current phase of the attack.</summary>
        public byte AttackPhase;
    }
    
    // ════════════════════════════════════════════════════════════════════════════════════════════
    // NETWORK MELEE HIT RESPONSE
    // ════════════════════════════════════════════════════════════════════════════════════════════
    
    /// <summary>
    /// Server response to a hit request. (~8 bytes)
    /// </summary>
    [Serializable]
    public struct NetworkMeleeHitResponse
    {
        /// <summary>The request ID this is responding to.</summary>
        public ushort RequestId;
        
        /// <summary>Whether the hit was validated.</summary>
        public bool Validated;
        
        /// <summary>Rejection reason if not validated.</summary>
        public MeleeHitRejectionReason RejectionReason;
        
        /// <summary>Server-calculated damage (if validated).</summary>
        public float Damage;
        
        /// <summary>Whether poise was broken.</summary>
        public bool PoiseBroken;
    }
    
    /// <summary>
    /// Reasons a melee hit can be rejected.
    /// </summary>
    public enum MeleeHitRejectionReason : byte
    {
        None = 0,
        TargetNotFound = 1,
        AttackerNotFound = 2,
        OutOfRange = 3,
        InvalidPhase = 4,
        TargetInvincible = 5,
        TargetDodged = 6,
        SkillMismatch = 7,
        WeaponMismatch = 8,
        AlreadyHit = 9,
        TimestampTooOld = 10,
        CheatSuspected = 11,
    }
    
    // ════════════════════════════════════════════════════════════════════════════════════════════
    // NETWORK MELEE HIT BROADCAST
    // ════════════════════════════════════════════════════════════════════════════════════════════
    
    /// <summary>
    /// Broadcast to all clients when a hit is confirmed. (~24 bytes)
    /// </summary>
    [Serializable]
    public struct NetworkMeleeHitBroadcast
    {
        /// <summary>Network ID of the attacker.</summary>
        public uint AttackerNetworkId;
        
        /// <summary>Network ID of the target.</summary>
        public uint TargetNetworkId;
        
        /// <summary>Hit point for effects.</summary>
        public Vector3 HitPoint;
        
        /// <summary>Strike direction for effects.</summary>
        public Vector3 StrikeDirection;
        
        /// <summary>Skill hash for looking up effects.</summary>
        public int SkillHash;
        
        /// <summary>Block type result.</summary>
        public byte BlockResult;
        
        /// <summary>Whether poise was broken.</summary>
        public bool PoiseBroken;
    }
    
    // ════════════════════════════════════════════════════════════════════════════════════════════
    // NETWORK ATTACK STATE
    // ════════════════════════════════════════════════════════════════════════════════════════════
    
    /// <summary>
    /// Compact attack state for synchronization. (~12 bytes)
    /// </summary>
    [Serializable]
    public struct NetworkAttackState
    {
        /// <summary>Hash of the current skill.</summary>
        public int SkillHash;
        
        /// <summary>Hash of the equipped weapon.</summary>
        public int WeaponHash;
        
        /// <summary>Current combo node ID.</summary>
        public short ComboNodeId;
        
        /// <summary>Current attack phase.</summary>
        public byte Phase;
        
        /// <summary>Normalized time within current phase (0-255 maps to 0-1).</summary>
        public byte PhaseProgress;
        
        public static NetworkAttackState None => new NetworkAttackState
        {
            SkillHash = 0,
            WeaponHash = 0,
            ComboNodeId = -1,
            Phase = 0,
            PhaseProgress = 0
        };
        
#if GC2_MELEE
        public static NetworkAttackState FromPhase(MeleePhase phase)
        {
            return new NetworkAttackState
            {
                Phase = (byte)phase,
            };
        }
        
        public MeleePhase GetMeleePhase() => (MeleePhase)Phase;
#endif
    }
    
    // ════════════════════════════════════════════════════════════════════════════════════════════
    // NETWORK SKILL REQUEST
    // ════════════════════════════════════════════════════════════════════════════════════════════
    
    /// <summary>
    /// Request to play a skill (sent by clients to server).
    /// </summary>
    [Serializable]
    public struct NetworkSkillRequest
    {
        /// <summary>Network ID of the target (0 if none).</summary>
        public uint TargetNetworkId;
        
        /// <summary>Hash of the skill to play.</summary>
        public int SkillHash;
        
        /// <summary>Hash of the weapon to use.</summary>
        public int WeaponHash;
        
        /// <summary>Input key used.</summary>
        public byte InputKey;
        
        /// <summary>Whether this is a charge release.</summary>
        public bool IsChargeRelease;
        
        /// <summary>Charge duration (if charge release).</summary>
        public float ChargeDuration;
    }
    
    // ════════════════════════════════════════════════════════════════════════════════════════════
    // BLOCK STATE
    // ════════════════════════════════════════════════════════════════════════════════════════════
    
    /// <summary>
    /// Network block result types.
    /// </summary>
    public enum NetworkBlockResult : byte
    {
        None = 0,
        Blocked = 1,
        Parried = 2,
        BlockBroken = 3
    }
    
    // ════════════════════════════════════════════════════════════════════════════════════════════
    // WEAPON STATE
    // ════════════════════════════════════════════════════════════════════════════════════════════
    
    /// <summary>
    /// Compact melee weapon state. (~8 bytes)
    /// </summary>
    [Serializable]
    public struct NetworkMeleeWeaponState
    {
        /// <summary>Hash of the equipped melee weapon (0 if none).</summary>
        public int WeaponHash;
        
        /// <summary>Current shield state flags.</summary>
        public byte ShieldFlags;
        
        /// <summary>Block/parry timing (0-255 normalized).</summary>
        public byte BlockTiming;
        
        public const byte SHIELD_RAISED = 0x01;
        public const byte SHIELD_PARRY_WINDOW = 0x02;
        public const byte SHIELD_BROKEN = 0x04;
        
        public bool IsShieldRaised => (ShieldFlags & SHIELD_RAISED) != 0;
        public bool InParryWindow => (ShieldFlags & SHIELD_PARRY_WINDOW) != 0;
        public bool IsShieldBroken => (ShieldFlags & SHIELD_BROKEN) != 0;
    }
    
    // ════════════════════════════════════════════════════════════════════════════════════════════
    // REACTION STATE
    // ════════════════════════════════════════════════════════════════════════════════════════════
    
    /// <summary>
    /// Network reaction broadcast. (~16 bytes)
    /// </summary>
    [Serializable]
    public struct NetworkReactionBroadcast
    {
        /// <summary>Network ID of the character reacting.</summary>
        public uint CharacterNetworkId;
        
        /// <summary>Network ID of the character who caused the reaction (attacker).</summary>
        public uint FromNetworkId;
        
        /// <summary>Hash of the reaction to play.</summary>
        public int ReactionHash;
        
        /// <summary>Reaction direction (compressed: 0-255 maps to -180 to 180 degrees).</summary>
        public byte Direction;
        
        /// <summary>Reaction power (compressed: 0-255 maps to 0-10 power).</summary>
        public byte Power;
        
        /// <summary>Convert direction angle to compressed byte.</summary>
        public static byte CompressDirection(Vector3 direction)
        {
            if (direction.sqrMagnitude < 0.001f) return 128; // Forward
            float angle = Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg;
            return (byte)Mathf.RoundToInt((angle + 180f) / 360f * 255f);
        }
        
        /// <summary>Decompress direction byte to normalized vector.</summary>
        public Vector3 GetDirection()
        {
            float angle = (Direction / 255f * 360f - 180f) * Mathf.Deg2Rad;
            return new Vector3(Mathf.Sin(angle), 0f, Mathf.Cos(angle));
        }
        
        /// <summary>Compress power (0-10 range) to byte.</summary>
        public static byte CompressPower(float power)
        {
            return (byte)Mathf.Clamp(Mathf.RoundToInt(power / 10f * 255f), 0, 255);
        }
        
        /// <summary>Decompress power byte to float.</summary>
        public float GetPower() => Power / 255f * 10f;
    }
    
    // ════════════════════════════════════════════════════════════════════════════════════════════
    // BLOCK/SHIELD REQUEST
    // ════════════════════════════════════════════════════════════════════════════════════════════
    
    /// <summary>
    /// Block action types for request.
    /// </summary>
    public enum NetworkBlockAction : byte
    {
        Raise = 0,
        Lower = 1
    }
    
    /// <summary>
    /// Client request to raise/lower block. (~8 bytes)
    /// </summary>
    [Serializable]
    public struct NetworkBlockRequest
    {
        /// <summary>Unique request ID for response matching.</summary>
        public ushort RequestId;
        
        /// <summary>Client timestamp when block was requested.</summary>
        public float ClientTimestamp;
        
        /// <summary>Action: raise or lower guard.</summary>
        public NetworkBlockAction Action;
        
        /// <summary>Hash of the shield being used (from weapon).</summary>
        public int ShieldHash;
    }
    
    /// <summary>
    /// Reasons a block request can be rejected.
    /// </summary>
    public enum BlockRejectionReason : byte
    {
        None = 0,
        CharacterBusy = 1,
        NoShieldEquipped = 2,
        OnCooldown = 3,
        InvalidState = 4,
        CheatSuspected = 5
    }
    
    /// <summary>
    /// Server response to block request. (~4 bytes)
    /// </summary>
    [Serializable]
    public struct NetworkBlockResponse
    {
        /// <summary>The request ID this is responding to.</summary>
        public ushort RequestId;
        
        /// <summary>Whether the block was validated.</summary>
        public bool Validated;
        
        /// <summary>Rejection reason if not validated.</summary>
        public BlockRejectionReason RejectionReason;
        
        /// <summary>Server-authoritative block start time (for parry window sync).</summary>
        public float ServerBlockStartTime;
    }
    
    /// <summary>
    /// Broadcast block state change to all clients. (~12 bytes)
    /// </summary>
    [Serializable]
    public struct NetworkBlockBroadcast
    {
        /// <summary>Network ID of the character.</summary>
        public uint CharacterNetworkId;
        
        /// <summary>Action that occurred.</summary>
        public NetworkBlockAction Action;
        
        /// <summary>Server timestamp of the action (for parry window calculation).</summary>
        public float ServerTimestamp;
        
        /// <summary>Shield hash for animation lookup.</summary>
        public int ShieldHash;
    }
    
    // ════════════════════════════════════════════════════════════════════════════════════════════
    // SKILL EXECUTION REQUEST/RESPONSE
    // ════════════════════════════════════════════════════════════════════════════════════════════
    
    /// <summary>
    /// Reasons a skill request can be rejected.
    /// </summary>
    public enum SkillRejectionReason : byte
    {
        None = 0,
        CharacterBusy = 1,
        InvalidPhase = 2,
        WeaponNotEquipped = 3,
        SkillNotAvailable = 4,
        OnCooldown = 5,
        InsufficientResources = 6,
        InvalidComboTransition = 7,
        ChargeNotValid = 8,
        CheatSuspected = 9
    }
    
    /// <summary>
    /// Server response to skill request. (~6 bytes)
    /// </summary>
    [Serializable]
    public struct NetworkSkillResponse
    {
        /// <summary>Request ID this responds to (from InputKey used as ID).</summary>
        public ushort RequestId;
        
        /// <summary>Whether the skill was validated.</summary>
        public bool Validated;
        
        /// <summary>Rejection reason if not validated.</summary>
        public SkillRejectionReason RejectionReason;
        
        /// <summary>Server-assigned combo node ID.</summary>
        public short ComboNodeId;
    }
    
    /// <summary>
    /// Broadcast skill execution to all clients. (~20 bytes)
    /// </summary>
    [Serializable]
    public struct NetworkSkillBroadcast
    {
        /// <summary>Network ID of the character executing skill.</summary>
        public uint CharacterNetworkId;
        
        /// <summary>Network ID of the target (0 if none).</summary>
        public uint TargetNetworkId;
        
        /// <summary>Hash of the skill being executed.</summary>
        public int SkillHash;
        
        /// <summary>Hash of the weapon being used.</summary>
        public int WeaponHash;
        
        /// <summary>Combo node ID for combo tracking.</summary>
        public short ComboNodeId;
        
        /// <summary>Server timestamp when skill started.</summary>
        public float ServerTimestamp;
        
        /// <summary>Whether this was a charged attack.</summary>
        public bool IsCharged;
        
        /// <summary>Charge level (0-255 normalized, 0 = uncharged).</summary>
        public byte ChargeLevel;
    }
    
    // ════════════════════════════════════════════════════════════════════════════════════════════
    // CHARGE STATE
    // ════════════════════════════════════════════════════════════════════════════════════════════
    
    /// <summary>
    /// Compact charge state for tracking charge attacks. (~12 bytes)
    /// </summary>
    [Serializable]
    public struct NetworkChargeState
    {
        /// <summary>Whether currently charging.</summary>
        public bool IsCharging;
        
        /// <summary>Input key being held.</summary>
        public byte InputKey;
        
        /// <summary>Hash of skill being charged.</summary>
        public int ChargeSkillHash;
        
        /// <summary>Server timestamp when charge started.</summary>
        public float ChargeStartTime;
        
        /// <summary>Combo node ID for the charge.</summary>
        public short ChargeComboNodeId;
        
        public static NetworkChargeState None => new NetworkChargeState
        {
            IsCharging = false,
            InputKey = 0,
            ChargeSkillHash = 0,
            ChargeStartTime = 0f,
            ChargeComboNodeId = -1
        };
    }
    
    /// <summary>
    /// Request to start charging (sent by clients). (~10 bytes)
    /// </summary>
    [Serializable]
    public struct NetworkChargeRequest
    {
        /// <summary>Unique request ID.</summary>
        public ushort RequestId;
        
        /// <summary>Client timestamp when charge started.</summary>
        public float ClientTimestamp;
        
        /// <summary>Input key being held.</summary>
        public byte InputKey;
        
        /// <summary>Hash of weapon being used.</summary>
        public int WeaponHash;
    }
    
    /// <summary>
    /// Server response to charge request. (~6 bytes)
    /// </summary>
    [Serializable]
    public struct NetworkChargeResponse
    {
        /// <summary>Request ID this responds to.</summary>
        public ushort RequestId;
        
        /// <summary>Whether charge was validated.</summary>
        public bool Validated;
        
        /// <summary>Server timestamp when charge started (for timing sync).</summary>
        public float ServerChargeStartTime;
        
        /// <summary>Hash of skill that will be charged.</summary>
        public int ChargeSkillHash;
    }
    
    /// <summary>
    /// Broadcast charge state change. (~12 bytes)
    /// </summary>
    [Serializable]
    public struct NetworkChargeBroadcast
    {
        /// <summary>Network ID of the character.</summary>
        public uint CharacterNetworkId;
        
        /// <summary>Whether charge started or ended.</summary>
        public bool ChargeStarted;
        
        /// <summary>Hash of skill being charged.</summary>
        public int ChargeSkillHash;
        
        /// <summary>Server timestamp.</summary>
        public float ServerTimestamp;
    }
    
    // ════════════════════════════════════════════════════════════════════════════════════════════
    // BLOCK OUTCOME (for hit processing)
    // ════════════════════════════════════════════════════════════════════════════════════════════
    
    /// <summary>
    /// Result of server-side block evaluation during hit processing.
    /// </summary>
    [Serializable]
    public struct BlockEvaluationResult
    {
        /// <summary>The block outcome.</summary>
        public NetworkBlockResult Result;
        
        /// <summary>Remaining defense after block (if blocked).</summary>
        public float RemainingDefense;
        
        /// <summary>Whether the defender should play a reaction.</summary>
        public bool TriggerReaction;
        
        /// <summary>Hash of reaction to play (0 if none).</summary>
        public int ReactionHash;
        
        public static BlockEvaluationResult NoBlock => new BlockEvaluationResult
        {
            Result = NetworkBlockResult.None,
            RemainingDefense = 0f,
            TriggerReaction = true,
            ReactionHash = 0
        };
        
        public static BlockEvaluationResult Blocked(float remainingDefense) => new BlockEvaluationResult
        {
            Result = NetworkBlockResult.Blocked,
            RemainingDefense = remainingDefense,
            TriggerReaction = false,
            ReactionHash = 0
        };
        
        public static BlockEvaluationResult Parried => new BlockEvaluationResult
        {
            Result = NetworkBlockResult.Parried,
            RemainingDefense = 0f,
            TriggerReaction = false,
            ReactionHash = 0
        };
        
        public static BlockEvaluationResult BlockBroken => new BlockEvaluationResult
        {
            Result = NetworkBlockResult.BlockBroken,
            RemainingDefense = 0f,
            TriggerReaction = true,
            ReactionHash = 0
        };
    }
}
