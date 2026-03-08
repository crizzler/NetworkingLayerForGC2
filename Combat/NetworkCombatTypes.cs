using System;
using UnityEngine;

namespace Arawn.GameCreator2.Networking
{
    /// <summary>
    /// Compact network data structures for combat synchronization.
    /// Designed for minimal bandwidth while preserving combat integrity.
    /// </summary>
    /// 
    // ════════════════════════════════════════════════════════════════════════════════════════
    // HIT REQUEST (Client → Server) - 20 bytes
    // ════════════════════════════════════════════════════════════════════════════════════════
    
    /// <summary>
    /// Client's hit request sent to server for validation.
    /// Contains the minimum data needed for server-side hit validation.
    /// </summary>
    [Serializable]
    public struct NetworkHitRequest
    {
        /// <summary>Unique identifier for this hit request (for response matching).</summary>
        public ushort requestId;

        /// <summary>Authoritative actor network id for strict ownership validation.</summary>
        public uint actorNetworkId;

        /// <summary>Protocol v2 correlation id for sequencing/replay protection.</summary>
        public uint correlationId;
        
        /// <summary>Network ID of the target character.</summary>
        public uint targetNetworkId;
        
        /// <summary>Client timestamp when the hit occurred (for lag compensation).</summary>
        public float clientTime;
        
        /// <summary>Hit point in world space (compressed).</summary>
        public Vector3 hitPoint;
        
        /// <summary>Attack direction (compressed to 2 bytes).</summary>
        public short directionCompressed;
        
        /// <summary>Weapon hash ID (which weapon was used).</summary>
        public int weaponHash;
        
        /// <summary>Attack type (melee strike, projectile, etc.).</summary>
        public HitType hitType;
        
        /// <summary>
        /// Compress direction to 2 bytes (360 degrees → 0-65535).
        /// </summary>
        public void SetDirection(Vector3 direction)
        {
            float angle = Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg;
            if (angle < 0) angle += 360f;
            directionCompressed = (short)(angle / 360f * 65535f - 32768f);
        }
        
        /// <summary>
        /// Decompress direction from 2 bytes.
        /// </summary>
        public Vector3 GetDirection()
        {
            float angle = ((directionCompressed + 32768f) / 65535f) * 360f * Mathf.Deg2Rad;
            return new Vector3(Mathf.Sin(angle), 0f, Mathf.Cos(angle));
        }
        
        public const int SIZE_BYTES = 28;
    }
    
    /// <summary>
    /// Type of hit/attack being validated.
    /// </summary>
    public enum HitType : byte
    {
        /// <summary>Melee weapon strike (sword, axe, etc.).</summary>
        MeleeStrike = 0,
        
        /// <summary>Projectile hit (bullet, arrow, etc.).</summary>
        Projectile = 1,
        
        /// <summary>Area of effect damage (explosion, spell).</summary>
        AreaOfEffect = 2,
        
        /// <summary>Environmental damage (fall, hazard).</summary>
        Environmental = 3
    }
    
    // ════════════════════════════════════════════════════════════════════════════════════════
    // HIT RESPONSE (Server → Client) - 12 bytes
    // ════════════════════════════════════════════════════════════════════════════════════════
    
    /// <summary>
    /// Server's response to a hit request.
    /// Tells client whether hit was valid and final damage dealt.
    /// </summary>
    [Serializable]
    public struct NetworkHitResponse
    {
        /// <summary>Matches the requestId from NetworkHitRequest.</summary>
        public ushort requestId;

        /// <summary>Actor network id from request for response correlation.</summary>
        public uint actorNetworkId;

        /// <summary>Correlation id from request for response correlation.</summary>
        public uint correlationId;
        
        /// <summary>Whether the hit was validated by the server.</summary>
        public HitResult result;
        
        /// <summary>Server-calculated final damage (after multipliers, defense, etc.).</summary>
        public float finalDamage;
        
        /// <summary>Which hit zone was struck (for client-side effects).</summary>
        public HitZone hitZone;
        
        /// <summary>Flags for additional hit effects.</summary>
        public HitEffectFlags effects;
        
        public const int SIZE_BYTES = 20;
    }
    
    /// <summary>
    /// Result of server hit validation.
    /// </summary>
    public enum HitResult : byte
    {
        /// <summary>Hit validated, damage applied.</summary>
        Valid = 0,
        
        /// <summary>Target was out of range at historical time.</summary>
        OutOfRange = 1,
        
        /// <summary>Target was invincible/dodging.</summary>
        Invincible = 2,
        
        /// <summary>Target successfully blocked.</summary>
        Blocked = 3,
        
        /// <summary>Target successfully parried.</summary>
        Parried = 4,
        
        /// <summary>Target not found or already dead.</summary>
        InvalidTarget = 5,
        
        /// <summary>Attacker's weapon not valid.</summary>
        InvalidWeapon = 6,
        
        /// <summary>Request too old (exceeded max rewind time).</summary>
        TooOld = 7,

        /// <summary>Protocol context did not match actor/correlation requirements.</summary>
        ProtocolMismatch = 8,

        /// <summary>Ownership/rate/sequence validation rejected this request.</summary>
        SecurityViolation = 9,

        /// <summary>Request queue was full and request was dropped.</summary>
        RateLimitExceeded = 10
    }
    
    /// <summary>
    /// Hit zone identifier for damage multipliers.
    /// </summary>
    public enum HitZone : byte
    {
        Body = 0,
        Head = 1,
        Torso = 2,
        LeftArm = 3,
        RightArm = 4,
        LeftLeg = 5,
        RightLeg = 6
    }
    
    /// <summary>
    /// Flags for additional hit effects (bitfield).
    /// </summary>
    [Flags]
    public enum HitEffectFlags : byte
    {
        None = 0,
        Critical = 1 << 0,
        Backstab = 1 << 1,
        Knockback = 1 << 2,
        Stagger = 1 << 3,
        BreakGuard = 1 << 4,
        Lethal = 1 << 5
    }
    
    // ════════════════════════════════════════════════════════════════════════════════════════
    // HIT BROADCAST (Server → All Clients) - 16 bytes
    // ════════════════════════════════════════════════════════════════════════════════════════
    
    /// <summary>
    /// Server broadcast of a validated hit to all clients.
    /// Used for visual/audio effects on non-authoritative clients.
    /// </summary>
    [Serializable]
    public struct NetworkHitBroadcast
    {
        /// <summary>Network ID of the attacker.</summary>
        public uint attackerNetworkId;
        
        /// <summary>Network ID of the target.</summary>
        public uint targetNetworkId;
        
        /// <summary>Compressed hit point (relative to target, saves bandwidth).</summary>
        public short hitOffsetX;
        public short hitOffsetY;
        public short hitOffsetZ;
        
        /// <summary>Which hit zone was struck.</summary>
        public HitZone hitZone;
        
        /// <summary>Effect flags for visual feedback.</summary>
        public HitEffectFlags effects;
        
        /// <summary>
        /// Set hit point relative to target position.
        /// </summary>
        public void SetHitOffset(Vector3 targetPosition, Vector3 hitPoint)
        {
            Vector3 offset = hitPoint - targetPosition;
            // Compress to ±32m range with 1mm precision
            hitOffsetX = (short)Mathf.Clamp(offset.x * 1000f, short.MinValue, short.MaxValue);
            hitOffsetY = (short)Mathf.Clamp(offset.y * 1000f, short.MinValue, short.MaxValue);
            hitOffsetZ = (short)Mathf.Clamp(offset.z * 1000f, short.MinValue, short.MaxValue);
        }
        
        /// <summary>
        /// Get world hit point from target position.
        /// </summary>
        public Vector3 GetHitPoint(Vector3 targetPosition)
        {
            return targetPosition + new Vector3(
                hitOffsetX / 1000f,
                hitOffsetY / 1000f,
                hitOffsetZ / 1000f
            );
        }
        
        public const int SIZE_BYTES = 16;
    }
    
    // ════════════════════════════════════════════════════════════════════════════════════════
    // DAMAGE APPLICATION (Internal)
    // ════════════════════════════════════════════════════════════════════════════════════════
    
    /// <summary>
    /// Internal structure for applying validated damage.
    /// Contains all data needed to execute damage on target.
    /// </summary>
    public struct ValidatedDamage
    {
        public uint attackerNetworkId;
        public uint targetNetworkId;
        public float baseDamage;
        public float finalDamage;
        public float damageMultiplier;
        public Vector3 hitPoint;
        public Vector3 hitDirection;
        public HitZone hitZone;
        public HitEffectFlags effects;
        public int weaponHash;
        public HitType hitType;
        
        /// <summary>
        /// Create reaction input for GC2's combat system.
        /// </summary>
        public GameCreator.Runtime.Characters.ReactionInput ToReactionInput()
        {
            return new GameCreator.Runtime.Characters.ReactionInput(
                hitDirection,
                finalDamage
            );
        }
    }
    
    // ════════════════════════════════════════════════════════════════════════════════════════
    // COMBAT STATISTICS
    // ════════════════════════════════════════════════════════════════════════════════════════
    
    /// <summary>
    /// Combat network statistics for debugging and metrics.
    /// </summary>
    public struct NetworkCombatStats
    {
        public int hitRequestsSent;
        public int hitRequestsReceived;
        public int hitsValidated;
        public int hitsRejected;
        public float averageValidationTime;
        public float averageRewindTime;
        
        public float HitAcceptRate => hitRequestsReceived > 0 
            ? (float)hitsValidated / hitRequestsReceived 
            : 0f;
    }
}
