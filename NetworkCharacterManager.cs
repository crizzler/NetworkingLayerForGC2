using System;
using UnityEngine;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.Characters;

namespace Arawn.GameCreator2.Networking
{
    /// <summary>
    /// Coordinates all network character components and handles lifecycle.
    /// Attach this to the Character GameObject to set up networking.
    /// </summary>
    [AddComponentMenu("Game Creator/Characters/Network Character Manager")]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Character))]
    public class NetworkCharacterManager : MonoBehaviour
    {
        // ENUMS: ---------------------------------------------------------------------------------

        public enum CharacterRole
        {
            LocalPlayer,        // This client controls this character
            RemotePlayer,       // Another client controls this character
            ServerAuthority     // Server-only, no local visuals needed
        }

        // EXPOSED MEMBERS: -----------------------------------------------------------------------

        [SerializeField] private CharacterRole m_Role = CharacterRole.LocalPlayer;
        [SerializeField] private NetworkCharacterConfig m_Config = new NetworkCharacterConfig();

        [Header("Events")]
        [SerializeField] private bool m_LogEvents = false;

        // MEMBERS: -------------------------------------------------------------------------------

        [NonSerialized] private Character m_Character;
        [NonSerialized] private UnitDriverNetworkClient m_LocalDriver;
        [NonSerialized] private UnitDriverNetworkRemote m_RemoteDriver;
        [NonSerialized] private UnitPlayerDirectionalNetwork m_LocalPlayer;
        [NonSerialized] private float m_SyncedServerTime;
        [NonSerialized] private bool m_IsInitialized;

        // EVENTS: --------------------------------------------------------------------------------

        /// <summary>
        /// Fired when inputs should be sent to network layer.
        /// Hook this up to your networking solution (Netcode, Mirror, Photon, etc.).
        /// </summary>
        public event Action<NetworkInputState[]> OnSendInputs;

        /// <summary>
        /// Fired when a position state should be broadcast (server only).
        /// </summary>
        public event Action<NetworkPositionState> OnBroadcastState;

        /// <summary>
        /// Fired when reconciliation occurs.
        /// </summary>
        public event Action<float> OnReconciliation;

        // PROPERTIES: ----------------------------------------------------------------------------

        public Character Character => m_Character;
        public CharacterRole Role => m_Role;
        public bool IsLocalPlayer => m_Role == CharacterRole.LocalPlayer;
        public bool IsRemotePlayer => m_Role == CharacterRole.RemotePlayer;
        public bool IsServerAuthority => m_Role == CharacterRole.ServerAuthority;
        public float ServerTime => m_SyncedServerTime;

        // LIFECYCLE: -----------------------------------------------------------------------------

        private void Awake()
        {
            m_Character = GetComponent<Character>();
        }

        private void Start()
        {
            InitializeForRole();
        }

        private void OnDestroy()
        {
            Cleanup();
        }

        // INITIALIZATION: ------------------------------------------------------------------------

        private void InitializeForRole()
        {
            if (m_IsInitialized) return;
            
            switch (m_Role)
            {
                case CharacterRole.LocalPlayer:
                    InitializeLocalPlayer();
                    break;
                    
                case CharacterRole.RemotePlayer:
                    InitializeRemotePlayer();
                    break;
                    
                case CharacterRole.ServerAuthority:
                    // Server authority is set up separately via CreateServerDriver
                    break;
            }
            
            m_IsInitialized = true;
            
            if (m_LogEvents)
            {
                Debug.Log($"[NetworkCharacter] Initialized as {m_Role}: {gameObject.name}");
            }
        }

        private void InitializeLocalPlayer()
        {
            // Store current motion/facing for reapplication
            var currentMotion = m_Character.Motion;
            var currentFacing = m_Character.Facing;
            
            // Create network-aware driver
            m_LocalDriver = new UnitDriverNetworkClient();
            
            // Replace driver (this triggers OnStartup internally)
            m_Character.Kernel.ChangeDriver(m_Character, m_LocalDriver);
            
            // Hook events
            m_LocalDriver.OnSendInput += HandleSendInput;
            m_LocalDriver.OnReconciliation += HandleReconciliation;
            
            // Create network-aware player input
            m_LocalPlayer = new UnitPlayerDirectionalNetwork();
            m_Character.Kernel.ChangePlayer(m_Character, m_LocalPlayer);
        }

        private void InitializeRemotePlayer()
        {
            // Create interpolating driver
            m_RemoteDriver = new UnitDriverNetworkRemote();
            m_Character.Kernel.ChangeDriver(m_Character, m_RemoteDriver);
            
            // Remote players don't need a player unit (no input)
            m_Character.Kernel.ChangePlayer(m_Character, null);
        }

        private void Cleanup()
        {
            if (m_LocalDriver != null)
            {
                m_LocalDriver.OnSendInput -= HandleSendInput;
                m_LocalDriver.OnReconciliation -= HandleReconciliation;
            }
        }

        // ROLE CHANGING: -------------------------------------------------------------------------

        /// <summary>
        /// Change the role of this character at runtime.
        /// Useful for host migration or spectator mode.
        /// </summary>
        public void ChangeRole(CharacterRole newRole)
        {
            if (m_Role == newRole) return;
            
            Cleanup();
            m_IsInitialized = false;
            m_Role = newRole;
            InitializeForRole();
        }

        // SERVER-SIDE DRIVER: --------------------------------------------------------------------

        /// <summary>
        /// Create and return a server-side driver for this character.
        /// Call this on the server to get the authoritative driver.
        /// </summary>
        public UnitDriverNetworkServer CreateServerDriver()
        {
            var serverDriver = new UnitDriverNetworkServer();
            m_Character.Kernel.ChangeDriver(m_Character, serverDriver);
            m_Character.Kernel.ChangePlayer(m_Character, null);
            
            m_Role = CharacterRole.ServerAuthority;
            m_IsInitialized = true;
            
            return serverDriver;
        }

        // INPUT/STATE TRANSMISSION: --------------------------------------------------------------

        /// <summary>
        /// Receive inputs from client (server-side).
        /// </summary>
        public void ReceiveClientInputs(NetworkInputState[] inputs)
        {
            if (m_Character.Driver is UnitDriverNetworkServer serverDriver)
            {
                foreach (var input in inputs)
                {
                    serverDriver.QueueInput(input);
                }
            }
        }

        /// <summary>
        /// Apply server state update (client-side).
        /// </summary>
        public void ApplyServerState(NetworkPositionState state)
        {
            if (m_LocalDriver != null)
            {
                m_LocalDriver.ApplyServerState(state);
            }
            else if (m_RemoteDriver != null)
            {
                m_RemoteDriver.AddSnapshot(state, m_SyncedServerTime);
            }
        }

        /// <summary>
        /// Get current position state for broadcasting (server-side).
        /// </summary>
        public NetworkPositionState GetCurrentState()
        {
            if (m_Character.Driver is UnitDriverNetworkServer serverDriver)
            {
                return serverDriver.GetCurrentState();
            }
            
            // Fallback for non-server
            return NetworkPositionState.Create(
                transform.position,
                transform.eulerAngles.y,
                0f,
                0,
                m_Character.Driver.IsGrounded,
                false
            );
        }
        
        /// <summary>
        /// Broadcast current state to all clients.
        /// Call this at your desired state broadcast rate (e.g., 20 Hz).
        /// </summary>
        public void BroadcastCurrentState()
        {
            var state = GetCurrentState();
            OnBroadcastState?.Invoke(state);
        }

        // TIME SYNCHRONIZATION: ------------------------------------------------------------------

        /// <summary>
        /// Update the synchronized server time.
        /// Call this every frame with the current server time.
        /// </summary>
        public void SetServerTime(float serverTime)
        {
            m_SyncedServerTime = serverTime;
            
            if (m_RemoteDriver != null)
            {
                m_RemoteDriver.SetServerTime(serverTime);
            }
        }

        // EVENT HANDLERS: ------------------------------------------------------------------------

        private void HandleSendInput(NetworkInputState[] inputs)
        {
            OnSendInputs?.Invoke(inputs);
            
            if (m_LogEvents)
            {
                Debug.Log($"[NetworkCharacter] Sending {inputs.Length} inputs, latest seq: {inputs[inputs.Length - 1].sequenceNumber}");
            }
        }

        private void HandleReconciliation(float positionError)
        {
            OnReconciliation?.Invoke(positionError);
            
            if (m_LogEvents)
            {
                Debug.Log($"[NetworkCharacter] Reconciliation triggered, error: {positionError:F3}m");
            }
        }

        // EDITOR HELPERS: ------------------------------------------------------------------------

#if UNITY_EDITOR
        private void OnValidate()
        {
            // Ensure config values are sensible
            if (m_Config.inputSendRate < 1) m_Config.inputSendRate = 30;
            if (m_Config.inputSendRate > 120) m_Config.inputSendRate = 120;
        }

        private void OnDrawGizmosSelected()
        {
            if (m_RemoteDriver != null && m_RemoteDriver.IsExtrapolating)
            {
                Gizmos.color = Color.yellow;
                Gizmos.DrawWireSphere(transform.position, 0.5f);
            }
        }
#endif
    }
}
