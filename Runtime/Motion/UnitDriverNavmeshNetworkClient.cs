using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.Characters;

namespace Arawn.GameCreator2.Networking
{
    /// <summary>
    /// Client-side NavMesh driver for networked characters.
    /// Follows server-provided paths with smooth interpolation.
    /// 
    /// Client responsibilities:
    /// - Send movement commands to server (MoveToPosition, MoveToDirection, Stop)
    /// - Receive path updates from server
    /// - Interpolate between server position updates
    /// - Follow path corners for smooth movement
    /// </summary>
    [Title("NavMesh Agent Network (Client)")]
    [Image(typeof(IconCharacterWalk), ColorTheme.Type.Yellow, typeof(OverlayArrowRight))]
    [Category("NavMesh Agent Network (Client)")]
    [Description("Client-side NavMesh driver. Sends commands to server and follows server-provided paths.")]
    [Serializable]
    public class UnitDriverNavmeshNetworkClient : TUnitDriver
    {
        // EXPOSED MEMBERS: -----------------------------------------------------------------------
        
        [Header("Network Settings")]
        [SerializeField] private NetworkNavMeshConfig m_Config = NetworkNavMeshConfig.Default;
        
        [Header("Local Movement (Optional)")]
        [Tooltip("Enable local NavMesh agent for prediction (requires NavMesh on client)")]
        [SerializeField] private bool m_EnableLocalNavMesh = false;
        
        [Header("Player Prediction")]
        [Tooltip("Enable client-side path prediction for responsive click-to-move. Requires NavMesh baked on client.")]
        [SerializeField] private bool m_EnableLocalPrediction = false;
        
        [Tooltip("Distance threshold to reconcile predicted path with server path")]
        [SerializeField] private float m_ReconciliationThreshold = 0.5f;

        // MEMBERS: -------------------------------------------------------------------------------

        [NonSerialized] protected NavMeshAgent m_Agent;
        [NonSerialized] protected CapsuleCollider m_Capsule;
        [NonSerialized] protected Vector3 m_MoveDirection;
        [NonSerialized] protected Vector3 m_Velocity;
        
        // Network state
        [NonSerialized] private ushort m_CurrentSequence;
        [NonSerialized] private ushort m_LastAcknowledgedSequence;
        
        // Path following
        [NonSerialized] private Vector3[] m_ServerPathCorners;
        [NonSerialized] private int m_CurrentCornerIndex;
        [NonSerialized] private byte m_PathStatus;
        
        // Interpolation
        [NonSerialized] private List<PositionSnapshot> m_SnapshotBuffer;
        [NonSerialized] private float m_InterpolationTime;
        [NonSerialized] private bool m_IsExtrapolating;
        [NonSerialized] private float m_ExtrapolationTime;
        
        // Movement mode
        [NonSerialized] private Vector3 m_DirectionInput;
        [NonSerialized] private bool m_IsDirectionMode;
        
        // Client-side prediction
        [NonSerialized] private Vector3[] m_PredictedPathCorners;
        [NonSerialized] private int m_PredictedCornerIndex;
        [NonSerialized] private bool m_IsPredicting;
        [NonSerialized] private ushort m_PredictedSequence;
        [NonSerialized] private NavMeshPath m_PredictionPath;
        
        // Off-mesh link handling
        [NonSerialized] private OffMeshLinkNetworkClient m_LinkController;

        // EVENTS: --------------------------------------------------------------------------------
        
        /// <summary>
        /// Raised when a command should be sent to server.
        /// </summary>
        public event Action<NetworkNavMeshCommand> OnSendCommand;

        // INTERFACE PROPERTIES: ------------------------------------------------------------------

        public override Vector3 WorldMoveDirection => this.m_Velocity;
        public override Vector3 LocalMoveDirection => this.Transform.InverseTransformDirection(this.WorldMoveDirection);
        public override float SkinWidth => 0.08f;
        
        public override bool IsGrounded => 
            this.m_ForceGrounded || 
            (m_EnableLocalNavMesh && m_Agent != null && m_Agent.isOnNavMesh) ||
            !m_EnableLocalNavMesh; // Assume grounded if no local NavMesh
        
        public override Vector3 FloorNormal => Vector3.up;
        
        public override bool Collision
        {
            get => this.m_Capsule != null && this.m_Capsule.enabled;
            set { if (this.m_Capsule != null) this.m_Capsule.enabled = value; }
        }
        
        public override Axonometry Axonometry
        {
            get => null;
            set => _ = value;
        }
        
        public NetworkNavMeshConfig Config => m_Config;
        public ushort CurrentSequence => m_CurrentSequence;
        public bool HasPath => m_ServerPathCorners != null && m_ServerPathCorners.Length > 0;
        public Vector3[] CurrentPath => m_ServerPathCorners ?? Array.Empty<Vector3>();
        public bool IsExtrapolating => m_IsExtrapolating;
        public bool IsPredicting => m_IsPredicting;
        
        /// <summary>
        /// Enable/disable client-side prediction at runtime.
        /// Requires NavMesh to be baked on client.
        /// </summary>
        public bool EnableLocalPrediction
        {
            get => m_EnableLocalPrediction;
            set
            {
                m_EnableLocalPrediction = value;
                if (value && m_Agent == null && m_EnableLocalNavMesh)
                {
                    SetupLocalNavMesh();
                }
            }
        }
        
        /// <summary>
        /// Distance threshold for reconciling predicted vs server path.
        /// </summary>
        public float ReconciliationThreshold
        {
            get => m_ReconciliationThreshold;
            set => m_ReconciliationThreshold = value;
        }

        // STRUCTS: -------------------------------------------------------------------------------

        private struct PositionSnapshot
        {
            public float timestamp;
            public Vector3 position;
            public float rotationY;
            public int cornerIndex;
            public float speedPercent;
        }

        // INITIALIZERS: --------------------------------------------------------------------------

        public UnitDriverNavmeshNetworkClient()
        {
            this.m_MoveDirection = Vector3.zero;
        }

        public override void OnStartup(Character character)
        {
            base.OnStartup(character);
            
            m_SnapshotBuffer = new List<PositionSnapshot>(32);
            m_CurrentSequence = 0;
            m_LastAcknowledgedSequence = 0;
            m_ServerPathCorners = null;
            m_CurrentCornerIndex = 0;

            if (m_EnableLocalNavMesh)
            {
                SetupLocalNavMesh();
            }

            // Setup capsule for collision
            this.m_Capsule = this.Character.GetComponent<CapsuleCollider>();
            if (this.m_Capsule == null)
            {
                this.m_Capsule = this.Character.gameObject.AddComponent<CapsuleCollider>();
                this.m_Capsule.hideFlags = HideFlags.HideInInspector;
            }
            
            // Initialize off-mesh link controller
            m_LinkController = this.Character.GetComponent<OffMeshLinkNetworkClient>();
            if (m_LinkController == null)
            {
                m_LinkController = this.Character.gameObject.AddComponent<OffMeshLinkNetworkClient>();
            }
            m_LinkController.Initialize(this.Character);
        }

        private void SetupLocalNavMesh()
        {
            this.m_Agent = this.Character.GetComponent<NavMeshAgent>();
            if (this.m_Agent == null)
            {
                this.m_Agent = this.Character.gameObject.AddComponent<NavMeshAgent>();
                this.m_Agent.hideFlags = HideFlags.HideInInspector;
            }

            // Client agent setup for prediction
            this.m_Agent.updatePosition = false; // We control position
            this.m_Agent.updateRotation = false;
            this.m_Agent.updateUpAxis = false;
            this.m_Agent.autoBraking = false;
            this.m_Agent.autoRepath = false;
            
            // Initialize prediction path
            m_PredictionPath = new NavMeshPath();
        }

        public override void OnDispose(Character character)
        {
            base.OnDispose(character);
            if (this.m_Agent != null) UnityEngine.Object.Destroy(this.m_Agent);
            if (this.m_Capsule != null) UnityEngine.Object.Destroy(this.m_Capsule);
        }

        // PUBLIC COMMAND METHODS: ----------------------------------------------------------------
        
        /// <summary>
        /// Request to move to a specific position (pathfinding on server).
        /// If prediction is enabled, also calculates local path immediately.
        /// </summary>
        public void RequestMoveToPosition(Vector3 target)
        {
            m_DirectionInput = Vector3.zero;
            m_IsDirectionMode = false;
            m_CurrentSequence++;
            var command = NetworkNavMeshCommand.CreateMoveToPosition(target, m_CurrentSequence);
            
            // Client-side prediction: calculate path locally for immediate feedback
            if (m_EnableLocalPrediction && m_EnableLocalNavMesh && m_Agent != null && m_PredictionPath != null)
            {
                if (NavMesh.CalculatePath(this.Transform.position, target, NavMesh.AllAreas, m_PredictionPath))
                {
                    if (m_PredictionPath.status != NavMeshPathStatus.PathInvalid)
                    {
                        m_PredictedPathCorners = m_PredictionPath.corners;
                        m_PredictedCornerIndex = 0;
                        m_PredictedSequence = m_CurrentSequence;
                        m_IsPredicting = true;
                    }
                }
            }
            
            OnSendCommand?.Invoke(command);
        }
        
        /// <summary>
        /// Request to move in a direction (no pathfinding).
        /// </summary>
        public void RequestMoveToDirection(Vector3 direction)
        {
            m_DirectionInput = direction.normalized;
            m_IsDirectionMode = true;

            m_CurrentSequence++;
            var command = NetworkNavMeshCommand.CreateMoveToDirection(direction, m_CurrentSequence);
            OnSendCommand?.Invoke(command);
        }
        
        /// <summary>
        /// Request to stop movement.
        /// </summary>
        public void RequestStop(bool immediate = false)
        {
            ClearMovementState();

            m_CurrentSequence++;
            var command = NetworkNavMeshCommand.CreateStop(m_CurrentSequence, immediate);
            OnSendCommand?.Invoke(command);
        }
        
        /// <summary>
        /// Request to warp/teleport to a position.
        /// </summary>
        public void RequestWarp(Vector3 position)
        {
            m_CurrentSequence++;
            var command = NetworkNavMeshCommand.CreateWarp(position, m_CurrentSequence);
            OnSendCommand?.Invoke(command);
        }

        // SERVER STATE APPLICATION: --------------------------------------------------------------
        
        /// <summary>
        /// Apply path state received from server.
        /// Handles reconciliation with predicted path if prediction is enabled.
        /// </summary>
        public void ApplyPathState(NetworkNavMeshPathState pathState)
        {
            m_LastAcknowledgedSequence = pathState.CommandSequence;
            m_PathStatus = pathState.PathStatus;
            
            // Handle prediction reconciliation
            if (m_IsPredicting && pathState.CommandSequence == m_PredictedSequence)
            {
                // Server confirmed our predicted command - check if paths match
                var serverCorners = pathState.GetCorners();
                bool needsReconciliation = ShouldReconcilePath(serverCorners);
                
                if (!needsReconciliation)
                {
                    // Prediction was correct - continue using predicted path
                    // Just update server corners for reference
                    m_ServerPathCorners = serverCorners;
                    return;
                }
                
                // Prediction was wrong - switch to server path
                m_IsPredicting = false;
                m_PredictedPathCorners = null;
            }
            else if (m_IsPredicting && IsSequenceNewer(pathState.CommandSequence, m_PredictedSequence))
            {
                // Newer server state received - stop predicting
                m_IsPredicting = false;
                m_PredictedPathCorners = null;
            }
            
            // Update path corners from server
            m_ServerPathCorners = pathState.GetCorners();
            m_CurrentCornerIndex = 0;
            
            // Add position snapshot
            AddSnapshot(new PositionSnapshot
            {
                timestamp = Time.time,
                position = pathState.GetPosition(),
                rotationY = pathState.GetRotationY(),
                cornerIndex = 0,
                speedPercent = 1f
            });
            
            // Teleport if too far
            float distance = Vector3.Distance(this.Transform.position, pathState.GetPosition());
            if (distance > m_Config.TeleportThreshold)
            {
                TeleportTo(pathState.GetPosition(), pathState.GetRotationY());
            }
        }
        
        /// <summary>
        /// Apply position update received from server.
        /// </summary>
        public void ApplyPositionUpdate(NetworkNavMeshPositionUpdate update)
        {
            AddSnapshot(new PositionSnapshot
            {
                timestamp = Time.time,
                position = update.GetPosition(),
                rotationY = update.GetRotationY(),
                cornerIndex = update.CurrentCornerIndex,
                speedPercent = update.GetSpeedPercent()
            });
            
            m_CurrentCornerIndex = update.CurrentCornerIndex;
            
            // Teleport if too far
            float distance = Vector3.Distance(this.Transform.position, update.GetPosition());
            if (distance > m_Config.TeleportThreshold)
            {
                TeleportTo(update.GetPosition(), update.GetRotationY());
            }
        }
        
        // OFF-MESH LINK APPLICATION: -------------------------------------------------------------
        
        /// <summary>
        /// Apply off-mesh link start message from server.
        /// </summary>
        public void ApplyLinkStart(NetworkOffMeshLinkStart startMsg)
        {
            m_LinkController?.ApplyLinkStart(startMsg);
        }
        
        /// <summary>
        /// Apply off-mesh link animation data from server.
        /// </summary>
        public void ApplyLinkAnimation(NetworkOffMeshLinkAnimation animData)
        {
            m_LinkController?.ApplyLinkAnimation(animData);
        }
        
        /// <summary>
        /// Apply off-mesh link progress update from server.
        /// </summary>
        public void ApplyLinkProgress(NetworkOffMeshLinkProgress progressMsg)
        {
            m_LinkController?.ApplyLinkProgress(progressMsg);
        }
        
        /// <summary>
        /// Apply off-mesh link completion from server.
        /// </summary>
        public void ApplyLinkComplete(NetworkOffMeshLinkComplete completeMsg)
        {
            m_LinkController?.ApplyLinkComplete(completeMsg);
        }
        
        /// <summary>
        /// Check if currently traversing an off-mesh link.
        /// </summary>
        public bool IsTraversingLink => m_LinkController?.IsTraversing ?? false;

        private void AddSnapshot(PositionSnapshot snapshot)
        {
            m_SnapshotBuffer.Add(snapshot);
            
            // Trim old snapshots
            float cutoff = Time.time - 1f; // Keep 1 second of history
            m_SnapshotBuffer.RemoveAll(s => s.timestamp < cutoff);
            
            m_IsExtrapolating = false;
            m_ExtrapolationTime = 0f;
        }

        private void ClearMovementState()
        {
            m_DirectionInput = Vector3.zero;
            m_IsDirectionMode = false;
            m_IsPredicting = false;
            m_PredictedPathCorners = null;
            m_PredictedCornerIndex = 0;
            m_ServerPathCorners = null;
            m_CurrentCornerIndex = 0;
            m_PathStatus = NetworkNavMeshPathState.STATUS_NONE;
            m_Velocity = Vector3.zero;
            m_MoveDirection = Vector3.zero;
            m_IsExtrapolating = false;
            m_ExtrapolationTime = 0f;
            m_SnapshotBuffer?.Clear();

            if (m_EnableLocalNavMesh && m_Agent != null && m_Agent.enabled && m_Agent.isOnNavMesh)
            {
                m_Agent.isStopped = true;
                m_Agent.velocity = Vector3.zero;
                m_Agent.ResetPath();
            }
        }
        
        /// <summary>
        /// Check if predicted path differs significantly from server path.
        /// </summary>
        private bool ShouldReconcilePath(Vector3[] serverCorners)
        {
            if (m_PredictedPathCorners == null || serverCorners == null)
                return true;
            
            // Compare final destinations
            if (m_PredictedPathCorners.Length == 0 || serverCorners.Length == 0)
                return true;
            
            Vector3 predictedEnd = m_PredictedPathCorners[m_PredictedPathCorners.Length - 1];
            Vector3 serverEnd = serverCorners[serverCorners.Length - 1];
            
            if (Vector3.Distance(predictedEnd, serverEnd) > m_ReconciliationThreshold)
                return true;
            
            // Compare current position on path
            if (m_PredictedCornerIndex < m_PredictedPathCorners.Length)
            {
                Vector3 predictedNext = m_PredictedPathCorners[m_PredictedCornerIndex];
                
                // Find closest server corner
                float minDist = float.MaxValue;
                foreach (var corner in serverCorners)
                {
                    float dist = Vector3.Distance(predictedNext, corner);
                    if (dist < minDist) minDist = dist;
                }
                
                if (minDist > m_ReconciliationThreshold)
                    return true;
            }
            
            return false;
        }
        
        private static bool IsSequenceNewer(ushort a, ushort b)
        {
            return (short)(a - b) > 0;
        }

        // UPDATE METHODS: ------------------------------------------------------------------------

        public override void OnUpdate()
        {
            if (this.Character.IsDead) return;
            
            // Handle off-mesh link traversal
            if (m_LinkController != null && m_LinkController.ProcessTraversal())
            {
                // Link controller is handling movement
                return;
            }
            
            // Update capsule properties
            UpdateCapsule(this.Character.Motion);
            
            // Client-side prediction takes priority for local player
            if (m_IsPredicting && m_PredictedPathCorners != null && m_PredictedPathCorners.Length > 0)
            {
                UpdatePredictedMovement();
            }
            else
            {
                // Interpolate between server snapshots (remote characters or no prediction)
                UpdateInterpolation();
            }
            
            // Update local NavMesh agent if enabled
            if (m_EnableLocalNavMesh && m_Agent != null)
            {
                UpdateLocalAgent();
            }
        }
        
        private void UpdatePredictedMovement()
        {
            if (m_PredictedCornerIndex >= m_PredictedPathCorners.Length)
            {
                // Reached end of predicted path
                m_IsPredicting = false;
                return;
            }
            
            Vector3 currentPos = this.Transform.position;
            Vector3 targetCorner = m_PredictedPathCorners[m_PredictedCornerIndex];
            
            float speed = this.Character.Motion.LinearSpeed;
            float distance = Vector3.Distance(currentPos, targetCorner);
            float arrivalThreshold = 0.1f;
            
            if (distance <= arrivalThreshold)
            {
                // Move to next corner
                m_PredictedCornerIndex++;
                if (m_PredictedCornerIndex >= m_PredictedPathCorners.Length)
                {
                    m_IsPredicting = false;
                    return;
                }
                targetCorner = m_PredictedPathCorners[m_PredictedCornerIndex];
            }
            
            // Move towards corner
            Vector3 direction = (targetCorner - currentPos).normalized;
            Vector3 movement = direction * speed * this.Character.Time.DeltaTime;
            
            // Clamp to not overshoot
            if (movement.magnitude > distance)
            {
                movement = direction * distance;
            }
            
            this.Transform.position = currentPos + movement;
            
            // Update velocity for animation
            m_Velocity = direction * speed;
            m_MoveDirection = direction * speed;
            
            // Face movement direction
            if (direction.sqrMagnitude > 0.001f)
            {
                Vector3 flatDir = new Vector3(direction.x, 0, direction.z);
                if (flatDir.sqrMagnitude > 0.001f)
                {
                    Quaternion targetRotation = Quaternion.LookRotation(flatDir);
                    this.Transform.rotation = Quaternion.Slerp(
                        this.Transform.rotation,
                        targetRotation,
                        this.Character.Motion.AngularSpeed * this.Character.Time.DeltaTime * 0.1f
                    );
                }
            }
        }

        private void UpdateCapsule(IUnitMotion motion)
        {
            if (m_Capsule == null) return;
            
            if (Math.Abs(this.m_Capsule.height - motion.Height) > float.Epsilon)
                this.m_Capsule.height = motion.Height;
            
            if (Math.Abs(this.m_Capsule.radius - motion.Radius) > float.Epsilon)
                this.m_Capsule.radius = motion.Radius;
            
            if (this.m_Capsule.center != Vector3.zero)
                this.m_Capsule.center = Vector3.zero;
        }

        private void UpdateInterpolation()
        {
            if (m_SnapshotBuffer.Count < 2)
            {
                // Not enough snapshots - extrapolate if we have any
                if (m_SnapshotBuffer.Count == 1)
                {
                    Extrapolate();
                }
                return;
            }
            
            // Target time is behind current time by buffer amount
            float targetTime = Time.time - m_Config.InterpolationBuffer;
            
            // Find surrounding snapshots
            int fromIndex = -1;
            int toIndex = -1;
            
            for (int i = 0; i < m_SnapshotBuffer.Count - 1; i++)
            {
                if (m_SnapshotBuffer[i].timestamp <= targetTime && 
                    m_SnapshotBuffer[i + 1].timestamp >= targetTime)
                {
                    fromIndex = i;
                    toIndex = i + 1;
                    break;
                }
            }
            
            if (fromIndex >= 0 && toIndex >= 0)
            {
                // Interpolate between snapshots
                var from = m_SnapshotBuffer[fromIndex];
                var to = m_SnapshotBuffer[toIndex];
                
                float t = (targetTime - from.timestamp) / (to.timestamp - from.timestamp);
                t = Mathf.Clamp01(t);
                
                InterpolateBetween(from, to, t);
                m_IsExtrapolating = false;
            }
            else if (m_SnapshotBuffer.Count > 0)
            {
                // All snapshots are in the past - extrapolate
                Extrapolate();
            }
        }

        private void InterpolateBetween(PositionSnapshot from, PositionSnapshot to, float t)
        {
            Vector3 position = Vector3.Lerp(from.position, to.position, t);
            float rotationY = Mathf.LerpAngle(from.rotationY, to.rotationY, t);
            
            // Apply position and rotation
            this.Transform.position = position;
            this.Transform.rotation = Quaternion.Euler(0f, rotationY, 0f);
            
            // Calculate velocity
            float deltaTime = to.timestamp - from.timestamp;
            if (deltaTime > 0)
            {
                m_Velocity = (to.position - from.position) / deltaTime;
            }
            
            m_MoveDirection = m_Velocity;
        }

        private void Extrapolate()
        {
            if (m_SnapshotBuffer.Count == 0) return;
            
            var lastSnapshot = m_SnapshotBuffer[m_SnapshotBuffer.Count - 1];
            float timeSinceSnapshot = Time.time - lastSnapshot.timestamp;
            
            // Limit extrapolation time
            if (timeSinceSnapshot > m_Config.MaxExtrapolationTime)
            {
                m_IsExtrapolating = true;
                return; // Stop extrapolating - wait for server
            }
            
            m_IsExtrapolating = true;
            m_ExtrapolationTime = timeSinceSnapshot;
            
            // Extrapolate along path if we have one
            if (m_ServerPathCorners != null && m_CurrentCornerIndex < m_ServerPathCorners.Length)
            {
                ExtrapolateAlongPath(lastSnapshot, timeSinceSnapshot);
            }
            else if (m_IsDirectionMode)
            {
                // Extrapolate in direction
                float speed = this.Character.Motion.LinearSpeed * lastSnapshot.speedPercent;
                Vector3 extrapolatedPos = lastSnapshot.position + m_DirectionInput * speed * timeSinceSnapshot;
                this.Transform.position = extrapolatedPos;
            }
        }

        private void ExtrapolateAlongPath(PositionSnapshot lastSnapshot, float deltaTime)
        {
            float speed = this.Character.Motion.LinearSpeed * lastSnapshot.speedPercent;
            float distanceToMove = speed * deltaTime;
            
            Vector3 currentPos = lastSnapshot.position;
            int cornerIndex = lastSnapshot.cornerIndex;
            
            while (distanceToMove > 0 && cornerIndex < m_ServerPathCorners.Length)
            {
                Vector3 targetCorner = m_ServerPathCorners[cornerIndex];
                Vector3 toCorner = targetCorner - currentPos;
                float distToCorner = toCorner.magnitude;
                
                if (distanceToMove >= distToCorner)
                {
                    currentPos = targetCorner;
                    distanceToMove -= distToCorner;
                    cornerIndex++;
                }
                else
                {
                    currentPos += toCorner.normalized * distanceToMove;
                    distanceToMove = 0;
                }
            }
            
            this.Transform.position = currentPos;
            
            // Update velocity direction
            if (cornerIndex < m_ServerPathCorners.Length)
            {
                m_Velocity = (m_ServerPathCorners[cornerIndex] - currentPos).normalized * speed;
            }
            else
            {
                m_Velocity = Vector3.zero;
            }
            
            m_MoveDirection = m_Velocity;
        }

        private void UpdateLocalAgent()
        {
            if (m_Agent == null) return;
            
            // Keep agent synced with our position
            if (m_Agent.isOnNavMesh)
            {
                m_Agent.nextPosition = this.Transform.position;
            }
            
            // Update agent properties
            var motion = this.Character.Motion;
            m_Agent.speed = motion.LinearSpeed;
            m_Agent.radius = motion.Radius;
            m_Agent.height = motion.Height;
        }

        private void TeleportTo(Vector3 position, float rotationY)
        {
            this.Transform.position = position;
            this.Transform.rotation = Quaternion.Euler(0f, rotationY, 0f);
            
            if (m_EnableLocalNavMesh && m_Agent != null && m_Agent.isOnNavMesh)
            {
                m_Agent.Warp(position);
            }
            
            // Clear snapshot buffer on teleport
            m_SnapshotBuffer.Clear();
        }

        // INTERFACE METHODS: ---------------------------------------------------------------------

        public override void SetPosition(Vector3 position, bool teleport = false)
        {
            if (teleport)
            {
                TeleportTo(position, this.Transform.eulerAngles.y);
            }
            else
            {
                this.Transform.position = position;
            }
        }

        public override void SetRotation(Quaternion rotation)
        {
            this.Transform.rotation = rotation;
        }

        public override void SetScale(Vector3 scale)
        {
            this.Transform.localScale = scale;
        }

        public override void AddPosition(Vector3 amount)
        {
            this.Transform.position += amount;
        }

        public override void AddRotation(Quaternion amount)
        {
            this.Transform.rotation *= amount;
        }
        
        public override void AddScale(Vector3 scale)
        {
            this.Transform.localScale += scale;
        }

        public override void ResetVerticalVelocity()
        { }

        // GIZMOS: --------------------------------------------------------------------------------

        public override void OnDrawGizmos(Character character)
        {
            if (!Application.isPlaying) return;
            
            // Draw server path
            if (m_ServerPathCorners != null && m_ServerPathCorners.Length > 0)
            {
                Gizmos.color = m_IsExtrapolating ? Color.yellow : Color.green;
                
                for (int i = 1; i < m_ServerPathCorners.Length; i++)
                {
                    Gizmos.DrawLine(m_ServerPathCorners[i - 1], m_ServerPathCorners[i]);
                }
                
                // Draw current target corner
                if (m_CurrentCornerIndex < m_ServerPathCorners.Length)
                {
                    Gizmos.color = Color.cyan;
                    Gizmos.DrawWireSphere(m_ServerPathCorners[m_CurrentCornerIndex], 0.2f);
                }
            }
            
            // Draw extrapolation indicator
            if (m_IsExtrapolating)
            {
                Gizmos.color = Color.yellow;
                Gizmos.DrawWireSphere(this.Transform.position + Vector3.up * 2f, 0.3f);
            }
        }

        public override string ToString() => "NavMesh Network (Client)";
    }
}
