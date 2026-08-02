#if GC2_INVENTORY
using System.Collections.Generic;
using Arawn.GameCreator2.Networking.Transport.Fusion;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Inventory;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Inventory.Transport.Fusion
{
    [DisallowMultipleComponent]
    [AddComponentMenu("Game Creator/Network/Transport/Fusion Inventory Bridge")]
    [DefaultExecutionOrder(-338)]
    public sealed class FusionInventoryTransportBridge : FusionModuleTransportBridgeBase,
        IFusionGameplayReadinessParticipant
    {
        private enum MessageType : ushort
        {
            ContentAddRequest = 1,
            ContentAddResponse = 2,
            ContentRemoveRequest = 3,
            ContentRemoveResponse = 4,
            ContentMoveRequest = 5,
            ContentMoveResponse = 6,
            ContentUseRequest = 7,
            ContentUseResponse = 8,
            ContentDropRequest = 9,
            ContentDropResponse = 10,
            EquipmentRequest = 11,
            EquipmentResponse = 12,
            SocketRequest = 13,
            SocketResponse = 14,
            WealthRequest = 15,
            WealthResponse = 16,
            MerchantRequest = 17,
            MerchantResponse = 18,
            CraftingRequest = 19,
            CraftingResponse = 20,
            TransferRequest = 21,
            TransferResponse = 22,
            PickupRequest = 23,
            PickupResponse = 24,
            CombineRequest = 25,
            CombineResponse = 26,
            ContentSplitRequest = 27,
            ContentSplitResponse = 28,
            ResyncRequest = 29,
            ItemAdded = 30,
            ItemRemoved = 31,
            ItemDropped = 32,
            DroppedItemRemoved = 33,
            ItemMoved = 34,
            ItemSplit = 35,
            ItemUsed = 36,
            ItemEquipped = 37,
            ItemUnequipped = 38,
            SocketChange = 39,
            WealthChange = 40,
            PropertyChange = 41,
            Snapshot = 42,
            Delta = 43,
            PickupState = 44,
            PickupStateSnapshot = 45
        }

        [Header("Controllers")]
        [SerializeField] private bool m_AutoRegisterSceneControllers = true;
        [SerializeField] private bool m_AutoAddControllersToBags = true;
        [Min(0.05f)]
        [SerializeField] private float m_ControllerScanInterval = 0.25f;

        [Header("Relevance")]
        [SerializeField] private bool m_UseSessionProfileRelevance = true;

        private readonly Dictionary<uint, NetworkInventoryController> m_RegisteredControllers =
            new(32);
        private readonly List<uint> m_RemoveBuffer = new(16);
        private readonly Dictionary<uint, FusionInventoryRuntimePickupIdentityAdapter>
            m_RegisteredRuntimePickups = new(16);
        private readonly Dictionary<uint, FusionInventoryRuntimePickupIdentityAdapter>
            m_RuntimePickupCandidates = new(16);
        private readonly HashSet<uint> m_DuplicateRuntimePickupIds = new();
        private readonly List<uint> m_RuntimePickupRemoveBuffer = new(8);
        private readonly HashSet<string> m_Warnings = new();

        private NetworkInventoryManager m_WiredManager;
        private bool m_ManagerInitialized;
        private bool m_LastServer;
        private float m_NextControllerScanTime;

        protected override ushort ModuleId => FusionModuleIds.Inventory;

        public string GameplayReadinessName => "Inventory";

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

            NetworkInventoryController relevant =
                identity.GetComponentInChildren<NetworkInventoryController>(true);
            if (relevant == null) return true;

            return m_RegisteredControllers.TryGetValue(
                       identity.NetworkId, out NetworkInventoryController registered) &&
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
            bool scanPickups = TransportBridge != null && TransportBridge.IsServer;
            if ((!m_AutoRegisterSceneControllers && !scanPickups) ||
                Time.unscaledTime < m_NextControllerScanTime) return;

            m_NextControllerScanTime =
                Time.unscaledTime + Mathf.Max(0.05f, m_ControllerScanInterval);
            RefreshControllerRegistry(false);
        }

        protected override void OnModuleDisabled()
        {
            UnwireManager();
            UnregisterAllControllers();
            UnregisterAllRuntimePickups();
            m_Warnings.Clear();
        }

        protected override void OnAuthorityChanged(bool isAuthority, uint authorityEpoch)
        {
            m_ManagerInitialized = false;
            WireManager();
            RefreshControllerRegistry(true);
            if (!isAuthority) UnregisterAllRuntimePickups();
        }

        public override string FullSnapshotProducerName => "Inventory";

        protected override FusionFullSnapshotResult ProduceFullSnapshotForClient(
            FusionFullSnapshotContext context)
        {
            WireManager();
            RefreshControllerRegistry(true);
            NetworkInventoryManager manager = GetManager();
            if (manager == null || manager != m_WiredManager || !m_ManagerInitialized)
            {
                return context.Fail("NetworkInventoryManager is unavailable or not initialized.");
            }

            manager.SendInitialState(context.ClientId);
            return context.Complete();
        }

        protected override void HandleModuleMessage(FusionModuleMessage message)
        {
            NetworkInventoryManager manager = GetManager();
            if (manager == null) return;
            bool request = TransportBridge != null &&
                           TransportBridge.IsServer &&
                           !message.FromAuthority;
            bool authority = TransportBridge != null &&
                             TransportBridge.IsClient &&
                             message.FromAuthority;

            switch ((MessageType)message.MessageType)
            {
                case MessageType.ContentAddRequest:
                    if (request && TryRead(message, out NetworkContentAddRequest addRequest))
                        manager.ReceiveContentAddRequest(addRequest, message.SenderClientId);
                    break;
                case MessageType.ContentAddResponse:
                    if (authority && TryRead(message, out NetworkContentAddResponse addResponse))
                        manager.ReceiveContentAddResponse(addResponse, addResponse.ActorNetworkId);
                    break;
                case MessageType.ContentRemoveRequest:
                    if (request && TryRead(message, out NetworkContentRemoveRequest removeRequest))
                        manager.ReceiveContentRemoveRequest(removeRequest, message.SenderClientId);
                    break;
                case MessageType.ContentRemoveResponse:
                    if (authority &&
                        TryRead(message, out NetworkContentRemoveResponse removeResponse))
                        manager.ReceiveContentRemoveResponse(
                            removeResponse, removeResponse.ActorNetworkId);
                    break;
                case MessageType.ContentMoveRequest:
                    if (request && TryRead(message, out NetworkContentMoveRequest moveRequest))
                        manager.ReceiveContentMoveRequest(moveRequest, message.SenderClientId);
                    break;
                case MessageType.ContentMoveResponse:
                    if (authority && TryRead(message, out NetworkContentMoveResponse moveResponse))
                        manager.ReceiveContentMoveResponse(moveResponse, moveResponse.ActorNetworkId);
                    break;
                case MessageType.ContentUseRequest:
                    if (request && TryRead(message, out NetworkContentUseRequest useRequest))
                        manager.ReceiveContentUseRequest(useRequest, message.SenderClientId);
                    break;
                case MessageType.ContentUseResponse:
                    if (authority && TryRead(message, out NetworkContentUseResponse useResponse))
                        manager.ReceiveContentUseResponse(useResponse, useResponse.ActorNetworkId);
                    break;
                case MessageType.ContentDropRequest:
                    if (request && TryRead(message, out NetworkContentDropRequest dropRequest))
                        manager.ReceiveContentDropRequest(dropRequest, message.SenderClientId);
                    break;
                case MessageType.ContentDropResponse:
                    if (authority && TryRead(message, out NetworkContentDropResponse dropResponse))
                        manager.ReceiveContentDropResponse(dropResponse, dropResponse.ActorNetworkId);
                    break;
                case MessageType.EquipmentRequest:
                    if (request && TryRead(message, out NetworkEquipmentRequest equipmentRequest))
                        _ = manager.ReceiveEquipmentRequest(
                            equipmentRequest, message.SenderClientId);
                    break;
                case MessageType.EquipmentResponse:
                    if (authority &&
                        TryRead(message, out NetworkEquipmentResponse equipmentResponse))
                        manager.ReceiveEquipmentResponse(
                            equipmentResponse, equipmentResponse.ActorNetworkId);
                    break;
                case MessageType.SocketRequest:
                    if (request && TryRead(message, out NetworkSocketRequest socketRequest))
                        manager.ReceiveSocketRequest(socketRequest, message.SenderClientId);
                    break;
                case MessageType.SocketResponse:
                    if (authority && TryRead(message, out NetworkSocketResponse socketResponse))
                        manager.ReceiveSocketResponse(socketResponse, socketResponse.ActorNetworkId);
                    break;
                case MessageType.WealthRequest:
                    if (request && TryRead(message, out NetworkWealthRequest wealthRequest))
                        manager.ReceiveWealthRequest(wealthRequest, message.SenderClientId);
                    break;
                case MessageType.WealthResponse:
                    if (authority && TryRead(message, out NetworkWealthResponse wealthResponse))
                        manager.ReceiveWealthResponse(wealthResponse, wealthResponse.ActorNetworkId);
                    break;
                case MessageType.MerchantRequest:
                    if (request && TryRead(message, out NetworkMerchantRequest merchantRequest))
                        manager.ReceiveMerchantRequest(merchantRequest, message.SenderClientId);
                    break;
                case MessageType.MerchantResponse:
                    if (authority &&
                        TryRead(message, out NetworkMerchantResponse merchantResponse))
                        manager.ReceiveMerchantResponse(
                            merchantResponse, merchantResponse.ActorNetworkId);
                    break;
                case MessageType.CraftingRequest:
                    if (request && TryRead(message, out NetworkCraftingRequest craftingRequest))
                        manager.ReceiveCraftingRequest(craftingRequest, message.SenderClientId);
                    break;
                case MessageType.CraftingResponse:
                    if (authority &&
                        TryRead(message, out NetworkCraftingResponse craftingResponse))
                        manager.ReceiveCraftingResponse(
                            craftingResponse, craftingResponse.ActorNetworkId);
                    break;
                case MessageType.TransferRequest:
                    if (request && TryRead(message, out NetworkTransferRequest transferRequest))
                        manager.ReceiveTransferRequest(transferRequest, message.SenderClientId);
                    break;
                case MessageType.TransferResponse:
                    if (authority &&
                        TryRead(message, out NetworkTransferResponse transferResponse))
                        manager.ReceiveTransferResponse(
                            transferResponse, transferResponse.ActorNetworkId);
                    break;
                case MessageType.PickupRequest:
                    if (request && TryRead(message, out NetworkPickupRequest pickupRequest))
                    {
                        RefreshRuntimePickupRegistry(manager);
                        manager.ReceivePickupRequest(pickupRequest, message.SenderClientId);
                    }
                    break;
                case MessageType.PickupResponse:
                    if (authority && TryRead(message, out NetworkPickupResponse pickupResponse))
                        manager.ReceivePickupResponse(pickupResponse, pickupResponse.ActorNetworkId);
                    break;
                case MessageType.CombineRequest:
                    if (request && TryRead(message, out NetworkCombineRequest combineRequest))
                        manager.ReceiveCombineRequest(combineRequest, message.SenderClientId);
                    break;
                case MessageType.CombineResponse:
                    if (authority && TryRead(message, out NetworkCombineResponse combineResponse))
                        manager.ReceiveCombineResponse(combineResponse, combineResponse.ActorNetworkId);
                    break;
                case MessageType.ContentSplitRequest:
                    if (request &&
                        TryRead(message, out NetworkContentSplitRequest splitRequest))
                        manager.ReceiveContentSplitRequest(splitRequest, message.SenderClientId);
                    break;
                case MessageType.ContentSplitResponse:
                    if (authority &&
                        TryRead(message, out NetworkContentSplitResponse splitResponse))
                        manager.ReceiveContentSplitResponse(
                            splitResponse, splitResponse.ActorNetworkId);
                    break;
                case MessageType.ResyncRequest:
                    if (request &&
                        TryRead(message, out NetworkInventoryResyncRequest resyncRequest))
                        manager.ReceiveResyncRequest(resyncRequest, message.SenderClientId);
                    break;
                case MessageType.ItemAdded:
                    if (authority && TryRead(message, out NetworkItemAddedBroadcast itemAdded))
                        manager.ReceiveItemAddedBroadcast(itemAdded);
                    break;
                case MessageType.ItemRemoved:
                    if (authority &&
                        TryRead(message, out NetworkItemRemovedBroadcast itemRemoved))
                        manager.ReceiveItemRemovedBroadcast(itemRemoved);
                    break;
                case MessageType.ItemDropped:
                    if (authority &&
                        TryRead(message, out NetworkItemDroppedBroadcast itemDropped))
                        manager.ReceiveItemDroppedBroadcast(itemDropped);
                    break;
                case MessageType.DroppedItemRemoved:
                    if (authority &&
                        TryRead(message, out NetworkDroppedItemRemovedBroadcast droppedRemoved))
                        manager.ReceiveDroppedItemRemovedBroadcast(droppedRemoved);
                    break;
                case MessageType.ItemMoved:
                    if (authority && TryRead(message, out NetworkItemMovedBroadcast itemMoved))
                        manager.ReceiveItemMovedBroadcast(itemMoved);
                    break;
                case MessageType.ItemSplit:
                    if (authority && TryRead(message, out NetworkItemSplitBroadcast itemSplit))
                        manager.ReceiveItemSplitBroadcast(itemSplit);
                    break;
                case MessageType.ItemUsed:
                    if (authority && TryRead(message, out NetworkItemUsedBroadcast itemUsed))
                        manager.ReceiveItemUsedBroadcast(itemUsed);
                    break;
                case MessageType.ItemEquipped:
                    if (authority &&
                        TryRead(message, out NetworkItemEquippedBroadcast itemEquipped))
                        manager.ReceiveItemEquippedBroadcast(itemEquipped);
                    break;
                case MessageType.ItemUnequipped:
                    if (authority &&
                        TryRead(message, out NetworkItemUnequippedBroadcast itemUnequipped))
                        manager.ReceiveItemUnequippedBroadcast(itemUnequipped);
                    break;
                case MessageType.SocketChange:
                    if (authority &&
                        TryRead(message, out NetworkSocketChangeBroadcast socketChange))
                        manager.ReceiveSocketChangeBroadcast(socketChange);
                    break;
                case MessageType.WealthChange:
                    if (authority &&
                        TryRead(message, out NetworkWealthChangeBroadcast wealthChange))
                        manager.ReceiveWealthChangeBroadcast(wealthChange);
                    break;
                case MessageType.PropertyChange:
                    if (authority &&
                        TryRead(message, out NetworkPropertyChangeBroadcast propertyChange))
                        manager.ReceivePropertyChangeBroadcast(propertyChange);
                    break;
                case MessageType.Snapshot:
                    if (authority && TryRead(message, out NetworkInventorySnapshot snapshot))
                        manager.ReceiveFullSnapshot(snapshot);
                    break;
                case MessageType.Delta:
                    if (authority && TryRead(message, out NetworkInventoryDelta delta))
                        manager.ReceiveDelta(delta);
                    break;
                case MessageType.PickupState:
                    if (authority &&
                        TryRead(message, out NetworkPickupStateBroadcast pickupState))
                        manager.ReceivePickupStateBroadcast(pickupState);
                    break;
                case MessageType.PickupStateSnapshot:
                    if (authority &&
                        TryRead(message, out NetworkPickupStateSnapshot pickupSnapshot))
                        manager.ReceivePickupStateSnapshot(pickupSnapshot);
                    break;
            }
        }

        private void WireManager()
        {
            NetworkInventoryManager manager = GetManager();
            if (manager == null) return;
            if (m_WiredManager != null && m_WiredManager != manager) UnwireManager();
            m_WiredManager = manager;

            manager.OnSendContentAddRequest -= SendContentAddRequest;
            manager.OnSendContentAddRequest += SendContentAddRequest;
            manager.OnSendContentRemoveRequest -= SendContentRemoveRequest;
            manager.OnSendContentRemoveRequest += SendContentRemoveRequest;
            manager.OnSendContentMoveRequest -= SendContentMoveRequest;
            manager.OnSendContentMoveRequest += SendContentMoveRequest;
            manager.OnSendContentUseRequest -= SendContentUseRequest;
            manager.OnSendContentUseRequest += SendContentUseRequest;
            manager.OnSendContentDropRequest -= SendContentDropRequest;
            manager.OnSendContentDropRequest += SendContentDropRequest;
            manager.OnSendEquipmentRequest -= SendEquipmentRequest;
            manager.OnSendEquipmentRequest += SendEquipmentRequest;
            manager.OnSendSocketRequest -= SendSocketRequest;
            manager.OnSendSocketRequest += SendSocketRequest;
            manager.OnSendWealthRequest -= SendWealthRequest;
            manager.OnSendWealthRequest += SendWealthRequest;
            manager.OnSendMerchantRequest -= SendMerchantRequest;
            manager.OnSendMerchantRequest += SendMerchantRequest;
            manager.OnSendCraftingRequest -= SendCraftingRequest;
            manager.OnSendCraftingRequest += SendCraftingRequest;
            manager.OnSendTransferRequest -= SendTransferRequest;
            manager.OnSendTransferRequest += SendTransferRequest;
            manager.OnSendPickupRequest -= SendPickupRequest;
            manager.OnSendPickupRequest += SendPickupRequest;
            manager.OnSendCombineRequest -= SendCombineRequest;
            manager.OnSendCombineRequest += SendCombineRequest;
            manager.OnSendContentSplitRequest -= SendContentSplitRequest;
            manager.OnSendContentSplitRequest += SendContentSplitRequest;
            manager.OnSendResyncRequest -= SendResyncRequest;
            manager.OnSendResyncRequest += SendResyncRequest;

            manager.OnSendContentAddResponse -= SendContentAddResponse;
            manager.OnSendContentAddResponse += SendContentAddResponse;
            manager.OnSendContentRemoveResponse -= SendContentRemoveResponse;
            manager.OnSendContentRemoveResponse += SendContentRemoveResponse;
            manager.OnSendContentMoveResponse -= SendContentMoveResponse;
            manager.OnSendContentMoveResponse += SendContentMoveResponse;
            manager.OnSendContentUseResponse -= SendContentUseResponse;
            manager.OnSendContentUseResponse += SendContentUseResponse;
            manager.OnSendContentDropResponse -= SendContentDropResponse;
            manager.OnSendContentDropResponse += SendContentDropResponse;
            manager.OnSendEquipmentResponse -= SendEquipmentResponse;
            manager.OnSendEquipmentResponse += SendEquipmentResponse;
            manager.OnSendSocketResponse -= SendSocketResponse;
            manager.OnSendSocketResponse += SendSocketResponse;
            manager.OnSendWealthResponse -= SendWealthResponse;
            manager.OnSendWealthResponse += SendWealthResponse;
            manager.OnSendMerchantResponse -= SendMerchantResponse;
            manager.OnSendMerchantResponse += SendMerchantResponse;
            manager.OnSendCraftingResponse -= SendCraftingResponse;
            manager.OnSendCraftingResponse += SendCraftingResponse;
            manager.OnSendTransferResponse -= SendTransferResponse;
            manager.OnSendTransferResponse += SendTransferResponse;
            manager.OnSendPickupResponse -= SendPickupResponse;
            manager.OnSendPickupResponse += SendPickupResponse;
            manager.OnSendCombineResponse -= SendCombineResponse;
            manager.OnSendCombineResponse += SendCombineResponse;
            manager.OnSendContentSplitResponse -= SendContentSplitResponse;
            manager.OnSendContentSplitResponse += SendContentSplitResponse;

            manager.OnBroadcastItemAdded -= BroadcastItemAdded;
            manager.OnBroadcastItemAdded += BroadcastItemAdded;
            manager.OnBroadcastItemRemoved -= BroadcastItemRemoved;
            manager.OnBroadcastItemRemoved += BroadcastItemRemoved;
            manager.OnBroadcastItemDropped -= BroadcastItemDropped;
            manager.OnBroadcastItemDropped += BroadcastItemDropped;
            manager.OnBroadcastDroppedItemRemoved -= BroadcastDroppedItemRemoved;
            manager.OnBroadcastDroppedItemRemoved += BroadcastDroppedItemRemoved;
            manager.OnBroadcastItemMoved -= BroadcastItemMoved;
            manager.OnBroadcastItemMoved += BroadcastItemMoved;
            manager.OnBroadcastItemSplit -= BroadcastItemSplit;
            manager.OnBroadcastItemSplit += BroadcastItemSplit;
            manager.OnBroadcastItemUsed -= BroadcastItemUsed;
            manager.OnBroadcastItemUsed += BroadcastItemUsed;
            manager.OnBroadcastItemEquipped -= BroadcastItemEquipped;
            manager.OnBroadcastItemEquipped += BroadcastItemEquipped;
            manager.OnBroadcastItemUnequipped -= BroadcastItemUnequipped;
            manager.OnBroadcastItemUnequipped += BroadcastItemUnequipped;
            manager.OnBroadcastSocketChange -= BroadcastSocketChange;
            manager.OnBroadcastSocketChange += BroadcastSocketChange;
            manager.OnBroadcastWealthChange -= BroadcastWealthChange;
            manager.OnBroadcastWealthChange += BroadcastWealthChange;
            manager.OnBroadcastPropertyChange -= BroadcastPropertyChange;
            manager.OnBroadcastPropertyChange += BroadcastPropertyChange;
            manager.OnBroadcastFullSnapshot -= BroadcastSnapshot;
            manager.OnBroadcastFullSnapshot += BroadcastSnapshot;
            manager.OnBroadcastDelta -= BroadcastDelta;
            manager.OnBroadcastDelta += BroadcastDelta;
            manager.OnBroadcastPickupState -= BroadcastPickupState;
            manager.OnBroadcastPickupState += BroadcastPickupState;
            manager.OnSendSnapshotToClient -= SendSnapshot;
            manager.OnSendSnapshotToClient += SendSnapshot;
            manager.OnSendPickupStateSnapshotToClient -= SendPickupStateSnapshot;
            manager.OnSendPickupStateSnapshotToClient += SendPickupStateSnapshot;

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
            NetworkInventoryManager manager = m_WiredManager;
            if (manager == null) return;
            manager.OnSendContentAddRequest -= SendContentAddRequest;
            manager.OnSendContentRemoveRequest -= SendContentRemoveRequest;
            manager.OnSendContentMoveRequest -= SendContentMoveRequest;
            manager.OnSendContentUseRequest -= SendContentUseRequest;
            manager.OnSendContentDropRequest -= SendContentDropRequest;
            manager.OnSendEquipmentRequest -= SendEquipmentRequest;
            manager.OnSendSocketRequest -= SendSocketRequest;
            manager.OnSendWealthRequest -= SendWealthRequest;
            manager.OnSendMerchantRequest -= SendMerchantRequest;
            manager.OnSendCraftingRequest -= SendCraftingRequest;
            manager.OnSendTransferRequest -= SendTransferRequest;
            manager.OnSendPickupRequest -= SendPickupRequest;
            manager.OnSendCombineRequest -= SendCombineRequest;
            manager.OnSendContentSplitRequest -= SendContentSplitRequest;
            manager.OnSendResyncRequest -= SendResyncRequest;
            manager.OnSendContentAddResponse -= SendContentAddResponse;
            manager.OnSendContentRemoveResponse -= SendContentRemoveResponse;
            manager.OnSendContentMoveResponse -= SendContentMoveResponse;
            manager.OnSendContentUseResponse -= SendContentUseResponse;
            manager.OnSendContentDropResponse -= SendContentDropResponse;
            manager.OnSendEquipmentResponse -= SendEquipmentResponse;
            manager.OnSendSocketResponse -= SendSocketResponse;
            manager.OnSendWealthResponse -= SendWealthResponse;
            manager.OnSendMerchantResponse -= SendMerchantResponse;
            manager.OnSendCraftingResponse -= SendCraftingResponse;
            manager.OnSendTransferResponse -= SendTransferResponse;
            manager.OnSendPickupResponse -= SendPickupResponse;
            manager.OnSendCombineResponse -= SendCombineResponse;
            manager.OnSendContentSplitResponse -= SendContentSplitResponse;
            manager.OnBroadcastItemAdded -= BroadcastItemAdded;
            manager.OnBroadcastItemRemoved -= BroadcastItemRemoved;
            manager.OnBroadcastItemDropped -= BroadcastItemDropped;
            manager.OnBroadcastDroppedItemRemoved -= BroadcastDroppedItemRemoved;
            manager.OnBroadcastItemMoved -= BroadcastItemMoved;
            manager.OnBroadcastItemSplit -= BroadcastItemSplit;
            manager.OnBroadcastItemUsed -= BroadcastItemUsed;
            manager.OnBroadcastItemEquipped -= BroadcastItemEquipped;
            manager.OnBroadcastItemUnequipped -= BroadcastItemUnequipped;
            manager.OnBroadcastSocketChange -= BroadcastSocketChange;
            manager.OnBroadcastWealthChange -= BroadcastWealthChange;
            manager.OnBroadcastPropertyChange -= BroadcastPropertyChange;
            manager.OnBroadcastFullSnapshot -= BroadcastSnapshot;
            manager.OnBroadcastDelta -= BroadcastDelta;
            manager.OnBroadcastPickupState -= BroadcastPickupState;
            manager.OnSendSnapshotToClient -= SendSnapshot;
            manager.OnSendPickupStateSnapshotToClient -= SendPickupStateSnapshot;
            m_WiredManager = null;
            m_ManagerInitialized = false;
        }

        private void RefreshControllerRegistry(bool force)
        {
            NetworkInventoryManager manager = GetManager();
            if (manager == null) return;

            PruneControllerRegistry(manager);
            RefreshRuntimePickupRegistry(manager);
            if (!m_AutoRegisterSceneControllers && !force) return;

            NetworkInventoryController[] controllers =
                FindObjectsByType<NetworkInventoryController>(
                    FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            for (int i = 0; i < controllers.Length; i++)
                RegisterController(manager, controllers[i]);

            if (!m_AutoAddControllersToBags) return;
            Bag[] bags = FindObjectsByType<Bag>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            for (int i = 0; i < bags.Length; i++)
            {
                Bag bag = bags[i];
                if (bag == null) continue;
                NetworkInventoryController controller =
                    bag.GetComponent<NetworkInventoryController>();
                if (controller == null)
                    controller = bag.gameObject.AddComponent<NetworkInventoryController>();
                RegisterController(manager, controller);
            }
        }

        private void RegisterController(
            NetworkInventoryManager manager, NetworkInventoryController controller)
        {
            if (manager == null || controller == null) return;

            NetworkCharacter character = controller.GetComponent<NetworkCharacter>();
            bool hasCharacter = character != null;
            if (hasCharacter &&
                (character.NetworkId == 0 ||
                 character.Role == NetworkCharacter.NetworkRole.None)) return;

            uint networkId = hasCharacter ? character.NetworkId : controller.NetworkId;
            if (networkId == 0) return;
            bool isServer = hasCharacter
                ? character.IsServerInstance
                : TransportBridge != null && TransportBridge.IsServer;
            bool isLocalClient = hasCharacter &&
                                 character.IsOwnerInstance &&
                                 character.Role == NetworkCharacter.NetworkRole.LocalClient;

            if (m_RegisteredControllers.TryGetValue(
                    networkId, out NetworkInventoryController existing))
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

        private void PruneControllerRegistry(NetworkInventoryManager manager)
        {
            m_RemoveBuffer.Clear();
            foreach (KeyValuePair<uint, NetworkInventoryController> pair in m_RegisteredControllers)
            {
                NetworkInventoryController controller = pair.Value;
                if (controller == null)
                {
                    m_RemoveBuffer.Add(pair.Key);
                    continue;
                }

                NetworkCharacter character = controller.GetComponent<NetworkCharacter>();
                if (character == null)
                {
                    if (controller.NetworkId != pair.Key) m_RemoveBuffer.Add(pair.Key);
                    continue;
                }

                if (character.NetworkId != pair.Key ||
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
            NetworkInventoryManager manager = GetManager();
            if (manager != null)
            {
                foreach (uint id in m_RegisteredControllers.Keys)
                    manager.UnregisterController(id);
            }
            m_RegisteredControllers.Clear();
        }

        private void RefreshRuntimePickupRegistry(NetworkInventoryManager manager)
        {
            if (manager == null || TransportBridge == null || !TransportBridge.IsServer)
            {
                UnregisterAllRuntimePickups(manager);
                return;
            }

            m_RuntimePickupCandidates.Clear();
            m_DuplicateRuntimePickupIds.Clear();
            FusionInventoryRuntimePickupIdentityAdapter[] adapters =
                FindObjectsByType<FusionInventoryRuntimePickupIdentityAdapter>(
                    FindObjectsInactive.Exclude, FindObjectsSortMode.None);

            for (int i = 0; i < adapters.Length; i++)
            {
                FusionInventoryRuntimePickupIdentityAdapter adapter = adapters[i];
                if (adapter == null || !adapter.IsSpawned) continue;
                uint id = adapter.NetworkPickupId;
                if (id == 0) continue;

                NetworkInventoryPickupSource source =
                    adapter.GetComponent<NetworkInventoryPickupSource>();
                if (source == null || !ReferenceEquals(source.RuntimeIdentity, adapter))
                {
                    WarnOnce(
                        $"source:{adapter.GetInstanceID()}",
                        $"Runtime pickup '{adapter.name}' must have a same-object " +
                        "NetworkInventoryPickupSource referencing its Fusion identity adapter.");
                    continue;
                }

                if (m_RuntimePickupCandidates.TryGetValue(
                        id, out FusionInventoryRuntimePickupIdentityAdapter existing) &&
                    existing != adapter)
                {
                    m_DuplicateRuntimePickupIds.Add(id);
                    continue;
                }

                m_RuntimePickupCandidates[id] = adapter;
            }

            m_RuntimePickupRemoveBuffer.Clear();
            foreach (KeyValuePair<uint, FusionInventoryRuntimePickupIdentityAdapter> pair in
                     m_RegisteredRuntimePickups)
            {
                bool keep = !m_DuplicateRuntimePickupIds.Contains(pair.Key) &&
                            m_RuntimePickupCandidates.TryGetValue(
                                pair.Key, out FusionInventoryRuntimePickupIdentityAdapter candidate) &&
                            candidate == pair.Value;
                if (keep) continue;
                manager.UnregisterRuntimePickupSource(pair.Key, pair.Value);
                m_RuntimePickupRemoveBuffer.Add(pair.Key);
            }

            for (int i = 0; i < m_RuntimePickupRemoveBuffer.Count; i++)
                m_RegisteredRuntimePickups.Remove(m_RuntimePickupRemoveBuffer[i]);

            foreach (uint duplicateId in m_DuplicateRuntimePickupIds)
            {
                WarnOnce(
                    $"duplicate:{duplicateId}",
                    $"Multiple Fusion runtime pickups resolved network id {duplicateId}; " +
                    "all colliding pickups are rejected.");
            }

            foreach (KeyValuePair<uint, FusionInventoryRuntimePickupIdentityAdapter> pair in
                     m_RuntimePickupCandidates)
            {
                if (m_DuplicateRuntimePickupIds.Contains(pair.Key)) continue;
                NetworkInventoryPickupSource source =
                    pair.Value.GetComponent<NetworkInventoryPickupSource>();
                if (source != null &&
                    manager.RegisterRuntimePickupSource(pair.Key, source, pair.Value))
                    m_RegisteredRuntimePickups[pair.Key] = pair.Value;
            }
        }

        private void UnregisterAllRuntimePickups(NetworkInventoryManager manager = null)
        {
            manager ??= GetManager();
            if (manager != null)
            {
                foreach (KeyValuePair<uint, FusionInventoryRuntimePickupIdentityAdapter> pair in
                         m_RegisteredRuntimePickups)
                    manager.UnregisterRuntimePickupSource(pair.Key, pair.Value);
            }

            m_RegisteredRuntimePickups.Clear();
            m_RuntimePickupCandidates.Clear();
            m_DuplicateRuntimePickupIds.Clear();
            m_RuntimePickupRemoveBuffer.Clear();
        }

        private void SendContentAddRequest(NetworkContentAddRequest value) =>
            SendToAuthority((ushort)MessageType.ContentAddRequest, value);
        private void SendContentRemoveRequest(NetworkContentRemoveRequest value) =>
            SendToAuthority((ushort)MessageType.ContentRemoveRequest, value);
        private void SendContentMoveRequest(NetworkContentMoveRequest value) =>
            SendToAuthority((ushort)MessageType.ContentMoveRequest, value);
        private void SendContentUseRequest(NetworkContentUseRequest value) =>
            SendToAuthority((ushort)MessageType.ContentUseRequest, value);
        private void SendContentDropRequest(NetworkContentDropRequest value) =>
            SendToAuthority((ushort)MessageType.ContentDropRequest, value);
        private void SendEquipmentRequest(NetworkEquipmentRequest value) =>
            SendToAuthority((ushort)MessageType.EquipmentRequest, value);
        private void SendSocketRequest(NetworkSocketRequest value) =>
            SendToAuthority((ushort)MessageType.SocketRequest, value);
        private void SendWealthRequest(NetworkWealthRequest value) =>
            SendToAuthority((ushort)MessageType.WealthRequest, value);
        private void SendMerchantRequest(NetworkMerchantRequest value) =>
            SendToAuthority((ushort)MessageType.MerchantRequest, value);
        private void SendCraftingRequest(NetworkCraftingRequest value) =>
            SendToAuthority((ushort)MessageType.CraftingRequest, value);
        private void SendTransferRequest(NetworkTransferRequest value) =>
            SendToAuthority((ushort)MessageType.TransferRequest, value);
        private void SendPickupRequest(NetworkPickupRequest value) =>
            SendToAuthority((ushort)MessageType.PickupRequest, value);
        private void SendCombineRequest(NetworkCombineRequest value) =>
            SendToAuthority((ushort)MessageType.CombineRequest, value);
        private void SendContentSplitRequest(NetworkContentSplitRequest value) =>
            SendToAuthority((ushort)MessageType.ContentSplitRequest, value);
        private void SendResyncRequest(NetworkInventoryResyncRequest value) =>
            SendToAuthority((ushort)MessageType.ResyncRequest, value);

        private void SendContentAddResponse(uint id, NetworkContentAddResponse value) =>
            SendToClient(id, (ushort)MessageType.ContentAddResponse, value);
        private void SendContentRemoveResponse(uint id, NetworkContentRemoveResponse value) =>
            SendToClient(id, (ushort)MessageType.ContentRemoveResponse, value);
        private void SendContentMoveResponse(uint id, NetworkContentMoveResponse value) =>
            SendToClient(id, (ushort)MessageType.ContentMoveResponse, value);
        private void SendContentUseResponse(uint id, NetworkContentUseResponse value) =>
            SendToClient(id, (ushort)MessageType.ContentUseResponse, value);
        private void SendContentDropResponse(uint id, NetworkContentDropResponse value) =>
            SendToClient(id, (ushort)MessageType.ContentDropResponse, value);
        private void SendEquipmentResponse(uint id, NetworkEquipmentResponse value) =>
            SendToClient(id, (ushort)MessageType.EquipmentResponse, value);
        private void SendSocketResponse(uint id, NetworkSocketResponse value) =>
            SendToClient(id, (ushort)MessageType.SocketResponse, value);
        private void SendWealthResponse(uint id, NetworkWealthResponse value) =>
            SendToClient(id, (ushort)MessageType.WealthResponse, value);
        private void SendMerchantResponse(uint id, NetworkMerchantResponse value) =>
            SendToClient(id, (ushort)MessageType.MerchantResponse, value);
        private void SendCraftingResponse(uint id, NetworkCraftingResponse value) =>
            SendToClient(id, (ushort)MessageType.CraftingResponse, value);
        private void SendTransferResponse(uint id, NetworkTransferResponse value) =>
            SendToClient(id, (ushort)MessageType.TransferResponse, value);
        private void SendPickupResponse(uint id, NetworkPickupResponse value) =>
            SendToClient(id, (ushort)MessageType.PickupResponse, value);
        private void SendCombineResponse(uint id, NetworkCombineResponse value) =>
            SendToClient(id, (ushort)MessageType.CombineResponse, value);
        private void SendContentSplitResponse(uint id, NetworkContentSplitResponse value) =>
            SendToClient(id, (ushort)MessageType.ContentSplitResponse, value);

        private void BroadcastItemAdded(NetworkItemAddedBroadcast value) =>
            BroadcastInventory(value.BagNetworkId, MessageType.ItemAdded, value);
        private void BroadcastItemRemoved(NetworkItemRemovedBroadcast value) =>
            BroadcastInventory(value.BagNetworkId, MessageType.ItemRemoved, value);
        private void BroadcastItemDropped(NetworkItemDroppedBroadcast value) =>
            BroadcastInventory(value.SourceBagNetworkId, MessageType.ItemDropped, value);
        private void BroadcastDroppedItemRemoved(NetworkDroppedItemRemovedBroadcast value) =>
            BroadcastInventory(
                value.SourceBagNetworkId, MessageType.DroppedItemRemoved, value);
        private void BroadcastItemMoved(NetworkItemMovedBroadcast value) =>
            BroadcastInventory(value.BagNetworkId, MessageType.ItemMoved, value);
        private void BroadcastItemSplit(NetworkItemSplitBroadcast value) =>
            BroadcastInventory(value.BagNetworkId, MessageType.ItemSplit, value);
        private void BroadcastItemUsed(NetworkItemUsedBroadcast value) =>
            BroadcastInventory(value.BagNetworkId, MessageType.ItemUsed, value);
        private void BroadcastItemEquipped(NetworkItemEquippedBroadcast value) =>
            BroadcastInventory(value.BagNetworkId, MessageType.ItemEquipped, value);
        private void BroadcastItemUnequipped(NetworkItemUnequippedBroadcast value) =>
            BroadcastInventory(value.BagNetworkId, MessageType.ItemUnequipped, value);
        private void BroadcastSocketChange(NetworkSocketChangeBroadcast value) =>
            BroadcastInventory(value.BagNetworkId, MessageType.SocketChange, value);
        private void BroadcastWealthChange(NetworkWealthChangeBroadcast value) =>
            BroadcastInventory(value.BagNetworkId, MessageType.WealthChange, value);
        private void BroadcastPropertyChange(NetworkPropertyChangeBroadcast value) =>
            BroadcastInventory(value.BagNetworkId, MessageType.PropertyChange, value);
        private void BroadcastSnapshot(NetworkInventorySnapshot value) =>
            BroadcastInventory(value.BagNetworkId, MessageType.Snapshot, value);
        private void BroadcastDelta(NetworkInventoryDelta value) =>
            BroadcastInventory(value.BagNetworkId, MessageType.Delta, value);

        private void BroadcastPickupState(NetworkPickupStateBroadcast value)
        {
            // Consumption is global persistent state; relevance culling would leave stale pickups.
            Broadcast((ushort)MessageType.PickupState, value);
        }

        private void SendSnapshot(ulong rawClientId, NetworkInventorySnapshot value)
        {
            if (!NetworkTransportBridge.TryConvertSenderClientId(rawClientId, out uint id)) return;
            SendToClient(id, (ushort)MessageType.Snapshot, value);
        }

        private void SendPickupStateSnapshot(
            ulong rawClientId, NetworkPickupStateSnapshot value)
        {
            if (!NetworkTransportBridge.TryConvertSenderClientId(rawClientId, out uint id)) return;
            SendToClient(id, (ushort)MessageType.PickupStateSnapshot, value);
        }

        private void BroadcastInventory<T>(
            uint bagNetworkId, MessageType type, T value)
        {
            FusionTransportBridge bridge = TransportBridge;
            if (bridge == null || !bridge.IsServer) return;
            if (!ShouldFilterBySessionProfile())
            {
                Broadcast((ushort)type, value);
                return;
            }

            foreach (uint clientId in bridge.ConnectedClientIds)
            {
                if (!ShouldSendInventoryToClient(clientId, bagNetworkId)) continue;
                SendToClient(clientId, (ushort)type, value);
            }
        }

        private bool ShouldFilterBySessionProfile()
        {
            if (!m_UseSessionProfileRelevance) return false;
            NetworkSessionProfile profile =
                TransportBridge != null ? TransportBridge.GlobalSessionProfile : null;
            return profile != null &&
                   (profile.enableDistanceCulling ||
                    profile.requireObserverCharacterForRelevance);
        }

        private bool ShouldSendInventoryToClient(uint clientId, uint bagNetworkId)
        {
            FusionTransportBridge bridge = TransportBridge;
            NetworkSessionProfile profile =
                bridge != null ? bridge.GlobalSessionProfile : null;
            if (profile == null) return true;

            if (bridge.TryGetCharacterOwner(bagNetworkId, out uint ownerId) &&
                ownerId == clientId) return true;
            if (!TryGetBagPosition(bagNetworkId, out Vector3 bagPosition))
                return !profile.requireObserverCharacterForRelevance;
            if (!TryGetObserverPosition(clientId, out Vector3 observerPosition))
                return !profile.requireObserverCharacterForRelevance;

            return !profile.enableDistanceCulling ||
                   Vector3.Distance(observerPosition, bagPosition) <= profile.cullDistance;
        }

        private bool TryGetBagPosition(uint bagNetworkId, out Vector3 position)
        {
            position = Vector3.zero;
            Character character = TransportBridge != null
                ? TransportBridge.ResolveCharacter(bagNetworkId)
                : null;
            if (character != null)
            {
                position = character.transform.position;
                return true;
            }

            if (!m_RegisteredControllers.TryGetValue(
                    bagNetworkId, out NetworkInventoryController controller) ||
                controller == null) return false;
            position = controller.transform.position;
            return true;
        }

        private bool TryGetObserverPosition(uint clientId, out Vector3 position)
        {
            position = Vector3.zero;
            FusionTransportBridge bridge = TransportBridge;
            if (bridge == null ||
                !bridge.TryGetRepresentativeCharacterId(clientId, out uint characterId))
                return false;
            Character character = bridge.ResolveCharacter(characterId);
            if (character == null) return false;
            position = character.transform.position;
            return true;
        }

        private void WarnOnce(string key, string message)
        {
            if (!m_Warnings.Add(key)) return;
            Debug.LogWarning($"[FusionInventoryTransportBridge] {message}", this);
        }

        private static NetworkInventoryManager GetManager()
        {
            return NetworkInventoryManager.Instance != null
                ? NetworkInventoryManager.Instance
                : FindFirstObjectByType<NetworkInventoryManager>(FindObjectsInactive.Include);
        }
    }
}
#endif
