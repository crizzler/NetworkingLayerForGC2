#if GC2_TRAVERSAL
using System;
using System.Collections.Generic;
using Arawn.GameCreator2.Networking.Transport.Fusion;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Traversal.Transport.Fusion
{
    [DisallowMultipleComponent]
    [AddComponentMenu("Game Creator/Network/Transport/Fusion Traversal Bridge")]
    [DefaultExecutionOrder(-338)]
    public sealed class FusionTraversalTransportBridge : MonoBehaviour,
        IFusionGameplayReadinessParticipant,
        IFusionFullSnapshotProducer
    {
        public const ushort ModuleId = 50;

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

        private readonly Dictionary<uint, NetworkTraversalController> m_RegisteredControllers = new(32);
        private readonly List<uint> m_RemoveBuffer = new(16);

        private FusionTransportBridge m_BoundBridge;
        private NetworkTraversalManager m_WiredManager;
        private bool m_ManagerInitialized;
        private bool m_LastServer;
        private float m_NextControllerScanTime;

        public string GameplayReadinessName => "Traversal";
        public FusionTransportBridge GameplayReadinessTransport => m_BoundBridge;
        public ushort GameplayReadinessModuleId => ModuleId;
        public ushort FullSnapshotModuleId => ModuleId;
        public string FullSnapshotProducerName => "Traversal";

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

            NetworkTraversalController relevant =
                identity.GetComponentInChildren<NetworkTraversalController>(true);
            if (relevant == null) return true;

            return m_RegisteredControllers.TryGetValue(
                       identity.NetworkId, out NetworkTraversalController registered) &&
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
            NetworkTraversalManager manager = GetManager();
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
                Log("module handler registration was rejected; another Traversal bridge is already bound");
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
                    ? context.Fail("Traversal bridge is not bound as the current authority.")
                    : default;
            }

            WireManager(forceRoleRefresh: true);
            RefreshControllerRegistry(force: true);
            NetworkTraversalManager manager = GetManager();
            if (manager == null || manager != m_WiredManager || !m_ManagerInitialized)
                return context.Fail("NetworkTraversalManager is unavailable or not initialized.");
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
            foreach (NetworkTraversalController controller in m_RegisteredControllers.Values)
            {
                if (controller == null) continue;
                NetworkCharacter character = controller.GetComponent<NetworkCharacter>();
                if (character == null) continue;
                controller.Initialize(isServer, character.IsOwnerInstance);
            }
        }

        private void WireManager(bool forceRoleRefresh = false)
        {
            NetworkTraversalManager manager = GetManager();
            if (manager == null) return;

            if (m_WiredManager != manager)
            {
                UnwireManager();
                m_WiredManager = manager;
            }

            manager.OnSendTraversalRequest -= SendRequestToAuthority;
            manager.OnSendTraversalRequest += SendRequestToAuthority;
            manager.OnSendTraversalResponse -= SendResponseToClient;
            manager.OnSendTraversalResponse += SendResponseToClient;
            manager.OnBroadcastTraversalChange -= BroadcastChange;
            manager.OnBroadcastTraversalChange += BroadcastChange;
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
            NetworkTraversalManager manager = m_WiredManager;
            if (manager != null)
            {
                manager.OnSendTraversalRequest -= SendRequestToAuthority;
                manager.OnSendTraversalResponse -= SendResponseToClient;
                manager.OnBroadcastTraversalChange -= BroadcastChange;
                manager.OnBroadcastFullSnapshot -= BroadcastSnapshot;
                manager.OnSendSnapshotToClient -= SendSnapshotToClient;
            }

            m_WiredManager = null;
            m_ManagerInitialized = false;
        }

        private void RefreshControllerRegistry(bool force)
        {
            NetworkTraversalManager manager = GetManager();
            if (manager == null) return;
            PruneControllerRegistry(manager);
            if (!m_AutoRegisterSceneControllers && !force) return;

            NetworkTraversalController[] controllers = FindObjectsByType<NetworkTraversalController>(
                FindObjectsInactive.Exclude,
                FindObjectsSortMode.None);
            for (int i = 0; i < controllers.Length; i++) RegisterController(manager, controllers[i]);
        }

        private void RegisterController(NetworkTraversalManager manager, NetworkTraversalController controller)
        {
            if (manager == null || controller == null) return;
            NetworkCharacter networkCharacter = controller.GetComponent<NetworkCharacter>();
            if (networkCharacter == null || networkCharacter.NetworkId == 0) return;

            uint networkId = networkCharacter.NetworkId;
            if (m_RegisteredControllers.TryGetValue(networkId, out NetworkTraversalController existing))
            {
                if (existing == controller) return;
                manager.UnregisterController(networkId);
            }

            bool isServer = m_BoundBridge != null && m_BoundBridge.IsServer;
            controller.Initialize(isServer, networkCharacter.IsOwnerInstance);
            manager.RegisterController(networkId, controller);
            m_RegisteredControllers[networkId] = controller;
        }

        private void PruneControllerRegistry(NetworkTraversalManager manager)
        {
            m_RemoveBuffer.Clear();
            foreach (KeyValuePair<uint, NetworkTraversalController> pair in m_RegisteredControllers)
            {
                NetworkTraversalController controller = pair.Value;
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

        private void SendRequestToAuthority(NetworkTraversalRequest request)
        {
            if (m_BoundBridge == null || !m_BoundBridge.IsClient) return;
            byte[] payload = FusionValueCodec.Encode(request, (writer, value) => writer.Write(value));
            m_BoundBridge.SendModuleToAuthority(ModuleId, (ushort)MessageType.Request, payload);
        }

        private void SendResponseToClient(uint clientId, NetworkTraversalResponse response)
        {
            if (m_BoundBridge == null || !m_BoundBridge.IsServer) return;
            byte[] payload = FusionValueCodec.Encode(response, (writer, value) => writer.Write(value));
            m_BoundBridge.SendModuleToClient(clientId, ModuleId, (ushort)MessageType.Response, payload);
        }

        private void BroadcastChange(NetworkTraversalBroadcast broadcast)
        {
            if (m_BoundBridge == null || !m_BoundBridge.IsServer) return;
            byte[] payload = FusionValueCodec.Encode(broadcast, (writer, value) => writer.Write(value));
            m_BoundBridge.BroadcastModule(ModuleId, (ushort)MessageType.Broadcast, payload);
        }

        private void BroadcastSnapshot(NetworkTraversalSnapshot snapshot)
        {
            if (m_BoundBridge == null || !m_BoundBridge.IsServer) return;
            byte[] payload = FusionValueCodec.Encode(snapshot, (writer, value) => writer.Write(value));
            m_BoundBridge.BroadcastModule(ModuleId, (ushort)MessageType.Snapshot, payload);
        }

        private void SendSnapshotToClient(ulong rawClientId, NetworkTraversalSnapshot snapshot)
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
                            (FusionValueReader reader, ref NetworkTraversalRequest value) => reader.Read(ref value),
                            out NetworkTraversalRequest request))
                    {
                        Log("dropped malformed request");
                        return;
                    }
                    RefreshControllerRegistry(force: true);
                    _ = GetManager()?.ReceiveTraversalRequest(request, message.SenderClientId);
                    break;

                case MessageType.Response:
                    if (!message.FromAuthority) return;
                    if (!FusionValueCodec.TryDecode(
                            message.Payload,
                            (FusionValueReader reader, ref NetworkTraversalResponse value) => reader.Read(ref value),
                            out NetworkTraversalResponse response))
                    {
                        Log("dropped malformed response");
                        return;
                    }
                    GetManager()?.ReceiveTraversalResponse(response, response.ActorNetworkId);
                    break;

                case MessageType.Broadcast:
                    if (!message.FromAuthority) return;
                    if (!FusionValueCodec.TryDecode(
                            message.Payload,
                            (FusionValueReader reader, ref NetworkTraversalBroadcast value) => reader.Read(ref value),
                            out NetworkTraversalBroadcast broadcast))
                    {
                        Log("dropped malformed broadcast");
                        return;
                    }
                    RefreshControllerRegistry(force: true);
                    GetManager()?.ReceiveTraversalChangeBroadcast(broadcast);
                    break;

                case MessageType.Snapshot:
                    if (!message.FromAuthority) return;
                    if (!FusionValueCodec.TryDecode(
                            message.Payload,
                            (FusionValueReader reader, ref NetworkTraversalSnapshot value) => reader.Read(ref value),
                            out NetworkTraversalSnapshot snapshot))
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
            Debug.Log("[FusionTraversalTransportBridge] " + message, this);
        }

        private static NetworkTraversalManager GetManager()
        {
            return NetworkTraversalManager.Instance != null
                ? NetworkTraversalManager.Instance
                : FindFirstObjectByType<NetworkTraversalManager>();
        }
    }
}
#endif
