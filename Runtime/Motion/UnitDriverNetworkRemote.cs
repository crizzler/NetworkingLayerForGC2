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
        [SerializeField] private bool m_LogMotionDiagnostics = true;
        [SerializeField] private float m_MotionDiagnosticInterval = 0.5f;

        // MEMBERS: -------------------------------------------------------------------------------

        [NonSerialized] protected CharacterController m_Controller;
        [NonSerialized] protected Vector3 m_MoveDirection;
        
        // Interpolation state
        [NonSerialized] private List<PositionSnapshot> m_SnapshotBuffer;
        [NonSerialized] private Vector3 m_InterpolatedPosition;
        [NonSerialized] private Quaternion m_InterpolatedRotation;
        [NonSerialized] private float m_ServerTime;
        [NonSerialized] private bool m_IsExtrapolating;
        [NonSerialized] private bool m_WasExtrapolating;
        [NonSerialized] private bool m_IsGrounded = true;
        [NonSerialized] private bool m_IsJumping;
        [NonSerialized] private bool m_HasLastReceivedSnapshot;
        [NonSerialized] private float m_LastReceivedServerTimestamp;
        [NonSerialized] private float m_LastReceivedSnapshotRealtime;
        [NonSerialized] private float m_LastMotionDiagnosticRealtime;

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

            m_SnapshotBuffer = new List<PositionSnapshot>(32);
            m_InterpolatedPosition = this.Transform.position;
            m_InterpolatedRotation = this.Transform.rotation;
            m_ServerTime = 0f;
            m_IsGrounded = true;
            m_IsJumping = false;
            m_WasExtrapolating = false;
            m_HasLastReceivedSnapshot = false;
            m_LastReceivedServerTimestamp = 0f;
            m_LastReceivedSnapshotRealtime = -100f;
            m_LastMotionDiagnosticRealtime = -100f;

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
            base.OnDispose(character);
            this.m_Controller = null;
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
            Vector3 position = state.GetPosition();
            float rotationY = state.GetRotationY();
            float realtime = Time.realtimeSinceStartup;

            m_IsGrounded = state.IsGrounded;
            m_IsJumping = state.IsJumping;

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
            }

            // Check for teleport
            if (m_SnapshotBuffer.Count > 0)
            {
                Vector3 lastPos = m_SnapshotBuffer[m_SnapshotBuffer.Count - 1].position;
                float distance = Vector3.Distance(position, lastPos);
                if (distance > m_SnapDistance)
                {
                    LogRemoteMotionDiagnostic(
                        $"remote snap distance={distance:F3} snapDistance={m_SnapDistance:F3} " +
                        $"from={FormatVector(lastPos)} to={FormatVector(position)}",
                        force: true);

                    // Teleport - clear buffer and snap
                    m_SnapshotBuffer.Clear();
                    TeleportTo(position, rotationY);
                }
            }
            
            // Calculate velocity from previous snapshot
            Vector3 velocity = Vector3.zero;
            if (m_SnapshotBuffer.Count > 0)
            {
                var lastSnapshot = m_SnapshotBuffer[m_SnapshotBuffer.Count - 1];
                float timeDelta = serverTimestamp - (float)lastSnapshot.timestamp;
                if (timeDelta > 0.001f)
                {
                    velocity = (position - lastSnapshot.position) / timeDelta;
                }
            }
            
            m_SnapshotBuffer.Add(new PositionSnapshot
            {
                timestamp = serverTimestamp,
                position = position,
                rotation = Quaternion.Euler(0f, rotationY, 0f),
                velocity = velocity,
                rotationY = rotationY,
                verticalVelocity = state.GetVerticalVelocity(),
                flags = state.flags
            });

            // Trim old snapshots
            float minTime = serverTimestamp - 1f; // Keep 1 second of history
            while (m_SnapshotBuffer.Count > 2 && m_SnapshotBuffer[0].timestamp < minTime)
            {
                m_SnapshotBuffer.RemoveAt(0);
            }

            m_HasLastReceivedSnapshot = true;
            m_LastReceivedServerTimestamp = serverTimestamp;
            m_LastReceivedSnapshotRealtime = realtime;
        }

        /// <summary>
        /// Set the current server time for interpolation.
        /// Should be called every frame with synchronized server time.
        /// </summary>
        public void SetServerTime(float serverTime)
        {
            m_ServerTime = serverTime;
        }

        // UPDATE METHOD: -------------------------------------------------------------------------

        public override void OnUpdate()
        {
            if (this.Character == null) return;
            if (this.Character.IsDead) return;

            float deltaTime = this.Character.Time.DeltaTime;
            
            // Calculate render time (with delay for interpolation)
            float renderTime = m_ServerTime - m_InterpolationDelay;
            
            // Interpolate position
            InterpolatePosition(renderTime, deltaTime);

            if (m_IsExtrapolating != m_WasExtrapolating)
            {
                LogRemoteMotionDiagnostic(
                    $"extrapolating {m_WasExtrapolating}->{m_IsExtrapolating} " +
                    $"serverTime={m_ServerTime:F3} renderTime={renderTime:F3} " +
                    $"buffer={m_SnapshotBuffer.Count} delay={m_InterpolationDelay:F3}");
                m_WasExtrapolating = m_IsExtrapolating;
            }

            // Apply interpolated transform
            ApplyInterpolatedTransform();
            
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
                m_InterpolatedPosition = m_SnapshotBuffer[0].position;
                m_InterpolatedRotation = m_SnapshotBuffer[0].rotation;
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
                
                m_InterpolatedPosition = Vector3.Lerp(before.Value.position, after.Value.position, t);
                m_InterpolatedRotation = Quaternion.Slerp(before.Value.rotation, after.Value.rotation, t);
                m_MoveDirection = Vector3.Lerp(before.Value.velocity, after.Value.velocity, t);
                m_IsExtrapolating = false;
            }
            else if (before.HasValue)
            {
                // No future snapshot - extrapolate
                float timeSinceLastSnapshot = (float)(renderTime - before.Value.timestamp);
                
                if (timeSinceLastSnapshot <= m_MaxExtrapolationTime)
                {
                    // Extrapolate using velocity
                    m_InterpolatedPosition = before.Value.position + before.Value.velocity * timeSinceLastSnapshot;
                    m_InterpolatedRotation = before.Value.rotation;
                    m_MoveDirection = before.Value.velocity;
                    m_IsExtrapolating = true;
                }
                else
                {
                    // Too long without update - stop extrapolating
                    m_InterpolatedPosition = before.Value.position + before.Value.velocity * m_MaxExtrapolationTime;
                    m_InterpolatedRotation = before.Value.rotation;
                    m_MoveDirection = Vector3.zero;
                    m_IsExtrapolating = true;
                }
            }
            else if (after.HasValue)
            {
                // Only future snapshot - use it
                m_InterpolatedPosition = after.Value.position;
                m_InterpolatedRotation = after.Value.rotation;
                m_MoveDirection = after.Value.velocity;
                m_IsExtrapolating = false;
            }
        }

        private void ApplyInterpolatedTransform()
        {
            if (m_Controller != null)
            {
                m_Controller.enabled = false;
                this.Transform.position = m_InterpolatedPosition;
                this.Transform.rotation = m_InterpolatedRotation;
                m_Controller.enabled = true;
            }
            else
            {
                this.Transform.position = m_InterpolatedPosition;
                this.Transform.rotation = m_InterpolatedRotation;
            }
        }

        private void TeleportTo(Vector3 position, float rotationY)
        {
            m_InterpolatedPosition = position;
            m_InterpolatedRotation = Quaternion.Euler(0f, rotationY, 0f);
            m_MoveDirection = Vector3.zero;
            
            ApplyInterpolatedTransform();
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

        private static string FormatVector(Vector3 value)
        {
            return $"({value.x:F3},{value.y:F3},{value.z:F3})";
        }

        // STANDARD DRIVER METHODS: ---------------------------------------------------------------

        public override void SetPosition(Vector3 position, bool teleport = false)
        {
            if (teleport)
            {
                m_SnapshotBuffer.Clear();
                TeleportTo(position, this.Transform.eulerAngles.y);
            }
            else
            {
                m_InterpolatedPosition = position;
                ApplyInterpolatedTransform();
            }
        }

        public override void SetRotation(Quaternion rotation)
        {
            m_InterpolatedRotation = rotation;
            ApplyInterpolatedTransform();
        }

        public override void SetScale(Vector3 scale)
        {
            this.Transform.localScale = scale;
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
            this.Transform.localScale = Vector3.Scale(this.Transform.localScale, scale);
        }

        public override void ResetVerticalVelocity()
        {
            // No-op for remote characters
        }
    }
}
