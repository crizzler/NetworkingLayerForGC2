#if GC2_STATS
using System.Collections.Generic;
using Arawn.GameCreator2.Networking;
using Arawn.GameCreator2.Networking.Transport.PurrNet;
using PurrNet;
using PurrNet.Transports;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Stats.Transport.PurrNet
{
    [DisallowMultipleComponent]
    [AddComponentMenu("Game Creator/Network/Transport/PurrNet Stats Bridge")]
    [DefaultExecutionOrder(-340)]
    public sealed class PurrNetStatsTransportBridge : MonoBehaviour
    {
        [Header("PurrNet")]
        [Tooltip("Optional reference to a specific NetworkManager. Leave empty to use NetworkManager.main.")]
        [SerializeField] private NetworkManager m_NetworkManager;

        [Tooltip("Reliable channel used for stat requests, responses, and snapshots.")]
        [SerializeField] private Channel m_Channel = Channel.ReliableOrdered;

        [Header("Controllers")]
        [Tooltip("Automatically finds NetworkStatsController components on spawned NetworkCharacter objects.")]
        [SerializeField] private bool m_AutoRegisterSceneControllers = true;

        [Min(0.05f)]
        [SerializeField] private float m_ControllerScanInterval = 0.25f;

        private readonly Dictionary<uint, NetworkStatsController> m_RegisteredControllers = new(32);
        private readonly List<uint> m_RemoveBuffer = new(16);

        private NetworkManager m_HookedManager;
        private bool m_SubscribedServer;
        private bool m_SubscribedClient;
        private bool m_ManagerInitialized;
        private bool m_LastServer;
        private float m_NextControllerScanTime;

        private NetworkManager ActiveManager => m_NetworkManager ? m_NetworkManager : NetworkManager.main;

        private void Awake()
        {
            if (m_NetworkManager == null) m_NetworkManager = NetworkManager.main;
        }

        private void OnEnable()
        {
            TryHookNetworkManager();
            WireStatsManager();
            RefreshControllerRegistry(force: true);
        }

        private void Start()
        {
            TryHookNetworkManager();
            WireStatsManager();
            RefreshControllerRegistry(force: true);
        }

        private void Update()
        {
            TryHookNetworkManager();
            WireStatsManager();

            if (!m_AutoRegisterSceneControllers) return;
            if (Time.unscaledTime < m_NextControllerScanTime) return;

            m_NextControllerScanTime = Time.unscaledTime + Mathf.Max(0.05f, m_ControllerScanInterval);
            RefreshControllerRegistry(force: false);
        }

        private void OnDisable()
        {
            UnhookNetworkManager();
            UnwireStatsManager();
            UnregisterAllControllers();
        }

        private void TryHookNetworkManager()
        {
            var nm = ActiveManager;
            if (nm == null) return;

            if (m_HookedManager != null && m_HookedManager != nm)
            {
                UnhookNetworkManager();
            }

            if (m_HookedManager == nm)
            {
                if (nm.isServer) HandleNetworkStarted(nm, true);
                if (nm.isClient) HandleNetworkStarted(nm, false);
                return;
            }

            m_HookedManager = nm;
            nm.onNetworkStarted -= HandleNetworkStarted;
            nm.onNetworkStarted += HandleNetworkStarted;
            nm.onNetworkShutdown -= HandleNetworkShutdown;
            nm.onNetworkShutdown += HandleNetworkShutdown;
            nm.onPlayerLoadedScene -= HandlePlayerLoadedScene;
            nm.onPlayerLoadedScene += HandlePlayerLoadedScene;

            if (nm.isServer) HandleNetworkStarted(nm, true);
            if (nm.isClient) HandleNetworkStarted(nm, false);
        }

        private void UnhookNetworkManager()
        {
            var nm = m_HookedManager;
            if (nm == null) return;

            nm.onNetworkStarted -= HandleNetworkStarted;
            nm.onNetworkShutdown -= HandleNetworkShutdown;
            nm.onPlayerLoadedScene -= HandlePlayerLoadedScene;

            if (m_SubscribedServer)
            {
                nm.Unsubscribe<GC2StatModifyRequestPacket>(HandleStatModifyRequestServer, true);
                nm.Unsubscribe<GC2AttributeModifyRequestPacket>(HandleAttributeModifyRequestServer, true);
                nm.Unsubscribe<GC2StatusEffectRequestPacket>(HandleStatusEffectRequestServer, true);
                nm.Unsubscribe<GC2StatModifierRequestPacket>(HandleStatModifierRequestServer, true);
                nm.Unsubscribe<GC2ClearStatusEffectsRequestPacket>(HandleClearStatusEffectsRequestServer, true);
                m_SubscribedServer = false;
            }

            if (m_SubscribedClient)
            {
                nm.Unsubscribe<GC2StatModifyResponsePacket>(HandleStatModifyResponseClient, false);
                nm.Unsubscribe<GC2AttributeModifyResponsePacket>(HandleAttributeModifyResponseClient, false);
                nm.Unsubscribe<GC2StatusEffectResponsePacket>(HandleStatusEffectResponseClient, false);
                nm.Unsubscribe<GC2StatModifierResponsePacket>(HandleStatModifierResponseClient, false);
                nm.Unsubscribe<GC2ClearStatusEffectsResponsePacket>(HandleClearStatusEffectsResponseClient, false);
                nm.Unsubscribe<GC2StatChangeBroadcastPacket>(HandleStatChangeBroadcastClient, false);
                nm.Unsubscribe<GC2AttributeChangeBroadcastPacket>(HandleAttributeChangeBroadcastClient, false);
                nm.Unsubscribe<GC2StatusEffectBroadcastPacket>(HandleStatusEffectBroadcastClient, false);
                nm.Unsubscribe<GC2StatModifierBroadcastPacket>(HandleStatModifierBroadcastClient, false);
                nm.Unsubscribe<GC2StatsSnapshotPacket>(HandleSnapshotClient, false);
                nm.Unsubscribe<GC2StatsDeltaPacket>(HandleDeltaClient, false);
                m_SubscribedClient = false;
            }

            m_HookedManager = null;
        }

        private void HandleNetworkStarted(NetworkManager manager, bool asServer)
        {
            if (asServer && !m_SubscribedServer)
            {
                manager.Subscribe<GC2StatModifyRequestPacket>(HandleStatModifyRequestServer, true);
                manager.Subscribe<GC2AttributeModifyRequestPacket>(HandleAttributeModifyRequestServer, true);
                manager.Subscribe<GC2StatusEffectRequestPacket>(HandleStatusEffectRequestServer, true);
                manager.Subscribe<GC2StatModifierRequestPacket>(HandleStatModifierRequestServer, true);
                manager.Subscribe<GC2ClearStatusEffectsRequestPacket>(HandleClearStatusEffectsRequestServer, true);
                m_SubscribedServer = true;
            }
            else if (!asServer && !m_SubscribedClient)
            {
                manager.Subscribe<GC2StatModifyResponsePacket>(HandleStatModifyResponseClient, false);
                manager.Subscribe<GC2AttributeModifyResponsePacket>(HandleAttributeModifyResponseClient, false);
                manager.Subscribe<GC2StatusEffectResponsePacket>(HandleStatusEffectResponseClient, false);
                manager.Subscribe<GC2StatModifierResponsePacket>(HandleStatModifierResponseClient, false);
                manager.Subscribe<GC2ClearStatusEffectsResponsePacket>(HandleClearStatusEffectsResponseClient, false);
                manager.Subscribe<GC2StatChangeBroadcastPacket>(HandleStatChangeBroadcastClient, false);
                manager.Subscribe<GC2AttributeChangeBroadcastPacket>(HandleAttributeChangeBroadcastClient, false);
                manager.Subscribe<GC2StatusEffectBroadcastPacket>(HandleStatusEffectBroadcastClient, false);
                manager.Subscribe<GC2StatModifierBroadcastPacket>(HandleStatModifierBroadcastClient, false);
                manager.Subscribe<GC2StatsSnapshotPacket>(HandleSnapshotClient, false);
                manager.Subscribe<GC2StatsDeltaPacket>(HandleDeltaClient, false);
                m_SubscribedClient = true;
            }

            WireStatsManager();
            RefreshControllerRegistry(force: true);
        }

        private void HandleNetworkShutdown(NetworkManager manager, bool asServer)
        {
            if (asServer && m_SubscribedServer)
            {
                manager.Unsubscribe<GC2StatModifyRequestPacket>(HandleStatModifyRequestServer, true);
                manager.Unsubscribe<GC2AttributeModifyRequestPacket>(HandleAttributeModifyRequestServer, true);
                manager.Unsubscribe<GC2StatusEffectRequestPacket>(HandleStatusEffectRequestServer, true);
                manager.Unsubscribe<GC2StatModifierRequestPacket>(HandleStatModifierRequestServer, true);
                manager.Unsubscribe<GC2ClearStatusEffectsRequestPacket>(HandleClearStatusEffectsRequestServer, true);
                m_SubscribedServer = false;
            }
            else if (!asServer && m_SubscribedClient)
            {
                manager.Unsubscribe<GC2StatModifyResponsePacket>(HandleStatModifyResponseClient, false);
                manager.Unsubscribe<GC2AttributeModifyResponsePacket>(HandleAttributeModifyResponseClient, false);
                manager.Unsubscribe<GC2StatusEffectResponsePacket>(HandleStatusEffectResponseClient, false);
                manager.Unsubscribe<GC2StatModifierResponsePacket>(HandleStatModifierResponseClient, false);
                manager.Unsubscribe<GC2ClearStatusEffectsResponsePacket>(HandleClearStatusEffectsResponseClient, false);
                manager.Unsubscribe<GC2StatChangeBroadcastPacket>(HandleStatChangeBroadcastClient, false);
                manager.Unsubscribe<GC2AttributeChangeBroadcastPacket>(HandleAttributeChangeBroadcastClient, false);
                manager.Unsubscribe<GC2StatusEffectBroadcastPacket>(HandleStatusEffectBroadcastClient, false);
                manager.Unsubscribe<GC2StatModifierBroadcastPacket>(HandleStatModifierBroadcastClient, false);
                manager.Unsubscribe<GC2StatsSnapshotPacket>(HandleSnapshotClient, false);
                manager.Unsubscribe<GC2StatsDeltaPacket>(HandleDeltaClient, false);
                m_SubscribedClient = false;
            }

            WireStatsManager();
        }

        private void HandlePlayerLoadedScene(PlayerID player, SceneID scene, bool asServer)
        {
            if (!asServer) return;
            GetStatsManager()?.SendInitialState(player.id);
        }

        private void WireStatsManager()
        {
            NetworkStatsManager manager = GetStatsManager();
            if (manager == null) return;

            manager.OnSendStatModifyRequest -= SendStatModifyRequestToServer;
            manager.OnSendStatModifyRequest += SendStatModifyRequestToServer;
            manager.OnSendAttributeModifyRequest -= SendAttributeModifyRequestToServer;
            manager.OnSendAttributeModifyRequest += SendAttributeModifyRequestToServer;
            manager.OnSendStatusEffectRequest -= SendStatusEffectRequestToServer;
            manager.OnSendStatusEffectRequest += SendStatusEffectRequestToServer;
            manager.OnSendStatModifierRequest -= SendStatModifierRequestToServer;
            manager.OnSendStatModifierRequest += SendStatModifierRequestToServer;
            manager.OnSendClearStatusEffectsRequest -= SendClearStatusEffectsRequestToServer;
            manager.OnSendClearStatusEffectsRequest += SendClearStatusEffectsRequestToServer;

            manager.OnSendStatModifyResponse -= SendStatModifyResponseToClient;
            manager.OnSendStatModifyResponse += SendStatModifyResponseToClient;
            manager.OnSendAttributeModifyResponse -= SendAttributeModifyResponseToClient;
            manager.OnSendAttributeModifyResponse += SendAttributeModifyResponseToClient;
            manager.OnSendStatusEffectResponse -= SendStatusEffectResponseToClient;
            manager.OnSendStatusEffectResponse += SendStatusEffectResponseToClient;
            manager.OnSendStatModifierResponse -= SendStatModifierResponseToClient;
            manager.OnSendStatModifierResponse += SendStatModifierResponseToClient;
            manager.OnSendClearStatusEffectsResponse -= SendClearStatusEffectsResponseToClient;
            manager.OnSendClearStatusEffectsResponse += SendClearStatusEffectsResponseToClient;

            manager.OnBroadcastStatChange -= BroadcastStatChangeToAllClients;
            manager.OnBroadcastStatChange += BroadcastStatChangeToAllClients;
            manager.OnBroadcastAttributeChange -= BroadcastAttributeChangeToAllClients;
            manager.OnBroadcastAttributeChange += BroadcastAttributeChangeToAllClients;
            manager.OnBroadcastStatusEffectChange -= BroadcastStatusEffectChangeToAllClients;
            manager.OnBroadcastStatusEffectChange += BroadcastStatusEffectChangeToAllClients;
            manager.OnBroadcastStatModifierChange -= BroadcastStatModifierChangeToAllClients;
            manager.OnBroadcastStatModifierChange += BroadcastStatModifierChangeToAllClients;
            manager.OnBroadcastFullSnapshot -= BroadcastSnapshotToAllClients;
            manager.OnBroadcastFullSnapshot += BroadcastSnapshotToAllClients;
            manager.OnBroadcastDelta -= BroadcastDeltaToAllClients;
            manager.OnBroadcastDelta += BroadcastDeltaToAllClients;
            manager.OnSendSnapshotToClient -= SendSnapshotToClient;
            manager.OnSendSnapshotToClient += SendSnapshotToClient;

            var nm = ActiveManager;
            bool isServer = nm != null && nm.isServer;
            if (!m_ManagerInitialized || isServer != m_LastServer)
            {
                manager.IsServer = isServer;
                m_ManagerInitialized = true;
                m_LastServer = isServer;
            }
        }

        private void UnwireStatsManager()
        {
            NetworkStatsManager manager = GetStatsManager();
            if (manager == null) return;

            manager.OnSendStatModifyRequest -= SendStatModifyRequestToServer;
            manager.OnSendAttributeModifyRequest -= SendAttributeModifyRequestToServer;
            manager.OnSendStatusEffectRequest -= SendStatusEffectRequestToServer;
            manager.OnSendStatModifierRequest -= SendStatModifierRequestToServer;
            manager.OnSendClearStatusEffectsRequest -= SendClearStatusEffectsRequestToServer;

            manager.OnSendStatModifyResponse -= SendStatModifyResponseToClient;
            manager.OnSendAttributeModifyResponse -= SendAttributeModifyResponseToClient;
            manager.OnSendStatusEffectResponse -= SendStatusEffectResponseToClient;
            manager.OnSendStatModifierResponse -= SendStatModifierResponseToClient;
            manager.OnSendClearStatusEffectsResponse -= SendClearStatusEffectsResponseToClient;

            manager.OnBroadcastStatChange -= BroadcastStatChangeToAllClients;
            manager.OnBroadcastAttributeChange -= BroadcastAttributeChangeToAllClients;
            manager.OnBroadcastStatusEffectChange -= BroadcastStatusEffectChangeToAllClients;
            manager.OnBroadcastStatModifierChange -= BroadcastStatModifierChangeToAllClients;
            manager.OnBroadcastFullSnapshot -= BroadcastSnapshotToAllClients;
            manager.OnBroadcastDelta -= BroadcastDeltaToAllClients;
            manager.OnSendSnapshotToClient -= SendSnapshotToClient;

            m_ManagerInitialized = false;
        }

        private void RefreshControllerRegistry(bool force)
        {
            NetworkStatsManager manager = GetStatsManager();
            if (manager == null) return;

            PruneControllerRegistry(manager);

            if (!m_AutoRegisterSceneControllers && !force) return;

            var controllers = FindObjectsByType<NetworkStatsController>(
                FindObjectsInactive.Exclude,
                FindObjectsSortMode.None);

            for (int i = 0; i < controllers.Length; i++)
            {
                RegisterController(manager, controllers[i]);
            }
        }

        private void RegisterController(NetworkStatsManager manager, NetworkStatsController controller)
        {
            if (manager == null || controller == null) return;

            var networkCharacter = controller.GetComponent<NetworkCharacter>();
            if (networkCharacter == null || networkCharacter.NetworkId == 0) return;
            if (networkCharacter.Role == NetworkCharacter.NetworkRole.None) return;

            bool isServer = networkCharacter.IsServerInstance;
            bool isLocalClient =
                networkCharacter.IsOwnerInstance &&
                networkCharacter.Role == NetworkCharacter.NetworkRole.LocalClient;

            uint networkId = networkCharacter.NetworkId;
            if (m_RegisteredControllers.TryGetValue(networkId, out var existing))
            {
                if (existing == controller)
                {
                    if (controller.IsServer != isServer || controller.IsLocalClient != isLocalClient)
                    {
                        controller.Initialize(isServer, isLocalClient);
                    }

                    return;
                }

                manager.UnregisterController(networkId);
            }

            controller.Initialize(isServer, isLocalClient);
            m_RegisteredControllers[networkId] = controller;
            manager.RegisterController(networkId, controller);

            if (manager.IsServer)
            {
                manager.BroadcastFullSnapshot(controller.GetFullSnapshot());
            }
        }

        private void PruneControllerRegistry(NetworkStatsManager manager)
        {
            m_RemoveBuffer.Clear();

            foreach (var pair in m_RegisteredControllers)
            {
                var controller = pair.Value;
                var networkCharacter = controller != null ? controller.GetComponent<NetworkCharacter>() : null;
                if (controller == null ||
                    networkCharacter == null ||
                    networkCharacter.NetworkId != pair.Key ||
                    networkCharacter.Role == NetworkCharacter.NetworkRole.None)
                {
                    m_RemoveBuffer.Add(pair.Key);
                }
            }

            for (int i = 0; i < m_RemoveBuffer.Count; i++)
            {
                uint networkId = m_RemoveBuffer[i];
                manager.UnregisterController(networkId);
                m_RegisteredControllers.Remove(networkId);
            }
        }

        private void UnregisterAllControllers()
        {
            NetworkStatsManager manager = GetStatsManager();
            if (manager != null)
            {
                foreach (uint networkId in m_RegisteredControllers.Keys)
                {
                    manager.UnregisterController(networkId);
                }
            }

            m_RegisteredControllers.Clear();
        }

        private void SendStatModifyRequestToServer(NetworkStatModifyRequest request)
        {
            var nm = ActiveManager;
            if (nm == null || !nm.isClient) return;

            var packet = new GC2StatModifyRequestPacket { request = request };
            if (nm.isServer)
            {
                if (nm.isLocalPlayerReady) DispatchStatModifyRequestOnServer(nm.localPlayer, packet);
                return;
            }

            nm.SendToServer(packet, m_Channel);
        }

        private void SendAttributeModifyRequestToServer(NetworkAttributeModifyRequest request)
        {
            var nm = ActiveManager;
            if (nm == null || !nm.isClient) return;

            var packet = new GC2AttributeModifyRequestPacket { request = request };
            if (nm.isServer)
            {
                if (nm.isLocalPlayerReady) DispatchAttributeModifyRequestOnServer(nm.localPlayer, packet);
                return;
            }

            nm.SendToServer(packet, m_Channel);
        }

        private void SendStatusEffectRequestToServer(NetworkStatusEffectRequest request)
        {
            var nm = ActiveManager;
            if (nm == null || !nm.isClient) return;

            var packet = new GC2StatusEffectRequestPacket { request = request };
            if (nm.isServer)
            {
                if (nm.isLocalPlayerReady) DispatchStatusEffectRequestOnServer(nm.localPlayer, packet);
                return;
            }

            nm.SendToServer(packet, m_Channel);
        }

        private void SendStatModifierRequestToServer(NetworkStatModifierRequest request)
        {
            var nm = ActiveManager;
            if (nm == null || !nm.isClient) return;

            var packet = new GC2StatModifierRequestPacket { request = request };
            if (nm.isServer)
            {
                if (nm.isLocalPlayerReady) DispatchStatModifierRequestOnServer(nm.localPlayer, packet);
                return;
            }

            nm.SendToServer(packet, m_Channel);
        }

        private void SendClearStatusEffectsRequestToServer(NetworkClearStatusEffectsRequest request)
        {
            var nm = ActiveManager;
            if (nm == null || !nm.isClient) return;

            var packet = new GC2ClearStatusEffectsRequestPacket { request = request };
            if (nm.isServer)
            {
                if (nm.isLocalPlayerReady) DispatchClearStatusEffectsRequestOnServer(nm.localPlayer, packet);
                return;
            }

            nm.SendToServer(packet, m_Channel);
        }

        private void SendStatModifyResponseToClient(uint clientNetworkId, NetworkStatModifyResponse response)
        {
            var nm = ActiveManager;
            if (nm == null || !nm.isServer) return;
            if (!TryGetPlayerId(nm, clientNetworkId, out var playerId)) return;
            nm.Send(playerId, new GC2StatModifyResponsePacket { response = response }, m_Channel);
        }

        private void SendAttributeModifyResponseToClient(uint clientNetworkId, NetworkAttributeModifyResponse response)
        {
            var nm = ActiveManager;
            if (nm == null || !nm.isServer) return;
            if (!TryGetPlayerId(nm, clientNetworkId, out var playerId)) return;
            nm.Send(playerId, new GC2AttributeModifyResponsePacket { response = response }, m_Channel);
        }

        private void SendStatusEffectResponseToClient(uint clientNetworkId, NetworkStatusEffectResponse response)
        {
            var nm = ActiveManager;
            if (nm == null || !nm.isServer) return;
            if (!TryGetPlayerId(nm, clientNetworkId, out var playerId)) return;
            nm.Send(playerId, new GC2StatusEffectResponsePacket { response = response }, m_Channel);
        }

        private void SendStatModifierResponseToClient(uint clientNetworkId, NetworkStatModifierResponse response)
        {
            var nm = ActiveManager;
            if (nm == null || !nm.isServer) return;
            if (!TryGetPlayerId(nm, clientNetworkId, out var playerId)) return;
            nm.Send(playerId, new GC2StatModifierResponsePacket { response = response }, m_Channel);
        }

        private void SendClearStatusEffectsResponseToClient(uint clientNetworkId, NetworkClearStatusEffectsResponse response)
        {
            var nm = ActiveManager;
            if (nm == null || !nm.isServer) return;
            if (!TryGetPlayerId(nm, clientNetworkId, out var playerId)) return;
            nm.Send(playerId, new GC2ClearStatusEffectsResponsePacket { response = response }, m_Channel);
        }

        private void BroadcastStatChangeToAllClients(NetworkStatChangeBroadcast broadcast)
        {
            var nm = ActiveManager;
            if (nm == null || !nm.isServer) return;
            nm.SendToAll(new GC2StatChangeBroadcastPacket { broadcast = broadcast }, m_Channel);
        }

        private void BroadcastAttributeChangeToAllClients(NetworkAttributeChangeBroadcast broadcast)
        {
            var nm = ActiveManager;
            if (nm == null || !nm.isServer) return;
            nm.SendToAll(new GC2AttributeChangeBroadcastPacket { broadcast = broadcast }, m_Channel);
        }

        private void BroadcastStatusEffectChangeToAllClients(NetworkStatusEffectBroadcast broadcast)
        {
            var nm = ActiveManager;
            if (nm == null || !nm.isServer) return;
            nm.SendToAll(new GC2StatusEffectBroadcastPacket { broadcast = broadcast }, m_Channel);
        }

        private void BroadcastStatModifierChangeToAllClients(NetworkStatModifierBroadcast broadcast)
        {
            var nm = ActiveManager;
            if (nm == null || !nm.isServer) return;
            nm.SendToAll(new GC2StatModifierBroadcastPacket { broadcast = broadcast }, m_Channel);
        }

        private void BroadcastSnapshotToAllClients(NetworkStatsSnapshot snapshot)
        {
            var nm = ActiveManager;
            if (nm == null || !nm.isServer) return;
            nm.SendToAll(new GC2StatsSnapshotPacket { snapshot = snapshot }, m_Channel);
        }

        private void BroadcastDeltaToAllClients(NetworkStatsDelta delta)
        {
            var nm = ActiveManager;
            if (nm == null || !nm.isServer) return;
            nm.SendToAll(new GC2StatsDeltaPacket { delta = delta }, m_Channel);
        }

        private void SendSnapshotToClient(ulong clientId, NetworkStatsSnapshot snapshot)
        {
            var nm = ActiveManager;
            if (nm == null || !nm.isServer) return;
            if (!TryGetPlayerId(nm, clientId, out var playerId)) return;
            nm.Send(playerId, new GC2StatsSnapshotPacket { snapshot = snapshot }, m_Channel);
        }

        private void HandleStatModifyRequestServer(PlayerID senderPlayer, GC2StatModifyRequestPacket data, bool asServer)
        {
            if (!asServer) return;
            DispatchStatModifyRequestOnServer(senderPlayer, data);
        }

        private void DispatchStatModifyRequestOnServer(PlayerID senderPlayer, GC2StatModifyRequestPacket data)
        {
            GetStatsManager()?.ReceiveStatModifyRequest(data.request, senderPlayer.id);
        }

        private void HandleAttributeModifyRequestServer(PlayerID senderPlayer, GC2AttributeModifyRequestPacket data, bool asServer)
        {
            if (!asServer) return;
            DispatchAttributeModifyRequestOnServer(senderPlayer, data);
        }

        private void DispatchAttributeModifyRequestOnServer(PlayerID senderPlayer, GC2AttributeModifyRequestPacket data)
        {
            GetStatsManager()?.ReceiveAttributeModifyRequest(data.request, senderPlayer.id);
        }

        private void HandleStatusEffectRequestServer(PlayerID senderPlayer, GC2StatusEffectRequestPacket data, bool asServer)
        {
            if (!asServer) return;
            DispatchStatusEffectRequestOnServer(senderPlayer, data);
        }

        private void DispatchStatusEffectRequestOnServer(PlayerID senderPlayer, GC2StatusEffectRequestPacket data)
        {
            GetStatsManager()?.ReceiveStatusEffectRequest(data.request, senderPlayer.id);
        }

        private void HandleStatModifierRequestServer(PlayerID senderPlayer, GC2StatModifierRequestPacket data, bool asServer)
        {
            if (!asServer) return;
            DispatchStatModifierRequestOnServer(senderPlayer, data);
        }

        private void DispatchStatModifierRequestOnServer(PlayerID senderPlayer, GC2StatModifierRequestPacket data)
        {
            GetStatsManager()?.ReceiveStatModifierRequest(data.request, senderPlayer.id);
        }

        private void HandleClearStatusEffectsRequestServer(PlayerID senderPlayer, GC2ClearStatusEffectsRequestPacket data, bool asServer)
        {
            if (!asServer) return;
            DispatchClearStatusEffectsRequestOnServer(senderPlayer, data);
        }

        private void DispatchClearStatusEffectsRequestOnServer(PlayerID senderPlayer, GC2ClearStatusEffectsRequestPacket data)
        {
            GetStatsManager()?.ReceiveClearStatusEffectsRequest(data.request, senderPlayer.id);
        }

        private void HandleStatModifyResponseClient(PlayerID senderPlayer, GC2StatModifyResponsePacket data, bool asServer)
        {
            if (asServer) return;
            GetStatsManager()?.ReceiveStatModifyResponse(data.response, data.response.ActorNetworkId);
        }

        private void HandleAttributeModifyResponseClient(PlayerID senderPlayer, GC2AttributeModifyResponsePacket data, bool asServer)
        {
            if (asServer) return;
            GetStatsManager()?.ReceiveAttributeModifyResponse(data.response, data.response.ActorNetworkId);
        }

        private void HandleStatusEffectResponseClient(PlayerID senderPlayer, GC2StatusEffectResponsePacket data, bool asServer)
        {
            if (asServer) return;
            GetStatsManager()?.ReceiveStatusEffectResponse(data.response, data.response.ActorNetworkId);
        }

        private void HandleStatModifierResponseClient(PlayerID senderPlayer, GC2StatModifierResponsePacket data, bool asServer)
        {
            if (asServer) return;
            GetStatsManager()?.ReceiveStatModifierResponse(data.response, data.response.ActorNetworkId);
        }

        private void HandleClearStatusEffectsResponseClient(PlayerID senderPlayer, GC2ClearStatusEffectsResponsePacket data, bool asServer)
        {
            if (asServer) return;
            GetStatsManager()?.ReceiveClearStatusEffectsResponse(data.response, data.response.ActorNetworkId);
        }

        private void HandleStatChangeBroadcastClient(PlayerID senderPlayer, GC2StatChangeBroadcastPacket data, bool asServer)
        {
            if (asServer) return;
            GetStatsManager()?.ReceiveStatChangeBroadcast(data.broadcast);
        }

        private void HandleAttributeChangeBroadcastClient(PlayerID senderPlayer, GC2AttributeChangeBroadcastPacket data, bool asServer)
        {
            if (asServer) return;
            GetStatsManager()?.ReceiveAttributeChangeBroadcast(data.broadcast);
        }

        private void HandleStatusEffectBroadcastClient(PlayerID senderPlayer, GC2StatusEffectBroadcastPacket data, bool asServer)
        {
            if (asServer) return;
            GetStatsManager()?.ReceiveStatusEffectBroadcast(data.broadcast);
        }

        private void HandleStatModifierBroadcastClient(PlayerID senderPlayer, GC2StatModifierBroadcastPacket data, bool asServer)
        {
            if (asServer) return;
            GetStatsManager()?.ReceiveStatModifierBroadcast(data.broadcast);
        }

        private void HandleSnapshotClient(PlayerID senderPlayer, GC2StatsSnapshotPacket data, bool asServer)
        {
            if (asServer) return;
            GetStatsManager()?.ReceiveFullSnapshot(data.snapshot);
        }

        private void HandleDeltaClient(PlayerID senderPlayer, GC2StatsDeltaPacket data, bool asServer)
        {
            if (asServer) return;
            GetStatsManager()?.ReceiveDelta(data.delta);
        }

        private static NetworkStatsManager GetStatsManager()
        {
            return NetworkStatsManager.Instance != null
                ? NetworkStatsManager.Instance
                : FindFirstObjectByType<NetworkStatsManager>();
        }

        private static uint PlayerIdToClientId(PlayerID playerId)
        {
            ulong raw = playerId.id;
            if (raw > uint.MaxValue) return NetworkTransportBridge.InvalidClientId;
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

        private static bool TryGetPlayerId(NetworkManager manager, ulong rawClientId, out PlayerID playerId)
        {
            playerId = default;
            if (!NetworkTransportBridge.TryConvertSenderClientId(rawClientId, out uint clientId)) return false;
            return TryGetPlayerId(manager, clientId, out playerId);
        }
    }
}
#endif
