#if GC2_TRAVERSAL
using System.Collections.Generic;
using Arawn.GameCreator2.Networking;
using Arawn.GameCreator2.Networking.Traversal;
using PurrNet;
using PurrNet.Transports;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Traversal.Transport.PurrNet
{
    [DisallowMultipleComponent]
    [AddComponentMenu("Game Creator/Network/Transport/PurrNet Traversal Bridge")]
    [DefaultExecutionOrder(-336)]
    public sealed class PurrNetTraversalTransportBridge : MonoBehaviour
    {
        [Header("PurrNet")]
        [Tooltip("Optional reference to a specific NetworkManager. Leave empty to use NetworkManager.main.")]
        [SerializeField] private NetworkManager m_NetworkManager;

        [Tooltip("Reliable channel used for traversal requests, responses, broadcasts, and snapshots.")]
        [SerializeField] private Channel m_Channel = Channel.ReliableOrdered;

        [Header("Controllers")]
        [SerializeField] private bool m_AutoRegisterSceneControllers = true;

        [Min(0.05f)]
        [SerializeField] private float m_ControllerScanInterval = 0.25f;

        [Header("Debug")]
        [SerializeField] private bool m_LogNetworkMessages;

        private readonly Dictionary<uint, NetworkTraversalController> m_RegisteredControllers = new(32);
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
            WireTraversalManager();
            RefreshControllerRegistry(force: true);
        }

        private void Start()
        {
            TryHookNetworkManager();
            WireTraversalManager();
            RefreshControllerRegistry(force: true);
        }

        private void Update()
        {
            TryHookNetworkManager();
            WireTraversalManager();

            if (!m_AutoRegisterSceneControllers) return;
            if (Time.unscaledTime < m_NextControllerScanTime) return;

            m_NextControllerScanTime = Time.unscaledTime + Mathf.Max(0.05f, m_ControllerScanInterval);
            RefreshControllerRegistry(force: false);
        }

        private void OnDisable()
        {
            UnhookNetworkManager();
            UnwireTraversalManager();
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
                nm.Unsubscribe<GC2TraversalRequestPacket>(HandleTraversalRequestServer, true);
                m_SubscribedServer = false;
            }

            if (m_SubscribedClient)
            {
                nm.Unsubscribe<GC2TraversalResponsePacket>(HandleTraversalResponseClient, false);
                nm.Unsubscribe<GC2TraversalBroadcastPacket>(HandleTraversalBroadcastClient, false);
                nm.Unsubscribe<GC2TraversalSnapshotPacket>(HandleTraversalSnapshotClient, false);
                m_SubscribedClient = false;
            }

            m_HookedManager = null;
        }

        private void HandleNetworkStarted(NetworkManager manager, bool asServer)
        {
            if (asServer && !m_SubscribedServer)
            {
                manager.Subscribe<GC2TraversalRequestPacket>(HandleTraversalRequestServer, true);
                m_SubscribedServer = true;
            }
            else if (!asServer && !m_SubscribedClient)
            {
                manager.Subscribe<GC2TraversalResponsePacket>(HandleTraversalResponseClient, false);
                manager.Subscribe<GC2TraversalBroadcastPacket>(HandleTraversalBroadcastClient, false);
                manager.Subscribe<GC2TraversalSnapshotPacket>(HandleTraversalSnapshotClient, false);
                m_SubscribedClient = true;
            }

            WireTraversalManager();
            RefreshControllerRegistry(force: true);
        }

        private void HandleNetworkShutdown(NetworkManager manager, bool asServer)
        {
            if (asServer && m_SubscribedServer)
            {
                manager.Unsubscribe<GC2TraversalRequestPacket>(HandleTraversalRequestServer, true);
                m_SubscribedServer = false;
            }
            else if (!asServer && m_SubscribedClient)
            {
                manager.Unsubscribe<GC2TraversalResponsePacket>(HandleTraversalResponseClient, false);
                manager.Unsubscribe<GC2TraversalBroadcastPacket>(HandleTraversalBroadcastClient, false);
                manager.Unsubscribe<GC2TraversalSnapshotPacket>(HandleTraversalSnapshotClient, false);
                m_SubscribedClient = false;
            }

            WireTraversalManager();
        }

        private void HandlePlayerLoadedScene(PlayerID player, SceneID scene, bool asServer)
        {
            if (!asServer) return;
            RefreshControllerRegistry(force: true);
            GetTraversalManager()?.SendAllSnapshotsToClient(player.id);
        }

        private void WireTraversalManager()
        {
            NetworkTraversalManager manager = GetTraversalManager();
            if (manager == null) return;

            manager.OnSendTraversalRequest -= SendTraversalRequestToServer;
            manager.OnSendTraversalRequest += SendTraversalRequestToServer;
            manager.OnSendTraversalResponse -= SendTraversalResponseToClient;
            manager.OnSendTraversalResponse += SendTraversalResponseToClient;
            manager.OnBroadcastTraversalChange -= BroadcastTraversalChangeToAllClients;
            manager.OnBroadcastTraversalChange += BroadcastTraversalChangeToAllClients;
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

        private void UnwireTraversalManager()
        {
            NetworkTraversalManager manager = GetTraversalManager();
            if (manager == null) return;

            manager.OnSendTraversalRequest -= SendTraversalRequestToServer;
            manager.OnSendTraversalResponse -= SendTraversalResponseToClient;
            manager.OnBroadcastTraversalChange -= BroadcastTraversalChangeToAllClients;
            manager.OnBroadcastFullSnapshot -= BroadcastSnapshotToAllClients;
            manager.OnSendSnapshotToClient -= SendSnapshotToClient;
            m_ManagerInitialized = false;
        }

        private void RefreshControllerRegistry(bool force)
        {
            NetworkTraversalManager manager = GetTraversalManager();
            if (manager == null) return;

            PruneControllerRegistry();

            if (!m_AutoRegisterSceneControllers && !force) return;

            NetworkTraversalController[] controllers = FindObjectsByType<NetworkTraversalController>(
                FindObjectsInactive.Exclude,
                FindObjectsSortMode.None);

            for (int i = 0; i < controllers.Length; i++)
            {
                RegisterController(manager, controllers[i]);
            }
        }

        private void RegisterController(NetworkTraversalManager manager, NetworkTraversalController controller)
        {
            if (manager == null || controller == null) return;

            uint networkId = controller.NetworkId;
            if (networkId == 0) return;

            NetworkManager nm = ActiveManager;
            bool isServer = nm != null && nm.isServer;
            NetworkCharacter networkCharacter = controller.GetComponent<NetworkCharacter>();
            bool isLocalClient = networkCharacter != null && networkCharacter.IsOwnerInstance;

            if (m_RegisteredControllers.TryGetValue(networkId, out NetworkTraversalController existing))
            {
                if (existing == controller)
                {
                    if (controller.IsServer != isServer || controller.IsLocalClient != isLocalClient)
                    {
                        controller.Initialize(isServer, isLocalClient);
                        manager.RegisterController(networkId, controller);
                        Log($"updated traversal controller role netId={networkId} name={controller.name} server={isServer} local={isLocalClient}");
                    }

                    return;
                }

                manager.UnregisterController(networkId);
            }

            controller.Initialize(isServer, isLocalClient);
            manager.RegisterController(networkId, controller);
            m_RegisteredControllers[networkId] = controller;
            Log($"registered traversal controller netId={networkId} name={controller.name} server={isServer} local={isLocalClient}");
        }

        private void PruneControllerRegistry()
        {
            m_RemoveBuffer.Clear();

            foreach (KeyValuePair<uint, NetworkTraversalController> pair in m_RegisteredControllers)
            {
                NetworkTraversalController controller = pair.Value;
                if (controller == null || controller.NetworkId != pair.Key)
                {
                    m_RemoveBuffer.Add(pair.Key);
                }
            }

            NetworkTraversalManager manager = GetTraversalManager();
            for (int i = 0; i < m_RemoveBuffer.Count; i++)
            {
                uint networkId = m_RemoveBuffer[i];
                manager?.UnregisterController(networkId);
                m_RegisteredControllers.Remove(networkId);
            }
        }

        private void SendTraversalRequestToServer(NetworkTraversalRequest request)
        {
            NetworkManager nm = ActiveManager;
            if (nm == null || !nm.isClient) return;

            TraceTraversal(
                $"send request to server requestId={request.RequestId} actor={request.ActorNetworkId} " +
                $"target={request.TargetNetworkId} correlation={request.CorrelationId} action={request.Action} " +
                $"traverse='{request.TraverseIdString}' isHost={nm.isServer}");
            Log($"send traversal request to server requestId={request.RequestId} actor={request.ActorNetworkId} target={request.TargetNetworkId} action={request.Action} traverse='{request.TraverseIdString}' hash={request.TraverseHash}");
            var packet = new GC2TraversalRequestPacket { request = request };
            if (nm.isServer)
            {
                TraceTraversal($"loopback request through host player={nm.localPlayer.id} ready={nm.isLocalPlayerReady}");
                Log($"loopback traversal request through local server player={nm.localPlayer.id}");
                if (nm.isLocalPlayerReady) _ = DispatchTraversalRequestOnServer(nm.localPlayer, packet);
                return;
            }

            nm.SendToServer(packet, m_Channel);
        }

        private void SendTraversalResponseToClient(uint clientNetworkId, NetworkTraversalResponse response)
        {
            NetworkManager nm = ActiveManager;
            if (nm == null || !nm.isServer) return;
            if (!TryGetPlayerId(nm, clientNetworkId, out PlayerID playerId))
            {
                TraceTraversal(
                    $"cannot send response: player not found clientNetworkId={clientNetworkId} " +
                    $"requestId={response.RequestId} actor={response.ActorNetworkId} action={response.Action}");
                Log($"cannot send traversal response: player not found clientNetworkId={clientNetworkId} requestId={response.RequestId} actor={response.ActorNetworkId} action={response.Action}");
                return;
            }

            TraceTraversal(
                $"send response to clientNetworkId={clientNetworkId} player={playerId.id} " +
                $"requestId={response.RequestId} actor={response.ActorNetworkId} correlation={response.CorrelationId} " +
                $"action={response.Action} authorized={response.Authorized} applied={response.Applied} " +
                $"rejection={response.RejectionReason} traversing={response.IsTraversing} " +
                $"traverse='{response.TraverseIdString}' error='{response.Error}'");
            Log(
                $"send traversal response to client={clientNetworkId} player={playerId.id} " +
                $"requestId={response.RequestId} actor={response.ActorNetworkId} action={response.Action} " +
                $"authorized={response.Authorized} applied={response.Applied} rejection={response.RejectionReason} " +
                $"error='{response.Error}' traverse='{response.TraverseIdString}' traversing={response.IsTraversing}");
            nm.Send(playerId, new GC2TraversalResponsePacket { response = response }, m_Channel);
        }

        private void BroadcastTraversalChangeToAllClients(NetworkTraversalBroadcast broadcast)
        {
            NetworkManager nm = ActiveManager;
            if (nm == null || !nm.isServer) return;

            TraceTraversal(
                $"broadcast change to all networkId={broadcast.NetworkId} actor={broadcast.ActorNetworkId} " +
                $"correlation={broadcast.CorrelationId} action={broadcast.Action} " +
                $"traverse='{broadcast.TraverseIdString}' hash={broadcast.TraverseHash} " +
                $"traversing={broadcast.IsTraversing} serverTime={broadcast.ServerTime:F3}");
            Log(
                $"broadcast traversal change networkId={broadcast.NetworkId} actor={broadcast.ActorNetworkId} " +
                $"correlation={broadcast.CorrelationId} action={broadcast.Action} traverse='{broadcast.TraverseIdString}' " +
                $"hash={broadcast.TraverseHash} traversing={broadcast.IsTraversing} serverTime={broadcast.ServerTime:F3}");
            nm.SendToAll(new GC2TraversalBroadcastPacket { broadcast = broadcast }, m_Channel);
        }

        private void BroadcastSnapshotToAllClients(NetworkTraversalSnapshot snapshot)
        {
            NetworkManager nm = ActiveManager;
            if (nm == null || !nm.isServer) return;

            TraceTraversal(
                $"broadcast snapshot to all networkId={snapshot.NetworkId} " +
                $"traverse='{snapshot.TraverseIdString}' hash={snapshot.TraverseHash} " +
                $"traversing={snapshot.IsTraversing} serverTime={snapshot.ServerTime:F3}");
            Log(
                $"broadcast traversal snapshot networkId={snapshot.NetworkId} traverse='{snapshot.TraverseIdString}' " +
                $"hash={snapshot.TraverseHash} traversing={snapshot.IsTraversing} serverTime={snapshot.ServerTime:F3}");
            nm.SendToAll(new GC2TraversalSnapshotPacket { snapshot = snapshot }, m_Channel);
        }

        private void SendSnapshotToClient(ulong clientId, NetworkTraversalSnapshot snapshot)
        {
            NetworkManager nm = ActiveManager;
            if (nm == null || !nm.isServer) return;
            if (!TryGetPlayerId(nm, clientId, out PlayerID playerId))
            {
                TraceTraversal($"cannot send snapshot: player not found rawClientId={clientId} networkId={snapshot.NetworkId}");
                Log($"cannot send traversal snapshot: player not found rawClientId={clientId} networkId={snapshot.NetworkId}");
                return;
            }

            TraceTraversal(
                $"send snapshot to client={clientId} player={playerId.id} " +
                $"networkId={snapshot.NetworkId} traverse='{snapshot.TraverseIdString}' " +
                $"hash={snapshot.TraverseHash} traversing={snapshot.IsTraversing} " +
                $"serverTime={snapshot.ServerTime:F3}");
            Log(
                $"send traversal snapshot to client={clientId} player={playerId.id} " +
                $"networkId={snapshot.NetworkId} traverse='{snapshot.TraverseIdString}' " +
                $"hash={snapshot.TraverseHash} traversing={snapshot.IsTraversing} serverTime={snapshot.ServerTime:F3}");
            nm.Send(playerId, new GC2TraversalSnapshotPacket { snapshot = snapshot }, m_Channel);
        }

        private void HandleTraversalRequestServer(PlayerID senderPlayer, GC2TraversalRequestPacket data, bool asServer)
        {
            if (!asServer) return;
            TraceTraversal(
                $"server received request player={senderPlayer.id} requestId={data.request.RequestId} " +
                $"actor={data.request.ActorNetworkId} target={data.request.TargetNetworkId} " +
                $"correlation={data.request.CorrelationId} action={data.request.Action} " +
                $"traverse='{data.request.TraverseIdString}'");
            Log(
                $"server received traversal request sender={senderPlayer.id} requestId={data.request.RequestId} " +
                $"actor={data.request.ActorNetworkId} target={data.request.TargetNetworkId} " +
                $"correlation={data.request.CorrelationId} action={data.request.Action} " +
                $"traverse='{data.request.TraverseIdString}' hash={data.request.TraverseHash}");
            RefreshControllerRegistry(force: true);
            _ = DispatchTraversalRequestOnServer(senderPlayer, data);
        }

        private System.Threading.Tasks.Task DispatchTraversalRequestOnServer(PlayerID senderPlayer, GC2TraversalRequestPacket data)
        {
            return GetTraversalManager()?.ReceiveTraversalRequest(data.request, senderPlayer.id) ??
                   System.Threading.Tasks.Task.CompletedTask;
        }

        private void HandleTraversalResponseClient(PlayerID senderPlayer, GC2TraversalResponsePacket data, bool asServer)
        {
            if (asServer) return;
            TraceTraversal(
                $"client received response sender={senderPlayer.id} requestId={data.response.RequestId} " +
                $"actor={data.response.ActorNetworkId} correlation={data.response.CorrelationId} action={data.response.Action} " +
                $"authorized={data.response.Authorized} applied={data.response.Applied} " +
                $"rejection={data.response.RejectionReason} traversing={data.response.IsTraversing} " +
                $"traverse='{data.response.TraverseIdString}' error='{data.response.Error}'");
            Log(
                $"client received traversal response sender={senderPlayer.id} requestId={data.response.RequestId} " +
                $"actor={data.response.ActorNetworkId} action={data.response.Action} " +
                $"authorized={data.response.Authorized} applied={data.response.Applied} " +
                $"rejection={data.response.RejectionReason} error='{data.response.Error}' " +
                $"traverse='{data.response.TraverseIdString}' traversing={data.response.IsTraversing}");
            GetTraversalManager()?.ReceiveTraversalResponse(data.response, data.response.ActorNetworkId);
        }

        private void HandleTraversalBroadcastClient(PlayerID senderPlayer, GC2TraversalBroadcastPacket data, bool asServer)
        {
            if (asServer) return;
            TraceTraversal(
                $"client received broadcast sender={senderPlayer.id} networkId={data.broadcast.NetworkId} " +
                $"actor={data.broadcast.ActorNetworkId} correlation={data.broadcast.CorrelationId} " +
                $"action={data.broadcast.Action} traverse='{data.broadcast.TraverseIdString}' " +
                $"hash={data.broadcast.TraverseHash} traversing={data.broadcast.IsTraversing} " +
                $"serverTime={data.broadcast.ServerTime:F3}");
            Log(
                $"client received traversal broadcast sender={senderPlayer.id} networkId={data.broadcast.NetworkId} " +
                $"actor={data.broadcast.ActorNetworkId} correlation={data.broadcast.CorrelationId} " +
                $"action={data.broadcast.Action} traverse='{data.broadcast.TraverseIdString}' " +
                $"hash={data.broadcast.TraverseHash} traversing={data.broadcast.IsTraversing} " +
                $"serverTime={data.broadcast.ServerTime:F3}");
            RefreshControllerRegistry(force: true);
            GetTraversalManager()?.ReceiveTraversalChangeBroadcast(data.broadcast);
        }

        private void HandleTraversalSnapshotClient(PlayerID senderPlayer, GC2TraversalSnapshotPacket data, bool asServer)
        {
            if (asServer) return;
            TraceTraversal(
                $"client received snapshot sender={senderPlayer.id} networkId={data.snapshot.NetworkId} " +
                $"traverse='{data.snapshot.TraverseIdString}' hash={data.snapshot.TraverseHash} " +
                $"traversing={data.snapshot.IsTraversing} serverTime={data.snapshot.ServerTime:F3}");
            Log(
                $"client received traversal snapshot sender={senderPlayer.id} networkId={data.snapshot.NetworkId} " +
                $"traverse='{data.snapshot.TraverseIdString}' hash={data.snapshot.TraverseHash} " +
                $"traversing={data.snapshot.IsTraversing} serverTime={data.snapshot.ServerTime:F3}");
            RefreshControllerRegistry(force: true);
            GetTraversalManager()?.ReceiveFullSnapshot(data.snapshot);
        }

        private void Log(string message)
        {
            if (!m_LogNetworkMessages) return;
            Debug.Log($"[PurrNetTraversalTransportBridge] {message}", this);
        }

        private void TraceTraversal(string message)
        {
            NetworkManager nm = ActiveManager;
            bool isServer = nm != null && nm.isServer;
            bool isClient = nm != null && nm.isClient;
            Debug.Log($"[TraversalTrace][PurrNetBridge] server={isServer} client={isClient} {message}", this);
        }

        private static NetworkTraversalManager GetTraversalManager()
        {
            return NetworkTraversalManager.Instance != null
                ? NetworkTraversalManager.Instance
                : FindFirstObjectByType<NetworkTraversalManager>();
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
