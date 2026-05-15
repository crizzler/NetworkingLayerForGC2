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
    public class UnitDriverNetworkClient : TUnitDriver
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
        [NonSerialized] private Vector3 m_ReconciliationTarget;
        [NonSerialized] private bool m_IsReconciling;
        [NonSerialized] private float m_ReconciliationProgress;
        [NonSerialized] private Vector3 m_ReconciliationVisualOffset;
        [NonSerialized] private Vector3 m_LastAppliedVisualOffset;
        [NonSerialized] private float m_ReconciliationVisualRotationOffsetY;
        [NonSerialized] private float m_LastAppliedVisualRotationOffsetY;
        [NonSerialized] private float m_ReconciliationSuppressedUntil;
        [NonSerialized] private float m_OwnerAuthorityPoseSyncUntil;
        [NonSerialized] private float m_LastMotionDiagnosticRealtime;
        
        // Input buffering
        [NonSerialized] private List<NetworkInputState> m_UnacknowledgedInputs;
        [NonSerialized] private float m_InputAccumulator;

        // Per-frame vs per-tick decoupling state.
        // m_LastInputDirection / m_LastCameraTransform: sampled live each frame, captured at
        // tick boundaries as the representative input for that interval (sent to the server).
        // m_PendingJumpForTick: set the moment a jump impulse is applied locally so the next
        // outgoing tick informs the server. Cleared once consumed by the tick snapshot.
        [NonSerialized] private Vector2 m_LastInputDirection;
        [NonSerialized] private Transform m_LastCameraTransform;
        [NonSerialized] private bool m_PendingJumpForTick;

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

        public void SetExternalMoveDirection(Vector3 velocity)
        {
            this.m_MoveDirection = velocity;
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
        }

        private const int PREDICTION_HISTORY_CAPACITY = 128;
        private const float EXTERNAL_AUTHORITY_POSITION_THRESHOLD = 0.005f;
        private const float EXTERNAL_AUTHORITY_ROTATION_THRESHOLD = 0.25f;

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
            this.m_InputAccumulator = 0f;
            this.m_LastInputDirection = Vector2.zero;
            this.m_LastCameraTransform = null;
            this.m_PendingJumpForTick = false;
            this.m_ReconciliationVisualOffset = Vector3.zero;
            this.m_LastAppliedVisualOffset = Vector3.zero;
            this.m_ReconciliationVisualRotationOffsetY = 0f;
            this.m_LastAppliedVisualRotationOffsetY = 0f;
            this.m_ReconciliationSuppressedUntil = 0f;
            this.m_OwnerAuthorityPoseSyncUntil = 0f;
            this.m_LastMotionDiagnosticRealtime = -100f;
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
            ClearVisualReconciliationOffset();
            base.OnDispose(character);
            this.m_Controller = null;
        }

        public override void OnDisable()
        {
            ClearVisualReconciliationOffset();
            base.OnDisable();
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
        ///    <see cref="NetworkInputState"/> is built from the most recently sampled input and
        ///    sent to the server, and the current actual transform is captured as a prediction
        ///    snapshot so that <see cref="ApplyServerState"/> can reconcile against it.
        ///
        /// Reconciliation replay still uses <see cref="ApplyInputPrediction"/> with the stored
        /// per-tick input chunks, matching how the server processes them; per-frame motion
        /// resumes once replay completes.
        /// </remarks>
        public void ProcessLocalInput(Vector2 inputDirection, Transform cameraTransform, bool jump = false)
        {
            float deltaTime = this.Character.Time.DeltaTime;

            // PER-FRAME: smooth visual movement using live frame dt.
            // We apply the jump impulse the moment it's requested for instant feel, then latch
            // a flag so the next outgoing tick still informs the server about the jump.
            bool applyJumpThisFrame = jump && CanJump();
            if (applyJumpThisFrame) m_PendingJumpForTick = true;

            ApplyFrameMovement(inputDirection, cameraTransform, deltaTime, applyJumpThisFrame);

            // Cache the latest input so the next tick boundary can snapshot a representative
            // sample. The outbound tick payload is converted to world-space below so the
            // authoritative server and reconciliation replay do not need the client's camera.
            m_LastInputDirection = inputDirection;
            m_LastCameraTransform = cameraTransform;

            // PER-TICK: build sequenced inputs at the configured send rate. The payload
            // delta uses the real elapsed time since the last sent input, matching the
            // per-frame movement already applied to the transform. Sending a fixed
            // interval here causes alternating over/under-correction whenever the render
            // frame rate does not divide the input send rate cleanly.
            m_InputAccumulator += deltaTime;
            float inputInterval = 1f / m_Config.inputSendRate;

            if (m_InputAccumulator >= inputInterval)
            {
                float inputDeltaTime = m_InputAccumulator;
                m_InputAccumulator = 0f;

                byte flags = 0;
                if (m_PendingJumpForTick) flags |= NetworkInputState.FLAG_JUMP;
                m_PendingJumpForTick = false;

                Vector2 networkInput = ToWorldSpaceInput(m_LastInputDirection, m_LastCameraTransform);
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
                    input = input
                });

                m_CurrentSequence++;

                SendInputsToServer();
            }

            // Handle reconciliation smoothing
            if (m_IsReconciling)
            {
                UpdateReconciliation(deltaTime);
            }
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
            Vector3 horizontalMovement = inputDirection * speed * deltaTime;

            UpdateGravity(deltaTime);

            if (applyJump)
            {
                m_VerticalSpeed = this.Character.Motion.JumpForce;
            }

            Vector3 translation = ApplyRootMotionBlend(horizontalMovement);
            translation = this.m_Axonometry?.ProcessTranslation(this, translation) ?? translation;

            Vector3 totalMovement = translation + Vector3.up * m_VerticalSpeed * deltaTime;
            m_Controller.Move(totalMovement);

            if (IsGrounded && m_VerticalSpeed < 0)
            {
                m_VerticalSpeed = -2f;
                m_GroundTime = this.Character.Time.Time;
                m_GroundFrame = this.Character.Time.Frame;
            }

            m_MoveDirection = translation / deltaTime;
        }

        private void ApplyInputPrediction(NetworkInputState input, Transform cameraTransform)
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
            Vector3 horizontalMovement = inputDirection * speed * deltaTime;

            // Apply gravity
            UpdateGravity(deltaTime);

            // Handle jump
            if (input.HasFlag(NetworkInputState.FLAG_JUMP))
            {
                m_VerticalSpeed = this.Character.Motion.JumpForce;
            }

            Vector3 translation = ApplyRootMotionBlend(horizontalMovement);
            translation = this.m_Axonometry?.ProcessTranslation(this, translation) ?? translation;

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

            m_MoveDirection = translation / deltaTime;
        }

        private Vector3 ApplyRootMotionBlend(Vector3 kineticMovement)
        {
            Vector3 rootMotion = this.Character.Animim.RootMotionDeltaPosition;
            return Vector3.Lerp(kineticMovement, rootMotion, this.Character.RootMotionPosition);
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
            m_LastAcknowledgedSequence = serverState.lastProcessedInput;
            
            // Remove acknowledged inputs
            m_UnacknowledgedInputs.RemoveAll(i => !IsSequenceNewer(i.sequenceNumber, serverState.lastProcessedInput));
            
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
                    LogClientMotionDiagnostic(
                        $"owner authority pose active; skipped server correction seq={serverState.lastProcessedInput} " +
                        $"error={positionError:F3} server={FormatVector(serverPosition)} predicted={FormatVector(predictedPosition)} " +
                        $"current={FormatVector(this.Transform.position)} history={m_PredictionHistoryCount} " +
                        $"unacked={m_UnacknowledgedInputs.Count} currentSeq={m_CurrentSequence} " +
                        $"rootMotion={this.Character.RootMotionPosition:F3}");
                }
                else if (externalAuthorityApplied)
                {
                    OnReconciliation?.Invoke(positionError);
                }
                else if (positionError > m_Config.reconciliationThreshold)
                {
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
                        ClearVisualReconciliationOffset();
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
                LogClientMotionDiagnostic(
                    $"owner authority pose active; skipped server correction without prediction seq={serverState.lastProcessedInput} " +
                    $"server={FormatVector(serverState.GetPosition())} current={FormatVector(this.Transform.position)} " +
                    $"history={m_PredictionHistoryCount} unacked={m_UnacknowledgedInputs.Count} currentSeq={m_CurrentSequence} " +
                    $"rootMotion={this.Character.RootMotionPosition:F3}");
            }
            else if (externalAuthorityActive)
            {
                TryApplyExternalAuthorityState(serverState, -1);
            }
            else if (m_PredictionHistoryCount > 0)
            {
                LogClientMotionDiagnostic(
                    $"server ack has no prediction seq={serverState.lastProcessedInput} " +
                    $"history={m_PredictionHistoryCount} first={GetPredictionState(0).sequence} " +
                    $"latest={GetPredictionState(m_PredictionHistoryCount - 1).sequence} " +
                    $"unacked={m_UnacknowledgedInputs.Count} currentSeq={m_CurrentSequence}");
            }
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

        private void StartExternalAuthorityCorrection(
            Vector3 serverPosition,
            float serverRotationY,
            float serverVerticalSpeed,
            int fromIndex)
        {
            Vector3 previousRootPosition = this.Transform.position;
            float previousRootRotationY = this.Transform.eulerAngles.y;
            Vector3 preReconcilePosition =
                previousRootPosition + this.Transform.TransformVector(m_LastAppliedVisualOffset);
            float preReconcileRotationY = previousRootRotationY + m_LastAppliedVisualRotationOffsetY;

            TeleportTo(serverPosition, serverRotationY, serverVerticalSpeed);

            Vector3 rootDelta = this.Transform.position - previousRootPosition;
            float rotationDeltaY = Mathf.DeltaAngle(previousRootRotationY, this.Transform.eulerAngles.y);
            RebasePredictionStatesAfter(fromIndex, rootDelta, rotationDeltaY);

            m_ReconciliationVisualOffset = preReconcilePosition - this.Transform.position;
            m_ReconciliationVisualRotationOffsetY = Mathf.DeltaAngle(
                this.Transform.eulerAngles.y,
                preReconcileRotationY
            );
            m_ReconciliationProgress = 0f;
            m_IsReconciling = true;

            ApplyVisualReconciliationOffset();
        }

        private void StartReconciliation(Vector3 serverPosition, float serverRotationY, float serverVerticalSpeed, int fromIndex)
        {
            // Capture the current visible model pose for visual smoothing. If a previous
            // correction is still fading, start the next one from that visible pose.
            Vector3 preReconcilePosition =
                this.Transform.position + this.Transform.TransformVector(m_LastAppliedVisualOffset);
            float preReconcileRotationY = this.Transform.eulerAngles.y + m_LastAppliedVisualRotationOffsetY;
            
            // Teleport to server position (physics correction)
            TeleportTo(serverPosition, serverRotationY, serverVerticalSpeed);
            
            // Re-apply all inputs after this point (standard CSP replay)
            for (int i = fromIndex + 1; i < m_PredictionHistoryCount; i++)
            {
                var state = GetPredictionState(i);
                ApplyInputPrediction(state.input, null);
                
                // Update the stored prediction
                SetPredictionState(i, new PredictedState
                {
                    sequence = state.sequence,
                    position = this.Transform.position,
                    rotationY = this.Transform.eulerAngles.y,
                    verticalSpeed = m_VerticalSpeed,
                    input = state.input
                });
            }
            
            // Calculate visual offset: the difference between where the player WAS visually
            // and where they ARE after correction+replay. The authoritative root is already
            // corrected; only the GC2 model offset is smoothed back to zero.
            m_ReconciliationVisualOffset = preReconcilePosition - this.Transform.position;
            m_ReconciliationVisualRotationOffsetY = Mathf.DeltaAngle(
                this.Transform.eulerAngles.y,
                preReconcileRotationY
            );
            m_ReconciliationProgress = 0f;
            m_IsReconciling = true;

            ApplyVisualReconciliationOffset();
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
                m_Controller.enabled = false;
                this.Transform.position = position;
                this.Transform.rotation = Quaternion.Euler(0f, rotationY, 0f);
                m_Controller.enabled = true;
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
            // Exponentially decay the visual offset toward zero.
            // This provides smooth visual reconciliation while physics stays authoritative.
            // Uses exponential decay: offset *= e^(-speed * dt), which is frame-rate independent.
            float decayFactor = Mathf.Exp(-m_Config.reconciliationSpeed * deltaTime);
            m_ReconciliationVisualOffset *= decayFactor;
            m_ReconciliationVisualRotationOffsetY *= decayFactor;
            ApplyVisualReconciliationOffset();
            
            m_ReconciliationProgress += deltaTime * m_Config.reconciliationSpeed;
            
            // Snap to zero once the offset is negligible (< 1mm)
            if ((m_ReconciliationVisualOffset.sqrMagnitude < 0.000001f &&
                 Mathf.Abs(m_ReconciliationVisualRotationOffsetY) < 0.01f) ||
                m_ReconciliationProgress >= 1f)
            {
                m_ReconciliationVisualOffset = Vector3.zero;
                m_ReconciliationVisualRotationOffsetY = 0f;
                m_IsReconciling = false;
                ApplyVisualReconciliationOffset();
            }
        }

        private void ApplyVisualReconciliationOffset()
        {
            IUnitAnimim animim = this.Character?.Animim;
            if (animim?.Mannequin == null)
            {
                m_LastAppliedVisualOffset = Vector3.zero;
                m_LastAppliedVisualRotationOffsetY = 0f;
                return;
            }

            Vector3 localOffset = this.Transform.InverseTransformVector(m_ReconciliationVisualOffset);
            Vector3 positionDelta = localOffset - m_LastAppliedVisualOffset;
            if (positionDelta.sqrMagnitude > 0f)
            {
                animim.Position += positionDelta;
                m_LastAppliedVisualOffset = localOffset;
                animim.ApplyMannequinPosition();
            }

            float rotationDelta = m_ReconciliationVisualRotationOffsetY - m_LastAppliedVisualRotationOffsetY;
            if (!Mathf.Approximately(rotationDelta, 0f))
            {
                Vector3 euler = animim.Rotation.eulerAngles;
                euler.y += rotationDelta;
                animim.Rotation = Quaternion.Euler(euler);
                m_LastAppliedVisualRotationOffsetY = m_ReconciliationVisualRotationOffsetY;
                animim.ApplyMannequinRotation();
            }
        }

        private void ClearVisualReconciliationOffset()
        {
            if (m_LastAppliedVisualOffset == Vector3.zero &&
                Mathf.Approximately(m_LastAppliedVisualRotationOffsetY, 0f))
            {
                m_ReconciliationVisualOffset = Vector3.zero;
                m_ReconciliationVisualRotationOffsetY = 0f;
                m_IsReconciling = false;
                m_ReconciliationProgress = 0f;
                return;
            }

            m_ReconciliationVisualOffset = Vector3.zero;
            m_ReconciliationVisualRotationOffsetY = 0f;
            ApplyVisualReconciliationOffset();
            m_IsReconciling = false;
            m_ReconciliationProgress = 0f;
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

        private static string FormatVector(Vector3 value)
        {
            return $"({value.x:F3},{value.y:F3},{value.z:F3})";
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

            return NetworkPositionState.Create(
                this.Transform.position,
                this.Transform.eulerAngles.y,
                m_VerticalSpeed,
                lastInput,
                IsGrounded,
                m_VerticalSpeed > 0f
            );
        }

        // STANDARD DRIVER METHODS: ---------------------------------------------------------------

        public override void OnUpdate()
        {
            if (this.Character.IsDead) return;
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
            if (m_Controller != null)
            {
                m_Controller.enabled = false;
                this.Transform.position = position;
                m_Controller.enabled = true;
            }
            else
            {
                this.Transform.position = position;
            }
        }

        public override void SetRotation(Quaternion rotation)
        {
            this.Transform.rotation = rotation;
        }

        public override void SetScale(Vector3 scale)
        {
            this.Transform.localScale = scale;
        }

        public override void AddPosition(Vector3 amount)
        {
            if (m_Controller != null && m_Controller.enabled)
            {
                Vector3 before = this.Transform.position;
                m_Controller.Move(amount);
                RecordExternalMoveVelocity(before);
            }
        }

        private void RecordExternalMoveVelocity(Vector3 before)
        {
            float deltaTime = this.Character != null
                ? this.Character.Time.DeltaTime
                : Time.deltaTime;

            if (deltaTime <= 0f) deltaTime = Time.deltaTime;
            if (deltaTime <= 0f) return;

            Vector3 actualDelta = this.Transform.position - before;
            if (actualDelta.sqrMagnitude <= 0.0000001f) return;

            this.m_MoveDirection = actualDelta / deltaTime;
        }

        public override void AddRotation(Quaternion amount)
        {
            this.Transform.rotation *= amount;
        }

        public override void AddScale(Vector3 scale)
        {
            this.Transform.localScale = Vector3.Scale(this.Transform.localScale, scale);
        }

        public override void ResetVerticalVelocity()
        {
            m_VerticalSpeed = 0f;
        }
    }
}
