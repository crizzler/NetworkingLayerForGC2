using PurrNet;
using PurrNet.Transports;
using UnityEngine;

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
        [Tooltip("Optional reference to a specific NetworkManager. Leave empty to use NetworkManager.main.")]
        [SerializeField] private NetworkManager m_NetworkManager;

        [Tooltip("Delivery channel used for client->server input broadcasts.")]
        [SerializeField] private Channel m_InputChannel = Channel.UnreliableSequenced;

        [Tooltip("Delivery channel used for server->client state broadcasts.")]
        [SerializeField] private Channel m_StateChannel = Channel.UnreliableSequenced;

        private bool m_SubscribedServer;
        private bool m_SubscribedClient;
        private PurrNetAnimationMotionTransportBridge m_AnimationMotionBridge;

        private NetworkManager ActiveManager => m_NetworkManager ? m_NetworkManager : NetworkManager.main;

        public override bool IsServer => ActiveManager != null && ActiveManager.isServer;
        public override bool IsClient => ActiveManager != null && ActiveManager.isClient;
        public override bool IsHost => ActiveManager != null && ActiveManager.isHost;

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
            if (m_NetworkManager == null) m_NetworkManager = NetworkManager.main;
            EnsureAnimationMotionBridge();
        }

        private void OnEnable()
        {
            EnsureAnimationMotionBridge();
            TryHookNetworkManager();
        }

        private void Start()
        {
            // NetworkManager.main may not be assigned until after the manager's Awake.
            EnsureAnimationMotionBridge();
            TryHookNetworkManager();
        }

        private void OnDisable()
        {
            var nm = ActiveManager;
            if (nm == null) return;

            nm.onNetworkStarted -= HandleNetworkStarted;
            nm.onNetworkShutdown -= HandleNetworkShutdown;

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

        private void TryHookNetworkManager()
        {
            var nm = ActiveManager;
            if (nm == null)
            {
                return;
            }

            nm.onNetworkStarted -= HandleNetworkStarted;
            nm.onNetworkStarted += HandleNetworkStarted;

            nm.onNetworkShutdown -= HandleNetworkShutdown;
            nm.onNetworkShutdown += HandleNetworkShutdown;

            // If the manager is already running when we hook in, subscribe immediately.
            if (nm.isServer) HandleNetworkStarted(nm, true);
            if (nm.isClient) HandleNetworkStarted(nm, false);

            EnsureAnimationMotionBridge();
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

        private void HandleNetworkStarted(NetworkManager manager, bool asServer)
        {
            if (asServer && !m_SubscribedServer)
            {
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
            }
            else if (!asServer && m_SubscribedClient)
            {
                manager.Unsubscribe<GC2StateBroadcast>(HandleStateBroadcastClient, false);
                m_SubscribedClient = false;
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

            RaiseInputReceivedServer(senderClientId, data.characterNetworkId, data.inputs);
        }

        private void HandleStateBroadcastClient(PlayerID senderPlayer, GC2StateBroadcast data, bool asServer)
        {
            if (asServer) return;
            if (data.characterNetworkId == 0) return;

            RaiseStateReceivedClient(data.characterNetworkId, data.state, data.serverTime);
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
