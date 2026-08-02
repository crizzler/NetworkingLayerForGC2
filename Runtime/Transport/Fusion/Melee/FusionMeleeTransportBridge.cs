#if GC2_MELEE
using System;
using System.Collections.Generic;
using Arawn.GameCreator2.Networking.Transport.Fusion;
using GameCreator.Runtime.Melee;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Melee.Transport.Fusion
{
    [DisallowMultipleComponent]
    [AddComponentMenu("Game Creator/Network/Transport/Fusion Melee Bridge")]
    [DefaultExecutionOrder(-340)]
    public sealed class FusionMeleeTransportBridge : MonoBehaviour,
        IFusionGameplayReadinessParticipant,
        IFusionFullSnapshotProducer
    {
        public const ushort ModuleId = 30;

        private enum MessageType : ushort
        {
            HitRequest = 1,
            HitResponse = 2,
            HitBroadcast = 3,
            BlockRequest = 4,
            BlockResponse = 5,
            BlockBroadcast = 6,
            SkillRequest = 7,
            SkillResponse = 8,
            SkillBroadcast = 9,
            ChargeRequest = 10,
            ChargeResponse = 11,
            ChargeBroadcast = 12,
            ReactionBroadcast = 13,
            WeaponState = 14,
            CharacterSnapshot = 15
        }

        [Header("Fusion")]
        [SerializeField] private FusionTransportBridge m_TransportBridge;

        [Header("Melee Assets")]
        [SerializeField] private MeleeWeapon[] m_RegisterWeapons = Array.Empty<MeleeWeapon>();

        [Header("Controllers")]
        [SerializeField] private bool m_AutoRegisterSceneControllers = true;
        [Min(0.05f)]
        [SerializeField] private float m_ControllerScanInterval = 0.25f;

        [Header("Debug")]
        [SerializeField] private bool m_LogNetworkMessages;

        private readonly Dictionary<uint, NetworkMeleeController> m_RegisteredControllers = new(32);
        private readonly List<uint> m_RemoveBuffer = new(16);

        private FusionTransportBridge m_BoundBridge;
        private NetworkMeleeManager m_WiredManager;
        private bool m_ManagerInitialized;
        private bool m_LastServer;
        private bool m_LastClient;
        private bool m_AssetsRegistered;
        private float m_NextControllerScanTime;

        public string GameplayReadinessName => "Melee";
        public FusionTransportBridge GameplayReadinessTransport => m_BoundBridge;
        public ushort GameplayReadinessModuleId => ModuleId;
        public ushort FullSnapshotModuleId => ModuleId;
        public string FullSnapshotProducerName => "Melee";

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

            NetworkMeleeController relevant =
                identity.GetComponentInChildren<NetworkMeleeController>(true);
            if (relevant == null) return true;

            return m_RegisteredControllers.TryGetValue(
                       identity.NetworkId, out NetworkMeleeController registered) &&
                   registered == relevant;
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
            RegisterConfiguredAssets();
            RefreshControllerRegistry(force: true);
        }

        private void Start()
        {
            TryBindTransport(force: false);
            WireManager();
            RegisterConfiguredAssets();
            RefreshControllerRegistry(force: true);
        }

        private void Update()
        {
            TryBindTransport(force: false);
            WireManager();
            RegisterConfiguredAssets();

            if (!m_AutoRegisterSceneControllers || Time.unscaledTime < m_NextControllerScanTime) return;
            m_NextControllerScanTime = Time.unscaledTime + Mathf.Max(0.05f, m_ControllerScanInterval);
            RefreshControllerRegistry(force: false);
        }

        private void OnDisable()
        {
            UnbindTransport();
            UnwireManager();
            UnregisterAllControllers();
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
                Log("module handler registration was rejected; another Melee bridge is already bound");
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
            WireManager(forceRoleRefresh: true);
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
                    ? context.Fail("Melee bridge is not bound as the current authority.")
                    : default;
            }

            WireManager(forceRoleRefresh: true);
            NetworkMeleeManager manager = GetManager();
            if (manager == null || manager != m_WiredManager || !m_ManagerInitialized)
                return context.Fail("NetworkMeleeManager is unavailable or not initialized.");

            RefreshControllerRegistry(force: true);
            NetworkMeleeCharacterSnapshot[] snapshots = manager.CaptureCharacterSnapshots();
            if (snapshots == null)
                return context.Fail("NetworkMeleeManager returned a null snapshot collection.");
            for (int i = 0; i < snapshots.Length; i++)
            {
                if (!SendCharacterSnapshot(context.ClientId, snapshots[i]))
                    return context.Fail($"Could not enqueue Melee snapshot {i}.");
            }
            return context.Complete();
        }

        private void HandleAuthorityChanged(bool isAuthority, uint authorityEpoch)
        {
            WireManager(forceRoleRefresh: true);
            RefreshControllerRegistry(force: true);
            Log("authority changed server=" + isAuthority + " epoch=" + authorityEpoch);
        }

        private void WireManager(bool forceRoleRefresh = false)
        {
            NetworkMeleeManager manager = GetManager();
            if (manager == null) return;
            if (m_WiredManager != manager)
            {
                UnwireManager();
                m_WiredManager = manager;
            }

            manager.SendHitRequestToServer -= SendHitRequest;
            manager.SendHitRequestToServer += SendHitRequest;
            manager.SendHitResponseToClient -= SendHitResponse;
            manager.SendHitResponseToClient += SendHitResponse;
            manager.BroadcastHitToAllClients -= BroadcastHit;
            manager.BroadcastHitToAllClients += BroadcastHit;

            manager.SendBlockRequestToServer -= SendBlockRequest;
            manager.SendBlockRequestToServer += SendBlockRequest;
            manager.SendBlockResponseToClient -= SendBlockResponse;
            manager.SendBlockResponseToClient += SendBlockResponse;
            manager.BroadcastBlockToAllClients -= BroadcastBlock;
            manager.BroadcastBlockToAllClients += BroadcastBlock;

            manager.SendSkillRequestToServer -= SendSkillRequest;
            manager.SendSkillRequestToServer += SendSkillRequest;
            manager.SendSkillResponseToClient -= SendSkillResponse;
            manager.SendSkillResponseToClient += SendSkillResponse;
            manager.BroadcastSkillToAllClients -= BroadcastSkill;
            manager.BroadcastSkillToAllClients += BroadcastSkill;

            manager.SendChargeRequestToServer -= SendChargeRequest;
            manager.SendChargeRequestToServer += SendChargeRequest;
            manager.SendChargeResponseToClient -= SendChargeResponse;
            manager.SendChargeResponseToClient += SendChargeResponse;
            manager.BroadcastChargeToAllClients -= BroadcastCharge;
            manager.BroadcastChargeToAllClients += BroadcastCharge;

            manager.BroadcastReactionToAllClients -= BroadcastReaction;
            manager.BroadcastReactionToAllClients += BroadcastReaction;
            manager.GetCharacterByNetworkIdFunc = ResolveNetworkCharacter;
            manager.GetNetworkTimeFunc = GetNetworkTime;

            bool isServer = m_BoundBridge != null && m_BoundBridge.IsServer;
            bool isClient = m_BoundBridge != null && m_BoundBridge.IsClient;
            if (forceRoleRefresh || !m_ManagerInitialized ||
                isServer != m_LastServer || isClient != m_LastClient)
            {
                manager.Initialize(isServer, isClient);
                m_ManagerInitialized = true;
                m_LastServer = isServer;
                m_LastClient = isClient;
            }
        }

        private void UnwireManager()
        {
            NetworkMeleeManager manager = m_WiredManager;
            if (manager != null)
            {
                manager.SendHitRequestToServer -= SendHitRequest;
                manager.SendHitResponseToClient -= SendHitResponse;
                manager.BroadcastHitToAllClients -= BroadcastHit;
                manager.SendBlockRequestToServer -= SendBlockRequest;
                manager.SendBlockResponseToClient -= SendBlockResponse;
                manager.BroadcastBlockToAllClients -= BroadcastBlock;
                manager.SendSkillRequestToServer -= SendSkillRequest;
                manager.SendSkillResponseToClient -= SendSkillResponse;
                manager.BroadcastSkillToAllClients -= BroadcastSkill;
                manager.SendChargeRequestToServer -= SendChargeRequest;
                manager.SendChargeResponseToClient -= SendChargeResponse;
                manager.BroadcastChargeToAllClients -= BroadcastCharge;
                manager.BroadcastReactionToAllClients -= BroadcastReaction;

                if (ReferenceEquals(manager.GetCharacterByNetworkIdFunc?.Target, this))
                    manager.GetCharacterByNetworkIdFunc = null;
                if (ReferenceEquals(manager.GetNetworkTimeFunc?.Target, this))
                    manager.GetNetworkTimeFunc = null;
            }

            m_WiredManager = null;
            m_ManagerInitialized = false;
        }

        private void RegisterConfiguredAssets()
        {
            if (m_AssetsRegistered) return;
            if (m_RegisterWeapons != null)
            {
                for (int i = 0; i < m_RegisterWeapons.Length; i++)
                {
                    RegisterWeaponAndSkills(m_RegisterWeapons[i]);
                }
            }
            m_AssetsRegistered = true;
        }

        private static void RegisterWeaponAndSkills(MeleeWeapon weapon)
        {
            if (weapon == null) return;
            NetworkMeleeManager.RegisterMeleeWeapon(weapon);
            RegisterComboSkills(weapon.Combo);
        }

        private static void RegisterComboSkills(ComboTree comboTree)
        {
            if (comboTree == null) return;
            var visited = new HashSet<int>();
            int[] rootIds = comboTree.RootIds;
            for (int i = 0; i < rootIds.Length; i++) RegisterComboNode(comboTree, rootIds[i], visited);
        }

        private static void RegisterComboNode(ComboTree comboTree, int nodeId, HashSet<int> visited)
        {
            if (nodeId == ComboTree.NODE_INVALID || !visited.Add(nodeId)) return;
            ComboItem item = comboTree.Get(nodeId);
            if (item?.Skill != null) NetworkMeleeManager.RegisterSkill(item.Skill);
            List<int> children = comboTree.Children(nodeId);
            for (int i = 0; i < children.Count; i++) RegisterComboNode(comboTree, children[i], visited);
        }

        private void RefreshControllerRegistry(bool force)
        {
            NetworkMeleeManager manager = GetManager();
            if (manager == null) return;
            PruneControllerRegistry(manager);
            if (!m_AutoRegisterSceneControllers && !force) return;

            NetworkMeleeController[] controllers = FindObjectsByType<NetworkMeleeController>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            for (int i = 0; i < controllers.Length; i++) RegisterController(manager, controllers[i]);
        }

        private void RegisterController(NetworkMeleeManager manager, NetworkMeleeController controller)
        {
            if (manager == null || controller == null) return;
            NetworkCharacter character = controller.GetComponent<NetworkCharacter>();
            if (character == null || character.NetworkId == 0 ||
                character.Role == NetworkCharacter.NetworkRole.None) return;

            bool isServer = character.IsServerInstance;
            bool isLocalClient = character.IsOwnerInstance &&
                                 m_BoundBridge != null && m_BoundBridge.IsClient;
            uint networkId = character.NetworkId;

            if (m_RegisteredControllers.TryGetValue(networkId, out NetworkMeleeController existing))
            {
                if (existing == controller)
                {
                    bool roleChanged = controller.IsServer != isServer ||
                                       controller.IsLocalClient != isLocalClient;
                    if (roleChanged) controller.Initialize(isServer, isLocalClient);
                    controller.RegisterCurrentMeleeAssets();
                    controller.OnWeaponStateChangedWithSender -= HandleWeaponStateChanged;
                    controller.OnWeaponStateChangedWithSender += HandleWeaponStateChanged;
                    if (roleChanged) controller.PublishCurrentWeaponState();
                    return;
                }

                if (existing != null)
                    existing.OnWeaponStateChangedWithSender -= HandleWeaponStateChanged;
                manager.UnregisterController(networkId);
            }

            controller.Initialize(isServer, isLocalClient);
            controller.RegisterCurrentMeleeAssets();
            controller.OnWeaponStateChangedWithSender -= HandleWeaponStateChanged;
            controller.OnWeaponStateChangedWithSender += HandleWeaponStateChanged;
            manager.RegisterController(networkId, controller);
            m_RegisteredControllers[networkId] = controller;
            controller.PublishCurrentWeaponState();
        }

        private void PruneControllerRegistry(NetworkMeleeManager manager)
        {
            m_RemoveBuffer.Clear();
            foreach (KeyValuePair<uint, NetworkMeleeController> pair in m_RegisteredControllers)
            {
                NetworkMeleeController controller = pair.Value;
                NetworkCharacter character = controller != null
                    ? controller.GetComponent<NetworkCharacter>()
                    : null;
                if (controller == null || character == null ||
                    character.NetworkId != pair.Key ||
                    character.Role == NetworkCharacter.NetworkRole.None)
                {
                    m_RemoveBuffer.Add(pair.Key);
                }
            }

            for (int i = 0; i < m_RemoveBuffer.Count; i++)
            {
                uint networkId = m_RemoveBuffer[i];
                if (m_RegisteredControllers.TryGetValue(networkId, out NetworkMeleeController controller) &&
                    controller != null)
                {
                    controller.OnWeaponStateChangedWithSender -= HandleWeaponStateChanged;
                }
                manager.UnregisterController(networkId);
                m_RegisteredControllers.Remove(networkId);
            }
        }

        private void UnregisterAllControllers()
        {
            NetworkMeleeManager manager = GetManager();
            foreach (KeyValuePair<uint, NetworkMeleeController> pair in m_RegisteredControllers)
            {
                if (pair.Value != null)
                    pair.Value.OnWeaponStateChangedWithSender -= HandleWeaponStateChanged;
                manager?.UnregisterController(pair.Key);
            }
            m_RegisteredControllers.Clear();
        }

        private void HandleWeaponStateChanged(
            NetworkMeleeController controller,
            NetworkMeleeWeaponState state)
        {
            if (controller == null || !controller.IsLocalClient) return;
            uint networkId = controller.GetComponent<NetworkCharacter>()?.NetworkId ?? 0;
            if (networkId != 0) SendWeaponState(networkId, state);
        }

        private void SendHitRequest(NetworkMeleeHitRequest value) =>
            SendToAuthority(MessageType.HitRequest, value, (writer, item) => writer.Write(item));
        private void SendBlockRequest(NetworkBlockRequest value) =>
            SendToAuthority(MessageType.BlockRequest, value, (writer, item) => writer.Write(item));
        private void SendSkillRequest(NetworkSkillRequest value) =>
            SendToAuthority(MessageType.SkillRequest, value, (writer, item) => writer.Write(item));
        private void SendChargeRequest(NetworkChargeRequest value) =>
            SendToAuthority(MessageType.ChargeRequest, value, (writer, item) => writer.Write(item));

        private void SendHitResponse(uint clientId, NetworkMeleeHitResponse value) =>
            SendToClient(clientId, MessageType.HitResponse, value, (writer, item) => writer.Write(item));
        private void SendBlockResponse(uint clientId, NetworkBlockResponse value) =>
            SendToClient(clientId, MessageType.BlockResponse, value, (writer, item) => writer.Write(item));
        private void SendSkillResponse(uint clientId, NetworkSkillResponse value) =>
            SendToClient(clientId, MessageType.SkillResponse, value, (writer, item) => writer.Write(item));
        private void SendChargeResponse(uint clientId, NetworkChargeResponse value) =>
            SendToClient(clientId, MessageType.ChargeResponse, value, (writer, item) => writer.Write(item));

        private void BroadcastHit(NetworkMeleeHitBroadcast value) =>
            Broadcast(MessageType.HitBroadcast, value, (writer, item) => writer.Write(item));
        private void BroadcastBlock(NetworkBlockBroadcast value) =>
            Broadcast(MessageType.BlockBroadcast, value, (writer, item) => writer.Write(item));
        private void BroadcastSkill(NetworkSkillBroadcast value) =>
            Broadcast(MessageType.SkillBroadcast, value, (writer, item) => writer.Write(item));
        private void BroadcastCharge(NetworkChargeBroadcast value) =>
            Broadcast(MessageType.ChargeBroadcast, value, (writer, item) => writer.Write(item));
        private void BroadcastReaction(NetworkReactionBroadcast value) =>
            Broadcast(MessageType.ReactionBroadcast, value, (writer, item) => writer.Write(item));

        private void SendToAuthority<T>(
            MessageType type,
            T value,
            Action<FusionValueWriter, T> write)
        {
            if (m_BoundBridge == null || !m_BoundBridge.IsClient) return;
            m_BoundBridge.SendModuleToAuthority(
                ModuleId, (ushort)type, FusionValueCodec.Encode(value, write));
        }

        private bool SendToClient<T>(
            uint clientId,
            MessageType type,
            T value,
            Action<FusionValueWriter, T> write)
        {
            if (m_BoundBridge == null || !m_BoundBridge.IsServer) return false;
            return m_BoundBridge.SendModuleToClient(
                clientId, ModuleId, (ushort)type, FusionValueCodec.Encode(value, write));
        }

        private void Broadcast<T>(
            MessageType type,
            T value,
            Action<FusionValueWriter, T> write)
        {
            if (m_BoundBridge == null || !m_BoundBridge.IsServer) return;
            m_BoundBridge.BroadcastModule(
                ModuleId, (ushort)type, FusionValueCodec.Encode(value, write));
        }

        private void SendWeaponState(uint characterNetworkId, NetworkMeleeWeaponState state)
        {
            if (m_BoundBridge == null || !m_BoundBridge.IsClient || characterNetworkId == 0) return;
            var writer = new FusionValueWriter();
            writer.Write(characterNetworkId);
            writer.Write(state);
            m_BoundBridge.SendModuleToAuthority(
                ModuleId,
                (ushort)MessageType.WeaponState,
                writer.ToArray(),
                reliable: false);
        }

        private void BroadcastWeaponState(uint characterNetworkId, NetworkMeleeWeaponState state)
        {
            if (m_BoundBridge == null || !m_BoundBridge.IsServer || characterNetworkId == 0) return;
            var writer = new FusionValueWriter();
            writer.Write(characterNetworkId);
            writer.Write(state);
            m_BoundBridge.BroadcastModule(
                ModuleId,
                (ushort)MessageType.WeaponState,
                writer.ToArray(),
                reliable: false);
        }

        private bool SendCharacterSnapshot(uint clientId, NetworkMeleeCharacterSnapshot snapshot)
        {
            return SendToClient(
                clientId,
                MessageType.CharacterSnapshot,
                snapshot,
                (writer, value) => writer.Write(value));
        }

        private void HandleModuleMessage(FusionModuleMessage message)
        {
            switch ((MessageType)message.MessageType)
            {
                case MessageType.HitRequest:
                    ReceiveRequest(message, (NetworkMeleeManager manager, uint sender, NetworkMeleeHitRequest value) =>
                        manager.ReceiveHitRequest(sender, value));
                    break;
                case MessageType.BlockRequest:
                    ReceiveRequest(message, (NetworkMeleeManager manager, uint sender, NetworkBlockRequest value) =>
                        manager.ReceiveBlockRequest(sender, value));
                    break;
                case MessageType.SkillRequest:
                    ReceiveRequest(message, (NetworkMeleeManager manager, uint sender, NetworkSkillRequest value) =>
                        manager.ReceiveSkillRequest(sender, value));
                    break;
                case MessageType.ChargeRequest:
                    ReceiveRequest(message, (NetworkMeleeManager manager, uint sender, NetworkChargeRequest value) =>
                        manager.ReceiveChargeRequest(sender, value));
                    break;
                case MessageType.HitResponse:
                    ReceiveAuthority(message, (NetworkMeleeManager manager, NetworkMeleeHitResponse value) =>
                        manager.ReceiveHitResponse(value));
                    break;
                case MessageType.BlockResponse:
                    ReceiveAuthority(message, (NetworkMeleeManager manager, NetworkBlockResponse value) =>
                        manager.ReceiveBlockResponse(value));
                    break;
                case MessageType.SkillResponse:
                    ReceiveAuthority(message, (NetworkMeleeManager manager, NetworkSkillResponse value) =>
                        manager.ReceiveSkillResponse(value));
                    break;
                case MessageType.ChargeResponse:
                    ReceiveAuthority(message, (NetworkMeleeManager manager, NetworkChargeResponse value) =>
                        manager.ReceiveChargeResponse(value));
                    break;
                case MessageType.HitBroadcast:
                    ReceiveAuthority(message, (NetworkMeleeManager manager, NetworkMeleeHitBroadcast value) =>
                        manager.ReceiveHitBroadcast(value));
                    break;
                case MessageType.BlockBroadcast:
                    ReceiveAuthority(message, (NetworkMeleeManager manager, NetworkBlockBroadcast value) =>
                        manager.ReceiveBlockBroadcast(value));
                    break;
                case MessageType.SkillBroadcast:
                    ReceiveAuthority(message, (NetworkMeleeManager manager, NetworkSkillBroadcast value) =>
                        manager.ReceiveSkillBroadcast(value));
                    break;
                case MessageType.ChargeBroadcast:
                    ReceiveAuthority(message, (NetworkMeleeManager manager, NetworkChargeBroadcast value) =>
                        manager.ReceiveChargeBroadcast(value));
                    break;
                case MessageType.ReactionBroadcast:
                    ReceiveAuthority(message, (NetworkMeleeManager manager, NetworkReactionBroadcast value) =>
                        manager.ReceiveReactionBroadcast(value));
                    break;
                case MessageType.WeaponState:
                    ReceiveWeaponState(message);
                    break;
                case MessageType.CharacterSnapshot:
                    ReceiveAuthority(message, (NetworkMeleeManager manager, NetworkMeleeCharacterSnapshot value) =>
                    {
                        RefreshControllerRegistry(force: true);
                        manager.ReceiveCharacterSnapshot(value);
                    });
                    break;
                default:
                    Log("dropped unknown message type=" + message.MessageType);
                    break;
            }
        }

        private delegate void RequestReceiver<T>(NetworkMeleeManager manager, uint sender, T value);
        private delegate void AuthorityReceiver<T>(NetworkMeleeManager manager, T value);

        private void ReceiveRequest<T>(FusionModuleMessage message, RequestReceiver<T> receive)
        {
            if (message.FromAuthority || m_BoundBridge == null || !m_BoundBridge.IsServer) return;
            NetworkMeleeManager manager = GetManager();
            if (manager == null) return;
            if (!FusionValueCodec.TryDecode(
                    message.Payload,
                    (FusionValueReader reader, ref T value) => reader.ReadDynamic(ref value),
                    out T decoded))
            {
                Log("dropped malformed request type=" + message.MessageType);
                return;
            }
            receive(manager, message.SenderClientId, decoded);
        }

        private void ReceiveAuthority<T>(
            FusionModuleMessage message,
            AuthorityReceiver<T> receive)
        {
            if (!message.FromAuthority) return;
            NetworkMeleeManager manager = GetManager();
            if (manager == null) return;
            if (!FusionValueCodec.TryDecode(
                    message.Payload,
                    (FusionValueReader reader, ref T value) => reader.ReadDynamic(ref value),
                    out T decoded))
            {
                Log("dropped malformed authority message type=" + message.MessageType);
                return;
            }
            receive(manager, decoded);
        }

        private void ReceiveWeaponState(FusionModuleMessage message)
        {
            var reader = new FusionValueReader(message.Payload);
            uint characterNetworkId = 0;
            NetworkMeleeWeaponState state = default;
            try
            {
                reader.Read(ref characterNetworkId);
                reader.Read(ref state);
                if (!reader.End || characterNetworkId == 0) return;
            }
            catch (Exception)
            {
                return;
            }

            NetworkMeleeManager manager = GetManager();
            if (!message.FromAuthority)
            {
                if (m_BoundBridge == null || !m_BoundBridge.IsServer) return;
                if (!IsAuthorizedStateSender(characterNetworkId, message.SenderClientId)) return;
                manager?.RecordAuthoritativeWeaponState(characterNetworkId, state);
                manager?.ReceiveWeaponState(characterNetworkId, state);
                BroadcastWeaponState(characterNetworkId, state);
                return;
            }

            RefreshControllerRegistry(force: true);
            manager?.ReceiveWeaponState(characterNetworkId, state);
        }

        private bool IsAuthorizedStateSender(uint characterNetworkId, uint senderClientId)
        {
            return m_BoundBridge != null &&
                   m_BoundBridge.TryGetCharacterOwner(characterNetworkId, out uint ownerClientId) &&
                   ownerClientId == senderClientId;
        }

        private NetworkCharacter ResolveNetworkCharacter(uint networkId)
        {
            if (m_BoundBridge != null)
            {
                GameCreator.Runtime.Characters.Character character =
                    m_BoundBridge.ResolveCharacter(networkId);
                if (character != null)
                {
                    NetworkCharacter networkCharacter = character.GetComponent<NetworkCharacter>();
                    if (networkCharacter != null) return networkCharacter;
                }
            }

            return m_RegisteredControllers.TryGetValue(networkId, out NetworkMeleeController controller) &&
                   controller != null
                ? controller.GetComponent<NetworkCharacter>()
                : null;
        }

        private float GetNetworkTime() =>
            m_BoundBridge != null ? m_BoundBridge.ServerTime : Time.time;

        private void Log(string message)
        {
            if (!m_LogNetworkMessages) return;
            Debug.Log("[FusionMeleeTransportBridge] " + message, this);
        }

        private static NetworkMeleeManager GetManager()
        {
            return NetworkMeleeManager.Instance != null
                ? NetworkMeleeManager.Instance
                : FindFirstObjectByType<NetworkMeleeManager>();
        }
    }

    internal static class FusionMeleeDynamicCodec
    {
        public static void ReadDynamic<T>(this FusionValueReader reader, ref T value)
        {
            object boxed = value;
            if (typeof(T) == typeof(NetworkMeleeHitRequest))
            {
                NetworkMeleeHitRequest typed = default; reader.Read(ref typed); boxed = typed;
            }
            else if (typeof(T) == typeof(NetworkBlockRequest))
            {
                NetworkBlockRequest typed = default; reader.Read(ref typed); boxed = typed;
            }
            else if (typeof(T) == typeof(NetworkSkillRequest))
            {
                NetworkSkillRequest typed = default; reader.Read(ref typed); boxed = typed;
            }
            else if (typeof(T) == typeof(NetworkChargeRequest))
            {
                NetworkChargeRequest typed = default; reader.Read(ref typed); boxed = typed;
            }
            else if (typeof(T) == typeof(NetworkMeleeHitResponse))
            {
                NetworkMeleeHitResponse typed = default; reader.Read(ref typed); boxed = typed;
            }
            else if (typeof(T) == typeof(NetworkBlockResponse))
            {
                NetworkBlockResponse typed = default; reader.Read(ref typed); boxed = typed;
            }
            else if (typeof(T) == typeof(NetworkSkillResponse))
            {
                NetworkSkillResponse typed = default; reader.Read(ref typed); boxed = typed;
            }
            else if (typeof(T) == typeof(NetworkChargeResponse))
            {
                NetworkChargeResponse typed = default; reader.Read(ref typed); boxed = typed;
            }
            else if (typeof(T) == typeof(NetworkMeleeHitBroadcast))
            {
                NetworkMeleeHitBroadcast typed = default; reader.Read(ref typed); boxed = typed;
            }
            else if (typeof(T) == typeof(NetworkBlockBroadcast))
            {
                NetworkBlockBroadcast typed = default; reader.Read(ref typed); boxed = typed;
            }
            else if (typeof(T) == typeof(NetworkSkillBroadcast))
            {
                NetworkSkillBroadcast typed = default; reader.Read(ref typed); boxed = typed;
            }
            else if (typeof(T) == typeof(NetworkChargeBroadcast))
            {
                NetworkChargeBroadcast typed = default; reader.Read(ref typed); boxed = typed;
            }
            else if (typeof(T) == typeof(NetworkReactionBroadcast))
            {
                NetworkReactionBroadcast typed = default; reader.Read(ref typed); boxed = typed;
            }
            else if (typeof(T) == typeof(NetworkMeleeCharacterSnapshot))
            {
                NetworkMeleeCharacterSnapshot typed = default; reader.Read(ref typed); boxed = typed;
            }
            else
            {
                throw new InvalidOperationException("Unsupported Melee payload type " + typeof(T).FullName);
            }
            value = (T)boxed;
        }
    }
}
#endif
