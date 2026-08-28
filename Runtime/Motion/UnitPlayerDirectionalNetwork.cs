using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.EventSystems;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.Cameras;
using GameCreator.Runtime.Characters;
using UnityEngine.UI;

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
        [NonSerialized] private INetworkDirectionalInputSink m_InputSink;
        [NonSerialized] private NetworkCharacter m_NetworkCharacter;

        // PROPERTIES: ----------------------------------------------------------------------------

        public Vector2 RawInput => m_CurrentInput;
        public bool IsJumpPressed => m_JumpPressed;

        // EVENTS: --------------------------------------------------------------------------------

        /// <summary>
        /// Fired when any input changes. Useful for UI feedback or debug.
        /// </summary>
        public event Action<Vector2, bool> OnInputCaptured;

        /// <summary>
        /// Allows feature-specific systems, such as Traversal, to consume jump input before
        /// it is sent to the network movement driver as a regular jump.
        /// </summary>
        public event Func<bool> EventTryConsumeJump;

        // INITIALIZERS: --------------------------------------------------------------------------

        public override void OnStartup(Character character)
        {
            base.OnStartup(character);

            this.m_InputMove.OnStartup();
            this.m_InputJump.OnStartup();

            // Register jump event
            this.m_InputJump.RegisterPerform(OnJumpPerformed);

            // Try to find a network prediction input sink.
            m_InputSink = character.Driver as INetworkDirectionalInputSink;
            m_NetworkCharacter = character.GetComponent<NetworkCharacter>();
        }

        public override void OnDispose(Character character)
        {
            ClearNetworkInputSink();
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
            ClearNetworkInputSink();
        }

        // UPDATE METHOD: -------------------------------------------------------------------------

        public override void OnUpdate()
        {
            base.OnUpdate();
            this.m_InputMove.OnUpdate();
            this.m_InputJump.OnUpdate();

            if (this.Character == null) return;
            if (!this.Character.IsPlayer)
            {
                if (!TryRestoreLocalNetworkPlayerFlag())
                {
                    TraceCiInputHealth("blocked", "character-is-player-false");
                    m_CurrentInput = Vector2.zero;
                    m_JumpPressed = false;
                    this.InputDirection = Vector3.zero;

                    // A network player unit can remain authored on an explicit NPC prefab while
                    // NetworkCharacter swaps only its Driver for the current authority role. GC2
                    // runs Player before Motion and Driver, so clearing Motion.MoveDirection here
                    // erased an AI MoveToDirection request before the authoritative NavMesh driver
                    // could consume it. Match GC2's built-in directional unit: a non-player owns no
                    // input and therefore must leave the shared Motion command untouched.
                    ClearNetworkInputSink();
                    return;
                }

                TraceCiInputHealth("player-flag-restored", "authenticated-local-owner");
            }

            if (!m_IsInputEnabled)
            {
                TraceCiInputHealth("blocked", "unit-input-disabled");
                m_CurrentInput = Vector2.zero;
                m_JumpPressed = false;
                this.InputDirection = Vector3.zero;
                ClearMotionDirection();
                ClearNetworkInputSink();
                return;
            }

            if (NetworkGameplayInputBlocker.IsTextInputFocused())
            {
                TraceCiInputHealth("blocked", "text-input-focused");
                m_CurrentInput = Vector2.zero;
                m_JumpPressed = false;
                m_JumpConsumed = false;
                this.InputDirection = Vector3.zero;
                ClearMotionDirection();

                RefreshNetworkDriver();
                if (m_InputSink != null)
                {
                    Transform camTransform = GetCameraTransform();
                    m_InputSink.ProcessDirectionalInput(Vector2.zero, camTransform, false);
                }

                return;
            }

            if (!this.m_IsControllable)
            {
                TraceCiInputHealth("blocked", "character-not-controllable");
                m_CurrentInput = Vector2.zero;
                m_JumpPressed = false;
                m_JumpConsumed = false;
                this.InputDirection = Vector3.zero;
                ClearMotionDirection();

                RefreshNetworkDriver();
                m_InputSink?.ProcessDirectionalInput(
                    Vector2.zero,
                    GetCameraTransform(),
                    false);
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
            SetMotionDirection(this.InputDirection);
            LogFocusedTraversalInput();

            OnInputCaptured?.Invoke(m_CurrentInput, m_JumpPressed);
            TryConsumeJumpExternally();

            // Feed input to network driver if available
            RefreshNetworkDriver();
            if (m_InputSink != null)
            {
                Transform camTransform = GetCameraTransform();
                m_InputSink.ProcessDirectionalInput(m_CurrentInput, camTransform, m_JumpPressed && !m_JumpConsumed);

                // Consume jump after sending
                if (m_JumpPressed)
                {
                    m_JumpConsumed = true;
                    m_JumpPressed = false;
                }
            }

            TraceCiInputHealth(
                "sample",
                m_InputSink != null ? "input-forwarded" : "network-input-sink-missing");
        }

        private void LogFocusedTraversalInput()
        {
            if (this.Character == null ||
                !NetworkTraversalClimbDiagnostics.IsFocused(this.Character.gameObject))
            {
                return;
            }

            m_NetworkCharacter ??= this.Character.GetComponent<NetworkCharacter>();
            uint networkId = m_NetworkCharacter != null ? m_NetworkCharacter.NetworkId : 0;
            string role = m_NetworkCharacter != null
                ? m_NetworkCharacter.CurrentRole.ToString()
                : "Local";
            Vector3 playerWorld = this.Character.Player?.InputDirection ?? Vector3.zero;
            Vector3 playerLocal = this.Character.Player?.LocalInputDirection ?? Vector3.zero;
            Vector3 motionMove = this.Character.Motion?.MoveDirection ?? Vector3.zero;
            Vector3 driverWorld = this.Character.Driver?.WorldMoveDirection ?? Vector3.zero;
            Vector3 driverLocal = this.Character.Driver?.LocalMoveDirection ?? Vector3.zero;
            string signature =
                $"{AxisSign(m_CurrentInput.x)},{AxisSign(m_CurrentInput.y)}:" +
                $"{AxisSign(playerLocal.x)},{AxisSign(playerLocal.y)},{AxisSign(playerLocal.z)}:" +
                $"{m_JumpPressed}:{m_IsInputEnabled}:{this.m_IsControllable}";
            bool changed = NetworkTraversalClimbDiagnostics.HasChanged(
                $"player-input:{this.Character.GetLegacyInstanceId()}",
                signature);

            NetworkTraversalClimbDiagnostics.Log(
                changed ? "InputChange" : "Input",
                $"actor={networkId} role={role} raw={NetworkTraversalClimbDiagnostics.Vector(m_CurrentInput)} " +
                $"unitInput={NetworkTraversalClimbDiagnostics.Vector(this.InputDirection)} " +
                $"playerWorld={NetworkTraversalClimbDiagnostics.Vector(playerWorld)} " +
                $"playerLocal={NetworkTraversalClimbDiagnostics.Vector(playerLocal)} " +
                $"motionMove={NetworkTraversalClimbDiagnostics.Vector(motionMove)} " +
                $"driverWorld={NetworkTraversalClimbDiagnostics.Vector(driverWorld)} " +
                $"driverLocal={NetworkTraversalClimbDiagnostics.Vector(driverLocal)} " +
                $"jump={m_JumpPressed} enabled={m_IsInputEnabled} controllable={this.m_IsControllable}",
                this.Character,
                changed ? null : $"player-input:{this.Character.GetLegacyInstanceId()}");
        }

        private static int AxisSign(float value)
        {
            return value > 0.05f ? 1 : value < -0.05f ? -1 : 0;
        }

        [System.Diagnostics.Conditional("UNITY_EDITOR")]
        [System.Diagnostics.Conditional("DEVELOPMENT_BUILD")]
        private void TraceCiInputHealth(string stage, string reason)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (!NetworkCiTrace.TraversalTraceWindowActive || this.Character == null)
            {
                return;
            }

            RefreshNetworkCharacter();
            if (m_NetworkCharacter == null || !m_NetworkCharacter.IsOwnerInstance ||
                !m_NetworkCharacter.HasAuthenticatedPlayerOwner)
            {
                return;
            }

            uint actorId = m_NetworkCharacter.NetworkId;
            bool shortcutMatches = ShortcutPlayer.Instance == this.Character.gameObject;
            string signature =
                $"{stage}:{reason}:{this.Character.IsPlayer}:{m_IsInputEnabled}:" +
                $"{this.m_IsControllable}:{shortcutMatches}:" +
                $"{this.Character.Driver?.GetType().Name ?? "<none>"}:" +
                $"{m_InputSink?.GetType().Name ?? "<none>"}";
            string stateKey = $"gc2-local-input-state:{actorId}";
            string sampleKey = $"gc2-local-input-sample:{actorId}";
            if (!NetworkCiTrace.HasChanged(stateKey, signature) &&
                !NetworkCiTrace.ShouldSample(sampleKey, 0.5f))
            {
                return;
            }

            Vector3 playerWorld = this.Character.Player?.InputDirection ?? Vector3.zero;
            Vector3 playerLocal = this.Character.Player?.LocalInputDirection ?? Vector3.zero;
            Vector3 motionMove = this.Character.Motion?.MoveDirection ?? Vector3.zero;
            string shortcut = ShortcutPlayer.Instance != null
                ? ShortcutPlayer.Instance.name
                : "<none>";
            NetworkCiTrace.Log(
                "gc2-player-input",
                stage,
                actorId,
                0,
                $"reason={reason} role={m_NetworkCharacter.CurrentRole} " +
                $"isPlayer={this.Character.IsPlayer} controllable={this.m_IsControllable} " +
                $"unitEnabled={m_IsInputEnabled} objectActive={this.Character.gameObject.activeInHierarchy} " +
                $"shortcut='{shortcut}' shortcutMatches={shortcutMatches} " +
                $"raw={NetworkTraversalClimbDiagnostics.Vector(m_CurrentInput)} " +
                $"unitInput={NetworkTraversalClimbDiagnostics.Vector(this.InputDirection)} " +
                $"playerWorld={NetworkTraversalClimbDiagnostics.Vector(playerWorld)} " +
                $"playerLocal={NetworkTraversalClimbDiagnostics.Vector(playerLocal)} " +
                $"motionMove={NetworkTraversalClimbDiagnostics.Vector(motionMove)} " +
                $"driver={this.Character.Driver?.GetType().Name ?? "<none>"} " +
                $"sink={m_InputSink?.GetType().Name ?? "<none>"} " +
                $"updateKinematics={this.Character.Driver?.UpdateKinematics ?? false} " +
                $"position={this.Character.transform.position:F3}",
                this.Character);
#endif
        }

        private void OnJumpPerformed()
        {
            if (m_IsInputEnabled &&
                this.m_IsControllable &&
                !NetworkGameplayInputBlocker.IsTextInputFocused() &&
                this.Character != null &&
                this.Character.IsPlayer)
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
                ClearNetworkInputSink();
            }
        }

        /// <summary>
        /// Inject input programmatically (for AI or testing).
        /// </summary>
        public void InjectInput(Vector2 moveInput, bool jump = false)
        {
            if (!m_IsInputEnabled || !this.m_IsControllable)
            {
                m_CurrentInput = Vector2.zero;
                m_JumpPressed = false;
                m_JumpConsumed = false;
                this.InputDirection = Vector3.zero;
                ClearMotionDirection();
                ClearNetworkInputSink();
                return;
            }

            m_CurrentInput = moveInput;
            m_JumpPressed = jump;

            if (m_CurrentInput.sqrMagnitude > 1f)
            {
                m_CurrentInput = m_CurrentInput.normalized;
            }

            this.InputDirection = GetMoveDirection(m_CurrentInput);
            SetMotionDirection(this.InputDirection);

            OnInputCaptured?.Invoke(m_CurrentInput, m_JumpPressed);
            TryConsumeJumpExternally();

            RefreshNetworkDriver();
            if (m_InputSink != null)
            {
                Transform camTransform = GetCameraTransform();
                m_InputSink.ProcessDirectionalInput(m_CurrentInput, camTransform, m_JumpPressed);
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

        private void ClearNetworkInputSink()
        {
            RefreshNetworkDriver();
            m_InputSink?.ProcessDirectionalInput(
                Vector2.zero,
                GetCameraTransform(),
                false);
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
            if (ReferenceEquals(m_InputSink, this.Character.Driver)) return;
            m_InputSink = this.Character.Driver as INetworkDirectionalInputSink;
        }

        private bool TryConsumeJumpExternally()
        {
            if (!m_JumpPressed) return false;
            if (EventTryConsumeJump == null) return false;

            bool consumed = false;
            foreach (Delegate subscriber in EventTryConsumeJump.GetInvocationList())
            {
                try
                {
                    if (subscriber is Func<bool> handler && handler.Invoke())
                    {
                        consumed = true;
                    }
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                }
            }

            if (!consumed) return false;

            if (this.Character != null &&
                NetworkTraversalClimbDiagnostics.IsFocused(this.Character.gameObject))
            {
                NetworkTraversalClimbDiagnostics.Log(
                    "JumpInput",
                    $"operation=consumed-externally character='{this.Character.name}' " +
                    $"input={NetworkTraversalClimbDiagnostics.Vector(m_CurrentInput)}",
                    this.Character);
            }

            m_JumpConsumed = true;
            m_JumpPressed = false;
            return true;
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
            if (m_InputSink == null || !ReferenceEquals(m_InputSink, this.Character.Driver)) return false;

            this.Character.IsPlayer = true;
            ShortcutPlayer.Change(this.Character.gameObject);
            return true;
        }
    }

    internal static class NetworkGameplayInputBlocker
    {
        public static bool IsTextInputFocused()
        {
            EventSystem eventSystem = EventSystem.current;
            GameObject selected = eventSystem != null ? eventSystem.currentSelectedGameObject : null;
            if (selected == null) return false;

            InputField inputField = selected.GetComponent<InputField>();
            if (inputField != null)
            {
                return inputField.isFocused;
            }

            Component tmpInputField = selected.GetComponent("TMP_InputField");
            if (tmpInputField == null) return false;

            PropertyInfo isFocused = tmpInputField.GetType().GetProperty(
                "isFocused",
                BindingFlags.Instance | BindingFlags.Public
            );

            return isFocused?.GetValue(tmpInputField) is true;
        }
    }
}
