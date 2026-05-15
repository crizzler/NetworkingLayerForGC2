#if GC2_INVENTORY
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.Inventory;

namespace Arawn.GameCreator2.Networking.Inventory
{
    // ════════════════════════════════════════════════════════════════════════════════════════════
    // CLIENT-SIDE — Requests, response handlers, and local change detection
    // ════════════════════════════════════════════════════════════════════════════════════════════
    
    public partial class NetworkInventoryController
    {
        // ════════════════════════════════════════════════════════════════════════════════════════
        // CLIENT-SIDE: REQUEST OPERATIONS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        #region Content Requests
        
        /// <summary>
        /// Request to add an item type to the bag.
        /// </summary>
        public void RequestAddItem(Item item, Vector2Int position, bool allowStack,
            InventoryModificationSource source = InventoryModificationSource.Direct, int sourceHash = 0)
        {
            if (m_IsRemoteClient)
            {
                Debug.LogWarning("[NetworkInventoryController] Cannot modify inventory on remote client");
                return;
            }
            
            var request = new NetworkContentAddRequest
            {
                RequestId = GetNextRequestId(),
                ActorNetworkId = NetworkId,
                CorrelationId = NetworkCorrelation.Compose(NetworkId, m_LastIssuedRequestId),
                TargetBagNetworkId = NetworkId,
                ItemHash = item.ID.Hash,
                ItemIdString = item.ID.String,
                Position = position,
                AllowStack = allowStack,
                Source = source,
                SourceHash = sourceHash
            };
            
            m_PendingAdds[GetPendingKey(request.ActorNetworkId, request.CorrelationId, request.RequestId)] = new PendingContentAdd
            {
                Request = request,
                SentTime = Time.time
            };
            
            OnContentAddRequested?.Invoke(request);
            
            if (m_IsServer)
            {
                var response = ProcessContentAddRequest(request, NetworkId);
                response.ActorNetworkId = request.ActorNetworkId;
                response.CorrelationId = request.CorrelationId;
                ReceiveContentAddResponse(response);
            }
            else
            {
                NetworkInventoryManager.Instance?.SendContentAddRequest(request);
            }
        }
        
        /// <summary>
        /// Request to add an existing RuntimeItem to the bag.
        /// </summary>
        public void RequestAddRuntimeItem(NetworkRuntimeItem runtimeItem, Vector2Int position, bool allowStack,
            InventoryModificationSource source = InventoryModificationSource.Direct, int sourceHash = 0)
        {
            if (m_IsRemoteClient) return;

            // Arbitrary runtime payload creation is server-authorized only.
            if (!m_IsServer)
            {
                if (m_LogRejections)
                {
                    Debug.LogWarning("[NetworkInventoryController] RequestAddRuntimeItem is server-authorized only");
                }
                OnOperationRejected?.Invoke(InventoryRejectionReason.SecurityViolation, "Add runtime item");
                return;
            }

            if (runtimeItem.ItemHash == 0)
            {
                if (m_LogRejections)
                {
                    Debug.LogWarning("[NetworkInventoryController] Runtime item payload missing item hash");
                }
                OnOperationRejected?.Invoke(InventoryRejectionReason.IdentityMismatch, "Add runtime item");
                return;
            }

            var request = new NetworkContentAddRequest
            {
                RequestId = GetNextRequestId(),
                ActorNetworkId = NetworkId,
                CorrelationId = NetworkCorrelation.Compose(NetworkId, m_LastIssuedRequestId),
                TargetBagNetworkId = NetworkId,
                // Server-authorized flow resolves by deterministic item hash.
                ItemHash = runtimeItem.ItemHash,
                ItemIdString = string.Empty,
                RuntimeItem = runtimeItem,
                Position = position,
                AllowStack = allowStack,
                Source = source,
                SourceHash = sourceHash
            };
            
            m_PendingAdds[GetPendingKey(request.ActorNetworkId, request.CorrelationId, request.RequestId)] = new PendingContentAdd
            {
                Request = request,
                SentTime = Time.time
            };
            
            OnContentAddRequested?.Invoke(request);
            
            if (m_IsServer)
            {
                var response = ProcessContentAddRequest(request, NetworkId);
                response.ActorNetworkId = request.ActorNetworkId;
                response.CorrelationId = request.CorrelationId;
                ReceiveContentAddResponse(response);
            }
        }
        
        /// <summary>
        /// Request to remove an item from the bag.
        /// </summary>
        public void RequestRemoveItem(RuntimeItem runtimeItem,
            InventoryModificationSource source = InventoryModificationSource.Direct)
        {
            if (m_IsRemoteClient) return;
            if (runtimeItem == null) return;
            
            var request = new NetworkContentRemoveRequest
            {
                RequestId = GetNextRequestId(),
                ActorNetworkId = NetworkId,
                CorrelationId = NetworkCorrelation.Compose(NetworkId, m_LastIssuedRequestId),
                TargetBagNetworkId = NetworkId,
                RuntimeIdHash = runtimeItem.RuntimeID.Hash,
                UsePosition = false,
                Source = source
            };
            
            m_PendingRemoves[GetPendingKey(request.ActorNetworkId, request.CorrelationId, request.RequestId)] = new PendingContentRemove
            {
                Request = request,
                RemovedItem = runtimeItem,
                SentTime = Time.time
            };
            
            OnContentRemoveRequested?.Invoke(request);
            
            if (m_IsServer)
            {
                var response = ProcessContentRemoveRequest(request, NetworkId);
                response.ActorNetworkId = request.ActorNetworkId;
                response.CorrelationId = request.CorrelationId;
                ReceiveContentRemoveResponse(response);
            }
            else
            {
                NetworkInventoryManager.Instance?.SendContentRemoveRequest(request);
            }
        }
        
        /// <summary>
        /// Request to remove item at position.
        /// </summary>
        public void RequestRemoveAtPosition(Vector2Int position,
            InventoryModificationSource source = InventoryModificationSource.Direct)
        {
            if (m_IsRemoteClient) return;
            
            var request = new NetworkContentRemoveRequest
            {
                RequestId = GetNextRequestId(),
                ActorNetworkId = NetworkId,
                CorrelationId = NetworkCorrelation.Compose(NetworkId, m_LastIssuedRequestId),
                TargetBagNetworkId = NetworkId,
                Position = position,
                UsePosition = true,
                Source = source
            };
            
            m_PendingRemoves[GetPendingKey(request.ActorNetworkId, request.CorrelationId, request.RequestId)] = new PendingContentRemove
            {
                Request = request,
                SentTime = Time.time
            };
            
            OnContentRemoveRequested?.Invoke(request);
            
            if (m_IsServer)
            {
                var response = ProcessContentRemoveRequest(request, NetworkId);
                response.ActorNetworkId = request.ActorNetworkId;
                response.CorrelationId = request.CorrelationId;
                ReceiveContentRemoveResponse(response);
            }
            else
            {
                NetworkInventoryManager.Instance?.SendContentRemoveRequest(request);
            }
        }
        
        /// <summary>
        /// Request to move item within bag.
        /// </summary>
        public void RequestMoveItem(Vector2Int fromPosition, Vector2Int toPosition, bool allowStack)
        {
            if (m_IsRemoteClient) return;
            
            var request = new NetworkContentMoveRequest
            {
                RequestId = GetNextRequestId(),
                ActorNetworkId = NetworkId,
                CorrelationId = NetworkCorrelation.Compose(NetworkId, m_LastIssuedRequestId),
                TargetBagNetworkId = NetworkId,
                FromPosition = fromPosition,
                ToPosition = toPosition,
                AllowStack = allowStack
            };
            
            m_PendingMoves[GetPendingKey(request.ActorNetworkId, request.CorrelationId, request.RequestId)] = new PendingContentMove
            {
                Request = request,
                SentTime = Time.time
            };
            
            OnContentMoveRequested?.Invoke(request);
            
            if (m_IsServer)
            {
                var response = ProcessContentMoveRequest(request, NetworkId);
                response.ActorNetworkId = request.ActorNetworkId;
                response.CorrelationId = request.CorrelationId;
                ReceiveContentMoveResponse(response);
            }
            else
            {
                NetworkInventoryManager.Instance?.SendContentMoveRequest(request);
            }
        }
        
        /// <summary>
        /// Request to use an item.
        /// </summary>
        public void RequestUseItem(RuntimeItem runtimeItem)
        {
            if (m_IsRemoteClient) return;
            if (runtimeItem == null) return;
            
            var request = new NetworkContentUseRequest
            {
                RequestId = GetNextRequestId(),
                ActorNetworkId = NetworkId,
                CorrelationId = NetworkCorrelation.Compose(NetworkId, m_LastIssuedRequestId),
                TargetBagNetworkId = NetworkId,
                RuntimeIdHash = runtimeItem.RuntimeID.Hash,
                UsePosition = false
            };
            
            OnContentUseRequested?.Invoke(request);
            
            if (m_IsServer)
            {
                var response = ProcessContentUseRequest(request, NetworkId);
                response.ActorNetworkId = request.ActorNetworkId;
                response.CorrelationId = request.CorrelationId;
                ReceiveContentUseResponse(response);
            }
            else
            {
                NetworkInventoryManager.Instance?.SendContentUseRequest(request);
            }
        }
        
        /// <summary>
        /// Request to drop an item.
        /// </summary>
        public void RequestDropItem(RuntimeItem runtimeItem, Vector3 dropPosition, int maxAmount = 1)
        {
            if (m_IsRemoteClient) return;
            if (runtimeItem == null) return;

            SendDropRequest(NetworkId, NetworkId, runtimeItem, dropPosition, maxAmount);
        }

        private void SendDropRequest(uint actorNetworkId, uint targetBagNetworkId, RuntimeItem runtimeItem, Vector3 dropPosition, int maxAmount = 1)
        {
            if (runtimeItem == null || actorNetworkId == 0 || targetBagNetworkId == 0) return;

            var request = new NetworkContentDropRequest
            {
                RequestId = GetNextRequestId(),
                ActorNetworkId = actorNetworkId,
                CorrelationId = NetworkCorrelation.Compose(actorNetworkId, m_LastIssuedRequestId),
                TargetBagNetworkId = targetBagNetworkId,
                RuntimeIdHash = runtimeItem.RuntimeID.Hash,
                DropPosition = dropPosition,
                MaxAmount = maxAmount
            };

            LogPickupDebug(
                $"{name}: sending drop request req={request.RequestId} actor={actorNetworkId} targetBag={targetBagNetworkId} item={DescribeRuntimeItem(runtimeItem)} position={dropPosition} server={m_IsServer} local={m_IsLocalClient}",
                this);
            
            if (m_IsServer)
            {
                var response = ProcessContentDropRequest(request, NetworkId);
                response.ActorNetworkId = request.ActorNetworkId;
                response.CorrelationId = request.CorrelationId;
                ReceiveContentDropResponse(response);
            }
            else
            {
                NetworkInventoryManager.Instance?.SendContentDropRequest(request);
            }
        }
        
        #endregion
        
        #region Equipment Requests
        
        /// <summary>
        /// Request to equip an item.
        /// </summary>
        public void RequestEquip(RuntimeItem runtimeItem, int slot = -1)
        {
            if (m_IsRemoteClient) return;
            if (runtimeItem == null) return;
            
            var request = new NetworkEquipmentRequest
            {
                RequestId = GetNextRequestId(),
                ActorNetworkId = NetworkId,
                CorrelationId = NetworkCorrelation.Compose(NetworkId, m_LastIssuedRequestId),
                TargetBagNetworkId = NetworkId,
                RuntimeIdHash = runtimeItem.RuntimeID.Hash,
                Action = slot >= 0 ? EquipmentAction.EquipToSlot : EquipmentAction.Equip,
                SlotOrIndex = slot
            };
            
            m_PendingEquipment[GetPendingKey(request.ActorNetworkId, request.CorrelationId, request.RequestId)] = new PendingEquipment
            {
                Request = request,
                SentTime = Time.time
            };
            
            OnEquipmentRequested?.Invoke(request);
            
            if (m_IsServer)
            {
                _ = ProcessLocalEquipmentRequestAsync(request);
            }
            else
            {
                NetworkInventoryManager.Instance?.SendEquipmentRequest(request);
            }
        }
        
        /// <summary>
        /// Request to unequip an item.
        /// </summary>
        public void RequestUnequip(RuntimeItem runtimeItem)
        {
            if (m_IsRemoteClient) return;
            if (runtimeItem == null) return;
            
            var request = new NetworkEquipmentRequest
            {
                RequestId = GetNextRequestId(),
                ActorNetworkId = NetworkId,
                CorrelationId = NetworkCorrelation.Compose(NetworkId, m_LastIssuedRequestId),
                TargetBagNetworkId = NetworkId,
                RuntimeIdHash = runtimeItem.RuntimeID.Hash,
                Action = EquipmentAction.Unequip,
                SlotOrIndex = -1
            };
            
            m_PendingEquipment[GetPendingKey(request.ActorNetworkId, request.CorrelationId, request.RequestId)] = new PendingEquipment
            {
                Request = request,
                SentTime = Time.time
            };
            
            OnEquipmentRequested?.Invoke(request);
            
            if (m_IsServer)
            {
                _ = ProcessLocalEquipmentRequestAsync(request);
            }
            else
            {
                NetworkInventoryManager.Instance?.SendEquipmentRequest(request);
            }
        }
        
        /// <summary>
        /// Request to unequip from specific index.
        /// </summary>
        public void RequestUnequipFromIndex(int index)
        {
            if (m_IsRemoteClient) return;
            
            var request = new NetworkEquipmentRequest
            {
                RequestId = GetNextRequestId(),
                ActorNetworkId = NetworkId,
                CorrelationId = NetworkCorrelation.Compose(NetworkId, m_LastIssuedRequestId),
                TargetBagNetworkId = NetworkId,
                RuntimeIdHash = 0,
                Action = EquipmentAction.UnequipFromIndex,
                SlotOrIndex = index
            };
            
            m_PendingEquipment[GetPendingKey(request.ActorNetworkId, request.CorrelationId, request.RequestId)] = new PendingEquipment
            {
                Request = request,
                SentTime = Time.time
            };
            
            OnEquipmentRequested?.Invoke(request);
            
            if (m_IsServer)
            {
                _ = ProcessLocalEquipmentRequestAsync(request);
            }
            else
            {
                NetworkInventoryManager.Instance?.SendEquipmentRequest(request);
            }
        }
        
        #endregion
        
        #region Socket Requests
        
        /// <summary>
        /// Request to attach item to socket.
        /// </summary>
        public void RequestAttachToSocket(RuntimeItem parent, RuntimeItem attachment, IdString socketId = default)
        {
            if (m_IsRemoteClient) return;
            if (parent == null || attachment == null) return;
            
            var request = new NetworkSocketRequest
            {
                RequestId = GetNextRequestId(),
                ActorNetworkId = NetworkId,
                CorrelationId = NetworkCorrelation.Compose(NetworkId, m_LastIssuedRequestId),
                TargetBagNetworkId = NetworkId,
                ParentRuntimeIdHash = parent.RuntimeID.Hash,
                AttachmentRuntimeIdHash = attachment.RuntimeID.Hash,
                SocketHash = socketId.Hash,
                SocketIdString = socketId.String,
                Action = socketId.Hash != 0 ? SocketAction.AttachToSocket : SocketAction.Attach
            };
            
            OnSocketRequested?.Invoke(request);
            
            if (m_IsServer)
            {
                var response = ProcessSocketRequest(request, NetworkId);
                response.ActorNetworkId = request.ActorNetworkId;
                response.CorrelationId = request.CorrelationId;
                ReceiveSocketResponse(response);
            }
            else
            {
                NetworkInventoryManager.Instance?.SendSocketRequest(request);
            }
        }
        
        /// <summary>
        /// Request to detach from socket.
        /// </summary>
        public void RequestDetachFromSocket(RuntimeItem parent, IdString socketId)
        {
            if (m_IsRemoteClient) return;
            if (parent == null) return;
            
            var request = new NetworkSocketRequest
            {
                RequestId = GetNextRequestId(),
                ActorNetworkId = NetworkId,
                CorrelationId = NetworkCorrelation.Compose(NetworkId, m_LastIssuedRequestId),
                TargetBagNetworkId = NetworkId,
                ParentRuntimeIdHash = parent.RuntimeID.Hash,
                SocketHash = socketId.Hash,
                SocketIdString = socketId.String,
                Action = SocketAction.DetachFromSocket
            };
            
            OnSocketRequested?.Invoke(request);
            
            if (m_IsServer)
            {
                var response = ProcessSocketRequest(request, NetworkId);
                response.ActorNetworkId = request.ActorNetworkId;
                response.CorrelationId = request.CorrelationId;
                ReceiveSocketResponse(response);
            }
            else
            {
                NetworkInventoryManager.Instance?.SendSocketRequest(request);
            }
        }

        /// <summary>
        /// Request to detach a specific attached item from its parent.
        /// </summary>
        public void RequestDetachFromSocket(RuntimeItem parent, RuntimeItem attachment)
        {
            if (m_IsRemoteClient) return;
            if (parent == null || attachment == null) return;

            var request = new NetworkSocketRequest
            {
                RequestId = GetNextRequestId(),
                ActorNetworkId = NetworkId,
                CorrelationId = NetworkCorrelation.Compose(NetworkId, m_LastIssuedRequestId),
                TargetBagNetworkId = NetworkId,
                ParentRuntimeIdHash = parent.RuntimeID.Hash,
                AttachmentRuntimeIdHash = attachment.RuntimeID.Hash,
                Action = SocketAction.Detach
            };

            OnSocketRequested?.Invoke(request);

            if (m_IsServer)
            {
                var response = ProcessSocketRequest(request, NetworkId);
                response.ActorNetworkId = request.ActorNetworkId;
                response.CorrelationId = request.CorrelationId;
                ReceiveSocketResponse(response);
            }
            else
            {
                NetworkInventoryManager.Instance?.SendSocketRequest(request);
            }
        }
        
        #endregion
        
        #region Wealth Requests
        
        /// <summary>
        /// Request to modify wealth.
        /// </summary>
        public void RequestWealthModify(Currency currency, int value, WealthAction action,
            InventoryModificationSource source = InventoryModificationSource.Direct, int sourceHash = 0)
        {
            if (m_IsRemoteClient) return;
            if (currency == null) return;
            
            var request = new NetworkWealthRequest
            {
                RequestId = GetNextRequestId(),
                ActorNetworkId = NetworkId,
                CorrelationId = NetworkCorrelation.Compose(NetworkId, m_LastIssuedRequestId),
                TargetBagNetworkId = NetworkId,
                CurrencyHash = currency.ID.Hash,
                CurrencyIdString = currency.ID.String,
                Value = value,
                Action = action,
                Source = source,
                SourceHash = sourceHash
            };
            
            int originalValue = m_Bag.Wealth.Get(currency);
            
            m_PendingWealth[GetPendingKey(request.ActorNetworkId, request.CorrelationId, request.RequestId)] = new PendingWealth
            {
                Request = request,
                OriginalValue = originalValue,
                SentTime = Time.time
            };
            
            OnWealthRequested?.Invoke(request);
            
            if (m_IsServer)
            {
                var response = ProcessWealthRequest(request, NetworkId);
                response.ActorNetworkId = request.ActorNetworkId;
                response.CorrelationId = request.CorrelationId;
                ReceiveWealthResponse(response);
            }
            else
            {
                NetworkInventoryManager.Instance?.SendWealthRequest(request);
            }
        }
        
        #endregion
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // CLIENT-SIDE: RECEIVE RESPONSES
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        #region Client Response Handlers
        
        public void ReceiveContentAddResponse(NetworkContentAddResponse response)
        {
            ulong key = GetPendingKey(response.ActorNetworkId, response.CorrelationId, response.RequestId);
            if (!m_PendingAdds.TryGetValue(key, out var pending))
                return;
            
            m_PendingAdds.Remove(key);
            
            if (!response.Authorized)
            {
                if (m_LogRejections)
                    Debug.LogWarning($"[NetworkInventoryController] Add rejected: {response.RejectionReason}");
                OnOperationRejected?.Invoke(response.RejectionReason, "Add item");
            }
        }
        
        public void ReceiveContentRemoveResponse(NetworkContentRemoveResponse response)
        {
            ulong key = GetPendingKey(response.ActorNetworkId, response.CorrelationId, response.RequestId);
            if (!m_PendingRemoves.TryGetValue(key, out var pending))
                return;
            
            m_PendingRemoves.Remove(key);
            
            if (!response.Authorized)
            {
                if (m_LogRejections)
                    Debug.LogWarning($"[NetworkInventoryController] Remove rejected: {response.RejectionReason}");
                OnOperationRejected?.Invoke(response.RejectionReason, "Remove item");
            }
        }
        
        public void ReceiveContentMoveResponse(NetworkContentMoveResponse response)
        {
            ulong key = GetPendingKey(response.ActorNetworkId, response.CorrelationId, response.RequestId);
            if (!m_PendingMoves.TryGetValue(key, out var pending))
                return;
            
            m_PendingMoves.Remove(key);
            
            if (!response.Authorized)
            {
                if (m_LogRejections)
                    Debug.LogWarning($"[NetworkInventoryController] Move rejected: {response.RejectionReason}");
                OnOperationRejected?.Invoke(response.RejectionReason, "Move item");
            }
        }
        
        public void ReceiveContentUseResponse(NetworkContentUseResponse response)
        {
            if (!response.Authorized)
            {
                if (m_LogRejections)
                    Debug.LogWarning($"[NetworkInventoryController] Use rejected: {response.RejectionReason}");
                OnOperationRejected?.Invoke(response.RejectionReason, "Use item");
            }
        }
        
        public void ReceiveContentDropResponse(NetworkContentDropResponse response)
        {
            if (!response.Authorized)
            {
                if (m_LogRejections)
                    Debug.LogWarning($"[NetworkInventoryController] Drop rejected: {response.RejectionReason}");
                OnOperationRejected?.Invoke(response.RejectionReason, "Drop item");
            }
        }
        
        public void ReceiveEquipmentResponse(NetworkEquipmentResponse response)
        {
            ulong key = GetPendingKey(response.ActorNetworkId, response.CorrelationId, response.RequestId);
            if (!m_PendingEquipment.TryGetValue(key, out var pending))
                return;
            
            m_PendingEquipment.Remove(key);
            
            if (!response.Authorized)
            {
                if (m_LogRejections)
                    Debug.LogWarning($"[NetworkInventoryController] Equipment rejected: {response.RejectionReason}");
                OnOperationRejected?.Invoke(response.RejectionReason, "Equipment operation");
            }
        }
        
        public void ReceiveSocketResponse(NetworkSocketResponse response)
        {
            if (!response.Authorized)
            {
                if (m_LogRejections)
                    Debug.LogWarning($"[NetworkInventoryController] Socket rejected: {response.RejectionReason}");
                OnOperationRejected?.Invoke(response.RejectionReason, "Socket operation");
            }
        }
        
        public void ReceiveWealthResponse(NetworkWealthResponse response)
        {
            ulong key = GetPendingKey(response.ActorNetworkId, response.CorrelationId, response.RequestId);
            if (!m_PendingWealth.TryGetValue(key, out var pending))
                return;
            
            m_PendingWealth.Remove(key);
            
            if (!response.Authorized)
            {
                if (m_LogRejections)
                    Debug.LogWarning($"[NetworkInventoryController] Wealth rejected: {response.RejectionReason}");
                OnOperationRejected?.Invoke(response.RejectionReason, "Wealth operation");
            }
        }
        
        #endregion
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // LOCAL CHANGE DETECTION
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        private void OnLocalItemAdded(RuntimeItem item)
        {
            if (item != null)
            {
                TrackRuntimeItemRecursive(item);
            }

            LogPickupDebug(
                $"{name}: local add observed item={DescribeRuntimeItem(item)} bag={NetworkId} server={m_IsServer} local={m_IsLocalClient} remote={m_IsRemoteClient} applying={m_IsApplyingNetworkState} " +
                $"hasDroppedInstance={(item != null && s_DroppedItemInstances.ContainsKey(item.RuntimeID.Hash))} position={(item != null ? m_Bag.Content.FindPosition(item.RuntimeID).ToString() : "n/a")}",
                this);

            if (m_IsServer && !m_IsApplyingNetworkState && item != null)
            {
                BroadcastServerPickupFromDroppedItemIfNeeded(item);
            }
            else if (!m_IsServer && !m_IsApplyingNetworkState)
            {
                if (!TrySendPickupForLocalAdd(item))
                {
                    TrySendTransferForLocalAdd(item);
                }
            }

            if (m_LogAllChanges && !m_IsServer)
                Debug.Log($"[NetworkInventoryController] Local item added: {item?.ItemID.String}");
        }
        
        private void OnLocalItemRemoved(RuntimeItem item)
        {
            if (item != null)
            {
                if (ContainsRuntimeItemRecursive(item.RuntimeID.Hash))
                {
                    TrackRuntimeItemRecursive(item);
                }
                else
                {
                    UntrackRuntimeItemRecursive(item);
                }
            }

            if (!m_IsApplyingNetworkState)
            {
                RememberLocalRemoval(this, item);
            }

            if (m_LogAllChanges && !m_IsServer)
                Debug.Log($"[NetworkInventoryController] Local item removed: {item?.ItemID.String}");
        }
        
        private void OnLocalItemUsed(RuntimeItem item)
        {
            if (!m_IsServer && !m_IsApplyingNetworkState && item != null && m_IsLocalClient)
            {
                RequestUseItem(item);
            }

            if (m_LogAllChanges && !m_IsServer)
                Debug.Log($"[NetworkInventoryController] Local item used: {item?.ItemID.String}");
        }
        
        private void OnLocalItemEquipped(RuntimeItem item, int index)
        {
            if (!m_IsServer && !m_IsApplyingNetworkState && item != null && m_IsLocalClient)
            {
                RequestEquipToIndexFromLocalEvent(item, index);
            }

            if (m_LogAllChanges && !m_IsServer)
                Debug.Log($"[NetworkInventoryController] Local item equipped: {item?.ItemID.String} at {index}");
        }
        
        private void OnLocalItemUnequipped(RuntimeItem item, int index)
        {
            if (!m_IsServer && !m_IsApplyingNetworkState && m_IsLocalClient)
            {
                RequestUnequipFromIndex(index);
            }

            if (m_LogAllChanges && !m_IsServer)
                Debug.Log($"[NetworkInventoryController] Local item unequipped: {item?.ItemID.String} from {index}");
        }
        
        private void OnLocalWealthChanged(IdString currencyId, int oldValue, int newValue)
        {
            if (m_LogAllChanges && !m_IsServer)
                Debug.Log($"[NetworkInventoryController] Local wealth changed: {currencyId.String} {oldValue} -> {newValue}");
        }

        private async Task ProcessLocalEquipmentRequestAsync(NetworkEquipmentRequest request)
        {
            NetworkEquipmentResponse response = await ProcessEquipmentRequest(request, NetworkId);
            response.ActorNetworkId = request.ActorNetworkId;
            response.CorrelationId = request.CorrelationId;
            ReceiveEquipmentResponse(response);
        }

        private void RequestEquipToIndexFromLocalEvent(RuntimeItem runtimeItem, int index)
        {
            if (runtimeItem == null) return;

            var request = new NetworkEquipmentRequest
            {
                RequestId = GetNextRequestId(),
                ActorNetworkId = NetworkId,
                CorrelationId = NetworkCorrelation.Compose(NetworkId, m_LastIssuedRequestId),
                TargetBagNetworkId = NetworkId,
                RuntimeIdHash = runtimeItem.RuntimeID.Hash,
                Action = EquipmentAction.EquipToIndex,
                SlotOrIndex = index
            };

            m_PendingEquipment[GetPendingKey(request.ActorNetworkId, request.CorrelationId, request.RequestId)] = new PendingEquipment
            {
                Request = request,
                SentTime = Time.time
            };

            OnEquipmentRequested?.Invoke(request);
            NetworkInventoryManager.Instance?.SendEquipmentRequest(request);
        }

        private void TrySendTransferForLocalAdd(RuntimeItem item)
        {
            if (item == null) return;
            if (!TryGetLocalActorNetworkId(out uint actorNetworkId)) return;
            if (!TryTakePendingRemoval(item.RuntimeID.Hash, out PendingLocalRemoval removal)) return;
            if (removal.SourceController == null || removal.SourceController == this) return;
            if (removal.SourceController.NetworkId == 0 || NetworkId == 0) return;

            LogPickupDebug(
                $"{name}: sending transfer fallback for local add item={DescribeRuntimeItem(item)} actor={actorNetworkId} sourceBag={removal.SourceController.NetworkId} destinationBag={NetworkId}",
                this);

            var request = new NetworkTransferRequest
            {
                RequestId = GetNextRequestId(),
                ActorNetworkId = actorNetworkId,
                CorrelationId = NetworkCorrelation.Compose(actorNetworkId, m_LastIssuedRequestId),
                SourceBagNetworkId = removal.SourceController.NetworkId,
                DestinationBagNetworkId = NetworkId,
                RuntimeIdHash = item.RuntimeID.Hash,
                DestinationPosition = m_Bag.Content.FindPosition(item.RuntimeID),
                AllowStack = true,
                Source = InventoryModificationSource.Loot
            };

            NetworkInventoryManager.Instance?.SendTransferRequest(request);
        }

        private bool TrySendPickupForLocalAdd(RuntimeItem item)
        {
            if (item == null) return false;

            bool hasDroppedInstance = TryGetDroppedItemInstance(item.RuntimeID.Hash, out DroppedItemInstance droppedItem);
            bool exactRuntimeMatch = hasDroppedInstance;
            if (!hasDroppedInstance)
            {
                hasDroppedInstance = TryFindDroppedItemInstanceForLocalPickup(item, out droppedItem);
            }

            if (!m_IsLocalClient || !UsesNetworkCharacterId)
            {
                if (hasDroppedInstance)
                {
                    LogPickupWarning(
                        $"{name}: pickup request skipped because controller is not a local network-character inventory item={DescribeRuntimeItem(item)} local={m_IsLocalClient} usesCharacterId={UsesNetworkCharacterId} bag={NetworkId}",
                        this);
                }
                return false;
            }

            if (!TryGetLocalActorNetworkId(out uint actorNetworkId))
            {
                LogPickupWarning(
                    $"{name}: pickup request skipped because no local actor network id was found item={DescribeRuntimeItem(item)} bag={NetworkId}",
                    this);
                return false;
            }

            if (!hasDroppedInstance)
            {
                LogPickupDebug(
                    $"{name}: pickup request skipped because local add is not a tracked network drop item={DescribeRuntimeItem(item)} bag={NetworkId} trackedDrops={s_DroppedItemInstances.Count}",
                    this);
                return false;
            }

            var request = new NetworkPickupRequest
            {
                RequestId = GetNextRequestId(),
                ActorNetworkId = actorNetworkId,
                CorrelationId = NetworkCorrelation.Compose(actorNetworkId, m_LastIssuedRequestId),
                PickerBagNetworkId = NetworkId,
                SourceBagNetworkId = droppedItem.SourceBagNetworkId,
                RuntimeIdHash = droppedItem.Item.RuntimeIdHash,
                DestinationPosition = m_Bag.Content.FindPosition(item.RuntimeID)
            };

            if (droppedItem.Item.RuntimeIdHash != 0 && droppedItem.Item.RuntimeIdHash != item.RuntimeID.Hash)
            {
                m_PendingPickupLocalRuntimeByServerRuntime[droppedItem.Item.RuntimeIdHash] = item.RuntimeID.Hash;
            }

            LogPickupDebug(
                $"{name}: sending pickup request req={request.RequestId} actor={actorNetworkId} pickerBag={NetworkId} sourceBag={droppedItem.SourceBagNetworkId} localItem={DescribeRuntimeItem(item)} serverRuntime={droppedItem.Item.RuntimeIdHash} exactRuntimeMatch={exactRuntimeMatch} destination={request.DestinationPosition} dropPosition={droppedItem.Position} instanceAlive={droppedItem.Instance != null}",
                this);

            NetworkInventoryManager.Instance?.SendPickupRequest(request);
            return true;
        }

        private static void RememberLocalRemoval(NetworkInventoryController source, RuntimeItem item)
        {
            if (source == null || item == null) return;
            if (!source.m_IsServer && !TryGetLocalActorNetworkId(out _)) return;

            PrunePendingLocalRemovals();
            long runtimeIdHash = item.RuntimeID.Hash;

            for (int i = s_PendingLocalRemovals.Count - 1; i >= 0; i--)
            {
                if (s_PendingLocalRemovals[i].RuntimeIdHash == runtimeIdHash)
                {
                    s_PendingLocalRemovals.RemoveAt(i);
                }
            }

            s_PendingLocalRemovals.Add(new PendingLocalRemoval
            {
                SourceController = source,
                Item = source.ConvertToNetworkItem(item),
                RuntimeIdHash = runtimeIdHash,
                Time = Time.unscaledTime
            });
        }

        private static bool TryTakePendingRemoval(long runtimeIdHash, out PendingLocalRemoval removal)
        {
            PrunePendingLocalRemovals();

            for (int i = 0; i < s_PendingLocalRemovals.Count; i++)
            {
                if (s_PendingLocalRemovals[i].RuntimeIdHash != runtimeIdHash) continue;

                removal = s_PendingLocalRemovals[i];
                s_PendingLocalRemovals.RemoveAt(i);
                return true;
            }

            removal = default;
            return false;
        }

        private static bool TryPeekPendingRemoval(long runtimeIdHash, out PendingLocalRemoval removal)
        {
            PrunePendingLocalRemovals();

            for (int i = 0; i < s_PendingLocalRemovals.Count; i++)
            {
                if (s_PendingLocalRemovals[i].RuntimeIdHash != runtimeIdHash) continue;

                removal = s_PendingLocalRemovals[i];
                return true;
            }

            removal = default;
            return false;
        }

        private static void PrunePendingLocalRemovals()
        {
            float now = Time.unscaledTime;
            for (int i = s_PendingLocalRemovals.Count - 1; i >= 0; i--)
            {
                if (now - s_PendingLocalRemovals[i].Time <= 2f) continue;
                s_PendingLocalRemovals.RemoveAt(i);
            }
        }

        private static bool TryGetLocalActorNetworkId(out uint actorNetworkId)
        {
            if (s_LocalPlayerController != null &&
                s_LocalPlayerController.NetworkId != 0 &&
                s_LocalPlayerController.m_IsLocalClient)
            {
                actorNetworkId = s_LocalPlayerController.NetworkId;
                return true;
            }

            for (int i = 0; i < s_Controllers.Count; i++)
            {
                NetworkInventoryController controller = s_Controllers[i];
                if (controller == null || !controller.m_IsLocalClient || !controller.UsesNetworkCharacterId) continue;
                if (controller.NetworkId == 0) continue;

                s_LocalPlayerController = controller;
                actorNetworkId = controller.NetworkId;
                return true;
            }

            actorNetworkId = 0;
            return false;
        }

        private static void HandleGlobalItemInstantiated()
        {
            RuntimeItem item = Item.LastItemInstantiated;
            GameObject instance = Item.LastItemInstanceInstantiated;
            if (item == null || instance == null) return;
            if (!TryPeekPendingRemoval(item.RuntimeID.Hash, out PendingLocalRemoval removal)) return;
            if (removal.SourceController == null) return;

            if (removal.SourceController.m_IsServer)
            {
                RememberDroppedItemInstance(item.RuntimeID.Hash, instance, removal.SourceController.NetworkId, removal.Item, instance.transform.position);
                LogPickupDebug(
                    $"global instantiated server-side drop item={DescribeRuntimeItem(item)} sourceBag={removal.SourceController.NetworkId} position={instance.transform.position}",
                    instance);
                removal.SourceController.BroadcastServerDropFromLocalMutation(removal.Item, item.RuntimeID.Hash, instance.transform.position);
                TryTakePendingRemoval(item.RuntimeID.Hash, out _);
                return;
            }

            if (!TryGetLocalActorNetworkId(out uint actorNetworkId)) return;

            s_LocalDropRuntimeIds.Add(item.RuntimeID.Hash);
            RememberDroppedItemInstance(item.RuntimeID.Hash, instance, removal.SourceController.NetworkId, removal.Item, instance.transform.position);
            LogPickupDebug(
                $"global instantiated client-side drop item={DescribeRuntimeItem(item)} sourceBag={removal.SourceController.NetworkId} actor={actorNetworkId} position={instance.transform.position}",
                instance);
            removal.SourceController.SendDropRequest(
                actorNetworkId,
                removal.SourceController.NetworkId,
                item,
                instance.transform.position,
                1);

            TryTakePendingRemoval(item.RuntimeID.Hash, out _);
        }

        private static void RememberDroppedItemInstance(
            long runtimeIdHash,
            GameObject instance,
            uint sourceBagNetworkId,
            NetworkRuntimeItem item,
            Vector3 position)
        {
            if (runtimeIdHash == 0 || instance == null) return;

            if (s_DroppedItemInstances.TryGetValue(runtimeIdHash, out DroppedItemInstance previous) &&
                previous.Instance != null &&
                previous.Instance != instance)
            {
                LogPickupDebug(
                    $"replacing tracked dropped instance runtime={runtimeIdHash} old={previous.Instance.name} new={instance.name} sourceBag={sourceBagNetworkId}");
                UnityEngine.Object.Destroy(previous.Instance);
            }

            s_DroppedItemInstances[runtimeIdHash] = new DroppedItemInstance
            {
                Instance = instance,
                SourceBagNetworkId = sourceBagNetworkId,
                Item = item,
                Position = position
            };

            LogPickupDebug(
                $"remembered dropped instance runtime={runtimeIdHash} sourceBag={sourceBagNetworkId} item={DescribeNetworkItem(item)} instance={instance.name} position={position} trackedDrops={s_DroppedItemInstances.Count}");
        }

        private static bool TryAdoptPredictedDroppedItemInstance(NetworkItemDroppedBroadcast broadcast)
        {
            long serverRuntimeIdHash = broadcast.Item.RuntimeIdHash;
            if (serverRuntimeIdHash != 0 && s_LocalDropRuntimeIds.Remove(serverRuntimeIdHash))
            {
                if (s_DroppedItemInstances.TryGetValue(serverRuntimeIdHash, out DroppedItemInstance exactDrop) &&
                    exactDrop.Instance != null)
                {
                    s_DroppedItemInstances[serverRuntimeIdHash] = new DroppedItemInstance
                    {
                        Instance = exactDrop.Instance,
                        SourceBagNetworkId = broadcast.SourceBagNetworkId,
                        Item = broadcast.Item,
                        Position = broadcast.Position
                    };
                }

                LogPickupDebug(
                    $"adopted server dropped broadcast for exact local predicted drop runtime={serverRuntimeIdHash} sourceBag={broadcast.SourceBagNetworkId} item={DescribeNetworkItem(broadcast.Item)}");
                return true;
            }

            if (serverRuntimeIdHash == 0 || s_LocalDropRuntimeIds.Count == 0)
            {
                return false;
            }

            long bestLocalRuntimeIdHash = 0;
            DroppedItemInstance bestDrop = default;
            float bestDistance = float.MaxValue;

            foreach (KeyValuePair<long, DroppedItemInstance> entry in s_DroppedItemInstances)
            {
                if (!s_LocalDropRuntimeIds.Contains(entry.Key)) continue;

                DroppedItemInstance candidate = entry.Value;
                if (candidate.Item.ItemHash != broadcast.Item.ItemHash) continue;
                if (broadcast.SourceBagNetworkId != 0 &&
                    candidate.SourceBagNetworkId != 0 &&
                    candidate.SourceBagNetworkId != broadcast.SourceBagNetworkId)
                {
                    continue;
                }

                Vector3 candidatePosition = candidate.Instance != null
                    ? candidate.Instance.transform.position
                    : candidate.Position;
                float distance = Vector3.SqrMagnitude(candidatePosition - broadcast.Position);
                if (distance >= bestDistance) continue;

                bestDistance = distance;
                bestLocalRuntimeIdHash = entry.Key;
                bestDrop = candidate;
            }

            if (bestLocalRuntimeIdHash == 0 || bestDrop.Instance == null || bestDistance > 16f)
            {
                return false;
            }

            s_LocalDropRuntimeIds.Remove(bestLocalRuntimeIdHash);
            s_DroppedItemInstances.Remove(bestLocalRuntimeIdHash);
            RememberDroppedItemInstance(
                serverRuntimeIdHash,
                bestDrop.Instance,
                broadcast.SourceBagNetworkId,
                broadcast.Item,
                broadcast.Position);

            LogPickupDebug(
                $"adopted server dropped broadcast by remapping local predicted drop localRuntime={bestLocalRuntimeIdHash} serverRuntime={serverRuntimeIdHash} sourceBag={broadcast.SourceBagNetworkId} distance={Mathf.Sqrt(bestDistance):0.00} item={DescribeNetworkItem(broadcast.Item)}",
                bestDrop.Instance);
            return true;
        }

        private static bool TryGetDroppedItemInstance(long runtimeIdHash, out DroppedItemInstance droppedItem)
        {
            if (runtimeIdHash != 0 && s_DroppedItemInstances.TryGetValue(runtimeIdHash, out droppedItem))
            {
                return true;
            }

            droppedItem = default;
            return false;
        }

        private bool TryFindDroppedItemInstanceForLocalPickup(RuntimeItem localItem, out DroppedItemInstance droppedItem)
        {
            droppedItem = default;
            if (localItem?.Item == null || s_DroppedItemInstances.Count == 0) return false;

            int itemHash = localItem.ItemID.Hash;
            Vector3 pickerPosition = transform.position;
            float bestDistance = float.MaxValue;
            bool found = false;

            foreach (var entry in s_DroppedItemInstances)
            {
                DroppedItemInstance candidate = entry.Value;
                if (candidate.Item.ItemHash != itemHash) continue;

                Vector3 candidatePosition = candidate.Instance != null
                    ? candidate.Instance.transform.position
                    : candidate.Position;

                float distance = Vector3.SqrMagnitude(candidatePosition - pickerPosition);
                if (distance >= bestDistance) continue;

                bestDistance = distance;
                droppedItem = candidate;
                found = true;
            }

            if (found)
            {
                LogPickupDebug(
                    $"{name}: matched local pickup by item type localItem={DescribeRuntimeItem(localItem)} serverItem={DescribeNetworkItem(droppedItem.Item)} sourceBag={droppedItem.SourceBagNetworkId} distance={Mathf.Sqrt(bestDistance):0.00}",
                    this);
            }

            return found;
        }

        private static bool TryDestroyDroppedItemInstance(long runtimeIdHash)
        {
            if (runtimeIdHash == 0) return false;
            if (!s_DroppedItemInstances.TryGetValue(runtimeIdHash, out DroppedItemInstance droppedItem)) return false;

            s_DroppedItemInstances.Remove(runtimeIdHash);
            s_LocalDropRuntimeIds.Remove(runtimeIdHash);
            GameObject instance = droppedItem.Instance;
            if (instance == null) return false;

            LogPickupDebug(
                $"destroying tracked dropped instance runtime={runtimeIdHash} sourceBag={droppedItem.SourceBagNetworkId} instance={instance.name} remainingTrackedDrops={s_DroppedItemInstances.Count}",
                instance);
            UnityEngine.Object.Destroy(instance);
            return true;
        }

        private static bool TryDestroyDroppedItemInstance(NetworkDroppedItemRemovedBroadcast broadcast, out long destroyedRuntimeIdHash)
        {
            destroyedRuntimeIdHash = broadcast.RuntimeIdHash;
            if (TryDestroyDroppedItemInstance(broadcast.RuntimeIdHash))
            {
                return true;
            }

            destroyedRuntimeIdHash = 0;
            if (s_DroppedItemInstances.Count == 0)
            {
                return false;
            }

            long bestRuntimeIdHash = 0;
            DroppedItemInstance bestDrop = default;
            float bestDistance = float.MaxValue;
            bool foundSameSource = false;

            foreach (KeyValuePair<long, DroppedItemInstance> entry in s_DroppedItemInstances)
            {
                DroppedItemInstance candidate = entry.Value;
                bool sameSource = broadcast.SourceBagNetworkId == 0 ||
                                  candidate.SourceBagNetworkId == 0 ||
                                  candidate.SourceBagNetworkId == broadcast.SourceBagNetworkId;

                if (foundSameSource && !sameSource) continue;
                if (!foundSameSource && sameSource)
                {
                    foundSameSource = true;
                    bestDistance = float.MaxValue;
                    bestRuntimeIdHash = 0;
                    bestDrop = default;
                }

                Vector3 candidatePosition = candidate.Instance != null
                    ? candidate.Instance.transform.position
                    : candidate.Position;
                float distance = Vector3.SqrMagnitude(candidatePosition - broadcast.Position);
                if (distance >= bestDistance) continue;

                bestDistance = distance;
                bestRuntimeIdHash = entry.Key;
                bestDrop = candidate;
            }

            float maxDistance = foundSameSource ? 16f : 2.25f;
            if (bestRuntimeIdHash == 0 || bestDrop.Instance == null || bestDistance > maxDistance)
            {
                return false;
            }

            destroyedRuntimeIdHash = bestRuntimeIdHash;
            return TryDestroyDroppedItemInstance(bestRuntimeIdHash);
        }

        private static void HandleGlobalSocketAttached(RuntimeItem parent, RuntimeItem attachment)
        {
            if (parent == null || attachment == null) return;
            NetworkInventoryController controller = FindControllerOwningRuntimeItem(parent.RuntimeID.Hash);
            if (controller == null || controller.m_IsApplyingNetworkState) return;
            if (!TryFindAttachedSocketId(parent, attachment, out IdString socketId)) socketId = IdString.EMPTY;

            if (controller.m_IsServer)
            {
                controller.BroadcastServerSocketAttach(parent, attachment, socketId);
                return;
            }

            if (!controller.m_IsLocalClient) return;
            controller.RequestAttachToSocket(parent, attachment, socketId);
        }

        private static void HandleGlobalSocketDetached(RuntimeItem parent, RuntimeItem attachment)
        {
            if (parent == null || attachment == null) return;
            NetworkInventoryController controller = FindControllerOwningRuntimeItem(parent.RuntimeID.Hash);
            if (controller == null || controller.m_IsApplyingNetworkState) return;

            if (controller.m_IsServer)
            {
                controller.BroadcastServerSocketDetach(parent);
                return;
            }

            if (!controller.m_IsLocalClient) return;

            controller.RequestDetachFromSocket(parent, attachment);
        }

        private static NetworkInventoryController FindControllerOwningRuntimeItem(long runtimeIdHash)
        {
            for (int i = 0; i < s_Controllers.Count; i++)
            {
                NetworkInventoryController controller = s_Controllers[i];
                if (controller == null) continue;
                if (controller.ContainsRuntimeItemRecursive(runtimeIdHash)) return controller;
            }

            return null;
        }

        private static bool TryFindAttachedSocketId(RuntimeItem parent, RuntimeItem attachment, out IdString socketId)
        {
            socketId = IdString.EMPTY;
            if (parent == null || attachment == null) return false;

            foreach (var socketEntry in parent.Sockets)
            {
                RuntimeSocket socket = socketEntry.Value;
                if (socket == null || !socket.HasAttachment) continue;
                if (socket.Attachment.RuntimeID.Hash != attachment.RuntimeID.Hash) continue;

                socketId = socketEntry.Key;
                return true;
            }

            return false;
        }
    }
}
#endif
