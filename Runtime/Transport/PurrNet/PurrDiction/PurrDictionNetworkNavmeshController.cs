using System;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Common;
using PurrNet.Prediction;
using UnityEngine;
using UnityEngine.AI;
using Arawn.GameCreator2.Networking.Security;

namespace Arawn.GameCreator2.Networking.Transport.PurrNet.PurrDiction
{
    public struct GC2PurrDictionNavMeshInput : IPredictedData
    {
        public byte commandType;
        public ushort sequence;
        public Vector3 target;
        public byte flags;

        public const byte FLAG_HAS_COMMAND = 1;
        public const byte FLAG_STOP_IMMEDIATE = 2;

        public bool HasCommand => HasFlag(FLAG_HAS_COMMAND);
        public bool HasFlag(byte flag) => (flags & flag) != 0;

        public static GC2PurrDictionNavMeshInput Create(
            byte commandType,
            ushort sequence,
            Vector3 target,
            byte flags = 0)
        {
            return new GC2PurrDictionNavMeshInput
            {
                commandType = commandType,
                sequence = sequence,
                target = target,
                flags = (byte)(flags | FLAG_HAS_COMMAND)
            };
        }

        public void Dispose() { }
    }

    public struct GC2PurrDictionNavMeshState : IPredictedData<GC2PurrDictionNavMeshState>
    {
        public Vector3 position;
        public Quaternion rotation;
        public Vector3 velocity;
        public Vector3 destination;
        public ushort activeSequence;
        public byte commandType;
        public byte pathStatus;
        public int currentCornerIndex;
        public byte flags;

        public const byte FLAG_GROUNDED = 1;
        public const byte FLAG_HAS_PATH = 2;
        public const byte FLAG_MOVING = 4;

        public bool IsGrounded => (flags & FLAG_GROUNDED) != 0;
        public bool HasPath => (flags & FLAG_HAS_PATH) != 0;
        public bool IsMoving => (flags & FLAG_MOVING) != 0;

        public GC2PurrDictionNavMeshState Add(
            GC2PurrDictionNavMeshState a,
            GC2PurrDictionNavMeshState b)
        {
            return new GC2PurrDictionNavMeshState
            {
                position = a.position + b.position,
                rotation = new Quaternion(
                    a.rotation.x + b.rotation.x,
                    a.rotation.y + b.rotation.y,
                    a.rotation.z + b.rotation.z,
                    a.rotation.w + b.rotation.w),
                velocity = a.velocity + b.velocity,
                destination = a.destination + b.destination,
                activeSequence = a.activeSequence,
                commandType = a.commandType,
                pathStatus = a.pathStatus,
                currentCornerIndex = a.currentCornerIndex,
                flags = a.flags
            };
        }

        public GC2PurrDictionNavMeshState Negate(GC2PurrDictionNavMeshState a)
        {
            return new GC2PurrDictionNavMeshState
            {
                position = -a.position,
                rotation = new Quaternion(-a.rotation.x, -a.rotation.y, -a.rotation.z, -a.rotation.w),
                velocity = -a.velocity,
                destination = -a.destination,
                activeSequence = a.activeSequence,
                commandType = a.commandType,
                pathStatus = a.pathStatus,
                currentCornerIndex = a.currentCornerIndex,
                flags = a.flags
            };
        }

        public GC2PurrDictionNavMeshState Scale(GC2PurrDictionNavMeshState a, float b)
        {
            return new GC2PurrDictionNavMeshState
            {
                position = a.position * b,
                rotation = new Quaternion(a.rotation.x * b, a.rotation.y * b, a.rotation.z * b, a.rotation.w * b),
                velocity = a.velocity * b,
                destination = a.destination * b,
                activeSequence = a.activeSequence,
                commandType = a.commandType,
                pathStatus = a.pathStatus,
                currentCornerIndex = a.currentCornerIndex,
                flags = a.flags
            };
        }

        public void Dispose() { }
    }

    [Serializable]
    [Title("PurrDiction NavMesh Controller")]
    [Image(typeof(IconCharacterWalk), ColorTheme.Type.Yellow)]
    [Category("PurrDiction NavMesh Controller")]
    [Description("GC2 driver shim for PurrDiction-owned NavMesh movement.")]
    public sealed class UnitDriverPurrDictionNavmesh : TUnitDriver, INetworkNavMeshCommandSink
    {
        [SerializeField] private float m_SkinWidth = 0.08f;
        [SerializeField] private Axonometry m_Axonometry = new Axonometry();

        [NonSerialized] private NavMeshAgent m_Agent;
        [NonSerialized] private CapsuleCollider m_Capsule;
        [NonSerialized] private GC2PurrDictionNavMeshInput m_PendingInput;
        [NonSerialized] private bool m_HasPendingInput;
        [NonSerialized] private ushort m_CurrentSequence;
        [NonSerialized] private Vector3 m_Velocity;
        [NonSerialized] private bool m_IsGrounded = true;
        [NonSerialized] private byte m_PathStatus = NetworkNavMeshPathState.STATUS_NONE;

        public override Vector3 WorldMoveDirection => m_Velocity;
        public override Vector3 LocalMoveDirection => Transform != null
            ? Transform.InverseTransformDirection(m_Velocity)
            : m_Velocity;

        public override float SkinWidth => m_SkinWidth;
        public override bool IsGrounded => m_ForceGrounded || m_IsGrounded;
        public override Vector3 FloorNormal => Vector3.up;

        public override bool Collision
        {
            get => m_Capsule != null && m_Capsule.enabled;
            set
            {
                if (m_Capsule != null) m_Capsule.enabled = value;
            }
        }

        public override Axonometry Axonometry
        {
            get => m_Axonometry;
            set => m_Axonometry = value;
        }

        public byte PathStatus => m_PathStatus;
        public ushort CurrentSequence => m_CurrentSequence;

        public override void OnStartup(Character character)
        {
            base.OnStartup(character);
            m_Agent = EnsureAgent(character);
            m_Capsule = EnsureCapsule(character);
        }

        public override void OnDispose(Character character)
        {
            base.OnDispose(character);
            m_Agent = null;
            m_Capsule = null;
        }

        public void RequestMoveToPosition(Vector3 target)
        {
            m_CurrentSequence++;
            QueueInput(GC2PurrDictionNavMeshInput.Create(
                NetworkNavMeshCommand.CMD_MOVE_TO_POSITION,
                m_CurrentSequence,
                target));
        }

        public void RequestMoveToDirection(Vector3 direction)
        {
            m_CurrentSequence++;
            QueueInput(GC2PurrDictionNavMeshInput.Create(
                NetworkNavMeshCommand.CMD_MOVE_TO_DIRECTION,
                m_CurrentSequence,
                direction.sqrMagnitude > 1f ? direction.normalized : direction));
        }

        public void RequestStop(bool immediate = false)
        {
            m_CurrentSequence++;
            QueueInput(GC2PurrDictionNavMeshInput.Create(
                NetworkNavMeshCommand.CMD_STOP,
                m_CurrentSequence,
                Vector3.zero,
                immediate ? GC2PurrDictionNavMeshInput.FLAG_STOP_IMMEDIATE : (byte)0));
        }

        public void RequestWarp(Vector3 position)
        {
            m_CurrentSequence++;
            QueueInput(GC2PurrDictionNavMeshInput.Create(
                NetworkNavMeshCommand.CMD_WARP,
                m_CurrentSequence,
                position));
        }

        public bool ConsumeInput(ref GC2PurrDictionNavMeshInput input)
        {
            if (!m_HasPendingInput) return false;

            input = m_PendingInput;
            m_PendingInput = default;
            m_HasPendingInput = false;
            return true;
        }

        public void ApplyPredictedState(
            Vector3 velocity,
            bool isGrounded,
            byte pathStatus)
        {
            m_Velocity = velocity;
            m_IsGrounded = isGrounded;
            m_PathStatus = pathStatus;
        }

        public override void SetPosition(Vector3 position, bool teleport = false)
        {
            Vector3 rootPosition = ToRootPosition(position);
            Transform.position = rootPosition;
            if (teleport && m_Agent != null && m_Agent.enabled && m_Agent.isOnNavMesh)
            {
                m_Agent.Warp(rootPosition);
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
            Transform.position += amount;
            Physics.SyncTransforms();
        }

        public override void AddRotation(Quaternion amount)
        {
            Transform.rotation *= amount;
            Physics.SyncTransforms();
        }

        public override void AddScale(Vector3 scale)
        {
            Transform.localScale += scale;
            Physics.SyncTransforms();
        }

        public override void ResetVerticalVelocity()
        { }

        internal static NavMeshAgent EnsureAgent(Character character)
        {
            if (character == null) return null;

            NavMeshAgent agent = character.GetComponent<NavMeshAgent>();
            if (agent == null)
            {
                agent = character.gameObject.AddComponent<NavMeshAgent>();
                agent.hideFlags = HideFlags.HideInInspector;
            }

            agent.updatePosition = false;
            agent.updateRotation = false;
            agent.updateUpAxis = false;
            agent.autoBraking = false;
            agent.autoRepath = false;

            if (character.Motion != null)
            {
                agent.speed = character.Motion.LinearSpeed;
                agent.radius = character.Motion.Radius;
                agent.height = character.Motion.Height;
            }

            return agent;
        }

        internal static CapsuleCollider EnsureCapsule(Character character)
        {
            if (character == null) return null;

            CapsuleCollider capsule = character.GetComponent<CapsuleCollider>();
            if (capsule == null)
            {
                capsule = character.gameObject.AddComponent<CapsuleCollider>();
                capsule.hideFlags = HideFlags.HideInInspector;
            }

            if (character.Motion != null)
            {
                capsule.height = character.Motion.Height;
                capsule.radius = character.Motion.Radius;
            }

            capsule.center = Vector3.zero;
            return capsule;
        }

        internal void UpdateAgentSettings()
        {
            if (m_Agent == null || Character?.Motion == null) return;

            m_Agent.speed = Character.Motion.LinearSpeed;
            m_Agent.radius = Character.Motion.Radius;
            m_Agent.height = Character.Motion.Height;
            if (m_Agent.enabled && m_Agent.isOnNavMesh)
            {
                m_Agent.nextPosition = Transform.position;
            }

            if (m_Capsule == null) return;
            m_Capsule.height = Character.Motion.Height;
            m_Capsule.radius = Character.Motion.Radius;
        }

        private void QueueInput(GC2PurrDictionNavMeshInput input)
        {
            m_PendingInput = input;
            m_HasPendingInput = true;
        }

        private Vector3 ToRootPosition(Vector3 driverPosition)
        {
            float halfHeight = Character?.Motion != null
                ? Character.Motion.Height * 0.5f
                : 0f;

            return driverPosition + Vector3.up * halfHeight;
        }
    }

    [AddComponentMenu("Game Creator/Network/Transport/PurrNet/PurrDiction NavMesh Controller")]
    [DefaultExecutionOrder(-150)]
    [RequireComponent(typeof(NetworkCharacter))]
    [RequireComponent(typeof(NavMeshAgent))]
    public sealed class PurrDictionNetworkNavmeshController :
        PurrDictionNetworkCharacterControllerBase<GC2PurrDictionNavMeshInput, GC2PurrDictionNavMeshState>
    {
        private const float ARRIVAL_THRESHOLD = 0.1f;
        private const float NAVMESH_SAMPLE_DISTANCE = 2f;
        private const float MAX_DIRECTION_INPUT_SQR = 1.0001f;
        private const float REJECT_DIRECTION_INPUT_SQR = 1.21f;
        private const byte VALID_INPUT_FLAGS =
            GC2PurrDictionNavMeshInput.FLAG_HAS_COMMAND |
            GC2PurrDictionNavMeshInput.FLAG_STOP_IMMEDIATE;

        [Header("NavMesh Server Authority")]
        [SerializeField] private bool m_EnableCommandSecurityValidation = true;
        [SerializeField] private bool m_EnableClickValidation = true;
        [SerializeField] private ClickValidationConfig m_ClickValidationConfig;
        [SerializeField] private bool m_AllowClientWarpCommands = false;

        private UnitDriverPurrDictionNavmesh m_Driver;
        private NavMeshAgent m_Agent;
        private NavMeshPath m_Path;
        private Vector3[] m_PathCorners;
        private ushort m_PathSequence;
        private Vector3 m_PathDestination;
        private GC2PurrDictionNavMeshState m_LastState;
        private ClickValidator m_ClickValidator;

        private bool ShouldValidateNavMeshCommands =>
            ShouldValidateServerSecurity && m_EnableCommandSecurityValidation;

        public override IUnitDriver CreateDriver(
            NetworkCharacter networkCharacter,
            NetworkCharacter.NetworkRole role)
        {
            EnsureReferences(networkCharacter);
            m_Driver ??= new UnitDriverPurrDictionNavmesh();
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
            m_Driver = GameCreatorCharacter?.Driver as UnitDriverPurrDictionNavmesh ?? m_Driver;
            EnsureClickValidator();
            PublishDriverState(GetStateFromTransform());
        }

        protected override void OnBackendReset(NetworkCharacter networkCharacter)
        {
            DisposeClickValidator();
            m_Driver = null;
            m_Agent = null;
            m_Path = null;
            m_PathCorners = null;
            m_LastState = default;
        }

        protected override GC2PurrDictionNavMeshState GetInitialState()
        {
            EnsureReferences();
            return GetStateFromTransform();
        }

        protected override void GetUnityState(ref GC2PurrDictionNavMeshState state)
        {
            state = GetStateFromTransform();
        }

        protected override void SetUnityState(GC2PurrDictionNavMeshState state)
        {
            EnsureReferences();
            transform.SetPositionAndRotation(state.position, state.rotation.normalized);
            PublishDriverState(state);
        }

        protected override void GetFinalInput(ref GC2PurrDictionNavMeshInput input)
        {
            ReadDriverInput(ref input);
        }

        protected override void UpdateInput(ref GC2PurrDictionNavMeshInput input)
        {
            ReadDriverInput(ref input);
        }

        protected override void SanitizeInput(ref GC2PurrDictionNavMeshInput input)
        {
            if (!input.HasCommand) return;

            if (!IsKnownCommand(input.commandType))
            {
                RecordCoreSecurityViolation(
                    SecurityViolationType.InvalidRequest,
                    $"PurrDictionNavMeshInput: unknown command={input.commandType}");
                if (!ShouldValidateNavMeshCommands) input = default;
                return;
            }

            if (input.sequence == 0)
            {
                RecordCoreSecurityViolation(
                    SecurityViolationType.InvalidRequest,
                    $"PurrDictionNavMeshInput: missing sequence for {GetCommandName(input.commandType)}");
                if (!ShouldValidateNavMeshCommands) input = default;
                return;
            }

            if ((input.flags & ~VALID_INPUT_FLAGS) != 0)
            {
                RecordCoreSecurityViolation(
                    SecurityViolationType.InvalidRequest,
                    $"PurrDictionNavMeshInput: invalid flags={input.flags}");
                if (!ShouldValidateNavMeshCommands) input = default;
                return;
            }

            if (!IsFinite(input.target))
            {
                RecordCoreSecurityViolation(
                    SecurityViolationType.OutOfBoundsValue,
                    $"PurrDictionNavMeshInput: invalid target={input.target}");
                if (!ShouldValidateNavMeshCommands) input = default;
                return;
            }

            switch (input.commandType)
            {
                case NetworkNavMeshCommand.CMD_MOVE_TO_DIRECTION:
                    if (Mathf.Abs(input.target.y) > 0.001f && ShouldValidateNavMeshCommands)
                    {
                        RecordCoreSecurityViolation(
                            SecurityViolationType.OutOfBoundsValue,
                            $"PurrDictionNavMeshInput: vertical direction component={input.target.y}");
                        return;
                    }

                    input.target.y = 0f;
                    float targetSqrMagnitude = input.target.sqrMagnitude;
                    if (targetSqrMagnitude > MAX_DIRECTION_INPUT_SQR)
                    {
                        if (ShouldValidateNavMeshCommands &&
                            targetSqrMagnitude > REJECT_DIRECTION_INPUT_SQR)
                        {
                            RecordCoreSecurityViolation(
                                SecurityViolationType.OutOfBoundsValue,
                                $"PurrDictionNavMeshInput: oversized direction magnitude={Mathf.Sqrt(targetSqrMagnitude):F3}");
                            return;
                        }

                        input.target.Normalize();
                    }
                    break;

                case NetworkNavMeshCommand.CMD_STOP:
                    input.target = Vector3.zero;
                    break;

                case NetworkNavMeshCommand.CMD_WARP:
                    if (ShouldValidateNavMeshCommands &&
                        !m_AllowClientWarpCommands &&
                        !IsServerOwnedPrediction())
                    {
                        RecordCoreSecurityViolation(
                            SecurityViolationType.UnauthorizedAction,
                            "PurrDictionNavMeshInput: client warp command rejected");
                    }
                    break;
            }
        }

        protected override void Simulate(
            GC2PurrDictionNavMeshInput input,
            ref GC2PurrDictionNavMeshState state,
            float delta)
        {
            EnsureReferences();
            if (GameCreatorCharacter == null || delta <= 0f) return;

            GC2PurrDictionNavMeshState safeState = IsValidState(state)
                ? state
                : GetStateFromTransform();
            if (!IsValidState(state))
            {
                state = safeState;
            }

            SetUnityState(state);
            m_Driver?.UpdateAgentSettings();
            EnsureClickValidator();

            if (input.HasCommand && IsSequenceNewer(input.sequence, state.activeSequence))
            {
                if (TryValidateCommand(input, ref state, out GC2PurrDictionNavMeshInput validatedInput))
                {
                    ApplyCommand(validatedInput, ref state);
                }
                else
                {
                    RejectCommand(input, ref state);
                }
            }

            switch (state.commandType)
            {
                case NetworkNavMeshCommand.CMD_MOVE_TO_POSITION:
                    SimulatePath(ref state, delta);
                    break;
                case NetworkNavMeshCommand.CMD_MOVE_TO_DIRECTION:
                    SimulateDirection(ref state, delta);
                    break;
            }

            state.position = transform.position;
            state.rotation = transform.rotation;
            if (state.velocity.sqrMagnitude <= 0.000001f)
            {
                state.flags &= unchecked((byte)~GC2PurrDictionNavMeshState.FLAG_MOVING);
            }

            state.flags |= GC2PurrDictionNavMeshState.FLAG_GROUNDED;
            if (!ValidateNavMeshServerState(state))
            {
                state = safeState;
                SetUnityState(state);
                return;
            }

            PublishDriverState(state);
        }

        protected override GC2PurrDictionNavMeshState Interpolate(
            GC2PurrDictionNavMeshState from,
            GC2PurrDictionNavMeshState to,
            float t)
        {
            return new GC2PurrDictionNavMeshState
            {
                position = Vector3.LerpUnclamped(from.position, to.position, t),
                rotation = Quaternion.SlerpUnclamped(from.rotation, to.rotation, t).normalized,
                velocity = Vector3.LerpUnclamped(from.velocity, to.velocity, t),
                destination = t < 0.5f ? from.destination : to.destination,
                activeSequence = t < 0.5f ? from.activeSequence : to.activeSequence,
                commandType = t < 0.5f ? from.commandType : to.commandType,
                pathStatus = t < 0.5f ? from.pathStatus : to.pathStatus,
                currentCornerIndex = t < 0.5f ? from.currentCornerIndex : to.currentCornerIndex,
                flags = t < 0.5f ? from.flags : to.flags
            };
        }

        protected override void UpdateView(
            GC2PurrDictionNavMeshState viewState,
            GC2PurrDictionNavMeshState? verified)
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
            if (m_Agent == null)
            {
                m_Agent = UnitDriverPurrDictionNavmesh.EnsureAgent(GameCreatorCharacter);
            }

            m_Path ??= new NavMeshPath();
            if (m_Driver == null && GameCreatorCharacter?.Driver is UnitDriverPurrDictionNavmesh driver)
            {
                m_Driver = driver;
            }
        }

        private GC2PurrDictionNavMeshState GetStateFromTransform()
        {
            GC2PurrDictionNavMeshState state = m_LastState;
            state.position = transform.position;
            state.rotation = transform.rotation;
            state.velocity = m_Driver?.WorldMoveDirection ?? state.velocity;
            state.flags |= GC2PurrDictionNavMeshState.FLAG_GROUNDED;
            return state;
        }

        private void ReadDriverInput(ref GC2PurrDictionNavMeshInput input)
        {
            EnsureReferences();
            m_Driver?.ConsumeInput(ref input);
        }

        private void ApplyCommand(
            GC2PurrDictionNavMeshInput input,
            ref GC2PurrDictionNavMeshState state)
        {
            state.activeSequence = input.sequence;
            state.commandType = input.commandType;
            state.destination = input.target;
            state.currentCornerIndex = 0;
            state.velocity = Vector3.zero;
            state.flags = GC2PurrDictionNavMeshState.FLAG_GROUNDED;

            switch (input.commandType)
            {
                case NetworkNavMeshCommand.CMD_MOVE_TO_POSITION:
                    BuildPath(ref state);
                    break;
                case NetworkNavMeshCommand.CMD_MOVE_TO_DIRECTION:
                    state.destination = input.target.sqrMagnitude > 1f
                        ? input.target.normalized
                        : input.target;
                    state.pathStatus = NetworkNavMeshPathState.STATUS_NONE;
                    state.flags |= GC2PurrDictionNavMeshState.FLAG_MOVING;
                    ClearPathCache();
                    break;
                case NetworkNavMeshCommand.CMD_STOP:
                    Stop(ref state);
                    break;
                case NetworkNavMeshCommand.CMD_WARP:
                    Stop(ref state);
                    state.position = input.target;
                    transform.position = input.target;
                    break;
            }
        }

        private void BuildPath(ref GC2PurrDictionNavMeshState state)
        {
            ClearPathCache();

            if (!TrySampleNavMesh(transform.position, out Vector3 start) ||
                !TrySampleNavMesh(state.destination, out Vector3 destination) ||
                m_Path == null ||
                !NavMesh.CalculatePath(start, destination, NavMesh.AllAreas, m_Path) ||
                m_Path.status == NavMeshPathStatus.PathInvalid ||
                m_Path.corners == null ||
                m_Path.corners.Length == 0)
            {
                state.pathStatus = NetworkNavMeshPathState.STATUS_INVALID;
                state.flags &= unchecked((byte)~GC2PurrDictionNavMeshState.FLAG_HAS_PATH);
                state.velocity = Vector3.zero;
                RecordCoreSecurityViolation(
                    SecurityViolationType.InvalidTarget,
                    $"PurrDictionNavMeshPath: invalid path to {state.destination}");
                return;
            }

            state.destination = destination;
            state.pathStatus = m_Path.status == NavMeshPathStatus.PathPartial
                ? NetworkNavMeshPathState.STATUS_PARTIAL
                : NetworkNavMeshPathState.STATUS_COMPLETE;
            state.currentCornerIndex = 0;
            state.flags |= GC2PurrDictionNavMeshState.FLAG_HAS_PATH |
                           GC2PurrDictionNavMeshState.FLAG_MOVING;

            m_PathCorners = m_Path.corners;
            m_PathSequence = state.activeSequence;
            m_PathDestination = destination;
        }

        private void SimulatePath(ref GC2PurrDictionNavMeshState state, float delta)
        {
            if (!EnsurePathForState(ref state))
            {
                state.velocity = Vector3.zero;
                return;
            }

            float speed = GameCreatorCharacter.Motion != null
                ? GameCreatorCharacter.Motion.LinearSpeed
                : 0f;

            float distanceToMove = speed * delta;
            Vector3 currentPosition = transform.position;

            while (distanceToMove > 0f &&
                   m_PathCorners != null &&
                   state.currentCornerIndex < m_PathCorners.Length)
            {
                Vector3 targetCorner = m_PathCorners[state.currentCornerIndex];
                Vector3 toCorner = targetCorner - currentPosition;
                float distance = toCorner.magnitude;

                if (distance <= ARRIVAL_THRESHOLD)
                {
                    state.currentCornerIndex++;
                    continue;
                }

                Vector3 direction = toCorner / distance;
                Vector3 movement = direction * Mathf.Min(distanceToMove, distance);
                currentPosition += movement;
                distanceToMove -= movement.magnitude;
            }

            Vector3 deltaPosition = currentPosition - transform.position;
            transform.position = currentPosition;
            state.velocity = delta > 0f ? deltaPosition / delta : Vector3.zero;

            if (state.velocity.sqrMagnitude > 0.0001f)
            {
                FaceVelocity(state.velocity, delta);
                state.flags |= GC2PurrDictionNavMeshState.FLAG_MOVING;
            }

            if (m_PathCorners == null || state.currentCornerIndex >= m_PathCorners.Length)
            {
                state.velocity = Vector3.zero;
                state.flags &= unchecked((byte)~GC2PurrDictionNavMeshState.FLAG_MOVING);
            }
        }

        private void SimulateDirection(ref GC2PurrDictionNavMeshState state, float delta)
        {
            Vector3 direction = state.destination;
            direction.y = 0f;
            if (direction.sqrMagnitude > 1f) direction.Normalize();

            float speed = GameCreatorCharacter.Motion != null
                ? GameCreatorCharacter.Motion.LinearSpeed
                : 0f;

            Vector3 movement = direction * speed * delta;
            transform.position += movement;
            state.velocity = delta > 0f ? movement / delta : Vector3.zero;

            if (state.velocity.sqrMagnitude > 0.0001f)
            {
                FaceVelocity(state.velocity, delta);
                state.flags |= GC2PurrDictionNavMeshState.FLAG_MOVING;
            }
        }

        private bool EnsurePathForState(ref GC2PurrDictionNavMeshState state)
        {
            if (m_PathCorners != null &&
                m_PathSequence == state.activeSequence &&
                Vector3.Distance(m_PathDestination, state.destination) <= 0.01f)
            {
                return state.pathStatus == NetworkNavMeshPathState.STATUS_COMPLETE ||
                       state.pathStatus == NetworkNavMeshPathState.STATUS_PARTIAL;
            }

            BuildPath(ref state);
            return state.pathStatus == NetworkNavMeshPathState.STATUS_COMPLETE ||
                   state.pathStatus == NetworkNavMeshPathState.STATUS_PARTIAL;
        }

        private static bool TrySampleNavMesh(Vector3 position, out Vector3 sampledPosition)
        {
            if (NavMesh.SamplePosition(
                    position,
                    out NavMeshHit hit,
                    NAVMESH_SAMPLE_DISTANCE,
                    NavMesh.AllAreas))
            {
                sampledPosition = hit.position;
                return true;
            }

            sampledPosition = position;
            return false;
        }

        private void Stop(ref GC2PurrDictionNavMeshState state)
        {
            ClearPathCache();
            state.commandType = NetworkNavMeshCommand.CMD_STOP;
            state.pathStatus = NetworkNavMeshPathState.STATUS_NONE;
            state.currentCornerIndex = 0;
            state.velocity = Vector3.zero;
            state.flags = GC2PurrDictionNavMeshState.FLAG_GROUNDED;
        }

        private void ClearPathCache()
        {
            m_PathCorners = null;
            m_PathSequence = 0;
            m_PathDestination = Vector3.zero;
            m_Path?.ClearCorners();
        }

        private void FaceVelocity(Vector3 velocity, float delta)
        {
            Vector3 flatVelocity = velocity;
            flatVelocity.y = 0f;
            if (flatVelocity.sqrMagnitude <= 0.0001f) return;

            Quaternion targetRotation = Quaternion.LookRotation(flatVelocity.normalized);
            float angularSpeed = GameCreatorCharacter?.Motion != null
                ? GameCreatorCharacter.Motion.AngularSpeed
                : 720f;

            transform.rotation = Quaternion.Slerp(
                transform.rotation,
                targetRotation,
                angularSpeed * delta * 0.1f);
        }

        private void PublishDriverState(GC2PurrDictionNavMeshState state)
        {
            m_LastState = state;
            m_Driver?.ApplyPredictedState(
                state.velocity,
                state.IsGrounded,
                state.pathStatus);

            if (ShouldValidateNavMeshCommands &&
                m_ClickValidator != null &&
                NetworkTransportBridge.IsValidClientId(SecurityOwnerClientId))
            {
                m_ClickValidator.UpdateClientPosition(SecurityOwnerClientId, state.position);
            }
        }

        private static bool IsSequenceNewer(ushort sequence, ushort previous)
        {
            return sequence != previous && (short)(sequence - previous) > 0;
        }

        private bool TryValidateCommand(
            GC2PurrDictionNavMeshInput input,
            ref GC2PurrDictionNavMeshState state,
            out GC2PurrDictionNavMeshInput validatedInput)
        {
            validatedInput = input;
            if (!input.HasCommand) return true;

            if (!IsKnownCommand(input.commandType))
            {
                RecordCoreSecurityViolation(
                    SecurityViolationType.InvalidRequest,
                    $"PurrDictionNavMeshInput: unknown command={input.commandType}");
                return false;
            }

            if (input.sequence == 0 || (input.flags & ~VALID_INPUT_FLAGS) != 0)
            {
                RecordCoreSecurityViolation(
                    SecurityViolationType.InvalidRequest,
                    $"PurrDictionNavMeshInput: invalid sequence/flags sequence={input.sequence}, flags={input.flags}");
                return false;
            }

            if (!IsFinite(input.target))
            {
                RecordCoreSecurityViolation(
                    SecurityViolationType.OutOfBoundsValue,
                    $"PurrDictionNavMeshInput: invalid target={input.target}");
                return false;
            }

            string requestType = $"PurrDictionNavMesh.{GetCommandName(input.commandType)}";
            if (ShouldValidateNavMeshCommands &&
                !ValidateServerCoreRequest(input.sequence, requestType))
            {
                return false;
            }

            switch (input.commandType)
            {
                case NetworkNavMeshCommand.CMD_MOVE_TO_POSITION:
                    return ValidateMoveToPositionCommand(ref validatedInput, requestType);

                case NetworkNavMeshCommand.CMD_MOVE_TO_DIRECTION:
                    return ValidateMoveToDirectionCommand(ref validatedInput);

                case NetworkNavMeshCommand.CMD_STOP:
                    validatedInput.target = Vector3.zero;
                    return true;

                case NetworkNavMeshCommand.CMD_WARP:
                    return ValidateWarpCommand(ref validatedInput, requestType);

                default:
                    return false;
            }
        }

        private bool ValidateMoveToPositionCommand(
            ref GC2PurrDictionNavMeshInput input,
            string requestType)
        {
            if (ShouldValidateNavMeshCommands &&
                !ValidateServerCorePosition(
                    input.target,
                    Vector3.zero,
                    ResolveMaxAllowedHorizontalSpeed(),
                    requestType))
            {
                return false;
            }

            if (ShouldValidateNavMeshCommands && m_EnableClickValidation)
            {
                EnsureClickValidator();
                if (m_ClickValidator != null)
                {
                    ClickValidator.ValidationResult result = m_ClickValidator.ValidateClick(
                        SecurityOwnerClientId,
                        transform.position,
                        input.target);

                    if (!result.IsValid)
                    {
                        RecordCoreSecurityViolation(
                            SecurityViolationType.InvalidTarget,
                            $"PurrDictionNavMeshInput: click rejected for target={input.target}: {result.RejectionReason}");
                        return false;
                    }

                    input.target = result.CorrectedPosition;
                }
            }

            if (!TryValidatePath(input.target, out Vector3 destination, out string pathError))
            {
                RecordCoreSecurityViolation(
                    SecurityViolationType.InvalidTarget,
                    $"PurrDictionNavMeshInput: invalid destination={input.target}: {pathError}");
                return false;
            }

            input.target = destination;
            return true;
        }

        private bool ValidateMoveToDirectionCommand(ref GC2PurrDictionNavMeshInput input)
        {
            if (Mathf.Abs(input.target.y) > 0.001f)
            {
                RecordCoreSecurityViolation(
                    SecurityViolationType.OutOfBoundsValue,
                    $"PurrDictionNavMeshInput: vertical direction component={input.target.y}");
                return false;
            }

            input.target.y = 0f;
            float targetSqrMagnitude = input.target.sqrMagnitude;
            if (targetSqrMagnitude > REJECT_DIRECTION_INPUT_SQR)
            {
                RecordCoreSecurityViolation(
                    SecurityViolationType.OutOfBoundsValue,
                    $"PurrDictionNavMeshInput: oversized direction={input.target}");
                return false;
            }

            if (targetSqrMagnitude > MAX_DIRECTION_INPUT_SQR)
            {
                input.target.Normalize();
            }

            return true;
        }

        private bool ValidateWarpCommand(
            ref GC2PurrDictionNavMeshInput input,
            string requestType)
        {
            if (ShouldValidateNavMeshCommands &&
                !m_AllowClientWarpCommands &&
                !IsServerOwnedPrediction())
            {
                RecordCoreSecurityViolation(
                    SecurityViolationType.UnauthorizedAction,
                    $"PurrDictionNavMeshInput: client warp rejected target={input.target}");
                return false;
            }

            if (ShouldValidateNavMeshCommands &&
                !ValidateServerCorePosition(
                    input.target,
                    Vector3.zero,
                    ResolveMaxAllowedHorizontalSpeed(),
                    requestType))
            {
                return false;
            }

            if (!TrySampleNavMesh(input.target, out Vector3 destination))
            {
                RecordCoreSecurityViolation(
                    SecurityViolationType.InvalidTarget,
                    $"PurrDictionNavMeshInput: warp target is not on NavMesh target={input.target}");
                return false;
            }

            input.target = destination;
            return true;
        }

        private void RejectCommand(
            GC2PurrDictionNavMeshInput input,
            ref GC2PurrDictionNavMeshState state)
        {
            ushort rejectedSequence = input.sequence;
            byte rejectedCommand = input.commandType;
            Vector3 rejectedTarget = IsFinite(input.target) ? input.target : Vector3.zero;

            Stop(ref state);
            if (rejectedSequence != 0)
            {
                state.activeSequence = rejectedSequence;
            }

            state.commandType = rejectedCommand;
            state.destination = rejectedTarget;
            state.pathStatus = NetworkNavMeshPathState.STATUS_INVALID;
            state.flags &= unchecked((byte)~GC2PurrDictionNavMeshState.FLAG_HAS_PATH);
        }

        private bool ValidateNavMeshServerState(GC2PurrDictionNavMeshState state)
        {
            if (!ShouldValidateNavMeshCommands) return true;

            if (!IsValidState(state))
            {
                RecordCoreSecurityViolation(
                    SecurityViolationType.OutOfBoundsValue,
                    $"PurrDictionNavMeshState: invalid state position={state.position}, rotation={state.rotation}, velocity={state.velocity}");
                return false;
            }

            return ValidateServerCorePosition(
                state.position,
                state.velocity,
                ResolveMaxAllowedHorizontalSpeed(),
                "PurrDictionNavMeshState");
        }

        private bool TryValidatePath(
            Vector3 destination,
            out Vector3 sampledDestination,
            out string error)
        {
            sampledDestination = destination;
            error = null;

            if (!TrySampleNavMesh(transform.position, out Vector3 start))
            {
                error = "character is not on NavMesh";
                return false;
            }

            if (!TrySampleNavMesh(destination, out sampledDestination))
            {
                error = "destination is not on NavMesh";
                return false;
            }

            m_Path ??= new NavMeshPath();
            m_Path.ClearCorners();
            if (!NavMesh.CalculatePath(start, sampledDestination, NavMesh.AllAreas, m_Path))
            {
                error = "NavMesh path calculation failed";
                return false;
            }

            if (m_Path.status == NavMeshPathStatus.PathInvalid ||
                m_Path.corners == null ||
                m_Path.corners.Length == 0)
            {
                error = $"invalid path status={m_Path.status}";
                return false;
            }

            return true;
        }

        private void EnsureClickValidator()
        {
            if (!ShouldValidateNavMeshCommands || !m_EnableClickValidation)
            {
                DisposeClickValidator();
                return;
            }

            if (m_ClickValidator != null) return;

            m_ClickValidator = new ClickValidator(m_ClickValidationConfig ?? ClickValidationConfig.Competitive);
            m_ClickValidator.OnShouldKickClient += HandleClickValidatorKick;
            m_ClickValidator.OnSuspiciousCommand += HandleSuspiciousClickCommand;
        }

        private void DisposeClickValidator()
        {
            if (m_ClickValidator == null) return;

            m_ClickValidator.OnShouldKickClient -= HandleClickValidatorKick;
            m_ClickValidator.OnSuspiciousCommand -= HandleSuspiciousClickCommand;
            m_ClickValidator = null;
        }

        private void HandleClickValidatorKick(ulong clientId, string reason)
        {
            if (!NetworkTransportBridge.TryConvertSenderClientId(clientId, out uint gc2ClientId))
            {
                gc2ClientId = NetworkTransportBridge.InvalidClientId;
            }

            SecurityIntegration.RecordViolation(
                gc2ClientId,
                SecurityActorNetworkId,
                SecurityViolationType.SuspiciousPattern,
                "Core",
                $"PurrDictionNavMeshInput: click validator threshold exceeded: {reason}");
        }

        private void HandleSuspiciousClickCommand(ulong clientId, string reason, Vector3 target)
        {
            if (!NetworkTransportBridge.TryConvertSenderClientId(clientId, out uint gc2ClientId))
            {
                gc2ClientId = NetworkTransportBridge.InvalidClientId;
            }

            SecurityIntegration.RecordViolation(
                gc2ClientId,
                SecurityActorNetworkId,
                SecurityViolationType.SuspiciousPattern,
                "Core",
                $"PurrDictionNavMeshInput: suspicious click target={target}: {reason}");
        }

        private bool IsServerOwnedPrediction()
        {
            return owner.HasValue && owner.Value.isServer;
        }

        private static bool IsKnownCommand(byte command)
        {
            return command == NetworkNavMeshCommand.CMD_MOVE_TO_POSITION ||
                   command == NetworkNavMeshCommand.CMD_MOVE_TO_DIRECTION ||
                   command == NetworkNavMeshCommand.CMD_STOP ||
                   command == NetworkNavMeshCommand.CMD_WARP;
        }

        private static string GetCommandName(byte command)
        {
            return command switch
            {
                NetworkNavMeshCommand.CMD_MOVE_TO_POSITION => "MoveToPosition",
                NetworkNavMeshCommand.CMD_MOVE_TO_DIRECTION => "MoveToDirection",
                NetworkNavMeshCommand.CMD_STOP => "Stop",
                NetworkNavMeshCommand.CMD_WARP => "Warp",
                _ => $"Unknown{command}"
            };
        }

        private static bool IsValidState(GC2PurrDictionNavMeshState state)
        {
            return IsFinite(state.position) &&
                   IsFinite(state.rotation) &&
                   IsFinite(state.velocity) &&
                   IsFinite(state.destination);
        }
    }
}
