using System;
using System.Collections.Generic;
using UnityEngine;

namespace Arawn.NetworkingCore.LagCompensation
{
    /// <summary>
    /// Stores position/rotation history for a single entity.
    /// Used by the server to validate hits at historical positions.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is a circular buffer that stores snapshots at regular intervals.
    /// When the buffer is full, old snapshots are overwritten.
    /// </para>
    /// <para>
    /// Typical usage: Store 1 second of history at 60Hz = 60 snapshots.
    /// This allows validation of hits from clients with up to 500ms ping.
    /// </para>
    /// </remarks>
    public class LagCompensationHistory
    {
        // STRUCTS ────────────────────────────────────────────────────────────
        
        /// <summary>
        /// A single snapshot of entity state.
        /// </summary>
        public struct StateSnapshot
        {
            public NetworkTimestamp timestamp;
            public Vector3 position;
            public Quaternion rotation;
            public Bounds bounds;
            public bool isActive;
            
            /// <summary>
            /// Check if a point is within this snapshot's bounds.
            /// </summary>
            public bool ContainsPoint(Vector3 point, float tolerance = 0f)
            {
                if (!isActive) return false;
                
                Bounds expanded = bounds;
                expanded.Expand(tolerance * 2f);
                return expanded.Contains(point);
            }
            
            /// <summary>
            /// Get distance from a point to the nearest point on bounds.
            /// </summary>
            public float DistanceToPoint(Vector3 point)
            {
                return Vector3.Distance(point, bounds.ClosestPoint(point));
            }
        }
        
        // FIELDS ─────────────────────────────────────────────────────────────
        
        private readonly StateSnapshot[] m_Buffer;
        private readonly int m_Capacity;
        private int m_WriteIndex;
        private int m_Count;
        private readonly object m_Lock = new object();
        
        // PROPERTIES ─────────────────────────────────────────────────────────
        
        /// <summary>
        /// Number of snapshots currently stored.
        /// </summary>
        public int Count => m_Count;
        
        /// <summary>
        /// Maximum number of snapshots that can be stored.
        /// </summary>
        public int Capacity => m_Capacity;
        
        /// <summary>
        /// Oldest timestamp in the buffer (if any snapshots exist).
        /// </summary>
        public NetworkTimestamp? OldestTimestamp
        {
            get
            {
                lock (m_Lock)
                {
                    if (m_Count == 0) return null;
                    int oldestIndex = (m_WriteIndex - m_Count + m_Capacity) % m_Capacity;
                    return m_Buffer[oldestIndex].timestamp;
                }
            }
        }
        
        /// <summary>
        /// Newest timestamp in the buffer (if any snapshots exist).
        /// </summary>
        public NetworkTimestamp? NewestTimestamp
        {
            get
            {
                lock (m_Lock)
                {
                    if (m_Count == 0) return null;
                    int newestIndex = (m_WriteIndex - 1 + m_Capacity) % m_Capacity;
                    return m_Buffer[newestIndex].timestamp;
                }
            }
        }
        
        /// <summary>
        /// Duration of history stored (newest - oldest).
        /// </summary>
        public double HistoryDuration
        {
            get
            {
                var oldest = OldestTimestamp;
                var newest = NewestTimestamp;
                if (!oldest.HasValue || !newest.HasValue) return 0;
                return newest.Value.serverTime - oldest.Value.serverTime;
            }
        }
        
        // CONSTRUCTOR ────────────────────────────────────────────────────────
        
        /// <summary>
        /// Creates a new history buffer with the specified capacity.
        /// </summary>
        /// <param name="capacity">Maximum number of snapshots to store.</param>
        public LagCompensationHistory(int capacity = 64)
        {
            m_Capacity = Mathf.Max(2, capacity);
            m_Buffer = new StateSnapshot[m_Capacity];
            m_WriteIndex = 0;
            m_Count = 0;
        }
        
        // PUBLIC METHODS ─────────────────────────────────────────────────────
        
        /// <summary>
        /// Record a new snapshot from an ILagCompensated entity.
        /// </summary>
        public void RecordSnapshot(ILagCompensated entity, NetworkTimestamp timestamp)
        {
            RecordSnapshot(
                timestamp,
                entity.Position,
                entity.Rotation,
                entity.Bounds,
                entity.IsActive
            );
        }
        
        /// <summary>
        /// Record a new snapshot with explicit values.
        /// </summary>
        public void RecordSnapshot(NetworkTimestamp timestamp, Vector3 position, 
            Quaternion rotation, Bounds bounds, bool isActive)
        {
            lock (m_Lock)
            {
                m_Buffer[m_WriteIndex] = new StateSnapshot
                {
                    timestamp = timestamp,
                    position = position,
                    rotation = rotation,
                    bounds = bounds,
                    isActive = isActive
                };
                
                m_WriteIndex = (m_WriteIndex + 1) % m_Capacity;
                m_Count = Mathf.Min(m_Count + 1, m_Capacity);
            }
        }
        
        /// <summary>
        /// Try to get the interpolated state at a specific timestamp.
        /// </summary>
        /// <param name="timestamp">The timestamp to query.</param>
        /// <param name="snapshot">The interpolated snapshot if found.</param>
        /// <returns>True if the timestamp is within the stored history range.</returns>
        public bool TryGetStateAt(NetworkTimestamp timestamp, out StateSnapshot snapshot)
        {
            lock (m_Lock)
            {
                snapshot = default;
                
                if (m_Count == 0) return false;
                
                // Find the two snapshots surrounding the target timestamp
                StateSnapshot? before = null;
                StateSnapshot? after = null;
                
                for (int i = 0; i < m_Count; i++)
                {
                    int index = (m_WriteIndex - m_Count + i + m_Capacity) % m_Capacity;
                    var current = m_Buffer[index];
                    
                    if (current.timestamp <= timestamp)
                    {
                        before = current;
                    }
                    else if (after == null)
                    {
                        after = current;
                        break;
                    }
                }
                
                // Exact match or extrapolation cases
                if (before == null && after == null)
                {
                    return false;
                }
                
                if (before == null)
                {
                    // Timestamp is before our history - return oldest
                    snapshot = after.Value;
                    return true;
                }
                
                if (after == null)
                {
                    // Timestamp is after our history - return newest
                    snapshot = before.Value;
                    return true;
                }
                
                // Interpolate between before and after
                double range = after.Value.timestamp.serverTime - before.Value.timestamp.serverTime;
                if (range < 0.0001)
                {
                    snapshot = before.Value;
                    return true;
                }
                
                float t = (float)((timestamp.serverTime - before.Value.timestamp.serverTime) / range);
                t = Mathf.Clamp01(t);
                
                snapshot = new StateSnapshot
                {
                    timestamp = timestamp,
                    position = Vector3.Lerp(before.Value.position, after.Value.position, t),
                    rotation = Quaternion.Slerp(before.Value.rotation, after.Value.rotation, t),
                    bounds = LerpBounds(before.Value.bounds, after.Value.bounds, t),
                    isActive = before.Value.isActive && after.Value.isActive
                };
                
                return true;
            }
        }
        
        /// <summary>
        /// Try to get the position at a specific timestamp.
        /// </summary>
        public bool TryGetPositionAt(NetworkTimestamp timestamp, out Vector3 position)
        {
            if (TryGetStateAt(timestamp, out var snapshot))
            {
                position = snapshot.position;
                return true;
            }
            position = Vector3.zero;
            return false;
        }
        
        /// <summary>
        /// Try to get the bounds at a specific timestamp.
        /// </summary>
        public bool TryGetBoundsAt(NetworkTimestamp timestamp, out Bounds bounds)
        {
            if (TryGetStateAt(timestamp, out var snapshot))
            {
                bounds = snapshot.bounds;
                return true;
            }
            bounds = default;
            return false;
        }
        
        /// <summary>
        /// Check if a point was inside the entity's bounds at a specific timestamp.
        /// </summary>
        /// <param name="timestamp">When to check.</param>
        /// <param name="point">World-space point to test.</param>
        /// <param name="tolerance">Extra tolerance in meters.</param>
        public bool WasPointInside(NetworkTimestamp timestamp, Vector3 point, float tolerance = 0f)
        {
            if (TryGetStateAt(timestamp, out var snapshot))
            {
                return snapshot.ContainsPoint(point, tolerance);
            }
            return false;
        }
        
        /// <summary>
        /// Get all snapshots in time order (oldest first).
        /// </summary>
        public StateSnapshot[] GetAllSnapshots()
        {
            lock (m_Lock)
            {
                var result = new StateSnapshot[m_Count];
                for (int i = 0; i < m_Count; i++)
                {
                    int index = (m_WriteIndex - m_Count + i + m_Capacity) % m_Capacity;
                    result[i] = m_Buffer[index];
                }
                return result;
            }
        }
        
        /// <summary>
        /// Clear all stored history.
        /// </summary>
        public void Clear()
        {
            lock (m_Lock)
            {
                m_WriteIndex = 0;
                m_Count = 0;
            }
        }
        
        // PRIVATE HELPERS ────────────────────────────────────────────────────
        
        private static Bounds LerpBounds(Bounds a, Bounds b, float t)
        {
            return new Bounds(
                Vector3.Lerp(a.center, b.center, t),
                Vector3.Lerp(a.size, b.size, t)
            );
        }
    }
    
    /// <summary>
    /// Configuration for lag compensation history.
    /// </summary>
    [Serializable]
    public class LagCompensationConfig
    {
        [Tooltip("How many snapshots to store per entity")]
        [Range(16, 256)]
        public int historySize = 64;
        
        [Tooltip("How often to record snapshots (Hz)")]
        [Range(10, 120)]
        public int snapshotRate = 60;
        
        [Tooltip("Maximum age of snapshots to consider (seconds)")]
        [Range(0.1f, 2f)]
        public float maxHistoryAge = 1f;
        
        [Tooltip("Extra tolerance when validating hits (meters)")]
        [Range(0f, 1f)]
        public float hitTolerance = 0.2f;
        
        [Tooltip("Maximum allowed client timestamp in the past (seconds)")]
        [Range(0.1f, 1f)]
        public float maxRewindTime = 0.5f;
        
        /// <summary>
        /// Calculate required buffer size for the configured history age.
        /// </summary>
        public int CalculateRequiredBufferSize()
        {
            return Mathf.CeilToInt(maxHistoryAge * snapshotRate) + 1;
        }
    }
}
