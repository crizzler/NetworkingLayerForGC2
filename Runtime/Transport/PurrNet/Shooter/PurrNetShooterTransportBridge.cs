#if GC2_SHOOTER
using System;
using System.Collections.Generic;
using Arawn.GameCreator2.Networking;
using Arawn.GameCreator2.Networking.Transport.PurrNet;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Shooter;
using PurrNet;
using PurrNet.Transports;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Shooter.Transport.PurrNet
{
    /// <summary>
    /// Hash-to-asset registration used to reconstruct a remote Shooter weapon locally.
    /// The weapon is gameplay data; model prefab and handle are presentation data.
    /// </summary>
    [Serializable]
    public struct ShooterWeaponRegistration
    {
        public ShooterWeapon Weapon;
        public GameObject ModelPrefab;
        public Handle Handle;
    }

    [DisallowMultipleComponent]
    [AddComponentMenu("Game Creator/Network/Transport/PurrNet Shooter Bridge")]
    [DefaultExecutionOrder(-330)]
    public sealed class PurrNetShooterTransportBridge : MonoBehaviour
    {
        [Header("PurrNet")]
        [Tooltip("Optional reference to a specific NetworkManager. Leave empty to use NetworkManager.main.")]
        [SerializeField] private NetworkManager m_NetworkManager;

        [Tooltip("Optional reference to the core GC2 PurrNet bridge used for character lookup and network time.")]
        [SerializeField] private PurrNetTransportBridge m_CoreBridge;

        [Tooltip("Reliable channel used for shooter requests, responses, weapon state, and effect broadcasts.")]
        [SerializeField] private Channel m_Channel = Channel.ReliableOrdered;

        [Header("Shooter Assets")]
        [Tooltip("Structured weapon registrations used for remote weapon reconstruction.")]
        [SerializeField] private ShooterWeaponRegistration[] m_WeaponRegistrations =
            Array.Empty<ShooterWeaponRegistration>();

        [Tooltip("Shooter weapons whose hashes should be registered for remote playback.")]
        [HideInInspector]
        [SerializeField] private ShooterWeapon[] m_RegisterWeapons = Array.Empty<ShooterWeapon>();

        [Tooltip("Weapon model prefabs matching the registered weapon array.")]
        [HideInInspector]
        [SerializeField] private GameObject[] m_RegisterWeaponPrefabs = Array.Empty<GameObject>();

        [Tooltip("Handle assets matching the registered weapon array.")]
        [HideInInspector]
        [SerializeField] private Handle[] m_RegisterWeaponHandles = Array.Empty<Handle>();

        [Header("Controllers")]
        [Tooltip("Automatically finds NetworkShooterController components on spawned NetworkCharacter objects.")]
        [SerializeField] private bool m_AutoRegisterSceneControllers = true;

        [Min(0.05f)]
        [SerializeField] private float m_ControllerScanInterval = 0.25f;

        [Header("Debug")]
        [SerializeField] private bool m_LogDiagnostics = true;

        private readonly Dictionary<uint, NetworkShooterController> m_RegisteredControllers = new(32);
        private readonly List<uint> m_RemoveBuffer = new(16);
        private readonly Dictionary<int, ShooterAssetEntry> m_WeaponAssets = new(16);
        private readonly Dictionary<uint, GC2ShooterWeaponStatePacket> m_LatestWeaponStates = new(32);
        private readonly Dictionary<uint, GC2ShooterAimStatePacket> m_LatestAimStates = new(32);
        private readonly Dictionary<uint, GC2ShooterWeaponStatePacket> m_PendingWeaponStates = new(32);
        private readonly Dictionary<uint, GC2ShooterAimStatePacket> m_PendingAimStates = new(32);
        private readonly Dictionary<uint, NetworkShooterImpactPropSnapshot> m_PendingImpactPropSnapshots = new(32);
        private readonly HashSet<int> m_MissingWeaponAssetDiagnostics = new();
        private const int MAX_PENDING_PERSISTENT_STATES = 128;

        private NetworkManager m_HookedManager;
        private bool m_SubscribedServer;
        private bool m_SubscribedClient;
        private bool m_ManagerInitialized;
        private bool m_LastServer;
        private bool m_LastClient;
        private bool m_AssetsRegistered;
        private float m_NextControllerScanTime;
        private float m_NextMissingManagerDiagnosticTime;

        private struct ShooterAssetEntry
        {
            public ShooterWeapon Weapon;
            public GameObject Prefab;
            public Handle Handle;
        }

        private NetworkManager ActiveManager => m_NetworkManager ? m_NetworkManager : NetworkManager.main;

        private PurrNetTransportBridge CoreBridge
        {
            get
            {
                if (m_CoreBridge != null) return m_CoreBridge;
                m_CoreBridge = NetworkTransportBridge.Active as PurrNetTransportBridge;
                if (m_CoreBridge == null) m_CoreBridge = FindFirstObjectByType<PurrNetTransportBridge>();
                return m_CoreBridge;
            }
        }

        private void LogDiagnostics(string message)
        {
            if (!m_LogDiagnostics && !NetworkShooterDebug.ForceDiagnostics) return;
            Debug.Log($"[PurrNetShooterTransportBridge] {message}", this);
        }

        private bool ShouldLogDiagnostic(ref float nextTime, float interval = 1f)
        {
            if (!m_LogDiagnostics && !NetworkShooterDebug.ForceDiagnostics) return false;

            float now = Time.unscaledTime;
            if (now < nextTime) return false;

            nextTime = now + Mathf.Max(0.1f, interval);
            return true;
        }

        private void Awake()
        {
            if (m_NetworkManager == null) m_NetworkManager = NetworkManager.main;
            if (m_CoreBridge == null) m_CoreBridge = NetworkTransportBridge.Active as PurrNetTransportBridge;
        }

        private void OnEnable()
        {
            TryHookNetworkManager();
            WireShooterManager();
            RegisterConfiguredAssets();

            FlushPendingPersistentStates();
            RefreshControllerRegistry(force: true);
        }

        private void Start()
        {
            TryHookNetworkManager();
            WireShooterManager();
            RegisterConfiguredAssets();
            RefreshControllerRegistry(force: true);
        }

        private void Update()
        {
            TryHookNetworkManager();
            WireShooterManager();
            RegisterConfiguredAssets();

            if (Time.unscaledTime < m_NextControllerScanTime) return;

            m_NextControllerScanTime = Time.unscaledTime + Mathf.Max(0.05f, m_ControllerScanInterval);
            if (m_AutoRegisterSceneControllers)
            {
                RefreshControllerRegistry(force: false);
            }
            FlushPendingPersistentStates();
        }

        private void OnDisable()
        {
            UnhookNetworkManager();
            UnwireShooterManager();
            UnregisterAllControllers();
            m_LatestWeaponStates.Clear();
            m_LatestAimStates.Clear();
            m_PendingWeaponStates.Clear();
            m_PendingAimStates.Clear();
            m_PendingImpactPropSnapshots.Clear();
            m_MissingWeaponAssetDiagnostics.Clear();
        }

        private void TryHookNetworkManager()
        {
            var nm = ActiveManager;
            if (nm == null)
            {
                LogDiagnostics("network manager hook skipped: no active NetworkManager");
                return;
            }

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
            LogDiagnostics(
                $"hooked NetworkManager server={nm.isServer} client={nm.isClient} " +
                $"localReady={nm.isLocalPlayerReady}");
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
                nm.Unsubscribe<GC2ShooterShotRequestPacket>(HandleShotRequestServer, true);
                nm.Unsubscribe<GC2ShooterHitRequestPacket>(HandleHitRequestServer, true);
                nm.Unsubscribe<GC2ShooterReloadRequestPacket>(HandleReloadRequestServer, true);
                nm.Unsubscribe<GC2ShooterQuickReloadRequestPacket>(HandleQuickReloadRequestServer, true);
                nm.Unsubscribe<GC2ShooterFixJamRequestPacket>(HandleFixJamRequestServer, true);
                nm.Unsubscribe<GC2ShooterChargeStartRequestPacket>(HandleChargeStartRequestServer, true);
                nm.Unsubscribe<GC2ShooterChargeCancelRequestPacket>(HandleChargeCancelRequestServer, true);
                nm.Unsubscribe<GC2ShooterSightSwitchRequestPacket>(HandleSightSwitchRequestServer, true);
                nm.Unsubscribe<GC2ShooterWeaponStatePacket>(HandleWeaponStateServer, true);
                nm.Unsubscribe<GC2ShooterAimStatePacket>(HandleAimStateServer, true);
                m_SubscribedServer = false;
            }

            if (m_SubscribedClient)
            {
                nm.Unsubscribe<GC2ShooterShotResponsePacket>(HandleShotResponseClient, false);
                nm.Unsubscribe<GC2ShooterShotBroadcastPacket>(HandleShotBroadcastClient, false);
                nm.Unsubscribe<GC2ShooterHitResponsePacket>(HandleHitResponseClient, false);
                nm.Unsubscribe<GC2ShooterHitBroadcastPacket>(HandleHitBroadcastClient, false);
                nm.Unsubscribe<GC2ShooterReloadResponsePacket>(HandleReloadResponseClient, false);
                nm.Unsubscribe<GC2ShooterReloadBroadcastPacket>(HandleReloadBroadcastClient, false);
                nm.Unsubscribe<GC2ShooterFixJamResponsePacket>(HandleFixJamResponseClient, false);
                nm.Unsubscribe<GC2ShooterJamBroadcastPacket>(HandleJamBroadcastClient, false);
                nm.Unsubscribe<GC2ShooterFixJamBroadcastPacket>(HandleFixJamBroadcastClient, false);
                nm.Unsubscribe<GC2ShooterChargeStartResponsePacket>(HandleChargeStartResponseClient, false);
                nm.Unsubscribe<GC2ShooterChargeBroadcastPacket>(HandleChargeBroadcastClient, false);
                nm.Unsubscribe<GC2ShooterSightSwitchResponsePacket>(HandleSightSwitchResponseClient, false);
                nm.Unsubscribe<GC2ShooterSightSwitchBroadcastPacket>(HandleSightSwitchBroadcastClient, false);
                nm.Unsubscribe<GC2ShooterWeaponStatePacket>(HandleWeaponStateClient, false);
                nm.Unsubscribe<GC2ShooterAimStatePacket>(HandleAimStateClient, false);
                nm.Unsubscribe<GC2ShooterCharacterSnapshotPacket>(HandleCharacterSnapshotClient, false);
                nm.Unsubscribe<GC2ShooterImpactPropSnapshotPacket>(HandleImpactPropSnapshotClient, false);
                m_SubscribedClient = false;
            }

            m_HookedManager = null;
        }

        private void HandleNetworkStarted(NetworkManager manager, bool asServer)
        {
            if (asServer && !m_SubscribedServer)
            {
                manager.Subscribe<GC2ShooterShotRequestPacket>(HandleShotRequestServer, true);
                manager.Subscribe<GC2ShooterHitRequestPacket>(HandleHitRequestServer, true);
                manager.Subscribe<GC2ShooterReloadRequestPacket>(HandleReloadRequestServer, true);
                manager.Subscribe<GC2ShooterQuickReloadRequestPacket>(HandleQuickReloadRequestServer, true);
                manager.Subscribe<GC2ShooterFixJamRequestPacket>(HandleFixJamRequestServer, true);
                manager.Subscribe<GC2ShooterChargeStartRequestPacket>(HandleChargeStartRequestServer, true);
                manager.Subscribe<GC2ShooterChargeCancelRequestPacket>(HandleChargeCancelRequestServer, true);
                manager.Subscribe<GC2ShooterSightSwitchRequestPacket>(HandleSightSwitchRequestServer, true);
                manager.Subscribe<GC2ShooterWeaponStatePacket>(HandleWeaponStateServer, true);
                manager.Subscribe<GC2ShooterAimStatePacket>(HandleAimStateServer, true);
                m_SubscribedServer = true;
                LogDiagnostics("subscribed shooter server packets");
            }
            else if (!asServer && !m_SubscribedClient)
            {
                manager.Subscribe<GC2ShooterShotResponsePacket>(HandleShotResponseClient, false);
                manager.Subscribe<GC2ShooterShotBroadcastPacket>(HandleShotBroadcastClient, false);
                manager.Subscribe<GC2ShooterHitResponsePacket>(HandleHitResponseClient, false);
                manager.Subscribe<GC2ShooterHitBroadcastPacket>(HandleHitBroadcastClient, false);
                manager.Subscribe<GC2ShooterReloadResponsePacket>(HandleReloadResponseClient, false);
                manager.Subscribe<GC2ShooterReloadBroadcastPacket>(HandleReloadBroadcastClient, false);
                manager.Subscribe<GC2ShooterFixJamResponsePacket>(HandleFixJamResponseClient, false);
                manager.Subscribe<GC2ShooterJamBroadcastPacket>(HandleJamBroadcastClient, false);
                manager.Subscribe<GC2ShooterFixJamBroadcastPacket>(HandleFixJamBroadcastClient, false);
                manager.Subscribe<GC2ShooterChargeStartResponsePacket>(HandleChargeStartResponseClient, false);
                manager.Subscribe<GC2ShooterChargeBroadcastPacket>(HandleChargeBroadcastClient, false);
                manager.Subscribe<GC2ShooterSightSwitchResponsePacket>(HandleSightSwitchResponseClient, false);
                manager.Subscribe<GC2ShooterSightSwitchBroadcastPacket>(HandleSightSwitchBroadcastClient, false);
                manager.Subscribe<GC2ShooterWeaponStatePacket>(HandleWeaponStateClient, false);
                manager.Subscribe<GC2ShooterAimStatePacket>(HandleAimStateClient, false);
                manager.Subscribe<GC2ShooterCharacterSnapshotPacket>(HandleCharacterSnapshotClient, false);
                manager.Subscribe<GC2ShooterImpactPropSnapshotPacket>(HandleImpactPropSnapshotClient, false);
                m_SubscribedClient = true;
                LogDiagnostics("subscribed shooter client packets");
            }

            WireShooterManager();
            RegisterConfiguredAssets();
            RefreshControllerRegistry(force: true);
        }

        private void HandleNetworkShutdown(NetworkManager manager, bool asServer)
        {
            if (asServer && m_SubscribedServer)
            {
                manager.Unsubscribe<GC2ShooterShotRequestPacket>(HandleShotRequestServer, true);
                manager.Unsubscribe<GC2ShooterHitRequestPacket>(HandleHitRequestServer, true);
                manager.Unsubscribe<GC2ShooterReloadRequestPacket>(HandleReloadRequestServer, true);
                manager.Unsubscribe<GC2ShooterQuickReloadRequestPacket>(HandleQuickReloadRequestServer, true);
                manager.Unsubscribe<GC2ShooterFixJamRequestPacket>(HandleFixJamRequestServer, true);
                manager.Unsubscribe<GC2ShooterChargeStartRequestPacket>(HandleChargeStartRequestServer, true);
                manager.Unsubscribe<GC2ShooterChargeCancelRequestPacket>(HandleChargeCancelRequestServer, true);
                manager.Unsubscribe<GC2ShooterSightSwitchRequestPacket>(HandleSightSwitchRequestServer, true);
                manager.Unsubscribe<GC2ShooterWeaponStatePacket>(HandleWeaponStateServer, true);
                manager.Unsubscribe<GC2ShooterAimStatePacket>(HandleAimStateServer, true);
                m_SubscribedServer = false;
                LogDiagnostics("unsubscribed shooter server packets");
            }
            else if (!asServer && m_SubscribedClient)
            {
                manager.Unsubscribe<GC2ShooterShotResponsePacket>(HandleShotResponseClient, false);
                manager.Unsubscribe<GC2ShooterShotBroadcastPacket>(HandleShotBroadcastClient, false);
                manager.Unsubscribe<GC2ShooterHitResponsePacket>(HandleHitResponseClient, false);
                manager.Unsubscribe<GC2ShooterHitBroadcastPacket>(HandleHitBroadcastClient, false);
                manager.Unsubscribe<GC2ShooterReloadResponsePacket>(HandleReloadResponseClient, false);
                manager.Unsubscribe<GC2ShooterReloadBroadcastPacket>(HandleReloadBroadcastClient, false);
                manager.Unsubscribe<GC2ShooterFixJamResponsePacket>(HandleFixJamResponseClient, false);
                manager.Unsubscribe<GC2ShooterJamBroadcastPacket>(HandleJamBroadcastClient, false);
                manager.Unsubscribe<GC2ShooterFixJamBroadcastPacket>(HandleFixJamBroadcastClient, false);
                manager.Unsubscribe<GC2ShooterChargeStartResponsePacket>(HandleChargeStartResponseClient, false);
                manager.Unsubscribe<GC2ShooterChargeBroadcastPacket>(HandleChargeBroadcastClient, false);
                manager.Unsubscribe<GC2ShooterSightSwitchResponsePacket>(HandleSightSwitchResponseClient, false);
                manager.Unsubscribe<GC2ShooterSightSwitchBroadcastPacket>(HandleSightSwitchBroadcastClient, false);
                manager.Unsubscribe<GC2ShooterWeaponStatePacket>(HandleWeaponStateClient, false);
                manager.Unsubscribe<GC2ShooterAimStatePacket>(HandleAimStateClient, false);
                manager.Unsubscribe<GC2ShooterCharacterSnapshotPacket>(HandleCharacterSnapshotClient, false);
                manager.Unsubscribe<GC2ShooterImpactPropSnapshotPacket>(HandleImpactPropSnapshotClient, false);
                m_SubscribedClient = false;
                LogDiagnostics("unsubscribed shooter client packets");
            }

            WireShooterManager();
        }

        private void HandlePlayerLoadedScene(PlayerID player, SceneID scene, bool asServer)
        {
            if (!asServer) return;

            RefreshControllerRegistry(force: true);
            float serverTime = GetNetworkTime();

            foreach (var pair in m_RegisteredControllers)
            {
                uint networkId = pair.Key;
                NetworkShooterController controller = pair.Value;
                if (controller == null) continue;

                NetworkWeaponState weaponState = m_LatestWeaponStates.TryGetValue(networkId, out var weaponPacket)
                    ? weaponPacket.state
                    : controller.WeaponState;
                NetworkAimState aimState = m_LatestAimStates.TryGetValue(networkId, out var aimPacket)
                    ? aimPacket.state
                    : controller.AimState;

                var snapshot = new NetworkShooterCharacterSnapshot
                {
                    CharacterNetworkId = networkId,
                    WeaponState = weaponState,
                    AimState = aimState,
                    ServerTime = serverTime
                };
                ActiveManager?.Send(
                    player,
                    new GC2ShooterCharacterSnapshotPacket { snapshot = snapshot },
                    m_Channel);
            }

            NetworkShooterImpactProp[] props = FindObjectsByType<NetworkShooterImpactProp>(
                FindObjectsInactive.Exclude,
                FindObjectsSortMode.None);
            for (int i = 0; i < props.Length; i++)
            {
                NetworkShooterImpactProp prop = props[i];
                if (prop == null || prop.NetworkId == 0) continue;

                ActiveManager?.Send(
                    player,
                    new GC2ShooterImpactPropSnapshotPacket
                    {
                        snapshot = prop.CaptureSnapshot(serverTime)
                    },
                    m_Channel);
            }

            LogDiagnostics(
                $"sent Shooter late-join snapshots player={player.id} characters={m_RegisteredControllers.Count} " +
                $"impactProps={props.Length} scene={scene}");
        }

        private void WireShooterManager()
        {
            NetworkShooterManager manager = GetShooterManager();
            if (manager == null)
            {
                if (ShouldLogDiagnostic(ref m_NextMissingManagerDiagnosticTime))
                {
                    LogDiagnostics("shooter manager wiring skipped: NetworkShooterManager not found");
                }

                return;
            }

            manager.SendShotRequestToServer -= SendShotRequestToServer;
            manager.SendShotRequestToServer += SendShotRequestToServer;
            manager.SendHitRequestToServer -= SendHitRequestToServer;
            manager.SendHitRequestToServer += SendHitRequestToServer;
            manager.SendReloadRequestToServer -= SendReloadRequestToServer;
            manager.SendReloadRequestToServer += SendReloadRequestToServer;
            manager.SendQuickReloadRequestToServer -= SendQuickReloadRequestToServer;
            manager.SendQuickReloadRequestToServer += SendQuickReloadRequestToServer;
            manager.SendFixJamRequestToServer -= SendFixJamRequestToServer;
            manager.SendFixJamRequestToServer += SendFixJamRequestToServer;
            manager.SendChargeStartRequestToServer -= SendChargeStartRequestToServer;
            manager.SendChargeStartRequestToServer += SendChargeStartRequestToServer;
            manager.SendChargeCancelRequestToServer -= SendChargeCancelRequestToServer;
            manager.SendChargeCancelRequestToServer += SendChargeCancelRequestToServer;
            manager.SendSightSwitchRequestToServer -= SendSightSwitchRequestToServer;
            manager.SendSightSwitchRequestToServer += SendSightSwitchRequestToServer;

            manager.SendShotResponseToClient -= SendShotResponseToClient;
            manager.SendShotResponseToClient += SendShotResponseToClient;
            manager.SendHitResponseToClient -= SendHitResponseToClient;
            manager.SendHitResponseToClient += SendHitResponseToClient;
            manager.SendReloadResponseToClient -= SendReloadResponseToClient;
            manager.SendReloadResponseToClient += SendReloadResponseToClient;
            manager.SendFixJamResponseToClient -= SendFixJamResponseToClient;
            manager.SendFixJamResponseToClient += SendFixJamResponseToClient;
            manager.SendChargeStartResponseToClient -= SendChargeStartResponseToClient;
            manager.SendChargeStartResponseToClient += SendChargeStartResponseToClient;
            manager.SendSightSwitchResponseToClient -= SendSightSwitchResponseToClient;
            manager.SendSightSwitchResponseToClient += SendSightSwitchResponseToClient;

            manager.BroadcastShotToAllClients -= BroadcastShotToAllClients;
            manager.BroadcastShotToAllClients += BroadcastShotToAllClients;
            manager.BroadcastHitToAllClients -= BroadcastHitToAllClients;
            manager.BroadcastHitToAllClients += BroadcastHitToAllClients;
            manager.BroadcastReloadToAllClients -= BroadcastReloadToAllClients;
            manager.BroadcastReloadToAllClients += BroadcastReloadToAllClients;
            manager.BroadcastJamToAllClients -= BroadcastJamToAllClients;
            manager.BroadcastJamToAllClients += BroadcastJamToAllClients;
            manager.BroadcastFixJamToAllClients -= BroadcastFixJamToAllClients;
            manager.BroadcastFixJamToAllClients += BroadcastFixJamToAllClients;
            manager.BroadcastChargeToAllClients -= BroadcastChargeToAllClients;
            manager.BroadcastChargeToAllClients += BroadcastChargeToAllClients;
            manager.BroadcastSightSwitchToAllClients -= BroadcastSightSwitchToAllClients;
            manager.BroadcastSightSwitchToAllClients += BroadcastSightSwitchToAllClients;

            manager.GetCharacterByNetworkIdFunc = ResolveNetworkCharacter;
            manager.GetNetworkTimeFunc = GetNetworkTime;

            var nm = ActiveManager;
            bool isServer = nm != null && nm.isServer;
            bool isClient = nm != null && nm.isClient;
            if (!m_ManagerInitialized || isServer != m_LastServer || isClient != m_LastClient)
            {
                manager.Initialize(isServer, isClient);
                m_ManagerInitialized = true;
                m_LastServer = isServer;
                m_LastClient = isClient;
                LogDiagnostics($"initialized Shooter manager server={isServer} client={isClient}");
            }
        }

        private void UnwireShooterManager()
        {
            NetworkShooterManager manager = GetShooterManager();
            if (manager == null) return;

            manager.SendShotRequestToServer -= SendShotRequestToServer;
            manager.SendHitRequestToServer -= SendHitRequestToServer;
            manager.SendReloadRequestToServer -= SendReloadRequestToServer;
            manager.SendQuickReloadRequestToServer -= SendQuickReloadRequestToServer;
            manager.SendFixJamRequestToServer -= SendFixJamRequestToServer;
            manager.SendChargeStartRequestToServer -= SendChargeStartRequestToServer;
            manager.SendChargeCancelRequestToServer -= SendChargeCancelRequestToServer;
            manager.SendSightSwitchRequestToServer -= SendSightSwitchRequestToServer;
            manager.SendShotResponseToClient -= SendShotResponseToClient;
            manager.SendHitResponseToClient -= SendHitResponseToClient;
            manager.SendReloadResponseToClient -= SendReloadResponseToClient;
            manager.SendFixJamResponseToClient -= SendFixJamResponseToClient;
            manager.SendChargeStartResponseToClient -= SendChargeStartResponseToClient;
            manager.SendSightSwitchResponseToClient -= SendSightSwitchResponseToClient;
            manager.BroadcastShotToAllClients -= BroadcastShotToAllClients;
            manager.BroadcastHitToAllClients -= BroadcastHitToAllClients;
            manager.BroadcastReloadToAllClients -= BroadcastReloadToAllClients;
            manager.BroadcastJamToAllClients -= BroadcastJamToAllClients;
            manager.BroadcastFixJamToAllClients -= BroadcastFixJamToAllClients;
            manager.BroadcastChargeToAllClients -= BroadcastChargeToAllClients;
            manager.BroadcastSightSwitchToAllClients -= BroadcastSightSwitchToAllClients;

            if (ReferenceEquals(manager.GetCharacterByNetworkIdFunc?.Target, this))
            {
                manager.GetCharacterByNetworkIdFunc = null;
            }

            if (ReferenceEquals(manager.GetNetworkTimeFunc?.Target, this))
            {
                manager.GetNetworkTimeFunc = null;
            }

            m_ManagerInitialized = false;
        }

        private void RegisterConfiguredAssets()
        {
            if (m_AssetsRegistered) return;
            m_WeaponAssets.Clear();
            var registeredHashes = new HashSet<int>();
            bool useStructured = m_WeaponRegistrations != null && m_WeaponRegistrations.Length > 0;

            if (useStructured)
            {
                for (int i = 0; i < m_WeaponRegistrations.Length; i++)
                {
                    ShooterWeaponRegistration registration = m_WeaponRegistrations[i];
                    RegisterConfiguredAsset(
                        registration.Weapon,
                        registration.ModelPrefab,
                        registration.Handle,
                        i,
                        registeredHashes,
                        "structured");
                }
            }
            else
            {
                int weaponCount = m_RegisterWeapons?.Length ?? 0;
                int prefabCount = m_RegisterWeaponPrefabs?.Length ?? 0;
                int handleCount = m_RegisterWeaponHandles?.Length ?? 0;
                if (weaponCount != prefabCount || weaponCount != handleCount)
                {
                    Debug.LogWarning(
                        $"[PurrNetShooterTransportBridge] Legacy Shooter asset arrays have mismatched lengths " +
                        $"weapons={weaponCount}, prefabs={prefabCount}, handles={handleCount}. " +
                        "Missing entries will be treated as null; migrate these to Weapon Registrations.",
                        this);
                }

                for (int i = 0; i < weaponCount; i++)
                {
                    RegisterConfiguredAsset(
                        m_RegisterWeapons[i],
                        GetArrayValue(m_RegisterWeaponPrefabs, i),
                        GetArrayValue(m_RegisterWeaponHandles, i),
                        i,
                        registeredHashes,
                        "legacy");
                }
            }

            if (m_WeaponAssets.Count == 0)
            {
                Debug.LogWarning(
                    "[PurrNetShooterTransportBridge] No Shooter weapons are registered. " +
                    "Remote weapon models and authored shot/impact presentation cannot be reconstructed. " +
                    "Add entries to Weapon Registrations.",
                    this);
            }

            m_AssetsRegistered = true;
        }

        private void RegisterConfiguredAsset(
            ShooterWeapon weapon,
            GameObject prefab,
            Handle handle,
            int index,
            HashSet<int> registeredHashes,
            string source)
        {
            if (weapon == null)
            {
                Debug.LogWarning(
                    $"[PurrNetShooterTransportBridge] Ignoring null {source} weapon registration at index {index}.",
                    this);
                return;
            }

            if (prefab == null)
            {
                Debug.LogWarning(
                    $"[PurrNetShooterTransportBridge] Shooter weapon '{weapon.name}' at {source} index {index} " +
                    "has no remote model prefab. State will still synchronize, but a newly spawned remote " +
                    "character cannot reconstruct the visible weapon model from this registration.",
                    this);
            }

            int hash = weapon.Id.Hash;
            if (!registeredHashes.Add(hash))
            {
                Debug.LogWarning(
                    $"[PurrNetShooterTransportBridge] Duplicate Shooter weapon hash {hash} at {source} index {index} " +
                    $"({weapon.name}). The last registration wins.",
                    this);
            }

            NetworkShooterManager.RegisterShooterWeapon(weapon, prefab, handle);
            m_WeaponAssets[hash] = new ShooterAssetEntry
            {
                Weapon = weapon,
                Prefab = prefab,
                Handle = handle
            };

            LogDiagnostics(
                $"registered shooter asset source={source} weapon={weapon.name} hash={hash} " +
                $"prefab={(prefab != null ? prefab.name : "null")} " +
                $"handle={(handle != null ? handle.name : "null")}");
        }

        private void RefreshControllerRegistry(bool force)
        {
            NetworkShooterManager manager = GetShooterManager();
            if (manager == null)
            {
                if (ShouldLogDiagnostic(ref m_NextMissingManagerDiagnosticTime))
                {
                    LogDiagnostics("controller registry refresh skipped: NetworkShooterManager not found");
                }

                return;
            }

            PruneControllerRegistry(manager);

            if (!m_AutoRegisterSceneControllers && !force) return;

            var controllers = FindObjectsByType<NetworkShooterController>(
                FindObjectsInactive.Exclude,
                FindObjectsSortMode.None);

            for (int i = 0; i < controllers.Length; i++)
            {
                RegisterController(manager, controllers[i]);
            }
        }

        private void RegisterController(NetworkShooterManager manager, NetworkShooterController controller)
        {
            if (manager == null || controller == null) return;

            var networkCharacter = controller.GetComponent<NetworkCharacter>();
            if (networkCharacter == null || networkCharacter.NetworkId == 0) return;
            if (networkCharacter.Role == NetworkCharacter.NetworkRole.None) return;

            bool isServer = networkCharacter.IsServerInstance;
            bool isLocalClient = networkCharacter.IsOwnerInstance;

            uint networkId = networkCharacter.NetworkId;
            if (m_RegisteredControllers.TryGetValue(networkId, out var existing))
            {
                if (existing == controller)
                {
                    bool roleChanged = controller.IsServer != isServer || controller.IsLocalClient != isLocalClient;
                    if (roleChanged)
                    {
                        controller.Initialize(isServer, isLocalClient);
                    }

                    controller.OnWeaponStateChanged -= HandleControllerWeaponStateChanged;
                    controller.OnWeaponStateChanged += HandleControllerWeaponStateChanged;
                    controller.OnAimStateChanged -= HandleControllerAimStateChanged;
                    controller.OnAimStateChanged += HandleControllerAimStateChanged;
                    LogDiagnostics(
                        $"refreshed controller subscriptions netId={networkId} name={controller.name} " +
                        $"server={isServer} localClient={isLocalClient}");
                    if (roleChanged)
                    {
                        controller.ForceNetworkStateSync();
                    }
                    FlushPendingPersistentState(networkId, controller);
                    return;
                }

                existing.OnWeaponStateChanged -= HandleControllerWeaponStateChanged;
                existing.OnAimStateChanged -= HandleControllerAimStateChanged;
                manager.UnregisterController(networkId);
            }

            controller.Initialize(isServer, isLocalClient);
            controller.OnWeaponStateChanged -= HandleControllerWeaponStateChanged;
            controller.OnWeaponStateChanged += HandleControllerWeaponStateChanged;
            controller.OnAimStateChanged -= HandleControllerAimStateChanged;
            controller.OnAimStateChanged += HandleControllerAimStateChanged;

            m_RegisteredControllers[networkId] = controller;
            manager.RegisterController(networkId, controller);
            LogDiagnostics(
                $"registered controller netId={networkId} name={controller.name} role={networkCharacter.Role} " +
                $"server={isServer} localClient={isLocalClient}");
            controller.ForceNetworkStateSync();
            FlushPendingPersistentState(networkId, controller);
        }

        private void PruneControllerRegistry(NetworkShooterManager manager)
        {
            m_RemoveBuffer.Clear();

            foreach (var pair in m_RegisteredControllers)
            {
                var controller = pair.Value;
                var networkCharacter = controller != null ? controller.GetComponent<NetworkCharacter>() : null;
                if (controller == null ||
                    networkCharacter == null ||
                    networkCharacter.NetworkId != pair.Key ||
                    networkCharacter.Role == NetworkCharacter.NetworkRole.None)
                {
                    m_RemoveBuffer.Add(pair.Key);
                }
            }

            for (int i = 0; i < m_RemoveBuffer.Count; i++)
            {
                uint networkId = m_RemoveBuffer[i];
                if (m_RegisteredControllers.TryGetValue(networkId, out var controller) && controller != null)
                {
                    controller.OnWeaponStateChanged -= HandleControllerWeaponStateChanged;
                    controller.OnAimStateChanged -= HandleControllerAimStateChanged;
                }

                manager.UnregisterController(networkId);
                m_RegisteredControllers.Remove(networkId);
                ClearPersistentStateForCharacter(networkId);
            }
        }

        private void ClearPersistentStateForCharacter(uint networkId)
        {
            m_LatestWeaponStates.Remove(networkId);
            m_LatestAimStates.Remove(networkId);
            m_PendingWeaponStates.Remove(networkId);
            m_PendingAimStates.Remove(networkId);
        }

        private void UnregisterAllControllers()
        {
            NetworkShooterManager manager = GetShooterManager();
            if (manager != null)
            {
                foreach (var pair in m_RegisteredControllers)
                {
                    if (pair.Value != null)
                    {
                        pair.Value.OnWeaponStateChanged -= HandleControllerWeaponStateChanged;
                        pair.Value.OnAimStateChanged -= HandleControllerAimStateChanged;
                    }

                    manager.UnregisterController(pair.Key);
                }
            }

            m_RegisteredControllers.Clear();
        }

        private void HandleControllerWeaponStateChanged(NetworkShooterController controller, NetworkWeaponState state)
        {
            if (controller == null || !controller.IsLocalClient) return;

            uint networkId = controller.GetComponent<NetworkCharacter>()?.NetworkId ?? 0;
            if (networkId == 0) return;

            LogDiagnostics(
                $"local weapon state changed netId={networkId} weaponHash={state.WeaponHash} " +
                $"ammo={state.AmmoInMagazine} flags=0x{state.StateFlags:X2} " +
                $"lean={state.LeanAmount:F1}/{state.LeanDecay:F2}");
            m_LatestWeaponStates[networkId] = new GC2ShooterWeaponStatePacket
            {
                characterNetworkId = networkId,
                state = state
            };
            SendWeaponStateToServer(networkId, state);
        }

        private void HandleControllerAimStateChanged(NetworkShooterController controller, NetworkAimState state)
        {
            if (controller == null || !controller.IsLocalClient) return;

            uint networkId = controller.GetComponent<NetworkCharacter>()?.NetworkId ?? 0;
            if (networkId == 0) return;

            LogDiagnostics(
                $"local aim state changed netId={networkId} aiming={state.IsAiming} " +
                $"accuracy={state.Accuracy} compressed={state.CompressedDirection} point={state.AimPoint}");
            m_LatestAimStates[networkId] = new GC2ShooterAimStatePacket
            {
                characterNetworkId = networkId,
                state = state
            };
            SendAimStateToServer(networkId, state);
        }

        private void SendShotRequestToServer(NetworkShotRequest request)
        {
            var nm = ActiveManager;
            NetworkShooterDebug.LogPhysics(
                "PurrNetSendShot",
                $"manager={(nm != null)} client={(nm != null && nm.isClient)} " +
                $"server={(nm != null && nm.isServer)} host={(nm != null && nm.isHost)} " +
                $"localReady={(nm != null && nm.isLocalPlayerReady)} actor={request.ActorNetworkId} " +
                $"req={request.RequestId} corr={request.CorrelationId} weaponHash={request.WeaponHash} " +
                $"muzzle={request.MuzzlePosition} exactDirection={request.ShotDirection}",
                this);
            if (nm == null || !nm.isClient)
            {
                LogDiagnostics(
                    $"dropped shot request send actor={request.ActorNetworkId} req={request.RequestId} " +
                    $"networkManager={(nm != null)} isClient={(nm != null && nm.isClient)}");
                return;
            }

            LogDiagnostics(
                $"sending shot request actor={request.ActorNetworkId} shooter={request.ShooterNetworkId} " +
                $"req={request.RequestId} weaponHash={request.WeaponHash} hostLoopback={nm.isServer}");

            var packet = new GC2ShooterShotRequestPacket { request = request };
            if (nm.isServer)
            {
                if (nm.isLocalPlayerReady) DispatchShotRequestOnServer(nm.localPlayer, packet);
                else LogDiagnostics($"dropped shot request host loopback actor={request.ActorNetworkId} req={request.RequestId}: local player not ready");
                return;
            }

            nm.SendToServer(packet, m_Channel);
        }

        private void SendHitRequestToServer(NetworkShooterHitRequest request)
        {
            var nm = ActiveManager;
            if (!request.IsCharacterHit || request.TargetNetworkId == 0)
            {
                NetworkShooterDebug.LogPhysics(
                    "PurrNetSendHit",
                    $"manager={(nm != null)} client={(nm != null && nm.isClient)} " +
                    $"server={(nm != null && nm.isServer)} host={(nm != null && nm.isHost)} " +
                    $"localReady={(nm != null && nm.isLocalPlayerReady)} actor={request.ActorNetworkId} " +
                    $"req={request.RequestId} corr={request.CorrelationId} sourceShot={request.SourceShotRequestId} " +
                    $"weaponHash={request.WeaponHash} point={request.HitPoint} normal={request.HitNormal}",
                    this);
            }
            if (nm == null || !nm.isClient)
            {
                LogDiagnostics(
                    $"dropped hit request send actor={request.ActorNetworkId} req={request.RequestId} " +
                    $"networkManager={(nm != null)} isClient={(nm != null && nm.isClient)}");
                return;
            }

            LogDiagnostics(
                $"sending hit request actor={request.ActorNetworkId} target={request.TargetNetworkId} " +
                $"req={request.RequestId} sourceShot={request.SourceShotRequestId} weaponHash={request.WeaponHash} " +
                $"hostLoopback={nm.isServer}");

            var packet = new GC2ShooterHitRequestPacket { request = request };
            if (nm.isServer)
            {
                if (nm.isLocalPlayerReady) DispatchHitRequestOnServer(nm.localPlayer, packet);
                else LogDiagnostics($"dropped hit request host loopback actor={request.ActorNetworkId} req={request.RequestId}: local player not ready");
                return;
            }

            nm.SendToServer(packet, m_Channel);
        }

        private void SendReloadRequestToServer(NetworkReloadRequest request)
        {
            var nm = ActiveManager;
            if (nm == null || !nm.isClient)
            {
                LogDiagnostics(
                    $"[ShooterAmmoDebug] dropped reload request send actor={request.ActorNetworkId} " +
                    $"req={request.RequestId} weaponHash={request.WeaponHash} networkManager={(nm != null)} " +
                    $"isClient={(nm != null && nm.isClient)}");
                return;
            }

            var packet = new GC2ShooterReloadRequestPacket { request = request };
            LogDiagnostics(
                $"[ShooterAmmoDebug] sending reload request actor={request.ActorNetworkId} " +
                $"req={request.RequestId} weaponHash={request.WeaponHash} hostLoopback={nm.isServer}");
            if (nm.isServer)
            {
                if (nm.isLocalPlayerReady) DispatchReloadRequestOnServer(nm.localPlayer, packet);
                else LogDiagnostics($"[ShooterAmmoDebug] dropped reload request host loopback actor={request.ActorNetworkId} req={request.RequestId}: local player not ready");
                return;
            }

            nm.SendToServer(packet, m_Channel);
        }

        private void SendQuickReloadRequestToServer(NetworkQuickReloadRequest request)
        {
            var nm = ActiveManager;
            if (nm == null || !nm.isClient) return;
            var packet = new GC2ShooterQuickReloadRequestPacket { request = request };
            if (nm.isServer)
            {
                if (nm.isLocalPlayerReady) DispatchQuickReloadRequestOnServer(nm.localPlayer, packet);
                return;
            }
            nm.SendToServer(packet, m_Channel);
        }

        private void SendFixJamRequestToServer(NetworkFixJamRequest request)
        {
            var nm = ActiveManager;
            if (nm == null || !nm.isClient) return;
            var packet = new GC2ShooterFixJamRequestPacket { request = request };
            if (nm.isServer)
            {
                if (nm.isLocalPlayerReady) DispatchFixJamRequestOnServer(nm.localPlayer, packet);
                return;
            }
            nm.SendToServer(packet, m_Channel);
        }

        private void SendChargeStartRequestToServer(NetworkChargeStartRequest request)
        {
            var nm = ActiveManager;
            if (nm == null || !nm.isClient) return;
            var packet = new GC2ShooterChargeStartRequestPacket { request = request };
            if (nm.isServer)
            {
                if (nm.isLocalPlayerReady) DispatchChargeStartRequestOnServer(nm.localPlayer, packet);
                return;
            }
            nm.SendToServer(packet, m_Channel);
        }

        private void SendChargeCancelRequestToServer(NetworkChargeCancelRequest request)
        {
            var nm = ActiveManager;
            if (nm == null || !nm.isClient) return;
            var packet = new GC2ShooterChargeCancelRequestPacket { request = request };
            if (nm.isServer)
            {
                if (nm.isLocalPlayerReady) DispatchChargeCancelRequestOnServer(nm.localPlayer, packet);
                return;
            }
            nm.SendToServer(packet, m_Channel);
        }

        private void SendSightSwitchRequestToServer(NetworkSightSwitchRequest request)
        {
            var nm = ActiveManager;
            if (nm == null || !nm.isClient) return;
            var packet = new GC2ShooterSightSwitchRequestPacket { request = request };
            if (nm.isServer)
            {
                if (nm.isLocalPlayerReady) DispatchSightSwitchRequestOnServer(nm.localPlayer, packet);
                return;
            }
            nm.SendToServer(packet, m_Channel);
        }

        private void SendWeaponStateToServer(uint characterNetworkId, NetworkWeaponState state)
        {
            var nm = ActiveManager;
            if (nm == null || !nm.isClient || characterNetworkId == 0)
            {
                LogDiagnostics(
                    $"dropped weapon state send character={characterNetworkId} weaponHash={state.WeaponHash} " +
                    $"networkManager={(nm != null)} isClient={(nm != null && nm.isClient)}");
                return;
            }

            var packet = new GC2ShooterWeaponStatePacket
            {
                characterNetworkId = characterNetworkId,
                state = state
            };

            LogDiagnostics(
                $"sending weapon state character={characterNetworkId} weaponHash={state.WeaponHash} " +
                $"ammo={state.AmmoInMagazine} flags=0x{state.StateFlags:X2} " +
                $"lean={state.LeanAmount:F1}/{state.LeanDecay:F2} hostLoopback={nm.isServer}");

            if (nm.isServer)
            {
                if (nm.isLocalPlayerReady) DispatchWeaponStateOnServer(nm.localPlayer, packet);
                else LogDiagnostics($"dropped weapon state host loopback character={characterNetworkId}: local player not ready");
                return;
            }

            nm.SendToServer(packet, m_Channel);
        }

        private void SendAimStateToServer(uint characterNetworkId, NetworkAimState state)
        {
            var nm = ActiveManager;
            if (nm == null || !nm.isClient || characterNetworkId == 0)
            {
                LogDiagnostics(
                    $"dropped aim state send character={characterNetworkId} " +
                    $"networkManager={(nm != null)} isClient={(nm != null && nm.isClient)}");
                return;
            }

            var packet = new GC2ShooterAimStatePacket
            {
                characterNetworkId = characterNetworkId,
                state = state
            };

            LogDiagnostics(
                $"sending aim state character={characterNetworkId} aiming={state.IsAiming} " +
                $"accuracy={state.Accuracy} compressed={state.CompressedDirection} " +
                $"point={state.AimPoint} hostLoopback={nm.isServer}");

            if (nm.isServer)
            {
                if (nm.isLocalPlayerReady) DispatchAimStateOnServer(nm.localPlayer, packet);
                else LogDiagnostics($"dropped aim state host loopback character={characterNetworkId}: local player not ready");
                return;
            }

            nm.SendToServer(packet, m_Channel);
        }

        private void SendShotResponseToClient(uint clientNetworkId, NetworkShotResponse response)
        {
            var nm = ActiveManager;
            if (nm == null || !nm.isServer)
            {
                LogDiagnostics($"dropped shot response send client={clientNetworkId} req={response.RequestId}: server not active");
                return;
            }
            if (!TryGetPlayerId(nm, clientNetworkId, out var playerId))
            {
                LogDiagnostics($"dropped shot response send client={clientNetworkId} req={response.RequestId}: PlayerID not found");
                return;
            }

            LogDiagnostics(
                $"sending shot response client={clientNetworkId} req={response.RequestId} " +
                $"validated={response.Validated} reason={response.RejectionReason}");
            nm.Send(playerId, new GC2ShooterShotResponsePacket { response = response }, m_Channel);
        }

        private void SendHitResponseToClient(uint clientNetworkId, NetworkShooterHitResponse response)
        {
            var nm = ActiveManager;
            if (nm == null || !nm.isServer)
            {
                LogDiagnostics($"dropped hit response send client={clientNetworkId} req={response.RequestId}: server not active");
                return;
            }
            if (!TryGetPlayerId(nm, clientNetworkId, out var playerId))
            {
                LogDiagnostics($"dropped hit response send client={clientNetworkId} req={response.RequestId}: PlayerID not found");
                return;
            }

            LogDiagnostics(
                $"sending hit response client={clientNetworkId} req={response.RequestId} " +
                $"validated={response.Validated} reason={response.RejectionReason}");
            nm.Send(playerId, new GC2ShooterHitResponsePacket { response = response }, m_Channel);
        }

        private void SendReloadResponseToClient(uint clientNetworkId, NetworkReloadResponse response)
        {
            var nm = ActiveManager;
            if (nm == null || !nm.isServer)
            {
                LogDiagnostics(
                    $"[ShooterAmmoDebug] dropped reload response send client={clientNetworkId} " +
                    $"req={response.RequestId}: server not active");
                return;
            }
            if (!TryGetPlayerId(nm, clientNetworkId, out var playerId))
            {
                LogDiagnostics(
                    $"[ShooterAmmoDebug] dropped reload response send client={clientNetworkId} " +
                    $"req={response.RequestId}: PlayerID not found");
                return;
            }

            LogDiagnostics(
                $"[ShooterAmmoDebug] sending reload response client={clientNetworkId} " +
                $"req={response.RequestId} actor={response.ActorNetworkId} validated={response.Validated} " +
                $"reason={response.RejectionReason}");
            nm.Send(playerId, new GC2ShooterReloadResponsePacket { response = response }, m_Channel);
        }

        private void SendFixJamResponseToClient(uint clientNetworkId, NetworkFixJamResponse response)
        {
            var nm = ActiveManager;
            if (nm == null || !nm.isServer || !TryGetPlayerId(nm, clientNetworkId, out var playerId)) return;
            nm.Send(playerId, new GC2ShooterFixJamResponsePacket { response = response }, m_Channel);
        }

        private void SendChargeStartResponseToClient(uint clientNetworkId, NetworkChargeStartResponse response)
        {
            var nm = ActiveManager;
            if (nm == null || !nm.isServer || !TryGetPlayerId(nm, clientNetworkId, out var playerId)) return;
            nm.Send(playerId, new GC2ShooterChargeStartResponsePacket { response = response }, m_Channel);
        }

        private void SendSightSwitchResponseToClient(uint clientNetworkId, NetworkSightSwitchResponse response)
        {
            var nm = ActiveManager;
            if (nm == null || !nm.isServer || !TryGetPlayerId(nm, clientNetworkId, out var playerId)) return;
            nm.Send(playerId, new GC2ShooterSightSwitchResponsePacket { response = response }, m_Channel);
        }

        private void BroadcastShotToAllClients(NetworkShotBroadcast broadcast)
        {
            var nm = ActiveManager;
            if (nm == null || !nm.isServer)
            {
                LogDiagnostics($"dropped shot broadcast shooter={broadcast.ShooterNetworkId}: server not active");
                return;
            }
            LogDiagnostics(
                $"broadcasting shot shooter={broadcast.ShooterNetworkId} weaponHash={broadcast.WeaponHash} " +
                $"muzzle={broadcast.MuzzlePosition} hitPoint={broadcast.HitPoint}");
            nm.SendToAll(new GC2ShooterShotBroadcastPacket { broadcast = broadcast }, m_Channel);
        }

        private void BroadcastHitToAllClients(NetworkShooterHitBroadcast broadcast)
        {
            var nm = ActiveManager;
            if (nm == null || !nm.isServer)
            {
                LogDiagnostics($"dropped hit broadcast shooter={broadcast.ShooterNetworkId}: server not active");
                return;
            }
            LogDiagnostics(
                $"broadcasting hit shooter={broadcast.ShooterNetworkId} target={broadcast.TargetNetworkId} " +
                $"weaponHash={broadcast.WeaponHash} point={broadcast.HitPoint}");
            int connectionCount = nm.rawTransport?.connections?.Count ?? -1;
            NetworkShooterDebug.LogPhysics(
                "PurrNetBroadcastHit",
                $"server={nm.isServer} client={nm.isClient} host={nm.isHost} connections={connectionCount} " +
                $"shooter={broadcast.ShooterNetworkId} target={broadcast.TargetNetworkId} " +
                $"weaponHash={broadcast.WeaponHash} point={broadcast.HitPoint} normal={broadcast.HitNormal} " +
                $"impactMotion={broadcast.HasImpactMotion}",
                this);
            nm.SendToAll(new GC2ShooterHitBroadcastPacket { broadcast = broadcast }, m_Channel);
        }

        private void BroadcastReloadToAllClients(NetworkReloadBroadcast broadcast)
        {
            var nm = ActiveManager;
            if (nm == null || !nm.isServer)
            {
                LogDiagnostics(
                    $"[ShooterAmmoDebug] dropped reload broadcast character={broadcast.CharacterNetworkId} " +
                    $"event={broadcast.EventType}: server not active");
                return;
            }
            LogDiagnostics(
                $"[ShooterAmmoDebug] broadcasting reload character={broadcast.CharacterNetworkId} " +
                $"weaponHash={broadcast.WeaponHash} event={broadcast.EventType} ammo={broadcast.NewAmmoCount}");
            nm.SendToAll(new GC2ShooterReloadBroadcastPacket { broadcast = broadcast }, m_Channel);
        }

        private void BroadcastJamToAllClients(NetworkJamBroadcast broadcast)
        {
            var nm = ActiveManager;
            if (nm == null || !nm.isServer) return;
            nm.SendToAll(new GC2ShooterJamBroadcastPacket { broadcast = broadcast }, m_Channel);
        }

        private void BroadcastFixJamToAllClients(NetworkFixJamBroadcast broadcast)
        {
            var nm = ActiveManager;
            if (nm == null || !nm.isServer) return;
            nm.SendToAll(new GC2ShooterFixJamBroadcastPacket { broadcast = broadcast }, m_Channel);
        }

        private void BroadcastChargeToAllClients(NetworkChargeBroadcast broadcast)
        {
            var nm = ActiveManager;
            if (nm == null || !nm.isServer) return;
            nm.SendToAll(new GC2ShooterChargeBroadcastPacket { broadcast = broadcast }, m_Channel);
        }

        private void BroadcastSightSwitchToAllClients(NetworkSightSwitchBroadcast broadcast)
        {
            var nm = ActiveManager;
            if (nm == null || !nm.isServer) return;
            nm.SendToAll(new GC2ShooterSightSwitchBroadcastPacket { broadcast = broadcast }, m_Channel);
        }

        private void BroadcastWeaponStateToAllClients(GC2ShooterWeaponStatePacket packet)
        {
            var nm = ActiveManager;
            if (nm == null || !nm.isServer)
            {
                LogDiagnostics($"dropped weapon state broadcast character={packet.characterNetworkId}: server not active");
                return;
            }
            LogDiagnostics(
                $"broadcasting weapon state character={packet.characterNetworkId} weaponHash={packet.state.WeaponHash} " +
                $"ammo={packet.state.AmmoInMagazine} flags=0x{packet.state.StateFlags:X2}");
            nm.SendToAll(packet, m_Channel);
        }

        private void BroadcastAimStateToAllClients(GC2ShooterAimStatePacket packet)
        {
            var nm = ActiveManager;
            if (nm == null || !nm.isServer)
            {
                LogDiagnostics($"dropped aim state broadcast character={packet.characterNetworkId}: server not active");
                return;
            }

            LogDiagnostics(
                $"broadcasting aim state character={packet.characterNetworkId} aiming={packet.state.IsAiming} " +
                $"accuracy={packet.state.Accuracy} compressed={packet.state.CompressedDirection} " +
                $"point={packet.state.AimPoint}");
            nm.SendToAll(packet, m_Channel);
        }

        private void HandleShotRequestServer(PlayerID senderPlayer, GC2ShooterShotRequestPacket data, bool asServer)
        {
            if (!asServer) return;
            DispatchShotRequestOnServer(senderPlayer, data);
        }

        private void DispatchShotRequestOnServer(PlayerID senderPlayer, GC2ShooterShotRequestPacket data)
        {
            if (!TryConvertPlayerId(senderPlayer, out uint senderClientId))
            {
                LogDiagnostics($"dropped shot request server dispatch: could not convert sender {senderPlayer}");
                return;
            }

            LogDiagnostics(
                $"server received shot request sender={senderClientId} actor={data.request.ActorNetworkId} " +
                $"req={data.request.RequestId} weaponHash={data.request.WeaponHash}");
            NetworkShooterDebug.LogPhysics(
                "PurrNetReceiveShotServer",
                $"sender={senderClientId} actor={data.request.ActorNetworkId} req={data.request.RequestId} " +
                $"corr={data.request.CorrelationId} weaponHash={data.request.WeaponHash} " +
                $"muzzle={data.request.MuzzlePosition} exactDirection={data.request.ShotDirection}",
                this);
            EnsureWeaponEquippedOnServer(data.request.ShooterNetworkId, data.request.WeaponHash);
            GetShooterManager()?.ReceiveShotRequest(senderClientId, data.request);
        }

        private void HandleHitRequestServer(PlayerID senderPlayer, GC2ShooterHitRequestPacket data, bool asServer)
        {
            if (!asServer) return;
            DispatchHitRequestOnServer(senderPlayer, data);
        }

        private void DispatchHitRequestOnServer(PlayerID senderPlayer, GC2ShooterHitRequestPacket data)
        {
            if (!TryConvertPlayerId(senderPlayer, out uint senderClientId))
            {
                LogDiagnostics($"dropped hit request server dispatch: could not convert sender {senderPlayer}");
                return;
            }

            LogDiagnostics(
                $"server received hit request sender={senderClientId} actor={data.request.ActorNetworkId} " +
                $"target={data.request.TargetNetworkId} req={data.request.RequestId} sourceShot={data.request.SourceShotRequestId}");
            if (!data.request.IsCharacterHit || data.request.TargetNetworkId == 0)
            {
                NetworkShooterDebug.LogPhysics(
                    "PurrNetReceiveHitServer",
                    $"sender={senderClientId} actor={data.request.ActorNetworkId} req={data.request.RequestId} " +
                    $"corr={data.request.CorrelationId} sourceShot={data.request.SourceShotRequestId} " +
                    $"weaponHash={data.request.WeaponHash} point={data.request.HitPoint} " +
                    $"normal={data.request.HitNormal}",
                    this);
            }
            GetShooterManager()?.ReceiveHitRequest(senderClientId, data.request);
        }

        private void HandleReloadRequestServer(PlayerID senderPlayer, GC2ShooterReloadRequestPacket data, bool asServer)
        {
            if (!asServer) return;
            DispatchReloadRequestOnServer(senderPlayer, data);
        }

        private void DispatchReloadRequestOnServer(PlayerID senderPlayer, GC2ShooterReloadRequestPacket data)
        {
            if (!TryConvertPlayerId(senderPlayer, out uint senderClientId))
            {
                LogDiagnostics($"[ShooterAmmoDebug] dropped reload request server dispatch: could not convert sender {senderPlayer}");
                return;
            }
            LogDiagnostics(
                $"[ShooterAmmoDebug] server dispatch reload request sender={senderClientId} " +
                $"actor={data.request.ActorNetworkId} req={data.request.RequestId} " +
                $"weaponHash={data.request.WeaponHash}");
            EnsureWeaponEquippedOnServer(data.request.CharacterNetworkId, data.request.WeaponHash);
            GetShooterManager()?.ReceiveReloadRequest(senderClientId, data.request);
        }

        private void HandleQuickReloadRequestServer(
            PlayerID senderPlayer,
            GC2ShooterQuickReloadRequestPacket data,
            bool asServer)
        {
            if (asServer) DispatchQuickReloadRequestOnServer(senderPlayer, data);
        }

        private void DispatchQuickReloadRequestOnServer(
            PlayerID senderPlayer,
            GC2ShooterQuickReloadRequestPacket data)
        {
            if (!TryConvertPlayerId(senderPlayer, out uint senderClientId)) return;
            GetShooterManager()?.ReceiveQuickReloadRequest(senderClientId, data.request);
        }

        private void HandleFixJamRequestServer(
            PlayerID senderPlayer,
            GC2ShooterFixJamRequestPacket data,
            bool asServer)
        {
            if (asServer) DispatchFixJamRequestOnServer(senderPlayer, data);
        }

        private void DispatchFixJamRequestOnServer(PlayerID senderPlayer, GC2ShooterFixJamRequestPacket data)
        {
            if (!TryConvertPlayerId(senderPlayer, out uint senderClientId)) return;
            GetShooterManager()?.ReceiveFixJamRequest(senderClientId, data.request);
        }

        private void HandleChargeStartRequestServer(
            PlayerID senderPlayer,
            GC2ShooterChargeStartRequestPacket data,
            bool asServer)
        {
            if (asServer) DispatchChargeStartRequestOnServer(senderPlayer, data);
        }

        private void DispatchChargeStartRequestOnServer(
            PlayerID senderPlayer,
            GC2ShooterChargeStartRequestPacket data)
        {
            if (!TryConvertPlayerId(senderPlayer, out uint senderClientId)) return;
            GetShooterManager()?.ReceiveChargeStartRequest(senderClientId, data.request);
        }

        private void HandleChargeCancelRequestServer(
            PlayerID senderPlayer,
            GC2ShooterChargeCancelRequestPacket data,
            bool asServer)
        {
            if (asServer) DispatchChargeCancelRequestOnServer(senderPlayer, data);
        }

        private void DispatchChargeCancelRequestOnServer(
            PlayerID senderPlayer,
            GC2ShooterChargeCancelRequestPacket data)
        {
            if (!TryConvertPlayerId(senderPlayer, out uint senderClientId)) return;
            GetShooterManager()?.ReceiveChargeCancelRequest(senderClientId, data.request);
        }

        private void HandleSightSwitchRequestServer(
            PlayerID senderPlayer,
            GC2ShooterSightSwitchRequestPacket data,
            bool asServer)
        {
            if (asServer) DispatchSightSwitchRequestOnServer(senderPlayer, data);
        }

        private void DispatchSightSwitchRequestOnServer(
            PlayerID senderPlayer,
            GC2ShooterSightSwitchRequestPacket data)
        {
            if (!TryConvertPlayerId(senderPlayer, out uint senderClientId)) return;
            GetShooterManager()?.ReceiveSightSwitchRequest(senderClientId, data.request);
        }

        private void HandleWeaponStateServer(PlayerID senderPlayer, GC2ShooterWeaponStatePacket data, bool asServer)
        {
            if (!asServer) return;
            DispatchWeaponStateOnServer(senderPlayer, data);
        }

        private void HandleAimStateServer(PlayerID senderPlayer, GC2ShooterAimStatePacket data, bool asServer)
        {
            if (!asServer) return;
            DispatchAimStateOnServer(senderPlayer, data);
        }

        private void DispatchWeaponStateOnServer(PlayerID senderPlayer, GC2ShooterWeaponStatePacket data)
        {
            if (!TryConvertPlayerId(senderPlayer, out uint senderClientId))
            {
                LogDiagnostics($"dropped weapon state server dispatch: could not convert sender {senderPlayer}");
                return;
            }
            if (data.characterNetworkId == 0)
            {
                LogDiagnostics("dropped weapon state server dispatch: character network id is 0");
                return;
            }

            LogDiagnostics(
                $"server received weapon state sender={senderClientId} character={data.characterNetworkId} " +
                $"weaponHash={data.state.WeaponHash} ammo={data.state.AmmoInMagazine} " +
                $"flags=0x{data.state.StateFlags:X2} lean={data.state.LeanAmount:F1}/{data.state.LeanDecay:F2}");

            if (!IsAuthorizedStateSender(data.characterNetworkId, senderClientId, out uint ownerClientId))
            {
                LogDiagnostics(
                    $"rejected weapon state sender={senderClientId} owner={ownerClientId} " +
                    $"character={data.characterNetworkId}; ownership was not positively verified");
                return;
            }

            m_LatestWeaponStates[data.characterNetworkId] = data;
            ApplyWeaponState(data);
            BroadcastWeaponStateToAllClients(data);
        }

        private void DispatchAimStateOnServer(PlayerID senderPlayer, GC2ShooterAimStatePacket data)
        {
            if (!TryConvertPlayerId(senderPlayer, out uint senderClientId))
            {
                LogDiagnostics($"dropped aim state server dispatch: could not convert sender {senderPlayer}");
                return;
            }
            if (data.characterNetworkId == 0)
            {
                LogDiagnostics("dropped aim state server dispatch: character network id is 0");
                return;
            }

            LogDiagnostics(
                $"server received aim state sender={senderClientId} character={data.characterNetworkId} " +
                $"aiming={data.state.IsAiming} accuracy={data.state.Accuracy} " +
                $"compressed={data.state.CompressedDirection} point={data.state.AimPoint}");

            if (!IsAuthorizedStateSender(data.characterNetworkId, senderClientId, out uint ownerClientId))
            {
                LogDiagnostics(
                    $"rejected aim state sender={senderClientId} owner={ownerClientId} " +
                    $"character={data.characterNetworkId}; ownership was not positively verified");
                return;
            }

            m_LatestAimStates[data.characterNetworkId] = data;
            ApplyAimState(data);
            BroadcastAimStateToAllClients(data);
        }

        private void HandleShotResponseClient(PlayerID senderPlayer, GC2ShooterShotResponsePacket data, bool asServer)
        {
            if (asServer) return;
            LogDiagnostics(
                $"client received shot response req={data.response.RequestId} actor={data.response.ActorNetworkId} " +
                $"validated={data.response.Validated} reason={data.response.RejectionReason}");
            GetShooterManager()?.ReceiveShotResponse(data.response);
        }

        private void HandleShotBroadcastClient(PlayerID senderPlayer, GC2ShooterShotBroadcastPacket data, bool asServer)
        {
            if (asServer) return;
            LogDiagnostics(
                $"client received shot broadcast shooter={data.broadcast.ShooterNetworkId} " +
                $"weaponHash={data.broadcast.WeaponHash} muzzle={data.broadcast.MuzzlePosition} hitPoint={data.broadcast.HitPoint}");
            GetShooterManager()?.ReceiveShotBroadcast(data.broadcast);
        }

        private void HandleHitResponseClient(PlayerID senderPlayer, GC2ShooterHitResponsePacket data, bool asServer)
        {
            if (asServer) return;
            LogDiagnostics(
                $"client received hit response req={data.response.RequestId} actor={data.response.ActorNetworkId} " +
                $"validated={data.response.Validated} reason={data.response.RejectionReason}");
            GetShooterManager()?.ReceiveHitResponse(data.response);
        }

        private void HandleHitBroadcastClient(PlayerID senderPlayer, GC2ShooterHitBroadcastPacket data, bool asServer)
        {
            NetworkShooterDebug.LogPhysics(
                "PurrNetReceiveHitClient",
                $"sender={senderPlayer.id} asServer={asServer} subscribedClient={m_SubscribedClient} " +
                $"manager={(ActiveManager != null)} client={(ActiveManager != null && ActiveManager.isClient)} " +
                $"server={(ActiveManager != null && ActiveManager.isServer)} " +
                $"shooter={data.broadcast.ShooterNetworkId} target={data.broadcast.TargetNetworkId} " +
                $"weaponHash={data.broadcast.WeaponHash} point={data.broadcast.HitPoint} " +
                $"normal={data.broadcast.HitNormal} impactMotion={data.broadcast.HasImpactMotion}",
                this);
            if (asServer) return;
            LogDiagnostics(
                $"client received hit broadcast shooter={data.broadcast.ShooterNetworkId} " +
                $"target={data.broadcast.TargetNetworkId} weaponHash={data.broadcast.WeaponHash} point={data.broadcast.HitPoint}");
            GetShooterManager()?.ReceiveHitBroadcast(data.broadcast);
        }

        private void HandleReloadResponseClient(PlayerID senderPlayer, GC2ShooterReloadResponsePacket data, bool asServer)
        {
            if (asServer) return;
            LogDiagnostics(
                $"[ShooterAmmoDebug] client received reload response req={data.response.RequestId} " +
                $"actor={data.response.ActorNetworkId} validated={data.response.Validated} " +
                $"reason={data.response.RejectionReason}");
            GetShooterManager()?.ReceiveReloadResponse(data.response);
        }

        private void HandleReloadBroadcastClient(PlayerID senderPlayer, GC2ShooterReloadBroadcastPacket data, bool asServer)
        {
            if (asServer) return;
            LogDiagnostics(
                $"[ShooterAmmoDebug] client received reload broadcast character={data.broadcast.CharacterNetworkId} " +
                $"weaponHash={data.broadcast.WeaponHash} event={data.broadcast.EventType} " +
                $"ammo={data.broadcast.NewAmmoCount}");
            GetShooterManager()?.ReceiveReloadBroadcast(data.broadcast);
        }

        private void HandleFixJamResponseClient(
            PlayerID senderPlayer,
            GC2ShooterFixJamResponsePacket data,
            bool asServer)
        {
            if (!asServer) GetShooterManager()?.ReceiveFixJamResponse(data.response);
        }

        private void HandleJamBroadcastClient(
            PlayerID senderPlayer,
            GC2ShooterJamBroadcastPacket data,
            bool asServer)
        {
            if (!asServer) GetShooterManager()?.ReceiveJamBroadcast(data.broadcast);
        }

        private void HandleFixJamBroadcastClient(
            PlayerID senderPlayer,
            GC2ShooterFixJamBroadcastPacket data,
            bool asServer)
        {
            if (!asServer) GetShooterManager()?.ReceiveFixJamBroadcast(data.broadcast);
        }

        private void HandleChargeStartResponseClient(
            PlayerID senderPlayer,
            GC2ShooterChargeStartResponsePacket data,
            bool asServer)
        {
            if (!asServer) GetShooterManager()?.ReceiveChargeStartResponse(data.response);
        }

        private void HandleChargeBroadcastClient(
            PlayerID senderPlayer,
            GC2ShooterChargeBroadcastPacket data,
            bool asServer)
        {
            if (!asServer) GetShooterManager()?.ReceiveChargeBroadcast(data.broadcast);
        }

        private void HandleSightSwitchResponseClient(
            PlayerID senderPlayer,
            GC2ShooterSightSwitchResponsePacket data,
            bool asServer)
        {
            if (!asServer) GetShooterManager()?.ReceiveSightSwitchResponse(data.response);
        }

        private void HandleSightSwitchBroadcastClient(
            PlayerID senderPlayer,
            GC2ShooterSightSwitchBroadcastPacket data,
            bool asServer)
        {
            if (!asServer) GetShooterManager()?.ReceiveSightSwitchBroadcast(data.broadcast);
        }

        private void HandleCharacterSnapshotClient(
            PlayerID senderPlayer,
            GC2ShooterCharacterSnapshotPacket data,
            bool asServer)
        {
            if (asServer || data.snapshot.CharacterNetworkId == 0) return;

            var weaponPacket = new GC2ShooterWeaponStatePacket
            {
                characterNetworkId = data.snapshot.CharacterNetworkId,
                state = data.snapshot.WeaponState
            };
            var aimPacket = new GC2ShooterAimStatePacket
            {
                characterNetworkId = data.snapshot.CharacterNetworkId,
                state = data.snapshot.AimState
            };

            ApplyWeaponState(weaponPacket);
            ApplyAimState(aimPacket);
        }

        private void HandleImpactPropSnapshotClient(
            PlayerID senderPlayer,
            GC2ShooterImpactPropSnapshotPacket data,
            bool asServer)
        {
            if (asServer || data.snapshot.PropNetworkId == 0) return;
            if (NetworkShooterImpactProp.TryApplySnapshot(data.snapshot)) return;

            AddBounded(
                m_PendingImpactPropSnapshots,
                data.snapshot.PropNetworkId,
                data.snapshot);
            LogDiagnostics(
                $"queued impact prop snapshot until scene prop registers id={data.snapshot.PropNetworkId}");
        }

        private void HandleWeaponStateClient(PlayerID senderPlayer, GC2ShooterWeaponStatePacket data, bool asServer)
        {
            if (asServer) return;
            LogDiagnostics(
                $"client received weapon state character={data.characterNetworkId} weaponHash={data.state.WeaponHash} " +
                $"ammo={data.state.AmmoInMagazine} flags=0x{data.state.StateFlags:X2} " +
                $"lean={data.state.LeanAmount:F1}/{data.state.LeanDecay:F2}");
            ApplyWeaponState(data);
        }

        private void HandleAimStateClient(PlayerID senderPlayer, GC2ShooterAimStatePacket data, bool asServer)
        {
            if (asServer) return;
            LogDiagnostics(
                $"client received aim state character={data.characterNetworkId} aiming={data.state.IsAiming} " +
                $"accuracy={data.state.Accuracy} compressed={data.state.CompressedDirection} point={data.state.AimPoint}");
            ApplyAimState(data);
        }

        private void ApplyWeaponState(GC2ShooterWeaponStatePacket data)
        {
            if (data.characterNetworkId == 0)
            {
                LogDiagnostics("weapon state apply skipped: character network id is 0");
                return;
            }
            if (!m_RegisteredControllers.TryGetValue(data.characterNetworkId, out var controller) || controller == null)
            {
                RefreshControllerRegistry(force: true);
                m_RegisteredControllers.TryGetValue(data.characterNetworkId, out controller);
            }

            if (controller == null)
            {
                LogDiagnostics(
                    $"weapon state queued: controller not found character={data.characterNetworkId} " +
                    $"weaponHash={data.state.WeaponHash}");
                AddBounded(m_PendingWeaponStates, data.characterNetworkId, data);
                return;
            }

            ShooterAssetEntry entry = ResolveWeaponAssets(data.state.WeaponHash);
            LogDiagnostics(
                $"applying weapon state character={data.characterNetworkId} controller={controller.name} " +
                $"weaponHash={data.state.WeaponHash} asset={(entry.Weapon != null ? entry.Weapon.name : "null")} " +
                $"prefab={(entry.Prefab != null ? entry.Prefab.name : "null")} flags=0x{data.state.StateFlags:X2} " +
                $"lean={data.state.LeanAmount:F1}/{data.state.LeanDecay:F2}");
            ApplyWeaponStateToController(controller, data);
        }

        private void ApplyAimState(GC2ShooterAimStatePacket data)
        {
            if (data.characterNetworkId == 0)
            {
                LogDiagnostics("aim state apply skipped: character network id is 0");
                return;
            }
            if (!m_RegisteredControllers.TryGetValue(data.characterNetworkId, out var controller) || controller == null)
            {
                RefreshControllerRegistry(force: true);
                m_RegisteredControllers.TryGetValue(data.characterNetworkId, out controller);
            }

            if (controller == null)
            {
                LogDiagnostics($"aim state queued: controller not found character={data.characterNetworkId}");
                AddBounded(m_PendingAimStates, data.characterNetworkId, data);
                return;
            }

            LogDiagnostics(
                $"applying aim state character={data.characterNetworkId} controller={controller.name} " +
                $"aiming={data.state.IsAiming} accuracy={data.state.Accuracy} " +
                $"compressed={data.state.CompressedDirection} point={data.state.AimPoint}");
            controller.ApplyRemoteAimState(data.state);
        }

        private void EnsureWeaponEquippedOnServer(uint characterNetworkId, int weaponHash)
        {
            if (characterNetworkId == 0 || weaponHash == 0) return;

            if (m_RegisteredControllers.TryGetValue(characterNetworkId, out var controller) &&
                controller != null &&
                controller.IsShooterWeaponEquipped(weaponHash))
            {
                LogDiagnostics(
                    $"ensuring shooter weapon equipped skipped; already equipped on server " +
                    $"character={characterNetworkId} weaponHash={weaponHash}");
                return;
            }

            LogDiagnostics(
                $"ensuring shooter weapon equipped on server character={characterNetworkId} weaponHash={weaponHash}");

            ApplyWeaponState(new GC2ShooterWeaponStatePacket
            {
                characterNetworkId = characterNetworkId,
                state = new NetworkWeaponState
                {
                    WeaponHash = weaponHash,
                    SightHash = 0,
                    AmmoInMagazine = 0,
                    StateFlags = 0
                }
            });
        }

        private void FlushPendingPersistentStates()
        {
            m_RemoveBuffer.Clear();
            foreach (var pair in m_PendingWeaponStates)
            {
                if (!m_RegisteredControllers.TryGetValue(pair.Key, out var controller) || controller == null) continue;
                ApplyWeaponStateToController(controller, pair.Value);
                m_RemoveBuffer.Add(pair.Key);
            }
            for (int i = 0; i < m_RemoveBuffer.Count; i++)
            {
                m_PendingWeaponStates.Remove(m_RemoveBuffer[i]);
            }

            m_RemoveBuffer.Clear();
            foreach (var pair in m_PendingAimStates)
            {
                if (!m_RegisteredControllers.TryGetValue(pair.Key, out var controller) || controller == null) continue;
                controller.ApplyRemoteAimState(pair.Value.state);
                m_RemoveBuffer.Add(pair.Key);
            }
            for (int i = 0; i < m_RemoveBuffer.Count; i++)
            {
                m_PendingAimStates.Remove(m_RemoveBuffer[i]);
            }

            m_RemoveBuffer.Clear();
            foreach (var pair in m_PendingImpactPropSnapshots)
            {
                if (!NetworkShooterImpactProp.TryApplySnapshot(pair.Value)) continue;
                m_RemoveBuffer.Add(pair.Key);
            }
            for (int i = 0; i < m_RemoveBuffer.Count; i++)
            {
                m_PendingImpactPropSnapshots.Remove(m_RemoveBuffer[i]);
            }
        }

        private void FlushPendingPersistentState(uint networkId, NetworkShooterController controller)
        {
            if (controller == null) return;

            if (m_PendingWeaponStates.TryGetValue(networkId, out var weaponPacket))
            {
                m_PendingWeaponStates.Remove(networkId);
                ApplyWeaponStateToController(controller, weaponPacket);
            }

            if (m_PendingAimStates.TryGetValue(networkId, out var aimPacket))
            {
                m_PendingAimStates.Remove(networkId);
                controller.ApplyRemoteAimState(aimPacket.state);
            }
        }

        private void ApplyWeaponStateToController(
            NetworkShooterController controller,
            GC2ShooterWeaponStatePacket data)
        {
            ShooterAssetEntry entry = ResolveWeaponAssets(data.state.WeaponHash);
            if (data.state.WeaponHash != 0 &&
                entry.Weapon == null &&
                m_MissingWeaponAssetDiagnostics.Add(data.state.WeaponHash))
            {
                Debug.LogWarning(
                    $"weapon state asset missing character={data.characterNetworkId} hash={data.state.WeaponHash}; " +
                    "the state remains authoritative but the remote model cannot be reconstructed. " +
                    "Add a Weapon Registration to PurrNetShooterTransportBridge.",
                    this);
            }

            controller.ApplyRemoteWeaponState(data.state, entry.Weapon, entry.Prefab, entry.Handle);
        }

        private static void AddBounded<TKey, TValue>(
            Dictionary<TKey, TValue> dictionary,
            TKey key,
            TValue value)
        {
            if (!dictionary.ContainsKey(key) && dictionary.Count >= MAX_PENDING_PERSISTENT_STATES)
            {
                TKey oldestKey = default;
                bool found = false;
                foreach (var pair in dictionary)
                {
                    oldestKey = pair.Key;
                    found = true;
                    break;
                }

                if (found) dictionary.Remove(oldestKey);
            }

            dictionary[key] = value;
        }

        private ShooterAssetEntry ResolveWeaponAssets(int weaponHash)
        {
            if (weaponHash != 0 && m_WeaponAssets.TryGetValue(weaponHash, out var entry))
            {
                return entry;
            }

            if (weaponHash != 0 &&
                NetworkShooterManager.TryGetShooterWeaponRegistryEntry(weaponHash, out var registryEntry))
            {
                return new ShooterAssetEntry
                {
                    Weapon = registryEntry.Weapon,
                    Prefab = registryEntry.ModelPrefab,
                    Handle = registryEntry.Handle
                };
            }

            return default;
        }

        private NetworkCharacter ResolveNetworkCharacter(uint networkId)
        {
            Character character = CoreBridge != null ? CoreBridge.ResolveCharacter(networkId) : null;
            if (character != null)
            {
                var networkCharacter = character.GetComponent<NetworkCharacter>();
                if (networkCharacter != null) return networkCharacter;
            }

            return m_RegisteredControllers.TryGetValue(networkId, out var controller) && controller != null
                ? controller.GetComponent<NetworkCharacter>()
                : null;
        }

        private bool IsAuthorizedStateSender(
            uint characterNetworkId,
            uint senderClientId,
            out uint ownerClientId)
        {
            ownerClientId = NetworkTransportBridge.InvalidClientId;

            var core = CoreBridge;
            if (core != null && core.TryGetCharacterOwner(characterNetworkId, out ownerClientId))
            {
                return ownerClientId == senderClientId;
            }

            // During host startup the ownership registry can trail the locally-owned
            // character by one frame. Accept only a positively identified local-player
            // loopback in that narrow case; unknown remote senders remain rejected.
            var nm = ActiveManager;
            if (nm == null || !nm.isServer || !nm.isLocalPlayerReady) return false;
            if (!TryConvertPlayerId(nm.localPlayer, out uint localClientId) ||
                localClientId != senderClientId) return false;
            if (!m_RegisteredControllers.TryGetValue(characterNetworkId, out var controller) ||
                controller == null) return false;

            NetworkCharacter character = controller.GetComponent<NetworkCharacter>();
            if (character == null || !character.IsOwnerInstance) return false;

            ownerClientId = localClientId;
            return true;
        }

        private float GetNetworkTime()
        {
            return CoreBridge != null ? CoreBridge.ServerTime : Time.time;
        }

        private static NetworkShooterManager GetShooterManager()
        {
            return NetworkShooterManager.Instance != null
                ? NetworkShooterManager.Instance
                : FindFirstObjectByType<NetworkShooterManager>();
        }

        private static T GetArrayValue<T>(T[] values, int index) where T : UnityEngine.Object
        {
            return values != null && index >= 0 && index < values.Length ? values[index] : null;
        }

        private static bool TryConvertPlayerId(PlayerID playerId, out uint clientId)
        {
            ulong raw = playerId.id;
            return NetworkTransportBridge.TryConvertSenderClientId(raw, out clientId);
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
    }
}
#endif
