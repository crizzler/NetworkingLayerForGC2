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
        
        #endregion
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // SERVER BROADCASTING
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        private void BroadcastFullState()
        {
            var snapshot = GetFullSnapshot();
            NetworkInventoryManager.Instance?.BroadcastFullSnapshot(snapshot);
        }
        
        private void BroadcastDeltaState()
        {
            // Implement delta tracking and broadcast
            // This is complex due to the nature of inventory changes
        }
        
        /// <summary>
        /// Get full inventory snapshot for initial sync.
        /// </summary>
        public NetworkInventorySnapshot GetFullSnapshot()
        {
            var cells = new List<NetworkCell>();
            var equipment = new List<NetworkEquipmentSlot>();
            var wealth = new List<NetworkWealthEntry>();
            
            // Collect cells
            foreach (var cell in m_Bag.Content.CellList)
            {
                if (cell == null || cell.Available) continue;
                
                var position = m_Bag.Content.FindPosition(cell.RootRuntimeItemID);
                
                cells.Add(new NetworkCell
                {
                    Position = position,
                    ItemHash = cell.Item.ID.Hash,
                    StackCount = cell.Count,
                    RootItem = ConvertToNetworkItem(cell.RootRuntimeItem),
                    StackedRuntimeIds = GetStackedIds(cell)
                });
            }
            
            // Collect equipment
            for (int i = 0; i < m_Bag.Equipment.Count; i++)
            {
                var slotId = m_Bag.Equipment.GetSlotRootRuntimeItemID(i);
                var baseId = m_Bag.Equipment.GetSlotBaseID(i);
                
                equipment.Add(new NetworkEquipmentSlot
                {
                    SlotIndex = i,
                    BaseItemHash = baseId.Hash,
                    IsOccupied = !string.IsNullOrEmpty(slotId.String),
                    EquippedRuntimeIdHash = slotId.Hash
                });
            }
            
            // Collect wealth
            foreach (var currencyId in m_Bag.Wealth.List)
            {
                wealth.Add(new NetworkWealthEntry
                {
                    CurrencyHash = currencyId.Hash,
                    Amount = m_Bag.Wealth.Get(currencyId)
                });
            }
            
            return new NetworkInventorySnapshot
            {
                BagNetworkId = NetworkId,
                Timestamp = Time.time,
                Cells = cells.ToArray(),
                Equipment = equipment.ToArray(),
                Wealth = wealth.ToArray()
            };
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // HELPER METHODS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        private void CleanupPendingRequests()
        {
            float timeout = 5f;
            float currentTime = Time.time;
            
            CleanupPending(m_PendingAdds, currentTime, timeout);
            CleanupPending(m_PendingRemoves, currentTime, timeout);
            CleanupPending(m_PendingMoves, currentTime, timeout);
            CleanupPending(m_PendingEquipment, currentTime, timeout);
            CleanupPending(m_PendingWealth, currentTime, timeout);
        }
        
        private void CleanupPending<T>(Dictionary<uint, T> pending, float currentTime, float timeout) where T : struct
        {
            s_SharedKeyBuffer.Clear();
            foreach (var kvp in pending)
            {
                // Using reflection to get SentTime - in production, use interface
                var sentTimeField = typeof(T).GetField("SentTime");
                if (sentTimeField != null)
                {
                    float sentTime = (float)sentTimeField.GetValue(kvp.Value);
                    if (currentTime - sentTime > timeout)
                        s_SharedKeyBuffer.Add(kvp.Key);
                }
            }
            foreach (var key in s_SharedKeyBuffer)
                pending.Remove(key);
        }
        
        private bool TryResolveItem(int itemHash, string itemIdString, out Item item)
        {
            item = null;
            InventoryRepository inventory = Settings.From<InventoryRepository>();
            if (inventory == null) return false;

            if (string.IsNullOrWhiteSpace(itemIdString))
            {
                item = FindItemByHash(inventory, itemHash);
                return item != null;
            }

            var itemId = new IdString(itemIdString);
            if (itemId.Hash != itemHash) return false;

            item = inventory.Items.Get(itemId);
            return item != null && item.ID.Hash == itemHash;
        }

        private static Item FindItemByHash(InventoryRepository inventory, int hash)
        {
            Item[] items = inventory.Items.List;
            if (items == null) return null;

            foreach (Item entry in items)
            {
                if (entry != null && entry.ID.Hash == hash)
                {
                    return entry;
                }
            }

            return null;
        }

        private bool TryResolveCurrencyId(int currencyHash, string currencyIdString, out IdString currencyId)
        {
            currencyId = IdString.EMPTY;
            if (string.IsNullOrWhiteSpace(currencyIdString)) return false;

            currencyId = new IdString(currencyIdString);
            if (currencyId.Hash != currencyHash) return false;

            foreach (IdString entry in m_Bag.Wealth.List)
            {
                if (entry.Hash == currencyHash && entry == currencyId)
                {
                    return true;
                }
            }

            return false;
        }

        private bool TryResolveCurrencyIdByHash(int currencyHash, out IdString currencyId)
        {
            currencyId = IdString.EMPTY;
            foreach (IdString entry in m_Bag.Wealth.List)
            {
                if (entry.Hash == currencyHash)
                {
                    currencyId = entry;
                    return true;
                }
            }

            return false;
        }

        private static bool TryResolveSocketId(RuntimeItem parentItem, int socketHash, string socketIdString, out IdString socketId)
        {
            socketId = IdString.EMPTY;
            if (parentItem == null || parentItem.Item == null) return false;
            if (string.IsNullOrWhiteSpace(socketIdString)) return false;

            socketId = new IdString(socketIdString);
            if (socketId.Hash != socketHash) return false;

            var sockets = Sockets.FlattenHierarchy(parentItem.Item);
            return sockets != null && sockets.ContainsKey(socketId);
        }
        
        private NetworkRuntimeItem ConvertToNetworkItem(RuntimeItem runtimeItem)
        {
            if (runtimeItem == null) return default;
            
            var properties = new List<NetworkRuntimeProperty>();
            foreach (var prop in runtimeItem.Properties)
            {
                properties.Add(new NetworkRuntimeProperty
                {
                    PropertyHash = prop.Key.Hash,
                    PropertyIdString = prop.Key.String,
                    Number = prop.Value.Number,
                    Text = prop.Value.Text
                });
            }
            
            var sockets = new List<NetworkRuntimeSocket>();
            foreach (var socket in runtimeItem.Sockets)
            {
                sockets.Add(new NetworkRuntimeSocket
                {
                    SocketHash = socket.Key.Hash,
                    SocketIdString = socket.Key.String,
                    HasAttachment = socket.Value.HasAttachment,
                    Attachment = socket.Value.HasAttachment ? ConvertToNetworkItem(socket.Value.Attachment) : default
                });
            }
            
            return new NetworkRuntimeItem
            {
                ItemHash = runtimeItem.ItemID.Hash,
                RuntimeIdHash = runtimeItem.RuntimeID.Hash,
                RuntimeIdString = runtimeItem.RuntimeID.String,
                Properties = properties.ToArray(),
                Sockets = sockets.ToArray()
            };
        }
        
        private RuntimeItem ReconstructRuntimeItem(NetworkRuntimeItem networkItem)
        {
            if (networkItem.ItemHash == 0) return null;

            if (!TryResolveItem(networkItem.ItemHash, string.Empty, out Item item))
            {
                return null;
            }

            var runtimeItem = new RuntimeItem(item);
            TryApplyRuntimeId(runtimeItem, networkItem.RuntimeIdString, networkItem.RuntimeIdHash);

            if (networkItem.Properties != null)
            {
                foreach (NetworkRuntimeProperty property in networkItem.Properties)
                {
                    if (!TryResolveRuntimePropertyId(runtimeItem, property.PropertyHash, property.PropertyIdString, out IdString propertyId))
                    {
                        continue;
                    }

                    if (!runtimeItem.Properties.TryGetValue(propertyId, out RuntimeProperty runtimeProperty))
                    {
                        continue;
                    }

                    runtimeProperty.Number = property.Number;
                    runtimeProperty.Text = property.Text;
                }
            }

            if (networkItem.Sockets != null && s_RuntimeSocketAttachmentField != null)
            {
                foreach (NetworkRuntimeSocket socket in networkItem.Sockets)
                {
                    if (!TryResolveRuntimeSocketId(runtimeItem, socket.SocketHash, socket.SocketIdString, out IdString socketId) ||
                        !runtimeItem.Sockets.TryGetValue(socketId, out RuntimeSocket runtimeSocket))
                    {
                        continue;
                    }

                    if (!socket.HasAttachment)
                    {
                        s_RuntimeSocketAttachmentField.SetValue(runtimeSocket, null);
                        continue;
                    }

                    RuntimeItem attachment = ReconstructRuntimeItem(socket.Attachment);
                    if (attachment != null)
                    {
                        s_RuntimeSocketAttachmentField.SetValue(runtimeSocket, attachment);
                    }
                }
            }

            return runtimeItem;
        }

        private void ApplyFullSnapshot(NetworkInventorySnapshot snapshot)
        {
            ClearCurrentInventoryState();

            if (snapshot.Cells != null)
            {
                foreach (NetworkCell cell in snapshot.Cells)
                {
                    RuntimeItem rootItem = ReconstructRuntimeItem(cell.RootItem);
                    if (rootItem == null) continue;

                    bool addedRoot = m_Bag.Content.Add(rootItem, cell.Position, true);
                    if (!addedRoot)
                    {
                        continue;
                    }

                    TrackRuntimeItemRecursive(rootItem);

                    int stackCount = Mathf.Max(1, cell.StackCount);
                    for (int i = 1; i < stackCount; i++)
                    {
                        RuntimeItem stackedItem = new RuntimeItem(rootItem, true);
                        if (m_Bag.Content.Add(stackedItem, cell.Position, true))
                        {
                            TrackRuntimeItemRecursive(stackedItem);
                        }
                    }
                }
            }

            for (int i = 0; i < m_Bag.Equipment.Count; i++)
            {
                _ = m_Bag.Equipment.UnequipFromIndex(i);
            }

            if (snapshot.Equipment != null)
            {
                foreach (NetworkEquipmentSlot slot in snapshot.Equipment)
                {
                    if (!slot.IsOccupied) continue;
                    if (!m_RuntimeItemMap.TryGetValue(slot.EquippedRuntimeIdHash, out RuntimeItem runtimeItem)) continue;
                    _ = m_Bag.Equipment.EquipToIndex(runtimeItem, slot.SlotIndex);
                }
            }

            foreach (IdString currencyId in m_Bag.Wealth.List)
            {
                m_Bag.Wealth.Set(currencyId, 0);
            }

            if (snapshot.Wealth != null)
            {
                foreach (NetworkWealthEntry wealthEntry in snapshot.Wealth)
                {
                    if (TryResolveCurrencyIdByHash(wealthEntry.CurrencyHash, out IdString currencyId))
                    {
                        m_Bag.Wealth.Set(currencyId, wealthEntry.Amount);
                    }
                }
            }
        }

        private void ClearCurrentInventoryState()
        {
            int safety = 0;
            while (safety++ < 4096)
            {
                RuntimeItem itemToRemove = null;
                foreach (Cell cell in m_Bag.Content.CellList)
                {
                    if (cell == null || cell.Available) continue;
                    itemToRemove = cell.Peek();
                    if (itemToRemove != null) break;
                }

                if (itemToRemove == null) break;
                m_Bag.Content.Remove(itemToRemove);
            }

            m_RuntimeItemMap.Clear();
        }

        private void TrackRuntimeItemRecursive(RuntimeItem runtimeItem)
        {
            if (runtimeItem == null) return;

            m_RuntimeItemMap[runtimeItem.RuntimeID.Hash] = runtimeItem;
            foreach (KeyValuePair<IdString, RuntimeSocket> socketEntry in runtimeItem.Sockets)
            {
                RuntimeSocket socket = socketEntry.Value;
                if (socket == null || !socket.HasAttachment) continue;
                TrackRuntimeItemRecursive(socket.Attachment);
            }
        }

        private void UntrackRuntimeItemRecursive(RuntimeItem runtimeItem)
        {
            if (runtimeItem == null) return;

            m_RuntimeItemMap.Remove(runtimeItem.RuntimeID.Hash);
            foreach (KeyValuePair<IdString, RuntimeSocket> socketEntry in runtimeItem.Sockets)
            {
                RuntimeSocket socket = socketEntry.Value;
                if (socket == null || !socket.HasAttachment) continue;
                UntrackRuntimeItemRecursive(socket.Attachment);
            }
        }

        private static void TryApplyRuntimeId(RuntimeItem runtimeItem, string runtimeIdString, long runtimeIdHash)
        {
            if (runtimeItem == null || s_RuntimeItemIdField == null) return;
            if (string.IsNullOrWhiteSpace(runtimeIdString)) return;

            IdString runtimeId = new IdString(runtimeIdString);
            if (runtimeIdHash != 0 && runtimeId.Hash != runtimeIdHash) return;
            s_RuntimeItemIdField.SetValue(runtimeItem, runtimeId);
        }

        private static bool TryResolveRuntimePropertyId(RuntimeItem runtimeItem, int propertyHash, string propertyIdString, out IdString propertyId)
        {
            propertyId = IdString.EMPTY;
            if (runtimeItem == null) return false;

            if (!string.IsNullOrWhiteSpace(propertyIdString))
            {
                IdString candidate = new IdString(propertyIdString);
                if (candidate.Hash == propertyHash && runtimeItem.Properties.ContainsKey(candidate))
                {
                    propertyId = candidate;
                    return true;
                }
            }

            foreach (KeyValuePair<IdString, RuntimeProperty> entry in runtimeItem.Properties)
            {
                if (entry.Key.Hash != propertyHash) continue;
                propertyId = entry.Key;
                return true;
            }

            return false;
        }

        private static bool TryResolveRuntimeSocketId(RuntimeItem runtimeItem, int socketHash, string socketIdString, out IdString socketId)
        {
            socketId = IdString.EMPTY;
            if (runtimeItem == null) return false;

            if (!string.IsNullOrWhiteSpace(socketIdString))
            {
                IdString candidate = new IdString(socketIdString);
                if (candidate.Hash == socketHash && runtimeItem.Sockets.ContainsKey(candidate))
                {
                    socketId = candidate;
                    return true;
                }
            }

            foreach (KeyValuePair<IdString, RuntimeSocket> entry in runtimeItem.Sockets)
            {
                if (entry.Key.Hash != socketHash) continue;
                socketId = entry.Key;
                return true;
            }

            return false;
        }
        
        private long[] GetStackedIds(Cell cell)
        {
            var ids = new List<long>();
            foreach (var id in cell.List)
            {
                if (id.Hash != cell.RootRuntimeItemID.Hash)
                    ids.Add(id.Hash);
            }
            return ids.ToArray();
        }
    }
}
#endif
