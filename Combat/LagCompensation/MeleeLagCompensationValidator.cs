#if GC2_MELEE
using System;
using System.Collections.Generic;
using UnityEngine;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Melee;
using Arawn.NetworkingCore;
using Arawn.NetworkingCore.LagCompensation;
using GameCreator.Runtime.Common;
using Arawn.GameCreator2.Networking.Melee;

namespace Arawn.GameCreator2.Networking.Combat
{
    /// <summary>
    /// Lag compensation validator specialized for melee combat.
    /// Validates melee hits using arc/cone checks, weapon reach, and attack timing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Melee validation is more complex than shooter because:
    /// 1. Attacks have arc/cone shapes, not rays
    /// 2. Attack timing/phase matters (can only hit during active frames)
    /// 3. Multiple targets can be hit in a single swing
    /// 4. Weapon reach varies by weapon and attack type
    /// </para>
    /// </remarks>
    public class MeleeLagCompensationValidator : ICombatLagCompensationValidator
    {
        // ════════════════════════════════════════════════════════════════════
        // CONFIGURATION
        // ════════════════════════════════════════════════════════════════════
        
        /// <summary>Configuration for melee validation.</summary>
        public MeleeValidationConfig Config { get; set; }
        
        // ════════════════════════════════════════════════════════════════════
        // PRIVATE FIELDS
        // ════════════════════════════════════════════════════════════════════
        
        private readonly LagCompensationManager m_LagManager;
        private readonly HashSet<int> m_HitThisSwing = new(16);
        
        // Cache for per-swing tracking
        private uint m_CurrentSwingAttacker;
        private int m_CurrentSwingHash;
        
        // ════════════════════════════════════════════════════════════════════
        // CONSTRUCTOR
        // ════════════════════════════════════════════════════════════════════
        
        public MeleeLagCompensationValidator(MeleeValidationConfig config = null)
        {
            Config = config ?? new MeleeValidationConfig();
            m_LagManager = LagCompensationManager.Instance;
        }
        
        // ════════════════════════════════════════════════════════════════════
        // ICombatLagCompensationValidator IMPLEMENTATION
        // ════════════════════════════════════════════════════════════════════
        
        public bool Validate(ref CombatValidationResult result)
        {
            // This base method is not used for melee - use ValidateMeleeHit instead
            result = CombatValidationResult.Failed(
                CombatValidationRejectionReason.InternalError,
                "Use ValidateMeleeHit for melee validation"
            );
            return false;
        }
        
        // ════════════════════════════════════════════════════════════════════
        // MELEE-SPECIFIC VALIDATION
        // ════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Validate a melee hit request using lag compensation.
        /// </summary>
        /// <param name="request">The hit request from the client.</param>
        /// <param name="attackerCharacter">The attacker's Character component.</param>
        /// <param name="skill">The skill being used (optional).</param>
        /// <param name="weapon">The weapon being used (optional).</param>
        /// <returns>Validation result with detailed information.</returns>
        public CombatValidationResult ValidateMeleeHit(
            NetworkMeleeHitRequest request,
            Character attackerCharacter,
            Skill skill = null,
            MeleeWeapon weapon = null)
        {
            var result = new CombatValidationResult
            {
                AttackerNetworkId = request.AttackerNetworkId,
                TargetNetworkId = request.TargetNetworkId,
                ClaimedHitPoint = request.HitPoint,
                HitNormal = request.StrikeDirection,
                HitZoneDamageMultiplier = 1f,
                DistanceFalloff = 1f
            };
            
            // Convert client timestamp
            result.ClientTimestamp = NetworkTimestamp.FromServerTime(request.ClientTimestamp);
            result.ServerTimestamp = m_LagManager.LastTimestamp;
            
            // ═══════════════════════════════════════════════════════════════
            // STEP 1: Validate timestamp
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
                result.RejectionDetails = $"Rewind {rewindAmount * 1000:F0}ms exceeds max {Config.MaxRewindTime * 1000:F0}ms";
                return result;
            }
            
            // ═══════════════════════════════════════════════════════════════
            // STEP 2: Get historical target state
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
            
            // Check if target was active
            if (!targetSnapshot.isActive)
            {
                result.IsValid = false;
                result.RejectionReason = CombatValidationRejectionReason.TargetNotActive;
                result.RejectionDetails = "Target was not active at claimed timestamp";
                return result;
            }
            
            // ═══════════════════════════════════════════════════════════════
            // STEP 3: Get attacker's historical position (for range check)
            // ═══════════════════════════════════════════════════════════════
            
            Vector3 attackerPosition = attackerCharacter.transform.position;
            
            if (m_LagManager.TryGetPositionAtTime(
                request.AttackerNetworkId, 
                result.ClientTimestamp, 
                out var historicalAttackerPos))
            {
                attackerPosition = historicalAttackerPos;
            }
            
            // ═══════════════════════════════════════════════════════════════
            // STEP 4: Validate attack phase
            // ═══════════════════════════════════════════════════════════════
            
            MeleePhase phase = (MeleePhase)request.AttackPhase;
            if (!IsValidHitPhase(phase))
            {
                result.IsValid = false;
                result.RejectionReason = CombatValidationRejectionReason.InvalidAttackPhase;
                result.RejectionDetails = $"Phase {phase} is not a valid hit phase";
                return result;
            }
            
            // ═══════════════════════════════════════════════════════════════
            // STEP 5: Validate range (with tolerance)
            // ═══════════════════════════════════════════════════════════════
            
            float weaponReach = GetWeaponReach(weapon, skill, attackerCharacter);
            float distanceToTarget = Vector3.Distance(attackerPosition, targetSnapshot.position);
            float maxRange = weaponReach + Config.RangeTolerance;
            
            if (distanceToTarget > maxRange)
            {
                result.IsValid = false;
                result.RejectionReason = CombatValidationRejectionReason.OutOfMeleeRange;
                result.RejectionDetails = $"Distance {distanceToTarget:F2}m exceeds range {maxRange:F2}m";
                return result;
            }
            
            // ═══════════════════════════════════════════════════════════════
            // STEP 6: Validate attack arc/cone
            // ═══════════════════════════════════════════════════════════════
            
            Vector3 attackDirection = request.StrikeDirection.normalized;
            if (attackDirection == Vector3.zero)
            {
                attackDirection = (targetSnapshot.position - attackerPosition).normalized;
            }
            
            Vector3 toTarget = (targetSnapshot.position - attackerPosition).normalized;
            float angle = Vector3.Angle(attackDirection, toTarget);
            float attackArc = GetAttackArc(weapon, skill);
            float maxAngle = (attackArc * 0.5f) + Config.ArcTolerance;
            
            if (angle > maxAngle)
            {
                result.IsValid = false;
                result.RejectionReason = CombatValidationRejectionReason.OutsideAttackArc;
                result.RejectionDetails = $"Angle {angle:F1}° exceeds arc {maxAngle:F1}°";
                return result;
            }
            
            // ═══════════════════════════════════════════════════════════════
            // STEP 7: Check for duplicate hits (same swing)
            // ═══════════════════════════════════════════════════════════════
            
            int swingHash = HashCode.Combine(request.AttackerNetworkId, request.SkillHash, request.ComboNodeId);
            if (swingHash != m_CurrentSwingHash || request.AttackerNetworkId != m_CurrentSwingAttacker)
            {
                // New swing - clear hit tracking
                m_HitThisSwing.Clear();
                m_CurrentSwingHash = swingHash;
                m_CurrentSwingAttacker = request.AttackerNetworkId;
            }
            
            int targetHitHash = (int)request.TargetNetworkId;
            if (m_HitThisSwing.Contains(targetHitHash))
            {
                result.IsValid = false;
                result.RejectionReason = CombatValidationRejectionReason.AlreadyHitThisSwing;
                result.RejectionDetails = "Target already hit in this swing";
                return result;
            }
            
            // ═══════════════════════════════════════════════════════════════
            // STEP 8: Validate hit point (with tolerance)
            // ═══════════════════════════════════════════════════════════════
            
            Bounds expandedBounds = targetSnapshot.bounds;
            expandedBounds.Expand(Config.HitPointTolerance * 2f);
            
            Vector3 validatedHitPoint = request.HitPoint;
            
            if (!expandedBounds.Contains(request.HitPoint))
            {
                // Clamp hit point to bounds
                validatedHitPoint = expandedBounds.ClosestPoint(request.HitPoint);
                result.HitPointDeviation = Vector3.Distance(request.HitPoint, validatedHitPoint);
                
                if (result.HitPointDeviation > Config.MaxHitPointDeviation)
                {
                    result.IsValid = false;
                    result.RejectionReason = CombatValidationRejectionReason.CheatSuspected;
                    result.RejectionDetails = $"Hit point deviation {result.HitPointDeviation:F2}m exceeds max";
                    return result;
                }
            }
            
            result.ValidatedHitPoint = validatedHitPoint;
            
            // ═══════════════════════════════════════════════════════════════
            // STEP 9: Determine hit zone
            // ═══════════════════════════════════════════════════════════════
            
            DetermineHitZone(ref result, targetSnapshot);
            
            // ═══════════════════════════════════════════════════════════════
            // STEP 10: Calculate damage
            // ═══════════════════════════════════════════════════════════════
            
            result.BaseDamage = GetBaseDamage(weapon, skill, attackerCharacter);
            result.DistanceFalloff = 1f; // Melee typically doesn't have distance falloff
            result.FinalDamage = result.BaseDamage * result.HitZoneDamageMultiplier * result.DistanceFalloff;
            
            // ═══════════════════════════════════════════════════════════════
            // HIT VALIDATED!
            // ═══════════════════════════════════════════════════════════════
            
            result.IsValid = true;
            result.RejectionReason = CombatValidationRejectionReason.None;
            
            // Track this hit for duplicate prevention
            m_HitThisSwing.Add(targetHitHash);
            
            return result;
        }
        
        /// <summary>
        /// Validate an arc sweep at a historical timestamp.
        /// Returns all valid targets within the attack arc.
        /// </summary>
        public List<CombatValidationResult> ValidateArcSweep(
            uint attackerNetworkId,
            Vector3 attackerPosition,
            Vector3 attackDirection,
            float weaponReach,
            float attackArc,
            NetworkTimestamp clientTimestamp,
            uint[] excludeNetworkIds = null)
        {
            var results = new List<CombatValidationResult>();
            var allEntityIds = m_LagManager.GetAllEntityIds();
            
            HashSet<uint> excludeSet = null;
            if (excludeNetworkIds != null && excludeNetworkIds.Length > 0)
            {
                excludeSet = new HashSet<uint>(excludeNetworkIds);
            }
            
            float maxRange = weaponReach + Config.RangeTolerance;
            float maxAngle = (attackArc * 0.5f) + Config.ArcTolerance;
            
            foreach (var entityId in allEntityIds)
            {
                if (excludeSet != null && excludeSet.Contains(entityId))
                    continue;
                
                if (!m_LagManager.TryGetStateAtTime(entityId, clientTimestamp, out var snapshot))
                    continue;
                
                if (!snapshot.isActive)
                    continue;
                
                // Range check
                float distance = Vector3.Distance(attackerPosition, snapshot.position);
                if (distance > maxRange)
                    continue;
                
                // Arc check
                Vector3 toTarget = (snapshot.position - attackerPosition).normalized;
                float angle = Vector3.Angle(attackDirection, toTarget);
                if (angle > maxAngle)
                    continue;
                
                // Valid target
                var result = CombatValidationResult.Success(
                    attackerNetworkId,
                    entityId,
                    snapshot.bounds.center
                );
                
                result.HistoricalTargetPosition = snapshot.position;
                result.HistoricalTargetRotation = snapshot.rotation;
                result.HistoricalTargetBounds = snapshot.bounds;
                result.ClientTimestamp = clientTimestamp;
                result.ServerTimestamp = m_LagManager.LastTimestamp;
                
                DetermineHitZone(ref result, snapshot);
                
                results.Add(result);
            }
            
            return results;
        }
        
        // ════════════════════════════════════════════════════════════════════
        // HELPER METHODS
        // ════════════════════════════════════════════════════════════════════
        
        private bool IsValidHitPhase(MeleePhase phase)
        {
            // Only active phases can register hits
            return phase == MeleePhase.Active;
        }
        
        private float GetWeaponReach(MeleeWeapon weapon, Skill skill, Character attacker)
        {
            // GC2 MeleeWeapon has no explicit reach property — melee hit detection
            // is physics-based (collider sweeps). The config default serves as the
            // server-side range gate for anti-cheat validation.
            return Config.DefaultWeaponReach;
        }
        
        private float GetAttackArc(MeleeWeapon weapon, Skill skill)
        {
            // Default arc (in degrees)
            return Config.DefaultAttackArc;
        }
        
        private float GetBaseDamage(MeleeWeapon weapon, Skill skill, Character attacker)
        {
            // Use the skill's configured power value if available.
            if (skill != null && attacker != null)
            {
                Args args = new Args(attacker);
                float power = skill.GetPower(args);
                if (power > 0f) return power;
            }
            
            return Config.DefaultBaseDamage;
        }
        
        private void DetermineHitZone(
            ref CombatValidationResult result,
            LagCompensationHistory.StateSnapshot snapshot)
        {
            // Calculate hit height relative to target
            float localHitY = result.ValidatedHitPoint.y - snapshot.position.y;
            float targetHeight = snapshot.bounds.size.y;
            float normalizedHeight = Mathf.Clamp01(localHitY / targetHeight);
            
            // Simple zone determination based on height
            if (normalizedHeight >= 0.75f)
            {
                result.HitZoneName = "Head";
                result.HitZoneDamageMultiplier = Config.HeadDamageMultiplier;
                result.IsCriticalZone = true;
            }
            else if (normalizedHeight >= 0.4f)
            {
                result.HitZoneName = "Torso";
                result.HitZoneDamageMultiplier = 1f;
                result.IsCriticalZone = false;
            }
            else
            {
                result.HitZoneName = "Legs";
                result.HitZoneDamageMultiplier = Config.LegsDamageMultiplier;
                result.IsCriticalZone = false;
            }
        }
        
        /// <summary>
        /// Clear swing tracking (call when a swing ends).
        /// </summary>
        public void ClearSwingTracking()
        {
            m_HitThisSwing.Clear();
            m_CurrentSwingHash = 0;
            m_CurrentSwingAttacker = 0;
        }
    }
    
    /// <summary>
    /// Configuration for melee lag compensation validation.
    /// </summary>
    [Serializable]
    public class MeleeValidationConfig
    {
        [Header("Timing")]
        [Tooltip("Maximum time to rewind for validation (seconds).")]
        public double MaxRewindTime = 0.5;
        
        [Tooltip("Tolerance for timestamps slightly in the future (seconds).")]
        public double FutureTimestampTolerance = 0.05;
        
        [Header("Range")]
        [Tooltip("Default weapon reach if not specified (meters).")]
        public float DefaultWeaponReach = 2f;
        
        [Tooltip("Additional tolerance on range checks (meters).")]
        public float RangeTolerance = 0.3f;
        
        [Header("Attack Arc")]
        [Tooltip("Default attack arc if not specified (degrees).")]
        public float DefaultAttackArc = 120f;
        
        [Tooltip("Additional tolerance on arc checks (degrees).")]
        public float ArcTolerance = 15f;
        
        [Header("Hit Point")]
        [Tooltip("Tolerance for hit point validation (meters).")]
        public float HitPointTolerance = 0.3f;
        
        [Tooltip("Maximum allowed hit point deviation before rejection (meters).")]
        public float MaxHitPointDeviation = 1f;
        
        [Header("Damage")]
        [Tooltip("Default base damage if not specified.")]
        public float DefaultBaseDamage = 10f;
        
        [Tooltip("Head hit zone damage multiplier.")]
        public float HeadDamageMultiplier = 1.5f;
        
        [Tooltip("Legs hit zone damage multiplier.")]
        public float LegsDamageMultiplier = 0.75f;
    }
}
#endif
