#if GC2_MELEE
using System;
using System.Collections.Generic;
using Arawn.GameCreator2.Networking.Transport.PurrNet;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Melee;
using PurrNet;
using PurrNet.Transports;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Melee.Transport.PurrNet
{
    [DisallowMultipleComponent]
    [AddComponentMenu("Game Creator/Network/Transport/PurrNet Melee Bridge")]
    [DefaultExecutionOrder(-350)]
    public sealed class PurrNetMeleeTransportBridge : MonoBehaviour
    {
        [Header("PurrNet")]
        [Tooltip("Optional reference to a specific NetworkManager. Leave empty to use NetworkManager.main.")]
        [SerializeField] private NetworkManager m_NetworkManager;

        [Tooltip("Optional reference to the core GC2 PurrNet bridge used for character lookup and network time.")]
        [SerializeField] private PurrNetTransportBridge m_CoreBridge;

        [Tooltip("Reliable channel used for melee requests, responses, and animation broadcasts.")]
        [SerializeField] private Channel m_Channel = Channel.ReliableOrdered;

        [Header("Melee Assets")]
        [Tooltip("Weapons whose hashes and combo skills should be registered for remote playback.")]
        [SerializeField] private MeleeWeapon[] m_RegisterWeapons = Array.Empty<MeleeWeapon>();

        [Header("Controllers")]
        [Tooltip("Automatically finds NetworkMeleeController components on spawned NetworkCharacter objects.")]
        [SerializeField] private bool m_AutoRegisterSceneControllers = true;

        [Min(0.05f)]
        [SerializeField] private float m_ControllerScanInterval = 0.25f;

        [Header("Debug")]
        [SerializeField] private bool m_LogBlockPackets = false;
        [SerializeField] private bool m_LogSkillPackets = false;

        private readonly Dictionary<uint, NetworkMeleeController> m_RegisteredControllers = new(32);
        private readonly List<uint> m_RemoveBuffer = new(16);

        private NetworkManager m_HookedManager;
        private bool m_SubscribedServer;
        private bool m_SubscribedClient;
        private bool m_ManagerInitialized;
        private bool m_LastServer;
        private bool m_LastClient;
        private bool m_AssetsRegistered;
        private float m_NextControllerScanTime;

        private NetworkManager ActiveManager => m_NetworkManager ? m_NetworkManager : NetworkManager.main;

        private void LogBlockPacket(string message)
        {
            if (!m_LogBlockPackets) return;
            Debug.Log($"[PurrNetMeleeTransportBridge] {message}", this);
        }

        private void LogSkillPacket(string message)
        {
            if (!m_LogSkillPackets && !NetworkMeleeDebug.ForcePacketDiagnostics) return;
            Debug.Log($"[NetworkMeleeSkillDebug][PurrNetBridge] {message}", this);
        }

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

        private void Awake()
        {
            if (m_NetworkManager == null) m_NetworkManager = NetworkManager.main;
            if (m_CoreBridge == null) m_CoreBridge = NetworkTransportBridge.Active as PurrNetTransportBridge;
        }

        private void OnEnable()
        {
            TryHookNetworkManager();
            WireMeleeManager();
            RegisterConfiguredAssets();
            RefreshControllerRegistry(force: true);
        }

        private void Start()
        {
            TryHookNetworkManager();
            WireMeleeManager();
            RegisterConfiguredAssets();
            RefreshControllerRegistry(force: true);
        }

        private void Update()
        {
            TryHookNetworkManager();
            WireMeleeManager();
            RegisterConfiguredAssets();

            if (!m_AutoRegisterSceneControllers) return;
            if (Time.unscaledTime < m_NextControllerScanTime) return;

            m_NextControllerScanTime = Time.unscaledTime + Mathf.Max(0.05f, m_ControllerScanInterval);
            RefreshControllerRegistry(force: false);
        }

        private void OnDisable()
        {
            UnhookNetworkManager();
            UnwireMeleeManager();
            UnregisterAllControllers();
        }

        private void TryHookNetworkManager()
        {
            var nm = ActiveManager;
            if (nm == null) return;

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
        }

        private void UnhookNetworkManager()
        {
            var nm = m_HookedManager;
            if (nm == null) return;

            nm.onNetworkStarted -= HandleNetworkStarted;
            nm.onNetworkShutdown -= HandleNetworkShutdown;

            if (m_SubscribedServer)
            {
                nm.Unsubscribe<GC2MeleeHitRequestPacket>(HandleHitRequestServer, true);
                nm.Unsubscribe<GC2MeleeBlockRequestPacket>(HandleBlockRequestServer, true);
                nm.Unsubscribe<GC2MeleeSkillRequestPacket>(HandleSkillRequestServer, true);
                nm.Unsubscribe<GC2MeleeChargeRequestPacket>(HandleChargeRequestServer, true);
                nm.Unsubscribe<GC2MeleeWeaponStatePacket>(HandleWeaponStateServer, true);
                m_SubscribedServer = false;
            }

            if (m_SubscribedClient)
            {
                nm.Unsubscribe<GC2MeleeHitResponsePacket>(HandleHitResponseClient, false);
                nm.Unsubscribe<GC2MeleeHitBroadcastPacket>(HandleHitBroadcastClient, false);
                nm.Unsubscribe<GC2MeleeBlockResponsePacket>(HandleBlockResponseClient, false);
                nm.Unsubscribe<GC2MeleeBlockBroadcastPacket>(HandleBlockBroadcastClient, false);
                nm.Unsubscribe<GC2MeleeSkillResponsePacket>(HandleSkillResponseClient, false);
                nm.Unsubscribe<GC2MeleeSkillBroadcastPacket>(HandleSkillBroadcastClient, false);
                nm.Unsubscribe<GC2MeleeChargeResponsePacket>(HandleChargeResponseClient, false);
                nm.Unsubscribe<GC2MeleeChargeBroadcastPacket>(HandleChargeBroadcastClient, false);
                nm.Unsubscribe<GC2MeleeReactionBroadcastPacket>(HandleReactionBroadcastClient, false);
                nm.Unsubscribe<GC2MeleeWeaponStatePacket>(HandleWeaponStateClient, false);
                m_SubscribedClient = false;
            }

            m_HookedManager = null;
        }

        private void HandleNetworkStarted(NetworkManager manager, bool asServer)
        {
            if (asServer && !m_SubscribedServer)
            {
                manager.Subscribe<GC2MeleeHitRequestPacket>(HandleHitRequestServer, true);
                manager.Subscribe<GC2MeleeBlockRequestPacket>(HandleBlockRequestServer, true);
                manager.Subscribe<GC2MeleeSkillRequestPacket>(HandleSkillRequestServer, true);
                manager.Subscribe<GC2MeleeChargeRequestPacket>(HandleChargeRequestServer, true);
                manager.Subscribe<GC2MeleeWeaponStatePacket>(HandleWeaponStateServer, true);
                m_SubscribedServer = true;
            }
            else if (!asServer && !m_SubscribedClient)
            {
                manager.Subscribe<GC2MeleeHitResponsePacket>(HandleHitResponseClient, false);
                manager.Subscribe<GC2MeleeHitBroadcastPacket>(HandleHitBroadcastClient, false);
                manager.Subscribe<GC2MeleeBlockResponsePacket>(HandleBlockResponseClient, false);
                manager.Subscribe<GC2MeleeBlockBroadcastPacket>(HandleBlockBroadcastClient, false);
                manager.Subscribe<GC2MeleeSkillResponsePacket>(HandleSkillResponseClient, false);
                manager.Subscribe<GC2MeleeSkillBroadcastPacket>(HandleSkillBroadcastClient, false);
                manager.Subscribe<GC2MeleeChargeResponsePacket>(HandleChargeResponseClient, false);
                manager.Subscribe<GC2MeleeChargeBroadcastPacket>(HandleChargeBroadcastClient, false);
                manager.Subscribe<GC2MeleeReactionBroadcastPacket>(HandleReactionBroadcastClient, false);
                manager.Subscribe<GC2MeleeWeaponStatePacket>(HandleWeaponStateClient, false);
                m_SubscribedClient = true;
            }

            WireMeleeManager();
            RefreshControllerRegistry(force: true);
        }

        private void HandleNetworkShutdown(NetworkManager manager, bool asServer)
        {
            if (asServer && m_SubscribedServer)
            {
                manager.Unsubscribe<GC2MeleeHitRequestPacket>(HandleHitRequestServer, true);
                manager.Unsubscribe<GC2MeleeBlockRequestPacket>(HandleBlockRequestServer, true);
                manager.Unsubscribe<GC2MeleeSkillRequestPacket>(HandleSkillRequestServer, true);
                manager.Unsubscribe<GC2MeleeChargeRequestPacket>(HandleChargeRequestServer, true);
                manager.Unsubscribe<GC2MeleeWeaponStatePacket>(HandleWeaponStateServer, true);
                m_SubscribedServer = false;
            }
            else if (!asServer && m_SubscribedClient)
            {
                manager.Unsubscribe<GC2MeleeHitResponsePacket>(HandleHitResponseClient, false);
                manager.Unsubscribe<GC2MeleeHitBroadcastPacket>(HandleHitBroadcastClient, false);
                manager.Unsubscribe<GC2MeleeBlockResponsePacket>(HandleBlockResponseClient, false);
                manager.Unsubscribe<GC2MeleeBlockBroadcastPacket>(HandleBlockBroadcastClient, false);
                manager.Unsubscribe<GC2MeleeSkillResponsePacket>(HandleSkillResponseClient, false);
                manager.Unsubscribe<GC2MeleeSkillBroadcastPacket>(HandleSkillBroadcastClient, false);
                manager.Unsubscribe<GC2MeleeChargeResponsePacket>(HandleChargeResponseClient, false);
                manager.Unsubscribe<GC2MeleeChargeBroadcastPacket>(HandleChargeBroadcastClient, false);
                manager.Unsubscribe<GC2MeleeReactionBroadcastPacket>(HandleReactionBroadcastClient, false);
                manager.Unsubscribe<GC2MeleeWeaponStatePacket>(HandleWeaponStateClient, false);
                m_SubscribedClient = false;
            }

            WireMeleeManager();
        }

        private void WireMeleeManager()
        {
            NetworkMeleeManager manager = GetMeleeManager();
            if (manager == null) return;

            manager.SendHitRequestToServer -= SendHitRequestToServer;
            manager.SendHitRequestToServer += SendHitRequestToServer;
            manager.SendHitResponseToClient -= SendHitResponseToClient;
            manager.SendHitResponseToClient += SendHitResponseToClient;
            manager.BroadcastHitToAllClients -= BroadcastHitToAllClients;
            manager.BroadcastHitToAllClients += BroadcastHitToAllClients;

            manager.SendBlockRequestToServer -= SendBlockRequestToServer;
            manager.SendBlockRequestToServer += SendBlockRequestToServer;
            manager.SendBlockResponseToClient -= SendBlockResponseToClient;
            manager.SendBlockResponseToClient += SendBlockResponseToClient;
            manager.BroadcastBlockToAllClients -= BroadcastBlockToAllClients;
            manager.BroadcastBlockToAllClients += BroadcastBlockToAllClients;

            manager.SendSkillRequestToServer -= SendSkillRequestToServer;
            manager.SendSkillRequestToServer += SendSkillRequestToServer;
            manager.SendSkillResponseToClient -= SendSkillResponseToClient;
            manager.SendSkillResponseToClient += SendSkillResponseToClient;
            manager.BroadcastSkillToAllClients -= BroadcastSkillToAllClients;
            manager.BroadcastSkillToAllClients += BroadcastSkillToAllClients;

            manager.SendChargeRequestToServer -= SendChargeRequestToServer;
            manager.SendChargeRequestToServer += SendChargeRequestToServer;
            manager.SendChargeResponseToClient -= SendChargeResponseToClient;
            manager.SendChargeResponseToClient += SendChargeResponseToClient;
            manager.BroadcastChargeToAllClients -= BroadcastChargeToAllClients;
            manager.BroadcastChargeToAllClients += BroadcastChargeToAllClients;

            manager.BroadcastReactionToAllClients -= BroadcastReactionToAllClients;
            manager.BroadcastReactionToAllClients += BroadcastReactionToAllClients;

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
            }
        }

        private void UnwireMeleeManager()
        {
            NetworkMeleeManager manager = GetMeleeManager();
            if (manager == null) return;

            manager.SendHitRequestToServer -= SendHitRequestToServer;
            manager.SendHitResponseToClient -= SendHitResponseToClient;
            manager.BroadcastHitToAllClients -= BroadcastHitToAllClients;
            manager.SendBlockRequestToServer -= SendBlockRequestToServer;
            manager.SendBlockResponseToClient -= SendBlockResponseToClient;
            manager.BroadcastBlockToAllClients -= BroadcastBlockToAllClients;
            manager.SendSkillRequestToServer -= SendSkillRequestToServer;
            manager.SendSkillResponseToClient -= SendSkillResponseToClient;
            manager.BroadcastSkillToAllClients -= BroadcastSkillToAllClients;
            manager.SendChargeRequestToServer -= SendChargeRequestToServer;
            manager.SendChargeResponseToClient -= SendChargeResponseToClient;
            manager.BroadcastChargeToAllClients -= BroadcastChargeToAllClients;
            manager.BroadcastReactionToAllClients -= BroadcastReactionToAllClients;

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
                RegisterWeaponAndSkills(m_RegisterWeapons[i]);
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
            for (int i = 0; i < rootIds.Length; i++)
            {
                RegisterComboNode(comboTree, rootIds[i], visited);
            }
        }

        private static void RegisterComboNode(ComboTree comboTree, int nodeId, HashSet<int> visited)
        {
            if (nodeId == ComboTree.NODE_INVALID || !visited.Add(nodeId)) return;

            ComboItem comboItem = comboTree.Get(nodeId);
            if (comboItem?.Skill != null)
            {
                NetworkMeleeManager.RegisterSkill(comboItem.Skill);
            }

            List<int> children = comboTree.Children(nodeId);
            for (int i = 0; i < children.Count; i++)
            {
                RegisterComboNode(comboTree, children[i], visited);
            }
        }

        private void RefreshControllerRegistry(bool force)
        {
            NetworkMeleeManager manager = GetMeleeManager();
            if (manager == null) return;

            PruneControllerRegistry(manager);

            if (!m_AutoRegisterSceneControllers && !force) return;

            var controllers = FindObjectsByType<NetworkMeleeController>(
                FindObjectsInactive.Exclude,
                FindObjectsSortMode.None);

            for (int i = 0; i < controllers.Length; i++)
            {
                RegisterController(manager, controllers[i]);
            }
        }

        private void RegisterController(NetworkMeleeManager manager, NetworkMeleeController controller)
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

                    controller.RegisterCurrentMeleeAssets();
                    controller.OnWeaponStateChanged -= HandleControllerWeaponStateChanged;
                    controller.OnWeaponStateChanged += HandleControllerWeaponStateChanged;
                    if (roleChanged) controller.PublishCurrentWeaponState();
                    LogSkillPacket(
                        $"controller already registered netId={networkId} name={controller.name} " +
                        $"server={isServer} local={isLocalClient} roleChanged={roleChanged}");
                    return;
                }

                existing.OnWeaponStateChanged -= HandleControllerWeaponStateChanged;
                manager.UnregisterController(networkId);
                LogSkillPacket(
                    $"replacing registered controller netId={networkId} old={(existing != null ? existing.name : "null")} " +
                    $"new={controller.name}");
            }

            controller.Initialize(isServer, isLocalClient);
            controller.RegisterCurrentMeleeAssets();
            controller.OnWeaponStateChanged -= HandleControllerWeaponStateChanged;
            controller.OnWeaponStateChanged += HandleControllerWeaponStateChanged;

            m_RegisteredControllers[networkId] = controller;
            manager.RegisterController(networkId, controller);
            controller.PublishCurrentWeaponState();
            LogSkillPacket(
                $"registered controller netId={networkId} name={controller.name} " +
                $"server={isServer} local={isLocalClient} role={networkCharacter.Role} " +
                $"registeredCount={m_RegisteredControllers.Count}");
        }

        private void PruneControllerRegistry(NetworkMeleeManager manager)
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
                }

                manager.UnregisterController(networkId);
                m_RegisteredControllers.Remove(networkId);
            }
        }

        private void UnregisterAllControllers()
        {
            NetworkMeleeManager manager = GetMeleeManager();
            if (manager != null)
            {
                foreach (uint networkId in m_RegisteredControllers.Keys)
                {
                    if (m_RegisteredControllers.TryGetValue(networkId, out var controller) && controller != null)
                    {
                        controller.OnWeaponStateChanged -= HandleControllerWeaponStateChanged;
                    }

                    manager.UnregisterController(networkId);
                }
            }

            m_RegisteredControllers.Clear();
        }

        private void HandleControllerWeaponStateChanged(NetworkMeleeWeaponState state)
        {
            NetworkMeleeController controller = null;
            foreach (var pair in m_RegisteredControllers)
            {
                if (pair.Value == null || !pair.Value.IsLocalClient) continue;
                controller = pair.Value;
                break;
            }

            if (controller == null) return;

            uint networkId = controller.GetComponent<NetworkCharacter>()?.NetworkId ?? 0;
            if (networkId == 0) return;

            SendWeaponStateToServer(networkId, state);
        }

        private void SendHitRequestToServer(NetworkMeleeHitRequest request)
        {
            var nm = ActiveManager;
            if (nm == null || !nm.isClient) return;

            var packet = new GC2MeleeHitRequestPacket { request = request };
            if (nm.isServer)
            {
                if (nm.isLocalPlayerReady) DispatchHitRequestOnServer(nm.localPlayer, packet);
                return;
            }

            nm.SendToServer(packet, m_Channel);
        }

        private void SendBlockRequestToServer(NetworkBlockRequest request)
        {
            var nm = ActiveManager;
            if (nm == null || !nm.isClient)
            {
                LogBlockPacket(
                    $"dropped block request send actor={request.ActorNetworkId} action={request.Action}: " +
                    $"manager={(nm != null ? nm.name : "null")} client={(nm != null && nm.isClient)}");
                return;
            }

            var packet = new GC2MeleeBlockRequestPacket { request = request };
            if (nm.isServer)
            {
                LogBlockPacket(
                    $"dispatching local-host block request actor={request.ActorNetworkId} req={request.RequestId} " +
                    $"action={request.Action} localReady={nm.isLocalPlayerReady}");
                if (nm.isLocalPlayerReady) DispatchBlockRequestOnServer(nm.localPlayer, packet);
                return;
            }

            LogBlockPacket(
                $"sending block request to server actor={request.ActorNetworkId} req={request.RequestId} " +
                $"action={request.Action} shieldHash={request.ShieldHash}");
            nm.SendToServer(packet, m_Channel);
        }

        private void SendSkillRequestToServer(NetworkSkillRequest request)
        {
            var nm = ActiveManager;
            if (nm == null || !nm.isClient)
            {
                LogSkillPacket(
                    $"dropped outbound skill request actor={request.ActorNetworkId} req={request.RequestId} " +
                    $"networkManager={(nm != null)} isClient={nm?.isClient}");
                return;
            }

            var packet = new GC2MeleeSkillRequestPacket { request = request };
            if (nm.isServer)
            {
                LogSkillPacket(
                    $"dispatching local-host skill request actor={request.ActorNetworkId} req={request.RequestId} " +
                    $"corr={request.CorrelationId} skillHash={request.SkillHash} weaponHash={request.WeaponHash} " +
                    $"localReady={nm.isLocalPlayerReady}");
                if (nm.isLocalPlayerReady) DispatchSkillRequestOnServer(nm.localPlayer, packet);
                return;
            }

            LogSkillPacket(
                $"sending skill request to server actor={request.ActorNetworkId} req={request.RequestId} " +
                $"corr={request.CorrelationId} skillHash={request.SkillHash} weaponHash={request.WeaponHash} combo={request.ComboNodeId}");
            nm.SendToServer(packet, m_Channel);
        }

        private void SendChargeRequestToServer(NetworkChargeRequest request)
        {
            var nm = ActiveManager;
            if (nm == null || !nm.isClient) return;

            var packet = new GC2MeleeChargeRequestPacket { request = request };
            if (nm.isServer)
            {
                if (nm.isLocalPlayerReady) DispatchChargeRequestOnServer(nm.localPlayer, packet);
                return;
            }

            nm.SendToServer(packet, m_Channel);
        }

        private void SendWeaponStateToServer(uint characterNetworkId, NetworkMeleeWeaponState state)
        {
            var nm = ActiveManager;
            if (nm == null || !nm.isClient || characterNetworkId == 0) return;

            var packet = new GC2MeleeWeaponStatePacket
            {
                characterNetworkId = characterNetworkId,
                state = state
            };

            if (nm.isServer)
            {
                if (nm.isLocalPlayerReady) DispatchWeaponStateOnServer(nm.localPlayer, packet);
                return;
            }

            nm.SendToServer(packet, m_Channel);
        }

        private void SendHitResponseToClient(uint clientNetworkId, NetworkMeleeHitResponse response)
        {
            var nm = ActiveManager;
            if (nm == null || !nm.isServer) return;
            if (!TryGetPlayerId(nm, clientNetworkId, out var playerId)) return;

            nm.Send(playerId, new GC2MeleeHitResponsePacket { response = response }, m_Channel);
        }

        private void SendBlockResponseToClient(uint clientNetworkId, NetworkBlockResponse response)
        {
            var nm = ActiveManager;
            if (nm == null || !nm.isServer)
            {
                LogBlockPacket(
                    $"dropped block response send actor={response.ActorNetworkId} client={clientNetworkId}: " +
                    $"manager={(nm != null ? nm.name : "null")} server={(nm != null && nm.isServer)}");
                return;
            }

            if (!TryGetPlayerId(nm, clientNetworkId, out var playerId))
            {
                LogBlockPacket(
                    $"dropped block response send actor={response.ActorNetworkId} client={clientNetworkId}: no player id");
                return;
            }

            LogBlockPacket(
                $"sending block response client={clientNetworkId} actor={response.ActorNetworkId} req={response.RequestId} " +
                $"validated={response.Validated} reason={response.RejectionReason}");
            nm.Send(playerId, new GC2MeleeBlockResponsePacket { response = response }, m_Channel);
        }

        private void SendSkillResponseToClient(uint clientNetworkId, NetworkSkillResponse response)
        {
            var nm = ActiveManager;
            if (nm == null || !nm.isServer)
            {
                LogSkillPacket(
                    $"dropped skill response actor={response.ActorNetworkId} client={clientNetworkId}: " +
                    $"manager={(nm != null)} isServer={nm?.isServer}");
                return;
            }
            if (!TryGetPlayerId(nm, clientNetworkId, out var playerId))
            {
                LogSkillPacket(
                    $"dropped skill response actor={response.ActorNetworkId} client={clientNetworkId} " +
                    $"req={response.RequestId}: no player id");
                return;
            }

            LogSkillPacket(
                $"sending skill response client={clientNetworkId} actor={response.ActorNetworkId} req={response.RequestId} " +
                $"corr={response.CorrelationId} validated={response.Validated} reason={response.RejectionReason}");
            nm.Send(playerId, new GC2MeleeSkillResponsePacket { response = response }, m_Channel);
        }

        private void SendChargeResponseToClient(uint clientNetworkId, NetworkChargeResponse response)
        {
            var nm = ActiveManager;
            if (nm == null || !nm.isServer) return;
            if (!TryGetPlayerId(nm, clientNetworkId, out var playerId)) return;

            nm.Send(playerId, new GC2MeleeChargeResponsePacket { response = response }, m_Channel);
        }

        private void BroadcastHitToAllClients(NetworkMeleeHitBroadcast broadcast)
        {
            var nm = ActiveManager;
            if (nm == null || !nm.isServer) return;
            nm.SendToAll(new GC2MeleeHitBroadcastPacket { broadcast = broadcast }, m_Channel);
        }

        private void BroadcastBlockToAllClients(NetworkBlockBroadcast broadcast)
        {
            var nm = ActiveManager;
            if (nm == null || !nm.isServer)
            {
                LogBlockPacket(
                    $"dropped block broadcast actor={broadcast.CharacterNetworkId} action={broadcast.Action}: " +
                    $"manager={(nm != null ? nm.name : "null")} server={(nm != null && nm.isServer)}");
                return;
            }

            LogBlockPacket(
                $"broadcasting block actor={broadcast.CharacterNetworkId} action={broadcast.Action} " +
                $"shieldHash={broadcast.ShieldHash}");
            nm.SendToAll(new GC2MeleeBlockBroadcastPacket { broadcast = broadcast }, m_Channel);
        }

        private void BroadcastSkillToAllClients(NetworkSkillBroadcast broadcast)
        {
            var nm = ActiveManager;
            if (nm == null || !nm.isServer)
            {
                LogSkillPacket(
                    $"dropped skill broadcast actor={broadcast.CharacterNetworkId}: manager={(nm != null)} isServer={nm?.isServer}");
                return;
            }
            LogSkillPacket(
                $"broadcasting skill actor={broadcast.CharacterNetworkId} skillHash={broadcast.SkillHash} " +
                $"weaponHash={broadcast.WeaponHash} combo={broadcast.ComboNodeId}");
            nm.SendToAll(new GC2MeleeSkillBroadcastPacket { broadcast = broadcast }, m_Channel);
        }

        private void BroadcastChargeToAllClients(NetworkChargeBroadcast broadcast)
        {
            var nm = ActiveManager;
            if (nm == null || !nm.isServer) return;
            nm.SendToAll(new GC2MeleeChargeBroadcastPacket { broadcast = broadcast }, m_Channel);
        }

        private void BroadcastReactionToAllClients(NetworkReactionBroadcast broadcast)
        {
            var nm = ActiveManager;
            if (nm == null || !nm.isServer) return;
            nm.SendToAll(new GC2MeleeReactionBroadcastPacket { broadcast = broadcast }, m_Channel);
        }

        private void BroadcastWeaponStateToAllClients(GC2MeleeWeaponStatePacket packet)
        {
            var nm = ActiveManager;
            if (nm == null || !nm.isServer) return;
            nm.SendToAll(packet, m_Channel);
        }

        private void HandleHitRequestServer(PlayerID senderPlayer, GC2MeleeHitRequestPacket data, bool asServer)
        {
            if (!asServer) return;
            DispatchHitRequestOnServer(senderPlayer, data);
        }

        private void DispatchHitRequestOnServer(PlayerID senderPlayer, GC2MeleeHitRequestPacket data)
        {
            if (!TryConvertPlayerId(senderPlayer, out uint senderClientId)) return;
            GetMeleeManager()?.ReceiveHitRequest(senderClientId, data.request);
        }

        private void HandleBlockRequestServer(PlayerID senderPlayer, GC2MeleeBlockRequestPacket data, bool asServer)
        {
            if (!asServer) return;
            DispatchBlockRequestOnServer(senderPlayer, data);
        }

        private void DispatchBlockRequestOnServer(PlayerID senderPlayer, GC2MeleeBlockRequestPacket data)
        {
            if (!TryConvertPlayerId(senderPlayer, out uint senderClientId))
            {
                LogBlockPacket(
                    $"dropped inbound block request actor={data.request.ActorNetworkId} action={data.request.Action}: " +
                    $"could not convert sender={senderPlayer}");
                return;
            }

            LogBlockPacket(
                $"dispatching inbound block request sender={senderClientId} actor={data.request.ActorNetworkId} " +
                $"req={data.request.RequestId} action={data.request.Action}");
            GetMeleeManager()?.ReceiveBlockRequest(senderClientId, data.request);
        }

        private void HandleSkillRequestServer(PlayerID senderPlayer, GC2MeleeSkillRequestPacket data, bool asServer)
        {
            if (!asServer) return;
            DispatchSkillRequestOnServer(senderPlayer, data);
        }

        private void DispatchSkillRequestOnServer(PlayerID senderPlayer, GC2MeleeSkillRequestPacket data)
        {
            if (!TryConvertPlayerId(senderPlayer, out uint senderClientId))
            {
                LogSkillPacket(
                    $"dropped inbound skill request: could not convert sender={senderPlayer} " +
                    $"actor={data.request.ActorNetworkId} req={data.request.RequestId}");
                return;
            }

            LogSkillPacket(
                $"dispatching inbound skill request sender={senderClientId} actor={data.request.ActorNetworkId} " +
                $"req={data.request.RequestId} corr={data.request.CorrelationId} " +
                $"skillHash={data.request.SkillHash} weaponHash={data.request.WeaponHash} combo={data.request.ComboNodeId}");
            GetMeleeManager()?.ReceiveSkillRequest(senderClientId, data.request);
        }

        private void HandleChargeRequestServer(PlayerID senderPlayer, GC2MeleeChargeRequestPacket data, bool asServer)
        {
            if (!asServer) return;
            DispatchChargeRequestOnServer(senderPlayer, data);
        }

        private void DispatchChargeRequestOnServer(PlayerID senderPlayer, GC2MeleeChargeRequestPacket data)
        {
            if (!TryConvertPlayerId(senderPlayer, out uint senderClientId)) return;
            GetMeleeManager()?.ReceiveChargeRequest(senderClientId, data.request);
        }

        private void HandleWeaponStateServer(PlayerID senderPlayer, GC2MeleeWeaponStatePacket data, bool asServer)
        {
            if (!asServer) return;
            DispatchWeaponStateOnServer(senderPlayer, data);
        }

        private void DispatchWeaponStateOnServer(PlayerID senderPlayer, GC2MeleeWeaponStatePacket data)
        {
            if (!TryConvertPlayerId(senderPlayer, out uint senderClientId)) return;
            if (data.characterNetworkId == 0) return;

            var core = CoreBridge;
            if (core != null &&
                core.TryGetCharacterOwner(data.characterNetworkId, out uint ownerClientId) &&
                ownerClientId != senderClientId)
            {
                LogBlockPacket(
                    $"rejected melee weapon state sender={senderClientId} owner={ownerClientId} " +
                    $"character={data.characterNetworkId}");
                return;
            }

            ApplyWeaponState(data);
            BroadcastWeaponStateToAllClients(data);
        }

        private void HandleHitResponseClient(PlayerID senderPlayer, GC2MeleeHitResponsePacket data, bool asServer)
        {
            if (asServer) return;
            GetMeleeManager()?.ReceiveHitResponse(data.response);
        }

        private void HandleHitBroadcastClient(PlayerID senderPlayer, GC2MeleeHitBroadcastPacket data, bool asServer)
        {
            if (asServer) return;
            GetMeleeManager()?.ReceiveHitBroadcast(data.broadcast);
        }

        private void HandleBlockResponseClient(PlayerID senderPlayer, GC2MeleeBlockResponsePacket data, bool asServer)
        {
            if (asServer) return;
            LogBlockPacket(
                $"received block response packet actor={data.response.ActorNetworkId} req={data.response.RequestId} " +
                $"validated={data.response.Validated} reason={data.response.RejectionReason}");
            GetMeleeManager()?.ReceiveBlockResponse(data.response);
        }

        private void HandleBlockBroadcastClient(PlayerID senderPlayer, GC2MeleeBlockBroadcastPacket data, bool asServer)
        {
            if (asServer) return;
            LogBlockPacket(
                $"received block broadcast packet actor={data.broadcast.CharacterNetworkId} " +
                $"action={data.broadcast.Action} shieldHash={data.broadcast.ShieldHash}");
            GetMeleeManager()?.ReceiveBlockBroadcast(data.broadcast);
        }

        private void HandleSkillResponseClient(PlayerID senderPlayer, GC2MeleeSkillResponsePacket data, bool asServer)
        {
            if (asServer) return;
            LogSkillPacket(
                $"received skill response packet actor={data.response.ActorNetworkId} req={data.response.RequestId} " +
                $"corr={data.response.CorrelationId} validated={data.response.Validated} reason={data.response.RejectionReason}");
            GetMeleeManager()?.ReceiveSkillResponse(data.response);
        }

        private void HandleSkillBroadcastClient(PlayerID senderPlayer, GC2MeleeSkillBroadcastPacket data, bool asServer)
        {
            if (asServer) return;
            LogSkillPacket(
                $"received skill broadcast packet actor={data.broadcast.CharacterNetworkId} " +
                $"skillHash={data.broadcast.SkillHash} weaponHash={data.broadcast.WeaponHash} combo={data.broadcast.ComboNodeId}");
            GetMeleeManager()?.ReceiveSkillBroadcast(data.broadcast);
        }

        private void HandleChargeResponseClient(PlayerID senderPlayer, GC2MeleeChargeResponsePacket data, bool asServer)
        {
            if (asServer) return;
            GetMeleeManager()?.ReceiveChargeResponse(data.response);
        }

        private void HandleChargeBroadcastClient(PlayerID senderPlayer, GC2MeleeChargeBroadcastPacket data, bool asServer)
        {
            if (asServer) return;
            GetMeleeManager()?.ReceiveChargeBroadcast(data.broadcast);
        }

        private void HandleReactionBroadcastClient(PlayerID senderPlayer, GC2MeleeReactionBroadcastPacket data, bool asServer)
        {
            if (asServer) return;
            GetMeleeManager()?.ReceiveReactionBroadcast(data.broadcast);
        }

        private void HandleWeaponStateClient(PlayerID senderPlayer, GC2MeleeWeaponStatePacket data, bool asServer)
        {
            if (asServer) return;
            ApplyWeaponState(data);
        }

        private void ApplyWeaponState(GC2MeleeWeaponStatePacket data)
        {
            if (data.characterNetworkId == 0) return;
            if (!m_RegisteredControllers.TryGetValue(data.characterNetworkId, out var controller) || controller == null)
            {
                RefreshControllerRegistry(force: true);
                m_RegisteredControllers.TryGetValue(data.characterNetworkId, out controller);
            }

            if (controller == null) return;

            MeleeWeapon weapon = data.state.WeaponHash != 0
                ? NetworkMeleeManager.GetMeleeWeaponByHash(data.state.WeaponHash)
                : null;

            controller.ApplyRemoteWeaponState(data.state, weapon);
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

        private static NetworkMeleeManager GetMeleeManager()
        {
            return NetworkMeleeManager.Instance != null
                ? NetworkMeleeManager.Instance
                : FindFirstObjectByType<NetworkMeleeManager>();
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
