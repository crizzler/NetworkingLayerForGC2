using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.Characters;

namespace Arawn.GameCreator2.Networking
{
    /// <summary>
    /// Server-authoritative NavMesh driver for networked characters.
    /// Handles pathfinding and broadcasts authoritative position/path to clients.
    /// 
    /// Server responsibilities:
    /// - Process client movement commands (MoveToPosition, MoveToDirection, Stop)
    /// - Calculate NavMesh paths
    /// - Broadcast path corners and position updates to clients
    /// - Validate movement requests (anti-cheat)
    /// </summary>
    [Title("NavMesh Agent Network (Server)")]
    [Image(typeof(IconCharacterWalk), ColorTheme.Type.Blue, typeof(OverlayArrowRight))]
    [Category("NavMesh Agent Network (Server)")]
    [Description("Server-authoritative NavMesh driver. Handles pathfinding and broadcasts state to clients.")]
    [Serializable]
    public class UnitDriverNavmeshNetworkServer : TUnitDriver
    {
        private const ObstacleAvoidanceType DEFAULT_QUALITY =
            ObstacleAvoidanceType.HighQualityObstacleAvoidance;
        
        // EXPOSED MEMBERS: -----------------------------------------------------------------------

        [SerializeField] private ObstacleAvoidanceType m_AvoidQuality = DEFAULT_QUALITY;
        [SerializeField] private int m_AvoidPriority = 50;
        [SerializeField] private bool m_AutoMeshLink = true;
        [SerializeField] private int m_AgentTypeID = 0;
        
        [Header("Network Settings")]
        [SerializeField] private NetworkNavMeshConfig m_Config = NetworkNavMeshConfig.Default;
        
        [Tooltip("Enable server-side click validation for anti-cheat")]
        [SerializeField] private bool m_EnableClickValidation = false;
        
        [SerializeField] private ClickValidationConfig m_ValidationConfig;

        // MEMBERS: -------------------------------------------------------------------------------

        [NonSerialized] protected NavMeshAgent m_Agent;
        [NonSerialized] protected CapsuleCollider m_Capsule;
        [NonSerialized] protected Vector3 m_MoveDirection;
        [NonSerialized] protected Vector3 m_Velocity;
        [NonSerialized] protected Vector3 m_PreviousPosition;
        
        // Click validation
        [NonSerialized] private ClickValidator m_ClickValidator;
        
        // Network state
        [NonSerialized] private Queue<NetworkNavMeshCommand> m_CommandQueue;
        [NonSerialized] private ushort m_LastProcessedSequence;
        [NonSerialized] private Vector3[] m_CurrentPathCorners;
        [NonSerialized] private int m_CurrentCornerIndex;
        [NonSerialized] private float m_LastPositionSendTime;
        [NonSerialized] private Vector3 m_LastSentPosition;
        [NonSerialized] private ulong m_OwnerClientId; // For click validation
        
        // Off-mesh link handling
        [NonSerialized] protected INavMeshTraverseLink m_Link;
        [NonSerialized] private DriverAdditionalTranslation m_AddTranslation;
        [NonSerialized] private OffMeshLinkNetworkServer m_LinkController;

        // EVENTS: --------------------------------------------------------------------------------
        
        /// <summary>
        /// Raised when path state should be sent to clients.
        /// </summary>
        public event Action<NetworkNavMeshPathState> OnPathStateReady;
        
        /// <summary>
        /// Raised when position update should be sent to clients.
        /// </summary>
        public event Action<NetworkNavMeshPositionUpdate> OnPositionUpdateReady;
        
        /// <summary>
        /// Raised when agent starts traversing an off-mesh link.
        /// </summary>
        public event Action<NetworkOffMeshLinkStart> OnLinkStartReady;
        
        /// <summary>
        /// Raised when off-mesh link traversal progress updates.
        /// </summary>
        public event Action<NetworkOffMeshLinkProgress> OnLinkProgressReady;
        
        /// <summary>
        /// Raised when agent completes off-mesh link traversal.
        /// </summary>
        public event Action<NetworkOffMeshLinkComplete> OnLinkCompleteReady;
        
        /// <summary>
        /// Raised when a client should be kicked for excessive violations.
        /// </summary>
        public event Action<ulong, string> OnShouldKickClient;
        
        /// <summary>
        /// Raised when off-mesh link has custom animation data.
        /// </summary>
        public event Action<NetworkOffMeshLinkAnimation> OnLinkAnimationReady;

        // INTERFACE PROPERTIES: ------------------------------------------------------------------

        public override Vector3 WorldMoveDirection => this.m_Velocity;
        public override Vector3 LocalMoveDirection => this.Transform.InverseTransformDirection(this.WorldMoveDirection);
        public override float SkinWidth => 0.08f;
        public override bool IsGrounded => this.m_ForceGrounded || (this.m_Agent != null && this.m_Agent.isOnNavMesh);
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
        public ushort LastProcessedSequence => m_LastProcessedSequence;
        public Vector3[] CurrentPath => m_CurrentPathCorners ?? Array.Empty<Vector3>();
        public int CurrentCornerIndex => m_CurrentCornerIndex;

        // INITIALIZERS: --------------------------------------------------------------------------

        public UnitDriverNavmeshNetworkServer()
        {
            this.m_MoveDirection = Vector3.zero;
        }

        public override void OnStartup(Character character)
        {
            base.OnStartup(character);
            
            m_CommandQueue = new Queue<NetworkNavMeshCommand>(16);
            m_LastProcessedSequence = 0;

            this.m_Agent = this.Character.GetComponent<NavMeshAgent>();
            if (this.m_Agent == null)
            {
                this.m_Agent = this.Character.gameObject.AddComponent<NavMeshAgent>();
                this.m_Agent.hideFlags = HideFlags.HideInInspector;
            }

            this.m_Agent.updatePosition = true;
            this.m_Agent.updateRotation = false;
            this.m_Agent.updateUpAxis = false;
            this.m_Agent.autoBraking = false;
            this.m_Agent.autoRepath = false;
            this.m_Agent.agentTypeID = this.m_AgentTypeID;

            this.m_Capsule = this.Character.GetComponent<CapsuleCollider>();
            if (this.m_Capsule == null)
            {
                this.m_Capsule = this.Character.gameObject.AddComponent<CapsuleCollider>();
                this.m_Capsule.hideFlags = HideFlags.HideInInspector;
            }
            
            // Initialize off-mesh link controller
            m_LinkController = this.Character.GetComponent<OffMeshLinkNetworkServer>();
            if (m_LinkController == null)
            {
                m_LinkController = this.Character.gameObject.AddComponent<OffMeshLinkNetworkServer>();
            }
            m_LinkController.Initialize(this.Character, this.m_Agent);
            
            // Forward link events
            m_LinkController.OnLinkStartReady += start => OnLinkStartReady?.Invoke(start);
            m_LinkController.OnLinkProgressReady += progress => OnLinkProgressReady?.Invoke(progress);
            m_LinkController.OnLinkCompleteReady += complete => OnLinkCompleteReady?.Invoke(complete);
            m_LinkController.OnLinkAnimationReady += anim => OnLinkAnimationReady?.Invoke(anim);
            
            // Initialize click validation if enabled
            if (m_EnableClickValidation)
            {
                m_ClickValidator = new ClickValidator(m_ValidationConfig ?? ClickValidationConfig.Competitive);
                m_ClickValidator.OnShouldKickClient += (clientId, reason) => OnShouldKickClient?.Invoke(clientId, reason);
            }
            
            m_PreviousPosition = this.Transform.position;
            m_LastSentPosition = this.Transform.position;
        }
        
        /// <summary>
        /// Set the owner client ID for click validation tracking.
        /// Call this when the network object is spawned.
        /// </summary>
        public void SetOwnerClientId(ulong clientId)
        {
            m_OwnerClientId = clientId;
        }

        public override void OnDispose(Character character)
        {
            base.OnDispose(character);
            if (this.m_Agent != null) UnityEngine.Object.Destroy(this.m_Agent);
            if (this.m_Capsule != null) UnityEngine.Object.Destroy(this.m_Capsule);
        }

        // PUBLIC METHODS: ------------------------------------------------------------------------
        
        /// <summary>
        /// Queue a command from a client for processing.
        /// </summary>
        public void QueueCommand(NetworkNavMeshCommand command)
        {
            // Validate sequence (prevent replay attacks)
            if (!IsSequenceNewer(command.Sequence, m_LastProcessedSequence))
            {
                return; // Old or duplicate command
            }
            
            m_CommandQueue.Enqueue(command);
        }
        
        /// <summary>
        /// Get current state for a newly connected client.
        /// </summary>
        public NetworkNavMeshPathState GetCurrentPathState()
        {
            return NetworkNavMeshPathState.Create(
                this.Transform.position,
                this.Transform.eulerAngles.y,
                m_LastProcessedSequence,
                m_Agent.hasPath ? (byte)m_Agent.pathStatus : NetworkNavMeshPathState.STATUS_NONE,
                m_CurrentPathCorners
            );
        }
        
        /// <summary>
        /// Get current position update.
        /// </summary>
        public NetworkNavMeshPositionUpdate GetCurrentPositionUpdate()
        {
            return NetworkNavMeshPositionUpdate.Create(
                this.Transform.position,
                this.Transform.eulerAngles.y,
                m_CurrentCornerIndex,
                m_Agent.velocity.magnitude,
                m_Agent.speed
            );
        }

        // UPDATE METHODS: ------------------------------------------------------------------------

        public override void OnUpdate()
        {
            if (this.Character.IsDead) return;
            
            // Handle off-mesh links via controller (handles both standard and custom links)
            if (m_LinkController != null && m_LinkController.ProcessLinkTraversal())
            {
                // Link controller is handling movement, apply root motion if any
                Vector3 additionalTranslation = this.m_AddTranslation.HasValue
                    ? this.m_AddTranslation.Consume()
                    : this.Character.Animim.RootMotionDeltaPosition;
                
                if (additionalTranslation != Vector3.zero) 
                    this.m_Agent.Move(additionalTranslation);
                
                return;
            }
            
            // Legacy fallback for custom INavMeshTraverseLink without controller
            if (this.m_Agent.isOnOffMeshLink && 
                this.m_Agent.currentOffMeshLinkData.owner is INavMeshTraverseLink navMeshLink)
            {
                if (this.m_Link == null)
                {
                    this.m_Link = navMeshLink;
                    this.m_Agent.isStopped = true;
                    this.m_Agent.velocity = Vector3.zero;
                    navMeshLink.Traverse(this.Character, this.OnTraverseComplete);
                }
                
                Vector3 additionalTranslation = this.m_AddTranslation.HasValue
                    ? this.m_AddTranslation.Consume()
                    : this.Character.Animim.RootMotionDeltaPosition;
                
                if (additionalTranslation != Vector3.zero) 
                    this.m_Agent.Move(additionalTranslation);
                
                return;
            }
            
            // Process queued commands
            ProcessCommands();
            
            // Update NavMesh properties
            UpdateProperties(this.Character.Motion);
            
            // Update movement
            UpdateTranslation(this.Character.Motion);
            
            // Send position updates
            SendPositionUpdate();
        }

        private void ProcessCommands()
        {
            while (m_CommandQueue.Count > 0)
            {
                var command = m_CommandQueue.Dequeue();
                
                if (!IsSequenceNewer(command.Sequence, m_LastProcessedSequence))
                    continue;
                
                m_LastProcessedSequence = command.Sequence;
                
                switch (command.CommandType)
                {
                    case NetworkNavMeshCommand.CMD_MOVE_TO_POSITION:
                        HandleMoveToPosition(command);
                        break;
                        
                    case NetworkNavMeshCommand.CMD_MOVE_TO_DIRECTION:
                        HandleMoveToDirection(command);
                        break;
                        
                    case NetworkNavMeshCommand.CMD_STOP:
                        HandleStop(command);
                        break;
                        
                    case NetworkNavMeshCommand.CMD_WARP:
                        HandleWarp(command);
                        break;
                }
            }
        }

        private void HandleMoveToPosition(NetworkNavMeshCommand command)
        {
            if (!m_Agent.isOnNavMesh) return;
            
            Vector3 target = command.GetTargetPosition();
            
            // Apply click validation if enabled
            if (m_ClickValidator != null)
            {
                var result = m_ClickValidator.ValidateClick(m_OwnerClientId, this.Transform.position, target);
                
                if (!result.IsValid)
                {
                    // Rejected - broadcast failure
                    BroadcastPathState(NavMeshPathStatus.PathInvalid);
                    return;
                }
                
                // Use corrected position if snapped to NavMesh
                target = result.CorrectedPosition;
            }
            
            // Validate target is reachable (anti-cheat)
            NavMeshPath path = new NavMeshPath();
            if (m_Agent.CalculatePath(target, path))
            {
                m_Agent.SetPath(path);
                m_Agent.isStopped = false;
                m_Agent.autoRepath = true;
                m_Agent.autoBraking = true;
                
                // Cache path corners
                m_CurrentPathCorners = path.corners;
                m_CurrentCornerIndex = 0;
                
                // Broadcast path to clients
                BroadcastPathState(path.status);
            }
            else
            {
                // Invalid path - broadcast failure
                BroadcastPathState(NavMeshPathStatus.PathInvalid);
            }
        }

        private void HandleMoveToDirection(NetworkNavMeshCommand command)
        {
            if (!m_Agent.isOnNavMesh) return;
            
            Vector3 direction = command.GetDirection();
            
            // Validate direction magnitude (anti-cheat)
            if (direction.sqrMagnitude > 1.1f) // Allow small tolerance
            {
                direction.Normalize();
            }
            
            m_MoveDirection = direction * this.Character.Motion.LinearSpeed;
            m_Agent.isStopped = true;
            m_Agent.velocity = Vector3.zero;
            m_Agent.autoRepath = false;
            
            // Clear path
            m_CurrentPathCorners = null;
            m_CurrentCornerIndex = 0;
            
            // Broadcast no-path state
            OnPathStateReady?.Invoke(NetworkNavMeshPathState.CreateNoPath(
                this.Transform.position,
                this.Transform.eulerAngles.y,
                command.Sequence
            ));
        }

        private void HandleStop(NetworkNavMeshCommand command)
        {
            m_Agent.isStopped = true;
            m_Agent.velocity = Vector3.zero;
            m_Agent.autoRepath = false;
            m_MoveDirection = Vector3.zero;
            
            m_CurrentPathCorners = null;
            m_CurrentCornerIndex = 0;
            
            OnPathStateReady?.Invoke(NetworkNavMeshPathState.CreateNoPath(
                this.Transform.position,
                this.Transform.eulerAngles.y,
                command.Sequence
            ));
        }

        private void HandleWarp(NetworkNavMeshCommand command)
        {
            Vector3 target = command.GetTargetPosition();
            
            // Validate warp target is on NavMesh
            if (NavMesh.SamplePosition(target, out NavMeshHit hit, 2f, NavMesh.AllAreas))
            {
                m_Agent.Warp(hit.position);
                m_Agent.isStopped = true;
                m_Agent.velocity = Vector3.zero;
                
                m_CurrentPathCorners = null;
                m_CurrentCornerIndex = 0;
                
                OnPathStateReady?.Invoke(NetworkNavMeshPathState.CreateNoPath(
                    hit.position,
                    this.Transform.eulerAngles.y,
                    command.Sequence
                ));
            }
        }

        private void BroadcastPathState(NavMeshPathStatus status)
        {
            byte networkStatus = status switch
            {
                NavMeshPathStatus.PathComplete => NetworkNavMeshPathState.STATUS_COMPLETE,
                NavMeshPathStatus.PathPartial => NetworkNavMeshPathState.STATUS_PARTIAL,
                _ => NetworkNavMeshPathState.STATUS_INVALID
            };
            
            var pathState = NetworkNavMeshPathState.Create(
                this.Transform.position,
                this.Transform.eulerAngles.y,
                m_LastProcessedSequence,
                networkStatus,
                m_CurrentPathCorners
            );
            
            OnPathStateReady?.Invoke(pathState);
        }

        protected virtual void UpdateProperties(IUnitMotion motion)
        {
            this.m_MoveDirection = Vector3.zero;

            this.m_Agent.speed = motion.LinearSpeed;
            this.m_Agent.angularSpeed = motion.AngularSpeed >= 0f 
                ? motion.AngularSpeed
                : float.MaxValue;

            this.m_Agent.acceleration = motion.UseAcceleration 
                ? (motion.Acceleration + motion.Deceleration) / 2f
                : 9999f;

            this.m_Agent.radius = motion.Radius;
            this.m_Agent.height = motion.Height;

            if (Math.Abs(this.m_Capsule.height - motion.Height) > float.Epsilon)
                this.m_Capsule.height = motion.Height;
            
            if (Math.Abs(this.m_Capsule.radius - motion.Radius) > float.Epsilon)
                this.m_Capsule.radius = motion.Radius;
            
            if (this.m_Capsule.center != Vector3.zero)
                this.m_Capsule.center = Vector3.zero;
            
            this.m_Agent.baseOffset = this.m_Agent.height / 2f;
            this.m_Agent.autoTraverseOffMeshLink = this.m_AutoMeshLink;
            this.m_Agent.obstacleAvoidanceType = this.m_AvoidQuality;
            this.m_Agent.avoidancePriority = this.m_AvoidPriority;
        }

        protected virtual void UpdateTranslation(IUnitMotion motion)
        {
            if (!this.m_Agent.isOnNavMesh) return;

            // Handle root motion
            if (this.Character.RootMotionPosition > 0.9f)
            {
                this.m_Agent.velocity = Vector3.zero;
                this.m_Agent.isStopped = true;
                
                this.m_MoveDirection = this.Character.Animim.RootMotionDeltaPosition;
                this.m_Agent.Move(this.m_MoveDirection);
            }
            else if (this.UpdateKinematics)
            {
                // Direction-based movement
                if (m_MoveDirection.sqrMagnitude > 0.01f && m_Agent.isStopped)
                {
                    Vector3 movement = m_MoveDirection * this.Character.Time.DeltaTime;
                    this.m_Agent.Move(movement);
                }
                else
                {
                    // Path-following - update corner index
                    if (m_CurrentPathCorners != null && m_CurrentPathCorners.Length > 0)
                    {
                        UpdateCornerIndex();
                    }
                    
                    this.m_MoveDirection = this.m_Agent.velocity;
                }
            }

            // Handle additional translation
            Vector3 additionalTranslation = this.m_AddTranslation.Consume();
            if (additionalTranslation != Vector3.zero) 
                this.m_Agent.Move(additionalTranslation);

            // Calculate velocity
            Vector3 currentPosition = this.Transform.position;
            this.m_Velocity = 
                Vector3.Normalize(currentPosition - this.m_PreviousPosition) *
                this.m_MoveDirection.magnitude;
            this.m_PreviousPosition = currentPosition;
        }

        private void UpdateCornerIndex()
        {
            if (m_CurrentPathCorners == null || m_CurrentCornerIndex >= m_CurrentPathCorners.Length)
                return;
            
            Vector3 currentPos = this.Transform.position;
            
            // Check if we've reached current corner
            while (m_CurrentCornerIndex < m_CurrentPathCorners.Length)
            {
                float distToCorner = Vector3.Distance(
                    new Vector3(currentPos.x, 0, currentPos.z),
                    new Vector3(m_CurrentPathCorners[m_CurrentCornerIndex].x, 0, m_CurrentPathCorners[m_CurrentCornerIndex].z)
                );
                
                if (distToCorner < m_Agent.radius * 2f)
                {
                    m_CurrentCornerIndex++;
                }
                else
                {
                    break;
                }
            }
        }

        private void SendPositionUpdate()
        {
            float timeSinceLastSend = Time.time - m_LastPositionSendTime;
            if (timeSinceLastSend < 1f / m_Config.PositionSendRate) return;
            
            Vector3 currentPos = this.Transform.position;
            float distance = Vector3.Distance(currentPos, m_LastSentPosition);
            
            // Only send if moved significantly or forced by time
            if (distance < m_Config.PositionThreshold && timeSinceLastSend < 1f) return;
            
            var update = GetCurrentPositionUpdate();
            OnPositionUpdateReady?.Invoke(update);
            
            m_LastPositionSendTime = Time.time;
            m_LastSentPosition = currentPos;
        }

        private void OnTraverseComplete()
        {
            this.m_Agent.updatePosition = true;
            this.m_Agent.updateRotation = false;
            this.m_Agent.isStopped = false;
            this.m_Agent.autoRepath = true;
            this.m_Agent.CompleteOffMeshLink();
            this.m_Link = null;
            this.m_Agent.autoTraverseOffMeshLink = this.m_AutoMeshLink;
        }

        // HELPER METHODS: ------------------------------------------------------------------------

        private static bool IsSequenceNewer(ushort a, ushort b)
        {
            return (short)(a - b) > 0;
        }

        // INTERFACE METHODS: ---------------------------------------------------------------------

        public override void SetPosition(Vector3 position, bool teleport = false)
        {
            position += Vector3.up * (this.Character.Motion.Height * 0.5f);
            this.m_Agent.Warp(position);
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
            this.m_AddTranslation.Add(amount);
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
            if (m_Agent == null || !m_Agent.hasPath) return;

            Gizmos.color = m_Agent.pathStatus switch
            {
                NavMeshPathStatus.PathComplete => Color.green,
                NavMeshPathStatus.PathPartial => Color.yellow,
                _ => Color.red
            };

            Vector3[] corners = m_Agent.path.corners;
            for (int i = 1; i < corners.Length; i++)
            {
                Gizmos.DrawLine(corners[i - 1], corners[i]);
            }
            
            // Draw current corner
            if (m_CurrentPathCorners != null && m_CurrentCornerIndex < m_CurrentPathCorners.Length)
            {
                Gizmos.color = Color.cyan;
                Gizmos.DrawWireSphere(m_CurrentPathCorners[m_CurrentCornerIndex], 0.2f);
            }
        }

        public override string ToString() => "NavMesh Network (Server)";
    }
}
