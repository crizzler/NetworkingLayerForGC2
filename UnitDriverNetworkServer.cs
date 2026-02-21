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

        // MEMBERS: -------------------------------------------------------------------------------

        [NonSerialized] protected CharacterController m_Controller;
        [NonSerialized] protected Vector3 m_MoveDirection;
        [NonSerialized] protected float m_VerticalSpeed;
        [NonSerialized] protected AnimVector3 m_FloorNormal;
        
        [NonSerialized] private Queue<NetworkInputState> m_InputBuffer;
        [NonSerialized] private ushort m_LastProcessedInput;
        [NonSerialized] private int m_SpeedViolations;
        [NonSerialized] private Vector3 m_LastValidatedPosition;
        [NonSerialized] private float m_ExpectedMaxSpeed;
        
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
                return this.m_Controller.isGrounded && !this.m_IsOnSteepSlope;
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
        
        public ushort LastProcessedInput => m_LastProcessedInput;
        public NetworkCharacterConfig Config => m_Config;

        // INITIALIZERS: --------------------------------------------------------------------------

        public UnitDriverNetworkServer()
        {
            this.m_MoveDirection = Vector3.zero;
            this.m_VerticalSpeed = 0f;
            this.m_InputBuffer = new Queue<NetworkInputState>(32);
        }

        public override void OnStartup(Character character)
        {
            base.OnStartup(character);

            this.m_FloorNormal = new AnimVector3(Vector3.up, 0.15f);
            this.m_InputBuffer = new Queue<NetworkInputState>(32);
            this.m_LastProcessedInput = 0;
            this.m_SpeedViolations = 0;

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
            if (this.m_Controller != null)
            {
                UnityEngine.Object.Destroy(this.m_Controller);
            }
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
                m_InputBuffer.Enqueue(input);
            }
        }

        /// <summary>
        /// Process all queued inputs and produce authoritative state.
        /// Call this at your server tick rate.
        /// </summary>
        public NetworkPositionState ProcessInputs(Transform cameraTransform = null)
        {
            while (m_InputBuffer.Count > 0)
            {
                var input = m_InputBuffer.Dequeue();
                ProcessSingleInput(input, cameraTransform);
                m_LastProcessedInput = input.sequenceNumber;
            }

            var state = CreateCurrentState();
            OnStateProduced?.Invoke(state);
            return state;
        }

        private void ProcessSingleInput(NetworkInputState input, Transform cameraTransform)
        {
            Vector2 rawInput = input.GetInputDirection();
            float deltaTime = input.GetDeltaTime();
            
            // Convert input to world direction
            Vector3 inputDirection = new Vector3(rawInput.x, 0f, rawInput.y);
            
            if (cameraTransform != null)
            {
                Quaternion cameraRotation = Quaternion.Euler(0f, cameraTransform.eulerAngles.y, 0f);
                inputDirection = cameraRotation * inputDirection;
            }
            
            inputDirection.Normalize();

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
            Vector3 totalMovement = horizontalMovement + Vector3.up * m_VerticalSpeed * deltaTime;
            
            // Move character controller
            if (m_Controller != null && m_Controller.enabled)
            {
                m_Controller.Move(totalMovement);
            }

            // Update grounded state
            if (IsGrounded && m_VerticalSpeed < 0)
            {
                m_VerticalSpeed = -2f; // Small downward force to stay grounded
                m_GroundTime = this.Character.Time.Time;
                m_GroundFrame = this.Character.Time.Frame;
            }

            // Store move direction for animation
            m_MoveDirection = horizontalMovement / deltaTime;
            
            // Update floor normal
            if (m_FloorNormal != null)
            {
                m_FloorNormal.UpdateWithDelta(deltaTime);
            }
        }

        private void UpdateGravity(float deltaTime)
        {
            if (!IsGrounded)
            {
                float gravity = m_VerticalSpeed >= 0 
                    ? this.Character.Motion.GravityUpwards 
                    : this.Character.Motion.GravityDownwards;
                
                m_VerticalSpeed += gravity * deltaTime;
                m_VerticalSpeed = Mathf.Max(m_VerticalSpeed, this.Character.Motion.TerminalVelocity);
            }
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
                m_Controller.Move(amount);
            }
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
