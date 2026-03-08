using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using GameCreator.Runtime.Characters;

namespace Arawn.GameCreator2.Networking
{
    /// <summary>
    /// Server-side off-mesh link traversal synchronization.
    /// Detects and broadcasts link traversal events to clients.
    /// 
    /// Usage:
    /// 1. Add to NavMesh characters on server
    /// 2. Subscribe to events for network broadcast
    /// 3. Integrate with UnitDriverNavmeshNetworkServer
    /// </summary>
    [AddComponentMenu("Game Creator/Network/Off-Mesh Link Network Server")]
    public class OffMeshLinkNetworkServer : MonoBehaviour
    {
        // CONFIGURATION: -------------------------------------------------------------------------
        
        [Header("Configuration")]
        [SerializeField] private NetworkOffMeshLinkConfig m_Config = new NetworkOffMeshLinkConfig();
        
        [Header("Link Type Registry")]
        [Tooltip("Optional registry for custom link types")]
        [SerializeField] private NetworkOffMeshLinkRegistry m_Registry;
        
        [Header("Debug")]
        [SerializeField] private bool m_LogEvents = false;
        
        // EVENTS: --------------------------------------------------------------------------------
        
        /// <summary>Raised when agent starts traversing an off-mesh link</summary>
        public event Action<NetworkOffMeshLinkStart> OnLinkStartReady;
        
        /// <summary>Raised during traversal with progress updates</summary>
        public event Action<NetworkOffMeshLinkProgress> OnLinkProgressReady;
        
        /// <summary>Raised when agent finishes traversing</summary>
        public event Action<NetworkOffMeshLinkComplete> OnLinkCompleteReady;
        
        /// <summary>Raised when link has custom animation data to send</summary>
        public event Action<NetworkOffMeshLinkAnimation> OnLinkAnimationReady;
        
        // MEMBERS: -------------------------------------------------------------------------------
        
        private NavMeshAgent m_Agent;
        private Character m_Character;
        
        private ushort m_Sequence;
        private int m_CurrentLinkId;
        private bool m_IsTraversing;
        private float m_TraversalStartTime;
        private float m_TraversalDuration;
        private Vector3 m_TraversalStart;
        private Vector3 m_TraversalEnd;
        private float m_LastProgressSendTime;
        private float m_LastProgress;
        
        // Cached link data
        private OffMeshLinkData m_CurrentLinkData;
        private INavMeshTraverseLink m_CustomLink;
        private OffMeshLinkTypeEntry m_CurrentLinkType;
        
        // INITIALIZATION: ------------------------------------------------------------------------
        
        public void Initialize(Character character, NavMeshAgent agent)
        {
            m_Character = character;
            m_Agent = agent;
            m_Sequence = 0;
            m_IsTraversing = false;
        }
        
        // PUBLIC API: ----------------------------------------------------------------------------
        
        /// <summary>
        /// Check if agent is on an off-mesh link and handle traversal.
        /// Call this every frame from the driver's OnUpdate.
        /// </summary>
        /// <returns>True if currently traversing a link (caller should skip normal movement)</returns>
        public bool ProcessLinkTraversal()
        {
            if (m_Agent == null || !m_Agent.isOnNavMesh) return false;
            
            // Check if we're on an off-mesh link
            if (m_Agent.isOnOffMeshLink)
            {
                if (!m_IsTraversing)
                {
                    BeginTraversal();
                }
                else
                {
                    UpdateTraversal();
                }
                return true;
            }
            else if (m_IsTraversing)
            {
                // Link traversal was interrupted or completed externally
                CompleteTraversal(NetworkOffMeshLinkComplete.STATUS_INTERRUPTED);
            }
            
            return false;
        }
        
        /// <summary>
        /// Force complete the current traversal.
        /// </summary>
        public void ForceCompleteTraversal()
        {
            if (m_IsTraversing)
            {
                CompleteTraversal(NetworkOffMeshLinkComplete.STATUS_INTERRUPTED);
            }
        }
        
        /// <summary>
        /// Get the current traversal progress (0-1).
        /// </summary>
        public float GetProgress()
        {
            if (!m_IsTraversing || m_TraversalDuration <= 0f) return 0f;
            return Mathf.Clamp01((Time.time - m_TraversalStartTime) / m_TraversalDuration);
        }
        
        // TRAVERSAL HANDLING: --------------------------------------------------------------------
        
        private void BeginTraversal()
        {
            m_CurrentLinkData = m_Agent.currentOffMeshLinkData;
            m_CurrentLinkId = GetLinkId(m_CurrentLinkData);
            m_Sequence++;
            
            // Determine start and end positions
            m_TraversalStart = m_Agent.transform.position;
            m_TraversalEnd = m_CurrentLinkData.endPos;
            
            // Check for custom traversal handler
            m_CustomLink = m_CurrentLinkData.owner as INavMeshTraverseLink;
            
            // Determine traversal type and duration
            var traversalType = DetermineTraversalType();
            m_TraversalDuration = DetermineTraversalDuration(traversalType);
            m_CurrentLinkType = m_Registry != null ? m_Registry.GetEntry(traversalType) : null;
            
            // Determine flags
            bool ascending = m_TraversalEnd.y > m_TraversalStart.y;
            bool rootMotion = m_CurrentLinkType?.UseRootMotion ?? false;
            bool snapEnd = true;
            bool hasAnimation = m_CurrentLinkType?.AnimationClip != null && m_Config.SyncCustomCurves;
            
            m_IsTraversing = true;
            m_TraversalStartTime = Time.time;
            m_LastProgressSendTime = Time.time;
            m_LastProgress = 0f;
            
            // Stop agent movement
            m_Agent.isStopped = true;
            m_Agent.velocity = Vector3.zero;
            
            // Create and broadcast start message
            var startMsg = NetworkOffMeshLinkStart.Create(
                m_CurrentLinkId,
                m_Sequence,
                m_TraversalStart,
                m_TraversalEnd,
                m_TraversalDuration,
                traversalType,
                ascending,
                rootMotion,
                snapEnd,
                hasAnimation,
                Time.time
            );
            
            if (m_LogEvents)
            {
                Debug.Log($"[OffMeshLinkServer] Started traversal: {traversalType}, duration: {m_TraversalDuration:F2}s");
            }
            
            OnLinkStartReady?.Invoke(startMsg);
            
            // Send animation data if needed
            if (hasAnimation && m_CurrentLinkType != null)
            {
                var animHash = Animator.StringToHash(m_CurrentLinkType.AnimationClip.name);
                var animData = NetworkOffMeshLinkAnimation.CreateCustom(
                    animHash,
                    m_CurrentLinkType.AnimationSpeed,
                    m_CurrentLinkType.MovementCurve
                );
                OnLinkAnimationReady?.Invoke(animData);
            }
            
            // If using custom traversal, let it handle movement
            if (m_CustomLink != null)
            {
                m_CustomLink.Traverse(m_Character, OnCustomTraversalComplete);
            }
        }
        
        private void UpdateTraversal()
        {
            float currentProgress = GetProgress();
            
            // Send progress updates if enabled
            if (m_Config.SendProgressUpdates)
            {
                float timeSinceLastSend = Time.time - m_LastProgressSendTime;
                float progressDelta = currentProgress - m_LastProgress;
                
                if (timeSinceLastSend >= 1f / m_Config.ProgressSendRate ||
                    Mathf.Abs(progressDelta) >= m_Config.ProgressThreshold)
                {
                    var progressMsg = NetworkOffMeshLinkProgress.Create(m_CurrentLinkId, currentProgress);
                    OnLinkProgressReady?.Invoke(progressMsg);
                    
                    m_LastProgressSendTime = Time.time;
                    m_LastProgress = currentProgress;
                }
            }
            
            // Handle auto-traverse (non-custom links)
            if (m_CustomLink == null)
            {
                // Move agent along path
                float curvedProgress = m_CurrentLinkType?.MovementCurve.Evaluate(currentProgress) ?? currentProgress;
                Vector3 targetPos = Vector3.Lerp(m_TraversalStart, m_TraversalEnd, curvedProgress);
                
                // Apply arc for jump-type traversals
                if (m_CurrentLinkType != null && 
                    (m_CurrentLinkType.TraversalType == OffMeshLinkTraversalType.Jump ||
                     m_CurrentLinkType.TraversalType == OffMeshLinkTraversalType.Vault))
                {
                    float arcOffset = 4f * m_CurrentLinkType.ArcHeight * currentProgress * (1f - currentProgress);
                    targetPos.y += arcOffset;
                }
                
                // Move to position
                Vector3 delta = targetPos - m_Agent.transform.position;
                m_Agent.Move(delta);
                
                // Check for completion
                if (currentProgress >= 1f)
                {
                    CompleteTraversal(NetworkOffMeshLinkComplete.STATUS_SUCCESS);
                }
            }
        }
        
        private void OnCustomTraversalComplete()
        {
            CompleteTraversal(NetworkOffMeshLinkComplete.STATUS_SUCCESS);
        }
        
        private void CompleteTraversal(byte status)
        {
            if (!m_IsTraversing) return;
            
            m_IsTraversing = false;
            
            // Restore agent
            m_Agent.updatePosition = true;
            m_Agent.isStopped = false;
            m_Agent.autoRepath = true;
            
            if (status == NetworkOffMeshLinkComplete.STATUS_SUCCESS && m_Agent.isOnOffMeshLink)
            {
                m_Agent.CompleteOffMeshLink();
            }
            
            // Broadcast completion
            var completeMsg = NetworkOffMeshLinkComplete.Create(
                m_CurrentLinkId,
                m_Sequence,
                m_Agent.transform.position,
                status
            );
            
            if (m_LogEvents)
            {
                Debug.Log($"[OffMeshLinkServer] Completed traversal: status={status}");
            }
            
            OnLinkCompleteReady?.Invoke(completeMsg);
            
            // Clean up
            m_CustomLink = null;
            m_CurrentLinkType = null;
        }
        
        // HELPER METHODS: ------------------------------------------------------------------------
        
        private int GetLinkId(OffMeshLinkData linkData)
        {
            // Use link owner's instance ID as unique identifier
            if (linkData.owner != null)
            {
                return linkData.owner.GetInstanceID();
            }
            
            // Fallback: hash start and end positions
            return HashCode.Combine(
                Mathf.RoundToInt(linkData.startPos.x * 100f),
                Mathf.RoundToInt(linkData.startPos.z * 100f),
                Mathf.RoundToInt(linkData.endPos.x * 100f),
                Mathf.RoundToInt(linkData.endPos.z * 100f)
            );
        }
        
        private OffMeshLinkTraversalType DetermineTraversalType()
        {
            // Check custom link component for type info
            if (m_CurrentLinkData.owner is MonoBehaviour linkMono)
            {
#if ENEMYMASSES
                // Check for EnemyMasses climbing system
                var climbable = linkMono.GetComponent<Navigation.Climbing.NavMeshLinkClimbable>();
                if (climbable != null)
                {
                    return OffMeshLinkTraversalType.Climb;
                }
#endif
                
                // Could check for other custom link types here
            }
            
            // Infer from height difference
            float heightDiff = m_TraversalEnd.y - m_TraversalStart.y;
            float horizontalDist = Vector3.Distance(
                new Vector3(m_TraversalStart.x, 0, m_TraversalStart.z),
                new Vector3(m_TraversalEnd.x, 0, m_TraversalEnd.z)
            );
            
            // Large drop
            if (heightDiff < -2f && horizontalDist < Mathf.Abs(heightDiff))
            {
                return OffMeshLinkTraversalType.Drop;
            }
            
            // Large climb
            if (heightDiff > 2f && horizontalDist < heightDiff)
            {
                return OffMeshLinkTraversalType.Climb;
            }
            
            // Jump (gap crossing)
            if (horizontalDist > 1.5f)
            {
                return OffMeshLinkTraversalType.Jump;
            }
            
            // Short vault
            if (Mathf.Abs(heightDiff) < 1f && horizontalDist < 1.5f)
            {
                return OffMeshLinkTraversalType.Vault;
            }
            
            // Default to auto
            return OffMeshLinkTraversalType.Auto;
        }
        
        private float DetermineTraversalDuration(OffMeshLinkTraversalType type)
        {
            // Check registry first
            if (m_CurrentLinkType != null)
            {
                return m_CurrentLinkType.DefaultDuration;
            }
            
            // Calculate based on distance and type
            float distance = Vector3.Distance(m_TraversalStart, m_TraversalEnd);
            
            switch (type)
            {
                case OffMeshLinkTraversalType.Teleport:
                    return 0.1f;
                    
                case OffMeshLinkTraversalType.Jump:
                case OffMeshLinkTraversalType.Vault:
                    return Mathf.Max(0.3f, distance / 5f); // Fast
                    
                case OffMeshLinkTraversalType.Drop:
                    return Mathf.Max(0.2f, distance / 8f); // Faster (gravity)
                    
                case OffMeshLinkTraversalType.Climb:
                case OffMeshLinkTraversalType.Ladder:
                    return Mathf.Max(1f, distance / 2f); // Slow
                    
                case OffMeshLinkTraversalType.Crawl:
                case OffMeshLinkTraversalType.Swim:
                    return Mathf.Max(0.5f, distance / 3f); // Medium
                    
                default:
                    return Mathf.Max(0.5f, distance / 4f);
            }
        }
    }
    
    /// <summary>
    /// Client-side off-mesh link traversal synchronization.
    /// Receives server broadcasts and animates traversal locally.
    /// 
    /// Usage:
    /// 1. Add to NavMesh characters on client
    /// 2. Feed server messages via Apply* methods
    /// 3. Call ProcessTraversal each frame for movement
    /// </summary>
    [AddComponentMenu("Game Creator/Network/Off-Mesh Link Network Client")]
    public class OffMeshLinkNetworkClient : MonoBehaviour
    {
        // CONFIGURATION: -------------------------------------------------------------------------
        
        [Header("Configuration")]
        [SerializeField] private NetworkOffMeshLinkConfig m_Config = new NetworkOffMeshLinkConfig();
        
        [Header("Link Type Registry")]
        [Tooltip("Optional registry for animation lookup")]
        [SerializeField] private NetworkOffMeshLinkRegistry m_Registry;
        
        [Header("Debug")]
        [SerializeField] private bool m_LogEvents = false;
        
        // EVENTS: --------------------------------------------------------------------------------
        
        /// <summary>Raised when traversal starts (for animation triggers)</summary>
        public event Action<OffMeshLinkTraversalType, bool> OnTraversalStarted;
        
        /// <summary>Raised when traversal completes</summary>
        public event Action<bool> OnTraversalCompleted;
        
        // MEMBERS: -------------------------------------------------------------------------------
        
        private Character m_Character;
        private Transform m_Transform;
        
        private Dictionary<int, OffMeshLinkTraversalState> m_ActiveTraversals = new Dictionary<int, OffMeshLinkTraversalState>();
        private OffMeshLinkTraversalState m_CurrentTraversal;
        private NetworkOffMeshLinkAnimation? m_PendingAnimation;
        
        // INITIALIZATION: ------------------------------------------------------------------------
        
        public void Initialize(Character character)
        {
            m_Character = character;
            m_Transform = character.transform;
        }
        
        // PUBLIC API: ----------------------------------------------------------------------------
        
        /// <summary>
        /// Apply a traversal start message from server.
        /// </summary>
        public void ApplyLinkStart(NetworkOffMeshLinkStart startMsg)
        {
            var state = new OffMeshLinkTraversalState
            {
                LinkId = startMsg.LinkId,
                Sequence = startMsg.Sequence,
                StartPosition = startMsg.GetStartPosition(),
                EndPosition = startMsg.GetEndPosition(),
                Duration = startMsg.GetDuration(),
                StartTime = Time.time,
                ServerStartTime = startMsg.ServerTime,
                TraversalType = startMsg.GetTraversalType(),
                IsAscending = startMsg.IsAscending,
                UsesRootMotion = startMsg.UsesRootMotion,
                SnapToEnd = startMsg.SnapToEnd,
                CurrentProgress = 0f,
                LastProgressUpdateTime = Time.time,
                LastServerProgress = 0f,
                IsComplete = false
            };
            
            // Wait for animation data if flagged
            if (startMsg.HasAnimation)
            {
                m_PendingAnimation = null; // Will be set by ApplyLinkAnimation
            }
            
            m_ActiveTraversals[startMsg.LinkId] = state;
            m_CurrentTraversal = state;
            
            // Check if we need to snap to start position
            float distToStart = Vector3.Distance(m_Transform.position, state.StartPosition);
            if (distToStart > m_Config.SnapThreshold)
            {
                m_Transform.position = state.StartPosition;
            }
            
            if (m_LogEvents)
            {
                Debug.Log($"[OffMeshLinkClient] Started: {state.TraversalType}, duration={state.Duration:F2}s");
            }
            
            OnTraversalStarted?.Invoke(state.TraversalType, state.IsAscending);
        }
        
        /// <summary>
        /// Apply animation data from server.
        /// </summary>
        public void ApplyLinkAnimation(NetworkOffMeshLinkAnimation animData)
        {
            if (m_CurrentTraversal != null)
            {
                m_CurrentTraversal.Animation = animData;
            }
            else
            {
                m_PendingAnimation = animData;
            }
        }
        
        /// <summary>
        /// Apply a progress update from server.
        /// </summary>
        public void ApplyLinkProgress(NetworkOffMeshLinkProgress progressMsg)
        {
            if (m_ActiveTraversals.TryGetValue(progressMsg.LinkId, out var state))
            {
                state.LastServerProgress = progressMsg.GetProgress();
                state.LastProgressUpdateTime = Time.time;
            }
        }
        
        /// <summary>
        /// Apply a traversal completion message from server.
        /// </summary>
        public void ApplyLinkComplete(NetworkOffMeshLinkComplete completeMsg)
        {
            if (m_ActiveTraversals.TryGetValue(completeMsg.LinkId, out var state))
            {
                state.IsComplete = true;
                state.CurrentProgress = 1f;
                
                // Snap to final position if requested
                if (state.SnapToEnd)
                {
                    m_Transform.position = completeMsg.GetFinalPosition();
                }
                
                m_ActiveTraversals.Remove(completeMsg.LinkId);
                
                if (m_CurrentTraversal?.LinkId == completeMsg.LinkId)
                {
                    m_CurrentTraversal = null;
                }
                
                if (m_LogEvents)
                {
                    Debug.Log($"[OffMeshLinkClient] Completed: success={completeMsg.IsSuccess}");
                }
                
                OnTraversalCompleted?.Invoke(completeMsg.IsSuccess);
            }
        }
        
        /// <summary>
        /// Process current traversal and update position.
        /// Call this every frame from the driver's OnUpdate.
        /// </summary>
        /// <returns>True if currently traversing (caller should skip normal movement)</returns>
        public bool ProcessTraversal()
        {
            if (m_CurrentTraversal == null || m_CurrentTraversal.IsComplete)
            {
                return false;
            }
            
            // Update progress
            float targetProgress;
            if (m_Config.SendProgressUpdates && m_CurrentTraversal.LastServerProgress > 0f)
            {
                // Interpolate from server progress
                targetProgress = m_CurrentTraversal.InterpolateProgress(
                    Time.time, 
                    m_Config.InterpolationBuffer
                );
            }
            else
            {
                // Calculate from time
                targetProgress = m_CurrentTraversal.GetExpectedProgress(Time.time);
            }
            
            // Clamp extrapolation
            float maxProgress = Mathf.Min(1f, targetProgress);
            if (Time.time - m_CurrentTraversal.LastProgressUpdateTime > m_Config.MaxExtrapolationTime)
            {
                maxProgress = Mathf.Min(maxProgress, m_CurrentTraversal.CurrentProgress + 0.1f);
            }
            
            m_CurrentTraversal.CurrentProgress = Mathf.Clamp01(maxProgress);
            
            // Calculate position
            Vector3 targetPosition;
            var type = m_CurrentTraversal.TraversalType;
            
            if (type == OffMeshLinkTraversalType.Jump || type == OffMeshLinkTraversalType.Vault)
            {
                // Get arc height from registry or calculate
                float arcHeight = 1f;
                var entry = m_Registry?.GetEntry(type);
                if (entry != null) arcHeight = entry.ArcHeight;
                
                targetPosition = m_CurrentTraversal.GetPositionWithArc(
                    m_CurrentTraversal.CurrentProgress, 
                    arcHeight
                );
            }
            else
            {
                targetPosition = m_CurrentTraversal.GetPosition(m_CurrentTraversal.CurrentProgress);
            }
            
            // Update transform
            if (!m_CurrentTraversal.UsesRootMotion)
            {
                m_Transform.position = targetPosition;
            }
            
            // Face movement direction
            Vector3 direction = m_CurrentTraversal.EndPosition - m_CurrentTraversal.StartPosition;
            direction.y = 0;
            if (direction.sqrMagnitude > 0.01f)
            {
                m_Transform.rotation = Quaternion.LookRotation(direction.normalized);
            }
            
            // Check for time-based completion (server complete message should arrive)
            if (m_CurrentTraversal.CurrentProgress >= 1f)
            {
                // Don't remove yet - wait for server complete message
            }
            
            return true;
        }
        
        /// <summary>
        /// Check if currently traversing a link.
        /// </summary>
        public bool IsTraversing => m_CurrentTraversal != null && !m_CurrentTraversal.IsComplete;
        
        /// <summary>
        /// Get current traversal progress (0-1).
        /// </summary>
        public float GetProgress() => m_CurrentTraversal?.CurrentProgress ?? 0f;
        
        /// <summary>
        /// Get current traversal type.
        /// </summary>
        public OffMeshLinkTraversalType? GetTraversalType()
        {
            return m_CurrentTraversal?.TraversalType;
        }
        
        // GIZMOS: --------------------------------------------------------------------------------
        
        private void OnDrawGizmos()
        {
            if (m_CurrentTraversal == null || m_CurrentTraversal.IsComplete) return;
            
            Gizmos.color = Color.cyan;
            Gizmos.DrawLine(m_CurrentTraversal.StartPosition, m_CurrentTraversal.EndPosition);
            
            Gizmos.color = Color.green;
            Gizmos.DrawWireSphere(m_CurrentTraversal.GetPosition(m_CurrentTraversal.CurrentProgress), 0.2f);
            
            Gizmos.color = Color.yellow;
            Gizmos.DrawWireSphere(m_CurrentTraversal.EndPosition, 0.3f);
        }
    }
}
