using System;
using System.Collections.Generic;
using UnityEngine;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Common;


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
    public partial class NetworkCharacter : MonoBehaviour
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

        [Tooltip("Animation clips to pre-register at startup so remote peers can resolve " +
                 "broadcast hashes (e.g., dash gesture clips referenced by a Network Dash " +
                 "instruction). Drop any clips that are referenced only by instruction assets " +
                 "(and therefore never auto-discovered from the character's own animators) " +
                 "into this list. Forwarded to UnitAnimimNetworkController.Initialize.")]
        [SerializeField] private AnimationClip[] m_PreRegisteredAnimationClips;

        [Tooltip("Enable Core feature networking (Ragdoll, Props, Invincibility, Poise, Busy, Interaction)")]
        [SerializeField] private bool m_UseCoreNetworking = true;
        
        [Header("Network Identity")]
        [Tooltip("Use automatic network IDs (transport runtime id if available, otherwise stable scene hash).")]
        [SerializeField] private bool m_UseAutomaticNetworkId = true;
        
        [Tooltip("Manual fallback ID used when automatic IDs are disabled.")]
        [SerializeField] private uint m_ManualNetworkId = 0;
        
        [Tooltip("Optional salt to disambiguate stable IDs for duplicated scene setups.")]
        [SerializeField] private string m_NetworkIdSalt = string.Empty;
        
        [Header("Session")]
        [Tooltip("Optional per-character profile override. If null, uses the active bridge's global profile.")]
        [SerializeField] private NetworkSessionProfile m_SessionProfileOverride;
        
        [Tooltip("Apply near/mid/far relevance tiers for remote characters.")]
        [SerializeField] private bool m_UseRelevanceTiers = true;
        
        [Tooltip("If enabled, host-owner character uses client prediction role. Disable for strict server-authority fairness.")]
        [SerializeField] private bool m_HostOwnerUsesClientPrediction = false;

        [Tooltip("Optional transform used as relevance observer. Defaults to local player or main camera.")]
        [SerializeField] private Transform m_RelevanceObserver;
        
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
        private bool m_RuntimeIsServer;
        private bool m_RuntimeIsOwner;
        private bool m_RuntimeIsHost;
        private uint m_RuntimeNetworkId;
        private bool m_TransportCallbacksWired;
        private float m_ServerSimulationAccumulator;
        private float m_LastStateBroadcastTime = -100f;
        private float m_NextRelevanceUpdateTime;
        private NetworkRelevanceTier m_CurrentRelevanceTier = NetworkRelevanceTier.Near;
        private NetworkSessionProfile m_ResolvedSessionProfile;
        private NetworkTransportBridge m_RegisteredBridge;
        private readonly Dictionary<uint, float> m_LastStateBroadcastPerClient = new Dictionary<uint, float>(32);

        // Network state (manual sync if not using transport)
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
        private bool m_HasCachedFootstepsState;
        private bool m_DefaultFootstepsActive = true;
        private bool m_HasCachedInteractionRadius;
        private float m_DefaultInteractionRadius;
        
        // Core networking controller (Ragdoll, Props, Invincibility, Poise, Busy, Interaction)
        private NetworkCoreController m_CoreController;
        
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
        
        /// <summary>
        /// Fired when local client input is ready for network transport.
        /// </summary>
        public event Action<uint, NetworkInputState[]> OnInputPayloadReady;
        
        /// <summary>
        /// Fired when authoritative state is ready to broadcast.
        /// </summary>
        public event Action<uint, NetworkPositionState, float> OnStatePayloadReady;
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // PROPERTIES
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>The underlying GC2 Character.</summary>
        public Character Character => m_Character;
        
        /// <summary>Current network role of this character.</summary>
        public NetworkRole Role => m_CurrentRole;
        
        /// <summary>Whether this is the local player's character.</summary>
        public bool IsLocalPlayer => m_RuntimeIsOwner;
        
        /// <summary>Whether this is running on the server.</summary>
        public bool IsServerInstance => m_RuntimeIsServer;
        
        /// <summary>Whether this instance owns this character.</summary>
        public bool IsOwnerInstance => m_RuntimeIsOwner;
        
        /// <summary>Whether this instance is host (server+client).</summary>
        public bool IsHostInstance => m_RuntimeIsHost;
        
        /// <summary>Whether this is a remote player's character.</summary>
        public bool IsRemotePlayer => m_CurrentRole == NetworkRole.RemoteClient;
        
        /// <summary>Stable network identifier used across subsystems.</summary>
        public uint NetworkId => m_RuntimeNetworkId;
        
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
        
        /// <summary>Resolved session profile in use by this character.</summary>
        public NetworkSessionProfile SessionProfile => m_ResolvedSessionProfile;
        
    }
    
    // ════════════════════════════════════════════════════════════════════════════════════════════
    // NETWORK STATE STRUCT
    // ════════════════════════════════════════════════════════════════════════════════════════════
    
    /// <summary>
    /// Compact state for network synchronization.
    /// For use with non-provider networking solutions.
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
