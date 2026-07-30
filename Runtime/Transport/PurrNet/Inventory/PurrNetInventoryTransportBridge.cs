#if GC2_INVENTORY
using System.Collections.Generic;
using System.Threading.Tasks;
using Arawn.GameCreator2.Networking;
using Arawn.GameCreator2.Networking.Transport.PurrNet;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Inventory;
using PurrNet;
using PurrNet.Packing;
using PurrNet.Transports;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Inventory.Transport.PurrNet
{
    [DisallowMultipleComponent]
    [AddComponentMenu("Game Creator/Network/Transport/PurrNet Inventory Bridge")]
    [DefaultExecutionOrder(-338)]
    public sealed class PurrNetInventoryTransportBridge : MonoBehaviour
    {
        [Header("PurrNet")]
        [Tooltip("Optional reference to a specific NetworkManager. Leave empty to use NetworkManager.main.")]
        [SerializeField] private NetworkManager m_NetworkManager;

        [Tooltip("Optional reference to the core GC2 PurrNet bridge used for ownership and relevance lookup.")]
        [SerializeField] private PurrNetTransportBridge m_CoreBridge;

        [Tooltip("Reliable channel used for inventory requests, responses, deltas, and snapshots.")]
        [SerializeField] private Channel m_Channel = Channel.ReliableOrdered;

        [Header("Controllers")]
        [Tooltip("Automatically finds NetworkInventoryController components on spawned NetworkCharacter objects.")]
        [SerializeField] private bool m_AutoRegisterSceneControllers = true;

        [Tooltip("Automatically adds NetworkInventoryController to scene/world Bag objects such as chests.")]
        [SerializeField] private bool m_AutoAddControllersToBags = true;

        [Min(0.05f)]
        [SerializeField] private float m_ControllerScanInterval = 0.25f;

        [Header("Relevance")]
        [Tooltip("When the active NetworkSessionProfile enables distance culling, inventory broadcasts are sent only to relevant observers and the owner.")]
        [SerializeField] private bool m_UseSessionProfileRelevance = true;

        private readonly Dictionary<uint, NetworkInventoryController> m_RegisteredControllers = new(32);
        private readonly List<uint> m_RemoveBuffer = new(16);
        private readonly Dictionary<uint, PurrNetInventoryRuntimePickupIdentityAdapter>
            m_RegisteredRuntimePickups = new(16);
        private readonly Dictionary<uint, PurrNetInventoryRuntimePickupIdentityAdapter>
            m_RuntimePickupCandidates = new(16);
        private readonly HashSet<uint> m_DuplicateRuntimePickupIds = new();
        private readonly List<uint> m_RuntimePickupRemoveBuffer = new(8);
        private readonly HashSet<string> m_RouteWarnings = new();

        private NetworkManager m_HookedManager;
        private bool m_SubscribedServer;
        private bool m_SubscribedClient;
        private bool m_ManagerInitialized;
        private bool m_LastServer;
        private float m_NextControllerScanTime;

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
            EnsureReliableOrderedChannel(logCorrection: true);
            if (m_NetworkManager == null) m_NetworkManager = NetworkManager.main;
            if (m_CoreBridge == null) m_CoreBridge = NetworkTransportBridge.Active as PurrNetTransportBridge;
        }

        private void OnValidate()
        {
            EnsureReliableOrderedChannel(logCorrection: false);
        }

        private void OnEnable()
        {
            TryHookNetworkManager();
            WireInventoryManager();
            RefreshControllerRegistry(force: true);
        }

        private void Start()
        {
            TryHookNetworkManager();
            WireInventoryManager();
            RefreshControllerRegistry(force: true);
        }

        private void Update()
        {
            TryHookNetworkManager();
            WireInventoryManager();

            NetworkManager manager = ActiveManager;
            bool needsRuntimePickupScan = manager != null && manager.isServer;
            if (!m_AutoRegisterSceneControllers && !needsRuntimePickupScan) return;
            if (Time.unscaledTime < m_NextControllerScanTime) return;

            m_NextControllerScanTime = Time.unscaledTime + Mathf.Max(0.05f, m_ControllerScanInterval);
            RefreshControllerRegistry(force: false);
        }

        private void OnDisable()
        {
            UnhookNetworkManager();
            UnwireInventoryManager();
            UnregisterAllControllers();
            UnregisterAllRuntimePickups();
            m_RouteWarnings.Clear();
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
                nm.Unsubscribe<GC2InventoryContentAddRequestPacket>(HandleContentAddRequestServer, true);
                nm.Unsubscribe<GC2InventoryContentRemoveRequestPacket>(HandleContentRemoveRequestServer, true);
                nm.Unsubscribe<GC2InventoryContentMoveRequestPacket>(HandleContentMoveRequestServer, true);
                nm.Unsubscribe<GC2InventoryContentUseRequestPacket>(HandleContentUseRequestServer, true);
                nm.Unsubscribe<GC2InventoryContentDropRequestPacket>(HandleContentDropRequestServer, true);
                nm.Unsubscribe<GC2InventoryEquipmentRequestPacket>(HandleEquipmentRequestServer, true);
                nm.Unsubscribe<GC2InventorySocketRequestPacket>(HandleSocketRequestServer, true);
                nm.Unsubscribe<GC2InventoryWealthRequestPacket>(HandleWealthRequestServer, true);
                nm.Unsubscribe<GC2InventoryMerchantRequestPacket>(HandleMerchantRequestServer, true);
                nm.Unsubscribe<GC2InventoryCraftingRequestPacket>(HandleCraftingRequestServer, true);
                nm.Unsubscribe<GC2InventoryTransferRequestPacket>(HandleTransferRequestServer, true);
                nm.Unsubscribe<GC2InventoryPickupRequestPacket>(HandlePickupRequestServer, true);
                nm.Unsubscribe<GC2InventoryCombineRequestPacket>(HandleCombineRequestServer, true);
                nm.Unsubscribe<GC2InventoryContentSplitRequestPacket>(HandleContentSplitRequestServer, true);
                nm.Unsubscribe<GC2InventoryResyncRequestPacket>(HandleResyncRequestServer, true);
                m_SubscribedServer = false;
            }

            if (m_SubscribedClient)
            {
                nm.Unsubscribe<GC2InventoryContentAddResponsePacket>(HandleContentAddResponseClient, false);
                nm.Unsubscribe<GC2InventoryContentRemoveResponsePacket>(HandleContentRemoveResponseClient, false);
                nm.Unsubscribe<GC2InventoryContentMoveResponsePacket>(HandleContentMoveResponseClient, false);
                nm.Unsubscribe<GC2InventoryContentUseResponsePacket>(HandleContentUseResponseClient, false);
                nm.Unsubscribe<GC2InventoryContentDropResponsePacket>(HandleContentDropResponseClient, false);
                nm.Unsubscribe<GC2InventoryEquipmentResponsePacket>(HandleEquipmentResponseClient, false);
                nm.Unsubscribe<GC2InventorySocketResponsePacket>(HandleSocketResponseClient, false);
                nm.Unsubscribe<GC2InventoryWealthResponsePacket>(HandleWealthResponseClient, false);
                nm.Unsubscribe<GC2InventoryMerchantResponsePacket>(HandleMerchantResponseClient, false);
                nm.Unsubscribe<GC2InventoryCraftingResponsePacket>(HandleCraftingResponseClient, false);
                nm.Unsubscribe<GC2InventoryTransferResponsePacket>(HandleTransferResponseClient, false);
                nm.Unsubscribe<GC2InventoryPickupResponsePacket>(HandlePickupResponseClient, false);
                nm.Unsubscribe<GC2InventoryCombineResponsePacket>(HandleCombineResponseClient, false);
                nm.Unsubscribe<GC2InventoryContentSplitResponsePacket>(HandleContentSplitResponseClient, false);
                nm.Unsubscribe<GC2InventoryItemAddedBroadcastPacket>(HandleItemAddedBroadcastClient, false);
                nm.Unsubscribe<GC2InventoryItemRemovedBroadcastPacket>(HandleItemRemovedBroadcastClient, false);
                nm.Unsubscribe<GC2InventoryItemDroppedBroadcastPacket>(HandleItemDroppedBroadcastClient, false);
                nm.Unsubscribe<GC2InventoryDroppedItemRemovedBroadcastPacket>(HandleDroppedItemRemovedBroadcastClient, false);
                nm.Unsubscribe<GC2InventoryItemMovedBroadcastPacket>(HandleItemMovedBroadcastClient, false);
                nm.Unsubscribe<GC2InventoryItemSplitBroadcastPacket>(HandleItemSplitBroadcastClient, false);
                nm.Unsubscribe<GC2InventoryItemUsedBroadcastPacket>(HandleItemUsedBroadcastClient, false);
                nm.Unsubscribe<GC2InventoryItemEquippedBroadcastPacket>(HandleItemEquippedBroadcastClient, false);
                nm.Unsubscribe<GC2InventoryItemUnequippedBroadcastPacket>(HandleItemUnequippedBroadcastClient, false);
                nm.Unsubscribe<GC2InventorySocketChangeBroadcastPacket>(HandleSocketChangeBroadcastClient, false);
                nm.Unsubscribe<GC2InventoryWealthChangeBroadcastPacket>(HandleWealthChangeBroadcastClient, false);
                nm.Unsubscribe<GC2InventoryPropertyChangeBroadcastPacket>(HandlePropertyChangeBroadcastClient, false);
                nm.Unsubscribe<GC2InventorySnapshotPacket>(HandleSnapshotClient, false);
                nm.Unsubscribe<GC2InventoryDeltaPacket>(HandleDeltaClient, false);
                nm.Unsubscribe<GC2InventoryPickupStateBroadcastPacket>(HandlePickupStateBroadcastClient, false);
                nm.Unsubscribe<GC2InventoryPickupStateSnapshotPacket>(HandlePickupStateSnapshotClient, false);
                m_SubscribedClient = false;
            }

            m_HookedManager = null;
        }

        private void HandleNetworkStarted(NetworkManager manager, bool asServer)
        {
            if (asServer && !m_SubscribedServer)
            {
                manager.Subscribe<GC2InventoryContentAddRequestPacket>(HandleContentAddRequestServer, true);
                manager.Subscribe<GC2InventoryContentRemoveRequestPacket>(HandleContentRemoveRequestServer, true);
                manager.Subscribe<GC2InventoryContentMoveRequestPacket>(HandleContentMoveRequestServer, true);
                manager.Subscribe<GC2InventoryContentUseRequestPacket>(HandleContentUseRequestServer, true);
                manager.Subscribe<GC2InventoryContentDropRequestPacket>(HandleContentDropRequestServer, true);
                manager.Subscribe<GC2InventoryEquipmentRequestPacket>(HandleEquipmentRequestServer, true);
                manager.Subscribe<GC2InventorySocketRequestPacket>(HandleSocketRequestServer, true);
                manager.Subscribe<GC2InventoryWealthRequestPacket>(HandleWealthRequestServer, true);
                manager.Subscribe<GC2InventoryMerchantRequestPacket>(HandleMerchantRequestServer, true);
                manager.Subscribe<GC2InventoryCraftingRequestPacket>(HandleCraftingRequestServer, true);
                manager.Subscribe<GC2InventoryTransferRequestPacket>(HandleTransferRequestServer, true);
                manager.Subscribe<GC2InventoryPickupRequestPacket>(HandlePickupRequestServer, true);
                manager.Subscribe<GC2InventoryCombineRequestPacket>(HandleCombineRequestServer, true);
                manager.Subscribe<GC2InventoryContentSplitRequestPacket>(HandleContentSplitRequestServer, true);
                manager.Subscribe<GC2InventoryResyncRequestPacket>(HandleResyncRequestServer, true);
                m_SubscribedServer = true;
            }
            else if (!asServer && !m_SubscribedClient)
            {
                manager.Subscribe<GC2InventoryContentAddResponsePacket>(HandleContentAddResponseClient, false);
                manager.Subscribe<GC2InventoryContentRemoveResponsePacket>(HandleContentRemoveResponseClient, false);
                manager.Subscribe<GC2InventoryContentMoveResponsePacket>(HandleContentMoveResponseClient, false);
                manager.Subscribe<GC2InventoryContentUseResponsePacket>(HandleContentUseResponseClient, false);
                manager.Subscribe<GC2InventoryContentDropResponsePacket>(HandleContentDropResponseClient, false);
                manager.Subscribe<GC2InventoryEquipmentResponsePacket>(HandleEquipmentResponseClient, false);
                manager.Subscribe<GC2InventorySocketResponsePacket>(HandleSocketResponseClient, false);
                manager.Subscribe<GC2InventoryWealthResponsePacket>(HandleWealthResponseClient, false);
                manager.Subscribe<GC2InventoryMerchantResponsePacket>(HandleMerchantResponseClient, false);
                manager.Subscribe<GC2InventoryCraftingResponsePacket>(HandleCraftingResponseClient, false);
                manager.Subscribe<GC2InventoryTransferResponsePacket>(HandleTransferResponseClient, false);
                manager.Subscribe<GC2InventoryPickupResponsePacket>(HandlePickupResponseClient, false);
                manager.Subscribe<GC2InventoryCombineResponsePacket>(HandleCombineResponseClient, false);
                manager.Subscribe<GC2InventoryContentSplitResponsePacket>(HandleContentSplitResponseClient, false);
                manager.Subscribe<GC2InventoryItemAddedBroadcastPacket>(HandleItemAddedBroadcastClient, false);
                manager.Subscribe<GC2InventoryItemRemovedBroadcastPacket>(HandleItemRemovedBroadcastClient, false);
                manager.Subscribe<GC2InventoryItemDroppedBroadcastPacket>(HandleItemDroppedBroadcastClient, false);
                manager.Subscribe<GC2InventoryDroppedItemRemovedBroadcastPacket>(HandleDroppedItemRemovedBroadcastClient, false);
                manager.Subscribe<GC2InventoryItemMovedBroadcastPacket>(HandleItemMovedBroadcastClient, false);
                manager.Subscribe<GC2InventoryItemSplitBroadcastPacket>(HandleItemSplitBroadcastClient, false);
                manager.Subscribe<GC2InventoryItemUsedBroadcastPacket>(HandleItemUsedBroadcastClient, false);
                manager.Subscribe<GC2InventoryItemEquippedBroadcastPacket>(HandleItemEquippedBroadcastClient, false);
                manager.Subscribe<GC2InventoryItemUnequippedBroadcastPacket>(HandleItemUnequippedBroadcastClient, false);
                manager.Subscribe<GC2InventorySocketChangeBroadcastPacket>(HandleSocketChangeBroadcastClient, false);
                manager.Subscribe<GC2InventoryWealthChangeBroadcastPacket>(HandleWealthChangeBroadcastClient, false);
                manager.Subscribe<GC2InventoryPropertyChangeBroadcastPacket>(HandlePropertyChangeBroadcastClient, false);
                manager.Subscribe<GC2InventorySnapshotPacket>(HandleSnapshotClient, false);
                manager.Subscribe<GC2InventoryDeltaPacket>(HandleDeltaClient, false);
                manager.Subscribe<GC2InventoryPickupStateBroadcastPacket>(HandlePickupStateBroadcastClient, false);
                manager.Subscribe<GC2InventoryPickupStateSnapshotPacket>(HandlePickupStateSnapshotClient, false);
                m_SubscribedClient = true;
            }

            WireInventoryManager();
            RefreshControllerRegistry(force: true);
        }

        private void HandleNetworkShutdown(NetworkManager manager, bool asServer)
        {
            if (asServer && m_SubscribedServer)
            {
                manager.Unsubscribe<GC2InventoryContentAddRequestPacket>(HandleContentAddRequestServer, true);
                manager.Unsubscribe<GC2InventoryContentRemoveRequestPacket>(HandleContentRemoveRequestServer, true);
                manager.Unsubscribe<GC2InventoryContentMoveRequestPacket>(HandleContentMoveRequestServer, true);
                manager.Unsubscribe<GC2InventoryContentUseRequestPacket>(HandleContentUseRequestServer, true);
                manager.Unsubscribe<GC2InventoryContentDropRequestPacket>(HandleContentDropRequestServer, true);
                manager.Unsubscribe<GC2InventoryEquipmentRequestPacket>(HandleEquipmentRequestServer, true);
                manager.Unsubscribe<GC2InventorySocketRequestPacket>(HandleSocketRequestServer, true);
                manager.Unsubscribe<GC2InventoryWealthRequestPacket>(HandleWealthRequestServer, true);
                manager.Unsubscribe<GC2InventoryMerchantRequestPacket>(HandleMerchantRequestServer, true);
                manager.Unsubscribe<GC2InventoryCraftingRequestPacket>(HandleCraftingRequestServer, true);
                manager.Unsubscribe<GC2InventoryTransferRequestPacket>(HandleTransferRequestServer, true);
                manager.Unsubscribe<GC2InventoryPickupRequestPacket>(HandlePickupRequestServer, true);
                manager.Unsubscribe<GC2InventoryCombineRequestPacket>(HandleCombineRequestServer, true);
                manager.Unsubscribe<GC2InventoryContentSplitRequestPacket>(HandleContentSplitRequestServer, true);
                manager.Unsubscribe<GC2InventoryResyncRequestPacket>(HandleResyncRequestServer, true);
                m_SubscribedServer = false;
            }
            else if (!asServer && m_SubscribedClient)
            {
                manager.Unsubscribe<GC2InventoryContentAddResponsePacket>(HandleContentAddResponseClient, false);
                manager.Unsubscribe<GC2InventoryContentRemoveResponsePacket>(HandleContentRemoveResponseClient, false);
                manager.Unsubscribe<GC2InventoryContentMoveResponsePacket>(HandleContentMoveResponseClient, false);
                manager.Unsubscribe<GC2InventoryContentUseResponsePacket>(HandleContentUseResponseClient, false);
                manager.Unsubscribe<GC2InventoryContentDropResponsePacket>(HandleContentDropResponseClient, false);
                manager.Unsubscribe<GC2InventoryEquipmentResponsePacket>(HandleEquipmentResponseClient, false);
                manager.Unsubscribe<GC2InventorySocketResponsePacket>(HandleSocketResponseClient, false);
                manager.Unsubscribe<GC2InventoryWealthResponsePacket>(HandleWealthResponseClient, false);
                manager.Unsubscribe<GC2InventoryMerchantResponsePacket>(HandleMerchantResponseClient, false);
                manager.Unsubscribe<GC2InventoryCraftingResponsePacket>(HandleCraftingResponseClient, false);
                manager.Unsubscribe<GC2InventoryTransferResponsePacket>(HandleTransferResponseClient, false);
                manager.Unsubscribe<GC2InventoryPickupResponsePacket>(HandlePickupResponseClient, false);
                manager.Unsubscribe<GC2InventoryCombineResponsePacket>(HandleCombineResponseClient, false);
                manager.Unsubscribe<GC2InventoryContentSplitResponsePacket>(HandleContentSplitResponseClient, false);
                manager.Unsubscribe<GC2InventoryItemAddedBroadcastPacket>(HandleItemAddedBroadcastClient, false);
                manager.Unsubscribe<GC2InventoryItemRemovedBroadcastPacket>(HandleItemRemovedBroadcastClient, false);
                manager.Unsubscribe<GC2InventoryItemDroppedBroadcastPacket>(HandleItemDroppedBroadcastClient, false);
                manager.Unsubscribe<GC2InventoryDroppedItemRemovedBroadcastPacket>(HandleDroppedItemRemovedBroadcastClient, false);
                manager.Unsubscribe<GC2InventoryItemMovedBroadcastPacket>(HandleItemMovedBroadcastClient, false);
                manager.Unsubscribe<GC2InventoryItemSplitBroadcastPacket>(HandleItemSplitBroadcastClient, false);
                manager.Unsubscribe<GC2InventoryItemUsedBroadcastPacket>(HandleItemUsedBroadcastClient, false);
                manager.Unsubscribe<GC2InventoryItemEquippedBroadcastPacket>(HandleItemEquippedBroadcastClient, false);
                manager.Unsubscribe<GC2InventoryItemUnequippedBroadcastPacket>(HandleItemUnequippedBroadcastClient, false);
                manager.Unsubscribe<GC2InventorySocketChangeBroadcastPacket>(HandleSocketChangeBroadcastClient, false);
                manager.Unsubscribe<GC2InventoryWealthChangeBroadcastPacket>(HandleWealthChangeBroadcastClient, false);
                manager.Unsubscribe<GC2InventoryPropertyChangeBroadcastPacket>(HandlePropertyChangeBroadcastClient, false);
                manager.Unsubscribe<GC2InventorySnapshotPacket>(HandleSnapshotClient, false);
                manager.Unsubscribe<GC2InventoryDeltaPacket>(HandleDeltaClient, false);
                manager.Unsubscribe<GC2InventoryPickupStateBroadcastPacket>(HandlePickupStateBroadcastClient, false);
                manager.Unsubscribe<GC2InventoryPickupStateSnapshotPacket>(HandlePickupStateSnapshotClient, false);
                m_SubscribedClient = false;
            }

            if (asServer) UnregisterAllRuntimePickups();
            WireInventoryManager();
        }

        private void HandlePlayerLoadedScene(PlayerID player, SceneID scene, bool asServer)
        {
            if (!asServer) return;
            RefreshControllerRegistry(force: true);
            GetInventoryManager()?.SendInitialState(player.id);
        }

        private void WireInventoryManager()
        {
            NetworkInventoryManager manager = GetInventoryManager();
            if (manager == null) return;

            manager.OnSendContentAddRequest -= SendContentAddRequestToServer;
            manager.OnSendContentAddRequest += SendContentAddRequestToServer;
            manager.OnSendContentRemoveRequest -= SendContentRemoveRequestToServer;
            manager.OnSendContentRemoveRequest += SendContentRemoveRequestToServer;
            manager.OnSendContentMoveRequest -= SendContentMoveRequestToServer;
            manager.OnSendContentMoveRequest += SendContentMoveRequestToServer;
            manager.OnSendContentUseRequest -= SendContentUseRequestToServer;
            manager.OnSendContentUseRequest += SendContentUseRequestToServer;
            manager.OnSendContentDropRequest -= SendContentDropRequestToServer;
            manager.OnSendContentDropRequest += SendContentDropRequestToServer;
            manager.OnSendEquipmentRequest -= SendEquipmentRequestToServer;
            manager.OnSendEquipmentRequest += SendEquipmentRequestToServer;
            manager.OnSendSocketRequest -= SendSocketRequestToServer;
            manager.OnSendSocketRequest += SendSocketRequestToServer;
            manager.OnSendWealthRequest -= SendWealthRequestToServer;
            manager.OnSendWealthRequest += SendWealthRequestToServer;
            manager.OnSendMerchantRequest -= SendMerchantRequestToServer;
            manager.OnSendMerchantRequest += SendMerchantRequestToServer;
            manager.OnSendCraftingRequest -= SendCraftingRequestToServer;
            manager.OnSendCraftingRequest += SendCraftingRequestToServer;
            manager.OnSendTransferRequest -= SendTransferRequestToServer;
            manager.OnSendTransferRequest += SendTransferRequestToServer;
            manager.OnSendPickupRequest -= SendPickupRequestToServer;
            manager.OnSendPickupRequest += SendPickupRequestToServer;
            manager.OnSendCombineRequest -= SendCombineRequestToServer;
            manager.OnSendCombineRequest += SendCombineRequestToServer;
            manager.OnSendContentSplitRequest -= SendContentSplitRequestToServer;
            manager.OnSendContentSplitRequest += SendContentSplitRequestToServer;
            manager.OnSendResyncRequest -= SendResyncRequestToServer;
            manager.OnSendResyncRequest += SendResyncRequestToServer;

            manager.OnSendContentAddResponse -= SendContentAddResponseToClient;
            manager.OnSendContentAddResponse += SendContentAddResponseToClient;
            manager.OnSendContentRemoveResponse -= SendContentRemoveResponseToClient;
            manager.OnSendContentRemoveResponse += SendContentRemoveResponseToClient;
            manager.OnSendContentMoveResponse -= SendContentMoveResponseToClient;
            manager.OnSendContentMoveResponse += SendContentMoveResponseToClient;
            manager.OnSendContentUseResponse -= SendContentUseResponseToClient;
            manager.OnSendContentUseResponse += SendContentUseResponseToClient;
            manager.OnSendContentDropResponse -= SendContentDropResponseToClient;
            manager.OnSendContentDropResponse += SendContentDropResponseToClient;
            manager.OnSendEquipmentResponse -= SendEquipmentResponseToClient;
            manager.OnSendEquipmentResponse += SendEquipmentResponseToClient;
            manager.OnSendSocketResponse -= SendSocketResponseToClient;
            manager.OnSendSocketResponse += SendSocketResponseToClient;
            manager.OnSendWealthResponse -= SendWealthResponseToClient;
            manager.OnSendWealthResponse += SendWealthResponseToClient;
            manager.OnSendMerchantResponse -= SendMerchantResponseToClient;
            manager.OnSendMerchantResponse += SendMerchantResponseToClient;
            manager.OnSendCraftingResponse -= SendCraftingResponseToClient;
            manager.OnSendCraftingResponse += SendCraftingResponseToClient;
            manager.OnSendTransferResponse -= SendTransferResponseToClient;
            manager.OnSendTransferResponse += SendTransferResponseToClient;
            manager.OnSendPickupResponse -= SendPickupResponseToClient;
            manager.OnSendPickupResponse += SendPickupResponseToClient;
            manager.OnSendCombineResponse -= SendCombineResponseToClient;
            manager.OnSendCombineResponse += SendCombineResponseToClient;
            manager.OnSendContentSplitResponse -= SendContentSplitResponseToClient;
            manager.OnSendContentSplitResponse += SendContentSplitResponseToClient;

            manager.OnBroadcastItemAdded -= BroadcastItemAddedToClients;
            manager.OnBroadcastItemAdded += BroadcastItemAddedToClients;
            manager.OnBroadcastItemRemoved -= BroadcastItemRemovedToClients;
            manager.OnBroadcastItemRemoved += BroadcastItemRemovedToClients;
            manager.OnBroadcastItemDropped -= BroadcastItemDroppedToClients;
            manager.OnBroadcastItemDropped += BroadcastItemDroppedToClients;
            manager.OnBroadcastDroppedItemRemoved -= BroadcastDroppedItemRemovedToClients;
            manager.OnBroadcastDroppedItemRemoved += BroadcastDroppedItemRemovedToClients;
            manager.OnBroadcastItemMoved -= BroadcastItemMovedToClients;
            manager.OnBroadcastItemMoved += BroadcastItemMovedToClients;
            manager.OnBroadcastItemSplit -= BroadcastItemSplitToClients;
            manager.OnBroadcastItemSplit += BroadcastItemSplitToClients;
            manager.OnBroadcastItemUsed -= BroadcastItemUsedToClients;
            manager.OnBroadcastItemUsed += BroadcastItemUsedToClients;
            manager.OnBroadcastItemEquipped -= BroadcastItemEquippedToClients;
            manager.OnBroadcastItemEquipped += BroadcastItemEquippedToClients;
            manager.OnBroadcastItemUnequipped -= BroadcastItemUnequippedToClients;
            manager.OnBroadcastItemUnequipped += BroadcastItemUnequippedToClients;
            manager.OnBroadcastSocketChange -= BroadcastSocketChangeToClients;
            manager.OnBroadcastSocketChange += BroadcastSocketChangeToClients;
            manager.OnBroadcastWealthChange -= BroadcastWealthChangeToClients;
            manager.OnBroadcastWealthChange += BroadcastWealthChangeToClients;
            manager.OnBroadcastPropertyChange -= BroadcastPropertyChangeToClients;
            manager.OnBroadcastPropertyChange += BroadcastPropertyChangeToClients;
            manager.OnBroadcastFullSnapshot -= BroadcastSnapshotToClients;
            manager.OnBroadcastFullSnapshot += BroadcastSnapshotToClients;
            manager.OnBroadcastDelta -= BroadcastDeltaToClients;
            manager.OnBroadcastDelta += BroadcastDeltaToClients;
            manager.OnBroadcastPickupState -= BroadcastPickupStateToClients;
            manager.OnBroadcastPickupState += BroadcastPickupStateToClients;
            manager.OnSendSnapshotToClient -= SendSnapshotToClient;
            manager.OnSendSnapshotToClient += SendSnapshotToClient;
            manager.OnSendPickupStateSnapshotToClient -= SendPickupStateSnapshotToClient;
            manager.OnSendPickupStateSnapshotToClient += SendPickupStateSnapshotToClient;

            NetworkManager nm = ActiveManager;
            bool isServer = nm != null && nm.isServer;
            if (!m_ManagerInitialized || isServer != m_LastServer)
            {
                manager.IsServer = isServer;
                m_ManagerInitialized = true;
                m_LastServer = isServer;
            }
        }

        private void UnwireInventoryManager()
        {
            NetworkInventoryManager manager = GetInventoryManager();
            if (manager == null) return;

            manager.OnSendContentAddRequest -= SendContentAddRequestToServer;
            manager.OnSendContentRemoveRequest -= SendContentRemoveRequestToServer;
            manager.OnSendContentMoveRequest -= SendContentMoveRequestToServer;
            manager.OnSendContentUseRequest -= SendContentUseRequestToServer;
            manager.OnSendContentDropRequest -= SendContentDropRequestToServer;
            manager.OnSendEquipmentRequest -= SendEquipmentRequestToServer;
            manager.OnSendSocketRequest -= SendSocketRequestToServer;
            manager.OnSendWealthRequest -= SendWealthRequestToServer;
            manager.OnSendMerchantRequest -= SendMerchantRequestToServer;
            manager.OnSendCraftingRequest -= SendCraftingRequestToServer;
            manager.OnSendTransferRequest -= SendTransferRequestToServer;
            manager.OnSendPickupRequest -= SendPickupRequestToServer;
            manager.OnSendCombineRequest -= SendCombineRequestToServer;
            manager.OnSendContentSplitRequest -= SendContentSplitRequestToServer;
            manager.OnSendResyncRequest -= SendResyncRequestToServer;

            manager.OnSendContentAddResponse -= SendContentAddResponseToClient;
            manager.OnSendContentRemoveResponse -= SendContentRemoveResponseToClient;
            manager.OnSendContentMoveResponse -= SendContentMoveResponseToClient;
            manager.OnSendContentUseResponse -= SendContentUseResponseToClient;
            manager.OnSendContentDropResponse -= SendContentDropResponseToClient;
            manager.OnSendEquipmentResponse -= SendEquipmentResponseToClient;
            manager.OnSendSocketResponse -= SendSocketResponseToClient;
            manager.OnSendWealthResponse -= SendWealthResponseToClient;
            manager.OnSendMerchantResponse -= SendMerchantResponseToClient;
            manager.OnSendCraftingResponse -= SendCraftingResponseToClient;
            manager.OnSendTransferResponse -= SendTransferResponseToClient;
            manager.OnSendPickupResponse -= SendPickupResponseToClient;
            manager.OnSendCombineResponse -= SendCombineResponseToClient;
            manager.OnSendContentSplitResponse -= SendContentSplitResponseToClient;

            manager.OnBroadcastItemAdded -= BroadcastItemAddedToClients;
            manager.OnBroadcastItemRemoved -= BroadcastItemRemovedToClients;
            manager.OnBroadcastItemDropped -= BroadcastItemDroppedToClients;
            manager.OnBroadcastDroppedItemRemoved -= BroadcastDroppedItemRemovedToClients;
            manager.OnBroadcastItemMoved -= BroadcastItemMovedToClients;
            manager.OnBroadcastItemSplit -= BroadcastItemSplitToClients;
            manager.OnBroadcastItemUsed -= BroadcastItemUsedToClients;
            manager.OnBroadcastItemEquipped -= BroadcastItemEquippedToClients;
            manager.OnBroadcastItemUnequipped -= BroadcastItemUnequippedToClients;
            manager.OnBroadcastSocketChange -= BroadcastSocketChangeToClients;
            manager.OnBroadcastWealthChange -= BroadcastWealthChangeToClients;
            manager.OnBroadcastPropertyChange -= BroadcastPropertyChangeToClients;
            manager.OnBroadcastFullSnapshot -= BroadcastSnapshotToClients;
            manager.OnBroadcastDelta -= BroadcastDeltaToClients;
            manager.OnBroadcastPickupState -= BroadcastPickupStateToClients;
            manager.OnSendSnapshotToClient -= SendSnapshotToClient;
            manager.OnSendPickupStateSnapshotToClient -= SendPickupStateSnapshotToClient;

            m_ManagerInitialized = false;
        }

        private void RefreshControllerRegistry(bool force)
        {
            NetworkInventoryManager manager = GetInventoryManager();
            if (manager == null) return;

            PruneControllerRegistry(manager);
            RefreshRuntimePickupRegistry(manager);
            if (!m_AutoRegisterSceneControllers && !force) return;

            NetworkInventoryController[] controllers = FindObjectsByType<NetworkInventoryController>(
                FindObjectsInactive.Exclude,
                FindObjectsSortMode.None);

            for (int i = 0; i < controllers.Length; i++)
            {
                RegisterController(manager, controllers[i]);
            }

            if (!m_AutoAddControllersToBags) return;

            Bag[] bags = FindObjectsByType<Bag>(
                FindObjectsInactive.Exclude,
                FindObjectsSortMode.None);

            for (int i = 0; i < bags.Length; i++)
            {
                Bag bag = bags[i];
                if (bag == null) continue;

                NetworkInventoryController controller = bag.GetComponent<NetworkInventoryController>();
                if (controller == null)
                {
                    controller = bag.gameObject.AddComponent<NetworkInventoryController>();
                }

                RegisterController(manager, controller);
            }
        }

        private void RegisterController(NetworkInventoryManager manager, NetworkInventoryController controller)
        {
            if (manager == null || controller == null) return;

            NetworkCharacter networkCharacter = controller.GetComponent<NetworkCharacter>();
            bool hasNetworkCharacter = networkCharacter != null;
            if (hasNetworkCharacter)
            {
                if (networkCharacter.NetworkId == 0) return;
                if (networkCharacter.Role == NetworkCharacter.NetworkRole.None) return;
            }

            NetworkManager nm = ActiveManager;
            bool isServer = hasNetworkCharacter
                ? networkCharacter.IsServerInstance
                : nm != null && nm.isServer;
            bool isLocalClient = hasNetworkCharacter &&
                networkCharacter.IsOwnerInstance &&
                networkCharacter.Role == NetworkCharacter.NetworkRole.LocalClient;

            uint networkId = hasNetworkCharacter ? networkCharacter.NetworkId : controller.NetworkId;
            if (networkId == 0) return;
            if (m_RegisteredControllers.TryGetValue(networkId, out NetworkInventoryController existing))
            {
                if (existing == controller)
                {
                    if (controller.IsServer != isServer || controller.IsLocalClient != isLocalClient)
                    {
                        controller.Initialize(isServer, isLocalClient);
                    }

                    return;
                }

                manager.UnregisterController(networkId);
            }

            controller.Initialize(isServer, isLocalClient);
            m_RegisteredControllers[networkId] = controller;
            manager.RegisterController(networkId, controller);

            if (manager.IsServer)
            {
                manager.BroadcastFullSnapshot(controller.GetFullSnapshot());
            }
        }

        private void PruneControllerRegistry(NetworkInventoryManager manager)
        {
            m_RemoveBuffer.Clear();

            foreach (KeyValuePair<uint, NetworkInventoryController> pair in m_RegisteredControllers)
            {
                NetworkInventoryController controller = pair.Value;
                NetworkCharacter networkCharacter = controller != null ? controller.GetComponent<NetworkCharacter>() : null;
                if (controller == null)
                {
                    m_RemoveBuffer.Add(pair.Key);
                    continue;
                }

                if (networkCharacter == null)
                {
                    if (controller.NetworkId != pair.Key)
                    {
                        m_RemoveBuffer.Add(pair.Key);
                    }

                    continue;
                }

                if (networkCharacter.NetworkId != pair.Key ||
                    networkCharacter.Role == NetworkCharacter.NetworkRole.None)
                {
                    m_RemoveBuffer.Add(pair.Key);
                }
            }

            for (int i = 0; i < m_RemoveBuffer.Count; i++)
            {
                uint networkId = m_RemoveBuffer[i];
                manager.UnregisterController(networkId);
                m_RegisteredControllers.Remove(networkId);
            }
        }

        private void UnregisterAllControllers()
        {
            NetworkInventoryManager manager = GetInventoryManager();
            if (manager != null)
            {
                foreach (uint networkId in m_RegisteredControllers.Keys)
                {
                    manager.UnregisterController(networkId);
                }
            }

            m_RegisteredControllers.Clear();
        }

        private void RefreshRuntimePickupRegistry(NetworkInventoryManager manager)
        {
            NetworkManager nm = ActiveManager;
            if (manager == null || nm == null || !nm.isServer)
            {
                UnregisterAllRuntimePickups(manager);
                return;
            }

            m_RuntimePickupCandidates.Clear();
            m_DuplicateRuntimePickupIds.Clear();

            PurrNetInventoryRuntimePickupIdentityAdapter[] adapters =
                FindObjectsByType<PurrNetInventoryRuntimePickupIdentityAdapter>(
                    FindObjectsInactive.Exclude,
                    FindObjectsSortMode.None);

            for (int i = 0; i < adapters.Length; i++)
            {
                PurrNetInventoryRuntimePickupIdentityAdapter adapter = adapters[i];
                if (adapter == null || !adapter.IsSpawned) continue;

                uint pickupId = adapter.NetworkPickupId;
                if (pickupId == 0) continue;

                NetworkInventoryPickupSource source = adapter.GetComponent<NetworkInventoryPickupSource>();
                if (source == null || !ReferenceEquals(source.RuntimeIdentity, adapter))
                {
                    WarnInvariantOnce(
                        $"runtime-pickup-source:{adapter.GetInstanceID()}",
                        $"Runtime pickup '{adapter.name}' is not paired with a same-object " +
                        "NetworkInventoryPickupSource whose Runtime Identity references the adapter.");
                    continue;
                }

                if (m_RuntimePickupCandidates.TryGetValue(
                        pickupId, out PurrNetInventoryRuntimePickupIdentityAdapter existing) &&
                    existing != adapter)
                {
                    m_DuplicateRuntimePickupIds.Add(pickupId);
                    continue;
                }

                m_RuntimePickupCandidates[pickupId] = adapter;
            }

            m_RuntimePickupRemoveBuffer.Clear();
            foreach (KeyValuePair<uint, PurrNetInventoryRuntimePickupIdentityAdapter> pair in
                     m_RegisteredRuntimePickups)
            {
                bool keep = !m_DuplicateRuntimePickupIds.Contains(pair.Key) &&
                            m_RuntimePickupCandidates.TryGetValue(pair.Key, out var candidate) &&
                            candidate == pair.Value;
                if (keep) continue;

                manager.UnregisterRuntimePickupSource(pair.Key, pair.Value);
                m_RuntimePickupRemoveBuffer.Add(pair.Key);
            }

            for (int i = 0; i < m_RuntimePickupRemoveBuffer.Count; i++)
                m_RegisteredRuntimePickups.Remove(m_RuntimePickupRemoveBuffer[i]);

            foreach (uint duplicateId in m_DuplicateRuntimePickupIds)
            {
                WarnInvariantOnce(
                    $"runtime-pickup-duplicate:{duplicateId}",
                    $"Multiple PurrNet runtime pickups resolved network id {duplicateId}. " +
                    "All pickups with that id are rejected until the collision is removed.");
            }

            foreach (KeyValuePair<uint, PurrNetInventoryRuntimePickupIdentityAdapter> pair in
                     m_RuntimePickupCandidates)
            {
                if (m_DuplicateRuntimePickupIds.Contains(pair.Key)) continue;

                PurrNetInventoryRuntimePickupIdentityAdapter adapter = pair.Value;
                NetworkInventoryPickupSource source = adapter.GetComponent<NetworkInventoryPickupSource>();
                if (source == null) continue;

                if (manager.RegisterRuntimePickupSource(pair.Key, source, adapter))
                    m_RegisteredRuntimePickups[pair.Key] = adapter;
            }
        }

        private void UnregisterAllRuntimePickups(NetworkInventoryManager manager = null)
        {
            manager ??= GetInventoryManager();
            if (manager != null)
            {
                foreach (KeyValuePair<uint, PurrNetInventoryRuntimePickupIdentityAdapter> pair in
                         m_RegisteredRuntimePickups)
                {
                    manager.UnregisterRuntimePickupSource(pair.Key, pair.Value);
                }
            }

            m_RegisteredRuntimePickups.Clear();
            m_RuntimePickupCandidates.Clear();
            m_DuplicateRuntimePickupIds.Clear();
            m_RuntimePickupRemoveBuffer.Clear();
        }

        private void SendContentAddRequestToServer(NetworkContentAddRequest request)
        {
            SendPacketToServer(
                new GC2InventoryContentAddRequestPacket { request = request },
                DispatchContentAddRequestOnServer);
        }

        private void SendContentRemoveRequestToServer(NetworkContentRemoveRequest request)
        {
            SendPacketToServer(
                new GC2InventoryContentRemoveRequestPacket { request = request },
                DispatchContentRemoveRequestOnServer);
        }

        private void SendContentMoveRequestToServer(NetworkContentMoveRequest request)
        {
            SendPacketToServer(
                new GC2InventoryContentMoveRequestPacket { request = request },
                DispatchContentMoveRequestOnServer);
        }

        private void SendContentUseRequestToServer(NetworkContentUseRequest request)
        {
            SendPacketToServer(
                new GC2InventoryContentUseRequestPacket { request = request },
                DispatchContentUseRequestOnServer);
        }

        private void SendContentDropRequestToServer(NetworkContentDropRequest request)
        {
            LogPickupDebug(
                $"send drop request req={request.RequestId} actor={request.ActorNetworkId} targetBag={request.TargetBagNetworkId} runtime={request.RuntimeIdHash} position={request.DropPosition} clientActive={(ActiveManager != null && ActiveManager.isClient)} serverActive={(ActiveManager != null && ActiveManager.isServer)}");
            SendPacketToServer(
                new GC2InventoryContentDropRequestPacket { request = request },
                DispatchContentDropRequestOnServer);
        }

        private void SendEquipmentRequestToServer(NetworkEquipmentRequest request)
        {
            SendPacketToServer(
                new GC2InventoryEquipmentRequestPacket { request = request },
                (sender, packet) => _ = DispatchEquipmentRequestOnServer(sender, packet));
        }

        private void SendSocketRequestToServer(NetworkSocketRequest request)
        {
            SendPacketToServer(
                new GC2InventorySocketRequestPacket { request = request },
                DispatchSocketRequestOnServer);
        }

        private void SendWealthRequestToServer(NetworkWealthRequest request)
        {
            SendPacketToServer(
                new GC2InventoryWealthRequestPacket { request = request },
                DispatchWealthRequestOnServer);
        }

        private void SendMerchantRequestToServer(NetworkMerchantRequest request)
        {
            SendPacketToServer(
                new GC2InventoryMerchantRequestPacket { request = request },
                DispatchMerchantRequestOnServer);
        }

        private void SendCraftingRequestToServer(NetworkCraftingRequest request)
        {
            SendPacketToServer(
                new GC2InventoryCraftingRequestPacket { request = request },
                DispatchCraftingRequestOnServer);
        }

        private void SendTransferRequestToServer(NetworkTransferRequest request)
        {
            SendPacketToServer(
                new GC2InventoryTransferRequestPacket { request = request },
                DispatchTransferRequestOnServer);
        }

        private void SendPickupRequestToServer(NetworkPickupRequest request)
        {
            LogPickupDebug(
                $"send pickup request req={request.RequestId} actor={request.ActorNetworkId} pickerBag={request.PickerBagNetworkId} sourceBag={request.SourceBagNetworkId} runtime={request.RuntimeIdHash} clientActive={(ActiveManager != null && ActiveManager.isClient)} serverActive={(ActiveManager != null && ActiveManager.isServer)}");
            SendPacketToServer(
                new GC2InventoryPickupRequestPacket { request = request },
                DispatchPickupRequestOnServer);
        }

        private void SendCombineRequestToServer(NetworkCombineRequest request)
        {
            SendPacketToServer(
                new GC2InventoryCombineRequestPacket { request = request },
                DispatchCombineRequestOnServer);
        }

        private void SendContentSplitRequestToServer(NetworkContentSplitRequest request)
        {
            SendPacketToServer(
                new GC2InventoryContentSplitRequestPacket { request = request },
                DispatchContentSplitRequestOnServer);
        }

        private void SendResyncRequestToServer(NetworkInventoryResyncRequest request)
        {
            SendPacketToServer(
                new GC2InventoryResyncRequestPacket { request = request },
                DispatchResyncRequestOnServer);
        }

        private void SendContentAddResponseToClient(uint clientNetworkId, NetworkContentAddResponse response)
        {
            SendPacketToClient(clientNetworkId, new GC2InventoryContentAddResponsePacket { response = response });
        }

        private void SendContentRemoveResponseToClient(uint clientNetworkId, NetworkContentRemoveResponse response)
        {
            SendPacketToClient(clientNetworkId, new GC2InventoryContentRemoveResponsePacket { response = response });
        }

        private void SendContentMoveResponseToClient(uint clientNetworkId, NetworkContentMoveResponse response)
        {
            SendPacketToClient(clientNetworkId, new GC2InventoryContentMoveResponsePacket { response = response });
        }

        private void SendContentUseResponseToClient(uint clientNetworkId, NetworkContentUseResponse response)
        {
            SendPacketToClient(clientNetworkId, new GC2InventoryContentUseResponsePacket { response = response });
        }

        private void SendContentDropResponseToClient(uint clientNetworkId, NetworkContentDropResponse response)
        {
            SendPacketToClient(clientNetworkId, new GC2InventoryContentDropResponsePacket { response = response });
        }

        private void SendEquipmentResponseToClient(uint clientNetworkId, NetworkEquipmentResponse response)
        {
            SendPacketToClient(clientNetworkId, new GC2InventoryEquipmentResponsePacket { response = response });
        }

        private void SendSocketResponseToClient(uint clientNetworkId, NetworkSocketResponse response)
        {
            SendPacketToClient(clientNetworkId, new GC2InventorySocketResponsePacket { response = response });
        }

        private void SendWealthResponseToClient(uint clientNetworkId, NetworkWealthResponse response)
        {
            SendPacketToClient(clientNetworkId, new GC2InventoryWealthResponsePacket { response = response });
        }

        private void SendMerchantResponseToClient(uint clientNetworkId, NetworkMerchantResponse response)
        {
            SendPacketToClient(clientNetworkId, new GC2InventoryMerchantResponsePacket { response = response });
        }

        private void SendCraftingResponseToClient(uint clientNetworkId, NetworkCraftingResponse response)
        {
            SendPacketToClient(clientNetworkId, new GC2InventoryCraftingResponsePacket { response = response });
        }

        private void SendTransferResponseToClient(uint clientNetworkId, NetworkTransferResponse response)
        {
            SendPacketToClient(clientNetworkId, new GC2InventoryTransferResponsePacket { response = response });
        }

        private void SendPickupResponseToClient(uint clientNetworkId, NetworkPickupResponse response)
        {
            LogPickupDebug(
                $"send pickup response target={clientNetworkId} req={response.RequestId} authorized={response.Authorized} reason={response.RejectionReason}");
            SendPacketToClient(clientNetworkId, new GC2InventoryPickupResponsePacket { response = response });
        }

        private void SendCombineResponseToClient(uint clientNetworkId, NetworkCombineResponse response)
        {
            SendPacketToClient(clientNetworkId, new GC2InventoryCombineResponsePacket { response = response });
        }

        private void SendContentSplitResponseToClient(uint clientNetworkId, NetworkContentSplitResponse response)
        {
            SendPacketToClient(
                clientNetworkId,
                new GC2InventoryContentSplitResponsePacket { response = response });
        }

        private void BroadcastItemAddedToClients(NetworkItemAddedBroadcast broadcast)
        {
            LogPickupDebug(
                $"broadcast item added bag={broadcast.BagNetworkId} runtime={broadcast.Item.RuntimeIdHash} item={broadcast.Item.ItemIdString} position={broadcast.Position} stack={broadcast.StackCount}");
            BroadcastInventoryPacket(broadcast.BagNetworkId, new GC2InventoryItemAddedBroadcastPacket { broadcast = broadcast });
        }

        private void BroadcastItemRemovedToClients(NetworkItemRemovedBroadcast broadcast)
        {
            BroadcastInventoryPacket(broadcast.BagNetworkId, new GC2InventoryItemRemovedBroadcastPacket { broadcast = broadcast });
        }

        private void BroadcastItemDroppedToClients(NetworkItemDroppedBroadcast broadcast)
        {
            LogPickupDebug(
                $"broadcast item dropped sourceBag={broadcast.SourceBagNetworkId} runtime={broadcast.Item.RuntimeIdHash} item={broadcast.Item.ItemIdString} position={broadcast.Position}");
            BroadcastInventoryPacket(broadcast.SourceBagNetworkId, new GC2InventoryItemDroppedBroadcastPacket { broadcast = broadcast });
        }

        private void BroadcastDroppedItemRemovedToClients(NetworkDroppedItemRemovedBroadcast broadcast)
        {
            LogPickupDebug(
                $"broadcast dropped item removed sourceBag={broadcast.SourceBagNetworkId} runtime={broadcast.RuntimeIdHash} position={broadcast.Position}");
            BroadcastInventoryPacket(broadcast.SourceBagNetworkId, new GC2InventoryDroppedItemRemovedBroadcastPacket { broadcast = broadcast });
        }

        private void BroadcastItemMovedToClients(NetworkItemMovedBroadcast broadcast)
        {
            BroadcastInventoryPacket(broadcast.BagNetworkId, new GC2InventoryItemMovedBroadcastPacket { broadcast = broadcast });
        }

        private void BroadcastItemSplitToClients(NetworkItemSplitBroadcast broadcast)
        {
            BroadcastInventoryPacket(
                broadcast.BagNetworkId,
                new GC2InventoryItemSplitBroadcastPacket { broadcast = broadcast });
        }

        private void BroadcastItemUsedToClients(NetworkItemUsedBroadcast broadcast)
        {
            BroadcastInventoryPacket(broadcast.BagNetworkId, new GC2InventoryItemUsedBroadcastPacket { broadcast = broadcast });
        }

        private void BroadcastItemEquippedToClients(NetworkItemEquippedBroadcast broadcast)
        {
            BroadcastInventoryPacket(broadcast.BagNetworkId, new GC2InventoryItemEquippedBroadcastPacket { broadcast = broadcast });
        }

        private void BroadcastItemUnequippedToClients(NetworkItemUnequippedBroadcast broadcast)
        {
            BroadcastInventoryPacket(broadcast.BagNetworkId, new GC2InventoryItemUnequippedBroadcastPacket { broadcast = broadcast });
        }

        private void BroadcastSocketChangeToClients(NetworkSocketChangeBroadcast broadcast)
        {
            BroadcastInventoryPacket(broadcast.BagNetworkId, new GC2InventorySocketChangeBroadcastPacket { broadcast = broadcast });
        }

        private void BroadcastWealthChangeToClients(NetworkWealthChangeBroadcast broadcast)
        {
            BroadcastInventoryPacket(broadcast.BagNetworkId, new GC2InventoryWealthChangeBroadcastPacket { broadcast = broadcast });
        }

        private void BroadcastPropertyChangeToClients(NetworkPropertyChangeBroadcast broadcast)
        {
            BroadcastInventoryPacket(
                broadcast.BagNetworkId,
                new GC2InventoryPropertyChangeBroadcastPacket { broadcast = broadcast });
        }

        private void BroadcastSnapshotToClients(NetworkInventorySnapshot snapshot)
        {
            BroadcastInventoryPacket(snapshot.BagNetworkId, new GC2InventorySnapshotPacket { snapshot = snapshot });
        }

        private void BroadcastDeltaToClients(NetworkInventoryDelta delta)
        {
            BroadcastInventoryPacket(delta.BagNetworkId, new GC2InventoryDeltaPacket { delta = delta });
        }

        private void BroadcastPickupStateToClients(NetworkPickupStateBroadcast broadcast)
        {
            NetworkManager nm = ActiveManager;
            if (nm == null || !nm.isServer) return;

            // Static pickup consumption is persistent global state. Relevance filtering here would
            // allow an out-of-range client to retain or later reveal an already consumed pickup.
            nm.SendToAll(
                new GC2InventoryPickupStateBroadcastPacket { broadcast = broadcast },
                m_Channel);
        }

        private void SendSnapshotToClient(ulong clientId, NetworkInventorySnapshot snapshot)
        {
            NetworkManager nm = ActiveManager;
            if (nm == null || !nm.isServer) return;
            if (!TryGetPlayerId(nm, clientId, out PlayerID playerId)) return;

            nm.Send(playerId, new GC2InventorySnapshotPacket { snapshot = snapshot }, m_Channel);
        }

        private void SendPickupStateSnapshotToClient(
            ulong clientId, NetworkPickupStateSnapshot snapshot)
        {
            NetworkManager nm = ActiveManager;
            if (nm == null || !nm.isServer) return;
            if (!TryGetPlayerId(nm, clientId, out PlayerID playerId)) return;

            nm.Send(
                playerId,
                new GC2InventoryPickupStateSnapshotPacket { snapshot = snapshot },
                m_Channel);
        }

        private void SendPacketToServer<T>(T packet, System.Action<PlayerID, T> hostDispatch)
            where T : struct, IPackedAuto
        {
            NetworkManager nm = ActiveManager;
            if (nm == null || !nm.isClient)
            {
                WarnRouteOnce<T>("no active PurrNet client route");
                return;
            }

            if (nm.isServer)
            {
                if (nm.isLocalPlayerReady) hostDispatch(nm.localPlayer, packet);
                else WarnRouteOnce<T>("the host local player is not ready");
                return;
            }

            nm.SendToServer(packet, m_Channel);
        }

        private void EnsureReliableOrderedChannel(bool logCorrection)
        {
            if (m_Channel == Channel.ReliableOrdered) return;
            if (logCorrection)
            {
                Debug.LogWarning(
                    $"[PurrNetInventoryTransportBridge] Inventory protocol v3 requires " +
                    $"{Channel.ReliableOrdered}; overriding configured channel {m_Channel}.",
                    this);
            }

            m_Channel = Channel.ReliableOrdered;
        }

        private void WarnRouteOnce<T>(string reason)
        {
            string key = $"{typeof(T).FullName}:{reason}";
            if (!m_RouteWarnings.Add(key)) return;
            Debug.LogWarning(
                $"[PurrNetInventoryTransportBridge] Dropped {typeof(T).Name}: {reason}.",
                this);
        }

        private void WarnInvariantOnce(string key, string message)
        {
            if (!m_RouteWarnings.Add($"invariant:{key}")) return;
            Debug.LogWarning($"[PurrNetInventoryTransportBridge] {message}", this);
        }

        private void SendPacketToClient<T>(uint clientNetworkId, T packet)
            where T : struct, IPackedAuto
        {
            NetworkManager nm = ActiveManager;
            if (nm == null || !nm.isServer) return;
            if (!TryGetPlayerId(nm, clientNetworkId, out PlayerID playerId)) return;

            nm.Send(playerId, packet, m_Channel);
        }

        private void BroadcastInventoryPacket<T>(uint bagNetworkId, T packet)
            where T : struct, IPackedAuto
        {
            NetworkManager nm = ActiveManager;
            if (nm == null || !nm.isServer) return;

            if (!ShouldFilterBySessionProfile())
            {
                nm.SendToAll(packet, m_Channel);
                return;
            }

            IReadOnlyList<PlayerID> players = nm.players;
            if (players == null || players.Count == 0) return;

            for (int i = 0; i < players.Count; i++)
            {
                PlayerID playerId = players[i];
                uint clientId = PlayerIdToClientId(playerId);
                if (!NetworkTransportBridge.IsValidClientId(clientId)) continue;
                if (!ShouldSendInventoryToClient(clientId, bagNetworkId)) continue;

                nm.Send(playerId, packet, m_Channel);
            }
        }

        private bool ShouldFilterBySessionProfile()
        {
            if (!m_UseSessionProfileRelevance) return false;

            NetworkSessionProfile profile = CoreBridge != null ? CoreBridge.GlobalSessionProfile : null;
            return profile != null &&
                   (profile.enableDistanceCulling || profile.requireObserverCharacterForRelevance);
        }

        private bool ShouldSendInventoryToClient(uint targetClientId, uint bagNetworkId)
        {
            PurrNetTransportBridge bridge = CoreBridge;
            NetworkSessionProfile profile = bridge != null ? bridge.GlobalSessionProfile : null;
            if (profile == null) return true;

            if (bridge != null &&
                bridge.TryGetCharacterOwner(bagNetworkId, out uint ownerClientId) &&
                ownerClientId == targetClientId)
            {
                return true;
            }

            if (!TryGetBagPosition(bagNetworkId, out Vector3 bagPosition))
            {
                return !profile.requireObserverCharacterForRelevance;
            }

            if (!TryGetObserverPosition(targetClientId, out Vector3 observerPosition))
            {
                return !profile.requireObserverCharacterForRelevance;
            }

            float distance = Vector3.Distance(observerPosition, bagPosition);
            return !profile.enableDistanceCulling || distance <= profile.cullDistance;
        }

        private bool TryGetBagPosition(uint bagNetworkId, out Vector3 position)
        {
            position = Vector3.zero;

            PurrNetTransportBridge bridge = CoreBridge;
            Character character = bridge != null ? bridge.ResolveCharacter(bagNetworkId) : null;
            if (character != null)
            {
                position = character.transform.position;
                return true;
            }

            if (m_RegisteredControllers.TryGetValue(bagNetworkId, out NetworkInventoryController controller) &&
                controller != null)
            {
                position = controller.transform.position;
                return true;
            }

            return false;
        }

        private bool TryGetObserverPosition(uint clientId, out Vector3 position)
        {
            position = Vector3.zero;

            PurrNetTransportBridge bridge = CoreBridge;
            if (bridge == null) return false;
            if (!bridge.TryGetRepresentativeCharacterId(clientId, out uint observerCharacterId)) return false;

            Character observer = bridge.ResolveCharacter(observerCharacterId);
            if (observer == null) return false;

            position = observer.transform.position;
            return true;
        }

        private void HandleContentAddRequestServer(PlayerID senderPlayer, GC2InventoryContentAddRequestPacket data, bool asServer)
        {
            if (!asServer) return;
            DispatchContentAddRequestOnServer(senderPlayer, data);
        }

        private void DispatchContentAddRequestOnServer(PlayerID senderPlayer, GC2InventoryContentAddRequestPacket data)
        {
            GetInventoryManager()?.ReceiveContentAddRequest(data.request, senderPlayer.id);
        }

        private void HandleContentRemoveRequestServer(PlayerID senderPlayer, GC2InventoryContentRemoveRequestPacket data, bool asServer)
        {
            if (!asServer) return;
            DispatchContentRemoveRequestOnServer(senderPlayer, data);
        }

        private void DispatchContentRemoveRequestOnServer(PlayerID senderPlayer, GC2InventoryContentRemoveRequestPacket data)
        {
            GetInventoryManager()?.ReceiveContentRemoveRequest(data.request, senderPlayer.id);
        }

        private void HandleContentMoveRequestServer(PlayerID senderPlayer, GC2InventoryContentMoveRequestPacket data, bool asServer)
        {
            if (!asServer) return;
            DispatchContentMoveRequestOnServer(senderPlayer, data);
        }

        private void DispatchContentMoveRequestOnServer(PlayerID senderPlayer, GC2InventoryContentMoveRequestPacket data)
        {
            GetInventoryManager()?.ReceiveContentMoveRequest(data.request, senderPlayer.id);
        }

        private void HandleContentUseRequestServer(PlayerID senderPlayer, GC2InventoryContentUseRequestPacket data, bool asServer)
        {
            if (!asServer) return;
            DispatchContentUseRequestOnServer(senderPlayer, data);
        }

        private void DispatchContentUseRequestOnServer(PlayerID senderPlayer, GC2InventoryContentUseRequestPacket data)
        {
            GetInventoryManager()?.ReceiveContentUseRequest(data.request, senderPlayer.id);
        }

        private void HandleContentDropRequestServer(PlayerID senderPlayer, GC2InventoryContentDropRequestPacket data, bool asServer)
        {
            if (!asServer) return;
            DispatchContentDropRequestOnServer(senderPlayer, data);
        }

        private void DispatchContentDropRequestOnServer(PlayerID senderPlayer, GC2InventoryContentDropRequestPacket data)
        {
            LogPickupDebug(
                $"dispatch drop request sender={senderPlayer.id} req={data.request.RequestId} actor={data.request.ActorNetworkId} targetBag={data.request.TargetBagNetworkId} runtime={data.request.RuntimeIdHash}");
            GetInventoryManager()?.ReceiveContentDropRequest(data.request, senderPlayer.id);
        }

        private async void HandleEquipmentRequestServer(PlayerID senderPlayer, GC2InventoryEquipmentRequestPacket data, bool asServer)
        {
            if (!asServer) return;
            await DispatchEquipmentRequestOnServer(senderPlayer, data);
        }

        private Task DispatchEquipmentRequestOnServer(PlayerID senderPlayer, GC2InventoryEquipmentRequestPacket data)
        {
            NetworkInventoryManager manager = GetInventoryManager();
            return manager != null
                ? manager.ReceiveEquipmentRequest(data.request, senderPlayer.id)
                : Task.CompletedTask;
        }

        private void HandleSocketRequestServer(PlayerID senderPlayer, GC2InventorySocketRequestPacket data, bool asServer)
        {
            if (!asServer) return;
            DispatchSocketRequestOnServer(senderPlayer, data);
        }

        private void DispatchSocketRequestOnServer(PlayerID senderPlayer, GC2InventorySocketRequestPacket data)
        {
            GetInventoryManager()?.ReceiveSocketRequest(data.request, senderPlayer.id);
        }

        private void HandleWealthRequestServer(PlayerID senderPlayer, GC2InventoryWealthRequestPacket data, bool asServer)
        {
            if (!asServer) return;
            DispatchWealthRequestOnServer(senderPlayer, data);
        }

        private void DispatchWealthRequestOnServer(PlayerID senderPlayer, GC2InventoryWealthRequestPacket data)
        {
            GetInventoryManager()?.ReceiveWealthRequest(data.request, senderPlayer.id);
        }

        private void HandleMerchantRequestServer(PlayerID senderPlayer, GC2InventoryMerchantRequestPacket data, bool asServer)
        {
            if (!asServer) return;
            DispatchMerchantRequestOnServer(senderPlayer, data);
        }

        private void DispatchMerchantRequestOnServer(PlayerID senderPlayer, GC2InventoryMerchantRequestPacket data)
        {
            GetInventoryManager()?.ReceiveMerchantRequest(data.request, senderPlayer.id);
        }

        private void HandleCraftingRequestServer(PlayerID senderPlayer, GC2InventoryCraftingRequestPacket data, bool asServer)
        {
            if (!asServer) return;
            DispatchCraftingRequestOnServer(senderPlayer, data);
        }

        private void DispatchCraftingRequestOnServer(PlayerID senderPlayer, GC2InventoryCraftingRequestPacket data)
        {
            GetInventoryManager()?.ReceiveCraftingRequest(data.request, senderPlayer.id);
        }

        private void HandleTransferRequestServer(PlayerID senderPlayer, GC2InventoryTransferRequestPacket data, bool asServer)
        {
            if (!asServer) return;
            DispatchTransferRequestOnServer(senderPlayer, data);
        }

        private void DispatchTransferRequestOnServer(PlayerID senderPlayer, GC2InventoryTransferRequestPacket data)
        {
            GetInventoryManager()?.ReceiveTransferRequest(data.request, senderPlayer.id);
        }

        private void HandlePickupRequestServer(PlayerID senderPlayer, GC2InventoryPickupRequestPacket data, bool asServer)
        {
            if (!asServer) return;
            DispatchPickupRequestOnServer(senderPlayer, data);
        }

        private void DispatchPickupRequestOnServer(PlayerID senderPlayer, GC2InventoryPickupRequestPacket data)
        {
            LogPickupDebug(
                $"dispatch pickup request sender={senderPlayer.id} req={data.request.RequestId} actor={data.request.ActorNetworkId} pickerBag={data.request.PickerBagNetworkId} sourceBag={data.request.SourceBagNetworkId} runtime={data.request.RuntimeIdHash}");
            NetworkInventoryManager manager = GetInventoryManager();
            if (manager == null) return;

            // A runtime pickup may have spawned after the periodic registry scan. Refresh before
            // validation so the first legitimate interaction is never rejected due to scan order.
            RefreshRuntimePickupRegistry(manager);
            manager.ReceivePickupRequest(data.request, senderPlayer.id);
        }

        private void HandleCombineRequestServer(PlayerID senderPlayer, GC2InventoryCombineRequestPacket data, bool asServer)
        {
            if (!asServer) return;
            DispatchCombineRequestOnServer(senderPlayer, data);
        }

        private void DispatchCombineRequestOnServer(PlayerID senderPlayer, GC2InventoryCombineRequestPacket data)
        {
            GetInventoryManager()?.ReceiveCombineRequest(data.request, senderPlayer.id);
        }

        private void HandleContentSplitRequestServer(
            PlayerID senderPlayer, GC2InventoryContentSplitRequestPacket data, bool asServer)
        {
            if (!asServer) return;
            DispatchContentSplitRequestOnServer(senderPlayer, data);
        }

        private void DispatchContentSplitRequestOnServer(
            PlayerID senderPlayer, GC2InventoryContentSplitRequestPacket data)
        {
            GetInventoryManager()?.ReceiveContentSplitRequest(data.request, senderPlayer.id);
        }

        private void HandleResyncRequestServer(
            PlayerID senderPlayer, GC2InventoryResyncRequestPacket data, bool asServer)
        {
            if (!asServer) return;
            DispatchResyncRequestOnServer(senderPlayer, data);
        }

        private void DispatchResyncRequestOnServer(
            PlayerID senderPlayer, GC2InventoryResyncRequestPacket data)
        {
            GetInventoryManager()?.ReceiveResyncRequest(data.request, senderPlayer.id);
        }

        private void HandleContentAddResponseClient(PlayerID senderPlayer, GC2InventoryContentAddResponsePacket data, bool asServer)
        {
            if (asServer) return;
            GetInventoryManager()?.ReceiveContentAddResponse(data.response, data.response.ActorNetworkId);
        }

        private void HandleContentRemoveResponseClient(PlayerID senderPlayer, GC2InventoryContentRemoveResponsePacket data, bool asServer)
        {
            if (asServer) return;
            GetInventoryManager()?.ReceiveContentRemoveResponse(data.response, data.response.ActorNetworkId);
        }

        private void HandleContentMoveResponseClient(PlayerID senderPlayer, GC2InventoryContentMoveResponsePacket data, bool asServer)
        {
            if (asServer) return;
            GetInventoryManager()?.ReceiveContentMoveResponse(data.response, data.response.ActorNetworkId);
        }

        private void HandleContentUseResponseClient(PlayerID senderPlayer, GC2InventoryContentUseResponsePacket data, bool asServer)
        {
            if (asServer) return;
            GetInventoryManager()?.ReceiveContentUseResponse(data.response, data.response.ActorNetworkId);
        }

        private void HandleContentDropResponseClient(PlayerID senderPlayer, GC2InventoryContentDropResponsePacket data, bool asServer)
        {
            if (asServer) return;
            LogPickupDebug(
                $"client received drop response sender={senderPlayer.id} req={data.response.RequestId} authorized={data.response.Authorized} reason={data.response.RejectionReason}");
            GetInventoryManager()?.ReceiveContentDropResponse(data.response, data.response.ActorNetworkId);
        }

        private void HandleEquipmentResponseClient(PlayerID senderPlayer, GC2InventoryEquipmentResponsePacket data, bool asServer)
        {
            if (asServer) return;
            GetInventoryManager()?.ReceiveEquipmentResponse(data.response, data.response.ActorNetworkId);
        }

        private void HandleSocketResponseClient(PlayerID senderPlayer, GC2InventorySocketResponsePacket data, bool asServer)
        {
            if (asServer) return;
            GetInventoryManager()?.ReceiveSocketResponse(data.response, data.response.ActorNetworkId);
        }

        private void HandleWealthResponseClient(PlayerID senderPlayer, GC2InventoryWealthResponsePacket data, bool asServer)
        {
            if (asServer) return;
            GetInventoryManager()?.ReceiveWealthResponse(data.response, data.response.ActorNetworkId);
        }

        private void HandleMerchantResponseClient(PlayerID senderPlayer, GC2InventoryMerchantResponsePacket data, bool asServer)
        {
            if (asServer) return;
            GetInventoryManager()?.ReceiveMerchantResponse(data.response, data.response.ActorNetworkId);
        }

        private void HandleCraftingResponseClient(PlayerID senderPlayer, GC2InventoryCraftingResponsePacket data, bool asServer)
        {
            if (asServer) return;
            GetInventoryManager()?.ReceiveCraftingResponse(data.response, data.response.ActorNetworkId);
        }

        private void HandleTransferResponseClient(PlayerID senderPlayer, GC2InventoryTransferResponsePacket data, bool asServer)
        {
            if (asServer) return;
            GetInventoryManager()?.ReceiveTransferResponse(data.response, data.response.ActorNetworkId);
        }

        private void HandlePickupResponseClient(PlayerID senderPlayer, GC2InventoryPickupResponsePacket data, bool asServer)
        {
            if (asServer) return;
            LogPickupDebug(
                $"client received pickup response sender={senderPlayer.id} req={data.response.RequestId} authorized={data.response.Authorized} reason={data.response.RejectionReason}");
            GetInventoryManager()?.ReceivePickupResponse(data.response, data.response.ActorNetworkId);
        }

        private void HandleCombineResponseClient(PlayerID senderPlayer, GC2InventoryCombineResponsePacket data, bool asServer)
        {
            if (asServer) return;
            GetInventoryManager()?.ReceiveCombineResponse(data.response, data.response.ActorNetworkId);
        }

        private void HandleContentSplitResponseClient(
            PlayerID senderPlayer, GC2InventoryContentSplitResponsePacket data, bool asServer)
        {
            if (asServer) return;
            GetInventoryManager()?.ReceiveContentSplitResponse(data.response, data.response.ActorNetworkId);
        }

        private void HandleItemAddedBroadcastClient(PlayerID senderPlayer, GC2InventoryItemAddedBroadcastPacket data, bool asServer)
        {
            if (asServer) return;
            LogPickupDebug(
                $"client received item added broadcast sender={senderPlayer.id} bag={data.broadcast.BagNetworkId} runtime={data.broadcast.Item.RuntimeIdHash} item={data.broadcast.Item.ItemIdString} position={data.broadcast.Position}");
            GetInventoryManager()?.ReceiveItemAddedBroadcast(data.broadcast);
        }

        private void HandleItemRemovedBroadcastClient(PlayerID senderPlayer, GC2InventoryItemRemovedBroadcastPacket data, bool asServer)
        {
            if (asServer) return;
            GetInventoryManager()?.ReceiveItemRemovedBroadcast(data.broadcast);
        }

        private void HandleItemDroppedBroadcastClient(PlayerID senderPlayer, GC2InventoryItemDroppedBroadcastPacket data, bool asServer)
        {
            if (asServer) return;
            LogPickupDebug(
                $"client received dropped broadcast sender={senderPlayer.id} sourceBag={data.broadcast.SourceBagNetworkId} runtime={data.broadcast.Item.RuntimeIdHash} item={data.broadcast.Item.ItemIdString}");
            GetInventoryManager()?.ReceiveItemDroppedBroadcast(data.broadcast);
        }

        private void HandleDroppedItemRemovedBroadcastClient(PlayerID senderPlayer, GC2InventoryDroppedItemRemovedBroadcastPacket data, bool asServer)
        {
            if (asServer) return;
            LogPickupDebug(
                $"client received dropped remove broadcast sender={senderPlayer.id} sourceBag={data.broadcast.SourceBagNetworkId} runtime={data.broadcast.RuntimeIdHash}");
            GetInventoryManager()?.ReceiveDroppedItemRemovedBroadcast(data.broadcast);
        }

        private void HandleItemMovedBroadcastClient(PlayerID senderPlayer, GC2InventoryItemMovedBroadcastPacket data, bool asServer)
        {
            if (asServer) return;
            GetInventoryManager()?.ReceiveItemMovedBroadcast(data.broadcast);
        }

        private void HandleItemSplitBroadcastClient(
            PlayerID senderPlayer, GC2InventoryItemSplitBroadcastPacket data, bool asServer)
        {
            if (asServer) return;
            GetInventoryManager()?.ReceiveItemSplitBroadcast(data.broadcast);
        }

        private void HandleItemUsedBroadcastClient(PlayerID senderPlayer, GC2InventoryItemUsedBroadcastPacket data, bool asServer)
        {
            if (asServer) return;
            GetInventoryManager()?.ReceiveItemUsedBroadcast(data.broadcast);
        }

        private void HandleItemEquippedBroadcastClient(PlayerID senderPlayer, GC2InventoryItemEquippedBroadcastPacket data, bool asServer)
        {
            if (asServer) return;
            GetInventoryManager()?.ReceiveItemEquippedBroadcast(data.broadcast);
        }

        private void HandleItemUnequippedBroadcastClient(PlayerID senderPlayer, GC2InventoryItemUnequippedBroadcastPacket data, bool asServer)
        {
            if (asServer) return;
            GetInventoryManager()?.ReceiveItemUnequippedBroadcast(data.broadcast);
        }

        private void HandleSocketChangeBroadcastClient(PlayerID senderPlayer, GC2InventorySocketChangeBroadcastPacket data, bool asServer)
        {
            if (asServer) return;
            GetInventoryManager()?.ReceiveSocketChangeBroadcast(data.broadcast);
        }

        private void HandleWealthChangeBroadcastClient(PlayerID senderPlayer, GC2InventoryWealthChangeBroadcastPacket data, bool asServer)
        {
            if (asServer) return;
            GetInventoryManager()?.ReceiveWealthChangeBroadcast(data.broadcast);
        }

        private void HandlePropertyChangeBroadcastClient(PlayerID senderPlayer, GC2InventoryPropertyChangeBroadcastPacket data, bool asServer)
        {
            if (asServer) return;
            GetInventoryManager()?.ReceivePropertyChangeBroadcast(data.broadcast);
        }

        private void HandleSnapshotClient(PlayerID senderPlayer, GC2InventorySnapshotPacket data, bool asServer)
        {
            if (asServer) return;
            GetInventoryManager()?.ReceiveFullSnapshot(data.snapshot);
        }

        private void HandleDeltaClient(PlayerID senderPlayer, GC2InventoryDeltaPacket data, bool asServer)
        {
            if (asServer) return;
            GetInventoryManager()?.ReceiveDelta(data.delta);
        }

        private void HandlePickupStateBroadcastClient(
            PlayerID senderPlayer, GC2InventoryPickupStateBroadcastPacket data, bool asServer)
        {
            if (asServer) return;
            GetInventoryManager()?.ReceivePickupStateBroadcast(data.broadcast);
        }

        private void HandlePickupStateSnapshotClient(
            PlayerID senderPlayer, GC2InventoryPickupStateSnapshotPacket data, bool asServer)
        {
            if (asServer) return;
            GetInventoryManager()?.ReceivePickupStateSnapshot(data.snapshot);
        }

        private static NetworkInventoryManager GetInventoryManager()
        {
            return NetworkInventoryManager.Instance != null
                ? NetworkInventoryManager.Instance
                : FindFirstObjectByType<NetworkInventoryManager>();
        }

        private static void LogPickupDebug(string message)
        {
            NetworkInventoryManager manager = GetInventoryManager();
            if (manager == null || !manager.DiagnosticsEnabled) return;
            Debug.Log($"[NetworkInventoryPickupDebug][PurrNetInventoryBridge] {message}");
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
            if (players == null) return false;

            for (int i = 0; i < players.Count; i++)
            {
                PlayerID pid = players[i];
                if (PlayerIdToClientId(pid) == clientId)
                {
                    playerId = pid;
                    return true;
                }
            }

            return false;
        }

        private static bool TryGetPlayerId(NetworkManager manager, ulong rawClientId, out PlayerID playerId)
        {
            playerId = default;
            if (!NetworkTransportBridge.TryConvertSenderClientId(rawClientId, out uint clientId)) return false;
            return TryGetPlayerId(manager, clientId, out playerId);
        }
    }
}
#endif
