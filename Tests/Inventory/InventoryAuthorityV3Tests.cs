#if GC2_INVENTORY
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Arawn.EnemyMasses.Editor.Integration.GameCreator2.Patches;
using GameCreator.Runtime.Inventory;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Arawn.GameCreator2.Networking.Inventory.Tests
{
    public sealed class InventoryAuthorityV3Tests
    {
        private const BindingFlags InstancePrivate =
            BindingFlags.Instance | BindingFlags.NonPublic;

        private const string BagEquipmentPatchPath =
            "Plugins/GameCreator/Packages/Inventory/Runtime/Classes/Bag/Equipment/BagEquipment.cs";

        private const string CraftingPatchPath =
            "Plugins/GameCreator/Packages/Inventory/Runtime/Classes/Items/ScriptableObject/Craft/Crafting.cs";

        private const string InventoryManagerPath =
            "Arawn/NetworkingLayerForGC2/Inventory/NetworkInventoryManager.cs";

        private const string RegressionMenuPath =
            "Arawn/NetworkingLayerForGC2/Editor/Inventory/InventoryAuthorityRegressionDemoSceneMenu.cs";

        private const string RegressionHarnessPath =
            "Arawn/NetworkingLayerForGC2/Inventory/NetworkInventoryAuthorityRegressionHarness.cs";

        private readonly List<UnityEngine.Object> m_Cleanup = new();

        private static readonly FieldInfo s_RuntimeItemAssetField = typeof(RuntimeItem).GetField(
            "m_Item",
            InstancePrivate);

        private static readonly PropertyInfo s_SelectedRuntimeItemProperty = typeof(RuntimeItem)
            .GetProperty(
                nameof(RuntimeItem.UI_LastItemSelected),
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

        private sealed class InventoryPatcherProxy : InventoryPatcher
        {
            public string Marker => PatchMarker;
            public string[] Files => FilesToPatch;

            public bool Verify(string path, string source, out string reason)
            {
                return VerifyPatchedFile(path, source, out reason);
            }
        }

        private sealed class InventoryFixture
        {
            public GameObject GameObject;
            public Bag Bag;
            public NetworkInventoryController Controller;
            public Item Item;
            public RuntimeItem Root;
        }

        [TearDown]
        public void TearDown()
        {
            try
            {
                s_SelectedRuntimeItemProperty?.SetValue(null, null);
            }
            catch
            {
                // Selection cleanup is best effort on GC2 versions that expose a readonly property.
            }

            for (int i = m_Cleanup.Count - 1; i >= 0; i--)
            {
                if (m_Cleanup[i] != null) UnityEngine.Object.DestroyImmediate(m_Cleanup[i]);
            }

            m_Cleanup.Clear();
        }

        [Test]
        public void InventoryPatch_UsesRevision300AndCompleteCapabilitySet()
        {
            const NetworkInventoryPatchCapability expected =
                NetworkInventoryPatchCapability.ContentMove |
                NetworkInventoryPatchCapability.AddType |
                NetworkInventoryPatchCapability.RemoveType |
                NetworkInventoryPatchCapability.AsyncInstructionAdd |
                NetworkInventoryPatchCapability.CellDrop |
                NetworkInventoryPatchCapability.Split |
                NetworkInventoryPatchCapability.Transfer |
                NetworkInventoryPatchCapability.UseDrop |
                NetworkInventoryPatchCapability.Wealth |
                NetworkInventoryPatchCapability.Crafting |
                NetworkInventoryPatchCapability.Merchant |
                NetworkInventoryPatchCapability.Sockets;

            Assert.That(TBagContent.NetworkPatchRevision, Is.EqualTo(300));
            Assert.That(TBagContent.NetworkPatchCapabilities, Is.EqualTo(expected));

            var patcher = new InventoryPatcherProxy();
            Assert.That(patcher.PatchVersion, Is.EqualTo("3.0.0-inventory"));
            Assert.That(patcher.Marker, Is.EqualTo("// [GC2_NETWORK_PATCH_Inventory_v3_0_0_inventory]"));
        }

        [Test]
        public void InventoryPatch_PublicAbiMatchesRuntimeHookSignatures()
        {
            AssertStaticField<TBagContent>(
                "NetworkAddTypeInterceptor",
                typeof(Func<TBagContent, Item, Vector2Int, bool, NetworkInventoryInterceptResult>));
            AssertStaticField<TBagContent>(
                "NetworkRemoveTypeInterceptor",
                typeof(Func<TBagContent, Item, NetworkInventoryInterceptResult>));
            AssertStaticField<TBagContent>(
                "NetworkMoveInterceptor",
                typeof(Func<TBagContent, Vector2Int, Vector2Int, bool,
                    NetworkInventoryInterceptResult>));
            AssertStaticField<TBagContent>(
                "NetworkInstructionAddItemInterceptor",
                typeof(Func<Bag, Item, GameObject, Task<NetworkInventoryInterceptResult>>));
            AssertStaticField<TBagContent>(
                "NetworkCellDropInterceptor",
                typeof(Func<TBagContent, Vector2Int, Vector2Int, bool,
                    NetworkInventoryInterceptResult>));
            AssertStaticField<TBagContent>(
                "NetworkSplitInterceptor",
                typeof(Func<TBagContent, Vector2Int, int, NetworkInventoryInterceptResult>));
            AssertStaticField<TBagContent>(
                "NetworkTransferInterceptor",
                typeof(Func<TBagContent, TBagContent, RuntimeItem, int,
                    NetworkInventoryInterceptResult>));

            AssertStaticField<Crafting>(
                "NetworkCraftInterceptor",
                typeof(Func<Item, Bag, Bag, NetworkInventoryCraftInterceptResult>));
            AssertStaticField<Crafting>(
                "NetworkDismantleItemInterceptor",
                typeof(Func<Item, Bag, Bag, float, NetworkInventoryCraftInterceptResult>));
            AssertStaticField<Crafting>(
                "NetworkDismantleRuntimeInterceptor",
                typeof(Func<RuntimeItem, Bag, Bag, float, NetworkInventoryCraftInterceptResult>));
            AssertStaticField<Crafting>(
                "NetworkCraftAsyncInterceptor",
                typeof(Func<Item, Bag, Bag, Task<NetworkInventoryCraftInterceptResult>>));
            AssertStaticField<Crafting>(
                "NetworkDismantleItemAsyncInterceptor",
                typeof(Func<Item, Bag, Bag, float, Task<NetworkInventoryCraftInterceptResult>>));
            AssertStaticField<Crafting>(
                "NetworkDismantleRuntimeAsyncInterceptor",
                typeof(Func<RuntimeItem, Bag, Bag, float,
                    Task<NetworkInventoryCraftInterceptResult>>));
            AssertStaticField<BagEquipment>(
                "NetworkAttachInterceptor",
                typeof(Func<Bag, RuntimeItem, RuntimeItem, GameCreator.Runtime.Common.IdString,
                    NetworkInventoryInterceptResult>));
            AssertStaticField<BagEquipment>(
                "NetworkDetachInterceptor",
                typeof(Func<Bag, RuntimeItem, GameCreator.Runtime.Common.IdString,
                    NetworkInventoryInterceptResult>));
            AssertStaticField<Merchant>(
                "NetworkBuyFromClientInterceptor",
                typeof(Func<Merchant, Bag, RuntimeItem, NetworkInventoryInterceptResult>));
            AssertStaticField<Merchant>(
                "NetworkSellToClientInterceptor",
                typeof(Func<Merchant, Bag, RuntimeItem, NetworkInventoryInterceptResult>));
            AssertStaticField<Merchant>(
                "NetworkBuyFromClientAsyncInterceptor",
                typeof(Func<Merchant, Bag, RuntimeItem, Task<NetworkInventoryInterceptResult>>));
            AssertStaticField<Merchant>(
                "NetworkSellToClientAsyncInterceptor",
                typeof(Func<Merchant, Bag, RuntimeItem, Task<NetworkInventoryInterceptResult>>));
        }

        [Test]
        public void SocketPatch_InterceptsBeforeNativeMutationAndPreservesReturnContract()
        {
            string source = ReadAssetSource(BagEquipmentPatchPath);

            int attachHook = source.IndexOf(
                "NetworkAttachInterceptor.Invoke", StringComparison.Ordinal);
            int attachMutation = source.IndexOf(
                "IBagEquipment equipment = this.Bag.Equipment", attachHook,
                StringComparison.Ordinal);
            Assert.That(attachHook, Is.GreaterThanOrEqualTo(0));
            Assert.That(attachMutation, Is.GreaterThan(attachHook),
                "Attach must be intercepted before sockets, equipment, or Bag content mutate.");
            StringAssert.Contains(
                "return networkResult == NetworkInventoryInterceptResult.HandledSuccess;",
                source,
                "AttachTo must preserve its synchronous bool contract for handled requests.");

            int detachHook = source.IndexOf(
                "NetworkDetachInterceptor.Invoke", StringComparison.Ordinal);
            int detachMutation = source.IndexOf(
                "IBagEquipment equipment = this.Bag.Equipment", detachHook,
                StringComparison.Ordinal);
            Assert.That(detachHook, Is.GreaterThanOrEqualTo(0));
            Assert.That(detachMutation, Is.GreaterThan(detachHook),
                "Detach must be intercepted before the socket or equipment mutates.");
            StringAssert.Contains(
                "if (networkResult != NetworkInventoryInterceptResult.Unhandled) return null;",
                source,
                "A client-side semantic detach cannot return a speculative RuntimeItem.");
        }

        [Test]
        public void CraftingPatch_PreservesSynchronousCraftAndDismantleResultContract()
        {
            Item item = Track(ScriptableObject.CreateInstance<Item>());
            RuntimeItem crafted = CreateRuntimeItem(item);
            RuntimeItem dismantled = CreateRuntimeItem(item);
            var result = new NetworkInventoryCraftInterceptResult(
                NetworkInventoryInterceptResult.HandledSuccess,
                crafted,
                new[] { dismantled });

            Assert.That(result.Status, Is.EqualTo(NetworkInventoryInterceptResult.HandledSuccess));
            Assert.That(result.CraftedItem, Is.SameAs(crafted));
            Assert.That(result.DismantledItems, Is.EqualTo(new[] { dismantled }));

            string source = ReadAssetSource(CraftingPatchPath);
            int craftHook = source.IndexOf("NetworkCraftInterceptor.Invoke", StringComparison.Ordinal);
            int ingredientMutation = source.IndexOf(
                "inputBag.Content.RemoveType", craftHook, StringComparison.Ordinal);
            Assert.That(craftHook, Is.GreaterThanOrEqualTo(0));
            Assert.That(ingredientMutation, Is.GreaterThan(craftHook),
                "Crafting must route before consuming ingredients.");
            Assert.That(Regex.Matches(source, @"\? result\.CraftedItem").Count,
                Is.EqualTo(2),
                "Synchronous and asynchronous Craft paths must return the semantic result.");
            Assert.That(Regex.Matches(source, @"\? result\.DismantledItems").Count,
                Is.EqualTo(4),
                "Sync/async Item and RuntimeItem dismantle paths must return the semantic result.");
            StringAssert.Contains(
                "    [Serializable]\n    public class Crafting",
                source.Replace("\r\n", "\n"),
                "The patch must preserve GC2 Crafting's original Serializable contract.");
        }

        [Test]
        public void GenericClientWealthMutation_DefaultsToExplicitDeny()
        {
            GameObject managerObject = Track(new GameObject("Inventory v3 secure wealth manager"));
            managerObject.SetActive(false);
            NetworkInventoryManager manager = managerObject.AddComponent<NetworkInventoryManager>();

            Assert.That(manager.CustomWealthValidator, Is.Null,
                "A project must explicitly authorize generic client-originated wealth changes.");

            string source = ReadAssetSource(InventoryManagerPath);
            int defaultDeny = source.IndexOf("if (CustomWealthValidator == null)",
                StringComparison.Ordinal);
            int rejection = source.IndexOf(
                "RejectionReason = InventoryRejectionReason.NotAuthorized", defaultDeny,
                StringComparison.Ordinal);
            Assert.That(defaultDeny, Is.GreaterThanOrEqualTo(0));
            Assert.That(rejection, Is.GreaterThan(defaultDeny),
                "A missing wealth validator must reject instead of applying a client grant.");
        }

        [Test]
        public void InventoryPatcher_CurrentOptionalPackageFilesPassV3Verification()
        {
            var patcher = new InventoryPatcherProxy();
            Assert.That(patcher.Files, Has.Length.EqualTo(12));

            foreach (string relativePath in patcher.Files)
            {
                string fullPath = Path.Combine(Application.dataPath, relativePath);
                Assert.That(File.Exists(fullPath), Is.True, $"Missing patch target: {relativePath}");

                string source = File.ReadAllText(fullPath);
                StringAssert.Contains(patcher.Marker, source, relativePath);
                Assert.That(
                    patcher.Verify(relativePath, source, out string reason),
                    Is.True,
                    $"{relativePath}: {reason}");
            }
        }

        [Test]
        public void InventoryPatcher_GeneratedBagCellUIHasBalancedSectionsAndPassesVerification()
        {
            const string relativePath =
                "Plugins/GameCreator/Packages/Inventory/Runtime/UI/UnityUI/Components/BagCellUI.cs";
            const string pristineSource = @"namespace GameCreator.Runtime.Inventory.UnityUI
{
    public class BagCellUI
    {
        private bool Drop(BagCellUI dropCellUI)
        {
            IBagContent content = this.m_CellInfo.Bag.Content;

            return this.m_OnDrop switch
            {
                _ => false
            };
        }

        private void SendToBag(Bag bag)
        {
            int times = TBagUI.TransferAmount switch
            {
                TBagUI.EnumTransferAmount.One => 1,
                TBagUI.EnumTransferAmount.Stack => this.Cell.Count,
                _ => throw new ArgumentOutOfRangeException()
            };

            for (int i = 0; i < times; ++i) { }
        }

        private void Split()
        {
            int splitAmount = TBagUI.SplitAmount switch
            {
                TBagUI.EnumSplitAmount.One => 1,
                TBagUI.EnumSplitAmount.Half => this.Cell.Count / 2,
                _ => throw new ArgumentOutOfRangeException()
            };

            RuntimeItem runtimeItem = this.BagUI.Bag.Content.Remove(this.Position);
        }
    }
}";

            var patcher = new InventoryPatcherProxy();
            MethodInfo patchMethod = typeof(InventoryPatcher).GetMethod(
                "PatchBagCellUI",
                InstancePrivate);
            Assert.That(patchMethod, Is.Not.Null);

            object[] arguments = { pristineSource };
            Assert.That((bool)patchMethod.Invoke(patcher, arguments), Is.True);

            string generated = patcher.Marker + "\n" + (string)arguments[0];
            int sectionStarts = Regex.Matches(
                generated,
                @"// \[GC2_NETWORK_PATCH\](?!_END)").Count;
            int sectionEnds = Regex.Matches(
                generated,
                @"// \[GC2_NETWORK_PATCH_END\]").Count;

            Assert.That(sectionStarts, Is.EqualTo(3));
            Assert.That(sectionEnds, Is.EqualTo(sectionStarts));
            Assert.That(
                patcher.Verify(relativePath, generated, out string reason),
                Is.True,
                reason);
        }

        [Test]
        public void RegressionSceneGenerator_ExercisesPatchedInstructionAndStockPickupConversion()
        {
            string menu = ReadAssetSource(RegressionMenuPath);
            string harness = ReadAssetSource(RegressionHarnessPath);

            StringAssert.Contains("InstructionInventoryAddItem", menu);
            StringAssert.Contains("ConvertStockScenePickups(false)", menu);
            StringAssert.Contains("EnsureGeneratedSceneInBuildSettings", menu);
            StringAssert.Contains("m_ValidatedAddTrigger.Execute", harness);
            StringAssert.Contains("m_StaticPickupTrigger.Execute", harness);
            StringAssert.Contains("NetworkInventoryRegressionContinuationInstruction", harness);
            StringAssert.Contains("TBagContent.NetworkSplitInterceptor", harness);
            StringAssert.Contains("nativeContent.Move", harness);
            StringAssert.Contains("FullSnapshotApplyCount", harness);
            StringAssert.Contains("GetItemInstance", ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Editor/InventorySceneSetupTools.cs"));
            StringAssert.Contains(
                "updated.Add(new EditorBuildSettingsScene(GeneratedScenePath, true))",
                menu,
                "The generated diagnostic must be build index zero for one-click standalone tests.");
            StringAssert.DoesNotContain("Input.GetKeyDown", harness,
                "The regression panel must work when the project uses the new Input System only.");
        }

        [Test]
        public void StockPickupResolver_UnwrapsFixedGetItemInstance()
        {
            Item item = Track(ScriptableObject.CreateInstance<Item>());
            var instruction = new InstructionInventoryAddItem();
            SetPrivate(instruction, "m_Item", GetItemInstance.Create(item));

            Type setupTools = typeof(global::Arawn.GameCreator2.Networking.Editor.InventorySceneSetupTools);
            MethodInfo resolver = setupTools.GetMethod(
                "ResolveInstructionItem",
                BindingFlags.Static | BindingFlags.NonPublic);

            Assert.That(resolver, Is.Not.Null);
            Assert.That(resolver.Invoke(null, new object[] { instruction }), Is.SameAs(item),
                "Stock pickup conversion must resolve the fixed Item nested in PropertyGetItem.");
        }

        [Test]
        public void InventoryV3_PersistentPacketsExposeStateVersion()
        {
            Type[] versionedTypes =
            {
                typeof(NetworkContentAddResponse),
                typeof(NetworkContentRemoveResponse),
                typeof(NetworkContentMoveResponse),
                typeof(NetworkContentUseResponse),
                typeof(NetworkContentDropResponse),
                typeof(NetworkEquipmentResponse),
                typeof(NetworkSocketResponse),
                typeof(NetworkWealthResponse),
                typeof(NetworkMerchantResponse),
                typeof(NetworkCraftingResponse),
                typeof(NetworkTransferResponse),
                typeof(NetworkPickupResponse),
                typeof(NetworkCombineResponse),
                typeof(NetworkContentSplitResponse),
                typeof(NetworkItemAddedBroadcast),
                typeof(NetworkItemRemovedBroadcast),
                typeof(NetworkItemMovedBroadcast),
                typeof(NetworkItemUsedBroadcast),
                typeof(NetworkItemEquippedBroadcast),
                typeof(NetworkItemUnequippedBroadcast),
                typeof(NetworkSocketChangeBroadcast),
                typeof(NetworkWealthChangeBroadcast),
                typeof(NetworkPropertyChangeBroadcast),
                typeof(NetworkItemSplitBroadcast),
                typeof(NetworkInventorySnapshot),
                typeof(NetworkInventoryDelta),
                typeof(NetworkPickupState)
            };

            foreach (Type type in versionedTypes)
            {
                FieldInfo field = type.GetField("StateVersion", BindingFlags.Instance | BindingFlags.Public);
                Assert.That(field, Is.Not.Null, $"{type.Name} must carry StateVersion");
                Assert.That(field.FieldType, Is.EqualTo(typeof(uint)), type.Name);
                Assert.That(field.GetValue(Activator.CreateInstance(type)), Is.EqualTo(0u), type.Name);
            }
        }

        [Test]
        public void FullSnapshot_IncludesCompletePayloadForEveryStackMember()
        {
            InventoryFixture fixture = CreateFixture(addRootItem: true, maxStack: 8);
            RuntimeItem stacked = CreateRuntimeItem(fixture.Item);

            Vector2Int stackedPosition = fixture.Bag.Content.Add(stacked, true);
            Assert.That(stackedPosition, Is.Not.EqualTo(TBagContent.INVALID));

            NetworkInventorySnapshot snapshot = fixture.Controller.GetFullSnapshot();

            Assert.That(snapshot.StateVersion, Is.GreaterThan(0u));
            Assert.That(snapshot.Cells, Has.Length.EqualTo(1));
            NetworkCell cell = snapshot.Cells[0];
            Assert.That(cell.StackCount, Is.EqualTo(2));
            Assert.That(cell.StackedItems, Is.Not.Null.And.Length.EqualTo(1));
            Assert.That(cell.StackedRuntimeIds, Is.Not.Null.And.Length.EqualTo(1));
            Assert.That(cell.StackedRuntimeIdStrings, Is.Not.Null.And.Length.EqualTo(1));

            NetworkRuntimeItem payload = cell.StackedItems[0];
            Assert.That(payload.ItemHash, Is.EqualTo(stacked.ItemID.Hash));
            Assert.That(payload.ItemIdString, Is.EqualTo(stacked.ItemID.String));
            Assert.That(payload.RuntimeIdHash, Is.EqualTo(stacked.RuntimeID.Hash));
            Assert.That(payload.RuntimeIdString, Is.EqualTo(stacked.RuntimeID.String));
            Assert.That(payload.Properties, Is.Not.Null);
            Assert.That(payload.Sockets, Is.Not.Null);
            Assert.That(cell.StackedRuntimeIds[0], Is.EqualTo(payload.RuntimeIdHash));
            Assert.That(cell.StackedRuntimeIdStrings[0], Is.EqualTo(payload.RuntimeIdString));
        }

        [Test]
        public void ReapplyingIdenticalSnapshot_IsNoOpAndPreservesRuntimeItemReference()
        {
            InventoryFixture fixture = CreateFixture(addRootItem: true);
            NetworkInventorySnapshot snapshot = fixture.Controller.GetFullSnapshot();
            fixture.Controller.Initialize(isServer: false, isLocalClient: true);

            int addEvents = 0;
            int removeEvents = 0;
            fixture.Bag.Content.EventAdd += _ => addEvents++;
            fixture.Bag.Content.EventRemove += _ => removeEvents++;
            s_SelectedRuntimeItemProperty?.SetValue(null, fixture.Root);

            fixture.Controller.ReceiveFullSnapshot(snapshot);
            fixture.Controller.ReceiveFullSnapshot(snapshot);

            RuntimeItem current = fixture.Bag.Content.GetRuntimeItem(fixture.Root.RuntimeID);
            Assert.That(current, Is.SameAs(fixture.Root));
            Assert.That(RuntimeItem.UI_LastItemSelected, Is.SameAs(fixture.Root));
            Assert.That(addEvents, Is.Zero);
            Assert.That(removeEvents, Is.Zero);
            Assert.That(GetPrivate<uint>(fixture.Controller, "m_LastAppliedStateVersion"),
                Is.EqualTo(snapshot.StateVersion));
            Assert.That(GetPrivate<uint>(fixture.Controller, "m_FullSnapshotApplyCount"),
                Is.EqualTo(2u),
                "The diagnostic acknowledgement must prove both recovery snapshots were applied.");
        }

        [Test]
        public void OlderSnapshot_IsIgnoredAfterNewerRevisionWasApplied()
        {
            InventoryFixture fixture = CreateFixture(addRootItem: true);
            NetworkInventorySnapshot current = fixture.Controller.GetFullSnapshot();
            current.StateVersion = 20u;
            fixture.Controller.Initialize(isServer: false, isLocalClient: true);
            fixture.Controller.ReceiveFullSnapshot(current);

            Item secondItem = Track(ScriptableObject.CreateInstance<Item>());
            secondItem.name = "Inventory v3 stale-state sentinel";
            RuntimeItem sentinel = CreateRuntimeItem(secondItem);
            Assert.That(fixture.Bag.Content.Add(sentinel, true), Is.Not.EqualTo(TBagContent.INVALID));

            NetworkInventorySnapshot stale = current;
            stale.StateVersion = 19u;
            fixture.Controller.ReceiveFullSnapshot(stale);

            Assert.That(fixture.Bag.Content.GetRuntimeItem(sentinel.RuntimeID), Is.SameAs(sentinel));
            Assert.That(GetPrivate<uint>(fixture.Controller, "m_LastAppliedStateVersion"), Is.EqualTo(20u));
        }

        [Test]
        public void MutationRevisionHelper_RecordsOnlyAfterSuccessfulConvergence()
        {
            InventoryFixture fixture = CreateFixture(addRootItem: false);
            fixture.Controller.Initialize(isServer: false, isLocalClient: true);
            MethodInfo accept = typeof(NetworkInventoryController).GetMethod(
                "TryAcceptMutationVersion",
                InstancePrivate);
            MethodInfo finish = typeof(NetworkInventoryController).GetMethod(
                "FinishMutationVersion",
                InstancePrivate);
            Assert.That(accept, Is.Not.Null);
            Assert.That(finish, Is.Not.Null);

            Assert.That((bool)accept.Invoke(fixture.Controller, new object[] { 7u }), Is.True);
            Assert.That(GetPrivate<bool>(fixture.Controller, "m_HasAppliedStateVersion"), Is.False,
                "Validation alone must not publish an unapplied revision.");
            Assert.That((bool)finish.Invoke(
                fixture.Controller, new object[] { 7u, true, "test mutation" }), Is.True);

            Assert.That((bool)accept.Invoke(fixture.Controller, new object[] { 7u }), Is.False);
            Assert.That((bool)accept.Invoke(fixture.Controller, new object[] { 6u }), Is.False);
            Assert.That((bool)accept.Invoke(fixture.Controller, new object[] { 8u }), Is.True);
            Assert.That((bool)finish.Invoke(
                fixture.Controller, new object[] { 8u, false, "failed mutation" }), Is.False);
            Assert.That(GetPrivate<uint>(fixture.Controller, "m_LastAppliedStateVersion"), Is.EqualTo(7u),
                "A failed local application must not advance the authoritative revision.");

            Assert.That((bool)accept.Invoke(fixture.Controller, new object[] { 8u }), Is.True);
            Assert.That((bool)finish.Invoke(
                fixture.Controller, new object[] { 8u, true, "retried mutation" }), Is.True);
            Assert.That((bool)accept.Invoke(fixture.Controller, new object[] { 10u }), Is.False);
            Assert.That(GetPrivate<uint>(fixture.Controller, "m_LastAppliedStateVersion"), Is.EqualTo(8u));
        }

        [Test]
        public void CrossBagTransfer_ResponseCarriesActorBagRevision()
        {
            InventoryFixture source = CreateFixture(addRootItem: true);
            InventoryFixture destination = CreateFixture(addRootItem: false);
            SetPrivate(source.Controller, "m_StaticNetworkIdOverride", 5101u);
            SetPrivate(destination.Controller, "m_StaticNetworkIdOverride", 5102u);

            // Advance only the destination so its post-transfer revision cannot accidentally equal
            // the source revision. This catches responses that always report the source Bag.
            RuntimeItem existingDestinationItem = CreateRuntimeItem(destination.Item);
            Assert.That(destination.Bag.Content.Add(existingDestinationItem, true),
                Is.Not.EqualTo(TBagContent.INVALID));
            _ = destination.Controller.GetFullSnapshot();

            var request = new NetworkTransferRequest
            {
                RequestId = 17,
                ActorNetworkId = destination.Controller.NetworkId,
                CorrelationId = NetworkCorrelation.Compose(destination.Controller.NetworkId, 17),
                SourceBagNetworkId = source.Controller.NetworkId,
                DestinationBagNetworkId = destination.Controller.NetworkId,
                RuntimeIdHash = source.Root.RuntimeID.Hash,
                DestinationPosition = TBagContent.INVALID,
                AllowStack = true,
                Amount = 1,
                Source = InventoryModificationSource.Loot
            };

            NetworkTransferResponse response = source.Controller.ProcessTransferRequest(
                request, destination.Controller, destination.Controller.NetworkId);

            Assert.That(response.Authorized, Is.True);
            uint destinationVersion = destination.Controller.GetFullSnapshot().StateVersion;
            uint sourceVersion = source.Controller.GetFullSnapshot().StateVersion;
            Assert.That(response.StateVersion, Is.EqualTo(destinationVersion));
            Assert.That(response.StateVersion, Is.Not.EqualTo(sourceVersion),
                "The actor/destination must not wait for a source Bag revision.");
        }

        [UnityTest]
        public IEnumerator StateApplicationQueue_DoesNotRunLaterRevisionWhileEarlierApplyAwaits()
        {
            InventoryFixture fixture = CreateFixture(addRootItem: false);
            MethodInfo enqueue = typeof(NetworkInventoryController).GetMethod(
                "EnqueueStateApplication", InstancePrivate);
            Assert.That(enqueue, Is.Not.Null);

            var order = new List<int>();
            var firstGate = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            Func<Task> first = async () =>
            {
                order.Add(1);
                await firstGate.Task;
                order.Add(2);
            };
            Func<Task> second = () =>
            {
                order.Add(3);
                return Task.CompletedTask;
            };

            enqueue.Invoke(fixture.Controller, new object[] { first });
            enqueue.Invoke(fixture.Controller, new object[] { second });
            CollectionAssert.AreEqual(new[] { 1 }, order,
                "The later operation must remain queued while the earlier apply is incomplete.");

            firstGate.TrySetResult(true);
            for (int frame = 0; frame < 30 && order.Count < 3; frame++) yield return null;

            CollectionAssert.AreEqual(new[] { 1, 2, 3 }, order);
        }

        [Test]
        public void PickupState_IsIdempotentAndRejectsStaleOrMismatchedIdentity()
        {
            GameObject sourceObject = Track(new GameObject("Inventory v3 pickup state"));
            BoxCollider collider = sourceObject.AddComponent<BoxCollider>();
            NetworkInventoryPickupSource source = sourceObject.AddComponent<NetworkInventoryPickupSource>();
            SetPrivate(source, "m_PickupId", 4107u);
            InvokeLifecycle(source, "Awake");

            MethodInfo applyState = typeof(NetworkInventoryPickupSource).GetMethod(
                "ApplyState",
                InstancePrivate);
            Assert.That(applyState, Is.Not.Null);

            var consumed = new NetworkPickupState
            {
                PickupId = 4107u,
                Consumed = true,
                ConsumedByActorNetworkId = 77u,
                StateVersion = 5u
            };
            applyState.Invoke(source, new object[] { consumed });
            applyState.Invoke(source, new object[] { consumed });

            Assert.That(source.IsConsumed, Is.True);
            Assert.That(source.StateVersion, Is.EqualTo(5u));
            Assert.That(collider.enabled, Is.False);

            var stale = consumed;
            stale.Consumed = false;
            stale.StateVersion = 4u;
            applyState.Invoke(source, new object[] { stale });

            var wrongIdentity = consumed;
            wrongIdentity.PickupId = 9999u;
            wrongIdentity.Consumed = false;
            wrongIdentity.StateVersion = 6u;
            applyState.Invoke(source, new object[] { wrongIdentity });

            Assert.That(source.IsConsumed, Is.True);
            Assert.That(source.StateVersion, Is.EqualTo(5u));
            Assert.That(collider.enabled, Is.False);
        }

        [Test]
        public void PickupReservation_AllowsOneCommitAndRejectsEveryLaterClaim()
        {
            InventoryFixture picker = CreateFixture(addRootItem: false);
            GameObject sourceObject = Track(new GameObject("Inventory v3 pickup reservation"));
            sourceObject.transform.position = picker.GameObject.transform.position;
            NetworkInventoryPickupSource source = sourceObject.AddComponent<NetworkInventoryPickupSource>();
            SetPrivate(source, "m_PickupId", 8119u);
            SetPrivate(source, "m_Item", picker.Item);

            MethodInfo reserve = typeof(NetworkInventoryPickupSource).GetMethod(
                "TryReserve",
                InstancePrivate);
            MethodInfo commit = typeof(NetworkInventoryPickupSource).GetMethod(
                "Commit",
                InstancePrivate);
            Assert.That(reserve, Is.Not.Null);
            Assert.That(commit, Is.Not.Null);

            Assert.That(TryReserve(reserve, source, picker.Controller, 91u, out var firstReason), Is.True);
            Assert.That(firstReason, Is.EqualTo(InventoryRejectionReason.None));
            Assert.That(TryReserve(reserve, source, picker.Controller, 92u, out var reservedReason), Is.False);
            Assert.That(reservedReason, Is.EqualTo(InventoryRejectionReason.InvalidOperation));

            NetworkPickupState committed = (NetworkPickupState)commit.Invoke(source, new object[] { 91u });
            Assert.That(committed.Consumed, Is.True);
            Assert.That(committed.ConsumedByActorNetworkId, Is.EqualTo(91u));
            Assert.That(committed.StateVersion, Is.EqualTo(1u));
            Assert.That(source.IsConsumed, Is.True);

            Assert.That(TryReserve(reserve, source, picker.Controller, 93u, out var consumedReason), Is.False);
            Assert.That(consumedReason, Is.EqualTo(InventoryRejectionReason.InvalidOperation));
        }

        [Test]
        public void PickupRegistry_RejectsDuplicateStableIdsWithoutReplacingFirstSource()
        {
            GameObject managerObject = Track(new GameObject("Inventory v3 manager"));
            managerObject.SetActive(false);
            NetworkInventoryManager manager = managerObject.AddComponent<NetworkInventoryManager>();

            NetworkInventoryPickupSource first = CreateInactivePickup("Pickup first", 12345u);
            NetworkInventoryPickupSource duplicate = CreateInactivePickup("Pickup duplicate", 12345u);

            manager.RegisterPickupSource(first);
            LogAssert.Expect(
                LogType.Warning,
                new Regex("Duplicate pickup id 12345.*duplicate source is rejected", RegexOptions.IgnoreCase));
            manager.RegisterPickupSource(duplicate);

            IDictionary sources = GetPrivate<IDictionary>(manager, "m_PickupSources");
            Assert.That(sources.Count, Is.EqualTo(1));
            Assert.That(sources[12345u], Is.SameAs(first));
        }

        private InventoryFixture CreateFixture(bool addRootItem, int maxStack = 1)
        {
            GameObject gameObject = Track(new GameObject("Inventory v3 test bag"));
            Bag bag = gameObject.AddComponent<Bag>();
            EnsureBagAwake(bag);

            NetworkInventoryController controller = gameObject.AddComponent<NetworkInventoryController>();
            EnsureControllerAwake(controller);

            Item item = Track(ScriptableObject.CreateInstance<Item>());
            item.name = "Inventory v3 test item";
            SetPrivate(item.Shape, "m_MaxStack", Mathf.Max(1, maxStack));

            RuntimeItem root = null;
            if (addRootItem)
            {
                root = CreateRuntimeItem(item);
                Assert.That(bag.Content.Add(root, true), Is.Not.EqualTo(TBagContent.INVALID));
            }

            controller.Initialize(isServer: true, isLocalClient: false);
            return new InventoryFixture
            {
                GameObject = gameObject,
                Bag = bag,
                Controller = controller,
                Item = item,
                Root = root
            };
        }

        private NetworkInventoryPickupSource CreateInactivePickup(string name, uint pickupId)
        {
            GameObject gameObject = Track(new GameObject(name));
            gameObject.SetActive(false);
            NetworkInventoryPickupSource source = gameObject.AddComponent<NetworkInventoryPickupSource>();
            SetPrivate(source, "m_PickupId", pickupId);
            return source;
        }

        private static RuntimeItem CreateRuntimeItem(Item item)
        {
            var runtimeItem = new RuntimeItem(item);
            Assert.That(s_RuntimeItemAssetField, Is.Not.Null);
            s_RuntimeItemAssetField.SetValue(runtimeItem, item);
            return runtimeItem;
        }

        private static void EnsureBagAwake(Bag bag)
        {
            if (bag.Args != null) return;
            InvokeLifecycle(bag, "Awake");
        }

        private static void EnsureControllerAwake(NetworkInventoryController controller)
        {
            if (controller.Bag != null) return;
            InvokeLifecycle(controller, "Awake");
        }

        private static void InvokeLifecycle(object target, string methodName)
        {
            MethodInfo method = target.GetType().GetMethod(methodName, InstancePrivate);
            Assert.That(method, Is.Not.Null, $"Missing {target.GetType().Name}.{methodName}");
            method.Invoke(target, null);
        }

        private static bool TryReserve(
            MethodInfo method,
            NetworkInventoryPickupSource source,
            NetworkInventoryController picker,
            uint actorNetworkId,
            out InventoryRejectionReason reason)
        {
            object[] arguments = { picker, actorNetworkId, InventoryRejectionReason.None };
            bool result = (bool)method.Invoke(source, arguments);
            reason = (InventoryRejectionReason)arguments[2];
            return result;
        }

        private static void AssertStaticField<T>(string name, Type expectedType)
        {
            FieldInfo field = typeof(T).GetField(name, BindingFlags.Static | BindingFlags.Public);
            Assert.That(field, Is.Not.Null, $"Missing {typeof(T).Name}.{name}");
            Assert.That(field.FieldType, Is.EqualTo(expectedType), $"Invalid ABI for {typeof(T).Name}.{name}");
        }

        private static string ReadAssetSource(string relativePath)
        {
            string fullPath = Path.Combine(Application.dataPath, relativePath);
            Assert.That(File.Exists(fullPath), Is.True, $"Missing source under test: {relativePath}");
            return File.ReadAllText(fullPath);
        }

        private static T GetPrivate<T>(object target, string fieldName)
        {
            FieldInfo field = target.GetType().GetField(fieldName, InstancePrivate);
            Assert.That(field, Is.Not.Null, $"Missing {target.GetType().Name}.{fieldName}");
            return (T)field.GetValue(target);
        }

        private static void SetPrivate(object target, string fieldName, object value)
        {
            FieldInfo field = target.GetType().GetField(fieldName, InstancePrivate);
            Assert.That(field, Is.Not.Null, $"Missing {target.GetType().Name}.{fieldName}");
            field.SetValue(target, value);
        }

        private T Track<T>(T value) where T : UnityEngine.Object
        {
            m_Cleanup.Add(value);
            return value;
        }
    }
}
#endif
