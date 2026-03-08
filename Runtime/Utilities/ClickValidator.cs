using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace Arawn.GameCreator2.Networking
{
    /// <summary>
    /// Server-side validation for point-and-click movement commands.
    /// Provides anti-cheat measures for competitive networked games.
    /// </summary>
    [Serializable]
    public class ClickValidationConfig
    {
        [Tooltip("Maximum distance from character position to click (0 = unlimited)")]
        public float MaxClickDistance = 100f;
        
        [Tooltip("Maximum movement speed (m/s) to detect teleport hacks")]
        public float MaxSpeed = 20f;
        
        [Tooltip("Maximum clicks per second per client")]
        public float MaxClickRate = 15f;
        
        [Tooltip("Require click position to be on NavMesh")]
        public bool RequireNavMeshPosition = true;
        
        [Tooltip("NavMesh sampling distance")]
        public float NavMeshSampleDistance = 2f;
        
        [Tooltip("Validate path exists to destination")]
        public bool ValidatePath = true;
        
        [Tooltip("Maximum path distance multiplier (path length vs direct distance)")]
        public float MaxPathDistanceMultiplier = 3f;
        
        [Tooltip("Track suspicious activity")]
        public bool TrackViolations = true;
        
        [Tooltip("Number of violations before action")]
        public int ViolationThreshold = 5;
        
        /// <summary>Default config for casual games.</summary>
        public static ClickValidationConfig Casual => new ClickValidationConfig
        {
            MaxClickDistance = 0, // Unlimited
            MaxSpeed = 30f,
            MaxClickRate = 20f,
            RequireNavMeshPosition = false,
            ValidatePath = false,
            TrackViolations = false
        };
        
        /// <summary>Default config for competitive games.</summary>
        public static ClickValidationConfig Competitive => new ClickValidationConfig
        {
            MaxClickDistance = 100f,
            MaxSpeed = 15f,
            MaxClickRate = 10f,
            RequireNavMeshPosition = true,
            NavMeshSampleDistance = 1f,
            ValidatePath = true,
            MaxPathDistanceMultiplier = 2.5f,
            TrackViolations = true,
            ViolationThreshold = 3
        };
    }
    
    /// <summary>
    /// Server-side click command validator.
    /// Tracks client commands and detects suspicious behavior.
    /// </summary>
    public class ClickValidator
    {
        private readonly ClickValidationConfig m_Config;
        private readonly Dictionary<ulong, ClientValidationState> m_ClientStates;
        private readonly NavMeshPath m_ValidationPath;
        
        private class ClientValidationState
        {
            public Vector3 LastPosition;
            public float LastCommandTime;
            public float LastValidMoveTime;
            public int ViolationCount;
            public Queue<float> RecentClickTimes;
            
            public ClientValidationState()
            {
                RecentClickTimes = new Queue<float>(32);
            }
        }
        
        /// <summary>
        /// Validation result with details.
        /// </summary>
        public struct ValidationResult
        {
            public bool IsValid;
            public string RejectionReason;
            public Vector3 CorrectedPosition;
            public int TotalViolations;
            public bool ShouldKick;
            
            public static ValidationResult Valid(Vector3 position) => new ValidationResult
            {
                IsValid = true,
                CorrectedPosition = position
            };
            
            public static ValidationResult Invalid(string reason, int violations, bool kick = false) => new ValidationResult
            {
                IsValid = false,
                RejectionReason = reason,
                TotalViolations = violations,
                ShouldKick = kick
            };
            
            public static ValidationResult Corrected(Vector3 correctedPos, string reason) => new ValidationResult
            {
                IsValid = true,
                CorrectedPosition = correctedPos,
                RejectionReason = reason // Logged but not rejected
            };
        }
        
        // EVENTS: --------------------------------------------------------------------------------
        
        /// <summary>Fired when a client should be kicked for excessive violations.</summary>
        public event Action<ulong, string> OnShouldKickClient;
        
        /// <summary>Fired when a suspicious command is detected (for logging).</summary>
        public event Action<ulong, string, Vector3> OnSuspiciousCommand;
        
        // CONSTRUCTOR: ---------------------------------------------------------------------------
        
        public ClickValidator(ClickValidationConfig config = null)
        {
            m_Config = config ?? new ClickValidationConfig();
            m_ClientStates = new Dictionary<ulong, ClientValidationState>();
            m_ValidationPath = new NavMeshPath();
        }
        
        // PUBLIC METHODS: ------------------------------------------------------------------------
        
        /// <summary>
        /// Validate a click command from a client.
        /// </summary>
        /// <param name="clientId">Network client ID</param>
        /// <param name="characterPosition">Current character position</param>
        /// <param name="clickedPosition">Clicked destination</param>
        /// <returns>Validation result with optional corrected position</returns>
        public ValidationResult ValidateClick(ulong clientId, Vector3 characterPosition, Vector3 clickedPosition)
        {
            var state = GetOrCreateState(clientId);
            float currentTime = Time.time;
            
            // Rate limiting
            if (!ValidateClickRate(state, currentTime, out string rateError))
            {
                return HandleViolation(clientId, state, rateError, clickedPosition);
            }
            
            // Distance check
            if (m_Config.MaxClickDistance > 0)
            {
                float clickDistance = Vector3.Distance(characterPosition, clickedPosition);
                if (clickDistance > m_Config.MaxClickDistance)
                {
                    return HandleViolation(clientId, state, 
                        $"Click distance {clickDistance:F1}m exceeds max {m_Config.MaxClickDistance}m",
                        clickedPosition);
                }
            }
            
            // Speed check (teleport detection)
            if (state.LastValidMoveTime > 0)
            {
                float timeDelta = currentTime - state.LastValidMoveTime;
                if (timeDelta > 0.01f) // Avoid division by tiny numbers
                {
                    float distance = Vector3.Distance(state.LastPosition, characterPosition);
                    float speed = distance / timeDelta;
                    
                    if (speed > m_Config.MaxSpeed)
                    {
                        OnSuspiciousCommand?.Invoke(clientId, 
                            $"Suspicious speed: {speed:F1}m/s (max: {m_Config.MaxSpeed}m/s)",
                            characterPosition);
                        // Don't reject - just flag for monitoring
                    }
                }
            }
            
            // NavMesh validation
            Vector3 finalPosition = clickedPosition;
            if (m_Config.RequireNavMeshPosition)
            {
                if (NavMesh.SamplePosition(clickedPosition, out NavMeshHit hit, 
                    m_Config.NavMeshSampleDistance, NavMesh.AllAreas))
                {
                    // Snap to NavMesh
                    finalPosition = hit.position;
                }
                else
                {
                    return HandleViolation(clientId, state,
                        "Click position not on NavMesh",
                        clickedPosition);
                }
            }
            
            // Path validation
            if (m_Config.ValidatePath)
            {
                if (!ValidatePath(characterPosition, finalPosition, out string pathError))
                {
                    return HandleViolation(clientId, state, pathError, clickedPosition);
                }
            }
            
            // Valid command - update state
            state.LastPosition = characterPosition;
            state.LastCommandTime = currentTime;
            state.LastValidMoveTime = currentTime;
            state.RecentClickTimes.Enqueue(currentTime);
            
            // Different from clicked position? Return corrected
            if (finalPosition != clickedPosition)
            {
                return ValidationResult.Corrected(finalPosition, 
                    $"Snapped to NavMesh (offset: {Vector3.Distance(clickedPosition, finalPosition):F2}m)");
            }
            
            return ValidationResult.Valid(finalPosition);
        }
        
        /// <summary>
        /// Update client position for speed validation.
        /// Call this when character actually moves.
        /// </summary>
        public void UpdateClientPosition(ulong clientId, Vector3 position)
        {
            if (m_ClientStates.TryGetValue(clientId, out var state))
            {
                state.LastPosition = position;
            }
        }
        
        /// <summary>
        /// Clear client state when they disconnect.
        /// </summary>
        public void RemoveClient(ulong clientId)
        {
            m_ClientStates.Remove(clientId);
        }
        
        /// <summary>
        /// Get violation count for a client.
        /// </summary>
        public int GetViolationCount(ulong clientId)
        {
            return m_ClientStates.TryGetValue(clientId, out var state) ? state.ViolationCount : 0;
        }
        
        /// <summary>
        /// Reset violation count for a client (e.g., after timeout).
        /// </summary>
        public void ResetViolations(ulong clientId)
        {
            if (m_ClientStates.TryGetValue(clientId, out var state))
            {
                state.ViolationCount = 0;
            }
        }
        
        // PRIVATE METHODS: -----------------------------------------------------------------------
        
        private ClientValidationState GetOrCreateState(ulong clientId)
        {
            if (!m_ClientStates.TryGetValue(clientId, out var state))
            {
                state = new ClientValidationState();
                m_ClientStates[clientId] = state;
            }
            return state;
        }
        
        private bool ValidateClickRate(ClientValidationState state, float currentTime, out string error)
        {
            error = null;
            
            // Clean old clicks
            float cutoff = currentTime - 1f; // 1 second window
            while (state.RecentClickTimes.Count > 0 && state.RecentClickTimes.Peek() < cutoff)
            {
                state.RecentClickTimes.Dequeue();
            }
            
            // Check rate
            if (state.RecentClickTimes.Count >= m_Config.MaxClickRate)
            {
                error = $"Click rate exceeded ({state.RecentClickTimes.Count} in 1s, max: {m_Config.MaxClickRate})";
                return false;
            }
            
            return true;
        }
        
        private bool ValidatePath(Vector3 start, Vector3 end, out string error)
        {
            error = null;
            
            // Sample start position on NavMesh
            if (!NavMesh.SamplePosition(start, out NavMeshHit startHit, 2f, NavMesh.AllAreas))
            {
                error = "Character not on NavMesh";
                return false;
            }
            
            // Calculate path
            if (!NavMesh.CalculatePath(startHit.position, end, NavMesh.AllAreas, m_ValidationPath))
            {
                error = "No path exists to destination";
                return false;
            }
            
            // Check path status
            if (m_ValidationPath.status != NavMeshPathStatus.PathComplete)
            {
                error = $"Path incomplete: {m_ValidationPath.status}";
                return false;
            }
            
            // Check path length vs direct distance
            float directDistance = Vector3.Distance(start, end);
            float pathLength = CalculatePathLength(m_ValidationPath);
            
            if (directDistance > 0.1f && pathLength / directDistance > m_Config.MaxPathDistanceMultiplier)
            {
                error = $"Path too convoluted (length: {pathLength:F1}m, direct: {directDistance:F1}m)";
                return false;
            }
            
            return true;
        }
        
        private float CalculatePathLength(NavMeshPath path)
        {
            float length = 0;
            for (int i = 1; i < path.corners.Length; i++)
            {
                length += Vector3.Distance(path.corners[i - 1], path.corners[i]);
            }
            return length;
        }
        
        private ValidationResult HandleViolation(ulong clientId, ClientValidationState state, 
            string reason, Vector3 clickedPosition)
        {
            if (m_Config.TrackViolations)
            {
                state.ViolationCount++;
                
                OnSuspiciousCommand?.Invoke(clientId, reason, clickedPosition);
                
                if (state.ViolationCount >= m_Config.ViolationThreshold)
                {
                    OnShouldKickClient?.Invoke(clientId, 
                        $"Too many violations ({state.ViolationCount}): {reason}");
                    
                    return ValidationResult.Invalid(reason, state.ViolationCount, kick: true);
                }
            }
            
            return ValidationResult.Invalid(reason, state.ViolationCount);
        }
    }
    
    /// <summary>
    /// Extension for UnitDriverNavmeshNetworkServer to integrate click validation.
    /// </summary>
    public static class ClickValidationExtensions
    {
        /// <summary>
        /// Validate and optionally correct a move command on the server.
        /// </summary>
        public static bool TryValidateMove(this ClickValidator validator, 
            ulong clientId, Vector3 characterPos, Vector3 destination, 
            out Vector3 validatedDestination)
        {
            var result = validator.ValidateClick(clientId, characterPos, destination);
            
            validatedDestination = result.CorrectedPosition;
            return result.IsValid;
        }
    }
}
