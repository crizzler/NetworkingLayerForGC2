using System.Collections.Generic;
using GameCreator.Runtime.Characters;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Transport.Fusion
{
    /// <summary>
    /// Fusion routing for the transport-independent GC2 Core manager.
    /// All requests are evaluated by the Host or Shared master before state is published.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("Game Creator/Network/Transport/Fusion Core Bridge")]
    [DefaultExecutionOrder(-390)]
    public sealed class FusionCoreTransportBridge : FusionModuleTransportBridgeBase,
        IFusionGameplayReadinessParticipant
    {
        private enum MessageType : ushort
        {
            RagdollRequest = 1,
            RagdollResponse = 2,
            RagdollBroadcast = 3,
            PropRequest = 4,
            PropResponse = 5,
            PropBroadcast = 6,
            InvincibilityRequest = 7,
            InvincibilityResponse = 8,
            InvincibilityBroadcast = 9,
            PoiseRequest = 10,
            PoiseResponse = 11,
            PoiseBroadcast = 12,
            BusyRequest = 13,
            BusyResponse = 14,
            BusyBroadcast = 15,
            InteractionRequest = 16,
            InteractionResponse = 17,
            InteractionBroadcast = 18,
            InteractionFocus = 19,
            Snapshot = 20
        }

        private NetworkCoreManager m_WiredManager;
        private bool m_ManagerInitialized;
        private bool m_LastServer;
        private bool m_LastClient;

        protected override ushort ModuleId => FusionModuleIds.Core;

        public string GameplayReadinessName => "Core";

        public bool IsGameplayReady(FusionNetworkIdentity identity)
        {
            if (!isActiveAndEnabled || identity == null || identity.NetworkId == 0 ||
                !identity.TransportAdmitted || TransportBridge == null ||
                !TransportBridge.IsClient)
            {
                return false;
            }

            WireCoreManager();
            if (!m_ManagerInitialized || m_WiredManager == null ||
                m_WiredManager != GetCoreManager())
            {
                return false;
            }

            Character character = TransportBridge.ResolveCharacter(identity.NetworkId);
            NetworkCharacter networkCharacter = character != null
                ? character.GetComponent<NetworkCharacter>()
                : null;
            return networkCharacter != null &&
                   networkCharacter.NetworkId == identity.NetworkId &&
                   networkCharacter.Role != NetworkCharacter.NetworkRole.None;
        }

        protected override void OnModuleEnabled()
        {
            WireCoreManager();
        }

        protected override void OnModuleStarted()
        {
            WireCoreManager();
        }

        protected override void OnModuleUpdate()
        {
            WireCoreManager();
        }

        protected override void OnModuleDisabled()
        {
            UnwireCoreManager();
        }

        protected override void OnAuthorityChanged(bool isAuthority, uint authorityEpoch)
        {
            m_ManagerInitialized = false;
            WireCoreManager();
        }

        public override string FullSnapshotProducerName => "Core";

        protected override FusionFullSnapshotResult ProduceFullSnapshotForClient(
            FusionFullSnapshotContext context)
        {
            WireCoreManager();
            NetworkCoreManager manager = GetCoreManager();
            if (manager == null || manager != m_WiredManager || !m_ManagerInitialized)
            {
                return context.Fail("NetworkCoreManager is unavailable or not initialized.");
            }

            NetworkCharacter[] characters = FindObjectsByType<NetworkCharacter>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            var sent = new HashSet<uint>();
            for (int i = 0; i < characters.Length; i++)
            {
                uint networkId = characters[i].NetworkId;
                if (networkId == 0 || !sent.Add(networkId)) continue;
                manager.SendSnapshotToClient(context.ClientId, networkId);
            }

            if (sent.Count == 0)
                return context.Fail("No authoritative NetworkCharacter was available to snapshot.");
            if (context.PacketsEnqueued != sent.Count)
                return context.Fail(
                    $"Expected {sent.Count} core snapshots but enqueued {context.PacketsEnqueued}.");
            return context.Complete();
        }

        protected override void HandleModuleMessage(FusionModuleMessage message)
        {
            NetworkCoreManager manager = GetCoreManager();
            if (manager == null) return;

            switch ((MessageType)message.MessageType)
            {
                case MessageType.RagdollRequest:
                    if (AcceptRequest(message) &&
                        TryRead(message, out NetworkRagdollRequest ragdollRequest))
                        manager.ReceiveRagdollRequest(message.SenderClientId, ragdollRequest);
                    break;
                case MessageType.RagdollResponse:
                    if (AcceptAuthority(message) &&
                        TryRead(message, out NetworkRagdollResponse ragdollResponse))
                        manager.ReceiveRagdollResponse(ragdollResponse);
                    break;
                case MessageType.RagdollBroadcast:
                    if (AcceptAuthority(message) &&
                        TryRead(message, out NetworkRagdollBroadcast ragdollBroadcast))
                        manager.ReceiveRagdollBroadcast(ragdollBroadcast);
                    break;
                case MessageType.PropRequest:
                    if (AcceptRequest(message) && TryRead(message, out NetworkPropRequest propRequest))
                        manager.ReceivePropRequest(message.SenderClientId, propRequest);
                    break;
                case MessageType.PropResponse:
                    if (AcceptAuthority(message) &&
                        TryRead(message, out NetworkPropResponse propResponse))
                        manager.ReceivePropResponse(propResponse);
                    break;
                case MessageType.PropBroadcast:
                    if (AcceptAuthority(message) &&
                        TryRead(message, out NetworkPropBroadcast propBroadcast))
                        manager.ReceivePropBroadcast(propBroadcast);
                    break;
                case MessageType.InvincibilityRequest:
                    if (AcceptRequest(message) &&
                        TryRead(message, out NetworkInvincibilityRequest invincibilityRequest))
                        manager.ReceiveInvincibilityRequest(message.SenderClientId, invincibilityRequest);
                    break;
                case MessageType.InvincibilityResponse:
                    if (AcceptAuthority(message) &&
                        TryRead(message, out NetworkInvincibilityResponse invincibilityResponse))
                        manager.ReceiveInvincibilityResponse(invincibilityResponse);
                    break;
                case MessageType.InvincibilityBroadcast:
                    if (AcceptAuthority(message) &&
                        TryRead(message, out NetworkInvincibilityBroadcast invincibilityBroadcast))
                        manager.ReceiveInvincibilityBroadcast(invincibilityBroadcast);
                    break;
                case MessageType.PoiseRequest:
                    if (AcceptRequest(message) &&
                        TryRead(message, out NetworkPoiseRequest poiseRequest))
                        manager.ReceivePoiseRequest(message.SenderClientId, poiseRequest);
                    break;
                case MessageType.PoiseResponse:
                    if (AcceptAuthority(message) &&
                        TryRead(message, out NetworkPoiseResponse poiseResponse))
                        manager.ReceivePoiseResponse(poiseResponse);
                    break;
                case MessageType.PoiseBroadcast:
                    if (AcceptAuthority(message) &&
                        TryRead(message, out NetworkPoiseBroadcast poiseBroadcast))
                        manager.ReceivePoiseBroadcast(poiseBroadcast);
                    break;
                case MessageType.BusyRequest:
                    if (AcceptRequest(message) && TryRead(message, out NetworkBusyRequest busyRequest))
                        manager.ReceiveBusyRequest(message.SenderClientId, busyRequest);
                    break;
                case MessageType.BusyResponse:
                    if (AcceptAuthority(message) &&
                        TryRead(message, out NetworkBusyResponse busyResponse))
                        manager.ReceiveBusyResponse(busyResponse);
                    break;
                case MessageType.BusyBroadcast:
                    if (AcceptAuthority(message) &&
                        TryRead(message, out NetworkBusyBroadcast busyBroadcast))
                        manager.ReceiveBusyBroadcast(busyBroadcast);
                    break;
                case MessageType.InteractionRequest:
                    if (AcceptRequest(message) &&
                        TryRead(message, out NetworkInteractionRequest interactionRequest))
                        manager.ReceiveInteractionRequest(message.SenderClientId, interactionRequest);
                    break;
                case MessageType.InteractionResponse:
                    if (AcceptAuthority(message) &&
                        TryRead(message, out NetworkInteractionResponse interactionResponse))
                        manager.ReceiveInteractionResponse(interactionResponse);
                    break;
                case MessageType.InteractionBroadcast:
                    if (AcceptAuthority(message) &&
                        TryRead(message, out NetworkInteractionBroadcast interactionBroadcast))
                        manager.ReceiveInteractionBroadcast(interactionBroadcast);
                    break;
                case MessageType.InteractionFocus:
                    if (AcceptAuthority(message) &&
                        TryRead(message, out NetworkInteractionFocusBroadcast interactionFocus))
                        manager.ReceiveInteractionFocusBroadcast(interactionFocus);
                    break;
                case MessageType.Snapshot:
                    if (AcceptAuthority(message) &&
                        TryRead(message, out NetworkCoreSnapshot snapshot))
                        manager.ReceiveCoreSnapshot(snapshot);
                    break;
            }
        }

        private bool AcceptRequest(FusionModuleMessage message)
        {
            return TransportBridge != null && TransportBridge.IsServer && !message.FromAuthority;
        }

        private bool AcceptAuthority(FusionModuleMessage message)
        {
            return TransportBridge != null && TransportBridge.IsClient && message.FromAuthority;
        }

        private void WireCoreManager()
        {
            NetworkCoreManager manager = GetCoreManager();
            if (manager == null) return;
            if (m_WiredManager != null && m_WiredManager != manager) UnwireCoreManager();
            m_WiredManager = manager;

            manager.SendRagdollRequestToServer = SendRagdollRequest;
            manager.SendRagdollResponseToClient = SendRagdollResponse;
            manager.BroadcastRagdoll = BroadcastRagdoll;
            manager.SendPropRequestToServer = SendPropRequest;
            manager.SendPropResponseToClient = SendPropResponse;
            manager.BroadcastProp = BroadcastProp;
            manager.SendInvincibilityRequestToServer = SendInvincibilityRequest;
            manager.SendInvincibilityResponseToClient = SendInvincibilityResponse;
            manager.BroadcastInvincibility = BroadcastInvincibility;
            manager.SendPoiseRequestToServer = SendPoiseRequest;
            manager.SendPoiseResponseToClient = SendPoiseResponse;
            manager.BroadcastPoise = BroadcastPoise;
            manager.SendBusyRequestToServer = SendBusyRequest;
            manager.SendBusyResponseToClient = SendBusyResponse;
            manager.BroadcastBusy = BroadcastBusy;
            manager.SendInteractionRequestToServer = SendInteractionRequest;
            manager.SendInteractionResponseToClient = SendInteractionResponse;
            manager.BroadcastInteraction = BroadcastInteraction;
            manager.BroadcastInteractionFocus = BroadcastInteractionFocus;
            manager.SendCoreSnapshotToClient = SendCoreSnapshot;
            manager.GetServerTime = GetServerTime;
            manager.GetCharacterByNetworkId = ResolveCharacter;
            manager.GetLocalPlayerNetworkId = ResolveLocalCharacterId;
            manager.GetNetworkIdForGameObject = ResolveNetworkObjectId;

            bool isServer = TransportBridge != null && TransportBridge.IsServer;
            bool isClient = TransportBridge != null && TransportBridge.IsClient;
            if (!m_ManagerInitialized || m_LastServer != isServer || m_LastClient != isClient)
            {
                manager.Initialize(isServer, isClient);
                m_ManagerInitialized = true;
                m_LastServer = isServer;
                m_LastClient = isClient;
            }
        }

        private void UnwireCoreManager()
        {
            NetworkCoreManager manager = m_WiredManager;
            if (manager == null) return;
            if (manager.SendRagdollRequestToServer == SendRagdollRequest)
                manager.SendRagdollRequestToServer = null;
            if (manager.SendRagdollResponseToClient == SendRagdollResponse)
                manager.SendRagdollResponseToClient = null;
            if (manager.BroadcastRagdoll == BroadcastRagdoll) manager.BroadcastRagdoll = null;
            if (manager.SendPropRequestToServer == SendPropRequest)
                manager.SendPropRequestToServer = null;
            if (manager.SendPropResponseToClient == SendPropResponse)
                manager.SendPropResponseToClient = null;
            if (manager.BroadcastProp == BroadcastProp) manager.BroadcastProp = null;
            if (manager.SendInvincibilityRequestToServer == SendInvincibilityRequest)
                manager.SendInvincibilityRequestToServer = null;
            if (manager.SendInvincibilityResponseToClient == SendInvincibilityResponse)
                manager.SendInvincibilityResponseToClient = null;
            if (manager.BroadcastInvincibility == BroadcastInvincibility)
                manager.BroadcastInvincibility = null;
            if (manager.SendPoiseRequestToServer == SendPoiseRequest)
                manager.SendPoiseRequestToServer = null;
            if (manager.SendPoiseResponseToClient == SendPoiseResponse)
                manager.SendPoiseResponseToClient = null;
            if (manager.BroadcastPoise == BroadcastPoise) manager.BroadcastPoise = null;
            if (manager.SendBusyRequestToServer == SendBusyRequest)
                manager.SendBusyRequestToServer = null;
            if (manager.SendBusyResponseToClient == SendBusyResponse)
                manager.SendBusyResponseToClient = null;
            if (manager.BroadcastBusy == BroadcastBusy) manager.BroadcastBusy = null;
            if (manager.SendInteractionRequestToServer == SendInteractionRequest)
                manager.SendInteractionRequestToServer = null;
            if (manager.SendInteractionResponseToClient == SendInteractionResponse)
                manager.SendInteractionResponseToClient = null;
            if (manager.BroadcastInteraction == BroadcastInteraction)
                manager.BroadcastInteraction = null;
            if (manager.BroadcastInteractionFocus == BroadcastInteractionFocus)
                manager.BroadcastInteractionFocus = null;
            if (manager.SendCoreSnapshotToClient == SendCoreSnapshot)
                manager.SendCoreSnapshotToClient = null;
            if (manager.GetServerTime == GetServerTime) manager.GetServerTime = null;
            if (manager.GetCharacterByNetworkId == ResolveCharacter)
                manager.GetCharacterByNetworkId = null;
            if (manager.GetLocalPlayerNetworkId == ResolveLocalCharacterId)
                manager.GetLocalPlayerNetworkId = null;
            if (manager.GetNetworkIdForGameObject == ResolveNetworkObjectId)
                manager.GetNetworkIdForGameObject = null;
            m_WiredManager = null;
            m_ManagerInitialized = false;
        }

        private void SendRagdollRequest(NetworkRagdollRequest value) =>
            SendToAuthority((ushort)MessageType.RagdollRequest, value);
        private void SendRagdollResponse(uint clientId, NetworkRagdollResponse value) =>
            SendToClient(clientId, (ushort)MessageType.RagdollResponse, value);
        private void BroadcastRagdoll(NetworkRagdollBroadcast value) =>
            Broadcast((ushort)MessageType.RagdollBroadcast, value);
        private void SendPropRequest(NetworkPropRequest value) =>
            SendToAuthority((ushort)MessageType.PropRequest, value);
        private void SendPropResponse(uint clientId, NetworkPropResponse value) =>
            SendToClient(clientId, (ushort)MessageType.PropResponse, value);
        private void BroadcastProp(NetworkPropBroadcast value) =>
            Broadcast((ushort)MessageType.PropBroadcast, value);
        private void SendInvincibilityRequest(NetworkInvincibilityRequest value) =>
            SendToAuthority((ushort)MessageType.InvincibilityRequest, value);
        private void SendInvincibilityResponse(uint clientId, NetworkInvincibilityResponse value) =>
            SendToClient(clientId, (ushort)MessageType.InvincibilityResponse, value);
        private void BroadcastInvincibility(NetworkInvincibilityBroadcast value) =>
            Broadcast((ushort)MessageType.InvincibilityBroadcast, value);
        private void SendPoiseRequest(NetworkPoiseRequest value) =>
            SendToAuthority((ushort)MessageType.PoiseRequest, value);
        private void SendPoiseResponse(uint clientId, NetworkPoiseResponse value) =>
            SendToClient(clientId, (ushort)MessageType.PoiseResponse, value);
        private void BroadcastPoise(NetworkPoiseBroadcast value) =>
            Broadcast((ushort)MessageType.PoiseBroadcast, value);
        private void SendBusyRequest(NetworkBusyRequest value) =>
            SendToAuthority((ushort)MessageType.BusyRequest, value);
        private void SendBusyResponse(uint clientId, NetworkBusyResponse value) =>
            SendToClient(clientId, (ushort)MessageType.BusyResponse, value);
        private void BroadcastBusy(NetworkBusyBroadcast value) =>
            Broadcast((ushort)MessageType.BusyBroadcast, value);
        private void SendInteractionRequest(NetworkInteractionRequest value) =>
            SendToAuthority((ushort)MessageType.InteractionRequest, value);
        private void SendInteractionResponse(uint clientId, NetworkInteractionResponse value) =>
            SendToClient(clientId, (ushort)MessageType.InteractionResponse, value);
        private void BroadcastInteraction(NetworkInteractionBroadcast value) =>
            Broadcast((ushort)MessageType.InteractionBroadcast, value);
        private void BroadcastInteractionFocus(NetworkInteractionFocusBroadcast value) =>
            Broadcast((ushort)MessageType.InteractionFocus, value);
        private void SendCoreSnapshot(uint clientId, NetworkCoreSnapshot value) =>
            SendToClient(clientId, (ushort)MessageType.Snapshot, value);

        private float GetServerTime()
        {
            return TransportBridge != null ? TransportBridge.ServerTime : Time.time;
        }

        private Character ResolveCharacter(uint networkId)
        {
            return TransportBridge != null ? TransportBridge.ResolveCharacter(networkId) : null;
        }

        private uint ResolveLocalCharacterId()
        {
            FusionTransportBridge bridge = TransportBridge;
            if (bridge == null || !bridge.TryGetLocalClientId(out uint clientId)) return 0;
            return bridge.TryGetRepresentativeCharacterId(clientId, out uint networkId)
                ? networkId
                : 0;
        }

        private static uint ResolveNetworkObjectId(GameObject gameObject)
        {
            if (gameObject == null) return 0;
            FusionNetworkIdentity identity = gameObject.GetComponentInParent<FusionNetworkIdentity>();
            return identity != null ? identity.NetworkId : 0;
        }

        private static NetworkCoreManager GetCoreManager()
        {
            return NetworkCoreManager.Instance != null
                ? NetworkCoreManager.Instance
                : FindFirstObjectByType<NetworkCoreManager>(FindObjectsInactive.Include);
        }
    }
}
