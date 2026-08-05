using System.Collections.Generic;
using PurrNet;
using PurrNet.Transports;
using UnityEngine;
using Arawn.NetworkingCore.LagCompensation;
using Arawn.GameCreator2.Networking.Security;

namespace Arawn.GameCreator2.Networking.Transport.PurrNet
{
    /// <summary>
    /// PurrNet implementation of the GC2 <see cref="NetworkTransportBridge"/>.
    ///
    /// Wiring:
    ///  - Place this component in your scene alongside a <see cref="NetworkManager"/>.
    ///  - Optionally drag the NetworkManager reference in the inspector; otherwise
    ///    <see cref="NetworkManager.main"/> is used at runtime.
    ///  - The bridge subscribes to two broadcast types on both server and client:
    ///      <see cref="GC2InputBroadcast"/>  (client -> server)
    ///      <see cref="GC2StateBroadcast"/>  (server -> clients)
    ///  - Ownership is auto-resolved from a sibling <see cref="NetworkIdentity"/>
    ///    when the spawned character's root has one. Override this with
    ///    <see cref="NetworkTransportBridge.SetCharacterOwner"/> if you don't use
    ///    NetworkIdentity for ownership.
    /// </summary>
    [AddComponentMenu("Game Creator/Network/Transport/PurrNet Transport Bridge")]
    [DefaultExecutionOrder(-400)]
    public sealed class PurrNetTransportBridge : NetworkTransportBridge
    {
        [Header("PurrNet Transport")]
        [InspectorName("PurrNet Network Manager (Optional Scene Override)")]
        [Tooltip("Optional PurrNet.NetworkManager scene-instance override. Leave empty to use NetworkManager.main.")]
        [SerializeField] private NetworkManager m_NetworkManager;

        [Tooltip("Delivery channel used for client->server input broadcasts.")]
        [SerializeField] private Channel m_InputChannel = Channel.UnreliableSequenced;

        [Tooltip("Delivery channel used for server->client state broadcasts.")]
        [SerializeField] private Channel m_StateChannel = Channel.UnreliableSequenced;

        private bool m_SubscribedServer;
        private bool m_SubscribedClient;
        private NetworkManager m_HookedManager;
        private PurrNetCoreTransportBridge m_CoreTransportBridge;
        private PurrNetAnimationMotionTransportBridge m_AnimationMotionBridge;
        private LagCompensationBootstrap m_LagCompensationBootstrap;
        private readonly HashSet<uint> m_ConnectedClientIds = new HashSet<uint>();

        private NetworkManager ActiveManager
        {
            get
            {
                if (m_NetworkManager != null) return m_NetworkManager;
                NetworkManager main = NetworkManager.main;
                return main != null ? main : null;
            }
        }

        /// <summary>
        /// The scene override selected by this bridge, or PurrNet's main manager when no
        /// override is assigned. Session UI and GC2 Inspector entries use this property so
        /// they cannot accidentally control a different manager than the gameplay bridge.
        /// </summary>
        public NetworkManager ActiveNetworkManager => ActiveManager;

        public override bool IsServer => ActiveManager != null && ActiveManager.isServer;
        public override bool IsClient => ActiveManager != null && ActiveManager.isClient;
        public override bool IsHost => ActiveManager != null && ActiveManager.isHost;
        public override bool IsRunning => IsServer || IsClient;
        public override IReadOnlyCollection<uint> ConnectedClientIds => m_ConnectedClientIds;

        public override bool TryGetLocalClientId(out uint clientId)
        {
            clientId = InvalidClientId;
            NetworkManager manager = ActiveManager;
            if (manager == null || !manager.isLocalPlayerReady) return false;

            clientId = PlayerIdToClientId(manager.localPlayer);
            return IsValidClientId(clientId);
        }

        public override float ServerTime
        {
            get
            {
                var nm = ActiveManager;
                if (nm != null && nm.tickModule != null)
                {
                    return nm.tickModule.PreciseTickToTime(nm.tickModule.syncedPreciseTick);
                }

                return Time.time;
            }
        }

        // ------------------------------------------------------------------
        // Lifecycle
        // ------------------------------------------------------------------

        protected override void Awake()
        {
            base.Awake();
            EnsureLagCompensationBootstrap();
            EnsureCoreTransportBridge();
            EnsureAnimationMotionBridge();
        }

        private void OnEnable()
        {
            EnsureLagCompensationBootstrap();
            EnsureCoreTransportBridge();
            EnsureAnimationMotionBridge();
            TryHookNetworkManager();
        }

        private void Start()
        {
            // NetworkManager.main may not be assigned until after the manager's Awake.
            EnsureLagCompensationBootstrap();
            EnsureCoreTransportBridge();
            EnsureAnimationMotionBridge();
            TryHookNetworkManager();
        }

        private void Update()
        {
            // NetworkManager.main may be published after this bridge starts (for
            // example by an additive bootstrap scene). It can also be replaced
            // between sessions. Rebind only when the resolved instance changes.
            if (!ReferenceEquals(m_HookedManager, ActiveManager))
            {
                TryHookNetworkManager();
            }

            RebuildConnectedClients();
        }

        protected override void OnDisable()
        {
            UnhookNetworkManager();
            base.OnDisable();
        }

        private void UnhookNetworkManager()
        {
            var nm = m_HookedManager;
            bool ownedServerLifecycle = m_SubscribedServer || (nm != null && nm.isServer);
            if (nm != null)
            {
                nm.onNetworkStarted -= HandleNetworkStarted;
                nm.onNetworkShutdown -= HandleNetworkShutdown;
                nm.onPlayerJoined -= HandlePlayerJoined;
                nm.onPlayerLeft -= HandlePlayerLeft;

                if (m_SubscribedServer)
                {
                    nm.Unsubscribe<GC2InputBroadcast>(HandleInputBroadcastServer, true);
                    m_SubscribedServer = false;
                }

                if (m_SubscribedClient)
                {
                    nm.Unsubscribe<GC2StateBroadcast>(HandleStateBroadcastClient, false);
                    m_SubscribedClient = false;
                }
            }

            // A destroyed Unity object compares equal to null, so make sure local
            // subscription state is still cleared even when callbacks cannot be removed.
            m_SubscribedServer = false;
            m_SubscribedClient = false;
            m_HookedManager = null;
            m_ConnectedClientIds.Clear();

            if (ownedServerLifecycle)
            {
                m_LagCompensationBootstrap?.SetServerMode(false);
            }
        }

        private void TryHookNetworkManager()
        {
            var nm = ActiveManager;
            if (ReferenceEquals(m_HookedManager, nm)) return;

            UnhookNetworkManager();
            if (nm == null) return;

            m_HookedManager = nm;

            nm.onNetworkStarted += HandleNetworkStarted;
            nm.onNetworkShutdown += HandleNetworkShutdown;
            nm.onPlayerJoined += HandlePlayerJoined;
            nm.onPlayerLeft += HandlePlayerLeft;

            // If the manager is already running when we hook in, subscribe immediately.
            if (nm.isServer) HandleNetworkStarted(nm, true);
            if (nm.isClient) HandleNetworkStarted(nm, false);
            RebuildConnectedClients();

            EnsureLagCompensationBootstrap();
            EnsureCoreTransportBridge();
            EnsureAnimationMotionBridge();
        }

        private void EnsureCoreTransportBridge()
        {
            if (m_CoreTransportBridge != null)
            {
                m_CoreTransportBridge.Configure(ActiveManager);
                return;
            }

#if UNITY_2023_1_OR_NEWER
            m_CoreTransportBridge = FindFirstObjectByType<PurrNetCoreTransportBridge>();
#else
            m_CoreTransportBridge = FindObjectOfType<PurrNetCoreTransportBridge>();
#endif

            if (m_CoreTransportBridge == null)
            {
                m_CoreTransportBridge = gameObject.AddComponent<PurrNetCoreTransportBridge>();
            }

            m_CoreTransportBridge.Configure(ActiveManager);
        }

        private void EnsureAnimationMotionBridge()
        {
            if (m_AnimationMotionBridge != null)
            {
                m_AnimationMotionBridge.Configure(ActiveManager);
                return;
            }

#if UNITY_2023_1_OR_NEWER
            m_AnimationMotionBridge = FindFirstObjectByType<PurrNetAnimationMotionTransportBridge>();
#else
            m_AnimationMotionBridge = FindObjectOfType<PurrNetAnimationMotionTransportBridge>();
#endif

            if (m_AnimationMotionBridge == null)
            {
                m_AnimationMotionBridge = gameObject.AddComponent<PurrNetAnimationMotionTransportBridge>();
            }

            m_AnimationMotionBridge.Configure(ActiveManager);
        }

        private void EnsureLagCompensationBootstrap()
        {
            if (m_LagCompensationBootstrap == null)
            {
#if UNITY_2023_1_OR_NEWER
                m_LagCompensationBootstrap = FindFirstObjectByType<LagCompensationBootstrap>(
                    FindObjectsInactive.Include);
#else
                m_LagCompensationBootstrap = FindObjectOfType<LagCompensationBootstrap>(true);
#endif
            }

            if (m_LagCompensationBootstrap == null)
            {
                m_LagCompensationBootstrap = gameObject.AddComponent<LagCompensationBootstrap>();
            }

            m_LagCompensationBootstrap.GetServerTimeFunc = GetLagCompensationServerTime;

            if (ActiveManager != null && ActiveManager.isServer)
            {
                StartServerLagCompensation();
            }
        }

        private void StartServerLagCompensation()
        {
            if (m_LagCompensationBootstrap == null) return;

            m_LagCompensationBootstrap.SetServerMode(true);

            // Characters can have initialized before a restarted network session replaced the
            // manager. Configure/Register is idempotent and rebinds those existing adapters.
#if UNITY_2023_1_OR_NEWER
            CharacterLagCompensation[] adapters = FindObjectsByType<CharacterLagCompensation>(
                FindObjectsInactive.Exclude,
                FindObjectsSortMode.None);
#else
            CharacterLagCompensation[] adapters = FindObjectsOfType<CharacterLagCompensation>();
#endif
            for (int i = 0; i < adapters.Length; i++)
            {
                NetworkCharacter networkCharacter = adapters[i].GetComponent<NetworkCharacter>();
                if (networkCharacter == null ||
                    !networkCharacter.IsServerInstance ||
                    networkCharacter.NetworkId == 0)
                {
                    continue;
                }
                adapters[i].NetworkId = networkCharacter.NetworkId;
                adapters[i].Register();
            }
        }

        private double GetLagCompensationServerTime()
        {
            return ServerTime;
        }

        private void HandleNetworkStarted(NetworkManager manager, bool asServer)
        {
            if (asServer && !m_SubscribedServer)
            {
                EnsureLagCompensationBootstrap();
                StartServerLagCompensation();
                manager.Subscribe<GC2InputBroadcast>(HandleInputBroadcastServer, true);
                m_SubscribedServer = true;
            }
            else if (!asServer && !m_SubscribedClient)
            {
                manager.Subscribe<GC2StateBroadcast>(HandleStateBroadcastClient, false);
                m_SubscribedClient = true;
            }
        }

        private void HandleNetworkShutdown(NetworkManager manager, bool asServer)
        {
            if (asServer && m_SubscribedServer)
            {
                manager.Unsubscribe<GC2InputBroadcast>(HandleInputBroadcastServer, true);
                m_SubscribedServer = false;
                m_LagCompensationBootstrap?.SetServerMode(false);
            }
            else if (!asServer && m_SubscribedClient)
            {
                manager.Unsubscribe<GC2StateBroadcast>(HandleStateBroadcastClient, false);
                m_SubscribedClient = false;
            }

            if (!manager.isServer && !manager.isClient)
            {
                m_ConnectedClientIds.Clear();
            }
        }

        private void HandlePlayerJoined(PlayerID player, bool isReconnect, bool asServer)
        {
            uint clientId = PlayerIdToClientId(player);
            if (IsValidClientId(clientId))
            {
                m_ConnectedClientIds.Add(clientId);
            }
        }

        private void HandlePlayerLeft(PlayerID player, bool asServer)
        {
            if (!asServer) return;

            uint clientId = PlayerIdToClientId(player);
            if (!IsValidClientId(clientId)) return;

            m_ConnectedClientIds.Remove(clientId);

            // A reconnect can reuse the same PurrNet PlayerID while its controllers restart
            // their request counters. Drop all per-client replay/rate-limit state with the
            // connection so the new incarnation is not mistaken for replay traffic.
            NetworkSecurityManager.Instance?.OnClientDisconnected(clientId);
        }

        private void RebuildConnectedClients()
        {
            NetworkManager manager = ActiveManager;
            if (manager == null || (!manager.isServer && !manager.isClient))
            {
                m_ConnectedClientIds.Clear();
                return;
            }

            var players = manager.players;
            if (players == null)
            {
                m_ConnectedClientIds.Clear();
                return;
            }

            m_ConnectedClientIds.Clear();
            for (int i = 0; i < players.Count; i++)
            {
                uint clientId = PlayerIdToClientId(players[i]);
                if (IsValidClientId(clientId))
                {
                    m_ConnectedClientIds.Add(clientId);
                }
            }
        }

        // ------------------------------------------------------------------
        // Outbound sends
        // ------------------------------------------------------------------

        public override void SendToServer(uint characterNetworkId, NetworkInputState[] inputs)
        {
            var nm = ActiveManager;
            if (nm == null || !nm.isClient) return;
            if (characterNetworkId == 0 || inputs == null || inputs.Length == 0) return;

            var packet = new GC2InputBroadcast
            {
                characterNetworkId = characterNetworkId,
                inputs = inputs
            };

            if (NetworkTraversalClimbDiagnostics.IsFocused(characterNetworkId))
            {
                NetworkInputState last = inputs[inputs.Length - 1];
                int ownerPoseCount = CountOwnerPoseInputs(inputs, out NetworkInputState latestOwnerPose);
                NetworkTraversalClimbDiagnostics.Log(
                    "PurrNetInputSend",
                    $"actor={characterNetworkId} path={(nm.isServer ? "host-loopback" : "client-server")} " +
                    $"count={inputs.Length} firstSeq={inputs[0].sequenceNumber} lastSeq={last.sequenceNumber} " +
                    $"ownerPoseCount={ownerPoseCount} latestOwnerSeq={(ownerPoseCount > 0 ? latestOwnerPose.sequenceNumber : 0)} " +
                    $"ownerPose={(ownerPoseCount > 0 ? NetworkTraversalClimbDiagnostics.Vector(latestOwnerPose.GetOwnerAuthorityPosition()) : "none")} " +
                    $"hasTraversalDirection={(ownerPoseCount > 0 && latestOwnerPose.HasTraversalPresentationDirection)} " +
                    $"traversalDirection={(ownerPoseCount > 0 && latestOwnerPose.HasTraversalPresentationDirection ? NetworkTraversalClimbDiagnostics.Vector(latestOwnerPose.GetTraversalPresentationDirection()) : "none")}",
                    this,
                    $"purrnet-input-send:{characterNetworkId}");
            }

            // Host shortcut: feed input directly into the server pipeline without a network hop.
            if (nm.isServer)
            {
                if (nm.isLocalPlayerReady)
                {
                    var localId = nm.localPlayer;
                    PrimeLocalOwnerIfNeeded(characterNetworkId, PlayerIdToClientId(localId));
                    DispatchInputOnServer(localId, packet);
                }
                return;
            }

            nm.SendToServer(packet, m_InputChannel);
        }

        public override void SendToOwner(uint ownerClientId, uint characterNetworkId, NetworkPositionState state, float serverTime)
        {
            var nm = ActiveManager;
            if (nm == null || !nm.isServer) return;
            if (!IsValidClientId(ownerClientId) || characterNetworkId == 0) return;

            if (!TryGetPlayerId(nm, ownerClientId, out var playerId)) return;

            var packet = new GC2StateBroadcast
            {
                characterNetworkId = characterNetworkId,
                state = state,
                serverTime = serverTime
            };

            if (NetworkTraversalClimbDiagnostics.IsFocused(characterNetworkId))
            {
                NetworkTraversalClimbDiagnostics.Log(
                    "PurrNetStateSend",
                    $"actor={characterNetworkId} targetOwner={ownerClientId} seq={state.lastProcessedInput} " +
                    $"pos={NetworkTraversalClimbDiagnostics.Vector(state.GetPosition())} " +
                    $"moveVelocity={NetworkTraversalClimbDiagnostics.Vector(state.GetMoveVelocity())} " +
                    $"serverTime={serverTime:F3}",
                    this,
                    $"purrnet-state-send:{characterNetworkId}");
            }

            nm.Send(playerId, packet, m_StateChannel);
        }

        public override void Broadcast(
            uint characterNetworkId,
            NetworkPositionState state,
            float serverTime,
            uint excludeClientId = uint.MaxValue,
            NetworkRecipientFilter relevanceFilter = null)
        {
            var nm = ActiveManager;
            if (nm == null || !nm.isServer) return;
            if (characterNetworkId == 0) return;

            var packet = new GC2StateBroadcast
            {
                characterNetworkId = characterNetworkId,
                state = state,
                serverTime = serverTime
            };

            var players = nm.players;
            if (players == null || players.Count == 0) return;

            // Fast path: no per-recipient filtering needed, push to all.
            if (excludeClientId == InvalidClientId && relevanceFilter == null && RecipientRelevanceFilter == null)
            {
                nm.SendToAll(packet, m_StateChannel);
                return;
            }

            for (int i = 0; i < players.Count; i++)
            {
                var pid = players[i];
                uint clientId = PlayerIdToClientId(pid);
                if (!IsValidClientId(clientId)) continue;
                if (clientId == excludeClientId) continue;
                if (!ShouldSendToClient(clientId, characterNetworkId, state, serverTime, relevanceFilter)) continue;

                nm.Send(pid, packet, m_StateChannel);
            }
        }

        // ------------------------------------------------------------------
        // Ownership resolution
        // ------------------------------------------------------------------

        protected override bool TryResolveOwnerClientId(NetworkCharacter networkCharacter, out uint ownerClientId)
        {
            ownerClientId = 0;
            if (networkCharacter == null) return false;

            var identity = networkCharacter.GetComponentInParent<NetworkIdentity>();
            if (identity == null)
            {
                return false;
            }

            PlayerID ownerPlayer = default;
            var autoInit = networkCharacter.GetComponentInParent<PurrNetNetworkCharacterAuto>();
            bool hasOwner = autoInit != null && autoInit.TryGetSpawnedOwnerHint(out ownerPlayer);

            if (!hasOwner)
            {
                if (!identity.owner.HasValue)
                {
                    return false;
                }

                ownerPlayer = identity.owner.Value;
                hasOwner = true;
            }

            if (!hasOwner)
            {
                return false;
            }

            ownerClientId = PlayerIdToClientId(ownerPlayer);
            bool valid = IsValidClientId(ownerClientId);
            return valid;
        }

        protected override bool TryResolveServerIssuedNetworkId(NetworkCharacter networkCharacter, out uint networkId)
        {
            return TryResolvePurrNetNetworkId(networkCharacter, out networkId);
        }

        private bool TryResolvePurrNetNetworkId(NetworkCharacter networkCharacter, out uint networkId)
        {
            networkId = 0;
            if (networkCharacter == null) return false;

            var identity = networkCharacter.GetComponentInParent<NetworkIdentity>();
            if (identity == null || !identity.isSpawned || identity.objectId >= uint.MaxValue)
            {
                return false;
            }

            // GC2 treats network id 0 as invalid, while PurrNet can assign object id 0.
            // Offset all PurrNet object ids by one so every spawned identity has a
            // stable non-zero GC2 id on every peer.
            networkId = (uint)(identity.objectId + 1UL);
            return networkId != 0;
        }

        private void PrimeLocalOwnerIfNeeded(uint characterNetworkId, uint ownerClientId)
        {
            if (characterNetworkId == 0 || !IsValidClientId(ownerClientId)) return;
            if (TryGetCharacterOwner(characterNetworkId, out _)) return;

            if (TryResolveNetworkCharacter(characterNetworkId, out var networkCharacter) &&
                networkCharacter != null &&
                networkCharacter.IsOwnerInstance)
            {
                SetCharacterOwner(characterNetworkId, ownerClientId);
            }
        }

        // ------------------------------------------------------------------
        // Inbound handlers
        // ------------------------------------------------------------------

        private void HandleInputBroadcastServer(PlayerID senderPlayer, GC2InputBroadcast data, bool asServer)
        {
            if (!asServer) return;
            DispatchInputOnServer(senderPlayer, data);
        }

        private void DispatchInputOnServer(PlayerID senderPlayer, GC2InputBroadcast data)
        {
            if (data.characterNetworkId == 0 || data.inputs == null || data.inputs.Length == 0) return;

            ulong raw = senderPlayer.id;
            if (!TryConvertSenderClientId(raw, out uint senderClientId))
            {
                return;
            }
            if (!TryAcceptInputFromSender(senderClientId, data.characterNetworkId))
            {
                // TryAcceptInputFromSender already logs warnings; nothing extra here.
                return;
            }

            if (NetworkTraversalClimbDiagnostics.IsFocused(data.characterNetworkId))
            {
                NetworkInputState last = data.inputs[data.inputs.Length - 1];
                int ownerPoseCount = CountOwnerPoseInputs(data.inputs, out NetworkInputState latestOwnerPose);
                NetworkTraversalClimbDiagnostics.Log(
                    "PurrNetInputReceive",
                    $"actor={data.characterNetworkId} sender={senderClientId} accepted=true " +
                    $"count={data.inputs.Length} firstSeq={data.inputs[0].sequenceNumber} lastSeq={last.sequenceNumber} " +
                    $"ownerPoseCount={ownerPoseCount} latestOwnerSeq={(ownerPoseCount > 0 ? latestOwnerPose.sequenceNumber : 0)} " +
                    $"ownerPose={(ownerPoseCount > 0 ? NetworkTraversalClimbDiagnostics.Vector(latestOwnerPose.GetOwnerAuthorityPosition()) : "none")} " +
                    $"hasTraversalDirection={(ownerPoseCount > 0 && latestOwnerPose.HasTraversalPresentationDirection)} " +
                    $"traversalDirection={(ownerPoseCount > 0 && latestOwnerPose.HasTraversalPresentationDirection ? NetworkTraversalClimbDiagnostics.Vector(latestOwnerPose.GetTraversalPresentationDirection()) : "none")}",
                    this,
                    $"purrnet-input-receive:{data.characterNetworkId}");
            }

            RaiseInputReceivedServer(senderClientId, data.characterNetworkId, data.inputs);
        }

        private void HandleStateBroadcastClient(PlayerID senderPlayer, GC2StateBroadcast data, bool asServer)
        {
            if (asServer) return;
            if (data.characterNetworkId == 0) return;

            if (NetworkTraversalClimbDiagnostics.IsFocused(data.characterNetworkId))
            {
                NetworkTraversalClimbDiagnostics.Log(
                    "PurrNetStateReceive",
                    $"actor={data.characterNetworkId} sender={senderPlayer.id} " +
                    $"seq={data.state.lastProcessedInput} pos={NetworkTraversalClimbDiagnostics.Vector(data.state.GetPosition())} " +
                    $"moveVelocity={NetworkTraversalClimbDiagnostics.Vector(data.state.GetMoveVelocity())} " +
                    $"serverTime={data.serverTime:F3}",
                    this,
                    $"purrnet-state-receive:{data.characterNetworkId}");
            }

            RaiseStateReceivedClient(data.characterNetworkId, data.state, data.serverTime);
        }

        private static int CountOwnerPoseInputs(
            NetworkInputState[] inputs,
            out NetworkInputState latestOwnerPose)
        {
            latestOwnerPose = default;
            if (inputs == null) return 0;

            int count = 0;
            for (int i = 0; i < inputs.Length; i++)
            {
                if (!inputs[i].HasOwnerAuthorityPosition) continue;
                latestOwnerPose = inputs[i];
                count++;
            }

            return count;
        }

        // ------------------------------------------------------------------
        // PlayerID <-> client id helpers
        // ------------------------------------------------------------------

        private static uint PlayerIdToClientId(PlayerID playerId)
        {
            ulong raw = playerId.id;
            if (raw > uint.MaxValue) return InvalidClientId;
            return (uint)raw;
        }

        private static bool TryGetPlayerId(NetworkManager manager, uint clientId, out PlayerID playerId)
        {
            playerId = default;
            if (manager == null) return false;

            var players = manager.players;
            for (int i = 0; i < players.Count; i++)
            {
                var pid = players[i];
                if (PlayerIdToClientId(pid) == clientId)
                {
                    playerId = pid;
                    return true;
                }
            }

            return false;
        }

        public override void RegisterCharacter(NetworkCharacter networkCharacter)
        {
            if (TryResolvePurrNetNetworkId(networkCharacter, out uint purrNetId))
            {
                networkCharacter.SetManualNetworkId(purrNetId);
            }

            base.RegisterCharacter(networkCharacter);
        }

        public override void UnregisterCharacter(NetworkCharacter networkCharacter)
        {
            base.UnregisterCharacter(networkCharacter);
        }
    }
}
