using System;
using System.Collections.Generic;
using UnityEngine;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Common;


namespace Arawn.GameCreator2.Networking
{
    public partial class NetworkCharacter
    {
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
            
            m_RuntimeIsServer = false;
            m_RuntimeIsOwner = false;
            m_RuntimeIsHost = false;
            m_RuntimeNetworkId = ResolveNetworkId();
            
            // Cache initial state
            m_LastIsDead = m_Character.IsDead;
            m_LastIsPlayer = m_Character.IsPlayer;
        }
        
        /// <summary>
        /// Manual initialization for non-provider networking solutions.
        /// Call this from your network spawn handler.
        /// </summary>
        /// <param name="isServer">True if this is running on the server.</param>
        /// <param name="isOwner">True if this is the local player's character.</param>
        /// <param name="isHost">True if this is a host (server + client).</param>
        public void InitializeNetworkRole(bool isServer, bool isOwner, bool isHost = false)
        {
            if (m_IsInitialized) return;
            
            m_RuntimeIsServer = isServer;
            m_RuntimeIsOwner = isOwner;
            m_RuntimeIsHost = isHost;
            
            m_CurrentRole = ResolveRole(isServer, isOwner, isHost);
            RefreshNetworkId();

            InitializeForRole();
        }
        
        private void InitializeForRole()
        {
            if (m_IsInitialized) return;
            m_IsInitialized = true;
            
            ResolveSessionProfile();
            
            // Assign appropriate driver
            AssignDriverForRole();
            ApplySessionProfileToDrivers();
            
            // Configure systems based on role
            ConfigureSystemsForRole();
            
            // Setup optional components
            SetupOptionalComponents();
            WireMovementEvents();
            RegisterWithBridge();
            
            // Server optimizations
            if (m_RuntimeIsServer && !m_RuntimeIsOwner && !m_RuntimeIsHost)
            {
                ApplyServerOptimizations();
            }
            
            // Subscribe to character events
            SubscribeToCharacterEvents();
            
            // Register local player with GC2's ShortcutPlayer system
            // so "Get Player" property getters work across the framework
            if (m_RuntimeIsOwner || m_CurrentRole == NetworkRole.LocalClient)
            {
                GameCreator.Runtime.Common.ShortcutPlayer.Change(gameObject);
            }
            
            ApplyCurrentRelevanceTier(force: true);
            
            OnRoleAssigned?.Invoke(m_CurrentRole);
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
                // Use GC2's public driver API.
                var kernel = m_Character.Kernel;
                if (kernel != null)
                {
                    SetCharacterDriver(driver);
                    EnsurePlayerUnitForRole();
                }
                
                OnDriverAssigned?.Invoke(driver);
            }
        }
        
        private UnitDriverNetworkServer CreateServerDriver()
        {
            if (m_Character?.Driver is UnitDriverNetworkServer currentDriver)
            {
                m_ServerDriver = currentDriver;
                return m_ServerDriver;
            }

            m_ServerDriver = new UnitDriverNetworkServer();
            return m_ServerDriver;
        }
        
        private UnitDriverNetworkClient CreateClientDriver()
        {
            if (m_Character?.Driver is UnitDriverNetworkClient currentDriver)
            {
                m_ClientDriver = currentDriver;
                return m_ClientDriver;
            }

            m_ClientDriver = new UnitDriverNetworkClient();
            return m_ClientDriver;
        }
        
        private UnitDriverNetworkRemote CreateRemoteDriver()
        {
            if (m_Character?.Driver is UnitDriverNetworkRemote currentDriver)
            {
                m_RemoteDriver = currentDriver;
                return m_RemoteDriver;
            }

            m_RemoteDriver = new UnitDriverNetworkRemote();
            return m_RemoteDriver;
        }
        
        private void SetCharacterDriver(IUnitDriver driver)
        {
            var kernel = m_Character.Kernel;
            if (kernel == null) return;

            if (driver is TUnitDriver typedDriver)
            {
                // Prefer GC2 public API to avoid brittle reflection on kernel internals.
                kernel.ChangeDriver(m_Character, typedDriver);
                return;
            }

            Debug.LogError(
                $"[NetworkCharacter] Cannot assign driver {driver?.GetType().Name ?? "<null>"} " +
                $"because it is not a {nameof(TUnitDriver)}.");
        }
        
        private void EnsurePlayerUnitForRole()
        {
            if (m_Character?.Kernel == null) return;

            bool isLocalOwner = m_RuntimeIsOwner || m_CurrentRole == NetworkRole.LocalClient;
            SetCharacterPlayerFlag(isLocalOwner);
            
            // Remote and dedicated server instances should not process local player input.
            if (m_CurrentRole == NetworkRole.RemoteClient || (m_CurrentRole == NetworkRole.Server && !m_RuntimeIsOwner))
            {
                m_Character.Kernel.ChangePlayer(m_Character, null);
                return;
            }
            
            // Auto-upgrade common GC2 player units to their network-aware counterparts.
            if (m_CurrentRole != NetworkRole.LocalClient) return;
            
            var currentPlayer = m_Character.Player;
            if (currentPlayer is UnitPlayerDirectionalNetwork ||
                currentPlayer is UnitPlayerPointClickNetwork ||
                currentPlayer is UnitPlayerFollowPointerNetwork ||
                currentPlayer is UnitPlayerTankNetwork)
            {
                return;
            }
            
            TUnitPlayer networkPlayer = currentPlayer switch
            {
                UnitPlayerTank => new UnitPlayerTankNetwork(),
                UnitPlayerPointClick => new UnitPlayerPointClickNetwork(),
                UnitPlayerFollowPointer => new UnitPlayerFollowPointerNetwork(),
                UnitPlayerDirectional => new UnitPlayerDirectionalNetwork(),
                _ => null
            };
            
            if (networkPlayer != null)
            {
                m_Character.Kernel.ChangePlayer(m_Character, networkPlayer);
            }
        }

        private void SetCharacterPlayerFlag(bool isPlayer)
        {
            if (m_Character == null || m_Character.IsPlayer == isPlayer) return;

            GameObject previousShortcut = ShortcutPlayer.Instance;
            m_Character.IsPlayer = isPlayer;

            if (!isPlayer && previousShortcut != null && previousShortcut != gameObject)
            {
                ShortcutPlayer.Change(previousShortcut);
            }
        }
        
        private void ConfigureSystemsForRole()
        {
            switch (m_CurrentRole)
            {
                case NetworkRole.Server:
                    if (m_RuntimeIsOwner)
                    {
                        // Host owner keeps local visual systems active.
                        ConfigureSystemMode(SystemType.Combat, m_CombatMode);
                        break;
                    }
                    
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
            if (m_Character == null || m_Character.Footsteps == null) return;

            if (!m_HasCachedFootstepsState)
            {
                m_DefaultFootstepsActive = m_Character.Footsteps.IsActive;
                m_HasCachedFootstepsState = true;
            }

            switch (mode)
            {
                case RemoteSystemMode.Disabled:
                    m_Character.Footsteps.IsActive = false;
                    break;
                
                case RemoteSystemMode.LocalOnly:
                case RemoteSystemMode.Synchronized:
                default:
                    m_Character.Footsteps.IsActive = m_DefaultFootstepsActive;
                    break;
            }
        }
        
        private void ConfigureInteraction(RemoteSystemMode mode)
        {
            if (m_Character?.Motion == null) return;

            if (!m_HasCachedInteractionRadius)
            {
                m_DefaultInteractionRadius = Mathf.Max(0f, m_Character.Motion.InteractionRadius);
                m_HasCachedInteractionRadius = true;
            }

            switch (mode)
            {
                case RemoteSystemMode.Disabled:
                    m_Character.Motion.InteractionRadius = 0f;
                    break;
                
                case RemoteSystemMode.LocalOnly:
                case RemoteSystemMode.Synchronized:
                default:
                    m_Character.Motion.InteractionRadius = m_DefaultInteractionRadius;
                    break;
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
                    if (m_CombatInterceptor == null && m_RuntimeIsOwner)
                    {
                        m_CombatInterceptor = gameObject.AddComponent<NetworkCombatInterceptor>();
                    }
                    if (m_CombatInterceptor != null)
                    {
                        m_CombatInterceptor.Initialize(
                            isServer: m_RuntimeIsServer,
                            isLocalPlayer: m_RuntimeIsOwner
                        );
                    }
                    break;
                    
                case RemoteSystemMode.Synchronized:
                    // Server: Process all hits authoritatively
                    if (m_CombatInterceptor == null && m_RuntimeIsServer)
                    {
                        m_CombatInterceptor = gameObject.AddComponent<NetworkCombatInterceptor>();
                    }
                    if (m_CombatInterceptor != null)
                    {
                        m_CombatInterceptor.Initialize(
                            isServer: m_RuntimeIsServer,
                            isLocalPlayer: m_RuntimeIsOwner
                        );
                    }
                    
                    // Also setup lag compensation for hit validation
                    if (m_RuntimeIsServer || m_UseLagCompensation)
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
            
            m_LagCompensation.NetworkId = NetworkId;
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
                m_IKController.Initialize(m_Character, m_RuntimeIsOwner);
            }
            
            // Setup motion network controller if enabled (for dash/teleport validation)
            if (m_UseNetworkMotion)
            {
                // Motion controller coordinates with UnitMotionNetworkController on the character
                // It validates dash/teleport requests and syncs motion config changes
                m_MotionController = m_Character.Motion as UnitMotionNetworkController;
                if (m_MotionController != null)
                {
                    m_MotionController.IsServer = m_RuntimeIsServer;
                    NetworkMotionManager.Instance?.RegisterController(m_MotionController);
                }
            }
            
            // Setup lag compensation if enabled (server-side only typically)
            if (m_UseLagCompensation && m_RuntimeIsServer)
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
                m_AnimimController.Initialize(m_Character, m_RuntimeIsOwner);
                m_AnimimController.RegisterClips(m_PreRegisteredAnimationClips);

                if (m_ResolvedSessionProfile != null)
                {
                    var nearSettings = m_ResolvedSessionProfile.GetTierSettings(NetworkRelevanceTier.Near);
                    m_AnimimController.SetRateLimits(nearSettings.animationStateRate, nearSettings.animationGestureRate);
                    m_AnimimController.SetSyncEnabled(nearSettings.syncAnimation);
                }

                NetworkAnimationManager.Instance?.RegisterController(m_AnimimController);
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
                    m_RuntimeIsServer,
                    m_RuntimeIsOwner
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
        
        private NetworkRole ResolveRole(bool isServer, bool isOwner, bool isHost)
        {
            if (isHost && isOwner && m_HostOwnerUsesClientPrediction)
            {
                return NetworkRole.LocalClient;
            }
            
            if (isServer && isOwner)
            {
                return NetworkRole.Server;
            }
            
            if (isServer) return NetworkRole.Server;
            if (isOwner) return NetworkRole.LocalClient;
            return NetworkRole.RemoteClient;
        }
        
        private uint ResolveNetworkId()
        {
            if (!m_UseAutomaticNetworkId)
            {
                return m_ManualNetworkId == 0 ? 1u : m_ManualNetworkId;
            }
            
            string scenePath = gameObject.scene.path;
            string hierarchyPath = BuildHierarchyPath(transform);
            string key = $"{scenePath}|{hierarchyPath}|{m_NetworkIdSalt}";
            uint stableHash = unchecked((uint)StableHashUtility.GetStableHash(key));
            
            return stableHash == 0 ? (uint)(Mathf.Abs(transform.GetInstanceID()) + 1) : stableHash;
        }
        
        private static string BuildHierarchyPath(Transform current)
        {
            if (current == null) return string.Empty;
            
            string path = current.name;
            Transform parent = current.parent;
            while (parent != null)
            {
                path = $"{parent.name}/{path}";
                parent = parent.parent;
            }
            
            return path;
        }
        
        public void RefreshNetworkId()
        {
            uint previousId = m_RuntimeNetworkId;
            uint resolvedId = ResolveNetworkId();
            if (resolvedId == 0) resolvedId = 1;
            
            bool changed = previousId != resolvedId;
            if (changed && m_RegisteredBridge != null && previousId != 0)
            {
                m_RegisteredBridge.UnregisterCharacter(this);
            }
            
            m_RuntimeNetworkId = resolvedId;
            
            if (m_LagCompensation != null)
            {
                m_LagCompensation.NetworkId = m_RuntimeNetworkId;
            }
            
            if (changed && m_RegisteredBridge != null)
            {
                m_RegisteredBridge.RegisterCharacter(this);
            }
        }
        
        public void SetManualNetworkId(uint networkId)
        {
            uint resolvedId = networkId == 0 ? 1u : networkId;
            uint previousId = m_RuntimeNetworkId;

            m_ManualNetworkId = resolvedId;
            m_UseAutomaticNetworkId = false;
            if (previousId == resolvedId) return;

            bool shouldReregister = m_RegisteredBridge != null && m_TransportCallbacksWired && previousId != 0;
            if (shouldReregister)
            {
                m_RegisteredBridge.UnregisterCharacter(this);
            }

            m_RuntimeNetworkId = resolvedId;

            if (m_LagCompensation != null)
            {
                m_LagCompensation.NetworkId = m_RuntimeNetworkId;
            }

            if (shouldReregister)
            {
                m_RegisteredBridge.RegisterCharacter(this);
            }
        }

        internal void ApplyServerIssuedNetworkId(uint networkId)
        {
            uint resolvedId = networkId == 0 ? 1u : networkId;
            if (m_RuntimeNetworkId == resolvedId) return;

            m_RuntimeNetworkId = resolvedId;

            if (m_LagCompensation != null)
            {
                m_LagCompensation.NetworkId = m_RuntimeNetworkId;
            }
        }
        
        private void ResolveSessionProfile()
        {
            m_ResolvedSessionProfile = m_SessionProfileOverride;
            if (m_ResolvedSessionProfile == null && NetworkTransportBridge.HasActive)
            {
                m_ResolvedSessionProfile = NetworkTransportBridge.Active.GlobalSessionProfile;
            }
        }
        
        private void ApplySessionProfileToDrivers()
        {
            if (m_ResolvedSessionProfile == null) return;
            
            m_ClientDriver?.ApplySessionProfile(m_ResolvedSessionProfile);
            m_ServerDriver?.ApplySessionProfile(m_ResolvedSessionProfile);
            
            if (m_RemoteDriver != null)
            {
                var nearSettings = m_ResolvedSessionProfile.GetTierSettings(NetworkRelevanceTier.Near);
                m_RemoteDriver.ApplyTierSettings(nearSettings);
            }
        }
        
        private void WireMovementEvents()
        {
            if (m_ClientDriver != null)
            {
                m_ClientDriver.OnSendInput -= OnClientInputReady;
                m_ClientDriver.OnSendInput += OnClientInputReady;
            }
            
            if (m_ServerDriver != null)
            {
                m_ServerDriver.OnStateProduced -= OnServerStateProduced;
                m_ServerDriver.OnStateProduced += OnServerStateProduced;
            }
        }
        
        private void UnwireMovementEvents()
        {
            if (m_ClientDriver != null)
            {
                m_ClientDriver.OnSendInput -= OnClientInputReady;
            }
            
            if (m_ServerDriver != null)
            {
                m_ServerDriver.OnStateProduced -= OnServerStateProduced;
            }
        }
        
        private void RegisterWithBridge()
        {
            UnregisterFromBridge();
            
            NetworkTransportBridge bridge = NetworkTransportBridge.Active;
            if (bridge == null) return;
            
            m_RegisteredBridge = bridge;
            m_RegisteredBridge.RegisterCharacter(this);
            m_RegisteredBridge.OnInputReceivedServer += OnBridgeInputReceivedServer;
            m_RegisteredBridge.OnStateReceivedClient += OnBridgeStateReceivedClient;
            m_TransportCallbacksWired = true;
            
            if (m_ResolvedSessionProfile == null && m_RegisteredBridge.GlobalSessionProfile != null)
            {
                m_ResolvedSessionProfile = m_RegisteredBridge.GlobalSessionProfile;
                ApplySessionProfileToDrivers();
            }
        }
        
        private void UnregisterFromBridge()
        {
            if (m_RegisteredBridge == null) return;
            
            if (m_TransportCallbacksWired)
            {
                m_RegisteredBridge.OnInputReceivedServer -= OnBridgeInputReceivedServer;
                m_RegisteredBridge.OnStateReceivedClient -= OnBridgeStateReceivedClient;
                m_TransportCallbacksWired = false;
            }
            
            m_RegisteredBridge.UnregisterCharacter(this);
            m_RegisteredBridge = null;
        }
        
        private void OnClientInputReady(NetworkInputState[] inputs)
        {
            if (inputs == null || inputs.Length == 0) return;

            OnInputPayloadReady?.Invoke(NetworkId, inputs);
            
            if (NetworkTransportBridge.HasActive)
            {
                NetworkTransportBridge.Active.SendToServer(NetworkId, inputs);
                return;
            }
        }
        
        private void OnServerStateProduced(NetworkPositionState state)
        {
            if (!m_RuntimeIsServer) return;
            
            float broadcastRate = m_ResolvedSessionProfile != null
                ? Mathf.Max(1f, m_ResolvedSessionProfile.serverStateBroadcastRate)
                : 20f;
            
            float minInterval = 1f / broadcastRate;
            if (Time.time - m_LastStateBroadcastTime < minInterval)
            {
                return;
            }
            
            float serverTime = NetworkTransportBridge.HasActive
                ? NetworkTransportBridge.Active.ServerTime
                : Time.time;
            
            OnStatePayloadReady?.Invoke(NetworkId, state, serverTime);

            if (NetworkTransportBridge.HasActive)
            {
                NetworkTransportBridge.Active.Broadcast(
                    NetworkId,
                    state,
                    serverTime,
                    relevanceFilter: ShouldBroadcastStateToClient
                );
            }
            
            m_LastStateBroadcastTime = Time.time;
        }

        private void PublishHostLocalClientState()
        {
            if (!m_RuntimeIsServer) return;
            if (m_CurrentRole != NetworkRole.LocalClient) return;
            if (m_ClientDriver == null) return;

            OnServerStateProduced(m_ClientDriver.GetCurrentState());
        }
        
        private void OnBridgeInputReceivedServer(uint senderClientId, uint characterNetworkId, NetworkInputState[] inputs)
        {
            if (!m_RuntimeIsServer) return;
            if (characterNetworkId != NetworkId) return;
            if (inputs == null || inputs.Length == 0) return;

            if (m_RegisteredBridge != null &&
                m_RegisteredBridge.TryGetCharacterOwner(characterNetworkId, out uint ownerClientId) &&
                ownerClientId != senderClientId)
            {
                Debug.LogWarning($"[NetworkCharacter] Rejected input for {name} ({characterNetworkId}) from sender {senderClientId}; owner is {ownerClientId}.");
                return;
            }
            
            if (m_ServerDriver == null)
            {
                return;
            }
            
            for (int i = 0; i < inputs.Length; i++)
            {
                m_ServerDriver.QueueInput(inputs[i]);
            }

        }

        private bool ShouldBroadcastStateToClient(uint targetClientId, uint characterNetworkId, NetworkPositionState state, float serverTime)
        {
            if (!m_UseRelevanceTiers || m_ResolvedSessionProfile == null) return true;
            if (m_RegisteredBridge == null) return true;

            if (m_RegisteredBridge.TryGetCharacterOwner(characterNetworkId, out uint ownerClientId) &&
                ownerClientId == targetClientId)
            {
                return true;
            }

            if (!TryGetObserverPositionForClient(targetClientId, out Vector3 observerPosition))
            {
                if (m_ResolvedSessionProfile.requireObserverCharacterForRelevance)
                {
                    return false;
                }

                // Fall back to far-tier throttling when observer lookup is unavailable.
                NetworkRelevanceSettings fallbackSettings = m_ResolvedSessionProfile.GetTierSettings(NetworkRelevanceTier.Far);
                return TryPassPerClientBroadcastRate(targetClientId, fallbackSettings.stateApplyRate);
            }

            float distance = Vector3.Distance(observerPosition, transform.position);

            if (m_ResolvedSessionProfile.enableDistanceCulling &&
                distance > m_ResolvedSessionProfile.cullDistance)
            {
                return TryPassPerClientBroadcastRate(targetClientId, m_ResolvedSessionProfile.culledKeepAliveRate);
            }

            NetworkRelevanceTier tier = m_ResolvedSessionProfile.GetTier(distance);
            NetworkRelevanceSettings tierSettings = m_ResolvedSessionProfile.GetTierSettings(tier);
            return TryPassPerClientBroadcastRate(targetClientId, tierSettings.stateApplyRate);
        }

        private bool TryGetObserverPositionForClient(uint targetClientId, out Vector3 observerPosition)
        {
            observerPosition = Vector3.zero;
            if (m_RegisteredBridge == null) return false;
            if (!m_RegisteredBridge.TryGetRepresentativeCharacterId(targetClientId, out uint observerCharacterId)) return false;

            Character observer = m_RegisteredBridge.ResolveCharacter(observerCharacterId);
            if (observer == null) return false;

            observerPosition = observer.transform.position;
            return true;
        }

        private bool TryPassPerClientBroadcastRate(uint targetClientId, float sendRateHz)
        {
            if (sendRateHz <= 0f)
            {
                return false;
            }

            float minInterval = 1f / Mathf.Max(0.01f, sendRateHz);
            if (m_LastStateBroadcastPerClient.TryGetValue(targetClientId, out float lastBroadcastTime))
            {
                if (Time.time - lastBroadcastTime < minInterval)
                {
                    return false;
                }
            }

            m_LastStateBroadcastPerClient[targetClientId] = Time.time;
            return true;
        }
        
        private void OnBridgeStateReceivedClient(uint characterNetworkId, NetworkPositionState state, float serverTime)
        {
            if (characterNetworkId != NetworkId) return;

            if (m_NetworkFacingUnit != null)
            {
                m_NetworkFacingUnit.OnServerYawReceived(state.GetRotationY());
            }

            // The host owner (server + owner running with client-side prediction) publishes
            // its own state via PublishHostLocalClientState. PurrNet then loops that broadcast
            // back to this character's bridge listener. Reconciling against a state that
            // originated from ourselves is not only redundant -- it causes visible regressions
            // for impulses that bypass the input stream (e.g., Network Dash drives motion via
            // Driver.AddPosition rather than NetworkInputState). The broadcast captures the
            // current transform AFTER the dash impulse was added, but the prediction history
            // at the matching input sequence was captured BEFORE the impulse, so reconciliation
            // teleports the host back and only replays WASD inputs -- losing the dash distance.
            // Skip self-reconciliation entirely when this owner is also the server.
            if (m_RuntimeIsServer && m_RuntimeIsOwner)
            {
                return;
            }

            if (m_ClientDriver != null)
            {
                m_ClientDriver.ApplyServerState(state);
                return;
            }
            
            if (m_RemoteDriver != null)
            {
                m_RemoteDriver.AddSnapshot(state, serverTime);
                m_RemoteDriver.SetServerTime(serverTime);
                return;
            }
            
        }
        
        private void ProcessServerSimulation(float deltaTime)
        {
            if (m_ServerDriver == null) return;
            
            float simulationRate = m_ResolvedSessionProfile != null
                ? Mathf.Max(1f, m_ResolvedSessionProfile.serverSimulationRate)
                : 30f;
            
            float tickInterval = 1f / simulationRate;
            m_ServerSimulationAccumulator += deltaTime;
            
            int tickCount = 0;
            while (m_ServerSimulationAccumulator >= tickInterval && tickCount < 4)
            {
                m_ServerSimulationAccumulator -= tickInterval;
                m_ServerDriver.ProcessInputs();
                tickCount++;
            }
        }
        
        private void ApplyCurrentRelevanceTier(bool force = false)
        {
            if (!m_UseRelevanceTiers || m_ResolvedSessionProfile == null) return;
            if (m_CurrentRole != NetworkRole.RemoteClient) return;
            
            if (!force && Time.time < m_NextRelevanceUpdateTime) return;
            
            float relevanceRate = Mathf.Max(0.5f, m_ResolvedSessionProfile.relevanceUpdateRate);
            m_NextRelevanceUpdateTime = Time.time + (1f / relevanceRate);
            
            Transform observer = GetRelevanceObserver();
            if (observer == null) return;
            
            float distance = Vector3.Distance(observer.position, transform.position);
            NetworkRelevanceTier tier = m_ResolvedSessionProfile.GetTier(distance);
            if (!force && tier == m_CurrentRelevanceTier) return;
            
            m_CurrentRelevanceTier = tier;
            NetworkRelevanceSettings settings = m_ResolvedSessionProfile.GetTierSettings(tier);
            
            m_RemoteDriver?.ApplyTierSettings(settings);
            
            if (m_IKController != null)
            {
                m_IKController.enabled = m_UseNetworkIK && settings.syncIK && m_IKMode == RemoteSystemMode.Synchronized;
            }
            
            if (m_AnimimController != null)
            {
                m_AnimimController.SetSyncEnabled(settings.syncAnimation);
                m_AnimimController.SetRateLimits(settings.animationStateRate, settings.animationGestureRate);
            }
            
            if (m_CoreController != null)
            {
                m_CoreController.enabled = m_UseCoreNetworking && settings.syncCore;
            }
            
            if (m_CombatInterceptor != null)
            {
                m_CombatInterceptor.enabled = settings.syncCombat;
            }
        }
        
        private Transform GetRelevanceObserver()
        {
            if (m_RelevanceObserver != null) return m_RelevanceObserver;
            if (ShortcutPlayer.Transform != null) return ShortcutPlayer.Transform;
            if (Camera.main != null) return Camera.main.transform;
            return null;
        }
        
    }
}
