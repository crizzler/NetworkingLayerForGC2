using System;
using System.Collections.Generic;
using UnityEngine;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.Characters;

namespace Arawn.GameCreator2.Networking
{
    /// <summary>
    /// Server-authoritative driver that processes inputs and produces authoritative position states.
    /// This should run on the server/host. It validates client inputs and simulates movement.
    /// </summary>
    [Title("Network Character Controller (Server)")]
    [Image(typeof(IconCapsuleSolid), ColorTheme.Type.Purple)]
    [Category("Network Character Controller (Server)")]
    [Description("Server-authoritative driver that validates and processes client inputs. " +
                 "Use this on server/host for competitive multiplayer with cheat prevention.")]
    [Serializable]
    public class UnitDriverNetworkServer : TUnitDriver, INetworkServerOwnerMotionAuthority
    {
        // EXPOSED MEMBERS: -----------------------------------------------------------------------

        [SerializeField] protected float m_SkinWidth = 0.08f;
        [SerializeField] protected float m_MaxSlope = 45f;
        [SerializeField] protected float m_StepHeight = 0.3f;
        [SerializeField] private Axonometry m_Axonometry = new Axonometry();

        [Header("Anti-Cheat")]
        [SerializeField] private NetworkCharacterConfig m_Config = new NetworkCharacterConfig();

        [Header("Debug")]
        [SerializeField] private bool m_LogMotionDiagnostics = false;

        // MEMBERS: -------------------------------------------------------------------------------

        [NonSerialized] protected CharacterController m_Controller;
        [NonSerialized] protected Vector3 m_MoveDirection;
        [NonSerialized] protected float m_VerticalSpeed;
        [NonSerialized] protected AnimVector3 m_FloorNormal;

        [NonSerialized] private Queue<NetworkInputState> m_InputBuffer;
        [NonSerialized] private HashSet<ushort> m_QueuedInputSequences;
        [NonSerialized] private ushort m_LastProcessedInput;
        [NonSerialized] private int m_SpeedViolations;
        [NonSerialized] private Vector3 m_LastValidatedPosition;
        [NonSerialized] private float m_ExpectedMaxSpeed;
        [NonSerialized] private int m_SuppressedDuplicateInputs;
        [NonSerialized] private float m_LastMotionDiagnosticRealtime;
        [NonSerialized] private float m_LastOwnerAuthorityPoseRealtime;
        [NonSerialized] private float m_ServerOwnerMotionWindowUntilRealtime;
        [NonSerialized] private uint m_ServerOwnerMotionOperationId;
        [NonSerialized] private bool m_ServerOwnerMotionWindowWasActive;
        [NonSerialized] private float m_LastSuppressedExternalRootWriteRealtime;
        [NonSerialized] private float m_LastAllowedExternalRootWriteRealtime;
        [NonSerialized] private float m_LastExternalMoveDirectionRealtime;
        [NonSerialized] private float m_LastExplicitMoveDirectionRealtime;
        [NonSerialized] private bool m_PreserveExplicitMoveDirectionWhileTraversal;
        [NonSerialized] private bool m_ControllerPhysicsRefreshPending;
        [NonSerialized] private int m_ControllerPhysicsRefreshNotBeforeFrame;
        [NonSerialized] private int m_ControllerPhysicsRefreshRetryCount;
        [NonSerialized] private Collider[] m_ControllerPhysicsQueryBuffer;
        [NonSerialized] private int m_ControllerPhysicsRefreshAttempts;
        [NonSerialized] private int m_ControllerPhysicsRefreshSuccesses;
        [NonSerialized] private bool m_LastControllerPhysicsRefreshQueryable;
        [NonSerialized] private int m_LastControllerPhysicsRefreshFrame;
        [NonSerialized] private float m_LastControllerPhysicsWarningRealtime;
        [NonSerialized] private int m_OwnerAuthorityNativeMoveCount;
        [NonSerialized] private int m_LastOwnerAuthorityNativeMoveFrame;
        [NonSerialized] private Vector3 m_LastOwnerAuthorityRequestedDelta;
        [NonSerialized] private Vector3 m_LastOwnerAuthorityAppliedDelta;

        /// <summary>
        /// Maximum number of buffered inputs. Protects against memory growth from
        /// packet floods or malicious clients. At 60 inputs/sec this is ~4 seconds.
        /// </summary>
        private const int MAX_BUFFERED_INPUTS = 256;
        private const float OWNER_AUTHORITY_POSITION_EPSILON = 0.005f;
        private const float OWNER_AUTHORITY_EXTRA_DISTANCE = 0.5f;
        private const float OWNER_AUTHORITY_ROOT_MOTION_THRESHOLD = 0.05f;
        private const float OWNER_AUTHORITY_ROOT_WRITE_SUPPRESSION_SECONDS = 0.5f;
        private const float EXTERNAL_MOVE_DIRECTION_SAMPLE_GRACE_SECONDS = 0.15f;
        private const float EXPLICIT_MOVE_DIRECTION_SAMPLE_GRACE_SECONDS = 0.25f;
        private const float CONTROLLER_PROXY_REFRESH_DISTANCE = 0.005f;
        private const int CONTROLLER_PROXY_MAX_REFRESH_RETRIES = 3;
        private const float CONTROLLER_PROXY_WARNING_INTERVAL = 5f;

        /// <summary>
        /// Diagnostic revision for the native CharacterController query-proxy repair. This is
        /// intentionally an instance property so runtime strike diagnostics prove that the
        /// server driver containing the repair is actually loaded rather than only showing
        /// newly compiled Melee diagnostics.
        /// </summary>
        public int ControllerPhysicsRepairRevision => 4;
        public int ControllerPhysicsRefreshAttempts => m_ControllerPhysicsRefreshAttempts;
        public int ControllerPhysicsRefreshSuccesses => m_ControllerPhysicsRefreshSuccesses;
        public bool LastControllerPhysicsRefreshQueryable => m_LastControllerPhysicsRefreshQueryable;
        public int LastControllerPhysicsRefreshFrame => m_LastControllerPhysicsRefreshFrame;
        public int OwnerAuthorityNativeMoveCount => m_OwnerAuthorityNativeMoveCount;
        public int LastOwnerAuthorityNativeMoveFrame => m_LastOwnerAuthorityNativeMoveFrame;
        public Vector3 LastOwnerAuthorityRequestedDelta => m_LastOwnerAuthorityRequestedDelta;
        public Vector3 LastOwnerAuthorityAppliedDelta => m_LastOwnerAuthorityAppliedDelta;

        /// <summary>
        /// Reports the controller's current membership in the PhysicsScene used by the object.
        /// This is intentionally a live query rather than the result of the last repair.
        /// </summary>
        public bool ControllerPhysicsQueryableNow => IsControllerQueryableInOwnPhysicsScene();

        [NonSerialized] protected int m_GroundFrame = -100;
        [NonSerialized] protected float m_GroundTime = -100f;
        [NonSerialized] private bool m_IsOnSteepSlope;

        // EVENTS: --------------------------------------------------------------------------------

        /// <summary>
        /// Fired when a new authoritative state is produced (send to clients).
        /// </summary>
        public event Action<NetworkPositionState> OnStateProduced;

        /// <summary>
        /// Fired when a speed violation is detected.
        /// </summary>
        public event Action<int> OnSpeedViolation;

        /// <summary>
        /// Fired after the server accepts an owner-authority pose sample. Optional modules
        /// can use this to keep their own local pose state aligned with the accepted root.
        /// </summary>
        public static event Action<Character, Vector3> OwnerAuthorityPositionAccepted;

        /// <summary>
        /// Optional module hook. Return a non-empty reason to reject an owner-authority
        /// pose sample before it is applied to the server transform.
        /// </summary>
        public static event Func<Character, Vector3, string> OwnerAuthorityPositionRejectionRequested;

        /// <summary>
        /// Optional module hook. Return a non-empty reason to allow an external root
        /// SetPosition even while recent owner-authority poses would normally suppress it.
        /// </summary>
        public static event Func<Character, Vector3, string> ExternalRootPositionWriteAllowanceRequested;

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

        public ushort LastProcessedInput => m_LastProcessedInput;
        public NetworkCharacterConfig Config => m_Config;

        public void ApplySessionProfile(NetworkSessionProfile profile)
        {
            if (profile == null) return;

            m_Config.maxSpeedMultiplier = profile.maxSpeedMultiplier;
            m_Config.violationThreshold = profile.violationThreshold;
        }

        // INITIALIZERS: --------------------------------------------------------------------------

        public UnitDriverNetworkServer()
        {
            this.m_MoveDirection = Vector3.zero;
            this.m_VerticalSpeed = 0f;
            this.m_InputBuffer = new Queue<NetworkInputState>(32);
            this.m_QueuedInputSequences = new HashSet<ushort>();
            this.m_LastProcessedInput = ushort.MaxValue;
        }

        public override void OnStartup(Character character)
        {
            base.OnStartup(character);

            this.m_FloorNormal = new AnimVector3(Vector3.up, 0.15f);
            this.m_InputBuffer = new Queue<NetworkInputState>(32);
            this.m_QueuedInputSequences = new HashSet<ushort>();
            this.m_LastProcessedInput = ushort.MaxValue;
            this.m_SpeedViolations = 0;
            this.m_SuppressedDuplicateInputs = 0;
            this.m_LastMotionDiagnosticRealtime = -100f;
            this.m_LastOwnerAuthorityPoseRealtime = -100f;
            this.m_ServerOwnerMotionWindowUntilRealtime = -100f;
            this.m_ServerOwnerMotionOperationId = 0;
            this.m_ServerOwnerMotionWindowWasActive = false;
            this.m_LastSuppressedExternalRootWriteRealtime = -100f;
            this.m_LastAllowedExternalRootWriteRealtime = -100f;
            this.m_LastExternalMoveDirectionRealtime = -100f;
            this.m_LastExplicitMoveDirectionRealtime = -100f;
            this.m_PreserveExplicitMoveDirectionWhileTraversal = false;
            this.m_ControllerPhysicsRefreshPending = false;
            this.m_ControllerPhysicsRefreshNotBeforeFrame = -1;
            this.m_ControllerPhysicsRefreshRetryCount = 0;
            this.m_ControllerPhysicsQueryBuffer = new Collider[16];
            this.m_ControllerPhysicsRefreshAttempts = 0;
            this.m_ControllerPhysicsRefreshSuccesses = 0;
            this.m_LastControllerPhysicsRefreshQueryable = false;
            this.m_LastControllerPhysicsRefreshFrame = -1;
            this.m_LastControllerPhysicsWarningRealtime = -100f;
            this.m_OwnerAuthorityNativeMoveCount = 0;
            this.m_LastOwnerAuthorityNativeMoveFrame = -1;
            this.m_LastOwnerAuthorityRequestedDelta = Vector3.zero;
            this.m_LastOwnerAuthorityAppliedDelta = Vector3.zero;
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

            // A controller can have been created by the prefab's original client driver before
            // NetworkCharacter replaces it with this server driver. Zero this unconditionally so
            // the verified recovery nudge cannot be filtered by a stale serialized threshold.
            this.m_Controller.minMoveDistance = 0f;

            this.m_LastValidatedPosition = this.Transform.position;
            this.m_ExpectedMaxSpeed = this.Character.Motion.LinearSpeed;

        }

        public override void OnEnable()
        {
            base.OnEnable();
            if (this.Character != null)
            {
                this.m_GroundTime = this.Character.Time.Time;
                this.m_GroundFrame = this.Character.Time.Frame;
            }
        }

        public override void OnDispose(Character character)
        {
            this.m_ServerOwnerMotionWindowUntilRealtime = -100f;
            this.m_ServerOwnerMotionOperationId = 0;
            this.m_ServerOwnerMotionWindowWasActive = false;
            this.m_ControllerPhysicsRefreshPending = false;
            this.m_ControllerPhysicsRefreshNotBeforeFrame = -1;
            this.m_ControllerPhysicsRefreshRetryCount = 0;
            this.m_ControllerPhysicsQueryBuffer = null;
            base.OnDispose(character);
            this.m_Controller = null;
        }

        /// <inheritdoc />
        public void OpenServerOwnerMotionWindow(float durationSeconds, uint operationId = 0)
        {
            if (durationSeconds <= 0f) return;

            float until = Time.realtimeSinceStartup + durationSeconds;
            if (until > m_ServerOwnerMotionWindowUntilRealtime)
            {
                m_ServerOwnerMotionWindowUntilRealtime = until;
            }
            m_ServerOwnerMotionWindowWasActive = true;

            if (operationId != 0) m_ServerOwnerMotionOperationId = operationId;
            LogFocusedTraversalMotion(
                "OwnerWindow",
                $"side=server operation=open id={m_ServerOwnerMotionOperationId} " +
                $"duration={durationSeconds:F3} until={m_ServerOwnerMotionWindowUntilRealtime:F3}",
                $"server-window-open:{this.Character?.GetInstanceID() ?? 0}");
            LogTraversalPose(
                $"server-owner-motion-window-open operation={m_ServerOwnerMotionOperationId} " +
                $"duration={durationSeconds:F3} until={m_ServerOwnerMotionWindowUntilRealtime:F3}");
        }

        /// <inheritdoc />
        public void CloseServerOwnerMotionWindow(float graceSeconds = 0f)
        {
            float now = Time.realtimeSinceStartup;
            float closeAt = now + Mathf.Max(0f, graceSeconds);
            m_ServerOwnerMotionWindowUntilRealtime = Mathf.Min(
                m_ServerOwnerMotionWindowUntilRealtime,
                closeAt);

            if (graceSeconds <= 0f) m_ServerOwnerMotionOperationId = 0;
            ScheduleDeferredControllerPhysicsRefresh();
            LogFocusedTraversalMotion(
                "OwnerWindow",
                $"side=server operation=close id={m_ServerOwnerMotionOperationId} " +
                $"grace={Mathf.Max(0f, graceSeconds):F3} until={m_ServerOwnerMotionWindowUntilRealtime:F3}");
            LogTraversalPose(
                $"server-owner-motion-window-close operation={m_ServerOwnerMotionOperationId} " +
                $"grace={Mathf.Max(0f, graceSeconds):F3} until={m_ServerOwnerMotionWindowUntilRealtime:F3}");
        }

        // INPUT PROCESSING: ----------------------------------------------------------------------

        /// <summary>
        /// Queue an input from a client for processing.
        /// Call this when receiving client input over the network.
        /// </summary>
        public void QueueInput(NetworkInputState input)
        {
            // Reject out-of-order inputs (simple protection)
            if (IsSequenceNewer(input.sequenceNumber, m_LastProcessedInput))
            {
                if (m_QueuedInputSequences.Contains(input.sequenceNumber))
                {
                    m_SuppressedDuplicateInputs++;
                    LogServerMotionDiagnostic(
                        $"suppressed duplicate queued input seq={input.sequenceNumber} " +
                        $"lastProcessed={m_LastProcessedInput} queued={m_InputBuffer.Count} " +
                        $"suppressedSinceLast={m_SuppressedDuplicateInputs}");
                    return;
                }

                // Cap buffer size to prevent memory growth from floods
                if (m_InputBuffer.Count >= MAX_BUFFERED_INPUTS)
                {
                    NetworkInputState dropped = m_InputBuffer.Dequeue(); // Drop oldest input
                    m_QueuedInputSequences.Remove(dropped.sequenceNumber);
                    LogServerMotionDiagnostic(
                        $"input buffer full; dropped oldest seq={dropped.sequenceNumber} " +
                        $"incoming={input.sequenceNumber}",
                        force: true);
                }

                m_InputBuffer.Enqueue(input);
                m_QueuedInputSequences.Add(input.sequenceNumber);
            }
        }

        /// <summary>
        /// Process all queued inputs and produce authoritative state.
        /// Call this at your server tick rate.
        /// </summary>
        public NetworkPositionState ProcessInputs(Transform cameraTransform = null)
        {
            int queuedAtStart = m_InputBuffer.Count;
            if (queuedAtStart > 4)
            {
                LogServerMotionDiagnostic(
                    $"processing input backlog queued={queuedAtStart} " +
                    $"lastProcessed={m_LastProcessedInput} position={FormatVector(this.Transform.position)}");
            }

            while (m_InputBuffer.Count > 0)
            {
                var input = m_InputBuffer.Dequeue();
                m_QueuedInputSequences.Remove(input.sequenceNumber);
                ProcessSingleInput(input, cameraTransform);
                m_LastProcessedInput = input.sequenceNumber;
            }

            var state = CreateCurrentState();
            LogFocusedTraversalMotion(
                "ServerState",
                $"lastProcessed={state.lastProcessedInput} pos={NetworkTraversalClimbDiagnostics.Vector(state.GetPosition())} " +
                $"grounded={IsGrounded} queuedBefore={queuedAtStart} rootMotion={this.Character?.RootMotionPosition ?? 0f:F3} " +
                $"{FormatBusyState()}",
                $"server-state:{this.Character?.GetInstanceID() ?? 0}");
            OnStateProduced?.Invoke(state);
            return state;
        }

        private void LogServerMotionDiagnostic(string message, bool force = false)
        {
            if (!m_LogMotionDiagnostics) return;

            float now = Time.realtimeSinceStartup;
            if (!force && now - m_LastMotionDiagnosticRealtime < 0.5f) return;

            Debug.Log(
                $"[NetworkMotionDebug][ServerDriver] {this.Character?.name ?? "Character"}: {message}",
                this.Character);
            m_LastMotionDiagnosticRealtime = now;
            m_SuppressedDuplicateInputs = 0;
        }

        private static string FormatVector(Vector3 value)
        {
            return $"({value.x:F3},{value.y:F3},{value.z:F3})";
        }

        private static string FormatVector2(Vector2 value)
        {
            return $"({value.x:F3},{value.y:F3})";
        }

        private void LogTraversalPose(string message)
        {
            if (!m_LogMotionDiagnostics) return;

            float now = Time.realtimeSinceStartup;
            if (now - m_LastMotionDiagnosticRealtime < 0.5f) return;

            Debug.Log(
                $"[TraversalPoseDebug][ServerDriver] {this.Character?.name ?? "Character"} " +
                $"pos={FormatVector(this.Transform.position)} y={this.Transform.position.y:F3} " +
                $"rotY={this.Transform.eulerAngles.y:F2} forward={FormatVector(this.Transform.forward)} " +
                $"{message}",
                this.Character);
            m_LastMotionDiagnosticRealtime = now;
        }

        private bool IsTraversalLikeAuthorityMotion()
        {
            if (this.Character == null) return false;
            if (this.Character.RootMotionPosition > OWNER_AUTHORITY_ROOT_MOTION_THRESHOLD) return true;
            return this.Character.Busy != null &&
                   (this.Character.Busy.IsBusy || this.Character.Busy.AreLegsBusy);
        }

        private string FormatBusyState()
        {
            if (this.Character?.Busy == null) return "busy=null legsBusy=null";
            return $"busy={this.Character.Busy.IsBusy} legsBusy={this.Character.Busy.AreLegsBusy}";
        }

        private void ProcessSingleInput(NetworkInputState input, Transform cameraTransform)
        {
            Vector2 rawInput = input.GetInputDirection();
            float deltaTime = input.GetDeltaTime();
            Vector3 positionBeforeInput = this.Transform.position;
            float rotationYBeforeInput = this.Transform.eulerAngles.y;
            float inputRotationY = input.GetRotationY();

            if (input.HasOwnerAuthorityPosition)
            {
                Vector3 ownerPosition = input.GetOwnerAuthorityPosition();
                LogFocusedTraversalMotion(
                    "OwnerPoseReceive",
                    $"seq={input.sequenceNumber} requested={NetworkTraversalClimbDiagnostics.Vector(ownerPosition)} " +
                    $"before={NetworkTraversalClimbDiagnostics.Vector(positionBeforeInput)} " +
                    $"delta={NetworkTraversalClimbDiagnostics.Vector(ownerPosition - positionBeforeInput)} " +
                    $"windowRemaining={Mathf.Max(0f, m_ServerOwnerMotionWindowUntilRealtime - Time.realtimeSinceStartup):F3} " +
                    $"operation={m_ServerOwnerMotionOperationId} grounded={IsGrounded} " +
                    $"rootMotion={this.Character?.RootMotionPosition ?? 0f:F3} {FormatBusyState()}",
                    $"server-owner-receive:{this.Character?.GetInstanceID() ?? 0}");
                LogTraversalPose(
                    $"process-owner-authority-input-begin seq={input.sequenceNumber} dt={deltaTime:F3} " +
                    $"rawInput={FormatVector2(rawInput)} inputRotY={inputRotationY:F2} " +
                    $"before={FormatVector(positionBeforeInput)} beforeY={positionBeforeInput.y:F3} " +
                    $"beforeRotY={rotationYBeforeInput:F2} ownerPos={FormatVector(ownerPosition)} " +
                    $"ownerPosY={ownerPosition.y:F3} ownerDelta={FormatVector(ownerPosition - positionBeforeInput)} " +
                    $"rootMotion={this.Character?.RootMotionPosition ?? 0f:F3} {FormatBusyState()}");
            }

            bool preserveExternalFacing = input.HasOwnerAuthorityPosition && IsTraversalLikeAuthorityMotion();
            if (preserveExternalFacing)
            {
                LogTraversalPose(
                    $"process-owner-authority-input-preserve-facing seq={input.sequenceNumber} " +
                    $"inputRotY={inputRotationY:F2} currentRotY={this.Transform.eulerAngles.y:F2} " +
                    $"rootMotion={this.Character?.RootMotionPosition ?? 0f:F3} {FormatBusyState()}");
            }
            else
            {
                this.Transform.rotation = Quaternion.Euler(0f, input.GetRotationY(), 0f);
            }

            // Convert input to world direction
            Vector3 inputDirection = new Vector3(rawInput.x, 0f, rawInput.y);

            if (cameraTransform != null)
            {
                Quaternion cameraRotation = Quaternion.Euler(0f, cameraTransform.eulerAngles.y, 0f);
                inputDirection = cameraRotation * inputDirection;
            }

            if (inputDirection.sqrMagnitude > 1f) inputDirection.Normalize();

            // Calculate expected movement
            float speed = this.Character.Motion.LinearSpeed;
            Vector3 horizontalMovement = inputDirection * speed * deltaTime;

            // Validate movement (anti-cheat)
            float maxAllowedDistance = speed * m_Config.maxSpeedMultiplier * deltaTime;
            if (horizontalMovement.magnitude > maxAllowedDistance)
            {
                m_SpeedViolations++;
                OnSpeedViolation?.Invoke(m_SpeedViolations);

                // Clamp to max allowed
                horizontalMovement = horizontalMovement.normalized * maxAllowedDistance;
            }

            if (!this.UpdateKinematics)
            {
                horizontalMovement = Vector3.zero;
            }

            // Apply gravity
            UpdateGravity(deltaTime);

            // Handle jump
            if (input.HasFlag(NetworkInputState.FLAG_JUMP) && CanJump())
            {
                m_VerticalSpeed = this.Character.Motion.JumpForce;
            }

            // Combine movement
            Vector3 translation = ApplyRootMotionBlend(horizontalMovement);
            translation = this.m_Axonometry?.ProcessTranslation(this, translation) ?? translation;

            Vector3 totalMovement = translation + Vector3.up * m_VerticalSpeed * deltaTime;

            // Move character controller
            if (m_Controller != null && m_Controller.enabled)
            {
                m_Controller.Move(totalMovement);
            }

            bool ownerAuthorityApplied = TryApplyOwnerAuthorityPosition(input, deltaTime, out Vector3 ownerAuthorityDelta);
            if (ownerAuthorityApplied)
            {
                translation = ownerAuthorityDelta;
            }

            // Update grounded state
            if (IsGrounded && m_VerticalSpeed < 0)
            {
                m_VerticalSpeed = -2f; // Small downward force to stay grounded
                m_GroundTime = this.Character.Time.Time;
                m_GroundFrame = this.Character.Time.Frame;
            }

            // Store move direction for animation. GC2 Traversal drives the root through
            // Driver.SetPosition/AddPosition outside the normal input simulation path; keep
            // that externally recorded vector long enough for Animim to sample the climb axes.
            if (!ShouldPreserveExternalMoveDirectionForAnimation())
            {
                m_MoveDirection = translation / deltaTime;
            }

            // Update floor normal
            if (m_FloorNormal != null)
            {
                m_FloorNormal.UpdateWithDelta(deltaTime);
            }

            if (input.HasOwnerAuthorityPosition)
            {
                LogFocusedTraversalMotion(
                    "OwnerPoseResult",
                    $"seq={input.sequenceNumber} accepted={ownerAuthorityApplied} " +
                    $"requested={NetworkTraversalClimbDiagnostics.Vector(input.GetOwnerAuthorityPosition())} " +
                    $"after={NetworkTraversalClimbDiagnostics.Vector(this.Transform.position)} " +
                    $"controllerDelta={NetworkTraversalClimbDiagnostics.Vector(this.Transform.position - positionBeforeInput)} " +
                    $"nativeRequested={NetworkTraversalClimbDiagnostics.Vector(m_LastOwnerAuthorityRequestedDelta)} " +
                    $"nativeApplied={NetworkTraversalClimbDiagnostics.Vector(m_LastOwnerAuthorityAppliedDelta)} " +
                    $"nativeFrame={m_LastOwnerAuthorityNativeMoveFrame} grounded={IsGrounded} " +
                    $"rootMotion={this.Character?.RootMotionPosition ?? 0f:F3} {FormatBusyState()}",
                    $"server-owner-result:{this.Character?.GetInstanceID() ?? 0}");
                LogTraversalPose(
                    $"process-owner-authority-input-end seq={input.sequenceNumber} appliedOwnerPose={ownerAuthorityApplied} " +
                    $"after={FormatVector(this.Transform.position)} afterY={this.Transform.position.y:F3} " +
                    $"afterRotY={this.Transform.eulerAngles.y:F2} inputRotY={inputRotationY:F2} " +
                    $"movedDelta={FormatVector(this.Transform.position - positionBeforeInput)} " +
                    $"ownerDeltaApplied={FormatVector(ownerAuthorityDelta)} verticalSpeed={m_VerticalSpeed:F3} " +
                    $"grounded={IsGrounded} rootMotion={this.Character?.RootMotionPosition ?? 0f:F3} {FormatBusyState()}");
            }
        }

        private Vector3 ApplyRootMotionBlend(Vector3 kineticMovement)
        {
            Vector3 rootMotion = this.Character.Animim.RootMotionDeltaPosition;
            return Vector3.Lerp(kineticMovement, rootMotion, this.Character.RootMotionPosition);
        }

        private bool TryApplyOwnerAuthorityPosition(
            NetworkInputState input,
            float deltaTime,
            out Vector3 appliedDelta)
        {
            appliedDelta = Vector3.zero;
            if (!input.HasOwnerAuthorityPosition) return false;

            Vector3 targetPosition = input.GetOwnerAuthorityPosition();
            if (TryGetOwnerAuthorityPositionRejection(targetPosition, out string externalRejectionReason))
            {
                LogFocusedOwnerPoseDecision(input, targetPosition, "rejected", externalRejectionReason, Vector3.zero);
                LogTraversalPose(
                    $"owner-pose-rejected-external-hook seq={input.sequenceNumber} reason={externalRejectionReason} " +
                    $"owner={FormatVector(targetPosition)} ownerY={targetPosition.y:F3} " +
                    $"current={FormatVector(this.Transform.position)} currentY={this.Transform.position.y:F3} " +
                    $"inputRotY={input.GetRotationY():F2} rootMotion={this.Character?.RootMotionPosition ?? 0f:F3} " +
                    $"{FormatBusyState()}");
                return false;
            }

            if (!ShouldAcceptOwnerAuthorityPosition(out string gateReason))
            {
                LogFocusedOwnerPoseDecision(input, targetPosition, "rejected", gateReason, Vector3.zero);
                string busy = this.Character?.Busy != null
                    ? this.Character.Busy.IsBusy.ToString()
                    : "null";
                string legsBusy = this.Character?.Busy != null
                    ? this.Character.Busy.AreLegsBusy.ToString()
                    : "null";

                TraceTraversalMotion(
                    $"owner pose rejected by server gate seq={input.sequenceNumber} reason={gateReason} " +
                    $"owner={FormatVector(input.GetOwnerAuthorityPosition())} current={FormatVector(this.Transform.position)} " +
                    $"rootMotion={this.Character?.RootMotionPosition ?? 0f:F3} " +
                    $"busy={busy} legsBusy={legsBusy}");
                LogTraversalPose(
                    $"owner-pose-rejected-gate seq={input.sequenceNumber} reason={gateReason} " +
                    $"owner={FormatVector(input.GetOwnerAuthorityPosition())} ownerY={input.GetOwnerAuthorityPosition().y:F3} " +
                    $"current={FormatVector(this.Transform.position)} currentY={this.Transform.position.y:F3} " +
                    $"inputRotY={input.GetRotationY():F2} {FormatBusyState()}");
                return false;
            }

            Vector3 currentPosition = this.Transform.position;
            Vector3 delta = targetPosition - currentPosition;
            float distance = delta.magnitude;

            if (distance <= OWNER_AUTHORITY_POSITION_EPSILON)
            {
                MarkOwnerAuthorityPoseReceived();
                NotifyOwnerAuthorityPositionAccepted(targetPosition);
                LogFocusedOwnerPoseDecision(input, targetPosition, "accepted-no-move", "epsilon", Vector3.zero);
                return false;
            }

            float speed = this.Character.Motion.LinearSpeed;
            float maxKineticDistance = speed * m_Config.maxSpeedMultiplier * deltaTime + OWNER_AUTHORITY_EXTRA_DISTANCE;
            float maxAuthorityDistance = Mathf.Max(m_Config.maxReconciliationDistance, maxKineticDistance);

            if (distance > maxAuthorityDistance)
            {
                LogFocusedOwnerPoseDecision(
                    input,
                    targetPosition,
                    "rejected",
                    $"distance:{distance:F3}>{maxAuthorityDistance:F3}",
                    Vector3.zero);
                m_SpeedViolations++;
                OnSpeedViolation?.Invoke(m_SpeedViolations);
                TraceTraversalMotion(
                    $"owner pose rejected by server distance seq={input.sequenceNumber} distance={distance:F3} " +
                    $"max={maxAuthorityDistance:F3} current={FormatVector(currentPosition)} " +
                    $"owner={FormatVector(targetPosition)} speedViolations={m_SpeedViolations}");
                LogTraversalPose(
                    $"owner-pose-rejected-distance seq={input.sequenceNumber} distance={distance:F3} " +
                    $"max={maxAuthorityDistance:F3} current={FormatVector(currentPosition)} currentY={currentPosition.y:F3} " +
                    $"owner={FormatVector(targetPosition)} ownerY={targetPosition.y:F3} " +
                    $"inputRotY={input.GetRotationY():F2} speedViolations={m_SpeedViolations}");
                LogServerMotionDiagnostic(
                    $"rejected owner authority position seq={input.sequenceNumber} distance={distance:F3} " +
                    $"max={maxAuthorityDistance:F3} current={FormatVector(currentPosition)} owner={FormatVector(targetPosition)}",
                    true);
                return false;
            }

            Vector3 acceptedPosition = ApplyOwnerAuthorityRootPosition(targetPosition);

            appliedDelta = acceptedPosition - currentPosition;
            MarkOwnerAuthorityPoseReceived();
            NotifyOwnerAuthorityPositionAccepted(acceptedPosition);
            LogFocusedOwnerPoseDecision(input, targetPosition, "accepted", "validated", appliedDelta);
            TraceTraversalMotion(
                $"owner pose accepted by server seq={input.sequenceNumber} distance={distance:F3} " +
                $"from={FormatVector(currentPosition)} requested={FormatVector(targetPosition)} " +
                $"accepted={FormatVector(acceptedPosition)} " +
                $"rootMotion={this.Character.RootMotionPosition:F3} busy={this.Character.Busy.IsBusy} " +
                $"legsBusy={this.Character.Busy.AreLegsBusy}");
            LogTraversalPose(
                $"owner-pose-accepted seq={input.sequenceNumber} distance={distance:F3} " +
                $"from={FormatVector(currentPosition)} fromY={currentPosition.y:F3} " +
                $"requested={FormatVector(targetPosition)} requestedY={targetPosition.y:F3} " +
                $"accepted={FormatVector(acceptedPosition)} acceptedY={acceptedPosition.y:F3} " +
                $"requestedDelta={FormatVector(delta)} appliedDelta={FormatVector(appliedDelta)} " +
                $"inputRotY={input.GetRotationY():F2} " +
                $"rootMotion={this.Character?.RootMotionPosition ?? 0f:F3} {FormatBusyState()}");
            LogServerMotionDiagnostic(
                $"accepted owner authority position seq={input.sequenceNumber} distance={distance:F3} " +
                $"rootMotion={this.Character.RootMotionPosition:F3} busy={this.Character.Busy.IsBusy} " +
                $"requested={FormatVector(targetPosition)} accepted={FormatVector(acceptedPosition)}",
                distance > 0.05f);
            return true;
        }

        private void LogFocusedOwnerPoseDecision(
            NetworkInputState input,
            Vector3 requested,
            string result,
            string reason,
            Vector3 appliedDelta)
        {
            LogFocusedTraversalMotion(
                "OwnerPoseValidation",
                $"seq={input.sequenceNumber} result={result} reason='{reason}' " +
                $"operation={m_ServerOwnerMotionOperationId} " +
                $"requested={NetworkTraversalClimbDiagnostics.Vector(requested)} " +
                $"applied={NetworkTraversalClimbDiagnostics.Vector(this.Transform.position)} " +
                $"delta={NetworkTraversalClimbDiagnostics.Vector(appliedDelta)} " +
                $"grounded={IsGrounded} rootMotion={this.Character?.RootMotionPosition ?? 0f:F3} " +
                $"{FormatBusyState()}",
                result.StartsWith("accepted", StringComparison.Ordinal)
                    ? $"server-owner-validation:{this.Character?.GetInstanceID() ?? 0}"
                    : null);
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

        /// <summary>
        /// Owner-authority reaction/traversal samples must finish with a genuine native
        /// CharacterController move. A Transform write can leave the managed pose correct while
        /// the native broadphase capsule remains at an older airborne position. Move also makes
        /// the server authoritative over collision constraints; callers receive the actual pose.
        /// </summary>
        private Vector3 ApplyOwnerAuthorityRootPosition(Vector3 rootPosition)
        {
            Vector3 before = this.Transform.position;
            Vector3 requestedDelta = rootPosition - before;
            m_LastOwnerAuthorityRequestedDelta = requestedDelta;

            if (m_Controller != null &&
                m_Controller.enabled &&
                m_Controller.gameObject.activeInHierarchy)
            {
                Physics.SyncTransforms();
                m_Controller.Move(requestedDelta);
                m_OwnerAuthorityNativeMoveCount++;
                m_LastOwnerAuthorityNativeMoveFrame = Time.frameCount;
                m_LastOwnerAuthorityAppliedDelta = this.Transform.position - before;

                // A real non-zero native Move supersedes an older pending Transform/shape
                // refresh. A later shape mutation or explicit window close can schedule a new
                // verified repair independently.
                m_ControllerPhysicsRefreshPending = false;
                m_ControllerPhysicsRefreshNotBeforeFrame = -1;
                m_ControllerPhysicsRefreshRetryCount = 0;
                return this.Transform.position;
            }

            ApplyAbsoluteRootPosition(rootPosition);
            m_LastOwnerAuthorityAppliedDelta = this.Transform.position - before;
            return this.Transform.position;
        }

        private void NotifyOwnerAuthorityPositionAccepted(Vector3 position)
        {
            try
            {
                OwnerAuthorityPositionAccepted?.Invoke(this.Character, position);
            }
            catch (Exception exception)
            {
                Debug.LogError(
                    $"[TraversalPoseDebug][ServerDriver] {this.Character?.name ?? "Character"} " +
                    $"owner-authority-pose-accepted hook failed position={FormatVector(position)}: " +
                    $"{exception.Message}\n{exception.StackTrace}",
                    this.Character);
            }
        }

        private bool TryGetOwnerAuthorityPositionRejection(Vector3 targetPosition, out string reason)
        {
            reason = string.Empty;
            Delegate[] handlers = OwnerAuthorityPositionRejectionRequested?.GetInvocationList();
            if (handlers == null) return false;

            foreach (Delegate handler in handlers)
            {
                if (handler is not Func<Character, Vector3, string> rejectionProvider) continue;

                try
                {
                    string candidateReason = rejectionProvider.Invoke(this.Character, targetPosition);
                    if (string.IsNullOrEmpty(candidateReason)) continue;

                    reason = candidateReason;
                    return true;
                }
                catch (Exception exception)
                {
                    Debug.LogError(
                        $"[TraversalPoseDebug][ServerDriver] {this.Character?.name ?? "Character"} " +
                        $"owner-authority rejection hook failed target={FormatVector(targetPosition)}: " +
                        $"{exception.Message}\n{exception.StackTrace}",
                        this.Character);
                }
            }

            return false;
        }

        private void MarkOwnerAuthorityPoseReceived()
        {
            m_LastOwnerAuthorityPoseRealtime = Time.realtimeSinceStartup;
        }

        private bool ShouldAcceptOwnerAuthorityPosition(out string reason)
        {
            reason = string.Empty;

            if (this.Character == null)
            {
                reason = "missing-character";
                return false;
            }

            // A client controls the packet's owner-pose flag, so root motion or Busy state alone
            // cannot authorize transform writes. A server gameplay system must first correlate
            // the sample with a validated traversal/combat operation.
            if (Time.realtimeSinceStartup > m_ServerOwnerMotionWindowUntilRealtime)
            {
                reason = "no-server-owner-motion-window";
                return false;
            }

            if (this.Character.RootMotionPosition > OWNER_AUTHORITY_ROOT_MOTION_THRESHOLD)
            {
                reason = "root-motion";
                return true;
            }

            if (this.Character.Busy == null)
            {
                reason = "missing-busy";
                return false;
            }

            // GC2 Traversal marks the legs busy while MotionLink drives the root with
            // Driver.AddPosition. Owner-authority pose sync is only present when an
            // approved networking controller enables it, so accepting legs-busy motion
            // lets connected clients traverse without server reconciliation fighting the
            // link animation.
            if (this.Character.Busy.IsBusy)
            {
                reason = "character-busy";
                return true;
            }

            if (this.Character.Busy.AreLegsBusy)
            {
                reason = "legs-busy";
                return true;
            }

            reason = "not-root-motion-or-busy";
            return false;
        }

        private void TraceTraversalMotion(string message)
        {
            if (!m_LogMotionDiagnostics) return;

            Debug.Log(
                $"[TraversalTrace][ServerDriver] {this.Character?.name ?? "Character"} " +
                $"pos={FormatVector(this.Transform.position)} {message}",
                this.Character);
        }

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

            // Coyote time check
            float timeSinceGrounded = this.Character.Time.Time - m_GroundTime;
            int framesSinceGrounded = this.Character.Time.Frame - m_GroundFrame;

            bool inCoyoteTime = timeSinceGrounded < COYOTE_TIME || framesSinceGrounded < COYOTE_FRAMES;
            return IsGrounded || inCoyoteTime;
        }

        private NetworkPositionState CreateCurrentState()
        {
            Vector3 position = this.Transform.position;
            float rotationY = this.Transform.eulerAngles.y;
            bool isGrounded = IsGrounded;

            NetworkPositionState state = TryCaptureSupportState(
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
                    m_LastProcessedInput,
                    isGrounded,
                    m_VerticalSpeed > 0,
                    m_MoveDirection,
                    supportId,
                    supportLocalPosition,
                    supportLocalYaw)
                : NetworkPositionState.Create(
                    position,
                    rotationY,
                    m_VerticalSpeed,
                    m_LastProcessedInput,
                    isGrounded,
                    m_VerticalSpeed > 0,
                    m_MoveDirection
                );

            if (IsTraversalLikeAuthorityMotion())
            {
                LogTraversalPose(
                    $"produce-authoritative-state seq={state.lastProcessedInput} " +
                    $"statePos={FormatVector(state.GetPosition())} stateY={state.GetPosition().y:F3} " +
                    $"stateRotY={state.GetRotationY():F2} verticalSpeed={state.GetVerticalVelocity():F3} " +
                    $"moveVelocity={FormatVector(state.GetMoveVelocity())} " +
                    $"flags=0x{state.flags:X2} grounded={state.IsGrounded} jumping={state.IsJumping} " +
                    $"rootMotion={this.Character?.RootMotionPosition ?? 0f:F3} {FormatBusyState()}");
            }

            return state;
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

        /// <summary>
        /// Get the current authoritative state without processing any inputs.
        /// </summary>
        public NetworkPositionState GetCurrentState()
        {
            return CreateCurrentState();
        }

        private static bool IsSequenceNewer(ushort a, ushort b)
        {
            // Handle wraparound
            return (short)(a - b) > 0;
        }

        // STANDARD DRIVER METHODS: ---------------------------------------------------------------

        public override void OnUpdate()
        {
            if (this.Character.IsDead) return;
            if (this.m_Controller == null) return;

            bool ownerMotionWindowActive =
                Time.realtimeSinceStartup <= m_ServerOwnerMotionWindowUntilRealtime;
            if (m_ServerOwnerMotionWindowWasActive && !ownerMotionWindowActive)
            {
                // CloseServerOwnerMotionWindow can run before an airborne reaction has fully
                // landed. Verify once more when its grace period actually expires.
                ScheduleDeferredControllerPhysicsRefresh();
            }
            m_ServerOwnerMotionWindowWasActive = ownerMotionWindowActive;

            // Update properties
            if (m_FloorNormal != null)
            {
                m_FloorNormal.UpdateWithDelta(this.Character.Time.DeltaTime);
            }

            float floorAngle = Vector3.Angle(FloorNormal, Vector3.up);
            m_IsOnSteepSlope = IsGrounded && floorAngle > m_MaxSlope;

            // Updating CharacterController shape properties can recreate its native PhysX
            // controller. If the remote owner is idle afterwards there may be no ordinary
            // Move call to register the recreated query proxy, leaving an enabled managed
            // component that sword overlap queries cannot find. Track every native property
            // mutation and explicitly refresh the proxy below.
            bool controllerPhysicsChanged = false;

            // Update controller properties
            if (Math.Abs(m_Controller.skinWidth - m_SkinWidth) > float.Epsilon)
            {
                m_Controller.skinWidth = m_SkinWidth;
                controllerPhysicsChanged = true;
            }
            if (Math.Abs(m_Controller.slopeLimit - m_MaxSlope) > float.Epsilon)
            {
                m_Controller.slopeLimit = m_MaxSlope;
                controllerPhysicsChanged = true;
            }
            if (Math.Abs(m_Controller.stepOffset - m_StepHeight) > float.Epsilon)
            {
                m_Controller.stepOffset = m_StepHeight;
                controllerPhysicsChanged = true;
            }
            if (Math.Abs(m_Controller.minMoveDistance) > float.Epsilon)
            {
                m_Controller.minMoveDistance = 0f;
                controllerPhysicsChanged = true;
            }

            // Sync height/radius from motion
            float height = this.Character.Motion.Height;
            float radius = this.Character.Motion.Radius;
            if (Math.Abs(m_Controller.height - height) > float.Epsilon)
            {
                m_Controller.height = height;
                controllerPhysicsChanged = true;
            }
            if (Math.Abs(m_Controller.radius - radius) > float.Epsilon)
            {
                m_Controller.radius = radius;
                controllerPhysicsChanged = true;
            }
            if (m_Controller.center != Vector3.zero)
            {
                m_Controller.center = Vector3.zero;
                controllerPhysicsChanged = true;
            }

            if (controllerPhysicsChanged)
            {
                RefreshControllerPhysicsProxy(out _);
                ScheduleDeferredControllerPhysicsRefresh();
            }
            else if (m_ControllerPhysicsRefreshPending &&
                     Time.frameCount >= m_ControllerPhysicsRefreshNotBeforeFrame &&
                     RefreshControllerPhysicsProxy(out bool queryable))
            {
                if (queryable ||
                    ++m_ControllerPhysicsRefreshRetryCount >= CONTROLLER_PROXY_MAX_REFRESH_RETRIES)
                {
                    m_ControllerPhysicsRefreshPending = false;
                    m_ControllerPhysicsRefreshNotBeforeFrame = -1;
                    if (!queryable)
                    {
                        WarnControllerPhysicsInvariant(
                            $"native CharacterController remained absent from its PhysicsScene " +
                            $"after {m_ControllerPhysicsRefreshRetryCount} verified refresh attempts");
                    }
                }
                else
                {
                    m_ControllerPhysicsRefreshNotBeforeFrame = Time.frameCount + 1;
                }
            }
        }

        public override void SetPosition(Vector3 position, bool teleport = false)
        {
            Vector3 rootPosition = ToRootPosition(position);
            if (!teleport && ShouldSuppressExternalRootPositionWrite(rootPosition))
            {
                return;
            }

            Vector3 before = this.Transform.position;

            ApplyAbsoluteRootPosition(rootPosition);

            if (!teleport)
            {
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

        /// <summary>
        /// Applies an absolute root position without disabling the CharacterController, then
        /// flushes the transform to PhysX. Repeatedly disabling and re-enabling a controller
        /// during owner-authority reaction poses can leave its managed component enabled while
        /// its overlap-query proxy is absent. This follows GC2's native controller SetPosition
        /// behavior and schedules one native refresh on the following frame.
        /// </summary>
        private void ApplyAbsoluteRootPosition(Vector3 rootPosition)
        {
            this.Transform.position = rootPosition;
            Physics.SyncTransforms();
            ScheduleDeferredControllerPhysicsRefresh();
        }

        private void ScheduleDeferredControllerPhysicsRefresh()
        {
            m_ControllerPhysicsRefreshPending = true;
            m_ControllerPhysicsRefreshNotBeforeFrame = Time.frameCount + 1;
            m_ControllerPhysicsRefreshRetryCount = 0;
        }

        /// <summary>
        /// Flushes late Transform writers and restores the native CharacterController query
        /// proxy when necessary. Combat modules call this before or during their local physics
        /// collection; it never creates a hit or bypasses GC2's striker filtering.
        /// </summary>
        public bool EnsureControllerPhysicsQueryable()
        {
            Physics.SyncTransforms();
            if (IsControllerQueryableInOwnPhysicsScene()) return true;

            if (!RefreshControllerPhysicsProxy(out bool queryable)) return false;

            if (queryable)
            {
                m_ControllerPhysicsRefreshPending = false;
                m_ControllerPhysicsRefreshNotBeforeFrame = -1;
                m_ControllerPhysicsRefreshRetryCount = 0;
            }
            else
            {
                ScheduleDeferredControllerPhysicsRefresh();
            }

            return queryable;
        }

        /// <summary>
        /// Flush managed transforms before touching the native CharacterController. With Auto
        /// Sync Transforms disabled, calling Move first can operate from the previous native
        /// pose. A verified, reversible non-zero nudge then ensures a recreated/idle controller
        /// has an active overlap-query proxy without materially changing its authored root
        /// position. A call being issued is not considered success.
        /// </summary>
        private bool RefreshControllerPhysicsProxy(out bool queryable)
        {
            queryable = false;
            Physics.SyncTransforms();
            if (m_Controller == null ||
                !m_Controller.enabled ||
                !m_Controller.gameObject.activeInHierarchy)
            {
                return false;
            }

            m_ControllerPhysicsRefreshAttempts++;
            m_LastControllerPhysicsRefreshFrame = Time.frameCount;
            m_Controller.minMoveDistance = 0f;

            bool queryableBefore = IsControllerQueryableInOwnPhysicsScene();
            if (!queryableBefore)
            {
                NudgeControllerPhysicsProxy();
                Physics.SyncTransforms();
            }

            queryable = queryableBefore || IsControllerQueryableInOwnPhysicsScene();
            if (!queryable)
            {
                // Bounded last-resort recovery. Continuous owner poses never toggle the
                // controller; this only runs after a query proves the native capsule is absent.
                bool detectCollisions = m_Controller.detectCollisions;
                m_Controller.enabled = false;
                m_Controller.enabled = true;
                m_Controller.detectCollisions = detectCollisions;
                m_Controller.minMoveDistance = 0f;
                Physics.SyncTransforms();
                NudgeControllerPhysicsProxy();
                Physics.SyncTransforms();
                queryable = IsControllerQueryableInOwnPhysicsScene();
            }

            m_LastControllerPhysicsRefreshQueryable = queryable;
            if (queryable)
            {
                m_ControllerPhysicsRefreshSuccesses++;
            }
            else
            {
                WarnControllerPhysicsInvariant(
                    $"verified proxy refresh failed attempt={m_ControllerPhysicsRefreshAttempts} " +
                    $"position={FormatVector(this.Transform.position)} " +
                    $"height={m_Controller.height:F3} radius={m_Controller.radius:F3}");
            }

            return true;
        }

        private void NudgeControllerPhysicsProxy()
        {
            Vector3 start = this.Transform.position;
            Vector3 direction = this.Transform.right;
            if (direction.sqrMagnitude <= float.Epsilon) direction = Vector3.right;
            direction.Normalize();

            m_Controller.Move(direction * CONTROLLER_PROXY_REFRESH_DISTANCE);
            Vector3 restoreDelta = start - this.Transform.position;
            if (restoreDelta.sqrMagnitude > float.Epsilon)
            {
                m_Controller.Move(restoreDelta);
            }
        }

        private bool IsControllerQueryableInOwnPhysicsScene()
        {
            if (m_Controller == null ||
                !m_Controller.enabled ||
                !m_Controller.gameObject.activeInHierarchy)
            {
                return false;
            }

            if (m_ControllerPhysicsQueryBuffer == null ||
                m_ControllerPhysicsQueryBuffer.Length == 0)
            {
                m_ControllerPhysicsQueryBuffer = new Collider[16];
            }

            Bounds bounds = m_Controller.bounds;
            int layerMask = 1 << m_Controller.gameObject.layer;
            PhysicsScene physicsScene = m_Controller.gameObject.scene.GetPhysicsScene();
            if (!physicsScene.IsValid()) return false;

            int count = physicsScene.OverlapSphere(
                bounds.center,
                Mathf.Max(0.01f, m_Controller.radius * 0.05f),
                m_ControllerPhysicsQueryBuffer,
                layerMask,
                QueryTriggerInteraction.Ignore);

            for (int i = 0; i < count; i++)
            {
                if (m_ControllerPhysicsQueryBuffer[i] == m_Controller) return true;
            }

            return false;
        }

        private void WarnControllerPhysicsInvariant(string message)
        {
            float now = Time.realtimeSinceStartup;
            if (now - m_LastControllerPhysicsWarningRealtime < CONTROLLER_PROXY_WARNING_INTERVAL) return;

            m_LastControllerPhysicsWarningRealtime = now;
            Debug.LogWarning(
                $"[NetworkMotionInvariant][ServerDriver] " +
                $"{this.Character?.name ?? "Character"}: {message}",
                this.Character);
        }

        private bool ShouldSuppressExternalRootPositionWrite(Vector3 position)
        {
            if (!IsRecentOwnerAuthorityPoseActive) return false;
            if (!IsTraversalLikeAuthorityMotion()) return false;

            float now = Time.realtimeSinceStartup;
            if (TryGetExternalRootPositionWriteAllowance(position, out string allowReason))
            {
                if (now - m_LastAllowedExternalRootWriteRealtime >= 0.25f)
                {
                    LogTraversalPose(
                        $"allowed-external-set-position reason={allowReason} target={FormatVector(position)} " +
                        $"targetY={position.y:F3} current={FormatVector(this.Transform.position)} " +
                        $"currentY={this.Transform.position.y:F3} " +
                        $"ownerAuthorityAge={(now - m_LastOwnerAuthorityPoseRealtime):F3} " +
                        $"rootMotion={this.Character?.RootMotionPosition ?? 0f:F3} {FormatBusyState()}");
                    m_LastAllowedExternalRootWriteRealtime = now;
                }

                return false;
            }

            if (now - m_LastSuppressedExternalRootWriteRealtime >= 0.25f)
            {
                LogTraversalPose(
                    $"suppressed-external-set-position target={FormatVector(position)} " +
                    $"targetY={position.y:F3} current={FormatVector(this.Transform.position)} " +
                    $"currentY={this.Transform.position.y:F3} " +
                    $"ownerAuthorityAge={(now - m_LastOwnerAuthorityPoseRealtime):F3} " +
                    $"rootMotion={this.Character?.RootMotionPosition ?? 0f:F3} {FormatBusyState()}");
                m_LastSuppressedExternalRootWriteRealtime = now;
            }

            return true;
        }

        private bool TryGetExternalRootPositionWriteAllowance(Vector3 position, out string reason)
        {
            reason = string.Empty;
            Delegate[] handlers = ExternalRootPositionWriteAllowanceRequested?.GetInvocationList();
            if (handlers == null) return false;

            foreach (Delegate handler in handlers)
            {
                if (handler is not Func<Character, Vector3, string> allowanceProvider) continue;

                try
                {
                    string candidateReason = allowanceProvider.Invoke(this.Character, position);
                    if (string.IsNullOrEmpty(candidateReason)) continue;

                    reason = candidateReason;
                    return true;
                }
                catch (Exception exception)
                {
                    Debug.LogError(
                        $"[TraversalPoseDebug][ServerDriver] {this.Character?.name ?? "Character"} " +
                        $"external root write allowance hook failed target={FormatVector(position)}: " +
                        $"{exception.Message}\n{exception.StackTrace}",
                        this.Character);
                }
            }

            return false;
        }

        private bool IsRecentOwnerAuthorityPoseActive =>
            Time.realtimeSinceStartup - m_LastOwnerAuthorityPoseRealtime <= OWNER_AUTHORITY_ROOT_WRITE_SUPPRESSION_SECONDS;

        public override void SetRotation(Quaternion rotation)
        {
            this.Transform.rotation = rotation;
            Physics.SyncTransforms();
        }

        public override void SetScale(Vector3 scale)
        {
            this.Transform.localScale = scale;
            Physics.SyncTransforms();
        }

        public override void AddPosition(Vector3 amount)
        {
            // During an explicitly authorized owner-motion operation the absolute owner pose is
            // the source of truth. GC2 can simultaneously run the same MotionWarp/root-motion
            // state on the server replica; applying its AddPosition as well would double the
            // authored delta between owner samples. Use the same guarded suppression and
            // traversal allowance hooks as SetPosition.
            if (ShouldSuppressExternalRootPositionWrite(this.Transform.position + amount))
            {
                return;
            }

            if (m_Controller != null && m_Controller.enabled)
            {
                Vector3 before = this.Transform.position;
                m_Controller.Move(amount);
                RecordExternalMoveVelocity(before);
            }
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
                   EXPLICIT_MOVE_DIRECTION_SAMPLE_GRACE_SECONDS;
        }

        public override void AddRotation(Quaternion amount)
        {
            this.Transform.rotation *= amount;
            Physics.SyncTransforms();
        }

        public override void AddScale(Vector3 scale)
        {
            this.Transform.localScale = Vector3.Scale(this.Transform.localScale, scale);
            Physics.SyncTransforms();
        }

        public override void ResetVerticalVelocity()
        {
            m_VerticalSpeed = 0f;
        }
    }
}
