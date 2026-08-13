using System.Collections.Generic;
using GameCreator.Runtime.Characters;
using PurrNet;
using PurrNet.Modules;
using PurrNet.Transports;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Transport.PurrNet
{
    /// <summary>
    /// PurrNet adapter for transport-neutral, authority-validated Network Actions.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Game Creator/Network/Transport/PurrNet Network Actions Bridge")]
    [DefaultExecutionOrder(-337)]
    public sealed class PurrNetNetworkActionTransportBridge : MonoBehaviour
    {
        [Header("PurrNet")]
        [SerializeField] private NetworkManager m_NetworkManager;
        [SerializeField] private Channel m_ReliableChannel = Channel.ReliableOrdered;
        [SerializeField] private Channel m_UnreliableChannel = Channel.UnreliableSequenced;

        [Header("Relevance")]
        [SerializeField] private PurrNetTransportBridge m_CoreBridge;
        [SerializeField] private bool m_UseSessionProfileRelevance = true;

        [Header("Endpoints")]
        [SerializeField] private bool m_AutoRegisterSceneEndpoints = true;
        [Min(0.05f)]
        [SerializeField] private float m_EndpointScanInterval = 0.25f;

        [Header("Debug")]
        [SerializeField] private bool m_LogNetworkMessages;

        private readonly Dictionary<NetworkActionEndpointKey, NetworkActionEndpoint> m_Registered =
            new(32);
        private readonly List<NetworkActionEndpointKey> m_RemoveBuffer = new(16);
        private readonly NetworkActionRelevanceDeliveryCache m_RelevanceDeliveries = new();
        private NetworkManager m_HookedManager;
        private HierarchyFactory m_ServerHierarchyFactory;
        private HierarchyFactory m_ClientHierarchyFactory;
        private NetworkActionManager m_WiredManager;
        private bool m_SubscribedServer;
        private bool m_SubscribedClient;
        private bool m_ManagerInitialized;
        private bool m_LastServer;
        private float m_NextEndpointScanTime;
        private float m_NextRelevanceSnapshotTime;

        private NetworkManager ActiveManager =>
            m_NetworkManager ? m_NetworkManager : NetworkManager.main;
        private PurrNetTransportBridge CoreBridge => m_CoreBridge != null
            ? m_CoreBridge
            : NetworkTransportBridge.Active as PurrNetTransportBridge;

        private void Awake()
        {
            if (m_NetworkManager == null) m_NetworkManager = NetworkManager.main;
        }

        private void OnEnable()
        {
            NetworkLifecycleEvents.LogicalAuthorityChanged += HandleLogicalAuthorityChanged;
            NetworkLifecycleEvents.SessionStopped += HandleSessionStopped;
            TryHookNetworkManager();
            WireManager();
            RefreshEndpointRegistry(true);
        }

        private void Start()
        {
            TryHookNetworkManager();
            WireManager();
            RefreshEndpointRegistry(true);
        }

        private void Update()
        {
            TryHookNetworkManager();
            WireManager();
            NetworkManager active = ActiveManager;
            if (active != null && active.isServer &&
                UsesDynamicRelevance() &&
                Time.unscaledTime >= m_NextRelevanceSnapshotTime)
            {
                m_NextRelevanceSnapshotTime = Time.unscaledTime + 1f;
                ProcessRelevanceCatchUp();
            }
            if (!m_AutoRegisterSceneEndpoints ||
                Time.unscaledTime < m_NextEndpointScanTime) return;
            m_NextEndpointScanTime = Time.unscaledTime +
                                     Mathf.Max(0.05f, m_EndpointScanInterval);
            RefreshEndpointRegistry(false);
        }

        private void OnDisable()
        {
            NetworkLifecycleEvents.LogicalAuthorityChanged -= HandleLogicalAuthorityChanged;
            NetworkLifecycleEvents.SessionStopped -= HandleSessionStopped;
            m_WiredManager?.ResetSessionState();
            m_RelevanceDeliveries.Clear();
            UnhookNetworkManager();
            UnwireManager();
            UnregisterAllEndpoints();
        }

        private void TryHookNetworkManager()
        {
            NetworkManager manager = ActiveManager;
            if (manager == null) return;
            if (m_HookedManager != null && m_HookedManager != manager) UnhookNetworkManager();
            if (m_HookedManager == manager)
            {
                RefreshHierarchyObservation(manager);
                if (manager.isServer) HandleNetworkStarted(manager, true);
                if (manager.isClient) HandleNetworkStarted(manager, false);
                return;
            }

            m_HookedManager = manager;
            RefreshHierarchyObservation(manager);
            manager.onNetworkStarted -= HandleNetworkStarted;
            manager.onNetworkStarted += HandleNetworkStarted;
            manager.onNetworkShutdown -= HandleNetworkShutdown;
            manager.onNetworkShutdown += HandleNetworkShutdown;
            manager.onPlayerLoadedScene -= HandlePlayerLoadedScene;
            manager.onPlayerLoadedScene += HandlePlayerLoadedScene;
            manager.onPlayerLeft -= HandlePlayerLeft;
            manager.onPlayerLeft += HandlePlayerLeft;
            if (manager.isServer) HandleNetworkStarted(manager, true);
            if (manager.isClient) HandleNetworkStarted(manager, false);
        }

        private void UnhookNetworkManager()
        {
            NetworkManager manager = m_HookedManager;
            StopHierarchyObservation();
            if (manager == null)
            {
                m_HookedManager = null;
                return;
            }
            manager.onNetworkStarted -= HandleNetworkStarted;
            manager.onNetworkShutdown -= HandleNetworkShutdown;
            manager.onPlayerLoadedScene -= HandlePlayerLoadedScene;
            manager.onPlayerLeft -= HandlePlayerLeft;
            if (m_SubscribedServer)
            {
                manager.Unsubscribe<GC2NetworkActionRequestPacket>(HandleRequestServer, true);
                m_SubscribedServer = false;
            }
            if (m_SubscribedClient)
            {
                manager.Unsubscribe<GC2NetworkActionResponsePacket>(HandleResponseClient, false);
                manager.Unsubscribe<GC2NetworkActionBroadcastPacket>(HandleBroadcastClient, false);
                manager.Unsubscribe<GC2NetworkActionSnapshotPacket>(HandleSnapshotClient, false);
                m_SubscribedClient = false;
            }
            m_HookedManager = null;
        }

        private void HandleNetworkStarted(NetworkManager manager, bool asServer)
        {
            if (asServer && !m_SubscribedServer)
            {
                manager.Subscribe<GC2NetworkActionRequestPacket>(HandleRequestServer, true);
                m_SubscribedServer = true;
            }
            else if (!asServer && !m_SubscribedClient)
            {
                manager.Subscribe<GC2NetworkActionResponsePacket>(HandleResponseClient, false);
                manager.Subscribe<GC2NetworkActionBroadcastPacket>(HandleBroadcastClient, false);
                manager.Subscribe<GC2NetworkActionSnapshotPacket>(HandleSnapshotClient, false);
                m_SubscribedClient = true;
            }
            RefreshHierarchyObservation(manager);
            WireManager();
            RefreshEndpointRegistry(true);
        }

        private void HandleNetworkShutdown(NetworkManager manager, bool asServer)
        {
            if (asServer && m_SubscribedServer)
            {
                manager.Unsubscribe<GC2NetworkActionRequestPacket>(HandleRequestServer, true);
                m_SubscribedServer = false;
            }
            else if (!asServer && m_SubscribedClient)
            {
                manager.Unsubscribe<GC2NetworkActionResponsePacket>(HandleResponseClient, false);
                manager.Unsubscribe<GC2NetworkActionBroadcastPacket>(HandleBroadcastClient, false);
                manager.Unsubscribe<GC2NetworkActionSnapshotPacket>(HandleSnapshotClient, false);
                m_SubscribedClient = false;
            }
            if (!manager.isServer && !manager.isClient)
            {
                m_WiredManager?.ResetSessionState();
                m_RelevanceDeliveries.Clear();
            }
            WireManager();
        }

        private void HandlePlayerLoadedScene(PlayerID player, SceneID scene, bool asServer)
        {
            if (!asServer) return;
            RefreshEndpointRegistry(true);
            GetManager()?.SendInitialState(PlayerIdToClientId(player));
        }

        private void HandlePlayerLeft(PlayerID player, bool asServer)
        {
            if (asServer) m_RelevanceDeliveries.RemoveClient(PlayerIdToClientId(player));
        }

        private void WireManager()
        {
            NetworkActionManager manager = GetManager();
            if (manager == null) return;
            if (m_WiredManager != null && m_WiredManager != manager) UnwireManager();
            m_WiredManager = manager;
            manager.OnSendActionRequest -= SendRequest;
            manager.OnSendActionRequest += SendRequest;
            manager.OnSendActionResponse -= SendResponse;
            manager.OnSendActionResponse += SendResponse;
            manager.OnBroadcastAction -= BroadcastAction;
            manager.OnBroadcastAction += BroadcastAction;
            manager.OnSendActionToClient -= SendAction;
            manager.OnSendActionToClient += SendAction;
            manager.OnSendSnapshotToClient -= SendSnapshot;
            manager.OnSendSnapshotToClient += SendSnapshot;
            manager.OnBroadcastSnapshot -= BroadcastSnapshot;
            manager.OnBroadcastSnapshot += BroadcastSnapshot;

            NetworkManager nm = ActiveManager;
            bool isServer = nm != null && nm.isServer;
            if (!m_ManagerInitialized || isServer != m_LastServer)
            {
                manager.IsServer = isServer;
                if (NetworkLifecycleEvents.LastSource == CoreBridge &&
                    NetworkLifecycleEvents.LastAuthorityEpoch != 0)
                    manager.SetAuthorityEpoch(NetworkLifecycleEvents.LastAuthorityEpoch);
                m_ManagerInitialized = true;
                m_LastServer = isServer;
            }
        }

        private void HandleLogicalAuthorityChanged(
            NetworkTransportBridge source,
            bool isAuthority,
            uint epoch)
        {
            if (source == null || source != CoreBridge) return;
            WireManager();
            if (m_WiredManager == null) return;
            m_WiredManager.IsServer = isAuthority;
            m_WiredManager.SetAuthorityEpoch(epoch);
            m_ManagerInitialized = true;
            m_LastServer = isAuthority;
            RefreshEndpointRegistry(true);
        }

        private void HandleSessionStopped(NetworkTransportBridge source)
        {
            if (source != null && source == CoreBridge)
            {
                m_WiredManager?.ResetSessionState();
                m_RelevanceDeliveries.Clear();
            }
        }

        private void UnwireManager()
        {
            NetworkActionManager manager = m_WiredManager;
            if (manager == null) return;
            manager.OnSendActionRequest -= SendRequest;
            manager.OnSendActionResponse -= SendResponse;
            manager.OnBroadcastAction -= BroadcastAction;
            manager.OnSendActionToClient -= SendAction;
            manager.OnSendSnapshotToClient -= SendSnapshot;
            manager.OnBroadcastSnapshot -= BroadcastSnapshot;
            m_WiredManager = null;
            m_ManagerInitialized = false;
        }

        private void RefreshEndpointRegistry(bool force)
        {
            NetworkActionManager manager = GetManager();
            if (manager == null) return;
            PruneEndpointRegistry(manager);
            if (!m_AutoRegisterSceneEndpoints && !force) return;
            NetworkActionEndpoint[] endpoints = FindObjectsByType<NetworkActionEndpoint>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            for (int i = 0; i < endpoints.Length; i++) RegisterEndpoint(manager, endpoints[i]);
        }

        private void RegisterEndpoint(NetworkActionManager manager, NetworkActionEndpoint endpoint)
        {
            if (manager == null || endpoint == null) return;
            NetworkIdentity identity = endpoint.GetComponentInParent<NetworkIdentity>();
            if (!TryResolveNetworkId(identity, out uint networkId)) return;
            uint ownerId = NetworkTransportBridge.InvalidClientId;
            if (TryResolveIdentityOwner(identity, out PlayerID owner))
                ownerId = PlayerIdToClientId(owner);

            NetworkActionEndpointKey key = new(networkId, endpoint.EndpointHash);
            if (!key.IsValid) return;
            if (m_Registered.TryGetValue(key, out var existing) && existing != endpoint)
            {
                Debug.LogError(
                    $"[PurrNetNetworkActionTransportBridge] Endpoint ID/hash collision under " +
                    $"Network ID {networkId}: '{existing?.name ?? "(destroyed)"}' and " +
                    $"'{endpoint.name}' resolve to {endpoint.EndpointHash}. The later endpoint " +
                    "was ignored. Give sibling endpoints unique stable Endpoint IDs.",
                    endpoint);
                return;
            }
            endpoint.ApplyTransportNetworkIdentity(
                networkId, identity.isController, ownerId, false);
            m_Registered[key] = endpoint;
            manager.RegisterEndpoint(networkId, endpoint);
        }

        private void PruneEndpointRegistry(NetworkActionManager manager)
        {
            m_RemoveBuffer.Clear();
            foreach (var pair in m_Registered)
            {
                NetworkActionEndpoint endpoint = pair.Value;
                NetworkIdentity identity = endpoint != null
                    ? endpoint.GetComponentInParent<NetworkIdentity>()
                    : null;
                if (endpoint == null || !TryResolveNetworkId(identity, out uint current) ||
                    current != pair.Key.NetworkId ||
                    endpoint.EndpointHash != pair.Key.EndpointHash)
                    m_RemoveBuffer.Add(pair.Key);
            }
            for (int i = 0; i < m_RemoveBuffer.Count; i++)
            {
                NetworkActionEndpointKey key = m_RemoveBuffer[i];
                if (m_Registered.TryGetValue(key, out var endpoint))
                {
                    bool terminal = endpoint == null || endpoint.EndpointHash != key.EndpointHash;
                    if (terminal)
                    {
                        manager.UnregisterEndpoint(key, true);
                        m_RelevanceDeliveries.RemoveEndpoint(key);
                    }
                    else
                    {
                        // A scan can race a temporary isSpawned transition. Confirmed hierarchy
                        // removal is handled by HandleIdentityRemoved below.
                        manager.UnregisterEndpoint(key.NetworkId, endpoint);
                    }
                    if (endpoint != null)
                        endpoint.ClearTransportNetworkIdentity(key.NetworkId);
                }
                m_Registered.Remove(key);
            }
        }

        private void UnregisterAllEndpoints()
        {
            NetworkActionManager manager = GetManager();
            foreach (var pair in m_Registered)
            {
                manager?.UnregisterEndpoint(pair.Key, false);
                if (pair.Value != null)
                    pair.Value.ClearTransportNetworkIdentity(pair.Key.NetworkId);
            }
            m_Registered.Clear();
        }

        private bool TryResolveIdentityOwner(NetworkIdentity identity, out PlayerID owner)
        {
            owner = default;
            if (identity == null) return false;
            NetworkManager nm = ActiveManager;
            if (nm != null)
            {
                if (nm.isServer &&
                    nm.TryGetModule(out GlobalOwnershipModule serverOwnership, true) &&
                    serverOwnership.TryGetOwner(identity, out owner)) return true;
                if (nm.isClient &&
                    nm.TryGetModule(out GlobalOwnershipModule clientOwnership, false) &&
                    clientOwnership.TryGetOwner(identity, out owner)) return true;
            }
            if (!identity.owner.HasValue) return false;
            owner = identity.owner.Value;
            return true;
        }

        private static bool TryResolveNetworkId(NetworkIdentity identity, out uint networkId)
        {
            networkId = 0;
            if (identity == null || !identity.isSpawned || identity.objectId >= uint.MaxValue)
                return false;
            networkId = (uint)(identity.objectId + 1UL);
            return networkId != 0;
        }

        private void SendRequest(NetworkActionRequest request)
        {
            NetworkManager nm = ActiveManager;
            if (nm == null || !nm.isClient) return;
            var packet = new GC2NetworkActionRequestPacket { Request = request };
            if (nm.isServer)
            {
                if (nm.isLocalPlayerReady) DispatchRequestServer(nm.localPlayer, packet);
                return;
            }
            Log($"send request action={request.ActionId} target={request.TargetNetworkId}");
            nm.SendToServer(packet, m_ReliableChannel);
        }

        private void SendResponse(uint clientId, NetworkActionResponse response)
        {
            NetworkManager nm = ActiveManager;
            if (nm == null || !nm.isServer || !TryGetPlayerId(nm, clientId, out var player))
                return;
            nm.Send(player, new GC2NetworkActionResponsePacket { Response = response },
                m_ReliableChannel);
        }

        private void SendAction(uint clientId, NetworkActionBroadcast broadcast)
        {
            TrySendAction(clientId, in broadcast);
        }

        private bool TrySendAction(uint clientId, in NetworkActionBroadcast broadcast)
        {
            NetworkManager nm = ActiveManager;
            if (nm == null || !nm.isServer || !TryGetPlayerId(nm, clientId, out var player))
                return false;
            nm.Send(player, new GC2NetworkActionBroadcastPacket { Broadcast = broadcast },
                ResolveChannel(broadcast));
            return true;
        }

        private void BroadcastAction(NetworkActionBroadcast broadcast)
        {
            NetworkManager nm = ActiveManager;
            if (nm == null || !nm.isServer) return;
            var packet = new GC2NetworkActionBroadcastPacket { Broadcast = broadcast };
            if (!ShouldFilterBySessionProfile(broadcast))
            {
                nm.SendToAll(packet, ResolveChannel(broadcast));
                return;
            }
            IReadOnlyList<PlayerID> players = nm.players;
            for (int i = 0; i < players.Count; i++)
            {
                PlayerID player = players[i];
                uint clientId = PlayerIdToClientId(player);
                if (!NetworkTransportBridge.IsValidClientId(clientId)) continue;
                if (!ShouldSendToClient(
                        clientId, broadcast.TargetNetworkId, broadcast.EndpointHash))
                {
                    m_RelevanceDeliveries.MarkIrrelevant(clientId, in broadcast);
                    continue;
                }

                nm.Send(player, packet, ResolveChannel(broadcast));
                m_RelevanceDeliveries.MarkDelivered(clientId, in broadcast);
            }
        }

        private void SendSnapshot(uint clientId, NetworkActionSnapshot snapshot)
        {
            NetworkManager nm = ActiveManager;
            if (nm == null || !nm.isServer || !TryGetPlayerId(nm, clientId, out var player))
                return;
            NetworkActionSnapshot filtered = FilterSnapshot(clientId, snapshot);
            nm.Send(player, new GC2NetworkActionSnapshotPacket
                { Snapshot = filtered },
                m_ReliableChannel);
            MarkSnapshotDelivered(clientId, in filtered);
        }

        private void BroadcastSnapshot(NetworkActionSnapshot snapshot)
        {
            NetworkManager nm = ActiveManager;
            if (nm == null || !nm.isServer) return;
            IReadOnlyList<PlayerID> players = nm.players;
            for (int i = 0; i < players.Count; i++)
            {
                uint clientId = PlayerIdToClientId(players[i]);
                if (NetworkTransportBridge.IsValidClientId(clientId))
                    SendSnapshot(clientId, snapshot);
            }
        }

        private NetworkActionSnapshot FilterSnapshot(
            uint clientId, NetworkActionSnapshot snapshot)
        {
            NetworkActionBroadcast[] source = snapshot.Entries ??
                                              System.Array.Empty<NetworkActionBroadcast>();
            var entries = new List<NetworkActionBroadcast>(source.Length);
            for (int i = 0; i < source.Length; i++)
            {
                NetworkActionBroadcast entry = source[i];
                if (entry.RecipientPolicy == NetworkActionRecipientPolicy.AllClients ||
                    (entry.RecipientPolicy == NetworkActionRecipientPolicy.RelevantObservers &&
                     ShouldSendToClient(
                         clientId, entry.TargetNetworkId, entry.EndpointHash)))
                    entries.Add(entry);
            }
            snapshot.Entries = entries.ToArray();
            return snapshot;
        }

        private Channel ResolveChannel(in NetworkActionBroadcast broadcast) =>
            broadcast.Reliable || broadcast.EffectKind == NetworkActionEffectKind.PersistentState
                ? m_ReliableChannel
                : m_UnreliableChannel;

        private void HandleRequestServer(
            PlayerID sender, GC2NetworkActionRequestPacket packet, bool asServer)
        {
            if (!asServer) return;
            RefreshEndpointRegistry(true);
            DispatchRequestServer(sender, packet);
        }

        private void DispatchRequestServer(PlayerID sender, GC2NetworkActionRequestPacket packet) =>
            GetManager()?.ReceiveActionRequest(packet.Request, PlayerIdToClientId(sender));

        private void HandleResponseClient(
            PlayerID sender, GC2NetworkActionResponsePacket packet, bool asServer)
        {
            if (!asServer && IsAuthoritativeSender(sender))
                GetManager()?.ReceiveActionResponse(packet.Response);
        }

        private void HandleBroadcastClient(
            PlayerID sender, GC2NetworkActionBroadcastPacket packet, bool asServer)
        {
            if (asServer || !IsAuthoritativeSender(sender)) return;
            RefreshEndpointRegistry(true);
            GetManager()?.ReceiveActionBroadcast(packet.Broadcast);
        }

        private void HandleSnapshotClient(
            PlayerID sender, GC2NetworkActionSnapshotPacket packet, bool asServer)
        {
            if (asServer || !IsAuthoritativeSender(sender)) return;
            RefreshEndpointRegistry(true);
            GetManager()?.ReceiveActionSnapshot(packet.Snapshot);
        }

        private static bool IsAuthoritativeSender(PlayerID sender) => sender.isServer;

        private void RefreshHierarchyObservation(NetworkManager manager)
        {
            HierarchyFactory serverFactory = null;
            HierarchyFactory clientFactory = null;
            if (manager != null)
            {
                if (manager.isServer)
                    manager.TryGetModule(out serverFactory, true);
                if (manager.isClient)
                    manager.TryGetModule(out clientFactory, false);
            }

            if (ReferenceEquals(serverFactory, m_ServerHierarchyFactory) &&
                ReferenceEquals(clientFactory, m_ClientHierarchyFactory)) return;

            StopHierarchyObservation();
            m_ServerHierarchyFactory = serverFactory;
            m_ClientHierarchyFactory = clientFactory;
            if (m_ServerHierarchyFactory != null)
                m_ServerHierarchyFactory.onIdentityRemoved += HandleIdentityRemoved;
            if (m_ClientHierarchyFactory != null &&
                !ReferenceEquals(m_ClientHierarchyFactory, m_ServerHierarchyFactory))
            {
                m_ClientHierarchyFactory.onIdentityRemoved += HandleIdentityRemoved;
            }
        }

        private void StopHierarchyObservation()
        {
            if (m_ServerHierarchyFactory != null)
                m_ServerHierarchyFactory.onIdentityRemoved -= HandleIdentityRemoved;
            if (m_ClientHierarchyFactory != null &&
                !ReferenceEquals(m_ClientHierarchyFactory, m_ServerHierarchyFactory))
            {
                m_ClientHierarchyFactory.onIdentityRemoved -= HandleIdentityRemoved;
            }
            m_ServerHierarchyFactory = null;
            m_ClientHierarchyFactory = null;
        }

        private void HandleIdentityRemoved(NetworkIdentity identity)
        {
            if (!TryResolveKnownNetworkId(identity, out uint networkId) ||
                m_Registered.Count == 0) return;

            NetworkActionManager manager = GetManager();
            m_RemoveBuffer.Clear();
            foreach (KeyValuePair<NetworkActionEndpointKey, NetworkActionEndpoint> pair in
                     m_Registered)
            {
                if (pair.Key.NetworkId != networkId) continue;
                NetworkActionEndpoint endpoint = pair.Value;
                if (endpoint != null)
                {
                    NetworkIdentity registeredIdentity =
                        endpoint.GetComponentInParent<NetworkIdentity>();
                    if (!ReferenceEquals(registeredIdentity, identity)) continue;
                }
                m_RemoveBuffer.Add(pair.Key);
            }

            for (int i = 0; i < m_RemoveBuffer.Count; i++)
            {
                NetworkActionEndpointKey key = m_RemoveBuffer[i];
                m_Registered.TryGetValue(key, out NetworkActionEndpoint endpoint);
                manager?.UnregisterEndpoint(key, true);
                m_RelevanceDeliveries.RemoveEndpoint(key);
                if (endpoint != null) endpoint.ClearTransportNetworkIdentity(key.NetworkId);
                m_Registered.Remove(key);
            }
        }

        private static bool TryResolveKnownNetworkId(
            NetworkIdentity identity,
            out uint networkId)
        {
            networkId = 0;
            if (ReferenceEquals(identity, null) || !identity.id.HasValue ||
                identity.objectId >= uint.MaxValue) return false;
            networkId = (uint)(identity.objectId + 1UL);
            return networkId != 0;
        }

        private bool ShouldFilterBySessionProfile(NetworkActionBroadcast broadcast)
        {
            if (!m_UseSessionProfileRelevance ||
                broadcast.RecipientPolicy != NetworkActionRecipientPolicy.RelevantObservers)
                return false;
            NetworkSessionProfile profile = CoreBridge != null ? CoreBridge.GlobalSessionProfile : null;
            return profile != null &&
                   (profile.enableDistanceCulling || profile.requireObserverCharacterForRelevance);
        }

        private bool UsesDynamicRelevance()
        {
            if (!m_UseSessionProfileRelevance) return false;
            NetworkSessionProfile profile = CoreBridge != null
                ? CoreBridge.GlobalSessionProfile
                : null;
            return profile != null &&
                   (profile.enableDistanceCulling || profile.requireObserverCharacterForRelevance);
        }

        private void ProcessRelevanceCatchUp()
        {
            NetworkManager networkManager = ActiveManager;
            NetworkActionManager actionManager = m_WiredManager;
            if (networkManager == null || !networkManager.isServer || actionManager == null) return;

            NetworkActionSnapshot current = actionManager.BuildSnapshot(Time.time);
            NetworkActionBroadcast[] entries = current.Entries ??
                                               System.Array.Empty<NetworkActionBroadcast>();
            IReadOnlyList<PlayerID> players = networkManager.players;
            for (int playerIndex = 0; playerIndex < players.Count; playerIndex++)
            {
                uint clientId = PlayerIdToClientId(players[playerIndex]);
                if (!NetworkTransportBridge.IsValidClientId(clientId)) continue;
                var deltas = new List<NetworkActionBroadcast>();
                for (int i = 0; i < entries.Length; i++)
                {
                    NetworkActionBroadcast entry = entries[i];
                    if (!NetworkActionRelevanceDeliveryCache.IsTracked(in entry)) continue;
                    if (!ShouldSendToClient(
                            clientId, entry.TargetNetworkId, entry.EndpointHash))
                    {
                        m_RelevanceDeliveries.MarkIrrelevant(clientId, in entry);
                        continue;
                    }

                    if (m_RelevanceDeliveries.NeedsDelivery(clientId, in entry))
                        deltas.Add(entry);
                }

                if (deltas.Count == 0) continue;
                SendSnapshot(clientId, new NetworkActionSnapshot
                {
                    Entries = deltas.ToArray(),
                    AuthorityEpoch = current.AuthorityEpoch,
                    ServerTime = current.ServerTime
                });
            }
        }

        private void MarkSnapshotDelivered(uint clientId, in NetworkActionSnapshot snapshot)
        {
            NetworkActionBroadcast[] entries = snapshot.Entries ??
                                               System.Array.Empty<NetworkActionBroadcast>();
            for (int i = 0; i < entries.Length; i++)
                m_RelevanceDeliveries.MarkDelivered(clientId, in entries[i]);
        }

        private bool ShouldSendToClient(
            uint clientId, uint targetNetworkId, int endpointHash = 0)
        {
            PurrNetTransportBridge bridge = CoreBridge;
            NetworkSessionProfile profile = bridge != null ? bridge.GlobalSessionProfile : null;
            if (profile == null) return true;
            if (bridge.TryGetCharacterOwner(targetNetworkId, out uint ownerId) &&
                ownerId == clientId) return true;
            if (!TryGetTargetPosition(
                    targetNetworkId, endpointHash, out Vector3 targetPosition) ||
                !TryGetObserverPosition(clientId, out Vector3 observerPosition))
                return !profile.requireObserverCharacterForRelevance;
            return !profile.enableDistanceCulling ||
                   Vector3.Distance(observerPosition, targetPosition) <= profile.cullDistance;
        }

        private bool TryGetTargetPosition(uint id, int endpointHash, out Vector3 position)
        {
            position = Vector3.zero;
            if (endpointHash != 0 && m_Registered.TryGetValue(
                    new NetworkActionEndpointKey(id, endpointHash), out var endpoint) &&
                endpoint != null)
            {
                position = endpoint.transform.position;
                return true;
            }
            Character character = CoreBridge != null ? CoreBridge.ResolveCharacter(id) : null;
            if (character != null)
            {
                position = character.transform.position;
                return true;
            }
            return false;
        }

        private bool TryGetObserverPosition(uint clientId, out Vector3 position)
        {
            position = Vector3.zero;
            PurrNetTransportBridge bridge = CoreBridge;
            if (bridge == null ||
                !bridge.TryGetRepresentativeCharacterId(clientId, out uint characterId)) return false;
            Character character = bridge.ResolveCharacter(characterId);
            if (character == null) return false;
            position = character.transform.position;
            return true;
        }

        private void Log(string message)
        {
            if (m_LogNetworkMessages)
                Debug.Log($"[PurrNetNetworkActionTransportBridge] {message}", this);
        }

        private static NetworkActionManager GetManager() =>
            NetworkActionManager.Instance != null
                ? NetworkActionManager.Instance
                : FindFirstObjectByType<NetworkActionManager>(FindObjectsInactive.Include);

        private static uint PlayerIdToClientId(PlayerID player)
        {
            ulong raw = player.id;
            return raw <= uint.MaxValue
                ? (uint)raw
                : NetworkTransportBridge.InvalidClientId;
        }

        private static bool TryGetPlayerId(
            NetworkManager manager, uint clientId, out PlayerID player)
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
    }
}
