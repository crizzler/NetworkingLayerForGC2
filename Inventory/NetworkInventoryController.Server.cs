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
            
            // Try to add
            Vector2Int resultPosition;
            if (request.Position.x >= 0 && request.Position.y >= 0)
            {
                bool success = m_Bag.Content.Add(runtimeItem, request.Position, request.AllowStack);
                resultPosition = success ? request.Position : TBagContent.INVALID;
            }
            else
            {
                resultPosition = m_Bag.Content.Add(runtimeItem, request.AllowStack);
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
            
            // Broadcast
            var broadcast = new NetworkItemAddedBroadcast
            {
                BagNetworkId = NetworkId,
                Item = ConvertToNetworkItem(runtimeItem),
                Position = resultPosition,
                StackCount = m_Bag.Content.GetContent(resultPosition)?.Count ?? 1
            };
            
            NetworkInventoryManager.Instance?.BroadcastItemAdded(broadcast);
            OnItemAdded?.Invoke(broadcast);
            
            return new NetworkContentAddResponse
            {
                RequestId = request.RequestId,
                Authorized = true,
                RejectionReason = InventoryRejectionReason.None,
                ResultPosition = resultPosition,
                AssignedRuntimeId = runtimeItem.RuntimeID.Hash,
                AssignedRuntimeIdString = runtimeItem.RuntimeID.String
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
            
            // Broadcast
            var cell = m_Bag.Content.GetContent(position);
            var removeBroadcast = new NetworkItemRemovedBroadcast
            {
                BagNetworkId = NetworkId,
                RuntimeIdHash = removed.RuntimeID.Hash,
                Position = position,
                RemainingStackCount = cell?.Count ?? 0
            };
            
            NetworkInventoryManager.Instance?.BroadcastItemRemoved(removeBroadcast);
            OnItemRemoved?.Invoke(removeBroadcast);
            
            return new NetworkContentRemoveResponse
            {
                RequestId = request.RequestId,
                Authorized = true,
                RejectionReason = InventoryRejectionReason.None,
                RemovedItem = ConvertToNetworkItem(removed)
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
            
            // Broadcast
            var moveBroadcast = new NetworkItemMovedBroadcast
            {
                BagNetworkId = NetworkId,
                RuntimeIdHash = runtimeIdHash,
                FromPosition = request.FromPosition,
                ToPosition = request.ToPosition
            };
            
            NetworkInventoryManager.Instance?.BroadcastItemMoved(moveBroadcast);
            OnItemMoved?.Invoke(moveBroadcast);
            
            return new NetworkContentMoveResponse
            {
                RequestId = request.RequestId,
                Authorized = true,
                RejectionReason = InventoryRejectionReason.None,
                FinalPosition = request.ToPosition
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
                
                var useItem = useCell.RootRuntimeItem;
                wasConsumed = useItem.Item.Usage.ConsumeWhenUse;
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
                
                wasConsumed = useItem.Item.Usage.ConsumeWhenUse;
                useSuccess = m_Bag.Content.Use(useItem);
                
                if (useSuccess && wasConsumed)
                {
                    UntrackRuntimeItemRecursive(useItem);
                }
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
            
            // Broadcast
            var useBroadcast = new NetworkItemUsedBroadcast
            {
                BagNetworkId = NetworkId,
                RuntimeIdHash = request.RuntimeIdHash,
                WasConsumed = wasConsumed
            };
            
            NetworkInventoryManager.Instance?.BroadcastItemUsed(useBroadcast);
            OnItemUsed?.Invoke(useBroadcast);
            
            return new NetworkContentUseResponse
            {
                RequestId = request.RequestId,
                Authorized = true,
                RejectionReason = InventoryRejectionReason.None,
                WasConsumed = wasConsumed
            };
        }
        
        /// <summary>
        /// [Server] Process content drop request.
        /// </summary>
        public NetworkContentDropResponse ProcessContentDropRequest(NetworkContentDropRequest request, uint clientNetworkId)
        {
            if (!m_IsServer)
            {
                return new NetworkContentDropResponse
                {
                    RequestId = request.RequestId,
                    Authorized = false,
                    RejectionReason = InventoryRejectionReason.NotAuthorized
                };
            }
            
            if (!m_RuntimeItemMap.TryGetValue(request.RuntimeIdHash, out var dropItem))
            {
                return new NetworkContentDropResponse
                {
                    RequestId = request.RequestId,
                    Authorized = false,
                    RejectionReason = InventoryRejectionReason.RuntimeItemNotFound
                };
            }
            
            if (!dropItem.Item.CanDrop)
            {
                return new NetworkContentDropResponse
                {
                    RequestId = request.RequestId,
                    Authorized = false,
                    RejectionReason = InventoryRejectionReason.CannotDrop
                };
            }
            
            var dropped = m_Bag.Content.Drop(dropItem, request.DropPosition);
            
            if (dropped == null)
            {
                return new NetworkContentDropResponse
                {
                    RequestId = request.RequestId,
                    Authorized = false,
                    RejectionReason = InventoryRejectionReason.CannotDrop
                };
            }
            
            m_RuntimeItemMap.Remove(request.RuntimeIdHash);
            
            // Note: The actual Prop NetworkObject spawning should be handled by the transport layer
            
            return new NetworkContentDropResponse
            {
                RequestId = request.RequestId,
                Authorized = true,
                RejectionReason = InventoryRejectionReason.None,
                DroppedCount = 1
            };
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
                            EquipmentIndex = equippedIndex
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
                            EquipmentIndex = equippedIndex
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
                            EquipmentIndex = equippedIndex
                        };
                        NetworkInventoryManager.Instance?.BroadcastItemUnequipped(unequipIdxBroadcast);
                        OnItemUnequipped?.Invoke(unequipIdxBroadcast);
                    }
                    break;
                }
            }
            
            return new NetworkEquipmentResponse
            {
                RequestId = request.RequestId,
                Authorized = equipSuccess,
                RejectionReason = equipSuccess ? InventoryRejectionReason.None : InventoryRejectionReason.CannotEquip,
                EquippedIndex = equippedIndex
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
            
            if (socketSuccess)
            {
                var socketBroadcast = new NetworkSocketChangeBroadcast
                {
                    BagNetworkId = NetworkId,
                    ParentRuntimeIdHash = request.ParentRuntimeIdHash,
                    SocketHash = usedSocketHash,
                    HasAttachment = request.Action == SocketAction.Attach || request.Action == SocketAction.AttachToSocket,
                    Attachment = attachedItem
                };
                NetworkInventoryManager.Instance?.BroadcastSocketChange(socketBroadcast);
                OnSocketChanged?.Invoke(socketBroadcast);
            }
            
            return new NetworkSocketResponse
            {
                RequestId = request.RequestId,
                Authorized = socketSuccess,
                RejectionReason = socketSuccess ? InventoryRejectionReason.None : InventoryRejectionReason.CannotAttach,
                UsedSocketHash = usedSocketHash,
                DetachedItem = detachedItem
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
            int newValue;
            
            switch (request.Action)
            {
                case WealthAction.Set:
                    m_Bag.Wealth.Set(currencyId, request.Value);
                    newValue = request.Value;
                    break;
                    
                case WealthAction.Add:
                    m_Bag.Wealth.Add(currencyId, request.Value);
                    newValue = oldValue + request.Value;
                    break;
                    
                case WealthAction.Subtract:
                    if (oldValue < request.Value)
                    {
                        return new NetworkWealthResponse
                        {
                            RequestId = request.RequestId,
                            Authorized = false,
                            RejectionReason = InventoryRejectionReason.InsufficientFunds
                        };
                    }
                    m_Bag.Wealth.Subtract(currencyId, request.Value);
                    newValue = oldValue - request.Value;
                    break;
                    
                default:
                    return new NetworkWealthResponse
                    {
                        RequestId = request.RequestId,
                        Authorized = false,
                        RejectionReason = InventoryRejectionReason.InvalidOperation
                    };
            }
            
            // Broadcast
            var wealthBroadcast = new NetworkWealthChangeBroadcast
            {
                BagNetworkId = NetworkId,
                CurrencyHash = request.CurrencyHash,
                NewValue = newValue,
                Change = newValue - oldValue
            };
            NetworkInventoryManager.Instance?.BroadcastWealthChange(wealthBroadcast);
            OnWealthChanged?.Invoke(wealthBroadcast);
            
            return new NetworkWealthResponse
            {
                RequestId = request.RequestId,
                Authorized = true,
                RejectionReason = InventoryRejectionReason.None,
                NewValue = newValue,
                OldValue = oldValue
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
            
            // Reconstruct and apply
            var runtimeItem = ReconstructRuntimeItem(broadcast.Item);
            if (runtimeItem != null)
            {
                m_Bag.Content.Add(runtimeItem, broadcast.Position, true);
                TrackRuntimeItemRecursive(runtimeItem);
            }
            
            OnItemAdded?.Invoke(broadcast);
        }
        
        public void ReceiveItemRemovedBroadcast(NetworkItemRemovedBroadcast broadcast)
        {
            if (m_IsServer) return;
            
            if (m_RuntimeItemMap.TryGetValue(broadcast.RuntimeIdHash, out var runtimeItem))
            {
                m_Bag.Content.Remove(runtimeItem);
                UntrackRuntimeItemRecursive(runtimeItem);
            }
            
            OnItemRemoved?.Invoke(broadcast);
        }
        
        public void ReceiveItemMovedBroadcast(NetworkItemMovedBroadcast broadcast)
        {
            if (m_IsServer) return;
            
            m_Bag.Content.Move(broadcast.FromPosition, broadcast.ToPosition, true);
            OnItemMoved?.Invoke(broadcast);
        }
        
        public void ReceiveItemUsedBroadcast(NetworkItemUsedBroadcast broadcast)
        {
            if (m_IsServer) return;
            
            if (broadcast.WasConsumed && m_RuntimeItemMap.TryGetValue(broadcast.RuntimeIdHash, out var runtimeItem))
            {
                m_Bag.Content.Remove(runtimeItem);
                UntrackRuntimeItemRecursive(runtimeItem);
            }
            
            OnItemUsed?.Invoke(broadcast);
        }
        
        public void ReceiveItemEquippedBroadcast(NetworkItemEquippedBroadcast broadcast)
        {
            if (m_IsServer) return;
            
            if (m_RuntimeItemMap.TryGetValue(broadcast.RuntimeIdHash, out var runtimeItem))
            {
                _ = m_Bag.Equipment.EquipToIndex(runtimeItem, broadcast.EquipmentIndex);
            }
            
            OnItemEquipped?.Invoke(broadcast);
        }
        
        public void ReceiveItemUnequippedBroadcast(NetworkItemUnequippedBroadcast broadcast)
        {
            if (m_IsServer) return;
            
            _ = m_Bag.Equipment.UnequipFromIndex(broadcast.EquipmentIndex);
            OnItemUnequipped?.Invoke(broadcast);
        }
        
        public void ReceiveSocketChangeBroadcast(NetworkSocketChangeBroadcast broadcast)
        {
            if (m_IsServer) return;

            if (!m_RuntimeItemMap.TryGetValue(broadcast.ParentRuntimeIdHash, out RuntimeItem parentItem))
            {
                OnSocketChanged?.Invoke(broadcast);
                return;
            }

            if (!TryResolveRuntimeSocketId(parentItem, broadcast.SocketHash, null, out IdString socketId) ||
                !parentItem.Sockets.TryGetValue(socketId, out RuntimeSocket socket))
            {
                OnSocketChanged?.Invoke(broadcast);
                return;
            }

            RuntimeItem previousAttachment = socket.Attachment;
            RuntimeItem nextAttachment = null;
            if (broadcast.HasAttachment)
            {
                RuntimeItem attachment = ReconstructRuntimeItem(broadcast.Attachment);
                if (attachment != null)
                {
                    if (m_Bag.Content.Contains(attachment))
                    {
                        m_Bag.Content.Remove(attachment);
                    }

                    if (s_RuntimeSocketAttachmentField != null)
                    {
                        s_RuntimeSocketAttachmentField.SetValue(socket, attachment);
                    }

                    TrackRuntimeItemRecursive(attachment);
                    nextAttachment = attachment;
                }
            }
            else if (s_RuntimeSocketAttachmentField != null)
            {
                s_RuntimeSocketAttachmentField.SetValue(socket, null);
            }

            if (previousAttachment != null &&
                (nextAttachment == null || previousAttachment.RuntimeID.Hash != nextAttachment.RuntimeID.Hash))
            {
                UntrackRuntimeItemRecursive(previousAttachment);
            }

            OnSocketChanged?.Invoke(broadcast);
        }
        
        public void ReceiveWealthChangeBroadcast(NetworkWealthChangeBroadcast broadcast)
        {
            if (m_IsServer) return;
            
            if (TryResolveCurrencyIdByHash(broadcast.CurrencyHash, out IdString currencyId))
            {
                m_Bag.Wealth.Set(currencyId, broadcast.NewValue);
            }
            
            OnWealthChanged?.Invoke(broadcast);
        }
        
        public void ReceiveFullSnapshot(NetworkInventorySnapshot snapshot)
        {
            if (m_IsServer) return;

            if (snapshot.BagNetworkId != 0 && snapshot.BagNetworkId != NetworkId)
            {
                return;
            }

            ApplyFullSnapshot(snapshot);

            if (m_LogAllChanges)
            {
                Debug.Log($"[NetworkInventoryController] Received full snapshot: {snapshot.Cells?.Length ?? 0} cells");
            }
        }

        public void ReceiveDelta(NetworkInventoryDelta delta)
        {
            if (m_IsServer) return;

            if (delta.BagNetworkId != 0 && delta.BagNetworkId != NetworkId)
            {
                return;
            }

            const uint maskCells = 1u << 0;
            const uint maskEquipment = 1u << 1;
            const uint maskWealth = 1u << 2;

            if ((delta.ChangeMask & maskCells) != 0 && delta.ChangedCells != null)
            {
                ApplyCellDelta(delta.ChangedCells);
            }

            if ((delta.ChangeMask & maskEquipment) != 0 && delta.ChangedEquipment != null)
            {
                ApplyEquipmentDelta(delta.ChangedEquipment);
            }

            if ((delta.ChangeMask & maskWealth) != 0 && delta.ChangedWealth != null)
            {
                ApplyWealthDelta(delta.ChangedWealth);
            }

            CacheCurrentSyncState();

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
