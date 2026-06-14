#if GC2_INVENTORY
using Arawn.GameCreator2.Networking.Inventory;
using JetBrains.Annotations;
using PurrNet.Modules;
using PurrNet.Packing;
using PurrNet.Utils;

namespace Arawn.GameCreator2.Networking.Inventory.Transport.PurrNet
{
    [UsedImplicitly]
    public static class PurrNetInventoryValuePackers
    {
        [RegisterPackers]
        static void Register()
        {
            Hasher.PrepareType(typeof(NetworkRuntimeProperty));
            Hasher.PrepareType(typeof(NetworkRuntimeSocket));
            Hasher.PrepareType(typeof(NetworkRuntimeItem));
            Hasher.PrepareType(typeof(NetworkCell));
            Hasher.PrepareType(typeof(NetworkContentAddRequest));
            Hasher.PrepareType(typeof(NetworkContentAddResponse));
            Hasher.PrepareType(typeof(NetworkContentRemoveRequest));
            Hasher.PrepareType(typeof(NetworkContentRemoveResponse));
            Hasher.PrepareType(typeof(NetworkContentMoveRequest));
            Hasher.PrepareType(typeof(NetworkContentMoveResponse));
            Hasher.PrepareType(typeof(NetworkContentUseRequest));
            Hasher.PrepareType(typeof(NetworkContentUseResponse));
            Hasher.PrepareType(typeof(NetworkContentDropRequest));
            Hasher.PrepareType(typeof(NetworkContentDropResponse));
            Hasher.PrepareType(typeof(NetworkEquipmentRequest));
            Hasher.PrepareType(typeof(NetworkEquipmentResponse));
            Hasher.PrepareType(typeof(NetworkSocketRequest));
            Hasher.PrepareType(typeof(NetworkSocketResponse));
            Hasher.PrepareType(typeof(NetworkWealthRequest));
            Hasher.PrepareType(typeof(NetworkWealthResponse));
            Hasher.PrepareType(typeof(NetworkTransferRequest));
            Hasher.PrepareType(typeof(NetworkTransferResponse));
            Hasher.PrepareType(typeof(NetworkPickupRequest));
            Hasher.PrepareType(typeof(NetworkPickupResponse));
            Hasher.PrepareType(typeof(NetworkItemAddedBroadcast));
            Hasher.PrepareType(typeof(NetworkItemRemovedBroadcast));
            Hasher.PrepareType(typeof(NetworkItemDroppedBroadcast));
            Hasher.PrepareType(typeof(NetworkDroppedItemRemovedBroadcast));
            Hasher.PrepareType(typeof(NetworkItemMovedBroadcast));
            Hasher.PrepareType(typeof(NetworkItemUsedBroadcast));
            Hasher.PrepareType(typeof(NetworkItemEquippedBroadcast));
            Hasher.PrepareType(typeof(NetworkItemUnequippedBroadcast));
            Hasher.PrepareType(typeof(NetworkSocketChangeBroadcast));
            Hasher.PrepareType(typeof(NetworkWealthChangeBroadcast));
            Hasher.PrepareType(typeof(NetworkPropertyChangeBroadcast));
            Hasher.PrepareType(typeof(NetworkInventorySnapshot));
            Hasher.PrepareType(typeof(NetworkEquipmentSlot));
            Hasher.PrepareType(typeof(NetworkWealthEntry));
            Hasher.PrepareType(typeof(NetworkInventoryDelta));
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkRuntimeProperty value)
        {
            packer.Write(value.PropertyHash);
            packer.Write(value.PropertyIdString);
            packer.Write(value.Number);
            packer.Write(value.Text);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkRuntimeProperty value)
        {
            packer.Read(ref value.PropertyHash);
            packer.Read(ref value.PropertyIdString);
            packer.Read(ref value.Number);
            packer.Read(ref value.Text);
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkRuntimeSocket value)
        {
            packer.Write(value.SocketHash);
            packer.Write(value.SocketIdString);
            packer.Write(value.HasAttachment);
            if (value.HasAttachment) packer.Write(value.Attachment);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkRuntimeSocket value)
        {
            packer.Read(ref value.SocketHash);
            packer.Read(ref value.SocketIdString);
            packer.Read(ref value.HasAttachment);
            value.Attachment = default;
            if (value.HasAttachment) packer.Read(ref value.Attachment);
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkRuntimeItem value)
        {
            packer.Write(value.ItemHash);
            packer.Write(value.ItemIdString);
            packer.Write(value.RuntimeIdHash);
            packer.Write(value.RuntimeIdString);
            packer.WriteList(value.Properties);
            packer.WriteList(value.Sockets);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkRuntimeItem value)
        {
            packer.Read(ref value.ItemHash);
            packer.Read(ref value.ItemIdString);
            packer.Read(ref value.RuntimeIdHash);
            packer.Read(ref value.RuntimeIdString);
            packer.ReadArray(ref value.Properties);
            packer.ReadArray(ref value.Sockets);
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkCell value)
        {
            packer.Write(value.Position);
            packer.Write(value.ItemHash);
            packer.Write(value.StackCount);
            packer.Write(value.RootItem);
            packer.WriteList(value.StackedRuntimeIds);
            packer.WriteList(value.StackedRuntimeIdStrings);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkCell value)
        {
            packer.Read(ref value.Position);
            packer.Read(ref value.ItemHash);
            packer.Read(ref value.StackCount);
            packer.Read(ref value.RootItem);
            packer.ReadArray(ref value.StackedRuntimeIds);
            packer.ReadArray(ref value.StackedRuntimeIdStrings);
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkContentAddRequest value)
        {
            packer.Write(value.RequestId);
            packer.Write(value.ActorNetworkId);
            packer.Write(value.CorrelationId);
            packer.Write(value.TargetBagNetworkId);
            packer.Write(value.ItemHash);
            packer.Write(value.ItemIdString);
            packer.Write(value.RuntimeItem);
            packer.Write(value.Position);
            packer.Write(value.AllowStack);
            packer.Write((byte)value.Source);
            packer.Write(value.SourceHash);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkContentAddRequest value)
        {
            byte source = 0;
            packer.Read(ref value.RequestId);
            packer.Read(ref value.ActorNetworkId);
            packer.Read(ref value.CorrelationId);
            packer.Read(ref value.TargetBagNetworkId);
            packer.Read(ref value.ItemHash);
            packer.Read(ref value.ItemIdString);
            packer.Read(ref value.RuntimeItem);
            packer.Read(ref value.Position);
            packer.Read(ref value.AllowStack);
            packer.Read(ref source);
            packer.Read(ref value.SourceHash);
            value.Source = (InventoryModificationSource)source;
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkContentAddResponse value)
        {
            packer.Write(value.RequestId);
            packer.Write(value.ActorNetworkId);
            packer.Write(value.CorrelationId);
            packer.Write(value.Authorized);
            packer.Write((byte)value.RejectionReason);
            packer.Write(value.ResultPosition);
            packer.Write(value.AssignedRuntimeId);
            packer.Write(value.AssignedRuntimeIdString);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkContentAddResponse value)
        {
            byte reason = 0;
            packer.Read(ref value.RequestId);
            packer.Read(ref value.ActorNetworkId);
            packer.Read(ref value.CorrelationId);
            packer.Read(ref value.Authorized);
            packer.Read(ref reason);
            packer.Read(ref value.ResultPosition);
            packer.Read(ref value.AssignedRuntimeId);
            packer.Read(ref value.AssignedRuntimeIdString);
            value.RejectionReason = (InventoryRejectionReason)reason;
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkContentRemoveRequest value)
        {
            packer.Write(value.RequestId);
            packer.Write(value.ActorNetworkId);
            packer.Write(value.CorrelationId);
            packer.Write(value.TargetBagNetworkId);
            packer.Write(value.RuntimeIdHash);
            packer.Write(value.Position);
            packer.Write(value.UsePosition);
            packer.Write((byte)value.Source);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkContentRemoveRequest value)
        {
            byte source = 0;
            packer.Read(ref value.RequestId);
            packer.Read(ref value.ActorNetworkId);
            packer.Read(ref value.CorrelationId);
            packer.Read(ref value.TargetBagNetworkId);
            packer.Read(ref value.RuntimeIdHash);
            packer.Read(ref value.Position);
            packer.Read(ref value.UsePosition);
            packer.Read(ref source);
            value.Source = (InventoryModificationSource)source;
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkContentRemoveResponse value)
        {
            packer.Write(value.RequestId);
            packer.Write(value.ActorNetworkId);
            packer.Write(value.CorrelationId);
            packer.Write(value.Authorized);
            packer.Write((byte)value.RejectionReason);
            packer.Write(value.RemovedItem);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkContentRemoveResponse value)
        {
            byte reason = 0;
            packer.Read(ref value.RequestId);
            packer.Read(ref value.ActorNetworkId);
            packer.Read(ref value.CorrelationId);
            packer.Read(ref value.Authorized);
            packer.Read(ref reason);
            packer.Read(ref value.RemovedItem);
            value.RejectionReason = (InventoryRejectionReason)reason;
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkContentMoveRequest value)
        {
            packer.Write(value.RequestId);
            packer.Write(value.ActorNetworkId);
            packer.Write(value.CorrelationId);
            packer.Write(value.TargetBagNetworkId);
            packer.Write(value.FromPosition);
            packer.Write(value.ToPosition);
            packer.Write(value.AllowStack);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkContentMoveRequest value)
        {
            packer.Read(ref value.RequestId);
            packer.Read(ref value.ActorNetworkId);
            packer.Read(ref value.CorrelationId);
            packer.Read(ref value.TargetBagNetworkId);
            packer.Read(ref value.FromPosition);
            packer.Read(ref value.ToPosition);
            packer.Read(ref value.AllowStack);
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkContentMoveResponse value)
        {
            packer.Write(value.RequestId);
            packer.Write(value.ActorNetworkId);
            packer.Write(value.CorrelationId);
            packer.Write(value.Authorized);
            packer.Write((byte)value.RejectionReason);
            packer.Write(value.FinalPosition);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkContentMoveResponse value)
        {
            byte reason = 0;
            packer.Read(ref value.RequestId);
            packer.Read(ref value.ActorNetworkId);
            packer.Read(ref value.CorrelationId);
            packer.Read(ref value.Authorized);
            packer.Read(ref reason);
            packer.Read(ref value.FinalPosition);
            value.RejectionReason = (InventoryRejectionReason)reason;
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkContentUseRequest value)
        {
            packer.Write(value.RequestId);
            packer.Write(value.ActorNetworkId);
            packer.Write(value.CorrelationId);
            packer.Write(value.TargetBagNetworkId);
            packer.Write(value.RuntimeIdHash);
            packer.Write(value.Position);
            packer.Write(value.UsePosition);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkContentUseRequest value)
        {
            packer.Read(ref value.RequestId);
            packer.Read(ref value.ActorNetworkId);
            packer.Read(ref value.CorrelationId);
            packer.Read(ref value.TargetBagNetworkId);
            packer.Read(ref value.RuntimeIdHash);
            packer.Read(ref value.Position);
            packer.Read(ref value.UsePosition);
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkContentUseResponse value)
        {
            packer.Write(value.RequestId);
            packer.Write(value.ActorNetworkId);
            packer.Write(value.CorrelationId);
            packer.Write(value.Authorized);
            packer.Write((byte)value.RejectionReason);
            packer.Write(value.WasConsumed);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkContentUseResponse value)
        {
            byte reason = 0;
            packer.Read(ref value.RequestId);
            packer.Read(ref value.ActorNetworkId);
            packer.Read(ref value.CorrelationId);
            packer.Read(ref value.Authorized);
            packer.Read(ref reason);
            packer.Read(ref value.WasConsumed);
            value.RejectionReason = (InventoryRejectionReason)reason;
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkContentDropRequest value)
        {
            packer.Write(value.RequestId);
            packer.Write(value.ActorNetworkId);
            packer.Write(value.CorrelationId);
            packer.Write(value.TargetBagNetworkId);
            packer.Write(value.RuntimeIdHash);
            packer.Write(value.DropPosition);
            packer.Write(value.MaxAmount);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkContentDropRequest value)
        {
            packer.Read(ref value.RequestId);
            packer.Read(ref value.ActorNetworkId);
            packer.Read(ref value.CorrelationId);
            packer.Read(ref value.TargetBagNetworkId);
            packer.Read(ref value.RuntimeIdHash);
            packer.Read(ref value.DropPosition);
            packer.Read(ref value.MaxAmount);
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkContentDropResponse value)
        {
            packer.Write(value.RequestId);
            packer.Write(value.ActorNetworkId);
            packer.Write(value.CorrelationId);
            packer.Write(value.Authorized);
            packer.Write((byte)value.RejectionReason);
            packer.Write(value.DroppedCount);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkContentDropResponse value)
        {
            byte reason = 0;
            packer.Read(ref value.RequestId);
            packer.Read(ref value.ActorNetworkId);
            packer.Read(ref value.CorrelationId);
            packer.Read(ref value.Authorized);
            packer.Read(ref reason);
            packer.Read(ref value.DroppedCount);
            value.RejectionReason = (InventoryRejectionReason)reason;
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkEquipmentRequest value)
        {
            packer.Write(value.RequestId);
            packer.Write(value.ActorNetworkId);
            packer.Write(value.CorrelationId);
            packer.Write(value.TargetBagNetworkId);
            packer.Write(value.RuntimeIdHash);
            packer.Write((byte)value.Action);
            packer.Write(value.SlotOrIndex);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkEquipmentRequest value)
        {
            byte action = 0;
            packer.Read(ref value.RequestId);
            packer.Read(ref value.ActorNetworkId);
            packer.Read(ref value.CorrelationId);
            packer.Read(ref value.TargetBagNetworkId);
            packer.Read(ref value.RuntimeIdHash);
            packer.Read(ref action);
            packer.Read(ref value.SlotOrIndex);
            value.Action = (EquipmentAction)action;
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkEquipmentResponse value)
        {
            packer.Write(value.RequestId);
            packer.Write(value.ActorNetworkId);
            packer.Write(value.CorrelationId);
            packer.Write(value.Authorized);
            packer.Write((byte)value.RejectionReason);
            packer.Write(value.EquippedIndex);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkEquipmentResponse value)
        {
            byte reason = 0;
            packer.Read(ref value.RequestId);
            packer.Read(ref value.ActorNetworkId);
            packer.Read(ref value.CorrelationId);
            packer.Read(ref value.Authorized);
            packer.Read(ref reason);
            packer.Read(ref value.EquippedIndex);
            value.RejectionReason = (InventoryRejectionReason)reason;
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkSocketRequest value)
        {
            packer.Write(value.RequestId);
            packer.Write(value.ActorNetworkId);
            packer.Write(value.CorrelationId);
            packer.Write(value.TargetBagNetworkId);
            packer.Write(value.ParentRuntimeIdHash);
            packer.Write(value.AttachmentRuntimeIdHash);
            packer.Write(value.SocketHash);
            packer.Write(value.SocketIdString);
            packer.Write((byte)value.Action);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkSocketRequest value)
        {
            byte action = 0;
            packer.Read(ref value.RequestId);
            packer.Read(ref value.ActorNetworkId);
            packer.Read(ref value.CorrelationId);
            packer.Read(ref value.TargetBagNetworkId);
            packer.Read(ref value.ParentRuntimeIdHash);
            packer.Read(ref value.AttachmentRuntimeIdHash);
            packer.Read(ref value.SocketHash);
            packer.Read(ref value.SocketIdString);
            packer.Read(ref action);
            value.Action = (SocketAction)action;
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkSocketResponse value)
        {
            packer.Write(value.RequestId);
            packer.Write(value.ActorNetworkId);
            packer.Write(value.CorrelationId);
            packer.Write(value.Authorized);
            packer.Write((byte)value.RejectionReason);
            packer.Write(value.UsedSocketHash);
            packer.Write(value.DetachedItem);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkSocketResponse value)
        {
            byte reason = 0;
            packer.Read(ref value.RequestId);
            packer.Read(ref value.ActorNetworkId);
            packer.Read(ref value.CorrelationId);
            packer.Read(ref value.Authorized);
            packer.Read(ref reason);
            packer.Read(ref value.UsedSocketHash);
            packer.Read(ref value.DetachedItem);
            value.RejectionReason = (InventoryRejectionReason)reason;
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkWealthRequest value)
        {
            packer.Write(value.RequestId);
            packer.Write(value.ActorNetworkId);
            packer.Write(value.CorrelationId);
            packer.Write(value.TargetBagNetworkId);
            packer.Write(value.CurrencyHash);
            packer.Write(value.CurrencyIdString);
            packer.Write(value.Value);
            packer.Write((byte)value.Action);
            packer.Write((byte)value.Source);
            packer.Write(value.SourceHash);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkWealthRequest value)
        {
            byte action = 0;
            byte source = 0;
            packer.Read(ref value.RequestId);
            packer.Read(ref value.ActorNetworkId);
            packer.Read(ref value.CorrelationId);
            packer.Read(ref value.TargetBagNetworkId);
            packer.Read(ref value.CurrencyHash);
            packer.Read(ref value.CurrencyIdString);
            packer.Read(ref value.Value);
            packer.Read(ref action);
            packer.Read(ref source);
            packer.Read(ref value.SourceHash);
            value.Action = (WealthAction)action;
            value.Source = (InventoryModificationSource)source;
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkWealthResponse value)
        {
            packer.Write(value.RequestId);
            packer.Write(value.ActorNetworkId);
            packer.Write(value.CorrelationId);
            packer.Write(value.Authorized);
            packer.Write((byte)value.RejectionReason);
            packer.Write(value.NewValue);
            packer.Write(value.OldValue);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkWealthResponse value)
        {
            byte reason = 0;
            packer.Read(ref value.RequestId);
            packer.Read(ref value.ActorNetworkId);
            packer.Read(ref value.CorrelationId);
            packer.Read(ref value.Authorized);
            packer.Read(ref reason);
            packer.Read(ref value.NewValue);
            packer.Read(ref value.OldValue);
            value.RejectionReason = (InventoryRejectionReason)reason;
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkTransferRequest value)
        {
            packer.Write(value.RequestId);
            packer.Write(value.ActorNetworkId);
            packer.Write(value.CorrelationId);
            packer.Write(value.SourceBagNetworkId);
            packer.Write(value.DestinationBagNetworkId);
            packer.Write(value.RuntimeIdHash);
            packer.Write(value.DestinationPosition);
            packer.Write(value.AllowStack);
            packer.Write((byte)value.Source);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkTransferRequest value)
        {
            byte source = 0;
            packer.Read(ref value.RequestId);
            packer.Read(ref value.ActorNetworkId);
            packer.Read(ref value.CorrelationId);
            packer.Read(ref value.SourceBagNetworkId);
            packer.Read(ref value.DestinationBagNetworkId);
            packer.Read(ref value.RuntimeIdHash);
            packer.Read(ref value.DestinationPosition);
            packer.Read(ref value.AllowStack);
            packer.Read(ref source);
            value.Source = (InventoryModificationSource)source;
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkTransferResponse value)
        {
            packer.Write(value.RequestId);
            packer.Write(value.ActorNetworkId);
            packer.Write(value.CorrelationId);
            packer.Write(value.Authorized);
            packer.Write((byte)value.RejectionReason);
            packer.Write(value.FinalPosition);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkTransferResponse value)
        {
            byte reason = 0;
            packer.Read(ref value.RequestId);
            packer.Read(ref value.ActorNetworkId);
            packer.Read(ref value.CorrelationId);
            packer.Read(ref value.Authorized);
            packer.Read(ref reason);
            packer.Read(ref value.FinalPosition);
            value.RejectionReason = (InventoryRejectionReason)reason;
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkPickupRequest value)
        {
            packer.Write(value.RequestId);
            packer.Write(value.ActorNetworkId);
            packer.Write(value.CorrelationId);
            packer.Write(value.PickerBagNetworkId);
            packer.Write(value.PropNetworkId);
            packer.Write(value.SourceBagNetworkId);
            packer.Write(value.RuntimeIdHash);
            packer.Write(value.DestinationPosition);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkPickupRequest value)
        {
            packer.Read(ref value.RequestId);
            packer.Read(ref value.ActorNetworkId);
            packer.Read(ref value.CorrelationId);
            packer.Read(ref value.PickerBagNetworkId);
            packer.Read(ref value.PropNetworkId);
            packer.Read(ref value.SourceBagNetworkId);
            packer.Read(ref value.RuntimeIdHash);
            packer.Read(ref value.DestinationPosition);
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkPickupResponse value)
        {
            packer.Write(value.RequestId);
            packer.Write(value.ActorNetworkId);
            packer.Write(value.CorrelationId);
            packer.Write(value.Authorized);
            packer.Write((byte)value.RejectionReason);
            packer.Write(value.PickedUpItem);
            packer.Write(value.PlacedPosition);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkPickupResponse value)
        {
            byte reason = 0;
            packer.Read(ref value.RequestId);
            packer.Read(ref value.ActorNetworkId);
            packer.Read(ref value.CorrelationId);
            packer.Read(ref value.Authorized);
            packer.Read(ref reason);
            packer.Read(ref value.PickedUpItem);
            packer.Read(ref value.PlacedPosition);
            value.RejectionReason = (InventoryRejectionReason)reason;
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkItemAddedBroadcast value)
        {
            packer.Write(value.BagNetworkId);
            packer.Write(value.Item);
            packer.Write(value.Position);
            packer.Write(value.StackCount);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkItemAddedBroadcast value)
        {
            packer.Read(ref value.BagNetworkId);
            packer.Read(ref value.Item);
            packer.Read(ref value.Position);
            packer.Read(ref value.StackCount);
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkItemRemovedBroadcast value)
        {
            packer.Write(value.BagNetworkId);
            packer.Write(value.RuntimeIdHash);
            packer.Write(value.Position);
            packer.Write(value.RemainingStackCount);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkItemRemovedBroadcast value)
        {
            packer.Read(ref value.BagNetworkId);
            packer.Read(ref value.RuntimeIdHash);
            packer.Read(ref value.Position);
            packer.Read(ref value.RemainingStackCount);
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkItemDroppedBroadcast value)
        {
            packer.Write(value.SourceBagNetworkId);
            packer.Write(value.Item);
            packer.Write(value.Position);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkItemDroppedBroadcast value)
        {
            packer.Read(ref value.SourceBagNetworkId);
            packer.Read(ref value.Item);
            packer.Read(ref value.Position);
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkDroppedItemRemovedBroadcast value)
        {
            packer.Write(value.SourceBagNetworkId);
            packer.Write(value.RuntimeIdHash);
            packer.Write(value.Position);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkDroppedItemRemovedBroadcast value)
        {
            packer.Read(ref value.SourceBagNetworkId);
            packer.Read(ref value.RuntimeIdHash);
            packer.Read(ref value.Position);
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkItemMovedBroadcast value)
        {
            packer.Write(value.BagNetworkId);
            packer.Write(value.RuntimeIdHash);
            packer.Write(value.FromPosition);
            packer.Write(value.ToPosition);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkItemMovedBroadcast value)
        {
            packer.Read(ref value.BagNetworkId);
            packer.Read(ref value.RuntimeIdHash);
            packer.Read(ref value.FromPosition);
            packer.Read(ref value.ToPosition);
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkItemUsedBroadcast value)
        {
            packer.Write(value.BagNetworkId);
            packer.Write(value.RuntimeIdHash);
            packer.Write(value.WasConsumed);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkItemUsedBroadcast value)
        {
            packer.Read(ref value.BagNetworkId);
            packer.Read(ref value.RuntimeIdHash);
            packer.Read(ref value.WasConsumed);
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkItemEquippedBroadcast value)
        {
            packer.Write(value.BagNetworkId);
            packer.Write(value.RuntimeIdHash);
            packer.Write(value.EquipmentIndex);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkItemEquippedBroadcast value)
        {
            packer.Read(ref value.BagNetworkId);
            packer.Read(ref value.RuntimeIdHash);
            packer.Read(ref value.EquipmentIndex);
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkItemUnequippedBroadcast value)
        {
            packer.Write(value.BagNetworkId);
            packer.Write(value.RuntimeIdHash);
            packer.Write(value.EquipmentIndex);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkItemUnequippedBroadcast value)
        {
            packer.Read(ref value.BagNetworkId);
            packer.Read(ref value.RuntimeIdHash);
            packer.Read(ref value.EquipmentIndex);
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkSocketChangeBroadcast value)
        {
            packer.Write(value.BagNetworkId);
            packer.Write(value.ParentRuntimeIdHash);
            packer.Write(value.SocketHash);
            packer.Write(value.HasAttachment);
            if (value.HasAttachment) packer.Write(value.Attachment);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkSocketChangeBroadcast value)
        {
            packer.Read(ref value.BagNetworkId);
            packer.Read(ref value.ParentRuntimeIdHash);
            packer.Read(ref value.SocketHash);
            packer.Read(ref value.HasAttachment);
            value.Attachment = default;
            if (value.HasAttachment) packer.Read(ref value.Attachment);
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkWealthChangeBroadcast value)
        {
            packer.Write(value.BagNetworkId);
            packer.Write(value.CurrencyHash);
            packer.Write(value.NewValue);
            packer.Write(value.Change);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkWealthChangeBroadcast value)
        {
            packer.Read(ref value.BagNetworkId);
            packer.Read(ref value.CurrencyHash);
            packer.Read(ref value.NewValue);
            packer.Read(ref value.Change);
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkPropertyChangeBroadcast value)
        {
            packer.Write(value.BagNetworkId);
            packer.Write(value.RuntimeIdHash);
            packer.Write(value.PropertyHash);
            packer.Write(value.NewNumber);
            packer.Write(value.NewText);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkPropertyChangeBroadcast value)
        {
            packer.Read(ref value.BagNetworkId);
            packer.Read(ref value.RuntimeIdHash);
            packer.Read(ref value.PropertyHash);
            packer.Read(ref value.NewNumber);
            packer.Read(ref value.NewText);
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkInventorySnapshot value)
        {
            packer.Write(value.BagNetworkId);
            packer.Write(value.Timestamp);
            packer.Write(value.BagType);
            packer.Write(value.BagSize);
            packer.Write(value.MaxWeight);
            packer.WriteList(value.Cells);
            packer.WriteList(value.Equipment);
            packer.WriteList(value.Wealth);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkInventorySnapshot value)
        {
            packer.Read(ref value.BagNetworkId);
            packer.Read(ref value.Timestamp);
            packer.Read(ref value.BagType);
            packer.Read(ref value.BagSize);
            packer.Read(ref value.MaxWeight);
            packer.ReadArray(ref value.Cells);
            packer.ReadArray(ref value.Equipment);
            packer.ReadArray(ref value.Wealth);
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkEquipmentSlot value)
        {
            packer.Write(value.SlotIndex);
            packer.Write(value.BaseItemHash);
            packer.Write(value.IsOccupied);
            packer.Write(value.EquippedRuntimeIdHash);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkEquipmentSlot value)
        {
            packer.Read(ref value.SlotIndex);
            packer.Read(ref value.BaseItemHash);
            packer.Read(ref value.IsOccupied);
            packer.Read(ref value.EquippedRuntimeIdHash);
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkWealthEntry value)
        {
            packer.Write(value.CurrencyHash);
            packer.Write(value.Amount);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkWealthEntry value)
        {
            packer.Read(ref value.CurrencyHash);
            packer.Read(ref value.Amount);
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkInventoryDelta value)
        {
            packer.Write(value.BagNetworkId);
            packer.Write(value.Timestamp);
            packer.Write(value.ChangeMask);
            packer.WriteList(value.ChangedCells);
            packer.WriteList(value.ChangedEquipment);
            packer.WriteList(value.ChangedWealth);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkInventoryDelta value)
        {
            packer.Read(ref value.BagNetworkId);
            packer.Read(ref value.Timestamp);
            packer.Read(ref value.ChangeMask);
            packer.ReadArray(ref value.ChangedCells);
            packer.ReadArray(ref value.ChangedEquipment);
            packer.ReadArray(ref value.ChangedWealth);
        }
    }
}
#endif
