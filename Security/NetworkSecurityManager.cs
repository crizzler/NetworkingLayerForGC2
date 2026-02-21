using System;
using System.Collections.Generic;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Security
{
    /// <summary>
    /// Central security manager for all GC2 networking modules.
    /// Provides unified rate limiting, violation tracking, and state validation.
    /// </summary>
    [AddComponentMenu("Game Creator/Network/Security/Network Security Manager")]
    public class NetworkSecurityManager : MonoBehaviour
    {
        // ════════════════════════════════════════════════════════════════════════════════════════
        // SINGLETON
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        private static NetworkSecurityManager s_Instance;
        public static NetworkSecurityManager Instance => s_Instance;
        public static bool HasInstance => s_Instance != null;
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // INSPECTOR
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        [Header("Global Security Settings")]
        [SerializeField] private NetworkSecurityConfig m_Config = new();
        
        [Header("Module-Specific Overrides")]
        [SerializeField] private bool m_UseModuleOverrides = false;
        [SerializeField] private NetworkSecurityConfig m_CoreConfig;
        [SerializeField] private NetworkSecurityConfig m_StatsConfig;
        [SerializeField] private NetworkSecurityConfig m_InventoryConfig;
        [SerializeField] private NetworkSecurityConfig m_MeleeConfig;
        [SerializeField] private NetworkSecurityConfig m_ShooterConfig;
        [SerializeField] private NetworkSecurityConfig m_AbilitiesConfig;
        
        [Header("Debug")]
        [SerializeField] private bool m_LogViolations = true;
        [SerializeField] private bool m_LogRateLimits = false;
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // EVENTS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Called when a security violation is detected.
        /// </summary>
        public event Action<SecurityViolationEvent> OnViolationDetected;
        
        /// <summary>
        /// Called when a client exceeds violation threshold.
        /// </summary>
        public event Action<uint, SecurityViolationType> OnThresholdExceeded;
        
        /// <summary>
        /// Called when a client is temporarily blocked.
        /// </summary>
        public event Action<uint, float> OnClientBlocked;
        
        /// <summary>
        /// Custom action delegate for ViolationAction.Custom.
        /// </summary>
        public Action<SecurityViolationEvent> CustomViolationAction;
        
        /// <summary>
        /// Delegate to kick a client (must be set by network layer).
        /// </summary>
        public Action<uint, string> KickClient;
        
        /// <summary>
        /// Delegate to send warning to client.
        /// </summary>
        public Action<uint, string> SendWarningToClient;
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // PRIVATE FIELDS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        private bool m_IsServer;
        private Func<float> m_GetServerTime;
        
        // Per-module rate limiters
        private readonly Dictionary<string, RateLimiter> m_RateLimiters = new(8);
        
        // Global violation tracker
        private ViolationTracker m_ViolationTracker;
        
        // Sequence tracker for replay prevention
        private readonly SequenceTracker m_SequenceTracker = new();
        
        // Statistics
        private SecurityStats m_Stats;
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // PROPERTIES
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        public NetworkSecurityConfig GlobalConfig => m_Config;
        public SecurityStats Stats => m_Stats;
        public bool IsServer => m_IsServer;
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // UNITY LIFECYCLE
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        private void Awake()
        {
            if (s_Instance != null && s_Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            
            s_Instance = this;
            
            // Initialize violation tracker
            m_ViolationTracker = new ViolationTracker(
                m_Config.ViolationThreshold,
                m_Config.ViolationWindow
            );
        }
        
        private void OnDestroy()
        {
            if (s_Instance == this)
            {
                s_Instance = null;
            }
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // INITIALIZATION
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Initialize the security manager.
        /// </summary>
        /// <param name="isServer">Whether this is the server.</param>
        /// <param name="getServerTime">Function to get current server time.</param>
        public void Initialize(bool isServer, Func<float> getServerTime)
        {
            m_IsServer = isServer;
            m_GetServerTime = getServerTime;
            
            // Create rate limiters for each module
            CreateRateLimiter("Core");
            CreateRateLimiter("Stats");
            CreateRateLimiter("Inventory");
            CreateRateLimiter("Melee");
            CreateRateLimiter("Shooter");
            CreateRateLimiter("Abilities");
            
            m_Stats.Reset();
            
            Debug.Log($"[NetworkSecurity] Initialized - Server: {isServer}");
        }
        
        private void CreateRateLimiter(string module)
        {
            var config = GetConfigForModule(module);
            m_RateLimiters[module] = new RateLimiter(
                config.MaxRequestsPerSecond,
                config.RateLimitWindow
            );
        }
        
        /// <summary>
        /// Get security config for a specific module.
        /// </summary>
        public NetworkSecurityConfig GetConfigForModule(string module)
        {
            if (!m_UseModuleOverrides)
                return m_Config;
            
            return module switch
            {
                "Core" => m_CoreConfig ?? m_Config,
                "Stats" => m_StatsConfig ?? m_Config,
                "Inventory" => m_InventoryConfig ?? m_Config,
                "Melee" => m_MeleeConfig ?? m_Config,
                "Shooter" => m_ShooterConfig ?? m_Config,
                "Abilities" => m_AbilitiesConfig ?? m_Config,
                _ => m_Config
            };
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // RATE LIMITING
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Check if a request from a client should be allowed (rate limiting).
        /// </summary>
        /// <param name="clientId">The client's network ID.</param>
        /// <param name="module">The module making the request.</param>
        /// <returns>True if allowed, false if rate limited.</returns>
        public bool CheckRateLimit(uint clientId, string module)
        {
            if (!m_IsServer) return true;
            
            var config = GetConfigForModule(module);
            if (!config.EnableRateLimiting) return true;
            
            float currentTime = m_GetServerTime?.Invoke() ?? Time.time;
            
            // Check if client is blocked
            if (m_ViolationTracker.IsBlocked(clientId, currentTime))
            {
                m_Stats.BlockedRequests++;
                return false;
            }
            
            // Check rate limit
            if (!m_RateLimiters.TryGetValue(module, out var limiter))
            {
                return true;
            }
            
            bool allowed = limiter.TryRequest(clientId, currentTime);
            
            if (!allowed)
            {
                m_Stats.RateLimitedRequests++;
                
                if (m_LogRateLimits)
                {
                    Debug.LogWarning($"[NetworkSecurity] Rate limit exceeded - Client: {clientId}, Module: {module}");
                }
                
                // Record as violation
                RecordViolation(clientId, 0, SecurityViolationType.RateLimitExceeded, module, 
                    $"Exceeded {config.MaxRequestsPerSecond}/sec");
            }
            
            return allowed;
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // VIOLATION TRACKING
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Record a security violation.
        /// </summary>
        public void RecordViolation(
            uint clientId, 
            uint characterNetworkId,
            SecurityViolationType type, 
            string module, 
            string details)
        {
            if (!m_IsServer) return;
            
            float currentTime = m_GetServerTime?.Invoke() ?? Time.time;
            var config = GetConfigForModule(module);
            
            if (!config.EnableAnomalyDetection) return;
            
            bool thresholdExceeded = m_ViolationTracker.RecordViolation(clientId, type, details, currentTime);
            int violationCount = m_ViolationTracker.GetViolationCount(clientId, currentTime);
            
            m_Stats.TotalViolations++;
            
            var violationEvent = new SecurityViolationEvent
            {
                ClientId = clientId,
                CharacterNetworkId = characterNetworkId,
                ViolationType = type,
                Module = module,
                Details = details,
                ServerTime = currentTime,
                ViolationCount = violationCount
            };
            
            // Log violation
            if (m_LogViolations)
            {
                Debug.LogWarning($"[NetworkSecurity] Violation: {type} - Client: {clientId}, Module: {module}, Details: {details}, Count: {violationCount}/{config.ViolationThreshold}");
            }
            
            OnViolationDetected?.Invoke(violationEvent);
            
            // Handle threshold exceeded
            if (thresholdExceeded)
            {
                HandleThresholdExceeded(clientId, violationEvent, config);
            }
        }
        
        private void HandleThresholdExceeded(uint clientId, SecurityViolationEvent evt, NetworkSecurityConfig config)
        {
            m_Stats.ThresholdsExceeded++;
            OnThresholdExceeded?.Invoke(clientId, evt.ViolationType);
            
            switch (config.ViolationAction)
            {
                case SecurityViolationAction.LogOnly:
                    Debug.LogError($"[NetworkSecurity] THRESHOLD EXCEEDED - Client: {clientId}, Type: {evt.ViolationType}");
                    break;
                    
                case SecurityViolationAction.LogAndWarn:
                    Debug.LogError($"[NetworkSecurity] THRESHOLD EXCEEDED - Client: {clientId}, Type: {evt.ViolationType}");
                    SendWarningToClient?.Invoke(clientId, $"Security warning: {evt.ViolationType}");
                    break;
                    
                case SecurityViolationAction.TempBlock:
                    float currentTime = m_GetServerTime?.Invoke() ?? Time.time;
                    m_ViolationTracker.BlockClient(clientId, config.TempBlockDuration, currentTime);
                    m_Stats.ClientsBlocked++;
                    Debug.LogError($"[NetworkSecurity] CLIENT BLOCKED - Client: {clientId}, Duration: {config.TempBlockDuration}s");
                    OnClientBlocked?.Invoke(clientId, config.TempBlockDuration);
                    break;
                    
                case SecurityViolationAction.Kick:
                    m_Stats.ClientsKicked++;
                    Debug.LogError($"[NetworkSecurity] KICKING CLIENT - Client: {clientId}, Reason: {evt.ViolationType}");
                    KickClient?.Invoke(clientId, $"Security violation: {evt.ViolationType}");
                    break;
                    
                case SecurityViolationAction.Custom:
                    CustomViolationAction?.Invoke(evt);
                    break;
            }
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // SEQUENCE VALIDATION
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Validate a request sequence number for replay attack prevention.
        /// </summary>
        public bool ValidateSequence(uint clientId, ushort requestId, string module)
        {
            if (!m_IsServer) return true;
            
            if (!m_SequenceTracker.ValidateSequence(clientId, requestId))
            {
                RecordViolation(clientId, 0, SecurityViolationType.ReplayAttack, module,
                    $"Duplicate request ID: {requestId}");
                return false;
            }
            
            return true;
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // VALUE VALIDATION
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Validate a numeric value is within expected bounds.
        /// </summary>
        public bool ValidateValueRange(uint clientId, string module, string valueName, 
            float value, float min, float max)
        {
            if (!m_IsServer) return true;
            
            if (value < min || value > max)
            {
                RecordViolation(clientId, 0, SecurityViolationType.OutOfBoundsValue, module,
                    $"{valueName}: {value} (expected {min}-{max})");
                return false;
            }
            
            return true;
        }
        
        /// <summary>
        /// Validate a position is within reasonable bounds of the world.
        /// </summary>
        public bool ValidatePosition(uint clientId, string module, Vector3 position, 
            float maxDistance = 10000f)
        {
            if (!m_IsServer) return true;
            
            if (float.IsNaN(position.x) || float.IsNaN(position.y) || float.IsNaN(position.z) ||
                float.IsInfinity(position.x) || float.IsInfinity(position.y) || float.IsInfinity(position.z))
            {
                RecordViolation(clientId, 0, SecurityViolationType.OutOfBoundsValue, module,
                    $"Invalid position: {position}");
                return false;
            }
            
            if (position.magnitude > maxDistance)
            {
                RecordViolation(clientId, 0, SecurityViolationType.OutOfBoundsValue, module,
                    $"Position too far: {position.magnitude}m");
                return false;
            }
            
            return true;
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // CLIENT MANAGEMENT
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Clear all tracking data for a client (call on disconnect).
        /// </summary>
        public void OnClientDisconnected(uint clientId)
        {
            foreach (var limiter in m_RateLimiters.Values)
            {
                limiter.ClearClient(clientId);
            }
            
            m_ViolationTracker.ClearClient(clientId);
            m_SequenceTracker.ClearClient(clientId);
        }
        
        /// <summary>
        /// Check if a client is currently blocked.
        /// </summary>
        public bool IsClientBlocked(uint clientId)
        {
            if (!m_IsServer) return false;
            
            float currentTime = m_GetServerTime?.Invoke() ?? Time.time;
            return m_ViolationTracker.IsBlocked(clientId, currentTime);
        }
        
        /// <summary>
        /// Manually block a client.
        /// </summary>
        public void BlockClient(uint clientId, float duration)
        {
            if (!m_IsServer) return;
            
            float currentTime = m_GetServerTime?.Invoke() ?? Time.time;
            m_ViolationTracker.BlockClient(clientId, duration, currentTime);
            m_Stats.ClientsBlocked++;
            OnClientBlocked?.Invoke(clientId, duration);
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // CLEANUP
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Clear all security tracking data.
        /// </summary>
        public void Clear()
        {
            foreach (var limiter in m_RateLimiters.Values)
            {
                limiter.Clear();
            }
            
            m_ViolationTracker.Clear();
            m_SequenceTracker.Clear();
        }
    }
    
    /// <summary>
    /// Statistics for security operations.
    /// </summary>
    [Serializable]
    public struct SecurityStats
    {
        public int TotalViolations;
        public int RateLimitedRequests;
        public int BlockedRequests;
        public int ThresholdsExceeded;
        public int ClientsBlocked;
        public int ClientsKicked;
        
        public void Reset()
        {
            TotalViolations = 0;
            RateLimitedRequests = 0;
            BlockedRequests = 0;
            ThresholdsExceeded = 0;
            ClientsBlocked = 0;
            ClientsKicked = 0;
        }
    }
}
