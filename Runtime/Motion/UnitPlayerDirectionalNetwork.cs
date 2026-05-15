using System;
using UnityEngine;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.Cameras;
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
        [SerializeField] private PropertyGetGameObject m_Camera = GetGameObjectCameraMain.Create;

        [SerializeField, HideInInspector] private Transform m_CameraTransform;
        [SerializeField, HideInInspector] private bool m_UseDriverCamera = true;

        [Header("Network Settings")]
        [Tooltip("Mirrors local input into GC2 Motion.MoveDirection so facing and animation units can read steering intent. Actual movement still uses the network driver.")]
        [SerializeField] private bool m_UpdateMotionDirection = true;
        
        // MEMBERS: -------------------------------------------------------------------------------

        [NonSerialized] private Vector2 m_CurrentInput;
        [NonSerialized] private bool m_JumpPressed;
        [NonSerialized] private bool m_JumpConsumed;
        [NonSerialized] private bool m_IsInputEnabled = true;
        [NonSerialized] private UnitDriverNetworkClient m_NetworkDriver;
        [NonSerialized] private NetworkCharacter m_NetworkCharacter;

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
            m_NetworkCharacter = character.GetComponent<NetworkCharacter>();
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
            ClearMotionDirection();
        }

        // UPDATE METHOD: -------------------------------------------------------------------------

        public override void OnUpdate()
        {
            base.OnUpdate();
            this.m_InputMove.OnUpdate();
            this.m_InputJump.OnUpdate();
            
            if (this.Character == null) return;
            if (!this.Character.IsPlayer && !TryRestoreLocalNetworkPlayerFlag())
            {
                m_CurrentInput = Vector2.zero;
                m_JumpPressed = false;
                this.InputDirection = Vector3.zero;
                ClearMotionDirection();
                return;
            }

            if (!m_IsInputEnabled)
            {
                m_CurrentInput = Vector2.zero;
                m_JumpPressed = false;
                this.InputDirection = Vector3.zero;
                ClearMotionDirection();
                return;
            }
            
            // Capture raw input
            m_CurrentInput = this.m_IsControllable
                ? m_InputMove.Read()
                : Vector2.zero;
            
            // Clamp magnitude to prevent cheating with modified input
            if (m_CurrentInput.sqrMagnitude > 1f)
            {
                m_CurrentInput = m_CurrentInput.normalized;
            }
            
            // Calculate input direction for GC2 compatibility
            this.InputDirection = GetMoveDirection(m_CurrentInput);
            SetMotionDirection(this.InputDirection);
            
            OnInputCaptured?.Invoke(m_CurrentInput, m_JumpPressed);
            
            // Feed input to network driver if available
            RefreshNetworkDriver();
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
            if (m_IsInputEnabled && this.Character != null && this.Character.IsPlayer)
            {
                m_JumpPressed = true;
                m_JumpConsumed = false;
            }
        }
        
        private Vector3 GetMoveDirection(Vector2 input)
        {
            Vector3 direction = new Vector3(input.x, 0f, input.y);

            Transform camera = GetCameraTransform();
            Quaternion cameraRotation = camera != null
                ? Quaternion.Euler(0f, camera.rotation.eulerAngles.y, 0f)
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
                this.InputDirection = Vector3.zero;
                ClearMotionDirection();
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

            this.InputDirection = GetMoveDirection(m_CurrentInput);
            SetMotionDirection(this.InputDirection);
            
            OnInputCaptured?.Invoke(m_CurrentInput, m_JumpPressed);
            
            RefreshNetworkDriver();
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
            m_Camera = GetGameObjectInstance.Create(cameraTransform);
            m_CameraTransform = cameraTransform;
            m_UseDriverCamera = false;
        }

        /// <summary>
        /// Use GC2's main camera for input direction.
        /// </summary>
        public void UseDriverCamera()
        {
            m_Camera = GetGameObjectCameraMain.Create;
            m_CameraTransform = null;
            m_UseDriverCamera = true;
        }

        // HELPER METHODS: ------------------------------------------------------------------------

        private Transform GetCameraTransform()
        {
            if (!m_UseDriverCamera && m_CameraTransform != null)
            {
                return m_CameraTransform;
            }

            m_Camera ??= GetGameObjectCameraMain.Create;
            Args args = this.Character != null
                ? new Args(this.Character.gameObject)
                : Args.EMPTY;

            Transform camera = m_Camera.Get<Transform>(args);
            return camera != null ? camera : this.Camera;
        }

        private void SetMotionDirection(Vector3 direction)
        {
            if (!m_UpdateMotionDirection) return;
            if (this.Character?.Motion is not TUnitMotion motion) return;

            float speed = motion.LinearSpeed;
            Vector3 velocity = direction * speed;

            motion.MoveDirection = velocity;
            motion.MovePosition = this.Transform.position + velocity;
        }

        private void ClearMotionDirection()
        {
            SetMotionDirection(Vector3.zero);
        }

        private void RefreshNetworkDriver()
        {
            if (this.Character == null) return;
            if (ReferenceEquals(m_NetworkDriver, this.Character.Driver)) return;
            m_NetworkDriver = this.Character.Driver as UnitDriverNetworkClient;
        }

        private void RefreshNetworkCharacter()
        {
            if (this.Character == null) return;
            if (m_NetworkCharacter != null) return;
            m_NetworkCharacter = this.Character.GetComponent<NetworkCharacter>();
        }

        private bool TryRestoreLocalNetworkPlayerFlag()
        {
            RefreshNetworkDriver();
            RefreshNetworkCharacter();

            if (m_NetworkCharacter == null) return false;
            if (m_NetworkCharacter.Role != NetworkCharacter.NetworkRole.LocalClient) return false;
            if (!m_NetworkCharacter.IsOwnerInstance) return false;
            if (m_NetworkDriver == null || !ReferenceEquals(m_NetworkDriver, this.Character.Driver)) return false;

            this.Character.IsPlayer = true;
            ShortcutPlayer.Change(this.Character.gameObject);
            return true;
        }

    }
}
