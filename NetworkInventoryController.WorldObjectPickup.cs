#if GC2_INVENTORY
using UnityEngine;
using GameCreator.Runtime.Inventory;

namespace Arawn.GameCreator2.Networking.Inventory
{
    public partial class NetworkInventoryController
    {
        // [LOCAL-EDIT] #PILFER-INVENTORY-WORLD-OBJECT
        public void RequestWorldObjectPickup(NetworkWorldObject worldObject, Vector2Int destinationPosition)
        {
            if (worldObject == null) return;
            if (m_IsRemoteClient) return;
            if (!m_IsLocalClient && !m_IsServer) return;

            if (!worldObject.AllowPickup || worldObject.Item == null)
            {
                LogPickupWarning(
                    $"{name}: world object pickup skipped invalid source prop={worldObject.NetworkId} allow={worldObject.AllowPickup} item={(worldObject.Item != null ? worldObject.Item.ID.String : "null")}",
                    this);
                return;
            }

            if (!TryGetLocalActorNetworkId(out uint actorNetworkId))
            {
                LogPickupWarning($"{name}: world object pickup skipped no local actor network id prop={worldObject.NetworkId}", this);
                return;
            }

            var request = new NetworkPickupRequest
            {
                RequestId = GetNextRequestId(),
                ActorNetworkId = actorNetworkId,
                CorrelationId = NetworkCorrelation.Compose(actorNetworkId, m_LastIssuedRequestId),
                PickerBagNetworkId = NetworkId,
                PropNetworkId = worldObject.NetworkId,
                SourceBagNetworkId = 0,
                RuntimeIdHash = 0,
                DestinationPosition = destinationPosition
            };

            LogPickupDebug(
                $"{name}: sending world object pickup request req={request.RequestId} actor={actorNetworkId} pickerBag={NetworkId} prop={request.PropNetworkId} item={worldObject.Item.ID.String} destination={destinationPosition} server={m_IsServer} local={m_IsLocalClient}",
                this);

            if (m_IsServer)
            {
                NetworkPickupResponse response = ProcessPickupRequest(request, NetworkId);
                response.ActorNetworkId = request.ActorNetworkId;
                response.CorrelationId = request.CorrelationId;
                if (!response.Authorized)
                {
                    OnOperationRejected?.Invoke(response.RejectionReason, "World object pickup");
                }
                return;
            }

            NetworkInventoryManager.Instance?.SendPickupRequest(request);
        }

        // [LOCAL-EDIT] #PILFER-INVENTORY-WORLD-OBJECT
        private bool TryProcessWorldObjectPickupRequest(
            NetworkPickupRequest request,
            uint clientNetworkId,
            out NetworkPickupResponse response)
        {
            response = default;

            if (request.PropNetworkId == 0) return false;

            if (!NetworkWorldObjectRegistry.TryGet(request.PropNetworkId, out NetworkWorldObject worldObject))
            {
                if (NetworkWorldObjectRegistry.IsConsumed(request.PropNetworkId))
                {
                    // [LOCAL-EDIT] #PILFER-INVENTORY-WORLD-OBJECT-CONSUMED-REGISTRY
                    LogPickupWarning(
                        $"{name}: world object pickup rejected consumed missing instance req={request.RequestId} prop={request.PropNetworkId} client={clientNetworkId}",
                        this);
                    response = BuildWorldObjectPickupResponse(
                        request,
                        false,
                        InventoryRejectionReason.InvalidOperation,
                        NetworkPickupFailure.WorldObjectConsumed,
                        default,
                        TBagContent.INVALID);
                    return true;
                }

                LogPickupWarning(
                    $"{name}: world object pickup rejected prop not found req={request.RequestId} prop={request.PropNetworkId} client={clientNetworkId}",
                    this);
                response = BuildWorldObjectPickupResponse(
                    request,
                    false,
                    InventoryRejectionReason.RuntimeItemNotFound,
                    NetworkPickupFailure.WorldObjectNotFound,
                    default,
                    TBagContent.INVALID);
                return true;
            }

            if (!worldObject.AllowPickup)
            {
                LogPickupWarning(
                    $"{name}: world object pickup rejected disabled req={request.RequestId} prop={request.PropNetworkId} allow={worldObject.AllowPickup} consumed={worldObject.IsConsumed} item={(worldObject.Item != null ? worldObject.Item.ID.String : "null")}",
                    worldObject);
                response = BuildWorldObjectPickupResponse(
                    request,
                    false,
                    InventoryRejectionReason.InvalidOperation,
                    NetworkPickupFailure.WorldObjectPickupDisabled,
                    default,
                    TBagContent.INVALID);
                return true;
            }

            if (worldObject.Item == null)
            {
                LogPickupWarning(
                    $"{name}: world object pickup rejected missing item req={request.RequestId} prop={request.PropNetworkId} allow={worldObject.AllowPickup} consumed={worldObject.IsConsumed}",
                    worldObject);
                response = BuildWorldObjectPickupResponse(
                    request,
                    false,
                    InventoryRejectionReason.ItemNotFound,
                    NetworkPickupFailure.WorldObjectItemMissing,
                    default,
                    TBagContent.INVALID);
                return true;
            }

            if (worldObject.IsConsumed)
            {
                LogPickupWarning(
                    $"{name}: world object pickup rejected consumed req={request.RequestId} prop={request.PropNetworkId} item={worldObject.Item.ID.String}",
                    worldObject);
                response = BuildWorldObjectPickupResponse(
                    request,
                    false,
                    InventoryRejectionReason.InvalidOperation,
                    NetworkPickupFailure.WorldObjectConsumed,
                    default,
                    TBagContent.INVALID);
                return true;
            }

            float pickupDistance3D = worldObject.GetDistanceTo(transform.position);
            float pickupHorizontalDistance = worldObject.GetHorizontalDistanceTo(transform.position);

            if (!worldObject.CanPickupFrom(transform.position))
            {
                // [LOCAL-EDIT] #PILFER-INVENTORY-WORLD-OBJECT-REJECT-DIAGNOSTICS
                LogPickupWarning(
                    $"{name}: world object pickup rejected out of range req={request.RequestId} prop={request.PropNetworkId} pickerPosition={transform.position} propPosition={worldObject.transform.position} distance3D={pickupDistance3D} horizontalDistance={pickupHorizontalDistance} radius={worldObject.PickupRadius}",
                    worldObject);
                response = BuildWorldObjectPickupResponse(
                    request,
                    false,
                    InventoryRejectionReason.InvalidPosition,
                    NetworkPickupFailure.WorldObjectOutOfRange,
                    default,
                    TBagContent.INVALID);
                return true;
            }

            RuntimeItem runtimeItem = worldObject.CreatePickupRuntimeItem();
            if (runtimeItem == null)
            {
                response = BuildWorldObjectPickupResponse(
                    request,
                    false,
                    InventoryRejectionReason.IdentityMismatch,
                    NetworkPickupFailure.WorldObjectRuntimeItemFailed,
                    default,
                    TBagContent.INVALID);
                return true;
            }

            Vector2Int finalPosition;
            m_IsApplyingNetworkState = true;
            try
            {
                if (request.DestinationPosition.x >= 0 && request.DestinationPosition.y >= 0)
                {
                    bool added = m_Bag.Content.Add(runtimeItem, request.DestinationPosition, true);
                    finalPosition = added ? request.DestinationPosition : TBagContent.INVALID;
                }
                else
                {
                    finalPosition = m_Bag.Content.Add(runtimeItem, true);
                }
            }
            finally
            {
                m_IsApplyingNetworkState = false;
            }

            if (finalPosition == TBagContent.INVALID)
            {
                response = BuildWorldObjectPickupResponse(
                    request,
                    false,
                    InventoryRejectionReason.InsufficientSpace,
                    NetworkPickupFailure.None,
                    default,
                    TBagContent.INVALID);
                return true;
            }

            worldObject.MarkPickedUp();
            TrackRuntimeItemRecursive(runtimeItem);

            NetworkRuntimeItem networkItem = ConvertToNetworkItem(runtimeItem);
            var addBroadcast = new NetworkItemAddedBroadcast
            {
                BagNetworkId = NetworkId,
                Item = networkItem,
                Position = finalPosition,
                StackCount = m_Bag.Content.GetContent(finalPosition)?.Count ?? 1
            };

            NetworkInventoryManager.Instance?.BroadcastItemAdded(addBroadcast);
            OnItemAdded?.Invoke(addBroadcast);
            CacheCurrentSyncState();

            LogPickupDebug(
                $"{name}: world object pickup accepted req={request.RequestId} prop={request.PropNetworkId} item={DescribeRuntimeItem(runtimeItem)} finalPosition={finalPosition} distance3D={pickupDistance3D} horizontalDistance={pickupHorizontalDistance} radius={worldObject.PickupRadius}",
                this);

            response = BuildWorldObjectPickupResponse(
                request,
                true,
                InventoryRejectionReason.None,
                NetworkPickupFailure.None,
                networkItem,
                finalPosition);
            return true;
        }

        // [LOCAL-EDIT] #PILFER-INVENTORY-WORLD-OBJECT
        private static NetworkPickupResponse BuildWorldObjectPickupResponse(
            NetworkPickupRequest request,
            bool authorized,
            InventoryRejectionReason reason,
            NetworkPickupFailure pickupFailure,
            NetworkRuntimeItem item,
            Vector2Int position)
        {
            return new NetworkPickupResponse
            {
                RequestId = request.RequestId,
                ActorNetworkId = request.ActorNetworkId,
                CorrelationId = request.CorrelationId,
                PropNetworkId = request.PropNetworkId,
                PickupFailure = pickupFailure,
                Authorized = authorized,
                RejectionReason = reason,
                PickedUpItem = item,
                PlacedPosition = position
            };
        }
    }
}
#endif
