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
            CharacterSnapshot = 15,
            FreeFlowState = 16
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
        private readonly List<NetworkMeleeController> m_OwnerWeaponPublishBuffer = new(4);
        private const int EquipmentSendLimit = 4;
        private const float EquipmentRetryInterval = 0.25f;
        private const int DeferredFallbackLimit = 128;
        private const float DeferredFallbackLifetime = 2f;

        // A null recipient is the local owner's route to authority.
        private readonly Dictionary<(uint? ClientId, uint ActorId), PendingEquipment> m_PendingEquipment = new();
        private readonly List<(uint? ClientId, uint ActorId)> m_EquipmentRetryKeys = new();
        private readonly LinkedList<DeferredFallback> m_DeferredFallbacks = new();

        private sealed class PendingEquipment
        {
            public NetworkCharacter Actor;
            public NetworkMeleeWeaponState State;
            public int Attempts;
            public float RetryAt;
            public bool Accepted;
            public bool Abandoned;
        }

        private readonly struct DeferredFallback
        {
            public readonly uint ClientId;
            public readonly NetworkCharacter Actor;
            public readonly NetworkReactionBroadcast Reaction;
            public readonly PendingEquipment Equipment;
            public readonly float ExpiresAt;

            public DeferredFallback(uint clientId, NetworkCharacter actor, NetworkReactionBroadcast reaction,
                PendingEquipment equipment)
            {
                ClientId = clientId;
                Actor = actor;
                Reaction = reaction;
                Equipment = equipment;
                ExpiresAt = Time.unscaledTime + DeferredFallbackLifetime;
            }
        }

#if UNITY_EDITOR
        // Offline tests replace only the equipment enqueue result, not admission or state handling.
        private Func<uint?, byte[], bool?> m_EquipmentSendProbe = null;
#endif

        private FusionTransportBridge m_BoundBridge;
        private NetworkMeleeManager m_WiredManager;
        private bool m_ManagerInitialized;
        private bool m_LastServer;
        private bool m_LastClient;
        private bool m_AssetsRegistered;
        private float m_NextControllerScanTime;
        private ulong m_OwnerWeaponPublishRevision;

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

            if (m_AutoRegisterSceneControllers && Time.unscaledTime >= m_NextControllerScanTime)
            {
                m_NextControllerScanTime = Time.unscaledTime + Mathf.Max(0.05f, m_ControllerScanInterval);
                RefreshControllerRegistry(force: false);
            }
            PublishOwnerWeaponsWhenReady();
            FlushEquipmentSends();
            FlushDeferredFallbacks();
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
            if (candidate == null) candidate = UnityObjectSearch.FindAny<FusionTransportBridge>();
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
            candidate.ClientSnapshotAcknowledged += CatchUpClientWeaponStates;
            candidate.PlayerObservedLeft += DiscardDepartedClientEquipment;
            candidate.RunnerObservedUnbound += DiscardUnboundRunnerEquipment;
            candidate.LocalSceneObservedStarted += DiscardPreviousSceneEquipment;
            WireManager(forceRoleRefresh: true);
        }

        private void UnbindTransport()
        {
            ResetEquipmentDelivery();
            if (m_BoundBridge == null) return;
            m_BoundBridge.UnregisterFullSnapshotProducer(this);
            m_BoundBridge.AuthorityChanged -= HandleAuthorityChanged;
            m_BoundBridge.ClientSnapshotAcknowledged -= CatchUpClientWeaponStates;
            m_BoundBridge.PlayerObservedLeft -= DiscardDepartedClientEquipment;
            m_BoundBridge.RunnerObservedUnbound -= DiscardUnboundRunnerEquipment;
            m_BoundBridge.LocalSceneObservedStarted -= DiscardPreviousSceneEquipment;
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
            ResetEquipmentDelivery();
            WireManager(forceRoleRefresh: true);
            RefreshControllerRegistry(force: true);
            Log("authority changed server=" + isAuthority + " epoch=" + authorityEpoch);
        }

        private void PublishOwnerWeaponsWhenReady()
        {
            ulong revision = m_BoundBridge != null ? m_BoundBridge.LocalGameplaySnapshotRevision : 0;
            if (revision == 0 || m_OwnerWeaponPublishRevision == revision) return;
            m_OwnerWeaponPublishRevision = revision;
            DiscardEquipmentForRecipient(null);

            // An equip rejected during resync may not emit another event. Rebuild the
            // current state after each distinct snapshot ack, not only on a new epoch.
            // Copy before sending: host loopback can refresh the registry synchronously.
            m_OwnerWeaponPublishBuffer.Clear();
            foreach (NetworkMeleeController controller in m_RegisteredControllers.Values)
            {
                if (controller != null && controller.IsLocalClient) m_OwnerWeaponPublishBuffer.Add(controller);
            }
            foreach (NetworkMeleeController controller in m_OwnerWeaponPublishBuffer)
            {
                if (controller != null && controller.IsLocalClient) controller.PublishCurrentWeaponState();
            }
            m_OwnerWeaponPublishBuffer.Clear();
        }

        private void CatchUpClientWeaponStates(uint clientId)
        {
            if (m_BoundBridge == null || !m_BoundBridge.IsServer || !m_BoundBridge.IsClientReady(clientId)) return;
            if (m_BoundBridge.TryGetLocalClientId(out uint localClientId) && clientId == localClientId) return;
            DiscardEquipmentForRecipient(clientId);
            NetworkMeleeManager manager = GetManager();
            if (manager == null) return;

            // A change after snapshot capture may have skipped this in-flight client.
            // Send persistent equipment only: do not replay other state or combat events.
            foreach (NetworkMeleeCharacterSnapshot snapshot in manager.CaptureCharacterSnapshots())
            {
                if (!snapshot.HasWeaponState) continue;
                NetworkCharacter actor = ResolveNetworkCharacter(snapshot.CharacterNetworkId);
                QueueEquipment(clientId, snapshot.CharacterNetworkId, actor, snapshot.WeaponState);
            }
        }

        private void ResetEquipmentDelivery()
        {
            m_OwnerWeaponPublishRevision = 0;
            m_PendingEquipment.Clear();
            m_DeferredFallbacks.Clear();
        }

        private void DiscardUnboundRunnerEquipment(FusionRunnerBindingInfo _) => ResetEquipmentDelivery();
        private void DiscardPreviousSceneEquipment(FusionSceneLifecycleInfo _) => ResetEquipmentDelivery();
        private void DiscardDepartedClientEquipment(FusionPlayerConnectionInfo client) =>
            DiscardEquipmentForRecipient(client.ClientId);

        private void DiscardEquipmentForRecipient(uint? clientId)
        {
            if (clientId.HasValue) DiscardDeferredFallbacks(clientId.Value);
            if (m_PendingEquipment.Count == 0) return;
            foreach (var key in new List<(uint? ClientId, uint ActorId)>(m_PendingEquipment.Keys))
            {
                if (key.ClientId == clientId) m_PendingEquipment.Remove(key);
            }
        }

        private void QueueEquipment(
            uint? clientId, uint actorId, NetworkCharacter actor, NetworkMeleeWeaponState state, bool defer = false)
        {
            if (m_BoundBridge == null || actorId == 0 || actor == null || actor.NetworkId != actorId) return;
            var key = (ClientId: clientId, ActorId: actorId);
            float retryAt = 0f;
            if (m_PendingEquipment.TryGetValue(key, out PendingEquipment previous) && previous.Actor == actor)
            {
                if (previous.State.WeaponHash == state.WeaponHash &&
                    previous.State.ShieldFlags == state.ShieldFlags &&
                    previous.State.BlockTiming == state.BlockTiming && !previous.Abandoned) return;
                // A new value replaces an unsent one but does not bypass its pacing.
                retryAt = previous.RetryAt;
            }
            // Do not play the fallback of a superseded update against its replacement.
            if (clientId.HasValue) DiscardDeferredFallbacks(clientId.Value, actorId);
            var pending = new PendingEquipment { Actor = actor, State = state, RetryAt = retryAt };
            m_PendingEquipment[key] = pending;
            if (!defer) TrySendEquipment(key, pending);
        }

        private void FlushEquipmentSends()
        {
            if (m_PendingEquipment.Count == 0) return;
            // An accepted loopback send may synchronously refresh controllers or enqueue fan-out.
            m_EquipmentRetryKeys.AddRange(m_PendingEquipment.Keys);
            foreach (var key in m_EquipmentRetryKeys)
            {
                if (m_PendingEquipment.TryGetValue(key, out PendingEquipment pending)) TrySendEquipment(key, pending);
            }
            m_EquipmentRetryKeys.Clear();
        }

        private void TrySendEquipment((uint? ClientId, uint ActorId) key, PendingEquipment pending)
        {
            FusionTransportBridge bridge = m_BoundBridge;
            NetworkCharacter actor = pending.Actor;
            if (bridge == null || actor == null || !actor.isActiveAndEnabled || actor.NetworkId != key.ActorId ||
                actor.Role == NetworkCharacter.NetworkRole.None ||
                (key.ClientId.HasValue ? !bridge.IsServer || !bridge.IsClientReady(key.ClientId.Value) : !actor.IsOwnerInstance))
            {
                m_PendingEquipment.Remove(key);
                return;
            }

            if (!key.ClientId.HasValue && !bridge.IsLocalGameplayReady) return;
            float now = Time.unscaledTime;
            if (now < pending.RetryAt) return;
            if (ResolveNetworkCharacter(key.ActorId) != actor)
            {
                m_PendingEquipment.Remove(key);
                return;
            }
            if (pending.Abandoned) return;

            // Pace before sending because offline/host loopback can run other callbacks inline.
            pending.RetryAt = now + EquipmentRetryInterval;
            ++pending.Attempts;
            byte[] payload = EncodeWeaponState(key.ActorId, pending.State);
            bool accepted = SendEquipmentPayload(bridge, key.ClientId, payload);
            if (!m_PendingEquipment.TryGetValue(key, out PendingEquipment current) || current != pending) return;
            if (!accepted && pending.Attempts < EquipmentSendLimit) return;

            if (accepted)
            {
                pending.Accepted = true;
                m_PendingEquipment.Remove(key);
            }
            else
            {
                // This recipient may still have an older weapon. Keep a dormant barrier,
                // not more retries, until a new publication or binding invalidation.
                pending.Abandoned = true;
                if (key.ClientId.HasValue) DiscardDeferredFallbacks(key.ClientId.Value, key.ActorId);
                else m_PendingEquipment.Remove(key);
                string destination = key.ClientId.HasValue ? $"client={key.ClientId.Value}" : "authority";
                Debug.LogWarning($"[FusionMeleeTransportBridge] Reliable equipment send abandoned: {destination} " +
                    $"actor={key.ActorId} epoch={bridge.AuthorityEpoch} attempts={pending.Attempts}.", this);
            }
        }

        private bool SendEquipmentPayload(FusionTransportBridge bridge, uint? clientId, byte[] payload)
        {
#if UNITY_EDITOR
            bool? probe = m_EquipmentSendProbe?.Invoke(clientId, payload);
            if (probe.HasValue) return probe.Value;
#endif
            return clientId.HasValue
                ? bridge.SendModuleToClient(clientId.Value, ModuleId, (ushort)MessageType.WeaponState, payload, reliable: true)
                : bridge.SendModuleToAuthority(ModuleId, (ushort)MessageType.WeaponState, payload, reliable: true);
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
            manager.BroadcastFreeFlowStateToAllClients -= BroadcastFreeFlowState;
            manager.BroadcastFreeFlowStateToAllClients += BroadcastFreeFlowState;

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
                manager.BroadcastFreeFlowStateToAllClients -= BroadcastFreeFlowState;
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

            NetworkMeleeController[] controllers = UnityObjectSearch.FindAll<NetworkMeleeController>(FindObjectsInactive.Exclude);
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
            if (controller == null) return;
            NetworkCharacter character = controller.GetComponent<NetworkCharacter>();
            uint networkId = character != null ? character.NetworkId : 0;
            if (networkId == 0) return;

            if (controller.IsLocalClient)
            {
                QueueEquipment(null, networkId, character, state);
                return;
            }

            bool trustedServerNpc = m_BoundBridge != null && m_BoundBridge.IsServer &&
                                    character.IsServerAuthoritativeNPC &&
                                    character.IsServerInstance &&
                                    character.HasSimulationAuthority &&
                                    !m_BoundBridge.TryGetCharacterOwner(networkId, out _);
            if (!trustedServerNpc) return;

            NetworkMeleeManager manager = GetManager();
            manager?.RecordAuthoritativeWeaponState(networkId, state);
            BroadcastWeaponState(networkId, state);
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
        private void BroadcastFreeFlowState(NetworkFreeFlowCombatState value) =>
            Broadcast(MessageType.FreeFlowState, value, (writer, item) => writer.Write(item));
        private void BroadcastCharge(NetworkChargeBroadcast value) =>
            Broadcast(MessageType.ChargeBroadcast, value, (writer, item) => writer.Write(item));
        private void BroadcastReaction(NetworkReactionBroadcast value)
        {
            FusionTransportBridge bridge = m_BoundBridge;
            if (bridge == null || !bridge.IsServer) return;
            RetireSupersededFallbacks(bridge, value);
            NetworkCharacter actor = null;
            if (value.PlaybackKind == NetworkReactionPlaybackKind.Stance && value.ReactionHash == 0 &&
                value.CharacterNetworkId != 0 && m_PendingEquipment.Count != 0)
            {
                foreach (uint clientId in bridge.ConnectedClientIds)
                {
                    if (!bridge.IsClientReady(clientId) ||
                        !m_PendingEquipment.TryGetValue((clientId, value.CharacterNetworkId), out var pending)) continue;
                    actor = ResolveNetworkCharacter(value.CharacterNetworkId);
                    if (actor != null && pending.Actor == actor) break;
                    actor = null;
                }
            }

            if (actor == null)
            {
                Broadcast(MessageType.ReactionBroadcast, value, (writer, item) => writer.Write(item));
                return;
            }

            // Split only this fallback's audience. Targeted live sends drain in Update,
            // outside snapshot production; unaffected recipients don't wait on retries.
            foreach (uint clientId in bridge.ConnectedClientIds)
            {
                if (!bridge.IsClientReady(clientId)) continue;
                m_PendingEquipment.TryGetValue((clientId, value.CharacterNetworkId), out var equipment);
                if (equipment != null && equipment.Actor != actor) equipment = null;
                if (equipment != null && equipment.Abandoned) continue;
                if (m_DeferredFallbacks.Count >= DeferredFallbackLimit) m_DeferredFallbacks.RemoveFirst();
                m_DeferredFallbacks.AddLast(new DeferredFallback(clientId, actor, value, equipment));
            }
        }

        private void RetireSupersededFallbacks(FusionTransportBridge bridge, NetworkReactionBroadcast successor)
        {
            if (successor.CharacterNetworkId == 0 || successor.Sequence == 0 || m_DeferredFallbacks.Count == 0) return;
            NetworkCharacter actor = ResolveNetworkCharacter(successor.CharacterNetworkId);
            if (actor == null) return;

            // A newer authoritative reaction supersedes older deferred presentation for
            // this actor in the current ready audience. Do this before sending: a loopback
            // handler can run synchronously, and an enqueue refusal cannot revive stale work.
            for (var node = m_DeferredFallbacks.First; node != null;)
            {
                var next = node.Next;
                DeferredFallback item = node.Value;
                if (item.Actor == actor && item.Reaction.CharacterNetworkId == successor.CharacterNetworkId &&
                    bridge.IsClientReady(item.ClientId) && item.Reaction.Sequence != 0 &&
                    unchecked((int)(successor.Sequence - item.Reaction.Sequence)) > 0)
                {
                    m_DeferredFallbacks.Remove(node);
                }
                node = next;
            }
        }

        private void DiscardDeferredFallbacks(uint clientId, uint? actorId = null)
        {
            for (var node = m_DeferredFallbacks.First; node != null;)
            {
                var next = node.Next;
                if (node.Value.ClientId == clientId &&
                    (!actorId.HasValue || node.Value.Reaction.CharacterNetworkId == actorId.Value))
                {
                    m_DeferredFallbacks.Remove(node);
                }
                node = next;
            }
        }

        private void FlushDeferredFallbacks()
        {
            FusionTransportBridge bridge = m_BoundBridge;
            if (bridge == null || !bridge.IsServer) return;
            for (var node = m_DeferredFallbacks.First; node != null && node.List == m_DeferredFallbacks;)
            {
                var next = node.Next;
                DeferredFallback item = node.Value;
                uint actorId = item.Reaction.CharacterNetworkId;
                if (Time.unscaledTime >= item.ExpiresAt || !bridge.IsClientReady(item.ClientId) ||
                    item.Actor == null || !item.Actor.isActiveAndEnabled ||
                    item.Actor.Role == NetworkCharacter.NetworkRole.None || ResolveNetworkCharacter(actorId) != item.Actor)
                {
                    m_DeferredFallbacks.Remove(node);
                }
                else if (item.Equipment == null || item.Equipment.Accepted)
                {
                    // Do not retry transient sends or replay a reentrant loopback callback.
                    m_DeferredFallbacks.Remove(node);
                    SendToClient(item.ClientId, MessageType.ReactionBroadcast, item.Reaction,
                        (writer, value) => writer.Write(value));
                }
                else if (!m_PendingEquipment.TryGetValue((item.ClientId, actorId), out var current) ||
                         current != item.Equipment)
                {
                    m_DeferredFallbacks.Remove(node);
                }
                node = next;
            }
        }

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

        private void BroadcastWeaponState(uint characterNetworkId, NetworkMeleeWeaponState state)
        {
            FusionTransportBridge bridge = m_BoundBridge;
            if (bridge == null || !bridge.IsServer || characterNetworkId == 0) return;
            NetworkCharacter actor = ResolveNetworkCharacter(characterNetworkId);
            // A broadcast count cannot identify which recipient refused. Enumerate the
            // same ready audience and retain only the unsuccessful targeted sends.
            // Flush outside synchronous snapshot production: targeted sends made inside
            // a producer would be counted as output of that producer.
            foreach (uint clientId in bridge.ConnectedClientIds)
            {
                if (bridge.IsClientReady(clientId)) QueueEquipment(clientId, characterNetworkId, actor, state, defer: true);
            }
        }

        private static byte[] EncodeWeaponState(uint characterNetworkId, NetworkMeleeWeaponState state)
        {
            var writer = new FusionValueWriter();
            writer.Write(characterNetworkId);
            writer.Write(state);
            return writer.ToArray();
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
                case MessageType.FreeFlowState:
                    ReceiveAuthority(message, (NetworkMeleeManager manager, NetworkFreeFlowCombatState value) =>
                    {
                        RefreshControllerRegistry(force: true);
                        manager.ReceiveFreeFlowState(value);
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
                : UnityObjectSearch.FindAny<NetworkMeleeManager>();
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
            else if (typeof(T) == typeof(NetworkFreeFlowCombatState))
            {
                NetworkFreeFlowCombatState typed = default; reader.Read(ref typed); boxed = typed;
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
