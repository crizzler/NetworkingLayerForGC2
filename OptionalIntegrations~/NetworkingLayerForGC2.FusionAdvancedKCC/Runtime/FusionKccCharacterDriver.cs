#if ARAWN_GC2_FUSION_KCC
using System;
using Fusion;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Common;
using UnityEngine;
using UnityEngine.AI;

namespace Arawn.GameCreator2.Networking.Transport.Fusion.KCC
{
    /// <summary>
    /// Game Creator 2 driver used by the optional Advanced KCC motor. Unity Update only
    /// samples intent; <see cref="FusionKccMotorBody"/> is the sole movement writer and
    /// consumes that intent from Fusion's fixed simulation lifecycle.
    /// </summary>
    [Title("Fusion Advanced KCC Network Character")]
    [Image(typeof(IconCapsuleSolid), ColorTheme.Type.Green)]
    [Category("Fusion Advanced KCC Network Character")]
    [Description("Runs Game Creator character movement through Photon Fusion Advanced KCC.")]
    [Serializable]
    public sealed class FusionKccCharacterDriver : TUnitDriver,
        INetworkDirectionalInputSink,
        INetworkOwnerMotionAuthority,
        INetworkServerOwnerMotionAuthority,
        INetworkExternalMoveDirectionSink,
        INetworkNavMeshCommandSink
    {
        private const float DefaultSkinWidth = 0.035f;

        [NonSerialized] private FusionKccMotorBody m_Motor;
        [NonSerialized] private Axonometry m_Axonometry = new Axonometry();
        [NonSerialized] private Vector2 m_SampledMove;
        [NonSerialized] private float m_SampledYaw;
        [NonSerialized] private bool m_HasInputSample;
        [NonSerialized] private bool m_JumpPending;
        [NonSerialized] private bool m_ResetVerticalVelocityPending;
        [NonSerialized] private bool m_HasPendingCollisionChange;
        [NonSerialized] private bool m_PendingCollisionEnabled = true;
        [NonSerialized] private bool m_WasMotionJumping;
        [NonSerialized] private Vector3 m_PresentationVelocity;
        [NonSerialized] private bool m_HasExplicitPresentationVelocity;
        [NonSerialized] private Vector3 m_ExplicitPresentationVelocity;

        [NonSerialized] private Vector3 m_SampledRootMotionVelocity;
        [NonSerialized] private float m_SampledRootMotionWeight;
        [NonSerialized] private int m_LastRootMotionFrame = -1;

        [NonSerialized] private bool m_HasPendingOwnerRootPosition;
        [NonSerialized] private Vector3 m_PendingOwnerRootPosition;

        [NonSerialized] private NavMeshPath m_NavigationPath;
        [NonSerialized] private Vector3[] m_NavigationCorners;
        [NonSerialized] private int m_NavigationCornerIndex;
        [NonSerialized] private Vector3 m_NavigationDestination;
        [NonSerialized] private NavigationMode m_NavigationMode;
        [NonSerialized] private Vector2 m_NavigationMove;
        [NonSerialized] private float m_NavigationYaw;
        [NonSerialized] private bool m_WarpRejectionReported;

        public override Vector3 WorldMoveDirection => m_HasExplicitPresentationVelocity
            ? m_ExplicitPresentationVelocity
            : m_PresentationVelocity;

        public override Vector3 LocalMoveDirection => Transform != null
            ? Transform.InverseTransformDirection(WorldMoveDirection)
            : Vector3.zero;

        public override float SkinWidth => m_Motor != null
            ? m_Motor.SkinWidth
            : DefaultSkinWidth;

        public override bool IsGrounded => m_ForceGrounded ||
                                           (m_Motor != null && m_Motor.IsGrounded);

        public override Vector3 FloorNormal => m_Motor != null
            ? m_Motor.FloorNormal
            : Vector3.up;

        public override bool Collision
        {
            get => m_Motor == null || m_Motor.CollisionEnabled;
            set => m_Motor?.SetCollisionEnabled(value);
        }

        public override Axonometry Axonometry
        {
            get => m_Axonometry;
            set => m_Axonometry = value ?? new Axonometry();
        }

        internal bool UpdateKinematicsEnabled => UpdateKinematics;
        internal float CurrentGravityInfluence => GravityInfluence;
        internal bool ForceGroundedValue => m_ForceGrounded;

        internal void AttachMotor(FusionKccMotorBody motor)
        {
            m_Motor = motor;
        }

        public override void OnStartup(Character character)
        {
            base.OnStartup(character);
            m_Axonometry = new Axonometry();
            m_SampledMove = Vector2.zero;
            m_SampledYaw = Transform != null ? Transform.eulerAngles.y : 0f;
            m_HasInputSample = false;
            m_JumpPending = false;
            m_ResetVerticalVelocityPending = false;
            m_HasPendingCollisionChange = false;
            m_PendingCollisionEnabled = true;
            m_WasMotionJumping = false;
            m_PresentationVelocity = Vector3.zero;
            m_HasExplicitPresentationVelocity = false;
            m_ExplicitPresentationVelocity = Vector3.zero;
            m_LastRootMotionFrame = -1;
            m_HasPendingOwnerRootPosition = false;
            m_NavigationPath = new NavMeshPath();
            ClearNavigationIntent(NavigationMode.Inactive);
            m_WarpRejectionReported = false;
        }

        public override void OnDispose(Character character)
        {
            m_Motor = null;
            m_NavigationPath = null;
            m_NavigationCorners = null;
            m_HasPendingOwnerRootPosition = false;
            m_ResetVerticalVelocityPending = false;
            m_HasPendingCollisionChange = false;
            base.OnDispose(character);
        }

        public override void OnUpdate()
        {
            if (Character == null) return;
            SampleRootMotionForCurrentFrame();
            RefreshNavigationIntent();
        }

        public void ProcessDirectionalInput(
            Vector2 inputDirection,
            Transform cameraTransform,
            bool jump)
        {
            Vector3 direction = new Vector3(inputDirection.x, 0f, inputDirection.y);
            if (cameraTransform != null)
            {
                Quaternion cameraYaw = Quaternion.Euler(
                    0f,
                    cameraTransform.eulerAngles.y,
                    0f);
                direction = cameraYaw * direction;
            }

            if (direction.sqrMagnitude > 1f) direction.Normalize();
            if (direction.sqrMagnitude > 0.0001f)
            {
                ClearNavigationIntent(NavigationMode.Inactive);
            }

            m_SampledMove = new Vector2(direction.x, direction.z);
            m_HasInputSample = true;
            m_JumpPending |= jump;
        }

        public void SetExternalMoveDirection(
            Vector3 velocity,
            bool preserveWhileTraversalLikeMotion = false)
        {
            if (!IsFinite(velocity) || m_Motor?.IsRemoteProxyRole == true) return;

            if (preserveWhileTraversalLikeMotion)
            {
                m_ExplicitPresentationVelocity = velocity;
                m_HasExplicitPresentationVelocity = true;
                return;
            }

            m_HasExplicitPresentationVelocity = false;
            m_PresentationVelocity = velocity;
        }

        public void RequestMoveToPosition(Vector3 target)
        {
            if (!CanAuthorNavigationIntent() || !IsFinite(target)) return;

            m_NavigationPath ??= new NavMeshPath();
            ClearNavigationIntent(NavigationMode.Stopped);

            float height = ActiveHeight;
            Vector3 feet = Transform.position - Vector3.up * (height * 0.5f);
            float sampleDistance = Mathf.Clamp(height * 0.4f, 0.35f, 0.8f);
            if (!NavMesh.SamplePosition(
                    feet,
                    out NavMeshHit startHit,
                    sampleDistance,
                    NavMesh.AllAreas) ||
                !NavMesh.SamplePosition(
                    target,
                    out NavMeshHit targetHit,
                    sampleDistance,
                    NavMesh.AllAreas) ||
                !NavMesh.CalculatePath(
                    startHit.position,
                    targetHit.position,
                    NavMesh.AllAreas,
                    m_NavigationPath) ||
                m_NavigationPath.status == NavMeshPathStatus.PathInvalid)
            {
                return;
            }

            Vector3[] corners = m_NavigationPath.corners;
            if (corners == null || corners.Length < 2) return;
            m_NavigationCorners = (Vector3[])corners.Clone();
            m_NavigationCornerIndex = 1;
            m_NavigationDestination =
                m_NavigationCorners[m_NavigationCorners.Length - 1];
            m_NavigationMode = NavigationMode.Path;
            RefreshNavigationIntent();
        }

        public void RequestMoveToDirection(Vector3 direction)
        {
            if (!CanAuthorNavigationIntent() || !IsFinite(direction)) return;
            direction.y = 0f;
            if (direction.sqrMagnitude <= 0.0001f)
            {
                RequestStop(true);
                return;
            }

            if (direction.sqrMagnitude > 1f) direction.Normalize();
            ClearNavigationIntent(NavigationMode.Direction);
            SetNavigationSample(direction);
        }

        public void RequestStop(bool immediate = false)
        {
            if (!CanAuthorNavigationIntent()) return;
            ClearNavigationIntent(NavigationMode.Stopped);
            if (immediate)
            {
                m_PresentationVelocity = Vector3.zero;
                m_HasExplicitPresentationVelocity = false;
            }
        }

        public void RequestWarp(Vector3 position)
        {
            if (!CanAuthorNavigationIntent() || !IsFinite(position)) return;
            ClearNavigationIntent(NavigationMode.Stopped);

            if (m_Motor != null && m_Motor.CanApplyAuthoritativeTeleport)
            {
                float sampleDistance = Mathf.Clamp(ActiveHeight * 0.4f, 0.35f, 0.8f);
                if (NavMesh.SamplePosition(
                        position,
                        out NavMeshHit hit,
                        sampleDistance,
                        NavMesh.AllAreas))
                {
                    m_Motor.QueueAuthoritativeTeleport(
                        hit.position,
                        Quaternion.Euler(0f, m_SampledYaw, 0f));
                }
                return;
            }

            if (m_WarpRejectionReported) return;
            m_WarpRejectionReported = true;
            Debug.LogWarning(
                $"[FusionKCC] Ignored non-authoritative NavMesh warp for " +
                $"'{Character?.name}'. Use the Networking Layer's validated teleport flow.",
                Character);
        }

        internal FusionNativeCharacterInput CaptureInput(int tick)
        {
            SampleRootMotionForCurrentFrame();

            bool navigation = m_NavigationMode != NavigationMode.Inactive;
            Vector2 move = navigation
                ? m_NavigationMove
                : m_HasInputSample
                    ? m_SampledMove
                    : ResolveMotionFallback();
            if (move.sqrMagnitude > 1f) move.Normalize();

            int flags = 0;
            bool motionJumping = Character?.Motion?.IsJumping == true;
            if (m_JumpPending || (motionJumping && !m_WasMotionJumping))
            {
                flags |= FusionNativeCharacterInput.FlagJump;
            }
            m_JumpPending = false;
            m_WasMotionJumping = motionJumping;

            if (m_ResetVerticalVelocityPending)
            {
                flags |= FusionNativeCharacterInput.FlagResetVerticalVelocity;
            }
            m_ResetVerticalVelocityPending = false;

            if (m_HasPendingCollisionChange)
            {
                flags |= FusionNativeCharacterInput.FlagCollisionChanged;
                if (m_PendingCollisionEnabled)
                {
                    flags |= FusionNativeCharacterInput.FlagCollisionEnabled;
                }
            }
            m_HasPendingCollisionChange = false;

            bool ownerMotionActive = IsOwnerMotionActive(tick);
            bool includeOwnerPose = ownerMotionActive &&
                                    m_HasPendingOwnerRootPosition &&
                                    IsFinite(m_PendingOwnerRootPosition);
            Vector3 ownerPosition = includeOwnerPose
                ? m_PendingOwnerRootPosition
                : Vector3.zero;
            m_HasPendingOwnerRootPosition = false;
            if (includeOwnerPose)
            {
                flags |= FusionNativeCharacterInput.FlagOwnerPose;
                if (NetworkOwnerMotionAuthorityHooks.IsContinuousOwnerPose(Character))
                {
                    flags |= FusionNativeCharacterInput.FlagContinuousOwnerPose;
                }
            }

            Vector3 rootMotionDelta = includeOwnerPose
                ? Vector3.zero
                : m_SampledRootMotionVelocity * SimulationDeltaTime;
            float rootMotionWeight = includeOwnerPose
                ? 0f
                : m_SampledRootMotionWeight;

            return new FusionNativeCharacterInput
            {
                Move = move,
                Yaw = navigation && move.sqrMagnitude > 0.0001f
                    ? m_NavigationYaw
                    : m_SampledYaw,
                SourceTick = tick,
                Flags = flags,
                OwnerPosition = ownerPosition,
                RootMotionDelta = rootMotionDelta,
                RootMotionWeight = rootMotionWeight,
                JumpForce = ResolveRequestedJumpForce()
            };
        }

        internal FusionNativeCharacterInput CaptureRenderIntent(int tick)
        {
            bool navigation = m_NavigationMode != NavigationMode.Inactive;
            Vector2 move = navigation
                ? m_NavigationMove
                : m_HasInputSample
                    ? m_SampledMove
                    : ResolveMotionFallback();
            if (move.sqrMagnitude > 1f) move.Normalize();

            return new FusionNativeCharacterInput
            {
                Move = move,
                Yaw = navigation && move.sqrMagnitude > 0.0001f
                    ? m_NavigationYaw
                    : m_SampledYaw,
                SourceTick = tick,
                Flags = 0,
                OwnerPosition = Vector3.zero,
                RootMotionDelta = Vector3.zero,
                RootMotionWeight = 0f,
                JumpForce = 0f
            };
        }

        internal void CaptureRenderRootMotion(
            out Vector3 velocity,
            out float weight)
        {
            SampleRootMotionForCurrentFrame();
            velocity = m_SampledRootMotionVelocity;
            weight = m_SampledRootMotionWeight;
        }

        internal void ApplySimulationVelocity(Vector3 velocity)
        {
            if (!IsFinite(velocity)) velocity = Vector3.zero;
            m_PresentationVelocity = velocity;
        }

        public void OpenOwnerMotionWindow(float durationSeconds)
        {
            m_Motor?.OpenOwnerMotionWindow(durationSeconds);
        }

        public void OpenServerOwnerMotionWindow(
            float durationSeconds,
            uint operationId = 0)
        {
            m_Motor?.OpenServerOwnerMotionWindow(durationSeconds, operationId);
        }

        public void CloseServerOwnerMotionWindow(float graceSeconds = 0f)
        {
            m_Motor?.CloseServerOwnerMotionWindow(graceSeconds);
        }

        public override void SetPosition(Vector3 position, bool teleport = false)
        {
            if (!IsFinite(position) || m_Motor == null) return;

            if (teleport)
            {
                m_Motor.QueueAuthoritativeTeleport(
                    position,
                    Quaternion.Euler(0f, m_SampledYaw, 0f));
                return;
            }

            // Update-authored owner poses belong exclusively to the logical owner input stream.
            // A server-side remote proxy may only move this character through the validated
            // teleport command above; retaining an ordinary remote pose here would leak into a
            // later ownership change as stale traversal input.
            if (m_Motor.IsRemoteProxyRole) return;

            m_PendingOwnerRootPosition =
                position + Vector3.up * (ActiveHeight * 0.5f);
            m_HasPendingOwnerRootPosition = true;
        }

        public override void SetRotation(Quaternion rotation)
        {
            if (!IsFinite(rotation)) return;
            if (m_Motor?.IsRemoteProxyRole != true)
            {
                m_SampledYaw = rotation.eulerAngles.y;
            }
            // Server-authoritative teleports can target a remote character. Updating the root
            // backend's still-pending command is authority-gated there, so this is harmless on
            // non-authoritative peers and preserves the requested rotation on the server.
            m_Motor?.UpdateQueuedTeleportRotation(rotation);
        }

        public override void SetScale(Vector3 scale)
        {
            if (!IsFinite(scale)) return;
            m_Motor?.RequestAuthoritativeScale(scale);
        }

        public override void AddPosition(Vector3 amount)
        {
            if (!IsFinite(amount) || Transform == null ||
                m_Motor?.IsRemoteProxyRole == true)
            {
                return;
            }

            Vector3 target = (m_HasPendingOwnerRootPosition
                ? m_PendingOwnerRootPosition
                : Transform.position) + amount;
            if (!IsFinite(target)) return;
            m_PendingOwnerRootPosition = target;
            m_HasPendingOwnerRootPosition = true;
        }

        public override void AddRotation(Quaternion amount)
        {
            if (!IsFinite(amount) || m_Motor?.IsRemoteProxyRole == true) return;
            Quaternion target = Quaternion.Euler(0f, m_SampledYaw, 0f) * amount;
            m_SampledYaw = target.eulerAngles.y;
        }

        public override void AddScale(Vector3 amount)
        {
            if (!IsFinite(amount) || Transform == null ||
                m_Motor?.IsRemoteProxyRole == true)
            {
                return;
            }
            Vector3 current = m_Motor != null
                ? m_Motor.GetRequestedOrReplicatedRootScale(Transform.localScale)
                : Transform.localScale;
            RequestAuthoritativeScale(current + amount);
        }

        public override void ResetVerticalVelocity()
        {
            m_Motor?.RequestVerticalVelocityReset();
        }

        internal void QueueLocalVerticalVelocityReset()
        {
            m_ResetVerticalVelocityPending = true;
        }

        internal void QueueLocalCollisionChange(bool enabled)
        {
            m_PendingCollisionEnabled = enabled;
            m_HasPendingCollisionChange = true;
        }

        private void RequestAuthoritativeScale(Vector3 scale)
        {
            m_Motor?.RequestAuthoritativeScale(scale);
        }

        private void SampleRootMotionForCurrentFrame()
        {
            if (m_LastRootMotionFrame == Time.frameCount) return;
            m_LastRootMotionFrame = Time.frameCount;
            m_SampledRootMotionVelocity = Vector3.zero;
            m_SampledRootMotionWeight = Character != null
                ? Mathf.Clamp01(Character.RootMotionPosition)
                : 0f;
            if (Character?.Animim == null || m_SampledRootMotionWeight <= 0f) return;

            float deltaTime = Character.Time.DeltaTime;
            if (deltaTime <= 0f) deltaTime = Time.deltaTime;
            if (deltaTime <= 0f) return;
            Vector3 delta = Character.Animim.RootMotionDeltaPosition;
            if (IsFinite(delta)) m_SampledRootMotionVelocity = delta / deltaTime;
        }

        private void RefreshNavigationIntent()
        {
            if (m_NavigationMode != NavigationMode.Path || Transform == null) return;
            if (m_NavigationCorners == null || m_NavigationCorners.Length == 0)
            {
                ClearNavigationIntent(NavigationMode.Stopped);
                return;
            }

            float arrivalRadius = Mathf.Max(0.1f, ActiveRadius * 0.75f);
            Vector3 feet = Transform.position - Vector3.up * (ActiveHeight * 0.5f);
            while (m_NavigationCornerIndex < m_NavigationCorners.Length)
            {
                Vector3 delta = m_NavigationCorners[m_NavigationCornerIndex] - feet;
                delta.y = 0f;
                if (delta.sqrMagnitude > arrivalRadius * arrivalRadius) break;
                m_NavigationCornerIndex++;
            }

            if (m_NavigationCornerIndex >= m_NavigationCorners.Length)
            {
                Vector3 destinationDelta = m_NavigationDestination - feet;
                destinationDelta.y = 0f;
                if (destinationDelta.sqrMagnitude <= arrivalRadius * arrivalRadius)
                {
                    ClearNavigationIntent(NavigationMode.Stopped);
                    return;
                }
                m_NavigationCornerIndex = m_NavigationCorners.Length - 1;
            }

            Vector3 direction =
                m_NavigationCorners[m_NavigationCornerIndex] - feet;
            direction.y = 0f;
            if (direction.sqrMagnitude <= 0.0001f)
            {
                SetNavigationSample(Vector3.zero);
                return;
            }
            SetNavigationSample(direction.normalized);
        }

        private void SetNavigationSample(Vector3 direction)
        {
            if (direction.sqrMagnitude > 1f) direction.Normalize();
            m_NavigationMove = new Vector2(direction.x, direction.z);
            if (direction.sqrMagnitude > 0.0001f)
            {
                m_NavigationYaw = Mathf.Atan2(direction.x, direction.z) *
                                  Mathf.Rad2Deg;
            }
        }

        private void ClearNavigationIntent(NavigationMode mode)
        {
            m_NavigationMode = mode;
            m_NavigationCorners = null;
            m_NavigationCornerIndex = 0;
            m_NavigationDestination = Vector3.zero;
            m_NavigationMove = Vector2.zero;
            m_NavigationYaw = m_SampledYaw;
        }

        private Vector2 ResolveMotionFallback()
        {
            if (Character?.Motion == null) return Vector2.zero;
            Vector3 direction = Character.Motion.MoveDirection;
            if (direction.sqrMagnitude > 1f) direction.Normalize();
            return new Vector2(direction.x, direction.z);
        }

        private bool CanAuthorNavigationIntent() =>
            Character != null && Transform != null &&
            m_Motor?.IsRemoteProxyRole != true;

        private bool IsOwnerMotionActive(int tick) =>
            m_Motor?.IsOwnerMotionActive(tick) == true;

        internal Vector3 ProcessAxonometryDirection(Vector3 direction)
        {
            if (!IsFinite(direction)) return Vector3.zero;
            Vector3 processed = m_Axonometry?.ProcessTranslation(this, direction) ??
                                direction;
            if (!IsFinite(processed)) return Vector3.zero;
            processed.y = 0f;
            return Vector3.ClampMagnitude(processed, 1f);
        }

        internal Vector3 ProcessAxonometryTranslation(Vector3 translation)
        {
            if (!IsFinite(translation)) return Vector3.zero;
            Vector3 processed = m_Axonometry?.ProcessTranslation(this, translation) ??
                                translation;
            return IsFinite(processed) ? processed : Vector3.zero;
        }

        private float ResolveRequestedJumpForce()
        {
            if (Character?.Motion == null) return 0f;
            return Character.Motion.IsJumpingForce > 0f
                ? Character.Motion.IsJumpingForce
                : Character.Motion.JumpForce;
        }

        private float SimulationDeltaTime => m_Motor != null
            ? m_Motor.SimulationDeltaTime
            : Mathf.Max(Time.fixedDeltaTime, 0.001f);
        private float ActiveHeight => Character?.Motion != null
            ? Mathf.Max(0.1f, Character.Motion.Height)
            : 2f;
        private float ActiveRadius => Character?.Motion != null
            ? Mathf.Max(0.01f, Character.Motion.Radius)
            : 0.35f;

        private enum NavigationMode
        {
            Inactive,
            Path,
            Direction,
            Stopped
        }

        private static bool IsFinite(float value) =>
            !float.IsNaN(value) && !float.IsInfinity(value);

        private static bool IsFinite(Vector2 value) =>
            IsFinite(value.x) && IsFinite(value.y);

        private static bool IsFinite(Vector3 value) =>
            IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);

        private static bool IsFinite(Quaternion value) =>
            IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z) &&
            IsFinite(value.w);
    }
}
#endif
