#if GC2_SHOOTER
using System;
using System.Collections.Generic;
using Arawn.GameCreator2.Networking.Transport.Fusion;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Shooter;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Shooter.Transport.Fusion
{
    [Serializable]
    public struct ShooterWeaponRegistration
    {
        public ShooterWeapon Weapon;
        public GameObject ModelPrefab;
        public Handle Handle;
    }

    [DisallowMultipleComponent]
    [AddComponentMenu("Game Creator/Network/Transport/Fusion Shooter Bridge")]
    [DefaultExecutionOrder(-330)]
    public sealed class FusionShooterTransportBridge : MonoBehaviour,
        IFusionGameplayReadinessParticipant,
        IFusionFullSnapshotProducer
    {
        public const ushort ModuleId = 31;
        private const int MaxPendingPersistentStates = 128;

        private enum MessageType : ushort
        {
            ShotRequest = 1,
            ShotResponse = 2,
            ShotBroadcast = 3,
            HitRequest = 4,
            HitResponse = 5,
            HitBroadcast = 6,
            ReloadRequest = 7,
            QuickReloadRequest = 8,
            ReloadResponse = 9,
            ReloadBroadcast = 10,
            FixJamRequest = 11,
            FixJamResponse = 12,
            JamBroadcast = 13,
            FixJamBroadcast = 14,
            ChargeStartRequest = 15,
            ChargeStartResponse = 16,
            ChargeCancelRequest = 17,
            ChargeBroadcast = 18,
            SightSwitchRequest = 19,
            SightSwitchResponse = 20,
            SightSwitchBroadcast = 21,
            WeaponState = 22,
            AimState = 23,
            CharacterSnapshot = 24,
            ImpactPropSnapshot = 25
        }

        [Header("Fusion")]
        [SerializeField] private FusionTransportBridge m_TransportBridge;

        [Header("Shooter Assets")]
        [SerializeField] private ShooterWeaponRegistration[] m_WeaponRegistrations =
            Array.Empty<ShooterWeaponRegistration>();

        [Header("Controllers")]
        [SerializeField] private bool m_AutoRegisterSceneControllers = true;
        [Min(0.05f)]
        [SerializeField] private float m_ControllerScanInterval = 0.25f;

        [Header("Debug")]
        [SerializeField] private bool m_LogDiagnostics;

        private readonly Dictionary<uint, NetworkShooterController> m_RegisteredControllers = new(32);
        private readonly List<uint> m_RemoveBuffer = new(16);
        private readonly Dictionary<int, ShooterAssetEntry> m_WeaponAssets = new(16);
        private readonly Dictionary<uint, NetworkWeaponState> m_LatestWeaponStates = new(32);
        private readonly Dictionary<uint, NetworkAimState> m_LatestAimStates = new(32);
        private readonly Dictionary<uint, NetworkWeaponState> m_PendingWeaponStates = new(32);
        private readonly Dictionary<uint, NetworkAimState> m_PendingAimStates = new(32);
        private readonly Dictionary<uint, NetworkShooterImpactPropSnapshot> m_PendingImpactSnapshots = new(32);

        private FusionTransportBridge m_BoundBridge;
        private NetworkShooterManager m_WiredManager;
        private bool m_ManagerInitialized;
        private bool m_LastServer;
        private bool m_LastClient;
        private bool m_AssetsRegistered;
        private float m_NextControllerScanTime;

        public string GameplayReadinessName => "Shooter";
        public FusionTransportBridge GameplayReadinessTransport => m_BoundBridge;
        public ushort GameplayReadinessModuleId => ModuleId;
        public ushort FullSnapshotModuleId => ModuleId;
        public string FullSnapshotProducerName => "Shooter";

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

            NetworkShooterController relevant =
                identity.GetComponentInChildren<NetworkShooterController>(true);
            if (relevant == null) return true;

            return m_RegisteredControllers.TryGetValue(
                       identity.NetworkId, out NetworkShooterController registered) &&
                   registered == relevant;
        }

        private struct ShooterAssetEntry
        {
            public ShooterWeapon Weapon;
            public GameObject Prefab;
            public Handle Handle;
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
            FlushPendingState();

            if (!m_AutoRegisterSceneControllers || Time.unscaledTime < m_NextControllerScanTime) return;
            m_NextControllerScanTime = Time.unscaledTime + Mathf.Max(0.05f, m_ControllerScanInterval);
            RefreshControllerRegistry(force: false);
        }

        private void OnDisable()
        {
            UnbindTransport();
            UnwireManager();
            UnregisterAllControllers();
            m_PendingWeaponStates.Clear();
            m_PendingAimStates.Clear();
            m_PendingImpactSnapshots.Clear();
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
                Log("module handler registration was rejected; another Shooter bridge is already bound");
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
                    ? context.Fail("Shooter bridge is not bound as the current authority.")
                    : default;
            }

            WireManager(forceRoleRefresh: true);
            NetworkShooterManager manager = GetManager();
            if (manager == null || manager != m_WiredManager || !m_ManagerInitialized)
                return context.Fail("NetworkShooterManager is unavailable or not initialized.");

            RefreshControllerRegistry(force: true);
            float serverTime = GetNetworkTime();

            foreach (KeyValuePair<uint, NetworkShooterController> pair in m_RegisteredControllers)
            {
                NetworkShooterController controller = pair.Value;
                if (controller == null) continue;
                NetworkWeaponState weapon = m_LatestWeaponStates.TryGetValue(pair.Key, out NetworkWeaponState savedWeapon)
                    ? savedWeapon : controller.WeaponState;
                NetworkAimState aim = m_LatestAimStates.TryGetValue(pair.Key, out NetworkAimState savedAim)
                    ? savedAim : controller.AimState;
                if (!SendToClient(
                    context.ClientId,
                    MessageType.CharacterSnapshot,
                    new NetworkShooterCharacterSnapshot
                    {
                        CharacterNetworkId = pair.Key,
                        WeaponState = weapon,
                        AimState = aim,
                        ServerTime = serverTime
                    },
                    (writer, value) => writer.Write(value)))
                {
                    return context.Fail($"Could not enqueue Shooter character snapshot {pair.Key}.");
                }
            }

            NetworkShooterImpactProp[] props = FindObjectsByType<NetworkShooterImpactProp>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            for (int i = 0; i < props.Length; i++)
            {
                if (props[i] == null || props[i].NetworkId == 0) continue;
                if (!SendToClient(
                    context.ClientId,
                    MessageType.ImpactPropSnapshot,
                    props[i].CaptureSnapshot(serverTime),
                    (writer, value) => writer.Write(value)))
                {
                    return context.Fail(
                        $"Could not enqueue Shooter impact snapshot {props[i].NetworkId}.");
                }
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
            NetworkShooterManager manager = GetManager();
            if (manager == null) return;
            if (m_WiredManager != manager)
            {
                UnwireManager();
                m_WiredManager = manager;
            }

            manager.SendShotRequestToServer -= SendShotRequest;
            manager.SendShotRequestToServer += SendShotRequest;
            manager.SendHitRequestToServer -= SendHitRequest;
            manager.SendHitRequestToServer += SendHitRequest;
            manager.SendReloadRequestToServer -= SendReloadRequest;
            manager.SendReloadRequestToServer += SendReloadRequest;
            manager.SendQuickReloadRequestToServer -= SendQuickReloadRequest;
            manager.SendQuickReloadRequestToServer += SendQuickReloadRequest;
            manager.SendFixJamRequestToServer -= SendFixJamRequest;
            manager.SendFixJamRequestToServer += SendFixJamRequest;
            manager.SendChargeStartRequestToServer -= SendChargeStartRequest;
            manager.SendChargeStartRequestToServer += SendChargeStartRequest;
            manager.SendChargeCancelRequestToServer -= SendChargeCancelRequest;
            manager.SendChargeCancelRequestToServer += SendChargeCancelRequest;
            manager.SendSightSwitchRequestToServer -= SendSightSwitchRequest;
            manager.SendSightSwitchRequestToServer += SendSightSwitchRequest;

            manager.SendShotResponseToClient -= SendShotResponse;
            manager.SendShotResponseToClient += SendShotResponse;
            manager.SendHitResponseToClient -= SendHitResponse;
            manager.SendHitResponseToClient += SendHitResponse;
            manager.SendReloadResponseToClient -= SendReloadResponse;
            manager.SendReloadResponseToClient += SendReloadResponse;
            manager.SendFixJamResponseToClient -= SendFixJamResponse;
            manager.SendFixJamResponseToClient += SendFixJamResponse;
            manager.SendChargeStartResponseToClient -= SendChargeStartResponse;
            manager.SendChargeStartResponseToClient += SendChargeStartResponse;
            manager.SendSightSwitchResponseToClient -= SendSightSwitchResponse;
            manager.SendSightSwitchResponseToClient += SendSightSwitchResponse;

            manager.BroadcastShotToAllClients -= BroadcastShot;
            manager.BroadcastShotToAllClients += BroadcastShot;
            manager.BroadcastHitToAllClients -= BroadcastHit;
            manager.BroadcastHitToAllClients += BroadcastHit;
            manager.BroadcastReloadToAllClients -= BroadcastReload;
            manager.BroadcastReloadToAllClients += BroadcastReload;
            manager.BroadcastJamToAllClients -= BroadcastJam;
            manager.BroadcastJamToAllClients += BroadcastJam;
            manager.BroadcastFixJamToAllClients -= BroadcastFixJam;
            manager.BroadcastFixJamToAllClients += BroadcastFixJam;
            manager.BroadcastChargeToAllClients -= BroadcastCharge;
            manager.BroadcastChargeToAllClients += BroadcastCharge;
            manager.BroadcastSightSwitchToAllClients -= BroadcastSightSwitch;
            manager.BroadcastSightSwitchToAllClients += BroadcastSightSwitch;
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
            NetworkShooterManager manager = m_WiredManager;
            if (manager != null)
            {
                manager.SendShotRequestToServer -= SendShotRequest;
                manager.SendHitRequestToServer -= SendHitRequest;
                manager.SendReloadRequestToServer -= SendReloadRequest;
                manager.SendQuickReloadRequestToServer -= SendQuickReloadRequest;
                manager.SendFixJamRequestToServer -= SendFixJamRequest;
                manager.SendChargeStartRequestToServer -= SendChargeStartRequest;
                manager.SendChargeCancelRequestToServer -= SendChargeCancelRequest;
                manager.SendSightSwitchRequestToServer -= SendSightSwitchRequest;
                manager.SendShotResponseToClient -= SendShotResponse;
                manager.SendHitResponseToClient -= SendHitResponse;
                manager.SendReloadResponseToClient -= SendReloadResponse;
                manager.SendFixJamResponseToClient -= SendFixJamResponse;
                manager.SendChargeStartResponseToClient -= SendChargeStartResponse;
                manager.SendSightSwitchResponseToClient -= SendSightSwitchResponse;
                manager.BroadcastShotToAllClients -= BroadcastShot;
                manager.BroadcastHitToAllClients -= BroadcastHit;
                manager.BroadcastReloadToAllClients -= BroadcastReload;
                manager.BroadcastJamToAllClients -= BroadcastJam;
                manager.BroadcastFixJamToAllClients -= BroadcastFixJam;
                manager.BroadcastChargeToAllClients -= BroadcastCharge;
                manager.BroadcastSightSwitchToAllClients -= BroadcastSightSwitch;
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
            m_WeaponAssets.Clear();
            var hashes = new HashSet<int>();
            if (m_WeaponRegistrations != null)
            {
                for (int i = 0; i < m_WeaponRegistrations.Length; i++)
                {
                    ShooterWeaponRegistration registration = m_WeaponRegistrations[i];
                    if (registration.Weapon == null) continue;
                    int hash = registration.Weapon.Id.Hash;
                    if (!hashes.Add(hash)) Log("duplicate weapon hash=" + hash);
                    NetworkShooterManager.RegisterShooterWeapon(
                        registration.Weapon, registration.ModelPrefab, registration.Handle);
                    m_WeaponAssets[hash] = new ShooterAssetEntry
                    {
                        Weapon = registration.Weapon,
                        Prefab = registration.ModelPrefab,
                        Handle = registration.Handle
                    };
                }
            }
            m_AssetsRegistered = true;
        }

        private void RefreshControllerRegistry(bool force)
        {
            NetworkShooterManager manager = GetManager();
            if (manager == null) return;
            PruneControllerRegistry(manager);
            if (!m_AutoRegisterSceneControllers && !force) return;

            NetworkShooterController[] controllers = FindObjectsByType<NetworkShooterController>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            for (int i = 0; i < controllers.Length; i++) RegisterController(manager, controllers[i]);
        }

        private void RegisterController(NetworkShooterManager manager, NetworkShooterController controller)
        {
            if (manager == null || controller == null) return;
            NetworkCharacter character = controller.GetComponent<NetworkCharacter>();
            if (character == null || character.NetworkId == 0 ||
                character.Role == NetworkCharacter.NetworkRole.None) return;

            uint networkId = character.NetworkId;
            bool isServer = character.IsServerInstance;
            bool isLocalClient = character.IsOwnerInstance;

            if (m_RegisteredControllers.TryGetValue(networkId, out NetworkShooterController existing))
            {
                if (existing == controller)
                {
                    bool roleChanged = controller.IsServer != isServer ||
                                       controller.IsLocalClient != isLocalClient;
                    if (roleChanged) controller.Initialize(isServer, isLocalClient);
                    ResubscribeController(controller);
                    if (roleChanged) controller.ForceNetworkStateSync();
                    FlushPendingState(networkId, controller);
                    return;
                }
                if (existing != null) UnsubscribeController(existing);
                manager.UnregisterController(networkId);
            }

            controller.Initialize(isServer, isLocalClient);
            ResubscribeController(controller);
            manager.RegisterController(networkId, controller);
            m_RegisteredControllers[networkId] = controller;
            controller.ForceNetworkStateSync();
            FlushPendingState(networkId, controller);
        }

        private void ResubscribeController(NetworkShooterController controller)
        {
            controller.OnWeaponStateChanged -= HandleWeaponStateChanged;
            controller.OnWeaponStateChanged += HandleWeaponStateChanged;
            controller.OnAimStateChanged -= HandleAimStateChanged;
            controller.OnAimStateChanged += HandleAimStateChanged;
        }

        private void UnsubscribeController(NetworkShooterController controller)
        {
            if (controller == null) return;
            controller.OnWeaponStateChanged -= HandleWeaponStateChanged;
            controller.OnAimStateChanged -= HandleAimStateChanged;
        }

        private void PruneControllerRegistry(NetworkShooterManager manager)
        {
            m_RemoveBuffer.Clear();
            foreach (KeyValuePair<uint, NetworkShooterController> pair in m_RegisteredControllers)
            {
                NetworkShooterController controller = pair.Value;
                NetworkCharacter character = controller != null
                    ? controller.GetComponent<NetworkCharacter>() : null;
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
                if (m_RegisteredControllers.TryGetValue(networkId, out NetworkShooterController controller) &&
                    controller != null)
                {
                    controller.OnWeaponStateChanged -= HandleWeaponStateChanged;
                    controller.OnAimStateChanged -= HandleAimStateChanged;
                }
                manager.UnregisterController(networkId);
                m_RegisteredControllers.Remove(networkId);
                m_LatestWeaponStates.Remove(networkId);
                m_LatestAimStates.Remove(networkId);
            }
        }

        private void UnregisterAllControllers()
        {
            NetworkShooterManager manager = GetManager();
            foreach (KeyValuePair<uint, NetworkShooterController> pair in m_RegisteredControllers)
            {
                if (pair.Value != null)
                {
                    pair.Value.OnWeaponStateChanged -= HandleWeaponStateChanged;
                    pair.Value.OnAimStateChanged -= HandleAimStateChanged;
                }
                manager?.UnregisterController(pair.Key);
            }
            m_RegisteredControllers.Clear();
        }

        private void HandleWeaponStateChanged(NetworkShooterController controller, NetworkWeaponState state)
        {
            if (controller == null || !controller.IsLocalClient) return;
            uint id = controller.GetComponent<NetworkCharacter>()?.NetworkId ?? 0;
            if (id == 0) return;
            m_LatestWeaponStates[id] = state;
            SendStateToAuthority(MessageType.WeaponState, id, state, (writer, value) => writer.Write(value));
        }

        private void HandleAimStateChanged(NetworkShooterController controller, NetworkAimState state)
        {
            if (controller == null || !controller.IsLocalClient) return;
            uint id = controller.GetComponent<NetworkCharacter>()?.NetworkId ?? 0;
            if (id == 0) return;
            m_LatestAimStates[id] = state;
            SendStateToAuthority(MessageType.AimState, id, state, (writer, value) => writer.Write(value));
        }

        private void SendShotRequest(NetworkShotRequest value) =>
            SendToAuthority(MessageType.ShotRequest, value, (writer, item) => writer.Write(item));
        private void SendHitRequest(NetworkShooterHitRequest value) =>
            SendToAuthority(MessageType.HitRequest, value, (writer, item) => writer.Write(item));
        private void SendReloadRequest(NetworkReloadRequest value) =>
            SendToAuthority(MessageType.ReloadRequest, value, (writer, item) => writer.Write(item));
        private void SendQuickReloadRequest(NetworkQuickReloadRequest value) =>
            SendToAuthority(MessageType.QuickReloadRequest, value, (writer, item) => writer.Write(item));
        private void SendFixJamRequest(NetworkFixJamRequest value) =>
            SendToAuthority(MessageType.FixJamRequest, value, (writer, item) => writer.Write(item));
        private void SendChargeStartRequest(NetworkChargeStartRequest value) =>
            SendToAuthority(MessageType.ChargeStartRequest, value, (writer, item) => writer.Write(item));
        private void SendChargeCancelRequest(NetworkChargeCancelRequest value) =>
            SendToAuthority(MessageType.ChargeCancelRequest, value, (writer, item) => writer.Write(item));
        private void SendSightSwitchRequest(NetworkSightSwitchRequest value) =>
            SendToAuthority(MessageType.SightSwitchRequest, value, (writer, item) => writer.Write(item));

        private void SendShotResponse(uint clientId, NetworkShotResponse value) =>
            SendToClient(clientId, MessageType.ShotResponse, value, (writer, item) => writer.Write(item));
        private void SendHitResponse(uint clientId, NetworkShooterHitResponse value) =>
            SendToClient(clientId, MessageType.HitResponse, value, (writer, item) => writer.Write(item));
        private void SendReloadResponse(uint clientId, NetworkReloadResponse value) =>
            SendToClient(clientId, MessageType.ReloadResponse, value, (writer, item) => writer.Write(item));
        private void SendFixJamResponse(uint clientId, NetworkFixJamResponse value) =>
            SendToClient(clientId, MessageType.FixJamResponse, value, (writer, item) => writer.Write(item));
        private void SendChargeStartResponse(uint clientId, NetworkChargeStartResponse value) =>
            SendToClient(clientId, MessageType.ChargeStartResponse, value, (writer, item) => writer.Write(item));
        private void SendSightSwitchResponse(uint clientId, NetworkSightSwitchResponse value) =>
            SendToClient(clientId, MessageType.SightSwitchResponse, value, (writer, item) => writer.Write(item));

        private void BroadcastShot(NetworkShotBroadcast value) =>
            Broadcast(MessageType.ShotBroadcast, value, (writer, item) => writer.Write(item));
        private void BroadcastHit(NetworkShooterHitBroadcast value) =>
            Broadcast(MessageType.HitBroadcast, value, (writer, item) => writer.Write(item));
        private void BroadcastReload(NetworkReloadBroadcast value) =>
            Broadcast(MessageType.ReloadBroadcast, value, (writer, item) => writer.Write(item));
        private void BroadcastJam(NetworkJamBroadcast value) =>
            Broadcast(MessageType.JamBroadcast, value, (writer, item) => writer.Write(item));
        private void BroadcastFixJam(NetworkFixJamBroadcast value) =>
            Broadcast(MessageType.FixJamBroadcast, value, (writer, item) => writer.Write(item));
        private void BroadcastCharge(NetworkChargeBroadcast value) =>
            Broadcast(MessageType.ChargeBroadcast, value, (writer, item) => writer.Write(item));
        private void BroadcastSightSwitch(NetworkSightSwitchBroadcast value) =>
            Broadcast(MessageType.SightSwitchBroadcast, value, (writer, item) => writer.Write(item));

        private void SendToAuthority<T>(MessageType type, T value, Action<FusionValueWriter, T> write)
        {
            if (m_BoundBridge == null || !m_BoundBridge.IsClient) return;
            m_BoundBridge.SendModuleToAuthority(
                ModuleId, (ushort)type, FusionValueCodec.Encode(value, write));
        }

        private bool SendToClient<T>(
            uint clientId, MessageType type, T value, Action<FusionValueWriter, T> write)
        {
            if (m_BoundBridge == null || !m_BoundBridge.IsServer) return false;
            return m_BoundBridge.SendModuleToClient(
                clientId, ModuleId, (ushort)type, FusionValueCodec.Encode(value, write));
        }

        private void Broadcast<T>(MessageType type, T value, Action<FusionValueWriter, T> write)
        {
            if (m_BoundBridge == null || !m_BoundBridge.IsServer) return;
            m_BoundBridge.BroadcastModule(
                ModuleId, (ushort)type, FusionValueCodec.Encode(value, write));
        }

        private void SendStateToAuthority<T>(
            MessageType type, uint characterId, T value, Action<FusionValueWriter, T> write)
        {
            if (m_BoundBridge == null || !m_BoundBridge.IsClient || characterId == 0) return;
            var writer = new FusionValueWriter();
            writer.Write(characterId);
            write(writer, value);
            m_BoundBridge.SendModuleToAuthority(
                ModuleId,
                (ushort)type,
                writer.ToArray(),
                reliable: false);
        }

        private void BroadcastState<T>(
            MessageType type, uint characterId, T value, Action<FusionValueWriter, T> write)
        {
            if (m_BoundBridge == null || !m_BoundBridge.IsServer || characterId == 0) return;
            var writer = new FusionValueWriter();
            writer.Write(characterId);
            write(writer, value);
            m_BoundBridge.BroadcastModule(
                ModuleId,
                (ushort)type,
                writer.ToArray(),
                reliable: false);
        }

        private void HandleModuleMessage(FusionModuleMessage message)
        {
            switch ((MessageType)message.MessageType)
            {
                case MessageType.ShotRequest:
                    ReceiveRequest(message, (NetworkShooterManager m, uint s, NetworkShotRequest v) => m.ReceiveShotRequest(s, v)); break;
                case MessageType.HitRequest:
                    ReceiveRequest(message, (NetworkShooterManager m, uint s, NetworkShooterHitRequest v) => m.ReceiveHitRequest(s, v)); break;
                case MessageType.ReloadRequest:
                    ReceiveRequest(message, (NetworkShooterManager m, uint s, NetworkReloadRequest v) => m.ReceiveReloadRequest(s, v)); break;
                case MessageType.QuickReloadRequest:
                    ReceiveRequest(message, (NetworkShooterManager m, uint s, NetworkQuickReloadRequest v) => m.ReceiveQuickReloadRequest(s, v)); break;
                case MessageType.FixJamRequest:
                    ReceiveRequest(message, (NetworkShooterManager m, uint s, NetworkFixJamRequest v) => m.ReceiveFixJamRequest(s, v)); break;
                case MessageType.ChargeStartRequest:
                    ReceiveRequest(message, (NetworkShooterManager m, uint s, NetworkChargeStartRequest v) => m.ReceiveChargeStartRequest(s, v)); break;
                case MessageType.ChargeCancelRequest:
                    ReceiveRequest(message, (NetworkShooterManager m, uint s, NetworkChargeCancelRequest v) => m.ReceiveChargeCancelRequest(s, v)); break;
                case MessageType.SightSwitchRequest:
                    ReceiveRequest(message, (NetworkShooterManager m, uint s, NetworkSightSwitchRequest v) => m.ReceiveSightSwitchRequest(s, v)); break;

                case MessageType.ShotResponse:
                    ReceiveAuthority(message, (NetworkShooterManager m, NetworkShotResponse v) => m.ReceiveShotResponse(v)); break;
                case MessageType.HitResponse:
                    ReceiveAuthority(message, (NetworkShooterManager m, NetworkShooterHitResponse v) => m.ReceiveHitResponse(v)); break;
                case MessageType.ReloadResponse:
                    ReceiveAuthority(message, (NetworkShooterManager m, NetworkReloadResponse v) => m.ReceiveReloadResponse(v)); break;
                case MessageType.FixJamResponse:
                    ReceiveAuthority(message, (NetworkShooterManager m, NetworkFixJamResponse v) => m.ReceiveFixJamResponse(v)); break;
                case MessageType.ChargeStartResponse:
                    ReceiveAuthority(message, (NetworkShooterManager m, NetworkChargeStartResponse v) => m.ReceiveChargeStartResponse(v)); break;
                case MessageType.SightSwitchResponse:
                    ReceiveAuthority(message, (NetworkShooterManager m, NetworkSightSwitchResponse v) => m.ReceiveSightSwitchResponse(v)); break;

                case MessageType.ShotBroadcast:
                    ReceiveAuthority(message, (NetworkShooterManager m, NetworkShotBroadcast v) => m.ReceiveShotBroadcast(v)); break;
                case MessageType.HitBroadcast:
                    ReceiveAuthority(message, (NetworkShooterManager m, NetworkShooterHitBroadcast v) => m.ReceiveHitBroadcast(v)); break;
                case MessageType.ReloadBroadcast:
                    ReceiveAuthority(message, (NetworkShooterManager m, NetworkReloadBroadcast v) => m.ReceiveReloadBroadcast(v)); break;
                case MessageType.JamBroadcast:
                    ReceiveAuthority(message, (NetworkShooterManager m, NetworkJamBroadcast v) => m.ReceiveJamBroadcast(v)); break;
                case MessageType.FixJamBroadcast:
                    ReceiveAuthority(message, (NetworkShooterManager m, NetworkFixJamBroadcast v) => m.ReceiveFixJamBroadcast(v)); break;
                case MessageType.ChargeBroadcast:
                    ReceiveAuthority(message, (NetworkShooterManager m, NetworkChargeBroadcast v) => m.ReceiveChargeBroadcast(v)); break;
                case MessageType.SightSwitchBroadcast:
                    ReceiveAuthority(message, (NetworkShooterManager m, NetworkSightSwitchBroadcast v) => m.ReceiveSightSwitchBroadcast(v)); break;

                case MessageType.WeaponState:
                    ReceiveWeaponState(message); break;
                case MessageType.AimState:
                    ReceiveAimState(message); break;
                case MessageType.CharacterSnapshot:
                    ReceiveAuthority(message, (NetworkShooterManager m, NetworkShooterCharacterSnapshot v) =>
                        ApplyCharacterSnapshot(v)); break;
                case MessageType.ImpactPropSnapshot:
                    ReceiveAuthority(message, (NetworkShooterManager m, NetworkShooterImpactPropSnapshot v) =>
                        ApplyImpactSnapshot(v)); break;
                default:
                    Log("dropped unknown message type=" + message.MessageType); break;
            }
        }

        private delegate void RequestReceiver<T>(NetworkShooterManager manager, uint sender, T value);
        private delegate void AuthorityReceiver<T>(NetworkShooterManager manager, T value);

        private void ReceiveRequest<T>(FusionModuleMessage message, RequestReceiver<T> receive)
        {
            if (message.FromAuthority || m_BoundBridge == null || !m_BoundBridge.IsServer) return;
            NetworkShooterManager manager = GetManager();
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
            NetworkShooterManager manager = GetManager();
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
            if (!TryDecodeState(message.Payload, out uint id, out NetworkWeaponState state)) return;
            if (!message.FromAuthority)
            {
                if (!IsAuthorizedStateSender(id, message.SenderClientId)) return;
                m_LatestWeaponStates[id] = state;
                ApplyWeaponState(id, state);
                BroadcastState(MessageType.WeaponState, id, state, (writer, value) => writer.Write(value));
                return;
            }
            m_LatestWeaponStates[id] = state;
            ApplyWeaponState(id, state);
        }

        private void ReceiveAimState(FusionModuleMessage message)
        {
            if (!TryDecodeState(message.Payload, out uint id, out NetworkAimState state)) return;
            if (!message.FromAuthority)
            {
                if (!IsAuthorizedStateSender(id, message.SenderClientId)) return;
                m_LatestAimStates[id] = state;
                ApplyAimState(id, state);
                BroadcastState(MessageType.AimState, id, state, (writer, value) => writer.Write(value));
                return;
            }
            m_LatestAimStates[id] = state;
            ApplyAimState(id, state);
        }

        private static bool TryDecodeState(
            ReadOnlyMemory<byte> payload, out uint id, out NetworkWeaponState state)
        {
            id = 0; state = default;
            try
            {
                var reader = new FusionValueReader(payload);
                reader.Read(ref id);
                reader.Read(ref state);
                return id != 0 && reader.End;
            }
            catch (Exception) { id = 0; state = default; return false; }
        }

        private static bool TryDecodeState(
            ReadOnlyMemory<byte> payload, out uint id, out NetworkAimState state)
        {
            id = 0; state = default;
            try
            {
                var reader = new FusionValueReader(payload);
                reader.Read(ref id);
                reader.Read(ref state);
                return id != 0 && reader.End;
            }
            catch (Exception) { id = 0; state = default; return false; }
        }

        private bool IsAuthorizedStateSender(uint characterId, uint senderClientId)
        {
            return m_BoundBridge != null && m_BoundBridge.IsServer &&
                   m_BoundBridge.TryGetCharacterOwner(characterId, out uint ownerClientId) &&
                   ownerClientId == senderClientId;
        }

        private void ApplyCharacterSnapshot(NetworkShooterCharacterSnapshot snapshot)
        {
            if (snapshot.CharacterNetworkId == 0) return;
            m_LatestWeaponStates[snapshot.CharacterNetworkId] = snapshot.WeaponState;
            m_LatestAimStates[snapshot.CharacterNetworkId] = snapshot.AimState;
            ApplyWeaponState(snapshot.CharacterNetworkId, snapshot.WeaponState);
            ApplyAimState(snapshot.CharacterNetworkId, snapshot.AimState);
        }

        private void ApplyImpactSnapshot(NetworkShooterImpactPropSnapshot snapshot)
        {
            if (snapshot.PropNetworkId == 0) return;
            if (!NetworkShooterImpactProp.TryApplySnapshot(snapshot))
                AddBounded(m_PendingImpactSnapshots, snapshot.PropNetworkId, snapshot);
        }

        private void ApplyWeaponState(uint characterId, NetworkWeaponState state)
        {
            if (!m_RegisteredControllers.TryGetValue(characterId, out NetworkShooterController controller) ||
                controller == null)
            {
                RefreshControllerRegistry(force: true);
                m_RegisteredControllers.TryGetValue(characterId, out controller);
            }
            if (controller == null)
            {
                AddBounded(m_PendingWeaponStates, characterId, state);
                return;
            }

            m_WeaponAssets.TryGetValue(state.WeaponHash, out ShooterAssetEntry entry);
            controller.ApplyRemoteWeaponState(state, entry.Weapon, entry.Prefab, entry.Handle);
        }

        private void ApplyAimState(uint characterId, NetworkAimState state)
        {
            if (!m_RegisteredControllers.TryGetValue(characterId, out NetworkShooterController controller) ||
                controller == null)
            {
                RefreshControllerRegistry(force: true);
                m_RegisteredControllers.TryGetValue(characterId, out controller);
            }
            if (controller == null)
            {
                AddBounded(m_PendingAimStates, characterId, state);
                return;
            }
            controller.ApplyRemoteAimState(state);
        }

        private void FlushPendingState()
        {
            m_RemoveBuffer.Clear();
            foreach (KeyValuePair<uint, NetworkWeaponState> pair in m_PendingWeaponStates)
            {
                if (!m_RegisteredControllers.TryGetValue(pair.Key, out NetworkShooterController controller) ||
                    controller == null) continue;
                ApplyWeaponState(pair.Key, pair.Value);
                m_RemoveBuffer.Add(pair.Key);
            }
            for (int i = 0; i < m_RemoveBuffer.Count; i++) m_PendingWeaponStates.Remove(m_RemoveBuffer[i]);

            m_RemoveBuffer.Clear();
            foreach (KeyValuePair<uint, NetworkAimState> pair in m_PendingAimStates)
            {
                if (!m_RegisteredControllers.TryGetValue(pair.Key, out NetworkShooterController controller) ||
                    controller == null) continue;
                controller.ApplyRemoteAimState(pair.Value);
                m_RemoveBuffer.Add(pair.Key);
            }
            for (int i = 0; i < m_RemoveBuffer.Count; i++) m_PendingAimStates.Remove(m_RemoveBuffer[i]);

            m_RemoveBuffer.Clear();
            foreach (KeyValuePair<uint, NetworkShooterImpactPropSnapshot> pair in m_PendingImpactSnapshots)
            {
                if (!NetworkShooterImpactProp.TryApplySnapshot(pair.Value)) continue;
                m_RemoveBuffer.Add(pair.Key);
            }
            for (int i = 0; i < m_RemoveBuffer.Count; i++) m_PendingImpactSnapshots.Remove(m_RemoveBuffer[i]);
        }

        private void FlushPendingState(uint id, NetworkShooterController controller)
        {
            if (controller == null) return;
            if (m_PendingWeaponStates.TryGetValue(id, out NetworkWeaponState weapon))
            {
                m_PendingWeaponStates.Remove(id);
                ApplyWeaponState(id, weapon);
            }
            if (m_PendingAimStates.TryGetValue(id, out NetworkAimState aim))
            {
                m_PendingAimStates.Remove(id);
                controller.ApplyRemoteAimState(aim);
            }
        }

        private static void AddBounded<TKey, TValue>(
            Dictionary<TKey, TValue> dictionary, TKey key, TValue value)
        {
            if (!dictionary.ContainsKey(key) && dictionary.Count >= MaxPendingPersistentStates)
            {
                using Dictionary<TKey, TValue>.Enumerator enumerator = dictionary.GetEnumerator();
                if (enumerator.MoveNext()) dictionary.Remove(enumerator.Current.Key);
            }
            dictionary[key] = value;
        }

        private NetworkCharacter ResolveNetworkCharacter(uint id)
        {
            if (m_BoundBridge != null)
            {
                GameCreator.Runtime.Characters.Character character = m_BoundBridge.ResolveCharacter(id);
                if (character != null)
                {
                    NetworkCharacter result = character.GetComponent<NetworkCharacter>();
                    if (result != null) return result;
                }
            }
            return m_RegisteredControllers.TryGetValue(id, out NetworkShooterController controller) &&
                   controller != null ? controller.GetComponent<NetworkCharacter>() : null;
        }

        private float GetNetworkTime() =>
            m_BoundBridge != null ? m_BoundBridge.ServerTime : Time.time;

        private void Log(string message)
        {
            if (!m_LogDiagnostics) return;
            Debug.Log("[FusionShooterTransportBridge] " + message, this);
        }

        private static NetworkShooterManager GetManager()
        {
            return NetworkShooterManager.Instance != null
                ? NetworkShooterManager.Instance
                : FindFirstObjectByType<NetworkShooterManager>();
        }
    }

    internal static class FusionShooterDynamicCodec
    {
        public static void ReadDynamic<T>(this FusionValueReader reader, ref T value)
        {
            object boxed;
            if (typeof(T) == typeof(NetworkShotRequest)) { NetworkShotRequest v = default; reader.Read(ref v); boxed = v; }
            else if (typeof(T) == typeof(NetworkShooterHitRequest)) { NetworkShooterHitRequest v = default; reader.Read(ref v); boxed = v; }
            else if (typeof(T) == typeof(NetworkReloadRequest)) { NetworkReloadRequest v = default; reader.Read(ref v); boxed = v; }
            else if (typeof(T) == typeof(NetworkQuickReloadRequest)) { NetworkQuickReloadRequest v = default; reader.Read(ref v); boxed = v; }
            else if (typeof(T) == typeof(NetworkFixJamRequest)) { NetworkFixJamRequest v = default; reader.Read(ref v); boxed = v; }
            else if (typeof(T) == typeof(NetworkChargeStartRequest)) { NetworkChargeStartRequest v = default; reader.Read(ref v); boxed = v; }
            else if (typeof(T) == typeof(NetworkChargeCancelRequest)) { NetworkChargeCancelRequest v = default; reader.Read(ref v); boxed = v; }
            else if (typeof(T) == typeof(NetworkSightSwitchRequest)) { NetworkSightSwitchRequest v = default; reader.Read(ref v); boxed = v; }
            else if (typeof(T) == typeof(NetworkShotResponse)) { NetworkShotResponse v = default; reader.Read(ref v); boxed = v; }
            else if (typeof(T) == typeof(NetworkShooterHitResponse)) { NetworkShooterHitResponse v = default; reader.Read(ref v); boxed = v; }
            else if (typeof(T) == typeof(NetworkReloadResponse)) { NetworkReloadResponse v = default; reader.Read(ref v); boxed = v; }
            else if (typeof(T) == typeof(NetworkFixJamResponse)) { NetworkFixJamResponse v = default; reader.Read(ref v); boxed = v; }
            else if (typeof(T) == typeof(NetworkChargeStartResponse)) { NetworkChargeStartResponse v = default; reader.Read(ref v); boxed = v; }
            else if (typeof(T) == typeof(NetworkSightSwitchResponse)) { NetworkSightSwitchResponse v = default; reader.Read(ref v); boxed = v; }
            else if (typeof(T) == typeof(NetworkShotBroadcast)) { NetworkShotBroadcast v = default; reader.Read(ref v); boxed = v; }
            else if (typeof(T) == typeof(NetworkShooterHitBroadcast)) { NetworkShooterHitBroadcast v = default; reader.Read(ref v); boxed = v; }
            else if (typeof(T) == typeof(NetworkReloadBroadcast)) { NetworkReloadBroadcast v = default; reader.Read(ref v); boxed = v; }
            else if (typeof(T) == typeof(NetworkJamBroadcast)) { NetworkJamBroadcast v = default; reader.Read(ref v); boxed = v; }
            else if (typeof(T) == typeof(NetworkFixJamBroadcast)) { NetworkFixJamBroadcast v = default; reader.Read(ref v); boxed = v; }
            else if (typeof(T) == typeof(NetworkChargeBroadcast)) { NetworkChargeBroadcast v = default; reader.Read(ref v); boxed = v; }
            else if (typeof(T) == typeof(NetworkSightSwitchBroadcast)) { NetworkSightSwitchBroadcast v = default; reader.Read(ref v); boxed = v; }
            else if (typeof(T) == typeof(NetworkShooterCharacterSnapshot)) { NetworkShooterCharacterSnapshot v = default; reader.Read(ref v); boxed = v; }
            else if (typeof(T) == typeof(NetworkShooterImpactPropSnapshot)) { NetworkShooterImpactPropSnapshot v = default; reader.Read(ref v); boxed = v; }
            else throw new InvalidOperationException("Unsupported Shooter payload type " + typeof(T).FullName);
            value = (T)boxed;
        }
    }
}
#endif
