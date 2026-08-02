#if GC2_DIALOGUE
using System;
using System.Collections.Generic;
using Arawn.GameCreator2.Networking.Transport.Fusion;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Dialogue.Transport.Fusion
{
    [DisallowMultipleComponent]
    [AddComponentMenu("Game Creator/Network/Transport/Fusion Dialogue Bridge")]
    [DefaultExecutionOrder(-338)]
    public sealed class FusionDialogueTransportBridge : MonoBehaviour,
        IFusionGameplayReadinessParticipant,
        IFusionFullSnapshotProducer
    {
        public const ushort ModuleId = 41;

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

        private readonly Dictionary<uint, NetworkDialogueController> m_RegisteredControllers = new(32);
        private readonly List<uint> m_RemoveBuffer = new(16);

        private FusionTransportBridge m_BoundBridge;
        private NetworkDialogueManager m_WiredManager;
        private bool m_ManagerInitialized;
        private bool m_LastServer;
        private float m_NextControllerScanTime;

        public string GameplayReadinessName => "Dialogue";
        public FusionTransportBridge GameplayReadinessTransport => m_BoundBridge;
        public ushort GameplayReadinessModuleId => ModuleId;
        public ushort FullSnapshotModuleId => ModuleId;
        public string FullSnapshotProducerName => "Dialogue";

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

            NetworkDialogueController relevant =
                identity.GetComponentInChildren<NetworkDialogueController>(true);
            if (relevant == null) return true;

            return m_RegisteredControllers.TryGetValue(
                       identity.NetworkId, out NetworkDialogueController registered) &&
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
            NetworkDialogueManager manager = GetManager();
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
                Log("module handler registration was rejected; another Dialogue bridge is already bound");
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
                    ? context.Fail("Dialogue bridge is not bound as the current authority.")
                    : default;
            }

            WireManager(forceRoleRefresh: true);
            RefreshControllerRegistry(force: true);
            NetworkDialogueManager manager = GetManager();
            if (manager == null || manager != m_WiredManager || !m_ManagerInitialized)
                return context.Fail("NetworkDialogueManager is unavailable or not initialized.");
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
            foreach (NetworkDialogueController controller in m_RegisteredControllers.Values)
            {
                if (controller == null) continue;
                InitializeControllerRole(controller, isServer);
            }
        }

        private void WireManager(bool forceRoleRefresh = false)
        {
            NetworkDialogueManager manager = GetManager();
            if (manager == null) return;

            if (m_WiredManager != manager)
            {
                UnwireManager();
                m_WiredManager = manager;
            }

            manager.OnSendDialogueRequest -= SendRequestToAuthority;
            manager.OnSendDialogueRequest += SendRequestToAuthority;
            manager.OnSendDialogueResponse -= SendResponseToClient;
            manager.OnSendDialogueResponse += SendResponseToClient;
            manager.OnBroadcastDialogueChange -= BroadcastChange;
            manager.OnBroadcastDialogueChange += BroadcastChange;
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
            NetworkDialogueManager manager = m_WiredManager;
            if (manager != null)
            {
                manager.OnSendDialogueRequest -= SendRequestToAuthority;
                manager.OnSendDialogueResponse -= SendResponseToClient;
                manager.OnBroadcastDialogueChange -= BroadcastChange;
                manager.OnBroadcastFullSnapshot -= BroadcastSnapshot;
                manager.OnSendSnapshotToClient -= SendSnapshotToClient;
            }

            m_WiredManager = null;
            m_ManagerInitialized = false;
        }

        private void RefreshControllerRegistry(bool force)
        {
            NetworkDialogueManager manager = GetManager();
            if (manager == null) return;
            PruneControllerRegistry(manager);
            if (!m_AutoRegisterSceneControllers && !force) return;

            NetworkDialogueController[] controllers = FindObjectsByType<NetworkDialogueController>(
                FindObjectsInactive.Exclude,
                FindObjectsSortMode.None);
            for (int i = 0; i < controllers.Length; i++) RegisterController(manager, controllers[i]);
        }

        private void RegisterController(NetworkDialogueManager manager, NetworkDialogueController controller)
        {
            if (manager == null || controller == null) return;
            uint networkId = controller.NetworkId;
            if (networkId == 0) return;
            bool isServer = m_BoundBridge != null && m_BoundBridge.IsServer;

            if (m_RegisteredControllers.TryGetValue(networkId, out NetworkDialogueController existing))
            {
                if (existing == controller)
                {
                    bool isLocalClient = IsControllerLocalClient(controller);
                    if (controller.IsServer != isServer ||
                        controller.IsLocalClient != isLocalClient)
                    {
                        controller.Initialize(isServer, isLocalClient);
                        manager.RegisterController(networkId, controller);
                    }
                    return;
                }
                manager.UnregisterController(networkId);
            }

            InitializeControllerRole(controller, isServer);
            manager.RegisterController(networkId, controller);
            m_RegisteredControllers[networkId] = controller;
        }

        private static void InitializeControllerRole(
            NetworkDialogueController controller,
            bool isServer)
        {
            if (controller == null) return;

            controller.Initialize(isServer, IsControllerLocalClient(controller));
        }

        private static bool IsControllerLocalClient(NetworkDialogueController controller)
        {
            NetworkCharacter networkCharacter = controller != null
                ? controller.NetworkCharacter
                : null;
            return networkCharacter != null
                ? networkCharacter.IsOwnerInstance
                : controller != null && !controller.RequiresTargetOwnership;
        }

        private void PruneControllerRegistry(NetworkDialogueManager manager)
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

            for (int i = 0; i < m_RemoveBuffer.Count; i++)
            {
                uint networkId = m_RemoveBuffer[i];
                manager.UnregisterController(networkId);
                m_RegisteredControllers.Remove(networkId);
            }
        }

        private void SendRequestToAuthority(NetworkDialogueRequest request)
        {
            if (m_BoundBridge == null || !m_BoundBridge.IsClient) return;
            byte[] payload = FusionValueCodec.Encode(request, (writer, value) => writer.Write(value));
            m_BoundBridge.SendModuleToAuthority(ModuleId, (ushort)MessageType.Request, payload);
        }

        private void SendResponseToClient(uint clientId, NetworkDialogueResponse response)
        {
            if (m_BoundBridge == null || !m_BoundBridge.IsServer) return;
            byte[] payload = FusionValueCodec.Encode(response, (writer, value) => writer.Write(value));
            m_BoundBridge.SendModuleToClient(clientId, ModuleId, (ushort)MessageType.Response, payload);
        }

        private void BroadcastChange(NetworkDialogueBroadcast broadcast)
        {
            if (m_BoundBridge == null || !m_BoundBridge.IsServer) return;
            byte[] payload = FusionValueCodec.Encode(broadcast, (writer, value) => writer.Write(value));
            m_BoundBridge.BroadcastModule(ModuleId, (ushort)MessageType.Broadcast, payload);
        }

        private void BroadcastSnapshot(NetworkDialogueSnapshot snapshot)
        {
            if (m_BoundBridge == null || !m_BoundBridge.IsServer) return;
            byte[] payload = FusionValueCodec.Encode(snapshot, (writer, value) => writer.Write(value));
            m_BoundBridge.BroadcastModule(ModuleId, (ushort)MessageType.Snapshot, payload);
        }

        private void SendSnapshotToClient(ulong rawClientId, NetworkDialogueSnapshot snapshot)
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
                            (FusionValueReader reader, ref NetworkDialogueRequest value) => reader.Read(ref value),
                            out NetworkDialogueRequest request))
                    {
                        Log("dropped malformed request");
                        return;
                    }
                    RefreshControllerRegistry(force: true);
                    _ = GetManager()?.ReceiveDialogueRequest(request, message.SenderClientId);
                    break;

                case MessageType.Response:
                    if (!message.FromAuthority) return;
                    if (!FusionValueCodec.TryDecode(
                            message.Payload,
                            (FusionValueReader reader, ref NetworkDialogueResponse value) => reader.Read(ref value),
                            out NetworkDialogueResponse response))
                    {
                        Log("dropped malformed response");
                        return;
                    }
                    GetManager()?.ReceiveDialogueResponse(response, response.TargetNetworkId);
                    break;

                case MessageType.Broadcast:
                    if (!message.FromAuthority) return;
                    if (!FusionValueCodec.TryDecode(
                            message.Payload,
                            (FusionValueReader reader, ref NetworkDialogueBroadcast value) => reader.Read(ref value),
                            out NetworkDialogueBroadcast broadcast))
                    {
                        Log("dropped malformed broadcast");
                        return;
                    }
                    RefreshControllerRegistry(force: true);
                    GetManager()?.ReceiveDialogueChangeBroadcast(broadcast);
                    break;

                case MessageType.Snapshot:
                    if (!message.FromAuthority) return;
                    if (!FusionValueCodec.TryDecode(
                            message.Payload,
                            (FusionValueReader reader, ref NetworkDialogueSnapshot value) => reader.Read(ref value),
                            out NetworkDialogueSnapshot snapshot))
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
            Debug.Log("[FusionDialogueTransportBridge] " + message, this);
        }

        private static NetworkDialogueManager GetManager()
        {
            return NetworkDialogueManager.Instance != null
                ? NetworkDialogueManager.Instance
                : FindFirstObjectByType<NetworkDialogueManager>();
        }
    }
}
#endif
