using System;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Shooter
{
    /// <summary>
    /// Network-optimized data types for GC2 Shooter synchronization.
    /// </summary>
    /// 
    // ════════════════════════════════════════════════════════════════════════════════════════════
    // NETWORK SHOT REQUEST
    // ════════════════════════════════════════════════════════════════════════════════════════════
    
    /// <summary>
    /// Compact shot request sent from client to server. (~40 bytes)
    /// Contains all data needed to validate and replicate a shot.
    /// </summary>
    [Serializable]
    public struct NetworkShotRequest
    {
        /// <summary>Unique request ID for response matching.</summary>
        public ushort RequestId;
        
        /// <summary>Client timestamp when shot was fired.</summary>
        public float ClientTimestamp;
        
        /// <summary>Network ID of the shooter.</summary>
        public uint ShooterNetworkId;
        
        /// <summary>Muzzle position when shot was fired.</summary>
        public Vector3 MuzzlePosition;
        
        /// <summary>Shot direction (with spread already applied).</summary>
        public Vector3 ShotDirection;
        
        /// <summary>Hash of the weapon used.</summary>
        public int WeaponHash;
        
        /// <summary>Hash of the sight used.</summary>
        public int SightHash;
        
        /// <summary>Charge ratio for charged weapons (0-1).</summary>
        public float ChargeRatio;
        
        /// <summary>Shot index for multi-projectile weapons (shotguns).</summary>
        public byte ProjectileIndex;
        
        /// <summary>Total projectiles in this shot.</summary>
        public byte TotalProjectiles;
    }
    
    // ════════════════════════════════════════════════════════════════════════════════════════════
    // NETWORK HIT REQUEST
    // ════════════════════════════════════════════════════════════════════════════════════════════
    
    /// <summary>
    /// Hit request sent when a shot hits something. (~36 bytes)
    /// Sent after shot validation for each hit detected.
    /// </summary>
    [Serializable]
    public struct NetworkShooterHitRequest
    {
        /// <summary>Unique request ID for response matching.</summary>
        public ushort RequestId;
        
        /// <summary>Client timestamp when hit was detected.</summary>
        public float ClientTimestamp;
        
        /// <summary>Network ID of the shooter.</summary>
        public uint ShooterNetworkId;
        
        /// <summary>Network ID of the target (0 if environment).</summary>
        public uint TargetNetworkId;
        
        /// <summary>Hit point in world space.</summary>
        public Vector3 HitPoint;
        
        /// <summary>Hit normal for effects.</summary>
        public Vector3 HitNormal;
        
        /// <summary>Distance from muzzle to hit.</summary>
        public float Distance;
        
        /// <summary>Hash of the weapon used.</summary>
        public int WeaponHash;
        
        /// <summary>Pierce index (0 = first hit, 1+ = pierced).</summary>
        public byte PierceIndex;
        
        /// <summary>Whether this hit a character (vs environment).</summary>
        public bool IsCharacterHit;
    }
    
    // ════════════════════════════════════════════════════════════════════════════════════════════
    // NETWORK SHOT RESPONSE
    // ════════════════════════════════════════════════════════════════════════════════════════════
    
    /// <summary>
    /// Server response to a shot request. (~6 bytes)
    /// </summary>
    [Serializable]
    public struct NetworkShotResponse
    {
        /// <summary>The request ID this is responding to.</summary>
        public ushort RequestId;
        
        /// <summary>Whether the shot was validated.</summary>
        public bool Validated;
        
        /// <summary>Rejection reason if not validated.</summary>
        public ShotRejectionReason RejectionReason;
        
        /// <summary>Ammo count after shot (for sync).</summary>
        public ushort AmmoRemaining;
    }
    
    /// <summary>
    /// Server response to a hit request. (~8 bytes)
    /// </summary>
    [Serializable]
    public struct NetworkShooterHitResponse
    {
        /// <summary>The request ID this is responding to.</summary>
        public ushort RequestId;
        
        /// <summary>Whether the hit was validated.</summary>
        public bool Validated;
        
        /// <summary>Rejection reason if not validated.</summary>
        public HitRejectionReason RejectionReason;
        
        /// <summary>Server-calculated damage.</summary>
        public float Damage;
        
        /// <summary>Block result.</summary>
        public NetworkBlockResult BlockResult;
    }
    
    /// <summary>
    /// Reasons a shot can be rejected.
    /// </summary>
    public enum ShotRejectionReason : byte
    {
        None = 0,
        ShooterNotFound = 1,
        WeaponNotEquipped = 2,
        NoAmmo = 3,
        WeaponJammed = 4,
        CooldownActive = 5,
        InvalidPosition = 6,
        InvalidDirection = 7,
        TimestampTooOld = 8,
        RateLimitExceeded = 9,
        CheatSuspected = 10,
    }
    
    /// <summary>
    /// Reasons a hit can be rejected.
    /// </summary>
    public enum HitRejectionReason : byte
    {
        None = 0,
        ShotNotValidated = 1,
        ShooterNotFound = 2,
        TargetNotFound = 3,
        OutOfRange = 4,
        ObstructionDetected = 5,
        TargetInvincible = 6,
        TargetDodged = 7,
        AlreadyHit = 8,
        TimestampTooOld = 9,
        RaycastMissed = 10,
        InvalidTrajectory = 11,
        InvalidPosition = 12,
        CheatSuspected = 13,
    }
    
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
    // NETWORK SHOT BROADCAST
    // ════════════════════════════════════════════════════════════════════════════════════════════
    
    /// <summary>
    /// Broadcast to all clients when a shot is fired. (~32 bytes)
    /// Used to replicate muzzle flash, tracer, and sound.
    /// </summary>
    [Serializable]
    public struct NetworkShotBroadcast
    {
        /// <summary>Network ID of the shooter.</summary>
        public uint ShooterNetworkId;
        
        /// <summary>Muzzle position.</summary>
        public Vector3 MuzzlePosition;
        
        /// <summary>Shot direction.</summary>
        public Vector3 ShotDirection;
        
        /// <summary>Weapon hash for effects lookup.</summary>
        public int WeaponHash;
        
        /// <summary>Sight hash.</summary>
        public int SightHash;
        
        /// <summary>Final hit point (for tracer endpoint).</summary>
        public Vector3 HitPoint;
        
        /// <summary>Whether the shot hit something.</summary>
        public bool DidHit;
    }
    
    /// <summary>
    /// Broadcast to all clients when a hit is confirmed. (~28 bytes)
    /// </summary>
    [Serializable]
    public struct NetworkShooterHitBroadcast
    {
        /// <summary>Network ID of the shooter.</summary>
        public uint ShooterNetworkId;
        
        /// <summary>Network ID of the target.</summary>
        public uint TargetNetworkId;
        
        /// <summary>Hit point for effects.</summary>
        public Vector3 HitPoint;
        
        /// <summary>Hit normal for effects orientation.</summary>
        public Vector3 HitNormal;
        
        /// <summary>Weapon hash for effects lookup.</summary>
        public int WeaponHash;
        
        /// <summary>Block result.</summary>
        public byte BlockResult;
        
        /// <summary>Material hash for impact sound.</summary>
        public int MaterialHash;
    }
    
    // ════════════════════════════════════════════════════════════════════════════════════════════
    // NETWORK WEAPON STATE
    // ════════════════════════════════════════════════════════════════════════════════════════════
    
    /// <summary>
    /// Compact weapon state for synchronization. (~12 bytes)
    /// </summary>
    [Serializable]
    public struct NetworkWeaponState
    {
        /// <summary>Hash of the equipped weapon (0 if none).</summary>
        public int WeaponHash;
        
        /// <summary>Current sight hash.</summary>
        public int SightHash;
        
        /// <summary>Ammo in magazine.</summary>
        public ushort AmmoInMagazine;
        
        /// <summary>Weapon state flags.</summary>
        public byte StateFlags;
        
        public const byte FLAG_IS_RELOADING = 0x01;
        public const byte FLAG_IS_JAMMED = 0x02;
        public const byte FLAG_IS_AIMING = 0x04;
        public const byte FLAG_IS_SHOOTING = 0x08;
        public const byte FLAG_IS_CHARGING = 0x10;
        
        public bool IsReloading => (StateFlags & FLAG_IS_RELOADING) != 0;
        public bool IsJammed => (StateFlags & FLAG_IS_JAMMED) != 0;
        public bool IsAiming => (StateFlags & FLAG_IS_AIMING) != 0;
        public bool IsShooting => (StateFlags & FLAG_IS_SHOOTING) != 0;
        public bool IsCharging => (StateFlags & FLAG_IS_CHARGING) != 0;
        
        public static NetworkWeaponState None => new NetworkWeaponState
        {
            WeaponHash = 0,
            SightHash = 0,
            AmmoInMagazine = 0,
            StateFlags = 0
        };
    }
    
    // ════════════════════════════════════════════════════════════════════════════════════════════
    // NETWORK RELOAD
    // ════════════════════════════════════════════════════════════════════════════════════════════
    
    /// <summary>
    /// Reload request sent from client to server. (~16 bytes)
    /// </summary>
    [Serializable]
    public struct NetworkReloadRequest
    {
        /// <summary>Unique request ID for response matching.</summary>
        public ushort RequestId;
        
        /// <summary>Network ID of the character reloading.</summary>
        public uint CharacterNetworkId;
        
        /// <summary>Hash of the weapon to reload.</summary>
        public int WeaponHash;
        
        /// <summary>Client timestamp.</summary>
        public float ClientTimestamp;
    }
    
    /// <summary>
    /// Server response to a reload request. (~6 bytes)
    /// </summary>
    [Serializable]
    public struct NetworkReloadResponse
    {
        /// <summary>The request ID this is responding to.</summary>
        public ushort RequestId;
        
        /// <summary>Whether the reload was validated.</summary>
        public bool Validated;
        
        /// <summary>Rejection reason if not validated.</summary>
        public ReloadRejectionReason RejectionReason;
        
        /// <summary>Time when quick reload window starts (normalized 0-1).</summary>
        public byte QuickReloadWindowStart;
        
        /// <summary>Time when quick reload window ends (normalized 0-1).</summary>
        public byte QuickReloadWindowEnd;
    }
    
    /// <summary>
    /// Quick reload request sent when player attempts quick reload. (~12 bytes)
    /// </summary>
    [Serializable]
    public struct NetworkQuickReloadRequest
    {
        /// <summary>Unique request ID for response matching.</summary>
        public ushort RequestId;
        
        /// <summary>Network ID of the character.</summary>
        public uint CharacterNetworkId;
        
        /// <summary>Hash of the weapon being reloaded.</summary>
        public int WeaponHash;
        
        /// <summary>Normalized time (0-1) when quick reload was attempted.</summary>
        public float AttemptTime;
    }
    
    /// <summary>
    /// Reload broadcast to all clients. (~16 bytes)
    /// </summary>
    [Serializable]
    public struct NetworkReloadBroadcast
    {
        /// <summary>Network ID of the character reloading.</summary>
        public uint CharacterNetworkId;
        
        /// <summary>Hash of the weapon being reloaded.</summary>
        public int WeaponHash;
        
        /// <summary>New ammo count after reload.</summary>
        public ushort NewAmmoCount;
        
        /// <summary>Reload event type.</summary>
        public ReloadEventType EventType;
    }
    
    /// <summary>
    /// Types of reload events.
    /// </summary>
    public enum ReloadEventType : byte
    {
        /// <summary>Reload started.</summary>
        Started = 0,
        /// <summary>Reload completed normally.</summary>
        Completed = 1,
        /// <summary>Reload cancelled.</summary>
        Cancelled = 2,
        /// <summary>Quick reload succeeded.</summary>
        QuickReloadSuccess = 3,
        /// <summary>Quick reload failed.</summary>
        QuickReloadFailed = 4,
        /// <summary>Partial reload (one round loaded).</summary>
        PartialReload = 5
    }
    
    /// <summary>
    /// Reasons a reload can be rejected.
    /// </summary>
    public enum ReloadRejectionReason : byte
    {
        None = 0,
        CharacterNotFound = 1,
        WeaponNotEquipped = 2,
        AlreadyReloading = 3,
        MagazineFull = 4,
        NoReserveAmmo = 5,
        WeaponJammed = 6,
        InvalidState = 7,
        RateLimitExceeded = 8
    }
    
    // ════════════════════════════════════════════════════════════════════════════════════════════
    // NETWORK JAM / FIX
    // ════════════════════════════════════════════════════════════════════════════════════════════
    
    /// <summary>
    /// Jam broadcast from server when weapon jams. (~8 bytes)
    /// Server decides when weapons jam (random chance on fire).
    /// </summary>
    [Serializable]
    public struct NetworkJamBroadcast
    {
        /// <summary>Network ID of the character whose weapon jammed.</summary>
        public uint CharacterNetworkId;
        
        /// <summary>Hash of the weapon that jammed.</summary>
        public int WeaponHash;
    }
    
    /// <summary>
    /// Request to fix a jammed weapon. (~12 bytes)
    /// </summary>
    [Serializable]
    public struct NetworkFixJamRequest
    {
        /// <summary>Unique request ID for response matching.</summary>
        public ushort RequestId;
        
        /// <summary>Network ID of the character.</summary>
        public uint CharacterNetworkId;
        
        /// <summary>Hash of the jammed weapon.</summary>
        public int WeaponHash;
        
        /// <summary>Client timestamp.</summary>
        public float ClientTimestamp;
    }
    
    /// <summary>
    /// Server response to fix jam request. (~4 bytes)
    /// </summary>
    [Serializable]
    public struct NetworkFixJamResponse
    {
        /// <summary>The request ID this is responding to.</summary>
        public ushort RequestId;
        
        /// <summary>Whether the fix was validated.</summary>
        public bool Validated;
        
        /// <summary>Rejection reason if not validated.</summary>
        public FixJamRejectionReason RejectionReason;
    }
    
    /// <summary>
    /// Broadcast when jam fix completes or fails. (~10 bytes)
    /// </summary>
    [Serializable]
    public struct NetworkFixJamBroadcast
    {
        /// <summary>Network ID of the character.</summary>
        public uint CharacterNetworkId;
        
        /// <summary>Hash of the weapon.</summary>
        public int WeaponHash;
        
        /// <summary>Whether the fix succeeded.</summary>
        public bool Success;
    }
    
    /// <summary>
    /// Reasons a fix jam can be rejected.
    /// </summary>
    public enum FixJamRejectionReason : byte
    {
        None = 0,
        CharacterNotFound = 1,
        WeaponNotEquipped = 2,
        WeaponNotJammed = 3,
        AlreadyFixing = 4,
        InvalidState = 5,
        RateLimitExceeded = 6
    }
    
    // ════════════════════════════════════════════════════════════════════════════════════════════
    // NETWORK CHARGE STATE
    // ════════════════════════════════════════════════════════════════════════════════════════════
    
    /// <summary>
    /// Request to start charging a weapon. (~12 bytes)
    /// For charge-type weapons (railgun, bow, etc.).
    /// </summary>
    [Serializable]
    public struct NetworkChargeStartRequest
    {
        /// <summary>Unique request ID for response matching.</summary>
        public ushort RequestId;
        
        /// <summary>Network ID of the character.</summary>
        public uint CharacterNetworkId;
        
        /// <summary>Hash of the weapon being charged.</summary>
        public int WeaponHash;
        
        /// <summary>Client timestamp when charge started.</summary>
        public float ClientTimestamp;
    }
    
    /// <summary>
    /// Server response to charge start request. (~4 bytes)
    /// </summary>
    [Serializable]
    public struct NetworkChargeStartResponse
    {
        /// <summary>The request ID this is responding to.</summary>
        public ushort RequestId;
        
        /// <summary>Whether the charge was validated.</summary>
        public bool Validated;
        
        /// <summary>Rejection reason if not validated.</summary>
        public ChargeRejectionReason RejectionReason;
    }
    
    /// <summary>
    /// Request to cancel a charge. (~12 bytes)
    /// </summary>
    [Serializable]
    public struct NetworkChargeCancelRequest
    {
        /// <summary>Unique request ID for response matching.</summary>
        public ushort RequestId;
        
        /// <summary>Network ID of the character.</summary>
        public uint CharacterNetworkId;
        
        /// <summary>Hash of the weapon.</summary>
        public int WeaponHash;
        
        /// <summary>Client timestamp when cancelled.</summary>
        public float ClientTimestamp;
    }
    
    /// <summary>
    /// Charge state broadcast for sync. (~12 bytes)
    /// Sent periodically while charging and on state changes.
    /// </summary>
    [Serializable]
    public struct NetworkChargeBroadcast
    {
        /// <summary>Network ID of the character.</summary>
        public uint CharacterNetworkId;
        
        /// <summary>Hash of the weapon being charged.</summary>
        public int WeaponHash;
        
        /// <summary>Current charge ratio (0-1 compressed to byte).</summary>
        public byte ChargeRatio;
        
        /// <summary>Charge event type.</summary>
        public ChargeEventType EventType;
    }
    
    /// <summary>
    /// Types of charge events.
    /// </summary>
    public enum ChargeEventType : byte
    {
        /// <summary>Charge started.</summary>
        Started = 0,
        /// <summary>Charge in progress (sync update).</summary>
        Charging = 1,
        /// <summary>Charge released (weapon fired).</summary>
        Released = 2,
        /// <summary>Charge cancelled.</summary>
        Cancelled = 3,
        /// <summary>Auto-release triggered (max charge reached).</summary>
        AutoReleased = 4
    }
    
    /// <summary>
    /// Reasons a charge can be rejected.
    /// </summary>
    public enum ChargeRejectionReason : byte
    {
        None = 0,
        CharacterNotFound = 1,
        WeaponNotEquipped = 2,
        WeaponNotChargeable = 3,
        AlreadyCharging = 4,
        NoAmmo = 5,
        WeaponJammed = 6,
        Reloading = 7,
        CooldownActive = 8,
        InvalidState = 9
    }
    
    // ════════════════════════════════════════════════════════════════════════════════════════════
    // NETWORK SIGHT SWITCH
    // ════════════════════════════════════════════════════════════════════════════════════════════
    
    /// <summary>
    /// Request to switch weapon sight/scope. (~12 bytes)
    /// </summary>
    [Serializable]
    public struct NetworkSightSwitchRequest
    {
        /// <summary>Unique request ID for response matching.</summary>
        public ushort RequestId;
        
        /// <summary>Network ID of the character.</summary>
        public uint CharacterNetworkId;
        
        /// <summary>Hash of the weapon.</summary>
        public int WeaponHash;
        
        /// <summary>Hash of the new sight to switch to.</summary>
        public int NewSightHash;
        
        /// <summary>Client timestamp.</summary>
        public float ClientTimestamp;
    }
    
    /// <summary>
    /// Server response to sight switch request. (~4 bytes)
    /// </summary>
    [Serializable]
    public struct NetworkSightSwitchResponse
    {
        /// <summary>The request ID this is responding to.</summary>
        public ushort RequestId;
        
        /// <summary>Whether the switch was validated.</summary>
        public bool Validated;
        
        /// <summary>Rejection reason if not validated.</summary>
        public SightSwitchRejectionReason RejectionReason;
    }
    
    /// <summary>
    /// Broadcast when sight is switched. (~12 bytes)
    /// </summary>
    [Serializable]
    public struct NetworkSightSwitchBroadcast
    {
        /// <summary>Network ID of the character.</summary>
        public uint CharacterNetworkId;
        
        /// <summary>Hash of the weapon.</summary>
        public int WeaponHash;
        
        /// <summary>Hash of the new active sight.</summary>
        public int NewSightHash;
    }
    
    /// <summary>
    /// Reasons a sight switch can be rejected.
    /// </summary>
    public enum SightSwitchRejectionReason : byte
    {
        None = 0,
        CharacterNotFound = 1,
        WeaponNotEquipped = 2,
        SightNotAvailable = 3,
        AlreadyUsingSight = 4,
        Reloading = 5,
        Shooting = 6,
        InvalidState = 7,
        RateLimitExceeded = 8
    }
    
    // ════════════════════════════════════════════════════════════════════════════════════════════
    // NETWORK AIM STATE
    // ════════════════════════════════════════════════════════════════════════════════════════════
    
    /// <summary>
    /// Compact aim state for synchronization. (~16 bytes)
    /// </summary>
    [Serializable]
    public struct NetworkAimState
    {
        /// <summary>Aim point in world space.</summary>
        public Vector3 AimPoint;
        
        /// <summary>Current accuracy (0 = perfect, 1 = worst).</summary>
        public byte Accuracy;
        
        /// <summary>Whether actively aiming down sights.</summary>
        public bool IsAiming;
        
        /// <summary>Compressed aim direction (2 bytes for yaw/pitch).</summary>
        public ushort CompressedDirection;
        
        /// <summary>Compress a direction to 2 bytes.</summary>
        public static ushort CompressDirection(Vector3 direction)
        {
            float yaw = Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg;
            float pitch = Mathf.Asin(direction.y) * Mathf.Rad2Deg;
            
            byte yawByte = (byte)((yaw + 180f) / 360f * 255f);
            byte pitchByte = (byte)((pitch + 90f) / 180f * 255f);
            
            return (ushort)((yawByte << 8) | pitchByte);
        }
        
        /// <summary>Decompress direction from 2 bytes.</summary>
        public static Vector3 DecompressDirection(ushort compressed)
        {
            byte yawByte = (byte)(compressed >> 8);
            byte pitchByte = (byte)(compressed & 0xFF);
            
            float yaw = (yawByte / 255f * 360f) - 180f;
            float pitch = (pitchByte / 255f * 180f) - 90f;
            
            float cosPitch = Mathf.Cos(pitch * Mathf.Deg2Rad);
            return new Vector3(
                Mathf.Sin(yaw * Mathf.Deg2Rad) * cosPitch,
                Mathf.Sin(pitch * Mathf.Deg2Rad),
                Mathf.Cos(yaw * Mathf.Deg2Rad) * cosPitch
            );
        }
    }
}
