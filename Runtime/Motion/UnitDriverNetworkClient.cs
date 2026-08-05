using System;
using System.Collections.Generic;
using UnityEngine;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.Characters;

namespace Arawn.GameCreator2.Networking
{
    /// <summary>
    /// Client-side driver with prediction and server reconciliation.
    /// Provides responsive movement while staying in sync with server authority.
    /// </summary>
    [Title("Network Character Controller (Client)")]
    [Image(typeof(IconCapsuleSolid), ColorTheme.Type.Yellow)]
    [Category("Network Character Controller (Client)")]
    [Description("Client-side driver with prediction and reconciliation. " +
                 "Provides responsive local movement that syncs with server authority.")]
    [Serializable]
    public class UnitDriverNetworkClient : TUnitDriver,
        INetworkDirectionalInputSink,
        INetworkOwnerMotionAuthority,
        INetworkExternalMoveDirectionSink
    {
        // EXPOSED MEMBERS: -----------------------------------------------------------------------

        [SerializeField] protected float m_SkinWidth = 0.08f;
        [SerializeField] protected float m_MaxSlope = 45f;
        [SerializeField] protected float m_StepHeight = 0.3f;
        [SerializeField] private Axonometry m_Axonometry = new Axonometry();

        [Header("Network Settings")]
        [SerializeField] private NetworkCharacterConfig m_Config = new NetworkCharacterConfig();

        [Header("Debug")]
        [SerializeField] private bool m_LogMotionDiagnostics = false;
        [SerializeField] private float m_MotionDiagnosticInterval = 0.25f;

        // MEMBERS: -------------------------------------------------------------------------------

        [NonSerialized] protected CharacterController m_Controller;
        [NonSerialized] protected Vector3 m_MoveDirection;
        [NonSerialized] protected float m_VerticalSpeed;
        [NonSerialized] protected AnimVector3 m_FloorNormal;

        // Prediction and reconciliation
        [NonSerialized] private PredictedState[] m_PredictionHistory;
        [NonSerialized] private int m_PredictionHistoryStart;
        [NonSerialized] private int m_PredictionHistoryCount;
        [NonSerialized] private ushort m_CurrentSequence;
        [NonSerialized] private ushort m_LastAcknowledgedSequence;
        [NonSerialized] private bool m_HasIssuedInput;
        [NonSerialized] private bool m_HasAcknowledgedSequence;
        [NonSerialized] private bool m_HasTeleportSequenceBarrier;
        [NonSerialized] private ushort m_TeleportSequenceBarrier;
        [NonSerialized] private Vector3 m_ReconciliationTarget;
        [NonSerialized] private bool m_IsReconciling;
        [NonSerialized] private float m_ReconciliationProgress;
        [NonSerialized] private Vector3 m_ReconciliationVisualOffset;
        [NonSerialized] private float m_ReconciliationVisualRotationOffsetY;
        [NonSerialized] private NetworkCharacterVisualPresentation m_VisualPresentation;
        [NonSerialized] private float m_ReconciliationSuppressedUntil;
        [NonSerialized] private float m_OwnerAuthorityPoseSyncUntil;
        [NonSerialized] private float m_LastMotionDiagnosticRealtime;
        [NonSerialized] private float m_LastExternalMoveDirectionRealtime;
        [NonSerialized] private float m_LastExplicitMoveDirectionRealtime;
        [NonSerialized] private bool m_PreserveExplicitMoveDirectionWhileTraversal;
        [NonSerialized] private bool m_ClimbDiagnosticOwnerWindowWasActive;

        // Input buffering
        [NonSerialized] private List<NetworkInputState> m_UnacknowledgedInputs;
        [NonSerialized] private float m_InputAccumulator;
        [NonSerialized] private float m_InputElapsedSinceSend;
        [NonSerialized] private Vector2 m_InputWeightedWorldDirection;
        [NonSerialized] private float m_InputDeltaQuantizationRemainderMs;

        // Per-frame vs per-tick decoupling state.
        // m_LastInputDirection / m_LastCameraTransform: sampled live each frame for diagnostics
        // and fallback. The packet direction is the time-weighted world-space input that was
        // actually predicted throughout the complete send interval.
        // m_PendingJumpForTick: set the moment a jump impulse is applied locally so the next
        // outgoing tick informs the server. Cleared once consumed by the tick snapshot.
        [NonSerialized] private Vector2 m_LastInputDirection;
        [NonSerialized] private Transform m_LastCameraTransform;
        [NonSerialized] private bool m_PendingJumpForTick;
        [NonSerialized] private Vector3 m_PendingRootMotionDeltaForTick;
        [NonSerialized] private Vector3 m_PendingMovementTranslationForTick;
        [NonSerialized] private Vector3 m_PendingExternalRootTranslationForTick;
        [NonSerialized] private bool m_AcceptsNetworkMotion;
        [NonSerialized] private bool m_GameplayRootSuspended;
        [NonSerialized] private bool m_RagdollEventSuspended;
        [NonSerialized] private bool m_TeleportRotationPending;
        [NonSerialized] private int m_TeleportRotationPendingFrame;

        [NonSerialized] protected int m_GroundFrame = -100;
        [NonSerialized] protected float m_GroundTime = -100f;
        [NonSerialized] private bool m_IsOnSteepSlope;

        // EVENTS: --------------------------------------------------------------------------------

        /// <summary>
        /// Fired when input should be sent to server. Contains all unacknowledged inputs for redundancy.
        /// </summary>
        public event Action<NetworkInputState[]> OnSendInput;

        /// <summary>
        /// Fired when reconciliation occurs (useful for debugging).
        /// </summary>
        public event Action<float> OnReconciliation;

        // INTERFACE PROPERTIES: ------------------------------------------------------------------

        public override Vector3 WorldMoveDirection => this.m_MoveDirection;
        public override Vector3 LocalMoveDirection => this.Transform.InverseTransformDirection(this.m_MoveDirection);

        public override float SkinWidth => this.m_Controller != null ? this.m_Controller.skinWidth : 0f;

        public override bool IsGrounded
        {
            get
            {
                if (this.m_Controller == null) return false;
                if (this.m_ForceGrounded) return true;
                if (this.m_Controller.isGrounded) return !this.m_IsOnSteepSlope;

                return TryProbeGround(out RaycastHit hit) &&
                       Vector3.Angle(hit.normal, Vector3.up) <= m_MaxSlope;
            }
        }

        public override Vector3 FloorNormal => this.m_FloorNormal?.Current ?? Vector3.up;

        public override bool Collision
        {
            get => this.m_Controller != null && this.m_Controller.detectCollisions;
            set { if (this.m_Controller != null) this.m_Controller.detectCollisions = value; }
        }

        public override Axonometry Axonometry
        {
            get => this.m_Axonometry;
            set => this.m_Axonometry = value;
        }

        public ushort CurrentSequence => m_CurrentSequence;
        public NetworkCharacterConfig Config => m_Config;
        public float OwnerMotionAuthorityRemaining =>
            Mathf.Max(0f, m_OwnerAuthorityPoseSyncUntil - Time.time);
        public float ReconciliationSuppressionRemaining =>
            Mathf.Max(0f, m_ReconciliationSuppressedUntil - Time.time);

        public void SetExternalMoveDirection(Vector3 velocity)
        {
            SetExternalMoveDirection(velocity, false);
        }

        public void SetExternalMoveDirection(
            Vector3 velocity,
            bool preserveWhileTraversalLikeMotion)
        {
            this.m_MoveDirection = velocity;
            this.m_LastExplicitMoveDirectionRealtime = Time.realtimeSinceStartup;
            this.m_PreserveExplicitMoveDirectionWhileTraversal =
                preserveWhileTraversalLikeMotion && IsTraversalLikeAuthorityMotion();
        }

        /// <summary>
        /// Temporarily defers smooth owner reconciliation while another authoritative gameplay
        /// system is driving local root motion, such as a server-confirmed melee reaction.
        /// Large corrections still snap through once they exceed maxReconciliationDistance.
        /// </summary>
        public void SuppressReconciliation(float duration)
        {
            if (duration <= 0f) return;
            m_ReconciliationSuppressedUntil = Mathf.Max(m_ReconciliationSuppressedUntil, Time.time + duration);
        }

        /// <summary>
        /// Temporarily includes the locally applied owner pose in outgoing inputs. This is used
        /// for server-confirmed gameplay root motion where the remote server replica cannot
        /// reliably reproduce the owner's animation delta, such as melee hit reactions.
        /// </summary>
        public void EnableOwnerAuthorityPoseSync(float duration)
        {
            if (duration <= 0f) return;

            float until = Time.time + duration;
            if (until <= m_OwnerAuthorityPoseSyncUntil) return;

            m_OwnerAuthorityPoseSyncUntil = until;
            ClearVisualReconciliationOffset();
            // Traversal and other owner-authored root-motion systems expect the authored
            // Mannequin hierarchy while they run. Recreate the render-only wrapper lazily if a
            // later ordinary locomotion correction needs it.
            ReleaseVisualPresentation();
            LogFocusedTraversalMotion(
                "OwnerWindow",
                $"side=client operation=open duration={duration:F3} until={m_OwnerAuthorityPoseSyncUntil:F3} " +
                $"position={NetworkTraversalClimbDiagnostics.Vector(this.Transform.position)}",
                $"client-window-open:{this.Character?.GetInstanceID() ?? 0}");
            LogTraversalPose(
                $"owner-authority-pose-sync-enabled duration={duration:F3} until={m_OwnerAuthorityPoseSyncUntil:F3} " +
                $"rootMotion={this.Character?.RootMotionPosition ?? 0f:F3} {FormatBusyState()}");
        }

        /// <inheritdoc />
        public void OpenOwnerMotionWindow(float durationSeconds)
        {
            if (durationSeconds <= 0f) return;

            SuppressReconciliation(durationSeconds);
            EnableOwnerAuthorityPoseSync(durationSeconds);
        }

        /// <summary>
        /// Visual offset caused by reconciliation. External systems (camera, visual mesh)
        /// should read this to smooth the visual snap. Decays to zero over time.
        /// </summary>
        public Vector3 ReconciliationVisualOffset => m_ReconciliationVisualOffset;

        /// <summary>
        /// Whether smooth reconciliation is currently in progress.
        /// </summary>
        public bool IsReconciling => m_IsReconciling;

        public void ApplySessionProfile(NetworkSessionProfile profile)
        {
            if (profile == null) return;

            m_Config.inputSendRate = profile.inputSendRate;
            m_Config.inputRedundancy = profile.inputRedundancy;
            m_Config.reconciliationThreshold = profile.reconciliationThreshold;
            m_Config.maxReconciliationDistance = profile.maxReconciliationDistance;
            m_Config.reconciliationSpeed = profile.reconciliationSpeed;
            m_Config.maxSpeedMultiplier = profile.maxSpeedMultiplier;
            m_Config.violationThreshold = profile.violationThreshold;
        }

        // STRUCTS: -------------------------------------------------------------------------------

        private struct PredictedState
        {
            public ushort sequence;
            public Vector3 position;
            public float rotationY;
            public float verticalSpeed;
            public NetworkInputState input;
            public bool updateKinematics;
            public Vector3 rootMotionDelta;
            public Vector3 movementTranslation;
        }

        private const int PREDICTION_HISTORY_CAPACITY = 128;
        private const float EXTERNAL_AUTHORITY_POSITION_THRESHOLD = 0.005f;
        private const float EXTERNAL_AUTHORITY_ROTATION_THRESHOLD = 0.25f;
        private const float EXTERNAL_MOVE_DIRECTION_SAMPLE_GRACE_SECONDS = 0.15f;

        // INITIALIZERS: --------------------------------------------------------------------------

        public UnitDriverNetworkClient()
        {
            this.m_MoveDirection = Vector3.zero;
            this.m_VerticalSpeed = 0f;
        }

        public override void OnStartup(Character character)
        {
            base.OnStartup(character);

            this.m_FloorNormal = new AnimVector3(Vector3.up, 0.15f);
            this.m_PredictionHistory = new PredictedState[PREDICTION_HISTORY_CAPACITY];
            this.m_PredictionHistoryStart = 0;
            this.m_PredictionHistoryCount = 0;
            this.m_UnacknowledgedInputs = new List<NetworkInputState>(32);
            this.m_CurrentSequence = 0;
            this.m_LastAcknowledgedSequence = 0;
            this.m_HasIssuedInput = false;
            this.m_HasAcknowledgedSequence = false;
            this.m_HasTeleportSequenceBarrier = false;
            this.m_TeleportSequenceBarrier = 0;
            this.m_InputAccumulator = 0f;
            this.m_InputElapsedSinceSend = 0f;
            this.m_InputWeightedWorldDirection = Vector2.zero;
            this.m_InputDeltaQuantizationRemainderMs = 0f;
            this.m_LastInputDirection = Vector2.zero;
            this.m_LastCameraTransform = null;
            this.m_PendingJumpForTick = false;
            this.m_PendingRootMotionDeltaForTick = Vector3.zero;
            this.m_PendingMovementTranslationForTick = Vector3.zero;
            this.m_PendingExternalRootTranslationForTick = Vector3.zero;
            this.m_AcceptsNetworkMotion = true;
            this.m_GameplayRootSuspended = false;
            this.m_RagdollEventSuspended = false;
            this.m_TeleportRotationPending = false;
            this.m_TeleportRotationPendingFrame = -1;
            this.m_ReconciliationTarget = Vector3.zero;
            this.m_ReconciliationVisualOffset = Vector3.zero;
            this.m_ReconciliationVisualRotationOffsetY = 0f;
            this.m_VisualPresentation = new NetworkCharacterVisualPresentation(
                this.Character,
                "ClientDriver");
            this.m_ReconciliationSuppressedUntil = 0f;
            this.m_OwnerAuthorityPoseSyncUntil = 0f;
            this.m_LastMotionDiagnosticRealtime = -100f;
            this.m_LastExternalMoveDirectionRealtime = -100f;
            this.m_LastExplicitMoveDirectionRealtime = -100f;
            this.m_PreserveExplicitMoveDirectionWhileTraversal = false;
            this.m_Controller = this.Character.GetComponent<CharacterController>();
            if (this.m_Controller == null)
            {
                this.m_Controller = this.Character.gameObject.AddComponent<CharacterController>();
                this.m_Controller.hideFlags = HideFlags.HideInInspector;

                float height = this.Character.Motion.Height;
                float radius = this.Character.Motion.Radius;

                this.m_Controller.height = height;
                this.m_Controller.radius = radius;
                this.m_Controller.center = Vector3.zero;
                this.m_Controller.skinWidth = this.m_SkinWidth;
                this.m_Controller.slopeLimit = this.m_MaxSlope;
                this.m_Controller.stepOffset = this.m_StepHeight;
                this.m_Controller.minMoveDistance = 0f;
            }

            if (this.Character.Ragdoll != null)
            {
                this.Character.Ragdoll.EventBeforeStartRagdoll -= OnBeforeStartRagdoll;
                this.Character.Ragdoll.EventBeforeStartRagdoll += OnBeforeStartRagdoll;
                this.Character.Ragdoll.EventAfterFinishRecover -= OnAfterFinishRagdollRecover;
                this.Character.Ragdoll.EventAfterFinishRecover += OnAfterFinishRagdollRecover;
            }
        }

        public override void OnEnable()
        {
            base.OnEnable();
            this.m_AcceptsNetworkMotion = true;
            if (this.Character != null)
            {
                this.m_GroundTime = this.Character.Time.Time;
                this.m_GroundFrame = this.Character.Time.Frame;
            }
        }

        public override void OnDispose(Character character)
        {
            if (character?.Ragdoll != null)
            {
                character.Ragdoll.EventBeforeStartRagdoll -= OnBeforeStartRagdoll;
                character.Ragdoll.EventAfterFinishRecover -= OnAfterFinishRagdollRecover;
            }

            ResetNetworkState();
            base.OnDispose(character);
            this.m_Controller = null;
        }

        public override void OnDisable()
        {
            ResetNetworkState();
            base.OnDisable();
        }

        /// <summary>
        /// Clears all prediction, acknowledgement, transient input, and reconciliation state for
        /// the current network lifecycle. The driver rejects late transport callbacks until GC2
        /// enables it again, so an old session cannot revive motion after teardown or a role swap.
        /// </summary>
        public void ResetNetworkState()
        {
            ClearVisualReconciliationOffset();

            if (m_PredictionHistory != null)
            {
                Array.Clear(m_PredictionHistory, 0, m_PredictionHistory.Length);
            }

            m_PredictionHistoryStart = 0;
            m_PredictionHistoryCount = 0;
            m_UnacknowledgedInputs?.Clear();
            m_CurrentSequence = 0;
            m_LastAcknowledgedSequence = 0;
            m_HasIssuedInput = false;
            m_HasAcknowledgedSequence = false;
            m_HasTeleportSequenceBarrier = false;
            m_TeleportSequenceBarrier = 0;

            m_InputAccumulator = 0f;
            m_InputElapsedSinceSend = 0f;
            m_InputWeightedWorldDirection = Vector2.zero;
            m_InputDeltaQuantizationRemainderMs = 0f;
            m_LastInputDirection = Vector2.zero;
            m_LastCameraTransform = null;
            m_PendingJumpForTick = false;
            m_PendingRootMotionDeltaForTick = Vector3.zero;
            m_PendingMovementTranslationForTick = Vector3.zero;
            m_PendingExternalRootTranslationForTick = Vector3.zero;

            m_MoveDirection = Vector3.zero;
            m_VerticalSpeed = 0f;
            m_ReconciliationTarget = Vector3.zero;
            m_ReconciliationProgress = 0f;
            m_ReconciliationSuppressedUntil = 0f;
            m_OwnerAuthorityPoseSyncUntil = 0f;
            m_LastMotionDiagnosticRealtime = -100f;
            m_LastExternalMoveDirectionRealtime = -100f;
            m_LastExplicitMoveDirectionRealtime = -100f;
            m_PreserveExplicitMoveDirectionWhileTraversal = false;
            m_ClimbDiagnosticOwnerWindowWasActive = false;
            m_IsOnSteepSlope = false;
            m_GroundFrame = -100;
            m_GroundTime = -100f;
            m_GameplayRootSuspended = false;
            m_RagdollEventSuspended = false;
            m_TeleportRotationPending = false;
            m_TeleportRotationPendingFrame = -1;
            m_AcceptsNetworkMotion = false;
            ReleaseVisualPresentation();
        }

        private void OnBeforeStartRagdoll()
        {
            m_RagdollEventSuspended = true;
            SuspendGameplayRoot();
        }

        private void OnAfterFinishRagdollRecover()
        {
            m_RagdollEventSuspended = false;
            m_GameplayRootSuspended = false;
            m_VerticalSpeed = 0f;
            m_MoveDirection = Vector3.zero;
            if (this.Character != null)
            {
                m_GroundTime = this.Character.Time.Time;
                m_GroundFrame = this.Character.Time.Frame;
            }
        }

        // CLIENT-SIDE PREDICTION: ----------------------------------------------------------------

        /// <summary>
        /// Process local input with client-side prediction.
        /// Call this every frame with player input.
        /// </summary>
        /// <remarks>
        /// This method runs two decoupled loops:
        /// 1) <b>Per-frame visual movement</b>: physical <see cref="CharacterController.Move"/>
        ///    is invoked every frame using live <c>Time.DeltaTime</c> and the latest input.
        ///    This makes locomotion smooth at any frame-rate, independent of the network tick.
        /// 2) <b>Per-tick networking</b>: at <c>1 / inputSendRate</c> intervals, a sequenced
        ///    <see cref="NetworkInputState"/> is built from the time-weighted input actually
        ///    predicted during that interval and sent to the server. The current transform is
        ///    captured as a prediction snapshot so <see cref="ApplyServerState"/> can reconcile.
        ///
        /// Reconciliation replay still uses <see cref="ApplyInputPrediction"/> with the stored
        /// per-tick input chunks, matching how the server processes them; per-frame motion
        /// resumes once replay completes.
        /// </remarks>
        public void ProcessLocalInput(Vector2 inputDirection, Transform cameraTransform, bool jump = false)
        {
            if (!CanProcessNetworkMotion()) return;
            if (ShouldSuspendGameplayRoot()) return;

            LogFocusedOwnerWindowTransition();
            float deltaTime = this.Character.Time.DeltaTime;

            // PER-FRAME: smooth visual movement using live frame dt.
            // We apply the jump impulse the moment it's requested for instant feel, then latch
            // a flag so the next outgoing tick still informs the server about the jump.
            bool applyJumpThisFrame = jump && CanJump();
            if (applyJumpThisFrame) m_PendingJumpForTick = true;

            ApplyFrameMovement(inputDirection, cameraTransform, deltaTime, applyJumpThisFrame);

            // Cache the latest input so the next tick boundary can snapshot a representative
            // sample. Accumulate the world-space direction with time weighting so a direction
            // or camera change inside one network interval reproduces the displacement that the
            // client actually predicted instead of assigning the newest sample to the whole
            // interval.
            m_LastInputDirection = inputDirection;
            m_LastCameraTransform = cameraTransform;
            Vector2 worldInputSample = ToWorldSpaceInput(inputDirection, cameraTransform);
            if (deltaTime > 0f && !float.IsNaN(deltaTime) && !float.IsInfinity(deltaTime))
            {
                m_InputWeightedWorldDirection += worldInputSample * deltaTime;
                m_InputElapsedSinceSend += deltaTime;
            }

            // PER-TICK: build sequenced inputs at the configured send rate. The payload
            // delta uses the real elapsed time since the last sent input, matching the
            // per-frame movement already applied to the transform. Sending a fixed
            // interval here causes alternating over/under-correction whenever the render
            // frame rate does not divide the input send rate cleanly.
            float inputInterval = 1f / Mathf.Max(1f, m_Config.inputSendRate);
            bool shouldSendInput = NetworkInputCadence.Advance(
                ref m_InputAccumulator,
                deltaTime,
                inputInterval);

            if (shouldSendInput && m_InputElapsedSinceSend > 0f)
            {
                // Preserve the scheduler phase. Resetting to zero aliases a nominal 60 FPS /
                // 60 Hz stream down to 30-40 packets per second whenever a render frame lands
                // just below the interval. Large hitches still produce at most one packet here;
                // the real elapsed payload below represents every predicted frame it contains.
                float elapsedSinceSend = m_InputElapsedSinceSend;
                Vector2 networkInput = m_InputWeightedWorldDirection / elapsedSinceSend;
                if (networkInput.sqrMagnitude > 1f) networkInput.Normalize();

                m_InputElapsedSinceSend = 0f;
                m_InputWeightedWorldDirection = Vector2.zero;

                float inputDeltaTime = NetworkInputCadence.QuantizeElapsedSeconds(
                    elapsedSinceSend,
                    ref m_InputDeltaQuantizationRemainderMs);
                Vector3 capturedRootMotionDelta = m_PendingRootMotionDeltaForTick;
                Vector3 capturedMovementTranslation = ConsumePendingMovementTranslationForTick();
                m_PendingRootMotionDeltaForTick = Vector3.zero;

                byte flags = 0;
                if (m_PendingJumpForTick) flags |= NetworkInputState.FLAG_JUMP;
                m_PendingJumpForTick = false;

                Vector3? ownerAuthorityPosition = IsOwnerAuthorityPoseSyncActive
                    ? this.Transform.position
                    : null;

                NetworkInputState input = NetworkInputState.Create(
                    networkInput,
                    m_CurrentSequence,
                    inputDeltaTime,
                    flags,
                    this.Transform.eulerAngles.y,
                    ownerAuthorityPosition
                );

                if (ownerAuthorityPosition.HasValue)
                {
                    Vector3 traversalPresentationDirection = Vector3.zero;
                    if (this.Character.Motion is UnitMotionNetworkController networkMotion)
                    {
                        networkMotion.TryGetTraversalPresentationDirection(
                            out traversalPresentationDirection);
                    }

                    // Presence is intentional even for zero. A sequenced zero sample clears a
                    // previously held edge direction without depending on a separate semantic
                    // StopDirection command arriving first.
                    input.SetTraversalPresentationDirection(traversalPresentationDirection);
                }

                if (ownerAuthorityPosition.HasValue)
                {
                    LogFocusedTraversalMotion(
                        "OwnerPoseSend",
                        $"seq={input.sequenceNumber} hasPose=true pose={NetworkTraversalClimbDiagnostics.Vector(input.GetOwnerAuthorityPosition())} " +
                        $"raw={NetworkTraversalClimbDiagnostics.Vector(m_LastInputDirection)} network={NetworkTraversalClimbDiagnostics.Vector(networkInput)} " +
                        $"dt={input.GetDeltaTime():F3} unacked={m_UnacknowledgedInputs.Count} " +
                        $"capturedMovement={NetworkTraversalClimbDiagnostics.Vector(capturedMovementTranslation)} " +
                        $"capturedRootMotion={NetworkTraversalClimbDiagnostics.Vector(capturedRootMotionDelta)} " +
                        $"hasTraversalDirection={input.HasTraversalPresentationDirection} " +
                        $"traversalDirection={NetworkTraversalClimbDiagnostics.Vector(input.GetTraversalPresentationDirection())} " +
                        $"ownerWindowRemaining={OwnerMotionAuthorityRemaining:F3} " +
                        $"updateKinematics={this.UpdateKinematics} grounded={IsGrounded} " +
                        $"rootMotion={this.Character.RootMotionPosition:F3}",
                        $"client-owner-pose:{this.Character.GetInstanceID()}");
                    LogTraversalPose(
                        $"send-owner-authority-input seq={input.sequenceNumber} dt={input.GetDeltaTime():F3} " +
                        $"rawInput={FormatVector2(m_LastInputDirection)} networkInput={FormatVector2(networkInput)} " +
                        $"inputRotY={input.GetRotationY():F2} transformRotY={this.Transform.eulerAngles.y:F2} " +
                        $"ownerPos={FormatVector(input.GetOwnerAuthorityPosition())} " +
                        $"ownerPosY={input.GetOwnerAuthorityPosition().y:F3} " +
                        $"currentSeq={m_CurrentSequence} unacked={m_UnacknowledgedInputs.Count} " +
                        $"rootMotion={this.Character.RootMotionPosition:F3} {FormatBusyState()}");
                }

                // Store for potential resend.
                m_UnacknowledgedInputs.Add(input);

                // Snapshot the current ACTUAL transform after this tick's worth of per-frame
                // movement has already been applied. This is the position the server will
                // reconcile against once it processes the matching input sequence.
                AppendPredictionState(new PredictedState
                {
                    sequence = m_CurrentSequence,
                    position = this.Transform.position,
                    rotationY = this.Transform.eulerAngles.y,
                    verticalSpeed = m_VerticalSpeed,
                    input = input,
                    updateKinematics = this.UpdateKinematics,
                    rootMotionDelta = capturedRootMotionDelta,
                    movementTranslation = capturedMovementTranslation
                });

                m_HasIssuedInput = true;
                m_CurrentSequence++;

                SendInputsToServer();
            }

            // Handle reconciliation smoothing
            if (m_IsReconciling)
            {
                UpdateReconciliation(deltaTime);
            }
        }

        public void ProcessDirectionalInput(Vector2 inputDirection, Transform cameraTransform, bool jump)
        {
            ProcessLocalInput(inputDirection, cameraTransform, jump);
        }

        /// <summary>
        /// Per-frame movement step. Runs at the host/owner's render frame rate so the visual
        /// transform advances smoothly regardless of <c>inputSendRate</c>. The server still
        /// simulates authoritatively at its own tick rate; reconciliation hides any divergence.
        /// </summary>
        private void ApplyFrameMovement(Vector2 rawInput, Transform cameraTransform, float deltaTime, bool applyJump)
        {
            if (m_Controller == null || !m_Controller.enabled) return;
            if (deltaTime <= 0f) return;

            Vector3 inputDirection = new Vector3(rawInput.x, 0f, rawInput.y);

            if (cameraTransform != null)
            {
                Quaternion cameraRotation = Quaternion.Euler(0f, cameraTransform.eulerAngles.y, 0f);
                inputDirection = cameraRotation * inputDirection;
            }

            // Use sqrMagnitude clamp instead of unconditional Normalize so that analog input
            // (joystick at 50%) maps to half speed, matching standard locomotion behavior.
            if (inputDirection.sqrMagnitude > 1f) inputDirection.Normalize();

            float speed = this.Character.Motion.LinearSpeed;
            Vector3 horizontalMovement = this.UpdateKinematics
                ? inputDirection * speed * deltaTime
                : Vector3.zero;

            UpdateGravity(deltaTime);

            if (applyJump)
            {
                m_VerticalSpeed = this.Character.Motion.JumpForce;
            }

            Vector3 rootMotionDelta = this.Character.Animim.RootMotionDeltaPosition;
            if (!NetworkCharacterVisualPresentation.IsFinite(rootMotionDelta))
            {
                rootMotionDelta = Vector3.zero;
            }

            Vector3 translation = ApplyRootMotionBlend(horizontalMovement, rootMotionDelta);
            translation = this.m_Axonometry?.ProcessTranslation(this, translation) ?? translation;
            if (!NetworkCharacterVisualPresentation.IsFinite(translation))
            {
                translation = Vector3.zero;
            }

            // Reconciliation can run several render frames after this movement was first
            // predicted. Capture the root-motion sample and final processed translation now;
            // replay must never read the Animator's delta from that later render frame.
            m_PendingRootMotionDeltaForTick += rootMotionDelta;
            m_PendingMovementTranslationForTick += translation;

            Vector3 totalMovement = translation + Vector3.up * m_VerticalSpeed * deltaTime;
            m_Controller.Move(totalMovement);

            if (IsGrounded && m_VerticalSpeed < 0)
            {
                m_VerticalSpeed = -2f;
                m_GroundTime = this.Character.Time.Time;
                m_GroundFrame = this.Character.Time.Frame;
            }

            if (!ShouldPreserveExternalMoveDirectionForAnimation())
            {
                m_MoveDirection = translation / deltaTime;
            }
        }

        private void ApplyInputPrediction(
            NetworkInputState input,
            Transform cameraTransform,
            bool updateKinematics)
        {
            ApplyInputPredictionInternal(
                input,
                cameraTransform,
                updateKinematics,
                false,
                Vector3.zero);
        }

        private void ApplyCapturedInputPrediction(PredictedState state)
        {
            ApplyInputPredictionInternal(
                state.input,
                null,
                state.updateKinematics,
                true,
                state.movementTranslation);
        }

        private void ApplyInputPredictionInternal(
            NetworkInputState input,
            Transform cameraTransform,
            bool updateKinematics,
            bool useCapturedMovementTranslation,
            Vector3 capturedMovementTranslation)
        {
            Vector2 rawInput = input.GetInputDirection();
            float deltaTime = input.GetDeltaTime();
            this.Transform.rotation = Quaternion.Euler(0f, input.GetRotationY(), 0f);

            // Convert to world direction
            Vector3 inputDirection = new Vector3(rawInput.x, 0f, rawInput.y);

            if (cameraTransform != null)
            {
                Quaternion cameraRotation = Quaternion.Euler(0f, cameraTransform.eulerAngles.y, 0f);
                inputDirection = cameraRotation * inputDirection;
            }

            if (inputDirection.sqrMagnitude > 1f) inputDirection.Normalize();

            // Calculate movement
            float speed = this.Character.Motion.LinearSpeed;
            Vector3 horizontalMovement = updateKinematics
                ? inputDirection * speed * deltaTime
                : Vector3.zero;

            // Apply gravity
            UpdateGravity(deltaTime);

            // Handle jump
            if (input.HasFlag(NetworkInputState.FLAG_JUMP))
            {
                m_VerticalSpeed = this.Character.Motion.JumpForce;
            }

            Vector3 translation;
            if (useCapturedMovementTranslation)
            {
                translation = NetworkCharacterVisualPresentation.IsFinite(capturedMovementTranslation)
                    ? capturedMovementTranslation
                    : Vector3.zero;
            }
            else
            {
                // This compatibility path intentionally has no root-motion sample. Using the
                // current Animator.deltaPosition here would replay an unrelated render frame.
                translation = this.m_Axonometry?.ProcessTranslation(this, horizontalMovement) ??
                              horizontalMovement;
            }

            // Combine and move
            Vector3 totalMovement = translation + Vector3.up * m_VerticalSpeed * deltaTime;

            if (m_Controller != null && m_Controller.enabled)
            {
                m_Controller.Move(totalMovement);
            }

            // Update grounded
            if (IsGrounded && m_VerticalSpeed < 0)
            {
                m_VerticalSpeed = -2f;
                m_GroundTime = this.Character.Time.Time;
                m_GroundFrame = this.Character.Time.Frame;
            }

            if (updateKinematics && deltaTime > 0f &&
                !ShouldPreserveExternalMoveDirectionForAnimation())
            {
                m_MoveDirection = translation / deltaTime;
            }
        }

        private Vector3 ApplyRootMotionBlend(Vector3 kineticMovement, Vector3 rootMotionDelta)
        {
            return Vector3.Lerp(
                kineticMovement,
                rootMotionDelta,
                this.Character.RootMotionPosition);
        }

        private void SendInputsToServer()
        {
            // Send recent inputs for redundancy
            int count = Mathf.Min(m_UnacknowledgedInputs.Count, m_Config.inputRedundancy);
            if (count > 0)
            {
                NetworkInputState[] inputs = new NetworkInputState[count];
                for (int i = 0; i < count; i++)
                {
                    inputs[i] = m_UnacknowledgedInputs[m_UnacknowledgedInputs.Count - count + i];
                }

                OnSendInput?.Invoke(inputs);
            }
        }

        private static Vector2 ToWorldSpaceInput(Vector2 rawInput, Transform cameraTransform)
        {
            Vector3 inputDirection = new Vector3(rawInput.x, 0f, rawInput.y);

            if (cameraTransform != null)
            {
                Quaternion cameraRotation = Quaternion.Euler(0f, cameraTransform.eulerAngles.y, 0f);
                inputDirection = cameraRotation * inputDirection;
            }

            if (inputDirection.sqrMagnitude > 1f) inputDirection.Normalize();
            return new Vector2(inputDirection.x, inputDirection.z);
        }

        // SERVER RECONCILIATION: -----------------------------------------------------------------

        /// <summary>
        /// Apply authoritative state from server and reconcile if needed.
        /// Call this when receiving server state updates.
        /// </summary>
        public void ApplyServerState(NetworkPositionState serverState)
        {
            if (!CanProcessNetworkMotion()) return;
            if (ShouldSuspendGameplayRoot()) return;
            if (ShouldRejectServerStateSequence(serverState.lastProcessedInput)) return;

            Vector3 focusedClientPositionBefore = this.Transform.position;
            Vector3 focusedServerPosition = serverState.GetPosition();
            float focusedInitialDistance = Vector3.Distance(focusedClientPositionBefore, focusedServerPosition);

            // Remove acknowledged inputs
            m_UnacknowledgedInputs?.RemoveAll(
                i => !IsSequenceNewer(i.sequenceNumber, serverState.lastProcessedInput));

            // Find the predicted state at this sequence
            int predictedIndex = -1;
            for (int i = 0; i < m_PredictionHistoryCount; i++)
            {
                if (GetPredictionState(i).sequence == serverState.lastProcessedInput)
                {
                    predictedIndex = i;
                    break;
                }
            }

            bool externalAuthorityActive = Time.time < m_ReconciliationSuppressedUntil;
            bool ownerAuthorityPoseActive = IsOwnerAuthorityPoseSyncActive;

            if (predictedIndex >= 0)
            {
                Vector3 serverPosition = serverState.GetPosition();
                float serverRotationY = serverState.GetRotationY();
                PredictedState predictedState = GetPredictionState(predictedIndex);
                Vector3 predictedPosition = predictedState.position;
                float positionError = Vector3.Distance(serverPosition, predictedPosition);
                bool externalAuthorityApplied = !ownerAuthorityPoseActive &&
                    externalAuthorityActive &&
                    TryApplyExternalAuthorityState(serverState, predictedIndex);

                if (ownerAuthorityPoseActive)
                {
                    LogTraversalPose(
                        $"apply-server-state-owner-authority-active seq={serverState.lastProcessedInput} " +
                        $"server={FormatVector(serverPosition)} serverY={serverPosition.y:F3} " +
                        $"serverRotY={serverRotationY:F2} predicted={FormatVector(predictedPosition)} " +
                        $"predictedY={predictedPosition.y:F3} predictedRotY={predictedState.rotationY:F2} " +
                        $"error={positionError:F3} history={m_PredictionHistoryCount} " +
                        $"unacked={m_UnacknowledgedInputs.Count} currentSeq={m_CurrentSequence} " +
                        $"rootMotion={this.Character.RootMotionPosition:F3} {FormatBusyState()}");
                    TraceTraversalMotion(
                        $"owner pose active: skipped correction with prediction seq={serverState.lastProcessedInput} " +
                        $"error={positionError:F3} server={FormatVector(serverPosition)} predicted={FormatVector(predictedPosition)} " +
                        $"current={FormatVector(this.Transform.position)} history={m_PredictionHistoryCount} " +
                        $"unacked={m_UnacknowledgedInputs.Count} currentSeq={m_CurrentSequence} " +
                        $"rootMotion={this.Character.RootMotionPosition:F3}");
                    LogClientMotionDiagnostic(
                        $"owner authority pose active; skipped server correction seq={serverState.lastProcessedInput} " +
                        $"error={positionError:F3} server={FormatVector(serverPosition)} predicted={FormatVector(predictedPosition)} " +
                        $"current={FormatVector(this.Transform.position)} history={m_PredictionHistoryCount} " +
                        $"unacked={m_UnacknowledgedInputs.Count} currentSeq={m_CurrentSequence} " +
                        $"rootMotion={this.Character.RootMotionPosition:F3}");
                }
                else if (externalAuthorityApplied)
                {
                    TraceTraversalMotion(
                        $"external authority correction applied seq={serverState.lastProcessedInput} " +
                        $"server={FormatVector(serverPosition)} current={FormatVector(this.Transform.position)} " +
                        $"history={m_PredictionHistoryCount} unacked={m_UnacknowledgedInputs.Count}");
                    OnReconciliation?.Invoke(positionError);
                }
                else if (positionError > m_Config.reconciliationThreshold)
                {
                    TraceTraversalMotion(
                        $"reconcile applying seq={serverState.lastProcessedInput} error={positionError:F3} " +
                        $"mode={(positionError > m_Config.maxReconciliationDistance ? "teleport" : "smooth")} " +
                        $"threshold={m_Config.reconciliationThreshold:F3} max={m_Config.maxReconciliationDistance:F3} " +
                        $"server={FormatVector(serverPosition)} predicted={FormatVector(predictedPosition)} " +
                        $"current={FormatVector(this.Transform.position)} history={m_PredictionHistoryCount} " +
                        $"unacked={m_UnacknowledgedInputs.Count} currentSeq={m_CurrentSequence} " +
                        $"rootMotion={this.Character.RootMotionPosition:F3}");
                    LogClientMotionDiagnostic(
                        $"reconcile seq={serverState.lastProcessedInput} error={positionError:F3} " +
                        $"threshold={m_Config.reconciliationThreshold:F3} max={m_Config.maxReconciliationDistance:F3} " +
                        $"server={FormatVector(serverPosition)} predicted={FormatVector(predictedPosition)} " +
                        $"current={FormatVector(this.Transform.position)} history={m_PredictionHistoryCount} " +
                        $"unacked={m_UnacknowledgedInputs.Count} currentSeq={m_CurrentSequence} " +
                        $"rootMotion={this.Character.RootMotionPosition:F3} visualOffset={FormatVector(m_ReconciliationVisualOffset)}",
                        force: positionError > m_Config.maxReconciliationDistance);

                    // Need reconciliation
                    if (positionError > m_Config.maxReconciliationDistance)
                    {
                        // Teleport - too far off
                        InvalidatePredictionForTeleport(clearAuthorityWindows: false);
                        TeleportTo(serverPosition, serverRotationY, serverState.GetVerticalVelocity());
                    }
                    else
                    {
                        // Smooth reconciliation
                        StartReconciliation(serverPosition, serverRotationY, serverState.GetVerticalVelocity(), predictedIndex);
                    }

                    OnReconciliation?.Invoke(positionError);
                }

                // Remove old prediction history
                if (predictedIndex > 0)
                {
                    RemoveOldestPredictionStates(predictedIndex);
                }
            }
            else if (ownerAuthorityPoseActive)
            {
                LogTraversalPose(
                    $"apply-server-state-owner-authority-active-no-prediction seq={serverState.lastProcessedInput} " +
                    $"server={FormatVector(serverState.GetPosition())} serverY={serverState.GetPosition().y:F3} " +
                    $"serverRotY={serverState.GetRotationY():F2} history={m_PredictionHistoryCount} " +
                    $"unacked={m_UnacknowledgedInputs.Count} currentSeq={m_CurrentSequence} " +
                    $"rootMotion={this.Character.RootMotionPosition:F3} {FormatBusyState()}");
                TraceTraversalMotion(
                    $"owner pose active: skipped correction without prediction seq={serverState.lastProcessedInput} " +
                    $"server={FormatVector(serverState.GetPosition())} current={FormatVector(this.Transform.position)} " +
                    $"history={m_PredictionHistoryCount} unacked={m_UnacknowledgedInputs.Count} currentSeq={m_CurrentSequence} " +
                    $"rootMotion={this.Character.RootMotionPosition:F3}");
                LogClientMotionDiagnostic(
                    $"owner authority pose active; skipped server correction without prediction seq={serverState.lastProcessedInput} " +
                    $"server={FormatVector(serverState.GetPosition())} current={FormatVector(this.Transform.position)} " +
                    $"history={m_PredictionHistoryCount} unacked={m_UnacknowledgedInputs.Count} currentSeq={m_CurrentSequence} " +
                    $"rootMotion={this.Character.RootMotionPosition:F3}");
            }
            else if (externalAuthorityActive)
            {
                bool applied = TryApplyExternalAuthorityState(serverState, -1);
                TraceTraversalMotion(
                    $"external authority active without prediction seq={serverState.lastProcessedInput} applied={applied} " +
                    $"server={FormatVector(serverState.GetPosition())} current={FormatVector(this.Transform.position)} " +
                    $"history={m_PredictionHistoryCount} unacked={m_UnacknowledgedInputs.Count}");
            }
            else if (m_PredictionHistoryCount > 0)
            {
                LogClientMotionDiagnostic(
                    $"server ack has no prediction seq={serverState.lastProcessedInput} " +
                    $"history={m_PredictionHistoryCount} first={GetPredictionState(0).sequence} " +
                    $"latest={GetPredictionState(m_PredictionHistoryCount - 1).sequence} " +
                    $"unacked={m_UnacknowledgedInputs.Count} currentSeq={m_CurrentSequence}");
            }

            if (NetworkTraversalClimbDiagnostics.IsFocused(this.Character?.gameObject))
            {
                string mode = ownerAuthorityPoseActive
                    ? "suppressed-owner-window"
                    : focusedInitialDistance > m_Config.maxReconciliationDistance
                        ? "teleport"
                        : focusedInitialDistance > m_Config.reconciliationThreshold
                            ? "smooth"
                            : externalAuthorityActive ? "external-window" : "none";
                LogFocusedTraversalMotion(
                    "Reconcile",
                    $"seq={serverState.lastProcessedInput} predictionFound={predictedIndex >= 0} " +
                    $"ownerWindow={ownerAuthorityPoseActive} suppression={externalAuthorityActive} " +
                    $"mode={mode} distance={focusedInitialDistance:F3} " +
                    $"threshold={m_Config.reconciliationThreshold:F3} max={m_Config.maxReconciliationDistance:F3} " +
                    $"server={NetworkTraversalClimbDiagnostics.Vector(focusedServerPosition)} " +
                    $"before={NetworkTraversalClimbDiagnostics.Vector(focusedClientPositionBefore)} " +
                    $"after={NetworkTraversalClimbDiagnostics.Vector(this.Transform.position)} " +
                    $"grounded={IsGrounded} reconciling={m_IsReconciling} " +
                    $"ownerRemaining={Mathf.Max(0f, m_OwnerAuthorityPoseSyncUntil - Time.time):F3} " +
                    $"suppressRemaining={Mathf.Max(0f, m_ReconciliationSuppressedUntil - Time.time):F3}",
                    $"client-reconcile:{this.Character.GetInstanceID()}");
            }
        }

        private void LogFocusedOwnerWindowTransition()
        {
            if (!NetworkTraversalClimbDiagnostics.IsFocused(this.Character?.gameObject)) return;

            bool active = IsOwnerAuthorityPoseSyncActive;
            if (active == m_ClimbDiagnosticOwnerWindowWasActive) return;
            m_ClimbDiagnosticOwnerWindowWasActive = active;
            LogFocusedTraversalMotion(
                "OwnerWindow",
                $"side=client operation={(active ? "active" : "expired")} " +
                $"remaining={Mathf.Max(0f, m_OwnerAuthorityPoseSyncUntil - Time.time):F3} " +
                $"position={NetworkTraversalClimbDiagnostics.Vector(this.Transform.position)}");
        }

        private void LogFocusedTraversalMotion(string stage, string message, string sampleKey = null)
        {
            if (!NetworkTraversalClimbDiagnostics.IsFocused(this.Character?.gameObject)) return;
            NetworkCharacter networkCharacter = this.Character.GetComponent<NetworkCharacter>();
            NetworkTraversalClimbDiagnostics.Log(
                stage,
                $"actor={networkCharacter?.NetworkId ?? 0} role={networkCharacter?.CurrentRole.ToString() ?? "none"} {message}",
                this.Character,
                sampleKey);
        }

        private bool TryApplyExternalAuthorityState(NetworkPositionState serverState, int fromIndex)
        {
            Vector3 serverPosition = serverState.GetPosition();
            float serverRotationY = serverState.GetRotationY();
            float positionError = Vector3.Distance(serverPosition, this.Transform.position);
            float rotationError = Mathf.Abs(Mathf.DeltaAngle(serverRotationY, this.Transform.eulerAngles.y));
            if (positionError <= EXTERNAL_AUTHORITY_POSITION_THRESHOLD &&
                rotationError <= EXTERNAL_AUTHORITY_ROTATION_THRESHOLD)
            {
                return false;
            }

            LogClientMotionDiagnostic(
                $"external authority sync seq={serverState.lastProcessedInput} error={positionError:F3} " +
                $"rotError={rotationError:F2} server={FormatVector(serverPosition)} current={FormatVector(this.Transform.position)} " +
                $"history={m_PredictionHistoryCount} unacked={m_UnacknowledgedInputs.Count} currentSeq={m_CurrentSequence} " +
                $"rootMotion={this.Character.RootMotionPosition:F3} visualOffset={FormatVector(m_ReconciliationVisualOffset)}",
                force: positionError > m_Config.maxReconciliationDistance);

            StartExternalAuthorityCorrection(
                serverPosition,
                serverRotationY,
                serverState.GetVerticalVelocity(),
                fromIndex);
            return true;
        }

        private bool IsOwnerAuthorityPoseSyncActive => Time.time < m_OwnerAuthorityPoseSyncUntil;

        private bool CanProcessNetworkMotion()
        {
            if (this.Character == null) return false;
            if (m_AcceptsNetworkMotion) return true;

            // NetworkCharacter can reuse the same serialized GC2 driver when a role is assigned
            // again without CharacterKernel replacing it. Reactivate only after that lifecycle is
            // observably local-owner and this exact driver is active; a teardown callback remains
            // rejected once Cleanup changes the role to None.
            NetworkCharacter networkCharacter = this.Character.GetComponent<NetworkCharacter>();
            if (networkCharacter == null ||
                networkCharacter.CurrentRole != NetworkCharacter.NetworkRole.LocalClient ||
                !ReferenceEquals(networkCharacter.ActiveDriver, this))
            {
                return false;
            }

            m_AcceptsNetworkMotion = true;
            return true;
        }

        private bool ShouldSuspendGameplayRoot()
        {
            bool shouldSuspend = m_RagdollEventSuspended ||
                                 this.Character.IsDead ||
                                 (this.Character.Ragdoll != null && this.Character.Ragdoll.IsRagdoll);
            if (shouldSuspend)
            {
                SuspendGameplayRoot();
                return true;
            }

            if (m_GameplayRootSuspended)
            {
                m_GameplayRootSuspended = false;
                m_VerticalSpeed = 0f;
                m_MoveDirection = Vector3.zero;
                m_GroundTime = this.Character.Time.Time;
                m_GroundFrame = this.Character.Time.Frame;
            }

            return false;
        }

        private void SuspendGameplayRoot()
        {
            if (m_GameplayRootSuspended) return;

            m_GameplayRootSuspended = true;
            InvalidatePredictionState(
                establishTeleportBarrier: true,
                clearAuthorityWindows: true);
        }

        private bool ShouldRejectServerStateSequence(ushort acknowledgedSequence)
        {
            // A true teleport invalidates every prediction that existed before it. States which
            // only acknowledge those discarded inputs may arrive later on the unreliable state
            // channel, but must never be allowed to reconcile the root back across the teleport.
            if (m_HasTeleportSequenceBarrier)
            {
                if (!IsSequenceNewer(acknowledgedSequence, m_TeleportSequenceBarrier))
                {
                    return true;
                }

                m_HasTeleportSequenceBarrier = false;
            }

            // Equal acknowledgements can legitimately carry a newer authoritative pose. Only a
            // sequence which is strictly older than the last accepted acknowledgement is stale.
            if (m_HasAcknowledgedSequence &&
                IsSequenceNewer(m_LastAcknowledgedSequence, acknowledgedSequence))
            {
                return true;
            }

            m_LastAcknowledgedSequence = acknowledgedSequence;
            m_HasAcknowledgedSequence = true;
            return false;
        }

        private void InvalidatePredictionForTeleport(bool clearAuthorityWindows)
        {
            InvalidatePredictionState(
                establishTeleportBarrier: true,
                clearAuthorityWindows: clearAuthorityWindows);
        }

        private void InvalidatePredictionState(
            bool establishTeleportBarrier,
            bool clearAuthorityWindows)
        {
            ClearVisualReconciliationOffset();
            ReleaseVisualPresentation();

            if (establishTeleportBarrier && m_HasIssuedInput)
            {
                ushort latestIssuedSequence = (ushort)(m_CurrentSequence - 1);
                if (!m_HasTeleportSequenceBarrier ||
                    IsSequenceNewer(latestIssuedSequence, m_TeleportSequenceBarrier))
                {
                    m_TeleportSequenceBarrier = latestIssuedSequence;
                }

                m_HasTeleportSequenceBarrier = true;
            }

            if (m_PredictionHistory != null)
            {
                Array.Clear(m_PredictionHistory, 0, m_PredictionHistory.Length);
            }

            m_PredictionHistoryStart = 0;
            m_PredictionHistoryCount = 0;
            m_UnacknowledgedInputs?.Clear();
            m_InputAccumulator = 0f;
            m_InputElapsedSinceSend = 0f;
            m_InputWeightedWorldDirection = Vector2.zero;
            m_InputDeltaQuantizationRemainderMs = 0f;
            m_LastInputDirection = Vector2.zero;
            m_LastCameraTransform = null;
            m_PendingJumpForTick = false;
            m_PendingRootMotionDeltaForTick = Vector3.zero;
            m_PendingMovementTranslationForTick = Vector3.zero;
            m_PendingExternalRootTranslationForTick = Vector3.zero;
            m_MoveDirection = Vector3.zero;
            m_VerticalSpeed = 0f;
            m_ReconciliationTarget = Vector3.zero;
            m_ReconciliationProgress = 0f;
            m_LastExternalMoveDirectionRealtime = -100f;
            m_LastExplicitMoveDirectionRealtime = -100f;
            m_PreserveExplicitMoveDirectionWhileTraversal = false;

            if (clearAuthorityWindows)
            {
                m_ReconciliationSuppressedUntil = 0f;
                m_OwnerAuthorityPoseSyncUntil = 0f;
                m_ClimbDiagnosticOwnerWindowWasActive = false;
            }
        }

        private void StartExternalAuthorityCorrection(
            Vector3 serverPosition,
            float serverRotationY,
            float serverVerticalSpeed,
            int fromIndex)
        {
            bool capturedVisualPose = TryCaptureReconciliationPresentation(
                out Vector3 visiblePosition,
                out Quaternion visibleRotation);
            Vector3 previousRootPosition = this.Transform.position;
            float previousRootRotationY = this.Transform.eulerAngles.y;

            TeleportTo(serverPosition, serverRotationY, serverVerticalSpeed);

            Vector3 rootDelta = this.Transform.position - previousRootPosition;
            float rotationDeltaY = Mathf.DeltaAngle(previousRootRotationY, this.Transform.eulerAngles.y);
            RebasePredictionStatesAfter(fromIndex, rootDelta, rotationDeltaY);
            BeginVisualReconciliation(capturedVisualPose, visiblePosition, visibleRotation);
        }

        private void StartReconciliation(Vector3 serverPosition, float serverRotationY, float serverVerticalSpeed, int fromIndex)
        {
            // Capture the render-only wrapper pose before correcting the physics root. This
            // deliberately avoids GC2 Animim.Position/Rotation: those are independently smoothed
            // authored offsets, so using them for reconciliation creates two competing filters.
            bool capturedVisualPose = TryCaptureReconciliationPresentation(
                out Vector3 visiblePosition,
                out Quaternion visibleRotation);

            // Teleport to server position (physics correction)
            TeleportTo(serverPosition, serverRotationY, serverVerticalSpeed);

            // Re-apply all inputs after this point (standard CSP replay)
            for (int i = fromIndex + 1; i < m_PredictionHistoryCount; i++)
            {
                var state = GetPredictionState(i);
                ApplyCapturedInputPrediction(state);

                // Update the stored prediction
                SetPredictionState(i, new PredictedState
                {
                    sequence = state.sequence,
                    position = this.Transform.position,
                    rotationY = this.Transform.eulerAngles.y,
                    verticalSpeed = m_VerticalSpeed,
                    input = state.input,
                    updateKinematics = state.updateKinematics,
                    rootMotionDelta = state.rootMotionDelta,
                    movementTranslation = state.movementTranslation
                });
            }

            // The authoritative root and CharacterController remain at the reconciled pose.
            // Only the validated Mannequin hierarchy transitions from its pre-correction pose.
            BeginVisualReconciliation(capturedVisualPose, visiblePosition, visibleRotation);
        }

        private void RebasePredictionStatesAfter(int fromIndex, Vector3 positionDelta, float rotationDeltaY)
        {
            if (fromIndex + 1 >= m_PredictionHistoryCount) return;
            if (positionDelta.sqrMagnitude <= 0.0000001f && Mathf.Abs(rotationDeltaY) <= 0.001f) return;

            for (int i = fromIndex + 1; i < m_PredictionHistoryCount; i++)
            {
                PredictedState state = GetPredictionState(i);
                state.position += positionDelta;
                state.rotationY = Mathf.Repeat(state.rotationY + rotationDeltaY, 360f);
                SetPredictionState(i, state);
            }
        }

        private void TeleportTo(Vector3 position, float rotationY, float verticalSpeed)
        {
            if (m_Controller != null)
            {
                bool controllerWasEnabled = m_Controller.enabled;
                if (controllerWasEnabled) m_Controller.enabled = false;
                this.Transform.position = position;
                this.Transform.rotation = Quaternion.Euler(0f, rotationY, 0f);
                if (controllerWasEnabled) m_Controller.enabled = true;
            }
            else
            {
                this.Transform.position = position;
                this.Transform.rotation = Quaternion.Euler(0f, rotationY, 0f);
            }

            m_VerticalSpeed = verticalSpeed;
        }

        private void UpdateReconciliation(float deltaTime)
        {
            if (!EnsureVisualPresentation())
            {
                ClearVisualReconciliationOffset();
                return;
            }

            deltaTime = Mathf.Max(0f, deltaTime);
            m_VisualPresentation.UpdateRootStepTransition(deltaTime);
            m_ReconciliationProgress += deltaTime * Mathf.Max(1f, m_Config.reconciliationSpeed);

            if (!m_VisualPresentation.TryGetWorldPose(
                    out Vector3 visiblePosition,
                    out Quaternion visibleRotation))
            {
                ClearVisualReconciliationOffset();
                return;
            }

            m_ReconciliationVisualOffset = visiblePosition - this.Transform.position;
            m_ReconciliationVisualRotationOffsetY = Mathf.DeltaAngle(
                this.Transform.eulerAngles.y,
                visibleRotation.eulerAngles.y);

            if ((m_ReconciliationVisualOffset.sqrMagnitude < 0.000001f &&
                 Mathf.Abs(m_ReconciliationVisualRotationOffsetY) < 0.01f) ||
                m_ReconciliationProgress >= 8f)
            {
                ClearVisualReconciliationOffset();
            }
        }

        private void ClearVisualReconciliationOffset()
        {
            m_VisualPresentation?.ResetOffset();
            m_ReconciliationVisualOffset = Vector3.zero;
            m_ReconciliationVisualRotationOffsetY = 0f;
            m_IsReconciling = false;
            m_ReconciliationProgress = 0f;
        }

        private bool TryCaptureReconciliationPresentation(
            out Vector3 position,
            out Quaternion rotation)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            return EnsureVisualPresentation() &&
                   m_VisualPresentation.TryGetWorldPose(out position, out rotation);
        }

        private void BeginVisualReconciliation(
            bool capturedVisualPose,
            Vector3 visiblePosition,
            Quaternion visibleRotation)
        {
            m_ReconciliationProgress = 0f;
            if (!capturedVisualPose || m_VisualPresentation == null)
            {
                ClearVisualReconciliationOffset();
                return;
            }

            float duration = Mathf.Clamp(
                3f / Mathf.Max(1f, m_Config.reconciliationSpeed),
                0.05f,
                0.25f);
            m_VisualPresentation.BeginRootStepTransition(
                visiblePosition,
                visibleRotation,
                duration,
                m_Config.maxReconciliationDistance);

            if (!m_VisualPresentation.TryGetWorldPose(
                    out Vector3 currentVisiblePosition,
                    out Quaternion currentVisibleRotation))
            {
                ClearVisualReconciliationOffset();
                return;
            }

            m_ReconciliationVisualOffset = currentVisiblePosition - this.Transform.position;
            m_ReconciliationVisualRotationOffsetY = Mathf.DeltaAngle(
                this.Transform.eulerAngles.y,
                currentVisibleRotation.eulerAngles.y);
            m_IsReconciling =
                m_ReconciliationVisualOffset.sqrMagnitude >= 0.000001f ||
                Mathf.Abs(m_ReconciliationVisualRotationOffsetY) >= 0.01f;
        }

        private bool EnsureVisualPresentation()
        {
            if (this.Character == null || IsOwnerAuthorityPoseSyncActive ||
                m_RagdollEventSuspended || this.Character.IsDead ||
                (this.Character.Ragdoll != null && this.Character.Ragdoll.IsRagdoll))
            {
                return false;
            }

            if (m_VisualPresentation == null)
            {
                m_VisualPresentation = new NetworkCharacterVisualPresentation(
                    this.Character,
                    "ClientDriver");
            }

            return m_VisualPresentation.TryEnsure(logWarning: true);
        }

        private void ReleaseVisualPresentation()
        {
            m_VisualPresentation?.Dispose();
            m_VisualPresentation = null;
        }

        private void LogClientMotionDiagnostic(string message, bool force = false)
        {
            if (!m_LogMotionDiagnostics) return;

            float now = Time.realtimeSinceStartup;
            float interval = Mathf.Max(0.05f, m_MotionDiagnosticInterval);
            if (!force && now - m_LastMotionDiagnosticRealtime < interval) return;

            Debug.Log(
                $"[NetworkMotionDebug][ClientDriver] {this.Character?.name ?? "Character"}: {message}",
                this.Character);
            m_LastMotionDiagnosticRealtime = now;
        }

        private void TraceTraversalMotion(string message)
        {
            if (!m_LogMotionDiagnostics) return;

            Debug.Log(
                $"[TraversalTrace][ClientDriver] {this.Character?.name ?? "Character"} " +
                $"pos={FormatVector(this.Transform.position)} {message}",
                this.Character);
        }

        private void LogTraversalPose(string message)
        {
            if (!m_LogMotionDiagnostics) return;

            float now = Time.realtimeSinceStartup;
            float interval = Mathf.Max(0.05f, m_MotionDiagnosticInterval);
            if (now - m_LastMotionDiagnosticRealtime < interval) return;

            Debug.Log(
                $"[TraversalPoseDebug][ClientDriver] {this.Character?.name ?? "Character"} " +
                $"pos={FormatVector(this.Transform.position)} y={this.Transform.position.y:F3} " +
                $"rotY={this.Transform.eulerAngles.y:F2} forward={FormatVector(this.Transform.forward)} " +
                $"ownerPoseActive={IsOwnerAuthorityPoseSyncActive} " +
                $"ownerPoseRemaining={Mathf.Max(0f, m_OwnerAuthorityPoseSyncUntil - Time.time):F3} " +
                $"reconcileSuppressedRemaining={Mathf.Max(0f, m_ReconciliationSuppressedUntil - Time.time):F3} " +
                $"isReconciling={m_IsReconciling} visualOffset={FormatVector(m_ReconciliationVisualOffset)} " +
                $"{message}",
                this.Character);
            m_LastMotionDiagnosticRealtime = now;
        }

        private string FormatBusyState()
        {
            if (this.Character?.Busy == null) return "busy=null legsBusy=null";
            return $"busy={this.Character.Busy.IsBusy} legsBusy={this.Character.Busy.AreLegsBusy}";
        }

        private static string FormatVector(Vector3 value)
        {
            return $"({value.x:F3},{value.y:F3},{value.z:F3})";
        }

        private static string FormatVector2(Vector2 value)
        {
            return $"({value.x:F3},{value.y:F3})";
        }

        // HELPER METHODS: ------------------------------------------------------------------------

        private void UpdateGravity(float deltaTime)
        {
            float gravityInfluence = this.GravityInfluence;

            if (IsGrounded)
            {
                if (m_VerticalSpeed <= 0f)
                {
                    m_VerticalSpeed = gravityInfluence <= 0.001f ? 0f : -2f;
                }

                m_GroundTime = this.Character.Time.Time;
                m_GroundFrame = this.Character.Time.Frame;
                return;
            }

            if (!IsGrounded)
            {
                float gravity = m_VerticalSpeed >= 0
                    ? this.Character.Motion.GravityUpwards
                    : this.Character.Motion.GravityDownwards;

                gravity *= gravityInfluence;
                m_VerticalSpeed += gravity * deltaTime;
                m_VerticalSpeed = Mathf.Max(m_VerticalSpeed, this.Character.Motion.TerminalVelocity);
            }
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
                QueryTriggerInteraction.Ignore
            );
        }

        private bool CanJump()
        {
            if (!this.Character.Motion.CanJump) return false;

            float timeSinceGrounded = this.Character.Time.Time - m_GroundTime;
            int framesSinceGrounded = this.Character.Time.Frame - m_GroundFrame;

            bool inCoyoteTime = timeSinceGrounded < COYOTE_TIME || framesSinceGrounded < COYOTE_FRAMES;
            return IsGrounded || inCoyoteTime;
        }

        private int GetPredictionBufferIndex(int logicalIndex)
        {
            return (m_PredictionHistoryStart + logicalIndex) % PREDICTION_HISTORY_CAPACITY;
        }

        private PredictedState GetPredictionState(int logicalIndex)
        {
            return m_PredictionHistory[GetPredictionBufferIndex(logicalIndex)];
        }

        private void SetPredictionState(int logicalIndex, PredictedState state)
        {
            m_PredictionHistory[GetPredictionBufferIndex(logicalIndex)] = state;
        }

        private void AppendPredictionState(PredictedState state)
        {
            if (m_PredictionHistoryCount < PREDICTION_HISTORY_CAPACITY)
            {
                int writeIndex = GetPredictionBufferIndex(m_PredictionHistoryCount);
                m_PredictionHistory[writeIndex] = state;
                m_PredictionHistoryCount++;
                return;
            }

            // Ring buffer full: overwrite oldest entry.
            m_PredictionHistory[m_PredictionHistoryStart] = state;
            m_PredictionHistoryStart = (m_PredictionHistoryStart + 1) % PREDICTION_HISTORY_CAPACITY;
        }

        private void RemoveOldestPredictionStates(int count)
        {
            if (count <= 0 || m_PredictionHistoryCount == 0)
            {
                return;
            }

            if (count >= m_PredictionHistoryCount)
            {
                m_PredictionHistoryStart = 0;
                m_PredictionHistoryCount = 0;
                return;
            }

            m_PredictionHistoryStart =
                (m_PredictionHistoryStart + count) % PREDICTION_HISTORY_CAPACITY;
            m_PredictionHistoryCount -= count;
        }

        private static bool IsSequenceNewer(ushort a, ushort b)
        {
            return (short)(a - b) > 0;
        }

        public NetworkPositionState GetCurrentState()
        {
            ushort lastInput = m_CurrentSequence == 0
                ? (ushort)0
                : (ushort)(m_CurrentSequence - 1);

            Vector3 position = this.Transform.position;
            float rotationY = this.Transform.eulerAngles.y;
            bool isGrounded = IsGrounded;

            return TryCaptureSupportState(
                    position,
                    rotationY,
                    isGrounded,
                    out uint supportId,
                    out Vector3 supportLocalPosition,
                    out float supportLocalYaw)
                ? NetworkPositionState.Create(
                    position,
                    rotationY,
                    m_VerticalSpeed,
                    lastInput,
                    isGrounded,
                    m_VerticalSpeed > 0f,
                    m_MoveDirection,
                    supportId,
                    supportLocalPosition,
                    supportLocalYaw)
                : NetworkPositionState.Create(
                    position,
                    rotationY,
                    m_VerticalSpeed,
                    lastInput,
                    isGrounded,
                    m_VerticalSpeed > 0f,
                    m_MoveDirection
                );
        }

        private bool TryCaptureSupportState(
            Vector3 position,
            float rotationY,
            bool isGrounded,
            out uint supportId,
            out Vector3 supportLocalPosition,
            out float supportLocalYaw)
        {
            supportId = 0;
            supportLocalPosition = Vector3.zero;
            supportLocalYaw = 0f;

            if (!isGrounded) return false;
            if (!TryProbeGround(out RaycastHit hit)) return false;
            if (!NetworkMotionSupportAnchor.TryResolveFromHit(hit, out NetworkMotionSupportAnchor support)) return false;

            supportId = support.SupportId;
            if (supportId == 0) return false;

            Transform supportTransform = support.transform;
            supportLocalPosition = supportTransform.InverseTransformPoint(position);
            supportLocalYaw = Mathf.DeltaAngle(supportTransform.eulerAngles.y, rotationY);
            return true;
        }

        // STANDARD DRIVER METHODS: ---------------------------------------------------------------

        public override void OnUpdate()
        {
            ClearExpiredTeleportRotationAllowance();
            if (!CanProcessNetworkMotion()) return;
            if (ShouldSuspendGameplayRoot()) return;
            if (this.m_Controller == null) return;

            if (m_FloorNormal != null)
            {
                m_FloorNormal.UpdateWithDelta(this.Character.Time.DeltaTime);
            }

            float floorAngle = Vector3.Angle(FloorNormal, Vector3.up);
            m_IsOnSteepSlope = IsGrounded && floorAngle > m_MaxSlope;

            // Sync controller properties
            if (Math.Abs(m_Controller.skinWidth - m_SkinWidth) > float.Epsilon)
                m_Controller.skinWidth = m_SkinWidth;
            if (Math.Abs(m_Controller.slopeLimit - m_MaxSlope) > float.Epsilon)
                m_Controller.slopeLimit = m_MaxSlope;
            if (Math.Abs(m_Controller.stepOffset - m_StepHeight) > float.Epsilon)
                m_Controller.stepOffset = m_StepHeight;

            float height = this.Character.Motion.Height;
            float radius = this.Character.Motion.Radius;
            if (Math.Abs(m_Controller.height - height) > float.Epsilon)
            {
                m_Controller.height = height;
                m_Controller.center = Vector3.zero;
            }
            if (Math.Abs(m_Controller.radius - radius) > float.Epsilon)
                m_Controller.radius = radius;
        }

        public override void SetPosition(Vector3 position, bool teleport = false)
        {
            if (teleport)
            {
                // GC2 uses this path for real warps (including ragdoll recovery). Remove every
                // prediction based on the previous origin before changing the authoritative root.
                InvalidatePredictionForTeleport(clearAuthorityWindows: false);
                m_TeleportRotationPending = true;
                m_TeleportRotationPendingFrame = Time.frameCount;
            }
            else
            {
                m_TeleportRotationPending = false;
                m_TeleportRotationPendingFrame = -1;
            }

            Vector3 rootPosition = ToRootPosition(position);
            Vector3 before = this.Transform.position;

            if (m_Controller != null)
            {
                bool controllerWasEnabled = m_Controller.enabled;
                if (controllerWasEnabled) m_Controller.enabled = false;
                this.Transform.position = rootPosition;
                if (controllerWasEnabled) m_Controller.enabled = true;
            }
            else
            {
                this.Transform.position = rootPosition;
            }

            if (!teleport)
            {
                RecordExternalRootTranslationForPrediction(before, "SetPosition", rootPosition);
                RecordExternalMoveVelocity(before);
            }
        }

        private Vector3 ToRootPosition(Vector3 driverPosition)
        {
            float halfHeight = this.Character != null
                ? this.Character.Motion.Height * 0.5f
                : 0f;

            return driverPosition + Vector3.up * halfHeight;
        }

        public override void SetRotation(Quaternion rotation)
        {
            bool isTeleportRotation = m_TeleportRotationPending &&
                                      m_TeleportRotationPendingFrame == Time.frameCount;
            m_TeleportRotationPending = false;
            m_TeleportRotationPendingFrame = -1;

            if (!isTeleportRotation && this.Character != null &&
                (m_RagdollEventSuspended || this.Character.IsDead ||
                 (this.Character.Ragdoll != null && this.Character.Ragdoll.IsRagdoll)))
            {
                return;
            }

            this.Transform.rotation = rotation;
            Physics.SyncTransforms();
        }

        private void ClearExpiredTeleportRotationAllowance()
        {
            if (!m_TeleportRotationPending ||
                m_TeleportRotationPendingFrame == Time.frameCount)
            {
                return;
            }

            m_TeleportRotationPending = false;
            m_TeleportRotationPendingFrame = -1;
        }

        public override void SetScale(Vector3 scale)
        {
            this.Transform.localScale = scale;
        }

        public override void AddPosition(Vector3 amount)
        {
            if (this.Character == null || ShouldSuspendGameplayRoot()) return;

            if (m_Controller != null && m_Controller.enabled)
            {
                Vector3 before = this.Transform.position;
                m_Controller.Move(amount);
                RecordExternalRootTranslationForPrediction(
                    before,
                    "AddPosition",
                    before + amount);
                RecordExternalMoveVelocity(before);
            }
        }

        /// <summary>
        /// Captures displacement authored outside the regular directional-input step. GC2
        /// Traversal links and motion-warp clips advance the character through SetPosition and
        /// AddPosition. That displacement is already present in the prediction snapshot, so it
        /// must also be replayed when reconciliation rewinds to an older server acknowledgement.
        /// </summary>
        private void RecordExternalRootTranslationForPrediction(
            Vector3 before,
            string writer,
            Vector3 requestedTarget)
        {
            if (!m_AcceptsNetworkMotion || (!m_HasIssuedInput && m_InputAccumulator <= 0f)) return;

            Vector3 actualDelta = this.Transform.position - before;
            if (!NetworkCharacterVisualPresentation.IsFinite(actualDelta) ||
                actualDelta.sqrMagnitude <= 0.0000001f)
            {
                return;
            }

            Vector3 accumulated = m_PendingExternalRootTranslationForTick + actualDelta;
            m_PendingExternalRootTranslationForTick =
                NetworkCharacterVisualPresentation.IsFinite(accumulated)
                    ? accumulated
                    : Vector3.zero;

            LogFocusedTraversalMotion(
                "PullUpOwnerDelta",
                $"writer={writer} seqNext={m_CurrentSequence} " +
                $"requestedTarget={NetworkTraversalClimbDiagnostics.Vector(requestedTarget)} " +
                $"before={NetworkTraversalClimbDiagnostics.Vector(before)} " +
                $"after={NetworkTraversalClimbDiagnostics.Vector(this.Transform.position)} " +
                $"requestedDelta={NetworkTraversalClimbDiagnostics.Vector(requestedTarget - before)} " +
                $"actualDelta={NetworkTraversalClimbDiagnostics.Vector(actualDelta)} " +
                $"pendingExternal={NetworkTraversalClimbDiagnostics.Vector(m_PendingExternalRootTranslationForTick)} " +
                $"inputAccumulator={m_InputAccumulator:F3} ownerWindowRemaining={OwnerMotionAuthorityRemaining:F3} " +
                $"updateKinematics={this.UpdateKinematics} grounded={IsGrounded}",
                $"client-pullup-delta:{this.Character.GetInstanceID()}:{writer}");
        }

        private Vector3 ConsumePendingMovementTranslationForTick()
        {
            Vector3 captured =
                m_PendingMovementTranslationForTick +
                m_PendingExternalRootTranslationForTick;

            m_PendingMovementTranslationForTick = Vector3.zero;
            m_PendingExternalRootTranslationForTick = Vector3.zero;

            return NetworkCharacterVisualPresentation.IsFinite(captured)
                ? captured
                : Vector3.zero;
        }

        private void RecordExternalMoveVelocity(Vector3 before)
        {
            if (ShouldPreserveExplicitMoveDirectionForAnimation()) return;

            float deltaTime = this.Character != null
                ? this.Character.Time.DeltaTime
                : Time.deltaTime;

            if (deltaTime <= 0f) deltaTime = Time.deltaTime;
            if (deltaTime <= 0f) return;

            Vector3 actualDelta = this.Transform.position - before;
            if (actualDelta.sqrMagnitude <= 0.0000001f) return;

            this.m_MoveDirection = actualDelta / deltaTime;
            this.m_LastExternalMoveDirectionRealtime = Time.realtimeSinceStartup;
        }

        private bool ShouldPreserveExternalMoveDirectionForAnimation()
        {
            if (!this.UpdateKinematics) return true;
            if (ShouldPreserveExplicitMoveDirectionForAnimation()) return true;
            if (!IsTraversalLikeAuthorityMotion()) return false;
            return Time.realtimeSinceStartup - m_LastExternalMoveDirectionRealtime <=
                   EXTERNAL_MOVE_DIRECTION_SAMPLE_GRACE_SECONDS;
        }

        private bool ShouldPreserveExplicitMoveDirectionForAnimation()
        {
            if (!IsTraversalLikeAuthorityMotion())
            {
                m_PreserveExplicitMoveDirectionWhileTraversal = false;
                return false;
            }

            if (m_PreserveExplicitMoveDirectionWhileTraversal) return true;

            return Time.realtimeSinceStartup - m_LastExplicitMoveDirectionRealtime <=
                   EXTERNAL_MOVE_DIRECTION_SAMPLE_GRACE_SECONDS;
        }

        private bool IsTraversalLikeAuthorityMotion()
        {
            if (this.Character == null) return false;
            if (this.Character.RootMotionPosition > 0.05f) return true;
            return this.Character.Busy != null &&
                   (this.Character.Busy.IsBusy || this.Character.Busy.AreLegsBusy);
        }

        public override void AddRotation(Quaternion amount)
        {
            if (this.Character != null &&
                (m_RagdollEventSuspended || this.Character.IsDead ||
                 (this.Character.Ragdoll != null && this.Character.Ragdoll.IsRagdoll)))
            {
                return;
            }

            this.Transform.rotation *= amount;
            Physics.SyncTransforms();
        }

        public override void AddScale(Vector3 scale)
        {
            Vector3 targetScale = this.Transform.localScale + scale;
            if (!NetworkCharacterVisualPresentation.IsFinite(targetScale)) return;
            this.Transform.localScale = targetScale;
            Physics.SyncTransforms();
        }

        public override void ResetVerticalVelocity()
        {
            m_VerticalSpeed = 0f;
        }
    }
}
