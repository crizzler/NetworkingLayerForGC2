#if GC2_QUESTS
using System;
using System.Collections.Generic;
using Arawn.GameCreator2.Networking.Transport.Fusion;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Quests.Transport.Fusion
{
    [DisallowMultipleComponent]
    [AddComponentMenu("Game Creator/Network/Transport/Fusion Quests Bridge")]
    [DefaultExecutionOrder(-338)]
    public sealed class FusionQuestsTransportBridge : MonoBehaviour,
        IFusionGameplayReadinessParticipant,
        IFusionFullSnapshotProducer
    {
        public const ushort ModuleId = 40;

        private enum MessageType : ushort
        {
            Request = 1,
            Response = 2,
            Broadcast = 3,
            Snapshot = 4
        }

        [Header("Fusion")]
        [SerializeField] private FusionTransportBridge m_TransportBridge;

        [Header("Controllers")]
        [SerializeField] private bool m_AutoRegisterSceneControllers = true;
        [Min(0.05f)]
        [SerializeField] private float m_ControllerScanInterval = 0.25f;

        [Header("Debug")]
        [SerializeField] private bool m_LogNetworkMessages;

        private readonly Dictionary<uint, NetworkQuestsController> m_RegisteredControllers = new(32);
        private readonly List<uint> m_RemoveBuffer = new(16);

        private FusionTransportBridge m_BoundBridge;
        private NetworkQuestsManager m_WiredManager;
        private bool m_ManagerInitialized;
        private bool m_LastServer;
        private float m_NextControllerScanTime;

        public string GameplayReadinessName => "Quests";
        public FusionTransportBridge GameplayReadinessTransport => m_BoundBridge;
        public ushort GameplayReadinessModuleId => ModuleId;
        public ushort FullSnapshotModuleId => ModuleId;
        public string FullSnapshotProducerName => "Quests";

        public bool IsGameplayReady(FusionNetworkIdentity identity)
        {
            if (!isActiveAndEnabled || identity == null || identity.NetworkId == 0 ||
                !identity.TransportAdmitted || m_BoundBridge == null ||
                !m_BoundBridge.IsClient)
            {
                return false;
            }

            WireManager();
            RefreshControllerRegistry(force: true);
            if (!m_ManagerInitialized || m_WiredManager == null ||
                m_WiredManager != GetManager())
            {
                return false;
            }

            NetworkQuestsController relevant =
                identity.GetComponentInChildren<NetworkQuestsController>(true);
            if (relevant == null) return true;

            return m_RegisteredControllers.TryGetValue(
                       identity.NetworkId, out NetworkQuestsController registered) &&
                   registered == relevant && m_WiredManager.GetController(identity.NetworkId) == relevant;
        }

        public void Configure(FusionTransportBridge transportBridge)
        {
            if (m_TransportBridge == transportBridge) return;
            m_TransportBridge = transportBridge;
            if (isActiveAndEnabled) TryBindTransport(force: true);
        }

        private void OnEnable()
        {
            TryBindTransport(force: true);
            WireManager();
            RefreshControllerRegistry(force: true);
        }

        private void Start()
        {
            TryBindTransport(force: false);
            WireManager();
            RefreshControllerRegistry(force: true);
        }

        private void Update()
        {
            TryBindTransport(force: false);
            WireManager();

            if (!m_AutoRegisterSceneControllers || Time.unscaledTime < m_NextControllerScanTime) return;
            m_NextControllerScanTime = Time.unscaledTime + Mathf.Max(0.05f, m_ControllerScanInterval);
            RefreshControllerRegistry(force: false);
        }

        private void OnDisable()
        {
            UnbindTransport();
            UnwireManager();
            NetworkQuestsManager manager = GetManager();
            foreach (uint networkId in m_RegisteredControllers.Keys) manager?.UnregisterController(networkId);
            m_RegisteredControllers.Clear();
        }

        private void TryBindTransport(bool force)
        {
            FusionTransportBridge candidate = m_TransportBridge;
            if (candidate == null) candidate = NetworkTransportBridge.Active as FusionTransportBridge;
            if (candidate == null) candidate = FindFirstObjectByType<FusionTransportBridge>();

            if (!force && candidate == m_BoundBridge) return;
            if (candidate == m_BoundBridge) return;

            UnbindTransport();
            if (candidate == null) return;
            if (!candidate.RegisterModuleHandler(ModuleId, HandleModuleMessage))
            {
                Log("module handler registration was rejected; another Quests bridge is already bound");
                return;
            }

            m_TransportBridge = candidate;
            m_BoundBridge = candidate;
            if (!candidate.RegisterFullSnapshotProducer(this))
            {
                candidate.UnregisterModuleHandler(ModuleId, HandleModuleMessage);
                m_BoundBridge = null;
                Log("full snapshot producer registration was rejected");
                return;
            }
            candidate.AuthorityChanged += HandleAuthorityChanged;
            WireManager();
        }

        private void UnbindTransport()
        {
            if (m_BoundBridge == null) return;
            m_BoundBridge.UnregisterFullSnapshotProducer(this);
            m_BoundBridge.AuthorityChanged -= HandleAuthorityChanged;
            m_BoundBridge.UnregisterModuleHandler(ModuleId, HandleModuleMessage);
            m_BoundBridge = null;
        }

        public FusionFullSnapshotResult ProduceFullSnapshot(FusionFullSnapshotContext context)
        {
            if (context == null || context.TransportBridge != m_BoundBridge ||
                context.ModuleId != ModuleId || m_BoundBridge == null || !m_BoundBridge.IsServer)
            {
                return context != null
                    ? context.Fail("Quests bridge is not bound as the current authority.")
                    : default;
            }

            WireManager(forceRoleRefresh: true);
            RefreshControllerRegistry(force: true);
            NetworkQuestsManager manager = GetManager();
            if (manager == null || manager != m_WiredManager || !m_ManagerInitialized)
                return context.Fail("NetworkQuestsManager is unavailable or not initialized.");
            manager.SendAllSnapshotsToClient(context.ClientId);
            return context.Complete();
        }

        private void HandleAuthorityChanged(bool isAuthority, uint authorityEpoch)
        {
            WireManager(forceRoleRefresh: true);
            RefreshControllerRegistry(force: true);
            RefreshRegisteredControllerRoles();
            Log("authority changed server=" + isAuthority + " epoch=" + authorityEpoch);
        }

        private void RefreshRegisteredControllerRoles()
        {
            bool isServer = m_BoundBridge != null && m_BoundBridge.IsServer;
            foreach (NetworkQuestsController controller in m_RegisteredControllers.Values)
            {
                if (controller == null) continue;
                NetworkCharacter character = controller.GetComponent<NetworkCharacter>();
                if (character == null) continue;
                controller.Initialize(isServer, character.IsOwnerInstance);
            }
        }

        private void WireManager(bool forceRoleRefresh = false)
        {
            NetworkQuestsManager manager = GetManager();
            if (manager == null) return;

            if (m_WiredManager != manager)
            {
                UnwireManager();
                m_WiredManager = manager;
            }

            manager.OnSendQuestRequest -= SendRequestToAuthority;
            manager.OnSendQuestRequest += SendRequestToAuthority;
            manager.OnSendQuestResponse -= SendResponseToClient;
            manager.OnSendQuestResponse += SendResponseToClient;
            manager.OnBroadcastQuestChange -= BroadcastChange;
            manager.OnBroadcastQuestChange += BroadcastChange;
            manager.OnBroadcastFullSnapshot -= BroadcastSnapshot;
            manager.OnBroadcastFullSnapshot += BroadcastSnapshot;
            manager.OnSendSnapshotToClient -= SendSnapshotToClient;
            manager.OnSendSnapshotToClient += SendSnapshotToClient;

            bool isServer = m_BoundBridge != null && m_BoundBridge.IsServer;
            if (forceRoleRefresh || !m_ManagerInitialized || isServer != m_LastServer)
            {
                manager.IsServer = isServer;
                m_ManagerInitialized = true;
                m_LastServer = isServer;
            }
        }

        private void UnwireManager()
        {
            NetworkQuestsManager manager = m_WiredManager;
            if (manager != null)
            {
                manager.OnSendQuestRequest -= SendRequestToAuthority;
                manager.OnSendQuestResponse -= SendResponseToClient;
                manager.OnBroadcastQuestChange -= BroadcastChange;
                manager.OnBroadcastFullSnapshot -= BroadcastSnapshot;
                manager.OnSendSnapshotToClient -= SendSnapshotToClient;
            }

            m_WiredManager = null;
            m_ManagerInitialized = false;
        }

        private void RefreshControllerRegistry(bool force)
        {
            NetworkQuestsManager manager = GetManager();
            if (manager == null) return;
            PruneControllerRegistry(manager);
            if (!m_AutoRegisterSceneControllers && !force) return;

            NetworkQuestsController[] controllers = FindObjectsByType<NetworkQuestsController>(
                FindObjectsInactive.Exclude,
                FindObjectsSortMode.None);
            for (int i = 0; i < controllers.Length; i++) RegisterController(manager, controllers[i]);
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

            bool isServer = m_BoundBridge != null && m_BoundBridge.IsServer;
            controller.Initialize(isServer, networkCharacter.IsOwnerInstance);
            manager.RegisterController(networkId, controller);
            m_RegisteredControllers[networkId] = controller;
        }

        private void PruneControllerRegistry(NetworkQuestsManager manager)
        {
            m_RemoveBuffer.Clear();
            foreach (KeyValuePair<uint, NetworkQuestsController> pair in m_RegisteredControllers)
            {
                NetworkQuestsController controller = pair.Value;
                NetworkCharacter character = controller != null
                    ? controller.GetComponent<NetworkCharacter>()
                    : null;
                if (controller == null || character == null || character.NetworkId != pair.Key)
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

        private void SendRequestToAuthority(NetworkQuestRequest request)
        {
            if (m_BoundBridge == null || !m_BoundBridge.IsClient) return;
            byte[] payload = FusionValueCodec.Encode(request, (writer, value) => writer.Write(value));
            m_BoundBridge.SendModuleToAuthority(ModuleId, (ushort)MessageType.Request, payload);
        }

        private void SendResponseToClient(uint clientId, NetworkQuestResponse response)
        {
            if (m_BoundBridge == null || !m_BoundBridge.IsServer) return;
            byte[] payload = FusionValueCodec.Encode(response, (writer, value) => writer.Write(value));
            m_BoundBridge.SendModuleToClient(clientId, ModuleId, (ushort)MessageType.Response, payload);
        }

        private void BroadcastChange(NetworkQuestBroadcast broadcast)
        {
            if (m_BoundBridge == null || !m_BoundBridge.IsServer) return;
            byte[] payload = FusionValueCodec.Encode(broadcast, (writer, value) => writer.Write(value));
            m_BoundBridge.BroadcastModule(ModuleId, (ushort)MessageType.Broadcast, payload);
        }

        private void BroadcastSnapshot(NetworkQuestsSnapshot snapshot)
        {
            if (m_BoundBridge == null || !m_BoundBridge.IsServer) return;
            byte[] payload = FusionValueCodec.Encode(snapshot, (writer, value) => writer.Write(value));
            m_BoundBridge.BroadcastModule(ModuleId, (ushort)MessageType.Snapshot, payload);
        }

        private void SendSnapshotToClient(ulong rawClientId, NetworkQuestsSnapshot snapshot)
        {
            if (m_BoundBridge == null || !m_BoundBridge.IsServer) return;
            if (!NetworkTransportBridge.TryConvertSenderClientId(rawClientId, out uint clientId)) return;
            byte[] payload = FusionValueCodec.Encode(snapshot, (writer, value) => writer.Write(value));
            m_BoundBridge.SendModuleToClient(clientId, ModuleId, (ushort)MessageType.Snapshot, payload);
        }

        private void HandleModuleMessage(FusionModuleMessage message)
        {
            switch ((MessageType)message.MessageType)
            {
                case MessageType.Request:
                    if (message.FromAuthority || m_BoundBridge == null || !m_BoundBridge.IsServer) return;
                    if (!FusionValueCodec.TryDecode(
                            message.Payload,
                            (FusionValueReader reader, ref NetworkQuestRequest value) => reader.Read(ref value),
                            out NetworkQuestRequest request))
                    {
                        Log("dropped malformed request");
                        return;
                    }
                    RefreshControllerRegistry(force: true);
                    _ = GetManager()?.ReceiveQuestRequest(request, message.SenderClientId);
                    break;

                case MessageType.Response:
                    if (!message.FromAuthority) return;
                    if (!FusionValueCodec.TryDecode(
                            message.Payload,
                            (FusionValueReader reader, ref NetworkQuestResponse value) => reader.Read(ref value),
                            out NetworkQuestResponse response))
                    {
                        Log("dropped malformed response");
                        return;
                    }
                    GetManager()?.ReceiveQuestResponse(response, response.ActorNetworkId);
                    break;

                case MessageType.Broadcast:
                    if (!message.FromAuthority) return;
                    if (!FusionValueCodec.TryDecode(
                            message.Payload,
                            (FusionValueReader reader, ref NetworkQuestBroadcast value) => reader.Read(ref value),
                            out NetworkQuestBroadcast broadcast))
                    {
                        Log("dropped malformed broadcast");
                        return;
                    }
                    RefreshControllerRegistry(force: true);
                    GetManager()?.ReceiveQuestChangeBroadcast(broadcast);
                    break;

                case MessageType.Snapshot:
                    if (!message.FromAuthority) return;
                    if (!FusionValueCodec.TryDecode(
                            message.Payload,
                            (FusionValueReader reader, ref NetworkQuestsSnapshot value) => reader.Read(ref value),
                            out NetworkQuestsSnapshot snapshot))
                    {
                        Log("dropped malformed snapshot");
                        return;
                    }
                    RefreshControllerRegistry(force: true);
                    GetManager()?.ReceiveFullSnapshot(snapshot);
                    break;

                default:
                    Log("dropped unknown message type=" + message.MessageType);
                    break;
            }
        }

        private void Log(string message)
        {
            if (!m_LogNetworkMessages) return;
            Debug.Log("[FusionQuestsTransportBridge] " + message, this);
        }

        private static NetworkQuestsManager GetManager()
        {
            return NetworkQuestsManager.Instance != null
                ? NetworkQuestsManager.Instance
                : FindFirstObjectByType<NetworkQuestsManager>();
        }
    }
}
#endif
