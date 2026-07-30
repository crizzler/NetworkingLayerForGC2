#if GC2_ABILITIES
using System;
using System.Collections.Generic;
using Arawn.GameCreator2.Networking;
using Arawn.GameCreator2.Networking.Transport.PurrNet;
using DaimahouGames.Runtime.Abilities;
using DaimahouGames.Runtime.Pawns;
using PurrNet;
using PurrNet.Transports;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Abilities.Transport.PurrNet
{
    [DisallowMultipleComponent]
    [AddComponentMenu("Game Creator/Network/Transport/PurrNet Abilities Bridge")]
    [DefaultExecutionOrder(-335)]
    public sealed class PurrNetAbilitiesTransportBridge : MonoBehaviour
    {
        [Header("PurrNet")]
        [Tooltip("Optional reference to a specific NetworkManager. Leave empty to use NetworkManager.main.")]
        [SerializeField] private NetworkManager m_NetworkManager;

        [Tooltip("Optional reference to the core GC2 PurrNet bridge used for character ownership and network time.")]
        [SerializeField] private PurrNetTransportBridge m_CoreBridge;

        [Tooltip("Reliable channel used for ability requests, responses, and state broadcasts.")]
        [SerializeField] private Channel m_Channel = Channel.ReliableOrdered;

        [Header("Abilities Assets")]
        [Tooltip("Ability assets whose hashes should be registered for remote lookup.")]
        [SerializeField] private Ability[] m_RegisterAbilities = Array.Empty<Ability>();

        [Tooltip("Projectile assets whose hashes should be registered for remote visual spawning.")]
        [SerializeField] private Projectile[] m_RegisterProjectiles = Array.Empty<Projectile>();

        [Tooltip("Impact assets whose hashes should be registered for remote visual spawning.")]
        [SerializeField] private Impact[] m_RegisterImpacts = Array.Empty<Impact>();

        [Header("Pawns")]
        [Tooltip("Automatically register Daimahou Pawn components that belong to NetworkCharacter instances.")]
        [SerializeField] private bool m_AutoRegisterScenePawns = true;

        [Min(0.05f)]
        [SerializeField] private float m_PawnScanInterval = 0.25f;

        [Header("Debug")]
        [SerializeField] private bool m_LogNetworkMessages;

        private readonly Dictionary<uint, Pawn> m_RegisteredPawns = new(64);
        private readonly List<uint> m_RemoveBuffer = new(16);

        private NetworkManager m_HookedManager;
        private bool m_SubscribedServer;
        private bool m_SubscribedClient;
        private bool m_ControllerInitialized;
        private bool m_LastServer;
        private bool m_LastClient;
        private bool m_AssetsRegistered;
        private float m_NextPawnScanTime;

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

        private void Awake()
        {
            if (m_NetworkManager == null) m_NetworkManager = NetworkManager.main;
            if (m_CoreBridge == null) m_CoreBridge = NetworkTransportBridge.Active as PurrNetTransportBridge;
        }

        private void OnEnable()
        {
            TryHookNetworkManager();
            WireAbilitiesController();
            RegisterConfiguredAssets();
            RefreshPawnRegistry(force: true);
        }

        private void Start()
        {
            TryHookNetworkManager();
            WireAbilitiesController();
            RegisterConfiguredAssets();
            RefreshPawnRegistry(force: true);
        }

        private void Update()
        {
            TryHookNetworkManager();
            WireAbilitiesController();
            RegisterConfiguredAssets();

            if (!m_AutoRegisterScenePawns) return;
            if (Time.unscaledTime < m_NextPawnScanTime) return;

            m_NextPawnScanTime = Time.unscaledTime + Mathf.Max(0.05f, m_PawnScanInterval);
            RefreshPawnRegistry(force: false);
        }

        private void OnDisable()
        {
            UnhookNetworkManager();
            UnwireAbilitiesController();
            UnregisterAllPawns();
        }

        private void TryHookNetworkManager()
        {
            NetworkManager nm = ActiveManager;
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
            nm.onPlayerLoadedScene -= HandlePlayerLoadedScene;
            nm.onPlayerLoadedScene += HandlePlayerLoadedScene;

            if (nm.isServer) HandleNetworkStarted(nm, true);
            if (nm.isClient) HandleNetworkStarted(nm, false);
        }

        private void UnhookNetworkManager()
        {
            NetworkManager nm = m_HookedManager;
            if (nm == null) return;

            nm.onNetworkStarted -= HandleNetworkStarted;
            nm.onNetworkShutdown -= HandleNetworkShutdown;
            nm.onPlayerLoadedScene -= HandlePlayerLoadedScene;

            if (m_SubscribedServer)
            {
                nm.Unsubscribe<GC2AbilityCastRequestPacket>(HandleCastRequestServer, true);
                nm.Unsubscribe<GC2AbilityCooldownRequestPacket>(HandleCooldownRequestServer, true);
                nm.Unsubscribe<GC2AbilityLearnRequestPacket>(HandleLearnRequestServer, true);
                nm.Unsubscribe<GC2AbilityCancelRequestPacket>(HandleCancelRequestServer, true);
                m_SubscribedServer = false;
            }

            if (m_SubscribedClient)
            {
                nm.Unsubscribe<GC2AbilityCastResponsePacket>(HandleCastResponseClient, false);
                nm.Unsubscribe<GC2AbilityCastBroadcastPacket>(HandleCastBroadcastClient, false);
                nm.Unsubscribe<GC2AbilityEffectBroadcastPacket>(HandleEffectBroadcastClient, false);
                nm.Unsubscribe<GC2AbilityProjectileSpawnPacket>(HandleProjectileSpawnClient, false);
                nm.Unsubscribe<GC2AbilityProjectileEventPacket>(HandleProjectileEventClient, false);
                nm.Unsubscribe<GC2AbilityImpactSpawnPacket>(HandleImpactSpawnClient, false);
                nm.Unsubscribe<GC2AbilityImpactHitPacket>(HandleImpactHitClient, false);
                nm.Unsubscribe<GC2AbilityCooldownResponsePacket>(HandleCooldownResponseClient, false);
                nm.Unsubscribe<GC2AbilityCooldownBroadcastPacket>(HandleCooldownBroadcastClient, false);
                nm.Unsubscribe<GC2AbilityLearnResponsePacket>(HandleLearnResponseClient, false);
                nm.Unsubscribe<GC2AbilityLearnBroadcastPacket>(HandleLearnBroadcastClient, false);
                nm.Unsubscribe<GC2AbilityCancelResponsePacket>(HandleCancelResponseClient, false);
                m_SubscribedClient = false;
            }

            m_HookedManager = null;
        }

        private void HandleNetworkStarted(NetworkManager manager, bool asServer)
        {
            if (asServer && !m_SubscribedServer)
            {
                manager.Subscribe<GC2AbilityCastRequestPacket>(HandleCastRequestServer, true);
                manager.Subscribe<GC2AbilityCooldownRequestPacket>(HandleCooldownRequestServer, true);
                manager.Subscribe<GC2AbilityLearnRequestPacket>(HandleLearnRequestServer, true);
                manager.Subscribe<GC2AbilityCancelRequestPacket>(HandleCancelRequestServer, true);
                m_SubscribedServer = true;
            }
            else if (!asServer && !m_SubscribedClient)
            {
                manager.Subscribe<GC2AbilityCastResponsePacket>(HandleCastResponseClient, false);
                manager.Subscribe<GC2AbilityCastBroadcastPacket>(HandleCastBroadcastClient, false);
                manager.Subscribe<GC2AbilityEffectBroadcastPacket>(HandleEffectBroadcastClient, false);
                manager.Subscribe<GC2AbilityProjectileSpawnPacket>(HandleProjectileSpawnClient, false);
                manager.Subscribe<GC2AbilityProjectileEventPacket>(HandleProjectileEventClient, false);
                manager.Subscribe<GC2AbilityImpactSpawnPacket>(HandleImpactSpawnClient, false);
                manager.Subscribe<GC2AbilityImpactHitPacket>(HandleImpactHitClient, false);
                manager.Subscribe<GC2AbilityCooldownResponsePacket>(HandleCooldownResponseClient, false);
                manager.Subscribe<GC2AbilityCooldownBroadcastPacket>(HandleCooldownBroadcastClient, false);
                manager.Subscribe<GC2AbilityLearnResponsePacket>(HandleLearnResponseClient, false);
                manager.Subscribe<GC2AbilityLearnBroadcastPacket>(HandleLearnBroadcastClient, false);
                manager.Subscribe<GC2AbilityCancelResponsePacket>(HandleCancelResponseClient, false);
                m_SubscribedClient = true;
            }

            WireAbilitiesController();
            RegisterConfiguredAssets();
            RefreshPawnRegistry(force: true);
        }

        private void HandleNetworkShutdown(NetworkManager manager, bool asServer)
        {
            if (asServer && m_SubscribedServer)
            {
                manager.Unsubscribe<GC2AbilityCastRequestPacket>(HandleCastRequestServer, true);
                manager.Unsubscribe<GC2AbilityCooldownRequestPacket>(HandleCooldownRequestServer, true);
                manager.Unsubscribe<GC2AbilityLearnRequestPacket>(HandleLearnRequestServer, true);
                manager.Unsubscribe<GC2AbilityCancelRequestPacket>(HandleCancelRequestServer, true);
                m_SubscribedServer = false;
            }
            else if (!asServer && m_SubscribedClient)
            {
                manager.Unsubscribe<GC2AbilityCastResponsePacket>(HandleCastResponseClient, false);
                manager.Unsubscribe<GC2AbilityCastBroadcastPacket>(HandleCastBroadcastClient, false);
                manager.Unsubscribe<GC2AbilityEffectBroadcastPacket>(HandleEffectBroadcastClient, false);
                manager.Unsubscribe<GC2AbilityProjectileSpawnPacket>(HandleProjectileSpawnClient, false);
                manager.Unsubscribe<GC2AbilityProjectileEventPacket>(HandleProjectileEventClient, false);
                manager.Unsubscribe<GC2AbilityImpactSpawnPacket>(HandleImpactSpawnClient, false);
                manager.Unsubscribe<GC2AbilityImpactHitPacket>(HandleImpactHitClient, false);
                manager.Unsubscribe<GC2AbilityCooldownResponsePacket>(HandleCooldownResponseClient, false);
                manager.Unsubscribe<GC2AbilityCooldownBroadcastPacket>(HandleCooldownBroadcastClient, false);
                manager.Unsubscribe<GC2AbilityLearnResponsePacket>(HandleLearnResponseClient, false);
                manager.Unsubscribe<GC2AbilityLearnBroadcastPacket>(HandleLearnBroadcastClient, false);
                manager.Unsubscribe<GC2AbilityCancelResponsePacket>(HandleCancelResponseClient, false);
                m_SubscribedClient = false;
            }

            WireAbilitiesController();
        }

        private void HandlePlayerLoadedScene(PlayerID player, SceneID scene, bool asServer)
        {
            if (!asServer) return;
            RefreshPawnRegistry(force: true);
        }

        private void WireAbilitiesController()
        {
            NetworkAbilitiesController controller = GetController();
            if (controller == null) return;

            NetworkAbilitiesManager.Initialize(GetNetworkTime, GetLocalPlayerNetworkId);
            NetworkAbilitiesManager.WireUpController(controller);

            controller.SendCastRequestToServer -= SendCastRequestToServer;
            controller.SendCastRequestToServer += SendCastRequestToServer;
            controller.SendCastResponseToClient -= SendCastResponseToClient;
            controller.SendCastResponseToClient += SendCastResponseToClient;
            controller.BroadcastCastToClients -= BroadcastCastToClients;
            controller.BroadcastCastToClients += BroadcastCastToClients;

            controller.BroadcastEffectToClients -= BroadcastEffectToClients;
            controller.BroadcastEffectToClients += BroadcastEffectToClients;

            controller.BroadcastProjectileSpawnToClients -= BroadcastProjectileSpawnToClients;
            controller.BroadcastProjectileSpawnToClients += BroadcastProjectileSpawnToClients;
            controller.BroadcastProjectileEventToClients -= BroadcastProjectileEventToClients;
            controller.BroadcastProjectileEventToClients += BroadcastProjectileEventToClients;

            controller.BroadcastImpactSpawnToClients -= BroadcastImpactSpawnToClients;
            controller.BroadcastImpactSpawnToClients += BroadcastImpactSpawnToClients;
            controller.BroadcastImpactHitToClients -= BroadcastImpactHitToClients;
            controller.BroadcastImpactHitToClients += BroadcastImpactHitToClients;

            controller.SendCooldownRequestToServer -= SendCooldownRequestToServer;
            controller.SendCooldownRequestToServer += SendCooldownRequestToServer;
            controller.SendCooldownResponseToClient -= SendCooldownResponseToClient;
            controller.SendCooldownResponseToClient += SendCooldownResponseToClient;
            controller.BroadcastCooldownToClients -= BroadcastCooldownToClients;
            controller.BroadcastCooldownToClients += BroadcastCooldownToClients;

            controller.SendLearnRequestToServer -= SendLearnRequestToServer;
            controller.SendLearnRequestToServer += SendLearnRequestToServer;
            controller.SendLearnResponseToClient -= SendLearnResponseToClient;
            controller.SendLearnResponseToClient += SendLearnResponseToClient;
            controller.BroadcastLearnToClients -= BroadcastLearnToClients;
            controller.BroadcastLearnToClients += BroadcastLearnToClients;

            controller.SendCancelRequestToServer -= SendCancelRequestToServer;
            controller.SendCancelRequestToServer += SendCancelRequestToServer;
            controller.SendCancelResponseToClient -= SendCancelResponseToClient;
            controller.SendCancelResponseToClient += SendCancelResponseToClient;

            NetworkManager nm = ActiveManager;
            bool isServer = nm != null && nm.isServer;
            bool isClient = nm != null && nm.isClient;
            if (!m_ControllerInitialized || isServer != m_LastServer || isClient != m_LastClient)
            {
                if (isServer && isClient) controller.InitializeAsHost();
                else if (isServer) controller.InitializeAsServer();
                else if (isClient) controller.InitializeAsClient();

                m_ControllerInitialized = isServer || isClient;
                m_LastServer = isServer;
                m_LastClient = isClient;
                Log($"initialized abilities controller server={isServer} client={isClient}");
            }
        }

        private void UnwireAbilitiesController()
        {
            NetworkAbilitiesController controller = GetController();
            if (controller == null) return;

            controller.SendCastRequestToServer -= SendCastRequestToServer;
            controller.SendCastResponseToClient -= SendCastResponseToClient;
            controller.BroadcastCastToClients -= BroadcastCastToClients;
            controller.BroadcastEffectToClients -= BroadcastEffectToClients;
            controller.BroadcastProjectileSpawnToClients -= BroadcastProjectileSpawnToClients;
            controller.BroadcastProjectileEventToClients -= BroadcastProjectileEventToClients;
            controller.BroadcastImpactSpawnToClients -= BroadcastImpactSpawnToClients;
            controller.BroadcastImpactHitToClients -= BroadcastImpactHitToClients;
            controller.SendCooldownRequestToServer -= SendCooldownRequestToServer;
            controller.SendCooldownResponseToClient -= SendCooldownResponseToClient;
            controller.BroadcastCooldownToClients -= BroadcastCooldownToClients;
            controller.SendLearnRequestToServer -= SendLearnRequestToServer;
            controller.SendLearnResponseToClient -= SendLearnResponseToClient;
            controller.BroadcastLearnToClients -= BroadcastLearnToClients;
            controller.SendCancelRequestToServer -= SendCancelRequestToServer;
            controller.SendCancelResponseToClient -= SendCancelResponseToClient;
            m_ControllerInitialized = false;
        }

        private void RegisterConfiguredAssets()
        {
            if (m_AssetsRegistered) return;

            if (m_RegisterAbilities != null)
            {
                for (int i = 0; i < m_RegisterAbilities.Length; i++)
                {
                    Ability ability = m_RegisterAbilities[i];
                    if (ability == null) continue;

                    NetworkAbilitiesManager.RegisterAbility(ability);
                    Log($"registered ability asset name={ability.name} hash={ability.ID.Hash}");
                }
            }

            if (m_RegisterProjectiles != null)
            {
                for (int i = 0; i < m_RegisterProjectiles.Length; i++)
                {
                    Projectile projectile = m_RegisterProjectiles[i];
                    if (projectile == null) continue;

                    NetworkAbilitiesManager.RegisterProjectile(projectile);
                    Log($"registered projectile asset name={projectile.name}");
                }
            }

            if (m_RegisterImpacts != null)
            {
                for (int i = 0; i < m_RegisterImpacts.Length; i++)
                {
                    Impact impact = m_RegisterImpacts[i];
                    if (impact == null) continue;

                    NetworkAbilitiesManager.RegisterImpact(impact);
                    Log($"registered impact asset name={impact.name}");
                }
            }

            m_AssetsRegistered = true;
        }

        private void RefreshPawnRegistry(bool force)
        {
            PrunePawnRegistry();

            if (!m_AutoRegisterScenePawns && !force) return;

            Pawn[] pawns = FindObjectsByType<Pawn>(
                FindObjectsInactive.Exclude,
                FindObjectsSortMode.None);

            for (int i = 0; i < pawns.Length; i++)
            {
                RegisterPawn(pawns[i]);
            }
        }

        private void RegisterPawn(Pawn pawn)
        {
            if (pawn == null) return;
            NetworkCharacter networkCharacter = ResolveNetworkCharacter(pawn);
            if (networkCharacter == null || networkCharacter.NetworkId == 0) return;

            uint networkId = networkCharacter.NetworkId;
            if (m_RegisteredPawns.TryGetValue(networkId, out Pawn existing) && existing == pawn)
            {
                return;
            }

            NetworkAbilitiesManager.RegisterPawn(pawn, networkId);
            m_RegisteredPawns[networkId] = pawn;
            Log($"registered pawn netId={networkId} name={pawn.name} {DescribePawnAbilities(pawn)}");
        }

        private void PrunePawnRegistry()
        {
            m_RemoveBuffer.Clear();

            foreach (KeyValuePair<uint, Pawn> pair in m_RegisteredPawns)
            {
                Pawn pawn = pair.Value;
                NetworkCharacter networkCharacter = pawn != null ? ResolveNetworkCharacter(pawn) : null;
                if (pawn == null || networkCharacter == null || networkCharacter.NetworkId != pair.Key)
                {
                    m_RemoveBuffer.Add(pair.Key);
                }
            }

            for (int i = 0; i < m_RemoveBuffer.Count; i++)
            {
                uint networkId = m_RemoveBuffer[i];
                NetworkAbilitiesManager.UnregisterPawn(networkId);
                m_RegisteredPawns.Remove(networkId);
            }
        }

        private void UnregisterAllPawns()
        {
            foreach (KeyValuePair<uint, Pawn> pair in m_RegisteredPawns)
            {
                if (pair.Value != null) NetworkAbilitiesManager.UnregisterPawn(pair.Value);
                else NetworkAbilitiesManager.UnregisterPawn(pair.Key);
            }

            m_RegisteredPawns.Clear();
        }

        private void SendCastRequestToServer(NetworkAbilityCastRequest request)
        {
            NetworkManager nm = ActiveManager;
            if (nm == null || !nm.isClient) return;

            var packet = new GC2AbilityCastRequestPacket { request = request };
            Log($"send cast request actor={request.ActorNetworkId} caster={request.CasterNetworkId} ability={request.AbilityIdHash} hostLoopback={nm.isServer}");
            if (nm.isServer)
            {
                if (nm.isLocalPlayerReady) DispatchCastRequestOnServer(nm.localPlayer, packet);
                return;
            }

            nm.SendToServer(packet, m_Channel);
        }

        private void SendCooldownRequestToServer(NetworkCooldownRequest request)
        {
            NetworkManager nm = ActiveManager;
            if (nm == null || !nm.isClient) return;

            var packet = new GC2AbilityCooldownRequestPacket { request = request };
            if (nm.isServer)
            {
                if (nm.isLocalPlayerReady) DispatchCooldownRequestOnServer(nm.localPlayer, packet);
                return;
            }

            nm.SendToServer(packet, m_Channel);
        }

        private void SendLearnRequestToServer(NetworkAbilityLearnRequest request)
        {
            NetworkManager nm = ActiveManager;
            if (nm == null || !nm.isClient) return;

            var packet = new GC2AbilityLearnRequestPacket { request = request };
            Log($"send learn request actor={request.ActorNetworkId} character={request.CharacterNetworkId} ability={request.AbilityIdHash} slot={request.Slot} learning={request.IsLearning} hostLoopback={nm.isServer}");
            if (nm.isServer)
            {
                if (nm.isLocalPlayerReady) DispatchLearnRequestOnServer(nm.localPlayer, packet);
                return;
            }

            nm.SendToServer(packet, m_Channel);
        }

        private void SendCancelRequestToServer(NetworkCastCancelRequest request)
        {
            NetworkManager nm = ActiveManager;
            if (nm == null || !nm.isClient) return;

            var packet = new GC2AbilityCancelRequestPacket { request = request };
            if (nm.isServer)
            {
                if (nm.isLocalPlayerReady) DispatchCancelRequestOnServer(nm.localPlayer, packet);
                return;
            }

            nm.SendToServer(packet, m_Channel);
        }

        private void SendCastResponseToClient(uint clientId, NetworkAbilityCastResponse response)
        {
            NetworkManager nm = ActiveManager;
            if (nm == null || !nm.isServer) return;
            if (!TryGetPlayerId(nm, clientId, out PlayerID playerId)) return;

            nm.Send(playerId, new GC2AbilityCastResponsePacket { response = response }, m_Channel);
        }

        private void SendCooldownResponseToClient(uint clientId, NetworkCooldownResponse response)
        {
            NetworkManager nm = ActiveManager;
            if (nm == null || !nm.isServer) return;
            if (!TryGetPlayerId(nm, clientId, out PlayerID playerId)) return;

            nm.Send(playerId, new GC2AbilityCooldownResponsePacket { response = response }, m_Channel);
        }

        private void SendLearnResponseToClient(uint clientId, NetworkAbilityLearnResponse response)
        {
            NetworkManager nm = ActiveManager;
            if (nm == null || !nm.isServer) return;
            if (!TryGetPlayerId(nm, clientId, out PlayerID playerId)) return;

            nm.Send(playerId, new GC2AbilityLearnResponsePacket { response = response }, m_Channel);
        }

        private void SendCancelResponseToClient(uint clientId, NetworkCastCancelResponse response)
        {
            NetworkManager nm = ActiveManager;
            if (nm == null || !nm.isServer) return;
            if (!TryGetPlayerId(nm, clientId, out PlayerID playerId)) return;

            nm.Send(playerId, new GC2AbilityCancelResponsePacket { response = response }, m_Channel);
        }

        private void BroadcastCastToClients(NetworkAbilityCastBroadcast broadcast)
        {
            NetworkManager nm = ActiveManager;
            if (nm == null || !nm.isServer) return;

            nm.SendToAll(new GC2AbilityCastBroadcastPacket { broadcast = broadcast }, m_Channel);
        }

        private void BroadcastEffectToClients(NetworkAbilityEffectBroadcast broadcast)
        {
            NetworkManager nm = ActiveManager;
            if (nm == null || !nm.isServer) return;

            nm.SendToAll(new GC2AbilityEffectBroadcastPacket { broadcast = broadcast }, m_Channel);
        }

        private void BroadcastProjectileSpawnToClients(NetworkProjectileSpawnBroadcast broadcast)
        {
            NetworkManager nm = ActiveManager;
            if (nm == null || !nm.isServer) return;

            nm.SendToAll(new GC2AbilityProjectileSpawnPacket { broadcast = broadcast }, m_Channel);
        }

        private void BroadcastProjectileEventToClients(NetworkProjectileEventBroadcast broadcast)
        {
            NetworkManager nm = ActiveManager;
            if (nm == null || !nm.isServer) return;

            nm.SendToAll(new GC2AbilityProjectileEventPacket { broadcast = broadcast }, m_Channel);
        }

        private void BroadcastImpactSpawnToClients(NetworkImpactSpawnBroadcast broadcast)
        {
            NetworkManager nm = ActiveManager;
            if (nm == null || !nm.isServer) return;

            nm.SendToAll(new GC2AbilityImpactSpawnPacket { broadcast = broadcast }, m_Channel);
        }

        private void BroadcastImpactHitToClients(NetworkImpactHitBroadcast broadcast)
        {
            NetworkManager nm = ActiveManager;
            if (nm == null || !nm.isServer) return;

            nm.SendToAll(new GC2AbilityImpactHitPacket { broadcast = broadcast }, m_Channel);
        }

        private void BroadcastCooldownToClients(NetworkCooldownBroadcast broadcast)
        {
            NetworkManager nm = ActiveManager;
            if (nm == null || !nm.isServer) return;

            nm.SendToAll(new GC2AbilityCooldownBroadcastPacket { broadcast = broadcast }, m_Channel);
        }

        private void BroadcastLearnToClients(NetworkAbilityLearnBroadcast broadcast)
        {
            NetworkManager nm = ActiveManager;
            if (nm == null || !nm.isServer) return;

            nm.SendToAll(new GC2AbilityLearnBroadcastPacket { broadcast = broadcast }, m_Channel);
        }

        private void HandleCastRequestServer(PlayerID senderPlayer, GC2AbilityCastRequestPacket data, bool asServer)
        {
            if (!asServer) return;
            RefreshPawnRegistry(force: true);
            DispatchCastRequestOnServer(senderPlayer, data);
        }

        private void HandleCooldownRequestServer(PlayerID senderPlayer, GC2AbilityCooldownRequestPacket data, bool asServer)
        {
            if (!asServer) return;
            RefreshPawnRegistry(force: true);
            DispatchCooldownRequestOnServer(senderPlayer, data);
        }

        private void HandleLearnRequestServer(PlayerID senderPlayer, GC2AbilityLearnRequestPacket data, bool asServer)
        {
            if (!asServer) return;
            RefreshPawnRegistry(force: true);
            DispatchLearnRequestOnServer(senderPlayer, data);
        }

        private void HandleCancelRequestServer(PlayerID senderPlayer, GC2AbilityCancelRequestPacket data, bool asServer)
        {
            if (!asServer) return;
            RefreshPawnRegistry(force: true);
            DispatchCancelRequestOnServer(senderPlayer, data);
        }

        private void DispatchCastRequestOnServer(PlayerID senderPlayer, GC2AbilityCastRequestPacket data)
        {
            GetController()?.ProcessCastRequest(PlayerIdToClientId(senderPlayer), data.request);
        }

        private void DispatchCooldownRequestOnServer(PlayerID senderPlayer, GC2AbilityCooldownRequestPacket data)
        {
            GetController()?.ProcessCooldownRequest(PlayerIdToClientId(senderPlayer), data.request);
        }

        private void DispatchLearnRequestOnServer(PlayerID senderPlayer, GC2AbilityLearnRequestPacket data)
        {
            GetController()?.ProcessLearnRequest(PlayerIdToClientId(senderPlayer), data.request);
        }

        private void DispatchCancelRequestOnServer(PlayerID senderPlayer, GC2AbilityCancelRequestPacket data)
        {
            GetController()?.ProcessCancelRequest(PlayerIdToClientId(senderPlayer), data.request);
        }

        private void HandleCastResponseClient(PlayerID senderPlayer, GC2AbilityCastResponsePacket data, bool asServer)
        {
            if (asServer) return;
            GetController()?.ReceiveCastResponse(data.response);
        }

        private void HandleCastBroadcastClient(PlayerID senderPlayer, GC2AbilityCastBroadcastPacket data, bool asServer)
        {
            if (asServer || IsHostServerInstance()) return;
            RefreshPawnRegistry(force: true);
            GetController()?.ReceiveCastBroadcast(data.broadcast);
        }

        private void HandleEffectBroadcastClient(PlayerID senderPlayer, GC2AbilityEffectBroadcastPacket data, bool asServer)
        {
            if (asServer || IsHostServerInstance()) return;
            GetController()?.ReceiveEffectBroadcast(data.broadcast);
        }

        private void HandleProjectileSpawnClient(PlayerID senderPlayer, GC2AbilityProjectileSpawnPacket data, bool asServer)
        {
            if (asServer || IsHostServerInstance()) return;
            RefreshPawnRegistry(force: true);
            GetController()?.ReceiveProjectileSpawnBroadcast(data.broadcast);
        }

        private void HandleProjectileEventClient(PlayerID senderPlayer, GC2AbilityProjectileEventPacket data, bool asServer)
        {
            if (asServer || IsHostServerInstance()) return;
            GetController()?.ReceiveProjectileEventBroadcast(data.broadcast);
        }

        private void HandleImpactSpawnClient(PlayerID senderPlayer, GC2AbilityImpactSpawnPacket data, bool asServer)
        {
            if (asServer || IsHostServerInstance()) return;
            RefreshPawnRegistry(force: true);
            GetController()?.ReceiveImpactSpawnBroadcast(data.broadcast);
        }

        private void HandleImpactHitClient(PlayerID senderPlayer, GC2AbilityImpactHitPacket data, bool asServer)
        {
            if (asServer || IsHostServerInstance()) return;
            GetController()?.ReceiveImpactHitBroadcast(data.broadcast);
        }

        private void HandleCooldownResponseClient(PlayerID senderPlayer, GC2AbilityCooldownResponsePacket data, bool asServer)
        {
            if (asServer) return;
            GetController()?.ReceiveCooldownResponse(data.response);
        }

        private void HandleCooldownBroadcastClient(PlayerID senderPlayer, GC2AbilityCooldownBroadcastPacket data, bool asServer)
        {
            if (asServer || IsHostServerInstance()) return;
            RefreshPawnRegistry(force: true);
            GetController()?.ReceiveCooldownBroadcast(data.broadcast);
        }

        private void HandleLearnResponseClient(PlayerID senderPlayer, GC2AbilityLearnResponsePacket data, bool asServer)
        {
            if (asServer) return;
            GetController()?.ReceiveLearnResponse(data.response);
        }

        private void HandleLearnBroadcastClient(PlayerID senderPlayer, GC2AbilityLearnBroadcastPacket data, bool asServer)
        {
            if (asServer || IsHostServerInstance()) return;
            RefreshPawnRegistry(force: true);
            GetController()?.ReceiveLearnBroadcast(data.broadcast);
        }

        private void HandleCancelResponseClient(PlayerID senderPlayer, GC2AbilityCancelResponsePacket data, bool asServer)
        {
            if (asServer) return;
            GetController()?.ReceiveCancelResponse(data.response);
        }

        private bool IsHostServerInstance()
        {
            NetworkManager nm = ActiveManager;
            return nm != null && nm.isServer;
        }

        private float GetNetworkTime()
        {
            PurrNetTransportBridge bridge = CoreBridge;
            return bridge != null ? bridge.ServerTime : Time.time;
        }

        private uint GetLocalPlayerNetworkId()
        {
            NetworkManager nm = ActiveManager;
            if (nm == null || !nm.isClient) return 0;

            uint clientId = PlayerIdToClientId(nm.localPlayer);
            if (NetworkTransportBridge.IsValidClientId(clientId))
            {
                PurrNetTransportBridge bridge = CoreBridge;
                if (bridge != null && bridge.TryGetRepresentativeCharacterId(clientId, out uint characterId))
                {
                    return characterId;
                }
            }

            NetworkCharacter[] characters = FindObjectsByType<NetworkCharacter>(
                FindObjectsInactive.Exclude,
                FindObjectsSortMode.None);
            for (int i = 0; i < characters.Length; i++)
            {
                NetworkCharacter character = characters[i];
                if (character == null || !character.IsOwnerInstance || character.NetworkId == 0) continue;
                return character.NetworkId;
            }

            return 0;
        }

        private void Log(string message)
        {
            if (!m_LogNetworkMessages) return;
            Debug.Log($"[PurrNetAbilitiesTransportBridge] {message}", this);
        }

        private static string DescribePawnAbilities(Pawn pawn)
        {
            if (pawn == null) return "pawn=null";

            Caster caster = pawn.GetFeature<Caster>();
            if (caster == null) return "caster=False";

            const int MaxSlotsToProbe = 16;
            int populated = 0;
            string names = string.Empty;

            for (int i = 0; i < MaxSlotsToProbe; i++)
            {
                Ability ability = caster.GetSlottedAbility(i);
                if (ability == null) continue;

                if (names.Length > 0) names += ",";
                names += $"{i}:{ability.name}#{ability.ID.Hash}";
                populated++;
            }

            return $"caster=True populatedSlots={populated} slots=[{names}]";
        }

        private static NetworkAbilitiesController GetController()
        {
            return NetworkAbilitiesController.HasInstance
                ? NetworkAbilitiesController.Instance
                : FindFirstObjectByType<NetworkAbilitiesController>();
        }

        private static NetworkCharacter ResolveNetworkCharacter(Pawn pawn)
        {
            if (pawn == null) return null;
            NetworkCharacter networkCharacter = pawn.GetComponent<NetworkCharacter>();
            if (networkCharacter != null) return networkCharacter;
            return pawn.GetComponentInParent<NetworkCharacter>();
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

            IReadOnlyList<PlayerID> players = manager.players;
            for (int i = 0; i < players.Count; i++)
            {
                PlayerID candidate = players[i];
                if (PlayerIdToClientId(candidate) == clientId)
                {
                    playerId = candidate;
                    return true;
                }
            }

            return false;
        }
    }
}
#endif
