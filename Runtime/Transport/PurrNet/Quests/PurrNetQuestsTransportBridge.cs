#if GC2_QUESTS
using System.Collections.Generic;
using Arawn.GameCreator2.Networking.Transport.PurrNet;
using PurrNet;
using PurrNet.Transports;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Quests.Transport.PurrNet
{
    [DisallowMultipleComponent]
    [AddComponentMenu("Game Creator/Network/Transport/PurrNet Quests Bridge")]
    [DefaultExecutionOrder(-338)]
    public sealed class PurrNetQuestsTransportBridge : MonoBehaviour
    {
        [Header("PurrNet")]
        [Tooltip("Optional reference to a specific NetworkManager. Leave empty to use NetworkManager.main.")]
        [SerializeField] private NetworkManager m_NetworkManager;

        [Tooltip("Reliable channel used for quest requests, responses, broadcasts, and snapshots.")]
        [SerializeField] private Channel m_Channel = Channel.ReliableOrdered;

        [Header("Controllers")]
        [SerializeField] private bool m_AutoRegisterSceneControllers = true;

        [Min(0.05f)]
        [SerializeField] private float m_ControllerScanInterval = 0.25f;

        [Header("Debug")]
        [SerializeField] private bool m_LogNetworkMessages;

        private readonly Dictionary<uint, NetworkQuestsController> m_RegisteredControllers = new(32);
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
            WireQuestsManager();
            RefreshControllerRegistry(force: true);
        }

        private void Start()
        {
            TryHookNetworkManager();
            WireQuestsManager();
            RefreshControllerRegistry(force: true);
        }

        private void Update()
        {
            TryHookNetworkManager();
            WireQuestsManager();

            if (!m_AutoRegisterSceneControllers) return;
            if (Time.unscaledTime < m_NextControllerScanTime) return;

            m_NextControllerScanTime = Time.unscaledTime + Mathf.Max(0.05f, m_ControllerScanInterval);
            RefreshControllerRegistry(force: false);
        }

        private void OnDisable()
        {
            UnhookNetworkManager();
            UnwireQuestsManager();
            m_RegisteredControllers.Clear();
        }

        private void TryHookNetworkManager()
        {
            NetworkManager nm = ActiveManager;
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
            NetworkManager nm = m_HookedManager;
            if (nm == null) return;

            nm.onNetworkStarted -= HandleNetworkStarted;
            nm.onNetworkShutdown -= HandleNetworkShutdown;
            nm.onPlayerLoadedScene -= HandlePlayerLoadedScene;

            if (m_SubscribedServer)
            {
                nm.Unsubscribe<GC2QuestRequestPacket>(HandleQuestRequestServer, true);
                m_SubscribedServer = false;
            }

            if (m_SubscribedClient)
            {
                nm.Unsubscribe<GC2QuestResponsePacket>(HandleQuestResponseClient, false);
                nm.Unsubscribe<GC2QuestBroadcastPacket>(HandleQuestBroadcastClient, false);
                nm.Unsubscribe<GC2QuestSnapshotPacket>(HandleQuestSnapshotClient, false);
                m_SubscribedClient = false;
            }

            m_HookedManager = null;
        }

        private void HandleNetworkStarted(NetworkManager manager, bool asServer)
        {
            if (asServer && !m_SubscribedServer)
            {
                manager.Subscribe<GC2QuestRequestPacket>(HandleQuestRequestServer, true);
                m_SubscribedServer = true;
            }
            else if (!asServer && !m_SubscribedClient)
            {
                manager.Subscribe<GC2QuestResponsePacket>(HandleQuestResponseClient, false);
                manager.Subscribe<GC2QuestBroadcastPacket>(HandleQuestBroadcastClient, false);
                manager.Subscribe<GC2QuestSnapshotPacket>(HandleQuestSnapshotClient, false);
                m_SubscribedClient = true;
            }

            WireQuestsManager();
            RefreshControllerRegistry(force: true);
        }

        private void HandleNetworkShutdown(NetworkManager manager, bool asServer)
        {
            if (asServer && m_SubscribedServer)
            {
                manager.Unsubscribe<GC2QuestRequestPacket>(HandleQuestRequestServer, true);
                m_SubscribedServer = false;
            }
            else if (!asServer && m_SubscribedClient)
            {
                manager.Unsubscribe<GC2QuestResponsePacket>(HandleQuestResponseClient, false);
                manager.Unsubscribe<GC2QuestBroadcastPacket>(HandleQuestBroadcastClient, false);
                manager.Unsubscribe<GC2QuestSnapshotPacket>(HandleQuestSnapshotClient, false);
                m_SubscribedClient = false;
            }

            WireQuestsManager();
        }

        private void HandlePlayerLoadedScene(PlayerID player, SceneID scene, bool asServer)
        {
            if (!asServer) return;
            RefreshControllerRegistry(force: true);
            GetQuestsManager()?.SendAllSnapshotsToClient(player.id);
        }

        private void WireQuestsManager()
        {
            NetworkQuestsManager manager = GetQuestsManager();
            if (manager == null) return;

            manager.OnSendQuestRequest -= SendQuestRequestToServer;
            manager.OnSendQuestRequest += SendQuestRequestToServer;
            manager.OnSendQuestResponse -= SendQuestResponseToClient;
            manager.OnSendQuestResponse += SendQuestResponseToClient;
            manager.OnBroadcastQuestChange -= BroadcastQuestChangeToAllClients;
            manager.OnBroadcastQuestChange += BroadcastQuestChangeToAllClients;
            manager.OnBroadcastFullSnapshot -= BroadcastSnapshotToAllClients;
            manager.OnBroadcastFullSnapshot += BroadcastSnapshotToAllClients;
            manager.OnSendSnapshotToClient -= SendSnapshotToClient;
            manager.OnSendSnapshotToClient += SendSnapshotToClient;

            NetworkManager nm = ActiveManager;
            bool isServer = nm != null && nm.isServer;
            if (!m_ManagerInitialized || isServer != m_LastServer)
            {
                manager.IsServer = isServer;
                m_ManagerInitialized = true;
                m_LastServer = isServer;
            }
        }

        private void UnwireQuestsManager()
        {
            NetworkQuestsManager manager = GetQuestsManager();
            if (manager == null) return;

            manager.OnSendQuestRequest -= SendQuestRequestToServer;
            manager.OnSendQuestResponse -= SendQuestResponseToClient;
            manager.OnBroadcastQuestChange -= BroadcastQuestChangeToAllClients;
            manager.OnBroadcastFullSnapshot -= BroadcastSnapshotToAllClients;
            manager.OnSendSnapshotToClient -= SendSnapshotToClient;
            m_ManagerInitialized = false;
        }

        private void RefreshControllerRegistry(bool force)
        {
            NetworkQuestsManager manager = GetQuestsManager();
            if (manager == null) return;

            PruneControllerRegistry();

            if (!m_AutoRegisterSceneControllers && !force) return;

            NetworkQuestsController[] controllers = FindObjectsByType<NetworkQuestsController>(
                FindObjectsInactive.Exclude,
                FindObjectsSortMode.None);

            for (int i = 0; i < controllers.Length; i++)
            {
                RegisterController(manager, controllers[i]);
            }
        }

        private void RegisterController(NetworkQuestsManager manager, NetworkQuestsController controller)
        {
            if (manager == null || controller == null) return;

            NetworkCharacter networkCharacter = controller.GetComponent<NetworkCharacter>();
            if (networkCharacter == null || networkCharacter.NetworkId == 0) return;

            uint networkId = networkCharacter.NetworkId;
            if (m_RegisteredControllers.TryGetValue(networkId, out NetworkQuestsController existing))
            {
                if (existing == controller) return;
                manager.UnregisterController(networkId);
            }

            NetworkManager nm = ActiveManager;
            bool isServer = nm != null && nm.isServer;
            bool isLocalClient = networkCharacter.IsOwnerInstance;
            controller.Initialize(isServer, isLocalClient);
            manager.RegisterController(networkId, controller);
            m_RegisteredControllers[networkId] = controller;
            Log($"registered quests controller netId={networkId} name={controller.name} server={isServer} local={isLocalClient}");
        }

        private void PruneControllerRegistry()
        {
            m_RemoveBuffer.Clear();

            foreach (KeyValuePair<uint, NetworkQuestsController> pair in m_RegisteredControllers)
            {
                NetworkQuestsController controller = pair.Value;
                NetworkCharacter networkCharacter = controller != null ? controller.GetComponent<NetworkCharacter>() : null;
                if (controller == null || networkCharacter == null || networkCharacter.NetworkId != pair.Key)
                {
                    m_RemoveBuffer.Add(pair.Key);
                }
            }

            NetworkQuestsManager manager = GetQuestsManager();
            for (int i = 0; i < m_RemoveBuffer.Count; i++)
            {
                uint networkId = m_RemoveBuffer[i];
                manager?.UnregisterController(networkId);
                m_RegisteredControllers.Remove(networkId);
            }
        }

        private void SendQuestRequestToServer(NetworkQuestRequest request)
        {
            NetworkManager nm = ActiveManager;
            if (nm == null || !nm.isClient) return;

            Log($"send quest request to server requestId={request.RequestId} actor={request.ActorNetworkId} target={request.TargetNetworkId} action={request.Action} share={request.ShareMode} quest='{request.QuestIdString}' hash={request.QuestHash} task={request.TaskId}");
            var packet = new GC2QuestRequestPacket { request = request };
            if (nm.isServer)
            {
                Log($"loopback quest request through local server player={nm.localPlayer.id}");
                if (nm.isLocalPlayerReady) _ = DispatchQuestRequestOnServer(nm.localPlayer, packet);
                return;
            }

            nm.SendToServer(packet, m_Channel);
        }

        private void SendQuestResponseToClient(uint clientNetworkId, NetworkQuestResponse response)
        {
            NetworkManager nm = ActiveManager;
            if (nm == null || !nm.isServer) return;
            if (!TryGetPlayerId(nm, clientNetworkId, out PlayerID playerId))
            {
                Log($"cannot send quest response: player not found clientNetworkId={clientNetworkId} requestId={response.RequestId} actor={response.ActorNetworkId} action={response.Action}");
                return;
            }

            Log($"send quest response to client={clientNetworkId} player={playerId.id} requestId={response.RequestId} actor={response.ActorNetworkId} action={response.Action} authorized={response.Authorized} applied={response.Applied} rejection={response.RejectionReason}");
            nm.Send(playerId, new GC2QuestResponsePacket { response = response }, m_Channel);
        }

        private void BroadcastQuestChangeToAllClients(NetworkQuestBroadcast broadcast)
        {
            NetworkManager nm = ActiveManager;
            if (nm == null || !nm.isServer) return;

            Log($"broadcast quest change networkId={broadcast.NetworkId} actor={broadcast.ActorNetworkId} action={broadcast.Action} share={broadcast.ShareMode} quest='{broadcast.QuestIdString}' state={broadcast.QuestState} task={broadcast.TaskId} taskState={broadcast.TaskState}");
            nm.SendToAll(new GC2QuestBroadcastPacket { broadcast = broadcast }, m_Channel);
        }

        private void BroadcastSnapshotToAllClients(NetworkQuestsSnapshot snapshot)
        {
            NetworkManager nm = ActiveManager;
            if (nm == null || !nm.isServer) return;

            Log($"broadcast quest snapshot networkId={snapshot.NetworkId} quests={snapshot.QuestEntries?.Length ?? 0} tasks={snapshot.TaskEntries?.Length ?? 0}");
            nm.SendToAll(new GC2QuestSnapshotPacket { snapshot = snapshot }, m_Channel);
        }

        private void SendSnapshotToClient(ulong clientId, NetworkQuestsSnapshot snapshot)
        {
            NetworkManager nm = ActiveManager;
            if (nm == null || !nm.isServer) return;
            if (!TryGetPlayerId(nm, clientId, out PlayerID playerId))
            {
                Log($"cannot send quest snapshot: player not found rawClientId={clientId} networkId={snapshot.NetworkId}");
                return;
            }

            Log($"send quest snapshot to client={clientId} player={playerId.id} networkId={snapshot.NetworkId} quests={snapshot.QuestEntries?.Length ?? 0} tasks={snapshot.TaskEntries?.Length ?? 0}");
            nm.Send(playerId, new GC2QuestSnapshotPacket { snapshot = snapshot }, m_Channel);
        }

        private void HandleQuestRequestServer(PlayerID senderPlayer, GC2QuestRequestPacket data, bool asServer)
        {
            if (!asServer) return;
            Log($"server received quest request sender={senderPlayer.id} requestId={data.request.RequestId} actor={data.request.ActorNetworkId} target={data.request.TargetNetworkId} action={data.request.Action} share={data.request.ShareMode}");
            RefreshControllerRegistry(force: true);
            _ = DispatchQuestRequestOnServer(senderPlayer, data);
        }

        private System.Threading.Tasks.Task DispatchQuestRequestOnServer(PlayerID senderPlayer, GC2QuestRequestPacket data)
        {
            return GetQuestsManager()?.ReceiveQuestRequest(data.request, senderPlayer.id) ??
                   System.Threading.Tasks.Task.CompletedTask;
        }

        private void HandleQuestResponseClient(PlayerID senderPlayer, GC2QuestResponsePacket data, bool asServer)
        {
            if (asServer) return;
            Log($"client received quest response sender={senderPlayer.id} requestId={data.response.RequestId} actor={data.response.ActorNetworkId} action={data.response.Action} authorized={data.response.Authorized} applied={data.response.Applied} rejection={data.response.RejectionReason}");
            GetQuestsManager()?.ReceiveQuestResponse(data.response, data.response.ActorNetworkId);
        }

        private void HandleQuestBroadcastClient(PlayerID senderPlayer, GC2QuestBroadcastPacket data, bool asServer)
        {
            if (asServer) return;
            Log($"client received quest broadcast sender={senderPlayer.id} networkId={data.broadcast.NetworkId} actor={data.broadcast.ActorNetworkId} action={data.broadcast.Action} share={data.broadcast.ShareMode} quest='{data.broadcast.QuestIdString}' state={data.broadcast.QuestState}");
            RefreshControllerRegistry(force: true);
            GetQuestsManager()?.ReceiveQuestChangeBroadcast(data.broadcast);
        }

        private void HandleQuestSnapshotClient(PlayerID senderPlayer, GC2QuestSnapshotPacket data, bool asServer)
        {
            if (asServer) return;
            Log($"client received quest snapshot sender={senderPlayer.id} networkId={data.snapshot.NetworkId} quests={data.snapshot.QuestEntries?.Length ?? 0} tasks={data.snapshot.TaskEntries?.Length ?? 0}");
            RefreshControllerRegistry(force: true);
            GetQuestsManager()?.ReceiveFullSnapshot(data.snapshot);
        }

        private void Log(string message)
        {
            if (!m_LogNetworkMessages) return;
            Debug.Log($"[PurrNetQuestsTransportBridge] {message}", this);
        }

        private static NetworkQuestsManager GetQuestsManager()
        {
            return NetworkQuestsManager.Instance != null
                ? NetworkQuestsManager.Instance
                : FindFirstObjectByType<NetworkQuestsManager>();
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

            IReadOnlyList<PlayerID> players = manager.players;
            for (int i = 0; i < players.Count; i++)
            {
                PlayerID candidate = players[i];
                if (PlayerIdToClientId(candidate) == clientId)
                {
                    playerId = candidate;
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
