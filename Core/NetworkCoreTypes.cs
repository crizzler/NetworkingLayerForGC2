using System;
using UnityEngine;

namespace Arawn.GameCreator2.Networking
{
    /// <summary>
    /// Network data structures for GC2 Core character features:
    /// Ragdoll, Props, Invincibility, Poise, Busy, and Interaction.
    /// </summary>
    /// 
    // ════════════════════════════════════════════════════════════════════════════════════════
    // RAGDOLL NETWORKING
    // ════════════════════════════════════════════════════════════════════════════════════════
    
    /// <summary>
    /// Request to start ragdoll on a character.
    /// Client → Server for validation.
    /// </summary>
    [Serializable]
    public struct NetworkRagdollRequest
    {
        /// <summary>Unique request ID for response matching.</summary>
        public ushort RequestId;
        public uint ActorNetworkId;
        public uint CorrelationId;
        
        /// <summary>Network ID of the character to ragdoll.</summary>
        public uint CharacterNetworkId;
        
        /// <summary>Client timestamp for lag compensation.</summary>
        public float ClientTime;
        
        /// <summary>Type of ragdoll action.</summary>
        public RagdollActionType ActionType;
        
        /// <summary>Optional force to apply (for knockback ragdolls).</summary>
        public Vector3 Force;
        
        /// <summary>Optional force application point.</summary>
        public Vector3 ForcePoint;
        
        public const int SIZE_BYTES = 34; // 2 + 4 + 4 + 1 + 12 + 12 - padding
    }
    
    /// <summary>
    /// Server response to ragdoll request.
    /// </summary>
    [Serializable]
    public struct NetworkRagdollResponse
    {
        /// <summary>Matches RequestId from request.</summary>
        public ushort RequestId;
        public uint ActorNetworkId;
        public uint CorrelationId;
        
        /// <summary>Whether the ragdoll was approved.</summary>
        public bool Approved;
        
        /// <summary>Rejection reason if not approved.</summary>
        public RagdollRejectReason RejectReason;
        
        public const int SIZE_BYTES = 4;
    }
    
    /// <summary>
    /// Broadcast ragdoll state to all clients.
    /// Server → All Clients.
    /// </summary>
    [Serializable]
    public struct NetworkRagdollBroadcast
    {
        /// <summary>Network ID of the ragdolled character.</summary>
        public uint CharacterNetworkId;
        
        /// <summary>Type of ragdoll action.</summary>
        public RagdollActionType ActionType;
        
        /// <summary>Server timestamp for synchronization.</summary>
        public float ServerTime;
        
        /// <summary>Force applied to ragdoll (if any).</summary>
        public Vector3 Force;
        
        /// <summary>Force application point (if any).</summary>
        public Vector3 ForcePoint;
        
        public const int SIZE_BYTES = 33;
    }
    
    /// <summary>
    /// Types of ragdoll actions.
    /// </summary>
    public enum RagdollActionType : byte
    {
        /// <summary>Start ragdoll physics.</summary>
        StartRagdoll = 0,
        
        /// <summary>Start recovery from ragdoll.</summary>
        StartRecover = 1,
        
        /// <summary>Forced ragdoll with knockback force.</summary>
        StartRagdollWithForce = 2,
        
        /// <summary>Instant recovery (teleport stand up).</summary>
        InstantRecover = 3
    }
    
    /// <summary>
    /// Reasons for ragdoll rejection.
    /// </summary>
    public enum RagdollRejectReason : byte
    {
        None = 0,
        AlreadyRagdoll = 1,
        NotRagdoll = 2,
        CharacterNotFound = 3,
        NotAuthorized = 4,
        Cooldown = 5,
        NotOwner = 6,
        ProtocolMismatch = 7,
        SecurityViolation = 8,
        Timeout = 9
    }
    
    // ════════════════════════════════════════════════════════════════════════════════════════
    // PROPS NETWORKING
    // ════════════════════════════════════════════════════════════════════════════════════════
    
    /// <summary>
    /// Request to attach/detach a prop on a character.
    /// Client → Server for validation.
    /// </summary>
    [Serializable]
    public struct NetworkPropRequest
    {
        /// <summary>Unique request ID for response matching.</summary>
        public ushort RequestId;
        public uint ActorNetworkId;
        public uint CorrelationId;
        
        /// <summary>Network ID of the character.</summary>
        public uint CharacterNetworkId;
        
        /// <summary>Type of prop action.</summary>
        public PropActionType ActionType;
        
        /// <summary>Hash of the prop prefab (for lookup).</summary>
        public int PropHash;
        
        /// <summary>Hash of the bone name to attach to.</summary>
        public int BoneHash;
        
        /// <summary>Local position offset.</summary>
        public Vector3 LocalPosition;
        
        /// <summary>Local rotation (compressed euler angles).</summary>
        public short RotationX;
        public short RotationY;
        public short RotationZ;
        
        public const int SIZE_BYTES = 32;
        
        public void SetLocalRotation(Quaternion rotation)
        {
            Vector3 euler = rotation.eulerAngles;
            RotationX = (short)(euler.x / 360f * 32767f);
            RotationY = (short)(euler.y / 360f * 32767f);
            RotationZ = (short)(euler.z / 360f * 32767f);
        }
        
        public Quaternion GetLocalRotation()
        {
            return Quaternion.Euler(
                RotationX / 32767f * 360f,
                RotationY / 32767f * 360f,
                RotationZ / 32767f * 360f
            );
        }
    }
    
    /// <summary>
    /// Server response to prop request.
    /// </summary>
    [Serializable]
    public struct NetworkPropResponse
    {
        /// <summary>Matches RequestId from request.</summary>
        public ushort RequestId;
        public uint ActorNetworkId;
        public uint CorrelationId;
        
        /// <summary>Whether the prop action was approved.</summary>
        public bool Approved;
        
        /// <summary>Rejection reason if not approved.</summary>
        public PropRejectReason RejectReason;
        
        /// <summary>Server-assigned instance ID for the prop (for future reference).</summary>
        public int PropInstanceId;
        
        public const int SIZE_BYTES = 8;
    }
    
    /// <summary>
    /// Broadcast prop attachment/detachment to all clients.
    /// Server → All Clients.
    /// </summary>
    [Serializable]
    public struct NetworkPropBroadcast
    {
        /// <summary>Network ID of the character.</summary>
        public uint CharacterNetworkId;
        
        /// <summary>Type of prop action.</summary>
        public PropActionType ActionType;
        
        /// <summary>Hash of the prop prefab.</summary>
        public int PropHash;
        
        /// <summary>Hash of the bone name.</summary>
        public int BoneHash;
        
        /// <summary>Server-assigned instance ID.</summary>
        public int PropInstanceId;
        
        /// <summary>Local position offset.</summary>
        public Vector3 LocalPosition;
        
        /// <summary>Compressed local rotation.</summary>
        public short RotationX;
        public short RotationY;
        public short RotationZ;
        
        public const int SIZE_BYTES = 35;
        
        public void SetLocalRotation(Quaternion rotation)
        {
            Vector3 euler = rotation.eulerAngles;
            RotationX = (short)(euler.x / 360f * 32767f);
            RotationY = (short)(euler.y / 360f * 32767f);
            RotationZ = (short)(euler.z / 360f * 32767f);
        }
        
        public Quaternion GetLocalRotation()
        {
            return Quaternion.Euler(
                RotationX / 32767f * 360f,
                RotationY / 32767f * 360f,
                RotationZ / 32767f * 360f
            );
        }
    }
    
    /// <summary>
    /// Types of prop actions.
    /// </summary>
    public enum PropActionType : byte
    {
        /// <summary>Attach a prefab instance.</summary>
        AttachPrefab = 0,
        
        /// <summary>Attach an existing instance.</summary>
        AttachInstance = 1,
        
        /// <summary>Detach a prefab (destroy instance).</summary>
        DetachPrefab = 2,
        
        /// <summary>Detach an instance (don't destroy).</summary>
        DetachInstance = 3,
        
        /// <summary>Detach all props.</summary>
        DetachAll = 4
    }
    
    /// <summary>
    /// Reasons for prop rejection.
    /// </summary>
    public enum PropRejectReason : byte
    {
        None = 0,
        CharacterNotFound = 1,
        PropNotFound = 2,
        BoneNotFound = 3,
        AlreadyAttached = 4,
        NotAttached = 5,
        NotAuthorized = 6,
        MaxPropsReached = 7,
        NotOwner = 8,
        ProtocolMismatch = 9,
        SecurityViolation = 10,
        Timeout = 11
    }
    
    // ════════════════════════════════════════════════════════════════════════════════════════
    // INVINCIBILITY NETWORKING
    // ════════════════════════════════════════════════════════════════════════════════════════
    
    /// <summary>
    /// Request to set invincibility on a character.
    /// Client → Server for validation.
    /// </summary>
    [Serializable]
    public struct NetworkInvincibilityRequest
    {
        /// <summary>Unique request ID for response matching.</summary>
        public ushort RequestId;
        public uint ActorNetworkId;
        public uint CorrelationId;
        
        /// <summary>Network ID of the character.</summary>
        public uint CharacterNetworkId;
        
        /// <summary>Duration of invincibility in seconds (0 = cancel).</summary>
        public float Duration;
        
        /// <summary>Client timestamp.</summary>
        public float ClientTime;
        
        public const int SIZE_BYTES = 14;
    }
    
    /// <summary>
    /// Server response to invincibility request.
    /// </summary>
    [Serializable]
    public struct NetworkInvincibilityResponse
    {
        /// <summary>Matches RequestId from request.</summary>
        public ushort RequestId;
        public uint ActorNetworkId;
        public uint CorrelationId;
        
        /// <summary>Whether the invincibility was approved.</summary>
        public bool Approved;
        
        /// <summary>Rejection reason if not approved.</summary>
        public InvincibilityRejectReason RejectReason;
        
        /// <summary>Server-adjusted duration (may differ from requested).</summary>
        public float ApprovedDuration;
        
        public const int SIZE_BYTES = 8;
    }
    
    /// <summary>
    /// Broadcast invincibility state to all clients.
    /// Server → All Clients.
    /// </summary>
    [Serializable]
    public struct NetworkInvincibilityBroadcast
    {
        /// <summary>Network ID of the character.</summary>
        public uint CharacterNetworkId;
        
        /// <summary>Whether character is now invincible.</summary>
        public bool IsInvincible;
        
        /// <summary>Server start time of invincibility.</summary>
        public float StartTime;
        
        /// <summary>Duration of invincibility.</summary>
        public float Duration;
        
        public const int SIZE_BYTES = 13;
    }
    
    /// <summary>
    /// Reasons for invincibility rejection.
    /// </summary>
    public enum InvincibilityRejectReason : byte
    {
        None = 0,
        CharacterNotFound = 1,
        AlreadyInvincible = 2,
        NotInvincible = 3,
        InvalidDuration = 4,
        NotAuthorized = 5,
        OnCooldown = 6,
        NotOwner = 7,
        ProtocolMismatch = 8,
        SecurityViolation = 9,
        Timeout = 10
    }
    
    // ════════════════════════════════════════════════════════════════════════════════════════
    // POISE NETWORKING
    // ════════════════════════════════════════════════════════════════════════════════════════
    
    /// <summary>
    /// Request to modify poise on a character.
    /// Client → Server for validation.
    /// </summary>
    [Serializable]
    public struct NetworkPoiseRequest
    {
        /// <summary>Unique request ID for response matching.</summary>
        public ushort RequestId;
        public uint ActorNetworkId;
        public uint CorrelationId;
        
        /// <summary>Network ID of the character.</summary>
        public uint CharacterNetworkId;
        
        /// <summary>Type of poise action.</summary>
        public PoiseActionType ActionType;
        
        /// <summary>Value for the action (damage amount, set value, etc.).</summary>
        public float Value;
        
        /// <summary>Client timestamp for lag compensation.</summary>
        public float ClientTime;
        
        public const int SIZE_BYTES = 15;
    }
    
    /// <summary>
    /// Server response to poise request.
    /// </summary>
    [Serializable]
    public struct NetworkPoiseResponse
    {
        /// <summary>Matches RequestId from request.</summary>
        public ushort RequestId;
        public uint ActorNetworkId;
        public uint CorrelationId;
        
        /// <summary>Whether the poise action was approved.</summary>
        public bool Approved;
        
        /// <summary>Rejection reason if not approved.</summary>
        public PoiseRejectReason RejectReason;
        
        /// <summary>Current poise value after action.</summary>
        public float CurrentPoise;
        
        /// <summary>Whether poise is now broken.</summary>
        public bool IsBroken;
        
        public const int SIZE_BYTES = 9;
    }
    
    /// <summary>
    /// Broadcast poise state to all clients.
    /// Server → All Clients.
    /// </summary>
    [Serializable]
    public struct NetworkPoiseBroadcast
    {
        /// <summary>Network ID of the character.</summary>
        public uint CharacterNetworkId;
        
        /// <summary>Current poise value.</summary>
        public float CurrentPoise;
        
        /// <summary>Maximum poise value.</summary>
        public float MaximumPoise;
        
        /// <summary>Whether poise is broken.</summary>
        public bool IsBroken;
        
        /// <summary>Server timestamp.</summary>
        public float ServerTime;
        
        public const int SIZE_BYTES = 17;
    }
    
    /// <summary>
    /// Types of poise actions.
    /// </summary>
    public enum PoiseActionType : byte
    {
        /// <summary>Deal poise damage.</summary>
        Damage = 0,
        
        /// <summary>Set poise to specific value.</summary>
        Set = 1,
        
        /// <summary>Reset poise to maximum.</summary>
        Reset = 2,
        
        /// <summary>Add poise (heal).</summary>
        Add = 3
    }
    
    /// <summary>
    /// Reasons for poise rejection.
    /// </summary>
    public enum PoiseRejectReason : byte
    {
        None = 0,
        CharacterNotFound = 1,
        InvalidValue = 2,
        AlreadyBroken = 3,
        NotAuthorized = 4,
        NotOwner = 5,
        ProtocolMismatch = 6,
        SecurityViolation = 7,
        Timeout = 8
    }
    
    // ════════════════════════════════════════════════════════════════════════════════════════
    // BUSY LIMBS NETWORKING
    // ════════════════════════════════════════════════════════════════════════════════════════
    
    /// <summary>
    /// Request to set busy state on character limbs.
    /// Client → Server for validation.
    /// </summary>
    [Serializable]
    public struct NetworkBusyRequest
    {
        /// <summary>Unique request ID for response matching.</summary>
        public ushort RequestId;
        public uint ActorNetworkId;
        public uint CorrelationId;
        
        /// <summary>Network ID of the character.</summary>
        public uint CharacterNetworkId;
        
        /// <summary>Limb flags to modify.</summary>
        public BusyLimbs Limbs;
        
        /// <summary>Whether to set busy (true) or clear busy (false).</summary>
        public bool SetBusy;
        
        /// <summary>Timeout duration in seconds (0 = no timeout).</summary>
        public float Timeout;
        
        public const int SIZE_BYTES = 12;
    }
    
    /// <summary>
    /// Server response to busy request.
    /// </summary>
    [Serializable]
    public struct NetworkBusyResponse
    {
        /// <summary>Matches RequestId from request.</summary>
        public ushort RequestId;
        public uint ActorNetworkId;
        public uint CorrelationId;
        
        /// <summary>Whether the busy action was approved.</summary>
        public bool Approved;
        
        /// <summary>Rejection reason if not approved.</summary>
        public BusyRejectReason RejectReason;
        
        public const int SIZE_BYTES = 4;
    }
    
    /// <summary>
    /// Broadcast busy state to all clients.
    /// Server → All Clients.
    /// </summary>
    [Serializable]
    public struct NetworkBusyBroadcast
    {
        /// <summary>Network ID of the character.</summary>
        public uint CharacterNetworkId;
        
        /// <summary>Current busy limb flags.</summary>
        public BusyLimbs CurrentBusyLimbs;
        
        /// <summary>Server timestamp.</summary>
        public float ServerTime;
        
        public const int SIZE_BYTES = 9;
    }
    
    /// <summary>
    /// Limb flags for busy system (matches GC2's Busy.Limb enum).
    /// </summary>
    [Flags]
    public enum BusyLimbs : byte
    {
        None = 0,
        ArmLeft = 1 << 0,
        ArmRight = 1 << 1,
        LegLeft = 1 << 2,
        LegRight = 1 << 3,
        Arms = ArmLeft | ArmRight,
        Legs = LegLeft | LegRight,
        Every = Arms | Legs
    }
    
    /// <summary>
    /// Reasons for busy rejection.
    /// </summary>
    public enum BusyRejectReason : byte
    {
        None = 0,
        CharacterNotFound = 1,
        AlreadyBusy = 2,
        NotBusy = 3,
        NotAuthorized = 4,
        NotOwner = 5,
        ProtocolMismatch = 6,
        SecurityViolation = 7,
        Timeout = 8
    }
    
    // ════════════════════════════════════════════════════════════════════════════════════════
    // INTERACTION NETWORKING
    // ════════════════════════════════════════════════════════════════════════════════════════
    
    /// <summary>
    /// Request to interact with an object.
    /// Client → Server for validation.
    /// </summary>
    [Serializable]
    public struct NetworkInteractionRequest
    {
        /// <summary>Unique request ID for response matching.</summary>
        public ushort RequestId;
        public uint ActorNetworkId;
        public uint CorrelationId;
        
        /// <summary>Network ID of the interacting character.</summary>
        public uint CharacterNetworkId;
        
        /// <summary>Network ID of the interaction target (if networked object).</summary>
        public uint TargetNetworkId;
        
        /// <summary>Hash of the interaction target (for non-networked objects).</summary>
        public int TargetHash;
        
        /// <summary>Position of interaction (for validation).</summary>
        public Vector3 InteractionPosition;
        
        /// <summary>Client timestamp.</summary>
        public float ClientTime;
        
        public const int SIZE_BYTES = 30;
    }
    
    /// <summary>
    /// Server response to interaction request.
    /// </summary>
    [Serializable]
    public struct NetworkInteractionResponse
    {
        /// <summary>Matches RequestId from request.</summary>
        public ushort RequestId;
        public uint ActorNetworkId;
        public uint CorrelationId;
        
        /// <summary>Whether the interaction was approved.</summary>
        public bool Approved;
        
        /// <summary>Rejection reason if not approved.</summary>
        public InteractionRejectReason RejectReason;
        
        /// <summary>Result data from the interaction (optional).</summary>
        public int ResultData;
        
        public const int SIZE_BYTES = 8;
    }
    
    /// <summary>
    /// Broadcast interaction to all clients.
    /// Server → All Clients.
    /// </summary>
    [Serializable]
    public struct NetworkInteractionBroadcast
    {
        /// <summary>Network ID of the interacting character.</summary>
        public uint CharacterNetworkId;
        
        /// <summary>Network ID of the interaction target.</summary>
        public uint TargetNetworkId;
        
        /// <summary>Hash of the interaction target (for non-networked).</summary>
        public int TargetHash;
        
        /// <summary>Type of interaction that occurred.</summary>
        public InteractionType InteractionType;
        
        /// <summary>Server timestamp.</summary>
        public float ServerTime;
        
        public const int SIZE_BYTES = 17;
    }
    
    /// <summary>
    /// Focus/blur events for interaction targets.
    /// </summary>
    [Serializable]
    public struct NetworkInteractionFocusBroadcast
    {
        /// <summary>Network ID of the character.</summary>
        public uint CharacterNetworkId;
        
        /// <summary>Network ID of the focused target (0 = blur).</summary>
        public uint TargetNetworkId;
        
        /// <summary>Whether this is focus (true) or blur (false).</summary>
        public bool IsFocus;
        
        public const int SIZE_BYTES = 9;
    }
    
    /// <summary>
    /// Types of interactions.
    /// </summary>
    public enum InteractionType : byte
    {
        /// <summary>Generic interaction.</summary>
        Generic = 0,
        
        /// <summary>Pickup item.</summary>
        Pickup = 1,
        
        /// <summary>Use/activate object.</summary>
        Use = 2,
        
        /// <summary>Open container/door.</summary>
        Open = 3,
        
        /// <summary>Close container/door.</summary>
        Close = 4,
        
        /// <summary>Talk to NPC.</summary>
        Talk = 5,
        
        /// <summary>Read sign/book.</summary>
        Read = 6,
        
        /// <summary>Custom interaction.</summary>
        Custom = 255
    }
    
    /// <summary>
    /// Reasons for interaction rejection.
    /// </summary>
    public enum InteractionRejectReason : byte
    {
        None = 0,
        CharacterNotFound = 1,
        TargetNotFound = 2,
        OutOfRange = 3,
        TargetBusy = 4,
        CharacterBusy = 5,
        NotAuthorized = 6,
        ConditionsFailed = 7,
        OnCooldown = 8,
        NotOwner = 9,
        ProtocolMismatch = 10,
        SecurityViolation = 11,
        Timeout = 12
    }
    
    // ════════════════════════════════════════════════════════════════════════════════════════
    // COMBINED CORE STATE (For efficient delta sync)
    // ════════════════════════════════════════════════════════════════════════════════════════
    
    /// <summary>
    /// Combined core state for efficient periodic sync.
    /// Contains all core feature states in one packet.
    /// </summary>
    [Serializable]
    public struct NetworkCoreState
    {
        /// <summary>Network ID of the character.</summary>
        public uint CharacterNetworkId;
        
        /// <summary>Flags indicating which fields have changed.</summary>
        public CoreStateDeltaFlags DeltaFlags;
        
        /// <summary>Is character ragdolled.</summary>
        public bool IsRagdoll;
        
        /// <summary>Is character invincible.</summary>
        public bool IsInvincible;
        
        /// <summary>Invincibility end time.</summary>
        public float InvincibilityEndTime;
        
        /// <summary>Current poise value.</summary>
        public float CurrentPoise;
        
        /// <summary>Maximum poise value.</summary>
        public float MaximumPoise;
        
        /// <summary>Is poise broken.</summary>
        public bool IsPoiseBroken;
        
        /// <summary>Current busy limb flags.</summary>
        public BusyLimbs BusyLimbs;
        
        /// <summary>Server timestamp.</summary>
        public float ServerTime;
        
        public const int SIZE_BYTES = 26;
        
        /// <summary>
        /// Calculate delta flags between two states.
        /// </summary>
        public static CoreStateDeltaFlags CalculateDelta(NetworkCoreState a, NetworkCoreState b)
        {
            CoreStateDeltaFlags flags = CoreStateDeltaFlags.None;
            
            if (a.IsRagdoll != b.IsRagdoll) flags |= CoreStateDeltaFlags.Ragdoll;
            if (a.IsInvincible != b.IsInvincible) flags |= CoreStateDeltaFlags.Invincibility;
            if (Math.Abs(a.CurrentPoise - b.CurrentPoise) > 0.01f) flags |= CoreStateDeltaFlags.Poise;
            if (a.IsPoiseBroken != b.IsPoiseBroken) flags |= CoreStateDeltaFlags.PoiseBroken;
            if (a.BusyLimbs != b.BusyLimbs) flags |= CoreStateDeltaFlags.Busy;
            
            return flags;
        }
    }
    
    /// <summary>
    /// Delta flags for core state changes.
    /// </summary>
    [Flags]
    public enum CoreStateDeltaFlags : byte
    {
        None = 0,
        Ragdoll = 1 << 0,
        Invincibility = 1 << 1,
        Poise = 1 << 2,
        PoiseBroken = 1 << 3,
        Busy = 1 << 4,
        Props = 1 << 5,
        All = Ragdoll | Invincibility | Poise | PoiseBroken | Busy | Props
    }
    
    // ════════════════════════════════════════════════════════════════════════════════════════
    // STATISTICS
    // ════════════════════════════════════════════════════════════════════════════════════════
    
    /// <summary>
    /// Statistics for core feature networking.
    /// </summary>
    public struct NetworkCoreStats
    {
        public int RagdollRequestsSent;
        public int RagdollRequestsReceived;
        public int RagdollApproved;
        public int RagdollRejected;
        
        public int PropRequestsSent;
        public int PropRequestsReceived;
        public int PropApproved;
        public int PropRejected;
        
        public int InvincibilityRequestsSent;
        public int InvincibilityRequestsReceived;
        public int InvincibilityApproved;
        public int InvincibilityRejected;
        
        public int PoiseRequestsSent;
        public int PoiseRequestsReceived;
        public int PoiseApproved;
        public int PoiseRejected;
        
        public int InteractionRequestsSent;
        public int InteractionRequestsReceived;
        public int InteractionApproved;
        public int InteractionRejected;
        
        public void Reset()
        {
            this = default;
        }
    }
}
