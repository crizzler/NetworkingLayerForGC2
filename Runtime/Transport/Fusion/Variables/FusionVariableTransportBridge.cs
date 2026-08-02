using System.Collections.Generic;
using GameCreator.Runtime.Characters;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Transport.Fusion
{
    [DisallowMultipleComponent]
    [AddComponentMenu("Game Creator/Network/Transport/Fusion Variables Bridge")]
    [DefaultExecutionOrder(-338)]
    public sealed class FusionVariableTransportBridge : FusionModuleTransportBridgeBase,
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

        [Header("Profiles")]
        [SerializeField] private NetworkVariableProfile[] m_GlobalProfiles;

        [Header("Controllers")]
        [SerializeField] private bool m_AutoRegisterSceneControllers = true;
        [Min(0.05f)]
        [SerializeField] private float m_ControllerScanInterval = 0.25f;

        private readonly Dictionary<uint, NetworkVariableController> m_RegisteredControllers = new(32);
        private readonly List<uint> m_RemoveBuffer = new(16);
        private NetworkVariableManager m_WiredManager;
        private bool m_ManagerInitialized;
        private bool m_LastServer;
        private float m_NextControllerScanTime;

        protected override ushort ModuleId => FusionModuleIds.Variables;

        public string GameplayReadinessName => "Variables";

        public bool IsGameplayReady(FusionNetworkIdentity identity)
        {
            if (!isActiveAndEnabled || identity == null || identity.NetworkId == 0 ||
                !identity.TransportAdmitted || TransportBridge == null ||
                !TransportBridge.IsClient)
            {
                return false;
            }

            WireVariableManager();
            RefreshControllerRegistry(true);
            if (!m_ManagerInitialized || m_WiredManager == null ||
                m_WiredManager != GetVariableManager())
            {
                return false;
            }

            NetworkVariableController relevant =
                identity.GetComponentInChildren<NetworkVariableController>(true);
            if (relevant == null) return true;

            return m_RegisteredControllers.TryGetValue(
                       identity.NetworkId, out NetworkVariableController registered) &&
                   registered == relevant;
        }

        protected override void OnModuleEnabled()
        {
            WireVariableManager();
            RefreshControllerRegistry(true);
        }

        protected override void OnModuleStarted()
        {
            WireVariableManager();
            RefreshControllerRegistry(true);
        }

        protected override void OnModuleUpdate()
        {
            WireVariableManager();
            if (!m_AutoRegisterSceneControllers ||
                Time.unscaledTime < m_NextControllerScanTime) return;

            m_NextControllerScanTime =
                Time.unscaledTime + Mathf.Max(0.05f, m_ControllerScanInterval);
            RefreshControllerRegistry(false);
        }

        protected override void OnModuleDisabled()
        {
            UnwireVariableManager();
            UnregisterAllControllers();
        }

        protected override void OnAuthorityChanged(bool isAuthority, uint authorityEpoch)
        {
            m_ManagerInitialized = false;
            WireVariableManager();
            RefreshControllerRegistry(true);
        }

        public override string FullSnapshotProducerName => "Variables";

        protected override FusionFullSnapshotResult ProduceFullSnapshotForClient(
            FusionFullSnapshotContext context)
        {
            WireVariableManager();
            RefreshControllerRegistry(true);
            NetworkVariableManager manager = GetVariableManager();
            if (manager == null || manager != m_WiredManager || !m_ManagerInitialized)
            {
                return context.Fail("NetworkVariableManager is unavailable or not initialized.");
            }

            manager.SendInitialState(context.ClientId);
            return context.Complete();
        }

        protected override void HandleModuleMessage(FusionModuleMessage message)
        {
            NetworkVariableManager manager = GetVariableManager();
            if (manager == null) return;

            switch ((MessageType)message.MessageType)
            {
                case MessageType.Request:
                    if (AcceptRequest(message) &&
                        TryRead(message, out NetworkVariableRequest request))
                    {
                        RefreshControllerRegistry(true);
                        manager.ReceiveVariableRequest(request, message.SenderClientId);
                    }
                    break;
                case MessageType.Response:
                    if (AcceptAuthority(message) &&
                        TryRead(message, out NetworkVariableResponse response))
                        manager.ReceiveVariableResponse(response);
                    break;
                case MessageType.Broadcast:
                    if (AcceptAuthority(message) &&
                        TryRead(message, out NetworkVariableBroadcast broadcast))
                    {
                        RefreshControllerRegistry(true);
                        manager.ReceiveVariableBroadcast(broadcast);
                    }
                    break;
                case MessageType.Snapshot:
                    if (AcceptAuthority(message) &&
                        TryRead(message, out NetworkVariableSnapshot snapshot))
                    {
                        RefreshControllerRegistry(true);
                        manager.ReceiveVariableSnapshot(snapshot);
                    }
                    break;
            }
        }

        private bool AcceptRequest(FusionModuleMessage message)
        {
            return TransportBridge != null && TransportBridge.IsServer && !message.FromAuthority;
        }

        private bool AcceptAuthority(FusionModuleMessage message)
        {
            return TransportBridge != null && TransportBridge.IsClient && message.FromAuthority;
        }

        private void WireVariableManager()
        {
            NetworkVariableManager manager = GetVariableManager();
            if (manager == null) return;
            if (m_WiredManager != null && m_WiredManager != manager) UnwireVariableManager();
            m_WiredManager = manager;

            manager.OnSendVariableRequest -= SendVariableRequestToAuthority;
            manager.OnSendVariableRequest += SendVariableRequestToAuthority;
            manager.OnSendVariableResponse -= SendVariableResponseToClient;
            manager.OnSendVariableResponse += SendVariableResponseToClient;
            manager.OnBroadcastVariableChange -= BroadcastVariableChange;
            manager.OnBroadcastVariableChange += BroadcastVariableChange;
            manager.OnBroadcastSnapshot -= BroadcastSnapshot;
            manager.OnBroadcastSnapshot += BroadcastSnapshot;
            manager.OnSendSnapshotToClient -= SendSnapshotToClient;
            manager.OnSendSnapshotToClient += SendSnapshotToClient;

            RegisterConfiguredProfiles(manager);
            bool isServer = TransportBridge != null && TransportBridge.IsServer;
            if (!m_ManagerInitialized || isServer != m_LastServer)
            {
                manager.IsServer = isServer;
                m_ManagerInitialized = true;
                m_LastServer = isServer;
            }
        }

        private void UnwireVariableManager()
        {
            NetworkVariableManager manager = m_WiredManager;
            if (manager == null) return;
            manager.OnSendVariableRequest -= SendVariableRequestToAuthority;
            manager.OnSendVariableResponse -= SendVariableResponseToClient;
            manager.OnBroadcastVariableChange -= BroadcastVariableChange;
            manager.OnBroadcastSnapshot -= BroadcastSnapshot;
            manager.OnSendSnapshotToClient -= SendSnapshotToClient;
            m_WiredManager = null;
            m_ManagerInitialized = false;
        }

        private void RegisterConfiguredProfiles(NetworkVariableManager manager)
        {
            if (m_GlobalProfiles == null) return;
            for (int i = 0; i < m_GlobalProfiles.Length; i++)
                manager.RegisterGlobalProfile(m_GlobalProfiles[i]);
        }

        private void RefreshControllerRegistry(bool force)
        {
            NetworkVariableManager manager = GetVariableManager();
            if (manager == null) return;
            PruneControllerRegistry(manager);
            if (!m_AutoRegisterSceneControllers && !force) return;

            NetworkVariableController[] controllers =
                FindObjectsByType<NetworkVariableController>(
                    FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            for (int i = 0; i < controllers.Length; i++)
                RegisterController(manager, controllers[i]);
        }

        private void RegisterController(
            NetworkVariableManager manager, NetworkVariableController controller)
        {
            if (manager == null || controller == null) return;

            NetworkCharacter character = controller.NetworkCharacter != null
                ? controller.NetworkCharacter
                : controller.GetComponent<NetworkCharacter>();
            if (!TryResolveControllerIdentity(
                    controller, character, out uint networkId, out FusionNetworkIdentity identity))
                return;

            if (m_RegisteredControllers.TryGetValue(
                    networkId, out NetworkVariableController existing))
            {
                if (existing == controller)
                {
                    ApplyTransportIdentity(controller, identity, networkId);
                    return;
                }

                manager.UnregisterController(networkId, existing);
                existing.ClearTransportNetworkIdentity(networkId);
            }

            ApplyTransportIdentity(controller, identity, networkId);
            m_RegisteredControllers[networkId] = controller;
            manager.RegisterController(networkId, controller);

            if (!manager.IsServer) return;
            NetworkVariableBroadcast[] changes = controller.BuildSnapshot(Time.time);
            for (int i = 0; i < changes.Length; i++)
                manager.OnBroadcastVariableChange?.Invoke(changes[i]);
        }

        private void PruneControllerRegistry(NetworkVariableManager manager)
        {
            m_RemoveBuffer.Clear();
            foreach (KeyValuePair<uint, NetworkVariableController> pair in m_RegisteredControllers)
            {
                NetworkVariableController controller = pair.Value;
                NetworkCharacter character = controller != null
                    ? controller.NetworkCharacter != null
                        ? controller.NetworkCharacter
                        : controller.GetComponent<NetworkCharacter>()
                    : null;
                if (controller == null ||
                    !TryResolveControllerIdentity(
                        controller, character, out uint currentId, out FusionNetworkIdentity identity) ||
                    currentId != pair.Key)
                {
                    m_RemoveBuffer.Add(pair.Key);
                    continue;
                }

                ApplyTransportIdentity(controller, identity, currentId);
            }

            for (int i = 0; i < m_RemoveBuffer.Count; i++)
            {
                uint id = m_RemoveBuffer[i];
                NetworkVariableController controller =
                    m_RegisteredControllers.TryGetValue(id, out NetworkVariableController value)
                        ? value
                        : null;
                manager.UnregisterController(id, controller);
                controller?.ClearTransportNetworkIdentity(id);
                m_RegisteredControllers.Remove(id);
            }
        }

        private void UnregisterAllControllers()
        {
            NetworkVariableManager manager = GetVariableManager();
            if (manager != null)
            {
                foreach (KeyValuePair<uint, NetworkVariableController> pair in m_RegisteredControllers)
                {
                    manager.UnregisterController(pair.Key, pair.Value);
                    pair.Value?.ClearTransportNetworkIdentity(pair.Key);
                }
            }

            m_RegisteredControllers.Clear();
        }

        private static bool TryResolveControllerIdentity(
            NetworkVariableController controller,
            NetworkCharacter character,
            out uint networkId,
            out FusionNetworkIdentity identity)
        {
            networkId = 0;
            identity = null;
            if (controller == null) return false;

            if (character != null)
            {
                if (character.NetworkId == 0 ||
                    character.Role == NetworkCharacter.NetworkRole.None) return false;
                networkId = character.NetworkId;
                identity = character.GetComponentInParent<FusionNetworkIdentity>();
                return true;
            }

            identity = controller.GetComponentInParent<FusionNetworkIdentity>();
            if (identity == null || identity.NetworkId == 0) return false;
            networkId = identity.NetworkId;
            return true;
        }

        private void ApplyTransportIdentity(
            NetworkVariableController controller,
            FusionNetworkIdentity identity,
            uint networkId)
        {
            if (controller == null || networkId == 0) return;

            uint ownerId = NetworkTransportBridge.InvalidClientId;
            bool isLocalController = false;
            if (identity != null && identity.TryGetLogicalOwnerClientId(out uint resolvedOwner))
            {
                ownerId = resolvedOwner;
                isLocalController = TransportBridge != null &&
                    TransportBridge.TryGetLocalClientId(out uint localId) &&
                    localId == ownerId;
            }
            else if (TransportBridge != null &&
                     TransportBridge.TryGetCharacterOwner(networkId, out resolvedOwner))
            {
                ownerId = resolvedOwner;
                isLocalController =
                    TransportBridge.TryGetLocalClientId(out uint localId) &&
                    localId == ownerId;
            }

            controller.ApplyTransportNetworkIdentity(networkId, isLocalController, ownerId);
        }

        private void SendVariableRequestToAuthority(NetworkVariableRequest request)
        {
            SendToAuthority((ushort)MessageType.Request, request);
        }

        private void SendVariableResponseToClient(
            uint clientId, NetworkVariableResponse response)
        {
            SendToClient(clientId, (ushort)MessageType.Response, response);
        }

        private void BroadcastVariableChange(NetworkVariableBroadcast broadcast)
        {
            FusionTransportBridge bridge = TransportBridge;
            if (bridge == null || !bridge.IsServer) return;

            if (!ShouldFilterBySessionProfile(broadcast))
            {
                Broadcast((ushort)MessageType.Broadcast, broadcast);
                return;
            }

            foreach (uint clientId in bridge.ConnectedClientIds)
            {
                if (!ShouldSendLocalVariableToClient(clientId, broadcast.TargetNetworkId)) continue;
                SendToClient(clientId, (ushort)MessageType.Broadcast, broadcast);
            }
        }

        private void BroadcastSnapshot(NetworkVariableSnapshot snapshot)
        {
            Broadcast((ushort)MessageType.Snapshot, snapshot);
        }

        private void SendSnapshotToClient(ulong rawClientId, NetworkVariableSnapshot snapshot)
        {
            if (!NetworkTransportBridge.TryConvertSenderClientId(rawClientId, out uint clientId)) return;
            SendToClient(clientId, (ushort)MessageType.Snapshot, snapshot);
        }

        private bool ShouldFilterBySessionProfile(NetworkVariableBroadcast broadcast)
        {
            if (!m_UseSessionProfileRelevance) return false;
            if (broadcast.Scope == NetworkVariableScope.GlobalName ||
                broadcast.Scope == NetworkVariableScope.GlobalList) return false;

            NetworkSessionProfile profile =
                TransportBridge != null ? TransportBridge.GlobalSessionProfile : null;
            return profile != null &&
                   (profile.enableDistanceCulling || profile.requireObserverCharacterForRelevance);
        }

        private bool ShouldSendLocalVariableToClient(uint clientId, uint targetNetworkId)
        {
            FusionTransportBridge bridge = TransportBridge;
            NetworkSessionProfile profile =
                bridge != null ? bridge.GlobalSessionProfile : null;
            if (profile == null) return true;

            if (bridge.TryGetCharacterOwner(targetNetworkId, out uint ownerId) &&
                ownerId == clientId) return true;
            if (!TryGetTargetPosition(targetNetworkId, out Vector3 targetPosition))
                return !profile.requireObserverCharacterForRelevance;
            if (!TryGetObserverPosition(clientId, out Vector3 observerPosition))
                return !profile.requireObserverCharacterForRelevance;

            return !profile.enableDistanceCulling ||
                   Vector3.Distance(observerPosition, targetPosition) <= profile.cullDistance;
        }

        private bool TryGetTargetPosition(uint networkId, out Vector3 position)
        {
            position = Vector3.zero;
            Character character = TransportBridge != null
                ? TransportBridge.ResolveCharacter(networkId)
                : null;
            if (character != null)
            {
                position = character.transform.position;
                return true;
            }

            if (!m_RegisteredControllers.TryGetValue(
                    networkId, out NetworkVariableController controller) ||
                controller == null) return false;
            position = controller.transform.position;
            return true;
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

        private static NetworkVariableManager GetVariableManager()
        {
            return NetworkVariableManager.Instance != null
                ? NetworkVariableManager.Instance
                : FindFirstObjectByType<NetworkVariableManager>(FindObjectsInactive.Include);
        }
    }
}
