using System;
using UnityEngine;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.Characters;

namespace Arawn.GameCreator2.Networking
{
    /// <summary>
    /// Network-aware player input unit that captures and compresses input for transmission.
    /// Works with UnitDriverNetworkClient for client-side prediction.
    /// </summary>
    [Title("Network Directional (Client)")]
    [Image(typeof(IconGamepadCross), ColorTheme.Type.Red, typeof(OverlayArrowRight) )]
    [Category("Network Directional (Client)")]
    [Description("Captures player input, compresses it, and coordinates with network driver. " +
                 "Use this for local player characters in multiplayer games.")]
    [Serializable]
    public class UnitPlayerDirectionalNetwork : TUnitPlayer
    {
        // EXPOSED MEMBERS: -----------------------------------------------------------------------

        [SerializeField] private InputPropertyValueVector2 m_InputMove = InputValueVector2MotionPrimary.Create();
        [SerializeField] private InputPropertyButton m_InputJump = InputButtonJump.Create();
        [SerializeField] private Transform m_CameraTransform;

        [Header("Network Settings")]
        [SerializeField] private bool m_UseDriverCamera = true;
        
        // MEMBERS: -------------------------------------------------------------------------------

        [NonSerialized] private Vector2 m_CurrentInput;
        [NonSerialized] private bool m_JumpPressed;
        [NonSerialized] private bool m_JumpConsumed;
        [NonSerialized] private bool m_IsInputEnabled = true;
        [NonSerialized] private UnitDriverNetworkClient m_NetworkDriver;

        // PROPERTIES: ----------------------------------------------------------------------------

        public Vector2 RawInput => m_CurrentInput;
        public bool IsJumpPressed => m_JumpPressed;

        // EVENTS: --------------------------------------------------------------------------------

        /// <summary>
        /// Fired when any input changes. Useful for UI feedback or debug.
        /// </summary>
        public event Action<Vector2, bool> OnInputCaptured;

        // INITIALIZERS: --------------------------------------------------------------------------

        public override void OnStartup(Character character)
        {
            base.OnStartup(character);
            
            this.m_InputMove.OnStartup();
            this.m_InputJump.OnStartup();
            
            // Register jump event
            this.m_InputJump.RegisterPerform(OnJumpPerformed);
            
            // Try to find network driver
            m_NetworkDriver = character.Driver as UnitDriverNetworkClient;
        }

        public override void OnDispose(Character character)
        {
            base.OnDispose(character);
            
            this.m_InputJump.ForgetPerform(OnJumpPerformed);
            
            this.m_InputMove.OnDispose();
            this.m_InputJump.OnDispose();
        }

        public override void OnEnable()
        {
            base.OnEnable();
        }

        public override void OnDisable()
        {
            base.OnDisable();
            m_CurrentInput = Vector2.zero;
            m_JumpPressed = false;
        }

        // UPDATE METHOD: -------------------------------------------------------------------------

        public override void OnUpdate()
        {
            base.OnUpdate();
            
            if (this.Character == null) return;
            if (!m_IsInputEnabled)
            {
                m_CurrentInput = Vector2.zero;
                m_JumpPressed = false;
                this.InputDirection = Vector3.zero;
                return;
            }
            
            // Capture raw input
            m_CurrentInput = m_InputMove.Read();
            
            // Clamp magnitude to prevent cheating with modified input
            if (m_CurrentInput.sqrMagnitude > 1f)
            {
                m_CurrentInput = m_CurrentInput.normalized;
            }
            
            // Calculate input direction for GC2 compatibility
            this.InputDirection = GetMoveDirection(m_CurrentInput);
            
            OnInputCaptured?.Invoke(m_CurrentInput, m_JumpPressed);
            
            // Feed input to network driver if available
            if (m_NetworkDriver != null)
            {
                Transform camTransform = GetCameraTransform();
                m_NetworkDriver.ProcessLocalInput(m_CurrentInput, camTransform, m_JumpPressed && !m_JumpConsumed);
                
                // Consume jump after sending
                if (m_JumpPressed)
                {
                    m_JumpConsumed = true;
                    m_JumpPressed = false;
                }
            }
        }
        
        private void OnJumpPerformed()
        {
            if (m_IsInputEnabled)
            {
                m_JumpPressed = true;
                m_JumpConsumed = false;
            }
        }
        
        private Vector3 GetMoveDirection(Vector2 input)
        {
            Vector3 direction = new Vector3(input.x, 0f, input.y);
            
            Camera cam = GetCamera();
            Quaternion cameraRotation = cam != null
                ? Quaternion.Euler(0f, cam.transform.eulerAngles.y, 0f)
                : Quaternion.identity;
            
            Vector3 moveDirection = cameraRotation * direction;
            moveDirection.y = 0f;
            moveDirection.Normalize();
            
            return moveDirection * direction.magnitude;
        }

        // PUBLIC METHODS: ------------------------------------------------------------------------

        /// <summary>
        /// Enable or disable input capture.
        /// </summary>
        public void SetInputEnabled(bool enabled)
        {
            m_IsInputEnabled = enabled;
            
            if (!enabled)
            {
                m_CurrentInput = Vector2.zero;
                m_JumpPressed = false;
            }
        }

        /// <summary>
        /// Inject input programmatically (for AI or testing).
        /// </summary>
        public void InjectInput(Vector2 moveInput, bool jump = false)
        {
            m_CurrentInput = moveInput;
            m_JumpPressed = jump;
            
            if (m_CurrentInput.sqrMagnitude > 1f)
            {
                m_CurrentInput = m_CurrentInput.normalized;
            }
            
            OnInputCaptured?.Invoke(m_CurrentInput, m_JumpPressed);
            
            if (m_NetworkDriver != null)
            {
                Transform camTransform = GetCameraTransform();
                m_NetworkDriver.ProcessLocalInput(m_CurrentInput, camTransform, m_JumpPressed);
            }
        }

        /// <summary>
        /// Set the camera transform to use for input direction.
        /// </summary>
        public void SetCamera(Transform cameraTransform)
        {
            m_CameraTransform = cameraTransform;
            m_UseDriverCamera = false;
        }

        /// <summary>
        /// Use the driver's camera (if axonometry provides one).
        /// </summary>
        public void UseDriverCamera()
        {
            m_UseDriverCamera = true;
        }

        // HELPER METHODS: ------------------------------------------------------------------------

        private Camera GetCamera()
        {
            if (!m_UseDriverCamera && m_CameraTransform != null)
            {
                return m_CameraTransform.GetComponent<Camera>();
            }
            
            return ShortcutMainCamera.Get<Camera>();
        }

        private Transform GetCameraTransform()
        {
            if (!m_UseDriverCamera && m_CameraTransform != null)
            {
                return m_CameraTransform;
            }
            
            Camera cam = GetCamera();
            return cam != null ? cam.transform : null;
        }
    }
}
