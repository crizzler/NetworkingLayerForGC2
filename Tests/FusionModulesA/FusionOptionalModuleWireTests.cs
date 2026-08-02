#if GC2_STATS && GC2_INVENTORY
using Arawn.GameCreator2.Networking.Inventory;
using Arawn.GameCreator2.Networking.Stats;
using Arawn.GameCreator2.Networking.Transport.Fusion;
using NUnit.Framework;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.FusionModulesA.Tests
{
    public sealed class FusionOptionalModuleWireTests
    {
        [Test]
        public void StatsSnapshot_RoundTripsAllCollections()
        {
            var source = new NetworkStatsSnapshot
            {
                NetworkId = 19,
                Timestamp = 4.5f,
                Stats = new[]
                {
                    new NetworkStatValue
                    {
                        StatHash = 1,
                        BaseValue = 2f,
                        ComputedValue = 3f
                    }
                },
                Attributes = new[]
                {
                    new NetworkAttributeValue
                    {
                        AttributeHash = 4,
                        CurrentValue = 5f,
                        MaxValue = 6f
                    }
                },
                StatusEffects = new[]
                {
                    new NetworkStatusEffectValue
                    {
                        StatusEffectHash = 7,
                        StackCount = 2,
                        RemainingDuration = 8f
                    }
                }
            };

            NetworkStatsSnapshot result =
                FusionWireSerializer.Deserialize<NetworkStatsSnapshot>(
                    FusionWireSerializer.Serialize(source));

            Assert.AreEqual(source.NetworkId, result.NetworkId);
            Assert.AreEqual(source.Timestamp, result.Timestamp);
            Assert.AreEqual(source.Stats[0].ComputedValue, result.Stats[0].ComputedValue);
            Assert.AreEqual(source.Attributes[0].MaxValue, result.Attributes[0].MaxValue);
            Assert.AreEqual(
                source.StatusEffects[0].RemainingDuration,
                result.StatusEffects[0].RemainingDuration);
        }

        [Test]
        public void InventorySnapshot_RoundTripsGridAndNestedItem()
        {
            var source = new NetworkInventorySnapshot
            {
                BagNetworkId = 21,
                StateVersion = 4,
                Timestamp = 10f,
                BagType = 1,
                BagSize = new Vector2Int(8, 6),
                MaxWeight = 100,
                Cells = new[]
                {
                    new NetworkCell
                    {
                        Position = new Vector2Int(3, 5),
                        ItemHash = 123,
                        StackCount = 2,
                        RootItem = new NetworkRuntimeItem
                        {
                            ItemHash = 123,
                            ItemIdString = "item/薬",
                            RuntimeIdHash = 456,
                            RuntimeIdString = "runtime-🙂",
                            Properties = System.Array.Empty<NetworkRuntimeProperty>(),
                            Sockets = System.Array.Empty<NetworkRuntimeSocket>()
                        },
                        StackedRuntimeIds = new long[] { 456, 789 },
                        StackedRuntimeIdStrings = new[] { "runtime-🙂", "runtime-2" },
                        StackedItems = System.Array.Empty<NetworkRuntimeItem>()
                    }
                },
                Equipment = System.Array.Empty<NetworkEquipmentSlot>(),
                Wealth = System.Array.Empty<NetworkWealthEntry>()
            };

            NetworkInventorySnapshot result =
                FusionWireSerializer.Deserialize<NetworkInventorySnapshot>(
                    FusionWireSerializer.Serialize(source));

            Assert.AreEqual(source.BagSize, result.BagSize);
            Assert.AreEqual(source.Cells[0].Position, result.Cells[0].Position);
            Assert.AreEqual(source.Cells[0].RootItem.ItemIdString,
                result.Cells[0].RootItem.ItemIdString);
            Assert.AreEqual(source.Cells[0].RootItem.RuntimeIdString,
                result.Cells[0].RootItem.RuntimeIdString);
            CollectionAssert.AreEqual(
                source.Cells[0].StackedRuntimeIds,
                result.Cells[0].StackedRuntimeIds);
        }
    }
}
#endif
