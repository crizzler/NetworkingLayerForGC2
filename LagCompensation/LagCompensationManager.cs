using System;
using System.Collections.Generic;
using UnityEngine;

namespace Arawn.NetworkingCore.LagCompensation
{
    /// <summary>
    /// Manages lag compensation history for all tracked entities.
    /// This is the main entry point for server-side hit validation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Usage Pattern:
    /// 1. On server startup, create LagCompensationManager
    /// 2. Register entities when they spawn (Register)
    /// 3. Call RecordFrame() every server tick
    /// 4. When validating hits, use TryGetPositionAtTime() or ValidateHit()
    /// 5. Unregister entities when they despawn (Unregister)
    /// </para>
    /// <para>
    /// This class is network-agnostic. Hook it up to your networking solution's
    /// server tick / simulation step.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// // In your server tick
    /// void OnServerTick(double serverTime)
    /// {
    ///     var timestamp = NetworkTimestamp.FromServerTime(serverTime);
    ///     LagCompensationManager.Instance.RecordFrame(timestamp);
    /// }
    /// 
    /// // When validating a hit
    /// void ValidateShot(uint shooterId, uint targetId, Vector3 hitPoint, double clientTime)
    /// {
    ///     var timestamp = NetworkTimestamp.FromServerTime(clientTime);
    ///     var result = LagCompensationManager.Instance.ValidateHit(targetId, hitPoint, timestamp);
    ///     if (result.isValid)
    ///         ApplyDamage(targetId, result.hitZone);
    /// }
    /// </code>
    /// </example>
    public class LagCompensationManager : IDisposable
    {
        // SINGLETON ──────────────────────────────────────────────────────────
        
        private static LagCompensationManager s_Instance;
        
        /// <summary>
        /// Global instance. Create with Initialize() first.
        /// </summary>
        public static LagCompensationManager Instance
        {
            get
            {
                if (s_Instance == null)
                {
                    Debug.LogWarning("[LagCompensation] Manager not initialized. Creating with defaults.");
                    s_Instance = new LagCompensationManager(new LagCompensationConfig());
                }
                return s_Instance;
            }
        }
        
        /// <summary>
        /// Initialize the global manager with configuration.
        /// Call this once on server startup.
        /// </summary>
        public static LagCompensationManager Initialize(LagCompensationConfig config)
        {
            s_Instance?.Dispose();
            s_Instance = new LagCompensationManager(config);
            return s_Instance;
        }
        
        /// <summary>
        /// Check if the manager is initialized.
        /// </summary>
        public static bool IsInitialized => s_Instance != null;
        
        // FIELDS ─────────────────────────────────────────────────────────────
        
        private readonly Dictionary<uint, TrackedEntity> m_Entities;
        private readonly LagCompensationConfig m_Config;
        private readonly object m_Lock = new object();
        
        private NetworkTimestamp m_LastRecordedTimestamp;
        private bool m_Disposed;
        
        // EVENTS ─────────────────────────────────────────────────────────────
        
        /// <summary>
        /// Fired when an entity is registered.
        /// </summary>
        public event Action<uint> OnEntityRegistered;
        
        /// <summary>
        /// Fired when an entity is unregistered.
        /// </summary>
        public event Action<uint> OnEntityUnregistered;
        
        /// <summary>
        /// Fired when a hit validation is performed (for debugging/metrics).
        /// </summary>
        public event Action<HitValidationResult> OnHitValidated;
        
        // PROPERTIES ─────────────────────────────────────────────────────────
        
        /// <summary>
        /// Number of entities being tracked.
        /// </summary>
        public int EntityCount
        {
            get
            {
                lock (m_Lock) return m_Entities.Count;
            }
        }
        
        /// <summary>
        /// Configuration used by this manager.
        /// </summary>
        public LagCompensationConfig Config => m_Config;
        
        /// <summary>
        /// Most recent recorded timestamp.
        /// </summary>
        public NetworkTimestamp LastTimestamp => m_LastRecordedTimestamp;
        
        // STRUCTS ────────────────────────────────────────────────────────────
        
        private class TrackedEntity
        {
            public ILagCompensated entity;
            public LagCompensationHistory history;
            public bool isActive;
        }
        
        // CONSTRUCTOR ────────────────────────────────────────────────────────
        
        /// <summary>
        /// Create a new lag compensation manager.
        /// </summary>
        public LagCompensationManager(LagCompensationConfig config)
        {
            m_Config = config ?? new LagCompensationConfig();
            m_Entities = new Dictionary<uint, TrackedEntity>(64);
            m_Disposed = false;
        }
        
        // REGISTRATION ───────────────────────────────────────────────────────
        
        /// <summary>
        /// Register an entity for lag compensation tracking.
        /// </summary>
        public void Register(ILagCompensated entity)
        {
            if (entity == null)
            {
                Debug.LogWarning("[LagCompensation] Cannot register null entity");
                return;
            }
            
            lock (m_Lock)
            {
                if (m_Entities.ContainsKey(entity.NetworkId))
                {
                    Debug.LogWarning($"[LagCompensation] Entity {entity.NetworkId} already registered");
                    return;
                }
                
                int bufferSize = m_Config.CalculateRequiredBufferSize();
                
                m_Entities[entity.NetworkId] = new TrackedEntity
                {
                    entity = entity,
                    history = new LagCompensationHistory(bufferSize),
                    isActive = true
                };
            }
            
            OnEntityRegistered?.Invoke(entity.NetworkId);
        }
        
        /// <summary>
        /// Unregister an entity from lag compensation tracking.
        /// </summary>
        public void Unregister(uint networkId)
        {
            bool removed = false;
            lock (m_Lock)
            {
                removed = m_Entities.Remove(networkId);
            }
            
            if (removed)
            {
                OnEntityUnregistered?.Invoke(networkId);
            }
        }
        
        /// <summary>
        /// Unregister an entity from lag compensation tracking.
        /// </summary>
        public void Unregister(ILagCompensated entity)
        {
            if (entity != null)
            {
                Unregister(entity.NetworkId);
            }
        }
        
        /// <summary>
        /// Check if an entity is registered.
        /// </summary>
        public bool IsRegistered(uint networkId)
        {
            lock (m_Lock)
            {
                return m_Entities.ContainsKey(networkId);
            }
        }
        
        // RECORDING ──────────────────────────────────────────────────────────
        
        /// <summary>
        /// Record the current state of all tracked entities.
        /// Call this every server tick.
        /// </summary>
        public void RecordFrame(NetworkTimestamp timestamp)
        {
            lock (m_Lock)
            {
                foreach (var kvp in m_Entities)
                {
                    var tracked = kvp.Value;
                    if (tracked.entity != null && tracked.isActive)
                    {
                        tracked.history.RecordSnapshot(tracked.entity, timestamp);
                    }
                }
                m_LastRecordedTimestamp = timestamp;
            }
        }
        
        /// <summary>
        /// Record a single entity's state (for entities that update at different rates).
        /// </summary>
        public void RecordEntity(uint networkId, NetworkTimestamp timestamp)
        {
            lock (m_Lock)
            {
                if (m_Entities.TryGetValue(networkId, out var tracked))
                {
                    if (tracked.entity != null && tracked.isActive)
                    {
                        tracked.history.RecordSnapshot(tracked.entity, timestamp);
                    }
                }
            }
        }
        
        // QUERYING ───────────────────────────────────────────────────────────
        
        /// <summary>
        /// Try to get an entity's position at a specific timestamp.
        /// </summary>
        public bool TryGetPositionAtTime(uint networkId, NetworkTimestamp timestamp, 
            out Vector3 position)
        {
            position = Vector3.zero;
            
            // Clamp timestamp to max rewind time
            double maxRewind = m_Config.maxRewindTime;
            double rewindAmount = m_LastRecordedTimestamp.serverTime - timestamp.serverTime;
            
            if (rewindAmount > maxRewind)
            {
                // Too far in the past - use clamped time
                timestamp = m_LastRecordedTimestamp.Offset(-maxRewind);
            }
            
            lock (m_Lock)
            {
                if (m_Entities.TryGetValue(networkId, out var tracked))
                {
                    return tracked.history.TryGetPositionAt(timestamp, out position);
                }
            }
            
            return false;
        }
        
        /// <summary>
        /// Try to get an entity's full state at a specific timestamp.
        /// </summary>
        public bool TryGetStateAtTime(uint networkId, NetworkTimestamp timestamp,
            out LagCompensationHistory.StateSnapshot snapshot)
        {
            snapshot = default;
            
            // Clamp timestamp
            double maxRewind = m_Config.maxRewindTime;
            double rewindAmount = m_LastRecordedTimestamp.serverTime - timestamp.serverTime;
            
            if (rewindAmount > maxRewind)
            {
                timestamp = m_LastRecordedTimestamp.Offset(-maxRewind);
            }
            
            lock (m_Lock)
            {
                if (m_Entities.TryGetValue(networkId, out var tracked))
                {
                    return tracked.history.TryGetStateAt(timestamp, out snapshot);
                }
            }
            
            return false;
        }
        
        /// <summary>
        /// Try to get an entity's bounds at a specific timestamp.
        /// </summary>
        public bool TryGetBoundsAtTime(uint networkId, NetworkTimestamp timestamp,
            out Bounds bounds)
        {
            bounds = default;
            
            if (TryGetStateAtTime(networkId, timestamp, out var snapshot))
            {
                bounds = snapshot.bounds;
                return true;
            }
            
            return false;
        }
        
        // HIT VALIDATION ─────────────────────────────────────────────────────
        
        /// <summary>
        /// Validate a hit against an entity at a historical timestamp.
        /// </summary>
        /// <param name="targetNetworkId">The target entity's network ID.</param>
        /// <param name="hitPoint">World-space point where the hit occurred.</param>
        /// <param name="clientTimestamp">The timestamp when the client fired.</param>
        /// <returns>Validation result with details.</returns>
        public HitValidationResult ValidateHit(uint targetNetworkId, Vector3 hitPoint,
            NetworkTimestamp clientTimestamp)
        {
            var result = new HitValidationResult
            {
                targetNetworkId = targetNetworkId,
                hitPoint = hitPoint,
                clientTimestamp = clientTimestamp,
                serverTimestamp = m_LastRecordedTimestamp
            };
            
            // Check if entity exists
            if (!TryGetStateAtTime(targetNetworkId, clientTimestamp, out var historicalState))
            {
                result.isValid = false;
                result.reason = HitRejectReason.EntityNotFound;
                OnHitValidated?.Invoke(result);
                return result;
            }
            
            // Check if entity was active
            if (!historicalState.isActive)
            {
                result.isValid = false;
                result.reason = HitRejectReason.EntityInactive;
                result.historicalPosition = historicalState.position;
                OnHitValidated?.Invoke(result);
                return result;
            }
            
            result.historicalPosition = historicalState.position;
            result.historicalBounds = historicalState.bounds;
            
            // Check if hit point is within bounds (with tolerance)
            float tolerance = m_Config.hitTolerance;
            float distance = historicalState.DistanceToPoint(hitPoint);
            result.distanceFromBounds = distance;
            
            if (distance <= tolerance)
            {
                result.isValid = true;
                result.reason = HitRejectReason.None;
            }
            else
            {
                result.isValid = false;
                result.reason = HitRejectReason.OutOfRange;
            }
            
            OnHitValidated?.Invoke(result);
            return result;
        }
        
        /// <summary>
        /// Validate a raycast hit against an entity at a historical timestamp.
        /// </summary>
        public HitValidationResult ValidateRaycastHit(uint targetNetworkId, 
            Vector3 rayOrigin, Vector3 rayDirection, float maxDistance,
            NetworkTimestamp clientTimestamp)
        {
            var result = new HitValidationResult
            {
                targetNetworkId = targetNetworkId,
                clientTimestamp = clientTimestamp,
                serverTimestamp = m_LastRecordedTimestamp
            };
            
            if (!TryGetStateAtTime(targetNetworkId, clientTimestamp, out var historicalState))
            {
                result.isValid = false;
                result.reason = HitRejectReason.EntityNotFound;
                OnHitValidated?.Invoke(result);
                return result;
            }
            
            if (!historicalState.isActive)
            {
                result.isValid = false;
                result.reason = HitRejectReason.EntityInactive;
                OnHitValidated?.Invoke(result);
                return result;
            }
            
            result.historicalPosition = historicalState.position;
            result.historicalBounds = historicalState.bounds;
            
            // Expand bounds by tolerance
            Bounds expandedBounds = historicalState.bounds;
            expandedBounds.Expand(m_Config.hitTolerance * 2f);
            
            // Check ray intersection
            Ray ray = new Ray(rayOrigin, rayDirection);
            if (expandedBounds.IntersectRay(ray, out float hitDistance))
            {
                if (hitDistance <= maxDistance)
                {
                    result.isValid = true;
                    result.reason = HitRejectReason.None;
                    result.hitPoint = ray.GetPoint(hitDistance);
                    result.distanceFromBounds = 0f;
                }
                else
                {
                    result.isValid = false;
                    result.reason = HitRejectReason.OutOfRange;
                    result.distanceFromBounds = hitDistance - maxDistance;
                }
            }
            else
            {
                result.isValid = false;
                result.reason = HitRejectReason.RayMissed;
                result.distanceFromBounds = Vector3.Distance(
                    rayOrigin, 
                    expandedBounds.ClosestPoint(rayOrigin)
                );
            }
            
            OnHitValidated?.Invoke(result);
            return result;
        }
        
        /// <summary>
        /// Validate a sphere overlap against an entity at a historical timestamp.
        /// </summary>
        public HitValidationResult ValidateSphereHit(uint targetNetworkId,
            Vector3 sphereCenter, float sphereRadius,
            NetworkTimestamp clientTimestamp)
        {
            var result = new HitValidationResult
            {
                targetNetworkId = targetNetworkId,
                hitPoint = sphereCenter,
                clientTimestamp = clientTimestamp,
                serverTimestamp = m_LastRecordedTimestamp
            };
            
            if (!TryGetStateAtTime(targetNetworkId, clientTimestamp, out var historicalState))
            {
                result.isValid = false;
                result.reason = HitRejectReason.EntityNotFound;
                OnHitValidated?.Invoke(result);
                return result;
            }
            
            if (!historicalState.isActive)
            {
                result.isValid = false;
                result.reason = HitRejectReason.EntityInactive;
                OnHitValidated?.Invoke(result);
                return result;
            }
            
            result.historicalPosition = historicalState.position;
            result.historicalBounds = historicalState.bounds;
            
            // Check sphere-bounds intersection
            Vector3 closestPoint = historicalState.bounds.ClosestPoint(sphereCenter);
            float distance = Vector3.Distance(closestPoint, sphereCenter);
            result.distanceFromBounds = Mathf.Max(0, distance - sphereRadius);
            
            float totalRadius = sphereRadius + m_Config.hitTolerance;
            if (distance <= totalRadius)
            {
                result.isValid = true;
                result.reason = HitRejectReason.None;
                result.hitPoint = closestPoint;
            }
            else
            {
                result.isValid = false;
                result.reason = HitRejectReason.OutOfRange;
            }
            
            OnHitValidated?.Invoke(result);
            return result;
        }
        
        // UTILITIES ──────────────────────────────────────────────────────────
        
        /// <summary>
        /// Get all registered entity IDs.
        /// </summary>
        public uint[] GetAllEntityIds()
        {
            lock (m_Lock)
            {
                var ids = new uint[m_Entities.Count];
                m_Entities.Keys.CopyTo(ids, 0);
                return ids;
            }
        }
        
        /// <summary>
        /// Get the history duration for an entity.
        /// </summary>
        public double GetHistoryDuration(uint networkId)
        {
            lock (m_Lock)
            {
                if (m_Entities.TryGetValue(networkId, out var tracked))
                {
                    return tracked.history.HistoryDuration;
                }
            }
            return 0;
        }
        
        /// <summary>
        /// Clear all history for all entities.
        /// </summary>
        public void ClearAllHistory()
        {
            lock (m_Lock)
            {
                foreach (var kvp in m_Entities)
                {
                    kvp.Value.history.Clear();
                }
            }
        }
        
        // CLEANUP ────────────────────────────────────────────────────────────
        
        /// <summary>
        /// Dispose of the manager and clear all data.
        /// </summary>
        public void Dispose()
        {
            if (m_Disposed) return;
            m_Disposed = true;
            
            lock (m_Lock)
            {
                m_Entities.Clear();
            }
            
            if (s_Instance == this)
            {
                s_Instance = null;
            }
        }
    }
    
    /// <summary>
    /// Result of a hit validation check.
    /// </summary>
    public struct HitValidationResult
    {
        /// <summary>Whether the hit was valid.</summary>
        public bool isValid;
        
        /// <summary>Reason for rejection (if invalid).</summary>
        public HitRejectReason reason;
        
        /// <summary>Target entity's network ID.</summary>
        public uint targetNetworkId;
        
        /// <summary>World-space hit point.</summary>
        public Vector3 hitPoint;
        
        /// <summary>Entity's position at the client timestamp.</summary>
        public Vector3 historicalPosition;
        
        /// <summary>Entity's bounds at the client timestamp.</summary>
        public Bounds historicalBounds;
        
        /// <summary>Distance from hit point to bounds (0 if inside).</summary>
        public float distanceFromBounds;
        
        /// <summary>Timestamp the client claimed to fire at.</summary>
        public NetworkTimestamp clientTimestamp;
        
        /// <summary>Current server timestamp when validation occurred.</summary>
        public NetworkTimestamp serverTimestamp;
        
        /// <summary>Hit zone name (if using ILagCompensatedWithHitZones).</summary>
        public string hitZoneName;
        
        /// <summary>Damage multiplier for hit zone (if applicable).</summary>
        public float damageMultiplier;
        
        /// <summary>How far in the past the client's timestamp was.</summary>
        public double RewindAmount => serverTimestamp.serverTime - clientTimestamp.serverTime;
        
        public override string ToString()
        {
            if (isValid)
                return $"[Hit VALID] Target:{targetNetworkId} Rewind:{RewindAmount:F3}s";
            else
                return $"[Hit INVALID] Target:{targetNetworkId} Reason:{reason} Distance:{distanceFromBounds:F2}m";
        }
    }
    
    /// <summary>
    /// Reasons why a hit validation failed.
    /// </summary>
    public enum HitRejectReason
    {
        /// <summary>Hit was valid (no rejection).</summary>
        None,
        
        /// <summary>Target entity not found in history.</summary>
        EntityNotFound,
        
        /// <summary>Target was inactive/dead at the time.</summary>
        EntityInactive,
        
        /// <summary>Hit point was outside tolerance range.</summary>
        OutOfRange,
        
        /// <summary>Raycast did not intersect target.</summary>
        RayMissed,
        
        /// <summary>Client timestamp was too far in the past.</summary>
        TimestampTooOld,
        
        /// <summary>Client timestamp was in the future (cheating?).</summary>
        TimestampInFuture,
        
        /// <summary>Shooter was not allowed to damage target.</summary>
        NotAllowed
    }
}
