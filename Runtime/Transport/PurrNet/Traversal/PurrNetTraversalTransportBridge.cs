#if GC2_TRAVERSAL
using System;
using System.Collections.Generic;
using Arawn.GameCreator2.Networking;
using Arawn.GameCreator2.Networking.Traversal;
using GameCreator.Runtime.Characters;
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
        private readonly Dictionary<string, float> m_DiagnosticTimes = new();

        private NetworkManager m_HookedManager;
        private bool m_SubscribedServer;
        private bool m_SubscribedClient;
        private bool m_ManagerInitialized;
        private bool m_LastServer;
        private float m_NextControllerScanTime;
        private NetworkTraversalManager m_WiredTraversalManager;

        private NetworkManager ActiveManager => m_NetworkManager ? m_NetworkManager : NetworkManager.main;
        private bool DiagnosticsEnabled =>
            m_LogNetworkMessages ||
            (GetTraversalManager() != null && GetTraversalManager().DiagnosticsEnabled);

        private void Awake()
        {
            if (m_NetworkManager == null) m_NetworkManager = NetworkManager.main;
            m_Channel = Channel.ReliableOrdered;
        }

        private void OnValidate()
        {
            m_Channel = Channel.ReliableOrdered;
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
                // Network-start events normally establish these subscriptions. Retain a
                // fail-safe for a bridge enabled after the event, but never replay the full
                // start path every Update once each side is already subscribed.
                if (nm.isServer && !m_SubscribedServer) HandleNetworkStarted(nm, true);
                if (nm.isClient && !m_SubscribedClient) HandleNetworkStarted(nm, false);
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
            bool subscriptionAdded = false;
            if (asServer && !m_SubscribedServer)
            {
                manager.Subscribe<GC2TraversalRequestPacket>(HandleTraversalRequestServer, true);
                m_SubscribedServer = true;
                subscriptionAdded = true;
            }
            else if (!asServer && !m_SubscribedClient)
            {
                manager.Subscribe<GC2TraversalResponsePacket>(HandleTraversalResponseClient, false);
                manager.Subscribe<GC2TraversalBroadcastPacket>(HandleTraversalBroadcastClient, false);
                manager.Subscribe<GC2TraversalSnapshotPacket>(HandleTraversalSnapshotClient, false);
                m_SubscribedClient = true;
                subscriptionAdded = true;
            }

            if (!subscriptionAdded) return;

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
            if (manager == null)
            {
                UnwireTraversalManager();
                return;
            }

            if (!ReferenceEquals(m_WiredTraversalManager, manager))
            {
                UnwireTraversalManager();

                manager.OnSendTraversalRequest += SendTraversalRequestToServer;
                manager.OnSendTraversalResponse += SendTraversalResponseToClient;
                manager.OnBroadcastTraversalChange += BroadcastTraversalChangeToAllClients;
                manager.OnBroadcastFullSnapshot += BroadcastSnapshotToAllClients;
                manager.OnSendSnapshotToClient += SendSnapshotToClient;
                manager.OnResolveRequestRouteStatusForActor += ResolveRequestRouteStatus;
                m_WiredTraversalManager = manager;
                m_ManagerInitialized = false;
            }

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
            NetworkTraversalManager manager = m_WiredTraversalManager;
            if (manager == null)
            {
                m_WiredTraversalManager = null;
                m_ManagerInitialized = false;
                return;
            }

            manager.OnSendTraversalRequest -= SendTraversalRequestToServer;
            manager.OnSendTraversalResponse -= SendTraversalResponseToClient;
            manager.OnBroadcastTraversalChange -= BroadcastTraversalChangeToAllClients;
            manager.OnBroadcastFullSnapshot -= BroadcastSnapshotToAllClients;
            manager.OnSendSnapshotToClient -= SendSnapshotToClient;
            manager.OnResolveRequestRouteStatusForActor -= ResolveRequestRouteStatus;
            m_WiredTraversalManager = null;
            m_ManagerInitialized = false;
        }

        private TraversalRouteStatus ResolveRequestRouteStatus(uint actorNetworkId)
        {
            NetworkTraversalManager manager = GetTraversalManager();
            if (manager == null) return TraversalRouteStatus.ManagerUnavailable;
            if (!manager.IsPatchModeActive) return TraversalRouteStatus.PatchRequired;

            NetworkManager nm = ActiveManager;
            if (nm == null) return TraversalRouteStatus.TransportUnavailable;
            if (!nm.isClient) return TraversalRouteStatus.ClientNotRunning;
            if (!nm.isLocalPlayerReady) return TraversalRouteStatus.LocalPlayerNotReady;

            // Traversal input is transient. Resolve and initialize the exact actor now rather
            // than waiting for the periodic scene scan and replaying stale input later.
            RefreshControllerRegistry(force: true);

            if (actorNetworkId == 0 ||
                !m_RegisteredControllers.TryGetValue(actorNetworkId, out NetworkTraversalController controller) ||
                controller == null ||
                controller.NetworkId != actorNetworkId)
            {
                TraceTraversal($"route actor={actorNetworkId} status={TraversalRouteStatus.ControllerNotReady} reason=exact-controller-missing");
                return TraversalRouteStatus.ControllerNotReady;
            }

            // Refresh role flags synchronously in case ownership was assigned earlier in this
            // frame after the controller's last Update/bridge scan.
            RegisterController(manager, controller);
            if (!controller.IsReadyForNetworkRouting || !controller.IsLocalClient || controller.IsRemoteClient)
            {
                TraceTraversal(
                    $"route actor={actorNetworkId} status={TraversalRouteStatus.ControllerNotReady} " +
                    $"ready={controller.IsReadyForNetworkRouting} server={controller.IsServer} " +
                    $"local={controller.IsLocalClient} remote={controller.IsRemoteClient}");
                return TraversalRouteStatus.ControllerNotReady;
            }

            NetworkCharacter networkCharacter = controller.GetComponent<NetworkCharacter>();
            if (networkCharacter == null ||
                networkCharacter.NetworkId != actorNetworkId ||
                !networkCharacter.IsOwnerInstance)
            {
                TraceTraversal(
                    $"route actor={actorNetworkId} status={TraversalRouteStatus.ControllerNotReady} " +
                    $"reason=identity-or-owner-mismatch characterId={(networkCharacter != null ? networkCharacter.NetworkId : 0)} " +
                    $"owner={(networkCharacter != null && networkCharacter.IsOwnerInstance)}");
                return TraversalRouteStatus.ControllerNotReady;
            }

            bool hasCurrentOwnerRole =
                networkCharacter.CurrentRole == NetworkCharacter.NetworkRole.LocalClient ||
                (nm.isServer && networkCharacter.CurrentRole == NetworkCharacter.NetworkRole.Server);
            if (!hasCurrentOwnerRole)
            {
                TraceTraversal(
                    $"route actor={actorNetworkId} status={TraversalRouteStatus.ControllerNotReady} " +
                    $"reason=owner-role-not-current role={networkCharacter.CurrentRole}");
                return TraversalRouteStatus.ControllerNotReady;
            }

            // Traversal support is a property of the active movement driver, not the serialized
            // backend enum. This keeps optional native backends fail-closed until their driver
            // actually implements the owner/server motion-window contracts, while allowing a
            // capable backend without adding another transport-specific allow-list here.
            IUnitDriver activeDriver = networkCharacter.ActiveDriver;
            bool hasOwnerAuthority = activeDriver is INetworkOwnerMotionAuthority;
            bool hasServerAuthority = activeDriver is INetworkServerOwnerMotionAuthority;
            bool ownerCapabilityMissing = controller.IsLocalClient &&
                                          !hasOwnerAuthority &&
                                          !(controller.IsServer && hasServerAuthority);
            bool serverCapabilityMissing = controller.IsServer &&
                                           !controller.IsLocalClient &&
                                           !hasServerAuthority;
            if (activeDriver == null || ownerCapabilityMissing || serverCapabilityMissing)
            {
                TraceTraversal(
                    $"route actor={actorNetworkId} status={TraversalRouteStatus.UnsupportedPredictionBackend} " +
                    $"reason=motion-authority-capability-missing backend={networkCharacter.PredictionBackend} " +
                    $"driver={(activeDriver != null ? activeDriver.GetType().FullName : "<missing>")} " +
                    $"ownerRequired={controller.IsLocalClient} ownerAuthority={hasOwnerAuthority} " +
                    $"serverRequired={controller.IsServer && !controller.IsLocalClient} serverAuthority={hasServerAuthority}");
                return TraversalRouteStatus.UnsupportedPredictionBackend;
            }

            TraceTraversal(
                $"route actor={actorNetworkId} status={TraversalRouteStatus.Ready} " +
                $"server={controller.IsServer} local={controller.IsLocalClient} " +
                $"backend={networkCharacter.PredictionBackend} " +
                $"driver={activeDriver.GetType().FullName} " +
                $"ownerAuthority={hasOwnerAuthority} serverAuthority={hasServerAuthority}");
            return TraversalRouteStatus.Ready;
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
            if (manager == null || controller == null || !controller.IsReadyForNetworkRouting) return;

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
                        Log($"updated traversal controller role netId={networkId} name={controller.name} server={isServer} local={isLocalClient}");
                    }

                    // Reassert only if another system explicitly removed or replaced the
                    // manager route while this bridge cache remained valid. Routine scans are
                    // otherwise side-effect free.
                    if (!ReferenceEquals(manager.GetController(networkId), controller))
                    {
                        manager.RegisterController(networkId, controller);
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
                if (controller == null ||
                    controller.NetworkId != pair.Key ||
                    !controller.IsReadyForNetworkRouting)
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
            LogFocusedTransport(
                request.ActorNetworkId,
                request.ActionIdString,
                request.TraverseIdString,
                "PurrNetTraversalRequest",
                $"direction=send request={request.RequestId} corr={request.CorrelationId} " +
                $"action={request.Action} actionId='{request.ActionIdString}' traverseHash={request.TraverseHash} " +
                $"host={nm.isServer}");

            // PurrNet's SendToServer is intentionally a no-op while executing as server.
            // A host therefore enters the same receive/validation path explicitly using its
            // authenticated local PlayerID. Responses and broadcasts remain on PurrNet.
            if (nm.isServer)
            {
                if (!nm.isLocalPlayerReady)
                {
                    WarnRateLimited(
                        "host-local-player-not-ready",
                        $"Host traversal request was not routed because PurrNet's local player is not ready. " +
                        $"actor={request.ActorNetworkId} requestId={request.RequestId}");
                    return;
                }

                RefreshControllerRegistry(force: true);
                LogFocusedTransport(
                    request.ActorNetworkId,
                    request.ActionIdString,
                    request.TraverseIdString,
                    "PurrNetTraversalRequest",
                    $"direction=host-dispatch player={nm.localPlayer.id} request={request.RequestId} " +
                    $"corr={request.CorrelationId} action={request.Action}");
                TraceTraversal(
                    $"host dispatch request player={nm.localPlayer.id} requestId={request.RequestId} " +
                    $"actor={request.ActorNetworkId} action={request.Action}");
                _ = DispatchTraversalRequestOnServer(nm.localPlayer, packet);
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
                WarnRateLimited(
                    $"response-player:{clientNetworkId}",
                    $"Cannot route traversal response for NetworkId={clientNetworkId}; no PurrNet player mapping exists.");
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
                $"version={response.StateVersion} traverse='{response.TraverseIdString}' error='{response.Error}'");
            LogFocusedTransport(
                response.ActorNetworkId,
                response.ActionIdString,
                response.TraverseIdString,
                "PurrNetTraversalResponse",
                $"direction=send player={playerId.id} request={response.RequestId} corr={response.CorrelationId} " +
                $"action={response.Action} authorized={response.Authorized} applied={response.Applied} " +
                $"rejection={response.RejectionReason} stateVersion={response.StateVersion} traversing={response.IsTraversing}");
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
                $"traversing={broadcast.IsTraversing} version={broadcast.StateVersion} " +
                $"serverTime={broadcast.ServerTime:F3}");
            LogFocusedTransport(
                broadcast.NetworkId,
                broadcast.ActionIdString,
                broadcast.TraverseIdString,
                "PurrNetTraversalBroadcast",
                $"direction=send actor={broadcast.ActorNetworkId} corr={broadcast.CorrelationId} " +
                $"action={broadcast.Action} actionId='{broadcast.ActionIdString}' stateVersion={broadcast.StateVersion} " +
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
                $"traversing={snapshot.IsTraversing} kind={snapshot.Kind} " +
                $"version={snapshot.StateVersion} serverTime={snapshot.ServerTime:F3}");
            LogFocusedTransport(
                snapshot.NetworkId,
                string.Empty,
                snapshot.TraverseIdString,
                "PurrNetTraversalSnapshot",
                $"direction=broadcast kind={snapshot.Kind} stateVersion={snapshot.StateVersion} " +
                $"traversing={snapshot.IsTraversing} hasRelative={snapshot.HasRelativePose} " +
                $"relative={NetworkTraversalClimbDiagnostics.Vector(snapshot.RelativePosition)} serverTime={snapshot.ServerTime:F3}");
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
                WarnRateLimited(
                    $"snapshot-player:{clientId}",
                    $"Cannot route traversal snapshot to client={clientId}; no PurrNet player mapping exists.");
                TraceTraversal($"cannot send snapshot: player not found rawClientId={clientId} networkId={snapshot.NetworkId}");
                Log($"cannot send traversal snapshot: player not found rawClientId={clientId} networkId={snapshot.NetworkId}");
                return;
            }

            TraceTraversal(
                $"send snapshot to client={clientId} player={playerId.id} " +
                $"networkId={snapshot.NetworkId} traverse='{snapshot.TraverseIdString}' " +
                $"hash={snapshot.TraverseHash} traversing={snapshot.IsTraversing} " +
                $"kind={snapshot.Kind} version={snapshot.StateVersion} serverTime={snapshot.ServerTime:F3}");
            Log(
                $"send traversal snapshot to client={clientId} player={playerId.id} " +
                $"networkId={snapshot.NetworkId} traverse='{snapshot.TraverseIdString}' " +
                $"hash={snapshot.TraverseHash} traversing={snapshot.IsTraversing} serverTime={snapshot.ServerTime:F3}");
            nm.Send(playerId, new GC2TraversalSnapshotPacket { snapshot = snapshot }, m_Channel);
        }

        private void HandleTraversalRequestServer(PlayerID senderPlayer, GC2TraversalRequestPacket data, bool asServer)
        {
            if (!asServer) return;
            LogFocusedTransport(
                data.request.ActorNetworkId,
                data.request.ActionIdString,
                data.request.TraverseIdString,
                "PurrNetTraversalRequest",
                $"direction=receive sender={senderPlayer.id} request={data.request.RequestId} " +
                $"corr={data.request.CorrelationId} action={data.request.Action} actionId='{data.request.ActionIdString}'");
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

        private async System.Threading.Tasks.Task DispatchTraversalRequestOnServer(
            PlayerID senderPlayer,
            GC2TraversalRequestPacket data)
        {
            float startedAt = Time.realtimeSinceStartup;
            LogFocusedTransport(
                data.request.ActorNetworkId,
                data.request.ActionIdString,
                data.request.TraverseIdString,
                "PurrNetTraversalRequest",
                $"direction=server-dispatch sender={senderPlayer.id} request={data.request.RequestId} " +
                $"corr={data.request.CorrelationId} action={data.request.Action}");

            NetworkTraversalManager traversalManager = GetTraversalManager();
            if (traversalManager == null)
            {
                LogFocusedTransport(
                    data.request.ActorNetworkId,
                    data.request.ActionIdString,
                    data.request.TraverseIdString,
                    "PurrNetTraversalRequest",
                    $"direction=server-complete outcome=no-manager sender={senderPlayer.id} " +
                    $"request={data.request.RequestId} corr={data.request.CorrelationId} " +
                    $"elapsed={Time.realtimeSinceStartup - startedAt:F3}s");
                return;
            }

            await traversalManager.ReceiveTraversalRequest(data.request, senderPlayer.id);
            LogFocusedTransport(
                data.request.ActorNetworkId,
                data.request.ActionIdString,
                data.request.TraverseIdString,
                "PurrNetTraversalRequest",
                $"direction=server-complete outcome=processed sender={senderPlayer.id} " +
                $"request={data.request.RequestId} corr={data.request.CorrelationId} " +
                $"elapsed={Time.realtimeSinceStartup - startedAt:F3}s");
        }

        private void HandleTraversalResponseClient(PlayerID senderPlayer, GC2TraversalResponsePacket data, bool asServer)
        {
            if (asServer) return;
            LogFocusedTransport(
                data.response.ActorNetworkId,
                data.response.ActionIdString,
                data.response.TraverseIdString,
                "PurrNetTraversalResponse",
                $"direction=receive sender={senderPlayer.id} request={data.response.RequestId} " +
                $"corr={data.response.CorrelationId} action={data.response.Action} authorized={data.response.Authorized} " +
                $"applied={data.response.Applied} rejection={data.response.RejectionReason} " +
                $"stateVersion={data.response.StateVersion} traversing={data.response.IsTraversing}");
            TraceTraversal(
                $"client received response sender={senderPlayer.id} requestId={data.response.RequestId} " +
                $"actor={data.response.ActorNetworkId} correlation={data.response.CorrelationId} action={data.response.Action} " +
                $"authorized={data.response.Authorized} applied={data.response.Applied} " +
                $"rejection={data.response.RejectionReason} traversing={data.response.IsTraversing} " +
                $"version={data.response.StateVersion} traverse='{data.response.TraverseIdString}' " +
                $"error='{data.response.Error}'");
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
            LogFocusedTransport(
                data.broadcast.NetworkId,
                data.broadcast.ActionIdString,
                data.broadcast.TraverseIdString,
                "PurrNetTraversalBroadcast",
                $"direction=receive sender={senderPlayer.id} actor={data.broadcast.ActorNetworkId} " +
                $"corr={data.broadcast.CorrelationId} action={data.broadcast.Action} " +
                $"actionId='{data.broadcast.ActionIdString}' stateVersion={data.broadcast.StateVersion} " +
                $"traversing={data.broadcast.IsTraversing}");
            TraceTraversal(
                $"client received broadcast sender={senderPlayer.id} networkId={data.broadcast.NetworkId} " +
                $"actor={data.broadcast.ActorNetworkId} correlation={data.broadcast.CorrelationId} " +
                $"action={data.broadcast.Action} traverse='{data.broadcast.TraverseIdString}' " +
                $"hash={data.broadcast.TraverseHash} traversing={data.broadcast.IsTraversing} " +
                $"version={data.broadcast.StateVersion} serverTime={data.broadcast.ServerTime:F3}");
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
            LogFocusedTransport(
                data.snapshot.NetworkId,
                string.Empty,
                data.snapshot.TraverseIdString,
                "PurrNetTraversalSnapshot",
                $"direction=receive sender={senderPlayer.id} kind={data.snapshot.Kind} " +
                $"stateVersion={data.snapshot.StateVersion} traversing={data.snapshot.IsTraversing} " +
                $"hasRelative={data.snapshot.HasRelativePose} " +
                $"relative={NetworkTraversalClimbDiagnostics.Vector(data.snapshot.RelativePosition)}");
            TraceTraversal(
                $"client received snapshot sender={senderPlayer.id} networkId={data.snapshot.NetworkId} " +
                $"traverse='{data.snapshot.TraverseIdString}' hash={data.snapshot.TraverseHash} " +
                $"traversing={data.snapshot.IsTraversing} kind={data.snapshot.Kind} " +
                $"version={data.snapshot.StateVersion} serverTime={data.snapshot.ServerTime:F3}");
            Log(
                $"client received traversal snapshot sender={senderPlayer.id} networkId={data.snapshot.NetworkId} " +
                $"traverse='{data.snapshot.TraverseIdString}' hash={data.snapshot.TraverseHash} " +
                $"traversing={data.snapshot.IsTraversing} serverTime={data.snapshot.ServerTime:F3}");
            RefreshControllerRegistry(force: true);
            GetTraversalManager()?.ReceiveFullSnapshot(data.snapshot);
        }

        private void Log(string message)
        {
            if (!DiagnosticsEnabled) return;
            Debug.Log($"[PurrNetTraversalTransportBridge] {message}", this);
        }

        private void TraceTraversal(string message)
        {
            if (!DiagnosticsEnabled) return;

            NetworkManager nm = ActiveManager;
            bool isServer = nm != null && nm.isServer;
            bool isClient = nm != null && nm.isClient;
            Debug.Log($"[TraversalTrace][PurrNetBridge] server={isServer} client={isClient} {message}", this);
        }

        private void LogFocusedTransport(
            uint networkId,
            string actionId,
            string traverseId,
            string stage,
            string message)
        {
            bool pullUp = ContainsPullUp(actionId) || ContainsPullUp(traverseId);
            if (pullUp)
            {
                NetworkTraversalClimbDiagnostics.SetCharacterFocus(null, networkId, true);
            }

            if (!pullUp && !NetworkTraversalClimbDiagnostics.IsFocused(networkId)) return;

            NetworkManager manager = ActiveManager;
            NetworkTraversalClimbDiagnostics.Log(
                stage,
                $"networkId={networkId} server={manager?.isServer ?? false} client={manager?.isClient ?? false} " +
                $"traverse='{traverseId}' {message}",
                this);
        }

        private static bool ContainsPullUp(string value)
        {
            return !string.IsNullOrEmpty(value) &&
                   value.IndexOf("PullUp", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void WarnRateLimited(string key, string message, float interval = 5f)
        {
            float now = Time.unscaledTime;
            if (m_DiagnosticTimes.TryGetValue(key, out float previous) && now - previous < interval)
            {
                return;
            }

            m_DiagnosticTimes[key] = now;
            Debug.LogWarning($"[PurrNetTraversalTransportBridge] {message}", this);
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
