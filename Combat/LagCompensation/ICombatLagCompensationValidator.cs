using System;
using UnityEngine;
using Arawn.NetworkingCore;
using Arawn.NetworkingCore.LagCompensation;

namespace Arawn.GameCreator2.Networking.Combat
{
    /// <summary>
    /// Base interface for combat-specific lag compensation validators.
    /// Implementations provide specialized hit validation for different combat types.
    /// </summary>
    public interface ICombatLagCompensationValidator
    {
        /// <summary>
        /// Validate a hit request against lag-compensated historical state.
        /// </summary>
        /// <param name="result">The result to populate.</param>
        /// <returns>True if the hit is valid.</returns>
        bool Validate(ref CombatValidationResult result);
    }
    
    /// <summary>
    /// Result of a combat-specific lag compensation validation.
    /// Extends HitValidationResult with combat-specific data.
    /// </summary>
    [Serializable]
    public struct CombatValidationResult
    {
        // ════════════════════════════════════════════════════════════════════
        // CORE HIT DATA
        // ════════════════════════════════════════════════════════════════════
        
        /// <summary>Whether the hit was valid.</summary>
        public bool IsValid;
        
        /// <summary>General rejection reason.</summary>
        public CombatValidationRejectionReason RejectionReason;
        
        /// <summary>Detailed rejection info for debugging.</summary>
        public string RejectionDetails;
        
        // ════════════════════════════════════════════════════════════════════
        // ENTITY DATA
        // ════════════════════════════════════════════════════════════════════
        
        /// <summary>Network ID of the attacker.</summary>
        public uint AttackerNetworkId;
        
        /// <summary>Network ID of the target.</summary>
        public uint TargetNetworkId;
        
        /// <summary>Target's position at the client timestamp.</summary>
        public Vector3 HistoricalTargetPosition;
        
        /// <summary>Target's rotation at the client timestamp.</summary>
        public Quaternion HistoricalTargetRotation;
        
        /// <summary>Target's bounds at the client timestamp.</summary>
        public Bounds HistoricalTargetBounds;
        
        // ════════════════════════════════════════════════════════════════════
        // HIT POINT DATA
        // ════════════════════════════════════════════════════════════════════
        
        /// <summary>Claimed hit point from client.</summary>
        public Vector3 ClaimedHitPoint;
        
        /// <summary>Server-validated hit point.</summary>
        public Vector3 ValidatedHitPoint;
        
        /// <summary>Hit normal/direction.</summary>
        public Vector3 HitNormal;
        
        /// <summary>Distance from claimed to validated hit point.</summary>
        public float HitPointDeviation;
        
        // ════════════════════════════════════════════════════════════════════
        // HIT ZONE DATA
        // ════════════════════════════════════════════════════════════════════
        
        /// <summary>Name of the hit zone (if applicable).</summary>
        public string HitZoneName;
        
        /// <summary>Damage multiplier from hit zone.</summary>
        public float HitZoneDamageMultiplier;
        
        /// <summary>Whether this was a critical hit zone.</summary>
        public bool IsCriticalZone;
        
        // ════════════════════════════════════════════════════════════════════
        // TIMING DATA
        // ════════════════════════════════════════════════════════════════════
        
        /// <summary>Client timestamp when hit was claimed.</summary>
        public NetworkTimestamp ClientTimestamp;
        
        /// <summary>Server timestamp when validation occurred.</summary>
        public NetworkTimestamp ServerTimestamp;
        
        /// <summary>How far back in time we rewound.</summary>
        public double RewindAmount => ServerTimestamp.serverTime - ClientTimestamp.serverTime;
        
        // ════════════════════════════════════════════════════════════════════
        // DAMAGE DATA
        // ════════════════════════════════════════════════════════════════════
        
        /// <summary>Base damage from weapon/skill.</summary>
        public float BaseDamage;
        
        /// <summary>Final calculated damage after multipliers.</summary>
        public float FinalDamage;
        
        /// <summary>Distance falloff applied (1.0 = full damage).</summary>
        public float DistanceFalloff;
        
        // ════════════════════════════════════════════════════════════════════
        // METHODS
        // ════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Create a failed validation result.
        /// </summary>
        public static CombatValidationResult Failed(
            CombatValidationRejectionReason reason, 
            string details = null)
        {
            return new CombatValidationResult
            {
                IsValid = false,
                RejectionReason = reason,
                RejectionDetails = details,
                HitZoneDamageMultiplier = 1f,
                DistanceFalloff = 1f
            };
        }
        
        /// <summary>
        /// Create a successful validation result.
        /// </summary>
        public static CombatValidationResult Success(
            uint attackerNetworkId,
            uint targetNetworkId,
            Vector3 hitPoint)
        {
            return new CombatValidationResult
            {
                IsValid = true,
                RejectionReason = CombatValidationRejectionReason.None,
                AttackerNetworkId = attackerNetworkId,
                TargetNetworkId = targetNetworkId,
                ValidatedHitPoint = hitPoint,
                HitZoneDamageMultiplier = 1f,
                DistanceFalloff = 1f
            };
        }
        
        public override string ToString()
        {
            if (IsValid)
            {
                return $"[VALID] Target:{TargetNetworkId} Zone:{HitZoneName ?? "none"} " +
                       $"Damage:{FinalDamage:F1} Rewind:{RewindAmount * 1000:F0}ms";
            }
            else
            {
                return $"[INVALID] Reason:{RejectionReason} Details:{RejectionDetails ?? "none"}";
            }
        }
    }
    
    /// <summary>
    /// Reasons why a combat validation can fail.
    /// </summary>
    public enum CombatValidationRejectionReason : byte
    {
        None = 0,
        
        // Entity issues
        AttackerNotFound = 1,
        TargetNotFound = 2,
        AttackerNotRegistered = 3,
        TargetNotRegistered = 4,
        
        // State issues
        TargetNotActive = 10,
        TargetInvincible = 11,
        TargetDodging = 12,
        TargetDead = 13,
        
        // Timing issues
        TimestampTooOld = 20,
        TimestampInFuture = 21,
        NoHistoryAvailable = 22,
        
        // Melee-specific
        OutOfMeleeRange = 30,
        OutsideAttackArc = 31,
        InvalidAttackPhase = 32,
        WeaponMismatch = 33,
        SkillMismatch = 34,
        AlreadyHitThisSwing = 35,
        
        // Shooter-specific
        RaycastMissed = 40,
        OutOfShooterRange = 41,
        InvalidTrajectory = 42,
        ShotNotValidated = 43,
        InvalidMuzzlePosition = 44,
        
        // General
        CheatSuspected = 100,
        InternalError = 255
    }
}
