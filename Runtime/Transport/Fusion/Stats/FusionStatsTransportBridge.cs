#if GC2_STATS
using System.Collections.Generic;
using Arawn.GameCreator2.Networking.Transport.Fusion;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Stats.Transport.Fusion
{
    [DisallowMultipleComponent]
    [AddComponentMenu("Game Creator/Network/Transport/Fusion Stats Bridge")]
    [DefaultExecutionOrder(-340)]
    public sealed class FusionStatsTransportBridge : FusionModuleTransportBridgeBase,
        IFusionGameplayReadinessParticipant
    {
        private enum MessageType : ushort
        {
            StatModifyRequest = 1,
            StatModifyResponse = 2,
            AttributeModifyRequest = 3,
            AttributeModifyResponse = 4,
            StatusEffectRequest = 5,
            StatusEffectResponse = 6,
            StatModifierRequest = 7,
            StatModifierResponse = 8,
            ClearStatusEffectsRequest = 9,
            ClearStatusEffectsResponse = 10,
            StatChangeBroadcast = 11,
            AttributeChangeBroadcast = 12,
            StatusEffectBroadcast = 13,
            StatModifierBroadcast = 14,
            Snapshot = 15,
            Delta = 16
        }

        [SerializeField] private bool m_AutoRegisterSceneControllers = true;
        [Min(0.05f)]
        [SerializeField] private float m_ControllerScanInterval = 0.25f;

        private readonly Dictionary<uint, NetworkStatsController> m_RegisteredControllers = new(32);
        private readonly List<uint> m_RemoveBuffer = new(16);
        private NetworkStatsManager m_WiredManager;
        private bool m_ManagerInitialized;
        private bool m_LastServer;
        private float m_NextControllerScanTime;

        protected override ushort ModuleId => FusionModuleIds.Stats;

        public string GameplayReadinessName => "Stats";

        public bool IsGameplayReady(FusionNetworkIdentity identity)
        {
            if (!isActiveAndEnabled || identity == null || identity.NetworkId == 0 ||
                !identity.TransportAdmitted || TransportBridge == null ||
                !TransportBridge.IsClient)
            {
                return false;
            }

            WireManager();
            RefreshControllerRegistry(true);
            if (!m_ManagerInitialized || m_WiredManager == null ||
                m_WiredManager != GetManager())
            {
                return false;
            }

            NetworkStatsController relevant =
                identity.GetComponentInChildren<NetworkStatsController>(true);
            if (relevant == null) return true;

            return m_RegisteredControllers.TryGetValue(
                       identity.NetworkId, out NetworkStatsController registered) &&
                   registered == relevant && m_WiredManager.GetController(identity.NetworkId) == relevant;
        }

        protected override void OnModuleEnabled()
        {
            WireManager();
            RefreshControllerRegistry(true);
        }

        protected override void OnModuleStarted()
        {
            WireManager();
            RefreshControllerRegistry(true);
        }

        protected override void OnModuleUpdate()
        {
            WireManager();
            if (!m_AutoRegisterSceneControllers ||
                Time.unscaledTime < m_NextControllerScanTime) return;
            m_NextControllerScanTime =
                Time.unscaledTime + Mathf.Max(0.05f, m_ControllerScanInterval);
            RefreshControllerRegistry(false);
        }

        protected override void OnModuleDisabled()
        {
            UnwireManager();
            UnregisterAllControllers();
        }

        protected override void OnAuthorityChanged(bool isAuthority, uint authorityEpoch)
        {
            m_ManagerInitialized = false;
            WireManager();
            RefreshControllerRegistry(true);
        }

        public override string FullSnapshotProducerName => "Stats";

        protected override FusionFullSnapshotResult ProduceFullSnapshotForClient(
            FusionFullSnapshotContext context)
        {
            WireManager();
            RefreshControllerRegistry(true);
            NetworkStatsManager manager = GetManager();
            if (manager == null || manager != m_WiredManager || !m_ManagerInitialized)
            {
                return context.Fail("NetworkStatsManager is unavailable or not initialized.");
            }

            manager.SendInitialState(context.ClientId);
            return context.Complete();
        }

        protected override void HandleModuleMessage(FusionModuleMessage message)
        {
            NetworkStatsManager manager = GetManager();
            if (manager == null) return;
            bool request = TransportBridge != null &&
                           TransportBridge.IsServer &&
                           !message.FromAuthority;
            bool authority = TransportBridge != null &&
                             TransportBridge.IsClient &&
                             message.FromAuthority;

            switch ((MessageType)message.MessageType)
            {
                case MessageType.StatModifyRequest:
                    if (request && TryRead(message, out NetworkStatModifyRequest statRequest))
                        manager.ReceiveStatModifyRequest(statRequest, message.SenderClientId);
                    break;
                case MessageType.StatModifyResponse:
                    if (authority && TryRead(message, out NetworkStatModifyResponse statResponse))
                        manager.ReceiveStatModifyResponse(statResponse, statResponse.ActorNetworkId);
                    break;
                case MessageType.AttributeModifyRequest:
                    if (request &&
                        TryRead(message, out NetworkAttributeModifyRequest attributeRequest))
                        manager.ReceiveAttributeModifyRequest(
                            attributeRequest, message.SenderClientId);
                    break;
                case MessageType.AttributeModifyResponse:
                    if (authority &&
                        TryRead(message, out NetworkAttributeModifyResponse attributeResponse))
                        manager.ReceiveAttributeModifyResponse(
                            attributeResponse, attributeResponse.ActorNetworkId);
                    break;
                case MessageType.StatusEffectRequest:
                    if (request &&
                        TryRead(message, out NetworkStatusEffectRequest statusEffectRequest))
                        manager.ReceiveStatusEffectRequest(
                            statusEffectRequest, message.SenderClientId);
                    break;
                case MessageType.StatusEffectResponse:
                    if (authority &&
                        TryRead(message, out NetworkStatusEffectResponse statusEffectResponse))
                        manager.ReceiveStatusEffectResponse(
                            statusEffectResponse, statusEffectResponse.ActorNetworkId);
                    break;
                case MessageType.StatModifierRequest:
                    if (request &&
                        TryRead(message, out NetworkStatModifierRequest modifierRequest))
                        manager.ReceiveStatModifierRequest(modifierRequest, message.SenderClientId);
                    break;
                case MessageType.StatModifierResponse:
                    if (authority &&
                        TryRead(message, out NetworkStatModifierResponse modifierResponse))
                        manager.ReceiveStatModifierResponse(
                            modifierResponse, modifierResponse.ActorNetworkId);
                    break;
                case MessageType.ClearStatusEffectsRequest:
                    if (request &&
                        TryRead(message, out NetworkClearStatusEffectsRequest clearRequest))
                        manager.ReceiveClearStatusEffectsRequest(clearRequest, message.SenderClientId);
                    break;
                case MessageType.ClearStatusEffectsResponse:
                    if (authority &&
                        TryRead(message, out NetworkClearStatusEffectsResponse clearResponse))
                        manager.ReceiveClearStatusEffectsResponse(
                            clearResponse, clearResponse.ActorNetworkId);
                    break;
                case MessageType.StatChangeBroadcast:
                    if (authority &&
                        TryRead(message, out NetworkStatChangeBroadcast statBroadcast))
                        manager.ReceiveStatChangeBroadcast(statBroadcast);
                    break;
                case MessageType.AttributeChangeBroadcast:
                    if (authority &&
                        TryRead(message, out NetworkAttributeChangeBroadcast attributeBroadcast))
                        manager.ReceiveAttributeChangeBroadcast(attributeBroadcast);
                    break;
                case MessageType.StatusEffectBroadcast:
                    if (authority &&
                        TryRead(message, out NetworkStatusEffectBroadcast statusBroadcast))
                        manager.ReceiveStatusEffectBroadcast(statusBroadcast);
                    break;
                case MessageType.StatModifierBroadcast:
                    if (authority &&
                        TryRead(message, out NetworkStatModifierBroadcast modifierBroadcast))
                        manager.ReceiveStatModifierBroadcast(modifierBroadcast);
                    break;
                case MessageType.Snapshot:
                    if (authority && TryRead(message, out NetworkStatsSnapshot snapshot))
                        manager.ReceiveFullSnapshot(snapshot);
                    break;
                case MessageType.Delta:
                    if (authority && TryRead(message, out NetworkStatsDelta delta))
                        manager.ReceiveDelta(delta);
                    break;
            }
        }

        private void WireManager()
        {
            NetworkStatsManager manager = GetManager();
            if (manager == null) return;
            if (m_WiredManager != null && m_WiredManager != manager) UnwireManager();
            m_WiredManager = manager;

            manager.OnSendStatModifyRequest -= SendStatRequest;
            manager.OnSendStatModifyRequest += SendStatRequest;
            manager.OnSendAttributeModifyRequest -= SendAttributeRequest;
            manager.OnSendAttributeModifyRequest += SendAttributeRequest;
            manager.OnSendStatusEffectRequest -= SendStatusRequest;
            manager.OnSendStatusEffectRequest += SendStatusRequest;
            manager.OnSendStatModifierRequest -= SendModifierRequest;
            manager.OnSendStatModifierRequest += SendModifierRequest;
            manager.OnSendClearStatusEffectsRequest -= SendClearRequest;
            manager.OnSendClearStatusEffectsRequest += SendClearRequest;
            manager.OnSendStatModifyResponse -= SendStatResponse;
            manager.OnSendStatModifyResponse += SendStatResponse;
            manager.OnSendAttributeModifyResponse -= SendAttributeResponse;
            manager.OnSendAttributeModifyResponse += SendAttributeResponse;
            manager.OnSendStatusEffectResponse -= SendStatusResponse;
            manager.OnSendStatusEffectResponse += SendStatusResponse;
            manager.OnSendStatModifierResponse -= SendModifierResponse;
            manager.OnSendStatModifierResponse += SendModifierResponse;
            manager.OnSendClearStatusEffectsResponse -= SendClearResponse;
            manager.OnSendClearStatusEffectsResponse += SendClearResponse;
            manager.OnBroadcastStatChange -= BroadcastStat;
            manager.OnBroadcastStatChange += BroadcastStat;
            manager.OnBroadcastAttributeChange -= BroadcastAttribute;
            manager.OnBroadcastAttributeChange += BroadcastAttribute;
            manager.OnBroadcastStatusEffectChange -= BroadcastStatus;
            manager.OnBroadcastStatusEffectChange += BroadcastStatus;
            manager.OnBroadcastStatModifierChange -= BroadcastModifier;
            manager.OnBroadcastStatModifierChange += BroadcastModifier;
            manager.OnBroadcastFullSnapshot -= BroadcastSnapshot;
            manager.OnBroadcastFullSnapshot += BroadcastSnapshot;
            manager.OnBroadcastDelta -= BroadcastDelta;
            manager.OnBroadcastDelta += BroadcastDelta;
            manager.OnSendSnapshotToClient -= SendSnapshot;
            manager.OnSendSnapshotToClient += SendSnapshot;

            bool isServer = TransportBridge != null && TransportBridge.IsServer;
            if (!m_ManagerInitialized || isServer != m_LastServer)
            {
                manager.IsServer = isServer;
                m_ManagerInitialized = true;
                m_LastServer = isServer;
            }
        }

        private void UnwireManager()
        {
            NetworkStatsManager manager = m_WiredManager;
            if (manager == null) return;
            manager.OnSendStatModifyRequest -= SendStatRequest;
            manager.OnSendAttributeModifyRequest -= SendAttributeRequest;
            manager.OnSendStatusEffectRequest -= SendStatusRequest;
            manager.OnSendStatModifierRequest -= SendModifierRequest;
            manager.OnSendClearStatusEffectsRequest -= SendClearRequest;
            manager.OnSendStatModifyResponse -= SendStatResponse;
            manager.OnSendAttributeModifyResponse -= SendAttributeResponse;
            manager.OnSendStatusEffectResponse -= SendStatusResponse;
            manager.OnSendStatModifierResponse -= SendModifierResponse;
            manager.OnSendClearStatusEffectsResponse -= SendClearResponse;
            manager.OnBroadcastStatChange -= BroadcastStat;
            manager.OnBroadcastAttributeChange -= BroadcastAttribute;
            manager.OnBroadcastStatusEffectChange -= BroadcastStatus;
            manager.OnBroadcastStatModifierChange -= BroadcastModifier;
            manager.OnBroadcastFullSnapshot -= BroadcastSnapshot;
            manager.OnBroadcastDelta -= BroadcastDelta;
            manager.OnSendSnapshotToClient -= SendSnapshot;
            m_WiredManager = null;
            m_ManagerInitialized = false;
        }

        private void RefreshControllerRegistry(bool force)
        {
            NetworkStatsManager manager = GetManager();
            if (manager == null) return;
            PruneControllerRegistry(manager);
            if (!m_AutoRegisterSceneControllers && !force) return;

            NetworkStatsController[] controllers = FindObjectsByType<NetworkStatsController>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            for (int i = 0; i < controllers.Length; i++)
                RegisterController(manager, controllers[i]);
        }

        private void RegisterController(
            NetworkStatsManager manager, NetworkStatsController controller)
        {
            if (manager == null || controller == null) return;
            NetworkCharacter character = controller.GetComponent<NetworkCharacter>();
            if (character == null || character.NetworkId == 0 ||
                character.Role == NetworkCharacter.NetworkRole.None) return;

            uint networkId = character.NetworkId;
            bool isServer = character.IsServerInstance;
            bool isLocalClient =
                character.IsOwnerInstance &&
                character.Role == NetworkCharacter.NetworkRole.LocalClient;

            if (m_RegisteredControllers.TryGetValue(
                    networkId, out NetworkStatsController existing))
            {
                if (existing == controller)
                {
                    if (controller.IsServer != isServer ||
                        controller.IsLocalClient != isLocalClient)
                        controller.Initialize(isServer, isLocalClient);
                    return;
                }
                manager.UnregisterController(networkId);
            }

            controller.Initialize(isServer, isLocalClient);
            m_RegisteredControllers[networkId] = controller;
            manager.RegisterController(networkId, controller);
            if (manager.IsServer) manager.BroadcastFullSnapshot(controller.GetFullSnapshot());
        }

        private void PruneControllerRegistry(NetworkStatsManager manager)
        {
            m_RemoveBuffer.Clear();
            foreach (KeyValuePair<uint, NetworkStatsController> pair in m_RegisteredControllers)
            {
                NetworkStatsController controller = pair.Value;
                NetworkCharacter character =
                    controller != null ? controller.GetComponent<NetworkCharacter>() : null;
                if (controller == null || character == null ||
                    character.NetworkId != pair.Key ||
                    character.Role == NetworkCharacter.NetworkRole.None)
                    m_RemoveBuffer.Add(pair.Key);
            }

            for (int i = 0; i < m_RemoveBuffer.Count; i++)
            {
                manager.UnregisterController(m_RemoveBuffer[i]);
                m_RegisteredControllers.Remove(m_RemoveBuffer[i]);
            }
        }

        private void UnregisterAllControllers()
        {
            NetworkStatsManager manager = GetManager();
            if (manager != null)
            {
                foreach (uint id in m_RegisteredControllers.Keys)
                    manager.UnregisterController(id);
            }
            m_RegisteredControllers.Clear();
        }

        private void SendStatRequest(NetworkStatModifyRequest value) =>
            SendToAuthority((ushort)MessageType.StatModifyRequest, value);
        private void SendAttributeRequest(NetworkAttributeModifyRequest value) =>
            SendToAuthority((ushort)MessageType.AttributeModifyRequest, value);
        private void SendStatusRequest(NetworkStatusEffectRequest value) =>
            SendToAuthority((ushort)MessageType.StatusEffectRequest, value);
        private void SendModifierRequest(NetworkStatModifierRequest value) =>
            SendToAuthority((ushort)MessageType.StatModifierRequest, value);
        private void SendClearRequest(NetworkClearStatusEffectsRequest value) =>
            SendToAuthority((ushort)MessageType.ClearStatusEffectsRequest, value);
        private void SendStatResponse(uint id, NetworkStatModifyResponse value) =>
            SendToClient(id, (ushort)MessageType.StatModifyResponse, value);
        private void SendAttributeResponse(uint id, NetworkAttributeModifyResponse value) =>
            SendToClient(id, (ushort)MessageType.AttributeModifyResponse, value);
        private void SendStatusResponse(uint id, NetworkStatusEffectResponse value) =>
            SendToClient(id, (ushort)MessageType.StatusEffectResponse, value);
        private void SendModifierResponse(uint id, NetworkStatModifierResponse value) =>
            SendToClient(id, (ushort)MessageType.StatModifierResponse, value);
        private void SendClearResponse(uint id, NetworkClearStatusEffectsResponse value) =>
            SendToClient(id, (ushort)MessageType.ClearStatusEffectsResponse, value);
        private void BroadcastStat(NetworkStatChangeBroadcast value) =>
            Broadcast((ushort)MessageType.StatChangeBroadcast, value);
        private void BroadcastAttribute(NetworkAttributeChangeBroadcast value) =>
            Broadcast((ushort)MessageType.AttributeChangeBroadcast, value);
        private void BroadcastStatus(NetworkStatusEffectBroadcast value) =>
            Broadcast((ushort)MessageType.StatusEffectBroadcast, value);
        private void BroadcastModifier(NetworkStatModifierBroadcast value) =>
            Broadcast((ushort)MessageType.StatModifierBroadcast, value);
        private void BroadcastSnapshot(NetworkStatsSnapshot value) =>
            Broadcast((ushort)MessageType.Snapshot, value);
        private void BroadcastDelta(NetworkStatsDelta value) =>
            Broadcast((ushort)MessageType.Delta, value);

        private void SendSnapshot(ulong rawClientId, NetworkStatsSnapshot value)
        {
            if (!NetworkTransportBridge.TryConvertSenderClientId(rawClientId, out uint id)) return;
            SendToClient(id, (ushort)MessageType.Snapshot, value);
        }

        private static NetworkStatsManager GetManager()
        {
            return NetworkStatsManager.Instance != null
                ? NetworkStatsManager.Instance
                : FindFirstObjectByType<NetworkStatsManager>(FindObjectsInactive.Include);
        }
    }
}
#endif
