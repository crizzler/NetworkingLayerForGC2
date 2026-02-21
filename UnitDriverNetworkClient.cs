using System;
using System.Collections.Generic;
using UnityEngine;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.Characters;

namespace Arawn.GameCreator2.Networking
{
    /// <summary>
    /// Client-side driver with prediction and server reconciliation.
    /// Provides responsive movement while staying in sync with server authority.
    /// </summary>
    [Title("Network Character Controller (Client)")]
    [Image(typeof(IconCapsuleSolid), ColorTheme.Type.Yellow)]
    [Category("Network Character Controller (Client)")]
    [Description("Client-side driver with prediction and reconciliation. " +
                 "Provides responsive local movement that syncs with server authority.")]
    [Serializable]
    public class UnitDriverNetworkClient : TUnitDriver
    {
        // EXPOSED MEMBERS: -----------------------------------------------------------------------

        [SerializeField] protected float m_SkinWidth = 0.08f;
        [SerializeField] protected float m_MaxSlope = 45f;
        [SerializeField] protected float m_StepHeight = 0.3f;
        [SerializeField] private Axonometry m_Axonometry = new Axonometry();
        
        [Header("Network Settings")]
        [SerializeField] private NetworkCharacterConfig m_Config = new NetworkCharacterConfig();

        // MEMBERS: -------------------------------------------------------------------------------

        [NonSerialized] protected CharacterController m_Controller;
        [NonSerialized] protected Vector3 m_MoveDirection;
        [NonSerialized] protected float m_VerticalSpeed;
        [NonSerialized] protected AnimVector3 m_FloorNormal;
        
        // Prediction and reconciliation
        [NonSerialized] private List<PredictedState> m_PredictionHistory;
        [NonSerialized] private ushort m_CurrentSequence;
        [NonSerialized] private ushort m_LastAcknowledgedSequence;
        [NonSerialized] private Vector3 m_ReconciliationTarget;
        [NonSerialized] private bool m_IsReconciling;
        [NonSerialized] private float m_ReconciliationProgress;
        
        // Input buffering
        [NonSerialized] private List<NetworkInputState> m_UnacknowledgedInputs;
        [NonSerialized] private float m_InputAccumulator;
        
        [NonSerialized] protected int m_GroundFrame = -100;
        [NonSerialized] protected float m_GroundTime = -100f;
        [NonSerialized] private bool m_IsOnSteepSlope;

        // EVENTS: --------------------------------------------------------------------------------
        
        /// <summary>
        /// Fired when input should be sent to server. Contains all unacknowledged inputs for redundancy.
        /// </summary>
        public event Action<NetworkInputState[]> OnSendInput;
        
        /// <summary>
        /// Fired when reconciliation occurs (useful for debugging).
        /// </summary>
        public event Action<float> OnReconciliation;

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
        
        public ushort CurrentSequence => m_CurrentSequence;
        public NetworkCharacterConfig Config => m_Config;

        // STRUCTS: -------------------------------------------------------------------------------

        private struct PredictedState
        {
            public ushort sequence;
            public Vector3 position;
            public float rotationY;
            public float verticalSpeed;
            public NetworkInputState input;
        }

        // INITIALIZERS: --------------------------------------------------------------------------

        public UnitDriverNetworkClient()
        {
            this.m_MoveDirection = Vector3.zero;
            this.m_VerticalSpeed = 0f;
        }

        public override void OnStartup(Character character)
        {
            base.OnStartup(character);

            this.m_FloorNormal = new AnimVector3(Vector3.up, 0.15f);
            this.m_PredictionHistory = new List<PredictedState>(128);
            this.m_UnacknowledgedInputs = new List<NetworkInputState>(32);
            this.m_CurrentSequence = 0;
            this.m_LastAcknowledgedSequence = 0;
            this.m_InputAccumulator = 0f;

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

        // CLIENT-SIDE PREDICTION: ----------------------------------------------------------------

        /// <summary>
        /// Process local input with client-side prediction.
        /// Call this every frame with player input.
        /// </summary>
        public void ProcessLocalInput(Vector2 inputDirection, Transform cameraTransform, bool jump = false)
        {
            float deltaTime = this.Character.Time.DeltaTime;
            m_InputAccumulator += deltaTime;
            
            float inputInterval = 1f / m_Config.inputSendRate;
            
            // Only create input at fixed intervals
            if (m_InputAccumulator >= inputInterval)
            {
                m_InputAccumulator -= inputInterval;
                
                // Create input state
                byte flags = 0;
                if (jump && CanJump()) flags |= NetworkInputState.FLAG_JUMP;
                
                NetworkInputState input = NetworkInputState.Create(
                    inputDirection,
                    m_CurrentSequence,
                    inputInterval,
                    flags
                );
                
                // Store for potential resend
                m_UnacknowledgedInputs.Add(input);
                
                // Apply prediction locally
                ApplyInputPrediction(input, cameraTransform);
                
                // Store predicted state
                m_PredictionHistory.Add(new PredictedState
                {
                    sequence = m_CurrentSequence,
                    position = this.Transform.position,
                    rotationY = this.Transform.eulerAngles.y,
                    verticalSpeed = m_VerticalSpeed,
                    input = input
                });
                
                // Increment sequence
                m_CurrentSequence++;
                
                // Send inputs to server (with redundancy)
                SendInputsToServer();
            }
            
            // Handle reconciliation smoothing
            if (m_IsReconciling)
            {
                UpdateReconciliation(deltaTime);
            }
        }

        private void ApplyInputPrediction(NetworkInputState input, Transform cameraTransform)
        {
            Vector2 rawInput = input.GetInputDirection();
            float deltaTime = input.GetDeltaTime();
            
            // Convert to world direction
            Vector3 inputDirection = new Vector3(rawInput.x, 0f, rawInput.y);
            
            if (cameraTransform != null)
            {
                Quaternion cameraRotation = Quaternion.Euler(0f, cameraTransform.eulerAngles.y, 0f);
                inputDirection = cameraRotation * inputDirection;
            }
            
            inputDirection.Normalize();

            // Calculate movement
            float speed = this.Character.Motion.LinearSpeed;
            Vector3 horizontalMovement = inputDirection * speed * deltaTime;

            // Apply gravity
            UpdateGravity(deltaTime);

            // Handle jump
            if (input.HasFlag(NetworkInputState.FLAG_JUMP))
            {
                m_VerticalSpeed = this.Character.Motion.JumpForce;
            }

            // Combine and move
            Vector3 totalMovement = horizontalMovement + Vector3.up * m_VerticalSpeed * deltaTime;
            
            if (m_Controller != null && m_Controller.enabled)
            {
                m_Controller.Move(totalMovement);
            }

            // Update grounded
            if (IsGrounded && m_VerticalSpeed < 0)
            {
                m_VerticalSpeed = -2f;
                m_GroundTime = this.Character.Time.Time;
                m_GroundFrame = this.Character.Time.Frame;
            }

            m_MoveDirection = horizontalMovement / deltaTime;
        }

        private void SendInputsToServer()
        {
            // Send recent inputs for redundancy
            int count = Mathf.Min(m_UnacknowledgedInputs.Count, m_Config.inputRedundancy);
            if (count > 0)
            {
                NetworkInputState[] inputs = new NetworkInputState[count];
                for (int i = 0; i < count; i++)
                {
                    inputs[i] = m_UnacknowledgedInputs[m_UnacknowledgedInputs.Count - count + i];
                }
                OnSendInput?.Invoke(inputs);
            }
        }

        // SERVER RECONCILIATION: -----------------------------------------------------------------

        /// <summary>
        /// Apply authoritative state from server and reconcile if needed.
        /// Call this when receiving server state updates.
        /// </summary>
        public void ApplyServerState(NetworkPositionState serverState)
        {
            m_LastAcknowledgedSequence = serverState.lastProcessedInput;
            
            // Remove acknowledged inputs
            m_UnacknowledgedInputs.RemoveAll(i => !IsSequenceNewer(i.sequenceNumber, serverState.lastProcessedInput));
            
            // Find the predicted state at this sequence
            int predictedIndex = -1;
            for (int i = 0; i < m_PredictionHistory.Count; i++)
            {
                if (m_PredictionHistory[i].sequence == serverState.lastProcessedInput)
                {
                    predictedIndex = i;
                    break;
                }
            }
            
            if (predictedIndex >= 0)
            {
                Vector3 serverPosition = serverState.GetPosition();
                Vector3 predictedPosition = m_PredictionHistory[predictedIndex].position;
                float positionError = Vector3.Distance(serverPosition, predictedPosition);
                
                if (positionError > m_Config.reconciliationThreshold)
                {
                    // Need reconciliation
                    if (positionError > m_Config.maxReconciliationDistance)
                    {
                        // Teleport - too far off
                        TeleportTo(serverPosition, serverState.GetRotationY(), serverState.GetVerticalVelocity());
                    }
                    else
                    {
                        // Smooth reconciliation
                        StartReconciliation(serverPosition, serverState.GetRotationY(), serverState.GetVerticalVelocity(), predictedIndex);
                    }
                    
                    OnReconciliation?.Invoke(positionError);
                }
                
                // Remove old prediction history
                if (predictedIndex > 0)
                {
                    m_PredictionHistory.RemoveRange(0, predictedIndex);
                }
            }
            
            // Trim history to prevent unbounded growth
            while (m_PredictionHistory.Count > 128)
            {
                m_PredictionHistory.RemoveAt(0);
            }
        }

        private void StartReconciliation(Vector3 serverPosition, float serverRotationY, float serverVerticalSpeed, int fromIndex)
        {
            // Teleport to server position
            TeleportTo(serverPosition, serverRotationY, serverVerticalSpeed);
            
            // Re-apply all inputs after this point
            for (int i = fromIndex + 1; i < m_PredictionHistory.Count; i++)
            {
                var state = m_PredictionHistory[i];
                ApplyInputPrediction(state.input, null); // Re-predict without camera (already transformed)
                
                // Update the stored prediction
                m_PredictionHistory[i] = new PredictedState
                {
                    sequence = state.sequence,
                    position = this.Transform.position,
                    rotationY = this.Transform.eulerAngles.y,
                    verticalSpeed = m_VerticalSpeed,
                    input = state.input
                };
            }
            
            m_IsReconciling = false;
        }

        private void TeleportTo(Vector3 position, float rotationY, float verticalSpeed)
        {
            if (m_Controller != null)
            {
                m_Controller.enabled = false;
                this.Transform.position = position;
                this.Transform.rotation = Quaternion.Euler(0f, rotationY, 0f);
                m_Controller.enabled = true;
            }
            else
            {
                this.Transform.position = position;
                this.Transform.rotation = Quaternion.Euler(0f, rotationY, 0f);
            }
            
            m_VerticalSpeed = verticalSpeed;
        }

        private void UpdateReconciliation(float deltaTime)
        {
            // Smooth interpolation to reconciliation target (currently using instant reconciliation)
            m_ReconciliationProgress += deltaTime * m_Config.reconciliationSpeed;
            
            if (m_ReconciliationProgress >= 1f)
            {
                m_IsReconciling = false;
            }
        }

        // HELPER METHODS: ------------------------------------------------------------------------

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
            
            float timeSinceGrounded = this.Character.Time.Time - m_GroundTime;
            int framesSinceGrounded = this.Character.Time.Frame - m_GroundFrame;
            
            bool inCoyoteTime = timeSinceGrounded < COYOTE_TIME || framesSinceGrounded < COYOTE_FRAMES;
            return IsGrounded || inCoyoteTime;
        }

        private static bool IsSequenceNewer(ushort a, ushort b)
        {
            return (short)(a - b) > 0;
        }

        // STANDARD DRIVER METHODS: ---------------------------------------------------------------

        public override void OnUpdate()
        {
            if (this.Character.IsDead) return;
            if (this.m_Controller == null) return;

            if (m_FloorNormal != null)
            {
                m_FloorNormal.UpdateWithDelta(this.Character.Time.DeltaTime);
            }

            float floorAngle = Vector3.Angle(FloorNormal, Vector3.up);
            m_IsOnSteepSlope = IsGrounded && floorAngle > m_MaxSlope;

            // Sync controller properties
            if (Math.Abs(m_Controller.skinWidth - m_SkinWidth) > float.Epsilon)
                m_Controller.skinWidth = m_SkinWidth;
            if (Math.Abs(m_Controller.slopeLimit - m_MaxSlope) > float.Epsilon)
                m_Controller.slopeLimit = m_MaxSlope;
            if (Math.Abs(m_Controller.stepOffset - m_StepHeight) > float.Epsilon)
                m_Controller.stepOffset = m_StepHeight;

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
