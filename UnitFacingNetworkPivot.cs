using System;
using UnityEngine;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Common;

#if UNITY_NETCODE
using Unity.Netcode;
#endif

namespace Arawn.GameCreator2.Networking
{
    /// <summary>
    /// Server-authoritative facing unit for networked characters.
    /// The server validates and broadcasts facing direction changes to all clients.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This facing unit should be used instead of UnitFacingPivot when you need
    /// server-authoritative control over character facing direction. This is important
    /// for mechanics like backstab damage, cone attacks, or any gameplay where
    /// facing direction affects outcomes.
    /// </para>
    /// <para>
    /// The server calculates and validates the facing direction, then broadcasts it
    /// to all clients. Clients interpolate smoothly between received values.
    /// </para>
    /// </remarks>
    [Title("Network Pivot (Server-Authoritative)")]
    [Image(typeof(IconRotationYaw), ColorTheme.Type.Blue)]
    
    [Category("Network/Network Pivot")]
    [Description("Server-authoritative facing that syncs across the network. " +
                 "Use for characters where facing direction affects gameplay (backstabs, cone attacks, etc.)")]
    
    [Serializable]
    public class UnitFacingNetworkPivot : TUnitFacing
    {
        private enum DirectionFrom
        {
            MotionDirection,
            DriverDirection
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // EXPOSED MEMBERS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        [SerializeField] private DirectionFrom m_DirectionFrom = DirectionFrom.MotionDirection;
        [SerializeField] private Axonometry m_Axonometry = new Axonometry();
        
#if UNITY_NETCODE
        [Header("Network Settings")]
        [Tooltip("How quickly clients interpolate to the server's facing direction")]
        [SerializeField] private float m_InterpolationSpeed = 15f;
        
        [Tooltip("Minimum angle change (degrees) before sending an update")]
        [SerializeField] private float m_MinAngleChange = 1f;
#endif
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // PRIVATE MEMBERS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        [NonSerialized] private NetworkCharacter m_NetworkCharacter;
        [NonSerialized] private float m_ServerYaw;
        [NonSerialized] private float m_ClientYaw;
        [NonSerialized] private float m_LastSentYaw;
        [NonSerialized] private bool m_IsNetworkInitialized;
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // PROPERTIES
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        public override Axonometry Axonometry
        {
            get => m_Axonometry;
            set => m_Axonometry = value;
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
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // INITIALIZATION
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        public override void OnStartup(Character character)
        {
            base.OnStartup(character);
            
            // Initialize yaw from current rotation
            m_ServerYaw = character.transform.eulerAngles.y;
            m_ClientYaw = m_ServerYaw;
            m_LastSentYaw = m_ServerYaw;
            
            // Try to find NetworkCharacter
            m_NetworkCharacter = character.GetComponent<NetworkCharacter>();
            
            if (m_NetworkCharacter != null)
            {
                m_NetworkCharacter.OnFacingUnitRegistered(this);
                m_IsNetworkInitialized = true;
            }
            else
            {
                Debug.LogWarning($"[UnitFacingNetworkPivot] No NetworkCharacter found on {character.name}. " +
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
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // UPDATE
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        public override void OnUpdate()
        {
            if (Character.IsDead) return;
            
#if UNITY_NETCODE
            if (m_IsNetworkInitialized && m_NetworkCharacter != null)
            {
                UpdateNetworked();
            }
            else
            {
                UpdateLocal();
            }
#else
            UpdateLocal();
#endif
        }
        
        private void UpdateLocal()
        {
            // Fallback: behave like regular UnitFacingPivot
            Vector3 direction = GetLocalDirection();
            m_ServerYaw = Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg;
            m_ClientYaw = m_ServerYaw;
            
            base.OnUpdate();
        }
        
#if UNITY_NETCODE
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
            // Server calculates authoritative facing direction
            Vector3 direction = GetLocalDirection();
            float targetYaw = Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg;
            
            // Smooth server-side rotation
            m_ServerYaw = Mathf.LerpAngle(m_ServerYaw, targetYaw, 
                Character.Motion.AngularSpeed * Character.Time.DeltaTime / 360f);
            m_ClientYaw = m_ServerYaw;
            
            // Check if we need to broadcast update
            float angleDelta = Mathf.Abs(Mathf.DeltaAngle(m_LastSentYaw, m_ServerYaw));
            if (angleDelta >= m_MinAngleChange)
            {
                m_LastSentYaw = m_ServerYaw;
                // NetworkCharacter will handle broadcasting via NetworkVariable
            }
            
            // Apply rotation
            ApplyRotation(m_ServerYaw);
        }
        
        private void UpdateAsLocalClient()
        {
            // Local client: calculate desired direction and send to server
            Vector3 direction = GetLocalDirection();
            float desiredYaw = Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg;
            
            // Send request to server if angle changed significantly
            float angleDelta = Mathf.Abs(Mathf.DeltaAngle(m_LastSentYaw, desiredYaw));
            if (angleDelta >= m_MinAngleChange)
            {
                m_LastSentYaw = desiredYaw;
                m_NetworkCharacter.RequestFacingUpdate(desiredYaw);
            }
            
            // Interpolate toward server yaw for smooth visuals
            m_ClientYaw = Mathf.LerpAngle(m_ClientYaw, m_ServerYaw, 
                m_InterpolationSpeed * Character.Time.DeltaTime);
            
            // Apply rotation
            ApplyRotation(m_ClientYaw);
        }
        
        private void UpdateAsRemoteClient()
        {
            // Remote client: interpolate toward received server yaw
            m_ClientYaw = Mathf.LerpAngle(m_ClientYaw, m_ServerYaw, 
                m_InterpolationSpeed * Character.Time.DeltaTime);
            
            // Apply rotation
            ApplyRotation(m_ClientYaw);
        }
#endif
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // NETWORK CALLBACKS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Called by NetworkCharacter when receiving a facing update from the server.
        /// </summary>
        /// <param name="yaw">The new server-authoritative yaw angle in degrees.</param>
        public void OnServerYawReceived(float yaw)
        {
            m_ServerYaw = yaw;
        }
        
        /// <summary>
        /// Called by NetworkCharacter on the server when a client requests a facing change.
        /// </summary>
        /// <param name="requestedYaw">The yaw angle the client wants to face.</param>
        /// <returns>The validated yaw angle (may be same as requested or adjusted).</returns>
        public float ValidateFacingRequest(float requestedYaw)
        {
            // Server can validate/modify the requested yaw here
            // For example: clamp rotation speed, check for cheating, etc.
            
            // Calculate max rotation delta based on angular speed
            float maxDelta = Character.Motion.AngularSpeed * Character.Time.DeltaTime;
            float currentYaw = m_ServerYaw;
            float delta = Mathf.DeltaAngle(currentYaw, requestedYaw);
            
            // Clamp to max rotation speed
            delta = Mathf.Clamp(delta, -maxDelta, maxDelta);
            
            // Apply validated rotation
            m_ServerYaw = currentYaw + delta;
            return m_ServerYaw;
        }
        
        /// <summary>
        /// Forces the facing to a specific yaw angle. Server-only.
        /// </summary>
        /// <param name="yaw">The yaw angle in degrees.</param>
        public void ForceServerYaw(float yaw)
        {
            m_ServerYaw = yaw;
            m_ClientYaw = yaw;
            m_LastSentYaw = yaw;
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // PROTECTED METHODS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        protected override Vector3 GetDefaultDirection()
        {
            // Return direction based on current client yaw
            return Quaternion.Euler(0f, m_ClientYaw, 0f) * Vector3.forward;
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // PRIVATE METHODS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        private Vector3 GetLocalDirection()
        {
            Vector3 driverDirection = Vector3.Scale(
                m_DirectionFrom switch
                {
                    DirectionFrom.MotionDirection => Character.Motion.MoveDirection,
                    DirectionFrom.DriverDirection => Character.Driver.WorldMoveDirection,
                    _ => throw new ArgumentOutOfRangeException()
                },
                Vector3Plane.NormalUp
            );
            
            Vector3 direction = DecideDirection(driverDirection);
            return m_Axonometry?.ProcessRotation(this, direction) ?? direction;
        }
        
        private void ApplyRotation(float yaw)
        {
            Quaternion targetRotation = Quaternion.Euler(0f, yaw, 0f);
            
            // Apply with root motion blending
            Transform.rotation = Quaternion.Lerp(
                targetRotation,
                Transform.rotation * Character.Animim.RootMotionDeltaRotation,
                Character.RootMotionRotation
            );
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // STRING
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        public override string ToString() => "Network Pivot";
    }
}
