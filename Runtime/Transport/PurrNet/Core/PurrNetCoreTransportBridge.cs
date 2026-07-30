using System;
using System.Collections.Generic;
using PurrNet;
using PurrNet.Packing;
using PurrNet.Transports;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Transport.PurrNet
{
    /// <summary>
    /// Complete PurrNet routing for GC2 Core persistent and request/response state.
    /// Cosmetic prop instances remain local; only validated descriptors cross the network.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Game Creator/Network/Transport/PurrNet Core Bridge")]
    [DefaultExecutionOrder(-390)]
    public sealed class PurrNetCoreTransportBridge : MonoBehaviour
    {
        [SerializeField] private NetworkManager m_NetworkManager;
        [SerializeField, HideInInspector] private Channel m_Channel = Channel.ReliableOrdered;
        [SerializeField] private bool m_LogNetworkMessages;

        private NetworkManager m_HookedManager;
        private NetworkCoreManager m_WiredCoreManager;
        private bool m_SubscribedServer;
        private bool m_SubscribedClient;
        private bool m_ManagerInitialized;
        private bool m_LastServer;
        private bool m_LastClient;

        private NetworkManager ActiveManager => m_NetworkManager ? m_NetworkManager : NetworkManager.main;

        /// <summary>Used by the base PurrNet bridge when it automatically ensures this component.</summary>
        public void Configure(NetworkManager networkManager)
        {
            m_Channel = Channel.ReliableOrdered;
            if (networkManager != null && m_NetworkManager != networkManager)
            {
                UnhookNetworkManager();
                m_NetworkManager = networkManager;
            }

            TryHookNetworkManager();
            WireCoreManager();
        }

        private void Awake()
        {
            m_Channel = Channel.ReliableOrdered;
            if (m_NetworkManager == null) m_NetworkManager = NetworkManager.main;
        }

        private void OnValidate()
        {
            // Core snapshots and the persistent broadcasts following them depend on one ordered
            // stream. This is intentionally not a user-configurable transport choice.
            m_Channel = Channel.ReliableOrdered;
        }

        private void OnEnable()
        {
            TryHookNetworkManager();
            WireCoreManager();
        }

        private void Start()
        {
            TryHookNetworkManager();
            WireCoreManager();
        }

        private void Update()
        {
            TryHookNetworkManager();
            WireCoreManager();
        }

        private void OnDisable()
        {
            UnhookNetworkManager();
            UnwireCoreManager();
        }

        private void TryHookNetworkManager()
        {
            NetworkManager manager = ActiveManager;
            if (manager == null) return;

            if (m_HookedManager != null && m_HookedManager != manager) UnhookNetworkManager();
            if (m_HookedManager == manager)
            {
                if (manager.isServer) HandleNetworkStarted(manager, true);
                if (manager.isClient) HandleNetworkStarted(manager, false);
                return;
            }

            m_HookedManager = manager;
            manager.onNetworkStarted -= HandleNetworkStarted;
            manager.onNetworkStarted += HandleNetworkStarted;
            manager.onNetworkShutdown -= HandleNetworkShutdown;
            manager.onNetworkShutdown += HandleNetworkShutdown;
            manager.onPlayerLoadedScene -= HandlePlayerLoadedScene;
            manager.onPlayerLoadedScene += HandlePlayerLoadedScene;

            if (manager.isServer) HandleNetworkStarted(manager, true);
            if (manager.isClient) HandleNetworkStarted(manager, false);
        }

        private void UnhookNetworkManager()
        {
            NetworkManager manager = m_HookedManager;
            if (manager == null) return;

            manager.onNetworkStarted -= HandleNetworkStarted;
            manager.onNetworkShutdown -= HandleNetworkShutdown;
            manager.onPlayerLoadedScene -= HandlePlayerLoadedScene;
            UnsubscribeServer(manager);
            UnsubscribeClient(manager);
            m_HookedManager = null;
        }

        private void HandleNetworkStarted(NetworkManager manager, bool asServer)
        {
            if (asServer) SubscribeServer(manager);
            else SubscribeClient(manager);
            WireCoreManager();
        }

        private void HandleNetworkShutdown(NetworkManager manager, bool asServer)
        {
            if (asServer) UnsubscribeServer(manager);
            else UnsubscribeClient(manager);
            WireCoreManager();
        }

        private void SubscribeServer(NetworkManager manager)
        {
            if (m_SubscribedServer) return;
            manager.Subscribe<GC2CoreRagdollRequestPacket>(HandleRagdollRequestServer, true);
            manager.Subscribe<GC2CorePropRequestPacket>(HandlePropRequestServer, true);
            manager.Subscribe<GC2CoreInvincibilityRequestPacket>(HandleInvincibilityRequestServer, true);
            manager.Subscribe<GC2CorePoiseRequestPacket>(HandlePoiseRequestServer, true);
            manager.Subscribe<GC2CoreBusyRequestPacket>(HandleBusyRequestServer, true);
            manager.Subscribe<GC2CoreInteractionRequestPacket>(HandleInteractionRequestServer, true);
            m_SubscribedServer = true;
        }

        private void UnsubscribeServer(NetworkManager manager)
        {
            if (!m_SubscribedServer || manager == null) return;
            manager.Unsubscribe<GC2CoreRagdollRequestPacket>(HandleRagdollRequestServer, true);
            manager.Unsubscribe<GC2CorePropRequestPacket>(HandlePropRequestServer, true);
            manager.Unsubscribe<GC2CoreInvincibilityRequestPacket>(HandleInvincibilityRequestServer, true);
            manager.Unsubscribe<GC2CorePoiseRequestPacket>(HandlePoiseRequestServer, true);
            manager.Unsubscribe<GC2CoreBusyRequestPacket>(HandleBusyRequestServer, true);
            manager.Unsubscribe<GC2CoreInteractionRequestPacket>(HandleInteractionRequestServer, true);
            m_SubscribedServer = false;
        }

        private void SubscribeClient(NetworkManager manager)
        {
            if (m_SubscribedClient) return;
            manager.Subscribe<GC2CoreRagdollResponsePacket>(HandleRagdollResponseClient, false);
            manager.Subscribe<GC2CoreRagdollBroadcastPacket>(HandleRagdollBroadcastClient, false);
            manager.Subscribe<GC2CorePropResponsePacket>(HandlePropResponseClient, false);
            manager.Subscribe<GC2CorePropBroadcastPacket>(HandlePropBroadcastClient, false);
            manager.Subscribe<GC2CoreInvincibilityResponsePacket>(HandleInvincibilityResponseClient, false);
            manager.Subscribe<GC2CoreInvincibilityBroadcastPacket>(HandleInvincibilityBroadcastClient, false);
            manager.Subscribe<GC2CorePoiseResponsePacket>(HandlePoiseResponseClient, false);
            manager.Subscribe<GC2CorePoiseBroadcastPacket>(HandlePoiseBroadcastClient, false);
            manager.Subscribe<GC2CoreBusyResponsePacket>(HandleBusyResponseClient, false);
            manager.Subscribe<GC2CoreBusyBroadcastPacket>(HandleBusyBroadcastClient, false);
            manager.Subscribe<GC2CoreInteractionResponsePacket>(HandleInteractionResponseClient, false);
            manager.Subscribe<GC2CoreInteractionBroadcastPacket>(HandleInteractionBroadcastClient, false);
            manager.Subscribe<GC2CoreInteractionFocusPacket>(HandleInteractionFocusClient, false);
            manager.Subscribe<GC2CoreSnapshotPacket>(HandleSnapshotClient, false);
            m_SubscribedClient = true;
        }

        private void UnsubscribeClient(NetworkManager manager)
        {
            if (!m_SubscribedClient || manager == null) return;
            manager.Unsubscribe<GC2CoreRagdollResponsePacket>(HandleRagdollResponseClient, false);
            manager.Unsubscribe<GC2CoreRagdollBroadcastPacket>(HandleRagdollBroadcastClient, false);
            manager.Unsubscribe<GC2CorePropResponsePacket>(HandlePropResponseClient, false);
            manager.Unsubscribe<GC2CorePropBroadcastPacket>(HandlePropBroadcastClient, false);
            manager.Unsubscribe<GC2CoreInvincibilityResponsePacket>(HandleInvincibilityResponseClient, false);
            manager.Unsubscribe<GC2CoreInvincibilityBroadcastPacket>(HandleInvincibilityBroadcastClient, false);
            manager.Unsubscribe<GC2CorePoiseResponsePacket>(HandlePoiseResponseClient, false);
            manager.Unsubscribe<GC2CorePoiseBroadcastPacket>(HandlePoiseBroadcastClient, false);
            manager.Unsubscribe<GC2CoreBusyResponsePacket>(HandleBusyResponseClient, false);
            manager.Unsubscribe<GC2CoreBusyBroadcastPacket>(HandleBusyBroadcastClient, false);
            manager.Unsubscribe<GC2CoreInteractionResponsePacket>(HandleInteractionResponseClient, false);
            manager.Unsubscribe<GC2CoreInteractionBroadcastPacket>(HandleInteractionBroadcastClient, false);
            manager.Unsubscribe<GC2CoreInteractionFocusPacket>(HandleInteractionFocusClient, false);
            manager.Unsubscribe<GC2CoreSnapshotPacket>(HandleSnapshotClient, false);
            m_SubscribedClient = false;
        }

        private void WireCoreManager()
        {
            NetworkCoreManager core = GetCoreManager();
            if (core == null) return;
            if (m_WiredCoreManager != null && m_WiredCoreManager != core) UnwireCoreManager();
            m_WiredCoreManager = core;

            core.SendRagdollRequestToServer = SendRagdollRequest;
            core.SendRagdollResponseToClient = SendRagdollResponse;
            core.BroadcastRagdoll = BroadcastRagdoll;
            core.SendPropRequestToServer = SendPropRequest;
            core.SendPropResponseToClient = SendPropResponse;
            core.BroadcastProp = BroadcastProp;
            core.SendInvincibilityRequestToServer = SendInvincibilityRequest;
            core.SendInvincibilityResponseToClient = SendInvincibilityResponse;
            core.BroadcastInvincibility = BroadcastInvincibility;
            core.SendPoiseRequestToServer = SendPoiseRequest;
            core.SendPoiseResponseToClient = SendPoiseResponse;
            core.BroadcastPoise = BroadcastPoise;
            core.SendBusyRequestToServer = SendBusyRequest;
            core.SendBusyResponseToClient = SendBusyResponse;
            core.BroadcastBusy = BroadcastBusy;
            core.SendInteractionRequestToServer = SendInteractionRequest;
            core.SendInteractionResponseToClient = SendInteractionResponse;
            core.BroadcastInteraction = BroadcastInteraction;
            core.BroadcastInteractionFocus = BroadcastInteractionFocus;
            core.SendCoreSnapshotToClient = SendCoreSnapshot;
            core.GetServerTime = GetServerTime;
            core.GetCharacterByNetworkId = ResolveCharacter;
            core.GetLocalPlayerNetworkId = ResolveLocalCharacterId;
            core.GetNetworkIdForGameObject = ResolveNetworkObjectId;

            NetworkManager manager = ActiveManager;
            bool isServer = manager != null && manager.isServer;
            bool isClient = manager != null && manager.isClient;
            if (!m_ManagerInitialized || isServer != m_LastServer || isClient != m_LastClient)
            {
                core.Initialize(isServer, isClient);
                m_ManagerInitialized = true;
                m_LastServer = isServer;
                m_LastClient = isClient;
            }
        }

        private void UnwireCoreManager()
        {
            NetworkCoreManager core = m_WiredCoreManager;
            if (core == null) return;
            if (core.SendRagdollRequestToServer == SendRagdollRequest) core.SendRagdollRequestToServer = null;
            if (core.SendRagdollResponseToClient == SendRagdollResponse) core.SendRagdollResponseToClient = null;
            if (core.BroadcastRagdoll == BroadcastRagdoll) core.BroadcastRagdoll = null;
            if (core.SendPropRequestToServer == SendPropRequest) core.SendPropRequestToServer = null;
            if (core.SendPropResponseToClient == SendPropResponse) core.SendPropResponseToClient = null;
            if (core.BroadcastProp == BroadcastProp) core.BroadcastProp = null;
            if (core.SendInvincibilityRequestToServer == SendInvincibilityRequest) core.SendInvincibilityRequestToServer = null;
            if (core.SendInvincibilityResponseToClient == SendInvincibilityResponse) core.SendInvincibilityResponseToClient = null;
            if (core.BroadcastInvincibility == BroadcastInvincibility) core.BroadcastInvincibility = null;
            if (core.SendPoiseRequestToServer == SendPoiseRequest) core.SendPoiseRequestToServer = null;
            if (core.SendPoiseResponseToClient == SendPoiseResponse) core.SendPoiseResponseToClient = null;
            if (core.BroadcastPoise == BroadcastPoise) core.BroadcastPoise = null;
            if (core.SendBusyRequestToServer == SendBusyRequest) core.SendBusyRequestToServer = null;
            if (core.SendBusyResponseToClient == SendBusyResponse) core.SendBusyResponseToClient = null;
            if (core.BroadcastBusy == BroadcastBusy) core.BroadcastBusy = null;
            if (core.SendInteractionRequestToServer == SendInteractionRequest) core.SendInteractionRequestToServer = null;
            if (core.SendInteractionResponseToClient == SendInteractionResponse) core.SendInteractionResponseToClient = null;
            if (core.BroadcastInteraction == BroadcastInteraction) core.BroadcastInteraction = null;
            if (core.BroadcastInteractionFocus == BroadcastInteractionFocus) core.BroadcastInteractionFocus = null;
            if (core.SendCoreSnapshotToClient == SendCoreSnapshot) core.SendCoreSnapshotToClient = null;
            if (core.GetServerTime == GetServerTime) core.GetServerTime = null;
            if (core.GetCharacterByNetworkId == ResolveCharacter) core.GetCharacterByNetworkId = null;
            if (core.GetLocalPlayerNetworkId == ResolveLocalCharacterId) core.GetLocalPlayerNetworkId = null;
            if (core.GetNetworkIdForGameObject == ResolveNetworkObjectId)
                core.GetNetworkIdForGameObject = null;
            m_WiredCoreManager = null;
            m_ManagerInitialized = false;
        }

        private void HandlePlayerLoadedScene(PlayerID player, SceneID scene, bool asServer)
        {
            if (!asServer) return;
            NetworkCoreManager core = GetCoreManager();
            if (core == null) return;

            uint clientId = PlayerIdToClientId(player);
            if (!NetworkTransportBridge.IsValidClientId(clientId)) return;

            NetworkCharacter[] characters = FindObjectsByType<NetworkCharacter>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            var sent = new HashSet<uint>();
            for (int i = 0; i < characters.Length; i++)
            {
                uint networkId = characters[i].NetworkId;
                if (networkId == 0 || !sent.Add(networkId)) continue;
                core.SendSnapshotToClient(clientId, networkId);
            }
        }

        private void SendRagdollRequest(NetworkRagdollRequest v) =>
            SendRequest(new GC2CoreRagdollRequestPacket { Value = v },
                (p, d) => DispatchRagdollRequest(p, d));
        private void SendPropRequest(NetworkPropRequest v) =>
            SendRequest(new GC2CorePropRequestPacket { Value = v },
                (p, d) => DispatchPropRequest(p, d));
        private void SendInvincibilityRequest(NetworkInvincibilityRequest v) =>
            SendRequest(new GC2CoreInvincibilityRequestPacket { Value = v },
                (p, d) => DispatchInvincibilityRequest(p, d));
        private void SendPoiseRequest(NetworkPoiseRequest v) =>
            SendRequest(new GC2CorePoiseRequestPacket { Value = v },
                (p, d) => DispatchPoiseRequest(p, d));
        private void SendBusyRequest(NetworkBusyRequest v) =>
            SendRequest(new GC2CoreBusyRequestPacket { Value = v },
                (p, d) => DispatchBusyRequest(p, d));
        private void SendInteractionRequest(NetworkInteractionRequest v) =>
            SendRequest(new GC2CoreInteractionRequestPacket { Value = v },
                (p, d) => DispatchInteractionRequest(p, d));

        private void SendRequest<T>(T packet, Action<PlayerID, T> hostDispatch)
            where T : struct, IPackedAuto
        {
            NetworkManager manager = ActiveManager;
            if (manager == null || !manager.isClient) return;
            if (manager.isServer)
            {
                if (manager.isLocalPlayerReady) hostDispatch(manager.localPlayer, packet);
                return;
            }
            manager.SendToServer(packet, m_Channel);
        }

        private void SendRagdollResponse(uint id, NetworkRagdollResponse v) =>
            SendToClient(id, new GC2CoreRagdollResponsePacket { Value = v });
        private void SendPropResponse(uint id, NetworkPropResponse v) =>
            SendToClient(id, new GC2CorePropResponsePacket { Value = v });
        private void SendInvincibilityResponse(uint id, NetworkInvincibilityResponse v) =>
            SendToClient(id, new GC2CoreInvincibilityResponsePacket { Value = v });
        private void SendPoiseResponse(uint id, NetworkPoiseResponse v) =>
            SendToClient(id, new GC2CorePoiseResponsePacket { Value = v });
        private void SendBusyResponse(uint id, NetworkBusyResponse v) =>
            SendToClient(id, new GC2CoreBusyResponsePacket { Value = v });
        private void SendInteractionResponse(uint id, NetworkInteractionResponse v) =>
            SendToClient(id, new GC2CoreInteractionResponsePacket { Value = v });
        private void SendCoreSnapshot(uint id, NetworkCoreSnapshot v) =>
            SendToClient(id, new GC2CoreSnapshotPacket { Value = v });

        private void SendToClient<T>(uint clientId, T packet) where T : struct, IPackedAuto
        {
            NetworkManager manager = ActiveManager;
            if (manager == null || !manager.isServer || !TryGetPlayerId(manager, clientId, out PlayerID player)) return;
            manager.Send(player, packet, m_Channel);
        }

        private void BroadcastRagdoll(NetworkRagdollBroadcast v) =>
            Broadcast(new GC2CoreRagdollBroadcastPacket { Value = v });
        private void BroadcastProp(NetworkPropBroadcast v) =>
            Broadcast(new GC2CorePropBroadcastPacket { Value = v });
        private void BroadcastInvincibility(NetworkInvincibilityBroadcast v) =>
            Broadcast(new GC2CoreInvincibilityBroadcastPacket { Value = v });
        private void BroadcastPoise(NetworkPoiseBroadcast v) =>
            Broadcast(new GC2CorePoiseBroadcastPacket { Value = v });
        private void BroadcastBusy(NetworkBusyBroadcast v) =>
            Broadcast(new GC2CoreBusyBroadcastPacket { Value = v });
        private void BroadcastInteraction(NetworkInteractionBroadcast v) =>
            Broadcast(new GC2CoreInteractionBroadcastPacket { Value = v });
        private void BroadcastInteractionFocus(NetworkInteractionFocusBroadcast v) =>
            Broadcast(new GC2CoreInteractionFocusPacket { Value = v });

        private void Broadcast<T>(T packet) where T : struct, IPackedAuto
        {
            NetworkManager manager = ActiveManager;
            if (manager == null || !manager.isServer) return;
            manager.SendToAll(packet, m_Channel);
        }

        private void HandleRagdollRequestServer(PlayerID p, GC2CoreRagdollRequestPacket d, bool s) { if (s) DispatchRagdollRequest(p, d); }
        private void HandlePropRequestServer(PlayerID p, GC2CorePropRequestPacket d, bool s) { if (s) DispatchPropRequest(p, d); }
        private void HandleInvincibilityRequestServer(PlayerID p, GC2CoreInvincibilityRequestPacket d, bool s) { if (s) DispatchInvincibilityRequest(p, d); }
        private void HandlePoiseRequestServer(PlayerID p, GC2CorePoiseRequestPacket d, bool s) { if (s) DispatchPoiseRequest(p, d); }
        private void HandleBusyRequestServer(PlayerID p, GC2CoreBusyRequestPacket d, bool s) { if (s) DispatchBusyRequest(p, d); }
        private void HandleInteractionRequestServer(PlayerID p, GC2CoreInteractionRequestPacket d, bool s) { if (s) DispatchInteractionRequest(p, d); }

        private void DispatchRagdollRequest(PlayerID p, GC2CoreRagdollRequestPacket d) => GetCoreManager()?.ReceiveRagdollRequest(PlayerIdToClientId(p), d.Value);
        private void DispatchPropRequest(PlayerID p, GC2CorePropRequestPacket d) => GetCoreManager()?.ReceivePropRequest(PlayerIdToClientId(p), d.Value);
        private void DispatchInvincibilityRequest(PlayerID p, GC2CoreInvincibilityRequestPacket d) => GetCoreManager()?.ReceiveInvincibilityRequest(PlayerIdToClientId(p), d.Value);
        private void DispatchPoiseRequest(PlayerID p, GC2CorePoiseRequestPacket d) => GetCoreManager()?.ReceivePoiseRequest(PlayerIdToClientId(p), d.Value);
        private void DispatchBusyRequest(PlayerID p, GC2CoreBusyRequestPacket d) => GetCoreManager()?.ReceiveBusyRequest(PlayerIdToClientId(p), d.Value);
        private void DispatchInteractionRequest(PlayerID p, GC2CoreInteractionRequestPacket d) => GetCoreManager()?.ReceiveInteractionRequest(PlayerIdToClientId(p), d.Value);

        private void HandleRagdollResponseClient(PlayerID p, GC2CoreRagdollResponsePacket d, bool s) { if (!s) GetCoreManager()?.ReceiveRagdollResponse(d.Value); }
        private void HandleRagdollBroadcastClient(PlayerID p, GC2CoreRagdollBroadcastPacket d, bool s) { if (!s) GetCoreManager()?.ReceiveRagdollBroadcast(d.Value); }
        private void HandlePropResponseClient(PlayerID p, GC2CorePropResponsePacket d, bool s) { if (!s) GetCoreManager()?.ReceivePropResponse(d.Value); }
        private void HandlePropBroadcastClient(PlayerID p, GC2CorePropBroadcastPacket d, bool s) { if (!s) GetCoreManager()?.ReceivePropBroadcast(d.Value); }
        private void HandleInvincibilityResponseClient(PlayerID p, GC2CoreInvincibilityResponsePacket d, bool s) { if (!s) GetCoreManager()?.ReceiveInvincibilityResponse(d.Value); }
        private void HandleInvincibilityBroadcastClient(PlayerID p, GC2CoreInvincibilityBroadcastPacket d, bool s) { if (!s) GetCoreManager()?.ReceiveInvincibilityBroadcast(d.Value); }
        private void HandlePoiseResponseClient(PlayerID p, GC2CorePoiseResponsePacket d, bool s) { if (!s) GetCoreManager()?.ReceivePoiseResponse(d.Value); }
        private void HandlePoiseBroadcastClient(PlayerID p, GC2CorePoiseBroadcastPacket d, bool s) { if (!s) GetCoreManager()?.ReceivePoiseBroadcast(d.Value); }
        private void HandleBusyResponseClient(PlayerID p, GC2CoreBusyResponsePacket d, bool s) { if (!s) GetCoreManager()?.ReceiveBusyResponse(d.Value); }
        private void HandleBusyBroadcastClient(PlayerID p, GC2CoreBusyBroadcastPacket d, bool s) { if (!s) GetCoreManager()?.ReceiveBusyBroadcast(d.Value); }
        private void HandleInteractionResponseClient(PlayerID p, GC2CoreInteractionResponsePacket d, bool s) { if (!s) GetCoreManager()?.ReceiveInteractionResponse(d.Value); }
        private void HandleInteractionBroadcastClient(PlayerID p, GC2CoreInteractionBroadcastPacket d, bool s) { if (!s) GetCoreManager()?.ReceiveInteractionBroadcast(d.Value); }
        private void HandleInteractionFocusClient(PlayerID p, GC2CoreInteractionFocusPacket d, bool s) { if (!s) GetCoreManager()?.ReceiveInteractionFocusBroadcast(d.Value); }
        private void HandleSnapshotClient(PlayerID p, GC2CoreSnapshotPacket d, bool s) { if (!s) GetCoreManager()?.ReceiveCoreSnapshot(d.Value); }

        private float GetServerTime() => NetworkTransportBridge.Active?.ServerTime ?? Time.time;
        private GameCreator.Runtime.Characters.Character ResolveCharacter(uint id) => NetworkTransportBridge.Active?.ResolveCharacter(id);

        private static uint ResolveNetworkObjectId(GameObject gameObject)
        {
            if (gameObject == null) return 0;
            NetworkIdentity identity = gameObject.GetComponentInParent<NetworkIdentity>();
            if (identity == null || !identity.isSpawned || identity.objectId >= uint.MaxValue)
            {
                return 0;
            }

            return (uint)(identity.objectId + 1UL);
        }

        private uint ResolveLocalCharacterId()
        {
            NetworkManager manager = ActiveManager;
            NetworkTransportBridge bridge = NetworkTransportBridge.Active;
            if (manager == null || bridge == null || !manager.isLocalPlayerReady) return 0;
            return bridge.TryGetRepresentativeCharacterId(PlayerIdToClientId(manager.localPlayer), out uint id) ? id : 0;
        }

        private static NetworkCoreManager GetCoreManager()
        {
            return NetworkCoreManager.Instance != null
                ? NetworkCoreManager.Instance
                : FindFirstObjectByType<NetworkCoreManager>();
        }

        private static uint PlayerIdToClientId(PlayerID player)
        {
            return player.id <= uint.MaxValue ? (uint)player.id : NetworkTransportBridge.InvalidClientId;
        }

        private static bool TryGetPlayerId(NetworkManager manager, uint clientId, out PlayerID player)
        {
            player = default;
            if (manager == null) return false;
            IReadOnlyList<PlayerID> players = manager.players;
            for (int i = 0; i < players.Count; i++)
            {
                if (PlayerIdToClientId(players[i]) != clientId) continue;
                player = players[i];
                return true;
            }
            return false;
        }

        private void Log(string message)
        {
            if (m_LogNetworkMessages) Debug.Log($"[PurrNetCoreTransportBridge] {message}", this);
        }
    }
}
