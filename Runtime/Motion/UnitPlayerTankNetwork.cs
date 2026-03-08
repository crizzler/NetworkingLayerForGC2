using System;
using UnityEngine;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.Characters;

namespace Arawn.GameCreator2.Networking
{
    /// <summary>
    /// Network-aware tank control player unit.
    /// Movement is relative to character facing (forward/back), rotation is left/right.
    /// Input is compressed and server-validated for competitive games.
    /// </summary>
    [Title("Network Tank (Client)")]
    [Image(typeof(IconTank), ColorTheme.Type.Red, typeof(OverlayArrowRight))]
    [Category("Network Tank (Client)")]
    [Description("Network-aware tank controls. Forward/back moves relative to character facing, " +
                 "left/right rotates. Input is compressed and server-validated.")]
    [Serializable]
    public class UnitPlayerTankNetwork : TUnitPlayer
    {
        // EXPOSED MEMBERS: -----------------------------------------------------------------------

        [SerializeField] private InputPropertyValueVector2 m_InputMove = InputValueVector2MotionPrimary.Create();
        
        [Header("Network Settings")]
        [Tooltip("Minimum input change to trigger network send")]
        [SerializeField] private float m_InputDeadzone = 0.1f;
        
        [Tooltip("Maximum input updates per second")]
        [SerializeField] private float m_MaxUpdateRate = 30f;

        // MEMBERS: -------------------------------------------------------------------------------
        
        [NonSerialized] private Vector2 m_CurrentInput;
        [NonSerialized] private Vector2 m_LastSentInput;
        [NonSerialized] private float m_LastSendTime;
        [NonSerialized] private bool m_IsInputEnabled = true;
        
        // Network
        [NonSerialized] private UnitDriverNetworkClient m_NetworkDriver;
        [NonSerialized] private ushort m_InputSequence;

        // PROPERTIES: ----------------------------------------------------------------------------
        
        /// <summary>
        /// Force tank facing unit for proper rotation handling.
        /// </summary>
        public override Type ForceFacing => typeof(UnitFacingTank);
        
        public Vector2 RawInput => m_CurrentInput;
        
        public bool IsInputEnabled
        {
            get => m_IsInputEnabled;
            set => m_IsInputEnabled = value;
        }

        // EVENTS: --------------------------------------------------------------------------------
        
        /// <summary>
        /// Fired when tank input changes significantly.
        /// X = rotation (left/right), Y = movement (forward/back)
        /// </summary>
        public event Action<NetworkTankInput> OnSendInput;

        // INITIALIZERS: --------------------------------------------------------------------------

        public override void OnStartup(Character character)
        {
            base.OnStartup(character);
            this.m_InputMove.OnStartup();
            
            // Try to find network driver
            m_NetworkDriver = character.Driver as UnitDriverNetworkClient;
        }

        public override void OnDispose(Character character)
        {
            base.OnDispose(character);
            this.m_InputMove.OnDispose();
        }

        public override void OnEnable()
        {
            base.OnEnable();
        }

        public override void OnDisable()
        {
            base.OnDisable();
            m_CurrentInput = Vector2.zero;
        }

        // UPDATE METHODS: ------------------------------------------------------------------------

        public override void OnUpdate()
        {
            base.OnUpdate();
            this.m_InputMove.OnUpdate();

            this.InputDirection = Vector3.zero;
            
            if (!this.Character.IsPlayer) return;
            if (!m_IsInputEnabled)
            {
                m_CurrentInput = Vector2.zero;
                return;
            }
            
            // Capture raw input
            m_CurrentInput = this.m_IsControllable 
                ? this.m_InputMove.Read()
                : Vector2.zero;
            
            // Clamp magnitude
            if (m_CurrentInput.sqrMagnitude > 1f)
            {
                m_CurrentInput = m_CurrentInput.normalized;
            }
            
            // Apply deadzone
            if (Mathf.Abs(m_CurrentInput.x) < m_InputDeadzone) m_CurrentInput.x = 0;
            if (Mathf.Abs(m_CurrentInput.y) < m_InputDeadzone) m_CurrentInput.y = 0;
            
            // Calculate move direction (tank style - relative to character facing)
            this.InputDirection = GetMoveDirection(m_CurrentInput);
            
            // Check if we should send to network
            if (ShouldSendInput())
            {
                SendInputToNetwork();
            }
            
            // Feed to network driver for prediction
            if (m_NetworkDriver != null)
            {
                // For tank controls, we send the raw input and let the driver/server handle
                // the character-relative transformation
                m_NetworkDriver.ProcessLocalInput(m_CurrentInput, this.Transform, false);
            }
        }
        
        private bool ShouldSendInput()
        {
            // Rate limiting
            float timeSinceLastSend = Time.time - m_LastSendTime;
            if (timeSinceLastSend < 1f / m_MaxUpdateRate) return false;
            
            // Input change threshold
            float inputDelta = Vector2.Distance(m_CurrentInput, m_LastSentInput);
            
            // Always send if input went to zero or from zero
            bool wasZero = m_LastSentInput.sqrMagnitude < 0.01f;
            bool isZero = m_CurrentInput.sqrMagnitude < 0.01f;
            
            if (wasZero != isZero) return true;
            
            return inputDelta >= m_InputDeadzone;
        }
        
        private void SendInputToNetwork()
        {
            m_InputSequence++;
            m_LastSendTime = Time.time;
            m_LastSentInput = m_CurrentInput;
            
            var input = NetworkTankInput.Create(m_CurrentInput, m_InputSequence);
            OnSendInput?.Invoke(input);
        }

        /// <summary>
        /// Tank-style movement: forward/back is relative to character facing.
        /// </summary>
        protected virtual Vector3 GetMoveDirection(Vector3 input)
        {
            // Only use Y (forward/back), X is for rotation handled by facing unit
            Vector3 direction = new Vector3(0f, 0f, input.y);
            Vector3 moveDirection = this.Transform.TransformDirection(direction);

            moveDirection.Scale(Vector3Plane.NormalUp);
            moveDirection.Normalize();

            return moveDirection * direction.magnitude;
        }
        
        // PUBLIC METHODS: ------------------------------------------------------------------------
        
        /// <summary>
        /// Inject input programmatically (for AI, replay, etc.)
        /// </summary>
        public void InjectInput(Vector2 input)
        {
            if (!m_IsInputEnabled) return;
            m_CurrentInput = input.sqrMagnitude > 1f ? input.normalized : input;
        }
        
        /// <summary>
        /// Connect to network driver after initialization.
        /// </summary>
        public void SetNetworkDriver(UnitDriverNetworkClient driver)
        {
            m_NetworkDriver = driver;
        }

        // STRING: --------------------------------------------------------------------------------

        public override string ToString() => "Network Tank";
    }
    
    // ========================================================================================
    // NETWORK DATA STRUCTURES
    // ========================================================================================
    
    /// <summary>
    /// Compressed tank input (5 bytes).
    /// Rotation and movement packed efficiently.
    /// </summary>
    [Serializable]
    public struct NetworkTankInput : IEquatable<NetworkTankInput>
    {
        /// <summary>Rotation input (-1 to 1) quantized to sbyte.</summary>
        public sbyte RotationQuantized;
        
        /// <summary>Movement input (-1 to 1) quantized to sbyte.</summary>
        public sbyte MovementQuantized;
        
        /// <summary>Flags: bit 0 = is stopping</summary>
        public byte Flags;
        
        /// <summary>Input sequence for ordering.</summary>
        public ushort Sequence;
        
        private const byte FLAG_STOPPING = 1;
        
        /// <summary>
        /// Create from raw input.
        /// X = rotation (left/right), Y = movement (forward/back)
        /// </summary>
        public static NetworkTankInput Create(Vector2 input, ushort sequence)
        {
            return new NetworkTankInput
            {
                RotationQuantized = (sbyte)Mathf.Clamp(input.x * 127f, -127f, 127f),
                MovementQuantized = (sbyte)Mathf.Clamp(input.y * 127f, -127f, 127f),
                Flags = (input.sqrMagnitude < 0.01f) ? FLAG_STOPPING : (byte)0,
                Sequence = sequence
            };
        }
        
        /// <summary>
        /// Create stop input.
        /// </summary>
        public static NetworkTankInput CreateStop(ushort sequence)
        {
            return new NetworkTankInput
            {
                RotationQuantized = 0,
                MovementQuantized = 0,
                Flags = FLAG_STOPPING,
                Sequence = sequence
            };
        }
        
        /// <summary>
        /// Get rotation value (-1 to 1).
        /// </summary>
        public float GetRotation() => RotationQuantized / 127f;
        
        /// <summary>
        /// Get movement value (-1 to 1).
        /// </summary>
        public float GetMovement() => MovementQuantized / 127f;
        
        /// <summary>
        /// Get as Vector2 (X = rotation, Y = movement).
        /// </summary>
        public Vector2 GetInput() => new Vector2(GetRotation(), GetMovement());
        
        /// <summary>
        /// Whether this is a stop input.
        /// </summary>
        public bool IsStopping => (Flags & FLAG_STOPPING) != 0;
        
        public bool Equals(NetworkTankInput other)
        {
            return RotationQuantized == other.RotationQuantized && 
                   MovementQuantized == other.MovementQuantized &&
                   Sequence == other.Sequence;
        }
        
        public override bool Equals(object obj) => obj is NetworkTankInput other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(RotationQuantized, MovementQuantized, Sequence);
    }
    
    /// <summary>
    /// Server-side tank input validator.
    /// Validates rotation rates and movement speeds.
    /// </summary>
    public class TankInputValidator
    {
        private readonly float m_MaxRotationRate;
        private readonly float m_MaxSpeed;
        
        private float m_LastRotation;
        private float m_LastValidationTime;
        private int m_ViolationCount;
        
        public int ViolationCount => m_ViolationCount;
        
        /// <summary>
        /// Create validator with limits.
        /// </summary>
        /// <param name="maxRotationRate">Maximum rotation in degrees per second.</param>
        /// <param name="maxSpeed">Maximum movement speed.</param>
        public TankInputValidator(float maxRotationRate = 180f, float maxSpeed = 10f)
        {
            m_MaxRotationRate = maxRotationRate;
            m_MaxSpeed = maxSpeed;
        }
        
        /// <summary>
        /// Validate tank input and clamp if necessary.
        /// </summary>
        /// <param name="input">The input to validate.</param>
        /// <param name="currentRotation">Current character Y rotation.</param>
        /// <param name="validated">Output validated/clamped input.</param>
        /// <returns>True if input was valid, false if it was clamped.</returns>
        public bool ValidateInput(NetworkTankInput input, float currentRotation, out NetworkTankInput validated)
        {
            validated = input;
            bool wasValid = true;
            
            float deltaTime = Time.time - m_LastValidationTime;
            m_LastValidationTime = Time.time;
            
            if (deltaTime <= 0) return true;
            
            // Validate rotation rate
            float rotation = input.GetRotation();
            float maxRotationThisFrame = m_MaxRotationRate * deltaTime / 180f; // Normalized to -1,1
            
            if (Mathf.Abs(rotation) > maxRotationThisFrame * 1.1f) // 10% tolerance
            {
                rotation = Mathf.Clamp(rotation, -maxRotationThisFrame, maxRotationThisFrame);
                validated = NetworkTankInput.Create(
                    new Vector2(rotation, input.GetMovement()), 
                    input.Sequence
                );
                wasValid = false;
                m_ViolationCount++;
            }
            
            // Validate movement magnitude
            float movement = input.GetMovement();
            if (Mathf.Abs(movement) > 1.1f)
            {
                movement = Mathf.Clamp(movement, -1f, 1f);
                validated = NetworkTankInput.Create(
                    new Vector2(validated.GetRotation(), movement),
                    input.Sequence
                );
                wasValid = false;
                m_ViolationCount++;
            }
            
            m_LastRotation = currentRotation;
            return wasValid;
        }
        
        /// <summary>
        /// Reset violation count.
        /// </summary>
        public void ResetViolations()
        {
            m_ViolationCount = 0;
        }
    }
}
