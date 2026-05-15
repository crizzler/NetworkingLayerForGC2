using System;
using System.Collections.Generic;
using UnityEngine;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.Characters;

namespace Arawn.GameCreator2.Networking
{
    /// <summary>
    /// Server-authoritative driver that processes inputs and produces authoritative position states.
    /// This should run on the server/host. It validates client inputs and simulates movement.
    /// </summary>
    [Title("Network Character Controller (Server)")]
    [Image(typeof(IconCapsuleSolid), ColorTheme.Type.Purple)]
    [Category("Network Character Controller (Server)")]
    [Description("Server-authoritative driver that validates and processes client inputs. " +
                 "Use this on server/host for competitive multiplayer with cheat prevention.")]
    [Serializable]
    public class UnitDriverNetworkServer : TUnitDriver
    {
        // EXPOSED MEMBERS: -----------------------------------------------------------------------

        [SerializeField] protected float m_SkinWidth = 0.08f;
        [SerializeField] protected float m_MaxSlope = 45f;
        [SerializeField] protected float m_StepHeight = 0.3f;
        [SerializeField] private Axonometry m_Axonometry = new Axonometry();
        
        [Header("Anti-Cheat")]
        [SerializeField] private NetworkCharacterConfig m_Config = new NetworkCharacterConfig();

        [Header("Debug")]
        [SerializeField] private bool m_LogMotionDiagnostics = false;

        // MEMBERS: -------------------------------------------------------------------------------

        [NonSerialized] protected CharacterController m_Controller;
        [NonSerialized] protected Vector3 m_MoveDirection;
        [NonSerialized] protected float m_VerticalSpeed;
        [NonSerialized] protected AnimVector3 m_FloorNormal;
        
        [NonSerialized] private Queue<NetworkInputState> m_InputBuffer;
        [NonSerialized] private HashSet<ushort> m_QueuedInputSequences;
        [NonSerialized] private ushort m_LastProcessedInput;
        [NonSerialized] private int m_SpeedViolations;
        [NonSerialized] private Vector3 m_LastValidatedPosition;
        [NonSerialized] private float m_ExpectedMaxSpeed;
        [NonSerialized] private int m_SuppressedDuplicateInputs;
        [NonSerialized] private float m_LastMotionDiagnosticRealtime;
        
        /// <summary>
        /// Maximum number of buffered inputs. Protects against memory growth from
        /// packet floods or malicious clients. At 60 inputs/sec this is ~4 seconds.
        /// </summary>
        private const int MAX_BUFFERED_INPUTS = 256;
        private const float OWNER_AUTHORITY_POSITION_EPSILON = 0.005f;
        private const float OWNER_AUTHORITY_EXTRA_DISTANCE = 0.5f;
        private const float OWNER_AUTHORITY_ROOT_MOTION_THRESHOLD = 0.05f;
        
        [NonSerialized] protected int m_GroundFrame = -100;
        [NonSerialized] protected float m_GroundTime = -100f;
        [NonSerialized] private bool m_IsOnSteepSlope;

        // EVENTS: --------------------------------------------------------------------------------
        
        /// <summary>
        /// Fired when a new authoritative state is produced (send to clients).
        /// </summary>
        public event Action<NetworkPositionState> OnStateProduced;
        
        /// <summary>
        /// Fired when a speed violation is detected.
        /// </summary>
        public event Action<int> OnSpeedViolation;

        // INTERFACE PROPERTIES: ------------------------------------------------------------------

        public override Vector3 WorldMoveDirection => this.m_MoveDirection;
        public override Vector3 LocalMoveDirection => this.Transform.InverseTransformDirection(this.m_MoveDirection);

        public override float SkinWidth => this.m_Controller != null ? this.m_Controller.skinWidth : 0f;

        public override bool IsGrounded
        {
            get
            {
                if (this.m_Controller == null) return false;
                if (this.m_ForceGrounded) return true;
                if (this.m_Controller.isGrounded) return !this.m_IsOnSteepSlope;

                return TryProbeGround(out RaycastHit hit) &&
                       Vector3.Angle(hit.normal, Vector3.up) <= m_MaxSlope;
            }
        }

        public override Vector3 FloorNormal => this.m_FloorNormal?.Current ?? Vector3.up;

        public override bool Collision
        {
            get => this.m_Controller != null && this.m_Controller.detectCollisions;
            set { if (this.m_Controller != null) this.m_Controller.detectCollisions = value; }
        }

        public override Axonometry Axonometry
        {
            get => this.m_Axonometry;
            set => this.m_Axonometry = value;
        }

        public void SetExternalMoveDirection(Vector3 velocity)
        {
            this.m_MoveDirection = velocity;
        }
        
        public ushort LastProcessedInput => m_LastProcessedInput;
        public NetworkCharacterConfig Config => m_Config;
        
        public void ApplySessionProfile(NetworkSessionProfile profile)
        {
            if (profile == null) return;
            
            m_Config.maxSpeedMultiplier = profile.maxSpeedMultiplier;
            m_Config.violationThreshold = profile.violationThreshold;
        }

        // INITIALIZERS: --------------------------------------------------------------------------

        public UnitDriverNetworkServer()
        {
            this.m_MoveDirection = Vector3.zero;
            this.m_VerticalSpeed = 0f;
            this.m_InputBuffer = new Queue<NetworkInputState>(32);
            this.m_QueuedInputSequences = new HashSet<ushort>();
            this.m_LastProcessedInput = ushort.MaxValue;
        }

        public override void OnStartup(Character character)
        {
            base.OnStartup(character);

            this.m_FloorNormal = new AnimVector3(Vector3.up, 0.15f);
            this.m_InputBuffer = new Queue<NetworkInputState>(32);
            this.m_QueuedInputSequences = new HashSet<ushort>();
            this.m_LastProcessedInput = ushort.MaxValue;
            this.m_SpeedViolations = 0;
            this.m_SuppressedDuplicateInputs = 0;
            this.m_LastMotionDiagnosticRealtime = -100f;
            this.m_Controller = this.Character.GetComponent<CharacterController>();
            if (this.m_Controller == null)
            {
                this.m_Controller = this.Character.gameObject.AddComponent<CharacterController>();
                this.m_Controller.hideFlags = HideFlags.HideInInspector;

                float height = this.Character.Motion.Height;
                float radius = this.Character.Motion.Radius;

                this.m_Controller.height = height;
                this.m_Controller.radius = radius;
                this.m_Controller.center = Vector3.zero;
                this.m_Controller.skinWidth = this.m_SkinWidth;
                this.m_Controller.slopeLimit = this.m_MaxSlope;
                this.m_Controller.stepOffset = this.m_StepHeight;
                this.m_Controller.minMoveDistance = 0f;
            }

            this.m_LastValidatedPosition = this.Transform.position;
            this.m_ExpectedMaxSpeed = this.Character.Motion.LinearSpeed;

        }

        public override void OnEnable()
        {
            base.OnEnable();
            if (this.Character != null)
            {
                this.m_GroundTime = this.Character.Time.Time;
                this.m_GroundFrame = this.Character.Time.Frame;
            }
        }

        public override void OnDispose(Character character)
        {
            base.OnDispose(character);
            this.m_Controller = null;
        }

        // INPUT PROCESSING: ----------------------------------------------------------------------

        /// <summary>
        /// Queue an input from a client for processing.
        /// Call this when receiving client input over the network.
        /// </summary>
        public void QueueInput(NetworkInputState input)
        {
            // Reject out-of-order inputs (simple protection)
            if (IsSequenceNewer(input.sequenceNumber, m_LastProcessedInput))
            {
                if (m_QueuedInputSequences.Contains(input.sequenceNumber))
                {
                    m_SuppressedDuplicateInputs++;
                    LogServerMotionDiagnostic(
                        $"suppressed duplicate queued input seq={input.sequenceNumber} " +
                        $"lastProcessed={m_LastProcessedInput} queued={m_InputBuffer.Count} " +
                        $"suppressedSinceLast={m_SuppressedDuplicateInputs}");
                    return;
                }

                // Cap buffer size to prevent memory growth from floods
                if (m_InputBuffer.Count >= MAX_BUFFERED_INPUTS)
                {
                    NetworkInputState dropped = m_InputBuffer.Dequeue(); // Drop oldest input
                    m_QueuedInputSequences.Remove(dropped.sequenceNumber);
                    LogServerMotionDiagnostic(
                        $"input buffer full; dropped oldest seq={dropped.sequenceNumber} " +
                        $"incoming={input.sequenceNumber}",
                        force: true);
                }

                m_InputBuffer.Enqueue(input);
                m_QueuedInputSequences.Add(input.sequenceNumber);
            }
        }

        /// <summary>
        /// Process all queued inputs and produce authoritative state.
        /// Call this at your server tick rate.
        /// </summary>
        public NetworkPositionState ProcessInputs(Transform cameraTransform = null)
        {
            int queuedAtStart = m_InputBuffer.Count;
            if (queuedAtStart > 4)
            {
                LogServerMotionDiagnostic(
                    $"processing input backlog queued={queuedAtStart} " +
                    $"lastProcessed={m_LastProcessedInput} position={FormatVector(this.Transform.position)}");
            }

            while (m_InputBuffer.Count > 0)
            {
                var input = m_InputBuffer.Dequeue();
                m_QueuedInputSequences.Remove(input.sequenceNumber);
                ProcessSingleInput(input, cameraTransform);
                m_LastProcessedInput = input.sequenceNumber;
            }

            var state = CreateCurrentState();
            OnStateProduced?.Invoke(state);
            return state;
        }

        private void LogServerMotionDiagnostic(string message, bool force = false)
        {
            if (!m_LogMotionDiagnostics) return;

            float now = Time.realtimeSinceStartup;
            if (!force && now - m_LastMotionDiagnosticRealtime < 0.5f) return;

            Debug.Log(
                $"[NetworkMotionDebug][ServerDriver] {this.Character?.name ?? "Character"}: {message}",
                this.Character);
            m_LastMotionDiagnosticRealtime = now;
            m_SuppressedDuplicateInputs = 0;
        }

        private static string FormatVector(Vector3 value)
        {
            return $"({value.x:F3},{value.y:F3},{value.z:F3})";
        }

        private void ProcessSingleInput(NetworkInputState input, Transform cameraTransform)
        {
            Vector2 rawInput = input.GetInputDirection();
            float deltaTime = input.GetDeltaTime();
            this.Transform.rotation = Quaternion.Euler(0f, input.GetRotationY(), 0f);
            
            // Convert input to world direction
            Vector3 inputDirection = new Vector3(rawInput.x, 0f, rawInput.y);
            
            if (cameraTransform != null)
            {
                Quaternion cameraRotation = Quaternion.Euler(0f, cameraTransform.eulerAngles.y, 0f);
                inputDirection = cameraRotation * inputDirection;
            }
            
            if (inputDirection.sqrMagnitude > 1f) inputDirection.Normalize();

            // Calculate expected movement
            float speed = this.Character.Motion.LinearSpeed;
            Vector3 horizontalMovement = inputDirection * speed * deltaTime;

            // Validate movement (anti-cheat)
            float maxAllowedDistance = speed * m_Config.maxSpeedMultiplier * deltaTime;
            if (horizontalMovement.magnitude > maxAllowedDistance)
            {
                m_SpeedViolations++;
                OnSpeedViolation?.Invoke(m_SpeedViolations);
                
                // Clamp to max allowed
                horizontalMovement = horizontalMovement.normalized * maxAllowedDistance;
            }

            // Apply gravity
            UpdateGravity(deltaTime);

            // Handle jump
            if (input.HasFlag(NetworkInputState.FLAG_JUMP) && CanJump())
            {
                m_VerticalSpeed = this.Character.Motion.JumpForce;
            }

            // Combine movement
            Vector3 translation = ApplyRootMotionBlend(horizontalMovement);
            translation = this.m_Axonometry?.ProcessTranslation(this, translation) ?? translation;

            Vector3 totalMovement = translation + Vector3.up * m_VerticalSpeed * deltaTime;
            
            // Move character controller
            if (m_Controller != null && m_Controller.enabled)
            {
                m_Controller.Move(totalMovement);
            }

            if (TryApplyOwnerAuthorityPosition(input, deltaTime, out Vector3 ownerAuthorityDelta))
            {
                translation = ownerAuthorityDelta;
            }

            // Update grounded state
            if (IsGrounded && m_VerticalSpeed < 0)
            {
                m_VerticalSpeed = -2f; // Small downward force to stay grounded
                m_GroundTime = this.Character.Time.Time;
                m_GroundFrame = this.Character.Time.Frame;
            }

            // Store move direction for animation
            m_MoveDirection = translation / deltaTime;
            
            // Update floor normal
            if (m_FloorNormal != null)
            {
                m_FloorNormal.UpdateWithDelta(deltaTime);
            }
        }

        private Vector3 ApplyRootMotionBlend(Vector3 kineticMovement)
        {
            Vector3 rootMotion = this.Character.Animim.RootMotionDeltaPosition;
            return Vector3.Lerp(kineticMovement, rootMotion, this.Character.RootMotionPosition);
        }

        private bool TryApplyOwnerAuthorityPosition(
            NetworkInputState input,
            float deltaTime,
            out Vector3 appliedDelta)
        {
            appliedDelta = Vector3.zero;
            if (!input.HasOwnerAuthorityPosition) return false;
            if (!ShouldAcceptOwnerAuthorityPosition()) return false;

            Vector3 targetPosition = input.GetOwnerAuthorityPosition();
            Vector3 currentPosition = this.Transform.position;
            Vector3 delta = targetPosition - currentPosition;
            float distance = delta.magnitude;

            if (distance <= OWNER_AUTHORITY_POSITION_EPSILON) return false;

            float speed = this.Character.Motion.LinearSpeed;
            float maxKineticDistance = speed * m_Config.maxSpeedMultiplier * deltaTime + OWNER_AUTHORITY_EXTRA_DISTANCE;
            float maxAuthorityDistance = Mathf.Max(m_Config.maxReconciliationDistance, maxKineticDistance);

            if (distance > maxAuthorityDistance)
            {
                m_SpeedViolations++;
                OnSpeedViolation?.Invoke(m_SpeedViolations);
                LogServerMotionDiagnostic(
                    $"rejected owner authority position seq={input.sequenceNumber} distance={distance:F3} " +
                    $"max={maxAuthorityDistance:F3} current={FormatVector(currentPosition)} owner={FormatVector(targetPosition)}",
                    true);
                return false;
            }

            if (m_Controller != null && m_Controller.enabled)
            {
                m_Controller.enabled = false;
                this.Transform.position = targetPosition;
                m_Controller.enabled = true;
            }
            else
            {
                this.Transform.position = targetPosition;
            }

            appliedDelta = delta;
            LogServerMotionDiagnostic(
                $"accepted owner authority position seq={input.sequenceNumber} distance={distance:F3} " +
                $"rootMotion={this.Character.RootMotionPosition:F3} busy={this.Character.Busy.IsBusy} " +
                $"owner={FormatVector(targetPosition)}",
                distance > 0.05f);
            return true;
        }

        private bool ShouldAcceptOwnerAuthorityPosition()
        {
            if (this.Character == null) return false;
            if (this.Character.RootMotionPosition > OWNER_AUTHORITY_ROOT_MOTION_THRESHOLD) return true;
            return this.Character.Busy != null && this.Character.Busy.IsBusy;
        }

        private void UpdateGravity(float deltaTime)
        {
            float gravityInfluence = this.GravityInfluence;

            if (IsGrounded)
            {
                if (m_VerticalSpeed <= 0f)
                {
                    m_VerticalSpeed = gravityInfluence <= 0.001f ? 0f : -2f;
                }

                m_GroundTime = this.Character.Time.Time;
                m_GroundFrame = this.Character.Time.Frame;
                return;
            }

            if (!IsGrounded)
            {
                float gravity = m_VerticalSpeed >= 0 
                    ? this.Character.Motion.GravityUpwards 
                    : this.Character.Motion.GravityDownwards;

                gravity *= gravityInfluence;
                m_VerticalSpeed += gravity * deltaTime;
                m_VerticalSpeed = Mathf.Max(m_VerticalSpeed, this.Character.Motion.TerminalVelocity);
            }
        }

        private bool TryProbeGround(out RaycastHit hit)
        {
            hit = default;
            if (m_Controller == null || !m_Controller.enabled) return false;

            float skin = Mathf.Max(0.01f, m_Controller.skinWidth);
            float radius = Mathf.Max(0.01f, m_Controller.radius - skin);
            float halfHeight = Mathf.Max(radius, m_Controller.height * 0.5f);
            float probeDistance = Mathf.Max(0.05f, halfHeight - radius + skin + 0.08f);
            Vector3 center = Transform.TransformPoint(m_Controller.center);

            return Physics.SphereCast(
                center,
                radius,
                Vector3.down,
                out hit,
                probeDistance,
                Physics.DefaultRaycastLayers,
                QueryTriggerInteraction.Ignore
            );
        }

        private bool CanJump()
        {
            if (!this.Character.Motion.CanJump) return false;
            
            // Coyote time check
            float timeSinceGrounded = this.Character.Time.Time - m_GroundTime;
            int framesSinceGrounded = this.Character.Time.Frame - m_GroundFrame;
            
            bool inCoyoteTime = timeSinceGrounded < COYOTE_TIME || framesSinceGrounded < COYOTE_FRAMES;
            return IsGrounded || inCoyoteTime;
        }

        private NetworkPositionState CreateCurrentState()
        {
            return NetworkPositionState.Create(
                this.Transform.position,
                this.Transform.eulerAngles.y,
                m_VerticalSpeed,
                m_LastProcessedInput,
                IsGrounded,
                m_VerticalSpeed > 0
            );
        }
        
        /// <summary>
        /// Get the current authoritative state without processing any inputs.
        /// </summary>
        public NetworkPositionState GetCurrentState()
        {
            return CreateCurrentState();
        }

        private static bool IsSequenceNewer(ushort a, ushort b)
        {
            // Handle wraparound
            return (short)(a - b) > 0;
        }

        // STANDARD DRIVER METHODS: ---------------------------------------------------------------

        public override void OnUpdate()
        {
            if (this.Character.IsDead) return;
            if (this.m_Controller == null) return;

            // Update properties
            if (m_FloorNormal != null)
            {
                m_FloorNormal.UpdateWithDelta(this.Character.Time.DeltaTime);
            }

            float floorAngle = Vector3.Angle(FloorNormal, Vector3.up);
            m_IsOnSteepSlope = IsGrounded && floorAngle > m_MaxSlope;

            // Update controller properties
            if (Math.Abs(m_Controller.skinWidth - m_SkinWidth) > float.Epsilon)
                m_Controller.skinWidth = m_SkinWidth;
            if (Math.Abs(m_Controller.slopeLimit - m_MaxSlope) > float.Epsilon)
                m_Controller.slopeLimit = m_MaxSlope;
            if (Math.Abs(m_Controller.stepOffset - m_StepHeight) > float.Epsilon)
                m_Controller.stepOffset = m_StepHeight;

            // Sync height/radius from motion
            float height = this.Character.Motion.Height;
            float radius = this.Character.Motion.Radius;
            if (Math.Abs(m_Controller.height - height) > float.Epsilon)
            {
                m_Controller.height = height;
                m_Controller.center = Vector3.zero;
            }
            if (Math.Abs(m_Controller.radius - radius) > float.Epsilon)
                m_Controller.radius = radius;
        }

        public override void SetPosition(Vector3 position, bool teleport = false)
        {
            if (m_Controller != null)
            {
                m_Controller.enabled = false;
                this.Transform.position = position;
                m_Controller.enabled = true;
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
            if (m_Controller != null && m_Controller.enabled)
            {
                Vector3 before = this.Transform.position;
                m_Controller.Move(amount);
                RecordExternalMoveVelocity(before);
            }
        }

        private void RecordExternalMoveVelocity(Vector3 before)
        {
            float deltaTime = this.Character != null
                ? this.Character.Time.DeltaTime
                : Time.deltaTime;

            if (deltaTime <= 0f) deltaTime = Time.deltaTime;
            if (deltaTime <= 0f) return;

            Vector3 actualDelta = this.Transform.position - before;
            if (actualDelta.sqrMagnitude <= 0.0000001f) return;

            this.m_MoveDirection = actualDelta / deltaTime;
        }

        public override void AddRotation(Quaternion amount)
        {
            this.Transform.rotation *= amount;
        }

        public override void AddScale(Vector3 scale)
        {
            this.Transform.localScale = Vector3.Scale(this.Transform.localScale, scale);
        }

        public override void ResetVerticalVelocity()
        {
            m_VerticalSpeed = 0f;
        }
    }
}
