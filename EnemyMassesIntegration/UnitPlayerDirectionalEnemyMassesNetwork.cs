using System;
using Arawn.EnemyMasses.Runtime;
using Arawn.GameCreator2.Networking;
using GameCreator.Runtime.Common;
using UnityEngine;

namespace GameCreator.Runtime.Characters
{
    /// <summary>
    /// Network-aware directional player unit with Enemy Masses fog/minimap integration.
    /// Works with UnitDriverNetworkClient for WASD/gamepad movement in multiplayer.
    /// </summary>
    [Title("Enemy Masses Directional Network (Client)")]
    [Image(typeof(IconGamepadCross), ColorTheme.Type.Red, typeof(OverlayArrowRight))]
    [Category("Enemy Masses Directional Network (Client)")]
    [Description("Network-aware directional movement with fog of war and minimap integration. " +
                 "Input is compressed and server-validated for competitive games.")]
    [Serializable]
    public class UnitPlayerDirectionalEnemyMassesNetwork : TUnitPlayer, IRTSFogRevealer, IRTSMinimapIcon
    {
        // EXPOSED MEMBERS: -----------------------------------------------------------------------
        
        [SerializeField] private InputPropertyValueVector2 m_InputMove = InputValueVector2MotionPrimary.Create();
        [SerializeField] private InputPropertyButton m_InputJump = InputButtonJump.Create();
        [SerializeField] private Transform m_CameraTransform;
        
        [Header("Camera")]
        [SerializeField] private bool m_UseDriverCamera = true;

        [Header("Fog of War")]
        [SerializeField] private bool m_RevealFogOfWar = true;
        [SerializeField] private float m_FogRevealRadius = 10f;

        [Header("Minimap")]
        [SerializeField] private bool m_ShowOnMinimap = true;
        [SerializeField] private Color m_MinimapColor = new Color(0.2f, 1f, 0.2f, 1f);
        
        [Header("Network Settings")]
        [Tooltip("Minimum input change to trigger network send")]
        [SerializeField] private float m_InputDeadzone = 0.1f;

        // MEMBERS: -------------------------------------------------------------------------------
        
        [NonSerialized] private Vector2 m_CurrentInput;
        [NonSerialized] private Vector2 m_LastSentInput;
        [NonSerialized] private bool m_JumpPressed;
        [NonSerialized] private bool m_JumpConsumed;
        [NonSerialized] private bool m_IsInputEnabled = true;
        [NonSerialized] private UnitDriverNetworkClient m_NetworkDriver;
        
        // Network identity
        [NonSerialized] private ulong m_NetworkId;
        [NonSerialized] private bool m_IsLocalPlayer;

        // EVENTS: --------------------------------------------------------------------------------
        
        /// <summary>
        /// Fired when input changes significantly.
        /// </summary>
        public event Action<Vector2, bool> OnInputCaptured;

        // PROPERTIES: ----------------------------------------------------------------------------
        
        public Vector2 RawInput => m_CurrentInput;
        public bool IsJumpPressed => m_JumpPressed;
        
        public bool IsInputEnabled
        {
            get => m_IsInputEnabled;
            set => m_IsInputEnabled = value;
        }
        
        public ulong NetworkId
        {
            get => m_NetworkId;
            set => m_NetworkId = value;
        }
        
        public bool IsLocalPlayer
        {
            get => m_IsLocalPlayer;
            set
            {
                m_IsLocalPlayer = value;
                if (value)
                {
                    RegisterWithRegistry();
                }
                else
                {
                    UnregisterFromRegistry();
                }
            }
        }

        // INITIALIZERS: --------------------------------------------------------------------------

        public override void OnStartup(Character character)
        {
            base.OnStartup(character);
            
            this.m_InputMove.OnStartup();
            this.m_InputJump.OnStartup();
            
            this.m_InputJump.RegisterPerform(OnJumpPerformed);
            
            // Try to find network driver
            m_NetworkDriver = character.Driver as UnitDriverNetworkClient;
            
            if (m_IsLocalPlayer)
            {
                RegisterWithRegistry();
            }
        }

        public override void AfterStartup(Character character)
        {
            base.AfterStartup(character);
            
            if (m_IsLocalPlayer)
            {
                RegisterWithRegistry();
            }
        }

        public override void OnDispose(Character character)
        {
            base.OnDispose(character);
            
            this.m_InputJump.ForgetPerform(OnJumpPerformed);
            
            this.m_InputMove.OnDispose();
            this.m_InputJump.OnDispose();
            
            UnregisterFromRegistry();
        }

        public override void OnEnable()
        {
            base.OnEnable();
            if (m_IsLocalPlayer)
            {
                RegisterWithRegistry();
            }
        }

        public override void OnDisable()
        {
            base.OnDisable();
            m_CurrentInput = Vector2.zero;
            m_JumpPressed = false;
            UnregisterFromRegistry();
        }

        private void RegisterWithRegistry()
        {
            RTSWorldUnitRegistry.RegisterFogRevealer(this);
            RTSWorldUnitRegistry.RegisterMinimapIcon(this);
        }

        private void UnregisterFromRegistry()
        {
            RTSWorldUnitRegistry.UnregisterFogRevealer(this);
            RTSWorldUnitRegistry.UnregisterMinimapIcon(this);
        }

        // UPDATE METHOD: -------------------------------------------------------------------------

        public override void OnUpdate()
        {
            base.OnUpdate();
            
            if (this.Character == null) return;
            
            // Only process input for local player
            if (!m_IsLocalPlayer || !m_IsInputEnabled)
            {
                m_CurrentInput = Vector2.zero;
                m_JumpPressed = false;
                this.InputDirection = Vector3.zero;
                return;
            }
            
            // Capture raw input
            m_CurrentInput = m_InputMove.Read();
            
            // Clamp magnitude to prevent cheating
            if (m_CurrentInput.sqrMagnitude > 1f)
            {
                m_CurrentInput = m_CurrentInput.normalized;
            }
            
            // Apply deadzone
            if (m_CurrentInput.sqrMagnitude < m_InputDeadzone * m_InputDeadzone)
            {
                m_CurrentInput = Vector2.zero;
            }
            
            // Calculate input direction for GC2 compatibility
            this.InputDirection = GetMoveDirection(m_CurrentInput);
            
            // Check if input changed significantly
            bool inputChanged = Vector2.Distance(m_CurrentInput, m_LastSentInput) > m_InputDeadzone;
            
            if (inputChanged || m_JumpPressed)
            {
                OnInputCaptured?.Invoke(m_CurrentInput, m_JumpPressed);
                m_LastSentInput = m_CurrentInput;
            }
            
            // Feed input to network driver
            if (m_NetworkDriver != null)
            {
                Transform camTransform = GetCameraTransform();
                m_NetworkDriver.ProcessLocalInput(m_CurrentInput, camTransform, m_JumpPressed && !m_JumpConsumed);
                
                if (m_JumpPressed)
                {
                    m_JumpConsumed = true;
                    m_JumpPressed = false;
                }
            }
        }

        private void OnJumpPerformed()
        {
            if (m_IsInputEnabled && m_IsLocalPlayer)
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
            
            return moveDirection.sqrMagnitude > 0.01f ? moveDirection.normalized : Vector3.zero;
        }

        private Camera GetCamera()
        {
            if (m_CameraTransform != null)
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

        // PUBLIC METHODS: ------------------------------------------------------------------------
        
        /// <summary>
        /// Inject input programmatically (for AI takeover, UI virtual joystick, etc.)
        /// </summary>
        public void InjectInput(Vector2 input, bool jump = false)
        {
            if (!m_IsInputEnabled) return;
            
            m_CurrentInput = input.sqrMagnitude > 1f ? input.normalized : input;
            if (jump) m_JumpPressed = true;
        }
        
        /// <summary>
        /// Connect to network driver after initialization.
        /// </summary>
        public void SetNetworkDriver(UnitDriverNetworkClient driver)
        {
            m_NetworkDriver = driver;
        }

        // IRTSFogRevealer ----------------------------------------------------------------------

        public bool RevealFogOfWar => m_RevealFogOfWar;

        public Vector3 FogRevealPosition
        {
            get
            {
                return this.Character != null ? this.Character.transform.position : Vector3.zero;
            }
        }

        public float FogRevealRadius => m_RevealFogOfWar ? m_FogRevealRadius : 0f;

        // IRTSMinimapIcon ----------------------------------------------------------------------

        public bool ShowOnMinimap => m_ShowOnMinimap;

        public Vector3 MinimapPosition
        {
            get
            {
                return this.Character != null ? this.Character.transform.position : Vector3.zero;
            }
        }

        public Color MinimapColor => m_MinimapColor;

        // STRING -------------------------------------------------------------------------------

        public override string ToString() => "Enemy Masses Directional Network";
    }
}
