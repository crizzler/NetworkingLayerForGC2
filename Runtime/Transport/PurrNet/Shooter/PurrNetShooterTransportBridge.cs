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
        [Tooltip("Shooter weapons whose hashes should be registered for remote playback.")]
        [SerializeField] private ShooterWeapon[] m_RegisterWeapons = Array.Empty<ShooterWeapon>();

        [Tooltip("Weapon model prefabs matching the registered weapon array.")]
        [SerializeField] private GameObject[] m_RegisterWeaponPrefabs = Array.Empty<GameObject>();

        [Tooltip("Handle assets matching the registered weapon array.")]
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

            if (!m_AutoRegisterSceneControllers) return;
            if (Time.unscaledTime < m_NextControllerScanTime) return;

            m_NextControllerScanTime = Time.unscaledTime + Mathf.Max(0.05f, m_ControllerScanInterval);
            RefreshControllerRegistry(force: false);
        }

        private void OnDisable()
        {
            UnhookNetworkManager();
            UnwireShooterManager();
            UnregisterAllControllers();
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

            if (m_SubscribedServer)
            {
                nm.Unsubscribe<GC2ShooterShotRequestPacket>(HandleShotRequestServer, true);
                nm.Unsubscribe<GC2ShooterHitRequestPacket>(HandleHitRequestServer, true);
                nm.Unsubscribe<GC2ShooterReloadRequestPacket>(HandleReloadRequestServer, true);
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
                nm.Unsubscribe<GC2ShooterWeaponStatePacket>(HandleWeaponStateClient, false);
                nm.Unsubscribe<GC2ShooterAimStatePacket>(HandleAimStateClient, false);
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
                manager.Subscribe<GC2ShooterWeaponStatePacket>(HandleWeaponStateClient, false);
                manager.Subscribe<GC2ShooterAimStatePacket>(HandleAimStateClient, false);
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
                manager.Unsubscribe<GC2ShooterWeaponStatePacket>(HandleWeaponStateClient, false);
                manager.Unsubscribe<GC2ShooterAimStatePacket>(HandleAimStateClient, false);
                m_SubscribedClient = false;
                LogDiagnostics("unsubscribed shooter client packets");
            }

            WireShooterManager();
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

            manager.SendShotResponseToClient -= SendShotResponseToClient;
            manager.SendShotResponseToClient += SendShotResponseToClient;
            manager.SendHitResponseToClient -= SendHitResponseToClient;
            manager.SendHitResponseToClient += SendHitResponseToClient;
            manager.SendReloadResponseToClient -= SendReloadResponseToClient;
            manager.SendReloadResponseToClient += SendReloadResponseToClient;

            manager.BroadcastShotToAllClients -= BroadcastShotToAllClients;
            manager.BroadcastShotToAllClients += BroadcastShotToAllClients;
            manager.BroadcastHitToAllClients -= BroadcastHitToAllClients;
            manager.BroadcastHitToAllClients += BroadcastHitToAllClients;
            manager.BroadcastReloadToAllClients -= BroadcastReloadToAllClients;
            manager.BroadcastReloadToAllClients += BroadcastReloadToAllClients;

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
            manager.SendShotResponseToClient -= SendShotResponseToClient;
            manager.SendHitResponseToClient -= SendHitResponseToClient;
            manager.SendReloadResponseToClient -= SendReloadResponseToClient;
            manager.BroadcastShotToAllClients -= BroadcastShotToAllClients;
            manager.BroadcastHitToAllClients -= BroadcastHitToAllClients;
            manager.BroadcastReloadToAllClients -= BroadcastReloadToAllClients;

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
            if (m_RegisterWeapons == null) return;

            for (int i = 0; i < m_RegisterWeapons.Length; i++)
            {
                ShooterWeapon weapon = m_RegisterWeapons[i];
                if (weapon == null) continue;

                NetworkShooterManager.RegisterShooterWeapon(
                    weapon,
                    GetArrayValue(m_RegisterWeaponPrefabs, i),
                    GetArrayValue(m_RegisterWeaponHandles, i));
                m_WeaponAssets[weapon.Id.Hash] = new ShooterAssetEntry
                {
                    Weapon = weapon,
                    Prefab = GetArrayValue(m_RegisterWeaponPrefabs, i),
                    Handle = GetArrayValue(m_RegisterWeaponHandles, i)
                };

                LogDiagnostics(
                    $"registered shooter asset weapon={weapon.name} hash={weapon.Id.Hash} " +
                    $"prefab={(GetArrayValue(m_RegisterWeaponPrefabs, i) != null ? GetArrayValue(m_RegisterWeaponPrefabs, i).name : "null")} " +
                    $"handle={(GetArrayValue(m_RegisterWeaponHandles, i) != null ? GetArrayValue(m_RegisterWeaponHandles, i).name : "null")}");
            }

            m_AssetsRegistered = true;
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
            bool isLocalClient =
                networkCharacter.IsOwnerInstance &&
                networkCharacter.Role == NetworkCharacter.NetworkRole.LocalClient;

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
            }
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
            SendAimStateToServer(networkId, state);
        }

        private void SendShotRequestToServer(NetworkShotRequest request)
        {
            var nm = ActiveManager;
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

            var core = CoreBridge;
            if (core != null &&
                core.TryGetCharacterOwner(data.characterNetworkId, out uint ownerClientId) &&
                ownerClientId != senderClientId)
            {
                LogDiagnostics(
                    $"rejected weapon state sender={senderClientId} owner={ownerClientId} character={data.characterNetworkId}");
                return;
            }

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

            var core = CoreBridge;
            if (core != null &&
                core.TryGetCharacterOwner(data.characterNetworkId, out uint ownerClientId) &&
                ownerClientId != senderClientId)
            {
                LogDiagnostics(
                    $"rejected aim state sender={senderClientId} owner={ownerClientId} character={data.characterNetworkId}");
                return;
            }

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
                    $"weapon state apply skipped: controller not found character={data.characterNetworkId} " +
                    $"weaponHash={data.state.WeaponHash}");
                return;
            }

            ShooterAssetEntry entry = ResolveWeaponAssets(data.state.WeaponHash);
            LogDiagnostics(
                $"applying weapon state character={data.characterNetworkId} controller={controller.name} " +
                $"weaponHash={data.state.WeaponHash} asset={(entry.Weapon != null ? entry.Weapon.name : "null")} " +
                $"prefab={(entry.Prefab != null ? entry.Prefab.name : "null")} flags=0x{data.state.StateFlags:X2} " +
                $"lean={data.state.LeanAmount:F1}/{data.state.LeanDecay:F2}");
            controller.ApplyRemoteWeaponState(data.state, entry.Weapon, entry.Prefab, entry.Handle);
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
                LogDiagnostics($"aim state apply skipped: controller not found character={data.characterNetworkId}");
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

        private ShooterAssetEntry ResolveWeaponAssets(int weaponHash)
        {
            if (weaponHash != 0 && m_WeaponAssets.TryGetValue(weaponHash, out var entry))
            {
                return entry;
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
