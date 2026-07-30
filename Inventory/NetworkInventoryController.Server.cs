#if GC2_INVENTORY
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using UnityEngine;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.Inventory;

namespace Arawn.GameCreator2.Networking.Inventory
{
    // ════════════════════════════════════════════════════════════════════════════════════════════
    // SERVER-SIDE — Request processing, broadcasting, and helper methods
    // ════════════════════════════════════════════════════════════════════════════════════════════

    public partial class NetworkInventoryController
    {
        private static readonly FieldInfo s_RuntimeItemIdField = typeof(RuntimeItem)
            .GetField("m_RuntimeID", BindingFlags.Instance | BindingFlags.NonPublic);

        private static readonly FieldInfo s_RuntimeSocketAttachmentField = typeof(RuntimeSocket)
            .GetField("m_AttachmentRuntimeItem", BindingFlags.Instance | BindingFlags.NonPublic);

        static NetworkInventoryController()
        {
            if (s_RuntimeItemIdField == null || s_RuntimeSocketAttachmentField == null)
            {
                Debug.LogWarning(
                    "[NetworkInventoryController] Reflection dependencies for RuntimeItem/RuntimeSocket could not be resolved. " +
                    "Inventory runtime reconstruction may degrade until patch signatures are updated for this GC2 version.");
            }
        }

        // ════════════════════════════════════════════════════════════════════════════════════════
        // SERVER-SIDE: PROCESS REQUESTS
        // ════════════════════════════════════════════════════════════════════════════════════════

        #region Server Processing

        /// <summary>
        /// [Server] Process content add request.
        /// </summary>
        public NetworkContentAddResponse ProcessContentAddRequest(NetworkContentAddRequest request, uint clientNetworkId)
        {
            if (!m_IsServer)
            {
                return new NetworkContentAddResponse
                {
                    RequestId = request.RequestId,
                    Authorized = false,
                    RejectionReason = InventoryRejectionReason.NotAuthorized
                };
            }

            // Client-originated arbitrary runtime payloads are not allowed.
            if (request.ItemHash == 0)
            {
                return new NetworkContentAddResponse
                {
                    RequestId = request.RequestId,
                    Authorized = false,
                    RejectionReason = InventoryRejectionReason.SecurityViolation
                };
            }

            if (!TryResolveItem(request.ItemHash, request.ItemIdString, out Item item))
            {
                return new NetworkContentAddResponse
                {
                    RequestId = request.RequestId,
                    Authorized = false,
                    RejectionReason = InventoryRejectionReason.IdentityMismatch
                };
            }

            RuntimeItem runtimeItem = new RuntimeItem(item);

            Vector2Int resultPosition;
            using (EnterNetworkMutationScope())
            {
                // This semantic request owns its authoritative broadcast. Suppress the primitive
                // GC2 EventAdd route so it cannot emit a second direct-add broadcast.
                if (request.Position.x >= 0 && request.Position.y >= 0)
                {
                    bool success = m_Bag.Content.Add(runtimeItem, request.Position, request.AllowStack);
                    resultPosition = success ? request.Position : TBagContent.INVALID;
                }
                else
                {
                    resultPosition = m_Bag.Content.Add(runtimeItem, request.AllowStack);
                }
            }

            if (resultPosition == TBagContent.INVALID)
            {
                return new NetworkContentAddResponse
                {
                    RequestId = request.RequestId,
                    Authorized = false,
                    RejectionReason = InventoryRejectionReason.InsufficientSpace
                };
            }

            // Update map
            TrackRuntimeItemRecursive(runtimeItem);
            uint stateVersion = GetAuthoritativeStateVersion();

            // Broadcast
            var broadcast = new NetworkItemAddedBroadcast
            {
                BagNetworkId = NetworkId,
                Item = ConvertToNetworkItem(runtimeItem),
                Position = resultPosition,
                StackCount = m_Bag.Content.GetContent(resultPosition)?.Count ?? 1,
                StateVersion = stateVersion
            };

            NetworkInventoryManager.Instance?.BroadcastItemAdded(broadcast);
            OnItemAdded?.Invoke(broadcast);
            CacheCurrentSyncState();

            return new NetworkContentAddResponse
            {
                RequestId = request.RequestId,
                Authorized = true,
                RejectionReason = InventoryRejectionReason.None,
                ResultPosition = resultPosition,
                AssignedRuntimeId = runtimeItem.RuntimeID.Hash,
                AssignedRuntimeIdString = runtimeItem.RuntimeID.String,
                StateVersion = stateVersion
            };
        }

        /// <summary>
        /// [Server] Process content remove request.
        /// </summary>
        public NetworkContentRemoveResponse ProcessContentRemoveRequest(NetworkContentRemoveRequest request, uint clientNetworkId)
        {
            if (!m_IsServer)
            {
                return new NetworkContentRemoveResponse
                {
                    RequestId = request.RequestId,
                    Authorized = false,
                    RejectionReason = InventoryRejectionReason.NotAuthorized
                };
            }

            RuntimeItem removed;
            Vector2Int position;

            using (EnterNetworkMutationScope())
            {
                // This semantic request owns its authoritative broadcast. Suppress the primitive
                // GC2 EventRemove route so it cannot emit a second direct-remove broadcast.
                if (request.UsePosition)
                {
                    position = request.Position;
                    removed = m_Bag.Content.Remove(position);
                }
                else
                {
                    if (!m_RuntimeItemMap.TryGetValue(request.RuntimeIdHash, out var runtimeItem))
                    {
                        return new NetworkContentRemoveResponse
                        {
                            RequestId = request.RequestId,
                            Authorized = false,
                            RejectionReason = InventoryRejectionReason.RuntimeItemNotFound
                        };
                    }

                    position = m_Bag.Content.FindPosition(runtimeItem.RuntimeID);
                    removed = m_Bag.Content.Remove(runtimeItem);
                }
            }

            if (removed == null)
            {
                return new NetworkContentRemoveResponse
                {
                    RequestId = request.RequestId,
                    Authorized = false,
                    RejectionReason = InventoryRejectionReason.RuntimeItemNotFound
                };
            }

            UntrackRuntimeItemRecursive(removed);
            uint stateVersion = GetAuthoritativeStateVersion();

            // Broadcast
            var cell = m_Bag.Content.GetContent(position);
            var removeBroadcast = new NetworkItemRemovedBroadcast
            {
                BagNetworkId = NetworkId,
                RuntimeIdHash = removed.RuntimeID.Hash,
                Position = position,
                RemainingStackCount = cell?.Count ?? 0,
                StateVersion = stateVersion
            };

            NetworkInventoryManager.Instance?.BroadcastItemRemoved(removeBroadcast);
            OnItemRemoved?.Invoke(removeBroadcast);
            CacheCurrentSyncState();

            return new NetworkContentRemoveResponse
            {
                RequestId = request.RequestId,
                Authorized = true,
                RejectionReason = InventoryRejectionReason.None,
                RemovedItem = ConvertToNetworkItem(removed),
                StateVersion = stateVersion
            };
        }

        /// <summary>
        /// [Server] Process content move request.
        /// </summary>
        public NetworkContentMoveResponse ProcessContentMoveRequest(NetworkContentMoveRequest request, uint clientNetworkId)
        {
            if (!m_IsServer)
            {
                return new NetworkContentMoveResponse
                {
                    RequestId = request.RequestId,
                    Authorized = false,
                    RejectionReason = InventoryRejectionReason.NotAuthorized
                };
            }

            if (!m_Bag.Content.CanMove(request.FromPosition, request.ToPosition, request.AllowStack))
            {
                return new NetworkContentMoveResponse
                {
                    RequestId = request.RequestId,
                    Authorized = false,
                    RejectionReason = InventoryRejectionReason.InvalidPosition
                };
            }

            var moveCell = m_Bag.Content.GetContent(request.FromPosition);
            long runtimeIdHash = moveCell?.RootRuntimeItemID.Hash ?? 0;

            bool moveSuccess = m_Bag.Content.Move(request.FromPosition, request.ToPosition, request.AllowStack);

            if (!moveSuccess)
            {
                return new NetworkContentMoveResponse
                {
                    RequestId = request.RequestId,
                    Authorized = false,
                    RejectionReason = InventoryRejectionReason.InvalidOperation
                };
            }
            uint stateVersion = GetAuthoritativeStateVersion();

            // Broadcast
            var moveBroadcast = new NetworkItemMovedBroadcast
            {
                BagNetworkId = NetworkId,
                RuntimeIdHash = runtimeIdHash,
                FromPosition = request.FromPosition,
                ToPosition = request.ToPosition,
                StateVersion = stateVersion
            };

            NetworkInventoryManager.Instance?.BroadcastItemMoved(moveBroadcast);
            OnItemMoved?.Invoke(moveBroadcast);
            CacheCurrentSyncState();

            return new NetworkContentMoveResponse
            {
                RequestId = request.RequestId,
                Authorized = true,
                RejectionReason = InventoryRejectionReason.None,
                FinalPosition = request.ToPosition,
                StateVersion = stateVersion
            };
        }

        /// <summary>
        /// [Server] Process content use request.
        /// </summary>
        public NetworkContentUseResponse ProcessContentUseRequest(NetworkContentUseRequest request, uint clientNetworkId)
        {
            if (!m_IsServer)
            {
                return new NetworkContentUseResponse
                {
                    RequestId = request.RequestId,
                    Authorized = false,
                    RejectionReason = InventoryRejectionReason.NotAuthorized
                };
            }

            bool useSuccess;
            bool wasConsumed = false;
            long usedRuntimeIdHash = request.RuntimeIdHash;
            RuntimeItem usedRuntimeItem = null;

            using (EnterNetworkMutationScope())
            {
                // EventUse is a primitive notification for a native operation. This semantic
                // request emits the one authoritative NetworkItemUsedBroadcast below.
                if (request.UsePosition)
                {
                    var useCell = m_Bag.Content.GetContent(request.Position);
                    if (useCell == null || useCell.Available)
                    {
                        return new NetworkContentUseResponse
                        {
                            RequestId = request.RequestId,
                            Authorized = false,
                            RejectionReason = InventoryRejectionReason.RuntimeItemNotFound
                        };
                    }

                    usedRuntimeItem = useCell.RootRuntimeItem;
                    usedRuntimeIdHash = usedRuntimeItem.RuntimeID.Hash;
                    wasConsumed = usedRuntimeItem.Item.Usage.ConsumeWhenUse;
                    useSuccess = m_Bag.Content.Use(request.Position);
                }
                else
                {
                    if (!m_RuntimeItemMap.TryGetValue(request.RuntimeIdHash, out var useItem))
                    {
                        return new NetworkContentUseResponse
                        {
                            RequestId = request.RequestId,
                            Authorized = false,
                            RejectionReason = InventoryRejectionReason.RuntimeItemNotFound
                        };
                    }

                    usedRuntimeItem = useItem;
                    wasConsumed = useItem.Item.Usage.ConsumeWhenUse;
                    useSuccess = m_Bag.Content.Use(useItem);
                }
            }

            if (useSuccess && wasConsumed && usedRuntimeItem != null)
            {
                UntrackRuntimeItemRecursive(usedRuntimeItem);
            }

            if (!useSuccess)
            {
                return new NetworkContentUseResponse
                {
                    RequestId = request.RequestId,
                    Authorized = false,
                    RejectionReason = InventoryRejectionReason.CannotUse
                };
            }
            uint stateVersion = GetAuthoritativeStateVersion();
            uint transientUseToken = request.CorrelationId != 0
                ? request.CorrelationId
                : NetworkCorrelation.Compose(NetworkId, request.RequestId);
            if (transientUseToken == 0) transientUseToken = 1u;

            // Broadcast
            var useBroadcast = new NetworkItemUsedBroadcast
            {
                BagNetworkId = NetworkId,
                RuntimeIdHash = usedRuntimeIdHash,
                WasConsumed = wasConsumed,
                // Non-consuming Use is an event, not a bag mutation. Its StateVersion wire slot
                // carries the request correlation token so repeated uses at one bag revision are
                // neither collapsed nor delivered twice. Consuming Use keeps normal revision use.
                StateVersion = wasConsumed ? stateVersion : transientUseToken
            };

            NetworkInventoryManager.Instance?.BroadcastItemUsed(useBroadcast);
            OnItemUsed?.Invoke(useBroadcast);
            CacheCurrentSyncState();

            return new NetworkContentUseResponse
            {
                RequestId = request.RequestId,
                Authorized = true,
                RejectionReason = InventoryRejectionReason.None,
                WasConsumed = wasConsumed,
                StateVersion = stateVersion
            };
        }

        /// <summary>
        /// [Server] Process content drop request.
        /// </summary>
        public NetworkContentDropResponse ProcessContentDropRequest(NetworkContentDropRequest request, uint clientNetworkId)
        {
            LogPickupDebug(
                $"{name}: server drop request received req={request.RequestId} client={clientNetworkId} actor={request.ActorNetworkId} targetBag={request.TargetBagNetworkId} runtime={request.RuntimeIdHash} position={request.DropPosition} controllerBag={NetworkId} hasRuntime={m_RuntimeItemMap.ContainsKey(request.RuntimeIdHash)}",
                this);

            if (!m_IsServer)
            {
                LogPickupWarning($"{name}: drop rejected not server req={request.RequestId} runtime={request.RuntimeIdHash}", this);
                return new NetworkContentDropResponse
                {
                    RequestId = request.RequestId,
                    Authorized = false,
                    RejectionReason = InventoryRejectionReason.NotAuthorized
                };
            }

            if (!m_RuntimeItemMap.TryGetValue(request.RuntimeIdHash, out var dropItem))
            {
                LogPickupWarning(
                    $"{name}: drop rejected runtime not found req={request.RequestId} runtime={request.RuntimeIdHash} trackedItems={m_RuntimeItemMap.Count}",
                    this);
                return new NetworkContentDropResponse
                {
                    RequestId = request.RequestId,
                    Authorized = false,
                    RejectionReason = InventoryRejectionReason.RuntimeItemNotFound
                };
            }

            if (!dropItem.Item.CanDrop)
            {
                LogPickupWarning(
                    $"{name}: drop rejected item cannot drop req={request.RequestId} item={DescribeRuntimeItem(dropItem)}",
                    this);
                return new NetworkContentDropResponse
                {
                    RequestId = request.RequestId,
                    Authorized = false,
                    RejectionReason = InventoryRejectionReason.CannotDrop
                };
            }

            Vector2Int sourcePosition = m_Bag.Content.FindPosition(dropItem.RuntimeID);
            NetworkRuntimeItem droppedItem = ConvertToNetworkItem(dropItem);
            GameObject dropped;
            m_IsApplyingNetworkState = true;
            try
            {
                dropped = m_Bag.Content.Drop(dropItem, request.DropPosition);
            }
            finally
            {
                m_IsApplyingNetworkState = false;
            }

            if (dropped == null)
            {
                LogPickupWarning(
                    $"{name}: drop rejected GC2 Content.Drop returned null req={request.RequestId} item={DescribeRuntimeItem(dropItem)} position={request.DropPosition}",
                    this);
                return new NetworkContentDropResponse
                {
                    RequestId = request.RequestId,
                    Authorized = false,
                    RejectionReason = InventoryRejectionReason.CannotDrop
                };
            }

            UntrackRuntimeItemRecursive(dropItem);
            uint stateVersion = GetAuthoritativeStateVersion();

            var removeBroadcast = new NetworkItemRemovedBroadcast
            {
                BagNetworkId = NetworkId,
                RuntimeIdHash = request.RuntimeIdHash,
                Position = sourcePosition,
                RemainingStackCount = m_Bag.Content.GetContent(sourcePosition)?.Count ?? 0,
                StateVersion = stateVersion
            };

            NetworkInventoryManager.Instance?.BroadcastItemRemoved(removeBroadcast);
            OnItemRemoved?.Invoke(removeBroadcast);

            var dropBroadcast = new NetworkItemDroppedBroadcast
            {
                SourceBagNetworkId = NetworkId,
                Item = droppedItem,
                Position = request.DropPosition
            };

            NetworkInventoryManager.Instance?.BroadcastItemDropped(dropBroadcast);
            RememberDroppedItemInstance(request.RuntimeIdHash, dropped, NetworkId, droppedItem, request.DropPosition);
            RememberServerDroppedWorldItem(request.RuntimeIdHash, NetworkId, droppedItem, request.DropPosition);
            LogPickupDebug(
                $"{name}: drop accepted req={request.RequestId} item={DescribeNetworkItem(droppedItem)} sourcePosition={sourcePosition} dropPosition={request.DropPosition} instance={(dropped != null ? dropped.name : "null")}",
                this);
            CacheCurrentSyncState();

            return new NetworkContentDropResponse
            {
                RequestId = request.RequestId,
                Authorized = true,
                RejectionReason = InventoryRejectionReason.None,
                DroppedCount = 1,
                StateVersion = stateVersion
            };
        }

        /// <summary>
        /// [Server] Process transfer from this bag to another registered bag.
        /// </summary>
        public NetworkTransferResponse ProcessTransferRequest(
            NetworkTransferRequest request,
            NetworkInventoryController destination,
            uint clientNetworkId)
        {
            LogPickupDebug(
                $"{name}: server transfer request received req={request.RequestId} client={clientNetworkId} actor={request.ActorNetworkId} sourceBag={request.SourceBagNetworkId} destinationBag={request.DestinationBagNetworkId} runtime={request.RuntimeIdHash} destination={request.DestinationPosition} sourceControllerBag={NetworkId} destinationControllerBag={(destination != null ? destination.NetworkId : 0)} trackedItems={m_RuntimeItemMap.Count}",
                this);

            if (!m_IsServer || destination == null || !destination.m_IsServer)
            {
                LogPickupWarning(
                    $"{name}: transfer rejected not server-authoritative req={request.RequestId} runtime={request.RuntimeIdHash} sourceServer={m_IsServer} destination={(destination != null ? destination.name : "null")} destinationServer={(destination != null && destination.m_IsServer)}",
                    this);
                return new NetworkTransferResponse
                {
                    RequestId = request.RequestId,
                    Authorized = false,
                    RejectionReason = InventoryRejectionReason.NotAuthorized
                };
            }

            if (destination == this)
            {
                LogPickupWarning($"{name}: transfer rejected source and destination are the same req={request.RequestId}", this);
                return new NetworkTransferResponse
                {
                    RequestId = request.RequestId,
                    Authorized = false,
                    RejectionReason = InventoryRejectionReason.InvalidOperation
                };
            }

            if (!m_RuntimeItemMap.TryGetValue(request.RuntimeIdHash, out RuntimeItem runtimeItem))
            {
                LogPickupWarning(
                    $"{name}: transfer rejected runtime not found req={request.RequestId} runtime={request.RuntimeIdHash} trackedItems={m_RuntimeItemMap.Count}",
                    this);
                return new NetworkTransferResponse
                {
                    RequestId = request.RequestId,
                    Authorized = false,
                    RejectionReason = InventoryRejectionReason.RuntimeItemNotFound
                };
            }

            Vector2Int sourcePosition = m_Bag.Content.FindPosition(runtimeItem.RuntimeID);
            int requestedAmount = Mathf.Max(1, request.Amount);
            Cell sourceCell = m_Bag.Content.GetContent(sourcePosition);
            requestedAmount = Mathf.Min(requestedAmount, sourceCell?.Count ?? 1);
            var transferred = new List<RuntimeItem>(requestedAmount);
            Vector2Int finalPosition = TBagContent.INVALID;

            using (EnterNetworkMutationScope())
            using (destination.EnterNetworkMutationScope())
            {
                for (int index = 0; index < requestedAmount; index++)
                {
                    RuntimeItem next = index == 0
                        ? runtimeItem
                        : m_Bag.Content.GetContent(sourcePosition)?.Peek();
                    RuntimeItem removed = next != null ? m_Bag.Content.Remove(next) : null;
                    if (removed == null) break;

                    Vector2Int placed;
                    if (request.DestinationPosition.x >= 0 && request.DestinationPosition.y >= 0)
                    {
                        bool added = destination.m_Bag.Content.Add(
                            removed, request.DestinationPosition, request.AllowStack);
                        placed = added ? request.DestinationPosition : TBagContent.INVALID;
                    }
                    else if (finalPosition != TBagContent.INVALID && request.AllowStack)
                    {
                        bool added = destination.m_Bag.Content.Add(removed, finalPosition, true);
                        placed = added ? finalPosition : TBagContent.INVALID;
                    }
                    else
                    {
                        placed = destination.m_Bag.Content.Add(removed, request.AllowStack);
                    }

                    if (placed == TBagContent.INVALID)
                    {
                        m_Bag.Content.Add(removed, sourcePosition, true);
                        for (int rollback = transferred.Count - 1; rollback >= 0; rollback--)
                        {
                            RuntimeItem moved = destination.m_Bag.Content.Remove(transferred[rollback]);
                            if (moved != null) m_Bag.Content.Add(moved, sourcePosition, true);
                        }
                        transferred.Clear();
                        break;
                    }

                    finalPosition = placed;
                    transferred.Add(removed);
                }
            }

            if (transferred.Count != requestedAmount)
            {
                RebuildRuntimeItemMap();
                destination.RebuildRuntimeItemMap();
                return new NetworkTransferResponse
                {
                    RequestId = request.RequestId,
                    Authorized = false,
                    RejectionReason = InventoryRejectionReason.InsufficientSpace
                };
            }

            for (int i = 0; i < transferred.Count; i++)
            {
                UntrackRuntimeItemRecursive(transferred[i]);
                destination.TrackRuntimeItemRecursive(transferred[i]);
            }
            uint sourceStateVersion = GetAuthoritativeStateVersion();
            CacheCurrentSyncState();
            destination.CacheCurrentSyncState();

            NetworkInventoryManager manager = NetworkInventoryManager.Instance;
            if (manager != null)
            {
                manager.BroadcastFullSnapshot(GetFullSnapshot());
                manager.BroadcastFullSnapshot(destination.GetFullSnapshot());
            }

            // The response is routed to ActorNetworkId. Report that controller's revision rather
            // than always returning the source Bag revision (for example, a player looting a world
            // chest is the destination actor). A response revision from another Bag can never be
            // satisfied by the actor controller and otherwise causes a false timeout/resync.
            uint actorStateVersion = request.ActorNetworkId == NetworkId
                ? sourceStateVersion
                : request.ActorNetworkId == destination.NetworkId
                    ? destination.CurrentAuthoritativeStateVersion
                    : 0u;

            LogPickupDebug(
                $"{name}: transfer accepted req={request.RequestId} item={DescribeRuntimeItem(transferred[0])} amount={transferred.Count} sourcePosition={sourcePosition} destinationBag={destination.NetworkId} finalPosition={finalPosition}",
                this);

            return new NetworkTransferResponse
            {
                RequestId = request.RequestId,
                Authorized = true,
                RejectionReason = InventoryRejectionReason.None,
                FinalPosition = finalPosition,
                StateVersion = actorStateVersion
            };
        }

        private void BroadcastServerDropFromLocalMutation(NetworkRuntimeItem droppedItem, long runtimeIdHash, Vector3 position)
        {
            if (!m_IsServer || droppedItem.ItemHash == 0) return;

            NetworkInventoryManager manager = NetworkInventoryManager.Instance;
            if (manager == null) return;

            var removeBroadcast = new NetworkItemRemovedBroadcast
            {
                BagNetworkId = NetworkId,
                RuntimeIdHash = runtimeIdHash,
                Position = TBagContent.INVALID,
                RemainingStackCount = 0,
                StateVersion = GetAuthoritativeStateVersion()
            };

            manager.BroadcastItemRemoved(removeBroadcast);

            manager.BroadcastItemDropped(new NetworkItemDroppedBroadcast
            {
                SourceBagNetworkId = NetworkId,
                Item = droppedItem,
                Position = position
            });

            RememberServerDroppedWorldItem(runtimeIdHash, NetworkId, droppedItem, position);
            CacheCurrentSyncState();
        }

        public NetworkPickupResponse ProcessPickupRequest(NetworkPickupRequest request, uint clientNetworkId)
        {
            LogPickupDebug(
                $"{name}: server pickup request received req={request.RequestId} client={clientNetworkId} actor={request.ActorNetworkId} pickerBag={request.PickerBagNetworkId} sourceBag={request.SourceBagNetworkId} runtime={request.RuntimeIdHash} destination={request.DestinationPosition} controllerBag={NetworkId} knownDropped={s_ServerDroppedWorldItems.ContainsKey(request.RuntimeIdHash)} knownDropCount={s_ServerDroppedWorldItems.Count}",
                this);

            if (!m_IsServer)
            {
                LogPickupWarning($"{name}: pickup rejected not server req={request.RequestId} runtime={request.RuntimeIdHash}", this);
                return new NetworkPickupResponse
                {
                    RequestId = request.RequestId,
                    Authorized = false,
                    RejectionReason = InventoryRejectionReason.NotAuthorized
                };
            }

            if (request.PickerBagNetworkId != NetworkId)
            {
                LogPickupWarning(
                    $"{name}: pickup rejected bag mismatch req={request.RequestId} runtime={request.RuntimeIdHash} requestPickerBag={request.PickerBagNetworkId} controllerBag={NetworkId}",
                    this);
                return new NetworkPickupResponse
                {
                    RequestId = request.RequestId,
                    Authorized = false,
                    RejectionReason = InventoryRejectionReason.BagNotFound
                };
            }

            if (!TryGetServerDroppedWorldItem(request.RuntimeIdHash, out ServerDroppedWorldItem droppedWorldItem))
            {
                LogPickupWarning(
                    $"{name}: pickup rejected dropped runtime not found req={request.RequestId} runtime={request.RuntimeIdHash} knownDropCount={s_ServerDroppedWorldItems.Count}",
                    this);
                return new NetworkPickupResponse
                {
                    RequestId = request.RequestId,
                    Authorized = false,
                    RejectionReason = InventoryRejectionReason.RuntimeItemNotFound
                };
            }

            NetworkInventoryManager inventoryManager = NetworkInventoryManager.Instance;
            if (inventoryManager == null ||
                !inventoryManager.IsWithinWorldInteractionRange(this, droppedWorldItem.Position))
            {
                LogPickupWarning(
                    $"{name}: pickup rejected out of range req={request.RequestId} runtime={request.RuntimeIdHash} picker={transform.position} drop={droppedWorldItem.Position} maxDistance={(inventoryManager != null ? inventoryManager.MaxWorldInteractionDistance : 0f)}",
                    this);
                return new NetworkPickupResponse
                {
                    RequestId = request.RequestId,
                    Authorized = false,
                    RejectionReason = InventoryRejectionReason.NotAuthorized
                };
            }

            if (request.SourceBagNetworkId != 0 && droppedWorldItem.SourceBagNetworkId != request.SourceBagNetworkId)
            {
                LogPickupWarning(
                    $"{name}: pickup rejected source mismatch req={request.RequestId} runtime={request.RuntimeIdHash} requestSource={request.SourceBagNetworkId} rememberedSource={droppedWorldItem.SourceBagNetworkId}",
                    this);
                return new NetworkPickupResponse
                {
                    RequestId = request.RequestId,
                    Authorized = false,
                    RejectionReason = InventoryRejectionReason.IdentityMismatch
                };
            }

            RuntimeItem runtimeItem = ReconstructRuntimeItem(droppedWorldItem.Item);
            if (runtimeItem == null)
            {
                LogPickupWarning(
                    $"{name}: pickup rejected failed reconstruct req={request.RequestId} droppedItem={DescribeNetworkItem(droppedWorldItem.Item)}",
                    this);
                return new NetworkPickupResponse
                {
                    RequestId = request.RequestId,
                    Authorized = false,
                    RejectionReason = InventoryRejectionReason.IdentityMismatch
                };
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
                LogPickupWarning(
                    $"{name}: pickup rejected insufficient space req={request.RequestId} item={DescribeRuntimeItem(runtimeItem)} requestedDestination={request.DestinationPosition}",
                    this);
                return new NetworkPickupResponse
                {
                    RequestId = request.RequestId,
                    Authorized = false,
                    RejectionReason = InventoryRejectionReason.InsufficientSpace
                };
            }

            s_ServerDroppedWorldItems.Remove(request.RuntimeIdHash);
            TrackRuntimeItemRecursive(runtimeItem);
            uint stateVersion = GetAuthoritativeStateVersion();

            NetworkInventoryManager manager = NetworkInventoryManager.Instance;
            if (manager != null)
            {
                var addBroadcast = new NetworkItemAddedBroadcast
                {
                    BagNetworkId = NetworkId,
                    Item = ConvertToNetworkItem(runtimeItem),
                    Position = finalPosition,
                    StackCount = m_Bag.Content.GetContent(finalPosition)?.Count ?? 1,
                    StateVersion = stateVersion
                };

                manager.BroadcastItemAdded(addBroadcast);
                OnItemAdded?.Invoke(addBroadcast);

                LogPickupDebug(
                    $"{name}: pickup accepted broadcasting item add req={request.RequestId} pickerBag={NetworkId} sourceBag={droppedWorldItem.SourceBagNetworkId} item={DescribeRuntimeItem(runtimeItem)} finalPosition={finalPosition}",
                    this);

                manager.BroadcastDroppedItemRemoved(new NetworkDroppedItemRemovedBroadcast
                {
                    SourceBagNetworkId = droppedWorldItem.SourceBagNetworkId,
                    RuntimeIdHash = request.RuntimeIdHash,
                    Position = droppedWorldItem.Position
                });
            }

            bool destroyedLocalDrop = TryDestroyDroppedItemInstance(request.RuntimeIdHash);
            LogPickupDebug(
                $"{name}: pickup completed req={request.RequestId} runtime={request.RuntimeIdHash} destroyedServerDropInstance={destroyedLocalDrop} remainingServerDrops={s_ServerDroppedWorldItems.Count}",
                this);
            CacheCurrentSyncState();

            return new NetworkPickupResponse
            {
                RequestId = request.RequestId,
                Authorized = true,
                RejectionReason = InventoryRejectionReason.None,
                PickedUpItem = ConvertToNetworkItem(runtimeItem),
                PlacedPosition = finalPosition,
                StateVersion = stateVersion
            };
        }

        private bool BroadcastServerPickupFromDroppedItemIfNeeded(RuntimeItem item)
        {
            if (!m_IsServer || item == null) return false;
            long removedDropRuntimeHash = item.RuntimeID.Hash;
            if (!TryTakeServerDroppedWorldItem(removedDropRuntimeHash, out ServerDroppedWorldItem droppedWorldItem))
            {
                if (!TryTakeServerDroppedWorldItemForLocalPickup(
                        item,
                        transform.position,
                        out removedDropRuntimeHash,
                        out droppedWorldItem))
                {
                    LogPickupDebug(
                        $"{name}: server local add is not a remembered dropped item={DescribeRuntimeItem(item)} bag={NetworkId} knownDropCount={s_ServerDroppedWorldItems.Count}",
                        this);
                    return false;
                }

                LogPickupDebug(
                    $"{name}: server local pickup matched remembered drop by item/proximity item={DescribeRuntimeItem(item)} rememberedRuntime={removedDropRuntimeHash} sourceBag={droppedWorldItem.SourceBagNetworkId} dropPosition={droppedWorldItem.Position}",
                    this);
            }

            TrackRuntimeItemRecursive(item);
            uint stateVersion = GetAuthoritativeStateVersion();

            Vector2Int position = m_Bag.Content.FindPosition(item.RuntimeID);
            NetworkInventoryManager manager = NetworkInventoryManager.Instance;
            if (manager == null) return false;

            var addBroadcast = new NetworkItemAddedBroadcast
            {
                BagNetworkId = NetworkId,
                Item = ConvertToNetworkItem(item),
                Position = position,
                StackCount = position != TBagContent.INVALID
                    ? m_Bag.Content.GetContent(position)?.Count ?? 1
                    : 1,
                StateVersion = stateVersion
            };

            manager.BroadcastItemAdded(addBroadcast);
            OnItemAdded?.Invoke(addBroadcast);

            manager.BroadcastDroppedItemRemoved(new NetworkDroppedItemRemovedBroadcast
            {
                SourceBagNetworkId = droppedWorldItem.SourceBagNetworkId,
                RuntimeIdHash = removedDropRuntimeHash,
                Position = droppedWorldItem.Position
            });

            bool destroyedLocalDrop = TryDestroyDroppedItemInstance(removedDropRuntimeHash);
            LogPickupDebug(
                $"{name}: server local pickup broadcast item={DescribeRuntimeItem(item)} removedDropRuntime={removedDropRuntimeHash} sourceBag={droppedWorldItem.SourceBagNetworkId} destinationBag={NetworkId} destroyedServerDropInstance={destroyedLocalDrop}",
                this);
            CacheCurrentSyncState();
            return true;
        }

        private void BroadcastServerDirectAdd(RuntimeItem item)
        {
            if (!m_IsServer || item == null) return;
            Vector2Int position = m_Bag.Content.FindPosition(item.RuntimeID);
            if (position == TBagContent.INVALID) return;

            TrackRuntimeItemRecursive(item);
            var broadcast = new NetworkItemAddedBroadcast
            {
                BagNetworkId = NetworkId,
                Item = ConvertToNetworkItem(item),
                Position = position,
                StackCount = m_Bag.Content.GetContent(position)?.Count ?? 1,
                StateVersion = GetAuthoritativeStateVersion()
            };
            NetworkInventoryManager.Instance?.BroadcastItemAdded(broadcast);
            OnItemAdded?.Invoke(broadcast);
            CacheCurrentSyncState();
        }

        private void BroadcastServerDirectRemove(RuntimeItem item)
        {
            if (!m_IsServer || item == null) return;
            var broadcast = new NetworkItemRemovedBroadcast
            {
                BagNetworkId = NetworkId,
                RuntimeIdHash = item.RuntimeID.Hash,
                Position = TBagContent.INVALID,
                RemainingStackCount = 0,
                StateVersion = GetAuthoritativeStateVersion()
            };
            NetworkInventoryManager.Instance?.BroadcastItemRemoved(broadcast);
            OnItemRemoved?.Invoke(broadcast);
            CacheCurrentSyncState();
        }

        private static void RememberServerDroppedWorldItem(
            long runtimeIdHash,
            uint sourceBagNetworkId,
            NetworkRuntimeItem item,
            Vector3 position)
        {
            if (runtimeIdHash == 0) return;

            PruneServerDroppedWorldItems();
            s_ServerDroppedWorldItems[runtimeIdHash] = new ServerDroppedWorldItem
            {
                SourceBagNetworkId = sourceBagNetworkId,
                Item = item,
                Position = position,
                Time = Time.unscaledTime
            };

            LogPickupDebug(
                $"remembered server dropped world item runtime={runtimeIdHash} sourceBag={sourceBagNetworkId} item={DescribeNetworkItem(item)} position={position} knownDropCount={s_ServerDroppedWorldItems.Count}");
        }

        private static bool TryGetServerDroppedWorldItem(long runtimeIdHash, out ServerDroppedWorldItem droppedWorldItem)
        {
            PruneServerDroppedWorldItems();
            return s_ServerDroppedWorldItems.TryGetValue(runtimeIdHash, out droppedWorldItem);
        }

        private static bool TryTakeServerDroppedWorldItem(long runtimeIdHash, out ServerDroppedWorldItem droppedWorldItem)
        {
            PruneServerDroppedWorldItems();

            if (s_ServerDroppedWorldItems.TryGetValue(runtimeIdHash, out droppedWorldItem))
            {
                s_ServerDroppedWorldItems.Remove(runtimeIdHash);
                return true;
            }

            return false;
        }

        private static bool TryTakeServerDroppedWorldItemForLocalPickup(
            RuntimeItem localItem,
            Vector3 pickerPosition,
            out long runtimeIdHash,
            out ServerDroppedWorldItem droppedWorldItem)
        {
            PruneServerDroppedWorldItems();
            runtimeIdHash = 0;
            droppedWorldItem = default;

            if (localItem?.Item == null || s_ServerDroppedWorldItems.Count == 0)
            {
                return false;
            }

            int itemHash = localItem.ItemID.Hash;
            float bestDistance = float.MaxValue;
            long bestRuntimeIdHash = 0;
            ServerDroppedWorldItem bestItem = default;

            foreach (KeyValuePair<long, ServerDroppedWorldItem> entry in s_ServerDroppedWorldItems)
            {
                ServerDroppedWorldItem candidate = entry.Value;
                if (candidate.Item.ItemHash != itemHash) continue;

                float distance = Vector3.SqrMagnitude(candidate.Position - pickerPosition);
                if (distance >= bestDistance) continue;

                bestDistance = distance;
                bestRuntimeIdHash = entry.Key;
                bestItem = candidate;
            }

            if (bestRuntimeIdHash == 0 || bestDistance > 16f)
            {
                return false;
            }

            s_ServerDroppedWorldItems.Remove(bestRuntimeIdHash);
            runtimeIdHash = bestRuntimeIdHash;
            droppedWorldItem = bestItem;
            return true;
        }

        private static void PruneServerDroppedWorldItems()
        {
            if (s_ServerDroppedWorldItems.Count == 0) return;

            s_SharedRuntimeIdBuffer.Clear();
            float now = Time.unscaledTime;
            foreach (KeyValuePair<long, ServerDroppedWorldItem> entry in s_ServerDroppedWorldItems)
            {
                if (now - entry.Value.Time <= 600f) continue;
                s_SharedRuntimeIdBuffer.Add(entry.Key);
            }

            for (int i = 0; i < s_SharedRuntimeIdBuffer.Count; i++)
            {
                s_ServerDroppedWorldItems.Remove(s_SharedRuntimeIdBuffer[i]);
            }
        }

        private void BroadcastServerSocketAttach(RuntimeItem parent, RuntimeItem attachment, IdString socketId)
        {
            if (!m_IsServer || parent == null || attachment == null) return;

            var broadcast = new NetworkSocketChangeBroadcast
            {
                BagNetworkId = NetworkId,
                ParentRuntimeIdHash = parent.RuntimeID.Hash,
                SocketHash = socketId.Hash,
                HasAttachment = true,
                Attachment = ConvertToNetworkItem(attachment),
                StateVersion = GetAuthoritativeStateVersion()
            };

            NetworkInventoryManager.Instance?.BroadcastSocketChange(broadcast);
            OnSocketChanged?.Invoke(broadcast);
            CacheCurrentSyncState();
        }

        private void BroadcastServerSocketDetach(RuntimeItem parent)
        {
            if (!m_IsServer || parent == null) return;

            NetworkInventoryManager.Instance?.BroadcastFullSnapshot(GetFullSnapshot());
            CacheCurrentSyncState();
        }

        /// <summary>
        /// [Server] Process equipment request.
        /// </summary>
        public async Task<NetworkEquipmentResponse> ProcessEquipmentRequest(NetworkEquipmentRequest request, uint clientNetworkId)
        {
            if (!m_IsServer)
            {
                return new NetworkEquipmentResponse
                {
                    RequestId = request.RequestId,
                    Authorized = false,
                    RejectionReason = InventoryRejectionReason.NotAuthorized
                };
            }

            bool equipSuccess = false;
            int equippedIndex = -1;

            switch (request.Action)
            {
                case EquipmentAction.Equip:
                case EquipmentAction.EquipToSlot:
                case EquipmentAction.EquipToIndex:
                {
                    if (!m_RuntimeItemMap.TryGetValue(request.RuntimeIdHash, out var equipItem))
                    {
                        return new NetworkEquipmentResponse
                        {
                            RequestId = request.RequestId,
                            Authorized = false,
                            RejectionReason = InventoryRejectionReason.RuntimeItemNotFound
                        };
                    }

                    if (request.Action == EquipmentAction.EquipToIndex)
                    {
                        equipSuccess = await m_Bag.Equipment.EquipToIndex(equipItem, request.SlotOrIndex);
                        equippedIndex = request.SlotOrIndex;
                    }
                    else if (request.Action == EquipmentAction.EquipToSlot)
                    {
                        equipSuccess = await m_Bag.Equipment.Equip(equipItem, request.SlotOrIndex);
                        equippedIndex = m_Bag.Equipment.GetEquippedIndex(equipItem);
                    }
                    else
                    {
                        equipSuccess = await m_Bag.Equipment.Equip(equipItem);
                        equippedIndex = m_Bag.Equipment.GetEquippedIndex(equipItem);
                    }

                    if (equipSuccess)
                    {
                        var equipBroadcast = new NetworkItemEquippedBroadcast
                        {
                            BagNetworkId = NetworkId,
                            RuntimeIdHash = request.RuntimeIdHash,
                            EquipmentIndex = equippedIndex,
                            StateVersion = GetAuthoritativeStateVersion()
                        };
                        NetworkInventoryManager.Instance?.BroadcastItemEquipped(equipBroadcast);
                        OnItemEquipped?.Invoke(equipBroadcast);
                    }
                    break;
                }

                case EquipmentAction.Unequip:
                {
                    if (!m_RuntimeItemMap.TryGetValue(request.RuntimeIdHash, out var unequipItem))
                    {
                        return new NetworkEquipmentResponse
                        {
                            RequestId = request.RequestId,
                            Authorized = false,
                            RejectionReason = InventoryRejectionReason.RuntimeItemNotFound
                        };
                    }

                    equippedIndex = m_Bag.Equipment.GetEquippedIndex(unequipItem);
                    equipSuccess = await m_Bag.Equipment.Unequip(unequipItem);

                    if (equipSuccess)
                    {
                        var unequipBroadcast = new NetworkItemUnequippedBroadcast
                        {
                            BagNetworkId = NetworkId,
                            RuntimeIdHash = request.RuntimeIdHash,
                            EquipmentIndex = equippedIndex,
                            StateVersion = GetAuthoritativeStateVersion()
                        };
                        NetworkInventoryManager.Instance?.BroadcastItemUnequipped(unequipBroadcast);
                        OnItemUnequipped?.Invoke(unequipBroadcast);
                    }
                    break;
                }

                case EquipmentAction.UnequipFromIndex:
                {
                    var slotId = m_Bag.Equipment.GetSlotRootRuntimeItemID(request.SlotOrIndex);
                    long runtimeIdHash = slotId.Hash;

                    equipSuccess = await m_Bag.Equipment.UnequipFromIndex(request.SlotOrIndex);
                    equippedIndex = request.SlotOrIndex;

                    if (equipSuccess)
                    {
                        var unequipIdxBroadcast = new NetworkItemUnequippedBroadcast
                        {
                            BagNetworkId = NetworkId,
                            RuntimeIdHash = runtimeIdHash,
                            EquipmentIndex = equippedIndex,
                            StateVersion = GetAuthoritativeStateVersion()
                        };
                        NetworkInventoryManager.Instance?.BroadcastItemUnequipped(unequipIdxBroadcast);
                        OnItemUnequipped?.Invoke(unequipIdxBroadcast);
                    }
                    break;
                }
            }

            if (equipSuccess) CacheCurrentSyncState();

            return new NetworkEquipmentResponse
            {
                RequestId = request.RequestId,
                Authorized = equipSuccess,
                RejectionReason = equipSuccess ? InventoryRejectionReason.None : InventoryRejectionReason.CannotEquip,
                EquippedIndex = equippedIndex,
                StateVersion = equipSuccess ? GetAuthoritativeStateVersion() : 0u
            };
        }

        /// <summary>
        /// [Server] Process socket request.
        /// </summary>
        public NetworkSocketResponse ProcessSocketRequest(NetworkSocketRequest request, uint clientNetworkId)
        {
            if (!m_IsServer)
            {
                return new NetworkSocketResponse
                {
                    RequestId = request.RequestId,
                    Authorized = false,
                    RejectionReason = InventoryRejectionReason.NotAuthorized
                };
            }

            if (!m_RuntimeItemMap.TryGetValue(request.ParentRuntimeIdHash, out var parentItem))
            {
                return new NetworkSocketResponse
                {
                    RequestId = request.RequestId,
                    Authorized = false,
                    RejectionReason = InventoryRejectionReason.RuntimeItemNotFound
                };
            }

            bool socketSuccess = false;
            NetworkRuntimeItem detachedItem = default;
            NetworkRuntimeItem attachedItem = default;
            int usedSocketHash = request.SocketHash;
            IdString socketId = IdString.EMPTY;

            if (request.Action == SocketAction.AttachToSocket || request.Action == SocketAction.DetachFromSocket)
            {
                if (!TryResolveSocketId(parentItem, request.SocketHash, request.SocketIdString, out socketId))
                {
                    return new NetworkSocketResponse
                    {
                        RequestId = request.RequestId,
                        Authorized = false,
                        RejectionReason = InventoryRejectionReason.IdentityMismatch
                    };
                }

                usedSocketHash = socketId.Hash;
            }

            // Suppress native socket/equipment callbacks and the attachment's raw Remove while
            // the server commits this one semantic operation. The explicit broadcast below is
            // therefore the only authoritative socket message.
            using (EnterNetworkMutationScope())
            {
                switch (request.Action)
                {
                    case SocketAction.Attach:
                    case SocketAction.AttachToSocket:
                    {
                        if (!m_RuntimeItemMap.TryGetValue(request.AttachmentRuntimeIdHash, out var attachment))
                        {
                            return new NetworkSocketResponse
                            {
                                RequestId = request.RequestId,
                                Authorized = false,
                                RejectionReason = InventoryRejectionReason.RuntimeItemNotFound
                            };
                        }

                        if (request.Action == SocketAction.AttachToSocket)
                        {
                            socketSuccess = m_Bag.Equipment.AttachTo(parentItem, attachment, socketId);
                        }
                        else
                        {
                            socketSuccess = m_Bag.Equipment.AttachTo(parentItem, attachment);
                        }

                        if (socketSuccess)
                        {
                            attachedItem = ConvertToNetworkItem(attachment);
                        }
                        break;
                    }

                    case SocketAction.Detach:
                    case SocketAction.DetachFromSocket:
                    {
                        RuntimeItem detached;
                        if (request.Action == SocketAction.DetachFromSocket)
                        {
                            detached = m_Bag.Equipment.DetachFrom(parentItem, socketId);
                        }
                        else
                        {
                            if (!m_RuntimeItemMap.TryGetValue(request.AttachmentRuntimeIdHash, out var detachAttachment))
                            {
                                return new NetworkSocketResponse
                                {
                                    RequestId = request.RequestId,
                                    Authorized = false,
                                    RejectionReason = InventoryRejectionReason.RuntimeItemNotFound
                                };
                            }
                            detached = m_Bag.Equipment.DetachFrom(parentItem, detachAttachment);
                        }

                        socketSuccess = detached != null;
                        if (socketSuccess)
                        {
                            detachedItem = ConvertToNetworkItem(detached);
                        }
                        break;
                    }
                }
            }

            if (socketSuccess)
            {
                var socketBroadcast = new NetworkSocketChangeBroadcast
                {
                    BagNetworkId = NetworkId,
                    ParentRuntimeIdHash = request.ParentRuntimeIdHash,
                    SocketHash = usedSocketHash,
                    HasAttachment = request.Action == SocketAction.Attach || request.Action == SocketAction.AttachToSocket,
                    Attachment = attachedItem,
                    StateVersion = GetAuthoritativeStateVersion()
                };
                NetworkInventoryManager.Instance?.BroadcastSocketChange(socketBroadcast);
                OnSocketChanged?.Invoke(socketBroadcast);
                CacheCurrentSyncState();
            }

            return new NetworkSocketResponse
            {
                RequestId = request.RequestId,
                Authorized = socketSuccess,
                RejectionReason = socketSuccess ? InventoryRejectionReason.None : InventoryRejectionReason.CannotAttach,
                UsedSocketHash = usedSocketHash,
                DetachedItem = detachedItem,
                StateVersion = socketSuccess ? GetAuthoritativeStateVersion() : 0u
            };
        }

        /// <summary>
        /// [Server] Process wealth request.
        /// </summary>
        public NetworkWealthResponse ProcessWealthRequest(NetworkWealthRequest request, uint clientNetworkId)
        {
            if (!m_IsServer)
            {
                return new NetworkWealthResponse
                {
                    RequestId = request.RequestId,
                    Authorized = false,
                    RejectionReason = InventoryRejectionReason.NotAuthorized
                };
            }

            if (!TryResolveCurrencyId(request.CurrencyHash, request.CurrencyIdString, out IdString currencyId))
            {
                return new NetworkWealthResponse
                {
                    RequestId = request.RequestId,
                    Authorized = false,
                    RejectionReason = InventoryRejectionReason.IdentityMismatch
                };
            }

            int oldValue = m_Bag.Wealth.Get(currencyId);
            int requestedValue;

            switch (request.Action)
            {
                case WealthAction.Set:
                    if (request.Value < 0)
                    {
                        return new NetworkWealthResponse
                        {
                            RequestId = request.RequestId,
                            Authorized = false,
                            RejectionReason = InventoryRejectionReason.InvalidOperation
                        };
                    }
                    requestedValue = request.Value;
                    break;

                case WealthAction.Add:
                    if (request.Value <= 0 || (long)oldValue + request.Value > int.MaxValue)
                    {
                        return new NetworkWealthResponse
                        {
                            RequestId = request.RequestId,
                            Authorized = false,
                            RejectionReason = InventoryRejectionReason.InvalidOperation
                        };
                    }
                    requestedValue = oldValue + request.Value;
                    break;

                case WealthAction.Subtract:
                    if (request.Value <= 0)
                    {
                        return new NetworkWealthResponse
                        {
                            RequestId = request.RequestId,
                            Authorized = false,
                            RejectionReason = InventoryRejectionReason.InvalidOperation
                        };
                    }
                    if (oldValue < request.Value)
                    {
                        return new NetworkWealthResponse
                        {
                            RequestId = request.RequestId,
                            Authorized = false,
                            RejectionReason = InventoryRejectionReason.InsufficientFunds
                        };
                    }
                    requestedValue = oldValue - request.Value;
                    break;

                default:
                    return new NetworkWealthResponse
                    {
                        RequestId = request.RequestId,
                        Authorized = false,
                        RejectionReason = InventoryRejectionReason.InvalidOperation
                    };
            }

            using (EnterNetworkMutationScope())
            {
                m_Bag.Wealth.Set(currencyId, requestedValue);
            }

            // Treat the GC2 bag as the source of truth. Custom inventory implementations may
            // clamp or otherwise normalize values, so responses and broadcasts must describe the
            // actual post-mutation balance rather than client-supplied arithmetic.
            int newValue = m_Bag.Wealth.Get(currencyId);

            // Broadcast
            var wealthBroadcast = new NetworkWealthChangeBroadcast
            {
                BagNetworkId = NetworkId,
                CurrencyHash = request.CurrencyHash,
                NewValue = newValue,
                Change = newValue - oldValue,
                StateVersion = GetAuthoritativeStateVersion()
            };
            NetworkInventoryManager.Instance?.BroadcastWealthChange(wealthBroadcast);
            OnWealthChanged?.Invoke(wealthBroadcast);
            CacheCurrentSyncState();

            return new NetworkWealthResponse
            {
                RequestId = request.RequestId,
                Authorized = true,
                RejectionReason = InventoryRejectionReason.None,
                NewValue = newValue,
                OldValue = oldValue,
                StateVersion = GetAuthoritativeStateVersion()
            };
        }

        #endregion

        // ════════════════════════════════════════════════════════════════════════════════════════
        // BROADCAST RECEIVERS
        // ════════════════════════════════════════════════════════════════════════════════════════

        #region Broadcast Receivers

        public void ReceiveItemAddedBroadcast(NetworkItemAddedBroadcast broadcast)
        {
            if (m_IsServer) return;
            EnqueueStateApplication(() =>
            {
                ApplyItemAddedBroadcast(broadcast);
                return Task.CompletedTask;
            });
        }

        private void ApplyItemAddedBroadcast(NetworkItemAddedBroadcast broadcast)
        {
            if (m_IsServer) return;
            if (!TryAcceptMutationVersion(broadcast.StateVersion)) return;

            bool converged = false;
            m_IsApplyingNetworkState = true;
            try
            {
                if (broadcast.Item.RuntimeIdHash != 0 &&
                    m_PendingPickupLocalRuntimeByServerRuntime.TryGetValue(
                        broadcast.Item.RuntimeIdHash,
                        out long provisionalRuntimeHash))
                {
                    m_PendingPickupLocalRuntimeByServerRuntime.Remove(broadcast.Item.RuntimeIdHash);

                    if (provisionalRuntimeHash != broadcast.Item.RuntimeIdHash &&
                        m_RuntimeItemMap.TryGetValue(provisionalRuntimeHash, out RuntimeItem provisionalItem))
                    {
                        Vector2Int provisionalPosition = m_Bag.Content.FindPosition(provisionalItem.RuntimeID);
                        m_Bag.Content.Remove(provisionalItem);
                        UntrackRuntimeItemRecursive(provisionalItem);

                        LogPickupDebug(
                            $"{name}: removed provisional local pickup item before authoritative add localRuntime={provisionalRuntimeHash} serverRuntime={broadcast.Item.RuntimeIdHash} provisionalPosition={provisionalPosition}",
                            this);
                    }
                    else
                    {
                        LogPickupDebug(
                            $"{name}: authoritative pickup add matched existing runtime serverRuntime={broadcast.Item.RuntimeIdHash}",
                            this);
                    }
                }

                if (broadcast.Item.RuntimeIdHash != 0 &&
                    (m_RuntimeItemMap.ContainsKey(broadcast.Item.RuntimeIdHash) ||
                     m_Bag.Content.Contains(new IdString(broadcast.Item.RuntimeIdString))))
                {
                    LogPickupDebug(
                        $"{name}: item add broadcast skipped duplicate bag={broadcast.BagNetworkId} item={DescribeNetworkItem(broadcast.Item)} position={broadcast.Position}",
                        this);
                    converged = true;
                }
                else
                {
                    // Reconstruct and apply
                    var runtimeItem = ReconstructRuntimeItem(broadcast.Item);
                    if (runtimeItem != null)
                    {
                        bool addedAtPosition = m_Bag.Content.Add(runtimeItem, broadcast.Position, true);
                        if (addedAtPosition) TrackRuntimeItemRecursive(runtimeItem);
                        converged = addedAtPosition;
                        LogPickupDebug(
                            $"{name}: item add broadcast applied bag={broadcast.BagNetworkId} item={DescribeRuntimeItem(runtimeItem)} requestedPosition={broadcast.Position} addedAtPosition={addedAtPosition} tracked={m_RuntimeItemMap.ContainsKey(runtimeItem.RuntimeID.Hash)}",
                            this);
                    }
                    else
                    {
                        LogPickupWarning(
                            $"{name}: item add broadcast failed reconstruct bag={broadcast.BagNetworkId} item={DescribeNetworkItem(broadcast.Item)}",
                            this);
                    }
                }
            }
            finally
            {
                m_IsApplyingNetworkState = false;
            }

            if (!FinishMutationVersion(broadcast.StateVersion, converged, "item add")) return;
            OnItemAdded?.Invoke(broadcast);
        }

        public void ReceiveItemRemovedBroadcast(NetworkItemRemovedBroadcast broadcast)
        {
            if (m_IsServer) return;
            EnqueueStateApplication(() =>
            {
                ApplyItemRemovedBroadcast(broadcast);
                return Task.CompletedTask;
            });
        }

        private void ApplyItemRemovedBroadcast(NetworkItemRemovedBroadcast broadcast)
        {
            if (m_IsServer) return;
            if (!TryAcceptMutationVersion(broadcast.StateVersion)) return;

            bool converged = true;
            m_IsApplyingNetworkState = true;
            try
            {
                if (m_RuntimeItemMap.TryGetValue(broadcast.RuntimeIdHash, out var runtimeItem))
                {
                    RuntimeItem removed = m_Bag.Content.Remove(runtimeItem);
                    converged = removed != null;
                    if (removed != null) UntrackRuntimeItemRecursive(removed);
                }
            }
            finally
            {
                m_IsApplyingNetworkState = false;
            }
            if (!FinishMutationVersion(broadcast.StateVersion, converged, "item remove")) return;
            OnItemRemoved?.Invoke(broadcast);
        }

        public void ReceiveItemMovedBroadcast(NetworkItemMovedBroadcast broadcast)
        {
            if (m_IsServer) return;
            EnqueueStateApplication(() =>
            {
                ApplyItemMovedBroadcast(broadcast);
                return Task.CompletedTask;
            });
        }

        private void ApplyItemMovedBroadcast(NetworkItemMovedBroadcast broadcast)
        {
            if (m_IsServer) return;
            if (!TryAcceptMutationVersion(broadcast.StateVersion)) return;

            bool converged;
            m_IsApplyingNetworkState = true;
            try
            {
                converged = broadcast.FromPosition == broadcast.ToPosition ||
                            m_Bag.Content.Move(broadcast.FromPosition, broadcast.ToPosition, true);
            }
            finally
            {
                m_IsApplyingNetworkState = false;
            }
            if (!FinishMutationVersion(broadcast.StateVersion, converged, "item move")) return;
            OnItemMoved?.Invoke(broadcast);
        }

        public void ReceiveItemUsedBroadcast(NetworkItemUsedBroadcast broadcast)
        {
            if (m_IsServer) return;
            EnqueueStateApplication(() =>
            {
                ApplyItemUsedBroadcast(broadcast);
                return Task.CompletedTask;
            });
        }

        private void ApplyItemUsedBroadcast(NetworkItemUsedBroadcast broadcast)
        {
            if (m_IsServer) return;
            if (!broadcast.WasConsumed)
            {
                if (!TryAcceptTransientUseEvent(broadcast)) return;
                OnItemUsed?.Invoke(broadcast);
                return;
            }

            if (!TryAcceptMutationVersion(broadcast.StateVersion)) return;

            bool converged = true;
            m_IsApplyingNetworkState = true;
            try
            {
                if (broadcast.WasConsumed && m_RuntimeItemMap.TryGetValue(broadcast.RuntimeIdHash, out var runtimeItem))
                {
                    RuntimeItem removed = m_Bag.Content.Remove(runtimeItem);
                    converged = removed != null;
                    if (removed != null) UntrackRuntimeItemRecursive(removed);
                }
            }
            finally
            {
                m_IsApplyingNetworkState = false;
            }
            if (!FinishMutationVersion(broadcast.StateVersion, converged, "item use")) return;
            OnItemUsed?.Invoke(broadcast);
        }

        private bool TryAcceptTransientUseEvent(NetworkItemUsedBroadcast broadcast)
        {
            // A zero token can only come from an older peer. Preserve compatibility by delivering
            // it, but exact deduplication requires matching v3 peers and a non-zero correlation.
            if (broadcast.StateVersion == 0) return true;

            var key = new TransientUseEventKey(broadcast.RuntimeIdHash, broadcast.StateVersion);
            if (!m_ReceivedTransientUseEvents.Add(key)) return false;

            m_ReceivedTransientUseEventOrder.Enqueue(key);
            while (m_ReceivedTransientUseEventOrder.Count > MAX_RECEIVED_TRANSIENT_USE_EVENTS)
            {
                m_ReceivedTransientUseEvents.Remove(m_ReceivedTransientUseEventOrder.Dequeue());
            }

            return true;
        }

        public void ReceiveItemDroppedBroadcast(NetworkItemDroppedBroadcast broadcast)
        {
            if (m_IsServer) return;
            if (TryAdoptPredictedDroppedItemInstance(broadcast))
            {
                LogPickupDebug(
                    $"{name}: dropped item broadcast adopted local predicted drop sourceBag={broadcast.SourceBagNetworkId} item={DescribeNetworkItem(broadcast.Item)} position={broadcast.Position}",
                    this);
                return;
            }

            RuntimeItem runtimeItem = ReconstructRuntimeItem(broadcast.Item);
            if (runtimeItem == null)
            {
                LogPickupWarning(
                    $"{name}: dropped item broadcast failed reconstruct sourceBag={broadcast.SourceBagNetworkId} item={DescribeNetworkItem(broadcast.Item)}",
                    this);
                return;
            }

            m_IsApplyingNetworkState = true;
            try
            {
                GameObject instance = Item.Drop(runtimeItem, broadcast.Position, Quaternion.identity);
                RememberDroppedItemInstance(
                    broadcast.Item.RuntimeIdHash,
                    instance,
                    broadcast.SourceBagNetworkId,
                    broadcast.Item,
                    broadcast.Position);
                LogPickupDebug(
                    $"{name}: dropped item broadcast spawned instance sourceBag={broadcast.SourceBagNetworkId} item={DescribeRuntimeItem(runtimeItem)} position={broadcast.Position} instance={(instance != null ? instance.name : "null")}",
                    this);
            }
            finally
            {
                m_IsApplyingNetworkState = false;
            }
        }

        public void ReceiveDroppedItemRemovedBroadcast(NetworkDroppedItemRemovedBroadcast broadcast)
        {
            if (m_IsServer) return;
            bool destroyed = TryDestroyDroppedItemInstance(broadcast, out long destroyedRuntimeIdHash);
            LogPickupDebug(
                $"{name}: dropped item remove broadcast sourceBag={broadcast.SourceBagNetworkId} runtime={broadcast.RuntimeIdHash} destroyed={destroyed} destroyedRuntime={destroyedRuntimeIdHash} position={broadcast.Position}",
                this);
        }

        public void ReceiveItemEquippedBroadcast(NetworkItemEquippedBroadcast broadcast)
        {
            if (m_IsServer) return;
            EnqueueStateApplication(() => ApplyItemEquippedBroadcastAsync(broadcast));
        }

        private async Task ApplyItemEquippedBroadcastAsync(NetworkItemEquippedBroadcast broadcast)
        {
            if (m_IsServer) return;
            if (!TryAcceptMutationVersion(broadcast.StateVersion)) return;

            bool converged = false;
            IDisposable mutationScope = EnterNetworkMutationScope();
            try
            {
                if (m_RuntimeItemMap.TryGetValue(broadcast.RuntimeIdHash, out var runtimeItem))
                {
                    IdString equippedId = m_Bag.Equipment.GetSlotRootRuntimeItemID(
                        broadcast.EquipmentIndex);
                    converged = equippedId.Hash == broadcast.RuntimeIdHash ||
                                await m_Bag.Equipment.EquipToIndex(
                                    runtimeItem,
                                    broadcast.EquipmentIndex);
                }
            }
            catch (Exception exception)
            {
                Debug.LogException(exception, this);
            }
            finally
            {
                mutationScope.Dispose();
            }

            if (!FinishMutationVersion(broadcast.StateVersion, converged, "item equip")) return;
            OnItemEquipped?.Invoke(broadcast);
        }

        public void ReceiveItemUnequippedBroadcast(NetworkItemUnequippedBroadcast broadcast)
        {
            if (m_IsServer) return;
            EnqueueStateApplication(() => ApplyItemUnequippedBroadcastAsync(broadcast));
        }

        private async Task ApplyItemUnequippedBroadcastAsync(NetworkItemUnequippedBroadcast broadcast)
        {
            if (m_IsServer) return;
            if (!TryAcceptMutationVersion(broadcast.StateVersion)) return;

            bool converged = false;
            IDisposable mutationScope = EnterNetworkMutationScope();
            try
            {
                IdString equippedId = m_Bag.Equipment.GetSlotRootRuntimeItemID(
                    broadcast.EquipmentIndex);
                converged = string.IsNullOrEmpty(equippedId.String) ||
                            await m_Bag.Equipment.UnequipFromIndex(
                                broadcast.EquipmentIndex);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception, this);
            }
            finally
            {
                mutationScope.Dispose();
            }

            if (!FinishMutationVersion(broadcast.StateVersion, converged, "item unequip")) return;
            OnItemUnequipped?.Invoke(broadcast);
        }

        public void ReceiveSocketChangeBroadcast(NetworkSocketChangeBroadcast broadcast)
        {
            if (m_IsServer) return;
            EnqueueStateApplication(() =>
            {
                ApplySocketChangeBroadcast(broadcast);
                return Task.CompletedTask;
            });
        }

        private void ApplySocketChangeBroadcast(NetworkSocketChangeBroadcast broadcast)
        {
            if (m_IsServer) return;
            if (!TryAcceptMutationVersion(broadcast.StateVersion)) return;

            bool converged = false;
            m_IsApplyingNetworkState = true;
            try
            {
                if (!m_RuntimeItemMap.TryGetValue(broadcast.ParentRuntimeIdHash, out RuntimeItem parentItem))
                {
                    converged = false;
                }
                else if (!TryResolveRuntimeSocketId(parentItem, broadcast.SocketHash, null, out IdString socketId) ||
                         !parentItem.Sockets.TryGetValue(socketId, out RuntimeSocket socket))
                {
                    converged = false;
                }
                else
                {
                    RuntimeItem previousAttachment = socket.Attachment;
                    RuntimeItem nextAttachment = null;
                    if (broadcast.HasAttachment)
                    {
                        RuntimeItem attachment = null;
                        if (broadcast.Attachment.RuntimeIdHash != 0)
                        {
                            m_RuntimeItemMap.TryGetValue(
                                broadcast.Attachment.RuntimeIdHash,
                                out attachment);
                        }

                        attachment ??= ReconstructRuntimeItem(broadcast.Attachment);
                        if (attachment != null && s_RuntimeSocketAttachmentField != null)
                        {
                            if (m_Bag.Content.Contains(attachment))
                            {
                                m_Bag.Content.Remove(attachment);
                            }

                            s_RuntimeSocketAttachmentField.SetValue(socket, attachment);
                            TrackRuntimeItemRecursive(attachment);
                            nextAttachment = attachment;
                            converged = true;
                        }
                    }
                    else if (s_RuntimeSocketAttachmentField != null)
                    {
                        s_RuntimeSocketAttachmentField.SetValue(socket, null);
                        converged = true;
                    }

                    if (previousAttachment != null &&
                        (nextAttachment == null || previousAttachment.RuntimeID.Hash != nextAttachment.RuntimeID.Hash))
                    {
                        UntrackRuntimeItemRecursive(previousAttachment);
                    }
                }
            }
            finally
            {
                m_IsApplyingNetworkState = false;
            }

            if (!FinishMutationVersion(broadcast.StateVersion, converged, "socket change")) return;
            OnSocketChanged?.Invoke(broadcast);
        }

        public void ReceiveWealthChangeBroadcast(NetworkWealthChangeBroadcast broadcast)
        {
            if (m_IsServer) return;
            EnqueueStateApplication(() =>
            {
                ApplyWealthChangeBroadcast(broadcast);
                return Task.CompletedTask;
            });
        }

        private void ApplyWealthChangeBroadcast(NetworkWealthChangeBroadcast broadcast)
        {
            if (m_IsServer) return;
            if (!TryAcceptMutationVersion(broadcast.StateVersion)) return;

            bool converged = false;
            m_IsApplyingNetworkState = true;
            try
            {
                if (TryResolveCurrencyIdByHash(broadcast.CurrencyHash, out IdString currencyId))
                {
                    if (m_Bag.Wealth.Get(currencyId) != broadcast.NewValue)
                    {
                        m_Bag.Wealth.Set(currencyId, broadcast.NewValue);
                    }
                    converged = m_Bag.Wealth.Get(currencyId) == broadcast.NewValue;
                }
            }
            finally
            {
                m_IsApplyingNetworkState = false;
            }

            if (!FinishMutationVersion(broadcast.StateVersion, converged, "wealth change")) return;
            OnWealthChanged?.Invoke(broadcast);
        }

        public void ReceivePropertyChangeBroadcast(NetworkPropertyChangeBroadcast broadcast)
        {
            if (m_IsServer) return;
            EnqueueStateApplication(() =>
            {
                ApplyPropertyChangeBroadcast(broadcast);
                return Task.CompletedTask;
            });
        }

        private void ApplyPropertyChangeBroadcast(NetworkPropertyChangeBroadcast broadcast)
        {
            if (m_IsServer) return;
            if (!TryAcceptMutationVersion(broadcast.StateVersion)) return;

            bool converged = false;
            m_IsApplyingNetworkState = true;
            try
            {
                if (!m_RuntimeItemMap.TryGetValue(broadcast.RuntimeIdHash, out RuntimeItem runtimeItem) ||
                    !TryResolveRuntimePropertyId(runtimeItem, broadcast.PropertyHash, null, out IdString propertyId) ||
                    !runtimeItem.Properties.TryGetValue(propertyId, out RuntimeProperty runtimeProperty))
                {
                    converged = false;
                }
                else
                {
                    if (!runtimeProperty.Number.Equals(broadcast.NewNumber))
                    {
                        runtimeProperty.Number = broadcast.NewNumber;
                    }
                    if (!string.Equals(runtimeProperty.Text, broadcast.NewText, StringComparison.Ordinal))
                    {
                        runtimeProperty.Text = broadcast.NewText;
                    }
                    converged = runtimeProperty.Number.Equals(broadcast.NewNumber) &&
                                string.Equals(
                                    runtimeProperty.Text,
                                    broadcast.NewText,
                                    StringComparison.Ordinal);
                }
            }
            finally
            {
                m_IsApplyingNetworkState = false;
            }

            FinishMutationVersion(broadcast.StateVersion, converged, "property change");
        }

        public void ReceiveFullSnapshot(NetworkInventorySnapshot snapshot)
        {
            if (m_IsServer) return;
            EnqueueStateApplication(() => ApplyFullSnapshotMessageAsync(snapshot));
        }

        private async Task ApplyFullSnapshotMessageAsync(NetworkInventorySnapshot snapshot)
        {
            if (m_IsServer) return;

            if (snapshot.BagNetworkId != 0 && snapshot.BagNetworkId != NetworkId)
            {
                return;
            }

            if (IsStaleStateVersion(snapshot.StateVersion)) return;

            IDisposable mutationScope = EnterNetworkMutationScope();
            try
            {
                await ApplyFullSnapshot(snapshot);
            }
            finally
            {
                mutationScope.Dispose();
            }

            RecordAppliedStateVersion(snapshot.StateVersion);
            m_FullSnapshotApplyCount = m_FullSnapshotApplyCount == uint.MaxValue
                ? 1u
                : m_FullSnapshotApplyCount + 1u;

            if (m_LogAllChanges)
            {
                Debug.Log($"[NetworkInventoryController] Received full snapshot: {snapshot.Cells?.Length ?? 0} cells");
            }
        }

        public void ReceiveDelta(NetworkInventoryDelta delta)
        {
            if (m_IsServer) return;
            EnqueueStateApplication(() => ApplyDeltaMessageAsync(delta));
        }

        private async Task ApplyDeltaMessageAsync(NetworkInventoryDelta delta)
        {
            if (m_IsServer) return;

            if (delta.BagNetworkId != 0 && delta.BagNetworkId != NetworkId)
            {
                return;
            }

            if (!TryAcceptMutationVersion(delta.StateVersion)) return;

            const uint maskCells = 1u << 0;
            const uint maskEquipment = 1u << 1;
            const uint maskWealth = 1u << 2;

            IDisposable mutationScope = EnterNetworkMutationScope();
            try
            {
                if ((delta.ChangeMask & maskCells) != 0 && delta.ChangedCells != null)
                {
                    ApplyCellDelta(delta.ChangedCells);
                }

                if ((delta.ChangeMask & maskEquipment) != 0 && delta.ChangedEquipment != null)
                {
                    await ApplyEquipmentDelta(delta.ChangedEquipment);
                }

                if ((delta.ChangeMask & maskWealth) != 0 && delta.ChangedWealth != null)
                {
                    ApplyWealthDelta(delta.ChangedWealth);
                }
            }
            finally
            {
                mutationScope.Dispose();
            }

            CacheCurrentSyncState();
            RecordAppliedStateVersion(delta.StateVersion);

            if (m_LogAllChanges)
            {
                Debug.Log(
                    $"[NetworkInventoryController] Applied partial delta (mask={delta.ChangeMask}) " +
                    $"cells={delta.ChangedCells?.Length ?? 0} " +
                    $"equipment={delta.ChangedEquipment?.Length ?? 0} " +
                    $"wealth={delta.ChangedWealth?.Length ?? 0}");
            }
        }

        #endregion
    }
}
#endif
