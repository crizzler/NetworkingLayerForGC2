using System;
using System.Collections.Generic;
using UnityEngine;

namespace Arawn.NetworkingCore.LagCompensation
{
    /// <summary>
    /// Utility class for performing lag-compensated hit detection.
    /// Provides static methods for common hit validation patterns.
    /// </summary>
    public static class HitValidationUtility
    {
        // RAYCAST VALIDATION ─────────────────────────────────────────────────
        
        /// <summary>
        /// Perform a lag-compensated raycast against all tracked entities.
        /// </summary>
        /// <param name="origin">Ray origin in world space.</param>
        /// <param name="direction">Ray direction (normalized).</param>
        /// <param name="maxDistance">Maximum ray distance.</param>
        /// <param name="clientTimestamp">When the client fired.</param>
        /// <param name="hits">Output array of valid hits, sorted by distance.</param>
        /// <param name="excludeNetworkIds">Optional network IDs to exclude (e.g., shooter).</param>
        /// <returns>Number of hits found.</returns>
        public static int RaycastAll(
            Vector3 origin,
            Vector3 direction,
            float maxDistance,
            NetworkTimestamp clientTimestamp,
            out HitValidationResult[] hits,
            uint[] excludeNetworkIds = null)
        {
            var manager = LagCompensationManager.Instance;
            var entityIds = manager.GetAllEntityIds();
            var validHits = new List<HitValidationResult>();
            
            HashSet<uint> excludeSet = null;
            if (excludeNetworkIds != null && excludeNetworkIds.Length > 0)
            {
                excludeSet = new HashSet<uint>(excludeNetworkIds);
            }
            
            Ray ray = new Ray(origin, direction);
            
            foreach (var entityId in entityIds)
            {
                if (excludeSet != null && excludeSet.Contains(entityId))
                    continue;
                
                var result = manager.ValidateRaycastHit(
                    entityId, origin, direction, maxDistance, clientTimestamp
                );
                
                if (result.isValid)
                {
                    validHits.Add(result);
                }
            }
            
            // Sort by distance
            validHits.Sort((a, b) => 
                Vector3.Distance(origin, a.hitPoint).CompareTo(
                Vector3.Distance(origin, b.hitPoint)));
            
            hits = validHits.ToArray();
            return hits.Length;
        }
        
        /// <summary>
        /// Perform a lag-compensated raycast and return the closest hit.
        /// </summary>
        public static bool Raycast(
            Vector3 origin,
            Vector3 direction,
            float maxDistance,
            NetworkTimestamp clientTimestamp,
            out HitValidationResult hit,
            uint[] excludeNetworkIds = null)
        {
            int count = RaycastAll(origin, direction, maxDistance, clientTimestamp, 
                out var hits, excludeNetworkIds);
            
            if (count > 0)
            {
                hit = hits[0];
                return true;
            }
            
            hit = default;
            return false;
        }
        
        // SPHERE OVERLAP VALIDATION ──────────────────────────────────────────
        
        /// <summary>
        /// Perform a lag-compensated sphere overlap against all tracked entities.
        /// </summary>
        public static int OverlapSphereAll(
            Vector3 center,
            float radius,
            NetworkTimestamp clientTimestamp,
            out HitValidationResult[] hits,
            uint[] excludeNetworkIds = null)
        {
            var manager = LagCompensationManager.Instance;
            var entityIds = manager.GetAllEntityIds();
            var validHits = new List<HitValidationResult>();
            
            HashSet<uint> excludeSet = null;
            if (excludeNetworkIds != null && excludeNetworkIds.Length > 0)
            {
                excludeSet = new HashSet<uint>(excludeNetworkIds);
            }
            
            foreach (var entityId in entityIds)
            {
                if (excludeSet != null && excludeSet.Contains(entityId))
                    continue;
                
                var result = manager.ValidateSphereHit(
                    entityId, center, radius, clientTimestamp
                );
                
                if (result.isValid)
                {
                    validHits.Add(result);
                }
            }
            
            hits = validHits.ToArray();
            return hits.Length;
        }
        
        /// <summary>
        /// Check if any entity is within a sphere at a historical timestamp.
        /// </summary>
        public static bool OverlapSphere(
            Vector3 center,
            float radius,
            NetworkTimestamp clientTimestamp,
            out HitValidationResult hit,
            uint[] excludeNetworkIds = null)
        {
            int count = OverlapSphereAll(center, radius, clientTimestamp,
                out var hits, excludeNetworkIds);
            
            if (count > 0)
            {
                hit = hits[0];
                return true;
            }
            
            hit = default;
            return false;
        }
        
        // BOX OVERLAP VALIDATION ─────────────────────────────────────────────
        
        /// <summary>
        /// Perform a lag-compensated box overlap against all tracked entities.
        /// </summary>
        public static int OverlapBoxAll(
            Vector3 center,
            Vector3 halfExtents,
            Quaternion orientation,
            NetworkTimestamp clientTimestamp,
            out HitValidationResult[] hits,
            uint[] excludeNetworkIds = null)
        {
            var manager = LagCompensationManager.Instance;
            var entityIds = manager.GetAllEntityIds();
            var validHits = new List<HitValidationResult>();
            
            HashSet<uint> excludeSet = null;
            if (excludeNetworkIds != null && excludeNetworkIds.Length > 0)
            {
                excludeSet = new HashSet<uint>(excludeNetworkIds);
            }
            
            Bounds queryBounds = new Bounds(center, halfExtents * 2f);
            float tolerance = manager.Config.hitTolerance;
            
            foreach (var entityId in entityIds)
            {
                if (excludeSet != null && excludeSet.Contains(entityId))
                    continue;
                
                if (!manager.TryGetStateAtTime(entityId, clientTimestamp, out var snapshot))
                    continue;
                
                if (!snapshot.isActive)
                    continue;
                
                // Expand entity bounds by tolerance
                Bounds expandedEntityBounds = snapshot.bounds;
                expandedEntityBounds.Expand(tolerance * 2f);
                
                // Check intersection
                if (queryBounds.Intersects(expandedEntityBounds))
                {
                    validHits.Add(new HitValidationResult
                    {
                        isValid = true,
                        reason = HitRejectReason.None,
                        targetNetworkId = entityId,
                        hitPoint = expandedEntityBounds.ClosestPoint(center),
                        historicalPosition = snapshot.position,
                        historicalBounds = snapshot.bounds,
                        distanceFromBounds = 0f,
                        clientTimestamp = clientTimestamp,
                        serverTimestamp = manager.LastTimestamp
                    });
                }
            }
            
            hits = validHits.ToArray();
            return hits.Length;
        }
        
        // CONE VALIDATION (for shotgun spread, etc.) ─────────────────────────
        
        /// <summary>
        /// Perform a lag-compensated cone overlap against all tracked entities.
        /// </summary>
        /// <param name="origin">Cone origin.</param>
        /// <param name="direction">Cone direction.</param>
        /// <param name="maxDistance">Cone length.</param>
        /// <param name="halfAngle">Half angle of cone in degrees.</param>
        /// <param name="clientTimestamp">When the attack occurred.</param>
        /// <param name="hits">Output hits.</param>
        /// <param name="excludeNetworkIds">Network IDs to exclude.</param>
        public static int OverlapConeAll(
            Vector3 origin,
            Vector3 direction,
            float maxDistance,
            float halfAngle,
            NetworkTimestamp clientTimestamp,
            out HitValidationResult[] hits,
            uint[] excludeNetworkIds = null)
        {
            var manager = LagCompensationManager.Instance;
            var entityIds = manager.GetAllEntityIds();
            var validHits = new List<HitValidationResult>();
            
            HashSet<uint> excludeSet = null;
            if (excludeNetworkIds != null && excludeNetworkIds.Length > 0)
            {
                excludeSet = new HashSet<uint>(excludeNetworkIds);
            }
            
            direction = direction.normalized;
            float cosHalfAngle = Mathf.Cos(halfAngle * Mathf.Deg2Rad);
            float tolerance = manager.Config.hitTolerance;
            
            foreach (var entityId in entityIds)
            {
                if (excludeSet != null && excludeSet.Contains(entityId))
                    continue;
                
                if (!manager.TryGetStateAtTime(entityId, clientTimestamp, out var snapshot))
                    continue;
                
                if (!snapshot.isActive)
                    continue;
                
                // Check if any corner of bounds is within cone
                Vector3 toTarget = snapshot.bounds.center - origin;
                float distanceToTarget = toTarget.magnitude;
                
                if (distanceToTarget > maxDistance + tolerance)
                    continue;
                
                // Check angle to bounds center
                float cosAngle = Vector3.Dot(toTarget.normalized, direction);
                
                // Account for bounds size when checking angle
                float boundsRadius = snapshot.bounds.extents.magnitude;
                float angleMargin = Mathf.Atan2(boundsRadius + tolerance, distanceToTarget);
                float adjustedCosHalfAngle = Mathf.Cos(halfAngle * Mathf.Deg2Rad + angleMargin);
                
                if (cosAngle >= adjustedCosHalfAngle)
                {
                    validHits.Add(new HitValidationResult
                    {
                        isValid = true,
                        reason = HitRejectReason.None,
                        targetNetworkId = entityId,
                        hitPoint = snapshot.bounds.ClosestPoint(origin),
                        historicalPosition = snapshot.position,
                        historicalBounds = snapshot.bounds,
                        distanceFromBounds = distanceToTarget,
                        clientTimestamp = clientTimestamp,
                        serverTimestamp = manager.LastTimestamp
                    });
                }
            }
            
            // Sort by distance
            validHits.Sort((a, b) => a.distanceFromBounds.CompareTo(b.distanceFromBounds));
            
            hits = validHits.ToArray();
            return hits.Length;
        }
        
        // HITSCAN VALIDATION (instant hit) ───────────────────────────────────
        
        /// <summary>
        /// Validate a hitscan weapon hit (instant, like a bullet).
        /// Combines raycast with position validation.
        /// </summary>
        public static HitValidationResult ValidateHitscan(
            Vector3 shooterPosition,
            Vector3 aimDirection,
            uint targetNetworkId,
            Vector3 reportedHitPoint,
            NetworkTimestamp clientTimestamp,
            float maxRange = 1000f)
        {
            var manager = LagCompensationManager.Instance;
            
            // First validate that target was where client claims
            var pointResult = manager.ValidateHit(
                targetNetworkId, reportedHitPoint, clientTimestamp
            );
            
            if (!pointResult.isValid)
                return pointResult;
            
            // Then validate that a ray from shooter could hit that point
            var rayResult = manager.ValidateRaycastHit(
                targetNetworkId, shooterPosition, aimDirection, maxRange, clientTimestamp
            );
            
            // Combine results
            if (rayResult.isValid)
            {
                // Use the more accurate of the two hit points
                pointResult.hitPoint = rayResult.hitPoint;
            }
            
            return pointResult;
        }
        
        // PROJECTILE VALIDATION ──────────────────────────────────────────────
        
        /// <summary>
        /// Validate a projectile hit (accounts for travel time).
        /// </summary>
        /// <param name="projectileOrigin">Where projectile was fired from.</param>
        /// <param name="hitPoint">Where projectile hit.</param>
        /// <param name="projectileSpeed">Speed of projectile in m/s.</param>
        /// <param name="targetNetworkId">Target that was hit.</param>
        /// <param name="fireTimestamp">When projectile was fired.</param>
        public static HitValidationResult ValidateProjectileHit(
            Vector3 projectileOrigin,
            Vector3 hitPoint,
            float projectileSpeed,
            uint targetNetworkId,
            NetworkTimestamp fireTimestamp)
        {
            // Calculate when projectile would have arrived
            float distance = Vector3.Distance(projectileOrigin, hitPoint);
            float travelTime = distance / projectileSpeed;
            
            // Adjust timestamp to impact time
            NetworkTimestamp impactTimestamp = fireTimestamp.Offset(travelTime);
            
            // Validate against position at impact time
            return LagCompensationManager.Instance.ValidateHit(
                targetNetworkId, hitPoint, impactTimestamp
            );
        }
        
        // MELEE VALIDATION ───────────────────────────────────────────────────
        
        /// <summary>
        /// Validate a melee hit (short range, often arc-based).
        /// </summary>
        public static int ValidateMeleeSwing(
            Vector3 attackerPosition,
            Vector3 attackDirection,
            float range,
            float arcAngle,
            NetworkTimestamp clientTimestamp,
            out HitValidationResult[] hits,
            uint[] excludeNetworkIds = null)
        {
            // Melee is essentially a cone with short range
            return OverlapConeAll(
                attackerPosition,
                attackDirection,
                range,
                arcAngle * 0.5f, // Convert full arc to half angle
                clientTimestamp,
                out hits,
                excludeNetworkIds
            );
        }
        
        // UTILITY METHODS ────────────────────────────────────────────────────
        
        /// <summary>
        /// Calculate the client timestamp from RTT and local time.
        /// </summary>
        public static NetworkTimestamp CalculateClientTimestamp(
            float localTime, 
            RTTTracker rttTracker,
            double serverTime)
        {
            // Client's perceived time = server time - one-way latency
            double clientPerceivedTime = serverTime - rttTracker.OneWayLatency;
            
            return NetworkTimestamp.FromServerTime(clientPerceivedTime);
        }
        
        /// <summary>
        /// Get the maximum allowed rewind time based on client's RTT.
        /// </summary>
        public static float GetMaxRewindTime(RTTTracker rttTracker, float baseRewind = 0.5f)
        {
            // Allow up to RTT + some buffer
            return Mathf.Min(baseRewind, rttTracker.SmoothedRTT + 0.1f);
        }
    }
}
