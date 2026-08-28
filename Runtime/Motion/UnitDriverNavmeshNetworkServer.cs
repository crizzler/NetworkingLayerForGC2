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
        private const float NAVMESH_BIND_RETRY_INTERVAL = 0.10f;
        private const float NAVMESH_BIND_SLOW_RETRY_INTERVAL = 1f;
        private const int NAVMESH_BIND_WARNING_ATTEMPT = 50;

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
        [NonSerialized] private bool m_UsingAuthoredMotion;
        [NonSerialized] private int m_NavMeshBindAttempts;
        [NonSerialized] private int m_ConsecutiveNavMeshBindFailures;
        [NonSerialized] private float m_NextNavMeshBindAttemptTime;
        [NonSerialized] private bool m_HasWarnedNavMeshBinding;

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
        public bool IsNavMeshReady =>
            m_Agent != null && m_Agent.enabled && m_Agent.isOnNavMesh;
        public NavMeshAgent BoundAgent => m_Agent;
        public int NavMeshBindAttempts => m_NavMeshBindAttempts;

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
            ResetTransientMotionState();

            EnsureNavigationComponents();

            // Runtime-created agents are not guaranteed to bind during Character.Awake because
            // a NavMeshSurface may register later in the frame. Configure dimensions first and
            // start a deterministic retry path instead of silently remaining off-mesh forever.
            UpdateProperties(this.Character.Motion);
            m_NavMeshBindAttempts = 0;
            m_ConsecutiveNavMeshBindFailures = 0;
            m_NextNavMeshBindAttemptTime = 0f;
            m_HasWarnedNavMeshBinding = false;
            TryEnsureNavMeshBinding(force: true);

            // Initialize off-mesh link controller
            m_LinkController = this.Character.GetComponent<OffMeshLinkNetworkServer>();
            if (m_LinkController == null)
            {
                m_LinkController = this.Character.gameObject.AddComponent<OffMeshLinkNetworkServer>();
            }
            m_LinkController.Initialize(this.Character, this.m_Agent);

            // Forward link events
            m_LinkController.OnLinkStartReady -= ForwardLinkStart;
            m_LinkController.OnLinkStartReady += ForwardLinkStart;
            m_LinkController.OnLinkProgressReady -= ForwardLinkProgress;
            m_LinkController.OnLinkProgressReady += ForwardLinkProgress;
            m_LinkController.OnLinkCompleteReady -= ForwardLinkComplete;
            m_LinkController.OnLinkCompleteReady += ForwardLinkComplete;
            m_LinkController.OnLinkAnimationReady -= ForwardLinkAnimation;
            m_LinkController.OnLinkAnimationReady += ForwardLinkAnimation;

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
            if (m_LinkController != null) m_LinkController.ForceCompleteTraversal();
            if (m_LinkController != null)
            {
                m_LinkController.OnLinkStartReady -= ForwardLinkStart;
                m_LinkController.OnLinkProgressReady -= ForwardLinkProgress;
                m_LinkController.OnLinkCompleteReady -= ForwardLinkComplete;
                m_LinkController.OnLinkAnimationReady -= ForwardLinkAnimation;
            }

            // NetworkCharacter preserves and reuses this authored driver across authority
            // migration. Destroying components here schedules them for end-of-frame removal, so
            // a same-frame authority regain can bind references that vanish moments later. Keep
            // the object-owned components with the GameObject and make them inert for observers.
            if (m_Agent != null)
            {
                if (m_Agent.enabled && m_Agent.isOnNavMesh)
                {
                    m_Agent.ResetPath();
                    m_Agent.isStopped = true;
                    m_Agent.velocity = Vector3.zero;
                }
                m_Agent.enabled = false;
            }
            if (m_Capsule != null) m_Capsule.enabled = false;
            ResetTransientMotionState();
            m_Agent = null;
            m_Capsule = null;
            m_LinkController = null;
            base.OnDispose(character);
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
            bool hasPath = m_Agent != null && m_Agent.enabled &&
                           m_Agent.isOnNavMesh && m_Agent.hasPath;
            return NetworkNavMeshPathState.Create(
                this.Transform.position,
                this.Transform.eulerAngles.y,
                m_LastProcessedSequence,
                hasPath ? (byte)m_Agent.pathStatus : NetworkNavMeshPathState.STATUS_NONE,
                hasPath ? m_CurrentPathCorners : null
            );
        }

        /// <summary>
        /// Get current position update.
        /// </summary>
        public NetworkNavMeshPositionUpdate GetCurrentPositionUpdate()
        {
            bool agentReady = m_Agent != null && m_Agent.enabled && m_Agent.isOnNavMesh;
            return NetworkNavMeshPositionUpdate.Create(
                this.Transform.position,
                this.Transform.eulerAngles.y,
                m_CurrentCornerIndex,
                agentReady ? m_Agent.velocity.magnitude : 0f,
                agentReady ? m_Agent.speed : 0f
            );
        }

        // UPDATE METHODS: ------------------------------------------------------------------------

        public override void OnUpdate()
        {
            if (this.Character.IsDead) return;

            if (m_Agent == null || m_Capsule == null)
            {
                EnsureNavigationComponents();
            }
            UpdateProperties(this.Character.Motion);
            if (!TryEnsureNavMeshBinding(force: false))
            {
                DiscardDeferredTranslationWhileUnbound();
                m_Velocity = Vector3.zero;
                m_PreviousPosition = Transform.position;
                return;
            }

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

            // Fallback for custom INavMeshTraverseLink when no link controller is installed.
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
            m_UsingAuthoredMotion = false;

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
            m_UsingAuthoredMotion = false;

            Vector3 direction = command.GetDirection();

            // Validate direction magnitude (anti-cheat)
            if (direction.sqrMagnitude > 1.1f) // Allow small tolerance
            {
                direction.Normalize();
            }

            m_MoveDirection = direction * this.Character.Motion.LinearSpeed;
            ClearActivePath();
            m_Agent.isStopped = true;
            m_Agent.velocity = Vector3.zero;
            m_Agent.autoRepath = false;

            // Broadcast no-path state
            OnPathStateReady?.Invoke(NetworkNavMeshPathState.CreateNoPath(
                this.Transform.position,
                this.Transform.eulerAngles.y,
                command.Sequence
            ));
        }

        private void HandleStop(NetworkNavMeshCommand command)
        {
            m_UsingAuthoredMotion = false;
            ClearActivePath();
            m_Agent.isStopped = true;
            m_Agent.velocity = Vector3.zero;
            m_Agent.autoRepath = false;
            m_MoveDirection = Vector3.zero;

            OnPathStateReady?.Invoke(NetworkNavMeshPathState.CreateNoPath(
                this.Transform.position,
                this.Transform.eulerAngles.y,
                command.Sequence
            ));
        }

        private void HandleWarp(NetworkNavMeshCommand command)
        {
            m_UsingAuthoredMotion = false;
            Vector3 target = command.GetTargetPosition();

            // Validate warp target is on NavMesh
            if (TryResolveNavMeshRootPosition(target, 2f, out Vector3 rootPosition, out _))
            {
                m_Agent.Warp(rootPosition);
                m_Agent.isStopped = true;
                m_Agent.velocity = Vector3.zero;

                m_CurrentPathCorners = null;
                m_CurrentCornerIndex = 0;

                OnPathStateReady?.Invoke(NetworkNavMeshPathState.CreateNoPath(
                    rootPosition,
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
                ClearActivePath();
                this.m_Agent.velocity = Vector3.zero;
                this.m_Agent.isStopped = true;

                BeginRootMotionFrame(this.Character.Animim.RootMotionDeltaPosition);
                this.m_Agent.Move(this.m_MoveDirection);
            }
            else if (this.UpdateKinematics)
            {
                // Server-owned NPCs issue ordinary GC2 Motion instructions (including Follow).
                // Once such a command is observed, continue honoring its transition back to None
                // instead of leaving the previous NavMesh path running forever.
                if (ShouldUpdateAuthoredMotion(motion.MovementType))
                {
                    m_UsingAuthoredMotion = motion.MovementType != Character.MovementType.None;
                    UpdateAuthoredMotion(motion);
                }
                else
                {
                    // Network-command movement remains available for player-owned NavMesh actors.
                    if (m_MoveDirection.sqrMagnitude > 0.01f && m_Agent.isStopped)
                    {
                        Vector3 movement = m_MoveDirection * this.Character.Time.DeltaTime;
                        this.m_Agent.Move(movement);
                    }
                    else
                    {
                        this.m_MoveDirection = this.m_Agent.velocity;
                    }

                    if (m_CurrentPathCorners != null && m_CurrentPathCorners.Length > 0)
                    {
                        UpdateCornerIndex();
                    }
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

        private void BeginRootMotionFrame(Vector3 deltaPosition)
        {
            // A Skill can start root motion while the NPC is already stopped. Retain one authored
            // cleanup frame so the following MovementType.None state clears this delta instead of
            // falling through to the legacy network-command branch and moving it again forever.
            m_UsingAuthoredMotion = true;
            m_MoveDirection = deltaPosition;
        }

        private bool ShouldUpdateAuthoredMotion(Character.MovementType movementType)
        {
            return movementType != Character.MovementType.None || m_UsingAuthoredMotion;
        }

        private void DiscardDeferredTranslationWhileUnbound()
        {
            // AddPosition and animation root motion are per-frame deltas. Retaining them while
            // the runtime agent waits for a NavMesh surface would apply multiple missed frames as
            // one teleport when binding succeeds. The authored movement/follow command remains on
            // Character.Motion and resumes normally once the agent is ready.
            if (m_AddTranslation.HasValue) m_AddTranslation.Consume();
        }

        private void UpdateAuthoredMotion(IUnitMotion motion)
        {
            switch (motion.MovementType)
            {
                case Character.MovementType.MoveToDirection:
                    ClearActivePath();
                    m_Agent.autoBraking = false;
                    m_Agent.autoRepath = false;
                    m_Agent.isStopped = true;
                    m_Agent.velocity = Vector3.zero;
                    m_MoveDirection = motion.MoveDirection;
                    m_Agent.Move(m_MoveDirection * this.Character.Time.DeltaTime);
                    break;

                case Character.MovementType.MoveToPosition:
                    m_Agent.autoBraking = true;
                    m_Agent.autoRepath = true;
                    m_Agent.isStopped = false;
                    m_Agent.SetDestination(motion.MovePosition);
                    m_MoveDirection = m_Agent.velocity;
                    if (m_Agent.hasPath)
                    {
                        m_CurrentPathCorners = m_Agent.path.corners;
                        UpdateCornerIndex();
                    }
                    break;

                case Character.MovementType.None:
                    ClearActivePath();
                    m_Agent.autoBraking = true;
                    m_Agent.autoRepath = false;
                    m_Agent.isStopped = true;
                    m_Agent.velocity = Vector3.zero;
                    m_MoveDirection = Vector3.zero;
                    break;

                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        private void ClearActivePath()
        {
            if (m_Agent != null && m_Agent.enabled && m_Agent.isOnNavMesh && m_Agent.hasPath)
            {
                m_Agent.ResetPath();
            }

            m_CurrentPathCorners = null;
            m_CurrentCornerIndex = 0;
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
            if (m_Agent == null || !m_Agent.enabled || !m_Agent.isOnNavMesh)
            {
                m_Link = null;
                return;
            }

            this.m_Agent.updatePosition = true;
            this.m_Agent.updateRotation = false;
            this.m_Agent.isStopped = false;
            this.m_Agent.autoRepath = true;
            this.m_Agent.CompleteOffMeshLink();
            this.m_Link = null;
            this.m_Agent.autoTraverseOffMeshLink = this.m_AutoMeshLink;
        }

        // HELPER METHODS: ------------------------------------------------------------------------

        private void EnsureNavigationComponents()
        {
            if (Character == null) return;

            if (m_Agent == null)
            {
                m_Agent = Character.GetComponent<NavMeshAgent>();
                if (m_Agent == null)
                {
                    m_Agent = Character.gameObject.AddComponent<NavMeshAgent>();
                    m_Agent.hideFlags = HideFlags.HideInInspector;
                }
            }

            if (!m_Agent.enabled) m_Agent.enabled = true;
            m_Agent.updatePosition = true;
            m_Agent.updateRotation = false;
            m_Agent.updateUpAxis = false;
            m_Agent.autoBraking = false;
            m_Agent.autoRepath = false;
            m_Agent.agentTypeID = m_AgentTypeID;

            if (m_Capsule == null)
            {
                m_Capsule = Character.GetComponent<CapsuleCollider>();
                if (m_Capsule == null)
                {
                    m_Capsule = Character.gameObject.AddComponent<CapsuleCollider>();
                    m_Capsule.hideFlags = HideFlags.HideInInspector;
                }
            }

            if (!m_Capsule.enabled) m_Capsule.enabled = true;

            // The observer interpolation driver uses a CharacterController. It remains attached
            // for reuse across Shared-master migration, but authority must not run two root
            // collision/movement components at the same time.
            CharacterController remoteController = Character.GetComponent<CharacterController>();
            if (remoteController != null && remoteController.enabled)
            {
                remoteController.enabled = false;
            }
        }

        private void ResetTransientMotionState()
        {
            m_CommandQueue?.Clear();
            m_MoveDirection = Vector3.zero;
            m_Velocity = Vector3.zero;
            m_UsingAuthoredMotion = false;
            m_CurrentPathCorners = null;
            m_CurrentCornerIndex = 0;
            m_Link = null;
            m_AddTranslation = default;
            m_LastPositionSendTime = 0f;
            m_NextNavMeshBindAttemptTime = 0f;
            m_HasWarnedNavMeshBinding = false;
        }

        private void ForwardLinkStart(NetworkOffMeshLinkStart start) =>
            OnLinkStartReady?.Invoke(start);

        private void ForwardLinkProgress(NetworkOffMeshLinkProgress progress) =>
            OnLinkProgressReady?.Invoke(progress);

        private void ForwardLinkComplete(NetworkOffMeshLinkComplete complete) =>
            OnLinkCompleteReady?.Invoke(complete);

        private void ForwardLinkAnimation(NetworkOffMeshLinkAnimation animation) =>
            OnLinkAnimationReady?.Invoke(animation);

        private float HalfHeight => this.Character != null
            ? this.Character.Motion.Height * 0.5f
            : 0f;

        private bool TryResolveNavMeshRootPosition(
            Vector3 requestedPosition,
            float sampleDistance,
            out Vector3 rootPosition,
            out Vector3 surfacePosition)
        {
            float halfHeight = HalfHeight;
            float distance = Mathf.Max(0.25f, sampleDistance);
            float verticalTolerance = Mathf.Max(0.1f, this.SkinWidth * 2f);

            if (TrySampleNavMeshSurface(requestedPosition, distance, verticalTolerance, out NavMeshHit surfaceHit))
            {
                surfacePosition = surfaceHit.position;
                rootPosition = surfacePosition + Vector3.up * halfHeight;
                return true;
            }

            Vector3 potentialSurface = requestedPosition - Vector3.up * halfHeight;
            if (TrySampleNavMeshSurface(potentialSurface, distance, verticalTolerance, out surfaceHit))
            {
                surfacePosition = surfaceHit.position;
                rootPosition = surfacePosition + Vector3.up * halfHeight;
                return true;
            }

            if (NavMesh.SamplePosition(requestedPosition, out surfaceHit, distance, NavMesh.AllAreas))
            {
                surfacePosition = surfaceHit.position;
                rootPosition = surfacePosition + Vector3.up * halfHeight;
                return true;
            }

            surfacePosition = requestedPosition - Vector3.up * halfHeight;
            rootPosition = requestedPosition;
            return false;
        }

        private bool TryEnsureNavMeshBinding(bool force)
        {
            if (m_Agent == null || !m_Agent.enabled) return false;
            if (m_Agent.isOnNavMesh)
            {
                m_ConsecutiveNavMeshBindFailures = 0;
                m_NextNavMeshBindAttemptTime = 0f;
                m_HasWarnedNavMeshBinding = false;
                return true;
            }

            float now = Time.unscaledTime;
            if (!force && now < m_NextNavMeshBindAttemptTime) return false;

            m_NavMeshBindAttempts++;
            float sampleDistance = Mathf.Max(2f, HalfHeight + 0.5f);
            bool resolved = TryResolveNavMeshRootPosition(
                Transform.position,
                sampleDistance,
                out Vector3 rootPosition,
                out _);
            bool warped = resolved && m_Agent.Warp(rootPosition);
            bool ready = warped && m_Agent.isOnNavMesh;

            NetworkCharacter networkCharacter = Character.GetComponent<NetworkCharacter>();
            uint networkId = networkCharacter != null ? networkCharacter.NetworkId : 0;
            if (ready)
            {
                int consecutiveFailures = m_ConsecutiveNavMeshBindFailures;
                m_ConsecutiveNavMeshBindFailures = 0;
                m_NextNavMeshBindAttemptTime = 0f;
                m_PreviousPosition = Transform.position;
                m_LastSentPosition = Transform.position;
                NetworkCiTrace.Log(
                    "npc-navmesh",
                    "bound",
                    networkId,
                    0,
                    $"attempts={m_NavMeshBindAttempts} " +
                    $"previousFailures={consecutiveFailures} position={Transform.position}",
                    Character);
                return true;
            }

            m_ConsecutiveNavMeshBindFailures++;
            bool useSlowRetry =
                m_ConsecutiveNavMeshBindFailures >= NAVMESH_BIND_WARNING_ATTEMPT;
            m_NextNavMeshBindAttemptTime = now + (useSlowRetry
                ? NAVMESH_BIND_SLOW_RETRY_INTERVAL
                : NAVMESH_BIND_RETRY_INTERVAL);

            if (useSlowRetry && !m_HasWarnedNavMeshBinding)
            {
                m_HasWarnedNavMeshBinding = true;
                LogNavMeshBindingFailure(sampleDistance);
                NetworkCiTrace.Log(
                    "npc-navmesh",
                    "bind-failed",
                    networkId,
                    0,
                    $"attempts={m_NavMeshBindAttempts} " +
                    $"consecutiveFailures={m_ConsecutiveNavMeshBindFailures} " +
                    $"position={Transform.position} " +
                    $"agentType={m_AgentTypeID} sampleDistance={sampleDistance:F2}",
                    Character);
            }

            return false;
        }

        [System.Diagnostics.Conditional("UNITY_EDITOR")]
        [System.Diagnostics.Conditional("DEVELOPMENT_BUILD")]
        private void LogNavMeshBindingFailure(float sampleDistance)
        {
            Debug.LogWarning(
                $"[UnitDriverNavmeshNetworkServer] '{Character.name}' could not bind its " +
                $"runtime NavMeshAgent after {m_ConsecutiveNavMeshBindFailures} consecutive " +
                $"attempts. Server NPC " +
                $"movement is paused and will retry once per second. Verify that a matching " +
                $"NavMesh surface is active near {Transform.position} (agentType={m_AgentTypeID}, " +
                $"sampleDistance={sampleDistance:F2}).",
                Character);
        }

        private static bool TrySampleNavMeshSurface(
            Vector3 position,
            float sampleDistance,
            float verticalTolerance,
            out NavMeshHit hit)
        {
            if (!NavMesh.SamplePosition(position, out hit, sampleDistance, NavMesh.AllAreas))
            {
                return false;
            }

            return Mathf.Abs(position.y - hit.position.y) <= verticalTolerance;
        }

        private static bool IsSequenceNewer(ushort a, ushort b)
        {
            return (short)(a - b) > 0;
        }

        // INTERFACE METHODS: ---------------------------------------------------------------------

        public override void SetPosition(Vector3 position, bool teleport = false)
        {
            if (this.m_Agent == null)
            {
                this.Transform.position = position;
                return;
            }

            float sampleDistance = Mathf.Max(2f, HalfHeight + 0.5f);
            if (TryResolveNavMeshRootPosition(position, sampleDistance, out Vector3 rootPosition, out _))
            {
                if (this.m_Agent.Warp(rootPosition))
                {
                    this.m_LastSentPosition = rootPosition;
                    this.m_PreviousPosition = rootPosition;
                    return;
                }
            }

            if (!this.m_Agent.Warp(position))
            {
                this.Transform.position = position;
                m_NextNavMeshBindAttemptTime = 0f;
            }
            this.m_LastSentPosition = position;
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
