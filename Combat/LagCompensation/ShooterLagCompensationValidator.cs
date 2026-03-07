#if GC2_SHOOTER
using System;
using System.Collections.Generic;
using UnityEngine;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Shooter;
using Arawn.NetworkingCore;
using Arawn.NetworkingCore.LagCompensation;
using GameCreator.Runtime.Common;
using Arawn.GameCreator2.Networking.Shooter;

namespace Arawn.GameCreator2.Networking.Combat
{
    /// <summary>
    /// Lag compensation validator specialized for shooter combat.
    /// Validates shots using raycast reconstruction, trajectory verification, and hit zones.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Shooter validation involves:
    /// 1. Raycast reconstruction at historical positions
    /// 2. Bullet trajectory verification (for projectiles)
    /// 3. Hit zone determination with damage multipliers
    /// 4. Distance falloff calculation
    /// 5. Penetration tracking for piercing shots
    /// </para>
    /// </remarks>
    public class ShooterLagCompensationValidator : ICombatLagCompensationValidator
    {
        // ════════════════════════════════════════════════════════════════════
        // CONFIGURATION
        // ════════════════════════════════════════════════════════════════════
        
        /// <summary>Configuration for shooter validation.</summary>
        public ShooterValidationConfig Config { get; set; }
        
        // ════════════════════════════════════════════════════════════════════
        // PRIVATE FIELDS
        // ════════════════════════════════════════════════════════════════════
        
        private readonly LagCompensationManager m_LagManager;
        
        // Reusable key buffer to avoid GC allocations during cleanup
        private static readonly List<ushort> s_SharedKeyBuffer = new(16);
        
        // Validated shot tracking (shot ID -> data)
        private readonly Dictionary<ushort, ValidatedShotData> m_ValidatedShots = new(32);
        
        // ════════════════════════════════════════════════════════════════════
        // STRUCTS
        // ════════════════════════════════════════════════════════════════════
        
        private struct ValidatedShotData
        {
            public NetworkShotRequest Request;
            public float ValidatedTime;
            public int HitsProcessed;
            public int MaxPierceCount;
        }
        
        // ════════════════════════════════════════════════════════════════════
        // CONSTRUCTOR
        // ════════════════════════════════════════════════════════════════════
        
        public ShooterLagCompensationValidator(ShooterValidationConfig config = null)
        {
            Config = config ?? new ShooterValidationConfig();
            m_LagManager = LagCompensationManager.Instance;
        }
        
        // ════════════════════════════════════════════════════════════════════
        // ICombatLagCompensationValidator IMPLEMENTATION
        // ════════════════════════════════════════════════════════════════════
        
        public bool Validate(ref CombatValidationResult result)
        {
            // This base method is not used - use ValidateShotHit instead
            result = CombatValidationResult.Failed(
                CombatValidationRejectionReason.InternalError,
                "Use ValidateShotHit for shooter validation"
            );
            return false;
        }
        
        // ════════════════════════════════════════════════════════════════════
        // SHOT VALIDATION
        // ════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Validate a shot request (fired shot, not hit).
        /// Call this when client sends shot request, before any hits.
        /// </summary>
        /// <param name="request">The shot request from client.</param>
        /// <param name="shooterCharacter">The shooter's Character component.</param>
        /// <param name="weapon">The weapon being used (optional).</param>
        /// <returns>Validation result.</returns>
        public ShotValidationResult ValidateShot(
            NetworkShotRequest request,
            Character shooterCharacter,
            ShooterWeapon weapon = null)
        {
            var result = new ShotValidationResult
            {
                RequestId = request.RequestId,
                ShooterNetworkId = request.ShooterNetworkId
            };
            
            var clientTimestamp = NetworkTimestamp.FromServerTime(request.ClientTimestamp);
            var serverTimestamp = m_LagManager.LastTimestamp;
            double rewindAmount = serverTimestamp.serverTime - clientTimestamp.serverTime;
            
            // ═══════════════════════════════════════════════════════════════
            // STEP 1: Validate timestamp
            // ═══════════════════════════════════════════════════════════════
            
            if (rewindAmount < -Config.FutureTimestampTolerance)
            {
                result.IsValid = false;
                result.RejectionReason = ShotRejectionReason.InvalidPosition;
                result.RejectionDetails = $"Timestamp {rewindAmount * 1000:F0}ms in future";
                return result;
            }
            
            if (rewindAmount > Config.MaxRewindTime)
            {
                result.IsValid = false;
                result.RejectionReason = ShotRejectionReason.InvalidPosition;
                result.RejectionDetails = $"Rewind {rewindAmount * 1000:F0}ms exceeds max";
                return result;
            }
            
            // ═══════════════════════════════════════════════════════════════
            // STEP 2: Validate muzzle position
            // ═══════════════════════════════════════════════════════════════
            
            Vector3 expectedMuzzlePos = shooterCharacter.transform.position + Vector3.up * 1.5f;
            
            // Try to get historical position
            if (m_LagManager.TryGetPositionAtTime(
                request.ShooterNetworkId, 
                clientTimestamp, 
                out var historicalPos))
            {
                expectedMuzzlePos = historicalPos + Vector3.up * 1.5f;
            }
            
            float muzzleDeviation = Vector3.Distance(request.MuzzlePosition, expectedMuzzlePos);
            if (muzzleDeviation > Config.MaxMuzzleDeviation)
            {
                result.IsValid = false;
                result.RejectionReason = ShotRejectionReason.InvalidPosition;
                result.RejectionDetails = $"Muzzle deviation {muzzleDeviation:F2}m exceeds max";
                return result;
            }
            
            // ═══════════════════════════════════════════════════════════════
            // STEP 3: Validate direction (sanity check)
            // ═══════════════════════════════════════════════════════════════
            
            if (request.ShotDirection.sqrMagnitude < 0.9f || 
                request.ShotDirection.sqrMagnitude > 1.1f)
            {
                result.IsValid = false;
                result.RejectionReason = ShotRejectionReason.InvalidPosition;
                result.RejectionDetails = "Invalid shot direction magnitude";
                return result;
            }
            
            // ═══════════════════════════════════════════════════════════════
            // SHOT VALIDATED - Store for hit validation
            // ═══════════════════════════════════════════════════════════════
            
            result.IsValid = true;
            result.RejectionReason = ShotRejectionReason.None;
            result.ValidatedMuzzlePosition = request.MuzzlePosition;
            result.ValidatedDirection = request.ShotDirection.normalized;
            
            // Store validated shot for subsequent hit validation
            m_ValidatedShots[request.RequestId] = new ValidatedShotData
            {
                Request = request,
                ValidatedTime = Time.time,
                HitsProcessed = 0,
                MaxPierceCount = GetMaxPierceCount(weapon)
            };
            
            // Clean up old validated shots
            CleanupOldShots();
            
            return result;
        }
        
        // ════════════════════════════════════════════════════════════════════
        // HIT VALIDATION
        // ════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Validate a hit request using lag compensation.
        /// The shot must have been validated first via ValidateShot().
        /// </summary>
        /// <param name="request">The hit request from client.</param>
        /// <param name="shooterCharacter">The shooter's Character component.</param>
        /// <param name="weapon">The weapon being used (optional).</param>
        /// <returns>Validation result with detailed information.</returns>
        public CombatValidationResult ValidateShotHit(
            NetworkShooterHitRequest request,
            Character shooterCharacter,
            ShooterWeapon weapon = null)
        {
            var result = new CombatValidationResult
            {
                AttackerNetworkId = request.ShooterNetworkId,
                TargetNetworkId = request.TargetNetworkId,
                ClaimedHitPoint = request.HitPoint,
                HitNormal = request.HitNormal,
                HitZoneDamageMultiplier = 1f,
                DistanceFalloff = 1f
            };
            
            // Convert timestamp
            result.ClientTimestamp = NetworkTimestamp.FromServerTime(request.ClientTimestamp);
            result.ServerTimestamp = m_LagManager.LastTimestamp;

            // ═══════════════════════════════════════════════════════════════
            // STEP 0: Validate that this hit is bound to a validated shot
            // ═══════════════════════════════════════════════════════════════

            if (request.SourceShotRequestId == 0 ||
                !m_ValidatedShots.TryGetValue(request.SourceShotRequestId, out var validatedShot))
            {
                result.IsValid = false;
                result.RejectionReason = CombatValidationRejectionReason.ShotNotValidated;
                result.RejectionDetails = "Missing or unknown source shot reference";
                return result;
            }

            if (Time.time - validatedShot.ValidatedTime > Config.ValidatedShotLifetime)
            {
                m_ValidatedShots.Remove(request.SourceShotRequestId);
                result.IsValid = false;
                result.RejectionReason = CombatValidationRejectionReason.ShotNotValidated;
                result.RejectionDetails = "Source shot expired";
                return result;
            }

            if (validatedShot.Request.ShooterNetworkId != request.ShooterNetworkId ||
                validatedShot.Request.WeaponHash != request.WeaponHash)
            {
                result.IsValid = false;
                result.RejectionReason = CombatValidationRejectionReason.ShotNotValidated;
                result.RejectionDetails = "Source shot metadata mismatch";
                return result;
            }

            int maxAllowedHits = Mathf.Max(1, validatedShot.MaxPierceCount);
            if (validatedShot.HitsProcessed >= maxAllowedHits)
            {
                result.IsValid = false;
                result.RejectionReason = CombatValidationRejectionReason.ShotNotValidated;
                result.RejectionDetails = "Source shot already consumed";
                return result;
            }

            Vector3 validatedMuzzlePosition = validatedShot.Request.MuzzlePosition;
            Vector3 validatedShotDirection = validatedShot.Request.ShotDirection;
            if (validatedShotDirection.sqrMagnitude < 0.9f || validatedShotDirection.sqrMagnitude > 1.1f)
            {
                result.IsValid = false;
                result.RejectionReason = CombatValidationRejectionReason.ShotNotValidated;
                result.RejectionDetails = "Source shot direction is invalid";
                return result;
            }

            validatedShotDirection = validatedShotDirection.normalized;
            
            // ═══════════════════════════════════════════════════════════════
            // STEP 1: Environment hits (no target validation needed)
            // ═══════════════════════════════════════════════════════════════
            
            if (!request.IsCharacterHit || request.TargetNetworkId == 0)
            {
                // Environment hit - validate that claimed point lies on validated shot trajectory.
                float maxRange = GetWeaponRange(weapon);
                Vector3 toHit = request.HitPoint - validatedMuzzlePosition;
                float projectedDistance = Vector3.Dot(toHit, validatedShotDirection);
                if (projectedDistance < -Config.MaxDistanceDeviation)
                {
                    result.IsValid = false;
                    result.RejectionReason = CombatValidationRejectionReason.InvalidTrajectory;
                    result.RejectionDetails = "Environment hit is behind validated muzzle direction";
                    return result;
                }

                if (projectedDistance > maxRange + Config.MaxDistanceDeviation)
                {
                    result.IsValid = false;
                    result.RejectionReason = CombatValidationRejectionReason.OutOfShooterRange;
                    result.RejectionDetails = $"Environment hit distance {projectedDistance:F2}m exceeds range {maxRange:F2}m";
                    return result;
                }

                Vector3 projectedPoint = validatedMuzzlePosition + validatedShotDirection * Mathf.Max(0f, projectedDistance);
                float hitPointDeviation = Vector3.Distance(request.HitPoint, projectedPoint);
                if (hitPointDeviation > Config.MaxHitPointDeviation)
                {
                    result.IsValid = false;
                    result.RejectionReason = CombatValidationRejectionReason.InvalidTrajectory;
                    result.RejectionDetails = $"Environment hit trajectory deviation {hitPointDeviation:F2}m exceeds max";
                    return result;
                }

                float distanceDeviation = Mathf.Abs(request.Distance - Mathf.Max(0f, projectedDistance));
                if (distanceDeviation > Config.MaxDistanceDeviation)
                {
                    result.IsValid = false;
                    result.RejectionReason = CombatValidationRejectionReason.InvalidTrajectory;
                    result.RejectionDetails = $"Environment distance deviation {distanceDeviation:F2}m exceeds max";
                    return result;
                }

                result.IsValid = true;
                result.RejectionReason = CombatValidationRejectionReason.None;
                result.ValidatedHitPoint = request.HitPoint;
                result.HitZoneName = "Environment";
                MarkShotHitProcessed(request.SourceShotRequestId, validatedShot);
                return result;
            }
            
            // ═══════════════════════════════════════════════════════════════
            // STEP 2: Validate timestamp
            // ═══════════════════════════════════════════════════════════════
            
            double rewindAmount = result.RewindAmount;
            
            if (rewindAmount < -Config.FutureTimestampTolerance)
            {
                result.IsValid = false;
                result.RejectionReason = CombatValidationRejectionReason.TimestampInFuture;
                result.RejectionDetails = $"Timestamp {rewindAmount * 1000:F0}ms in future";
                return result;
            }
            
            if (rewindAmount > Config.MaxRewindTime)
            {
                result.IsValid = false;
                result.RejectionReason = CombatValidationRejectionReason.TimestampTooOld;
                result.RejectionDetails = $"Rewind {rewindAmount * 1000:F0}ms exceeds max";
                return result;
            }
            
            // ═══════════════════════════════════════════════════════════════
            // STEP 3: Get historical target state
            // ═══════════════════════════════════════════════════════════════
            
            if (!m_LagManager.TryGetStateAtTime(
                request.TargetNetworkId,
                result.ClientTimestamp,
                out var targetSnapshot))
            {
                result.IsValid = false;
                result.RejectionReason = CombatValidationRejectionReason.TargetNotRegistered;
                result.RejectionDetails = $"No history for target {request.TargetNetworkId}";
                return result;
            }
            
            result.HistoricalTargetPosition = targetSnapshot.position;
            result.HistoricalTargetRotation = targetSnapshot.rotation;
            result.HistoricalTargetBounds = targetSnapshot.bounds;
            
            if (!targetSnapshot.isActive)
            {
                result.IsValid = false;
                result.RejectionReason = CombatValidationRejectionReason.TargetNotActive;
                result.RejectionDetails = "Target was not active at claimed timestamp";
                return result;
            }
            
            // ═══════════════════════════════════════════════════════════════
            // STEP 4: Validate raycast (reconstruct shot trajectory)
            // ═══════════════════════════════════════════════════════════════
            
            // Use the validated shot trajectory as authoritative source.
            Vector3 muzzlePosition = validatedMuzzlePosition;
            Vector3 shotDirection = validatedShotDirection;
            
            // Validate that the shot could have hit the target
            float maxRange = GetWeaponRange(weapon);
            var hitValidation = m_LagManager.ValidateRaycastHit(
                request.TargetNetworkId,
                muzzlePosition,
                shotDirection,
                maxRange,
                result.ClientTimestamp
            );
            
            if (!hitValidation.isValid)
            {
                result.IsValid = false;
                result.RejectionReason = CombatValidationRejectionReason.RaycastMissed;
                result.RejectionDetails = $"Raycast did not hit target: {hitValidation.reason}";
                return result;
            }
            
            // ═══════════════════════════════════════════════════════════════
            // STEP 5: Validate hit point (with tolerance)
            // ═══════════════════════════════════════════════════════════════
            
            Vector3 validatedHitPoint = hitValidation.hitPoint;
            result.HitPointDeviation = Vector3.Distance(request.HitPoint, validatedHitPoint);
            
            if (result.HitPointDeviation > Config.MaxHitPointDeviation)
            {
                result.IsValid = false;
                result.RejectionReason = CombatValidationRejectionReason.CheatSuspected;
                result.RejectionDetails = $"Hit point deviation {result.HitPointDeviation:F2}m exceeds max";
                return result;
            }
            
            result.ValidatedHitPoint = validatedHitPoint;
            
            // ═══════════════════════════════════════════════════════════════
            // STEP 6: Validate distance
            // ═══════════════════════════════════════════════════════════════
            
            float actualDistance = request.Distance;
            float calculatedDistance = Vector3.Distance(muzzlePosition, validatedHitPoint);
            float distanceDeviation = Mathf.Abs(actualDistance - calculatedDistance);
            
            if (distanceDeviation > Config.MaxDistanceDeviation)
            {
                result.IsValid = false;
                result.RejectionReason = CombatValidationRejectionReason.InvalidTrajectory;
                result.RejectionDetails = $"Distance deviation {distanceDeviation:F2}m exceeds max";
                return result;
            }
            
            if (calculatedDistance > maxRange)
            {
                result.IsValid = false;
                result.RejectionReason = CombatValidationRejectionReason.OutOfShooterRange;
                result.RejectionDetails = $"Distance {calculatedDistance:F2}m exceeds range {maxRange:F2}m";
                return result;
            }
            
            // ═══════════════════════════════════════════════════════════════
            // STEP 7: Determine hit zone
            // ═══════════════════════════════════════════════════════════════
            
            DetermineHitZone(ref result, targetSnapshot);
            
            // ═══════════════════════════════════════════════════════════════
            // STEP 8: Calculate damage with falloff
            // ═══════════════════════════════════════════════════════════════
            
            result.BaseDamage = GetBaseDamage(weapon, shooterCharacter);
            result.DistanceFalloff = CalculateDistanceFalloff(calculatedDistance, weapon);
            result.FinalDamage = result.BaseDamage * result.HitZoneDamageMultiplier * result.DistanceFalloff;
            
            // ═══════════════════════════════════════════════════════════════
            // HIT VALIDATED!
            // ═══════════════════════════════════════════════════════════════
            
            result.IsValid = true;
            result.RejectionReason = CombatValidationRejectionReason.None;
            MarkShotHitProcessed(request.SourceShotRequestId, validatedShot);
            
            return result;
        }

        private void MarkShotHitProcessed(ushort sourceShotRequestId, ValidatedShotData shotData)
        {
            shotData.HitsProcessed++;
            m_ValidatedShots[sourceShotRequestId] = shotData;
        }
        
        /// <summary>
        /// Perform a lag-compensated raycast and return all valid hits.
        /// </summary>
        public List<CombatValidationResult> RaycastAll(
            uint shooterNetworkId,
            Vector3 muzzlePosition,
            Vector3 shotDirection,
            float maxRange,
            NetworkTimestamp clientTimestamp,
            ShooterWeapon weapon = null)
        {
            var results = new List<CombatValidationResult>();
            
            // Use the built-in utility for raycast against all entities
            int hitCount = HitValidationUtility.RaycastAll(
                muzzlePosition,
                shotDirection,
                maxRange,
                clientTimestamp,
                out var hits,
                new[] { shooterNetworkId }
            );
            
            for (int i = 0; i < hitCount; i++)
            {
                var hit = hits[i];
                
                var result = CombatValidationResult.Success(
                    shooterNetworkId,
                    hit.targetNetworkId,
                    hit.hitPoint
                );
                
                result.HistoricalTargetPosition = hit.historicalPosition;
                result.HistoricalTargetBounds = hit.historicalBounds;
                result.ClientTimestamp = clientTimestamp;
                result.ServerTimestamp = hit.serverTimestamp;
                
                // Get full state for hit zone calculation
                if (m_LagManager.TryGetStateAtTime(
                    hit.targetNetworkId, 
                    clientTimestamp, 
                    out var snapshot))
                {
                    result.HistoricalTargetRotation = snapshot.rotation;
                    DetermineHitZone(ref result, snapshot);
                }
                
                // Calculate damage
                float distance = Vector3.Distance(muzzlePosition, hit.hitPoint);
                result.BaseDamage = GetBaseDamage(weapon);
                result.DistanceFalloff = CalculateDistanceFalloff(distance, weapon);
                result.FinalDamage = result.BaseDamage * result.HitZoneDamageMultiplier * result.DistanceFalloff;
                
                results.Add(result);
            }
            
            return results;
        }
        
        // ════════════════════════════════════════════════════════════════════
        // HELPER METHODS
        // ════════════════════════════════════════════════════════════════════
        
        private void DetermineHitZone(
            ref CombatValidationResult result,
            LagCompensationHistory.StateSnapshot snapshot)
        {
            // Calculate hit height relative to target
            float localHitY = result.ValidatedHitPoint.y - snapshot.position.y;
            float targetHeight = snapshot.bounds.size.y;
            float normalizedHeight = Mathf.Clamp01(localHitY / targetHeight);
            
            // Determine zone based on height (shooters care more about headshots)
            if (normalizedHeight >= 0.80f)
            {
                result.HitZoneName = "Head";
                result.HitZoneDamageMultiplier = Config.HeadDamageMultiplier;
                result.IsCriticalZone = true;
            }
            else if (normalizedHeight >= 0.45f)
            {
                result.HitZoneName = "Torso";
                result.HitZoneDamageMultiplier = Config.TorsoDamageMultiplier;
                result.IsCriticalZone = false;
            }
            else
            {
                result.HitZoneName = "Legs";
                result.HitZoneDamageMultiplier = Config.LegsDamageMultiplier;
                result.IsCriticalZone = false;
            }
        }
        
        private float CalculateDistanceFalloff(float distance, ShooterWeapon weapon)
        {
            float falloffStart = Config.DefaultFalloffStartDistance;
            float falloffEnd = GetWeaponRange(weapon);
            
            if (distance <= falloffStart)
                return 1f;
            
            if (distance >= falloffEnd)
                return Config.MinDamageFalloff;
            
            // Linear falloff between start and end
            float t = (distance - falloffStart) / (falloffEnd - falloffStart);
            return Mathf.Lerp(1f, Config.MinDamageFalloff, t);
        }
        
        private float GetWeaponRange(ShooterWeapon weapon)
        {
            // GC2 ShooterWeapon range is per-shot-type (e.g. ShotRaycast.m_MaxDistance)
            // and stored in private fields. The config default serves as the server-side
            // maximum range gate for anti-cheat validation.
            return Config.DefaultWeaponRange;
        }
        
        private float GetBaseDamage(ShooterWeapon weapon)
        {
            return GetBaseDamage(weapon, null);
        }
        
        private float GetBaseDamage(ShooterWeapon weapon, Character shooter)
        {
            // Use weapon's configured fire power if available.
            if (weapon != null && shooter != null)
            {
                Args args = new Args(shooter);
                float power = (float)weapon.Fire.Power(args);
                if (power > 0f) return power;
            }
            
            return Config.DefaultBaseDamage;
        }
        
        private int GetMaxPierceCount(ShooterWeapon weapon)
        {
            // GC2 pierce count is configured per-shot-type (ShotRaycast.m_Pierces)
            // in a private field. The config default is used for server validation.
            return Config.DefaultMaxPierceCount;
        }
        
        private void CleanupOldShots()
        {
            float currentTime = Time.time;
            s_SharedKeyBuffer.Clear();
            
            foreach (var kvp in m_ValidatedShots)
            {
                if (currentTime - kvp.Value.ValidatedTime > Config.ValidatedShotLifetime)
                {
                    s_SharedKeyBuffer.Add(kvp.Key);
                }
            }
            
            foreach (var key in s_SharedKeyBuffer)
            {
                m_ValidatedShots.Remove(key);
            }
        }
        
        /// <summary>
        /// Clear all validated shot tracking.
        /// </summary>
        public void ClearValidatedShots()
        {
            m_ValidatedShots.Clear();
        }
    }
    
    /// <summary>
    /// Result of shot validation (not hit validation).
    /// </summary>
    [Serializable]
    public struct ShotValidationResult
    {
        public ushort RequestId;
        public uint ShooterNetworkId;
        public bool IsValid;
        public ShotRejectionReason RejectionReason;
        public string RejectionDetails;
        public Vector3 ValidatedMuzzlePosition;
        public Vector3 ValidatedDirection;
        
        public override string ToString()
        {
            if (IsValid)
                return $"[Shot VALID] ID:{RequestId} Shooter:{ShooterNetworkId}";
            else
                return $"[Shot INVALID] Reason:{RejectionReason} Details:{RejectionDetails ?? "none"}";
        }
    }
    
    /// <summary>
    /// Configuration for shooter lag compensation validation.
    /// </summary>
    [Serializable]
    public class ShooterValidationConfig
    {
        [Header("Timing")]
        [Tooltip("Maximum time to rewind for validation (seconds).")]
        public double MaxRewindTime = 0.5;
        
        [Tooltip("Tolerance for timestamps slightly in the future (seconds).")]
        public double FutureTimestampTolerance = 0.05;
        
        [Tooltip("How long validated shots are kept for hit validation (seconds).")]
        public float ValidatedShotLifetime = 2f;
        
        [Header("Position Validation")]
        [Tooltip("Maximum allowed muzzle position deviation (meters).")]
        public float MaxMuzzleDeviation = 1.5f;
        
        [Tooltip("Maximum allowed hit point deviation (meters).")]
        public float MaxHitPointDeviation = 0.5f;
        
        [Tooltip("Maximum allowed distance deviation (meters).")]
        public float MaxDistanceDeviation = 2f;
        
        [Header("Range")]
        [Tooltip("Default weapon range if not specified (meters).")]
        public float DefaultWeaponRange = 100f;
        
        [Header("Damage")]
        [Tooltip("Default base damage if not specified.")]
        public float DefaultBaseDamage = 20f;
        
        [Tooltip("Head hit zone damage multiplier.")]
        public float HeadDamageMultiplier = 2f;
        
        [Tooltip("Torso hit zone damage multiplier.")]
        public float TorsoDamageMultiplier = 1f;
        
        [Tooltip("Legs hit zone damage multiplier.")]
        public float LegsDamageMultiplier = 0.75f;
        
        [Header("Falloff")]
        [Tooltip("Distance at which damage falloff begins (meters).")]
        public float DefaultFalloffStartDistance = 30f;
        
        [Tooltip("Minimum damage multiplier at max range.")]
        public float MinDamageFalloff = 0.5f;
        
        [Header("Piercing")]
        [Tooltip("Default max pierce count if not specified.")]
        public int DefaultMaxPierceCount = 0;
    }
}
#endif
