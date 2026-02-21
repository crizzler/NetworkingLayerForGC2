using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.Characters;

namespace Arawn.GameCreator2.Networking
{
    /// <summary>
    /// Network-aware follow pointer player unit.
    /// Captures pointer position and sends direction to server for validation.
    /// Supports twin-stick shooter style games where character moves toward pointer.
    /// </summary>
    [Title("Network Follow Pointer (Client)")]
    [Image(typeof(IconCursor), ColorTheme.Type.Red, typeof(OverlayArrowRight))]
    [Category("Network Follow Pointer (Client)")]
    [Description("Network-aware pointer following movement. " +
                 "Pointer direction is compressed and server-validated. " +
                 "Use for twin-stick shooters or follow-cursor gameplay.")]
    [Serializable]
    public class UnitPlayerFollowPointerNetwork : TUnitPlayer
    {
        private const int BUFFER_SIZE = 32;
        
        // RAYCAST COMPARER: ----------------------------------------------------------------------
        
        private static readonly RaycastComparer RAYCAST_COMPARER = new RaycastComparer();
        
        private class RaycastComparer : IComparer<RaycastHit>
        {
            public int Compare(RaycastHit a, RaycastHit b)
            {
                return a.distance.CompareTo(b.distance);
            }
        }
        
        // EXPOSED MEMBERS: -----------------------------------------------------------------------

        [SerializeField] 
        private InputPropertyButton m_InputMove;

        [SerializeField]
        private PropertyGetInstantiate m_Indicator;
        
        [Header("Network Settings")]
        [Tooltip("Minimum direction change to trigger network send (radians)")]
        [SerializeField] private float m_DirectionThreshold = 0.1f;
        
        [Tooltip("Maximum direction updates per second")]
        [SerializeField] private float m_MaxUpdateRate = 30f;
        
        [Tooltip("Send stop command when pointer released")]
        [SerializeField] private bool m_StopOnRelease = true;

        // MEMBERS: -------------------------------------------------------------------------------
        
        [NonSerialized] private RaycastHit[] m_HitBuffer;
        [NonSerialized] private Vector3 m_Direction;
        [NonSerialized] private Vector3 m_LastSentDirection;
        [NonSerialized] private float m_LastSendTime;
        [NonSerialized] private bool m_PointerPress;
        [NonSerialized] private Vector3 m_Pointer;
        [NonSerialized] private bool m_IsInputEnabled = true;
        [NonSerialized] private bool m_WasPressed;
        
        // Network
        [NonSerialized] private UnitDriverNetworkClient m_NetworkDriver;
        [NonSerialized] private ushort m_InputSequence;

        // EVENTS: --------------------------------------------------------------------------------
        
        /// <summary>
        /// Fired when direction input should be sent to server.
        /// Direction is normalized, ready for NetworkInputState compression.
        /// </summary>
        public event Action<Vector3, ushort> OnSendDirection;
        
        /// <summary>
        /// Fired when movement stops (pointer released).
        /// </summary>
        public event Action<ushort> OnSendStop;
        
        /// <summary>
        /// Fired when pointer position changes (for visual feedback).
        /// </summary>
        public event Action<Vector3> OnPointerUpdate;

        // PROPERTIES: ----------------------------------------------------------------------------
        
        public Vector3 CurrentDirection => m_Direction;
        public Vector3 PointerPosition => m_Pointer;
        public bool IsPointerPressed => m_PointerPress;
        
        public bool IsInputEnabled
        {
            get => m_IsInputEnabled;
            set => m_IsInputEnabled = value;
        }

        // INITIALIZERS: --------------------------------------------------------------------------

        public UnitPlayerFollowPointerNetwork()
        {
            this.m_Indicator = new PropertyGetInstantiate
            {
                usePooling = true,
                size = 5,
                hasDuration = true,
                duration = 1f
            };

            this.m_InputMove = InputButtonMouseWhilePressing.Create();
        }
        
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

            this.m_HitBuffer = new RaycastHit[BUFFER_SIZE];
            
            this.m_InputMove.RegisterStart(this.OnStartPointer);
            this.m_InputMove.RegisterPerform(this.OnPerformPointer);
            this.m_InputMove.RegisterCancel(this.OnCancelPointer);
            
            this.m_Direction = Vector3.zero;
        }

        public override void OnDisable()
        {
            base.OnDisable();
            this.m_HitBuffer = Array.Empty<RaycastHit>();
            
            this.m_InputMove.ForgetStart(this.OnStartPointer);
            this.m_InputMove.ForgetPerform(this.OnPerformPointer);
            this.m_InputMove.ForgetCancel(this.OnCancelPointer);
            
            this.m_Direction = Vector3.zero;
        }

        // UPDATE METHODS: ------------------------------------------------------------------------

        public override void OnUpdate()
        {
            base.OnUpdate();
            this.m_InputMove.OnUpdate();
            
            if (!this.Character.IsPlayer) return;
            if (!m_IsInputEnabled)
            {
                m_Direction = Vector3.zero;
                this.InputDirection = Vector3.zero;
                return;
            }
            
            // Update GC2 InputDirection for compatibility
            this.InputDirection = m_Direction;
            
            // Show indicator on press
            if (this.m_PointerPress && m_Pointer != Vector3.zero)
            {
                this.m_Indicator.Get(
                    this.Character.gameObject,
                    this.m_Pointer, Quaternion.identity
                );
                
                OnPointerUpdate?.Invoke(m_Pointer);
            }
            
            // Check if direction changed enough to send
            if (ShouldSendDirection())
            {
                SendDirectionToNetwork();
            }
            
            // Feed to network driver for prediction
            if (m_NetworkDriver != null && m_Direction != Vector3.zero)
            {
                // Convert direction to input format
                Vector2 input = new Vector2(m_Direction.x, m_Direction.z);
                m_NetworkDriver.ProcessLocalInput(input, null, false);
            }
            
            // Track press state for stop detection
            m_WasPressed = m_PointerPress;
            
            // Reset per-frame state
            this.m_PointerPress = false;
        }
        
        private bool ShouldSendDirection()
        {
            // Rate limiting
            float timeSinceLastSend = Time.time - m_LastSendTime;
            if (timeSinceLastSend < 1f / m_MaxUpdateRate) return false;
            
            // Direction change threshold
            float angleDiff = Vector3.Angle(m_Direction, m_LastSentDirection) * Mathf.Deg2Rad;
            if (angleDiff < m_DirectionThreshold && m_LastSentDirection != Vector3.zero) return false;
            
            // Always send if direction is now zero (stopped)
            if (m_Direction == Vector3.zero && m_LastSentDirection != Vector3.zero) return true;
            
            // Always send if we just started moving
            if (m_Direction != Vector3.zero && m_LastSentDirection == Vector3.zero) return true;
            
            return angleDiff >= m_DirectionThreshold;
        }
        
        private void SendDirectionToNetwork()
        {
            m_InputSequence++;
            m_LastSendTime = Time.time;
            m_LastSentDirection = m_Direction;
            
            if (m_Direction != Vector3.zero)
            {
                OnSendDirection?.Invoke(m_Direction.normalized, m_InputSequence);
            }
            else if (m_StopOnRelease)
            {
                OnSendStop?.Invoke(m_InputSequence);
            }
        }
        
        // INPUT HANDLERS: ------------------------------------------------------------------------

        private void OnStartPointer()
        {
            if (!this.Character.IsPlayer) return;
            if (!this.Character.Player.IsControllable) return;
            if (!m_IsInputEnabled) return;

            this.m_PointerPress = true;
        }
        
        private void OnPerformPointer()
        {
            if (!this.Character.IsPlayer) return;
            if (!m_IsInputEnabled) return;
            
            this.m_Pointer = this.GetFollowPoint();
            this.m_Direction = (this.m_Pointer - this.Character.Feet).normalized;
            this.m_PointerPress = true;
        }
        
        private void OnCancelPointer()
        {
            if (!m_IsInputEnabled) return;
            
            // Send stop when pointer released
            if (m_StopOnRelease && m_WasPressed)
            {
                m_Direction = Vector3.zero;
                m_InputSequence++;
                OnSendStop?.Invoke(m_InputSequence);
                m_LastSentDirection = Vector3.zero;
            }
        }

        private Vector3 GetFollowPoint()
        {
            if (!this.m_IsControllable) return this.Character.Feet;
            
            Camera camera = ShortcutMainCamera.Get<Camera>();
            if (camera == null) return this.Character.Feet;
            
            Ray ray = camera.ScreenPointToRay(Application.isMobilePlatform
                ? Touchscreen.current.primaryTouch.position.ReadValue()
                : Mouse.current.position.ReadValue()
            );

            int hitCount = Physics.RaycastNonAlloc(
                ray, this.m_HitBuffer,
                Mathf.Infinity, -1,
                QueryTriggerInteraction.Ignore
            );
            
            Array.Sort(this.m_HitBuffer, 0, hitCount, RAYCAST_COMPARER);
            
            if (hitCount == 0) return this.Character.Feet;
            
            int colliderLayer = this.m_HitBuffer[0].transform.gameObject.layer;
            if ((colliderLayer & LAYER_UI) > 0) return this.Character.Feet;
            
            Plane plane = new Plane(Vector3.up, this.Character.Feet);
            if (!plane.Raycast(ray, out float rayDistance)) return this.Character.Feet;
            
            Vector3 pointer = ray.GetPoint(rayDistance);

            float curDistance = Vector3.Distance(this.Character.Feet, pointer); 
            float minDistance = this.Character.Motion?.Radius ?? 0.5f;
            
            return curDistance >= minDistance ? pointer : this.Character.Feet;
        }
        
        // PUBLIC METHODS: ------------------------------------------------------------------------
        
        /// <summary>
        /// Inject direction programmatically (for AI, virtual joystick, etc.)
        /// </summary>
        public void InjectDirection(Vector3 direction)
        {
            if (!m_IsInputEnabled) return;
            m_Direction = direction.sqrMagnitude > 1f ? direction.normalized : direction;
        }
        
        /// <summary>
        /// Connect to network driver after initialization.
        /// </summary>
        public void SetNetworkDriver(UnitDriverNetworkClient driver)
        {
            m_NetworkDriver = driver;
        }

        // STRING: --------------------------------------------------------------------------------

        public override string ToString() => "Network Follow Pointer";
    }
    
    // ========================================================================================
    // NETWORK DATA STRUCTURES
    // ========================================================================================
    
    /// <summary>
    /// Compressed direction input for follow pointer (4 bytes).
    /// Uses angle encoding for efficient transmission.
    /// </summary>
    [Serializable]
    public struct NetworkPointerDirection : IEquatable<NetworkPointerDirection>
    {
        /// <summary>Direction angle in degrees (0-360), quantized to ushort.</summary>
        public ushort AngleQuantized;
        
        /// <summary>Input sequence for ordering.</summary>
        public ushort Sequence;
        
        /// <summary>
        /// Create from world direction.
        /// </summary>
        public static NetworkPointerDirection Create(Vector3 direction, ushort sequence)
        {
            float angle = Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg;
            if (angle < 0) angle += 360f;
            
            return new NetworkPointerDirection
            {
                AngleQuantized = (ushort)(angle * (65535f / 360f)),
                Sequence = sequence
            };
        }
        
        /// <summary>
        /// Create stop command (zero direction).
        /// </summary>
        public static NetworkPointerDirection CreateStop(ushort sequence)
        {
            return new NetworkPointerDirection
            {
                AngleQuantized = ushort.MaxValue, // Special value for stop
                Sequence = sequence
            };
        }
        
        /// <summary>
        /// Get world direction from compressed data.
        /// </summary>
        public Vector3 GetDirection()
        {
            if (AngleQuantized == ushort.MaxValue) return Vector3.zero; // Stop
            
            float angle = AngleQuantized * (360f / 65535f) * Mathf.Deg2Rad;
            return new Vector3(Mathf.Sin(angle), 0f, Mathf.Cos(angle));
        }
        
        /// <summary>
        /// Whether this is a stop command.
        /// </summary>
        public bool IsStop => AngleQuantized == ushort.MaxValue;
        
        public bool Equals(NetworkPointerDirection other)
        {
            return AngleQuantized == other.AngleQuantized && Sequence == other.Sequence;
        }
        
        public override bool Equals(object obj) => obj is NetworkPointerDirection other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(AngleQuantized, Sequence);
    }
}
