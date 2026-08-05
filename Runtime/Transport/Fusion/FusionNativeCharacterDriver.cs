using System;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Common;
using UnityEngine;
using UnityEngine.AI;

namespace Arawn.GameCreator2.Networking.Transport.Fusion
{
    /// <summary>
    /// GC2 character driver used by <see cref="FusionNativeNetworkCharacterMotor"/>.
    ///
    /// Unlike the transport-neutral drivers, this driver does not advance locomotion from
    /// Unity's Update loop. It only samples intent there; the Fusion behaviour consumes that
    /// intent and advances the CharacterController from FixedUpdateNetwork. This is important
    /// because Fusion can then restore, predict and resimulate the same movement tick.
    /// </summary>
    [Title("Fusion Native Network Character")]
    [Image(typeof(IconCapsuleSolid), ColorTheme.Type.Green)]
    [Category("Fusion Native Network Character")]
    [Description("Runs Game Creator character movement on Fusion simulation ticks.")]
    [Serializable]
    public sealed class FusionNativeCharacterDriver : TUnitDriver,
        INetworkDirectionalInputSink,
        INetworkOwnerMotionAuthority,
        INetworkServerOwnerMotionAuthority,
        INetworkExternalMoveDirectionSink,
        INetworkNavMeshCommandSink
    {
        private const float DefaultSkinWidth = 0.08f;
        private const float DefaultMaxSlope = 45f;
        private const float DefaultStepHeight = 0.3f;
        private const float GroundSnapSpeed = -2f;
        private const float OwnerPoseEpsilon = 0.005f;
        private const float OwnerPoseApplicationTolerance = 0.02f;
        private const float OwnerPoseWriteSuppressionSeconds = 0.5f;
        private const int ServerMotionAuthorizationCapacity = 8;

        [NonSerialized] private CharacterController m_Controller;
        [NonSerialized] private AnimVector3 m_FloorNormal;
        [NonSerialized] private Axonometry m_Axonometry = new Axonometry();
        [NonSerialized] private Vector3 m_MoveVelocity;
        [NonSerialized] private Vector3 m_ExplicitPresentationVelocity;
        [NonSerialized] private bool m_HasExplicitPresentationVelocity;
        [NonSerialized] private float m_VerticalSpeed;
        [NonSerialized] private bool m_IsOnSteepSlope;

        [NonSerialized] private Vector2 m_SampledWorldInput;
        [NonSerialized] private bool m_JumpPending;
        [NonSerialized] private float m_SampledYaw;
        [NonSerialized] private bool m_HasInputSample;
        [NonSerialized] private bool m_WasMotionJumping;
        [NonSerialized] private int m_LastJumpTick = int.MinValue;
        [NonSerialized] private bool m_RemoteTeleportRotationPending;
        [NonSerialized] private bool m_AuthorityTeleportRotationPending;
        [NonSerialized] private int m_TeleportRotationPendingFrame = -1;
        [NonSerialized] private Vector3 m_SampledRootMotionVelocity;
        [NonSerialized] private float m_SampledRootMotionWeight;
        [NonSerialized] private int m_LastRootMotionSampleFrame = -1;

        // NavMesh is an owner-side input shaper only. It never moves a Transform or runs during
        // Fusion resimulation; CaptureInput records its cached direction into the tick input.
        [NonSerialized] private NavMeshPath m_NavigationPath;
        [NonSerialized] private Vector3[] m_NavigationCorners;
        [NonSerialized] private int m_NavigationCornerIndex;
        [NonSerialized] private Vector3 m_NavigationDestination;
        [NonSerialized] private NavigationMode m_NavigationMode;
        [NonSerialized] private Vector2 m_NavigationMove;
        [NonSerialized] private float m_NavigationYaw;
        [NonSerialized] private bool m_WarpRejectionWarningIssued;

        [NonSerialized] private int m_OwnerMotionUntilTick = int.MinValue;
        [NonSerialized] private readonly ServerMotionAuthorization[] m_ServerMotionAuthorizations =
            new ServerMotionAuthorization[ServerMotionAuthorizationCapacity];
        [NonSerialized] private int m_ServerMotionAuthorizationCount;
        [NonSerialized] private float m_MaxSpeedMultiplier = 1.2f;
        [NonSerialized] private float m_MaxOwnerPoseDistance = 3f;
        [NonSerialized] private FusionNativeNetworkCharacterMotor m_Motor;

        [NonSerialized] private int m_LastGroundedTick = int.MinValue;
        [NonSerialized] private int m_LastAcceptedOwnerPoseTick = int.MinValue;
        [NonSerialized] private bool m_WasGrounded;

        private Vector3 PresentationMoveVelocity =>
            m_HasExplicitPresentationVelocity
                ? m_ExplicitPresentationVelocity
                : m_MoveVelocity;

        public override Vector3 WorldMoveDirection => PresentationMoveVelocity;
        public override Vector3 LocalMoveDirection =>
            Transform != null
                ? Transform.InverseTransformDirection(PresentationMoveVelocity)
                : Vector3.zero;
        public override float SkinWidth => m_Controller != null ? m_Controller.skinWidth : 0f;

        public override bool IsGrounded
        {
            get
            {
                if (m_ForceGrounded) return true;
                if (m_Controller == null || !m_Controller.enabled) return false;
                if (m_Controller.isGrounded && !m_IsOnSteepSlope) return true;

                return TryProbeGround(out RaycastHit hit) &&
                       Vector3.Angle(hit.normal, Vector3.up) <= DefaultMaxSlope;
            }
        }

        public override Vector3 FloorNormal => m_FloorNormal?.Current ?? Vector3.up;

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
            set => m_Axonometry = value ?? new Axonometry();
        }

        internal float VerticalSpeed => m_VerticalSpeed;
        internal Vector3 SimulationVelocity => m_MoveVelocity;
        internal int LastJumpTick => m_LastJumpTick;
        internal int LastGroundedTick => m_LastGroundedTick;
        internal int LastAcceptedOwnerPoseTick => m_LastAcceptedOwnerPoseTick;
        internal bool RequiresSimulationRootPresentation =>
            IsOwnerMotionActive(CurrentTick) ||
            IsServerMotionTickAuthorized(CurrentTick);
        internal uint ServerOwnerMotionOperation => m_ServerMotionAuthorizationCount > 0
            ? m_ServerMotionAuthorizations[m_ServerMotionAuthorizationCount - 1].OperationId
            : 0;
        internal int ServerOwnerMotionFromTick => m_ServerMotionAuthorizationCount > 0
            ? m_ServerMotionAuthorizations[m_ServerMotionAuthorizationCount - 1].FromTick
            : int.MinValue;
        internal int ServerOwnerMotionUntilTick => m_ServerMotionAuthorizationCount > 0
            ? m_ServerMotionAuthorizations[m_ServerMotionAuthorizationCount - 1].UntilTick
            : int.MinValue;

        internal void AttachMotor(FusionNativeNetworkCharacterMotor motor)
        {
            m_Motor = motor;
        }

        /// <summary>
        /// Clears one-incarnation prediction and traversal state without disposing the GC2
        /// driver. Fusion may pool a NetworkObject or migrate Shared State Authority while the
        /// same driver instance remains attached; those boundaries must not inherit a previous
        /// owner's motion authorization, root-motion sample, or accepted-pose suppression tick.
        /// </summary>
        internal void ResetNetworkTransientState()
        {
            m_MoveVelocity = Vector3.zero;
            ClearExplicitPresentationVelocity();
            m_VerticalSpeed = 0f;
            m_IsOnSteepSlope = false;
            m_SampledWorldInput = Vector2.zero;
            m_HasInputSample = false;
            m_JumpPending = false;
            m_WasMotionJumping = Character?.Motion?.IsJumping == true;
            m_LastJumpTick = int.MinValue;
            m_RemoteTeleportRotationPending = false;
            m_AuthorityTeleportRotationPending = false;
            m_TeleportRotationPendingFrame = -1;
            m_SampledRootMotionVelocity = Vector3.zero;
            m_SampledRootMotionWeight = 0f;
            m_LastRootMotionSampleFrame = -1;
            m_OwnerMotionUntilTick = int.MinValue;
            Array.Clear(
                m_ServerMotionAuthorizations,
                0,
                m_ServerMotionAuthorizations.Length);
            m_ServerMotionAuthorizationCount = 0;
            bool groundedNow = Character != null && m_Controller != null && IsGrounded;
            m_LastGroundedTick = groundedNow ? CurrentTick : int.MinValue;
            m_LastAcceptedOwnerPoseTick = int.MinValue;
            m_WasGrounded = groundedNow;
            m_WarpRejectionWarningIssued = false;
            m_SampledYaw = Transform != null ? Transform.eulerAngles.y : 0f;
            ResetNavigationIntent();
        }

        public override void OnStartup(Character character)
        {
            base.OnStartup(character);

            m_Controller = character.GetComponent<CharacterController>();
            if (m_Controller == null)
            {
                m_Controller = character.gameObject.AddComponent<CharacterController>();
                m_Controller.hideFlags = HideFlags.HideInInspector;
            }

            m_FloorNormal = new AnimVector3(Vector3.up, 0.15f);
            m_MoveVelocity = Vector3.zero;
            ClearExplicitPresentationVelocity();
            m_VerticalSpeed = 0f;
            m_SampledWorldInput = Vector2.zero;
            m_SampledYaw = Transform.eulerAngles.y;
            m_HasInputSample = false;
            m_JumpPending = false;
            m_WasMotionJumping = false;
            m_LastJumpTick = int.MinValue;
            m_RemoteTeleportRotationPending = false;
            m_AuthorityTeleportRotationPending = false;
            m_TeleportRotationPendingFrame = -1;
            m_SampledRootMotionVelocity = Vector3.zero;
            m_SampledRootMotionWeight = 0f;
            m_LastRootMotionSampleFrame = -1;
            m_NavigationPath = new NavMeshPath();
            ClearNavigationIntent(NavigationMode.Inactive);
            m_WarpRejectionWarningIssued = false;
            m_OwnerMotionUntilTick = int.MinValue;
            m_ServerMotionAuthorizationCount = 0;
            m_LastGroundedTick = int.MinValue;
            m_LastAcceptedOwnerPoseTick = int.MinValue;

            RefreshControllerShape();
            m_WasGrounded = IsGrounded;
            if (m_WasGrounded) m_LastGroundedTick = CurrentTick;
        }

        public override void OnDispose(Character character)
        {
            // The CharacterController belongs to the character prefab (or was added as the
            // same shared fallback used by the built-in drivers). Role changes must not destroy
            // it while Fusion still owns the NetworkObject.
            m_Controller = null;
            m_FloorNormal = null;
            m_NavigationPath = null;
            ClearExplicitPresentationVelocity();
            ClearNavigationIntent(NavigationMode.Inactive);
            base.OnDispose(character);
        }

        public override void OnUpdate()
        {
            // Presentation and input sampling are render-frame work, but transform movement is
            // deliberately absent here. FixedUpdateNetwork is the sole locomotion clock.
            if (Character == null || m_Controller == null) return;
            RefreshControllerShape();

            if (m_FloorNormal != null)
            {
                m_FloorNormal.UpdateWithDelta(Mathf.Max(0f, Character.Time.DeltaTime));
            }

            SampleRootMotionForCurrentFrame();
            RefreshNavigationIntent();

            float floorAngle = Vector3.Angle(FloorNormal, Vector3.up);
            m_IsOnSteepSlope = IsGrounded && floorAngle > DefaultMaxSlope;
        }

        public void ProcessDirectionalInput(
            Vector2 inputDirection,
            Transform cameraTransform,
            bool jump)
        {
            Vector3 direction = new Vector3(inputDirection.x, 0f, inputDirection.y);
            if (cameraTransform != null)
            {
                Quaternion cameraYaw = Quaternion.Euler(0f, cameraTransform.eulerAngles.y, 0f);
                direction = cameraYaw * direction;
            }

            if (direction.sqrMagnitude > 1f) direction.Normalize();
            if (direction.sqrMagnitude > 0.0001f)
            {
                ClearNavigationIntent(NavigationMode.Inactive);
            }
            m_SampledWorldInput = new Vector2(direction.x, direction.z);
            m_HasInputSample = true;
            m_JumpPending |= jump;
        }

        public void SetExternalMoveDirection(
            Vector3 velocity,
            bool preserveWhileTraversalLikeMotion = false)
        {
            if (!IsFinite(velocity)) return;

            // A remote Fusion proxy has no local traversal input. GC2 still evaluates its
            // MotionInteractive for presentation and consequently emits a synthetic priority-9
            // zero direction every Update. Accepting that zero here makes it fight the semantic
            // traversal velocity applied by FusionNativeNetworkCharacterMotor.Render: the climb
            // blend tree sees replicated direction, zero, replicated direction, zero. PurrNet's
            // remote driver avoids the same fight by not exposing this external-direction sink.
            // Keep the Fusion equivalent single-writer rule: remote presentation motion comes
            // exclusively from ApplyReplicatedMotion below.
            if (m_Motor?.IsRemoteProxyRole == true) return;

            if (preserveWhileTraversalLikeMotion)
            {
                // GC2 Traversal blend trees consume semantic intent, not physical displacement.
                // Keep that value separate from the tick velocity so an idle zero remains zero
                // even when CharacterController depenetration produces a non-zero simulation
                // delta. The explicit-active bit is required because zero is a real idle state.
                m_ExplicitPresentationVelocity = velocity;
                m_HasExplicitPresentationVelocity = true;
                return;
            }

            ClearExplicitPresentationVelocity();

            // Navigation and Traversal can author displacement through GC2's AddPosition API
            // outside the ordinary directional-input path. Preserve their intended velocity
            // for locomotion presentation; the next Fusion simulation tick still derives the
            // authoritative transform velocity from the displacement it actually achieved.
            m_MoveVelocity = velocity;
        }

        public void RequestMoveToPosition(Vector3 target)
        {
            if (!CanAuthorNavigationIntent() || !IsFinite(target)) return;

            ClearNavigationIntent(NavigationMode.Stopped);
            m_NavigationPath ??= new NavMeshPath();

            float height = Character?.Motion != null
                ? Mathf.Max(0.1f, Character.Motion.Height)
                : 2f;
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
            m_NavigationDestination = m_NavigationCorners[m_NavigationCorners.Length - 1];
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
            if (immediate) m_MoveVelocity = Vector3.zero;
        }

        public void RequestWarp(Vector3 position)
        {
            if (!CanAuthorNavigationIntent() || !IsFinite(position)) return;
            ClearNavigationIntent(NavigationMode.Stopped);

            // A client-authored warp is never placed in movement input. Only State Authority may
            // execute Fusion's explicit teleport path; client requests therefore fail closed.
            if (m_Motor?.Object != null && m_Motor.Object.IsValid &&
                m_Motor.Object.HasStateAuthority)
            {
                float height = Character?.Motion != null
                    ? Mathf.Max(0.1f, Character.Motion.Height)
                    : 2f;
                float sampleDistance = Mathf.Clamp(height * 0.4f, 0.35f, 0.8f);
                if (NavMesh.SamplePosition(
                        position,
                        out NavMeshHit hit,
                        sampleDistance,
                        NavMesh.AllAreas))
                {
                    m_Motor.Teleport(
                        hit.position + Vector3.up * (height * 0.5f),
                        Transform.rotation);
                }
                return;
            }

            if (!m_WarpRejectionWarningIssued)
            {
                m_WarpRejectionWarningIssued = true;
                Debug.LogWarning(
                    $"[FusionNativeCharacter] Ignored non-authoritative NavMesh warp for " +
                    $"'{Character?.name}'. Use the Networking Layer's validated teleport flow.",
                    Character);
            }
        }

        internal void ResetNavigationIntent()
        {
            ClearNavigationIntent(NavigationMode.Inactive);
        }

        internal FusionNativeCharacterInput CaptureInput(int tick)
        {
            SampleRootMotionForCurrentFrame();

            bool navigationOverridesInput = m_NavigationMode != NavigationMode.Inactive;
            Vector2 movement = navigationOverridesInput
                ? m_NavigationMove
                : m_HasInputSample
                    ? m_SampledWorldInput
                    : ResolveMotionFallback();

            int flags = 0;
            bool motionJumping = Character?.Motion?.IsJumping == true;
            if (m_JumpPending || (motionJumping && !m_WasMotionJumping))
            {
                flags |= FusionNativeCharacterInput.FlagJump;
            }

            m_JumpPending = false;
            m_WasMotionJumping = motionJumping;

            Vector3 ownerPosition = Transform != null
                ? Transform.position
                : Vector3.zero;
            bool ownerMotionActive = IsOwnerMotionActive(tick);
            Vector3 pendingOwnerPosition = default;
            bool hasPendingOwnerPosition =
                ownerMotionActive &&
                m_Motor?.TryGetPendingExternalOwnerPoseTarget(
                    out pendingOwnerPosition) == true;
            if (hasPendingOwnerPosition)
            {
                // Fusion can collect input after a prediction reset restored Transform to a
                // historical pose. GC2's Update-authored traversal endpoint is retained by the
                // motor specifically so input collection never sends that stale restored root.
                ownerPosition = pendingOwnerPosition;
            }

            // An owner-motion window authorizes animation-authored displacement; it does not
            // mean every tick contains a new absolute pose. Once a MotionLink's warping clip
            // stops calling AddPosition, holding the last Transform as OwnerPosition would
            // suppress the animation's root-motion phase and pin Vault/Jump at the warp point.
            // Absolute poses therefore exist only when GC2 queued a fresh external endpoint.
            bool includeOwnerPose =
                ownerMotionActive &&
                hasPendingOwnerPosition &&
                IsFinite(ownerPosition);
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
            float rootMotionWeight = includeOwnerPose ? 0f : m_SampledRootMotionWeight;
            m_Motor?.LogOwnerMotionCapture(
                tick,
                ownerMotionActive,
                hasPendingOwnerPosition,
                includeOwnerPose,
                ownerPosition,
                rootMotionDelta,
                rootMotionWeight);

            return new FusionNativeCharacterInput
            {
                Move = movement,
                Yaw = navigationOverridesInput && movement.sqrMagnitude > 0.0001f
                    ? m_NavigationYaw
                    : m_SampledYaw,
                SourceTick = tick,
                Flags = flags,
                OwnerPosition = includeOwnerPose ? ownerPosition : Vector3.zero,
                // Absolute owner poses already include animation displacement. Sending both
                // would apply the same root motion twice on the authoritative simulation.
                RootMotionDelta = rootMotionDelta,
                RootMotionWeight = rootMotionWeight,
                JumpForce = ResolveRequestedJumpForce()
            };
        }

        internal void Simulate(
            FusionNativeCharacterInput input,
            float deltaTime,
            bool authoritative,
            bool invokeGameplayEvents)
        {
            if (Character == null || Character.IsDead || m_Controller == null || deltaTime <= 0f)
            {
                m_MoveVelocity = Vector3.zero;
                return;
            }

            if (m_Motor?.EnsureFiniteEnginePose("before native simulation") == false)
            {
                m_MoveVelocity = Vector3.zero;
                return;
            }

            RefreshControllerShape();

            Vector2 move = input.Move;
            if (!IsFinite(move)) move = Vector2.zero;
            if (move.sqrMagnitude > 1f) move.Normalize();

            if (IsFinite(input.Yaw))
            {
                // m_SampledYaw belongs exclusively to live GC2 Update/input collection.
                // Fusion can replay historical inputs here on a connected client; assigning a
                // historical yaw back into that accumulator rewinds the next live sample and
                // creates a visible local-owner rotation fight during resimulation.
                float simulationYaw = Mathf.Repeat(input.Yaw, 360f);
                Transform.rotation = Quaternion.Euler(0f, simulationYaw, 0f);
            }

            bool hasOwnerPose = input.HasOwnerPose;
            bool groundedBefore = IsGrounded;
            if (!hasOwnerPose)
            {
                UpdateGravity(deltaTime, groundedBefore);
            }

            int jumpCooldownTicks = Mathf.Max(
                1,
                Mathf.CeilToInt(Character.Motion.JumpCooldown / deltaTime));
            bool jumpCooldownReady =
                m_LastJumpTick == int.MinValue ||
                input.SourceTick - m_LastJumpTick >= jumpCooldownTicks;
            if (!hasOwnerPose && input.HasJump && jumpCooldownReady &&
                CanJump(input.SourceTick, deltaTime))
            {
                float requestedJumpForce = IsFinite(input.JumpForce)
                    ? Mathf.Max(0f, input.JumpForce)
                    : 0f;
                float configuredJumpForce = Mathf.Max(
                    Character.Motion.JumpForce,
                    Character.Motion.IsJumpingForce);
                m_VerticalSpeed = authoritative
                    ? Mathf.Min(requestedJumpForce, configuredJumpForce)
                    : requestedJumpForce;
                if (m_VerticalSpeed <= 0f) m_VerticalSpeed = configuredJumpForce;
                m_LastJumpTick = input.SourceTick;
                if (invokeGameplayEvents) Character.OnJump(m_VerticalSpeed);
            }

            Vector3 horizontalVelocity = !hasOwnerPose && UpdateKinematics
                ? new Vector3(move.x, 0f, move.y) * Character.Motion.LinearSpeed
                : Vector3.zero;

            Vector3 horizontalDelta = horizontalVelocity * deltaTime;
            Vector3 rootMotionDelta = IsFinite(input.RootMotionDelta)
                ? input.RootMotionDelta
                : Vector3.zero;
            float rootMotionWeight = IsFinite(input.RootMotionWeight)
                ? Mathf.Clamp01(input.RootMotionWeight)
                : 0f;
            if (authoritative && rootMotionWeight > 0f)
            {
                // Root displacement is client-authored input. Accept it only inside a
                // server-approved, tick-addressed gameplay window or while the authoritative
                // GC2 animation state itself has root motion enabled. Never grant a fixed
                // per-packet allowance (which becomes an excessive speed at high tick rates).
                bool serverAnimationAllowsRootMotion =
                    Character.RootMotionPosition > 0.001f;
                bool serverMotionTickAuthorized =
                    IsServerMotionTickAuthorized(input.SourceTick);
                if ((!serverMotionTickAuthorized &&
                     !serverAnimationAllowsRootMotion) || input.HasOwnerPose)
                {
                    if (rootMotionDelta.sqrMagnitude > 0.000001f)
                    {
                        m_Motor?.LogOwnerMotionRejection(
                            input.HasOwnerPose
                                ? "root-motion-conflicts-with-owner-pose"
                                : "root-motion-outside-server-window",
                            input.SourceTick,
                            rootMotionDelta,
                            rootMotionWeight);
                    }
                    rootMotionDelta = Vector3.zero;
                    rootMotionWeight = 0f;
                }
                else
                {
                    if (serverAnimationAllowsRootMotion)
                    {
                        rootMotionWeight = Mathf.Min(
                            rootMotionWeight,
                            Mathf.Clamp01(Character.RootMotionPosition));
                    }

                    float maximumRootMotionDelta =
                        Character.Motion.LinearSpeed * m_MaxSpeedMultiplier * deltaTime;
                    rootMotionDelta = Vector3.ClampMagnitude(
                        rootMotionDelta,
                        maximumRootMotionDelta);
                }
            }
            Vector3 translation = Vector3.Lerp(
                horizontalDelta,
                rootMotionDelta,
                rootMotionWeight);
            translation = m_Axonometry?.ProcessTranslation(this, translation) ?? translation;
            Vector3 requestedDelta = hasOwnerPose
                ? Vector3.zero
                : translation + Vector3.up * (m_VerticalSpeed * deltaTime);

            Vector3 before = Transform.position;
            if (!hasOwnerPose && m_Controller.enabled)
            {
                m_Controller.Move(requestedDelta);
            }

            if (hasOwnerPose)
            {
                TryApplyOwnerPose(
                    input.OwnerPosition,
                    input.SourceTick,
                    deltaTime,
                    authoritative);
            }

            if (m_Motor?.EnsureFiniteEnginePose("after native simulation") == false)
            {
                m_MoveVelocity = Vector3.zero;
                m_VerticalSpeed = 0f;
                return;
            }

            bool groundedAfter = IsGrounded;
            if (groundedAfter && m_VerticalSpeed < 0f)
            {
                float landingSpeed = m_VerticalSpeed;
                m_VerticalSpeed = GravityInfluence <= 0.001f ? 0f : GroundSnapSpeed;
                m_LastGroundedTick = input.SourceTick;
                if (!m_WasGrounded && invokeGameplayEvents)
                {
                    Character.OnLand(landingSpeed);
                }
            }
            else if (groundedAfter)
            {
                m_LastGroundedTick = input.SourceTick;
            }

            m_WasGrounded = groundedAfter;

            m_MoveVelocity = (Transform.position - before) / deltaTime;
            if (m_FloorNormal != null) m_FloorNormal.UpdateWithDelta(deltaTime);
        }

        internal void ApplyReplicatedMotion(Vector3 velocity, bool grounded)
        {
            // Remote proxies use the already-selected interpolated semantic velocity supplied
            // by Fusion Render. They must never retain an override from an earlier local role.
            ClearExplicitPresentationVelocity();
            m_MoveVelocity = IsFinite(velocity) ? velocity : Vector3.zero;
            if (grounded && m_VerticalSpeed < 0f) m_VerticalSpeed = GroundSnapSpeed;
        }

        private void ClearExplicitPresentationVelocity()
        {
            m_ExplicitPresentationVelocity = Vector3.zero;
            m_HasExplicitPresentationVelocity = false;
        }

        internal void RestoreSimulationMotion(
            Vector3 velocity,
            float verticalSpeed,
            int lastJumpTick,
            int lastGroundedTick,
            int lastAcceptedOwnerPoseTick,
            bool grounded)
        {
            m_MoveVelocity = IsFinite(velocity) ? velocity : Vector3.zero;
            m_VerticalSpeed = IsFinite(verticalSpeed) ? verticalSpeed : 0f;
            m_LastJumpTick = lastJumpTick;
            m_LastGroundedTick = lastGroundedTick;
            m_LastAcceptedOwnerPoseTick = lastAcceptedOwnerPoseTick;
            m_WasGrounded = grounded;
            if (grounded && m_VerticalSpeed < GroundSnapSpeed)
            {
                m_VerticalSpeed = GroundSnapSpeed;
            }
        }

        internal void ApplySessionProfile(NetworkSessionProfile profile)
        {
            if (profile == null) return;
            m_MaxSpeedMultiplier = Mathf.Max(1f, profile.maxSpeedMultiplier);
            m_MaxOwnerPoseDistance = Mathf.Max(0.1f, profile.maxReconciliationDistance);
        }

        public void OpenOwnerMotionWindow(float durationSeconds)
        {
            if (durationSeconds <= 0f) return;

            // NetworkTraversalController refreshes this window from Update for as long as a
            // TraverseLink is active. Opening authorization must be a pure timer operation:
            // restoring the Fusion simulation root here would erase the GC2 animation's
            // render-frame pose before its next AddPosition sample can accumulate. The actual
            // Set/Add position writers prepare the root immediately before they mutate it.
            int untilTick = CurrentTick + SecondsToTicks(durationSeconds);
            m_OwnerMotionUntilTick = Math.Max(m_OwnerMotionUntilTick, untilTick);
        }

        public void OpenServerOwnerMotionWindow(float durationSeconds, uint operationId = 0)
        {
            if (durationSeconds <= 0f) return;

            int currentTick = CurrentTick;
            int untilTick = currentTick + SecondsToTicks(durationSeconds);
            int lastIndex = m_ServerMotionAuthorizationCount - 1;

            if (lastIndex >= 0)
            {
                ServerMotionAuthorization latest = m_ServerMotionAuthorizations[lastIndex];
                bool sameOperation = operationId == 0 || latest.OperationId == 0 ||
                                     latest.OperationId == operationId;
                if (sameOperation && currentTick <= latest.UntilTick + 1)
                {
                    latest.UntilTick = Math.Max(latest.UntilTick, untilTick);
                    if (operationId != 0) latest.OperationId = operationId;
                    m_ServerMotionAuthorizations[lastIndex] = latest;
                    m_Motor?.LogServerOwnerMotionWindow(
                        "refreshed",
                        latest.OperationId,
                        latest.FromTick,
                        latest.UntilTick);
                    return;
                }
            }

            if (m_ServerMotionAuthorizationCount == ServerMotionAuthorizationCapacity)
            {
                Array.Copy(
                    m_ServerMotionAuthorizations,
                    1,
                    m_ServerMotionAuthorizations,
                    0,
                    ServerMotionAuthorizationCapacity - 1);
                m_ServerMotionAuthorizationCount--;
            }

            m_ServerMotionAuthorizations[m_ServerMotionAuthorizationCount++] =
                new ServerMotionAuthorization
                {
                    FromTick = currentTick,
                    UntilTick = untilTick,
                    OperationId = operationId
                };
            m_Motor?.LogServerOwnerMotionWindow(
                "opened",
                operationId,
                currentTick,
                untilTick);
        }

        public void CloseServerOwnerMotionWindow(float graceSeconds = 0f)
        {
            int lastIndex = m_ServerMotionAuthorizationCount - 1;
            if (lastIndex < 0) return;

            ServerMotionAuthorization latest = m_ServerMotionAuthorizations[lastIndex];
            uint closedOperationId = latest.OperationId;
            int closeAtTick = CurrentTick + SecondsToTicks(Mathf.Max(0f, graceSeconds));
            latest.UntilTick = Math.Min(latest.UntilTick, closeAtTick);
            if (graceSeconds <= 0f) latest.OperationId = 0;
            m_ServerMotionAuthorizations[lastIndex] = latest;
            m_Motor?.LogServerOwnerMotionWindow(
                "closed",
                closedOperationId,
                latest.FromTick,
                latest.UntilTick);
        }

        public override void SetPosition(Vector3 position, bool teleport = false)
        {
            if (m_Motor?.IsRemoteProxyRole == true && !teleport) return;
            if (!IsFinite(position) || Character?.Motion == null ||
                !IsFinite(Character.Motion.Height)) return;

            m_Motor?.PrepareForExternalRootWrite();

            Vector3 rootPosition = position + Vector3.up * (Character.Motion.Height * 0.5f);
            if (!IsFinite(rootPosition)) return;
            if (!teleport && ShouldSuppressExternalRootPositionWrite(rootPosition)) return;
            SetRootPosition(rootPosition);
            if (teleport) m_MoveVelocity = Vector3.zero;
            if (teleport) m_TeleportRotationPendingFrame = Time.frameCount;

            if (teleport && m_Motor?.ShouldGuardRemoteOwnerWrites == true)
            {
                m_AuthorityTeleportRotationPending = true;
            }

            if (m_Motor?.IsRemoteProxyRole == true)
            {
                // The approved teleport broadcast calls SetRotation immediately afterwards.
                // Ordinary remote animation writes remain ignored so they cannot fight TRSP.
                m_RemoteTeleportRotationPending = true;
                return;
            }

            m_Motor?.NotifyExternalPositionChanged(teleport);
        }

        public override void SetRotation(Quaternion rotation)
        {
            if (!IsFinite(rotation)) return;
            bool teleportRotation =
                m_TeleportRotationPendingFrame == Time.frameCount &&
                (m_RemoteTeleportRotationPending ||
                 m_AuthorityTeleportRotationPending ||
                 m_Motor?.IsRemoteProxyRole != true);
            if (m_Motor?.IsRemoteProxyRole == true && !teleportRotation) return;
            if (m_Motor?.ShouldGuardRemoteOwnerWrites == true &&
                !teleportRotation &&
                m_Motor.IsInSimulationTick == false)
            {
                return;
            }

            bool simulationTick = m_Motor?.IsInSimulationTick == true;
            if (!simulationTick || teleportRotation)
            {
                m_SampledYaw = rotation.eulerAngles.y;
            }

            if (!teleportRotation && !simulationTick)
            {
                // GC2 facing runs from Unity Update. Feed yaw into the next Fusion input instead
                // of mutating a rendered/interpolated root and copying that render position back
                // into prediction state.
                return;
            }

            Transform.rotation = rotation;
            Physics.SyncTransforms();

            if (m_Motor?.IsRemoteProxyRole == true)
            {
                m_RemoteTeleportRotationPending = false;
                m_TeleportRotationPendingFrame = -1;
                return;
            }

            m_AuthorityTeleportRotationPending = false;
            m_TeleportRotationPendingFrame = -1;

            m_Motor?.NotifyExternalRotationChanged(teleportRotation);
        }

        public override void SetScale(Vector3 scale)
        {
            if (!IsFinite(scale)) return;
            if (m_Motor?.IsRemoteProxyRole == true) return;
            m_Motor?.PrepareForExternalRootWrite();
            Transform.localScale = scale;
            Physics.SyncTransforms();
            m_Motor?.NotifyExternalScaleChanged();
        }

        public override void AddPosition(Vector3 amount)
        {
            if (!IsFinite(amount)) return;
            if (m_Motor?.IsRemoteProxyRole == true) return;
            if (Transform == null || !IsFinite(Transform.position))
            {
                m_Motor?.EnsureFiniteEnginePose("before GC2 AddPosition");
                return;
            }

            // GC2 computes amount against the pose it sees in Update. Preparing the Fusion root
            // can replace an interpolated/render pose with the current simulation pose, so retain
            // the caller's intended world endpoint before that restore and sweep to it afterward.
            Vector3 requestedPosition = Transform.position + amount;
            if (!IsFinite(requestedPosition)) return;

            m_Motor?.PrepareForExternalRootWrite();
            Vector3 positionBeforeMove = Transform.position;
            if (ShouldSuppressExternalRootPositionWrite(requestedPosition)) return;
            Vector3 requestedDelta = requestedPosition - positionBeforeMove;
            if (m_Controller != null && m_Controller.enabled)
            {
                m_Controller.Move(requestedDelta);
            }
            else
            {
                Transform.position = requestedPosition;
            }
            if (m_Motor?.EnsureFiniteEnginePose("after GC2 AddPosition") == false) return;
            if (!IsFinite(Transform.position)) return;
            m_Motor?.NotifyExternalPositionTarget(Transform.position);
        }

        public override void AddRotation(Quaternion amount)
        {
            if (!IsFinite(amount)) return;
            if (m_Motor?.IsRemoteProxyRole == true) return;
            if (m_Motor?.ShouldGuardRemoteOwnerWrites == true &&
                m_Motor.IsInSimulationTick == false)
            {
                return;
            }

            bool simulationTick = m_Motor?.IsInSimulationTick == true;
            Quaternion target;
            if (!simulationTick)
            {
                target = Quaternion.Euler(0f, m_SampledYaw, 0f) * amount;
                m_SampledYaw = target.eulerAngles.y;
                return;
            }

            // Tick-time animation/root rotation belongs to the pose currently being simulated.
            // Do not feed it back into the live Update accumulator during rollback/replay.
            target = Transform.rotation * amount;

            Transform.rotation = target;
            Physics.SyncTransforms();
        }

        public override void AddScale(Vector3 scale)
        {
            if (!IsFinite(scale)) return;
            if (m_Motor?.IsRemoteProxyRole == true) return;
            m_Motor?.PrepareForExternalRootWrite();
            Transform.localScale = Vector3.Scale(Transform.localScale, scale);
            Physics.SyncTransforms();
            m_Motor?.NotifyExternalScaleChanged();
        }

        public override void ResetVerticalVelocity()
        {
            m_VerticalSpeed = 0f;
        }

        private void UpdateGravity(float deltaTime, bool grounded)
        {
            float influence = GravityInfluence;
            if (grounded && m_VerticalSpeed <= 0f)
            {
                m_VerticalSpeed = influence <= 0.001f ? 0f : GroundSnapSpeed;
                return;
            }

            float gravity = m_VerticalSpeed >= 0f
                ? Character.Motion.GravityUpwards
                : Character.Motion.GravityDownwards;
            m_VerticalSpeed += gravity * influence * deltaTime;
            m_VerticalSpeed = Mathf.Max(m_VerticalSpeed, Character.Motion.TerminalVelocity);
        }

        private bool CanJump(int tick, float deltaTime)
        {
            if (Character?.Motion == null || !Character.Motion.CanJump) return false;
            if (IsGrounded) return true;
            if (m_LastGroundedTick == int.MinValue) return false;

            int coyoteTicks = Mathf.Max(1, Mathf.CeilToInt(COYOTE_TIME / deltaTime));
            int ticksSinceGrounded = tick - m_LastGroundedTick;
            return ticksSinceGrounded >= 0 && ticksSinceGrounded <= coyoteTicks;
        }

        private bool TryApplyOwnerPose(
            Vector3 target,
            int sourceTick,
            float deltaTime,
            bool authoritative)
        {
            if (!IsFinite(target)) return false;

            if (authoritative && !IsServerMotionTickAuthorized(sourceTick))
            {
                m_Motor?.LogOwnerMotionRejection(
                    "owner-pose-outside-server-window",
                    sourceTick,
                    target - Transform.position,
                    1f);
                return false;
            }

            if (authoritative &&
                NetworkOwnerMotionAuthorityHooks.TryGetPositionRejection(
                    Character,
                    target,
                    out string rejectionReason))
            {
                m_Motor?.LogOwnerMotionRejection(
                    $"owner-pose-gameplay-rejection:{rejectionReason}",
                    sourceTick,
                    target - Transform.position,
                    1f);
                return false;
            }

            float distance = Vector3.Distance(Transform.position, target);
            if (distance <= OwnerPoseEpsilon)
            {
                MarkOwnerPoseAccepted(sourceTick, target, authoritative);
                return true;
            }

            float maxKineticDistance = 0f;
            float maxAuthorityDistance = 0f;
            if (authoritative)
            {
                maxKineticDistance =
                    Character.Motion.LinearSpeed * m_MaxSpeedMultiplier * deltaTime + 0.1f;
                maxAuthorityDistance = Mathf.Max(
                    m_MaxOwnerPoseDistance,
                    maxKineticDistance);
                if (distance > maxAuthorityDistance)
                {
                    // Owner poses are absolute points on an animation-authored timeline. Moving
                    // only part-way towards a valid sample makes State Authority permanently lag
                    // eased Traversal (for example a zipline whose peak speed exceeds walk speed),
                    // which Fusion then corrects on every resimulation. The server-issued,
                    // tick-addressed motion window and gameplay rejection hook above authorize
                    // this path; the reconciliation envelope remains its bounded distance guard.
                    m_Motor?.LogOwnerPoseValidation(
                        accepted: false,
                        sourceTick,
                        Transform.position,
                        target,
                        Transform.position,
                        distance,
                        maxKineticDistance,
                        maxAuthorityDistance);
                    return false;
                }
            }

            Vector3 before = Transform.position;
            Vector3 requestedDelta = target - before;
            CollisionFlags collisionFlags = CollisionFlags.None;
            if (m_Controller != null && m_Controller.enabled &&
                m_Controller.gameObject.activeInHierarchy)
            {
                Physics.SyncTransforms();
                collisionFlags = m_Controller.Move(requestedDelta);
            }
            else
            {
                Transform.position = target;
                Physics.SyncTransforms();
            }

            Vector3 applied = Transform.position;
            float residualDistance = Vector3.Distance(applied, target);
            float applicationTolerance = Mathf.Max(
                OwnerPoseApplicationTolerance,
                m_Controller != null ? m_Controller.skinWidth * 0.25f : 0f);
            if (residualDistance > applicationTolerance)
            {
                // CharacterController.Move can legally stop before the requested endpoint. Do
                // not call that partial result "accepted": doing so suppresses GC2's server-side
                // fallback writer for half a second and strands short Vault/Jump motions at the
                // obstacle. Clearing the acceptance lets authoritative Traversal continue while
                // the diagnostic identifies a lost collision override or a genuinely blocked
                // endpoint.
                if (authoritative) m_LastAcceptedOwnerPoseTick = int.MinValue;
                m_Motor?.LogOwnerPoseCollisionBlocked(
                    sourceTick,
                    authoritative,
                    before,
                    target,
                    applied,
                    collisionFlags,
                    residualDistance,
                    applicationTolerance);
                return false;
            }

            if (authoritative)
            {
                m_Motor?.LogOwnerPoseValidation(
                    accepted: true,
                    sourceTick,
                    before,
                    target,
                    applied,
                    distance,
                    maxKineticDistance,
                    maxAuthorityDistance);
            }

            MarkOwnerPoseAccepted(sourceTick, applied, authoritative);
            return true;
        }

        private void SetRootPosition(Vector3 position)
        {
            // This matches GC2's UnitDriverController and Ninjutsu's normal Fusion path. A
            // routine disable/re-enable invalidates Physics.IgnoreCollision pairs configured by
            // TraverseLink and makes the next owner-pose sweep collide with Vault/Jump geometry.
            Transform.position = position;
            Physics.SyncTransforms();
        }

        private void SampleRootMotionForCurrentFrame()
        {
            if (m_LastRootMotionSampleFrame == Time.frameCount) return;
            m_LastRootMotionSampleFrame = Time.frameCount;
            m_SampledRootMotionVelocity = Vector3.zero;
            m_SampledRootMotionWeight = Character != null
                ? Mathf.Clamp01(Character.RootMotionPosition)
                : 0f;

            if (Character?.Animim == null || m_SampledRootMotionWeight <= 0f) return;

            float sampleDeltaTime = Character.Time.DeltaTime;
            if (sampleDeltaTime <= 0f) sampleDeltaTime = Time.deltaTime;
            if (sampleDeltaTime <= 0f) return;

            Vector3 delta = Character.Animim.RootMotionDeltaPosition;
            if (!IsFinite(delta)) return;
            m_SampledRootMotionVelocity = delta / sampleDeltaTime;
        }

        private bool IsOwnerMotionActive(int tick)
        {
            if (m_OwnerMotionUntilTick == int.MinValue) return false;
            return tick <= m_OwnerMotionUntilTick;
        }

        private bool IsServerMotionTickAuthorized(int tick)
        {
            for (int i = m_ServerMotionAuthorizationCount - 1; i >= 0; i--)
            {
                ServerMotionAuthorization authorization = m_ServerMotionAuthorizations[i];
                if (tick >= authorization.FromTick && tick <= authorization.UntilTick)
                {
                    return true;
                }
            }

            return false;
        }

        private void MarkOwnerPoseAccepted(int tick, Vector3 position, bool authoritative)
        {
            m_LastAcceptedOwnerPoseTick = tick;
            if (authoritative && m_Motor?.IsResimulating != true)
            {
                // Fusion may replay many historical climb poses before the forward tick. The
                // replicated transform must replay, but TraversalStance is live GC2 render-frame
                // state rather than Fusion simulation state. Rewinding RelativePosition from
                // each historical pose creates a host-only climb fight and visible jitter.
                NetworkOwnerMotionAuthorityHooks.NotifyPositionAccepted(Character, position);
            }
        }

        private bool ShouldSuppressExternalRootPositionWrite(Vector3 position)
        {
            if (m_Motor?.ShouldGuardRemoteOwnerWrites != true) return false;
            if (m_LastAcceptedOwnerPoseTick == int.MinValue) return false;
            if (!IsTraversalLikeAuthorityMotion()) return false;

            int suppressionTicks = SecondsToTicks(OwnerPoseWriteSuppressionSeconds);
            int ageTicks = CurrentTick - m_LastAcceptedOwnerPoseTick;
            if (ageTicks < 0 || ageTicks > suppressionTicks) return false;

            return !NetworkOwnerMotionAuthorityHooks.TryGetExternalRootWriteAllowance(
                Character,
                position,
                out _);
        }

        private bool IsTraversalLikeAuthorityMotion()
        {
            if (Character == null) return false;
            if (Character.RootMotionPosition > 0.001f) return true;
            return Character.Busy != null &&
                   (Character.Busy.IsBusy || Character.Busy.AreLegsBusy);
        }

        private int CurrentTick => m_Motor != null ? m_Motor.CurrentSimulationTick : 0;
        private float SimulationDeltaTime => m_Motor != null
            ? m_Motor.SimulationDeltaTime
            : Mathf.Max(Time.fixedDeltaTime, 0.001f);

        private int SecondsToTicks(float seconds)
        {
            return Mathf.Max(0, Mathf.CeilToInt(seconds / SimulationDeltaTime));
        }

        private Vector2 ResolveMotionFallback()
        {
            if (Character?.Motion == null) return Vector2.zero;
            Vector3 direction = Character.Motion.MoveDirection;
            if (direction.sqrMagnitude > 1f) direction.Normalize();
            return new Vector2(direction.x, direction.z);
        }

        private bool CanAuthorNavigationIntent()
        {
            return Character != null && Transform != null &&
                   m_Motor?.IsRemoteProxyRole != true;
        }

        private void RefreshNavigationIntent()
        {
            if (m_NavigationMode != NavigationMode.Path || Transform == null) return;
            if (m_NavigationCorners == null || m_NavigationCorners.Length == 0)
            {
                ClearNavigationIntent(NavigationMode.Stopped);
                return;
            }

            float arrivalRadius = Mathf.Max(
                0.1f,
                (m_Controller != null ? m_Controller.radius : 0.2f) * 0.75f);
            Vector3 current = Transform.position;

            while (m_NavigationCornerIndex < m_NavigationCorners.Length)
            {
                Vector3 cornerDelta = m_NavigationCorners[m_NavigationCornerIndex] - current;
                cornerDelta.y = 0f;
                if (cornerDelta.sqrMagnitude > arrivalRadius * arrivalRadius) break;
                m_NavigationCornerIndex++;
            }

            Vector3 destinationDelta = m_NavigationDestination - current;
            destinationDelta.y = 0f;
            if (m_NavigationCornerIndex >= m_NavigationCorners.Length ||
                destinationDelta.sqrMagnitude <= arrivalRadius * arrivalRadius)
            {
                ClearNavigationIntent(NavigationMode.Stopped);
                return;
            }

            Vector3 direction = m_NavigationCorners[m_NavigationCornerIndex] - current;
            direction.y = 0f;
            if (direction.sqrMagnitude <= 0.0001f)
            {
                ClearNavigationIntent(NavigationMode.Stopped);
                return;
            }

            SetNavigationSample(direction.normalized);
        }

        private void SetNavigationSample(Vector3 direction)
        {
            if (direction.sqrMagnitude > 1f) direction.Normalize();
            m_NavigationMove = new Vector2(direction.x, direction.z);
            m_NavigationYaw = Mathf.Repeat(
                Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg,
                360f);
        }

        private void ClearNavigationIntent(NavigationMode mode)
        {
            m_NavigationMode = mode;
            m_NavigationCorners = null;
            m_NavigationCornerIndex = 0;
            m_NavigationDestination = Vector3.zero;
            m_NavigationMove = Vector2.zero;
            m_NavigationYaw = Transform != null ? Transform.eulerAngles.y : 0f;
        }

        private float ResolveRequestedJumpForce()
        {
            if (Character?.Motion == null) return 0f;
            return Character.Motion.IsJumpingForce > 0f
                ? Character.Motion.IsJumpingForce
                : Character.Motion.JumpForce;
        }

        private void RefreshControllerShape()
        {
            if (m_Controller == null || Character?.Motion == null) return;

            if (!Mathf.Approximately(m_Controller.skinWidth, DefaultSkinWidth))
                m_Controller.skinWidth = DefaultSkinWidth;
            if (!Mathf.Approximately(m_Controller.slopeLimit, DefaultMaxSlope))
                m_Controller.slopeLimit = DefaultMaxSlope;
            if (!Mathf.Approximately(m_Controller.stepOffset, DefaultStepHeight))
                m_Controller.stepOffset = DefaultStepHeight;
            if (!Mathf.Approximately(m_Controller.minMoveDistance, 0f))
                m_Controller.minMoveDistance = 0f;

            float height = Character.Motion.Height;
            float radius = Character.Motion.Radius;
            if (!Mathf.Approximately(m_Controller.height, height))
            {
                m_Controller.height = height;
                m_Controller.center = Vector3.zero;
            }
            if (!Mathf.Approximately(m_Controller.radius, radius)) m_Controller.radius = radius;
        }

        private bool TryProbeGround(out RaycastHit hit)
        {
            hit = default;
            if (m_Controller == null || !m_Controller.enabled) return false;

            float skin = Mathf.Max(0.01f, m_Controller.skinWidth);
            float radius = Mathf.Max(0.01f, m_Controller.radius - skin);
            float halfHeight = Mathf.Max(radius, m_Controller.height * 0.5f);
            float probeDistance = Mathf.Max(0.05f, halfHeight - radius + skin + 0.08f);
            Vector3 center = Transform.TransformPoint(m_Controller.center);

            return Physics.SphereCast(
                center,
                radius,
                Vector3.down,
                out hit,
                probeDistance,
                Physics.DefaultRaycastLayers,
                QueryTriggerInteraction.Ignore);
        }

        private static bool IsFinite(float value) =>
            !float.IsNaN(value) && !float.IsInfinity(value);

        private static bool IsFinite(Vector2 value) =>
            IsFinite(value.x) && IsFinite(value.y);

        private static bool IsFinite(Vector3 value) =>
            IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);

        private static bool IsFinite(Quaternion value) =>
            IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z) && IsFinite(value.w);

        private struct ServerMotionAuthorization
        {
            public int FromTick;
            public int UntilTick;
            public uint OperationId;
        }

        private enum NavigationMode : byte
        {
            Inactive = 0,
            Stopped = 1,
            Direction = 2,
            Path = 3
        }

        public override string ToString() => "Fusion Native Network Character";
    }
}
