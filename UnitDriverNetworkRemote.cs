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

        // MEMBERS: -------------------------------------------------------------------------------

        [NonSerialized] protected CharacterController m_Controller;
        [NonSerialized] protected Vector3 m_MoveDirection;
        
        // Interpolation state
        [NonSerialized] private List<PositionSnapshot> m_SnapshotBuffer;
        [NonSerialized] private Vector3 m_InterpolatedPosition;
        [NonSerialized] private Quaternion m_InterpolatedRotation;
        [NonSerialized] private float m_ServerTime;
        [NonSerialized] private float m_LastSnapshotTime;
        [NonSerialized] private bool m_IsExtrapolating;

        // INTERFACE PROPERTIES: ------------------------------------------------------------------

        public override Vector3 WorldMoveDirection => this.m_MoveDirection;
        public override Vector3 LocalMoveDirection => this.Transform.InverseTransformDirection(this.m_MoveDirection);
        public override float SkinWidth => this.m_Controller != null ? this.m_Controller.skinWidth : 0f;
        public override bool IsGrounded => true; // Remote characters don't need local ground checks
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
            
            if (this.m_Controller != null)
            {
                UnityEngine.Object.Destroy(this.m_Controller);
            }
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
            
            // Check for teleport
            if (m_SnapshotBuffer.Count > 0)
            {
                Vector3 lastPos = m_SnapshotBuffer[m_SnapshotBuffer.Count - 1].position;
                if (Vector3.Distance(position, lastPos) > m_SnapDistance)
                {
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
            
            m_LastSnapshotTime = serverTimestamp;
            
            // Trim old snapshots
            float minTime = serverTimestamp - 1f; // Keep 1 second of history
            while (m_SnapshotBuffer.Count > 2 && m_SnapshotBuffer[0].timestamp < minTime)
            {
                m_SnapshotBuffer.RemoveAt(0);
            }
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
