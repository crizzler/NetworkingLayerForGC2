using System;
using UnityEngine;

namespace Arawn.GameCreator2.Networking
{
    /// <summary>
    /// Network-agnostic camera state synchronization.
    /// Captures the local camera's offset from the player and broadcasts it
    /// to remote clients so they can display an approximate camera representation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// On the owner, this component reads the Main Camera's local-space offset
    /// relative to the player every <see cref="m_SyncRate"/> seconds and invokes
    /// <see cref="OnCameraStateBroadcast"/>. Your networking solution should
    /// relay this to remote clients.
    /// </para>
    /// <para>
    /// On remote clients, call <see cref="ApplyRemoteCameraState"/> from
    /// your networking callback. The component smoothly interpolates a
    /// "camera indicator" transform to the received state.
    /// </para>
    /// </remarks>
    [AddComponentMenu("Game Creator/Network/Camera Sync")]
    [DisallowMultipleComponent]
    public class NetworkCameraSync : MonoBehaviour
    {
        // ════════════════════════════════════════════════════════════════════
        // NESTED TYPES
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Camera state payload for network transport.
        /// </summary>
        [Serializable]
        public struct CameraState
        {
            /// <summary>Camera position relative to the player.</summary>
            public Vector3 LocalPosition;

            /// <summary>Camera rotation relative to the player.</summary>
            public Quaternion LocalRotation;

            /// <summary>Camera field of view.</summary>
            public float FieldOfView;

            /// <summary>Server timestamp for interpolation ordering.</summary>
            public float Timestamp;
        }

        // ════════════════════════════════════════════════════════════════════
        // INSPECTOR
        // ════════════════════════════════════════════════════════════════════

        [Header("Owner Settings")]
        [Tooltip("How often to broadcast camera state (seconds)")]
        [SerializeField] private float m_SyncRate = 0.1f;

        [Tooltip("Minimum position change (units) to trigger a broadcast")]
        [SerializeField] private float m_PositionThreshold = 0.01f;

        [Tooltip("Minimum rotation change (degrees) to trigger a broadcast")]
        [SerializeField] private float m_RotationThreshold = 0.5f;

        [Header("Remote Settings")]
        [Tooltip("Transform to move as the remote camera indicator (optional)")]
        [SerializeField] private Transform m_CameraIndicator;

        [Tooltip("Interpolation speed for the camera indicator")]
        [SerializeField] private float m_InterpolationSpeed = 10f;

        [Header("Role")]
        [SerializeField] private bool m_IsOwner;

        [Header("Debug")]
        [SerializeField] private bool m_DebugMode;

        // ════════════════════════════════════════════════════════════════════
        // FIELDS
        // ════════════════════════════════════════════════════════════════════

        private float m_LastSyncTime;
        private CameraState m_LastBroadcastState;
        private CameraState m_TargetRemoteState;
        private bool m_HasRemoteState;

        // ════════════════════════════════════════════════════════════════════
        // EVENTS
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Raised when the owner's camera state should be broadcast.
        /// Hook this to your networking solution.
        /// </summary>
        public event Action<CameraState> OnCameraStateBroadcast;

        // ════════════════════════════════════════════════════════════════════
        // PROPERTIES
        // ════════════════════════════════════════════════════════════════════

        public bool IsOwner
        {
            get => m_IsOwner;
            set => m_IsOwner = value;
        }

        /// <summary>The last known camera state (owner: local, remote: received).</summary>
        public CameraState LastState => m_IsOwner ? m_LastBroadcastState : m_TargetRemoteState;

        /// <summary>Camera indicator transform for remote clients.</summary>
        public Transform CameraIndicator
        {
            get => m_CameraIndicator;
            set => m_CameraIndicator = value;
        }

        // ════════════════════════════════════════════════════════════════════
        // LIFECYCLE
        // ════════════════════════════════════════════════════════════════════

        private void LateUpdate()
        {
            if (m_IsOwner)
            {
                UpdateOwnerBroadcast();
            }
            else if (m_HasRemoteState && m_CameraIndicator != null)
            {
                InterpolateRemoteState();
            }
        }

        // ════════════════════════════════════════════════════════════════════
        // OWNER LOGIC
        // ════════════════════════════════════════════════════════════════════

        private void UpdateOwnerBroadcast()
        {
            if (Time.time - m_LastSyncTime < m_SyncRate) return;

            Camera cam = Camera.main;
            if (cam == null) return;

            var camTransform = cam.transform;

            // Compute local-space offset from this player
            Vector3 localPos = transform.InverseTransformPoint(camTransform.position);
            Quaternion localRot = Quaternion.Inverse(transform.rotation) * camTransform.rotation;

            // Check thresholds
            float posDelta = Vector3.Distance(localPos, m_LastBroadcastState.LocalPosition);
            float rotDelta = Quaternion.Angle(localRot, m_LastBroadcastState.LocalRotation);

            if (posDelta < m_PositionThreshold && rotDelta < m_RotationThreshold) return;

            var state = new CameraState
            {
                LocalPosition = localPos,
                LocalRotation = localRot,
                FieldOfView = cam.fieldOfView,
                Timestamp = Time.time
            };

            m_LastBroadcastState = state;
            m_LastSyncTime = Time.time;

            if (m_DebugMode)
                Debug.Log($"[CameraSync] Broadcasting: pos={localPos}, rot={localRot.eulerAngles}");

            OnCameraStateBroadcast?.Invoke(state);
        }

        // ════════════════════════════════════════════════════════════════════
        // REMOTE RECEIVE
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Apply a camera state received from the network.
        /// Call this from your networking solution's RPC handler on remote clients.
        /// </summary>
        public void ApplyRemoteCameraState(CameraState state)
        {
            m_TargetRemoteState = state;
            m_HasRemoteState = true;

            if (m_DebugMode)
                Debug.Log($"[CameraSync] Received: pos={state.LocalPosition}");
        }

        /// <summary>
        /// Get the current camera state for sending to a late joiner.
        /// Call on the owner.
        /// </summary>
        public CameraState GetCurrentState()
        {
            if (m_IsOwner)
            {
                Camera cam = Camera.main;
                if (cam != null)
                {
                    return new CameraState
                    {
                        LocalPosition = transform.InverseTransformPoint(cam.transform.position),
                        LocalRotation = Quaternion.Inverse(transform.rotation) * cam.transform.rotation,
                        FieldOfView = cam.fieldOfView,
                        Timestamp = Time.time
                    };
                }
            }

            return m_LastBroadcastState;
        }

        // ════════════════════════════════════════════════════════════════════
        // INTERPOLATION
        // ════════════════════════════════════════════════════════════════════

        private void InterpolateRemoteState()
        {
            // Convert local-space target back to world-space
            Vector3 targetWorldPos = transform.TransformPoint(m_TargetRemoteState.LocalPosition);
            Quaternion targetWorldRot = transform.rotation * m_TargetRemoteState.LocalRotation;

            float t = m_InterpolationSpeed * Time.deltaTime;
            m_CameraIndicator.position = Vector3.Lerp(m_CameraIndicator.position, targetWorldPos, t);
            m_CameraIndicator.rotation = Quaternion.Slerp(m_CameraIndicator.rotation, targetWorldRot, t);
        }
    }
}
