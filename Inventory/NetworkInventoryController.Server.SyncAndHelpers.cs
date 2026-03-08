#if GC2_INVENTORY
using System;
using System.Collections.Generic;
using UnityEngine;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.Inventory;

namespace Arawn.GameCreator2.Networking.Inventory
{
    // ════════════════════════════════════════════════════════════════════════════════════════════
    // SERVER-SIDE — Sync, snapshot, and runtime-item helper methods
    // ════════════════════════════════════════════════════════════════════════════════════════════
    
    public partial class NetworkInventoryController
    {
        // SERVER BROADCASTING
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        private void BroadcastFullState()
        {
            var snapshot = GetFullSnapshot();
            NetworkInventoryManager.Instance?.BroadcastFullSnapshot(snapshot);
        }
        
        private void BroadcastDeltaState()
        {
            bool cellsChanged = HasInventoryPositionStateChanged();
            bool equipmentChanged = HasEquipmentStateChanged();
            bool wealthChanged = HasWealthStateChanged();

            if (!cellsChanged && !equipmentChanged && !wealthChanged)
            {
                return;
            }

            const uint maskCells = 1u << 0;
            const uint maskEquipment = 1u << 1;
            const uint maskWealth = 1u << 2;

            var delta = new NetworkInventoryDelta
            {
                BagNetworkId = NetworkId,
                Timestamp = Time.time,
                ChangeMask = (cellsChanged ? maskCells : 0u) |
                             (equipmentChanged ? maskEquipment : 0u) |
                             (wealthChanged ? maskWealth : 0u),
                ChangedCells = cellsChanged ? BuildChangedCellDelta() : Array.Empty<NetworkCell>(),
                ChangedEquipment = equipmentChanged ? BuildChangedEquipmentDelta() : Array.Empty<NetworkEquipmentSlot>(),
                ChangedWealth = wealthChanged ? BuildChangedWealthDelta() : Array.Empty<NetworkWealthEntry>()
            };

            NetworkInventoryManager.Instance?.BroadcastDelta(delta);
            CacheCurrentSyncState();

            if (m_LogAllChanges)
            {
                Debug.Log(
                    $"[NetworkInventoryController] Broadcasted delta update (mask={delta.ChangeMask}) " +
                    $"cells={delta.ChangedCells.Length} equipment={delta.ChangedEquipment.Length} wealth={delta.ChangedWealth.Length}");
            }
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
                GetStackedRuntimeIdentity(cell, out long[] stackedRuntimeIds, out string[] stackedRuntimeIdStrings);
                
                cells.Add(new NetworkCell
                {
                    Position = position,
                    ItemHash = cell.Item.ID.Hash,
                    StackCount = cell.Count,
                    RootItem = ConvertToNetworkItem(cell.RootRuntimeItem),
                    StackedRuntimeIds = stackedRuntimeIds,
                    StackedRuntimeIdStrings = stackedRuntimeIdStrings
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
            
            CleanupPendingBucket(m_PendingAdds, currentTime, timeout, "Add item");
            CleanupPendingBucket(m_PendingRemoves, currentTime, timeout, "Remove item");
            CleanupPendingBucket(m_PendingMoves, currentTime, timeout, "Move item");
            CleanupPendingBucket(m_PendingEquipment, currentTime, timeout, "Equipment operation");
            CleanupPendingBucket(m_PendingWealth, currentTime, timeout, "Wealth operation");

            void CleanupPendingBucket<T>(Dictionary<ulong, T> pending, float now, float timeoutSeconds, string operationName)
                where T : struct, ITimedPendingRequest
            {
                int removedCount = PendingRequestCleanup.RemoveTimedOut(
                    pending,
                    s_SharedKeyBuffer,
                    now,
                    timeoutSeconds);

                if (removedCount <= 0) return;

                if (m_LogRejections)
                {
                    Debug.LogWarning($"[NetworkInventoryController] {operationName} timed out ({removedCount} pending request(s) dropped).");
                }

                if (!m_IsServer)
                {
                    OnOperationRejected?.Invoke(InventoryRejectionReason.RequestTimeout, operationName);
                }
            }
        }

        private bool HasInventoryPositionStateChanged()
        {
            Dictionary<long, Vector2Int> current = BuildCurrentPositionState();
            return !DictionariesEqual(m_LastSyncedPositions, current);
        }

        private bool HasEquipmentStateChanged()
        {
            var current = new Dictionary<int, long>(Mathf.Max(1, m_Bag.Equipment.Count));
            for (int i = 0; i < m_Bag.Equipment.Count; i++)
            {
                current[i] = m_Bag.Equipment.GetSlotRootRuntimeItemID(i).Hash;
            }

            return !DictionariesEqual(m_LastSyncedEquipment, current);
        }

        private bool HasWealthStateChanged()
        {
            var current = new Dictionary<int, int>(8);
            foreach (IdString currencyId in m_Bag.Wealth.List)
            {
                current[currencyId.Hash] = m_Bag.Wealth.Get(currencyId);
            }

            return !DictionariesEqual(m_LastSyncedWealth, current);
        }

        private Dictionary<long, Vector2Int> BuildCurrentPositionState()
        {
            var current = new Dictionary<long, Vector2Int>(m_RuntimeItemMap.Count);
            foreach (Cell cell in m_Bag.Content.CellList)
            {
                if (cell == null || cell.Available) continue;

                Vector2Int position = m_Bag.Content.FindPosition(cell.RootRuntimeItemID);
                foreach (IdString runtimeId in cell.List)
                {
                    current[runtimeId.Hash] = position;
                }
            }

            return current;
        }

        private NetworkCell[] BuildChangedCellDelta()
        {
            Dictionary<long, Vector2Int> currentPositions = BuildCurrentPositionState();
            var changedPositions = new HashSet<Vector2Int>();

            foreach (KeyValuePair<long, Vector2Int> entry in currentPositions)
            {
                if (!m_LastSyncedPositions.TryGetValue(entry.Key, out Vector2Int previousPosition) ||
                    previousPosition != entry.Value)
                {
                    changedPositions.Add(entry.Value);
                }
            }

            foreach (KeyValuePair<long, Vector2Int> entry in m_LastSyncedPositions)
            {
                if (!currentPositions.ContainsKey(entry.Key))
                {
                    changedPositions.Add(entry.Value);
                }
            }

            if (changedPositions.Count == 0) return Array.Empty<NetworkCell>();

            var orderedPositions = new List<Vector2Int>(changedPositions);
            orderedPositions.Sort((left, right) =>
            {
                int x = left.x.CompareTo(right.x);
                return x != 0 ? x : left.y.CompareTo(right.y);
            });

            var changedCells = new List<NetworkCell>(orderedPositions.Count);
            foreach (Vector2Int position in orderedPositions)
            {
                Cell cell = m_Bag.Content.GetContent(position);
                if (cell == null || cell.Available)
                {
                    changedCells.Add(new NetworkCell
                    {
                        Position = position,
                        ItemHash = 0,
                        StackCount = 0,
                        RootItem = default,
                        StackedRuntimeIds = Array.Empty<long>(),
                        StackedRuntimeIdStrings = Array.Empty<string>()
                    });
                    continue;
                }

                GetStackedRuntimeIdentity(cell, out long[] stackedRuntimeIds, out string[] stackedRuntimeIdStrings);
                changedCells.Add(new NetworkCell
                {
                    Position = position,
                    ItemHash = cell.Item.ID.Hash,
                    StackCount = cell.Count,
                    RootItem = ConvertToNetworkItem(cell.RootRuntimeItem),
                    StackedRuntimeIds = stackedRuntimeIds,
                    StackedRuntimeIdStrings = stackedRuntimeIdStrings
                });
            }

            return changedCells.ToArray();
        }

        private NetworkEquipmentSlot[] BuildChangedEquipmentDelta()
        {
            var changedSlots = new List<NetworkEquipmentSlot>(Mathf.Max(1, m_Bag.Equipment.Count));
            for (int i = 0; i < m_Bag.Equipment.Count; i++)
            {
                IdString slotRuntimeId = m_Bag.Equipment.GetSlotRootRuntimeItemID(i);
                long currentRuntimeHash = slotRuntimeId.Hash;
                if (m_LastSyncedEquipment.TryGetValue(i, out long previousRuntimeHash) &&
                    previousRuntimeHash == currentRuntimeHash)
                {
                    continue;
                }

                changedSlots.Add(new NetworkEquipmentSlot
                {
                    SlotIndex = i,
                    BaseItemHash = m_Bag.Equipment.GetSlotBaseID(i).Hash,
                    IsOccupied = !string.IsNullOrEmpty(slotRuntimeId.String),
                    EquippedRuntimeIdHash = currentRuntimeHash
                });
            }

            return changedSlots.ToArray();
        }

        private NetworkWealthEntry[] BuildChangedWealthDelta()
        {
            var changedEntries = new List<NetworkWealthEntry>(m_Bag.Wealth.List.Count);
            var seenCurrencyHashes = new HashSet<int>();

            foreach (IdString currencyId in m_Bag.Wealth.List)
            {
                int hash = currencyId.Hash;
                int amount = m_Bag.Wealth.Get(currencyId);
                seenCurrencyHashes.Add(hash);

                if (m_LastSyncedWealth.TryGetValue(hash, out int previousAmount) &&
                    previousAmount == amount)
                {
                    continue;
                }

                changedEntries.Add(new NetworkWealthEntry
                {
                    CurrencyHash = hash,
                    Amount = amount
                });
            }

            foreach (KeyValuePair<int, int> entry in m_LastSyncedWealth)
            {
                if (seenCurrencyHashes.Contains(entry.Key)) continue;

                changedEntries.Add(new NetworkWealthEntry
                {
                    CurrencyHash = entry.Key,
                    Amount = 0
                });
            }

            return changedEntries.ToArray();
        }

        private void CacheCurrentSyncState()
        {
            Dictionary<long, Vector2Int> currentPositions = BuildCurrentPositionState();
            m_LastSyncedPositions.Clear();
            foreach (KeyValuePair<long, Vector2Int> entry in currentPositions)
            {
                m_LastSyncedPositions[entry.Key] = entry.Value;
            }

            m_LastSyncedEquipment.Clear();
            for (int i = 0; i < m_Bag.Equipment.Count; i++)
            {
                m_LastSyncedEquipment[i] = m_Bag.Equipment.GetSlotRootRuntimeItemID(i).Hash;
            }

            m_LastSyncedWealth.Clear();
            foreach (IdString currencyId in m_Bag.Wealth.List)
            {
                m_LastSyncedWealth[currencyId.Hash] = m_Bag.Wealth.Get(currencyId);
            }
        }

        private static bool DictionariesEqual<TKey, TValue>(
            Dictionary<TKey, TValue> left,
            Dictionary<TKey, TValue> right)
        {
            if (ReferenceEquals(left, right)) return true;
            if (left == null || right == null) return false;
            if (left.Count != right.Count) return false;

            var comparer = EqualityComparer<TValue>.Default;
            foreach (var entry in left)
            {
                if (!right.TryGetValue(entry.Key, out TValue value)) return false;
                if (!comparer.Equals(entry.Value, value)) return false;
            }

            return true;
        }
        
        private bool TryResolveItem(int itemHash, string itemIdString, out Item item)
        {
            item = null;
            InventoryRepository inventory = Settings.From<InventoryRepository>();
            if (inventory == null) return false;

            if (string.IsNullOrWhiteSpace(itemIdString))
            {
                return false;
            }

            var itemId = new IdString(itemIdString);
            if (itemId.Hash != itemHash) return false;

            item = inventory.Items.Get(itemId);
            return item != null && item.ID.Hash == itemHash;
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
                ItemIdString = runtimeItem.ItemID.String,
                RuntimeIdHash = runtimeItem.RuntimeID.Hash,
                RuntimeIdString = runtimeItem.RuntimeID.String,
                Properties = properties.ToArray(),
                Sockets = sockets.ToArray()
            };
        }
        
        private RuntimeItem ReconstructRuntimeItem(NetworkRuntimeItem networkItem)
        {
            if (networkItem.ItemHash == 0) return null;

            if (!TryResolveItem(networkItem.ItemHash, networkItem.ItemIdString, out Item item))
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

        private void ApplyCellDelta(NetworkCell[] changedCells)
        {
            if (changedCells == null) return;

            foreach (NetworkCell cell in changedCells)
            {
                ClearCellAtPosition(cell.Position);

                bool isDeleteEntry = cell.ItemHash == 0 || cell.StackCount <= 0 || cell.RootItem.ItemHash == 0;
                if (isDeleteEntry)
                {
                    continue;
                }

                RuntimeItem rootItem = ReconstructRuntimeItem(cell.RootItem);
                if (rootItem == null)
                {
                    continue;
                }

                bool addedRoot = m_Bag.Content.Add(rootItem, cell.Position, true);
                if (!addedRoot)
                {
                    continue;
                }

                TrackRuntimeItemRecursive(rootItem);

                int stackCount = Mathf.Max(1, cell.StackCount);
                long[] stackedRuntimeIds = cell.StackedRuntimeIds;
                string[] stackedRuntimeIdStrings = cell.StackedRuntimeIdStrings;
                for (int i = 1; i < stackCount; i++)
                {
                    RuntimeItem stackedItem = new RuntimeItem(rootItem, true);
                    int stackedIndex = i - 1;
                    if (stackedRuntimeIds != null && stackedIndex < stackedRuntimeIds.Length)
                    {
                        string runtimeIdString = stackedRuntimeIdStrings != null && stackedIndex < stackedRuntimeIdStrings.Length
                            ? stackedRuntimeIdStrings[stackedIndex]
                            : null;
                        TryApplyRuntimeId(stackedItem, runtimeIdString, stackedRuntimeIds[stackedIndex]);
                    }

                    if (m_Bag.Content.Add(stackedItem, cell.Position, true))
                    {
                        TrackRuntimeItemRecursive(stackedItem);
                    }
                }
            }
        }

        private void ApplyEquipmentDelta(NetworkEquipmentSlot[] changedEquipment)
        {
            if (changedEquipment == null) return;

            foreach (NetworkEquipmentSlot slot in changedEquipment)
            {
                if (slot.SlotIndex < 0 || slot.SlotIndex >= m_Bag.Equipment.Count)
                {
                    continue;
                }

                _ = m_Bag.Equipment.UnequipFromIndex(slot.SlotIndex);
                if (!slot.IsOccupied)
                {
                    continue;
                }

                if (m_RuntimeItemMap.TryGetValue(slot.EquippedRuntimeIdHash, out RuntimeItem runtimeItem))
                {
                    _ = m_Bag.Equipment.EquipToIndex(runtimeItem, slot.SlotIndex);
                }
            }
        }

        private void ApplyWealthDelta(NetworkWealthEntry[] changedWealth)
        {
            if (changedWealth == null) return;

            foreach (NetworkWealthEntry wealthEntry in changedWealth)
            {
                if (TryResolveCurrencyIdByHash(wealthEntry.CurrencyHash, out IdString currencyId))
                {
                    m_Bag.Wealth.Set(currencyId, wealthEntry.Amount);
                }
            }
        }

        private void ClearCellAtPosition(Vector2Int position)
        {
            int safety = 0;
            while (safety++ < 256)
            {
                RuntimeItem removed = m_Bag.Content.Remove(position);
                if (removed == null)
                {
                    break;
                }

                UntrackRuntimeItemRecursive(removed);
            }
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
                    long[] stackedRuntimeIds = cell.StackedRuntimeIds;
                    string[] stackedRuntimeIdStrings = cell.StackedRuntimeIdStrings;
                    for (int i = 1; i < stackCount; i++)
                    {
                        RuntimeItem stackedItem = new RuntimeItem(rootItem, true);
                        int stackedIndex = i - 1;
                        if (stackedRuntimeIds != null && stackedIndex < stackedRuntimeIds.Length)
                        {
                            string runtimeIdString = stackedRuntimeIdStrings != null && stackedIndex < stackedRuntimeIdStrings.Length
                                ? stackedRuntimeIdStrings[stackedIndex]
                                : null;
                            TryApplyRuntimeId(stackedItem, runtimeIdString, stackedRuntimeIds[stackedIndex]);
                        }

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

            CacheCurrentSyncState();
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
        
        private static void GetStackedRuntimeIdentity(Cell cell, out long[] runtimeIds, out string[] runtimeIdStrings)
        {
            var ids = new List<long>();
            var idStrings = new List<string>();
            foreach (var id in cell.List)
            {
                if (id.Hash == cell.RootRuntimeItemID.Hash) continue;
                ids.Add(id.Hash);
                idStrings.Add(id.String);
            }

            runtimeIds = ids.ToArray();
            runtimeIdStrings = idStrings.ToArray();
        }
    }
}
#endif
