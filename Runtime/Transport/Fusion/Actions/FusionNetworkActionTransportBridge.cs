using System.Collections.Generic;
using GameCreator.Runtime.Characters;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Transport.Fusion
{
    /// <summary>
    /// Carries the transport-neutral Network Actions protocol over Fusion. Requests always go
    /// to logical authority; only authority-approved canonical events/state are replicated.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Game Creator/Network/Transport/Fusion Network Actions Bridge")]
    [DefaultExecutionOrder(-337)]
    public sealed class FusionNetworkActionTransportBridge : FusionModuleTransportBridgeBase,
        IFusionGameplayReadinessParticipant
    {
        private enum MessageType : ushort
        {
            Request = 1,
            Response = 2,
            Broadcast = 3,
            Snapshot = 4
        }

        [Header("Relevance")]
        [SerializeField] private bool m_UseSessionProfileRelevance = true;

        [Header("Endpoints")]
        [SerializeField] private bool m_AutoRegisterSceneEndpoints = true;
        [Min(0.05f)]
        [SerializeField] private float m_EndpointScanInterval = 0.25f;

        private readonly Dictionary<NetworkActionEndpointKey, NetworkActionEndpoint> m_Registered =
            new(32);
        private readonly List<NetworkActionEndpointKey> m_RemoveBuffer = new(16);
        private readonly NetworkActionRelevanceDeliveryCache m_RelevanceDeliveries = new();
        private FusionTransportBridge m_ObservedTransport;
        private NetworkActionManager m_WiredManager;
        private bool m_ManagerInitialized;
        private bool m_LastServer;
        private bool m_LastRunning;
        private bool m_ObservingIdentityLifecycle;
        private float m_NextRelevanceSnapshotTime;
        private float m_NextEndpointScanTime;

        protected override ushort ModuleId => FusionModuleIds.NetworkActions;
        public string GameplayReadinessName => "Network Actions";
        public override string FullSnapshotProducerName => "Network Actions";

        public bool IsGameplayReady(FusionNetworkIdentity identity)
        {
            if (!isActiveAndEnabled || identity == null || identity.NetworkId == 0 ||
                !identity.TransportAdmitted || TransportBridge == null ||
                !TransportBridge.IsClient)
            {
                return false;
            }

            WireManager();
            RefreshEndpointRegistry(true);
            if (!m_ManagerInitialized || m_WiredManager == null ||
                m_WiredManager != GetManager()) return false;

            NetworkActionEndpoint relevant =
                identity.GetComponentInChildren<NetworkActionEndpoint>(true);
            if (relevant == null) return true;
            NetworkActionEndpoint[] endpoints =
                identity.GetComponentsInChildren<NetworkActionEndpoint>(true);
            for (int i = 0; i < endpoints.Length; i++)
            {
                NetworkActionEndpoint endpoint = endpoints[i];
                if (endpoint == null || endpoint.EndpointHash == 0 ||
                    !m_Registered.TryGetValue(
                        new NetworkActionEndpointKey(identity.NetworkId, endpoint.EndpointHash),
                        out var registered) || registered != endpoint) return false;
            }
            return true;
        }

        protected override void OnModuleEnabled()
        {
            StartIdentityLifecycleObservation();
            RefreshTransportObservation();
            WireManager();
            RefreshEndpointRegistry(true);
        }

        protected override void OnModuleStarted()
        {
            StartIdentityLifecycleObservation();
            RefreshTransportObservation();
            WireManager();
            RefreshEndpointRegistry(true);
        }

        protected override void OnModuleUpdate()
        {
            RefreshTransportObservation();
            WireManager();
            bool running = TransportBridge != null && TransportBridge.IsRunning;
            if (m_LastRunning && !running)
            {
                m_WiredManager?.ResetSessionState();
                m_RelevanceDeliveries.Clear();
            }
            m_LastRunning = running;
            if (running && TransportBridge != null && TransportBridge.IsServer &&
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

        protected override void OnModuleDisabled()
        {
            m_WiredManager?.ResetSessionState();
            m_RelevanceDeliveries.Clear();
            StopIdentityLifecycleObservation();
            StopTransportObservation();
            UnwireManager();
            UnregisterAllEndpoints();
        }

        protected override void OnAuthorityChanged(bool isAuthority, uint authorityEpoch)
        {
            RefreshTransportObservation();
            m_RelevanceDeliveries.Clear();
            WireManager();
            if (m_WiredManager != null)
            {
                m_WiredManager.IsServer = isAuthority;
                m_WiredManager.SetAuthorityEpoch(authorityEpoch);
                m_ManagerInitialized = true;
                m_LastServer = isAuthority;
            }
            RefreshEndpointRegistry(true);
        }

        protected override FusionFullSnapshotResult ProduceFullSnapshotForClient(
            FusionFullSnapshotContext context)
        {
            WireManager();
            RefreshEndpointRegistry(true);
            NetworkActionManager manager = GetManager();
            if (manager == null || manager != m_WiredManager || !m_ManagerInitialized)
                return context.Fail("NetworkActionManager is unavailable or not initialized.");
            manager.SendInitialState(context.ClientId);
            return context.Complete();
        }

        protected override void HandleModuleMessage(FusionModuleMessage message)
        {
            NetworkActionManager manager = GetManager();
            if (manager == null) return;
            switch ((MessageType)message.MessageType)
            {
                case MessageType.Request:
                    if (TransportBridge != null && TransportBridge.IsServer &&
                        !message.FromAuthority &&
                        TryRead(message, out NetworkActionRequest request))
                    {
                        RefreshEndpointRegistry(true);
                        manager.ReceiveActionRequest(request, message.SenderClientId);
                    }
                    break;
                case MessageType.Response:
                    if (AcceptAuthority(message) &&
                        TryRead(message, out NetworkActionResponse response))
                        manager.ReceiveActionResponse(response);
                    break;
                case MessageType.Broadcast:
                    if (AcceptAuthority(message) &&
                        TryRead(message, out NetworkActionBroadcast broadcast))
                    {
                        RefreshEndpointRegistry(true);
                        manager.ReceiveActionBroadcast(broadcast);
                    }
                    break;
                case MessageType.Snapshot:
                    if (AcceptAuthority(message) &&
                        TryRead(message, out NetworkActionSnapshot snapshot))
                    {
                        RefreshEndpointRegistry(true);
                        manager.ReceiveActionSnapshot(snapshot);
                    }
                    break;
            }
        }

        private bool AcceptAuthority(FusionModuleMessage message) =>
            TransportBridge != null && TransportBridge.IsClient && message.FromAuthority;

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

            bool isServer = TransportBridge != null && TransportBridge.IsServer;
            if (!m_ManagerInitialized || isServer != m_LastServer)
            {
                manager.IsServer = isServer;
                if (TransportBridge != null)
                    manager.SetAuthorityEpoch(TransportBridge.AuthorityEpoch);
                m_ManagerInitialized = true;
                m_LastServer = isServer;
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
            m_LastRunning = false;
        }

        private void RefreshEndpointRegistry(bool force)
        {
            NetworkActionManager manager = GetManager();
            if (manager == null) return;
            PruneEndpointRegistry(manager);
            if (!m_AutoRegisterSceneEndpoints && !force) return;

            NetworkActionEndpoint[] endpoints = UnityObjectSearch.FindAll<NetworkActionEndpoint>(FindObjectsInactive.Exclude);
            for (int i = 0; i < endpoints.Length; i++) RegisterEndpoint(manager, endpoints[i]);
        }

        private void RegisterEndpoint(NetworkActionManager manager, NetworkActionEndpoint endpoint)
        {
            if (manager == null || endpoint == null) return;
            FusionNetworkIdentity identity = endpoint.GetComponentInParent<FusionNetworkIdentity>();
            if (identity == null || identity.NetworkId == 0 || !identity.TransportAdmitted) return;
            uint networkId = identity.NetworkId;
            uint ownerId = NetworkTransportBridge.InvalidClientId;
            bool isLocalController = false;
            if (identity.TryGetLogicalOwnerClientId(out uint resolvedOwner))
            {
                ownerId = resolvedOwner;
                isLocalController = TransportBridge != null &&
                    TransportBridge.TryGetLocalClientId(out uint localId) && localId == ownerId;
            }

            NetworkActionEndpointKey key = new(networkId, endpoint.EndpointHash);
            if (!key.IsValid) return;
            if (m_Registered.TryGetValue(key, out var existing) && existing != endpoint)
            {
                Debug.LogError(
                    $"[FusionNetworkActionTransportBridge] Endpoint ID/hash collision under " +
                    $"Network ID {networkId}: '{existing?.name ?? "(destroyed)"}' and " +
                    $"'{endpoint.name}' resolve to {endpoint.EndpointHash}. The later endpoint " +
                    "was ignored. Give sibling endpoints unique stable Endpoint IDs.",
                    endpoint);
                return;
            }

            endpoint.ApplyTransportNetworkIdentity(networkId, isLocalController, ownerId, false);
            m_Registered[key] = endpoint;
            manager.RegisterEndpoint(networkId, endpoint);
        }

        private void PruneEndpointRegistry(NetworkActionManager manager)
        {
            m_RemoveBuffer.Clear();
            foreach (var pair in m_Registered)
            {
                NetworkActionEndpoint endpoint = pair.Value;
                FusionNetworkIdentity identity = endpoint != null
                    ? endpoint.GetComponentInParent<FusionNetworkIdentity>()
                    : null;
                if (endpoint == null || identity == null ||
                    identity.NetworkId != pair.Key.NetworkId ||
                    endpoint.EndpointHash != pair.Key.EndpointHash ||
                    !identity.TransportAdmitted)
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
                        // Polling can observe a short admission gap during scene load/migration.
                        // Confirmed Fusion despawns are handled by the lifecycle callback below.
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

        private void SendRequest(NetworkActionRequest request)
        {
            FusionTransportBridge bridge = TransportBridge;
            NetworkActionManager manager = m_WiredManager ?? GetManager();
            if (bridge == null || !bridge.IsLocalGameplayReady)
            {
                manager?.RejectLocalActionRequest(
                    in request,
                    NetworkActionRejectReason.NotRunning);
                return;
            }

            if (!SendToAuthority((ushort)MessageType.Request, request, true))
            {
                manager?.RejectLocalActionRequest(
                    in request,
                    NetworkActionRejectReason.TransportUnavailable);
            }
        }

        private void SendResponse(uint clientId, NetworkActionResponse response) =>
            SendToClient(clientId, (ushort)MessageType.Response, response, true);

        private void SendAction(uint clientId, NetworkActionBroadcast broadcast)
        {
            TrySendAction(clientId, in broadcast);
        }

        private bool TrySendAction(uint clientId, in NetworkActionBroadcast broadcast)
        {
            bool reliable = ResolveWireReliability(broadcast);
            bool delivered = SendToClient(
                clientId, (ushort)MessageType.Broadcast, broadcast, reliable);
            if (!delivered)
            {
                Debug.LogWarning(
                    $"[Fusion Network Actions] Could not deliver action '{broadcast.ActionId}' " +
                    $"to client {clientId}.", this);
            }
            return delivered;
        }

        private void BroadcastAction(NetworkActionBroadcast broadcast)
        {
            FusionTransportBridge bridge = TransportBridge;
            if (bridge == null || !bridge.IsServer) return;
            if (!ShouldFilterBySessionProfile(broadcast))
            {
                Broadcast(
                    (ushort)MessageType.Broadcast,
                    broadcast,
                    ResolveWireReliability(broadcast));
                if (IsSharedPersistentState(in broadcast))
                {
                    foreach (uint clientId in bridge.ConnectedClientIds)
                        if (bridge.IsClientReady(clientId))
                            m_RelevanceDeliveries.MarkDelivered(clientId, in broadcast);
                }
                return;
            }

            foreach (uint clientId in bridge.ConnectedClientIds)
            {
                if (!bridge.IsClientReady(clientId))
                {
                    m_RelevanceDeliveries.RemoveClient(clientId);
                    continue;
                }
                if (!ShouldSendToClient(
                        clientId, broadcast.TargetNetworkId, broadcast.EndpointHash))
                {
                    m_RelevanceDeliveries.MarkIrrelevant(clientId, in broadcast);
                    continue;
                }

                if (TrySendAction(clientId, in broadcast))
                    m_RelevanceDeliveries.MarkDelivered(clientId, in broadcast);
            }
        }

        private static bool ResolveWireReliability(in NetworkActionBroadcast broadcast)
        {
            if (broadcast.Reliable ||
                broadcast.EffectKind == NetworkActionEffectKind.PersistentState) return true;

            // Fusion's unreliable RPC envelope is intentionally small. Promote a large
            // disposable event to the reliable segmented route instead of silently dropping an
            // already authority-approved action. The authored semantic remains transient.
            byte[] payload = FusionWireSerializer.Serialize(broadcast);
            return payload.Length + FusionProtocol.EnvelopeHeaderLength >
                   FusionProtocol.RpcPayloadLimit;
        }

        private void SendSnapshot(uint clientId, NetworkActionSnapshot snapshot)
        {
            NetworkActionSnapshot filtered = FilterSnapshot(clientId, snapshot);
            if (!SendToClient(
                    clientId,
                    (ushort)MessageType.Snapshot,
                    filtered,
                    true)) return;
            MarkSnapshotDelivered(clientId, in filtered);
        }

        private void BroadcastSnapshot(NetworkActionSnapshot snapshot)
        {
            FusionTransportBridge bridge = TransportBridge;
            if (bridge == null || !bridge.IsServer) return;
            foreach (uint clientId in bridge.ConnectedClientIds)
                SendSnapshot(clientId, snapshot);
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
                     (IsSharedPersistentState(in entry) ||
                      ShouldSendToClient(
                          clientId, entry.TargetNetworkId, entry.EndpointHash))))
                    entries.Add(entry);
            }
            snapshot.Entries = entries.ToArray();
            return snapshot;
        }

        private bool ShouldFilterBySessionProfile(NetworkActionBroadcast broadcast)
        {
            if (!m_UseSessionProfileRelevance ||
                broadcast.RecipientPolicy != NetworkActionRecipientPolicy.RelevantObservers ||
                IsSharedPersistentState(in broadcast))
                return false;
            NetworkSessionProfile profile =
                TransportBridge != null ? TransportBridge.GlobalSessionProfile : null;
            return profile != null &&
                   (profile.enableDistanceCulling || profile.requireObserverCharacterForRelevance);
        }

        private bool UsesDynamicRelevance()
        {
            if (!m_UseSessionProfileRelevance || TransportBridge == null) return false;
            NetworkSessionProfile profile = TransportBridge.GlobalSessionProfile;
            return profile != null &&
                   (profile.enableDistanceCulling || profile.requireObserverCharacterForRelevance);
        }

        private void ProcessRelevanceCatchUp()
        {
            FusionTransportBridge bridge = TransportBridge;
            NetworkActionManager manager = m_WiredManager;
            if (bridge == null || manager == null || !bridge.IsServer) return;

            NetworkActionSnapshot current = manager.BuildSnapshot(bridge.ServerTime);
            NetworkActionBroadcast[] entries = current.Entries ??
                                               System.Array.Empty<NetworkActionBroadcast>();
            foreach (uint clientId in bridge.ConnectedClientIds)
            {
                if (!bridge.IsClientReady(clientId)) continue;
                var deltas = new List<NetworkActionBroadcast>();
                for (int i = 0; i < entries.Length; i++)
                {
                    NetworkActionBroadcast entry = entries[i];
                    if (!NetworkActionRelevanceDeliveryCache.IsTracked(in entry) ||
                        IsSharedPersistentState(in entry)) continue;

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
                var delta = new NetworkActionSnapshot
                {
                    Entries = deltas.ToArray(),
                    AuthorityEpoch = current.AuthorityEpoch,
                    ServerTime = current.ServerTime
                };
                if (!SendToClient(
                        clientId, (ushort)MessageType.Snapshot, delta, true)) continue;
                MarkSnapshotDelivered(clientId, in delta);
            }
        }

        private void MarkSnapshotDelivered(uint clientId, in NetworkActionSnapshot snapshot)
        {
            NetworkActionBroadcast[] entries = snapshot.Entries ??
                                               System.Array.Empty<NetworkActionBroadcast>();
            for (int i = 0; i < entries.Length; i++)
                m_RelevanceDeliveries.MarkDelivered(clientId, in entries[i]);
        }

        private bool IsSharedPersistentState(in NetworkActionBroadcast broadcast)
        {
            FusionTransportBridge bridge = TransportBridge;
            return broadcast.EffectKind == NetworkActionEffectKind.PersistentState &&
                   bridge != null && bridge.Runner != null &&
                   bridge.Runner.GameMode == global::Fusion.GameMode.Shared;
        }

        private void RefreshTransportObservation()
        {
            FusionTransportBridge bridge = TransportBridge;
            if (bridge == m_ObservedTransport) return;
            StopTransportObservation();
            m_ObservedTransport = bridge;
            if (m_ObservedTransport != null)
                m_ObservedTransport.PlayerObservedLeft += HandlePlayerObservedLeft;
        }

        private void StopTransportObservation()
        {
            if (m_ObservedTransport != null)
                m_ObservedTransport.PlayerObservedLeft -= HandlePlayerObservedLeft;
            m_ObservedTransport = null;
        }

        private void StartIdentityLifecycleObservation()
        {
            if (m_ObservingIdentityLifecycle) return;
            FusionNetworkIdentity.AnyIdentityObservedDespawned += HandleIdentityObservedDespawned;
            m_ObservingIdentityLifecycle = true;
        }

        private void StopIdentityLifecycleObservation()
        {
            if (!m_ObservingIdentityLifecycle) return;
            FusionNetworkIdentity.AnyIdentityObservedDespawned -= HandleIdentityObservedDespawned;
            m_ObservingIdentityLifecycle = false;
        }

        private void HandleIdentityObservedDespawned(FusionIdentityObservation observation)
        {
            FusionTransportBridge bridge = TransportBridge;
            if (bridge == null || observation.Runner != bridge.Runner) return;
            if (observation.NetworkId == 0 || m_Registered.Count == 0) return;
            NetworkActionManager manager = GetManager();
            m_RemoveBuffer.Clear();
            foreach (KeyValuePair<NetworkActionEndpointKey, NetworkActionEndpoint> pair in
                     m_Registered)
            {
                if (pair.Key.NetworkId != observation.NetworkId) continue;
                NetworkActionEndpoint endpoint = pair.Value;
                if (endpoint != null)
                {
                    FusionNetworkIdentity identity =
                        endpoint.GetComponentInParent<FusionNetworkIdentity>();
                    if (!ReferenceEquals(identity, observation.Identity)) continue;
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

        private void HandlePlayerObservedLeft(FusionPlayerConnectionInfo info)
        {
            m_RelevanceDeliveries.RemoveClient(info.ClientId);
        }

        private bool ShouldSendToClient(
            uint clientId, uint targetNetworkId, int endpointHash = 0)
        {
            FusionTransportBridge bridge = TransportBridge;
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
            Character character = TransportBridge != null ? TransportBridge.ResolveCharacter(id) : null;
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
            FusionTransportBridge bridge = TransportBridge;
            if (bridge == null ||
                !bridge.TryGetRepresentativeCharacterId(clientId, out uint characterId)) return false;
            Character character = bridge.ResolveCharacter(characterId);
            if (character == null) return false;
            position = character.transform.position;
            return true;
        }

        private static NetworkActionManager GetManager() =>
            NetworkActionManager.Instance != null
                ? NetworkActionManager.Instance
                : UnityObjectSearch.FindAny<NetworkActionManager>(FindObjectsInactive.Include);
    }
}
