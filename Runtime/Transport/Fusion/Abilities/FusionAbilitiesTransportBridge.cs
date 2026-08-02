#if GC2_ABILITIES
using System;
using System.Collections.Generic;
using Arawn.GameCreator2.Networking.Transport.Fusion;
using DaimahouGames.Runtime.Abilities;
using DaimahouGames.Runtime.Pawns;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Abilities.Transport.Fusion
{
    [DisallowMultipleComponent]
    [AddComponentMenu("Game Creator/Network/Transport/Fusion Abilities Bridge")]
    [DefaultExecutionOrder(-335)]
    public sealed class FusionAbilitiesTransportBridge : MonoBehaviour,
        IFusionGameplayReadinessParticipant,
        IFusionFullSnapshotProducer
    {
        public const ushort ModuleId = 51;

        private enum MessageType : ushort
        {
            CastRequest = 1,
            CastResponse = 2,
            CastBroadcast = 3,
            EffectBroadcast = 4,
            ProjectileSpawn = 5,
            ProjectileEvent = 6,
            ImpactSpawn = 7,
            ImpactHit = 8,
            CooldownRequest = 9,
            CooldownResponse = 10,
            CooldownBroadcast = 11,
            LearnRequest = 12,
            LearnResponse = 13,
            LearnBroadcast = 14,
            CancelRequest = 15,
            CancelResponse = 16,
            FullSnapshot = 17
        }

        [Header("Fusion")]
        [SerializeField] private FusionTransportBridge m_TransportBridge;

        [Header("Abilities Assets")]
        [SerializeField] private Ability[] m_RegisterAbilities = Array.Empty<Ability>();
        [SerializeField] private Projectile[] m_RegisterProjectiles = Array.Empty<Projectile>();
        [SerializeField] private Impact[] m_RegisterImpacts = Array.Empty<Impact>();

        [Header("Pawns")]
        [SerializeField] private bool m_AutoRegisterScenePawns = true;
        [Min(0.05f)]
        [SerializeField] private float m_PawnScanInterval = 0.25f;

        [Header("Debug")]
        [SerializeField] private bool m_LogNetworkMessages;

        private readonly Dictionary<uint, Pawn> m_RegisteredPawns = new(64);
        private readonly List<uint> m_RemoveBuffer = new(16);

        private FusionTransportBridge m_BoundBridge;
        private NetworkAbilitiesController m_WiredController;
        private bool m_ControllerInitialized;
        private bool m_LastServer;
        private bool m_LastClient;
        private bool m_AssetsRegistered;
        private float m_NextPawnScanTime;

        public string GameplayReadinessName => "Abilities";
        public FusionTransportBridge GameplayReadinessTransport => m_BoundBridge;
        public ushort GameplayReadinessModuleId => ModuleId;
        public ushort FullSnapshotModuleId => ModuleId;
        public string FullSnapshotProducerName => "Abilities";

        public bool IsGameplayReady(FusionNetworkIdentity identity)
        {
            if (!isActiveAndEnabled || identity == null || identity.NetworkId == 0 ||
                !identity.TransportAdmitted || m_BoundBridge == null ||
                !m_BoundBridge.IsClient)
            {
                return false;
            }

            WireController();
            RegisterConfiguredAssets();
            RefreshPawnRegistry(force: true);
            if (!m_ControllerInitialized || m_WiredController == null ||
                m_WiredController != GetController())
            {
                return false;
            }

            Pawn relevant = identity.GetComponentInChildren<Pawn>(true);
            if (relevant == null) return true;

            return m_RegisteredPawns.TryGetValue(
                       identity.NetworkId, out Pawn registered) && registered == relevant;
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
            WireController();
            RegisterConfiguredAssets();
            RefreshPawnRegistry(force: true);
        }

        private void Start()
        {
            TryBindTransport(force: false);
            WireController();
            RegisterConfiguredAssets();
            RefreshPawnRegistry(force: true);
        }

        private void Update()
        {
            TryBindTransport(force: false);
            WireController();
            RegisterConfiguredAssets();

            if (!m_AutoRegisterScenePawns || Time.unscaledTime < m_NextPawnScanTime) return;
            m_NextPawnScanTime = Time.unscaledTime + Mathf.Max(0.05f, m_PawnScanInterval);
            RefreshPawnRegistry(force: false);
        }

        private void OnDisable()
        {
            UnbindTransport();
            UnwireController();
            UnregisterAllPawns();
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
                Log("module handler registration was rejected; another Abilities bridge is already bound");
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
            WireController(forceRoleRefresh: true);
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
                    ? context.Fail("Abilities bridge is not bound as the current authority.")
                    : default;
            }

            WireController(forceRoleRefresh: true);
            NetworkAbilitiesController controller = GetController();
            if (controller == null || controller != m_WiredController || !m_ControllerInitialized)
                return context.Fail("NetworkAbilitiesController is unavailable or not initialized.");

            RefreshPawnRegistry(force: true);
            NetworkAbilitiesFullSnapshot snapshot =
                controller.CaptureFullSnapshot(m_RegisteredPawns.Keys);
            if (!SendToClient(
                context.ClientId,
                MessageType.FullSnapshot,
                snapshot,
                (writer, value) => writer.Write(value)))
            {
                return context.Fail("Could not enqueue the Abilities full snapshot.");
            }
            return context.Complete();
        }

        private void HandleAuthorityChanged(bool isAuthority, uint authorityEpoch)
        {
            NetworkAbilitiesController controller = GetController();
            RefreshPawnRegistry(force: true);
            NetworkAbilitiesFullSnapshot replica = controller != null
                ? controller.CaptureFullSnapshot(m_RegisteredPawns.Keys)
                : default;

            if (isAuthority && ContainsActiveCast(replica))
            {
                const string reason =
                    "Fusion Shared authority changed while an Abilities cast was active. " +
                    "The Daimahou RuntimeAbility execution task cannot be reconstructed safely; " +
                    "the session is shutting down to prevent competing or divergent authority.";
                Debug.LogError("[FusionAbilitiesTransportBridge] " + reason, this);
                m_BoundBridge?.ShutdownSessionForAuthorityFailure(reason);
                return;
            }

            WireController(forceRoleRefresh: true);
            controller = GetController();
            if (controller != null && replica.Characters != null)
            {
                controller.ApplyFullSnapshot(replica);
            }

            Log("authority changed server=" + isAuthority + " epoch=" + authorityEpoch);
        }

        private static bool ContainsActiveCast(NetworkAbilitiesFullSnapshot snapshot)
        {
            NetworkAbilityCharacterSnapshot[] characters = snapshot.Characters;
            if (characters == null) return false;

            for (int i = 0; i < characters.Length; i++)
            {
                if (characters[i].State.IsCasting ||
                    characters[i].State.CurrentCastId != 0)
                {
                    return true;
                }

                NetworkAbilityCastBroadcast[] casts = characters[i].ActiveCasts;
                if (casts != null && casts.Length > 0) return true;
            }

            return false;
        }

        private void WireController(bool forceRoleRefresh = false)
        {
            NetworkAbilitiesController controller = GetController();
            if (controller == null) return;
            if (m_WiredController != controller)
            {
                UnwireController();
                m_WiredController = controller;
            }

            NetworkAbilitiesManager.Initialize(GetNetworkTime, GetLocalPlayerNetworkId);
            NetworkAbilitiesManager.WireUpController(controller);

            controller.SendCastRequestToServer -= SendCastRequest;
            controller.SendCastRequestToServer += SendCastRequest;
            controller.SendCastResponseToClient -= SendCastResponse;
            controller.SendCastResponseToClient += SendCastResponse;
            controller.BroadcastCastToClients -= BroadcastCast;
            controller.BroadcastCastToClients += BroadcastCast;
            controller.BroadcastEffectToClients -= BroadcastEffect;
            controller.BroadcastEffectToClients += BroadcastEffect;
            controller.BroadcastProjectileSpawnToClients -= BroadcastProjectileSpawn;
            controller.BroadcastProjectileSpawnToClients += BroadcastProjectileSpawn;
            controller.BroadcastProjectileEventToClients -= BroadcastProjectileEvent;
            controller.BroadcastProjectileEventToClients += BroadcastProjectileEvent;
            controller.BroadcastImpactSpawnToClients -= BroadcastImpactSpawn;
            controller.BroadcastImpactSpawnToClients += BroadcastImpactSpawn;
            controller.BroadcastImpactHitToClients -= BroadcastImpactHit;
            controller.BroadcastImpactHitToClients += BroadcastImpactHit;
            controller.SendCooldownRequestToServer -= SendCooldownRequest;
            controller.SendCooldownRequestToServer += SendCooldownRequest;
            controller.SendCooldownResponseToClient -= SendCooldownResponse;
            controller.SendCooldownResponseToClient += SendCooldownResponse;
            controller.BroadcastCooldownToClients -= BroadcastCooldown;
            controller.BroadcastCooldownToClients += BroadcastCooldown;
            controller.SendLearnRequestToServer -= SendLearnRequest;
            controller.SendLearnRequestToServer += SendLearnRequest;
            controller.SendLearnResponseToClient -= SendLearnResponse;
            controller.SendLearnResponseToClient += SendLearnResponse;
            controller.BroadcastLearnToClients -= BroadcastLearn;
            controller.BroadcastLearnToClients += BroadcastLearn;
            controller.SendCancelRequestToServer -= SendCancelRequest;
            controller.SendCancelRequestToServer += SendCancelRequest;
            controller.SendCancelResponseToClient -= SendCancelResponse;
            controller.SendCancelResponseToClient += SendCancelResponse;

            bool isServer = m_BoundBridge != null && m_BoundBridge.IsServer;
            bool isClient = m_BoundBridge != null && m_BoundBridge.IsClient;
            if (forceRoleRefresh || !m_ControllerInitialized ||
                isServer != m_LastServer || isClient != m_LastClient)
            {
                if (isServer && isClient) controller.InitializeAsHost();
                else if (isServer) controller.InitializeAsServer();
                else if (isClient) controller.InitializeAsClient();

                m_ControllerInitialized = isServer || isClient;
                m_LastServer = isServer;
                m_LastClient = isClient;
            }
        }

        private void UnwireController()
        {
            NetworkAbilitiesController controller = m_WiredController;
            if (controller != null)
            {
                controller.SendCastRequestToServer -= SendCastRequest;
                controller.SendCastResponseToClient -= SendCastResponse;
                controller.BroadcastCastToClients -= BroadcastCast;
                controller.BroadcastEffectToClients -= BroadcastEffect;
                controller.BroadcastProjectileSpawnToClients -= BroadcastProjectileSpawn;
                controller.BroadcastProjectileEventToClients -= BroadcastProjectileEvent;
                controller.BroadcastImpactSpawnToClients -= BroadcastImpactSpawn;
                controller.BroadcastImpactHitToClients -= BroadcastImpactHit;
                controller.SendCooldownRequestToServer -= SendCooldownRequest;
                controller.SendCooldownResponseToClient -= SendCooldownResponse;
                controller.BroadcastCooldownToClients -= BroadcastCooldown;
                controller.SendLearnRequestToServer -= SendLearnRequest;
                controller.SendLearnResponseToClient -= SendLearnResponse;
                controller.BroadcastLearnToClients -= BroadcastLearn;
                controller.SendCancelRequestToServer -= SendCancelRequest;
                controller.SendCancelResponseToClient -= SendCancelResponse;
            }
            m_WiredController = null;
            m_ControllerInitialized = false;
        }

        private void RegisterConfiguredAssets()
        {
            if (m_AssetsRegistered) return;

            if (m_RegisterAbilities != null)
            {
                for (int i = 0; i < m_RegisterAbilities.Length; i++)
                    if (m_RegisterAbilities[i] != null)
                        NetworkAbilitiesManager.RegisterAbility(m_RegisterAbilities[i]);
            }
            if (m_RegisterProjectiles != null)
            {
                for (int i = 0; i < m_RegisterProjectiles.Length; i++)
                    if (m_RegisterProjectiles[i] != null)
                        NetworkAbilitiesManager.RegisterProjectile(m_RegisterProjectiles[i]);
            }
            if (m_RegisterImpacts != null)
            {
                for (int i = 0; i < m_RegisterImpacts.Length; i++)
                    if (m_RegisterImpacts[i] != null)
                        NetworkAbilitiesManager.RegisterImpact(m_RegisterImpacts[i]);
            }
            m_AssetsRegistered = true;
        }

        private void RefreshPawnRegistry(bool force)
        {
            PrunePawnRegistry();
            if (!m_AutoRegisterScenePawns && !force) return;

            Pawn[] pawns = FindObjectsByType<Pawn>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            for (int i = 0; i < pawns.Length; i++) RegisterPawn(pawns[i]);
        }

        private void RegisterPawn(Pawn pawn)
        {
            if (pawn == null) return;
            NetworkCharacter character = ResolveNetworkCharacter(pawn);
            if (character == null || character.NetworkId == 0) return;
            uint id = character.NetworkId;
            if (m_RegisteredPawns.TryGetValue(id, out Pawn existing) && existing == pawn) return;

            NetworkAbilitiesManager.RegisterPawn(pawn, id);
            m_RegisteredPawns[id] = pawn;
        }

        private void PrunePawnRegistry()
        {
            m_RemoveBuffer.Clear();
            foreach (KeyValuePair<uint, Pawn> pair in m_RegisteredPawns)
            {
                NetworkCharacter character = pair.Value != null
                    ? ResolveNetworkCharacter(pair.Value) : null;
                if (pair.Value == null || character == null || character.NetworkId != pair.Key)
                    m_RemoveBuffer.Add(pair.Key);
            }

            for (int i = 0; i < m_RemoveBuffer.Count; i++)
            {
                NetworkAbilitiesManager.UnregisterPawn(m_RemoveBuffer[i]);
                m_RegisteredPawns.Remove(m_RemoveBuffer[i]);
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

        private void SendCastRequest(NetworkAbilityCastRequest value) =>
            SendToAuthority(MessageType.CastRequest, value, (writer, item) => writer.Write(item));
        private void SendCooldownRequest(NetworkCooldownRequest value) =>
            SendToAuthority(MessageType.CooldownRequest, value, (writer, item) => writer.Write(item));
        private void SendLearnRequest(NetworkAbilityLearnRequest value) =>
            SendToAuthority(MessageType.LearnRequest, value, (writer, item) => writer.Write(item));
        private void SendCancelRequest(NetworkCastCancelRequest value) =>
            SendToAuthority(MessageType.CancelRequest, value, (writer, item) => writer.Write(item));

        private void SendCastResponse(uint clientId, NetworkAbilityCastResponse value) =>
            SendToClient(clientId, MessageType.CastResponse, value, (writer, item) => writer.Write(item));
        private void SendCooldownResponse(uint clientId, NetworkCooldownResponse value) =>
            SendToClient(clientId, MessageType.CooldownResponse, value, (writer, item) => writer.Write(item));
        private void SendLearnResponse(uint clientId, NetworkAbilityLearnResponse value) =>
            SendToClient(clientId, MessageType.LearnResponse, value, (writer, item) => writer.Write(item));
        private void SendCancelResponse(uint clientId, NetworkCastCancelResponse value) =>
            SendToClient(clientId, MessageType.CancelResponse, value, (writer, item) => writer.Write(item));

        private void BroadcastCast(NetworkAbilityCastBroadcast value) =>
            Broadcast(MessageType.CastBroadcast, value, (writer, item) => writer.Write(item));
        private void BroadcastEffect(NetworkAbilityEffectBroadcast value) =>
            Broadcast(MessageType.EffectBroadcast, value, (writer, item) => writer.Write(item));
        private void BroadcastProjectileSpawn(NetworkProjectileSpawnBroadcast value) =>
            Broadcast(MessageType.ProjectileSpawn, value, (writer, item) => writer.Write(item));
        private void BroadcastProjectileEvent(NetworkProjectileEventBroadcast value) =>
            Broadcast(MessageType.ProjectileEvent, value, (writer, item) => writer.Write(item));
        private void BroadcastImpactSpawn(NetworkImpactSpawnBroadcast value) =>
            Broadcast(MessageType.ImpactSpawn, value, (writer, item) => writer.Write(item));
        private void BroadcastImpactHit(NetworkImpactHitBroadcast value) =>
            Broadcast(MessageType.ImpactHit, value, (writer, item) => writer.Write(item));
        private void BroadcastCooldown(NetworkCooldownBroadcast value) =>
            Broadcast(MessageType.CooldownBroadcast, value, (writer, item) => writer.Write(item));
        private void BroadcastLearn(NetworkAbilityLearnBroadcast value) =>
            Broadcast(MessageType.LearnBroadcast, value, (writer, item) => writer.Write(item));

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

        private void HandleModuleMessage(FusionModuleMessage message)
        {
            switch ((MessageType)message.MessageType)
            {
                case MessageType.CastRequest:
                    ReceiveRequest(message, (NetworkAbilitiesController c, uint s, NetworkAbilityCastRequest v) => c.ProcessCastRequest(s, v)); break;
                case MessageType.CooldownRequest:
                    ReceiveRequest(message, (NetworkAbilitiesController c, uint s, NetworkCooldownRequest v) => c.ProcessCooldownRequest(s, v)); break;
                case MessageType.LearnRequest:
                    ReceiveRequest(message, (NetworkAbilitiesController c, uint s, NetworkAbilityLearnRequest v) => c.ProcessLearnRequest(s, v)); break;
                case MessageType.CancelRequest:
                    ReceiveRequest(message, (NetworkAbilitiesController c, uint s, NetworkCastCancelRequest v) => c.ProcessCancelRequest(s, v)); break;

                case MessageType.CastResponse:
                    ReceiveAuthority(message, (NetworkAbilitiesController c, NetworkAbilityCastResponse v) => c.ReceiveCastResponse(v)); break;
                case MessageType.CooldownResponse:
                    ReceiveAuthority(message, (NetworkAbilitiesController c, NetworkCooldownResponse v) => c.ReceiveCooldownResponse(v)); break;
                case MessageType.LearnResponse:
                    ReceiveAuthority(message, (NetworkAbilitiesController c, NetworkAbilityLearnResponse v) => c.ReceiveLearnResponse(v)); break;
                case MessageType.CancelResponse:
                    ReceiveAuthority(message, (NetworkAbilitiesController c, NetworkCastCancelResponse v) => c.ReceiveCancelResponse(v)); break;

                case MessageType.CastBroadcast:
                    ReceiveAuthority(message, (NetworkAbilitiesController c, NetworkAbilityCastBroadcast v) => c.ReceiveCastBroadcast(v)); break;
                case MessageType.EffectBroadcast:
                    ReceiveAuthority(message, (NetworkAbilitiesController c, NetworkAbilityEffectBroadcast v) => c.ReceiveEffectBroadcast(v)); break;
                case MessageType.ProjectileSpawn:
                    ReceiveAuthority(message, (NetworkAbilitiesController c, NetworkProjectileSpawnBroadcast v) => c.ReceiveProjectileSpawnBroadcast(v)); break;
                case MessageType.ProjectileEvent:
                    ReceiveAuthority(message, (NetworkAbilitiesController c, NetworkProjectileEventBroadcast v) => c.ReceiveProjectileEventBroadcast(v)); break;
                case MessageType.ImpactSpawn:
                    ReceiveAuthority(message, (NetworkAbilitiesController c, NetworkImpactSpawnBroadcast v) => c.ReceiveImpactSpawnBroadcast(v)); break;
                case MessageType.ImpactHit:
                    ReceiveAuthority(message, (NetworkAbilitiesController c, NetworkImpactHitBroadcast v) => c.ReceiveImpactHitBroadcast(v)); break;
                case MessageType.CooldownBroadcast:
                    ReceiveAuthority(message, (NetworkAbilitiesController c, NetworkCooldownBroadcast v) => c.ReceiveCooldownBroadcast(v)); break;
                case MessageType.LearnBroadcast:
                    ReceiveAuthority(message, (NetworkAbilitiesController c, NetworkAbilityLearnBroadcast v) => c.ReceiveLearnBroadcast(v)); break;
                case MessageType.FullSnapshot:
                    ReceiveAuthority(message, (NetworkAbilitiesController c, NetworkAbilitiesFullSnapshot v) => c.ApplyFullSnapshot(v)); break;
                default:
                    Log("dropped unknown message type=" + message.MessageType); break;
            }
        }

        private delegate void RequestReceiver<T>(NetworkAbilitiesController controller, uint sender, T value);
        private delegate void AuthorityReceiver<T>(NetworkAbilitiesController controller, T value);

        private void ReceiveRequest<T>(FusionModuleMessage message, RequestReceiver<T> receive)
        {
            if (message.FromAuthority || m_BoundBridge == null || !m_BoundBridge.IsServer) return;
            NetworkAbilitiesController controller = GetController();
            if (controller == null) return;
            RefreshPawnRegistry(force: true);
            if (!FusionValueCodec.TryDecode(
                    message.Payload,
                    (FusionValueReader reader, ref T value) => reader.ReadDynamic(ref value),
                    out T decoded))
            {
                Log("dropped malformed request type=" + message.MessageType);
                return;
            }
            receive(controller, message.SenderClientId, decoded);
        }

        private void ReceiveAuthority<T>(
            FusionModuleMessage message,
            AuthorityReceiver<T> receive)
        {
            if (!message.FromAuthority) return;
            NetworkAbilitiesController controller = GetController();
            if (controller == null) return;
            if (!FusionValueCodec.TryDecode(
                    message.Payload,
                    (FusionValueReader reader, ref T value) => reader.ReadDynamic(ref value),
                    out T decoded))
            {
                Log("dropped malformed authority message type=" + message.MessageType);
                return;
            }
            RefreshPawnRegistry(force: true);
            receive(controller, decoded);
        }

        private float GetNetworkTime() =>
            m_BoundBridge != null ? m_BoundBridge.ServerTime : Time.time;

        private uint GetLocalPlayerNetworkId()
        {
            if (m_BoundBridge != null &&
                m_BoundBridge.TryGetLocalClientId(out uint clientId) &&
                m_BoundBridge.TryGetRepresentativeCharacterId(clientId, out uint characterId))
            {
                return characterId;
            }

            NetworkCharacter[] characters = FindObjectsByType<NetworkCharacter>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            for (int i = 0; i < characters.Length; i++)
            {
                if (characters[i] != null &&
                    characters[i].IsOwnerInstance &&
                    characters[i].NetworkId != 0)
                {
                    return characters[i].NetworkId;
                }
            }
            return 0;
        }

        private static NetworkCharacter ResolveNetworkCharacter(Pawn pawn)
        {
            if (pawn == null) return null;
            NetworkCharacter character = pawn.GetComponent<NetworkCharacter>();
            return character != null ? character : pawn.GetComponentInParent<NetworkCharacter>();
        }

        private void Log(string message)
        {
            if (!m_LogNetworkMessages) return;
            Debug.Log("[FusionAbilitiesTransportBridge] " + message, this);
        }

        private static NetworkAbilitiesController GetController()
        {
            return NetworkAbilitiesController.HasInstance
                ? NetworkAbilitiesController.Instance
                : FindFirstObjectByType<NetworkAbilitiesController>();
        }
    }

    internal static class FusionAbilitiesDynamicCodec
    {
        public static void ReadDynamic<T>(this FusionValueReader reader, ref T value)
        {
            object boxed;
            if (typeof(T) == typeof(NetworkAbilityCastRequest)) { NetworkAbilityCastRequest v = default; reader.Read(ref v); boxed = v; }
            else if (typeof(T) == typeof(NetworkCooldownRequest)) { NetworkCooldownRequest v = default; reader.Read(ref v); boxed = v; }
            else if (typeof(T) == typeof(NetworkAbilityLearnRequest)) { NetworkAbilityLearnRequest v = default; reader.Read(ref v); boxed = v; }
            else if (typeof(T) == typeof(NetworkCastCancelRequest)) { NetworkCastCancelRequest v = default; reader.Read(ref v); boxed = v; }
            else if (typeof(T) == typeof(NetworkAbilityCastResponse)) { NetworkAbilityCastResponse v = default; reader.Read(ref v); boxed = v; }
            else if (typeof(T) == typeof(NetworkCooldownResponse)) { NetworkCooldownResponse v = default; reader.Read(ref v); boxed = v; }
            else if (typeof(T) == typeof(NetworkAbilityLearnResponse)) { NetworkAbilityLearnResponse v = default; reader.Read(ref v); boxed = v; }
            else if (typeof(T) == typeof(NetworkCastCancelResponse)) { NetworkCastCancelResponse v = default; reader.Read(ref v); boxed = v; }
            else if (typeof(T) == typeof(NetworkAbilityCastBroadcast)) { NetworkAbilityCastBroadcast v = default; reader.Read(ref v); boxed = v; }
            else if (typeof(T) == typeof(NetworkAbilityEffectBroadcast)) { NetworkAbilityEffectBroadcast v = default; reader.Read(ref v); boxed = v; }
            else if (typeof(T) == typeof(NetworkProjectileSpawnBroadcast)) { NetworkProjectileSpawnBroadcast v = default; reader.Read(ref v); boxed = v; }
            else if (typeof(T) == typeof(NetworkProjectileEventBroadcast)) { NetworkProjectileEventBroadcast v = default; reader.Read(ref v); boxed = v; }
            else if (typeof(T) == typeof(NetworkImpactSpawnBroadcast)) { NetworkImpactSpawnBroadcast v = default; reader.Read(ref v); boxed = v; }
            else if (typeof(T) == typeof(NetworkImpactHitBroadcast)) { NetworkImpactHitBroadcast v = default; reader.Read(ref v); boxed = v; }
            else if (typeof(T) == typeof(NetworkCooldownBroadcast)) { NetworkCooldownBroadcast v = default; reader.Read(ref v); boxed = v; }
            else if (typeof(T) == typeof(NetworkAbilityLearnBroadcast)) { NetworkAbilityLearnBroadcast v = default; reader.Read(ref v); boxed = v; }
            else if (typeof(T) == typeof(NetworkAbilitiesFullSnapshot)) { NetworkAbilitiesFullSnapshot v = default; reader.Read(ref v); boxed = v; }
            else throw new InvalidOperationException("Unsupported Abilities payload type " + typeof(T).FullName);
            value = (T)boxed;
        }
    }
}
#endif
