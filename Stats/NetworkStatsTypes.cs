#if GC2_STATS
using System;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Stats
{
    /// <summary>
    /// Network-optimized data types for GC2 Stats synchronization.
    /// Designed for server-authoritative stat management in competitive multiplayer.
    /// </summary>
    /// 
    // ════════════════════════════════════════════════════════════════════════════════════════════
    // STAT MODIFICATION REQUEST
    // ════════════════════════════════════════════════════════════════════════════════════════════
    
    /// <summary>
    /// Request to modify a stat value. Sent from client to server for validation.
    /// Server authorizes all stat changes to prevent cheating. (~20 bytes)
    /// </summary>
    [Serializable]
    public struct NetworkStatModifyRequest
    {
        /// <summary>Unique request ID for response matching.</summary>
        public ushort RequestId;
        public uint ActorNetworkId;
        public uint CorrelationId;
        
        /// <summary>Network ID of the target character.</summary>
        public uint TargetNetworkId;
        
        /// <summary>Hash of the stat ID being modified.</summary>
        public int StatHash;
        
        /// <summary>Type of modification.</summary>
        public StatModificationType ModificationType;
        
        /// <summary>Value for the modification.</summary>
        public float Value;
        
        /// <summary>Source of the modification (for validation).</summary>
        public StatModificationSource Source;
        
        /// <summary>Hash of the source item/skill (if applicable).</summary>
        public int SourceHash;
    }
    
    /// <summary>
    /// Server response to stat modification request. (~6 bytes)
    /// </summary>
    [Serializable]
    public struct NetworkStatModifyResponse
    {
        /// <summary>The request ID this is responding to.</summary>
        public ushort RequestId;
        public uint ActorNetworkId;
        public uint CorrelationId;
        
        /// <summary>Whether the modification was authorized.</summary>
        public bool Authorized;
        
        /// <summary>Rejection reason if not authorized.</summary>
        public StatRejectionReason RejectionReason;
        
        /// <summary>Server-validated new base value.</summary>
        public float NewValue;
    }
    
    // ════════════════════════════════════════════════════════════════════════════════════════════
    // ATTRIBUTE MODIFICATION REQUEST
    // ════════════════════════════════════════════════════════════════════════════════════════════
    
    /// <summary>
    /// Request to modify an attribute value (health, mana, stamina, etc.). (~20 bytes)
    /// Critical for preventing cheating - all attribute changes must be server-authorized.
    /// </summary>
    [Serializable]
    public struct NetworkAttributeModifyRequest
    {
        /// <summary>Unique request ID for response matching.</summary>
        public ushort RequestId;
        public uint ActorNetworkId;
        public uint CorrelationId;
        
        /// <summary>Network ID of the target character.</summary>
        public uint TargetNetworkId;
        
        /// <summary>Hash of the attribute ID being modified.</summary>
        public int AttributeHash;
        
        /// <summary>Type of modification.</summary>
        public AttributeModificationType ModificationType;
        
        /// <summary>Value for the modification (absolute or delta).</summary>
        public float Value;
        
        /// <summary>Source of the modification.</summary>
        public StatModificationSource Source;
        
        /// <summary>Hash of the source item/skill/effect.</summary>
        public int SourceHash;
    }
    
    /// <summary>
    /// Server response to attribute modification request. (~10 bytes)
    /// </summary>
    [Serializable]
    public struct NetworkAttributeModifyResponse
    {
        /// <summary>The request ID this is responding to.</summary>
        public ushort RequestId;
        public uint ActorNetworkId;
        public uint CorrelationId;
        
        /// <summary>Whether the modification was authorized.</summary>
        public bool Authorized;
        
        /// <summary>Rejection reason if not authorized.</summary>
        public StatRejectionReason RejectionReason;
        
        /// <summary>Server-validated new value.</summary>
        public float NewValue;
        
        /// <summary>Server-validated max value (may have changed).</summary>
        public float MaxValue;
    }
    
    // ════════════════════════════════════════════════════════════════════════════════════════════
    // STATUS EFFECT REQUEST
    // ════════════════════════════════════════════════════════════════════════════════════════════
    
    /// <summary>
    /// Request to add or remove a status effect. (~16 bytes)
    /// </summary>
    [Serializable]
    public struct NetworkStatusEffectRequest
    {
        /// <summary>Unique request ID for response matching.</summary>
        public ushort RequestId;
        public uint ActorNetworkId;
        public uint CorrelationId;
        
        /// <summary>Network ID of the target character.</summary>
        public uint TargetNetworkId;
        
        /// <summary>Hash of the status effect ID.</summary>
        public int StatusEffectHash;
        
        /// <summary>Action type (add/remove).</summary>
        public StatusEffectAction Action;
        
        /// <summary>Amount to add/remove (for stacking).</summary>
        public byte Amount;
        
        /// <summary>Source of the status effect.</summary>
        public StatModificationSource Source;
        
        /// <summary>Hash of the source.</summary>
        public int SourceHash;
    }
    
    /// <summary>
    /// Server response to status effect request. (~8 bytes)
    /// </summary>
    [Serializable]
    public struct NetworkStatusEffectResponse
    {
        /// <summary>The request ID this is responding to.</summary>
        public ushort RequestId;
        public uint ActorNetworkId;
        public uint CorrelationId;
        
        /// <summary>Whether the action was authorized.</summary>
        public bool Authorized;
        
        /// <summary>Rejection reason if not authorized.</summary>
        public StatRejectionReason RejectionReason;
        
        /// <summary>Current stack count after action.</summary>
        public byte CurrentStackCount;
    }
    
    // ════════════════════════════════════════════════════════════════════════════════════════════
    // STAT MODIFIER REQUEST
    // ════════════════════════════════════════════════════════════════════════════════════════════
    
    /// <summary>
    /// Request to add or remove a stat modifier. (~20 bytes)
    /// </summary>
    [Serializable]
    public struct NetworkStatModifierRequest
    {
        /// <summary>Unique request ID for response matching.</summary>
        public ushort RequestId;
        public uint ActorNetworkId;
        public uint CorrelationId;
        
        /// <summary>Network ID of the target character.</summary>
        public uint TargetNetworkId;
        
        /// <summary>Hash of the stat ID.</summary>
        public int StatHash;
        
        /// <summary>Action type (add/remove).</summary>
        public ModifierAction Action;
        
        /// <summary>Modifier type (constant or percent).</summary>
        public NetworkModifierType ModifierType;
        
        /// <summary>Modifier value.</summary>
        public float Value;
        
        /// <summary>Source of the modifier.</summary>
        public StatModificationSource Source;
        
        /// <summary>Hash of the source.</summary>
        public int SourceHash;
    }
    
    /// <summary>
    /// Server response to stat modifier request. (~8 bytes)
    /// </summary>
    [Serializable]
    public struct NetworkStatModifierResponse
    {
        /// <summary>The request ID this is responding to.</summary>
        public ushort RequestId;
        public uint ActorNetworkId;
        public uint CorrelationId;
        
        /// <summary>Whether the action was authorized.</summary>
        public bool Authorized;
        
        /// <summary>Rejection reason if not authorized.</summary>
        public StatRejectionReason RejectionReason;
        
        /// <summary>New stat value after modifier applied.</summary>
        public float NewStatValue;
    }
    
    // ════════════════════════════════════════════════════════════════════════════════════════════
    // BROADCAST TYPES
    // ════════════════════════════════════════════════════════════════════════════════════════════
    
    /// <summary>
    /// Broadcast when a stat changes. Sent to all clients. (~16 bytes)
    /// </summary>
    [Serializable]
    public struct NetworkStatChangeBroadcast
    {
        /// <summary>Network ID of the character.</summary>
        public uint NetworkId;
        
        /// <summary>Hash of the stat ID.</summary>
        public int StatHash;
        
        /// <summary>New base value.</summary>
        public float NewBaseValue;
        
        /// <summary>New computed value (with modifiers).</summary>
        public float NewComputedValue;
    }
    
    /// <summary>
    /// Broadcast when an attribute changes. Sent to all clients. (~20 bytes)
    /// </summary>
    [Serializable]
    public struct NetworkAttributeChangeBroadcast
    {
        /// <summary>Network ID of the character.</summary>
        public uint NetworkId;
        
        /// <summary>Hash of the attribute ID.</summary>
        public int AttributeHash;
        
        /// <summary>New current value.</summary>
        public float NewValue;
        
        /// <summary>New max value (may change due to stat changes).</summary>
        public float MaxValue;
        
        /// <summary>Change delta (for UI feedback).</summary>
        public float Change;
    }
    
    /// <summary>
    /// Broadcast when a status effect is added/removed. (~12 bytes)
    /// </summary>
    [Serializable]
    public struct NetworkStatusEffectBroadcast
    {
        /// <summary>Network ID of the character.</summary>
        public uint NetworkId;
        
        /// <summary>Hash of the status effect ID.</summary>
        public int StatusEffectHash;
        
        /// <summary>Action that occurred.</summary>
        public StatusEffectAction Action;
        
        /// <summary>Current stack count.</summary>
        public byte StackCount;
        
        /// <summary>Remaining duration (if applicable).</summary>
        public float RemainingDuration;
    }
    
    /// <summary>
    /// Broadcast when a stat modifier is added/removed. (~16 bytes)
    /// </summary>
    [Serializable]
    public struct NetworkStatModifierBroadcast
    {
        /// <summary>Network ID of the character.</summary>
        public uint NetworkId;
        
        /// <summary>Hash of the stat ID.</summary>
        public int StatHash;
        
        /// <summary>Action that occurred.</summary>
        public ModifierAction Action;
        
        /// <summary>Modifier type (constant or percent).</summary>
        public NetworkModifierType ModifierType;
        
        /// <summary>Modifier value.</summary>
        public float Value;
        
        /// <summary>New computed stat value after change.</summary>
        public float NewStatValue;
    }
    
    // ════════════════════════════════════════════════════════════════════════════════════════════
    // CLEAR STATUS EFFECTS REQUEST
    // ════════════════════════════════════════════════════════════════════════════════════════════
    
    /// <summary>
    /// Request to clear status effects by type mask. (~12 bytes)
    /// </summary>
    [Serializable]
    public struct NetworkClearStatusEffectsRequest
    {
        /// <summary>Unique request ID for response matching.</summary>
        public ushort RequestId;
        public uint ActorNetworkId;
        public uint CorrelationId;
        
        /// <summary>Network ID of the target character.</summary>
        public uint TargetNetworkId;
        
        /// <summary>Type mask for status effects to clear (mirrors StatusEffectTypeMask).</summary>
        public byte TypeMask;
        
        /// <summary>Source of the request.</summary>
        public StatModificationSource Source;
        
        /// <summary>Hash of the source.</summary>
        public int SourceHash;
    }
    
    /// <summary>
    /// Server response to clear status effects request. (~4 bytes)
    /// </summary>
    [Serializable]
    public struct NetworkClearStatusEffectsResponse
    {
        /// <summary>The request ID this is responding to.</summary>
        public ushort RequestId;
        public uint ActorNetworkId;
        public uint CorrelationId;
        
        /// <summary>Whether the action was authorized.</summary>
        public bool Authorized;
        
        /// <summary>Rejection reason if not authorized.</summary>
        public StatRejectionReason RejectionReason;
    }
    
    // ════════════════════════════════════════════════════════════════════════════════════════════
    // FULL STATE SYNC
    // ════════════════════════════════════════════════════════════════════════════════════════════
    
    /// <summary>
    /// Full stats snapshot for initial sync or reconnection.
    /// Variable size based on number of stats/attributes.
    /// </summary>
    [Serializable]
    public struct NetworkStatsSnapshot
    {
        /// <summary>Network ID of the character.</summary>
        public uint NetworkId;
        
        /// <summary>Server timestamp when snapshot was taken.</summary>
        public float Timestamp;
        
        /// <summary>All stat values.</summary>
        public NetworkStatValue[] Stats;
        
        /// <summary>All attribute values.</summary>
        public NetworkAttributeValue[] Attributes;
        
        /// <summary>All active status effects.</summary>
        public NetworkStatusEffectValue[] StatusEffects;
    }
    
    /// <summary>
    /// Single stat value for snapshot. (~12 bytes)
    /// </summary>
    [Serializable]
    public struct NetworkStatValue
    {
        public int StatHash;
        public float BaseValue;
        public float ComputedValue;
    }
    
    /// <summary>
    /// Single attribute value for snapshot. (~12 bytes)
    /// </summary>
    [Serializable]
    public struct NetworkAttributeValue
    {
        public int AttributeHash;
        public float CurrentValue;
        public float MaxValue;
    }
    
    /// <summary>
    /// Single status effect value for snapshot. (~12 bytes)
    /// </summary>
    [Serializable]
    public struct NetworkStatusEffectValue
    {
        public int StatusEffectHash;
        public byte StackCount;
        public float RemainingDuration;
    }
    
    // ════════════════════════════════════════════════════════════════════════════════════════════
    // DELTA SYNC (BANDWIDTH OPTIMIZED)
    // ════════════════════════════════════════════════════════════════════════════════════════════
    
    /// <summary>
    /// Delta update for stats that changed since last sync.
    /// Sent at regular intervals for efficient bandwidth usage.
    /// </summary>
    [Serializable]
    public struct NetworkStatsDelta
    {
        /// <summary>Network ID of the character.</summary>
        public uint NetworkId;
        
        /// <summary>Server timestamp.</summary>
        public float Timestamp;
        
        /// <summary>Bitmask of which stats changed (up to 32 stats).</summary>
        public uint StatChangeMask;
        
        /// <summary>Bitmask of which attributes changed (up to 32 attributes).</summary>
        public uint AttributeChangeMask;
        
        /// <summary>Only changed stat values.</summary>
        public NetworkStatValue[] ChangedStats;
        
        /// <summary>Only changed attribute values.</summary>
        public NetworkAttributeValue[] ChangedAttributes;
    }
    
    // ════════════════════════════════════════════════════════════════════════════════════════════
    // ENUMS
    // ════════════════════════════════════════════════════════════════════════════════════════════
    
    /// <summary>
    /// Type of stat modification.
    /// </summary>
    public enum StatModificationType : byte
    {
        /// <summary>Set base value directly.</summary>
        SetBase = 0,
        
        /// <summary>Add to base value.</summary>
        AddToBase = 1,
        
        /// <summary>Multiply base value.</summary>
        MultiplyBase = 2
    }
    
    /// <summary>
    /// Type of attribute modification.
    /// </summary>
    public enum AttributeModificationType : byte
    {
        /// <summary>Set value directly.</summary>
        Set = 0,
        
        /// <summary>Add delta to current value.</summary>
        Add = 1,
        
        /// <summary>Set to percentage of max (0-1).</summary>
        SetPercent = 2,
        
        /// <summary>Add percentage of max.</summary>
        AddPercent = 3
    }
    
    /// <summary>
    /// Source of a stat/attribute modification.
    /// </summary>
    public enum StatModificationSource : byte
    {
        /// <summary>Direct modification (console, debug).</summary>
        Direct = 0,
        
        /// <summary>From combat damage.</summary>
        Combat = 1,
        
        /// <summary>From skill/ability.</summary>
        Skill = 2,
        
        /// <summary>From item usage.</summary>
        Item = 3,
        
        /// <summary>From status effect.</summary>
        StatusEffect = 4,
        
        /// <summary>From regeneration/degeneration.</summary>
        Regeneration = 5,
        
        /// <summary>From environmental effect.</summary>
        Environment = 6,
        
        /// <summary>Server-initiated (respawn, level up, etc.).</summary>
        Server = 7
    }
    
    /// <summary>
    /// Status effect action type.
    /// </summary>
    public enum StatusEffectAction : byte
    {
        Add = 0,
        Remove = 1,
        RemoveAll = 2,
        Refresh = 3
    }
    
    /// <summary>
    /// Stat modifier action type.
    /// </summary>
    public enum ModifierAction : byte
    {
        Add = 0,
        Remove = 1,
        Clear = 2
    }
    
    /// <summary>
    /// Network-friendly modifier type (mirrors GC2's ModifierType).
    /// </summary>
    public enum NetworkModifierType : byte
    {
        Constant = 0,
        Percent = 1
    }
    
    /// <summary>
    /// Reasons for rejecting a stat modification request.
    /// </summary>
    public enum StatRejectionReason : byte
    {
        None = 0,
        
        // Target issues
        TargetNotFound = 1,
        TargetNotOwned = 2,
        TargetDead = 3,
        
        // Stat/Attribute issues
        StatNotFound = 10,
        AttributeNotFound = 11,
        StatusEffectNotFound = 12,
        
        // Value issues
        ValueOutOfRange = 20,
        ValueTooHigh = 21,
        ValueTooLow = 22,
        AlreadyAtMax = 23,
        AlreadyAtMin = 24,
        
        // Modifier issues
        ModifierNotFound = 30,
        MaxStackReached = 31,
        
        // Permission issues
        NotAuthorized = 40,
        InvalidSource = 41,
        NotOwner = 42,
        ProtocolMismatch = 43,
        SecurityViolation = 44,
        
        // Rate limiting
        RateLimitExceeded = 50,
        
        // Anti-cheat
        CheatSuspected = 100,
        InvalidRequest = 101
    }
}
#endif
