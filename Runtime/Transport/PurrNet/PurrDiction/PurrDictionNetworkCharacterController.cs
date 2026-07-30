using System;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Common;
using PurrNet.Prediction;
using UnityEngine;
using Arawn.GameCreator2.Networking.Security;

namespace Arawn.GameCreator2.Networking.Transport.PurrNet.PurrDiction
{
    public struct GC2PurrDictionInput : IPredictedData
    {
        public Vector3 moveDirection;
        public float rotationY;
        public byte flags;

        public const byte FLAG_JUMP = 1;

        public bool HasFlag(byte flag) => (flags & flag) != 0;
        public void Dispose() { }
    }

    public struct GC2PurrDictionState : IPredictedData<GC2PurrDictionState>
    {
        public Vector3 position;
        public Quaternion rotation;
        public Vector3 moveVelocity;
        public float verticalSpeed;
        public byte flags;

        public const byte FLAG_GROUNDED = 1;
        public const byte FLAG_JUMPING = 2;

        public bool IsGrounded => (flags & FLAG_GROUNDED) != 0;
        public bool IsJumping => (flags & FLAG_JUMPING) != 0;

        public GC2PurrDictionState Add(GC2PurrDictionState a, GC2PurrDictionState b)
        {
            return new GC2PurrDictionState
            {
                position = a.position + b.position,
                rotation = new Quaternion(
                    a.rotation.x + b.rotation.x,
                    a.rotation.y + b.rotation.y,
                    a.rotation.z + b.rotation.z,
                    a.rotation.w + b.rotation.w),
                moveVelocity = a.moveVelocity + b.moveVelocity,
                verticalSpeed = a.verticalSpeed + b.verticalSpeed,
                flags = a.flags
            };
        }

        public GC2PurrDictionState Negate(GC2PurrDictionState a)
        {
            return new GC2PurrDictionState
            {
                position = -a.position,
                rotation = new Quaternion(-a.rotation.x, -a.rotation.y, -a.rotation.z, -a.rotation.w),
                moveVelocity = -a.moveVelocity,
                verticalSpeed = -a.verticalSpeed,
                flags = a.flags
            };
        }

        public GC2PurrDictionState Scale(GC2PurrDictionState a, float b)
        {
            return new GC2PurrDictionState
            {
                position = a.position * b,
                rotation = new Quaternion(a.rotation.x * b, a.rotation.y * b, a.rotation.z * b, a.rotation.w * b),
                moveVelocity = a.moveVelocity * b,
                verticalSpeed = a.verticalSpeed * b,
                flags = a.flags
            };
        }

        public void Dispose() { }
    }

    [Serializable]
    [Title("PurrDiction Character Controller")]
    [Image(typeof(IconCapsuleSolid), ColorTheme.Type.Yellow)]
    [Category("PurrDiction Character Controller")]
    [Description("GC2 driver shim for PurrDiction-owned character movement.")]
    public sealed class UnitDriverPurrDiction : TUnitDriver, INetworkDirectionalInputSink
    {
        [SerializeField] private float m_SkinWidth = 0.08f;
        [SerializeField] private Axonometry m_Axonometry = new Axonometry();

        [NonSerialized] private CharacterController m_Controller;
        [NonSerialized] private Vector2 m_InputDirection;
        [NonSerialized] private Transform m_CameraTransform;
        [NonSerialized] private bool m_JumpRequested;
        [NonSerialized] private Vector3 m_MoveDirection;
        [NonSerialized] private Vector3 m_FloorNormal = Vector3.up;
        [NonSerialized] private float m_VerticalSpeed;
        [NonSerialized] private bool m_IsGrounded = true;

        public override Vector3 WorldMoveDirection => m_MoveDirection;
        public override Vector3 LocalMoveDirection => Transform != null
            ? Transform.InverseTransformDirection(m_MoveDirection)
            : m_MoveDirection;

        public override float SkinWidth => m_Controller != null ? m_Controller.skinWidth : m_SkinWidth;
        public override bool IsGrounded => m_IsGrounded;
        public override Vector3 FloorNormal => m_FloorNormal;

        public override bool Collision
        {
            get => m_Controller != null && m_Controller.detectCollisions;
            set
            {
                if (m_Controller != null) m_Controller.detectCollisions = value;
            }
        }

        public override Axonometry Axonometry
        {
            get => m_Axonometry;
            set => m_Axonometry = value;
        }

        public override void OnStartup(Character character)
        {
            base.OnStartup(character);
            m_Controller = EnsureController(character);
        }

        public override void OnDispose(Character character)
        {
            base.OnDispose(character);
            m_Controller = null;
        }

        public void ProcessDirectionalInput(Vector2 inputDirection, Transform cameraTransform, bool jump)
        {
            m_InputDirection = inputDirection.sqrMagnitude > 1f
                ? inputDirection.normalized
                : inputDirection;

            m_CameraTransform = cameraTransform;
            m_JumpRequested |= jump;
        }

        public void ConsumeInput(out Vector2 inputDirection, out Transform cameraTransform, out bool jump)
        {
            inputDirection = m_InputDirection;
            cameraTransform = m_CameraTransform;
            jump = m_JumpRequested;
            m_JumpRequested = false;
        }

        public void ApplyPredictedState(
            Vector3 moveDirection,
            float verticalSpeed,
            bool isGrounded,
            Vector3 floorNormal)
        {
            m_MoveDirection = moveDirection;
            m_VerticalSpeed = verticalSpeed;
            m_IsGrounded = isGrounded;
            m_FloorNormal = floorNormal.sqrMagnitude > 0.0001f ? floorNormal.normalized : Vector3.up;
        }

        public float VerticalSpeed => m_VerticalSpeed;

        public override void SetPosition(Vector3 position, bool teleport = false)
        {
            Vector3 rootPosition = ToRootPosition(position);
            Vector3 before = Transform.position;

            bool wasEnabled = m_Controller != null && m_Controller.enabled;
            if (m_Controller != null) m_Controller.enabled = false;
            Transform.position = rootPosition;
            if (m_Controller != null) m_Controller.enabled = wasEnabled;

            if (!teleport)
            {
                RecordExternalMoveVelocity(before);
            }

            Physics.SyncTransforms();
        }

        public override void SetRotation(Quaternion rotation)
        {
            Transform.rotation = rotation;
            Physics.SyncTransforms();
        }

        public override void SetScale(Vector3 scale)
        {
            Transform.localScale = scale;
            Physics.SyncTransforms();
        }

        public override void AddPosition(Vector3 amount)
        {
            Vector3 before = Transform.position;

            if (m_Controller != null && m_Controller.enabled)
            {
                m_Controller.Move(amount);
            }
            else
            {
                Transform.position += amount;
            }

            RecordExternalMoveVelocity(before);
            Physics.SyncTransforms();
        }

        public override void AddRotation(Quaternion amount)
        {
            Transform.rotation *= amount;
            Physics.SyncTransforms();
        }

        public override void AddScale(Vector3 scale)
        {
            Transform.localScale = Vector3.Scale(Transform.localScale, scale);
            Physics.SyncTransforms();
        }

        public override void ResetVerticalVelocity()
        {
            m_VerticalSpeed = 0f;
        }

        internal static CharacterController EnsureController(Character character)
        {
            if (character == null) return null;

            CharacterController controller = character.GetComponent<CharacterController>();
            if (controller == null)
            {
                controller = character.gameObject.AddComponent<CharacterController>();
                controller.hideFlags = HideFlags.HideInInspector;
            }

            if (character.Motion != null)
            {
                controller.height = character.Motion.Height;
                controller.radius = character.Motion.Radius;
            }

            controller.center = Vector3.zero;
            controller.minMoveDistance = 0f;
            return controller;
        }

        private Vector3 ToRootPosition(Vector3 driverPosition)
        {
            float halfHeight = Character?.Motion != null
                ? Character.Motion.Height * 0.5f
                : 0f;

            return driverPosition + Vector3.up * halfHeight;
        }

        private void RecordExternalMoveVelocity(Vector3 before)
        {
            float deltaTime = Character != null
                ? Character.Time.DeltaTime
                : Time.deltaTime;

            if (deltaTime <= 0f) deltaTime = Time.deltaTime;
            if (deltaTime <= 0f) return;

            Vector3 actualDelta = Transform.position - before;
            if (actualDelta.sqrMagnitude <= 0.0000001f) return;

            m_MoveDirection = actualDelta / deltaTime;
        }
    }

    [AddComponentMenu("Game Creator/Network/Transport/PurrNet/PurrDiction Character Controller")]
    [DefaultExecutionOrder(-150)]
    [RequireComponent(typeof(NetworkCharacter))]
    [RequireComponent(typeof(CharacterController))]
    public sealed class PurrDictionNetworkCharacterController :
        PurrDictionNetworkCharacterControllerBase<GC2PurrDictionInput, GC2PurrDictionState>
    {
        private const float GROUND_PROBE_EXTRA = 0.08f;
        private const float MAX_DIRECTIONAL_INPUT_SQR = 1.0001f;
        private const float REJECT_DIRECTIONAL_INPUT_SQR = 1.21f;
        private const byte VALID_INPUT_FLAGS = GC2PurrDictionInput.FLAG_JUMP;

        [SerializeField] private float m_MaxSlope = 45f;
        [SerializeField] private float m_StepHeight = 0.3f;

        private UnitDriverPurrDiction m_Driver;
        private CharacterController m_Controller;

        public override IUnitDriver CreateDriver(
            NetworkCharacter networkCharacter,
            NetworkCharacter.NetworkRole role)
        {
            EnsureReferences(networkCharacter);
            m_Driver ??= new UnitDriverPurrDiction();
            return m_Driver;
        }

        protected override void OnBackendInitialized(
            NetworkCharacter networkCharacter,
            NetworkCharacter.NetworkRole role,
            bool isServer,
            bool isOwner,
            bool isHost)
        {
            EnsureReferences(networkCharacter);
            m_Driver = GameCreatorCharacter?.Driver as UnitDriverPurrDiction ?? m_Driver;
            PublishDriverState(GetStateFromTransform());
        }

        protected override void OnBackendReset(NetworkCharacter networkCharacter)
        {
            m_Driver = null;
            m_Controller = null;
        }

        protected override GC2PurrDictionState GetInitialState()
        {
            EnsureReferences();
            return GetStateFromTransform();
        }

        protected override void GetUnityState(ref GC2PurrDictionState state)
        {
            state = GetStateFromTransform();
        }

        protected override void SetUnityState(GC2PurrDictionState state)
        {
            EnsureReferences();

            bool wasEnabled = m_Controller != null && m_Controller.enabled;
            if (m_Controller != null) m_Controller.enabled = false;
            transform.SetPositionAndRotation(state.position, state.rotation.normalized);
            if (m_Controller != null) m_Controller.enabled = wasEnabled;

            PublishDriverState(state);
        }

        protected override void GetFinalInput(ref GC2PurrDictionInput input)
        {
            ReadDriverInput(ref input);
        }

        protected override void UpdateInput(ref GC2PurrDictionInput input)
        {
            ReadDriverInput(ref input);
        }

        protected override void SanitizeInput(ref GC2PurrDictionInput input)
        {
            if (!IsFinite(input.moveDirection))
            {
                RecordCoreSecurityViolation(
                    SecurityViolationType.OutOfBoundsValue,
                    "PurrDictionDirectionalInput: invalid move direction");
                input = default;
                return;
            }

            if (!IsFinite(input.rotationY))
            {
                RecordCoreSecurityViolation(
                    SecurityViolationType.OutOfBoundsValue,
                    "PurrDictionDirectionalInput: invalid yaw");
                input = default;
                return;
            }

            if ((input.flags & ~VALID_INPUT_FLAGS) != 0)
            {
                RecordCoreSecurityViolation(
                    SecurityViolationType.InvalidRequest,
                    $"PurrDictionDirectionalInput: invalid input flags={input.flags}");
                input = default;
                return;
            }

            if (Mathf.Abs(input.moveDirection.y) > 0.001f)
            {
                RecordCoreSecurityViolation(
                    SecurityViolationType.OutOfBoundsValue,
                    $"PurrDictionDirectionalInput: vertical move component={input.moveDirection.y}");
                input = default;
                return;
            }

            input.moveDirection.y = 0f;
            float moveSqrMagnitude = input.moveDirection.sqrMagnitude;
            if (moveSqrMagnitude > MAX_DIRECTIONAL_INPUT_SQR)
            {
                if (ShouldValidateServerSecurity && moveSqrMagnitude > REJECT_DIRECTIONAL_INPUT_SQR)
                {
                    RecordCoreSecurityViolation(
                        SecurityViolationType.OutOfBoundsValue,
                        $"PurrDictionDirectionalInput: oversized move vector magnitude={Mathf.Sqrt(moveSqrMagnitude):F3}");
                    input = default;
                    return;
                }

                input.moveDirection.Normalize();
            }

            input.rotationY = Mathf.Repeat(input.rotationY, 360f);
        }

        protected override void Simulate(GC2PurrDictionInput input, ref GC2PurrDictionState state, float delta)
        {
            EnsureReferences();
            if (GameCreatorCharacter == null || m_Controller == null || delta <= 0f)
            {
                return;
            }

            GC2PurrDictionState safeState = IsValidState(state)
                ? state
                : GetStateFromTransform();
            if (!IsValidState(state))
            {
                state = safeState;
            }

            if (!ValidateDirectionalInput(input))
            {
                input = default;
            }

            SetUnityState(state);

            Vector3 moveDirection = input.moveDirection;
            if (moveDirection.sqrMagnitude > 1f) moveDirection.Normalize();

            transform.rotation = Quaternion.Euler(0f, input.rotationY, 0f);

            float speed = GameCreatorCharacter.Motion != null ? GameCreatorCharacter.Motion.LinearSpeed : 0f;
            Vector3 horizontalMovement = moveDirection * speed * delta;

            UpdateGravity(ref state, delta);
            if (input.HasFlag(GC2PurrDictionInput.FLAG_JUMP) && CanJump(state))
            {
                state.verticalSpeed = GameCreatorCharacter.Motion != null ? GameCreatorCharacter.Motion.JumpForce : 0f;
            }

            Vector3 translation = ApplyRootMotionBlend(horizontalMovement);
            translation = m_Driver?.Axonometry?.ProcessTranslation(m_Driver, translation) ?? translation;
            Vector3 totalMovement = translation + Vector3.up * state.verticalSpeed * delta;

            if (m_Controller.enabled)
            {
                m_Controller.Move(totalMovement);
            }

            bool grounded = IsControllerGrounded(out Vector3 floorNormal);
            if (grounded && state.verticalSpeed < 0f)
            {
                state.verticalSpeed = -2f;
            }

            state.position = transform.position;
            state.rotation = transform.rotation;
            state.moveVelocity = delta > 0f ? translation / delta : Vector3.zero;
            state.flags = 0;
            if (grounded) state.flags |= GC2PurrDictionState.FLAG_GROUNDED;
            if (!grounded && state.verticalSpeed > 0.01f) state.flags |= GC2PurrDictionState.FLAG_JUMPING;

            if (!ValidateDirectionalServerState(state))
            {
                state = safeState;
                SetUnityState(state);
                return;
            }

            PublishDriverState(state, floorNormal);
        }

        protected override GC2PurrDictionState Interpolate(
            GC2PurrDictionState from,
            GC2PurrDictionState to,
            float t)
        {
            return new GC2PurrDictionState
            {
                position = Vector3.LerpUnclamped(from.position, to.position, t),
                rotation = Quaternion.SlerpUnclamped(from.rotation, to.rotation, t).normalized,
                moveVelocity = Vector3.LerpUnclamped(from.moveVelocity, to.moveVelocity, t),
                verticalSpeed = Mathf.LerpUnclamped(from.verticalSpeed, to.verticalSpeed, t),
                flags = t < 0.5f ? from.flags : to.flags
            };
        }

        protected override void UpdateView(GC2PurrDictionState viewState, GC2PurrDictionState? verified)
        {
            if (!isController)
            {
                SetUnityState(viewState);
            }
            else
            {
                PublishDriverState(viewState);
            }
        }

        private void EnsureReferences(NetworkCharacter networkCharacter = null)
        {
            EnsureBaseReferences(networkCharacter);
            if (m_Controller == null)
            {
                m_Controller = UnitDriverPurrDiction.EnsureController(GameCreatorCharacter);
                if (m_Controller != null)
                {
                    m_Controller.slopeLimit = m_MaxSlope;
                    m_Controller.stepOffset = m_StepHeight;
                }
            }

            if (m_Driver == null && GameCreatorCharacter?.Driver is UnitDriverPurrDiction driver)
            {
                m_Driver = driver;
            }
        }

        private GC2PurrDictionState GetStateFromTransform()
        {
            EnsureReferences();
            bool grounded = IsControllerGrounded(out _);

            return new GC2PurrDictionState
            {
                position = transform.position,
                rotation = transform.rotation,
                moveVelocity = m_Driver?.WorldMoveDirection ?? Vector3.zero,
                verticalSpeed = m_Driver?.VerticalSpeed ?? 0f,
                flags = grounded ? GC2PurrDictionState.FLAG_GROUNDED : (byte)0
            };
        }

        private void ReadDriverInput(ref GC2PurrDictionInput input)
        {
            EnsureReferences();
            if (m_Driver == null)
            {
                input = default;
                return;
            }

            m_Driver.ConsumeInput(out Vector2 rawInput, out Transform cameraTransform, out bool jump);
            input.moveDirection = ToWorldDirection(rawInput, cameraTransform);
            input.rotationY = transform.eulerAngles.y;
            if (jump) input.flags |= GC2PurrDictionInput.FLAG_JUMP;
        }

        private Vector3 ToWorldDirection(Vector2 rawInput, Transform cameraTransform)
        {
            Vector3 inputDirection = new Vector3(rawInput.x, 0f, rawInput.y);
            if (inputDirection.sqrMagnitude > 1f) inputDirection.Normalize();

            if (cameraTransform != null)
            {
                Quaternion cameraRotation = Quaternion.Euler(0f, cameraTransform.eulerAngles.y, 0f);
                inputDirection = cameraRotation * inputDirection;
            }

            inputDirection.y = 0f;
            if (inputDirection.sqrMagnitude > 1f) inputDirection.Normalize();
            return inputDirection;
        }

        private Vector3 ApplyRootMotionBlend(Vector3 kineticMovement)
        {
            if (GameCreatorCharacter?.Animim == null) return kineticMovement;
            return Vector3.Lerp(
                kineticMovement,
                GameCreatorCharacter.Animim.RootMotionDeltaPosition,
                GameCreatorCharacter.RootMotionPosition);
        }

        private void UpdateGravity(ref GC2PurrDictionState state, float delta)
        {
            if (GameCreatorCharacter?.Motion == null) return;

            if (state.IsGrounded && state.verticalSpeed <= 0f)
            {
                state.verticalSpeed = -2f;
                return;
            }

            float gravity = state.verticalSpeed >= 0f
                ? GameCreatorCharacter.Motion.GravityUpwards
                : GameCreatorCharacter.Motion.GravityDownwards;

            state.verticalSpeed += gravity * delta;
            state.verticalSpeed = Mathf.Max(state.verticalSpeed, GameCreatorCharacter.Motion.TerminalVelocity);
        }

        private bool CanJump(GC2PurrDictionState state)
        {
            if (GameCreatorCharacter?.Motion == null) return false;
            return GameCreatorCharacter.Motion.CanJump && state.IsGrounded;
        }

        private bool ValidateDirectionalInput(GC2PurrDictionInput input)
        {
            if (!ShouldValidateServerSecurity) return true;

            if (!IsFinite(input.moveDirection) || !IsFinite(input.rotationY))
            {
                RecordCoreSecurityViolation(
                    SecurityViolationType.OutOfBoundsValue,
                    $"PurrDictionDirectionalInput: non-finite input direction={input.moveDirection}, yaw={input.rotationY}");
                return false;
            }

            if ((input.flags & ~VALID_INPUT_FLAGS) != 0)
            {
                RecordCoreSecurityViolation(
                    SecurityViolationType.InvalidRequest,
                    $"PurrDictionDirectionalInput: invalid input flags={input.flags}");
                return false;
            }

            if (Mathf.Abs(input.moveDirection.y) > 0.001f ||
                input.moveDirection.sqrMagnitude > REJECT_DIRECTIONAL_INPUT_SQR)
            {
                RecordCoreSecurityViolation(
                    SecurityViolationType.OutOfBoundsValue,
                    $"PurrDictionDirectionalInput: invalid move vector={input.moveDirection}");
                return false;
            }

            return true;
        }

        private bool ValidateDirectionalServerState(GC2PurrDictionState state)
        {
            if (!ShouldValidateServerSecurity) return true;

            if (!IsValidState(state))
            {
                RecordCoreSecurityViolation(
                    SecurityViolationType.OutOfBoundsValue,
                    $"PurrDictionDirectionalState: invalid state position={state.position}, rotation={state.rotation}, velocity={state.moveVelocity}");
                return false;
            }

            return ValidateServerCorePosition(
                state.position,
                Vector3.zero,
                ResolveMaxAllowedHorizontalSpeed(),
                "PurrDictionDirectionalState");
        }

        private static bool IsValidState(GC2PurrDictionState state)
        {
            return IsFinite(state.position) &&
                   IsFinite(state.rotation) &&
                   IsFinite(state.moveVelocity) &&
                   IsFinite(state.verticalSpeed);
        }

        private bool IsControllerGrounded(out Vector3 floorNormal)
        {
            floorNormal = Vector3.up;
            if (m_Controller == null || !m_Controller.enabled) return false;
            if (m_Controller.isGrounded) return true;

            float skin = Mathf.Max(0.01f, m_Controller.skinWidth);
            float radius = Mathf.Max(0.01f, m_Controller.radius - skin);
            float halfHeight = Mathf.Max(radius, m_Controller.height * 0.5f);
            float probeDistance = Mathf.Max(0.05f, halfHeight - radius + skin + GROUND_PROBE_EXTRA);
            Vector3 center = transform.TransformPoint(m_Controller.center);

            if (!Physics.SphereCast(
                    center,
                    radius,
                    Vector3.down,
                    out RaycastHit hit,
                    probeDistance,
                    Physics.DefaultRaycastLayers,
                    QueryTriggerInteraction.Ignore))
            {
                return false;
            }

            floorNormal = hit.normal;
            return Vector3.Angle(hit.normal, Vector3.up) <= m_MaxSlope;
        }

        private void PublishDriverState(GC2PurrDictionState state)
        {
            PublishDriverState(state, Vector3.up);
        }

        private void PublishDriverState(GC2PurrDictionState state, Vector3 floorNormal)
        {
            m_Driver?.ApplyPredictedState(
                state.moveVelocity,
                state.verticalSpeed,
                state.IsGrounded,
                floorNormal);
        }
    }
}
