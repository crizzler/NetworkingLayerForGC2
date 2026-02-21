using System;
using UnityEngine;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Common;
using Arawn.GameCreator2.Networking;

#if UNITY_NETCODE
using Unity.Netcode;
#endif

namespace Arawn.GameCreator2.Networking
{
    /// <summary>
    /// Network-enabled wrapper for Game Creator 2 Character.
    /// Handles state synchronization, driver assignment, and optional system syncing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This component works alongside the GC2 Character without modifying it.
    /// It synchronizes critical state (IsDead, IsPlayer) across the network and
    /// provides options for syncing expensive systems like IK, Footsteps, etc.
    /// </para>
    /// <para>
    /// The component automatically assigns the correct driver based on network role:
    /// - Server: UnitDriverNetworkServer (authoritative)
    /// - Local Client: UnitDriverNetworkClient (prediction)
    /// - Remote Client: UnitDriverNetworkRemote (interpolation)
    /// </para>
    /// </remarks>
    [RequireComponent(typeof(Character))]
    [AddComponentMenu("Game Creator/Network/Network Character")]
    [DefaultExecutionOrder(ApplicationManager.EXECUTION_ORDER_DEFAULT_EARLIER - 1)]
#if UNITY_NETCODE
    public class NetworkCharacter : NetworkBehaviour
#else
    public class NetworkCharacter : MonoBehaviour
#endif
    {
        // ════════════════════════════════════════════════════════════════════════════════════════
        // ENUMS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Determines how an expensive system is handled for remote characters.
        /// </summary>
        public enum RemoteSystemMode
        {
            /// <summary>Disable the system entirely on remote characters.</summary>
            Disabled,
            /// <summary>Run the system locally without network sync (cosmetic only).</summary>
            LocalOnly,
            /// <summary>Synchronize the system over the network.</summary>
            Synchronized
        }
        
        /// <summary>
        /// The current network role of this character.
        /// </summary>
        public enum NetworkRole
        {
            /// <summary>Not yet assigned or offline.</summary>
            None,
            /// <summary>Authoritative server instance.</summary>
            Server,
            /// <summary>Local player with client-side prediction.</summary>
            LocalClient,
            /// <summary>Remote player with interpolation.</summary>
            RemoteClient
        }
        
        /// <summary>
        /// Determines how NPC AI and movement is synchronized across the network.
        /// </summary>
        public enum NPCSyncMode
        {
            /// <summary>
            /// Server runs all AI logic and broadcasts state to clients.
            /// Best for: Important NPCs (bosses, loot-droppers), competitive PvE.
            /// Pros: Consistent behavior, cheat-resistant.
            /// Cons: Higher bandwidth, server CPU load.
            /// </summary>
            ServerAuthoritative,
            
            /// <summary>
            /// Each client runs AI locally with deterministic seeding.
            /// Best for: Ambient creatures, cosmetic NPCs, high NPC counts (100+).
            /// Pros: Minimal bandwidth, scales to hundreds of NPCs.
            /// Cons: Minor visual desync possible, not cheat-resistant.
            /// </summary>
            ClientSideDeterministic
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // INSPECTOR - NPC Mode
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        [Header("NPC Synchronization")]
        [Tooltip("How this NPC's AI and movement is synchronized.\n\n" +
                 "Server Authoritative: Server runs AI, broadcasts to clients. Use for important NPCs.\n" +
                 "Client-Side Deterministic: Each client runs AI locally. Use for ambient/cosmetic NPCs.")]
        [SerializeField] private NPCSyncMode m_NPCMode = NPCSyncMode.ServerAuthoritative;
        
        [Tooltip("Random seed for deterministic client-side behavior. Only used in ClientSideDeterministic mode.")]
        [SerializeField] private int m_DeterministicSeed = 0;
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // INSPECTOR - System Sync Options
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        [Header("Remote Character Systems")]
        [Tooltip("How to handle IK on remote characters")]
        [SerializeField] private RemoteSystemMode m_IKMode = RemoteSystemMode.Synchronized;
        
        [Tooltip("How to handle footsteps on remote characters")]
        [SerializeField] private RemoteSystemMode m_FootstepsMode = RemoteSystemMode.LocalOnly;
        
        [Tooltip("How to handle interaction on remote characters")]
        [SerializeField] private RemoteSystemMode m_InteractionMode = RemoteSystemMode.Disabled;
        
        [Tooltip("How to handle combat (hit detection) on remote characters")]
        [SerializeField] private RemoteSystemMode m_CombatMode = RemoteSystemMode.Disabled;
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // INSPECTOR - Core Feature Systems
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        [Header("Core Feature Systems")]
        [Tooltip("How to handle ragdoll synchronization")]
        [SerializeField] private RemoteSystemMode m_RagdollMode = RemoteSystemMode.Synchronized;
        
        [Tooltip("How to handle props attachment synchronization")]
        [SerializeField] private RemoteSystemMode m_PropsMode = RemoteSystemMode.Synchronized;
        
        [Tooltip("How to handle invincibility state synchronization")]
        [SerializeField] private RemoteSystemMode m_InvincibilityMode = RemoteSystemMode.Synchronized;
        
        [Tooltip("How to handle poise state synchronization")]
        [SerializeField] private RemoteSystemMode m_PoiseMode = RemoteSystemMode.Synchronized;
        
        [Tooltip("How to handle busy limbs state synchronization")]
        [SerializeField] private RemoteSystemMode m_BusyMode = RemoteSystemMode.LocalOnly;
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // INSPECTOR - Optional Components
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        [Header("Optional Network Components")]
        [Tooltip("Enable IK network sync controller (requires Synchronized IK mode)")]
        [SerializeField] private bool m_UseNetworkIK = true;
        
        [Tooltip("Enable motion network controller for dash/teleport validation")]
        [SerializeField] private bool m_UseNetworkMotion = true;
        
        [Tooltip("Enable lag compensation for hit validation")]
        [SerializeField] private bool m_UseLagCompensation = true;
        
        [Tooltip("Enable animation sync for States and Gestures (attack anims, emotes, etc.)")]
        [SerializeField] private bool m_UseAnimationSync = true;
        
        [Tooltip("Enable Core feature networking (Ragdoll, Props, Invincibility, Poise, Busy, Interaction)")]
        [SerializeField] private bool m_UseCoreNetworking = true;
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // INSPECTOR - Server Settings
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        [Header("Server Optimization")]
        [Tooltip("Disable visuals on server (saves memory/performance)")]
        [SerializeField] private bool m_DisableVisualsOnServer = true;
        
        [Tooltip("Disable audio on server")]
        [SerializeField] private bool m_DisableAudioOnServer = true;
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // PRIVATE MEMBERS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        private Character m_Character;
        private NetworkRole m_CurrentRole = NetworkRole.None;
        private bool m_IsInitialized;
        
        // Network state (manual sync if not using Netcode)
        private bool m_LastIsDead;
        private bool m_LastIsPlayer;
        
        // Runtime-created drivers (created when network role is assigned)
        private UnitDriverNetworkServer m_ServerDriver;
        private UnitDriverNetworkClient m_ClientDriver;
        private UnitDriverNetworkRemote m_RemoteDriver;
        
        // Optional components
        private UnitIKNetworkController m_IKController;
        private UnitMotionNetworkController m_MotionController;
        private CharacterLagCompensation m_LagCompensation;
        private NetworkCombatInterceptor m_CombatInterceptor;
        private UnitFacingNetworkPivot m_NetworkFacingUnit;
        private UnitAnimimNetworkKinematic m_NetworkAnimimUnit;
        private UnitAnimimNetworkController m_AnimimController;
        
        // Core networking controller (Ragdoll, Props, Invincibility, Poise, Busy, Interaction)
        private NetworkCoreController m_CoreController;
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // NETWORKED STATE
        // ════════════════════════════════════════════════════════════════════════════════════════
        
#if UNITY_NETCODE
        // Server-authoritative state
        private NetworkVariable<bool> m_NetworkIsDead = new NetworkVariable<bool>(
            false,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server
        );
        
        private NetworkVariable<bool> m_NetworkIsPlayer = new NetworkVariable<bool>(
            false,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server
        );
        
        // Sequence number for state updates (for non-NetworkVariable sync)
        private NetworkVariable<uint> m_StateSequence = new NetworkVariable<uint>(
            0,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server
        );
        
        // Server-authoritative facing direction (yaw in degrees)
        private NetworkVariable<float> m_NetworkFacingYaw = new NetworkVariable<float>(
            0f,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server
        );
        
        // Server-authoritative animation state (6 bytes packed)
        private NetworkVariable<NetworkAnimimState> m_NetworkAnimimState = new NetworkVariable<NetworkAnimimState>(
            default,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server
        );
#endif
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // EVENTS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Fired when IsDead changes over the network.
        /// </summary>
        public event Action<bool> OnNetworkDeathChanged;
        
        /// <summary>
        /// Fired when IsPlayer changes over the network.
        /// </summary>
        public event Action<bool> OnNetworkPlayerChanged;
        
        /// <summary>
        /// Fired when the network role is assigned.
        /// </summary>
        public event Action<NetworkRole> OnRoleAssigned;
        
        /// <summary>
        /// Fired when a driver is assigned.
        /// </summary>
        public event Action<IUnitDriver> OnDriverAssigned;
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // PROPERTIES
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>The underlying GC2 Character.</summary>
        public Character Character => m_Character;
        
        /// <summary>Current network role of this character.</summary>
        public NetworkRole Role => m_CurrentRole;
        
        /// <summary>Whether this is the local player's character.</summary>
        public bool IsLocalPlayer => m_CurrentRole == NetworkRole.LocalClient;
        
        /// <summary>Whether this is running on the server.</summary>
        public bool IsServerInstance => m_CurrentRole == NetworkRole.Server;
        
        /// <summary>Whether this is a remote player's character.</summary>
        public bool IsRemotePlayer => m_CurrentRole == NetworkRole.RemoteClient;
        
        /// <summary>The network IK controller if enabled.</summary>
        public UnitIKNetworkController IKController => m_IKController;
        
        /// <summary>The network motion controller if enabled.</summary>
        public UnitMotionNetworkController MotionController => m_MotionController;
        
        /// <summary>The lag compensation adapter if enabled.</summary>
        public CharacterLagCompensation LagCompensation => m_LagCompensation;
        
        /// <summary>The currently active driver.</summary>
        public IUnitDriver ActiveDriver => m_Character?.Driver;
        
        /// <summary>Server driver instance (for external access).</summary>
        public UnitDriverNetworkServer ServerDriver => m_ServerDriver;
        
        /// <summary>Client driver instance (for external access).</summary>
        public UnitDriverNetworkClient ClientDriver => m_ClientDriver;
        
        /// <summary>Remote driver instance (for external access).</summary>
        public UnitDriverNetworkRemote RemoteDriver => m_RemoteDriver;
        
        // NPC Mode
        /// <summary>The NPC synchronization mode (server-authoritative or client-side deterministic).</summary>
        public NPCSyncMode NPCMode => m_NPCMode;
        
        /// <summary>Whether this NPC uses server-authoritative synchronization.</summary>
        public bool IsServerAuthoritativeNPC => m_NPCMode == NPCSyncMode.ServerAuthoritative;
        
        /// <summary>Whether this NPC uses client-side deterministic behavior.</summary>
        public bool IsClientSideNPC => m_NPCMode == NPCSyncMode.ClientSideDeterministic;
        
        /// <summary>The deterministic seed for client-side NPCs.</summary>
        public int DeterministicSeed => m_DeterministicSeed;
        
        // System modes
        public RemoteSystemMode IKMode => m_IKMode;
        public RemoteSystemMode FootstepsMode => m_FootstepsMode;
        public RemoteSystemMode InteractionMode => m_InteractionMode;
        public RemoteSystemMode CombatMode => m_CombatMode;
        
        /// <summary>The combat interceptor for server-authoritative hit validation.</summary>
        public NetworkCombatInterceptor CombatInterceptor => m_CombatInterceptor;
        
        /// <summary>The network facing unit for server-authoritative facing.</summary>
        public UnitFacingNetworkPivot NetworkFacingUnit => m_NetworkFacingUnit;
        
        /// <summary>The animation sync controller for States and Gestures.</summary>
        public UnitAnimimNetworkController AnimimController => m_AnimimController;
        
        /// <summary>The Core networking controller for Ragdoll, Props, Invincibility, Poise, Busy, Interaction.</summary>
        public NetworkCoreController CoreController => m_CoreController;
        
        /// <summary>Current network role (alias for Role for compatibility with facing unit).</summary>
        public NetworkRole CurrentRole => m_CurrentRole;
        
        // Core Feature System modes
        public RemoteSystemMode RagdollMode => m_RagdollMode;
        public RemoteSystemMode PropsMode => m_PropsMode;
        public RemoteSystemMode InvincibilityMode => m_InvincibilityMode;
        public RemoteSystemMode PoiseMode => m_PoiseMode;
        public RemoteSystemMode BusyMode => m_BusyMode;
        
        /// <summary>Whether Core networking is enabled.</summary>
        public bool UseCoreNetworking => m_UseCoreNetworking;
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // INITIALIZATION
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        private void Awake()
        {
            m_Character = GetComponent<Character>();
            if (m_Character == null)
            {
                Debug.LogError($"[NetworkCharacter] No Character component found on {gameObject.name}");
                enabled = false;
                return;
            }
            
            // Cache initial state
            m_LastIsDead = m_Character.IsDead;
            m_LastIsPlayer = m_Character.IsPlayer;
        }
        
#if UNITY_NETCODE
        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();
            
            // Subscribe to network variable changes
            m_NetworkIsDead.OnValueChanged += OnNetworkIsDeadChanged;
            m_NetworkIsPlayer.OnValueChanged += OnNetworkIsPlayerChanged;
            
            // Determine role and initialize
            DetermineRoleAndInitialize();
            
            // Sync initial state on server
            if (IsServer)
            {
                m_NetworkIsDead.Value = m_Character.IsDead;
                m_NetworkIsPlayer.Value = m_Character.IsPlayer;
            }
        }
        
        public override void OnNetworkDespawn()
        {
            m_NetworkIsDead.OnValueChanged -= OnNetworkIsDeadChanged;
            m_NetworkIsPlayer.OnValueChanged -= OnNetworkIsPlayerChanged;
            
            Cleanup();
            
            base.OnNetworkDespawn();
        }
#endif
        
        /// <summary>
        /// Manual initialization for non-Netcode networking solutions.
        /// Call this from your network spawn handler.
        /// </summary>
        /// <param name="isServer">True if this is running on the server.</param>
        /// <param name="isOwner">True if this is the local player's character.</param>
        /// <param name="isHost">True if this is a host (server + client).</param>
        public void InitializeNetworkRole(bool isServer, bool isOwner, bool isHost = false)
        {
            if (m_IsInitialized) return;
            
            // Determine role
            if (isServer && !isHost)
            {
                // Dedicated server
                m_CurrentRole = NetworkRole.Server;
            }
            else if (isHost && isOwner)
            {
                // Host's own character - use client driver for responsive feel
                m_CurrentRole = NetworkRole.LocalClient;
            }
            else if (isServer)
            {
                // Host validating other players
                m_CurrentRole = NetworkRole.Server;
            }
            else if (isOwner)
            {
                // Client's own character
                m_CurrentRole = NetworkRole.LocalClient;
            }
            else
            {
                // Remote player
                m_CurrentRole = NetworkRole.RemoteClient;
            }
            
            InitializeForRole();
        }
        
#if UNITY_NETCODE
        private void DetermineRoleAndInitialize()
        {
            // Use Netcode's built-in checks
            if (IsServer && !IsHost)
            {
                m_CurrentRole = NetworkRole.Server;
            }
            else if (IsHost && IsOwner)
            {
                m_CurrentRole = NetworkRole.LocalClient;
            }
            else if (IsServer)
            {
                m_CurrentRole = NetworkRole.Server;
            }
            else if (IsOwner)
            {
                m_CurrentRole = NetworkRole.LocalClient;
            }
            else
            {
                m_CurrentRole = NetworkRole.RemoteClient;
            }
            
            InitializeForRole();
        }
#endif
        
        private void InitializeForRole()
        {
            if (m_IsInitialized) return;
            m_IsInitialized = true;
            
            // Assign appropriate driver
            AssignDriverForRole();
            
            // Configure systems based on role
            ConfigureSystemsForRole();
            
            // Setup optional components
            SetupOptionalComponents();
            
            // Server optimizations
            if (m_CurrentRole == NetworkRole.Server)
            {
                ApplyServerOptimizations();
            }
            
            // Subscribe to character events
            SubscribeToCharacterEvents();
            
            OnRoleAssigned?.Invoke(m_CurrentRole);
            
            Debug.Log($"[NetworkCharacter] {gameObject.name} initialized as {m_CurrentRole}");
        }
        
        private void AssignDriverForRole()
        {
            // Create the appropriate driver at runtime based on role
            IUnitDriver driver = m_CurrentRole switch
            {
                NetworkRole.Server => CreateServerDriver(),
                NetworkRole.LocalClient => CreateClientDriver(),
                NetworkRole.RemoteClient => CreateRemoteDriver(),
                _ => null
            };
            
            if (driver != null)
            {
                // Use GC2's ChangeDriver if available, otherwise set directly on kernel
                var kernel = m_Character.Kernel;
                if (kernel != null)
                {
                    // Access the driver setter through reflection or public API
                    SetCharacterDriver(driver);
                }
                
                OnDriverAssigned?.Invoke(driver);
                Debug.Log($"[NetworkCharacter] Assigned {driver.GetType().Name} to {gameObject.name}");
            }
        }
        
        private UnitDriverNetworkServer CreateServerDriver()
        {
            m_ServerDriver = new UnitDriverNetworkServer();
            return m_ServerDriver;
        }
        
        private UnitDriverNetworkClient CreateClientDriver()
        {
            m_ClientDriver = new UnitDriverNetworkClient();
            return m_ClientDriver;
        }
        
        private UnitDriverNetworkRemote CreateRemoteDriver()
        {
            m_RemoteDriver = new UnitDriverNetworkRemote();
            return m_RemoteDriver;
        }
        
        private void SetCharacterDriver(IUnitDriver driver)
        {
            // GC2 kernel driver change - need to go through CharacterKernel
            var kernel = m_Character.Kernel;
            if (kernel == null) return;
            
            // Use reflection to access the driver field since CharacterKernel might not expose it directly
            var driverField = kernel.GetType().GetField("m_Driver", 
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            
            if (driverField != null)
            {
                // Dispose old driver
                var oldDriver = m_Character.Driver;
                oldDriver?.OnDispose(m_Character);
                
                // Set new driver
                driverField.SetValue(kernel, driver);
                
                // Initialize new driver
                driver.OnStartup(m_Character);
                driver.OnEnable();
            }
        }
        
        private void ConfigureSystemsForRole()
        {
            switch (m_CurrentRole)
            {
                case NetworkRole.Server:
                    // Server doesn't need visual IK, footsteps, etc.
                    ConfigureSystemMode(SystemType.IK, RemoteSystemMode.Disabled);
                    ConfigureSystemMode(SystemType.Footsteps, RemoteSystemMode.Disabled);
                    ConfigureSystemMode(SystemType.Interaction, RemoteSystemMode.Disabled);
                    // Combat might be needed for hit detection
                    ConfigureSystemMode(SystemType.Combat, m_CombatMode);
                    break;
                    
                case NetworkRole.LocalClient:
                    // Local client runs everything
                    // No configuration needed - all systems run normally
                    break;
                    
                case NetworkRole.RemoteClient:
                    // Remote client uses configured modes
                    ConfigureSystemMode(SystemType.IK, m_IKMode);
                    ConfigureSystemMode(SystemType.Footsteps, m_FootstepsMode);
                    ConfigureSystemMode(SystemType.Interaction, m_InteractionMode);
                    ConfigureSystemMode(SystemType.Combat, m_CombatMode);
                    break;
            }
        }
        
        private enum SystemType { IK, Footsteps, Interaction, Combat }
        
        private void ConfigureSystemMode(SystemType system, RemoteSystemMode mode)
        {
            switch (system)
            {
                case SystemType.IK:
                    ConfigureIK(mode);
                    break;
                case SystemType.Footsteps:
                    ConfigureFootsteps(mode);
                    break;
                case SystemType.Interaction:
                    ConfigureInteraction(mode);
                    break;
                case SystemType.Combat:
                    ConfigureCombat(mode);
                    break;
            }
        }
        
        private void ConfigureIK(RemoteSystemMode mode)
        {
            // IK configuration is handled by UnitIKNetworkController if synchronized
            // For disabled mode, we'd need to disable GC2's IK system
            if (mode == RemoteSystemMode.Disabled)
            {
                // Disable IK updates - access through character's IK property
                // GC2's IK system runs in InverseKinematics class
                // We can't fully disable without modifying GC2, but we can skip updates
            }
            // LocalOnly and Synchronized are handled by UnitIKNetworkController
        }
        
        private void ConfigureFootsteps(RemoteSystemMode mode)
        {
            if (mode == RemoteSystemMode.Disabled)
            {
                // Disable footstep sounds for remote characters
                // Access through m_Character.Footsteps
            }
            // LocalOnly runs footsteps based on local animation
            // Synchronized would require a NetworkFootstepsController (not implemented)
        }
        
        private void ConfigureInteraction(RemoteSystemMode mode)
        {
            if (mode == RemoteSystemMode.Disabled)
            {
                // Disable interaction detection for remote characters
                // Remote characters shouldn't trigger interactions locally
            }
        }
        
        private void ConfigureCombat(RemoteSystemMode mode)
        {
            // Get or create combat interceptor
            m_CombatInterceptor = GetComponent<NetworkCombatInterceptor>();
            
            switch (mode)
            {
                case RemoteSystemMode.Disabled:
                    // Remote characters: No local hit detection, just receive broadcasts
                    if (m_CombatInterceptor != null)
                    {
                        m_CombatInterceptor.InterceptMelee = true;
                        m_CombatInterceptor.InterceptShooter = true;
                        m_CombatInterceptor.Initialize(
                            isServer: false,
                            isLocalPlayer: false
                        );
                    }
                    break;
                    
                case RemoteSystemMode.LocalOnly:
                    // Local player: Intercept hits and send to server
                    if (m_CombatInterceptor == null && m_CurrentRole == NetworkRole.LocalClient)
                    {
                        m_CombatInterceptor = gameObject.AddComponent<NetworkCombatInterceptor>();
                    }
                    if (m_CombatInterceptor != null)
                    {
                        m_CombatInterceptor.Initialize(
                            isServer: m_CurrentRole == NetworkRole.Server,
                            isLocalPlayer: m_CurrentRole == NetworkRole.LocalClient
                        );
                    }
                    break;
                    
                case RemoteSystemMode.Synchronized:
                    // Server: Process all hits authoritatively
                    if (m_CombatInterceptor == null && m_CurrentRole == NetworkRole.Server)
                    {
                        m_CombatInterceptor = gameObject.AddComponent<NetworkCombatInterceptor>();
                    }
                    if (m_CombatInterceptor != null)
                    {
                        m_CombatInterceptor.Initialize(
                            isServer: m_CurrentRole == NetworkRole.Server,
                            isLocalPlayer: m_CurrentRole == NetworkRole.LocalClient
                        );
                    }
                    
                    // Also setup lag compensation for hit validation
                    if (m_CurrentRole == NetworkRole.Server || m_UseLagCompensation)
                    {
                        SetupLagCompensation();
                    }
                    break;
            }
        }
        
        private void SetupLagCompensation()
        {
            if (m_LagCompensation != null) return;
            
            m_LagCompensation = GetComponent<CharacterLagCompensation>();
            if (m_LagCompensation == null)
            {
                m_LagCompensation = gameObject.AddComponent<CharacterLagCompensation>();
            }
            
            // Set network ID if using Netcode
#if UNITY_NETCODE
            var networkObject = GetComponent<NetworkObject>();
            if (networkObject != null)
            {
                m_LagCompensation.NetworkId = (uint)networkObject.NetworkObjectId;
            }
#endif
        }
        
        private void SetupOptionalComponents()
        {
            // Setup IK network controller if enabled and in sync mode
            if (m_UseNetworkIK && m_IKMode == RemoteSystemMode.Synchronized)
            {
                m_IKController = GetComponent<UnitIKNetworkController>();
                if (m_IKController == null)
                {
                    m_IKController = gameObject.AddComponent<UnitIKNetworkController>();
                }
                m_IKController.Initialize(m_Character, m_CurrentRole == NetworkRole.LocalClient);
            }
            
            // Setup motion network controller if enabled (for dash/teleport validation)
            if (m_UseNetworkMotion)
            {
                // Motion controller coordinates with UnitMotionNetworkController on the character
                // It validates dash/teleport requests and syncs motion config changes
                m_MotionController = m_Character.Motion as UnitMotionNetworkController;
                if (m_MotionController != null)
                {
                    m_MotionController.IsServer = m_CurrentRole == NetworkRole.Server;
                }
            }
            
            // Setup lag compensation if enabled (server-side only typically)
            if (m_UseLagCompensation && m_CurrentRole == NetworkRole.Server)
            {
                m_LagCompensation = GetComponent<CharacterLagCompensation>();
                if (m_LagCompensation == null)
                {
                    m_LagCompensation = gameObject.AddComponent<CharacterLagCompensation>();
                }
                // LagCompensation auto-registers on Start
            }
            
            // Setup animation sync controller if enabled
            if (m_UseAnimationSync)
            {
                m_AnimimController = GetComponent<UnitAnimimNetworkController>();
                if (m_AnimimController == null)
                {
                    m_AnimimController = gameObject.AddComponent<UnitAnimimNetworkController>();
                }
                m_AnimimController.Initialize(m_Character, m_CurrentRole == NetworkRole.LocalClient);
            }
            
            // Setup Core networking controller if enabled (Ragdoll, Props, Invincibility, Poise, Busy, Interaction)
            if (m_UseCoreNetworking)
            {
                m_CoreController = GetComponent<NetworkCoreController>();
                if (m_CoreController == null)
                {
                    m_CoreController = gameObject.AddComponent<NetworkCoreController>();
                }
                m_CoreController.Initialize(
                    m_CurrentRole == NetworkRole.Server,
                    m_CurrentRole == NetworkRole.LocalClient
                );
            }
        }
        
        private void ApplyServerOptimizations()
        {
            if (m_DisableVisualsOnServer)
            {
                // Disable renderers
                foreach (var renderer in GetComponentsInChildren<Renderer>())
                {
                    renderer.enabled = false;
                }
                
                // Disable particle systems
                foreach (var particles in GetComponentsInChildren<ParticleSystem>())
                {
                    particles.Stop();
                    var emission = particles.emission;
                    emission.enabled = false;
                }
            }
            
            if (m_DisableAudioOnServer)
            {
                // Disable audio sources
                foreach (var audio in GetComponentsInChildren<AudioSource>())
                {
                    audio.enabled = false;
                }
            }
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // CHARACTER EVENT HANDLING
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        private void SubscribeToCharacterEvents()
        {
            if (m_Character == null) return;
            
            m_Character.EventDie += OnLocalDeath;
            m_Character.EventRevive += OnLocalRevive;
        }
        
        private void UnsubscribeFromCharacterEvents()
        {
            if (m_Character == null) return;
            
            m_Character.EventDie -= OnLocalDeath;
            m_Character.EventRevive -= OnLocalRevive;
        }
        
        private void OnLocalDeath()
        {
            // Only server can change the authoritative state
#if UNITY_NETCODE
            if (IsServer)
            {
                m_NetworkIsDead.Value = true;
            }
#else
            // For non-Netcode, raise event for network layer to handle
            OnNetworkDeathChanged?.Invoke(true);
#endif
        }
        
        private void OnLocalRevive()
        {
#if UNITY_NETCODE
            if (IsServer)
            {
                m_NetworkIsDead.Value = false;
            }
#else
            OnNetworkDeathChanged?.Invoke(false);
#endif
        }
        
#if UNITY_NETCODE
        private void OnNetworkIsDeadChanged(bool previous, bool current)
        {
            // Apply to local character if we're not the authority
            if (!IsServer && m_Character.IsDead != current)
            {
                m_Character.IsDead = current;
            }
            
            OnNetworkDeathChanged?.Invoke(current);
        }
        
        private void OnNetworkIsPlayerChanged(bool previous, bool current)
        {
            if (!IsServer && m_Character.IsPlayer != current)
            {
                m_Character.IsPlayer = current;
            }
            
            OnNetworkPlayerChanged?.Invoke(current);
        }
#endif
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // SERVER-AUTHORITATIVE STATE CHANGES
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Server-authoritative kill. Only callable on server.
        /// </summary>
        public void ServerKill()
        {
#if UNITY_NETCODE
            if (!IsServer)
            {
                Debug.LogWarning("[NetworkCharacter] ServerKill called on client");
                return;
            }
#endif
            
            m_Character.IsDead = true;
            
#if UNITY_NETCODE
            m_NetworkIsDead.Value = true;
#endif
        }
        
        /// <summary>
        /// Server-authoritative revive. Only callable on server.
        /// </summary>
        public void ServerRevive()
        {
#if UNITY_NETCODE
            if (!IsServer)
            {
                Debug.LogWarning("[NetworkCharacter] ServerRevive called on client");
                return;
            }
#endif
            
            m_Character.IsDead = false;
            
#if UNITY_NETCODE
            m_NetworkIsDead.Value = false;
#endif
        }
        
        /// <summary>
        /// Server-authoritative player designation. Only callable on server.
        /// </summary>
        public void ServerSetIsPlayer(bool isPlayer)
        {
#if UNITY_NETCODE
            if (!IsServer)
            {
                Debug.LogWarning("[NetworkCharacter] ServerSetIsPlayer called on client");
                return;
            }
#endif
            
            m_Character.IsPlayer = isPlayer;
            
#if UNITY_NETCODE
            m_NetworkIsPlayer.Value = isPlayer;
#endif
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // CLIENT REQUESTS (Sent to Server)
        // ════════════════════════════════════════════════════════════════════════════════════════
        
#if UNITY_NETCODE
        /// <summary>
        /// Client requests death (e.g., suicide command). Server validates and applies.
        /// </summary>
        [ServerRpc]
        public void RequestDeathServerRpc()
        {
            // Server can add validation here (e.g., cooldowns, game rules)
            ServerKill();
        }
        
        /// <summary>
        /// Client requests a facing direction change. Server validates and applies.
        /// </summary>
        [ServerRpc]
        public void RequestFacingUpdateServerRpc(float requestedYaw)
        {
            if (m_NetworkFacingUnit != null)
            {
                float validatedYaw = m_NetworkFacingUnit.ValidateFacingRequest(requestedYaw);
                m_NetworkFacingYaw.Value = validatedYaw;
            }
        }
#endif
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // FACING UNIT SUPPORT
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Called by UnitFacingNetworkPivot when it initializes.
        /// </summary>
        public void OnFacingUnitRegistered(UnitFacingNetworkPivot facingUnit)
        {
            m_NetworkFacingUnit = facingUnit;
            
#if UNITY_NETCODE
            // Subscribe to facing yaw changes
            m_NetworkFacingYaw.OnValueChanged += OnFacingYawChanged;
            
            // Initialize with current rotation
            if (IsServer)
            {
                m_NetworkFacingYaw.Value = transform.eulerAngles.y;
            }
#endif
        }
        
        /// <summary>
        /// Called by UnitFacingNetworkPivot when it is disposed.
        /// </summary>
        public void OnFacingUnitUnregistered()
        {
#if UNITY_NETCODE
            m_NetworkFacingYaw.OnValueChanged -= OnFacingYawChanged;
#endif
            m_NetworkFacingUnit = null;
        }
        
        /// <summary>
        /// Request a facing update from the server. Called by local client.
        /// </summary>
        public void RequestFacingUpdate(float desiredYaw)
        {
#if UNITY_NETCODE
            if (IsOwner && !IsServer)
            {
                RequestFacingUpdateServerRpc(desiredYaw);
            }
            else if (IsServer && m_NetworkFacingUnit != null)
            {
                // Server can update directly
                float validatedYaw = m_NetworkFacingUnit.ValidateFacingRequest(desiredYaw);
                m_NetworkFacingYaw.Value = validatedYaw;
            }
#endif
        }
        
#if UNITY_NETCODE
        private void OnFacingYawChanged(float previousYaw, float newYaw)
        {
            if (m_NetworkFacingUnit != null)
            {
                m_NetworkFacingUnit.OnServerYawReceived(newYaw);
            }
        }
#endif
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // ANIMIM UNIT SUPPORT
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Called by UnitAnimimNetworkKinematic when it initializes.
        /// </summary>
        public void OnAnimimUnitRegistered(UnitAnimimNetworkKinematic animimUnit)
        {
            m_NetworkAnimimUnit = animimUnit;
            
#if UNITY_NETCODE
            // Subscribe to animim state changes
            m_NetworkAnimimState.OnValueChanged += OnAnimimStateChanged;
#endif
        }
        
        /// <summary>
        /// Called by UnitAnimimNetworkKinematic when it is disposed.
        /// </summary>
        public void OnAnimimUnitUnregistered()
        {
#if UNITY_NETCODE
            m_NetworkAnimimState.OnValueChanged -= OnAnimimStateChanged;
#endif
            m_NetworkAnimimUnit = null;
        }
        
        /// <summary>
        /// Request an animim state update from the server. Called by local client.
        /// </summary>
        public void RequestAnimimUpdate(NetworkAnimimState state)
        {
#if UNITY_NETCODE
            if (IsOwner && !IsServer)
            {
                RequestAnimimUpdateServerRpc(state);
            }
            else if (IsServer)
            {
                // Server can update directly
                m_NetworkAnimimState.Value = state;
            }
#endif
        }
        
#if UNITY_NETCODE
        [ServerRpc]
        private void RequestAnimimUpdateServerRpc(NetworkAnimimState state)
        {
            // Server validates and applies the animim state
            // For now, we trust the client's animation state since it's cosmetic
            // Add validation here if needed for anti-cheat
            m_NetworkAnimimState.Value = state;
        }
        
        private void OnAnimimStateChanged(NetworkAnimimState previousState, NetworkAnimimState newState)
        {
            if (m_NetworkAnimimUnit != null)
            {
                m_NetworkAnimimUnit.OnServerStateReceived(newState);
            }
        }
#endif
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // UPDATE
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        private void Update()
        {
            if (!m_IsInitialized) return;
            
#if !UNITY_NETCODE
            // For non-Netcode solutions, detect local state changes and raise events
            DetectStateChanges();
#endif
        }
        
        private void DetectStateChanges()
        {
            // Only relevant for server or solutions that need polling
            if (m_Character.IsDead != m_LastIsDead)
            {
                m_LastIsDead = m_Character.IsDead;
                OnNetworkDeathChanged?.Invoke(m_LastIsDead);
            }
            
            if (m_Character.IsPlayer != m_LastIsPlayer)
            {
                m_LastIsPlayer = m_Character.IsPlayer;
                OnNetworkPlayerChanged?.Invoke(m_LastIsPlayer);
            }
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // CLEANUP
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        private void OnDestroy()
        {
            Cleanup();
        }
        
        private void Cleanup()
        {
            UnsubscribeFromCharacterEvents();
            m_IsInitialized = false;
            m_CurrentRole = NetworkRole.None;
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // MANUAL NETWORK SYNC (For non-Netcode solutions)
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Apply state received from network. Call this when receiving state updates.
        /// For Photon, FishNet, Mirror, etc.
        /// </summary>
        public void ApplyNetworkState(NetworkCharacterState state)
        {
            if (m_CurrentRole != NetworkRole.RemoteClient && m_CurrentRole != NetworkRole.LocalClient)
            {
                return; // Server doesn't apply received state
            }
            
            if (m_Character.IsDead != state.isDead)
            {
                m_Character.IsDead = state.isDead;
            }
            
            if (m_Character.IsPlayer != state.isPlayer)
            {
                m_Character.IsPlayer = state.isPlayer;
            }
        }
        
        /// <summary>
        /// Get current state for sending over network.
        /// </summary>
        public NetworkCharacterState GetNetworkState()
        {
            return new NetworkCharacterState
            {
                isDead = m_Character.IsDead,
                isPlayer = m_Character.IsPlayer
            };
        }
    }
    
    // ════════════════════════════════════════════════════════════════════════════════════════════
    // NETWORK STATE STRUCT
    // ════════════════════════════════════════════════════════════════════════════════════════════
    
    /// <summary>
    /// Compact state for network synchronization.
    /// For use with non-Netcode networking solutions.
    /// </summary>
    [Serializable]
    public struct NetworkCharacterState
    {
        public bool isDead;
        public bool isPlayer;
        
        // Packed into single byte for efficiency
        public byte ToPacked()
        {
            byte packed = 0;
            if (isDead) packed |= 0x01;
            if (isPlayer) packed |= 0x02;
            return packed;
        }
        
        public static NetworkCharacterState FromPacked(byte packed)
        {
            return new NetworkCharacterState
            {
                isDead = (packed & 0x01) != 0,
                isPlayer = (packed & 0x02) != 0
            };
        }
    }
}
