using System.Collections.Generic;
using GameCreator.Runtime.Characters;
using PurrNet;
using PurrNet.Modules;
using PurrNet.Transports;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Transport.PurrNet
{
    [DisallowMultipleComponent]
    [AddComponentMenu("Game Creator/Network/Transport/PurrNet Variables Bridge")]
    [DefaultExecutionOrder(-338)]
    public sealed class PurrNetVariableTransportBridge : MonoBehaviour
    {
        [Header("PurrNet")]
        [Tooltip("Optional reference to a specific NetworkManager. Leave empty to use NetworkManager.main.")]
        [SerializeField] private NetworkManager m_NetworkManager;

        [Tooltip("Reliable channel used for variable requests, responses, broadcasts, and snapshots.")]
        [SerializeField] private Channel m_Channel = Channel.ReliableOrdered;

        [Header("Relevance")]
        [Tooltip("Core PurrNet transport bridge used to resolve the active NetworkSessionProfile and character positions.")]
        [SerializeField] private PurrNetTransportBridge m_CoreBridge;

        [Tooltip("When the active NetworkSessionProfile enables distance culling, local variable broadcasts are sent only to relevant observers and the owner. Global variables are still sent to all clients.")]
        [SerializeField] private bool m_UseSessionProfileRelevance = true;

        [Header("Profiles")]
        [Tooltip("Global variable profiles used by this session. Local profiles are also discovered from NetworkVariableController components.")]
        [SerializeField] private NetworkVariableProfile[] m_GlobalProfiles;

        [Header("Controllers")]
        [Tooltip("Automatically finds NetworkVariableController components in the scene.")]
        [SerializeField] private bool m_AutoRegisterSceneControllers = true;

        [Min(0.05f)]
        [SerializeField] private float m_ControllerScanInterval = 0.25f;

        [Header("Debug")]
        [SerializeField] private bool m_LogNetworkMessages;

        private readonly Dictionary<uint, NetworkVariableController> m_RegisteredControllers = new(32);
        private readonly List<uint> m_RemoveBuffer = new(16);

        private NetworkManager m_HookedManager;
        private bool m_SubscribedServer;
        private bool m_SubscribedClient;
        private bool m_ManagerInitialized;
        private bool m_LastServer;
        private float m_NextControllerScanTime;

        private NetworkManager ActiveManager => m_NetworkManager ? m_NetworkManager : NetworkManager.main;
        private PurrNetTransportBridge CoreBridge => m_CoreBridge != null
            ? m_CoreBridge
            : NetworkTransportBridge.Active as PurrNetTransportBridge;

        private void Awake()
        {
            if (m_NetworkManager == null) m_NetworkManager = NetworkManager.main;
        }

        private void OnEnable()
        {
            TryHookNetworkManager();
            WireVariableManager();
            RefreshControllerRegistry(force: true);
        }

        private void Start()
        {
            TryHookNetworkManager();
            WireVariableManager();
            RefreshControllerRegistry(force: true);
        }

        private void Update()
        {
            TryHookNetworkManager();
            WireVariableManager();

            if (!m_AutoRegisterSceneControllers) return;
            if (Time.unscaledTime < m_NextControllerScanTime) return;

            m_NextControllerScanTime = Time.unscaledTime + Mathf.Max(0.05f, m_ControllerScanInterval);
            RefreshControllerRegistry(force: false);
        }

        private void OnDisable()
        {
            UnhookNetworkManager();
            UnwireVariableManager();
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
                nm.Unsubscribe<GC2VariableRequestPacket>(HandleVariableRequestServer, true);
                m_SubscribedServer = false;
            }

            if (m_SubscribedClient)
            {
                nm.Unsubscribe<GC2VariableResponsePacket>(HandleVariableResponseClient, false);
                nm.Unsubscribe<GC2VariableBroadcastPacket>(HandleVariableBroadcastClient, false);
                nm.Unsubscribe<GC2VariableSnapshotPacket>(HandleVariableSnapshotClient, false);
                m_SubscribedClient = false;
            }

            m_HookedManager = null;
        }

        private void HandleNetworkStarted(NetworkManager manager, bool asServer)
        {
            if (asServer && !m_SubscribedServer)
            {
                manager.Subscribe<GC2VariableRequestPacket>(HandleVariableRequestServer, true);
                m_SubscribedServer = true;
            }
            else if (!asServer && !m_SubscribedClient)
            {
                manager.Subscribe<GC2VariableResponsePacket>(HandleVariableResponseClient, false);
                manager.Subscribe<GC2VariableBroadcastPacket>(HandleVariableBroadcastClient, false);
                manager.Subscribe<GC2VariableSnapshotPacket>(HandleVariableSnapshotClient, false);
                m_SubscribedClient = true;
            }

            WireVariableManager();
            RefreshControllerRegistry(force: true);
        }

        private void HandleNetworkShutdown(NetworkManager manager, bool asServer)
        {
            if (asServer && m_SubscribedServer)
            {
                manager.Unsubscribe<GC2VariableRequestPacket>(HandleVariableRequestServer, true);
                m_SubscribedServer = false;
            }
            else if (!asServer && m_SubscribedClient)
            {
                manager.Unsubscribe<GC2VariableResponsePacket>(HandleVariableResponseClient, false);
                manager.Unsubscribe<GC2VariableBroadcastPacket>(HandleVariableBroadcastClient, false);
                manager.Unsubscribe<GC2VariableSnapshotPacket>(HandleVariableSnapshotClient, false);
                m_SubscribedClient = false;
            }

            WireVariableManager();
        }

        private void HandlePlayerLoadedScene(PlayerID player, SceneID scene, bool asServer)
        {
            if (!asServer) return;
            RefreshControllerRegistry(force: true);
            GetVariableManager()?.SendInitialState(player.id);
        }

        private void WireVariableManager()
        {
            NetworkVariableManager manager = GetVariableManager();
            if (manager == null) return;

            manager.OnSendVariableRequest -= SendVariableRequestToServer;
            manager.OnSendVariableRequest += SendVariableRequestToServer;
            manager.OnSendVariableResponse -= SendVariableResponseToClient;
            manager.OnSendVariableResponse += SendVariableResponseToClient;
            manager.OnBroadcastVariableChange -= BroadcastVariableChangeToAllClients;
            manager.OnBroadcastVariableChange += BroadcastVariableChangeToAllClients;
            manager.OnBroadcastSnapshot -= BroadcastSnapshotToAllClients;
            manager.OnBroadcastSnapshot += BroadcastSnapshotToAllClients;
            manager.OnSendSnapshotToClient -= SendSnapshotToClient;
            manager.OnSendSnapshotToClient += SendSnapshotToClient;

            RegisterConfiguredProfiles(manager);

            var nm = ActiveManager;
            bool isServer = nm != null && nm.isServer;
            if (!m_ManagerInitialized || isServer != m_LastServer)
            {
                manager.IsServer = isServer;
                m_ManagerInitialized = true;
                m_LastServer = isServer;
            }
        }

        private void UnwireVariableManager()
        {
            NetworkVariableManager manager = GetVariableManager();
            if (manager == null) return;

            manager.OnSendVariableRequest -= SendVariableRequestToServer;
            manager.OnSendVariableResponse -= SendVariableResponseToClient;
            manager.OnBroadcastVariableChange -= BroadcastVariableChangeToAllClients;
            manager.OnBroadcastSnapshot -= BroadcastSnapshotToAllClients;
            manager.OnSendSnapshotToClient -= SendSnapshotToClient;
            m_ManagerInitialized = false;
        }

        private void RegisterConfiguredProfiles(NetworkVariableManager manager)
        {
            if (manager == null || m_GlobalProfiles == null) return;
            for (int i = 0; i < m_GlobalProfiles.Length; i++)
            {
                manager.RegisterGlobalProfile(m_GlobalProfiles[i]);
            }
        }

        private void RefreshControllerRegistry(bool force)
        {
            NetworkVariableManager manager = GetVariableManager();
            if (manager == null) return;

            PruneControllerRegistry(manager);

            if (!m_AutoRegisterSceneControllers && !force) return;

            var controllers = FindObjectsByType<NetworkVariableController>(
                FindObjectsInactive.Exclude,
                FindObjectsSortMode.None);

            for (int i = 0; i < controllers.Length; i++)
            {
                RegisterController(manager, controllers[i]);
            }
        }

        private void RegisterController(NetworkVariableManager manager, NetworkVariableController controller)
        {
            if (manager == null || controller == null) return;

            NetworkCharacter networkCharacter = controller.NetworkCharacter != null
                ? controller.NetworkCharacter
                : controller.GetComponent<NetworkCharacter>();

            if (!TryResolveControllerNetworkId(controller, networkCharacter, out uint networkId, out NetworkIdentity identity))
            {
                return;
            }

            if (m_RegisteredControllers.TryGetValue(networkId, out var existing))
            {
                if (existing == controller) return;
                manager.UnregisterController(networkId, existing);
                existing.ClearTransportNetworkIdentity(networkId);
            }

            ApplyTransportIdentity(controller, identity, networkId);
            m_RegisteredControllers[networkId] = controller;
            manager.RegisterController(networkId, controller);

            if (manager.IsServer)
            {
                NetworkVariableBroadcast[] snapshotChanges = controller.BuildSnapshot(Time.time);
                for (int i = 0; i < snapshotChanges.Length; i++)
                {
                    manager.OnBroadcastVariableChange?.Invoke(snapshotChanges[i]);
                }
            }
        }

        private void PruneControllerRegistry(NetworkVariableManager manager)
        {
            m_RemoveBuffer.Clear();

            foreach (var pair in m_RegisteredControllers)
            {
                var controller = pair.Value;
                NetworkCharacter networkCharacter = controller != null
                    ? controller.NetworkCharacter != null ? controller.NetworkCharacter : controller.GetComponent<NetworkCharacter>()
                    : null;

                if (controller == null ||
                    !TryResolveControllerNetworkId(controller, networkCharacter, out uint currentNetworkId, out NetworkIdentity identity) ||
                    currentNetworkId != pair.Key)
                {
                    m_RemoveBuffer.Add(pair.Key);
                    continue;
                }

                ApplyTransportIdentity(controller, identity, currentNetworkId);
            }

            for (int i = 0; i < m_RemoveBuffer.Count; i++)
            {
                uint networkId = m_RemoveBuffer[i];
                NetworkVariableController controller = m_RegisteredControllers.TryGetValue(networkId, out var existing)
                    ? existing
                    : null;

                manager.UnregisterController(networkId, controller);
                controller?.ClearTransportNetworkIdentity(networkId);
                m_RegisteredControllers.Remove(networkId);
            }
        }

        private void UnregisterAllControllers()
        {
            NetworkVariableManager manager = GetVariableManager();
            if (manager != null)
            {
                foreach (var pair in m_RegisteredControllers)
                {
                    manager.UnregisterController(pair.Key, pair.Value);
                    pair.Value?.ClearTransportNetworkIdentity(pair.Key);
                }
            }

            m_RegisteredControllers.Clear();
        }

        private bool TryResolveControllerNetworkId(
            NetworkVariableController controller,
            NetworkCharacter networkCharacter,
            out uint networkId,
            out NetworkIdentity identity)
        {
            networkId = 0;
            identity = null;
            if (controller == null) return false;

            if (networkCharacter != null)
            {
                if (networkCharacter.NetworkId == 0) return false;
                if (networkCharacter.Role == NetworkCharacter.NetworkRole.None) return false;

                networkId = networkCharacter.NetworkId;
                identity = networkCharacter.GetComponentInParent<NetworkIdentity>();
                return true;
            }

            identity = controller.GetComponentInParent<NetworkIdentity>();
            return TryResolvePurrNetNetworkId(identity, out networkId);
        }

        private void ApplyTransportIdentity(
            NetworkVariableController controller,
            NetworkIdentity identity,
            uint networkId)
        {
            if (controller == null || identity == null || networkId == 0) return;

            uint ownerClientId = NetworkTransportBridge.InvalidClientId;
            if (TryResolveIdentityOwner(identity, out PlayerID owner))
            {
                ownerClientId = PlayerIdToClientId(owner);
            }

            controller.ApplyTransportNetworkIdentity(
                networkId,
                identity.isController,
                ownerClientId);
        }

        private bool TryResolveIdentityOwner(NetworkIdentity identity, out PlayerID owner)
        {
            owner = default;
            if (identity == null) return false;

            var nm = ActiveManager;
            if (nm != null)
            {
                if (nm.isServer &&
                    nm.TryGetModule(out GlobalOwnershipModule serverOwnership, true) &&
                    serverOwnership.TryGetOwner(identity, out owner))
                {
                    return true;
                }

                if (nm.isClient &&
                    nm.TryGetModule(out GlobalOwnershipModule clientOwnership, false) &&
                    clientOwnership.TryGetOwner(identity, out owner))
                {
                    return true;
                }
            }

            if (!identity.owner.HasValue) return false;
            owner = identity.owner.Value;
            return true;
        }

        private static bool TryResolvePurrNetNetworkId(NetworkIdentity identity, out uint networkId)
        {
            networkId = 0;
            if (identity == null || !identity.isSpawned || identity.objectId >= uint.MaxValue)
            {
                return false;
            }

            networkId = (uint)(identity.objectId + 1UL);
            return networkId != 0;
        }

        private void SendVariableRequestToServer(NetworkVariableRequest request)
        {
            var nm = ActiveManager;
            if (nm == null || !nm.isClient) return;

            var packet = new GC2VariableRequestPacket { request = request };
            if (nm.isServer)
            {
                if (nm.isLocalPlayerReady) DispatchVariableRequestOnServer(nm.localPlayer, packet);
                return;
            }

            Log($"SendToServer variable request actor={request.ActorNetworkId} scope={request.Scope} op={request.Operation}");
            nm.SendToServer(packet, m_Channel);
        }

        private void SendVariableResponseToClient(uint clientNetworkId, NetworkVariableResponse response)
        {
            var nm = ActiveManager;
            if (nm == null || !nm.isServer) return;
            if (!TryGetPlayerId(nm, clientNetworkId, out var playerId)) return;

            nm.Send(playerId, new GC2VariableResponsePacket { response = response }, m_Channel);
        }

        private void BroadcastVariableChangeToAllClients(NetworkVariableBroadcast broadcast)
        {
            var nm = ActiveManager;
            if (nm == null || !nm.isServer) return;

            var packet = new GC2VariableBroadcastPacket { broadcast = broadcast };
            if (!ShouldFilterBySessionProfile(broadcast))
            {
                nm.SendToAll(packet, m_Channel);
                return;
            }

            IReadOnlyList<PlayerID> players = nm.players;
            if (players == null || players.Count == 0) return;

            for (int i = 0; i < players.Count; i++)
            {
                PlayerID playerId = players[i];
                uint clientId = PlayerIdToClientId(playerId);
                if (!NetworkTransportBridge.IsValidClientId(clientId)) continue;
                if (!ShouldSendLocalVariableToClient(clientId, broadcast.TargetNetworkId)) continue;

                nm.Send(playerId, packet, m_Channel);
            }
        }

        private void BroadcastSnapshotToAllClients(NetworkVariableSnapshot snapshot)
        {
            var nm = ActiveManager;
            if (nm == null || !nm.isServer) return;

            nm.SendToAll(new GC2VariableSnapshotPacket { snapshot = snapshot }, m_Channel);
        }

        private void SendSnapshotToClient(ulong clientId, NetworkVariableSnapshot snapshot)
        {
            var nm = ActiveManager;
            if (nm == null || !nm.isServer) return;
            if (!TryGetPlayerId(nm, clientId, out var playerId)) return;

            nm.Send(playerId, new GC2VariableSnapshotPacket { snapshot = snapshot }, m_Channel);
        }

        private void HandleVariableRequestServer(PlayerID senderPlayer, GC2VariableRequestPacket data, bool asServer)
        {
            if (!asServer) return;
            RefreshControllerRegistry(force: true);
            DispatchVariableRequestOnServer(senderPlayer, data);
        }

        private void DispatchVariableRequestOnServer(PlayerID senderPlayer, GC2VariableRequestPacket data)
        {
            GetVariableManager()?.ReceiveVariableRequest(data.request, PlayerIdToClientId(senderPlayer));
        }

        private void HandleVariableResponseClient(PlayerID senderPlayer, GC2VariableResponsePacket data, bool asServer)
        {
            if (asServer) return;
            GetVariableManager()?.ReceiveVariableResponse(data.response);
        }

        private void HandleVariableBroadcastClient(PlayerID senderPlayer, GC2VariableBroadcastPacket data, bool asServer)
        {
            if (asServer) return;
            RefreshControllerRegistry(force: true);
            GetVariableManager()?.ReceiveVariableBroadcast(data.broadcast);
        }

        private void HandleVariableSnapshotClient(PlayerID senderPlayer, GC2VariableSnapshotPacket data, bool asServer)
        {
            if (asServer) return;
            RefreshControllerRegistry(force: true);
            GetVariableManager()?.ReceiveVariableSnapshot(data.snapshot);
        }

        private bool ShouldFilterBySessionProfile(NetworkVariableBroadcast broadcast)
        {
            if (!m_UseSessionProfileRelevance) return false;
            if (broadcast.Scope == NetworkVariableScope.GlobalName ||
                broadcast.Scope == NetworkVariableScope.GlobalList)
            {
                return false;
            }

            NetworkSessionProfile profile = CoreBridge != null ? CoreBridge.GlobalSessionProfile : null;
            return profile != null &&
                   (profile.enableDistanceCulling || profile.requireObserverCharacterForRelevance);
        }

        private bool ShouldSendLocalVariableToClient(uint targetClientId, uint targetNetworkId)
        {
            PurrNetTransportBridge bridge = CoreBridge;
            NetworkSessionProfile profile = bridge != null ? bridge.GlobalSessionProfile : null;
            if (profile == null) return true;

            if (bridge != null &&
                bridge.TryGetCharacterOwner(targetNetworkId, out uint ownerClientId) &&
                ownerClientId == targetClientId)
            {
                return true;
            }

            if (!TryGetVariableTargetPosition(targetNetworkId, out Vector3 targetPosition))
            {
                return !profile.requireObserverCharacterForRelevance;
            }

            if (!TryGetObserverPosition(targetClientId, out Vector3 observerPosition))
            {
                return !profile.requireObserverCharacterForRelevance;
            }

            float distance = Vector3.Distance(observerPosition, targetPosition);
            return !profile.enableDistanceCulling || distance <= profile.cullDistance;
        }

        private bool TryGetVariableTargetPosition(uint targetNetworkId, out Vector3 position)
        {
            position = Vector3.zero;

            PurrNetTransportBridge bridge = CoreBridge;
            Character character = bridge != null ? bridge.ResolveCharacter(targetNetworkId) : null;
            if (character != null)
            {
                position = character.transform.position;
                return true;
            }

            if (m_RegisteredControllers.TryGetValue(targetNetworkId, out NetworkVariableController controller) &&
                controller != null)
            {
                position = controller.transform.position;
                return true;
            }

            return false;
        }

        private bool TryGetObserverPosition(uint clientId, out Vector3 position)
        {
            position = Vector3.zero;

            PurrNetTransportBridge bridge = CoreBridge;
            if (bridge == null) return false;
            if (!bridge.TryGetRepresentativeCharacterId(clientId, out uint observerCharacterId)) return false;

            Character observer = bridge.ResolveCharacter(observerCharacterId);
            if (observer == null) return false;

            position = observer.transform.position;
            return true;
        }

        private void Log(string message)
        {
            if (!m_LogNetworkMessages) return;
            Debug.Log($"[PurrNetVariableTransportBridge] {message}", this);
        }

        private static NetworkVariableManager GetVariableManager()
        {
            return NetworkVariableManager.Instance != null
                ? NetworkVariableManager.Instance
                : FindFirstObjectByType<NetworkVariableManager>();
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
