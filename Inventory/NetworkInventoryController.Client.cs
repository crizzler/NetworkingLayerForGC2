#if GC2_INVENTORY
using System;
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
                RequestId = m_NextRequestId++,
                ActorNetworkId = NetworkId,
                CorrelationId = NetworkCorrelation.Compose(NetworkId, (ushort)(m_NextRequestId - 1)),
                TargetBagNetworkId = NetworkId,
                ItemHash = item.ID.Hash,
                ItemIdString = item.ID.String,
                Position = position,
                AllowStack = allowStack,
                Source = source,
                SourceHash = sourceHash
            };
            
            m_PendingAdds[GetPendingKey(request.CorrelationId, request.RequestId)] = new PendingContentAdd
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
                RequestId = m_NextRequestId++,
                ActorNetworkId = NetworkId,
                CorrelationId = NetworkCorrelation.Compose(NetworkId, (ushort)(m_NextRequestId - 1)),
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
            
            m_PendingAdds[GetPendingKey(request.CorrelationId, request.RequestId)] = new PendingContentAdd
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
                RequestId = m_NextRequestId++,
                ActorNetworkId = NetworkId,
                CorrelationId = NetworkCorrelation.Compose(NetworkId, (ushort)(m_NextRequestId - 1)),
                TargetBagNetworkId = NetworkId,
                RuntimeIdHash = runtimeItem.RuntimeID.Hash,
                UsePosition = false,
                Source = source
            };
            
            m_PendingRemoves[GetPendingKey(request.CorrelationId, request.RequestId)] = new PendingContentRemove
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
                RequestId = m_NextRequestId++,
                ActorNetworkId = NetworkId,
                CorrelationId = NetworkCorrelation.Compose(NetworkId, (ushort)(m_NextRequestId - 1)),
                TargetBagNetworkId = NetworkId,
                Position = position,
                UsePosition = true,
                Source = source
            };
            
            m_PendingRemoves[GetPendingKey(request.CorrelationId, request.RequestId)] = new PendingContentRemove
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
                RequestId = m_NextRequestId++,
                ActorNetworkId = NetworkId,
                CorrelationId = NetworkCorrelation.Compose(NetworkId, (ushort)(m_NextRequestId - 1)),
                TargetBagNetworkId = NetworkId,
                FromPosition = fromPosition,
                ToPosition = toPosition,
                AllowStack = allowStack
            };
            
            m_PendingMoves[GetPendingKey(request.CorrelationId, request.RequestId)] = new PendingContentMove
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
                RequestId = m_NextRequestId++,
                ActorNetworkId = NetworkId,
                CorrelationId = NetworkCorrelation.Compose(NetworkId, (ushort)(m_NextRequestId - 1)),
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
            
            var request = new NetworkContentDropRequest
            {
                RequestId = m_NextRequestId++,
                ActorNetworkId = NetworkId,
                CorrelationId = NetworkCorrelation.Compose(NetworkId, (ushort)(m_NextRequestId - 1)),
                TargetBagNetworkId = NetworkId,
                RuntimeIdHash = runtimeItem.RuntimeID.Hash,
                DropPosition = dropPosition,
                MaxAmount = maxAmount
            };
            
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
                RequestId = m_NextRequestId++,
                ActorNetworkId = NetworkId,
                CorrelationId = NetworkCorrelation.Compose(NetworkId, (ushort)(m_NextRequestId - 1)),
                TargetBagNetworkId = NetworkId,
                RuntimeIdHash = runtimeItem.RuntimeID.Hash,
                Action = slot >= 0 ? EquipmentAction.EquipToSlot : EquipmentAction.Equip,
                SlotOrIndex = slot
            };
            
            m_PendingEquipment[GetPendingKey(request.CorrelationId, request.RequestId)] = new PendingEquipment
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
                RequestId = m_NextRequestId++,
                ActorNetworkId = NetworkId,
                CorrelationId = NetworkCorrelation.Compose(NetworkId, (ushort)(m_NextRequestId - 1)),
                TargetBagNetworkId = NetworkId,
                RuntimeIdHash = runtimeItem.RuntimeID.Hash,
                Action = EquipmentAction.Unequip,
                SlotOrIndex = -1
            };
            
            m_PendingEquipment[GetPendingKey(request.CorrelationId, request.RequestId)] = new PendingEquipment
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
                RequestId = m_NextRequestId++,
                ActorNetworkId = NetworkId,
                CorrelationId = NetworkCorrelation.Compose(NetworkId, (ushort)(m_NextRequestId - 1)),
                TargetBagNetworkId = NetworkId,
                RuntimeIdHash = 0,
                Action = EquipmentAction.UnequipFromIndex,
                SlotOrIndex = index
            };
            
            m_PendingEquipment[GetPendingKey(request.CorrelationId, request.RequestId)] = new PendingEquipment
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
                RequestId = m_NextRequestId++,
                ActorNetworkId = NetworkId,
                CorrelationId = NetworkCorrelation.Compose(NetworkId, (ushort)(m_NextRequestId - 1)),
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
                RequestId = m_NextRequestId++,
                ActorNetworkId = NetworkId,
                CorrelationId = NetworkCorrelation.Compose(NetworkId, (ushort)(m_NextRequestId - 1)),
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
                RequestId = m_NextRequestId++,
                ActorNetworkId = NetworkId,
                CorrelationId = NetworkCorrelation.Compose(NetworkId, (ushort)(m_NextRequestId - 1)),
                TargetBagNetworkId = NetworkId,
                CurrencyHash = currency.ID.Hash,
                CurrencyIdString = currency.ID.String,
                Value = value,
                Action = action,
                Source = source,
                SourceHash = sourceHash
            };
            
            int originalValue = m_Bag.Wealth.Get(currency);
            
            m_PendingWealth[GetPendingKey(request.CorrelationId, request.RequestId)] = new PendingWealth
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
            uint key = GetPendingKey(response.CorrelationId, response.RequestId);
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
            uint key = GetPendingKey(response.CorrelationId, response.RequestId);
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
            uint key = GetPendingKey(response.CorrelationId, response.RequestId);
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
            uint key = GetPendingKey(response.CorrelationId, response.RequestId);
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
            uint key = GetPendingKey(response.CorrelationId, response.RequestId);
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
            if (m_LogAllChanges && !m_IsServer)
                Debug.Log($"[NetworkInventoryController] Local item added: {item?.ItemID.String}");
        }
        
        private void OnLocalItemRemoved(RuntimeItem item)
        {
            if (m_LogAllChanges && !m_IsServer)
                Debug.Log($"[NetworkInventoryController] Local item removed: {item?.ItemID.String}");
        }
        
        private void OnLocalItemUsed(RuntimeItem item)
        {
            if (m_LogAllChanges && !m_IsServer)
                Debug.Log($"[NetworkInventoryController] Local item used: {item?.ItemID.String}");
        }
        
        private void OnLocalItemEquipped(RuntimeItem item, int index)
        {
            if (m_LogAllChanges && !m_IsServer)
                Debug.Log($"[NetworkInventoryController] Local item equipped: {item?.ItemID.String} at {index}");
        }
        
        private void OnLocalItemUnequipped(RuntimeItem item, int index)
        {
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
    }
}
#endif
