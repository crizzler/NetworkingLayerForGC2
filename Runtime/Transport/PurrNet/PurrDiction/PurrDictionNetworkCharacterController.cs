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
        public Vector3 rootMotionDelta;
        public float rootMotionWeight;
        public float gravityInfluence;
        public float authoritativeRootMotionAllowance;
        public PurrDictionExternalPoseCommand externalPose;
        public ushort transientSequence;
        public byte flags;

        public const byte FLAG_JUMP = 1;
        public const byte FLAG_RESET_VERTICAL = 2;
        public const byte FLAG_UPDATE_KINEMATICS = 4;
        public const byte FLAG_FORCE_GROUNDED = 8;

        public bool HasFlag(byte flag) => (flags & flag) != 0;
        public void Dispose() { }
    }

    public struct GC2PurrDictionState : IPredictedData<GC2PurrDictionState>
    {
        public Vector3 position;
        public Quaternion rotation;
        public Vector3 moveVelocity;
        public float verticalSpeed;
        public Vector3 scale;
        public ushort lastExternalPoseSequence;
        public ushort lastTrustedExternalPoseSequence;
        public ushort lastTransientSequence;
        public ulong lastJumpTick;
        public ulong lastGroundedTick;
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
                scale = a.scale + b.scale,
                lastExternalPoseSequence = a.lastExternalPoseSequence,
                lastTrustedExternalPoseSequence = a.lastTrustedExternalPoseSequence,
                lastTransientSequence = a.lastTransientSequence,
                lastJumpTick = a.lastJumpTick,
                lastGroundedTick = a.lastGroundedTick,
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
                scale = -a.scale,
                lastExternalPoseSequence = a.lastExternalPoseSequence,
                lastTrustedExternalPoseSequence = a.lastTrustedExternalPoseSequence,
                lastTransientSequence = a.lastTransientSequence,
                lastJumpTick = a.lastJumpTick,
                lastGroundedTick = a.lastGroundedTick,
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
                scale = a.scale * b,
                lastExternalPoseSequence = a.lastExternalPoseSequence,
                lastTrustedExternalPoseSequence = a.lastTrustedExternalPoseSequence,
                lastTransientSequence = a.lastTransientSequence,
                lastJumpTick = a.lastJumpTick,
                lastGroundedTick = a.lastGroundedTick,
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
    public sealed class UnitDriverPurrDiction : TUnitDriver,
        INetworkDirectionalInputSink,
        INetworkOwnerMotionAuthority,
        INetworkServerOwnerMotionAuthority
    {
        [SerializeField] private float m_SkinWidth = 0.08f;
        [SerializeField] private Axonometry m_Axonometry = new Axonometry();

        [NonSerialized] private CharacterController m_Controller;
        [NonSerialized] private Vector2 m_InputDirection;
        [NonSerialized] private Transform m_CameraTransform;
        [NonSerialized] private bool m_JumpRequested;
        [NonSerialized] private bool m_ResetVerticalRequested;
        [NonSerialized] private float m_SampledYaw;
        [NonSerialized] private Vector3 m_SampledRootMotionVelocity;
        [NonSerialized] private float m_SampledRootMotionWeight;
        [NonSerialized] private int m_LastRootMotionSampleFrame = -1;
        [NonSerialized] private Vector3 m_MoveDirection;
        [NonSerialized] private Vector3 m_FloorNormal = Vector3.up;
        [NonSerialized] private float m_VerticalSpeed;
        [NonSerialized] private bool m_IsGrounded = true;
        [NonSerialized] private bool m_TeleportRotationPending;
        [NonSerialized] private int m_TeleportRotationPendingFrame = -1;
        [NonSerialized] private IPurrDictionNativeMovementBackend m_Backend;
        [NonSerialized] private bool m_ControllerEnabledBeforeRagdoll;
        [NonSerialized] private bool m_CollisionBeforeRagdoll;
        [NonSerialized] private bool m_RagdollControllerSuspended;
        [NonSerialized] private bool m_HasOwnerPoseTarget;
        [NonSerialized] private Vector3 m_OwnerPoseTarget;

        public override Vector3 WorldMoveDirection => m_MoveDirection;
        public override Vector3 LocalMoveDirection => Transform != null
            ? Transform.InverseTransformDirection(m_MoveDirection)
            : m_MoveDirection;

        public override float SkinWidth => m_Controller != null ? m_Controller.skinWidth : m_SkinWidth;
        public override bool IsGrounded => m_ForceGrounded || m_IsGrounded;
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
            m_SampledYaw = Transform != null ? Transform.eulerAngles.y : 0f;
            m_LastRootMotionSampleFrame = -1;
            character.Ragdoll.EventBeforeStartRagdoll -= HandleStartRagdoll;
            character.Ragdoll.EventAfterStartRecover -= HandleEndRagdoll;
            character.Ragdoll.EventBeforeStartRagdoll += HandleStartRagdoll;
            character.Ragdoll.EventAfterStartRecover += HandleEndRagdoll;
        }

        public override void OnDispose(Character character)
        {
            if (character?.Ragdoll != null)
            {
                character.Ragdoll.EventBeforeStartRagdoll -= HandleStartRagdoll;
                character.Ragdoll.EventAfterStartRecover -= HandleEndRagdoll;
            }
            if (m_RagdollControllerSuspended) HandleEndRagdoll();
            base.OnDispose(character);
            m_Controller = null;
            m_Backend = null;
            m_SampledRootMotionVelocity = Vector3.zero;
            m_SampledRootMotionWeight = 0f;
            m_ResetVerticalRequested = false;
            m_TeleportRotationPending = false;
            m_TeleportRotationPendingFrame = -1;
            m_HasOwnerPoseTarget = false;
        }

        internal void AttachBackend(IPurrDictionNativeMovementBackend backend)
        {
            m_Backend = backend;
        }

        public void ProcessDirectionalInput(Vector2 inputDirection, Transform cameraTransform, bool jump)
        {
            m_InputDirection = inputDirection.sqrMagnitude > 1f
                ? inputDirection.normalized
                : inputDirection;

            m_CameraTransform = cameraTransform;
            m_JumpRequested |= jump;
        }

        public void ConsumeInput(
            out Vector2 inputDirection,
            out Transform cameraTransform,
            out bool jump,
            out bool resetVertical,
            out float yaw)
        {
            inputDirection = m_InputDirection;
            cameraTransform = m_CameraTransform;
            jump = m_JumpRequested;
            resetVertical = m_ResetVerticalRequested;
            yaw = m_SampledYaw;
            m_JumpRequested = false;
            m_ResetVerticalRequested = false;
        }

        public void GetRootMotionForTick(
            float tickDelta,
            out Vector3 delta,
            out float weight)
        {
            SampleRootMotionForCurrentFrame();
            delta = m_SampledRootMotionVelocity * Mathf.Max(0f, tickDelta);
            weight = m_SampledRootMotionWeight;
        }

        internal void SampleFrameIntent()
        {
            SampleRootMotionForCurrentFrame();
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
        internal bool IsForceGrounded => m_ForceGrounded;

        public void OpenOwnerMotionWindow(float durationSeconds)
        {
            if (Transform != null && IsFinite(Transform.position))
            {
                m_OwnerPoseTarget = Transform.position;
                m_HasOwnerPoseTarget = true;
            }
            m_Backend?.OpenOwnerMotionWindow(durationSeconds);
        }

        public void OpenServerOwnerMotionWindow(float durationSeconds, uint operationId = 0)
        {
            m_Backend?.OpenServerOwnerMotionWindow(durationSeconds, operationId);
        }

        public void CloseServerOwnerMotionWindow(float graceSeconds = 0f)
        {
            m_Backend?.CloseServerOwnerMotionWindow(graceSeconds);
        }

        public override void SetPosition(Vector3 position, bool teleport = false)
        {
            Vector3 rootPosition = ToRootPosition(position);
            if (!IsFinite(rootPosition)) return;
            if (teleport)
            {
                m_TeleportRotationPending = true;
                m_TeleportRotationPendingFrame = Time.frameCount;
            }
            if (m_Backend?.IsOwnerMotionWindowActive == true)
            {
                m_OwnerPoseTarget = rootPosition;
                m_HasOwnerPoseTarget = true;
            }
            m_Backend?.QueueExternalPosition(
                rootPosition,
                absolute: true,
                teleport: teleport);
        }

        public override void SetRotation(Quaternion rotation)
        {
            if (!IsUsableRotation(rotation)) return;
            m_SampledYaw = rotation.eulerAngles.y;

            bool externalRotation =
                (m_TeleportRotationPending &&
                 m_TeleportRotationPendingFrame == Time.frameCount) ||
                m_Backend?.IsOwnerMotionWindowActive == true ||
                m_Backend?.CanAuthorTrustedServerPose == true;
            if (externalRotation)
            {
                m_Backend?.QueueExternalRotation(rotation.normalized, absolute: true);
            }
            m_TeleportRotationPending = false;
            m_TeleportRotationPendingFrame = -1;
        }

        public override void SetScale(Vector3 scale)
        {
            if (!IsFinite(scale)) return;
            m_Backend?.QueueExternalScale(scale, absolute: true);
        }

        public override void AddPosition(Vector3 amount)
        {
            if (!IsFinite(amount)) return;
            if (m_Backend?.IsOwnerMotionWindowActive == true)
            {
                if (!m_HasOwnerPoseTarget)
                {
                    m_OwnerPoseTarget = Transform != null
                        ? Transform.position
                        : Vector3.zero;
                    m_HasOwnerPoseTarget = true;
                }
                m_OwnerPoseTarget += amount;
                m_Backend.QueueExternalPosition(
                    m_OwnerPoseTarget,
                    absolute: true,
                    teleport: false);
                return;
            }
            m_Backend?.QueueExternalPosition(amount, absolute: false, teleport: false);
        }

        public override void AddRotation(Quaternion amount)
        {
            if (!IsUsableRotation(amount)) return;
            Quaternion target = Quaternion.Euler(0f, m_SampledYaw, 0f) * amount.normalized;
            m_SampledYaw = target.eulerAngles.y;
            if (m_Backend?.IsOwnerMotionWindowActive == true ||
                m_Backend?.CanAuthorTrustedServerPose == true)
            {
                m_Backend.QueueExternalRotation(amount.normalized, absolute: false);
            }
        }

        public override void AddScale(Vector3 scale)
        {
            if (!IsFinite(scale)) return;
            m_Backend?.QueueExternalScale(scale, absolute: false);
        }

        public override void ResetVerticalVelocity()
        {
            m_VerticalSpeed = 0f;
            m_ResetVerticalRequested = true;
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

        private void SampleRootMotionForCurrentFrame()
        {
            if (m_LastRootMotionSampleFrame == Time.frameCount) return;
            m_LastRootMotionSampleFrame = Time.frameCount;
            m_SampledRootMotionVelocity = Vector3.zero;
            m_SampledRootMotionWeight = 0f;
            if (Character?.Animim == null) return;

            Vector3 delta = Character.Animim.RootMotionDeltaPosition;
            float weight = Mathf.Clamp01(Character.RootMotionPosition);
            if (!IsFinite(delta) || !IsFinite(weight) || weight <= 0f) return;

            float sampleDelta = Character.Time.DeltaTime;
            if (!IsFinite(sampleDelta) || sampleDelta <= 0f) sampleDelta = Time.deltaTime;
            if (!IsFinite(sampleDelta) || sampleDelta <= 0f) return;

            m_SampledRootMotionVelocity = delta / sampleDelta;
            m_SampledRootMotionWeight = weight;
        }

        internal void QueueHeldOwnerPoseForTick()
        {
            if (m_Backend?.IsOwnerMotionWindowActive != true)
            {
                m_HasOwnerPoseTarget = false;
                return;
            }
            if (!m_HasOwnerPoseTarget || !IsFinite(m_OwnerPoseTarget)) return;

            m_Backend.QueueExternalPosition(
                m_OwnerPoseTarget,
                absolute: true,
                teleport: false);
        }

        private void HandleStartRagdoll()
        {
            if (m_Controller == null) return;
            m_ControllerEnabledBeforeRagdoll = m_Controller.enabled;
            m_CollisionBeforeRagdoll = m_Controller.detectCollisions;
            m_RagdollControllerSuspended = true;
            m_Controller.enabled = false;
            m_Controller.detectCollisions = false;
        }

        private void HandleEndRagdoll()
        {
            if (m_Controller == null) return;
            m_Controller.detectCollisions = m_CollisionBeforeRagdoll;
            m_Controller.enabled = m_ControllerEnabledBeforeRagdoll;
            if (m_Controller.enabled) m_Controller.Move(Vector3.zero);
            m_MoveDirection = Vector3.zero;
            m_VerticalSpeed = 0f;
            m_RagdollControllerSuspended = false;
        }

        private static bool IsFinite(Vector3 value)
        {
            return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
        }

        private static bool IsUsableRotation(Quaternion value)
        {
            if (!IsFinite(value.x) || !IsFinite(value.y) ||
                !IsFinite(value.z) || !IsFinite(value.w)) return false;
            return value.x * value.x + value.y * value.y +
                   value.z * value.z + value.w * value.w > 0.000001f;
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
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
        private const float COYOTE_TIME_SECONDS = 0.3f;
        private const byte VALID_INPUT_FLAGS =
            GC2PurrDictionInput.FLAG_JUMP |
            GC2PurrDictionInput.FLAG_RESET_VERTICAL |
            GC2PurrDictionInput.FLAG_UPDATE_KINEMATICS |
            GC2PurrDictionInput.FLAG_FORCE_GROUNDED;
        private const float MAX_ROOT_MOTION_PER_TICK = 4f;

        [SerializeField] private float m_MaxSlope = 45f;
        [SerializeField] private float m_StepHeight = 0.3f;

        private UnitDriverPurrDiction m_Driver;
        private CharacterController m_Controller;
        private ushort m_LastExternalPoseSequence;
        private ushort m_LastTrustedExternalPoseSequence;
        private ulong m_LastJumpTick = ulong.MaxValue;
        private ulong m_LastGroundedTick = ulong.MaxValue;
        private ushort m_LastTransientSequence;
        private ushort m_NextTransientSequence;

        public override IUnitDriver CreateDriver(
            NetworkCharacter networkCharacter,
            NetworkCharacter.NetworkRole role)
        {
            EnsureReferences(networkCharacter);
            m_Driver ??= new UnitDriverPurrDiction();
            m_Driver.AttachBackend(this);
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
            m_Driver?.AttachBackend(this);
            PublishDriverState(GetStateFromTransform());
        }

        protected override void OnBackendReset(NetworkCharacter networkCharacter)
        {
            m_Driver?.AttachBackend(null);
            m_Driver = null;
            m_Controller = null;
            m_LastExternalPoseSequence = 0;
            m_LastTrustedExternalPoseSequence = 0;
            m_LastJumpTick = ulong.MaxValue;
            m_LastGroundedTick = ulong.MaxValue;
            m_LastTransientSequence = 0;
            m_NextTransientSequence = 0;
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

            TryResolveFiniteRootPose(
                state.position,
                state.rotation,
                state.scale,
                out Vector3 safePosition,
                out Quaternion safeRotation,
                out Vector3 safeScale);
            state.position = safePosition;
            state.rotation = safeRotation;
            state.scale = safeScale;

            m_LastExternalPoseSequence = state.lastExternalPoseSequence;
            m_LastTrustedExternalPoseSequence = state.lastTrustedExternalPoseSequence;
            m_LastJumpTick = state.lastJumpTick;
            m_LastGroundedTick = state.lastGroundedTick;
            m_LastTransientSequence = state.lastTransientSequence;
            if (GameCreatorCharacter?.Ragdoll?.IsRagdoll == true)
            {
                PublishDriverState(state);
                return;
            }

            bool wasEnabled = m_Controller != null && m_Controller.enabled;
            if (m_Controller != null) m_Controller.enabled = false;
            transform.SetPositionAndRotation(state.position, state.rotation.normalized);
            transform.localScale = state.scale;
            if (m_Controller != null) m_Controller.enabled = wasEnabled;

            RememberRootPose(state.position, state.rotation, state.scale);
            PublishDriverState(state);
        }

        protected override void GetFinalInput(ref GC2PurrDictionInput input)
        {
            ReadDriverInput(ref input, finalTickInput: true);
        }

        protected override void UpdateInput(ref GC2PurrDictionInput input)
        {
            ReadDriverInput(ref input, finalTickInput: false);
            m_Driver?.SampleFrameIntent();
        }

        protected override void ModifyExtrapolatedInput(ref GC2PurrDictionInput input)
        {
            input.flags &= unchecked((byte)~(
                GC2PurrDictionInput.FLAG_JUMP |
                GC2PurrDictionInput.FLAG_RESET_VERTICAL));
            input.rootMotionDelta = Vector3.zero;
            input.rootMotionWeight = 0f;
            input.externalPose.ClearOneShot();
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

            if (!IsFinite(input.gravityInfluence))
            {
                RecordCoreSecurityViolation(
                    SecurityViolationType.OutOfBoundsValue,
                    "PurrDictionDirectionalInput: invalid gravity influence");
                input.gravityInfluence = 1f;
            }
            else
            {
                input.gravityInfluence = Mathf.Clamp01(input.gravityInfluence);
            }

            input.authoritativeRootMotionAllowance =
                CaptureAuthoritativeRootMotionAllowance();

            bool validRootMotion = IsFinite(input.rootMotionDelta) &&
                                   IsFinite(input.rootMotionWeight);
            if (!validRootMotion)
            {
                RecordCoreSecurityViolation(
                    SecurityViolationType.OutOfBoundsValue,
                    "PurrDictionDirectionalInput: invalid root-motion sample");
                input.rootMotionDelta = Vector3.zero;
                input.rootMotionWeight = 0f;
            }
            else
            {
                input.rootMotionWeight = Mathf.Clamp01(input.rootMotionWeight);
                input.rootMotionDelta = Vector3.ClampMagnitude(
                    input.rootMotionDelta,
                    MAX_ROOT_MOTION_PER_TICK);
            }

            SanitizeExternalPose(
                ref input.externalPose,
                "PurrDictionDirectionalInput.ExternalPose");

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
                safeState.lastExternalPoseSequence = state.lastExternalPoseSequence;
                safeState.lastTrustedExternalPoseSequence =
                    state.lastTrustedExternalPoseSequence;
                safeState.lastJumpTick = state.lastJumpTick;
                safeState.lastGroundedTick = state.lastGroundedTick;
                safeState.lastTransientSequence = state.lastTransientSequence;
                state = safeState;
            }

            bool hasFreshTransient = input.transientSequence != 0 &&
                                     IsSequenceNewer(
                                         input.transientSequence,
                                         state.lastTransientSequence);
            if (hasFreshTransient)
            {
                state.lastTransientSequence = input.transientSequence;
            }
            else
            {
                input.flags &= unchecked((byte)~(
                    GC2PurrDictionInput.FLAG_JUMP |
                    GC2PurrDictionInput.FLAG_RESET_VERTICAL));
                input.rootMotionDelta = Vector3.zero;
                input.rootMotionWeight = 0f;
                input.externalPose.ClearOneShot();
            }

            if (!ValidateDirectionalInput(input))
            {
                input = default;
            }

            if (GameCreatorCharacter.Ragdoll?.IsRagdoll == true)
            {
                state.moveVelocity = Vector3.zero;
                state.verticalSpeed = 0f;
                m_LastTransientSequence = state.lastTransientSequence;
                PublishDriverState(state);
                return;
            }

            SetUnityState(state);
            if (GameCreatorCharacter.IsDead)
            {
                state.moveVelocity = Vector3.zero;
                state.verticalSpeed = 0f;
                PublishDriverState(state);
                return;
            }
            Vector3 tickStartPosition = transform.position;
            bool hasServerPose = TryConsumeTrustedServerPose(
                out PurrDictionExternalPoseCommand serverPose);
            bool externalPositionWillBeFinal = input.externalPose.HasPosition ||
                                               (hasServerPose && serverPose.HasPosition);

            Vector3 moveDirection = input.moveDirection;
            if (moveDirection.sqrMagnitude > 1f) moveDirection.Normalize();

            transform.rotation = Quaternion.Euler(0f, input.rotationY, 0f);

            float speed = GameCreatorCharacter.Motion != null &&
                          input.HasFlag(GC2PurrDictionInput.FLAG_UPDATE_KINEMATICS)
                ? GameCreatorCharacter.Motion.LinearSpeed
                : 0f;
            Vector3 horizontalMovement = moveDirection * speed * delta;

            bool wasGrounded = state.IsGrounded;
            bool forceGrounded =
                input.HasFlag(GC2PurrDictionInput.FLAG_FORCE_GROUNDED);
            if (!externalPositionWillBeFinal)
            {
                UpdateGravity(
                    ref state,
                    delta,
                    input.gravityInfluence,
                    forceGrounded);
                if (input.HasFlag(GC2PurrDictionInput.FLAG_RESET_VERTICAL))
                {
                    state.verticalSpeed = 0f;
                }
                ulong currentTick = CurrentPredictionTick;
                int jumpCooldownTicks = Mathf.Max(
                    1,
                    Mathf.CeilToInt(
                        GameCreatorCharacter.Motion.JumpCooldown /
                        Mathf.Max(delta, 0.0001f)));
                bool jumpCooldownReady = state.lastJumpTick == ulong.MaxValue ||
                    (currentTick >= state.lastJumpTick &&
                     currentTick - state.lastJumpTick >= (ulong)jumpCooldownTicks);
                if (input.HasFlag(GC2PurrDictionInput.FLAG_JUMP) &&
                    jumpCooldownReady &&
                    CanJump(state, currentTick, delta, forceGrounded))
                {
                    state.verticalSpeed = Mathf.Max(
                        GameCreatorCharacter.Motion.JumpForce,
                        GameCreatorCharacter.Motion.IsJumpingForce);
                    state.lastJumpTick = currentTick;
                    if (!IsPredictionReplay)
                    {
                        GameCreatorCharacter.OnJump(state.verticalSpeed);
                    }
                }
            }

            Vector3 rootMotionDelta = input.rootMotionDelta;
            float rootMotionWeight = Mathf.Clamp01(input.rootMotionWeight);
            if (ShouldValidateServerSecurity && rootMotionWeight > 0f)
            {
                rootMotionWeight = Mathf.Min(
                    rootMotionWeight,
                    Mathf.Clamp01(input.authoritativeRootMotionAllowance));
                if (rootMotionWeight <= 0f)
                {
                    rootMotionDelta = Vector3.zero;
                }
                else
                {
                    float maxRootDistance =
                        ResolveMaxAllowedHorizontalSpeed() * Mathf.Max(delta, 0.001f);
                    rootMotionDelta = Vector3.ClampMagnitude(
                        rootMotionDelta,
                        Mathf.Max(0.01f, maxRootDistance));
                }
            }

            Vector3 translation = Vector3.Lerp(
                horizontalMovement,
                rootMotionDelta,
                rootMotionWeight);
            translation = m_Driver?.Axonometry?.ProcessTranslation(m_Driver, translation) ?? translation;
            Vector3 totalMovement = translation + Vector3.up * state.verticalSpeed * delta;

            if (externalPositionWillBeFinal)
            {
                translation = Vector3.zero;
                totalMovement = Vector3.zero;
            }

            if (m_Controller.enabled)
            {
                m_Controller.Move(totalMovement);
            }

            Vector3 floorNormal = Vector3.up;
            bool grounded = forceGrounded || IsControllerGrounded(out floorNormal);
            bool inputPoseApplied = ApplyExternalPoseCommand(
                input.externalPose,
                ref state,
                delta,
                trustedServerCommand: false);
            bool serverPoseApplied = false;
            if (hasServerPose)
            {
                serverPoseApplied = ApplyExternalPoseCommand(
                    serverPose,
                    ref state,
                    delta,
                    trustedServerCommand: true);
            }

            if (externalPositionWillBeFinal)
            {
                grounded = forceGrounded || IsControllerGrounded(out floorNormal);
                if (forceGrounded) floorNormal = Vector3.up;
            }
            if (!wasGrounded && grounded && state.verticalSpeed < 0f && !IsPredictionReplay)
            {
                GameCreatorCharacter.OnLand(state.verticalSpeed);
            }
            if (grounded && state.verticalSpeed < 0f)
            {
                state.verticalSpeed = input.gravityInfluence <= 0.001f
                    ? 0f
                    : -2f * input.gravityInfluence;
            }
            if (grounded)
            {
                state.lastGroundedTick = CurrentPredictionTick;
            }

            bool acceptedTeleport =
                (inputPoseApplied && input.externalPose.IsTeleport) ||
                (serverPoseApplied && serverPose.IsTeleport);
            state.position = transform.position;
            state.rotation = transform.rotation;
            state.scale = transform.localScale;
            state.moveVelocity = acceptedTeleport || delta <= 0f
                ? Vector3.zero
                : (state.position - tickStartPosition) / delta;
            state.flags = 0;
            if (grounded) state.flags |= GC2PurrDictionState.FLAG_GROUNDED;
            if (!grounded && state.verticalSpeed > 0.01f) state.flags |= GC2PurrDictionState.FLAG_JUMPING;

            if (!ValidateDirectionalServerState(state))
            {
                state = safeState;
                SetUnityState(state);
                return;
            }

            m_LastJumpTick = state.lastJumpTick;
            m_LastGroundedTick = state.lastGroundedTick;
            m_LastTransientSequence = state.lastTransientSequence;
            PublishDriverState(state, floorNormal);
            RememberRootPose(state.position, state.rotation, state.scale);
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
                scale = Vector3.LerpUnclamped(from.scale, to.scale, t),
                lastExternalPoseSequence = t < 0.5f
                    ? from.lastExternalPoseSequence
                    : to.lastExternalPoseSequence,
                lastTrustedExternalPoseSequence = t < 0.5f
                    ? from.lastTrustedExternalPoseSequence
                    : to.lastTrustedExternalPoseSequence,
                lastTransientSequence = t < 0.5f
                    ? from.lastTransientSequence
                    : to.lastTransientSequence,
                lastJumpTick = t < 0.5f ? from.lastJumpTick : to.lastJumpTick,
                lastGroundedTick = t < 0.5f
                    ? from.lastGroundedTick
                    : to.lastGroundedTick,
                flags = t < 0.5f ? from.flags : to.flags
            };
        }

        protected override void UpdateView(GC2PurrDictionState viewState, GC2PurrDictionState? verified)
        {
            ApplyPresentationView(viewState.position, viewState.rotation, viewState.scale);
            PublishDriverState(viewState);
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
                m_Driver.AttachBackend(this);
            }
        }

        private GC2PurrDictionState GetStateFromTransform()
        {
            EnsureReferences();
            bool grounded = m_Driver?.IsForceGrounded == true ||
                            IsControllerGrounded(out _);

            return new GC2PurrDictionState
            {
                position = transform.position,
                rotation = transform.rotation,
                moveVelocity = m_Driver?.WorldMoveDirection ?? Vector3.zero,
                verticalSpeed = m_Driver?.VerticalSpeed ?? 0f,
                scale = IsUsableScale(transform.localScale)
                    ? transform.localScale
                    : Vector3.one,
                lastExternalPoseSequence = m_LastExternalPoseSequence,
                lastTrustedExternalPoseSequence =
                    m_LastTrustedExternalPoseSequence,
                lastJumpTick = m_LastJumpTick,
                lastGroundedTick = grounded
                    ? CurrentPredictionTick
                    : m_LastGroundedTick,
                lastTransientSequence = m_LastTransientSequence,
                flags = grounded ? GC2PurrDictionState.FLAG_GROUNDED : (byte)0
            };
        }

        private void ReadDriverInput(
            ref GC2PurrDictionInput input,
            bool finalTickInput)
        {
            EnsureReferences();
            if (m_Driver == null)
            {
                input = default;
                return;
            }

            m_Driver.ConsumeInput(
                out Vector2 rawInput,
                out Transform cameraTransform,
                out bool jump,
                out bool resetVertical,
                out float yaw);
            input.moveDirection = ToWorldDirection(rawInput, cameraTransform);
            input.rotationY = yaw;
            if (jump) input.flags |= GC2PurrDictionInput.FLAG_JUMP;
            if (resetVertical)
            {
                input.flags |= GC2PurrDictionInput.FLAG_RESET_VERTICAL;
            }

            if (m_Driver.UpdateKinematics)
            {
                input.flags |= GC2PurrDictionInput.FLAG_UPDATE_KINEMATICS;
            }
            else
            {
                input.flags &= unchecked((byte)~GC2PurrDictionInput.FLAG_UPDATE_KINEMATICS);
            }
            if (m_Driver.IsForceGrounded)
            {
                input.flags |= GC2PurrDictionInput.FLAG_FORCE_GROUNDED;
            }
            else
            {
                input.flags &= unchecked((byte)~GC2PurrDictionInput.FLAG_FORCE_GROUNDED);
            }
            input.gravityInfluence = Mathf.Clamp01(m_Driver.GravityInfluence);

            if (!finalTickInput) return;

            unchecked
            {
                m_NextTransientSequence++;
                if (m_NextTransientSequence == 0) m_NextTransientSequence = 1;
            }
            input.transientSequence = m_NextTransientSequence;

            m_Driver.GetRootMotionForTick(
                PredictionDelta,
                out input.rootMotionDelta,
                out input.rootMotionWeight);
            m_Driver.QueueHeldOwnerPoseForTick();
            CapturePendingExternalPose(ref input.externalPose);
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

        private void UpdateGravity(
            ref GC2PurrDictionState state,
            float delta,
            float gravityInfluence,
            bool forceGrounded)
        {
            if (GameCreatorCharacter?.Motion == null) return;

            gravityInfluence = Mathf.Clamp01(gravityInfluence);
            if ((state.IsGrounded || forceGrounded) && state.verticalSpeed <= 0f)
            {
                state.verticalSpeed = gravityInfluence <= 0.001f
                    ? 0f
                    : -2f * gravityInfluence;
                return;
            }

            float gravity = state.verticalSpeed >= 0f
                ? GameCreatorCharacter.Motion.GravityUpwards
                : GameCreatorCharacter.Motion.GravityDownwards;

            state.verticalSpeed += gravity * gravityInfluence * delta;
            state.verticalSpeed = Mathf.Max(state.verticalSpeed, GameCreatorCharacter.Motion.TerminalVelocity);
        }

        private bool CanJump(
            GC2PurrDictionState state,
            ulong currentTick,
            float delta,
            bool forceGrounded)
        {
            if (GameCreatorCharacter?.Motion == null) return false;
            if (!GameCreatorCharacter.Motion.CanJump) return false;
            if (state.IsGrounded || forceGrounded) return true;
            if (state.lastGroundedTick == ulong.MaxValue ||
                currentTick < state.lastGroundedTick) return false;

            ulong coyoteTicks = (ulong)Mathf.Max(
                1,
                Mathf.CeilToInt(COYOTE_TIME_SECONDS / Mathf.Max(delta, 0.0001f)));
            return currentTick - state.lastGroundedTick <= coyoteTicks;
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
            if (!IsValidState(state))
            {
                RecordCoreSecurityViolation(
                    SecurityViolationType.OutOfBoundsValue,
                    $"PurrDictionDirectionalState: invalid state position={state.position}, rotation={state.rotation}, velocity={state.moveVelocity}");
                return false;
            }

            if (!ShouldValidateServerSecurity) return true;

            return ValidateServerCorePosition(
                state.position,
                new Vector3(state.moveVelocity.x, 0f, state.moveVelocity.z),
                ResolveMaxAllowedHorizontalSpeed(),
                "PurrDictionDirectionalState");
        }

        private static bool IsValidState(GC2PurrDictionState state)
        {
            return IsFinite(state.position) &&
                   IsUsableRotation(state.rotation) &&
                   IsFinite(state.moveVelocity) &&
                   IsFinite(state.verticalSpeed) &&
                   IsUsableScale(state.scale);
        }

        private static bool IsSequenceNewer(ushort sequence, ushort previous)
        {
            return sequence != previous && (short)(sequence - previous) > 0;
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

        private bool ApplyExternalPoseCommand(
            PurrDictionExternalPoseCommand command,
            ref GC2PurrDictionState state,
            float delta,
            bool trustedServerCommand)
        {
            ushort sequence = trustedServerCommand
                ? state.lastTrustedExternalPoseSequence
                : state.lastExternalPoseSequence;
            if (!TryResolveExternalPose(
                    command,
                    transform.position,
                    transform.rotation,
                    transform.localScale,
                    ref sequence,
                    delta,
                    trustedServerCommand,
                    out PurrDictionResolvedExternalPose resolved))
            {
                if (trustedServerCommand)
                {
                    state.lastTrustedExternalPoseSequence = sequence;
                    m_LastTrustedExternalPoseSequence = sequence;
                }
                else
                {
                    state.lastExternalPoseSequence = sequence;
                    m_LastExternalPoseSequence = sequence;
                }
                return false;
            }

            if (trustedServerCommand)
            {
                state.lastTrustedExternalPoseSequence = sequence;
                m_LastTrustedExternalPoseSequence = sequence;
            }
            else
            {
                state.lastExternalPoseSequence = sequence;
                m_LastExternalPoseSequence = sequence;
            }
            bool controllerWasEnabled = m_Controller != null && m_Controller.enabled;
            if (resolved.hasPosition)
            {
                if (resolved.teleport && controllerWasEnabled)
                {
                    m_Controller.enabled = false;
                }

                if (resolved.teleport || m_Controller == null || !m_Controller.enabled)
                {
                    transform.position = resolved.position;
                }
                else
                {
                    m_Controller.Move(resolved.position - transform.position);
                }

                if (resolved.teleport && controllerWasEnabled)
                {
                    m_Controller.enabled = true;
                }

                if (!IsPredictionReplay && IsAuthoritativeServer)
                {
                    NetworkOwnerMotionAuthorityHooks.NotifyPositionAccepted(
                        GameCreatorCharacter,
                        transform.position);
                }
            }

            if (resolved.hasRotation) transform.rotation = resolved.rotation;
            if (resolved.hasScale) transform.localScale = resolved.scale;

            if (resolved.teleport)
            {
                state.verticalSpeed = 0f;
                state.moveVelocity = Vector3.zero;
            }

            state.position = transform.position;
            state.rotation = transform.rotation;
            state.scale = transform.localScale;
            RememberRootPose(state.position, state.rotation, state.scale);
            Physics.SyncTransforms();
            return true;
        }
    }
}
