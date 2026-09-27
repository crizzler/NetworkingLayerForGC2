#if GC2_INVENTORY
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using GameCreator.Runtime.Inventory;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.VisualScripting;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Arawn.GameCreator2.Networking.Inventory.Tests
{
    /// <summary>
    /// Regression coverage for the reported defect: a world object created by the authoritative
    /// inventory drop is instantiated from the item's prefab and therefore never carries a
    /// <see cref="NetworkInventoryPickupSource"/>. The native Add Item interaction of a non-host
    /// client used to be routed to the unvalidated client-owned add request, which the server
    /// correctly rejects with NotAuthorized, so the drop was unclaimable by every non-host peer
    /// and unclaimable after the server drop registry expired.
    /// A dropped object must instead submit the canonical server-validated pickup request bound to
    /// the exact tracked runtime identity of the object the player actually interacted with.
    /// </summary>
    public sealed class InventoryDropPickupRouteTests
    {
        private const BindingFlags InstancePrivate =
            BindingFlags.Instance | BindingFlags.NonPublic;

        private const BindingFlags StaticPrivate =
            BindingFlags.Static | BindingFlags.NonPublic;

        private readonly List<UnityEngine.Object> m_Cleanup = new();

        private readonly List<Task> m_Operations = new();

        [SetUp]
        public void ClearFixtureRegistries()
        {
            foreach (string name in new[] {"s_DroppedItemInstances", "s_ServerDroppedWorldItems"})
                ((IDictionary)typeof(NetworkInventoryController).GetField(name, StaticPrivate).GetValue(null)).Clear();
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            try
            {
                foreach (Task operation in m_Operations)
                {
                    yield return WaitForTask(operation, 3f, "test operation cleanup");
                    if (operation.IsFaulted) _ = operation.Exception;
                }
            }
            finally
            {
                m_Operations.Clear();
                for (int i=m_Cleanup.Count-1;i>=0;i--)
                    if (m_Cleanup[i]!=null) UnityEngine.Object.DestroyImmediate(m_Cleanup[i]);
                m_Cleanup.Clear();
            }
        }

        [UnityTest]
        public IEnumerator NativeInstructionRejection_StopsChainAndAllowsTheSameListToRetry()
        { return VerifyNativeInstructionRetry(false); }

        [UnityTest]
        public IEnumerator NativeInstructionFault_StopsChainAndAllowsTheSameListToRetry()
        { return VerifyNativeInstructionRetry(true); }

        private sealed class SubsequentInstruction : Instruction
        {
            public int Calls;
            protected override Task Run(Args args) { Calls++;return Task.CompletedTask; }
        }

        private IEnumerator VerifyNativeInstructionRetry(bool fault)
        {
            Fixture fixture=CreateFixture(isServer:true,isLocalClient:true);
            var instruction=new InstructionInventoryAddItem();
            typeof(InstructionInventoryAddItem).GetField("m_Item",InstancePrivate).SetValue(instruction,GetItemInstance.Create(fixture.Item));
            typeof(InstructionInventoryAddItem).GetField("m_Bag",InstancePrivate).SetValue(instruction,GetGameObjectSelf.Create());
            var after=new SubsequentInstruction();var list=new InstructionList(instruction,after);
            var previous=TBagContent.NetworkInstructionAddItemInterceptor;
            int interceptions=0;
            try
            {
                TBagContent.NetworkInstructionAddItemInterceptor=(_,_,_)=>
                {
                    interceptions++;
                    return fault ? Task.FromException<NetworkInventoryInterceptResult>(new InvalidOperationException("native instruction fault probe"))
                        : Task.FromResult(NetworkInventoryInterceptResult.HandledFailure);
                };
                if(fault)LogAssert.Expect(LogType.Exception,"InvalidOperationException: native instruction fault probe");
                var rejected=list.Run(new Args(fixture.Bag.gameObject));m_Operations.Add(rejected);
                yield return WaitForTask(rejected,2f,"native instruction rejection");
                Assert.That(rejected.IsFaulted,Is.False,"Rejection must return a scheduler stop, not strand InstructionList.IsRunning");
                Assert.That(after.Calls,Is.Zero,"Destroy Self or later instructions must not execute after rejection");
                Assert.That(list.IsRunning,Is.False);
                Assert.That(typeof(Instruction).GetProperty("NextInstruction",InstancePrivate).GetValue(instruction),Is.EqualTo(int.MaxValue));
                TBagContent.NetworkInstructionAddItemInterceptor=(_,_,_)=>{interceptions++;return Task.FromResult(NetworkInventoryInterceptResult.HandledSuccess);};
                var retry=list.Run(new Args(fixture.Bag.gameObject));m_Operations.Add(retry);
                yield return WaitForTask(retry,2f,"native instruction retry");
                // GC2's AsyncManager deliberately reports exit in EditMode, so later
                // instruction propagation is proved by the native player range/retry case.
                Assert.That(retry.IsFaulted,Is.False);Assert.That(interceptions,Is.EqualTo(2));Assert.That(list.IsRunning,Is.False);
                Assert.That(typeof(Instruction).GetProperty("NextInstruction",InstancePrivate).GetValue(instruction),Is.EqualTo(1));
            }
            finally { TBagContent.NetworkInstructionAddItemInterceptor=previous; }
        }

        [Test]
        public void DroppedWorldObjectInteraction_SubmitsAuthoritativePickupForExactRuntimeIdentity()
        {
            Fixture fixture = CreateFixture(isServer: false, isLocalClient: true);

            const long droppedRuntimeIdHash = 0x51F00D42L;
            const uint sourceBagNetworkId = 4242u;
            GameObject droppedInstance = Track(new GameObject("Dropped item world object"));
            RememberDroppedItemInstance(droppedRuntimeIdHash, droppedInstance, sourceBagNetworkId);

            var pickupRequests = new List<NetworkPickupRequest>();
            var contentAddRequests = new List<NetworkContentAddRequest>();
            fixture.Manager.OnSendPickupRequest += pickupRequests.Add;
            fixture.Manager.OnSendContentAddRequest += contentAddRequests.Add;

            InvokeInstructionAdd(fixture.Bag, fixture.Item, droppedInstance);

            Assert.That(
                contentAddRequests,
                Is.Empty,
                "A dropped world object must never fall back to an unvalidated client-owned add request.");
            Assert.That(
                pickupRequests.Count,
                Is.EqualTo(1),
                "The interaction must submit exactly one authoritative pickup request.");
            Assert.That(
                pickupRequests[0].PropNetworkId,
                Is.EqualTo(0u),
                "A zero prop id selects the runtime-drop route instead of the registered-source lookup.");
            Assert.That(
                pickupRequests[0].RuntimeIdHash,
                Is.EqualTo(droppedRuntimeIdHash),
                "The pickup must carry the exact tracked runtime identity of the object that was touched.");
            Assert.That(
                pickupRequests[0].SourceBagNetworkId,
                Is.EqualTo(sourceBagNetworkId),
                "The request must keep the authoritative source bag recorded for the drop.");
        }

        [Test]
        public void DroppedWorldObjectInteraction_ResolvesDistinctSameTypeDropsByIdentity()
        {
            Fixture fixture = CreateFixture(isServer: false, isLocalClient: true);

            GameObject firstDrop = Track(new GameObject("First drop"));
            GameObject secondDrop = Track(new GameObject("Second drop"));
            GameObject unrelated = Track(new GameObject("Unrelated world object"));

            // GC2 runs the Add Item instruction list on the trigger's own game object, which is a
            // child of the dropped prefab root, so parent/child identity must resolve.
            GameObject firstTrigger = Track(new GameObject("First drop trigger"));
            firstTrigger.transform.SetParent(firstDrop.transform, false);

            RememberDroppedItemInstance(1111L, firstDrop, 71u);
            RememberDroppedItemInstance(2222L, secondDrop, 72u);

            var pickupRequests = new List<NetworkPickupRequest>();
            var contentAddRequests = new List<NetworkContentAddRequest>();
            fixture.Manager.OnSendPickupRequest += pickupRequests.Add;
            fixture.Manager.OnSendContentAddRequest += contentAddRequests.Add;

            InvokeInstructionAdd(fixture.Bag, fixture.Item, firstTrigger);
            InvokeInstructionAdd(fixture.Bag, fixture.Item, secondDrop);
            InvokeInstructionAdd(fixture.Bag, fixture.Item, unrelated);

            Assert.That(pickupRequests.Count, Is.EqualTo(2));
            Assert.That(
                pickupRequests[0].RuntimeIdHash,
                Is.EqualTo(1111L),
                "A trigger child must resolve to the drop it belongs to.");
            Assert.That(
                pickupRequests[1].RuntimeIdHash,
                Is.EqualTo(2222L),
                "Two same-type drops must keep distinct identities; item type and proximity must not be used.");
            Assert.That(
                contentAddRequests.Count,
                Is.EqualTo(1),
                "Only the untracked interaction source may fall back to the generic Add Item route.");
        }

        [Test]
        public void UntrackedInteractionSource_StillUsesGenericInstructionAddRoute()
        {
            Fixture fixture = CreateFixture(isServer: false, isLocalClient: true);
            GameObject unrelated = Track(new GameObject("Untracked world object"));

            var pickupRequests = new List<NetworkPickupRequest>();
            var contentAddRequests = new List<NetworkContentAddRequest>();
            fixture.Manager.OnSendPickupRequest += pickupRequests.Add;
            fixture.Manager.OnSendContentAddRequest += contentAddRequests.Add;

            InvokeInstructionAdd(fixture.Bag, fixture.Item, unrelated);

            Assert.That(
                pickupRequests,
                Is.Empty,
                "Only an object tracked as a networked drop may use the runtime-drop pickup route.");
            Assert.That(
                contentAddRequests.Count,
                Is.EqualTo(1),
                "An untracked interaction source must keep the pre-existing generic Add Item route.");
        }

        [Test]
        public void DroppedWorldItemPickupRequest_ZeroRuntimeIdentityIsRejectedWithoutSending()
        {
            Fixture fixture = CreateFixture(isServer: false, isLocalClient: true);
            var pickupRequests = new List<NetworkPickupRequest>();
            fixture.Manager.OnSendPickupRequest += pickupRequests.Add;

            NetworkPickupResponse response = RequestDroppedWorldItemPickup(
                fixture.Manager, fixture.Controller, 0L, 5u);

            Assert.That(response.Authorized, Is.False);
            Assert.That(
                response.RejectionReason,
                Is.EqualTo(InventoryRejectionReason.InvalidOperation),
                "An absent runtime identity must be rejected before any transport traffic.");
            Assert.That(pickupRequests, Is.Empty);
        }

        [Test]
        public void ServerDroppedWorldItemPickup_NonPlayerInventoryFailsClosedWithoutGenericAdd()
        {
            Fixture fixture = CreateFixture(isServer: true, isLocalClient: false);

            // Focused routing test: the authoritative role is forced directly because this
            // EditMode fixture has no transport session. It asserts which server handler runs and
            // that a failed grant stays atomic; it is not multiplayer evidence.
            SetPrivate(fixture.Manager, "m_IsServer", true);

            const long droppedRuntimeIdHash = 777L;
            GameObject droppedInstance = Track(new GameObject("Server dropped world object"));
            droppedInstance.transform.position = fixture.Controller.transform.position;
            RememberDroppedItemInstance(droppedRuntimeIdHash, droppedInstance, 12u);
            RememberServerDroppedWorldItem(droppedRuntimeIdHash, 12u);

            var contentAddRequests = new List<NetworkContentAddRequest>();
            fixture.Manager.OnSendContentAddRequest += contentAddRequests.Add;

            NetworkPickupResponse response = RequestDroppedWorldItemPickup(
                fixture.Manager, fixture.Controller, droppedRuntimeIdHash, 12u);

            Assert.That(
                contentAddRequests,
                Is.Empty,
                "The runtime-drop route must never degrade into an unvalidated content add.");
            Assert.That(response.Authorized, Is.False);
            Assert.That(
                response.RejectionReason,
                Is.EqualTo(InventoryRejectionReason.NotAuthorized),
                "Only a player-backed inventory may claim a world drop; the range gate rejects others.");
            Assert.That(
                IsServerDropRegistered(droppedRuntimeIdHash),
                Is.True,
                "A failed grant must leave the drop registered and available for a later attempt.");
        }

        /// <summary>
        /// A supplied interaction source that merely encloses several tracked drops cannot identify
        /// one of them. Claiming whichever happened to be enumerated first would grant an arbitrary
        /// same-type drop, so the interaction must fail closed instead of falling through to the
        /// generic Add Item grant.
        /// </summary>
        [Test]
        public void DroppedWorldObjectInteraction_SharedAncestorWithTwoDropsFailsClosedWithoutGrant()
        {
            Fixture fixture = CreateFixture(isServer: false, isLocalClient: true);

            GameObject wrapper = Track(new GameObject("WorldItems"));
            GameObject dropA = Track(new GameObject("DropA"));
            GameObject dropB = Track(new GameObject("DropB"));
            dropA.transform.SetParent(wrapper.transform, false);
            dropB.transform.SetParent(wrapper.transform, false);

            RememberDroppedItemInstance(11L, dropA, 31u);
            RememberDroppedItemInstance(22L, dropB, 32u);

            var pickupRequests = new List<NetworkPickupRequest>();
            var contentAddRequests = new List<NetworkContentAddRequest>();
            fixture.Manager.OnSendPickupRequest += pickupRequests.Add;
            fixture.Manager.OnSendContentAddRequest += contentAddRequests.Add;

            InvokeInstructionAdd(fixture.Bag, fixture.Item, wrapper);

            Assert.That(
                pickupRequests,
                Is.Empty,
                "An ambiguous enclosing source must not claim an arbitrary tracked drop.");
            Assert.That(
                contentAddRequests,
                Is.Empty,
                "A recognised but ambiguous drop interaction must not degrade into a generic grant.");
        }

        /// <summary>
        /// Nested registered drops form an ancestry chain, so the innermost enclosing registered
        /// root is the only correct answer and must not depend on registry enumeration order.
        /// </summary>
        [Test]
        public void DroppedWorldObjectInteraction_NestedRegisteredRootsResolveToInnermostRegardlessOfOrder()
        {
            Fixture fixture = CreateFixture(isServer: false, isLocalClient: true);

            GameObject outer = Track(new GameObject("OuterDrop"));
            GameObject inner = Track(new GameObject("InnerDrop"));
            inner.transform.SetParent(outer.transform, false);
            GameObject innerTrigger = Track(new GameObject("InnerDrop trigger"));
            innerTrigger.transform.SetParent(inner.transform, false);

            // Outermost registered first so a first-match implementation returns the wrong outer id.
            RememberDroppedItemInstance(100L, outer, 41u);
            RememberDroppedItemInstance(200L, inner, 42u);

            var pickupRequests = new List<NetworkPickupRequest>();
            fixture.Manager.OnSendPickupRequest += pickupRequests.Add;

            InvokeInstructionAdd(fixture.Bag, fixture.Item, innerTrigger);

            Assert.That(pickupRequests.Count, Is.EqualTo(1));
            Assert.That(
                pickupRequests[0].RuntimeIdHash,
                Is.EqualTo(200L),
                "A nested interaction must resolve to the innermost enclosing registered root.");
        }

        /// <summary>
        /// An Add Item instruction that does not request the tracked drop's own payload is not a
        /// pickup claim. Unrelated reward/quest instructions that happen to sit inside a drop
        /// hierarchy must retain their documented generic behaviour.
        /// </summary>
        [Test]
        public void DroppedWorldObjectInteraction_RequestedItemMismatchKeepsGenericInstructionRoute()
        {
            Fixture fixture = CreateFixture(isServer: false, isLocalClient: true);

            GameObject droppedInstance = Track(new GameObject("Dropped item world object"));
            RememberDroppedItemInstance(0x77AA11L, droppedInstance, 55u, itemHash: 0x5EED);

            var pickupRequests = new List<NetworkPickupRequest>();
            var contentAddRequests = new List<NetworkContentAddRequest>();
            fixture.Manager.OnSendPickupRequest += pickupRequests.Add;
            fixture.Manager.OnSendContentAddRequest += contentAddRequests.Add;

            InvokeInstructionAdd(fixture.Bag, fixture.Item, droppedInstance);

            Assert.That(
                pickupRequests,
                Is.Empty,
                "A drop must not be repurposed as the pickup claim for a different requested item.");
            Assert.That(
                contentAddRequests.Count,
                Is.EqualTo(1),
                "A non-matching Add Item instruction keeps its documented generic route.");
        }

        [Test]
        public void DroppedWorldObjectInteraction_DestroyedTrackedInstanceIsUntracked()
        {
            Fixture fixture = CreateFixture(isServer: false, isLocalClient: true);

            GameObject destroyedDrop = Track(new GameObject("Destroyed drop"));
            RememberDroppedItemInstance(313L, destroyedDrop, 61u);
            UnityEngine.Object.DestroyImmediate(destroyedDrop);

            GameObject replacement = Track(new GameObject("Destroyed drop"));

            var pickupRequests = new List<NetworkPickupRequest>();
            var contentAddRequests = new List<NetworkContentAddRequest>();
            fixture.Manager.OnSendPickupRequest += pickupRequests.Add;
            fixture.Manager.OnSendContentAddRequest += contentAddRequests.Add;

            InvokeInstructionAdd(fixture.Bag, fixture.Item, replacement);

            Assert.That(
                pickupRequests,
                Is.Empty,
                "A destroyed tracked instance must not be matched by a later same-named object.");
            Assert.That(contentAddRequests.Count, Is.EqualTo(1));
        }

        /// <summary>
        /// A controlled transport is used here: the test's own sender delegate plays the role of
        /// the network so an authoritative response can be delivered at a chosen moment. These are
        /// focused completion/timeout tests and are not multiplayer evidence.
        /// </summary>
        [UnityTest]
        public IEnumerator PickupCompletion_AuthorizationWithoutAppliedPayloadCannotFinishInstruction()
        {
            Fixture fixture = CreateFixture(isServer: false, isLocalClient: true);
            GameObject droppedInstance = Track(new GameObject("Dropped item world object"));
            RememberDroppedItemInstance(0x41L, droppedInstance, 11u);

            var pickupRequests = new List<NetworkPickupRequest>();
            fixture.Manager.OnSendPickupRequest += pickupRequests.Add;

            Task<NetworkInventoryInterceptResult> operation =
                StartInstructionAdd(fixture.Bag, fixture.Item, droppedInstance);
            yield return null;
            Assert.That(pickupRequests.Count, Is.EqualTo(1));

            CompletePickupResponse(fixture.Manager, AuthorizedResponse(pickupRequests[0], stateVersion: 0u));
            yield return WaitForTask(operation, 5f, "authorized pickup");

            Assert.That(operation.Result, Is.EqualTo(NetworkInventoryInterceptResult.HandledFailure));
            Assert.That(CountBagItems(fixture.Bag), Is.Zero);
            Assert.That(PendingPickupResponses(fixture.Manager), Is.Empty,
                "The pending completion must be released once the response arrives.");
        }

        [UnityTest]
        public IEnumerator PickupCompletion_ServerRejectionStopsTheInstructionWithoutLocalMutation()
        {
            Fixture fixture = CreateFixture(isServer: false, isLocalClient: true);
            GameObject droppedInstance = Track(new GameObject("Dropped item world object"));
            RememberDroppedItemInstance(0x42L, droppedInstance, 12u);

            int itemsBefore = CountBagItems(fixture.Bag);
            var pickupRequests = new List<NetworkPickupRequest>();
            fixture.Manager.OnSendPickupRequest += pickupRequests.Add;

            Task<NetworkInventoryInterceptResult> operation =
                StartInstructionAdd(fixture.Bag, fixture.Item, droppedInstance);
            yield return null;

            CompletePickupResponse(
                fixture.Manager,
                RejectedResponse(pickupRequests[0], InventoryRejectionReason.InsufficientSpace));
            yield return WaitForTask(operation, 5f, "rejected pickup");

            Assert.That(operation.Result, Is.EqualTo(NetworkInventoryInterceptResult.HandledFailure),
                "A rejected claim must not report success to the instruction list.");
            Assert.That(CountBagItems(fixture.Bag), Is.EqualTo(itemsBefore),
                "A rejected claim must not mutate the bag, so success-only destruction cannot run.");
            Assert.That(PendingPickupResponses(fixture.Manager), Is.Empty);
        }

        [UnityTest]
        public IEnumerator PickupCompletion_AbsentResponseTimesOutWithoutMutation()
        {
            Fixture fixture = CreateFixture(isServer: false, isLocalClient: true);
            GameObject droppedInstance = Track(new GameObject("Dropped item world object"));
            RememberDroppedItemInstance(0x43L, droppedInstance, 13u);

            int itemsBefore = CountBagItems(fixture.Bag);
            Task<NetworkInventoryInterceptResult> operation =
                StartInstructionAdd(fixture.Bag, fixture.Item, droppedInstance);

            yield return WaitForTask(operation, 5f, "timed-out pickup");

            Assert.That(operation.Result, Is.EqualTo(NetworkInventoryInterceptResult.HandledFailure));
            Assert.That(CountBagItems(fixture.Bag), Is.EqualTo(itemsBefore));
            Assert.That(PendingPickupResponses(fixture.Manager), Is.Empty,
                "A timed-out request must not leak its pending completion into later tests.");
        }

        [UnityTest]
        public IEnumerator PickupCompletion_UnappliedRevisionConvergenceTimesOut()
        {
            Fixture fixture = CreateFixture(isServer: false, isLocalClient: true);
            GameObject droppedInstance = Track(new GameObject("Dropped item world object"));
            RememberDroppedItemInstance(0x44L, droppedInstance, 14u);

            var pickupRequests = new List<NetworkPickupRequest>();
            fixture.Manager.OnSendPickupRequest += pickupRequests.Add;

            Task<NetworkInventoryInterceptResult> operation =
                StartInstructionAdd(fixture.Bag, fixture.Item, droppedInstance);
            yield return null;

            // Authorized, but the referenced revision never converges locally. Success must not be
            // reported, because the instruction list would then destroy the world object without an item.
            CompletePickupResponse(fixture.Manager, AuthorizedResponse(pickupRequests[0], stateVersion: 987654u));
            yield return WaitForTask(operation, 5f, "non-converging pickup");

            Assert.That(operation.Result, Is.EqualTo(NetworkInventoryInterceptResult.HandledFailure));
            Assert.That(PendingPickupResponses(fixture.Manager), Is.Empty);
        }

        [UnityTest]
        public IEnumerator PickupCompletion_DuplicateResponseAfterCompletionIsIgnored()
        {
            Fixture fixture = CreateFixture(isServer: false, isLocalClient: true);
            GameObject droppedInstance = Track(new GameObject("Dropped item world object"));
            RememberDroppedItemInstance(0x45L, droppedInstance, 15u);

            var pickupRequests = new List<NetworkPickupRequest>();
            fixture.Manager.OnSendPickupRequest += pickupRequests.Add;

            Task<NetworkInventoryInterceptResult> operation =
                StartInstructionAdd(fixture.Bag, fixture.Item, droppedInstance);
            yield return null;

            NetworkPickupResponse first = AuthorizedResponse(pickupRequests[0], stateVersion: 0u);
            CompletePickupResponse(fixture.Manager, first);
            yield return WaitForTask(operation, 5f, "duplicate-response pickup");

            // A replayed/stale response must be a no-op rather than a second grant or an exception.
            CompletePickupResponse(fixture.Manager, first);
            Assert.That(PendingPickupResponses(fixture.Manager), Is.Empty);
        }

        [UnityTest]
        public IEnumerator PickupCompletion_DelayedGrantWaitsForActualPayloadApplication()
        {
            yield return ApplyRealGrant(delayed:true);
        }

        [UnityTest]
        public IEnumerator PickupCompletion_AlreadyAppliedGrantAndDuplicateResponseAreIdempotent()
        {
            yield return ApplyRealGrant(delayed:false);
        }

        private IEnumerator ApplyRealGrant(bool delayed)
        {
            Fixture f=CreateFixture(false,true);
            f.Item=Settings.From<InventoryRepository>().Items.Get(new IdString("potion_health"));
            Assert.That(f.Item,Is.Not.Null,"Declared Inventory demo dependency must provide Potion_Health");
            var runtime=new RuntimeItem(f.Item);
            foreach (RuntimeProperty property in runtime.Properties.Values)
            { property.Number=37; property.Text="customized pickup regression"; }
            var payload=(NetworkRuntimeItem)typeof(NetworkInventoryController)
                .GetMethod("ConvertToNetworkItem",InstancePrivate).Invoke(f.Controller,new object[]{runtime});
            GameObject drop=Track(new GameObject("Actual payload drop"));
            RememberDroppedItemInstance(runtime.RuntimeID.Hash,drop,71u,f.Item.ID.Hash);
            var requests=new List<NetworkPickupRequest>();
            f.Manager.OnSendPickupRequest += requests.Add;
            var operation=StartInstructionAdd(f.Bag,f.Item,drop);
            Assert.That(requests.Count,Is.EqualTo(1));
            NetworkPickupResponse response=AuthorizedResponse(requests[0],77u);
            response.PickedUpItem=payload; response.PlacedPosition=Vector2Int.zero;
            var add=new NetworkItemAddedBroadcast {BagNetworkId=f.Controller.NetworkId,
                Item=payload,Position=Vector2Int.zero,StackCount=1,StateVersion=77u};
            if (!delayed) f.Controller.ReceiveItemAddedBroadcast(add);
            CompletePickupResponse(f.Manager,response);
            if (delayed)
            {
                yield return null;
                Assert.That(operation.IsCompleted,Is.False,"Response alone cannot complete the instruction");
                Assert.That(CountBagItems(f.Bag),Is.Zero);
                f.Controller.ReceiveItemAddedBroadcast(add);
            }
            yield return WaitForTask(operation,2f,"real applied grant");
            Assert.That(operation.Result,Is.EqualTo(NetworkInventoryInterceptResult.HandledSuccess));
            var applied=f.Bag.Content.GetRuntimeItem(runtime.RuntimeID);
            Assert.That(applied,Is.Not.Null);
            Assert.That(applied.RuntimeID.String,Is.EqualTo(runtime.RuntimeID.String));
            foreach (var property in runtime.Properties)
            {
                Assert.That(applied.Properties[property.Key].Number,Is.EqualTo(property.Value.Number));
                Assert.That(applied.Properties[property.Key].Text,Is.EqualTo(property.Value.Text));
            }
            CompletePickupResponse(f.Manager,response);
            f.Controller.ReceiveItemAddedBroadcast(add);
            Assert.That(CountBagItems(f.Bag),Is.EqualTo(1));
            Assert.That(f.Bag.Content.GetRuntimeItem(runtime.RuntimeID),Is.SameAs(applied));
            Assert.That(PendingPickupResponses(f.Manager),Is.Empty);
        }

        [UnityTest]
        public IEnumerator PickupCompletion_ThrowingTransportReleasesPendingRequest()
        {
            Fixture f=CreateFixture(false,true);
            GameObject drop=Track(new GameObject("Transport failure"));
            RememberDroppedItemInstance(991L,drop,71u);
            f.Manager.OnSendPickupRequest += _ => throw new InvalidOperationException("controlled transport failure");
            var operation=StartInstructionAdd(f.Bag,f.Item,drop);
            yield return WaitForTask(operation,1f,"faulted pickup");
            Assert.That(operation.IsFaulted,Is.True);
            Assert.That(operation.Exception.InnerException.Message,Is.EqualTo("controlled transport failure"));
            Assert.That(PendingPickupResponses(f.Manager),Is.Empty);
        }

        [UnityTest]
        public IEnumerator PickupCompletion_TeardownDuringConvergenceStopsInstruction()
        {
            Fixture f=CreateFixture(false,true);
            GameObject drop=Track(new GameObject("Pending convergence"));
            RememberDroppedItemInstance(991L,drop,71u);
            var requests=new List<NetworkPickupRequest>();
            f.Manager.OnSendPickupRequest += requests.Add;
            var operation=StartInstructionAdd(f.Bag,f.Item,drop);
            CompletePickupResponse(f.Manager,AuthorizedResponse(requests[0],777u));
            yield return null;
            f.Manager.ClearControllers();
            yield return WaitForTask(operation,1f,"cancelled convergence");
            Assert.That(operation.Result,Is.EqualTo(NetworkInventoryInterceptResult.HandledFailure));
            Assert.That(PendingPickupResponses(f.Manager),Is.Empty);
        }

        [Test]
        public void TrackedDrop_WithPickupSourceStillClaimsExactRuntimePayload()
        {
            Fixture f = CreateFixture(false, true);
            GameObject drop = Track(new GameObject("Tracked drop with pickup component"));
            var source = drop.AddComponent<NetworkInventoryPickupSource>();
            SetPrivate(source, "m_PickupId", 912u);
            SetPrivate(source, "m_Item", f.Item);
            RememberDroppedItemInstance(991L, drop, 71u, f.Item.ID.Hash);
            var requests = new List<NetworkPickupRequest>();
            f.Manager.OnSendPickupRequest += requests.Add;
            InvokeInstructionAdd(f.Bag, f.Item, drop);
            Assert.That(requests.Count, Is.EqualTo(1));
            Assert.That(requests[0].PropNetworkId, Is.Zero, "Tracked payload takes precedence over base Item source");
            Assert.That(requests[0].RuntimeIdHash, Is.EqualTo(991L));
        }

        [Test]
        public void TwoRuntimeIdentitiesForSameWorldObject_FailClosed()
        {
            Fixture f = CreateFixture(false, true);
            GameObject drop = Track(new GameObject("Ambiguously registered drop"));
            RememberDroppedItemInstance(991L, drop, 71u);
            RememberDroppedItemInstance(992L, drop, 71u);
            var requests = new List<NetworkPickupRequest>();
            f.Manager.OnSendPickupRequest += requests.Add;
            InvokeInstructionAdd(f.Bag, f.Item, drop);
            Assert.That(requests, Is.Empty);
        }

        [Test]
        public void DuplicateRemovalForKnownIdentity_DoesNotRemoveNearbyUnrelatedDrop()
        {
            Fixture f = CreateFixture(false, true);
            GameObject other = Track(new GameObject("Unrelated same-position drop"));
            RememberDroppedItemInstance(992L, other, 71u);
            f.Controller.ReceiveDroppedItemRemovedBroadcast(new NetworkDroppedItemRemovedBroadcast
                { RuntimeIdHash=991L, SourceBagNetworkId=71u, Position=Vector3.zero });
            IDictionary registry = (IDictionary)typeof(NetworkInventoryController)
                .GetField("s_DroppedItemInstances", StaticPrivate).GetValue(null);
            Assert.That(registry.Contains(992L), Is.True);
            Assert.That(other != null, Is.True);
        }

        [Test]
        public void RegistryExpiry_RemovesWorldObjectAndBroadcastsExactRemoval()
        {
            Fixture f = CreateFixture(true, true);
            SetPrivate(f.Manager, "m_IsServer", true);
            GameObject drop = Track(new GameObject("Expired drop"));
            RememberDroppedItemInstance(991L, drop, 71u);
            RememberServerDroppedWorldItem(991L, 71u);
            IDictionary registry = (IDictionary)typeof(NetworkInventoryController)
                .GetField("s_ServerDroppedWorldItems", StaticPrivate).GetValue(null);
            object entry=registry[991L];
            entry.GetType().GetField("Time").SetValue(entry, Time.unscaledTime-601f);
            registry[991L]=entry;
            var removed = new List<NetworkDroppedItemRemovedBroadcast>();
            f.Manager.OnBroadcastDroppedItemRemoved += removed.Add;
            typeof(NetworkInventoryController).GetMethod("MaintainDroppedWorldItems", StaticPrivate).Invoke(null,new object[] {f.Manager, Time.unscaledTime});
            Assert.That(registry.Contains(991L), Is.False);
            Assert.That(removed.Count, Is.EqualTo(1));
            Assert.That(removed[0].RuntimeIdHash, Is.EqualTo(991L));
            Assert.That(drop == null || !drop.activeSelf, Is.True, "Expiry must not leave a visible unclaimable object");
        }

        [UnityTest]
        public IEnumerator ManagerTeardown_CompletesPendingClaimAndClearsItImmediately()
        {
            Fixture f=CreateFixture(false,true);

            GameObject drop=Track(new GameObject("Pending claim"));
            RememberDroppedItemInstance(991L,drop,71u);
            var requests=new List<NetworkPickupRequest>();
            f.Manager.OnSendPickupRequest += requests.Add;
            var operation=StartInstructionAdd(f.Bag,f.Item,drop);
            f.Manager.ClearControllers();
            Assert.That(PendingPickupResponses(f.Manager),Is.Empty);
            yield return WaitForTask(operation,1f,"cancelled pickup");
            Assert.That(operation.Result,Is.EqualTo(NetworkInventoryInterceptResult.HandledFailure));
        }

        private static NetworkPickupResponse AuthorizedResponse(NetworkPickupRequest request, uint stateVersion)
        {
            return new NetworkPickupResponse
            {
                RequestId = request.RequestId,
                ActorNetworkId = request.ActorNetworkId,
                CorrelationId = request.CorrelationId,
                Authorized = true,
                RejectionReason = InventoryRejectionReason.None,
                StateVersion = stateVersion,
                PlacedPosition = TBagContent.INVALID
            };
        }

        private static NetworkPickupResponse RejectedResponse(
            NetworkPickupRequest request, InventoryRejectionReason reason)
        {
            return new NetworkPickupResponse
            {
                RequestId = request.RequestId,
                ActorNetworkId = request.ActorNetworkId,
                CorrelationId = request.CorrelationId,
                Authorized = false,
                RejectionReason = reason,
                PlacedPosition = TBagContent.INVALID
            };
        }

        private static void CompletePickupResponse(NetworkInventoryManager manager, NetworkPickupResponse response)
        {
            MethodInfo method = typeof(NetworkInventoryManager).GetMethod(
                "CompletePickupResponse",
                InstancePrivate);
            Assert.That(method, Is.Not.Null, "Missing NetworkInventoryManager.CompletePickupResponse");
            method.Invoke(manager, new object[] { response });
        }

        private static int CountBagItems(Bag bag)
        {
            int count = 0;
            foreach (Cell cell in bag.Content.CellList)
            {
                if (cell != null && !cell.Available) count++;
            }

            return count;
        }

        private static ICollection PendingPickupResponses(NetworkInventoryManager manager)
        {
            return GetPrivate<IDictionary>(manager, "m_PendingPickupResponses").Values;
        }

        /// <summary>
        /// Polls the operation from the test coroutine. Nothing here blocks the editor main thread
        /// on an incomplete Task.
        /// </summary>
        private static IEnumerator WaitForTask(Task task, float timeoutSeconds, string what)
        {
            float deadline = Time.realtimeSinceStartup + timeoutSeconds;
            while (!task.IsCompleted && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }

            Assert.That(task.IsCompleted, Is.True, $"{what} did not complete within {timeoutSeconds:0.00}s.");
        }

        /// <summary>
        /// Runs the delegate <see cref="NetworkInventoryPatchHooks"/> installs for GC2's patched
        /// InstructionInventoryAddItem.Run. The delegate emits its request synchronously before its
        /// first incomplete await, so the emitted request is observable without awaiting the
        /// transport response.
        /// </summary>
        private void InvokeInstructionAdd(Bag bag, Item item, GameObject source)
        {
            Func<Bag, Item, GameObject, Task<NetworkInventoryInterceptResult>> interceptor =
                TBagContent.NetworkInstructionAddItemInterceptor;
            Assert.That(
                interceptor,
                Is.Not.Null,
                "The Inventory patch ABI must be installed before the interaction entry point is exercised.");
            m_Operations.Add(interceptor.Invoke(bag, item, source));
        }

        /// <summary>Starts the production interaction entry point without waiting for its completion.</summary>
        private Task<NetworkInventoryInterceptResult> StartInstructionAdd(
            Bag bag, Item item, GameObject source)
        {
            Func<Bag, Item, GameObject, Task<NetworkInventoryInterceptResult>> interceptor =
                TBagContent.NetworkInstructionAddItemInterceptor;
            Assert.That(interceptor, Is.Not.Null, "The Inventory patch ABI must be installed.");
            var operation = interceptor.Invoke(bag, item, source);
            m_Operations.Add(operation);
            return operation;
        }

        /// <summary>
        /// Invokes the dropped-world-item pickup API added by the fix. Resolving it by reflection
        /// keeps this file compilable against the reverted baseline so the client route tests above
        /// can still demonstrate the behavioural regression; those tests are the red/green proof.
        /// </summary>
        private static NetworkPickupResponse RequestDroppedWorldItemPickup(
            NetworkInventoryManager manager,
            NetworkInventoryController picker,
            long runtimeIdHash,
            uint sourceBagNetworkId)
        {
            MethodInfo method = typeof(NetworkInventoryManager).GetMethod(
                "RequestDroppedWorldItemPickupAsync",
                BindingFlags.Instance | BindingFlags.Public);
            if (method == null)
            {
                Assert.Ignore(
                    "This build has no RequestDroppedWorldItemPickupAsync; the production fix is absent.");
            }

            var task = (Task<NetworkPickupResponse>)method.Invoke(
                manager,
                new object[] { picker, runtimeIdHash, sourceBagNetworkId });
            Assert.That(task, Is.Not.Null);

            // Both callers complete synchronously: the zero-identity and server paths return before
            // the first incomplete await, so blocking on the awaiter cannot deadlock the editor.
            return task.GetAwaiter().GetResult();
        }

        private static void RememberDroppedItemInstance(
            long runtimeIdHash, GameObject instance, uint sourceBagNetworkId, int itemHash = 0)
        {
            MethodInfo method = typeof(NetworkInventoryController).GetMethod(
                "RememberDroppedItemInstance",
                StaticPrivate);
            Assert.That(method, Is.Not.Null, "Missing NetworkInventoryController.RememberDroppedItemInstance");
            var item = new NetworkRuntimeItem { ItemHash = itemHash };
            method.Invoke(
                null,
                new object[]
                {
                    runtimeIdHash,
                    instance,
                    sourceBagNetworkId,
                    item,
                    Vector3.zero
                });
        }

        private static void RememberServerDroppedWorldItem(long runtimeIdHash, uint sourceBagNetworkId)
        {
            MethodInfo method = typeof(NetworkInventoryController).GetMethod(
                "RememberServerDroppedWorldItem",
                StaticPrivate);
            Assert.That(method, Is.Not.Null, "Missing NetworkInventoryController.RememberServerDroppedWorldItem");
            method.Invoke(
                null,
                new object[]
                {
                    runtimeIdHash,
                    sourceBagNetworkId,
                    default(NetworkRuntimeItem),
                    Vector3.zero
                });
        }

        private static bool IsServerDropRegistered(long runtimeIdHash)
        {
            FieldInfo field = typeof(NetworkInventoryController).GetField(
                "s_ServerDroppedWorldItems",
                StaticPrivate);
            Assert.That(field, Is.Not.Null, "Missing NetworkInventoryController.s_ServerDroppedWorldItems");
            var registry = (IDictionary)field.GetValue(null);
            return registry.Contains(runtimeIdHash);
        }

        private Fixture CreateFixture(bool isServer, bool isLocalClient)
        {
            Assert.That(
                NetworkInventoryPatchHooks.IsInventoryPatched(),
                Is.True,
                "The Inventory patch ABI must be present before the semantic interaction entry point can be exercised.");

            GameObject managerObject = Track(new GameObject("Inventory drop route manager"));
            NetworkInventoryManager manager = managerObject.AddComponent<NetworkInventoryManager>();

            // Install the semantic hooks through the production entry point instead of relying on
            // component lifecycle ordering, which would make this fixture depend on teardown timing
            // between tests. Shutdown first so a stale m_Installed flag cannot leave the delegates
            // cleared while a later Initialize call early-returns.
            NetworkInventoryPatchHooks hooks = managerObject.GetComponent<NetworkInventoryPatchHooks>();
            if (hooks == null) hooks = managerObject.AddComponent<NetworkInventoryPatchHooks>();
            hooks.Shutdown();
            hooks.Initialize(isServer);

            Assert.That(
                TBagContent.NetworkInstructionAddItemInterceptor,
                Is.Not.Null,
                "NetworkInventoryPatchHooks.Initialize must install the Add Item interceptor.");

            GameObject bagObject = Track(new GameObject("Inventory drop route bag"));
            Bag bag = bagObject.AddComponent<Bag>();
            EnsureBagAwake(bag);

            NetworkInventoryController controller =
                bagObject.AddComponent<NetworkInventoryController>();
            EnsureControllerAwake(controller);
            EnsureControllerStarted(controller);

            Item item = Track(ScriptableObject.CreateInstance<Item>());
            item.name = "Inventory drop route item";

            SetPrivate(controller, "m_StaticNetworkIdOverride", 5001u);
            controller.Initialize(isServer, isLocalClient);

            // Keep the focused routing tests fast: the awaited transport response never arrives in
            // EditMode, so the request path must reach its bounded timeout promptly.
            SetPrivate(manager, "m_RequestTimeout", 0.25f);

            Assert.That(
                controller.NetworkId,
                Is.Not.EqualTo(0u),
                "The fixture must expose a stable non-zero bag identity.");
            Assert.That(
                NetworkInventoryManager.Instance,
                Is.SameAs(manager),
                "The fixture manager must be discoverable as the active Inventory manager.");

            return new Fixture
            {
                Manager = manager,
                Hooks = hooks,
                Bag = bag,
                Controller = controller,
                Item = item
            };
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

        /// <summary>
        /// EditMode never runs Start on a newly added component, but the controller registers
        /// itself in its static controller list and subscribes to the GC2 bag events there. Without
        /// this call the patched entry points cannot resolve the bag and return Unhandled.
        /// </summary>
        private static void EnsureControllerStarted(NetworkInventoryController controller)
        {
            InvokeLifecycle(controller, "Start");

            MethodInfo resolve = typeof(NetworkInventoryController).GetMethod(
                "TryResolveForBag",
                StaticPrivate);
            Assert.That(resolve, Is.Not.Null, "Missing NetworkInventoryController.TryResolveForBag");
            object[] arguments = { controller.Bag, null };
            bool resolved = (bool)resolve.Invoke(null, arguments);
            Assert.That(
                resolved,
                Is.True,
                "The fixture bag must resolve to its controller before an interaction is exercised.");
            Assert.That(arguments[1], Is.SameAs(controller));
        }

        private static void InvokeLifecycle(object target, string methodName)
        {
            MethodInfo method = target.GetType().GetMethod(methodName, InstancePrivate);
            Assert.That(method, Is.Not.Null, $"Missing {target.GetType().Name}.{methodName}");
            method.Invoke(target, null);
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

        private sealed class Fixture
        {
            public NetworkInventoryManager Manager;
            public NetworkInventoryPatchHooks Hooks;
            public Bag Bag;
            public NetworkInventoryController Controller;
            public Item Item;
        }
    }
}
#endif
