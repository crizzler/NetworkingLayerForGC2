using System;
using System.Collections.Generic;
using UnityEngine;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.Characters;

namespace Arawn.GameCreator2.Networking
{
    /// <summary>
    /// Handles interpolation and rendering of remote (non-local) characters.
    /// Use this for characters controlled by other players or the server.
    /// </summary>
    [Title("Network Remote Character")]
    [Image(typeof(IconCharacter), ColorTheme.Type.Purple)]
    [Category("Network Remote Character")]
    [Description("Interpolates remote character positions and handles visual smoothing. " +
                 "Use this for non-local player characters in multiplayer.")]
    [Serializable]
    public class UnitDriverNetworkRemote : TUnitDriver
    {
        // EXPOSED MEMBERS: -----------------------------------------------------------------------

        [SerializeField] protected float m_SkinWidth = 0.08f;
        [SerializeField] private Axonometry m_Axonometry = new Axonometry();

        [Header("Interpolation")]
        [SerializeField] private float m_InterpolationDelay = 0.1f;
        [SerializeField] private float m_MaxExtrapolationTime = 0.25f;
        [SerializeField] private float m_SnapDistance = 5f;

        [Header("Debug")]
        [SerializeField] private bool m_LogMotionDiagnostics = false;
        [SerializeField] private float m_MotionDiagnosticInterval = 0.5f;

        // MEMBERS: -------------------------------------------------------------------------------

        [NonSerialized] protected CharacterController m_Controller;
        [NonSerialized] protected Vector3 m_MoveDirection;
        [NonSerialized] private NetworkCharacter m_NetworkCharacter;

        // Interpolation state
        [NonSerialized] private List<PositionSnapshot> m_SnapshotBuffer;
        [NonSerialized] private Vector3 m_InterpolatedPosition;
        [NonSerialized] private Quaternion m_InterpolatedRotation;
        [NonSerialized] private float m_ServerTime;
        [NonSerialized] private float m_RenderTime;
        [NonSerialized] private float m_EstimatedSnapshotInterval;
        [NonSerialized] private bool m_IsExtrapolating;
        [NonSerialized] private bool m_WasExtrapolating;
        [NonSerialized] private bool m_HasRenderTime;
        [NonSerialized] private bool m_IsGrounded = true;
        [NonSerialized] private bool m_IsJumping;
        [NonSerialized] private bool m_HasLastReceivedSnapshot;
        [NonSerialized] private float m_LastReceivedServerTimestamp;
        [NonSerialized] private bool m_HasAcceptedSnapshotTimestamp;
        [NonSerialized] private float m_LastAcceptedSnapshotTimestamp;
        [NonSerialized] private float m_LastReceivedSnapshotRealtime;
        [NonSerialized] private float m_LastMotionDiagnosticRealtime;
        [NonSerialized] private float m_LastSuppressedExternalRootWriteRealtime;
        [NonSerialized] private PositionSnapshot m_LatestAuthoritativeSnapshot;
        [NonSerialized] private bool m_HasLatestAuthoritativeSnapshot;
        [NonSerialized] private NetworkCharacterVisualPresentation m_VisualPresentation;
        [NonSerialized] private bool m_TeleportRotationPending;
        [NonSerialized] private int m_TeleportRotationPendingFrame;
        [NonSerialized] private bool m_RagdollPresentationSuspended;

        private const float NETWORK_AUTHORITY_ROOT_WRITE_GRACE_SECONDS = 0.75f;
        private const float DEFAULT_SNAPSHOT_INTERVAL = 1f / 30f;
        private const float MIN_SNAPSHOT_INTERVAL = 1f / 90f;
        private const float MAX_SNAPSHOT_INTERVAL = 0.2f;
        private const float MIN_LATEST_SNAPSHOT_BUFFER = 0.005f;
        private const float MAX_LATEST_SNAPSHOT_BUFFER = 0.05f;
        private const float MAX_RENDER_CATCHUP_MULTIPLIER = 1.5f;

        // INTERFACE PROPERTIES: ------------------------------------------------------------------

        public override Vector3 WorldMoveDirection => this.m_MoveDirection;
        public override Vector3 LocalMoveDirection => this.Transform.InverseTransformDirection(this.m_MoveDirection);
        public override float SkinWidth => this.m_Controller != null ? this.m_Controller.skinWidth : 0f;
        public override bool IsGrounded => m_IsGrounded;
        public override Vector3 FloorNormal => Vector3.up;

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

        public bool IsExtrapolating => m_IsExtrapolating;
        public float InterpolationDelay => m_InterpolationDelay;
        public bool IsJumping => m_IsJumping;

        public void ApplyTierSettings(NetworkRelevanceSettings settings)
        {
            m_InterpolationDelay = settings.interpolationDelay;
            m_MaxExtrapolationTime = settings.maxExtrapolationTime;
            m_SnapDistance = settings.snapDistance;
        }

        // INITIALIZERS: --------------------------------------------------------------------------

        public UnitDriverNetworkRemote()
        {
            this.m_MoveDirection = Vector3.zero;
        }

        public override void OnStartup(Character character)
        {
            base.OnStartup(character);

            m_NetworkCharacter = this.Character.GetComponent<NetworkCharacter>();
            m_SnapshotBuffer = new List<PositionSnapshot>(32);
            m_InterpolatedPosition = this.Transform.position;
            m_InterpolatedRotation = this.Transform.rotation;
            m_ServerTime = 0f;
            m_RenderTime = 0f;
            m_EstimatedSnapshotInterval = DEFAULT_SNAPSHOT_INTERVAL;
            m_IsGrounded = true;
            m_IsJumping = false;
            m_WasExtrapolating = false;
            m_HasRenderTime = false;
            m_HasLastReceivedSnapshot = false;
            m_LastReceivedServerTimestamp = 0f;
            m_HasAcceptedSnapshotTimestamp = false;
            m_LastAcceptedSnapshotTimestamp = 0f;
            m_LastReceivedSnapshotRealtime = -100f;
            m_LastMotionDiagnosticRealtime = -100f;
            m_LastSuppressedExternalRootWriteRealtime = -100f;
            m_HasLatestAuthoritativeSnapshot = false;
            m_TeleportRotationPending = false;
            m_TeleportRotationPendingFrame = -1;
            m_RagdollPresentationSuspended = false;
            m_VisualPresentation = new NetworkCharacterVisualPresentation(
                this.Character,
                "RemoteDriver");

            if (this.Character.Ragdoll != null)
            {
                this.Character.Ragdoll.EventBeforeStartRagdoll -= OnBeforeStartRagdoll;
                this.Character.Ragdoll.EventBeforeStartRagdoll += OnBeforeStartRagdoll;
                this.Character.Ragdoll.EventAfterFinishRecover -= OnAfterFinishRagdollRecover;
                this.Character.Ragdoll.EventAfterFinishRecover += OnAfterFinishRagdollRecover;
            }

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
                this.m_Controller.minMoveDistance = 0f;
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
            this.m_NetworkCharacter = null;
        }

        public override void OnDisable()
        {
            // GC2 can keep and later reuse this driver instance after a role change. Invalidate
            // packet watermarks and buffered poses so the next lifecycle cannot consume stale
            // transport state.
            ResetNetworkState();
            base.OnDisable();
        }

        /// <summary>
        /// Clears all transport-fed interpolation state before this driver leaves the remote role.
        /// Cleanup deliberately does not depend on an active transport bridge: role changes and
        /// scene teardown must invalidate late packets even after the bridge has already stopped.
        /// </summary>
        public void ResetNetworkState()
        {
            m_SnapshotBuffer?.Clear();
            m_MoveDirection = Vector3.zero;
            m_ServerTime = 0f;
            m_RenderTime = 0f;
            m_EstimatedSnapshotInterval = DEFAULT_SNAPSHOT_INTERVAL;
            m_IsExtrapolating = false;
            m_WasExtrapolating = false;
            m_HasRenderTime = false;
            m_IsGrounded = true;
            m_IsJumping = false;
            m_HasLastReceivedSnapshot = false;
            m_LastReceivedServerTimestamp = 0f;
            m_HasAcceptedSnapshotTimestamp = false;
            m_LastAcceptedSnapshotTimestamp = 0f;
            m_LastReceivedSnapshotRealtime = -100f;
            m_LastMotionDiagnosticRealtime = -100f;
            m_LastSuppressedExternalRootWriteRealtime = -100f;
            m_HasLatestAuthoritativeSnapshot = false;
            m_LatestAuthoritativeSnapshot = default;
            m_TeleportRotationPending = false;
            m_TeleportRotationPendingFrame = -1;
            m_RagdollPresentationSuspended = false;

            if (this.Transform != null &&
                NetworkCharacterVisualPresentation.HasUsablePose(
                    this.Transform.position,
                    this.Transform.rotation))
            {
                m_InterpolatedPosition = this.Transform.position;
                m_InterpolatedRotation = this.Transform.rotation;
            }
            else
            {
                m_InterpolatedPosition = Vector3.zero;
                m_InterpolatedRotation = Quaternion.identity;
            }

            ReleaseVisualPresentation();
        }

        private void OnBeforeStartRagdoll()
        {
            // GC2 reparents and duplicates parts of the Mannequin while entering ragdoll. Restore
            // the authored hierarchy before that transition starts so the temporary render frame
            // can never become part of the ragdoll hierarchy.
            m_RagdollPresentationSuspended = true;
            ReleaseVisualPresentation();
        }

        private void OnAfterFinishRagdollRecover()
        {
            m_RagdollPresentationSuspended = false;

            // Network snapshots continue to advance while GC2 owns the root for ragdoll. Recover
            // directly to the newest authoritative pose instead of replaying the stale buffer.
            if (CanAcceptNetworkSnapshots() && !IsGameplayRootSuspended())
            {
                ApplyLatestAuthoritativeTransform(preserveVisiblePose: false);
            }
        }

        // PREDICTIVE MOTION (CLIENT-SIDE): -------------------------------------------------------

        /// <summary>
        /// Synthesize forward-projected snapshots for a dash so the remote
        /// representation moves at full fidelity instead of waiting for the
        /// (slower) authoritative server position broadcasts.
        ///
        /// Real server snapshots that arrive afterwards will simply append to
        /// the buffer with their actual timestamps and reconcile the position.
        /// </summary>
        public void BeginPredictedDash(Vector3 worldDirection, float speed, float duration, float gravity)
        {
            if (!CanAcceptNetworkSnapshots())
            {
                ResetNetworkState();
                return;
            }

            if (m_SnapshotBuffer == null) return;
            if (duration <= 0f || speed <= 0f) return;
            if (worldDirection.sqrMagnitude <= 0f) return;

            Vector3 direction = worldDirection.normalized;

            // Anchor at whatever the remote currently shows so there is no rubber-banding.
            float renderTime = m_ServerTime - m_InterpolationDelay;
            Vector3 anchorPosition = m_InterpolatedPosition;
            float anchorRotationY = m_InterpolatedRotation.eulerAngles.y;

            // Drop any pending snapshots whose timestamp is in the future of the
            // anchor: they would override our prediction the moment we add it.
            for (int i = m_SnapshotBuffer.Count - 1; i >= 0; i--)
            {
                if ((float)m_SnapshotBuffer[i].timestamp > renderTime)
                {
                    m_SnapshotBuffer.RemoveAt(i);
                }
            }

            // Step the prediction at a fixed cadence; ~60 Hz matches typical
            // remote-render rates and keeps the buffer small.
            const float StepHz = 60f;
            const float StepDt = 1f / StepHz;

            int stepCount = Mathf.Max(2, Mathf.CeilToInt(duration * StepHz));
            float stepDuration = duration / stepCount;
            Vector3 horizontalVelocity = direction * speed;

            float t = 0f;
            Vector3 lastPosition = anchorPosition;
            float startTimestamp = renderTime;

            // First snapshot is the anchor itself so interpolation has both
            // endpoints to lerp between when the next predicted point is added.
            PushPredictedSnapshot(
                timestamp: startTimestamp,
                position: anchorPosition,
                rotationY: anchorRotationY,
                velocity: horizontalVelocity,
                verticalVelocity: 0f);

            for (int i = 1; i <= stepCount; i++)
            {
                t += stepDuration;
                Vector3 nextPosition = anchorPosition + horizontalVelocity * t;

                Vector3 segmentVelocity = (nextPosition - lastPosition) / Mathf.Max(StepDt, stepDuration);

                PushPredictedSnapshot(
                    timestamp: startTimestamp + t,
                    position: nextPosition,
                    rotationY: anchorRotationY,
                    velocity: segmentVelocity,
                    verticalVelocity: 0f);

                lastPosition = nextPosition;
            }
        }

        private void PushPredictedSnapshot(double timestamp, Vector3 position, float rotationY, Vector3 velocity, float verticalVelocity)
        {
            m_SnapshotBuffer.Add(new PositionSnapshot
            {
                timestamp = timestamp,
                position = position,
                rotation = Quaternion.Euler(0f, rotationY, 0f),
                velocity = velocity,
                rotationY = rotationY,
                verticalVelocity = verticalVelocity,
                flags = 0
            });
        }

        // NETWORK STATE UPDATES: -----------------------------------------------------------------

        /// <summary>
        /// Add a new position snapshot from the server.
        /// Call this when receiving server state updates.
        /// </summary>
        public void AddSnapshot(NetworkPositionState state, float serverTimestamp)
        {
            if (!CanAcceptNetworkSnapshots())
            {
                ResetNetworkState();
                return;
            }

            if (m_SnapshotBuffer == null) return;
            if (!NetworkCharacterVisualPresentation.IsFinite(serverTimestamp))
            {
                LogRemoteMotionDiagnostic(
                    $"rejected non-finite snapshot timestamp={serverTimestamp}",
                    force: true);
                return;
            }

            if (m_HasAcceptedSnapshotTimestamp &&
                serverTimestamp <= m_LastAcceptedSnapshotTimestamp)
            {
                LogRemoteMotionDiagnostic(
                    $"rejected stale snapshot timestamp current={serverTimestamp:F3} " +
                    $"watermark={m_LastAcceptedSnapshotTimestamp:F3} buffer={m_SnapshotBuffer.Count}",
                    force: true);
                return;
            }

            PositionSnapshot incomingSnapshot = PositionSnapshot.Create(state, serverTimestamp);
            Vector3 position = ResolveSnapshotWorldPosition(incomingSnapshot);
            Quaternion rotation = ResolveSnapshotWorldRotation(incomingSnapshot);
            if (!NetworkCharacterVisualPresentation.HasUsablePose(position, rotation))
            {
                LogRemoteMotionDiagnostic(
                    $"rejected non-finite snapshot serverTime={serverTimestamp:F3} " +
                    $"position={FormatVector(position)} rotation={rotation}",
                    force: true);
                return;
            }

            LogFocusedTraversalSnapshot(state, serverTimestamp, position);

            float rotationY = rotation.eulerAngles.y;
            float realtime = Time.realtimeSinceStartup;
            bool firstAuthoritativeSnapshot = !m_HasLatestAuthoritativeSnapshot;
            bool teleportSnapshot = false;

            m_IsGrounded = state.IsGrounded;
            m_IsJumping = state.IsJumping;

            if (m_LogMotionDiagnostics)
            {
                Vector3 previousSnapshotPosition = m_SnapshotBuffer.Count > 0
                    ? ResolveSnapshotWorldPosition(m_SnapshotBuffer[m_SnapshotBuffer.Count - 1])
                    : this.Transform.position;
                float previousSnapshotRotationY = m_SnapshotBuffer.Count > 0
                    ? ResolveSnapshotWorldRotation(m_SnapshotBuffer[m_SnapshotBuffer.Count - 1]).eulerAngles.y
                    : this.Transform.eulerAngles.y;
                float snapshotDistance = Vector3.Distance(position, previousSnapshotPosition);
                float snapshotYDelta = position.y - previousSnapshotPosition.y;
                float snapshotRotationDelta = Mathf.DeltaAngle(previousSnapshotRotationY, rotationY);

                if (IsTraversalLikeRemoteMotion() ||
                    Mathf.Abs(snapshotYDelta) > 0.05f ||
                    Mathf.Abs(snapshotRotationDelta) > 5f)
                {
                    LogTraversalPose(
                        $"received-state-snapshot serverTime={serverTimestamp:F3} seq={state.lastProcessedInput} " +
                        $"statePos={FormatVector(position)} stateY={position.y:F3} " +
                        $"stateRotY={rotationY:F2} previousSnapshot={FormatVector(previousSnapshotPosition)} " +
                        $"previousY={previousSnapshotPosition.y:F3} previousRotY={previousSnapshotRotationY:F2} " +
                        $"snapshotDistance={snapshotDistance:F3} snapshotYDelta={snapshotYDelta:F3} " +
                        $"snapshotRotDelta={snapshotRotationDelta:F2} verticalSpeed={state.GetVerticalVelocity():F3} " +
                        $"hasMoveVelocity={state.HasMoveVelocity} moveVelocity={FormatVector(state.GetMoveVelocity())} " +
                        $"flags=0x{state.flags:X2} grounded={state.IsGrounded} jumping={state.IsJumping} " +
                        $"buffer={m_SnapshotBuffer.Count} serverTimeLocal={m_ServerTime:F3} {FormatBusyState()}");
                }
            }

            if (m_HasLastReceivedSnapshot)
            {
                float serverGap = serverTimestamp - m_LastReceivedServerTimestamp;
                float receiveGap = realtime - m_LastReceivedSnapshotRealtime;
                float expectedMaxGap = Mathf.Max(m_InterpolationDelay * 1.5f, 0.08f);

                if (serverGap <= 0f)
                {
                    LogRemoteMotionDiagnostic(
                        $"received non-increasing snapshot timestamp current={serverTimestamp:F3} " +
                        $"previous={m_LastReceivedServerTimestamp:F3} buffer={m_SnapshotBuffer.Count}",
                        force: true);
                }
                else if (serverGap > expectedMaxGap || receiveGap > expectedMaxGap)
                {
                    LogRemoteMotionDiagnostic(
                        $"snapshot gap serverGap={serverGap:F3}s receiveGap={receiveGap:F3}s " +
                        $"expectedMax={expectedMaxGap:F3}s buffer={m_SnapshotBuffer.Count} " +
                        $"serverTime={m_ServerTime:F3} delay={m_InterpolationDelay:F3}");
                }

                if (serverGap > 0f)
                {
                    float interval = Mathf.Clamp(serverGap, MIN_SNAPSHOT_INTERVAL, MAX_SNAPSHOT_INTERVAL);
                    m_EstimatedSnapshotInterval = Mathf.Lerp(m_EstimatedSnapshotInterval, interval, 0.2f);
                }
            }

            // Check for teleport
            if (m_SnapshotBuffer.Count > 0)
            {
                Vector3 lastPos = ResolveSnapshotWorldPosition(m_SnapshotBuffer[m_SnapshotBuffer.Count - 1]);
                float distance = Vector3.Distance(position, lastPos);
                if (distance > m_SnapDistance)
                {
                    teleportSnapshot = true;
                    LogRemoteMotionDiagnostic(
                        $"remote snap distance={distance:F3} snapDistance={m_SnapDistance:F3} " +
                        $"from={FormatVector(lastPos)} to={FormatVector(position)}",
                        force: true);

                    // Teleport - clear buffer and snap
                    m_SnapshotBuffer.Clear();
                }
            }

            // Prefer the authoritative move velocity carried with the state. For
            // traversal/free-climb this preserves the owner's animation direction
            // even when root attachment corrections make position deltas ambiguous.
            Vector3 velocity = state.HasMoveVelocity ? state.GetMoveVelocity() : Vector3.zero;
            if (m_SnapshotBuffer.Count > 0)
            {
                var lastSnapshot = m_SnapshotBuffer[m_SnapshotBuffer.Count - 1];
                float timeDelta = serverTimestamp - (float)lastSnapshot.timestamp;
                if (!state.HasMoveVelocity && timeDelta > 0.001f)
                {
                    velocity = (position - ResolveSnapshotWorldPosition(lastSnapshot)) / timeDelta;
                }
            }

            if (!NetworkCharacterVisualPresentation.IsFinite(velocity))
            {
                velocity = Vector3.zero;
            }

            if (this.Character?.Motion is UnitMotionNetworkController networkMotion)
            {
                // Position snapshots are the persistent fallback for Traversal presentation.
                // At a clamped rail edge the root is stationary, but the authoritative state
                // still carries the owner's attempted direction in moveVelocity.
                networkMotion.ApplyReplicatedTraversalPresentationDirection(velocity);
            }

            incomingSnapshot.velocity = velocity;
            incomingSnapshot.supportLocalVelocity = ResolveSupportLocalVelocity(
                incomingSnapshot,
                serverTimestamp,
                velocity);
            m_SnapshotBuffer.Add(incomingSnapshot);

            // The Character root and CharacterController are gameplay state, not a render target.
            // Move them to the newest authoritative pose immediately. The delayed/interpolated
            // pose is applied only to a validated visual-only Mannequin wrapper below.
            m_LatestAuthoritativeSnapshot = incomingSnapshot;
            m_HasLatestAuthoritativeSnapshot = true;
            if (!IsGameplayRootSuspended())
            {
                ApplyLatestAuthoritativeTransform(
                    preserveVisiblePose: !firstAuthoritativeSnapshot && !teleportSnapshot);
            }

            if (firstAuthoritativeSnapshot || teleportSnapshot)
            {
                m_InterpolatedPosition = position;
                m_InterpolatedRotation = rotation;
                m_MoveDirection = teleportSnapshot ? Vector3.zero : velocity;
                m_VisualPresentation?.ResetOffset();
            }

            // Trim old snapshots
            float minTime = serverTimestamp - 1f; // Keep 1 second of history
            while (m_SnapshotBuffer.Count > 2 && m_SnapshotBuffer[0].timestamp < minTime)
            {
                m_SnapshotBuffer.RemoveAt(0);
            }

            m_HasLastReceivedSnapshot = true;
            m_LastReceivedServerTimestamp = serverTimestamp;
            m_LastReceivedSnapshotRealtime = realtime;
            m_HasAcceptedSnapshotTimestamp = true;
            m_LastAcceptedSnapshotTimestamp = serverTimestamp;
        }

        private bool CanAcceptNetworkSnapshots()
        {
            if (m_NetworkCharacter == null && this.Character != null)
            {
                m_NetworkCharacter = this.Character.GetComponent<NetworkCharacter>();
            }

            return m_NetworkCharacter != null &&
                   m_NetworkCharacter.CurrentRole == NetworkCharacter.NetworkRole.RemoteClient &&
                   ReferenceEquals(m_NetworkCharacter.ActiveDriver, this);
        }

        private bool IsGameplayRootSuspended()
        {
            return this.Character == null ||
                   m_RagdollPresentationSuspended ||
                   this.Character.IsDead ||
                   (this.Character.Ragdoll != null && this.Character.Ragdoll.IsRagdoll);
        }

        /// <summary>
        /// Set the current server time for interpolation.
        /// Should be called every frame with synchronized server time.
        /// </summary>
        public void SetServerTime(float serverTime)
        {
            if (!CanAcceptNetworkSnapshots()) return;
            if (!NetworkCharacterVisualPresentation.IsFinite(serverTime)) return;

            // State packets carry the server time from when they were sent. On pure
            // clients that value is already older than the live synced transport time,
            // so accepting it after a newer frame time makes remote proxies step back
            // at packet frequency. Keep the render clock monotonic; new snapshots are
            // still inserted at their original timestamps in AddSnapshot.
            m_ServerTime = Mathf.Max(m_ServerTime, serverTime);
        }

        // UPDATE METHOD: -------------------------------------------------------------------------

        public override void OnUpdate()
        {
            if (this.Character == null) return;
            if (!CanAcceptNetworkSnapshots())
            {
                ReleaseVisualPresentation();
                return;
            }

            if (IsGameplayRootSuspended())
            {
                ReleaseVisualPresentation();
                return;
            }

            EnsureVisualPresentation();

            if (m_TeleportRotationPending &&
                Time.frameCount > m_TeleportRotationPendingFrame)
            {
                m_TeleportRotationPending = false;
                m_TeleportRotationPendingFrame = -1;
            }

            // A support/platform can move between packets. Resolve the newest authoritative
            // support-relative pose every render frame while preserving the already-rendered
            // Mannequin pose. This keeps physics current without introducing a second gameplay
            // root writer from interpolation.
            ApplyLatestAuthoritativeTransform(preserveVisiblePose: true);

            float deltaTime = this.Character.Time.DeltaTime;

            // Calculate render time (with delay for interpolation)
            float targetRenderTime = CalculateTargetRenderTime();
            float renderTime = AdvanceRenderTime(targetRenderTime, deltaTime);

            // Interpolate position
            InterpolatePosition(renderTime, deltaTime);

            if (m_IsExtrapolating != m_WasExtrapolating)
            {
                LogRemoteMotionDiagnostic(
                    $"extrapolating {m_WasExtrapolating}->{m_IsExtrapolating} " +
                    $"serverTime={m_ServerTime:F3} renderTime={renderTime:F3} " +
                    $"targetRenderTime={targetRenderTime:F3} buffer={m_SnapshotBuffer.Count} " +
                    $"delay={m_InterpolationDelay:F3} snapshotInterval={m_EstimatedSnapshotInterval:F3}");
                LogFocusedTraversalRender(
                    "RemoteExtrapolation",
                    $"active={m_IsExtrapolating} previous={m_WasExtrapolating}");
                m_WasExtrapolating = m_IsExtrapolating;
            }

            // Apply interpolated transform
            ApplyInterpolatedTransform();
            LogFocusedTraversalRender("RemoteRender", string.Empty, sample: true);

            // Update controller size
            if (m_Controller != null)
            {
                float height = this.Character.Motion.Height;
                float radius = this.Character.Motion.Radius;

                if (Math.Abs(m_Controller.height - height) > float.Epsilon)
                {
                    m_Controller.height = height;
                    m_Controller.center = Vector3.zero;
                }
                if (Math.Abs(m_Controller.radius - radius) > float.Epsilon)
                {
                    m_Controller.radius = radius;
                }
            }
        }

        private float CalculateTargetRenderTime()
        {
            float targetRenderTime = m_ServerTime - m_InterpolationDelay;
            if (m_SnapshotBuffer == null || m_SnapshotBuffer.Count < 2)
            {
                return targetRenderTime;
            }

            float latestSnapshotTime = (float)m_SnapshotBuffer[m_SnapshotBuffer.Count - 1].timestamp;
            float latestSnapshotBuffer = Mathf.Clamp(
                m_EstimatedSnapshotInterval * 0.5f,
                MIN_LATEST_SNAPSHOT_BUFFER,
                MAX_LATEST_SNAPSHOT_BUFFER);

            return Mathf.Min(targetRenderTime, latestSnapshotTime - latestSnapshotBuffer);
        }

        private float AdvanceRenderTime(float targetRenderTime, float deltaTime)
        {
            if (!m_HasRenderTime)
            {
                m_RenderTime = targetRenderTime;
                m_HasRenderTime = true;
                return m_RenderTime;
            }

            if (targetRenderTime < m_RenderTime)
            {
                // If late packets move the target cursor backwards, avoid a visible
                // root rewind. Holding briefly is less noticeable than stepping back.
                if (m_RenderTime - targetRenderTime > m_MaxExtrapolationTime)
                {
                    m_RenderTime = targetRenderTime;
                }

                return m_RenderTime;
            }

            float remaining = targetRenderTime - m_RenderTime;
            if (remaining <= 0f) return m_RenderTime;

            float catchupMultiplier = remaining > m_EstimatedSnapshotInterval * 2f
                ? MAX_RENDER_CATCHUP_MULTIPLIER
                : 1f;

            m_RenderTime += Mathf.Min(remaining, deltaTime * catchupMultiplier);
            return m_RenderTime;
        }

        private void InterpolatePosition(float renderTime, float deltaTime)
        {
            if (m_SnapshotBuffer.Count == 0)
            {
                m_IsExtrapolating = false;
                return;
            }

            if (m_SnapshotBuffer.Count == 1)
            {
                // Only one snapshot, use it directly
                m_InterpolatedPosition = ResolveSnapshotWorldPosition(m_SnapshotBuffer[0]);
                m_InterpolatedRotation = ResolveSnapshotWorldRotation(m_SnapshotBuffer[0]);
                m_MoveDirection = m_SnapshotBuffer[0].velocity;
                m_IsExtrapolating = false;
                return;
            }

            // Find the two snapshots to interpolate between
            PositionSnapshot? before = null;
            PositionSnapshot? after = null;

            for (int i = 0; i < m_SnapshotBuffer.Count; i++)
            {
                if (m_SnapshotBuffer[i].timestamp <= renderTime)
                {
                    before = m_SnapshotBuffer[i];
                }
                else
                {
                    after = m_SnapshotBuffer[i];
                    break;
                }
            }

            if (before.HasValue && after.HasValue)
            {
                // Interpolate between two snapshots
                float duration = (float)(after.Value.timestamp - before.Value.timestamp);
                float elapsed = (float)(renderTime - before.Value.timestamp);
                float t = duration > 0 ? Mathf.Clamp01(elapsed / duration) : 0f;

                if (TryInterpolateSupportedPose(before.Value, after.Value, t, out Vector3 supportedPosition, out Quaternion supportedRotation))
                {
                    m_InterpolatedPosition = supportedPosition;
                    m_InterpolatedRotation = supportedRotation;
                }
                else
                {
                    m_InterpolatedPosition = Vector3.Lerp(before.Value.position, after.Value.position, t);
                    m_InterpolatedRotation = Quaternion.Slerp(before.Value.rotation, after.Value.rotation, t);
                }

                m_MoveDirection = Vector3.Lerp(before.Value.velocity, after.Value.velocity, t);
                m_IsExtrapolating = false;
            }
            else if (before.HasValue)
            {
                // No future snapshot - extrapolate
                float timeSinceLastSnapshot = (float)(renderTime - before.Value.timestamp);

                if (timeSinceLastSnapshot <= m_MaxExtrapolationTime)
                {
                    if (TryExtrapolateSupportedPose(before.Value, timeSinceLastSnapshot, out Vector3 supportedPosition, out Quaternion supportedRotation))
                    {
                        m_InterpolatedPosition = supportedPosition;
                        m_InterpolatedRotation = supportedRotation;
                    }
                    else
                    {
                        // Extrapolate using velocity
                        m_InterpolatedPosition = before.Value.position + before.Value.velocity * timeSinceLastSnapshot;
                        m_InterpolatedRotation = ExtrapolateRotation(before.Value, timeSinceLastSnapshot);
                    }

                    m_MoveDirection = before.Value.velocity;
                    m_IsExtrapolating = true;
                }
                else
                {
                    // Too long without update - stop extrapolating
                    if (TryExtrapolateSupportedPose(before.Value, m_MaxExtrapolationTime, out Vector3 supportedPosition, out Quaternion supportedRotation))
                    {
                        m_InterpolatedPosition = supportedPosition;
                        m_InterpolatedRotation = supportedRotation;
                    }
                    else
                    {
                        m_InterpolatedPosition = before.Value.position + before.Value.velocity * m_MaxExtrapolationTime;
                        m_InterpolatedRotation = ExtrapolateRotation(before.Value, m_MaxExtrapolationTime);
                    }

                    m_MoveDirection = Vector3.zero;
                    m_IsExtrapolating = true;
                }
            }
            else if (after.HasValue)
            {
                // Only future snapshot - use it
                m_InterpolatedPosition = ResolveSnapshotWorldPosition(after.Value);
                m_InterpolatedRotation = ResolveSnapshotWorldRotation(after.Value);
                m_MoveDirection = after.Value.velocity;
                m_IsExtrapolating = false;
            }
        }

        private bool TryInterpolateSupportedPose(
            PositionSnapshot before,
            PositionSnapshot after,
            float t,
            out Vector3 position,
            out Quaternion rotation)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;

            if (!before.HasSupport || !after.HasSupport) return false;
            if (before.supportId != after.supportId) return false;
            if (!NetworkMotionSupportAnchor.TryResolve(before.supportId, out NetworkMotionSupportAnchor support)) return false;

            Vector3 localPosition = Vector3.Lerp(before.supportLocalPosition, after.supportLocalPosition, t);
            float localYaw = Mathf.LerpAngle(before.supportLocalYaw, after.supportLocalYaw, t);
            position = support.transform.TransformPoint(localPosition);
            rotation = ResolveSupportWorldRotation(support.transform, localYaw);
            return true;
        }

        private bool TryExtrapolateSupportedPose(
            PositionSnapshot snapshot,
            float timeSinceSnapshot,
            out Vector3 position,
            out Quaternion rotation)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;

            if (!snapshot.HasSupport) return false;
            if (!NetworkMotionSupportAnchor.TryResolve(snapshot.supportId, out NetworkMotionSupportAnchor support)) return false;

            Vector3 localPosition = snapshot.supportLocalPosition +
                                    snapshot.supportLocalVelocity * timeSinceSnapshot;
            position = support.transform.TransformPoint(localPosition);
            rotation = ResolveSupportWorldRotation(support.transform, snapshot.supportLocalYaw);
            return true;
        }

        private Vector3 ResolveSupportLocalVelocity(
            PositionSnapshot incoming,
            float serverTimestamp,
            Vector3 worldVelocity)
        {
            if (!incoming.HasSupport) return Vector3.zero;

            if (m_SnapshotBuffer != null && m_SnapshotBuffer.Count > 0)
            {
                PositionSnapshot previous = m_SnapshotBuffer[m_SnapshotBuffer.Count - 1];
                float deltaTime = serverTimestamp - (float)previous.timestamp;
                if (previous.HasSupport &&
                    previous.supportId == incoming.supportId &&
                    deltaTime > 0.001f)
                {
                    Vector3 localVelocity =
                        (incoming.supportLocalPosition - previous.supportLocalPosition) / deltaTime;
                    return NetworkCharacterVisualPresentation.IsFinite(localVelocity)
                        ? localVelocity
                        : Vector3.zero;
                }
            }

            if (NetworkMotionSupportAnchor.TryResolve(
                    incoming.supportId,
                    out NetworkMotionSupportAnchor support))
            {
                Vector3 localVelocity = support.transform.InverseTransformDirection(worldVelocity);
                return NetworkCharacterVisualPresentation.IsFinite(localVelocity)
                    ? localVelocity
                    : Vector3.zero;
            }

            return Vector3.zero;
        }

        private Vector3 ResolveSnapshotWorldPosition(PositionSnapshot snapshot)
        {
            if (snapshot.HasSupport &&
                NetworkMotionSupportAnchor.TryResolve(snapshot.supportId, out NetworkMotionSupportAnchor support))
            {
                Vector3 supportedPosition = support.transform.TransformPoint(snapshot.supportLocalPosition);
                if (NetworkCharacterVisualPresentation.IsFinite(supportedPosition))
                {
                    return supportedPosition;
                }
            }

            return snapshot.position;
        }

        private Quaternion ResolveSnapshotWorldRotation(PositionSnapshot snapshot)
        {
            if (snapshot.HasSupport &&
                NetworkMotionSupportAnchor.TryResolve(snapshot.supportId, out NetworkMotionSupportAnchor support))
            {
                Quaternion supportedRotation = ResolveSupportWorldRotation(
                    support.transform,
                    snapshot.supportLocalYaw);
                if (NetworkCharacterVisualPresentation.IsUsableRotation(supportedRotation))
                {
                    return supportedRotation;
                }
            }

            return snapshot.rotation;
        }

        private static Quaternion ResolveSupportWorldRotation(Transform supportTransform, float supportLocalYaw)
        {
            return Quaternion.Euler(0f, supportTransform.eulerAngles.y + supportLocalYaw, 0f);
        }

        private Quaternion ExtrapolateRotation(PositionSnapshot snapshot, float timeSinceSnapshot)
        {
            if (m_SnapshotBuffer == null || m_SnapshotBuffer.Count < 2)
            {
                return snapshot.rotation;
            }

            int snapshotIndex = -1;
            for (int i = m_SnapshotBuffer.Count - 1; i >= 0; i--)
            {
                if (Math.Abs(m_SnapshotBuffer[i].timestamp - snapshot.timestamp) <= 0.0001)
                {
                    snapshotIndex = i;
                    break;
                }
            }

            if (snapshotIndex <= 0) return snapshot.rotation;

            PositionSnapshot previous = m_SnapshotBuffer[snapshotIndex - 1];
            float snapshotDeltaTime = (float)(snapshot.timestamp - previous.timestamp);
            if (snapshotDeltaTime <= 0.001f) return snapshot.rotation;

            float angularVelocity = Mathf.DeltaAngle(previous.rotationY, snapshot.rotationY) / snapshotDeltaTime;
            float extrapolatedYaw = snapshot.rotationY + angularVelocity * timeSinceSnapshot;
            return Quaternion.Euler(0f, extrapolatedYaw, 0f);
        }

        private void ApplyInterpolatedTransform()
        {
            if (!NetworkCharacterVisualPresentation.HasUsablePose(
                    m_InterpolatedPosition,
                    m_InterpolatedRotation))
            {
                m_VisualPresentation?.ResetOffset();
                return;
            }

            if (!EnsureVisualPresentation()) return;
            m_VisualPresentation.ApplyWorldPose(
                m_InterpolatedPosition,
                m_InterpolatedRotation);
        }

        private void TeleportTo(Vector3 position, float rotationY)
        {
            Quaternion rotation = Quaternion.Euler(0f, rotationY, 0f);
            if (!NetworkCharacterVisualPresentation.HasUsablePose(position, rotation)) return;

            m_InterpolatedPosition = position;
            m_InterpolatedRotation = rotation;
            m_MoveDirection = Vector3.zero;
            m_HasLatestAuthoritativeSnapshot = false;
            EstablishSnapshotWatermarkFromCurrentServerTime();

            ApplyAuthoritativeRootPose(position, rotation, preserveVisiblePose: false);
            m_VisualPresentation?.ResetOffset();
        }

        private void EstablishSnapshotWatermarkFromCurrentServerTime()
        {
            if (!NetworkCharacterVisualPresentation.IsFinite(m_ServerTime)) return;
            if (!m_HasAcceptedSnapshotTimestamp && m_ServerTime <= 0f) return;

            if (!m_HasAcceptedSnapshotTimestamp ||
                m_ServerTime > m_LastAcceptedSnapshotTimestamp)
            {
                m_HasAcceptedSnapshotTimestamp = true;
                m_LastAcceptedSnapshotTimestamp = m_ServerTime;
            }
        }

        private bool ApplyLatestAuthoritativeTransform(bool preserveVisiblePose)
        {
            if (!m_HasLatestAuthoritativeSnapshot) return false;

            Vector3 position = ResolveSnapshotWorldPosition(m_LatestAuthoritativeSnapshot);
            Quaternion rotation = ResolveSnapshotWorldRotation(m_LatestAuthoritativeSnapshot);
            if (!NetworkCharacterVisualPresentation.HasUsablePose(position, rotation))
            {
                return false;
            }

            return ApplyAuthoritativeRootPose(position, rotation, preserveVisiblePose);
        }

        private bool ApplyAuthoritativeRootPose(
            Vector3 position,
            Quaternion rotation,
            bool preserveVisiblePose)
        {
            if (!NetworkCharacterVisualPresentation.HasUsablePose(position, rotation)) return false;

            Vector3 visiblePosition = Vector3.zero;
            Quaternion visibleRotation = Quaternion.identity;
            bool capturedVisualPose = preserveVisiblePose &&
                                      EnsureVisualPresentation() &&
                                      m_VisualPresentation.TryGetWorldPose(
                                          out visiblePosition,
                                          out visibleRotation);

            if ((this.Transform.position - position).sqrMagnitude > 0.0000001f ||
                Quaternion.Angle(this.Transform.rotation, rotation) > 0.001f)
            {
                this.Transform.SetPositionAndRotation(position, rotation);
                Physics.SyncTransforms();
            }

            if (capturedVisualPose)
            {
                m_VisualPresentation.ApplyWorldPose(visiblePosition, visibleRotation);
            }

            return true;
        }

        private bool EnsureVisualPresentation()
        {
            if (IsGameplayRootSuspended())
            {
                return false;
            }
            if (m_VisualPresentation == null)
            {
                m_VisualPresentation = new NetworkCharacterVisualPresentation(
                    this.Character,
                    "RemoteDriver");
            }

            return m_VisualPresentation.TryEnsure(logWarning: true);
        }

        private void ReleaseVisualPresentation()
        {
            m_VisualPresentation?.Dispose();
            m_VisualPresentation = null;
        }

        private void LogRemoteMotionDiagnostic(string message, bool force = false)
        {
            if (!m_LogMotionDiagnostics) return;

            float now = Time.realtimeSinceStartup;
            float interval = Mathf.Max(0.05f, m_MotionDiagnosticInterval);
            if (!force && now - m_LastMotionDiagnosticRealtime < interval) return;

            Debug.Log(
                $"[NetworkMotionDebug][RemoteDriver] {this.Character?.name ?? "Character"}: {message}",
                this.Character);
            m_LastMotionDiagnosticRealtime = now;
        }

        private void LogFocusedTraversalSnapshot(
            NetworkPositionState state,
            float serverTimestamp,
            Vector3 incomingPosition)
        {
            if (!NetworkTraversalClimbDiagnostics.IsFocused(this.Character?.gameObject)) return;

            Vector3 previousVelocity = m_HasLatestAuthoritativeSnapshot
                ? m_LatestAuthoritativeSnapshot.velocity
                : Vector3.zero;
            Vector3 incomingVelocity = state.GetMoveVelocity();
            Vector3 renderCorrection = incomingPosition - m_InterpolatedPosition;
            float correctionAlong = previousVelocity.sqrMagnitude > 0.0001f
                ? Vector3.Dot(renderCorrection, previousVelocity.normalized)
                : 0f;
            bool stoppedAtEndpoint = previousVelocity.sqrMagnitude > 0.25f &&
                                     incomingVelocity.sqrMagnitude <= 0.01f;
            bool backwardsCorrection = correctionAlong < -0.002f;

            NetworkTraversalClimbDiagnostics.Log(
                backwardsCorrection ? "RemoteEndpointReverse" : "RemoteSnapshot",
                $"actor={m_NetworkCharacter?.NetworkId ?? 0} role={m_NetworkCharacter?.CurrentRole.ToString() ?? "none"} " +
                $"seq={state.lastProcessedInput} serverTime={serverTimestamp:F3} " +
                $"incoming={NetworkTraversalClimbDiagnostics.Vector(incomingPosition)} " +
                $"rendered={NetworkTraversalClimbDiagnostics.Vector(m_InterpolatedPosition)} " +
                $"root={NetworkTraversalClimbDiagnostics.Vector(this.Transform.position)} " +
                $"correction={NetworkTraversalClimbDiagnostics.Vector(renderCorrection)} " +
                $"correctionAlong={correctionAlong:F4} previousVelocity={NetworkTraversalClimbDiagnostics.Vector(previousVelocity)} " +
                $"incomingVelocity={NetworkTraversalClimbDiagnostics.Vector(incomingVelocity)} " +
                $"stoppedAtEndpoint={stoppedAtEndpoint} extrapolating={m_IsExtrapolating} " +
                $"renderTime={m_RenderTime:F3} buffer={m_SnapshotBuffer?.Count ?? 0}",
                this.Character,
                backwardsCorrection || stoppedAtEndpoint
                    ? null
                    : $"remote-snapshot:{this.Character.GetInstanceID()}");
        }

        private void LogFocusedTraversalRender(string stage, string extra, bool sample = false)
        {
            if (!NetworkTraversalClimbDiagnostics.IsFocused(this.Character?.gameObject)) return;

            Vector3 visual = this.Character?.Animim?.Mannequin != null
                ? this.Character.Animim.Mannequin.position
                : m_InterpolatedPosition;
            PositionSnapshot latest = m_HasLatestAuthoritativeSnapshot
                ? m_LatestAuthoritativeSnapshot
                : default;
            string suffix = string.IsNullOrEmpty(extra) ? string.Empty : $" {extra}";
            NetworkTraversalClimbDiagnostics.Log(
                stage,
                $"actor={m_NetworkCharacter?.NetworkId ?? 0} role={m_NetworkCharacter?.CurrentRole.ToString() ?? "none"} " +
                $"root={NetworkTraversalClimbDiagnostics.Vector(this.Transform.position)} " +
                $"visual={NetworkTraversalClimbDiagnostics.Vector(visual)} " +
                $"interpolated={NetworkTraversalClimbDiagnostics.Vector(m_InterpolatedPosition)} " +
                $"latest={NetworkTraversalClimbDiagnostics.Vector(ResolveSnapshotWorldPosition(latest))} " +
                $"latestVelocity={NetworkTraversalClimbDiagnostics.Vector(latest.velocity)} " +
                $"serverTime={m_ServerTime:F3} renderTime={m_RenderTime:F3} " +
                $"extrapolating={m_IsExtrapolating} buffer={m_SnapshotBuffer?.Count ?? 0}{suffix}",
                this.Character,
                sample ? $"remote-render:{this.Character.GetInstanceID()}" : null,
                sample ? 0.05f : NetworkTraversalClimbDiagnostics.SampleInterval);
        }

        private bool ShouldIgnoreExternalRootWrite(string operation, Vector3 targetPosition, Quaternion? targetRotation)
        {
            if (!IsNetworkSnapshotAuthorityActive) return false;

            float now = Time.realtimeSinceStartup;
            if (now - m_LastSuppressedExternalRootWriteRealtime >= 0.25f)
            {
                string rotation = targetRotation.HasValue
                    ? $" targetRotY={targetRotation.Value.eulerAngles.y:F2}"
                    : string.Empty;
                LogRemoteMotionDiagnostic(
                    $"ignored external {operation} while network snapshots are authoritative " +
                    $"target={FormatVector(targetPosition)}{rotation} current={FormatVector(this.Transform.position)} " +
                    $"interpolated={FormatVector(m_InterpolatedPosition)} " +
                    $"snapshotAge={(now - m_LastReceivedSnapshotRealtime):F3}s {FormatBusyState()}",
                    force: true);
                m_LastSuppressedExternalRootWriteRealtime = now;
            }

            return true;
        }

        private bool IsNetworkSnapshotAuthorityActive =>
            m_HasLastReceivedSnapshot &&
            Time.realtimeSinceStartup - m_LastReceivedSnapshotRealtime <= NETWORK_AUTHORITY_ROOT_WRITE_GRACE_SECONDS;

        private void LogTraversalPose(string message)
        {
            if (!m_LogMotionDiagnostics) return;

            float now = Time.realtimeSinceStartup;
            float interval = Mathf.Max(0.05f, m_MotionDiagnosticInterval);
            if (now - m_LastMotionDiagnosticRealtime < interval) return;

            Debug.Log(
                $"[TraversalPoseDebug][RemoteDriver] {this.Character?.name ?? "Character"} " +
                $"pos={FormatVector(this.Transform.position)} y={this.Transform.position.y:F3} " +
                $"rotY={this.Transform.eulerAngles.y:F2} forward={FormatVector(this.Transform.forward)} " +
                $"interpolated={FormatVector(m_InterpolatedPosition)} interpolatedY={m_InterpolatedPosition.y:F3} " +
                $"interpolatedRotY={m_InterpolatedRotation.eulerAngles.y:F2} extrapolating={m_IsExtrapolating} " +
                $"{message}",
                this.Character);
            m_LastMotionDiagnosticRealtime = now;
        }

        private bool IsTraversalLikeRemoteMotion()
        {
            return this.Character?.Busy != null &&
                   (this.Character.Busy.IsBusy || this.Character.Busy.AreLegsBusy);
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

        // STANDARD DRIVER METHODS: ---------------------------------------------------------------

        public override void SetPosition(Vector3 position, bool teleport = false)
        {
            Vector3 rootPosition = ToRootPosition(position);
            if (!NetworkCharacterVisualPresentation.IsFinite(rootPosition)) return;

            if (teleport)
            {
                m_SnapshotBuffer?.Clear();
                TeleportTo(rootPosition, this.Transform.eulerAngles.y);
                // The approved teleport flow calls SetRotation immediately afterwards. Permit
                // exactly that same-frame rotation while ordinary remote facing writes remain
                // suppressed by snapshot authority.
                m_TeleportRotationPending = true;
                m_TeleportRotationPendingFrame = Time.frameCount;
            }
            else
            {
                if (ShouldIgnoreExternalRootWrite("SetPosition", rootPosition, null)) return;

                m_InterpolatedPosition = rootPosition;
                ApplyAuthoritativeRootPose(
                    rootPosition,
                    m_InterpolatedRotation,
                    preserveVisiblePose: false);
                m_VisualPresentation?.ResetOffset();
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
            if (!NetworkCharacterVisualPresentation.IsUsableRotation(rotation)) return;
            bool teleportRotation = m_TeleportRotationPending &&
                                    m_TeleportRotationPendingFrame == Time.frameCount;
            if (!teleportRotation &&
                ShouldIgnoreExternalRootWrite("SetRotation", this.Transform.position, rotation))
            {
                return;
            }

            m_InterpolatedRotation = rotation;
            ApplyAuthoritativeRootPose(
                this.Transform.position,
                rotation,
                preserveVisiblePose: false);
            m_VisualPresentation?.ResetOffset();
            m_TeleportRotationPending = false;
            m_TeleportRotationPendingFrame = -1;
        }

        public override void SetScale(Vector3 scale)
        {
            if (!NetworkCharacterVisualPresentation.IsFinite(scale)) return;
            this.Transform.localScale = scale;
            Physics.SyncTransforms();
        }

        public override void AddPosition(Vector3 amount)
        {
            // Remote characters shouldn't have position added locally
            // Ignore this call
        }

        public override void AddRotation(Quaternion amount)
        {
            // Remote characters shouldn't have rotation added locally
            // Ignore this call
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
            // No-op for remote characters
        }
    }

    /// <summary>
    /// Creates a temporary render-only frame around GC2's direct-child Mannequin. The Character
    /// root, CharacterController and every collider remain outside this hierarchy and therefore
    /// stay on the latest authoritative pose. This class deliberately has no transport SDK
    /// dependency so the built-in prediction backend remains usable without PurrDiction.
    /// </summary>
    internal sealed class NetworkCharacterVisualPresentation : IDisposable
    {
        private readonly Character m_Character;
        private readonly string m_OwnerName;

        private Transform m_CharacterRoot;
        private Transform m_PresentationRoot;
        private Transform m_VisualRoot;
        private int m_OriginalSiblingIndex = -1;
        private Vector3 m_WorldPosition;
        private Quaternion m_WorldRotation = Quaternion.identity;
        private bool m_HasWorldPose;
        private bool m_BeforeRenderSubscribed;
        private bool m_WarningIssued;

        private bool m_TransitionActive;
        private Vector3 m_TransitionFromPosition;
        private Quaternion m_TransitionFromRotation = Quaternion.identity;
        private float m_TransitionElapsed;
        private float m_TransitionDuration;
        private readonly List<MonoBehaviour> m_BehaviourBuffer = new List<MonoBehaviour>(16);

        public NetworkCharacterVisualPresentation(Character character, string ownerName)
        {
            m_Character = character;
            m_OwnerName = ownerName;
            m_CharacterRoot = character != null ? character.transform : null;
        }

        public bool TryEnsure(bool logWarning)
        {
            if (m_Character == null) return false;
            m_CharacterRoot = m_Character.transform;

            Transform candidate = m_Character.Animim?.Mannequin;
            if (m_PresentationRoot != null)
            {
                if (m_VisualRoot == candidate &&
                    m_VisualRoot != null &&
                    m_VisualRoot.parent == m_PresentationRoot &&
                    m_PresentationRoot.parent == m_CharacterRoot &&
                    IsSafeVisualSubtree(m_VisualRoot))
                {
                    return true;
                }

                RestoreHierarchy();
            }

            if (!IsSafeVisualRoot(m_CharacterRoot, candidate))
            {
                if (logWarning && !m_WarningIssued)
                {
                    m_WarningIssued = true;
                    Debug.LogWarning(
                        $"[NetworkCharacterPresentation][{m_OwnerName}] " +
                        $"'{m_Character.name}' has no safe direct-child GC2 Mannequin. " +
                        "Its authoritative Character root will remain tick-accurate, but " +
                        "visual interpolation is disabled. Keep colliders, rigidbodies and " +
                        "CharacterControllers or networking behaviours outside the Mannequin " +
                        "hierarchy.",
                        m_Character);
                }

                return false;
            }

            m_OriginalSiblingIndex = candidate.GetSiblingIndex();
            GameObject presentationObject = new GameObject("__NetworkCharacterPresentation");
            presentationObject.hideFlags = HideFlags.HideInHierarchy | HideFlags.DontSave;
            m_PresentationRoot = presentationObject.transform;
            m_PresentationRoot.SetParent(m_CharacterRoot, false);
            m_PresentationRoot.SetSiblingIndex(m_OriginalSiblingIndex);
            m_PresentationRoot.localPosition = Vector3.zero;
            m_PresentationRoot.localRotation = Quaternion.identity;
            m_PresentationRoot.localScale = Vector3.one;

            m_VisualRoot = candidate;
            m_VisualRoot.SetParent(m_PresentationRoot, false);

            // Merely creating the wrapper must not opt it into absolute world-pose holding.
            // Until ApplyWorldPose is called, the presentation follows the Character root as
            // an ordinary child. Otherwise onBeforeRender can pin a newly wrapped local owner
            // to the world pose captured on the first reconciliation frame.
            ClearCachedWorldPose();

            if (!m_BeforeRenderSubscribed)
            {
                Application.onBeforeRender += ReapplyWorldPose;
                m_BeforeRenderSubscribed = true;
            }

            return true;
        }

        public bool TryGetWorldPose(out Vector3 position, out Quaternion rotation)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            if (m_PresentationRoot == null) return false;

            position = m_PresentationRoot.position;
            rotation = m_PresentationRoot.rotation;
            return HasUsablePose(position, rotation);
        }

        public bool ApplyWorldPose(Vector3 position, Quaternion rotation)
        {
            if (m_PresentationRoot == null || !HasUsablePose(position, rotation))
            {
                return false;
            }

            m_PresentationRoot.SetPositionAndRotation(position, rotation);
            m_WorldPosition = position;
            m_WorldRotation = rotation;
            m_HasWorldPose = true;
            return true;
        }

        public void BeginRootStepTransition(
            Vector3 visiblePosition,
            Quaternion visibleRotation,
            float duration,
            float snapDistance)
        {
            if (m_PresentationRoot == null || m_CharacterRoot == null ||
                !HasUsablePose(visiblePosition, visibleRotation) ||
                !HasUsablePose(m_CharacterRoot.position, m_CharacterRoot.rotation))
            {
                ResetOffset();
                return;
            }

            float distance = Vector3.Distance(visiblePosition, m_CharacterRoot.position);
            if (distance > Mathf.Max(0.01f, snapDistance))
            {
                ResetOffset();
                return;
            }

            m_TransitionFromPosition = visiblePosition;
            m_TransitionFromRotation = visibleRotation;
            m_TransitionElapsed = 0f;
            m_TransitionDuration = Mathf.Max(0.001f, duration);
            m_TransitionActive = distance > 0.0001f ||
                                 Quaternion.Angle(visibleRotation, m_CharacterRoot.rotation) > 0.01f;

            if (m_TransitionActive) ApplyWorldPose(visiblePosition, visibleRotation);
            else ResetOffset();
        }

        public void UpdateRootStepTransition(float deltaTime)
        {
            if (m_PresentationRoot == null || m_CharacterRoot == null)
            {
                return;
            }

            if (!m_TransitionActive)
            {
                // External GC2 motion (Traversal, support motion, facing) already advances at
                // render rate. Keep the wrapper at identity so it inherits that root motion.
                ResetOffset();
                return;
            }

            if (!IsFinite(deltaTime) || deltaTime < 0f ||
                !HasUsablePose(m_CharacterRoot.position, m_CharacterRoot.rotation))
            {
                ResetOffset();
                return;
            }

            m_TransitionElapsed += deltaTime;
            float t = Mathf.Clamp01(m_TransitionElapsed / m_TransitionDuration);
            Vector3 position = Vector3.Lerp(
                m_TransitionFromPosition,
                m_CharacterRoot.position,
                t);
            Quaternion rotation = Quaternion.Slerp(
                m_TransitionFromRotation,
                m_CharacterRoot.rotation,
                t);

            if (!ApplyWorldPose(position, rotation) || t >= 1f)
            {
                ResetOffset();
            }
        }

        public void ResetOffset()
        {
            m_TransitionActive = false;
            m_TransitionElapsed = 0f;
            ClearCachedWorldPose();
            if (m_PresentationRoot == null) return;

            m_PresentationRoot.localPosition = Vector3.zero;
            m_PresentationRoot.localRotation = Quaternion.identity;
            m_PresentationRoot.localScale = Vector3.one;

            // Reset means "follow the Character root". Leaving m_HasWorldPose set here causes
            // Application.onBeforeRender to restore this frame's now-stale absolute pose after
            // every later prediction update, visually freezing only the owning client's model.
        }

        public void Dispose()
        {
            RestoreHierarchy();
        }

        private void ReapplyWorldPose()
        {
            if (!m_HasWorldPose || m_PresentationRoot == null ||
                !HasUsablePose(m_WorldPosition, m_WorldRotation))
            {
                return;
            }

            m_PresentationRoot.SetPositionAndRotation(m_WorldPosition, m_WorldRotation);
        }

        private void ClearCachedWorldPose()
        {
            m_HasWorldPose = false;
            m_WorldPosition = Vector3.zero;
            m_WorldRotation = Quaternion.identity;
        }

        private void RestoreHierarchy()
        {
            if (m_BeforeRenderSubscribed)
            {
                Application.onBeforeRender -= ReapplyWorldPose;
                m_BeforeRenderSubscribed = false;
            }

            m_TransitionActive = false;
            m_HasWorldPose = false;
            if (m_PresentationRoot == null) return;

            // Clear every render offset before putting the authored Mannequin hierarchy back.
            m_PresentationRoot.localPosition = Vector3.zero;
            m_PresentationRoot.localRotation = Quaternion.identity;
            m_PresentationRoot.localScale = Vector3.one;
            if (m_VisualRoot != null && m_CharacterRoot != null)
            {
                m_VisualRoot.SetParent(m_CharacterRoot, false);
                if (m_OriginalSiblingIndex >= 0 && m_CharacterRoot.childCount > 0)
                {
                    m_VisualRoot.SetSiblingIndex(Mathf.Min(
                        m_OriginalSiblingIndex,
                        m_CharacterRoot.childCount - 1));
                }
            }

            GameObject presentationObject = m_PresentationRoot.gameObject;
            m_PresentationRoot = null;
            m_VisualRoot = null;
            m_OriginalSiblingIndex = -1;
            if (Application.isPlaying) UnityEngine.Object.Destroy(presentationObject);
            else UnityEngine.Object.DestroyImmediate(presentationObject);
        }

        private bool IsSafeVisualRoot(Transform characterRoot, Transform candidate)
        {
            if (characterRoot == null || candidate == null || candidate == characterRoot)
            {
                return false;
            }

            if (candidate.parent != characterRoot) return false;
            return IsSafeVisualSubtree(candidate);
        }

        private bool IsSafeVisualSubtree(Transform candidate)
        {
            if (candidate == null) return false;
            if (candidate.GetComponentInChildren<CharacterController>(true) != null) return false;
            if (candidate.GetComponentInChildren<Rigidbody>(true) != null) return false;
            if (candidate.GetComponentInChildren<Collider>(true) != null) return false;
            if (candidate.GetComponentInChildren<NetworkCharacter>(true) != null) return false;
            if (ContainsNetworkBehaviour(candidate)) return false;

            return candidate.GetComponentInChildren<Renderer>(true) != null ||
                   candidate.GetComponentInChildren<Animator>(true) != null;
        }

        private bool ContainsNetworkBehaviour(Transform candidate)
        {
            m_BehaviourBuffer.Clear();
            candidate.GetComponentsInChildren(true, m_BehaviourBuffer);
            for (int i = 0; i < m_BehaviourBuffer.Count; i++)
            {
                MonoBehaviour behaviour = m_BehaviourBuffer[i];
                if (behaviour == null) continue;

                for (Type type = behaviour.GetType();
                     type != null && type != typeof(MonoBehaviour);
                     type = type.BaseType)
                {
                    string name = type.Name;
                    if (name.Equals("NetworkBehaviour", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("NetworkBehavior", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("NetworkIdentity", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("NetworkObject", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("NetworkTransform", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }

                    // Transport SDKs consistently place their synchronization components in a
                    // transport namespace. Reject network-named behaviours from those namespaces
                    // without taking a compile-time dependency on any SDK.
                    string typeNamespace = type.Namespace ?? string.Empty;
                    if (name.IndexOf("Network", StringComparison.OrdinalIgnoreCase) >= 0 &&
                        IsKnownNetworkNamespace(typeNamespace))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool IsKnownNetworkNamespace(string typeNamespace)
        {
            return typeNamespace.Equals("Fusion", StringComparison.Ordinal) ||
                   typeNamespace.StartsWith("Fusion.", StringComparison.Ordinal) ||
                   typeNamespace.Equals("PurrNet", StringComparison.Ordinal) ||
                   typeNamespace.StartsWith("PurrNet.", StringComparison.Ordinal) ||
                   typeNamespace.Equals("Mirror", StringComparison.Ordinal) ||
                   typeNamespace.StartsWith("Mirror.", StringComparison.Ordinal) ||
                   typeNamespace.Equals("FishNet", StringComparison.Ordinal) ||
                   typeNamespace.StartsWith("FishNet.", StringComparison.Ordinal) ||
                   typeNamespace.Equals("Photon", StringComparison.Ordinal) ||
                   typeNamespace.StartsWith("Photon.", StringComparison.Ordinal) ||
                   typeNamespace.Equals("Unity.Netcode", StringComparison.Ordinal) ||
                   typeNamespace.StartsWith("Unity.Netcode.", StringComparison.Ordinal);
        }

        public static bool HasUsablePose(Vector3 position, Quaternion rotation)
        {
            return IsFinite(position) && IsUsableRotation(rotation);
        }

        public static bool IsUsableRotation(Quaternion value)
        {
            return IsFinite(value) && value.x * value.x + value.y * value.y +
                   value.z * value.z + value.w * value.w > 0.000001f;
        }

        public static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        public static bool IsFinite(Vector3 value)
        {
            return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
        }

        public static bool IsFinite(Quaternion value)
        {
            return IsFinite(value.x) && IsFinite(value.y) &&
                   IsFinite(value.z) && IsFinite(value.w);
        }
    }
}
