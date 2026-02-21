using System;
using System.Collections.Generic;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Security
{
    /// <summary>
    /// Configuration for server-side security validation.
    /// </summary>
    [Serializable]
    public class NetworkSecurityConfig
    {
        [Header("State Validation")]
        [Tooltip("Enable periodic state validation checks.")]
        public bool EnableStateValidation = true;
        
        [Tooltip("Interval between state validation checks (seconds).")]
        [Range(0.5f, 10f)]
        public float StateValidationInterval = 2f;
        
        [Tooltip("Maximum allowed state discrepancy before triggering warning.")]
        public float StateDiscrepancyThreshold = 0.1f;
        
        [Header("Rate Limiting")]
        [Tooltip("Enable request rate limiting.")]
        public bool EnableRateLimiting = true;
        
        [Tooltip("Maximum requests per second per client.")]
        [Range(1, 100)]
        public int MaxRequestsPerSecond = 30;
        
        [Tooltip("Time window for rate limiting (seconds).")]
        [Range(0.5f, 5f)]
        public float RateLimitWindow = 1f;
        
        [Header("Anomaly Detection")]
        [Tooltip("Enable anomaly detection and logging.")]
        public bool EnableAnomalyDetection = true;
        
        [Tooltip("Number of violations before triggering action.")]
        [Range(1, 20)]
        public int ViolationThreshold = 5;
        
        [Tooltip("Time window to accumulate violations (seconds).")]
        [Range(10f, 300f)]
        public float ViolationWindow = 60f;
        
        [Header("Response Actions")]
        [Tooltip("Action to take when violations exceed threshold.")]
        public SecurityViolationAction ViolationAction = SecurityViolationAction.LogAndWarn;
        
        [Tooltip("Duration to temporarily block a client (seconds).")]
        [Range(5f, 300f)]
        public float TempBlockDuration = 30f;
        
        [Header("Value Validation")]
        [Tooltip("Maximum allowed value change per request (0 = no limit).")]
        public float MaxValueChangePerRequest = 0f;
        
        [Tooltip("Maximum allowed position change per update (0 = no limit).")]
        public float MaxPositionDelta = 0f;
    }
    
    /// <summary>
    /// Actions to take when security violations are detected.
    /// </summary>
    public enum SecurityViolationAction
    {
        /// <summary>Log only, no action.</summary>
        LogOnly,
        
        /// <summary>Log and send warning to client.</summary>
        LogAndWarn,
        
        /// <summary>Temporarily block requests from client.</summary>
        TempBlock,
        
        /// <summary>Kick client from server.</summary>
        Kick,
        
        /// <summary>Custom action via delegate.</summary>
        Custom
    }
    
    /// <summary>
    /// Types of security violations.
    /// </summary>
    public enum SecurityViolationType
    {
        None,
        RateLimitExceeded,
        StateDiscrepancy,
        InvalidRequest,
        UnauthorizedAction,
        SuspiciousPattern,
        ReplayAttack,
        OutOfBoundsValue
    }
    
    /// <summary>
    /// Security violation event data.
    /// </summary>
    public struct SecurityViolationEvent
    {
        public uint ClientId;
        public uint CharacterNetworkId;
        public SecurityViolationType ViolationType;
        public string Module;
        public string Details;
        public float ServerTime;
        public int ViolationCount;
    }
    
    /// <summary>
    /// Rate limiter for tracking request frequency.
    /// </summary>
    public class RateLimiter
    {
        private readonly int m_MaxRequests;
        private readonly float m_WindowSize;
        private readonly Dictionary<uint, Queue<float>> m_RequestTimestamps = new(64);
        
        public RateLimiter(int maxRequests, float windowSize)
        {
            m_MaxRequests = maxRequests;
            m_WindowSize = windowSize;
        }
        
        /// <summary>
        /// Check if a request from the given client should be allowed.
        /// </summary>
        /// <returns>True if request is allowed, false if rate limited.</returns>
        public bool TryRequest(uint clientId, float currentTime)
        {
            if (!m_RequestTimestamps.TryGetValue(clientId, out var timestamps))
            {
                timestamps = new Queue<float>(m_MaxRequests + 1);
                m_RequestTimestamps[clientId] = timestamps;
            }
            
            // Remove old timestamps outside the window
            float windowStart = currentTime - m_WindowSize;
            while (timestamps.Count > 0 && timestamps.Peek() < windowStart)
            {
                timestamps.Dequeue();
            }
            
            // Check if under limit
            if (timestamps.Count >= m_MaxRequests)
            {
                return false;
            }
            
            // Add current timestamp
            timestamps.Enqueue(currentTime);
            return true;
        }
        
        /// <summary>
        /// Get current request count for a client.
        /// </summary>
        public int GetRequestCount(uint clientId, float currentTime)
        {
            if (!m_RequestTimestamps.TryGetValue(clientId, out var timestamps))
                return 0;
            
            float windowStart = currentTime - m_WindowSize;
            int count = 0;
            foreach (var ts in timestamps)
            {
                if (ts >= windowStart) count++;
            }
            return count;
        }
        
        /// <summary>
        /// Clear data for a client (on disconnect).
        /// </summary>
        public void ClearClient(uint clientId)
        {
            m_RequestTimestamps.Remove(clientId);
        }
        
        /// <summary>
        /// Clear all data.
        /// </summary>
        public void Clear()
        {
            m_RequestTimestamps.Clear();
        }
    }
    
    /// <summary>
    /// Tracks security violations per client.
    /// </summary>
    public class ViolationTracker
    {
        private readonly int m_Threshold;
        private readonly float m_WindowSize;
        private readonly Dictionary<uint, List<ViolationRecord>> m_Violations = new(64);
        private readonly HashSet<uint> m_BlockedClients = new(16);
        private readonly Dictionary<uint, float> m_BlockExpiry = new(16);
        
        public ViolationTracker(int threshold, float windowSize)
        {
            m_Threshold = threshold;
            m_WindowSize = windowSize;
        }
        
        /// <summary>
        /// Record a violation for a client.
        /// </summary>
        /// <returns>True if threshold exceeded.</returns>
        public bool RecordViolation(uint clientId, SecurityViolationType type, string details, float currentTime)
        {
            if (!m_Violations.TryGetValue(clientId, out var violations))
            {
                violations = new List<ViolationRecord>(m_Threshold + 1);
                m_Violations[clientId] = violations;
            }
            
            // Remove old violations
            float windowStart = currentTime - m_WindowSize;
            violations.RemoveAll(v => v.Time < windowStart);
            
            // Add new violation
            violations.Add(new ViolationRecord
            {
                Type = type,
                Details = details,
                Time = currentTime
            });
            
            return violations.Count >= m_Threshold;
        }
        
        /// <summary>
        /// Get current violation count for a client.
        /// </summary>
        public int GetViolationCount(uint clientId, float currentTime)
        {
            if (!m_Violations.TryGetValue(clientId, out var violations))
                return 0;
            
            float windowStart = currentTime - m_WindowSize;
            int count = 0;
            foreach (var v in violations)
            {
                if (v.Time >= windowStart) count++;
            }
            return count;
        }
        
        /// <summary>
        /// Block a client temporarily.
        /// </summary>
        public void BlockClient(uint clientId, float duration, float currentTime)
        {
            m_BlockedClients.Add(clientId);
            m_BlockExpiry[clientId] = currentTime + duration;
        }
        
        /// <summary>
        /// Check if a client is blocked.
        /// </summary>
        public bool IsBlocked(uint clientId, float currentTime)
        {
            if (!m_BlockedClients.Contains(clientId))
                return false;
            
            if (m_BlockExpiry.TryGetValue(clientId, out float expiry) && currentTime >= expiry)
            {
                m_BlockedClients.Remove(clientId);
                m_BlockExpiry.Remove(clientId);
                return false;
            }
            
            return true;
        }
        
        /// <summary>
        /// Clear data for a client.
        /// </summary>
        public void ClearClient(uint clientId)
        {
            m_Violations.Remove(clientId);
            m_BlockedClients.Remove(clientId);
            m_BlockExpiry.Remove(clientId);
        }
        
        /// <summary>
        /// Clear all data.
        /// </summary>
        public void Clear()
        {
            m_Violations.Clear();
            m_BlockedClients.Clear();
            m_BlockExpiry.Clear();
        }
        
        private struct ViolationRecord
        {
            public SecurityViolationType Type;
            public string Details;
            public float Time;
        }
    }
    
    /// <summary>
    /// Tracks server-authoritative state for validation.
    /// </summary>
    public class ServerStateTracker<TKey, TState> where TState : struct
    {
        private readonly Dictionary<TKey, TState> m_States = new(64);
        private readonly Dictionary<TKey, float> m_LastUpdate = new(64);
        
        /// <summary>
        /// Set the authoritative state for a key.
        /// </summary>
        public void SetState(TKey key, TState state, float currentTime)
        {
            m_States[key] = state;
            m_LastUpdate[key] = currentTime;
        }
        
        /// <summary>
        /// Get the authoritative state for a key.
        /// </summary>
        public bool TryGetState(TKey key, out TState state)
        {
            return m_States.TryGetValue(key, out state);
        }
        
        /// <summary>
        /// Check if state exists for a key.
        /// </summary>
        public bool HasState(TKey key)
        {
            return m_States.ContainsKey(key);
        }
        
        /// <summary>
        /// Remove state for a key.
        /// </summary>
        public void RemoveState(TKey key)
        {
            m_States.Remove(key);
            m_LastUpdate.Remove(key);
        }
        
        /// <summary>
        /// Get all keys.
        /// </summary>
        public IEnumerable<TKey> Keys => m_States.Keys;
        
        /// <summary>
        /// Clear all states.
        /// </summary>
        public void Clear()
        {
            m_States.Clear();
            m_LastUpdate.Clear();
        }
    }
    
    /// <summary>
    /// Sequence number tracker for replay attack prevention.
    /// </summary>
    public class SequenceTracker
    {
        private readonly Dictionary<uint, ushort> m_LastSequence = new(64);
        private readonly Dictionary<uint, HashSet<ushort>> m_RecentSequences = new(64);
        private const int MAX_RECENT = 32;
        
        /// <summary>
        /// Validate a sequence number from a client.
        /// </summary>
        /// <returns>True if valid (not a replay), false if replay detected.</returns>
        public bool ValidateSequence(uint clientId, ushort sequence)
        {
            if (!m_RecentSequences.TryGetValue(clientId, out var recent))
            {
                recent = new HashSet<ushort>(MAX_RECENT);
                m_RecentSequences[clientId] = recent;
            }
            
            // Check if this sequence was recently used (replay)
            if (recent.Contains(sequence))
            {
                return false;
            }
            
            // Add to recent
            recent.Add(sequence);
            
            // Prune old sequences if too many
            if (recent.Count > MAX_RECENT)
            {
                // Keep sequences near the current one
                var toRemove = new List<ushort>();
                foreach (var s in recent)
                {
                    // Simple distance check (handles wraparound)
                    int dist = Math.Abs(sequence - s);
                    if (dist > MAX_RECENT / 2 && dist < 65536 - MAX_RECENT / 2)
                    {
                        toRemove.Add(s);
                    }
                }
                foreach (var s in toRemove)
                {
                    recent.Remove(s);
                }
            }
            
            m_LastSequence[clientId] = sequence;
            return true;
        }
        
        /// <summary>
        /// Clear data for a client.
        /// </summary>
        public void ClearClient(uint clientId)
        {
            m_LastSequence.Remove(clientId);
            m_RecentSequences.Remove(clientId);
        }
        
        /// <summary>
        /// Clear all data.
        /// </summary>
        public void Clear()
        {
            m_LastSequence.Clear();
            m_RecentSequences.Clear();
        }
    }
}
