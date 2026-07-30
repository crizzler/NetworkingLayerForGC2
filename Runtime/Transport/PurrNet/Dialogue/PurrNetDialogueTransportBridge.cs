#if GC2_DIALOGUE
using System.Collections.Generic;
using Arawn.GameCreator2.Networking;
using Arawn.GameCreator2.Networking.Transport.PurrNet;
using PurrNet;
using PurrNet.Transports;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Dialogue.Transport.PurrNet
{
    [DisallowMultipleComponent]
    [AddComponentMenu("Game Creator/Network/Transport/PurrNet Dialogue Bridge")]
    [DefaultExecutionOrder(-337)]
    public sealed class PurrNetDialogueTransportBridge : MonoBehaviour
    {
        [Header("PurrNet")]
        [Tooltip("Optional reference to a specific NetworkManager. Leave empty to use NetworkManager.main.")]
        [SerializeField] private NetworkManager m_NetworkManager;

        [Tooltip("Reliable channel used for dialogue requests, responses, broadcasts, and snapshots.")]
        [SerializeField] private Channel m_Channel = Channel.ReliableOrdered;

        [Header("Controllers")]
        [SerializeField] private bool m_AutoRegisterSceneControllers = true;

        [Min(0.05f)]
        [SerializeField] private float m_ControllerScanInterval = 0.25f;

        [Header("Debug")]
        [SerializeField] private bool m_LogNetworkMessages;

        private readonly Dictionary<uint, NetworkDialogueController> m_RegisteredControllers = new(32);
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
            WireDialogueManager();
            RefreshControllerRegistry(force: true);
        }

        private void Start()
        {
            TryHookNetworkManager();
            WireDialogueManager();
            RefreshControllerRegistry(force: true);
        }

        private void Update()
        {
            TryHookNetworkManager();
            WireDialogueManager();

            if (!m_AutoRegisterSceneControllers) return;
            if (Time.unscaledTime < m_NextControllerScanTime) return;

            m_NextControllerScanTime = Time.unscaledTime + Mathf.Max(0.05f, m_ControllerScanInterval);
            RefreshControllerRegistry(force: false);
        }

        private void OnDisable()
        {
            UnhookNetworkManager();
            UnwireDialogueManager();
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
                nm.Unsubscribe<GC2DialogueRequestPacket>(HandleDialogueRequestServer, true);
                m_SubscribedServer = false;
            }

            if (m_SubscribedClient)
            {
                nm.Unsubscribe<GC2DialogueResponsePacket>(HandleDialogueResponseClient, false);
                nm.Unsubscribe<GC2DialogueBroadcastPacket>(HandleDialogueBroadcastClient, false);
                nm.Unsubscribe<GC2DialogueSnapshotPacket>(HandleDialogueSnapshotClient, false);
                m_SubscribedClient = false;
            }

            m_HookedManager = null;
        }

        private void HandleNetworkStarted(NetworkManager manager, bool asServer)
        {
            if (asServer && !m_SubscribedServer)
            {
                manager.Subscribe<GC2DialogueRequestPacket>(HandleDialogueRequestServer, true);
                m_SubscribedServer = true;
            }
            else if (!asServer && !m_SubscribedClient)
            {
                manager.Subscribe<GC2DialogueResponsePacket>(HandleDialogueResponseClient, false);
                manager.Subscribe<GC2DialogueBroadcastPacket>(HandleDialogueBroadcastClient, false);
                manager.Subscribe<GC2DialogueSnapshotPacket>(HandleDialogueSnapshotClient, false);
                m_SubscribedClient = true;
            }

            WireDialogueManager();
            RefreshControllerRegistry(force: true);
        }

        private void HandleNetworkShutdown(NetworkManager manager, bool asServer)
        {
            if (asServer && m_SubscribedServer)
            {
                manager.Unsubscribe<GC2DialogueRequestPacket>(HandleDialogueRequestServer, true);
                m_SubscribedServer = false;
            }
            else if (!asServer && m_SubscribedClient)
            {
                manager.Unsubscribe<GC2DialogueResponsePacket>(HandleDialogueResponseClient, false);
                manager.Unsubscribe<GC2DialogueBroadcastPacket>(HandleDialogueBroadcastClient, false);
                manager.Unsubscribe<GC2DialogueSnapshotPacket>(HandleDialogueSnapshotClient, false);
                m_SubscribedClient = false;
            }

            WireDialogueManager();
        }

        private void HandlePlayerLoadedScene(PlayerID player, SceneID scene, bool asServer)
        {
            if (!asServer) return;
            RefreshControllerRegistry(force: true);
            GetDialogueManager()?.SendAllSnapshotsToClient(player.id);
        }

        private void WireDialogueManager()
        {
            NetworkDialogueManager manager = GetDialogueManager();
            if (manager == null) return;

            manager.OnSendDialogueRequest -= SendDialogueRequestToServer;
            manager.OnSendDialogueRequest += SendDialogueRequestToServer;
            manager.OnSendDialogueResponse -= SendDialogueResponseToClient;
            manager.OnSendDialogueResponse += SendDialogueResponseToClient;
            manager.OnBroadcastDialogueChange -= BroadcastDialogueChangeToAllClients;
            manager.OnBroadcastDialogueChange += BroadcastDialogueChangeToAllClients;
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

        private void UnwireDialogueManager()
        {
            NetworkDialogueManager manager = GetDialogueManager();
            if (manager == null) return;

            manager.OnSendDialogueRequest -= SendDialogueRequestToServer;
            manager.OnSendDialogueResponse -= SendDialogueResponseToClient;
            manager.OnBroadcastDialogueChange -= BroadcastDialogueChangeToAllClients;
            manager.OnBroadcastFullSnapshot -= BroadcastSnapshotToAllClients;
            manager.OnSendSnapshotToClient -= SendSnapshotToClient;
            m_ManagerInitialized = false;
        }

        private void RefreshControllerRegistry(bool force)
        {
            NetworkDialogueManager manager = GetDialogueManager();
            if (manager == null) return;

            PruneControllerRegistry();

            if (!m_AutoRegisterSceneControllers && !force) return;

            NetworkDialogueController[] controllers = FindObjectsByType<NetworkDialogueController>(
                FindObjectsInactive.Exclude,
                FindObjectsSortMode.None);

            for (int i = 0; i < controllers.Length; i++)
            {
                RegisterController(manager, controllers[i]);
            }
        }

        private void RegisterController(NetworkDialogueManager manager, NetworkDialogueController controller)
        {
            if (manager == null || controller == null) return;

            NetworkCharacter networkCharacter = controller.NetworkCharacter;
            uint networkId = controller.NetworkId;
            if (networkId == 0) return;

            NetworkManager nm = ActiveManager;
            bool isServer = nm != null && nm.isServer;
            bool isLocalClient = networkCharacter != null
                ? networkCharacter.IsOwnerInstance
                : !controller.RequiresTargetOwnership;

            if (m_RegisteredControllers.TryGetValue(networkId, out NetworkDialogueController existing))
            {
                if (existing == controller)
                {
                    if (controller.IsServer != isServer || controller.IsLocalClient != isLocalClient)
                    {
                        controller.Initialize(isServer, isLocalClient);
                        manager.RegisterController(networkId, controller);
                        Log($"updated dialogue controller role netId={networkId} name={controller.name} authority={controller.AuthorityMode} server={isServer} local={isLocalClient}");
                    }

                    return;
                }

                manager.UnregisterController(networkId);
            }

            controller.Initialize(isServer, isLocalClient);
            manager.RegisterController(networkId, controller);
            m_RegisteredControllers[networkId] = controller;
            Log($"registered dialogue controller netId={networkId} name={controller.name} authority={controller.AuthorityMode} server={isServer} local={isLocalClient}");
        }

        private void PruneControllerRegistry()
        {
            m_RemoveBuffer.Clear();

            foreach (KeyValuePair<uint, NetworkDialogueController> pair in m_RegisteredControllers)
            {
                NetworkDialogueController controller = pair.Value;
                if (controller == null || controller.NetworkId != pair.Key)
                {
                    m_RemoveBuffer.Add(pair.Key);
                }
            }

            NetworkDialogueManager manager = GetDialogueManager();
            for (int i = 0; i < m_RemoveBuffer.Count; i++)
            {
                uint networkId = m_RemoveBuffer[i];
                manager?.UnregisterController(networkId);
                m_RegisteredControllers.Remove(networkId);
            }
        }

        private void SendDialogueRequestToServer(NetworkDialogueRequest request)
        {
            NetworkManager nm = ActiveManager;
            if (nm == null || !nm.isClient) return;

            Log($"send dialogue request to server requestId={request.RequestId} actor={request.ActorNetworkId} target={request.TargetNetworkId} action={request.Action} dialogue='{request.DialogueIdString}' hash={request.DialogueHash} choice={request.ChoiceNodeId}");
            var packet = new GC2DialogueRequestPacket { request = request };
            if (nm.isServer)
            {
                Log($"loopback dialogue request through local server player={nm.localPlayer.id}");
                if (nm.isLocalPlayerReady) _ = DispatchDialogueRequestOnServer(nm.localPlayer, packet);
                return;
            }

            nm.SendToServer(packet, m_Channel);
        }

        private void SendDialogueResponseToClient(uint clientNetworkId, NetworkDialogueResponse response)
        {
            NetworkManager nm = ActiveManager;
            if (nm == null || !nm.isServer) return;
            if (!TryGetPlayerId(nm, clientNetworkId, out PlayerID playerId))
            {
                Log($"cannot send dialogue response: player not found clientNetworkId={clientNetworkId} requestId={response.RequestId} actor={response.ActorNetworkId} target={response.TargetNetworkId} action={response.Action}");
                return;
            }

            Log($"send dialogue response to client={clientNetworkId} player={playerId.id} requestId={response.RequestId} actor={response.ActorNetworkId} target={response.TargetNetworkId} action={response.Action} authorized={response.Authorized} applied={response.Applied} rejection={response.RejectionReason}");
            nm.Send(playerId, new GC2DialogueResponsePacket { response = response }, m_Channel);
        }

        private void BroadcastDialogueChangeToAllClients(NetworkDialogueBroadcast broadcast)
        {
            NetworkManager nm = ActiveManager;
            if (nm == null || !nm.isServer) return;

            Log($"broadcast dialogue change networkId={broadcast.NetworkId} actor={broadcast.ActorNetworkId} action={broadcast.Action} dialogue='{broadcast.DialogueIdString}' playing={broadcast.IsPlaying} node={broadcast.CurrentNodeId} choice={broadcast.ChoiceNodeId}");
            nm.SendToAll(new GC2DialogueBroadcastPacket { broadcast = broadcast }, m_Channel);
        }

        private void BroadcastSnapshotToAllClients(NetworkDialogueSnapshot snapshot)
        {
            NetworkManager nm = ActiveManager;
            if (nm == null || !nm.isServer) return;

            Log($"broadcast dialogue snapshot networkId={snapshot.NetworkId} dialogue='{snapshot.DialogueIdString}' playing={snapshot.IsPlaying} visitedNodes={snapshot.VisitedNodeIds?.Length ?? 0} visitedTags={snapshot.VisitedTagIds?.Length ?? 0}");
            nm.SendToAll(new GC2DialogueSnapshotPacket { snapshot = snapshot }, m_Channel);
        }

        private void SendSnapshotToClient(ulong clientId, NetworkDialogueSnapshot snapshot)
        {
            NetworkManager nm = ActiveManager;
            if (nm == null || !nm.isServer) return;
            if (!TryGetPlayerId(nm, clientId, out PlayerID playerId))
            {
                Log($"cannot send dialogue snapshot: player not found rawClientId={clientId} networkId={snapshot.NetworkId}");
                return;
            }

            Log($"send dialogue snapshot to client={clientId} player={playerId.id} networkId={snapshot.NetworkId} dialogue='{snapshot.DialogueIdString}' playing={snapshot.IsPlaying}");
            nm.Send(playerId, new GC2DialogueSnapshotPacket { snapshot = snapshot }, m_Channel);
        }

        private void HandleDialogueRequestServer(PlayerID senderPlayer, GC2DialogueRequestPacket data, bool asServer)
        {
            if (!asServer) return;
            Log($"server received dialogue request sender={senderPlayer.id} requestId={data.request.RequestId} actor={data.request.ActorNetworkId} target={data.request.TargetNetworkId} action={data.request.Action}");
            RefreshControllerRegistry(force: true);
            _ = DispatchDialogueRequestOnServer(senderPlayer, data);
        }

        private System.Threading.Tasks.Task DispatchDialogueRequestOnServer(PlayerID senderPlayer, GC2DialogueRequestPacket data)
        {
            return GetDialogueManager()?.ReceiveDialogueRequest(data.request, senderPlayer.id) ??
                   System.Threading.Tasks.Task.CompletedTask;
        }

        private void HandleDialogueResponseClient(PlayerID senderPlayer, GC2DialogueResponsePacket data, bool asServer)
        {
            if (asServer) return;
            Log($"client received dialogue response sender={senderPlayer.id} requestId={data.response.RequestId} actor={data.response.ActorNetworkId} target={data.response.TargetNetworkId} action={data.response.Action} authorized={data.response.Authorized} applied={data.response.Applied} rejection={data.response.RejectionReason}");
            GetDialogueManager()?.ReceiveDialogueResponse(data.response, data.response.TargetNetworkId);
        }

        private void HandleDialogueBroadcastClient(PlayerID senderPlayer, GC2DialogueBroadcastPacket data, bool asServer)
        {
            if (asServer) return;
            Log($"client received dialogue broadcast sender={senderPlayer.id} networkId={data.broadcast.NetworkId} actor={data.broadcast.ActorNetworkId} action={data.broadcast.Action} dialogue='{data.broadcast.DialogueIdString}' playing={data.broadcast.IsPlaying}");
            RefreshControllerRegistry(force: true);
            GetDialogueManager()?.ReceiveDialogueChangeBroadcast(data.broadcast);
        }

        private void HandleDialogueSnapshotClient(PlayerID senderPlayer, GC2DialogueSnapshotPacket data, bool asServer)
        {
            if (asServer) return;
            Log($"client received dialogue snapshot sender={senderPlayer.id} networkId={data.snapshot.NetworkId} dialogue='{data.snapshot.DialogueIdString}' playing={data.snapshot.IsPlaying}");
            RefreshControllerRegistry(force: true);
            GetDialogueManager()?.ReceiveFullSnapshot(data.snapshot);
        }

        private void Log(string message)
        {
            if (!m_LogNetworkMessages) return;
            Debug.Log($"[PurrNetDialogueTransportBridge] {message}", this);
        }

        private static NetworkDialogueManager GetDialogueManager()
        {
            return NetworkDialogueManager.Instance != null
                ? NetworkDialogueManager.Instance
                : FindFirstObjectByType<NetworkDialogueManager>();
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
