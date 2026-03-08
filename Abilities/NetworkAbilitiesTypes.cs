using System;
using UnityEngine;

namespace Arawn.GameCreator2.Networking
{
    /// <summary>
    /// Network data structures for DaimahouGames Abilities system.
    /// Server-authoritative ability casting, cooldowns, projectiles, and impacts.
    /// </summary>
    /// 
    // ════════════════════════════════════════════════════════════════════════════════════════
    // ABILITY CAST NETWORKING
    // ════════════════════════════════════════════════════════════════════════════════════════
    
    /// <summary>
    /// Request to cast an ability.
    /// Client → Server for validation.
    /// </summary>
    [Serializable]
    public struct NetworkAbilityCastRequest
    {
        /// <summary>Unique request ID for response matching.</summary>
        public ushort RequestId;
        public uint ActorNetworkId;
        public uint CorrelationId;
        
        /// <summary>Network ID of the caster character.</summary>
        public uint CasterNetworkId;
        
        /// <summary>Hash of the Ability's UniqueID (ability.ID.Hash).</summary>
        public int AbilityIdHash;
        
        /// <summary>Client timestamp for lag compensation.</summary>
        public float ClientTime;
        
        /// <summary>Target type (0=none, 1=position, 2=character, 3=object).</summary>
        public byte TargetType;
        
        /// <summary>Target position (for position targets).</summary>
        public Vector3 TargetPosition;
        
        /// <summary>Target network ID (for character/object targets).</summary>
        public uint TargetNetworkId;
        
        /// <summary>Whether this is an auto-confirm (AI/instruction-triggered) cast.</summary>
        public bool AutoConfirm;
        
        public const int SIZE_BYTES = 38; // 2 + 4 + 4 + 4 + 1 + 12 + 4 + 1 + padding
    }
    
    /// <summary>
    /// Server response to ability cast request.
    /// </summary>
    [Serializable]
    public struct NetworkAbilityCastResponse
    {
        /// <summary>Matches RequestId from request.</summary>
        public ushort RequestId;
        public uint ActorNetworkId;
        public uint CorrelationId;
        
        /// <summary>Server-assigned cast instance ID (for tracking this specific cast).</summary>
        public uint CastInstanceId;
        
        /// <summary>Whether the cast was approved.</summary>
        public bool Approved;
        
        /// <summary>Rejection reason if not approved.</summary>
        public AbilityCastRejectReason RejectReason;
        
        /// <summary>Server-computed cooldown end time.</summary>
        public float CooldownEndTime;
        
        public const int SIZE_BYTES = 12;
    }
    
    /// <summary>
    /// Broadcast ability cast start to all clients.
    /// Server → All Clients.
    /// </summary>
    [Serializable]
    public struct NetworkAbilityCastBroadcast
    {
        /// <summary>Network ID of the caster.</summary>
        public uint CasterNetworkId;
        
        /// <summary>Server-assigned cast instance ID.</summary>
        public uint CastInstanceId;
        
        /// <summary>Hash of the ability being cast.</summary>
        public int AbilityIdHash;
        
        /// <summary>Server timestamp when cast started.</summary>
        public float ServerTime;
        
        /// <summary>Target type.</summary>
        public byte TargetType;
        
        /// <summary>Target position.</summary>
        public Vector3 TargetPosition;
        
        /// <summary>Target network ID.</summary>
        public uint TargetNetworkId;
        
        /// <summary>Cast state (0=started, 1=trigger, 2=completed, 3=canceled).</summary>
        public AbilityCastState CastState;
        
        public const int SIZE_BYTES = 38;
    }
    
    /// <summary>
    /// Ability cast state changes.
    /// </summary>
    public enum AbilityCastState : byte
    {
        /// <summary>Cast has started (entering cast state).</summary>
        Started = 0,
        
        /// <summary>Cast trigger point (effects apply).</summary>
        Triggered = 1,
        
        /// <summary>Cast completed normally.</summary>
        Completed = 2,
        
        /// <summary>Cast was canceled.</summary>
        Canceled = 3
    }
    
    /// <summary>
    /// Reasons for cast rejection.
    /// </summary>
    public enum AbilityCastRejectReason : byte
    {
        None = 0,
        CasterNotFound = 1,
        AbilityNotKnown = 2,
        OnCooldown = 3,
        RequirementNotMet = 4,
        AlreadyCasting = 5,
        TargetInvalid = 6,
        OutOfRange = 7,
        NotAuthorized = 8,
        InternalError = 9,
        NotOwner = 10,
        ProtocolMismatch = 11,
        SecurityViolation = 12,
        Timeout = 13
    }
    
    // ════════════════════════════════════════════════════════════════════════════════════════
    // ABILITY EFFECT/TRIGGER NETWORKING
    // ════════════════════════════════════════════════════════════════════════════════════════
    
    /// <summary>
    /// Server broadcasts when an ability effect triggers on specific targets.
    /// This is for client-side VFX/SFX synchronization.
    /// </summary>
    [Serializable]
    public struct NetworkAbilityEffectBroadcast
    {
        /// <summary>Cast instance this effect belongs to.</summary>
        public uint CastInstanceId;
        
        /// <summary>Server timestamp.</summary>
        public float ServerTime;
        
        /// <summary>Effect type for client-side handling.</summary>
        public AbilityEffectType EffectType;
        
        /// <summary>Position where effect occurs.</summary>
        public Vector3 Position;
        
        /// <summary>Direction/rotation of effect.</summary>
        public Vector3 Direction;
        
        /// <summary>Number of targets hit (for multi-target effects).</summary>
        public byte TargetCount;
        
        /// <summary>Network IDs of targets (up to 8, additional sent in follow-up).</summary>
        public uint Target0;
        public uint Target1;
        public uint Target2;
        public uint Target3;
        public uint Target4;
        public uint Target5;
        public uint Target6;
        public uint Target7;
        
        public const int SIZE_BYTES = 66;
    }
    
    /// <summary>
    /// Types of ability effects.
    /// </summary>
    public enum AbilityEffectType : byte
    {
        Generic = 0,
        Projectile = 1,
        Impact = 2,
        Melee = 3,
        SFX = 4,
        Instruction = 5
    }
    
    // ════════════════════════════════════════════════════════════════════════════════════════
    // PROJECTILE NETWORKING
    // ════════════════════════════════════════════════════════════════════════════════════════
    
    /// <summary>
    /// Server spawns projectile and broadcasts to clients.
    /// </summary>
    [Serializable]
    public struct NetworkProjectileSpawnBroadcast
    {
        /// <summary>Unique projectile instance ID.</summary>
        public uint ProjectileId;
        
        /// <summary>Cast instance this projectile belongs to.</summary>
        public uint CastInstanceId;
        
        /// <summary>Hash of the Projectile ScriptableObject.</summary>
        public int ProjectileHash;
        
        /// <summary>Spawn position.</summary>
        public Vector3 SpawnPosition;
        
        /// <summary>Initial direction.</summary>
        public Vector3 Direction;
        
        /// <summary>Target position (for homing/range calculation).</summary>
        public Vector3 TargetPosition;
        
        /// <summary>Target network ID (for homing projectiles).</summary>
        public uint TargetNetworkId;
        
        /// <summary>Server spawn timestamp.</summary>
        public float ServerTime;
        
        public const int SIZE_BYTES = 56;
    }
    
    /// <summary>
    /// Projectile hit or destroyed event.
    /// </summary>
    [Serializable]
    public struct NetworkProjectileEventBroadcast
    {
        /// <summary>The projectile instance ID.</summary>
        public uint ProjectileId;
        
        /// <summary>Event type.</summary>
        public ProjectileEventType EventType;
        
        /// <summary>Position where event occurred.</summary>
        public Vector3 Position;
        
        /// <summary>Network ID of hit target (if any).</summary>
        public uint HitTargetNetworkId;
        
        /// <summary>Server timestamp.</summary>
        public float ServerTime;
        
        public const int SIZE_BYTES = 25;
    }
    
    /// <summary>
    /// Projectile event types.
    /// </summary>
    public enum ProjectileEventType : byte
    {
        Hit = 0,
        Destroyed = 1,
        Pierced = 2,
        Exploded = 3
    }
    
    // ════════════════════════════════════════════════════════════════════════════════════════
    // IMPACT NETWORKING
    // ════════════════════════════════════════════════════════════════════════════════════════
    
    /// <summary>
    /// Server spawns impact and broadcasts to clients.
    /// </summary>
    [Serializable]
    public struct NetworkImpactSpawnBroadcast
    {
        /// <summary>Unique impact instance ID.</summary>
        public uint ImpactId;
        
        /// <summary>Cast instance this impact belongs to.</summary>
        public uint CastInstanceId;
        
        /// <summary>Hash of the Impact ScriptableObject.</summary>
        public int ImpactHash;
        
        /// <summary>Spawn position.</summary>
        public Vector3 Position;
        
        /// <summary>Rotation.</summary>
        public Quaternion Rotation;
        
        /// <summary>Server spawn timestamp.</summary>
        public float ServerTime;
        
        public const int SIZE_BYTES = 44;
    }
    
    /// <summary>
    /// Impact hit notification (server tells clients who was hit).
    /// </summary>
    [Serializable]
    public struct NetworkImpactHitBroadcast
    {
        /// <summary>The impact instance ID.</summary>
        public uint ImpactId;
        
        /// <summary>Server timestamp.</summary>
        public float ServerTime;
        
        /// <summary>Number of targets hit.</summary>
        public byte TargetCount;
        
        /// <summary>Network IDs of hit targets (up to 16).</summary>
        public uint Target0;
        public uint Target1;
        public uint Target2;
        public uint Target3;
        public uint Target4;
        public uint Target5;
        public uint Target6;
        public uint Target7;
        public uint Target8;
        public uint Target9;
        public uint Target10;
        public uint Target11;
        public uint Target12;
        public uint Target13;
        public uint Target14;
        public uint Target15;
        
        public const int SIZE_BYTES = 73;
    }
    
    // ════════════════════════════════════════════════════════════════════════════════════════
    // COOLDOWN NETWORKING
    // ════════════════════════════════════════════════════════════════════════════════════════
    
    /// <summary>
    /// Request cooldown state for an ability.
    /// Used when client needs to verify cooldown state.
    /// </summary>
    [Serializable]
    public struct NetworkCooldownRequest
    {
        /// <summary>Request ID for matching.</summary>
        public ushort RequestId;
        public uint ActorNetworkId;
        public uint CorrelationId;
        
        /// <summary>Network ID of the caster.</summary>
        public uint CasterNetworkId;
        
        /// <summary>Hash of the ability.</summary>
        public int AbilityIdHash;
        
        public const int SIZE_BYTES = 10;
    }
    
    /// <summary>
    /// Server response with cooldown state.
    /// </summary>
    [Serializable]
    public struct NetworkCooldownResponse
    {
        /// <summary>Matches request ID.</summary>
        public ushort RequestId;
        public uint ActorNetworkId;
        public uint CorrelationId;
        
        /// <summary>Whether ability is on cooldown.</summary>
        public bool IsOnCooldown;
        
        /// <summary>Server time when cooldown ends.</summary>
        public float CooldownEndTime;
        
        /// <summary>Total cooldown duration.</summary>
        public float TotalDuration;

        /// <summary>True when no authoritative response was received before local timeout.</summary>
        public bool TimedOut;
        
        public const int SIZE_BYTES = 13;
    }
    
    /// <summary>
    /// Server broadcasts cooldown changes (when server modifies cooldowns).
    /// </summary>
    [Serializable]
    public struct NetworkCooldownBroadcast
    {
        /// <summary>Network ID of the character.</summary>
        public uint CharacterNetworkId;
        
        /// <summary>Hash of the ability.</summary>
        public int AbilityIdHash;
        
        /// <summary>Server time when cooldown ends (0 = cleared).</summary>
        public float CooldownEndTime;
        
        /// <summary>Total cooldown duration.</summary>
        public float TotalDuration;
        
        /// <summary>Reason for cooldown change.</summary>
        public CooldownChangeReason Reason;
        
        public const int SIZE_BYTES = 17;
    }
    
    /// <summary>
    /// Reasons for cooldown changes.
    /// </summary>
    public enum CooldownChangeReason : byte
    {
        AbilityCast = 0,
        ServerReset = 1,
        ServerReduced = 2,
        ServerExtended = 3,
        EffectApplied = 4
    }
    
    // ════════════════════════════════════════════════════════════════════════════════════════
    // ABILITY LEARNING NETWORKING
    // ════════════════════════════════════════════════════════════════════════════════════════
    
    /// <summary>
    /// Request to learn or unlearn an ability.
    /// Client → Server for validation.
    /// </summary>
    [Serializable]
    public struct NetworkAbilityLearnRequest
    {
        /// <summary>Request ID for matching.</summary>
        public ushort RequestId;
        public uint ActorNetworkId;
        public uint CorrelationId;
        
        /// <summary>Network ID of the character.</summary>
        public uint CharacterNetworkId;
        
        /// <summary>Hash of the ability to learn/unlearn.</summary>
        public int AbilityIdHash;
        
        /// <summary>Slot to learn into (-1 for unlearn).</summary>
        public sbyte Slot;
        
        /// <summary>True to learn, false to unlearn.</summary>
        public bool IsLearning;
        
        public const int SIZE_BYTES = 12;
    }
    
    /// <summary>
    /// Server response to learn request.
    /// </summary>
    [Serializable]
    public struct NetworkAbilityLearnResponse
    {
        /// <summary>Matches request ID.</summary>
        public ushort RequestId;
        public uint ActorNetworkId;
        public uint CorrelationId;
        
        /// <summary>Whether the operation was approved.</summary>
        public bool Approved;
        
        /// <summary>Rejection reason if not approved.</summary>
        public AbilityLearnRejectReason RejectReason;
        
        public const int SIZE_BYTES = 4;
    }
    
    /// <summary>
    /// Server broadcasts ability learned/unlearned.
    /// </summary>
    [Serializable]
    public struct NetworkAbilityLearnBroadcast
    {
        /// <summary>Network ID of the character.</summary>
        public uint CharacterNetworkId;
        
        /// <summary>Hash of the ability.</summary>
        public int AbilityIdHash;
        
        /// <summary>Slot index.</summary>
        public sbyte Slot;
        
        /// <summary>True if learned, false if unlearned.</summary>
        public bool IsLearned;
        
        public const int SIZE_BYTES = 10;
    }
    
    /// <summary>
    /// Reasons for learn/unlearn rejection.
    /// </summary>
    public enum AbilityLearnRejectReason : byte
    {
        None = 0,
        CharacterNotFound = 1,
        AbilityNotFound = 2,
        SlotInvalid = 3,
        SlotOccupied = 4,
        AbilityNotKnown = 5,
        NotAuthorized = 6,
        NotOwner = 7,
        ProtocolMismatch = 8,
        SecurityViolation = 9,
        Timeout = 10
    }
    
    // ════════════════════════════════════════════════════════════════════════════════════════
    // CANCEL CAST NETWORKING
    // ════════════════════════════════════════════════════════════════════════════════════════
    
    /// <summary>
    /// Request to cancel an ongoing cast.
    /// </summary>
    [Serializable]
    public struct NetworkCastCancelRequest
    {
        /// <summary>Request ID for matching.</summary>
        public ushort RequestId;
        public uint ActorNetworkId;
        public uint CorrelationId;
        
        /// <summary>Network ID of the caster.</summary>
        public uint CasterNetworkId;
        
        /// <summary>Cast instance to cancel (0 = current cast).</summary>
        public uint CastInstanceId;
        
        public const int SIZE_BYTES = 10;
    }
    
    /// <summary>
    /// Server response to cancel request.
    /// </summary>
    [Serializable]
    public struct NetworkCastCancelResponse
    {
        /// <summary>Matches request ID.</summary>
        public ushort RequestId;
        public uint ActorNetworkId;
        public uint CorrelationId;
        
        /// <summary>Whether cancel was approved.</summary>
        public bool Approved;
        
        /// <summary>The cast instance that was canceled.</summary>
        public uint CastInstanceId;

        /// <summary>True when no authoritative response was received before local timeout.</summary>
        public bool TimedOut;
        
        public const int SIZE_BYTES = 8;
    }
    
    // ════════════════════════════════════════════════════════════════════════════════════════
    // FULL STATE SYNC (for late joiners or reconnection)
    // ════════════════════════════════════════════════════════════════════════════════════════
    
    /// <summary>
    /// Request full ability state for a character.
    /// </summary>
    [Serializable]
    public struct NetworkAbilityStateRequest
    {
        /// <summary>Request ID.</summary>
        public ushort RequestId;
        public uint ActorNetworkId;
        public uint CorrelationId;
        
        /// <summary>Network ID of the character to get state for.</summary>
        public uint CharacterNetworkId;
        
        public const int SIZE_BYTES = 6;
    }
    
    /// <summary>
    /// Full ability state for a character (header).
    /// Followed by slot entries.
    /// </summary>
    [Serializable]
    public struct NetworkAbilityStateResponse
    {
        /// <summary>Matches request ID.</summary>
        public ushort RequestId;
        public uint ActorNetworkId;
        public uint CorrelationId;
        
        /// <summary>Network ID of the character.</summary>
        public uint CharacterNetworkId;
        
        /// <summary>Number of ability slots.</summary>
        public byte SlotCount;
        
        /// <summary>Number of active cooldowns.</summary>
        public byte CooldownCount;
        
        /// <summary>Whether currently casting.</summary>
        public bool IsCasting;
        
        /// <summary>Current cast instance ID (if casting).</summary>
        public uint CurrentCastId;
        
        /// <summary>Current cast ability hash (if casting).</summary>
        public int CurrentCastAbilityHash;
        
        public const int SIZE_BYTES = 17;
    }
    
    /// <summary>
    /// Ability slot entry for state sync.
    /// </summary>
    [Serializable]
    public struct NetworkAbilitySlotEntry
    {
        /// <summary>Slot index.</summary>
        public byte SlotIndex;
        
        /// <summary>Ability hash in this slot (0 = empty).</summary>
        public int AbilityHash;
        
        public const int SIZE_BYTES = 5;
    }
    
    /// <summary>
    /// Cooldown entry for state sync.
    /// </summary>
    [Serializable]
    public struct NetworkCooldownEntry
    {
        /// <summary>Ability hash.</summary>
        public int AbilityHash;
        
        /// <summary>Server time when cooldown ends.</summary>
        public float EndTime;
        
        /// <summary>Total duration.</summary>
        public float TotalDuration;
        
        public const int SIZE_BYTES = 12;
    }
    
    // ════════════════════════════════════════════════════════════════════════════════════════
    // STATISTICS AND DEBUGGING
    // ════════════════════════════════════════════════════════════════════════════════════════
    
    /// <summary>
    /// Network statistics for abilities.
    /// </summary>
    [Serializable]
    public struct NetworkAbilitiesStats
    {
        // Cast statistics
        public int TotalCastRequests;
        public int ApprovedCasts;
        public int RejectedCasts;
        
        // Rejection breakdown
        public int RejectedOnCooldown;
        public int RejectedRequirements;
        public int RejectedAlreadyCasting;
        public int RejectedTargetInvalid;
        public int RejectedOutOfRange;
        
        // Projectile statistics
        public int ProjectilesSpawned;
        public int ProjectileHits;
        
        // Impact statistics
        public int ImpactsSpawned;
        public int ImpactTargetsHit;
        
        // Learning statistics
        public int AbilitiesLearned;
        public int AbilitiesUnlearned;
        
        // Cooldown statistics
        public int CooldownsSet;
        public int CooldownsCleared;
    }
    
    /// <summary>
    /// Runtime state snapshot for debugging.
    /// </summary>
    [Serializable]
    public struct NetworkAbilitiesState
    {
        /// <summary>Number of active casters being tracked.</summary>
        public int ActiveCasters;
        
        /// <summary>Number of ongoing casts.</summary>
        public int OngoingCasts;
        
        /// <summary>Number of active projectiles.</summary>
        public int ActiveProjectiles;
        
        /// <summary>Number of active impacts.</summary>
        public int ActiveImpacts;
        
        /// <summary>Number of tracked cooldowns.</summary>
        public int TrackedCooldowns;
        
        /// <summary>Number of pending client requests.</summary>
        public int PendingRequests;
    }
}
