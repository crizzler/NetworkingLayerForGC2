#if GC2_INVENTORY
using Arawn.GameCreator2.Networking.Inventory;
using PurrNet.Packing;

namespace Arawn.GameCreator2.Networking.Inventory.Transport.PurrNet
{
    public struct GC2InventoryContentAddRequestPacket : IPackedAuto
    {
        public NetworkContentAddRequest request;
    }

    public struct GC2InventoryContentAddResponsePacket : IPackedAuto
    {
        public NetworkContentAddResponse response;
    }

    public struct GC2InventoryContentRemoveRequestPacket : IPackedAuto
    {
        public NetworkContentRemoveRequest request;
    }

    public struct GC2InventoryContentRemoveResponsePacket : IPackedAuto
    {
        public NetworkContentRemoveResponse response;
    }

    public struct GC2InventoryContentMoveRequestPacket : IPackedAuto
    {
        public NetworkContentMoveRequest request;
    }

    public struct GC2InventoryContentMoveResponsePacket : IPackedAuto
    {
        public NetworkContentMoveResponse response;
    }

    public struct GC2InventoryContentUseRequestPacket : IPackedAuto
    {
        public NetworkContentUseRequest request;
    }

    public struct GC2InventoryContentUseResponsePacket : IPackedAuto
    {
        public NetworkContentUseResponse response;
    }

    public struct GC2InventoryContentDropRequestPacket : IPackedAuto
    {
        public NetworkContentDropRequest request;
    }

    public struct GC2InventoryContentDropResponsePacket : IPackedAuto
    {
        public NetworkContentDropResponse response;
    }

    public struct GC2InventoryEquipmentRequestPacket : IPackedAuto
    {
        public NetworkEquipmentRequest request;
    }

    public struct GC2InventoryEquipmentResponsePacket : IPackedAuto
    {
        public NetworkEquipmentResponse response;
    }

    public struct GC2InventorySocketRequestPacket : IPackedAuto
    {
        public NetworkSocketRequest request;
    }

    public struct GC2InventorySocketResponsePacket : IPackedAuto
    {
        public NetworkSocketResponse response;
    }

    public struct GC2InventoryWealthRequestPacket : IPackedAuto
    {
        public NetworkWealthRequest request;
    }

    public struct GC2InventoryWealthResponsePacket : IPackedAuto
    {
        public NetworkWealthResponse response;
    }

    public struct GC2InventoryTransferRequestPacket : IPackedAuto
    {
        public NetworkTransferRequest request;
    }

    public struct GC2InventoryTransferResponsePacket : IPackedAuto
    {
        public NetworkTransferResponse response;
    }

    public struct GC2InventoryPickupRequestPacket : IPackedAuto
    {
        public NetworkPickupRequest request;
    }

    public struct GC2InventoryPickupResponsePacket : IPackedAuto
    {
        public NetworkPickupResponse response;
    }

    public struct GC2InventoryItemAddedBroadcastPacket : IPackedAuto
    {
        public NetworkItemAddedBroadcast broadcast;
    }

    public struct GC2InventoryItemRemovedBroadcastPacket : IPackedAuto
    {
        public NetworkItemRemovedBroadcast broadcast;
    }

    public struct GC2InventoryItemDroppedBroadcastPacket : IPackedAuto
    {
        public NetworkItemDroppedBroadcast broadcast;
    }

    public struct GC2InventoryDroppedItemRemovedBroadcastPacket : IPackedAuto
    {
        public NetworkDroppedItemRemovedBroadcast broadcast;
    }

    public struct GC2InventoryItemMovedBroadcastPacket : IPackedAuto
    {
        public NetworkItemMovedBroadcast broadcast;
    }

    public struct GC2InventoryItemUsedBroadcastPacket : IPackedAuto
    {
        public NetworkItemUsedBroadcast broadcast;
    }

    public struct GC2InventoryItemEquippedBroadcastPacket : IPackedAuto
    {
        public NetworkItemEquippedBroadcast broadcast;
    }

    public struct GC2InventoryItemUnequippedBroadcastPacket : IPackedAuto
    {
        public NetworkItemUnequippedBroadcast broadcast;
    }

    public struct GC2InventorySocketChangeBroadcastPacket : IPackedAuto
    {
        public NetworkSocketChangeBroadcast broadcast;
    }

    public struct GC2InventoryWealthChangeBroadcastPacket : IPackedAuto
    {
        public NetworkWealthChangeBroadcast broadcast;
    }

    public struct GC2InventorySnapshotPacket : IPackedAuto
    {
        public NetworkInventorySnapshot snapshot;
    }

    public struct GC2InventoryDeltaPacket : IPackedAuto
    {
        public NetworkInventoryDelta delta;
    }
}
#endif
