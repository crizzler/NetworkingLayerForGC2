using System;
using GameCreator.Runtime.Cameras;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Common;
using UnityEngine;

namespace Arawn.GameCreator2.Networking
{
    /// <summary>
    /// Network facing unit that uses another object's forward direction as the desired yaw.
    /// Useful for shooter controllers that should face the camera or aim object while strafing.
    /// </summary>
    [Title("Network Object Direction (Server-Authoritative)")]
    [Image(typeof(IconCubeSolid), ColorTheme.Type.Blue, typeof(OverlayArrowRight))]

    [Category("Network/Network Object Direction")]
    [Description("Server-authoritative facing that syncs the yaw of another object's direction. " +
                 "Use for shooter-style characters that face the camera or aim object while moving.")]

    [Serializable]
    public class UnitFacingNetworkObjectDirection : TUnitFacing, INetworkFacingUnit
    {
        // EXPOSED MEMBERS: -----------------------------------------------------------------------

        [SerializeField]
        private PropertyGetGameObject m_DirectionOf = GetGameObjectCameraMain.Create;

        [Header("Network Settings")]
        [Tooltip("How quickly clients interpolate to the server's facing direction")]
        [SerializeField] private float m_InterpolationSpeed = 15f;

        [Tooltip("Minimum angle change (degrees) before sending an update")]
        [SerializeField] private float m_MinAngleChange = 1f;

        [Tooltip("If enabled, the server evaluates Direction Of directly. Leave disabled for player camera-driven facing on dedicated servers.")]
        [SerializeField] private bool m_ServerEvaluatesDirectionObject;

        // MEMBERS: -------------------------------------------------------------------------------

        [NonSerialized] private NetworkCharacter m_NetworkCharacter;
        [NonSerialized] private float m_ServerYaw;
        [NonSerialized] private float m_ClientYaw;
        [NonSerialized] private float m_LastSentYaw;
        [NonSerialized] private bool m_IsNetworkInitialized;

        // PROPERTIES: ----------------------------------------------------------------------------

        public override Axonometry Axonometry
        {
            get => null;
            set => _ = value;
        }

        /// <summary>
        /// The server-authoritative yaw angle in degrees.
        /// </summary>
        public float ServerYaw => m_ServerYaw;

        /// <summary>
        /// The current interpolated yaw angle on this client.
        /// </summary>
        public float ClientYaw => m_ClientYaw;

        /// <summary>
        /// Whether this facing unit is network-initialized and ready.
        /// </summary>
        public bool IsNetworkInitialized => m_IsNetworkInitialized;

        // INITIALIZATION: ------------------------------------------------------------------------

        public override void OnStartup(Character character)
        {
            base.OnStartup(character);

            m_ServerYaw = character.transform.eulerAngles.y;
            m_ClientYaw = m_ServerYaw;
            m_LastSentYaw = m_ServerYaw;

            m_NetworkCharacter = character.GetComponent<NetworkCharacter>();

            if (m_NetworkCharacter != null)
            {
                m_NetworkCharacter.OnFacingUnitRegistered(this);
                m_IsNetworkInitialized = true;
            }
            else
            {
                Debug.LogWarning($"[UnitFacingNetworkObjectDirection] No NetworkCharacter found on {character.name}. " +
                                 "Falling back to local-only facing.");
            }
        }

        public override void OnDispose(Character character)
        {
            if (m_NetworkCharacter != null)
            {
                m_NetworkCharacter.OnFacingUnitUnregistered();
            }

            base.OnDispose(character);
        }

        // UPDATE: --------------------------------------------------------------------------------

        public override void OnUpdate()
        {
            if (Character.IsDead) return;

            if (m_IsNetworkInitialized && m_NetworkCharacter != null)
            {
                UpdateNetworked();
            }
            else
            {
                UpdateLocal();
            }
        }

        private void UpdateLocal()
        {
            Vector3 direction = GetLocalDirection();
            m_ServerYaw = DirectionToYaw(direction);
            m_ClientYaw = m_ServerYaw;

            base.OnUpdate();
        }

        private void UpdateNetworked()
        {
            var role = m_NetworkCharacter.CurrentRole;

            switch (role)
            {
                case NetworkCharacter.NetworkRole.Server:
                    UpdateAsServer();
                    break;

                case NetworkCharacter.NetworkRole.LocalClient:
                    UpdateAsLocalClient();
                    break;

                case NetworkCharacter.NetworkRole.RemoteClient:
                    UpdateAsRemoteClient();
                    break;

                default:
                    UpdateLocal();
                    break;
            }
        }

        private void UpdateAsServer()
        {
            if (m_ServerEvaluatesDirectionObject)
            {
                Vector3 direction = GetLocalDirection();
                float targetYaw = DirectionToYaw(direction);

                m_ServerYaw = Mathf.LerpAngle(
                    m_ServerYaw,
                    targetYaw,
                    Character.Motion.AngularSpeed * Character.Time.DeltaTime / 360f
                );
            }
            else
            {
                m_ServerYaw = Transform.eulerAngles.y;
            }

            m_ClientYaw = m_ServerYaw;
            m_LastSentYaw = m_ServerYaw;

            ApplyRotation(m_ServerYaw);
        }

        private void UpdateAsLocalClient()
        {
            Vector3 direction = GetLocalDirection();
            float desiredYaw = DirectionToYaw(direction);

            float requestedDelta = Mathf.Abs(Mathf.DeltaAngle(m_LastSentYaw, desiredYaw));
            // Pure clients transport object-facing yaw through NetworkInputState.rotationY.
            // Keep m_ServerYaw as the last server-confirmed value.
            if (m_NetworkCharacter.IsServerInstance && requestedDelta >= m_MinAngleChange)
            {
                m_LastSentYaw = desiredYaw;
                m_NetworkCharacter.RequestFacingUpdate(desiredYaw);
            }

            m_ClientYaw = desiredYaw;
            ApplyRotation(m_ClientYaw);
        }

        private void UpdateAsRemoteClient()
        {
            m_ClientYaw = Mathf.LerpAngle(
                m_ClientYaw,
                m_ServerYaw,
                m_InterpolationSpeed * Character.Time.DeltaTime
            );

            ApplyRotation(m_ClientYaw);
        }

        // NETWORK CALLBACKS: ---------------------------------------------------------------------

        /// <summary>
        /// Called by NetworkCharacter when receiving a facing update from the server.
        /// </summary>
        public void OnServerYawReceived(float yaw)
        {
            m_ServerYaw = yaw;
        }

        /// <summary>
        /// Called by NetworkCharacter when a local owner requests a facing change.
        /// </summary>
        public float ValidateFacingRequest(float requestedYaw)
        {
            float maxDelta = Character.Motion.AngularSpeed * Character.Time.DeltaTime;
            float currentYaw = m_ServerYaw;
            float delta = Mathf.DeltaAngle(currentYaw, requestedYaw);

            delta = Mathf.Clamp(delta, -maxDelta, maxDelta);

            m_ServerYaw = currentYaw + delta;
            return m_ServerYaw;
        }

        /// <summary>
        /// Forces the facing to a specific yaw angle.
        /// </summary>
        public void ForceServerYaw(float yaw)
        {
            m_ServerYaw = yaw;
            m_ClientYaw = yaw;
            m_LastSentYaw = yaw;
        }

        // PROTECTED METHODS: ---------------------------------------------------------------------

        protected override Vector3 GetDefaultDirection()
        {
            return Quaternion.Euler(0f, m_ClientYaw, 0f) * Vector3.forward;
        }

        // PRIVATE METHODS: -----------------------------------------------------------------------

        private Vector3 GetLocalDirection()
        {
            GameObject gameObject = m_DirectionOf.Get(Character.gameObject);
            Vector3 driverDirection = gameObject != null
                ? Vector3.Scale(gameObject.transform.forward, Vector3Plane.NormalUp)
                : Vector3.zero;

            return DecideDirection(driverDirection);
        }

        private static float DirectionToYaw(Vector3 direction)
        {
            return Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg;
        }

        private void ApplyRotation(float yaw)
        {
            Quaternion targetRotation = Quaternion.Euler(0f, yaw, 0f);
            Quaternion sourceRotation = Transform.rotation;

            m_FaceDirection = targetRotation * Vector3.forward;
            m_PivotSpeed = Vector3.SignedAngle(
                sourceRotation * Vector3.forward,
                m_FaceDirection,
                Vector3.up
            );

            Transform.rotation = Quaternion.Lerp(
                targetRotation,
                sourceRotation * Character.Animim.RootMotionDeltaRotation,
                Character.RootMotionRotation
            );
        }

        // STRING: --------------------------------------------------------------------------------

        public override string ToString() => "Network Object Direction";
    }
}
