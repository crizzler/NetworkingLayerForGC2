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
    // SERVER-SIDE — Sync, snapshot, and runtime-item helper methods
    // ════════════════════════════════════════════════════════════════════════════════════════════

    public partial class NetworkInventoryController
    {
        private const ulong FnvOffsetBasis = 14695981039346656037UL;
        private const ulong FnvPrime = 1099511628211UL;

        // SERVER BROADCASTING
        // ════════════════════════════════════════════════════════════════════════════════════════

        private void BroadcastFullState()
        {
            var snapshot = GetFullSnapshot();
            NetworkInventoryManager.Instance?.BroadcastFullSnapshot(snapshot);
            CacheCurrentSyncState();
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
                StateVersion = GetAuthoritativeStateVersion(),
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

                Vector2Int position = m_Bag.Content.FindPosition(cell.RootRuntimeItemID);
                cells.Add(CreateNetworkCell(cell, position));
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
                StateVersion = GetAuthoritativeStateVersion(),
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
            Dictionary<Vector2Int, ulong> current = BuildCurrentCellState();
            return !DictionariesEqual(m_LastSyncedCells, current);
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

        private Dictionary<Vector2Int, ulong> BuildCurrentCellState()
        {
            var current = new Dictionary<Vector2Int, ulong>(m_Bag.Content.CellList.Count);
            foreach (Cell cell in m_Bag.Content.CellList)
            {
                if (cell == null || cell.Available) continue;

                Vector2Int position = m_Bag.Content.FindPosition(cell.RootRuntimeItemID);
                if (position == TBagContent.INVALID) continue;
                current[position] = ComputeCellFingerprint(CreateNetworkCell(cell, position));
            }

            return current;
        }

        private NetworkCell[] BuildChangedCellDelta()
        {
            Dictionary<Vector2Int, ulong> currentCells = BuildCurrentCellState();
            var changedPositions = new HashSet<Vector2Int>();

            foreach (KeyValuePair<Vector2Int, ulong> entry in currentCells)
            {
                if (!m_LastSyncedCells.TryGetValue(entry.Key, out ulong previousFingerprint) ||
                    previousFingerprint != entry.Value)
                {
                    changedPositions.Add(entry.Key);
                }
            }

            foreach (KeyValuePair<Vector2Int, ulong> entry in m_LastSyncedCells)
            {
                if (!currentCells.ContainsKey(entry.Key))
                {
                    changedPositions.Add(entry.Key);
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
                        StackedRuntimeIdStrings = Array.Empty<string>(),
                        StackedItems = Array.Empty<NetworkRuntimeItem>()
                    });
                    continue;
                }

                changedCells.Add(CreateNetworkCell(cell, position));
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
            Dictionary<Vector2Int, ulong> currentCells = BuildCurrentCellState();
            m_LastSyncedCells.Clear();
            foreach (KeyValuePair<Vector2Int, ulong> entry in currentCells)
            {
                m_LastSyncedCells[entry.Key] = entry.Value;
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

        private uint GetAuthoritativeStateVersion()
        {
            if (!m_IsServer)
            {
                return m_HasAppliedStateVersion ? m_LastAppliedStateVersion : 0u;
            }

            ulong fingerprint = ComputeInventoryFingerprint();
            if (!m_HasVersionedStateFingerprint)
            {
                m_StateVersion = 1u;
                m_LastVersionedStateFingerprint = fingerprint;
                m_HasVersionedStateFingerprint = true;
                return m_StateVersion;
            }

            if (m_LastVersionedStateFingerprint == fingerprint) return m_StateVersion;

            m_StateVersion = m_StateVersion == uint.MaxValue ? 1u : m_StateVersion + 1u;
            m_LastVersionedStateFingerprint = fingerprint;
            return m_StateVersion;
        }

        private bool IsStaleStateVersion(uint incomingVersion)
        {
            if (incomingVersion == 0 || !m_HasAppliedStateVersion) return false;
            return unchecked((int)(incomingVersion - m_LastAppliedStateVersion)) < 0;
        }

        private void RecordAppliedStateVersion(uint incomingVersion)
        {
            if (incomingVersion == 0) return;
            if (!m_HasAppliedStateVersion ||
                unchecked((int)(incomingVersion - m_LastAppliedStateVersion)) > 0)
            {
                m_LastAppliedStateVersion = incomingVersion;
                m_HasAppliedStateVersion = true;
            }
            TryCompleteDeferredAsyncAdds();
        }

        private ulong ComputeInventoryFingerprint()
        {
            ulong hash = FnvOffsetBasis;
            var cells = new List<KeyValuePair<Vector2Int, ulong>>(BuildCurrentCellState());
            cells.Sort((left, right) => ComparePositions(left.Key, right.Key));
            foreach (KeyValuePair<Vector2Int, ulong> entry in cells)
            {
                hash = HashInt(hash, entry.Key.x);
                hash = HashInt(hash, entry.Key.y);
                hash = HashULong(hash, entry.Value);
            }

            for (int i = 0; i < m_Bag.Equipment.Count; i++)
            {
                hash = HashInt(hash, i);
                hash = HashLong(hash, m_Bag.Equipment.GetSlotRootRuntimeItemID(i).Hash);
            }

            var wealth = new List<KeyValuePair<int, int>>(m_Bag.Wealth.List.Count);
            foreach (IdString currencyId in m_Bag.Wealth.List)
            {
                wealth.Add(new KeyValuePair<int, int>(currencyId.Hash, m_Bag.Wealth.Get(currencyId)));
            }

            wealth.Sort((left, right) => left.Key.CompareTo(right.Key));
            foreach (KeyValuePair<int, int> entry in wealth)
            {
                hash = HashInt(hash, entry.Key);
                hash = HashInt(hash, entry.Value);
            }

            return hash;
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

        private NetworkCell CreateNetworkCell(Cell cell, Vector2Int position)
        {
            GetStackedRuntimeIdentity(
                cell,
                out long[] stackedRuntimeIds,
                out string[] stackedRuntimeIdStrings,
                out NetworkRuntimeItem[] stackedItems);

            return new NetworkCell
            {
                Position = position,
                ItemHash = cell.Item.ID.Hash,
                StackCount = cell.Count,
                RootItem = ConvertToNetworkItem(cell.RootRuntimeItem),
                StackedRuntimeIds = stackedRuntimeIds,
                StackedRuntimeIdStrings = stackedRuntimeIdStrings,
                StackedItems = stackedItems
            };
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

            properties.Sort((left, right) =>
            {
                int hash = left.PropertyHash.CompareTo(right.PropertyHash);
                return hash != 0
                    ? hash
                    : string.CompareOrdinal(left.PropertyIdString, right.PropertyIdString);
            });

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

            sockets.Sort((left, right) =>
            {
                int hash = left.SocketHash.CompareTo(right.SocketHash);
                return hash != 0
                    ? hash
                    : string.CompareOrdinal(left.SocketIdString, right.SocketIdString);
            });

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

        private static int ComparePositions(Vector2Int left, Vector2Int right)
        {
            int x = left.x.CompareTo(right.x);
            return x != 0 ? x : left.y.CompareTo(right.y);
        }

        private static ulong ComputeCellFingerprint(NetworkCell cell)
        {
            ulong hash = FnvOffsetBasis;
            hash = HashInt(hash, cell.Position.x);
            hash = HashInt(hash, cell.Position.y);
            hash = HashInt(hash, cell.ItemHash);
            hash = HashInt(hash, cell.StackCount);
            hash = HashRuntimeItem(hash, cell.RootItem);

            if (cell.StackedItems != null && cell.StackedItems.Length > 0)
            {
                hash = HashInt(hash, cell.StackedItems.Length);
                for (int i = 0; i < cell.StackedItems.Length; i++)
                {
                    hash = HashRuntimeItem(hash, cell.StackedItems[i]);
                }
            }
            else
            {
                int count = cell.StackedRuntimeIds?.Length ?? 0;
                hash = HashInt(hash, count);
                for (int i = 0; i < count; i++)
                {
                    hash = HashLong(hash, cell.StackedRuntimeIds[i]);
                    hash = HashString(hash,
                        cell.StackedRuntimeIdStrings != null && i < cell.StackedRuntimeIdStrings.Length
                            ? cell.StackedRuntimeIdStrings[i]
                            : null);
                }
            }

            return hash;
        }

        private static ulong HashRuntimeItem(ulong hash, NetworkRuntimeItem item)
        {
            hash = HashInt(hash, item.ItemHash);
            hash = HashString(hash, item.ItemIdString);
            hash = HashLong(hash, item.RuntimeIdHash);
            hash = HashString(hash, item.RuntimeIdString);

            int propertyCount = item.Properties?.Length ?? 0;
            hash = HashInt(hash, propertyCount);
            for (int i = 0; i < propertyCount; i++)
            {
                NetworkRuntimeProperty property = item.Properties[i];
                hash = HashInt(hash, property.PropertyHash);
                hash = HashString(hash, property.PropertyIdString);
                hash = HashInt(hash, BitConverter.SingleToInt32Bits(property.Number));
                hash = HashString(hash, property.Text);
            }

            int socketCount = item.Sockets?.Length ?? 0;
            hash = HashInt(hash, socketCount);
            for (int i = 0; i < socketCount; i++)
            {
                NetworkRuntimeSocket socket = item.Sockets[i];
                hash = HashInt(hash, socket.SocketHash);
                hash = HashString(hash, socket.SocketIdString);
                hash = HashInt(hash, socket.HasAttachment ? 1 : 0);
                if (socket.HasAttachment) hash = HashRuntimeItem(hash, socket.Attachment);
            }

            return hash;
        }

        private static ulong HashInt(ulong hash, int value) => HashULong(hash, unchecked((uint)value));

        private static ulong HashLong(ulong hash, long value) => HashULong(hash, unchecked((ulong)value));

        private static ulong HashULong(ulong hash, ulong value)
        {
            for (int i = 0; i < sizeof(ulong); i++)
            {
                hash ^= (byte)(value >> (i * 8));
                hash *= FnvPrime;
            }

            return hash;
        }

        private static ulong HashString(ulong hash, string value)
        {
            if (value == null) return HashInt(hash, -1);

            hash = HashInt(hash, value.Length);
            for (int i = 0; i < value.Length; i++)
            {
                hash ^= value[i];
                hash *= FnvPrime;
            }

            return hash;
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
            if (changedCells == null || changedCells.Length == 0) return;

            var changedPositions = new HashSet<Vector2Int>();
            for (int i = 0; i < changedCells.Length; i++)
            {
                changedPositions.Add(changedCells[i].Position);
            }

            // Preserve RuntimeItem instances for ordinary moves before applying removals. This keeps
            // GC2's selected-item UI reference alive when a reliable operation broadcast was missed.
            for (int i = 0; i < changedCells.Length; i++)
            {
                NetworkCell desired = changedCells[i];
                if (IsDeleteCell(desired) || CellMatchesLocal(desired)) continue;
                if (desired.RootItem.RuntimeIdHash == 0) continue;
                if (!m_RuntimeItemMap.TryGetValue(desired.RootItem.RuntimeIdHash, out RuntimeItem existing)) continue;
                if (existing == null || existing.Bag != m_Bag) continue;

                Vector2Int source = m_Bag.Content.FindPosition(existing.RuntimeID);
                if (source == TBagContent.INVALID || source == desired.Position) continue;
                if (!changedPositions.Contains(source)) continue;

                Cell sourceCell = m_Bag.Content.GetContent(source);
                if (!CellIdentityMatchesNetwork(sourceCell, desired)) continue;
                _ = m_Bag.Content.Move(source, desired.Position, true);
            }

            for (int i = 0; i < changedCells.Length; i++)
            {
                NetworkCell desired = changedCells[i];
                if (IsDeleteCell(desired))
                {
                    if (m_Bag.Content.GetContent(desired.Position) is { Available: false })
                    {
                        ClearCellAtPosition(desired.Position);
                    }

                    continue;
                }

                if (CellMatchesLocal(desired)) continue;
                if (TryApplyCellPayloadInPlace(desired) && CellMatchesLocal(desired)) continue;

                ClearCellAtPosition(desired.Position);
                AddNetworkCell(desired);
            }
        }

        private async Task ApplyEquipmentDelta(NetworkEquipmentSlot[] changedEquipment)
        {
            if (changedEquipment == null) return;

            foreach (NetworkEquipmentSlot slot in changedEquipment)
            {
                if (slot.SlotIndex < 0 || slot.SlotIndex >= m_Bag.Equipment.Count)
                {
                    continue;
                }

                long currentRuntimeHash = m_Bag.Equipment.GetSlotRootRuntimeItemID(slot.SlotIndex).Hash;
                long desiredRuntimeHash = slot.IsOccupied ? slot.EquippedRuntimeIdHash : 0L;
                if (currentRuntimeHash == desiredRuntimeHash) continue;

                if (currentRuntimeHash != 0)
                {
                    bool unequipped = await m_Bag.Equipment.UnequipFromIndex(slot.SlotIndex);
                    if (!unequipped &&
                        m_Bag.Equipment.GetSlotRootRuntimeItemID(slot.SlotIndex).Hash != 0)
                    {
                        continue;
                    }
                }
                if (!slot.IsOccupied)
                {
                    continue;
                }

                if (m_RuntimeItemMap.TryGetValue(slot.EquippedRuntimeIdHash, out RuntimeItem runtimeItem))
                {
                    await m_Bag.Equipment.EquipToIndex(runtimeItem, slot.SlotIndex);
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
                    if (m_Bag.Wealth.Get(currencyId) == wealthEntry.Amount) continue;
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

        private async Task ApplyFullSnapshot(NetworkInventorySnapshot snapshot)
        {
            var desiredPositions = new HashSet<Vector2Int>();
            var cells = new List<NetworkCell>(snapshot.Cells?.Length ?? 0);
            if (snapshot.Cells != null)
            {
                for (int i = 0; i < snapshot.Cells.Length; i++)
                {
                    NetworkCell cell = snapshot.Cells[i];
                    desiredPositions.Add(cell.Position);
                    cells.Add(cell);
                }
            }

            foreach (Cell localCell in m_Bag.Content.CellList)
            {
                if (localCell == null || localCell.Available) continue;
                Vector2Int position = m_Bag.Content.FindPosition(localCell.RootRuntimeItemID);
                if (position == TBagContent.INVALID || desiredPositions.Contains(position)) continue;
                cells.Add(CreateDeleteCell(position));
            }

            ApplyCellDelta(cells.ToArray());
            RebuildRuntimeItemMap();

            var equipment = new NetworkEquipmentSlot[m_Bag.Equipment.Count];
            var equipmentByIndex = new Dictionary<int, NetworkEquipmentSlot>();
            if (snapshot.Equipment != null)
            {
                for (int i = 0; i < snapshot.Equipment.Length; i++)
                {
                    equipmentByIndex[snapshot.Equipment[i].SlotIndex] = snapshot.Equipment[i];
                }
            }

            for (int i = 0; i < equipment.Length; i++)
            {
                equipment[i] = equipmentByIndex.TryGetValue(i, out NetworkEquipmentSlot slot)
                    ? slot
                    : new NetworkEquipmentSlot { SlotIndex = i, IsOccupied = false };
            }

            await ApplyEquipmentDelta(equipment);

            var desiredWealth = new Dictionary<int, int>();
            if (snapshot.Wealth != null)
            {
                for (int i = 0; i < snapshot.Wealth.Length; i++)
                {
                    desiredWealth[snapshot.Wealth[i].CurrencyHash] = snapshot.Wealth[i].Amount;
                }
            }

            var wealth = new List<NetworkWealthEntry>(m_Bag.Wealth.List.Count);
            foreach (IdString currencyId in m_Bag.Wealth.List)
            {
                wealth.Add(new NetworkWealthEntry
                {
                    CurrencyHash = currencyId.Hash,
                    Amount = desiredWealth.TryGetValue(currencyId.Hash, out int amount) ? amount : 0
                });
            }

            ApplyWealthDelta(wealth.ToArray());

            CacheCurrentSyncState();
        }

        private static NetworkCell CreateDeleteCell(Vector2Int position)
        {
            return new NetworkCell
            {
                Position = position,
                ItemHash = 0,
                StackCount = 0,
                RootItem = default,
                StackedRuntimeIds = Array.Empty<long>(),
                StackedRuntimeIdStrings = Array.Empty<string>(),
                StackedItems = Array.Empty<NetworkRuntimeItem>()
            };
        }

        private static bool IsDeleteCell(NetworkCell cell)
        {
            return cell.ItemHash == 0 || cell.StackCount <= 0 || cell.RootItem.ItemHash == 0;
        }

        private bool CellMatchesLocal(NetworkCell desired)
        {
            Cell local = m_Bag.Content.GetContent(desired.Position);
            if (IsDeleteCell(desired)) return local == null || local.Available;
            if (local == null || local.Available) return false;

            NetworkCell current = CreateNetworkCell(local, desired.Position);
            return NetworkCellsEquivalent(current, desired);
        }

        private static bool NetworkCellsEquivalent(NetworkCell current, NetworkCell desired)
        {
            if (current.Position != desired.Position ||
                current.ItemHash != desired.ItemHash ||
                current.StackCount != desired.StackCount ||
                !NetworkRuntimeItemsEquivalent(current.RootItem, desired.RootItem))
            {
                return false;
            }

            int desiredStackedCount = Mathf.Max(0, desired.StackCount - 1);
            for (int i = 0; i < desiredStackedCount; i++)
            {
                long currentId = GetStackedRuntimeId(current, i);
                long desiredId = GetStackedRuntimeId(desired, i);
                if (currentId != desiredId) return false;

                string desiredIdString = GetStackedRuntimeIdString(desired, i);
                if (!string.IsNullOrEmpty(desiredIdString) &&
                    !string.Equals(GetStackedRuntimeIdString(current, i), desiredIdString, StringComparison.Ordinal))
                {
                    return false;
                }

                if (desired.StackedItems != null && i < desired.StackedItems.Length &&
                    desired.StackedItems[i].ItemHash != 0)
                {
                    if (current.StackedItems == null || i >= current.StackedItems.Length ||
                        !NetworkRuntimeItemsEquivalent(current.StackedItems[i], desired.StackedItems[i]))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static bool NetworkRuntimeItemsEquivalent(NetworkRuntimeItem current, NetworkRuntimeItem desired)
        {
            if (current.ItemHash != desired.ItemHash || current.RuntimeIdHash != desired.RuntimeIdHash)
            {
                return false;
            }

            if (!string.IsNullOrEmpty(desired.ItemIdString) &&
                !string.Equals(current.ItemIdString, desired.ItemIdString, StringComparison.Ordinal))
            {
                return false;
            }

            if (!string.IsNullOrEmpty(desired.RuntimeIdString) &&
                !string.Equals(current.RuntimeIdString, desired.RuntimeIdString, StringComparison.Ordinal))
            {
                return false;
            }

            int desiredPropertyCount = desired.Properties?.Length ?? 0;
            if ((current.Properties?.Length ?? 0) != desiredPropertyCount) return false;
            for (int i = 0; i < desiredPropertyCount; i++)
            {
                NetworkRuntimeProperty desiredProperty = desired.Properties[i];
                if (!TryFindProperty(current.Properties, desiredProperty, out NetworkRuntimeProperty currentProperty) ||
                    !currentProperty.Number.Equals(desiredProperty.Number) ||
                    !string.Equals(currentProperty.Text, desiredProperty.Text, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            int desiredSocketCount = desired.Sockets?.Length ?? 0;
            if ((current.Sockets?.Length ?? 0) != desiredSocketCount) return false;
            for (int i = 0; i < desiredSocketCount; i++)
            {
                NetworkRuntimeSocket desiredSocket = desired.Sockets[i];
                if (!TryFindSocket(current.Sockets, desiredSocket, out NetworkRuntimeSocket currentSocket) ||
                    currentSocket.HasAttachment != desiredSocket.HasAttachment)
                {
                    return false;
                }

                if (desiredSocket.HasAttachment &&
                    !NetworkRuntimeItemsEquivalent(currentSocket.Attachment, desiredSocket.Attachment))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool TryFindProperty(
            NetworkRuntimeProperty[] properties,
            NetworkRuntimeProperty desired,
            out NetworkRuntimeProperty result)
        {
            if (properties != null)
            {
                for (int i = 0; i < properties.Length; i++)
                {
                    NetworkRuntimeProperty candidate = properties[i];
                    if (candidate.PropertyHash != desired.PropertyHash) continue;
                    if (!string.IsNullOrEmpty(desired.PropertyIdString) &&
                        !string.Equals(candidate.PropertyIdString, desired.PropertyIdString, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    result = candidate;
                    return true;
                }
            }

            result = default;
            return false;
        }

        private static bool TryFindSocket(
            NetworkRuntimeSocket[] sockets,
            NetworkRuntimeSocket desired,
            out NetworkRuntimeSocket result)
        {
            if (sockets != null)
            {
                for (int i = 0; i < sockets.Length; i++)
                {
                    NetworkRuntimeSocket candidate = sockets[i];
                    if (candidate.SocketHash != desired.SocketHash) continue;
                    if (!string.IsNullOrEmpty(desired.SocketIdString) &&
                        !string.Equals(candidate.SocketIdString, desired.SocketIdString, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    result = candidate;
                    return true;
                }
            }

            result = default;
            return false;
        }

        private bool TryApplyCellPayloadInPlace(NetworkCell desired)
        {
            Cell local = m_Bag.Content.GetContent(desired.Position);
            if (!CellIdentityMatchesNetwork(local, desired)) return false;

            if (!ApplyNetworkRuntimeItemState(local.RootRuntimeItem, desired.RootItem)) return false;

            for (int i = 0; i < desired.StackCount - 1; i++)
            {
                if (desired.StackedItems == null || i >= desired.StackedItems.Length ||
                    desired.StackedItems[i].ItemHash == 0)
                {
                    continue;
                }

                IdString runtimeId = local.List[i + 1];
                RuntimeItem runtimeItem = m_Bag.Content.GetRuntimeItem(runtimeId);
                if (!ApplyNetworkRuntimeItemState(runtimeItem, desired.StackedItems[i])) return false;
            }

            return true;
        }

        private bool ApplyNetworkRuntimeItemState(RuntimeItem runtimeItem, NetworkRuntimeItem desired)
        {
            if (runtimeItem == null || desired.ItemHash == 0) return false;
            if (runtimeItem.ItemID.Hash != desired.ItemHash || runtimeItem.RuntimeID.Hash != desired.RuntimeIdHash)
            {
                return false;
            }

            if (desired.Properties != null)
            {
                for (int i = 0; i < desired.Properties.Length; i++)
                {
                    NetworkRuntimeProperty property = desired.Properties[i];
                    if (!TryResolveRuntimePropertyId(runtimeItem, property.PropertyHash, property.PropertyIdString, out IdString propertyId) ||
                        !runtimeItem.Properties.TryGetValue(propertyId, out RuntimeProperty runtimeProperty))
                    {
                        continue;
                    }

                    if (!runtimeProperty.Number.Equals(property.Number)) runtimeProperty.Number = property.Number;
                    if (!string.Equals(runtimeProperty.Text, property.Text, StringComparison.Ordinal)) runtimeProperty.Text = property.Text;
                }
            }

            if (desired.Sockets == null || s_RuntimeSocketAttachmentField == null) return true;
            for (int i = 0; i < desired.Sockets.Length; i++)
            {
                NetworkRuntimeSocket socket = desired.Sockets[i];
                if (!TryResolveRuntimeSocketId(runtimeItem, socket.SocketHash, socket.SocketIdString, out IdString socketId) ||
                    !runtimeItem.Sockets.TryGetValue(socketId, out RuntimeSocket runtimeSocket))
                {
                    continue;
                }

                RuntimeItem currentAttachment = runtimeSocket.Attachment;
                if (!socket.HasAttachment)
                {
                    if (currentAttachment != null)
                    {
                        UntrackRuntimeItemRecursive(currentAttachment);
                        s_RuntimeSocketAttachmentField.SetValue(runtimeSocket, null);
                    }

                    continue;
                }

                if (currentAttachment != null &&
                    currentAttachment.RuntimeID.Hash == socket.Attachment.RuntimeIdHash &&
                    ApplyNetworkRuntimeItemState(currentAttachment, socket.Attachment))
                {
                    continue;
                }

                if (currentAttachment != null) UntrackRuntimeItemRecursive(currentAttachment);
                RuntimeItem replacement = ReconstructRuntimeItem(socket.Attachment);
                s_RuntimeSocketAttachmentField.SetValue(runtimeSocket, replacement);
                if (replacement != null) TrackRuntimeItemRecursive(replacement);
            }

            return true;
        }

        private static bool CellIdentityMatchesNetwork(Cell local, NetworkCell desired)
        {
            if (local == null || local.Available || local.Count != desired.StackCount) return false;
            if (local.RootRuntimeItemID.Hash != desired.RootItem.RuntimeIdHash) return false;
            if (local.Item.ID.Hash != desired.ItemHash) return false;

            for (int i = 0; i < desired.StackCount - 1; i++)
            {
                if (i + 1 >= local.List.Count || local.List[i + 1].Hash != GetStackedRuntimeId(desired, i))
                {
                    return false;
                }
            }

            return true;
        }

        private static long GetStackedRuntimeId(NetworkCell cell, int index)
        {
            if (cell.StackedItems != null && index >= 0 && index < cell.StackedItems.Length &&
                cell.StackedItems[index].RuntimeIdHash != 0)
            {
                return cell.StackedItems[index].RuntimeIdHash;
            }

            return cell.StackedRuntimeIds != null && index >= 0 && index < cell.StackedRuntimeIds.Length
                ? cell.StackedRuntimeIds[index]
                : 0L;
        }

        private static string GetStackedRuntimeIdString(NetworkCell cell, int index)
        {
            if (cell.StackedItems != null && index >= 0 && index < cell.StackedItems.Length &&
                !string.IsNullOrEmpty(cell.StackedItems[index].RuntimeIdString))
            {
                return cell.StackedItems[index].RuntimeIdString;
            }

            return cell.StackedRuntimeIdStrings != null && index >= 0 && index < cell.StackedRuntimeIdStrings.Length
                ? cell.StackedRuntimeIdStrings[index]
                : null;
        }

        private void AddNetworkCell(NetworkCell cell)
        {
            RuntimeItem rootItem = ReconstructRuntimeItem(cell.RootItem);
            if (rootItem == null || !m_Bag.Content.Add(rootItem, cell.Position, true)) return;
            TrackRuntimeItemRecursive(rootItem);

            int stackCount = Mathf.Max(1, cell.StackCount);
            for (int i = 1; i < stackCount; i++)
            {
                int stackedIndex = i - 1;
                NetworkRuntimeItem stackedPayload = cell.StackedItems != null && stackedIndex < cell.StackedItems.Length
                    ? cell.StackedItems[stackedIndex]
                    : default;

                RuntimeItem stackedItem = stackedPayload.ItemHash != 0
                    ? ReconstructRuntimeItem(stackedPayload)
                    : new RuntimeItem(rootItem, true);
                if (stackedItem == null) continue;

                if (stackedPayload.ItemHash == 0)
                {
                    TryApplyRuntimeId(
                        stackedItem,
                        GetStackedRuntimeIdString(cell, stackedIndex),
                        GetStackedRuntimeId(cell, stackedIndex));
                }

                if (m_Bag.Content.Add(stackedItem, cell.Position, true))
                {
                    TrackRuntimeItemRecursive(stackedItem);
                }
            }
        }

        private void RebuildRuntimeItemMap()
        {
            m_RuntimeItemMap.Clear();
            foreach (Cell cell in m_Bag.Content.CellList)
            {
                if (cell == null || cell.Available) continue;
                foreach (IdString runtimeId in cell.List)
                {
                    TrackRuntimeItemRecursive(m_Bag.Content.GetRuntimeItem(runtimeId));
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

        private bool ContainsRuntimeItemRecursive(long runtimeIdHash)
        {
            foreach (Cell cell in m_Bag.Content.CellList)
            {
                if (cell == null || cell.Available) continue;

                RuntimeItem rootItem = cell.RootRuntimeItem;
                if (ContainsRuntimeItemRecursive(rootItem, runtimeIdHash)) return true;

                foreach (IdString stackedId in cell.List)
                {
                    RuntimeItem stackedItem = m_Bag.Content.GetRuntimeItem(stackedId);
                    if (ContainsRuntimeItemRecursive(stackedItem, runtimeIdHash)) return true;
                }
            }

            return false;
        }

        private static bool ContainsRuntimeItemRecursive(RuntimeItem runtimeItem, long runtimeIdHash)
        {
            if (runtimeItem == null) return false;
            if (runtimeItem.RuntimeID.Hash == runtimeIdHash) return true;

            foreach (KeyValuePair<IdString, RuntimeSocket> socketEntry in runtimeItem.Sockets)
            {
                RuntimeSocket socket = socketEntry.Value;
                if (socket == null || !socket.HasAttachment) continue;
                if (ContainsRuntimeItemRecursive(socket.Attachment, runtimeIdHash)) return true;
            }

            return false;
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

        private void GetStackedRuntimeIdentity(
            Cell cell,
            out long[] runtimeIds,
            out string[] runtimeIdStrings,
            out NetworkRuntimeItem[] stackedItems)
        {
            var ids = new List<long>();
            var idStrings = new List<string>();
            var items = new List<NetworkRuntimeItem>();
            foreach (var id in cell.List)
            {
                if (id.Hash == cell.RootRuntimeItemID.Hash) continue;
                ids.Add(id.Hash);
                idStrings.Add(id.String);
                items.Add(ConvertToNetworkItem(m_Bag.Content.GetRuntimeItem(id)));
            }

            runtimeIds = ids.ToArray();
            runtimeIdStrings = idStrings.ToArray();
            stackedItems = items.ToArray();
        }
    }
}
#endif
