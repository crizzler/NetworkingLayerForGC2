#if GC2_TRAVERSAL
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Arawn.GameCreator2.Networking;
using Arawn.GameCreator2.Networking.Traversal.Transport.PurrNet;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.Traversal;
using GameCreator.Runtime.VisualScripting;
using NUnit.Framework;
using PurrNet.Packing;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Traversal.Tests
{
    internal sealed class TraversalMotionTestTransportBridge : NetworkTransportBridge
    {
        public override bool IsServer => true;
        public override bool IsClient => true;
        public override bool IsHost => true;
        public override float ServerTime => Time.time;

        public override void SendToServer(uint characterNetworkId, NetworkInputState[] inputs) { }

        public override void SendToOwner(
            uint ownerClientId,
            uint characterNetworkId,
            NetworkPositionState state,
            float serverTime) { }

        public override void Broadcast(
            uint characterNetworkId,
            NetworkPositionState state,
            float serverTime,
            uint excludeClientId = uint.MaxValue,
            NetworkRecipientFilter relevanceFilter = null) { }
    }

    public sealed class TraversalReplicationTests
    {
        private GameObject m_ManagerObject;
        private readonly List<UnityEngine.Object> m_Cleanup = new();

        [TearDown]
        public void TearDown()
        {
            for (int i = m_Cleanup.Count - 1; i >= 0; i--)
            {
                if (m_Cleanup[i] != null) Object.DestroyImmediate(m_Cleanup[i]);
            }

            m_Cleanup.Clear();
            if (m_ManagerObject != null) Object.DestroyImmediate(m_ManagerObject);
            m_ManagerObject = null;
        }

        [Test]
        public void StateVersionComparison_IsMonotonicAndWrapSafe()
        {
            Assert.That(NetworkTraversalVersion.IsNewer(2, 1), Is.True);
            Assert.That(NetworkTraversalVersion.IsNewer(1, 1), Is.False);
            Assert.That(NetworkTraversalVersion.IsNewer(1, 2), Is.False);
            Assert.That(NetworkTraversalVersion.IsNewer(1, uint.MaxValue), Is.True);
            Assert.That(NetworkTraversalVersion.IsNewer(0, uint.MaxValue), Is.False);
        }

        [Test]
        public void TraversalPackets_PurrNetRoundTrip_PreserveVersionAndPersistentPose()
        {
            var response = new NetworkTraversalResponse
            {
                RequestId = 8,
                ActorNetworkId = 17,
                CorrelationId = 81,
                Action = TraversalActionType.EnterTraverseInteractive,
                Authorized = true,
                Applied = true,
                TraverseHash = 119,
                TraverseIdString = "scene:climb",
                IsTraversing = true,
                StateVersion = 42
            };
            NetworkTraversalResponse responseResult = RoundTrip(response);
            Assert.That(responseResult.StateVersion, Is.EqualTo(42));
            Assert.That(responseResult.TraverseIdString, Is.EqualTo("scene:climb"));

            var broadcast = new NetworkTraversalBroadcast
            {
                NetworkId = 17,
                ActorNetworkId = 17,
                CorrelationId = 81,
                Action = TraversalActionType.EnterTraverseInteractive,
                TraverseHash = 119,
                TraverseIdString = "scene:climb",
                IsTraversing = true,
                StateVersion = 43,
                ServerTime = 12.5f
            };
            NetworkTraversalBroadcast broadcastResult = RoundTrip(broadcast);
            Assert.That(broadcastResult.StateVersion, Is.EqualTo(43));
            Assert.That(broadcastResult.ServerTime, Is.EqualTo(12.5f));

            var snapshot = new NetworkTraversalSnapshot
            {
                NetworkId = 17,
                ServerTime = 13f,
                IsTraversing = true,
                TraverseHash = 119,
                TraverseIdString = "scene:climb",
                StateVersion = 44,
                Kind = TraversalSnapshotKind.ActiveInteractive,
                HasRelativePose = true,
                RelativePosition = new Vector3(1.25f, -0.5f, 2.75f),
                RelativeRotation = Quaternion.Euler(0f, 45f, 0f)
            };
            NetworkTraversalSnapshot snapshotResult = RoundTrip(snapshot);
            Assert.That(snapshotResult.StateVersion, Is.EqualTo(44));
            Assert.That(snapshotResult.Kind, Is.EqualTo(TraversalSnapshotKind.ActiveInteractive));
            Assert.That(snapshotResult.HasRelativePose, Is.True);
            Assert.That(snapshotResult.RelativePosition, Is.EqualTo(snapshot.RelativePosition));
            Assert.That(Quaternion.Angle(snapshotResult.RelativeRotation, snapshot.RelativeRotation), Is.LessThan(0.01f));
        }

        [Test]
        public void PendingSnapshot_KeepsLatestPersistentStatePerCharacter()
        {
            NetworkTraversalManager manager = CreateManager();
            manager.ReceiveFullSnapshot(new NetworkTraversalSnapshot
            {
                NetworkId = 55,
                StateVersion = 10,
                ServerTime = 1f,
                IsTraversing = true,
                Kind = TraversalSnapshotKind.ActiveInteractive,
                TraverseHash = 100,
                TraverseIdString = "old"
            });
            manager.ReceiveFullSnapshot(new NetworkTraversalSnapshot
            {
                NetworkId = 55,
                StateVersion = 11,
                ServerTime = 2f,
                IsTraversing = false,
                Kind = TraversalSnapshotKind.None
            });
            manager.ReceiveFullSnapshot(new NetworkTraversalSnapshot
            {
                NetworkId = 55,
                StateVersion = 9,
                ServerTime = 3f,
                IsTraversing = true,
                Kind = TraversalSnapshotKind.ActiveInteractive,
                TraverseHash = 200,
                TraverseIdString = "stale"
            });

            IDictionary pending = GetPrivateField<IDictionary>(manager, "m_PendingSnapshots");
            Assert.That(pending.Count, Is.EqualTo(1));
            object pendingEntry = pending[55u];
            FieldInfo valueField = pendingEntry.GetType().GetField(
                "Value",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(valueField, Is.Not.Null);
            var latest = (NetworkTraversalSnapshot)valueField.GetValue(pendingEntry);
            Assert.That(latest.StateVersion, Is.EqualTo(11));
            Assert.That(latest.IsTraversing, Is.False);
        }

        [Test]
        public void RequestRoute_FailsClosedUntilTransportReportsReady()
        {
            NetworkTraversalManager manager = CreateManager();
            Assert.That(manager.ResolveRequestRouteStatus(41), Is.EqualTo(TraversalRouteStatus.TransportUnavailable));

            manager.OnSendTraversalRequest = _ => { };
            uint resolvedActor = 0;
            manager.OnResolveRequestRouteStatusForActor = actorNetworkId =>
            {
                resolvedActor = actorNetworkId;
                return TraversalRouteStatus.PatchRequired;
            };
            Assert.That(manager.ResolveRequestRouteStatus(41), Is.EqualTo(TraversalRouteStatus.PatchRequired));
            Assert.That(resolvedActor, Is.EqualTo(41));

            manager.OnResolveRequestRouteStatusForActor = actorNetworkId =>
                actorNetworkId == 41
                    ? TraversalRouteStatus.Ready
                    : TraversalRouteStatus.ControllerNotReady;
            Assert.That(
                manager.TrySendTraversalRequest(
                    new NetworkTraversalRequest { ActorNetworkId = 41 },
                    out TraversalRouteStatus status),
                Is.True);
            Assert.That(status, Is.EqualTo(TraversalRouteStatus.Ready));
        }

        [Test]
        public void RequestRoute_LegacyParameterlessResolverRemainsCompatibilityFallback()
        {
            NetworkTraversalManager manager = CreateManager();
            manager.OnSendTraversalRequest = _ => { };
#pragma warning disable CS0618
            manager.OnResolveRequestRouteStatus = () => TraversalRouteStatus.Ready;
#pragma warning restore CS0618

            Assert.That(manager.ResolveRequestRouteStatus(99), Is.EqualTo(TraversalRouteStatus.Ready));
        }

        [Test]
        public void PendingTransientBroadcast_ExpiresBeforeLateControllerRegistration()
        {
            NetworkTraversalManager manager = CreateManager();
            SetPrivateField(manager, "m_TransientStateTtl", 0.1f);
            manager.ReceiveTraversalChangeBroadcast(new NetworkTraversalBroadcast
            {
                NetworkId = 77,
                StateVersion = 3,
                Action = TraversalActionType.TryJump
            });

            IDictionary pendingByCharacter = GetPrivateField<IDictionary>(manager, "m_PendingBroadcasts");
            IList pending = (IList)pendingByCharacter[77u];
            Assert.That(pending.Count, Is.EqualTo(1));

            object expired = pending[0];
            FieldInfo receivedAtField = expired.GetType().GetField(
                "ReceivedAt",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(receivedAtField, Is.Not.Null);
            receivedAtField.SetValue(expired, Time.unscaledTime - 10f);
            pending[0] = expired;

            InvokePrivate(manager, "CleanupExpiredPendingState");
            Assert.That(pendingByCharacter.Contains(77u), Is.False);
        }

        [Test]
        public void InteractiveSnapshot_ExactIdentityReplacesActiveAToB()
        {
            NetworkTraversalController controller = CreateRemoteController(
                101,
                out _,
                out TraversalStance stance);
            TraverseInteractive traverseA = CreateInteractive("Snapshot Traverse A");
            TraverseInteractive traverseB = CreateInteractive("Snapshot Traverse B");

            int enterCount = 0;
            int exitCount = 0;
            stance.EventMotionEnter += () => enterCount++;
            stance.EventMotionExit += () => exitCount++;

            controller.ReceiveFullSnapshot(CreateActiveInteractiveSnapshot(controller, traverseA, 1));
            Assert.That(stance.Traverse, Is.SameAs(traverseA));

            controller.ReceiveFullSnapshot(CreateActiveInteractiveSnapshot(controller, traverseB, 2));

            Assert.That(stance.Traverse, Is.SameAs(traverseB));
            Assert.That(enterCount, Is.EqualTo(2));
            Assert.That(exitCount, Is.EqualTo(1));
            Assert.That(GetPrivateField<uint>(controller, "m_LastAppliedStateVersion"), Is.EqualTo(2));
        }

        [Test]
        public async Task InteractiveSnapshot_WaitsForNativeAToExitBeforeRestoringB()
        {
            NetworkTraversalController controller = CreateRemoteController(
                118,
                out _,
                out TraversalStance stance);
            TraverseInteractive traverseA = CreateInteractive("Native Snapshot Traverse A");
            TraverseInteractive traverseB = CreateInteractive("Native Snapshot Traverse B");

            System.Func<TraversalStance, bool> previousForceCancelValidator =
                TraversalStance.NetworkForceCancelValidator;
            TraversalStance.NetworkForceCancelValidator = null;

            try
            {
                MethodInfo enterMethod = typeof(TraversalStance).GetMethod(
                    "OnTraverseEnter",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                MethodInfo exitMethod = typeof(TraversalStance).GetMethod(
                    "OnTraverseExit",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.That(enterMethod, Is.Not.Null);
                Assert.That(exitMethod, Is.Not.Null);

                TraversalToken token = await (Task<TraversalToken>)enterMethod.Invoke(
                    stance,
                    new object[] { traverseA });
                Assert.That(stance.Traverse, Is.SameAs(traverseA));

                controller.ReceiveFullSnapshot(
                    CreateActiveInteractiveSnapshot(controller, traverseB, 1));

                Assert.That(
                    stance.Traverse,
                    Is.SameAs(traverseA),
                    "B must not start before A's native motion cleanup reaches OnTraverseExit");

                exitMethod.Invoke(stance, new object[] { traverseA, token });
                for (int i = 0; i < 20 && !ReferenceEquals(stance.Traverse, traverseB); i++)
                {
                    await Task.Yield();
                }

                Assert.That(stance.Traverse, Is.SameAs(traverseB));
                Assert.That(GetPrivateField<uint>(controller, "m_LastAppliedStateVersion"), Is.EqualTo(1));
            }
            finally
            {
                TraversalStance.NetworkForceCancelValidator = previousForceCancelValidator;
            }
        }

        [Test]
        public void ActiveLinkSnapshot_CancelsStaleInteractiveWithoutReplayingLink()
        {
            NetworkTraversalController controller = CreateRemoteController(
                102,
                out _,
                out TraversalStance stance);
            TraverseInteractive activeInteractive = CreateInteractive("Stale Interactive A");
            TraverseLink transientLink = CreateLink("Transient Link B");

            int enterCount = 0;
            int exitCount = 0;
            stance.EventMotionEnter += () => enterCount++;
            stance.EventMotionExit += () => exitCount++;

            controller.ReceiveFullSnapshot(
                CreateActiveInteractiveSnapshot(controller, activeInteractive, 1));
            Assert.That(stance.Traverse, Is.SameAs(activeInteractive));

            string linkId = BuildTraverseId(transientLink);
            controller.ReceiveFullSnapshot(new NetworkTraversalSnapshot
            {
                NetworkId = controller.NetworkId,
                ServerTime = 2f,
                IsTraversing = true,
                TraverseHash = StableHashUtility.GetStableHash(linkId),
                TraverseIdString = linkId,
                StateVersion = 2,
                Kind = TraversalSnapshotKind.ActiveLink
            });

            Assert.That(stance.Traverse, Is.Null);
            Assert.That(enterCount, Is.EqualTo(1), "A link snapshot must not replay transient motion");
            Assert.That(exitCount, Is.EqualTo(1));
            Assert.That(
                GetPrivateField<uint>(controller, "m_LastAppliedStateVersion"),
                Is.EqualTo(1),
                "A transient snapshot must not claim that its link was applied");
            Assert.That(
                GetPrivateField<uint>(controller, "m_LatestTransientSnapshotVersion"),
                Is.EqualTo(2),
                "Transient ordering must still reject older state after the link snapshot");
        }

        [Test]
        public void UnresolvedSnapshot_SameVersionRemainsRetryableUntilTargetSpawns()
        {
            NetworkTraversalController controller = CreateRemoteController(
                103,
                out _,
                out TraversalStance stance);
            TraverseInteractive delayedTraverse = CreateInteractive("Delayed Snapshot Traverse");
            NetworkTraversalSnapshot snapshot = CreateActiveInteractiveSnapshot(
                controller,
                delayedTraverse,
                7);

            delayedTraverse.gameObject.SetActive(false);
            controller.ReceiveFullSnapshot(snapshot);

            Assert.That(GetPrivateField<bool>(controller, "m_HasPendingUnresolvedSnapshot"), Is.True);
            Assert.That(GetPrivateField<bool>(controller, "m_HasAppliedAuthoritativeState"), Is.False);

            delayedTraverse.gameObject.SetActive(true);
            SetPrivateField(controller, "m_NextUnresolvedStateRetryTime", 0f);
            InvokePrivate(controller, "RetryPendingUnresolvedAuthoritativeState");

            Assert.That(stance.Traverse, Is.SameAs(delayedTraverse));
            Assert.That(GetPrivateField<bool>(controller, "m_HasPendingUnresolvedSnapshot"), Is.False);
            Assert.That(GetPrivateField<uint>(controller, "m_LastAppliedStateVersion"), Is.EqualTo(7));
        }

        [Test]
        public void TraverseValidation_DistinguishesUnresolvedAndUnusableMotion()
        {
            NetworkTraversalController controller = CreateRemoteController(
                104,
                out _,
                out _);
            TraverseInteractive traverse = CreateInteractive("Motion Validation Traverse");
            NetworkTraversalRequest request = CreateStartRequest(controller, traverse, 19);

            bool resolved = TryResolveTraverseForRequest(
                controller,
                request,
                out TraversalRejectionReason rejection);
            Assert.That(resolved, Is.False);
            Assert.That(rejection, Is.EqualTo(TraversalRejectionReason.UnresolvedMotion));

            MotionInteractive motion = Track(ScriptableObject.CreateInstance<MotionInteractive>());
            SetPrivateField(
                motion,
                "m_CanUse",
                new RunConditionsList(new ConditionMathAlwaysFalse()));
            SetPrivateField(traverse, "m_Motion", motion);

            resolved = TryResolveTraverseForRequest(controller, request, out rejection);
            Assert.That(resolved, Is.False);
            Assert.That(rejection, Is.EqualTo(TraversalRejectionReason.UnusableMotion));
        }

        [Test]
        public async Task ServerStartAcknowledgement_PastDeadlineMapsToStartTimeoutRejection()
        {
            NetworkTraversalController controller = CreateRemoteController(
                105,
                out _,
                out _);
            SetPrivateField(controller, "m_IsServer", true);
            TraverseInteractive target = CreateInteractive("Unacknowledged Traverse");
            NetworkTraversalRequest request = CreateStartRequest(controller, target, 23);

            object acknowledgement = InvokePrivateResult(
                controller,
                "BeginServerStartAcknowledgement",
                request,
                target);
            Assert.That(acknowledgement, Is.Not.Null);
            SetField(acknowledgement, "CreatedAt", Time.realtimeSinceStartup - 2f);

            var waitTask = (Task<bool>)InvokePrivateResult(
                controller,
                "WaitForServerStartAcknowledgementAsync",
                acknowledgement);
            Assert.That(await waitTask, Is.False);

            var response = (NetworkTraversalResponse)InvokePrivateStaticResult(
                typeof(NetworkTraversalController),
                "CreateStartTimeoutResponse",
                request);
            Assert.That(response.Authorized, Is.False);
            Assert.That(response.Applied, Is.False);
            Assert.That(response.RejectionReason, Is.EqualTo(TraversalRejectionReason.StartTimeout));
        }

        [Test]
        public async Task ConcurrentRequests_WaitOnThePerControllerSerializationGate()
        {
            NetworkTraversalController controller = CreateRemoteController(
                106,
                out _,
                out _);
            SemaphoreSlim gate = GetPrivateField<SemaphoreSlim>(controller, "m_ServerRequestGate");
            await gate.WaitAsync();

            Task<NetworkTraversalResponse> first = controller.ProcessTraversalRequestAsync(default, 1);
            Task<NetworkTraversalResponse> second = controller.ProcessTraversalRequestAsync(default, 2);
            Assert.That(first.IsCompleted, Is.False);
            Assert.That(second.IsCompleted, Is.False);

            gate.Release();
            NetworkTraversalResponse[] responses = await Task.WhenAll(first, second);

            Assert.That(responses[0].Authorized, Is.False);
            Assert.That(responses[1].Authorized, Is.False);
            Assert.That(gate.CurrentCount, Is.EqualTo(1));
        }

        [Test]
        public void AuthoritativeOperationToken_DoesNotConsumeForStaleTraverse()
        {
            NetworkTraversalController controller = CreateRemoteController(
                107,
                out _,
                out _);
            TraverseInteractive expected = CreateInteractive("Expected Operation Traverse");
            TraverseInteractive stale = CreateInteractive("Stale Operation Traverse");

            object operation = InvokePrivateResult(
                controller,
                "CreateAuthoritativeMotionOperation",
                expected,
                31u,
                9u,
                2f);
            SetPrivateField(controller, "m_PendingAuthoritativeMotionEnter", operation);

            bool staleConsumed = (bool)InvokePrivateResult(
                controller,
                "TryConsumeAuthoritativeMotionEnter",
                stale);
            Assert.That(staleConsumed, Is.False);
            object stillPending = GetPrivateField<object>(
                controller,
                "m_PendingAuthoritativeMotionEnter");
            Assert.That((uint)GetField(stillPending, "Sequence"), Is.Not.Zero);

            bool expectedConsumed = (bool)InvokePrivateResult(
                controller,
                "TryConsumeAuthoritativeMotionEnter",
                expected);
            Assert.That(expectedConsumed, Is.True);
            object cleared = GetPrivateField<object>(
                controller,
                "m_PendingAuthoritativeMotionEnter");
            Assert.That((uint)GetField(cleared, "Sequence"), Is.Zero);
        }

        [Test]
        public async Task YieldedOlderTraversalStart_CannotOverwriteNewerTarget()
        {
            NetworkTraversalController controller = CreateRemoteController(
                108,
                out _,
                out TraversalStance stance);
            TraverseInteractive activeA = CreateInteractive("Generation Active A");
            TraverseInteractive yieldedB = CreateInteractive("Generation Yielded B");
            TraverseInteractive newerC = CreateInteractive("Generation Newer C");
            Assert.That(stance.NetworkRestoreInteractiveSnapshot(activeA, Vector3.zero), Is.True);

            System.Func<TraversalStance, bool> previousForceCancelValidator =
                TraversalStance.NetworkForceCancelValidator;
            TraversalStance.NetworkForceCancelValidator = null;

            try
            {
                MethodInfo enterMethod = typeof(TraversalStance).GetMethod(
                    "OnTraverseEnter",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.That(enterMethod, Is.Not.Null);

                var olderTask = (Task<TraversalToken>)enterMethod.Invoke(
                    stance,
                    new object[] { yieldedB });
                Assert.That(olderTask.IsCompleted, Is.False, "B must yield while cancelling A");

                var newerTask = (Task<TraversalToken>)enterMethod.Invoke(
                    stance,
                    new object[] { newerC });
                TraversalToken newerToken = await newerTask;
                TraversalToken olderToken = await olderTask;

                Assert.That(newerToken.IsCancelled, Is.False);
                Assert.That(olderToken.IsCancelled, Is.True);
                Assert.That(stance.Traverse, Is.SameAs(newerC));
            }
            finally
            {
                TraversalStance.NetworkForceCancelValidator = previousForceCancelValidator;
            }

            _ = controller;
        }

        [Test]
        public async Task TimedOutEnterGeneration_DoesNotCancelRetryToSameTraverse()
        {
            NetworkTraversalController controller = CreateRemoteController(
                109,
                out _,
                out TraversalStance stance);
            TraverseInteractive active = CreateInteractive("Timeout Active");
            TraverseInteractive target = CreateInteractive("Timeout Retry Target");
            Assert.That(stance.NetworkRestoreInteractiveSnapshot(active, Vector3.zero), Is.True);

            System.Func<TraversalStance, bool> previousForceCancelValidator =
                TraversalStance.NetworkForceCancelValidator;
            TraversalStance.NetworkForceCancelValidator = null;

            try
            {
                MethodInfo enterMethod = typeof(TraversalStance).GetMethod(
                    "OnTraverseEnter",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.That(enterMethod, Is.Not.Null);

                var timedOutTask = (Task<TraversalToken>)enterMethod.Invoke(
                    stance,
                    new object[] { target });
                Assert.That(timedOutTask.IsCompleted, Is.False);

                stance.NetworkInvalidatePendingEnter();

                var retryTask = (Task<TraversalToken>)enterMethod.Invoke(
                    stance,
                    new object[] { target });
                TraversalToken retryToken = await retryTask;
                TraversalToken timedOutToken = await timedOutTask;

                Assert.That(retryToken.IsCancelled, Is.False);
                Assert.That(timedOutToken.IsCancelled, Is.True);
                Assert.That(stance.Traverse, Is.SameAs(target));
            }
            finally
            {
                TraversalStance.NetworkForceCancelValidator = previousForceCancelValidator;
            }

            _ = controller;
        }

        [Test]
        public async Task AuthoritativeCancel_InvalidatesOlderYieldedEnter()
        {
            NetworkTraversalController controller = CreateRemoteController(
                116,
                out _,
                out TraversalStance stance);
            TraverseInteractive active = CreateInteractive("Cancel Active A");
            TraverseInteractive staleStart = CreateInteractive("Cancel Stale B");
            Assert.That(stance.NetworkRestoreInteractiveSnapshot(active, Vector3.zero), Is.True);

            System.Func<TraversalStance, bool> previousForceCancelValidator =
                TraversalStance.NetworkForceCancelValidator;
            TraversalStance.NetworkForceCancelValidator = null;

            try
            {
                MethodInfo enterMethod = typeof(TraversalStance).GetMethod(
                    "OnTraverseEnter",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.That(enterMethod, Is.Not.Null);

                var staleTask = (Task<TraversalToken>)enterMethod.Invoke(
                    stance,
                    new object[] { staleStart });
                Assert.That(staleTask.IsCompleted, Is.False);

                InvokePrivateResult(
                    controller,
                    "ForceCancelAuthoritativeTraversal",
                    stance,
                    44u,
                    9u);

                TraversalToken staleToken = await staleTask;
                Assert.That(staleToken.IsCancelled, Is.True);
                Assert.That(stance.Traverse, Is.Not.SameAs(staleStart));
            }
            finally
            {
                TraversalStance.NetworkForceCancelValidator = previousForceCancelValidator;
            }
        }

        [Test]
        public void AuthoritativeStateMatch_RequiresExactTraversalIdentity()
        {
            NetworkTraversalController controller = CreateRemoteController(
                117,
                out _,
                out TraversalStance stance);
            TraverseInteractive traverseA = CreateInteractive("Identity A");
            TraverseInteractive traverseB = CreateInteractive("Identity B");
            Assert.That(stance.NetworkRestoreInteractiveSnapshot(traverseA, Vector3.zero), Is.True);

            string idA = BuildTraverseId(traverseA);
            string idB = BuildTraverseId(traverseB);
            Assert.That(
                InvokePrivateResult(
                    controller,
                    "LocalTraversalMatchesAuthoritativeState",
                    true,
                    StableHashUtility.GetStableHash(idA),
                    idA),
                Is.True);
            Assert.That(
                InvokePrivateResult(
                    controller,
                    "LocalTraversalMatchesAuthoritativeState",
                    true,
                    StableHashUtility.GetStableHash(idB),
                    idB),
                Is.False);
            Assert.That(
                InvokePrivateResult(
                    controller,
                    "LocalTraversalMatchesAuthoritativeState",
                    false,
                    0,
                    string.Empty),
                Is.False);
        }

        [Test]
        public void LocalOwnerInteractiveSnapshot_ResumesMovementLoopWithoutGameplayEntry()
        {
            NetworkTraversalController controller = CreateController(
                110,
                isLocalClient: true,
                out Character character,
                out TraversalStance stance);
            TraverseInteractive interactive = CreateInteractive("Owner Snapshot Traverse");
            MotionInteractive motion = Track(ScriptableObject.CreateInstance<MotionInteractive>());
            SetPrivateField(interactive, "m_Motion", motion);

            controller.ReceiveFullSnapshot(CreateActiveInteractiveSnapshot(controller, interactive, 3));

            Assert.That(stance.Traverse, Is.SameAs(interactive));
            Assert.That(
                (bool)GetProperty(stance, "AllowMovement"),
                Is.True,
                "The local owner snapshot shell must accept traversal input");
            Assert.That(
                character.Driver.UpdateKinematics,
                Is.False,
                "The presentation-safe MotionInteractive loop must be running for the owner");
        }

        [Test]
        public void NewerServerInteractiveSnapshot_CorrectsLocallyClearedState()
        {
            NetworkTraversalController controller = CreateRemoteController(
                111,
                out _,
                out TraversalStance stance);
            TraverseInteractive authoritative = CreateInteractive("Authoritative Reentry Traverse");

            controller.ReceiveFullSnapshot(
                CreateActiveInteractiveSnapshot(controller, authoritative, 1));
            Assert.That(stance.Traverse, Is.SameAs(authoritative));

            InvokePrivateResult(
                controller,
                "ForceCancelAuthoritativeTraversal",
                stance,
                0u,
                0u);
            Assert.That(stance.Traverse, Is.Null);

            controller.ReceiveFullSnapshot(
                CreateActiveInteractiveSnapshot(controller, authoritative, 2));
            Assert.That(stance.Traverse, Is.SameAs(authoritative));
            Assert.That(GetPrivateField<uint>(controller, "m_LastAppliedStateVersion"), Is.EqualTo(2));
        }

        [Test]
        public void InteractiveConnectionMarker_IsValidForConfiguredLinkActionShape()
        {
            NetworkTraversalController controller = CreateRemoteController(
                112,
                out _,
                out _);
            TraverseLink link = CreateLink("Interactive Connection Link");
            NetworkTraversalRequest request = CreateStartRequest(controller, link, 12);
            const string connectionMarker = "__network_interactive_connection";
            request.ActionIdString = connectionMarker;
            request.ActionIdHash = StableHashUtility.GetStableHash(connectionMarker);

            MethodInfo method = typeof(NetworkTraversalController).GetMethod(
                "ValidateRequestIdentity",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            object[] arguments = { request, string.Empty };

            bool valid = (bool)method.Invoke(controller, arguments);

            Assert.That(valid, Is.True, (string)arguments[1]);
        }

        [Test]
        public void BuiltInHostServerDriver_IsAcceptedForTraversalRouting()
        {
            NetworkTraversalManager manager = CreateManager();
            manager.OnSendTraversalRequest = _ => { };
            manager.OnResolveRequestRouteStatusForActor = actorNetworkId =>
                actorNetworkId == 113 ? TraversalRouteStatus.Ready : TraversalRouteStatus.ControllerNotReady;
            NetworkTraversalController controller = CreateHostController(
                113,
                NetworkPredictionBackend.BuiltIn);

            object[] arguments = { TraversalRouteStatus.Unknown };
            bool accepted = (bool)InvokePrivateResult(
                controller,
                "CanAcceptPatchedRequest",
                arguments);

            Assert.That(accepted, Is.True);
            Assert.That(arguments[0], Is.EqualTo(TraversalRouteStatus.Ready));
        }

        [Test]
        public void CachedRemoteRole_IsRefreshedBeforePatchedRouteValidation()
        {
            NetworkTraversalManager manager = CreateManager();
            manager.OnSendTraversalRequest = _ => { };
            manager.OnResolveRequestRouteStatusForActor = actorNetworkId =>
                actorNetworkId == 116 ? TraversalRouteStatus.Ready : TraversalRouteStatus.ControllerNotReady;
            NetworkTraversalController controller = CreateHostController(
                116,
                NetworkPredictionBackend.BuiltIn);

            controller.Initialize(isServer: false, isLocalClient: false);
            Assert.That(controller.IsRemoteClient, Is.True);

            object[] arguments = { TraversalRouteStatus.Unknown };
            bool accepted = (bool)InvokePrivateResult(
                controller,
                "CanAcceptPatchedRequest",
                arguments);

            Assert.That(accepted, Is.True);
            Assert.That(controller.IsServer, Is.True);
            Assert.That(controller.IsLocalClient, Is.True);
            Assert.That(controller.IsRemoteClient, Is.False);
        }

        [Test]
        public void ClientPredictedHost_UsesOwnerMotionAuthorityWindow()
        {
            NetworkTraversalController controller = CreateHostController(
                117,
                NetworkPredictionBackend.BuiltIn,
                hostUsesClientPrediction: true);

            InvokePrivateResult(controller, "OpenServerOwnerMotionWindow", 77u);

            Assert.That(GetPrivateField<bool>(controller, "m_ServerOwnerMotionWindowOpen"), Is.True);
            Assert.That(GetPrivateField<bool>(controller, "m_ServerOwnerMotionUsesClientAuthority"), Is.True);
            Assert.That(GetPrivateField<uint>(controller, "m_ServerOwnerMotionOperationId"), Is.EqualTo(77u));
        }

        [Test]
        public void PurrDictionHost_IsRejectedBeforeServerRoleBypass()
        {
            NetworkTraversalController controller = CreateHostController(
                114,
                NetworkPredictionBackend.PurrDiction);

            object[] arguments = { TraversalRouteStatus.Unknown };
            bool accepted = (bool)InvokePrivateResult(
                controller,
                "CanAcceptPatchedRequest",
                arguments);

            Assert.That(accepted, Is.False);
            Assert.That(
                arguments[0],
                Is.EqualTo(TraversalRouteStatus.UnsupportedPredictionBackend));
        }

        [Test]
        public async Task TrustedServerRequest_StillRejectsPurrDictionTraversal()
        {
            NetworkTraversalController controller = CreateHostController(
                115,
                NetworkPredictionBackend.PurrDiction);
            var request = new NetworkTraversalRequest
            {
                RequestId = 1,
                ActorNetworkId = 115,
                TargetNetworkId = 115,
                CorrelationId = 1,
                Action = TraversalActionType.TryJump
            };

            NetworkTraversalResponse response = await controller.ProcessTraversalRequestAsync(
                request,
                NetworkTransportBridge.InvalidClientId);

            Assert.That(response.Authorized, Is.False);
            Assert.That(
                response.RejectionReason,
                Is.EqualTo(TraversalRejectionReason.UnsupportedPredictionBackend));
        }

        [Test]
        public async Task UnversionedAndAuthoritativeStartState_JoinOneInFlightClientApply()
        {
            NetworkTraversalController controller = CreateRemoteController(
                119,
                out _,
                out TraversalStance stance);
            TraverseInteractive active = CreateInteractive("Exact Once Active");
            TraverseInteractive replacement = CreateInteractive("Exact Once Replacement");
            TraverseInteractive conflicting = CreateInteractive("Exact Once Conflict");
            TraverseLink staleTransientLink = CreateLink("Older Transient Snapshot");
            SetPrivateField(
                active,
                "m_Motion",
                Track(ScriptableObject.CreateInstance<MotionInteractive>()));
            SetPrivateField(
                replacement,
                "m_Motion",
                Track(ScriptableObject.CreateInstance<MotionInteractive>()));
            SetPrivateField(
                conflicting,
                "m_Motion",
                Track(ScriptableObject.CreateInstance<MotionInteractive>()));

            System.Func<TraversalStance, bool> previousForceCancelValidator =
                TraversalStance.NetworkForceCancelValidator;
            TraversalStance.NetworkForceCancelValidator = null;

            try
            {
                MethodInfo enterMethod = typeof(TraversalStance).GetMethod(
                    "OnTraverseEnter",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                MethodInfo exitMethod = typeof(TraversalStance).GetMethod(
                    "OnTraverseExit",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.That(enterMethod, Is.Not.Null);
                Assert.That(exitMethod, Is.Not.Null);

                SetPrivateField(controller, "m_SuppressInterception", true);
                TraversalToken activeToken = await (Task<TraversalToken>)enterMethod.Invoke(
                    stance,
                    new object[] { active });
                SetPrivateField(controller, "m_SuppressInterception", false);

                int enterCount = 1;
                stance.EventMotionEnter += () => enterCount++;
                string replacementId = BuildTraverseId(replacement);
                string conflictingId = BuildTraverseId(conflicting);

                var responseApply = (Task<bool>)InvokePrivateResult(
                    controller,
                    "BeginOrJoinClientAuthoritativeStateApply",
                    TraversalActionType.EnterTraverseInteractive,
                    true,
                    StableHashUtility.GetStableHash(replacementId),
                    replacementId,
                    replacement,
                    string.Empty,
                    string.Empty,
                    0u,
                    0u,
                    71u,
                    0u,
                    false,
                    default(NetworkTraversalSnapshot));
                Assert.That(
                    responseApply.IsCompleted,
                    Is.False,
                    "The replacement must wait for the exact previous traversal cleanup");

                object firstOperation = GetPrivateField<object>(
                    controller,
                    "m_ClientAuthoritativeStateApply");
                var broadcastApply = (Task<bool>)InvokePrivateResult(
                    controller,
                    "BeginOrJoinClientAuthoritativeStateApply",
                    TraversalActionType.EnterTraverseInteractive,
                    true,
                    StableHashUtility.GetStableHash(replacementId),
                    replacementId,
                    replacement,
                    string.Empty,
                    string.Empty,
                    0u,
                    0u,
                    71u,
                    15u,
                    false,
                    default(NetworkTraversalSnapshot));

                Assert.That(
                    broadcastApply,
                    Is.SameAs(responseApply),
                    "Matching response and broadcast state must share one application task");
                Assert.That(
                    GetPrivateField<object>(controller, "m_ClientAuthoritativeStateApply"),
                    Is.SameAs(firstOperation));
                Assert.That(
                    GetField(firstOperation, "StateVersion"),
                    Is.EqualTo(15u),
                    "The authoritative broadcast must promote the optimistic operation's version");

                controller.ReceiveFullSnapshot(new NetworkTraversalSnapshot
                {
                    NetworkId = 119,
                    IsTraversing = true,
                    TraverseHash = StableHashUtility.GetStableHash(replacementId),
                    TraverseIdString = replacementId,
                    StateVersion = 15,
                    Kind = TraversalSnapshotKind.ActiveInteractive
                });
                Assert.That(
                    GetPrivateField<object>(controller, "m_ClientAuthoritativeStateApply"),
                    Is.SameAs(firstOperation),
                    "A matching snapshot must join the response/broadcast application operation");

                string staleLinkId = BuildTraverseId(staleTransientLink);
                controller.ReceiveFullSnapshot(new NetworkTraversalSnapshot
                {
                    NetworkId = 119,
                    IsTraversing = true,
                    TraverseHash = StableHashUtility.GetStableHash(staleLinkId),
                    TraverseIdString = staleLinkId,
                    StateVersion = 14,
                    Kind = TraversalSnapshotKind.ActiveLink
                });
                Assert.That(
                    GetPrivateField<object>(controller, "m_ClientAuthoritativeStateApply"),
                    Is.SameAs(firstOperation),
                    "An older transient snapshot must not supersede a newer in-flight operation");
                Assert.That(
                    GetPrivateField<uint>(controller, "m_LatestTransientSnapshotVersion"),
                    Is.Zero);

                var contradictoryApply = (Task<bool>)InvokePrivateResult(
                    controller,
                    "BeginOrJoinClientAuthoritativeStateApply",
                    TraversalActionType.EnterTraverseInteractive,
                    true,
                    StableHashUtility.GetStableHash(conflictingId),
                    conflictingId,
                    conflicting,
                    string.Empty,
                    string.Empty,
                    0u,
                    0u,
                    72u,
                    15u,
                    false,
                    default(NetworkTraversalSnapshot));
                Assert.That(await contradictoryApply, Is.False);
                Assert.That(
                    GetPrivateField<object>(controller, "m_ClientAuthoritativeStateApply"),
                    Is.SameAs(firstOperation),
                    "A contradictory state at the same version must not supersede the operation");

                exitMethod.Invoke(stance, new object[] { active, activeToken });
                for (int i = 0; i < 50 && !responseApply.IsCompleted; i++)
                {
                    await Task.Yield();
                }

                Assert.That(
                    responseApply.IsCompleted,
                    Is.True,
                    "The replacement operation did not converge after the old traversal exited");
                Assert.That(await responseApply, Is.True);
                Assert.That(await broadcastApply, Is.True);
                Assert.That(stance.Traverse, Is.SameAs(replacement));
                Assert.That(enterCount, Is.EqualTo(2), "The replacement must enter exactly once");
                Assert.That(
                    GetPrivateField<uint>(controller, "m_LastAppliedStateVersion"),
                    Is.EqualTo(15));
            }
            finally
            {
                TraversalStance.NetworkForceCancelValidator = previousForceCancelValidator;
            }
        }

        [Test]
        public void StatefulStart_WithOptimismEnabled_WaitsForAuthoritativeConfirmation()
        {
            NetworkTraversalManager manager = CreateManager();
            int sends = 0;
            manager.OnSendTraversalRequest = _ => sends++;
            manager.OnResolveRequestRouteStatusForActor = actorNetworkId =>
                actorNetworkId == 122
                    ? TraversalRouteStatus.Ready
                    : TraversalRouteStatus.ControllerNotReady;

            NetworkTraversalController controller = CreateController(
                122,
                isLocalClient: true,
                out Character character,
                out TraversalStance stance);
            NetworkCharacter networkCharacter = character.GetComponent<NetworkCharacter>();
            character.Kernel.ChangeDriver(character, new UnitDriverNetworkClient());
            networkCharacter.InitializeNetworkRole(
                isServer: false,
                isOwner: true,
                isHost: false);
            controller.Initialize(isServer: false, isLocalClient: true);
            SetPrivateField(controller, "m_OptimisticUpdates", true);

            TraverseInteractive target = CreateInteractive("Confirmed Traversal Start");
            controller.RequestEnterTraverseInteractive(target);

            Assert.That(sends, Is.EqualTo(1));
            Assert.That(stance.Traverse, Is.Null);
            Assert.That(
                GetPrivateField<object>(controller, "m_ClientAuthoritativeStateApply"),
                Is.Null,
                "Stateful Traversal must not create a local optimistic entry operation");
            Assert.That(
                GetPrivateField<IDictionary>(controller, "m_PendingRequests").Count,
                Is.EqualTo(1));
        }

        [Test]
        public async Task ProtectedConnectionLink_ConsumesRepeatedLocalTryJumpWithoutSending()
        {
            NetworkTraversalManager manager = CreateManager();
            int sends = 0;
            manager.OnSendTraversalRequest = _ => sends++;
            manager.OnResolveRequestRouteStatusForActor = actorNetworkId =>
                actorNetworkId == 120
                    ? TraversalRouteStatus.Ready
                    : TraversalRouteStatus.ControllerNotReady;

            NetworkTraversalController controller = CreateController(
                120,
                isLocalClient: true,
                out _,
                out TraversalStance stance);
            TraverseLink pullUpLink = CreateLink("Protected PullUp Link");

            MethodInfo enterMethod = typeof(TraversalStance).GetMethod(
                "OnTraverseEnter",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(enterMethod, Is.Not.Null);

            SetPrivateField(controller, "m_SuppressInterception", true);
            await (Task<TraversalToken>)enterMethod.Invoke(
                stance,
                new object[] { pullUpLink });
            SetPrivateField(controller, "m_SuppressInterception", false);

            InvokePrivateResult(
                controller,
                "ActivateProtectedConnectionLink",
                pullUpLink,
                81u,
                19u);
            Assert.That(
                InvokePrivateResult(
                    controller,
                    "IsProtectedConnectionLinkActive",
                    pullUpLink),
                Is.True);

            controller.RequestTryJump();
            controller.RequestTryJump();

            Assert.That(sends, Is.Zero);
            Assert.That(
                GetPrivateField<IDictionary>(controller, "m_PendingRequests").Count,
                Is.Zero,
                "Repeated PullUp input must not create requests which can replay the link");
            Assert.That(
                GetPrivateField<uint>(controller, "m_ProtectedConnectionLinkStateVersion"),
                Is.EqualTo(19));
        }

        [Test]
        public void ClientPredictionReplay_CapturesAndHonorsDisabledKinematics()
        {
            GameObject gameObject = Track(new GameObject("Traversal Kinematics Replay"));
            Character character = gameObject.AddComponent<Character>();
            var driver = new UnitDriverNetworkClient();
            driver.OnStartup(character);

            try
            {
                driver.UpdateKinematics = false;
                driver.SetExternalMoveDirection(new Vector3(0.75f, 0.25f, -0.5f));
                SetPrivateField(driver, "m_InputAccumulator", 1f);
                Vector3 beforeLiveInput = gameObject.transform.position;
                driver.ProcessLocalInput(Vector2.right, null);

                Assert.That(
                    gameObject.transform.position.x,
                    Is.EqualTo(beforeLiveInput.x).Within(0.0001f));
                Assert.That(
                    gameObject.transform.position.z,
                    Is.EqualTo(beforeLiveInput.z).Within(0.0001f));

                Assert.That(GetPrivateField<int>(driver, "m_PredictionHistoryCount"), Is.EqualTo(1));
                System.Array history = GetPrivateField<System.Array>(driver, "m_PredictionHistory");
                int historyStart = GetPrivateField<int>(driver, "m_PredictionHistoryStart");
                object capturedState = history.GetValue(historyStart);
                Assert.That(
                    GetField(capturedState, "updateKinematics"),
                    Is.False,
                    "Every replay entry must retain the kinematics mode from its input tick");

                Vector3 beforeReplay = gameObject.transform.position;
                Vector3 externalDirection = driver.WorldMoveDirection;
                NetworkInputState input = NetworkInputState.Create(
                    Vector2.right,
                    2,
                    0.1f);
                InvokePrivateResult(driver, "ApplyInputPrediction", input, null, false);

                Vector3 afterDisabledReplay = gameObject.transform.position;
                Assert.That(afterDisabledReplay.x, Is.EqualTo(beforeReplay.x).Within(0.0001f));
                Assert.That(afterDisabledReplay.z, Is.EqualTo(beforeReplay.z).Within(0.0001f));
                Assert.That(
                    driver.WorldMoveDirection,
                    Is.EqualTo(externalDirection),
                    "Locomotion replay must not overwrite Traversal's animation velocity");

                InvokePrivateResult(driver, "ApplyInputPrediction", input, null, true);
                Assert.That(
                    gameObject.transform.position.x,
                    Is.GreaterThan(afterDisabledReplay.x + 0.001f),
                    "The test input must still move when its captured kinematics mode is enabled");
            }
            finally
            {
                driver.OnDispose(character);
            }
        }

        [Test]
        public void ServerSimulation_HonorsDisabledKinematicsAndPreservesTraversalVelocity()
        {
            GameObject gameObject = Track(new GameObject("Traversal Server Kinematics"));
            Character character = gameObject.AddComponent<Character>();
            var driver = new UnitDriverNetworkServer();
            driver.OnStartup(character);

            try
            {
                driver.UpdateKinematics = false;
                Vector3 traversalVelocity = new Vector3(-0.6f, 0.1f, 0.8f);
                driver.SetExternalMoveDirection(traversalVelocity);
                Vector3 beforeInput = gameObject.transform.position;

                driver.QueueInput(NetworkInputState.Create(
                    Vector2.right,
                    1,
                    0.1f));
                driver.ProcessInputs();

                Assert.That(
                    gameObject.transform.position.x,
                    Is.EqualTo(beforeInput.x).Within(0.0001f));
                Assert.That(
                    gameObject.transform.position.z,
                    Is.EqualTo(beforeInput.z).Within(0.0001f));
                Assert.That(
                    driver.WorldMoveDirection,
                    Is.EqualTo(traversalVelocity),
                    "Server locomotion must not replace Traversal's animation velocity");
            }
            finally
            {
                driver.OnDispose(character);
            }
        }

        [Test]
        public async Task LedgeAnimationOverride_HoldsIntentPoseAtBoundaryAndUsesOnlyShortReleaseMemory()
        {
            GameObject characterObject = Track(new GameObject("Ledge Animation Character"));
            Character character = characterObject.AddComponent<Character>();
            var networkPlayer = new UnitPlayerDirectionalNetwork();
            character.Kernel.ChangePlayer(character, networkPlayer);

            TraverseInteractive ledge = CreateInteractive("Ledge Animation Traverse");
            MotionInteractive motion = Track(ScriptableObject.CreateInstance<MotionInteractive>());
            motion.name = "Motion_Ledge_Climb";
            SetPrivateField(ledge, "m_Motion", motion);
            SetPrivateField(ledge, "m_PositionA", -1f);
            SetPrivateField(ledge, "m_PositionB", 1f);

            TraversalStance stance = character.Combat.RequestStance<TraversalStance>();
            MethodInfo enterMethod = typeof(TraversalStance).GetMethod(
                "OnTraverseEnter",
                BindingFlags.Instance | BindingFlags.NonPublic);
            MethodInfo exitMethod = typeof(TraversalStance).GetMethod(
                "OnTraverseExit",
                BindingFlags.Instance | BindingFlags.NonPublic);
            PropertyInfo relativePosition = typeof(TraversalStance).GetProperty(
                "RelativePosition",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(enterMethod, Is.Not.Null);
            Assert.That(exitMethod, Is.Not.Null);
            Assert.That(relativePosition, Is.Not.Null);

            TraversalToken token = await (Task<TraversalToken>)enterMethod.Invoke(
                stance,
                new object[] { ledge });
            relativePosition.SetValue(stance, new Vector3(0f, 0f, -1f));

            GameObject hooksObject = Track(new GameObject("Ledge Animation Hooks"));
            NetworkTraversalPatchHooks hooks = hooksObject.AddComponent<NetworkTraversalPatchHooks>();
            Vector3 movingSpeed = new Vector3(-0.7f, 0.15f, 0.05f);

            networkPlayer.InjectInput(Vector2.left);
            object[] liveArguments =
            {
                character,
                Vector3.zero,
                movingSpeed,
                movingSpeed
            };
            bool liveOverride = (bool)InvokePrivateResult(
                hooks,
                "ApplyTraversalAnimationInputOverride",
                liveArguments);

            Assert.That(liveOverride, Is.True);
            Assert.That(
                (Vector3)liveArguments[2],
                Is.EqualTo(Vector3.zero),
                "Outward input at the exact boundary must select the Intent blend, not locomotion");
            Assert.That(
                (Vector3)liveArguments[1],
                Is.EqualTo(Vector3.left),
                "The edge pose must receive one stable authored intent axis");

            networkPlayer.InjectInput(Vector2.zero);
            object[] releaseArguments =
            {
                character,
                Vector3.zero,
                movingSpeed,
                movingSpeed
            };
            bool releaseOverride = (bool)InvokePrivateResult(
                hooks,
                "ApplyTraversalAnimationInputOverride",
                releaseArguments);

            Assert.That(releaseOverride, Is.True);
            Assert.That((Vector3)releaseArguments[2], Is.EqualTo(Vector3.zero));
            Assert.That(((Vector3)releaseArguments[1]).x, Is.EqualTo(-1f));

            IDictionary memory = GetPrivateField<IDictionary>(hooks, "m_LedgeEdgeIntentMemory");
            int characterKey = character.GetInstanceID();
            object expiredMemory = memory[characterKey];
            Assert.That(expiredMemory, Is.Not.Null);
            SetField(expiredMemory, "Timestamp", Time.time - 1f);
            memory[characterKey] = expiredMemory;

            object[] expiredArguments =
            {
                character,
                Vector3.zero,
                movingSpeed,
                movingSpeed
            };
            bool expiredOverride = (bool)InvokePrivateResult(
                hooks,
                "ApplyTraversalAnimationInputOverride",
                expiredArguments);
            Assert.That(expiredOverride, Is.False);
            Assert.That((Vector3)expiredArguments[2], Is.EqualTo(movingSpeed));

            networkPlayer.InjectInput(Vector2.left);
            object[] rememberAgainArguments =
            {
                character,
                Vector3.zero,
                movingSpeed,
                movingSpeed
            };
            InvokePrivateResult(
                hooks,
                "ApplyTraversalAnimationInputOverride",
                rememberAgainArguments);

            networkPlayer.InjectInput(Vector2.right);
            object[] reversalArguments =
            {
                character,
                Vector3.zero,
                -movingSpeed,
                -movingSpeed
            };
            bool reversalOverride = (bool)InvokePrivateResult(
                hooks,
                "ApplyTraversalAnimationInputOverride",
                reversalArguments);
            Assert.That(reversalOverride, Is.False);
            Assert.That(memory.Contains(characterKey), Is.False, "Leaving/reversing from edge A must clear its lease");

            relativePosition.SetValue(stance, new Vector3(0f, 0f, 1f));
            object[] rightBoundaryArguments =
            {
                character,
                Vector3.zero,
                -movingSpeed,
                -movingSpeed
            };
            bool rightBoundaryOverride = (bool)InvokePrivateResult(
                hooks,
                "ApplyTraversalAnimationInputOverride",
                rightBoundaryArguments);
            Assert.That(rightBoundaryOverride, Is.True);
            Assert.That((Vector3)rightBoundaryArguments[2], Is.EqualTo(Vector3.zero));
            Assert.That((Vector3)rightBoundaryArguments[1], Is.EqualTo(Vector3.right));

            exitMethod.Invoke(stance, new object[] { ledge, token });
        }

        [Test]
        public async Task LedgeAnimationOverride_MapsBlockedVerticalInputToForwardAndBackwardEdgeIntent()
        {
            GameObject characterObject = Track(new GameObject("Vertical Ledge Animation Character"));
            Character character = characterObject.AddComponent<Character>();
            var networkPlayer = new UnitPlayerDirectionalNetwork();
            character.Kernel.ChangePlayer(character, networkPlayer);

            TraverseInteractive ledge = CreateInteractive("Vertical Ledge Animation Traverse");
            MotionInteractive motion = Track(ScriptableObject.CreateInstance<MotionInteractive>());
            motion.name = "Motion_Ledge_Climb";
            SetPrivateField(ledge, "m_Motion", motion);
            SetPrivateField(ledge, "m_PositionA", -1f);
            SetPrivateField(ledge, "m_PositionB", 1f);

            TraversalStance stance = character.Combat.RequestStance<TraversalStance>();
            MethodInfo enterMethod = typeof(TraversalStance).GetMethod(
                "OnTraverseEnter",
                BindingFlags.Instance | BindingFlags.NonPublic);
            MethodInfo exitMethod = typeof(TraversalStance).GetMethod(
                "OnTraverseExit",
                BindingFlags.Instance | BindingFlags.NonPublic);
            PropertyInfo relativePosition = typeof(TraversalStance).GetProperty(
                "RelativePosition",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(enterMethod, Is.Not.Null);
            Assert.That(exitMethod, Is.Not.Null);
            Assert.That(relativePosition, Is.Not.Null);

            TraversalToken token = await (Task<TraversalToken>)enterMethod.Invoke(
                stance,
                new object[] { ledge });
            relativePosition.SetValue(stance, Vector3.zero);

            GameObject hooksObject = Track(new GameObject("Vertical Ledge Animation Hooks"));
            NetworkTraversalPatchHooks hooks = hooksObject.AddComponent<NetworkTraversalPatchHooks>();

            networkPlayer.InjectInput(Vector2.up);
            object[] forwardArguments =
            {
                character,
                Vector3.forward,
                Vector3.up,
                Vector3.up
            };
            bool forwardOverride = (bool)InvokePrivateResult(
                hooks,
                "ApplyTraversalAnimationInputOverride",
                forwardArguments);

            Assert.That(forwardOverride, Is.True);
            Assert.That(
                (Vector3)forwardArguments[2],
                Is.EqualTo(Vector3.zero),
                "Blocked forward ledge input must not select Move Forward");
            Assert.That(
                (Vector3)forwardArguments[1],
                Is.EqualTo(Vector3.up),
                "Blocked forward ledge input must select Edge Forward through Intent-Y");

            networkPlayer.InjectInput(Vector2.down);
            object[] backwardArguments =
            {
                character,
                Vector3.back,
                Vector3.down,
                Vector3.down
            };
            bool backwardOverride = (bool)InvokePrivateResult(
                hooks,
                "ApplyTraversalAnimationInputOverride",
                backwardArguments);

            Assert.That(backwardOverride, Is.True);
            Assert.That(
                (Vector3)backwardArguments[2],
                Is.EqualTo(Vector3.zero),
                "Blocked backward ledge input must not select Move Backward");
            Assert.That(
                (Vector3)backwardArguments[1],
                Is.EqualTo(Vector3.down),
                "Blocked backward ledge input must select Edge Backward through Intent-Y");

            exitMethod.Invoke(stance, new object[] { ledge, token });
        }

        [Test]
        public async Task LedgeAnimationOverride_AllNonOwnerRolesUseCurrentPoseAndReplicatedIntent()
        {
            GameObject characterObject = Track(new GameObject("Observed Ledge Animation Character"));
            Character character = characterObject.AddComponent<Character>();
            NetworkCharacter networkCharacter = characterObject.AddComponent<NetworkCharacter>();
            networkCharacter.SetManualNetworkId(902);
            var networkMotion = new UnitMotionNetworkController
            {
                IsServer = false
            };
            character.Kernel.ChangeMotion(character, networkMotion);

            TraverseInteractive ledge = CreateInteractive("Observed Ledge Animation Traverse");
            MotionInteractive motion = Track(ScriptableObject.CreateInstance<MotionInteractive>());
            motion.name = "Motion_Ledge_Climb";
            SetPrivateField(ledge, "m_Motion", motion);
            SetPrivateField(ledge, "m_PositionA", -1f);
            SetPrivateField(ledge, "m_PositionB", 1f);

            TraversalStance stance = character.Combat.RequestStance<TraversalStance>();
            MethodInfo enterMethod = typeof(TraversalStance).GetMethod(
                "OnTraverseEnter",
                BindingFlags.Instance | BindingFlags.NonPublic);
            MethodInfo exitMethod = typeof(TraversalStance).GetMethod(
                "OnTraverseExit",
                BindingFlags.Instance | BindingFlags.NonPublic);
            PropertyInfo relativePosition = typeof(TraversalStance).GetProperty(
                "RelativePosition",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(enterMethod, Is.Not.Null);
            Assert.That(exitMethod, Is.Not.Null);
            Assert.That(relativePosition, Is.Not.Null);

            TraversalToken token = await (Task<TraversalToken>)enterMethod.Invoke(
                stance,
                new object[] { ledge });
            GameObject hooksObject = Track(new GameObject("Observed Ledge Animation Hooks"));
            NetworkTraversalPatchHooks hooks = hooksObject.AddComponent<NetworkTraversalPatchHooks>();

            (Vector3 position, Vector3 replicatedDirection, Vector3 expectedIntent)[] cases =
            {
                (new Vector3(0f, 0f, -1f), Vector3.back, Vector3.left),
                (new Vector3(0f, 0f, 1f), Vector3.forward, Vector3.right),
                (Vector3.zero, Vector3.up, Vector3.up),
                (Vector3.zero, Vector3.down, Vector3.down)
            };

            NetworkCharacter.NetworkRole[] observerRoles =
            {
                NetworkCharacter.NetworkRole.RemoteClient,
                NetworkCharacter.NetworkRole.Server
            };

            ushort sequence = 1;
            foreach (NetworkCharacter.NetworkRole observerRole in observerRoles)
            {
                SetPrivateField(networkCharacter, "m_CurrentRole", observerRole);
                foreach ((Vector3 position, Vector3 replicatedDirection, Vector3 expectedIntent) in cases)
                {
                    // Deliberately keep the persistent stance pose stale. Both client and
                    // server observers must use their current root pose for the boundary while
                    // using the retained motion broadcast for attempted direction.
                    relativePosition.SetValue(stance, -position);
                    character.transform.position = ledge.Transform.TransformPoint(position);
                    networkMotion.ApplyBroadcastCommand(NetworkMotionCommand.CreateMoveToDirection(
                        replicatedDirection,
                        true,
                        9,
                        sequence++));
                    object[] arguments =
                    {
                        character,
                        Vector3.zero,
                        Vector3.zero,
                        Vector3.zero
                    };
                    bool overridden = (bool)InvokePrivateResult(
                        hooks,
                        "ApplyTraversalAnimationInputOverride",
                        arguments);

                    Assert.That(
                        overridden,
                        Is.True,
                        $"Expected {observerRole} edge override at {position}");
                    Assert.That(
                        (Vector3)arguments[2],
                        Is.EqualTo(Vector3.zero),
                        "An observer must not select a Move clip while the owner is pushing a blocked edge");
                    Assert.That((Vector3)arguments[1], Is.EqualTo(expectedIntent));
                }
            }

            exitMethod.Invoke(stance, new object[] { ledge, token });
        }

        [Test]
        public void TraversalMoveDirection_LocalPredictionConsumesItsServerEcho()
        {
            GameObject characterObject = Track(new GameObject("Traversal Direction Prediction"));
            Character character = characterObject.AddComponent<Character>();
            var motion = new UnitMotionNetworkController
            {
                IsServer = false
            };
            motion.OnStartup(character);

            NetworkMotionCommand sent = default;
            int sendCount = 0;
            motion.OnSendCommand += command =>
            {
                sent = command;
                sendCount++;
            };

            motion.MoveToDirection(Vector3.right, Space.World, 9);

            Assert.That(sendCount, Is.EqualTo(1));
            Assert.That(sent.commandType, Is.EqualTo(NetworkMotionCommandType.MoveToDirection));
            Assert.That(
                motion.ConsumePredictedCommand(sent),
                Is.True,
                "The owner must consume the echoed traversal direction it already applied locally");
            Assert.That(
                motion.ConsumePredictedCommand(sent),
                Is.False,
                "A predicted traversal echo must only be consumed once");
        }

        [Test]
        public void TraversalMoveDirection_PassiveServerReplicaCannotOverrideClientOwner()
        {
            GameObject bridgeObject = Track(new GameObject("Traversal Motion Test Bridge"));
            TraversalMotionTestTransportBridge bridge =
                bridgeObject.AddComponent<TraversalMotionTestTransportBridge>();

            GameObject characterObject = Track(new GameObject("Client-Owned Server Traversal Replica"));
            Character character = characterObject.AddComponent<Character>();
            NetworkCharacter networkCharacter = characterObject.AddComponent<NetworkCharacter>();
            networkCharacter.SetManualNetworkId(901);
            SetPrivateField(networkCharacter, "m_RuntimeIsServer", true);
            SetPrivateField(networkCharacter, "m_RuntimeIsOwner", false);
            bridge.SetCharacterOwner(901, 42);

            var motion = new UnitMotionNetworkController
            {
                IsServer = true
            };
            motion.OnStartup(character);

            int broadcastCount = 0;
            NetworkMotionCommand broadcast = default;
            motion.OnBroadcastCommand += command =>
            {
                broadcast = command;
                broadcastCount++;
            };

            motion.MoveToDirection(Vector3.zero, Space.World, 9);
            motion.StopToDirection(9);
            Assert.That(
                broadcastCount,
                Is.Zero,
                "A passive server proxy must not publish its missing local input over the owner direction");

            NetworkMotionCommand ownerCommand = NetworkMotionCommand.CreateMoveToDirection(
                Vector3.right,
                true,
                9,
                77);
            NetworkMotionResult result = (NetworkMotionResult)InvokePrivateResult(
                motion,
                "ProcessValidatedClientCommand",
                ownerCommand);

            Assert.That(result.approved, Is.True);
            Assert.That(broadcastCount, Is.EqualTo(1));
            Assert.That(broadcast.sequenceNumber, Is.EqualTo(77));
            Assert.That(broadcast.GetVelocity(), Is.EqualTo(Vector3.right));
        }

        [Test]
        public async Task FreeClimbAnimationOverride_MapsAllBlockedEdgesToIntentPlane()
        {
            GameObject characterObject = Track(new GameObject("Free Climb Edge Animation Character"));
            Character character = characterObject.AddComponent<Character>();
            NetworkCharacter networkCharacter = characterObject.AddComponent<NetworkCharacter>();
            networkCharacter.SetManualNetworkId(903);

            TraverseInteractive freeClimb = CreateInteractive("Free Climb Edge Traverse");
            MotionInteractive motion = Track(ScriptableObject.CreateInstance<MotionInteractive>());
            motion.name = "Motion_Free_Climb";
            SetPrivateField(freeClimb, "m_Motion", motion);
            SetPrivateField(freeClimb, "m_PositionA", -2f);
            SetPrivateField(freeClimb, "m_PositionB", 2f);
            SetPrivateField(freeClimb, "m_Width", 4f);

            TraversalStance stance = character.Combat.RequestStance<TraversalStance>();
            MethodInfo enterMethod = typeof(TraversalStance).GetMethod(
                "OnTraverseEnter",
                BindingFlags.Instance | BindingFlags.NonPublic);
            MethodInfo exitMethod = typeof(TraversalStance).GetMethod(
                "OnTraverseExit",
                BindingFlags.Instance | BindingFlags.NonPublic);
            PropertyInfo relativePosition = typeof(TraversalStance).GetProperty(
                "RelativePosition",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(enterMethod, Is.Not.Null);
            Assert.That(exitMethod, Is.Not.Null);
            Assert.That(relativePosition, Is.Not.Null);

            TraversalToken token = await (Task<TraversalToken>)enterMethod.Invoke(
                stance,
                new object[] { freeClimb });
            GameObject hooksObject = Track(new GameObject("Free Climb Edge Hooks"));
            NetworkTraversalPatchHooks hooks = hooksObject.AddComponent<NetworkTraversalPatchHooks>();

            (Vector3 position, Vector3 input, Vector3 expectedIntent)[] cases =
            {
                (new Vector3(-2f, 0f, 0f), Vector3.left, Vector3.left),
                (new Vector3(2f, 0f, 0f), Vector3.right, Vector3.right),
                (new Vector3(0f, 0f, -2f), Vector3.back, Vector3.down),
                (new Vector3(0f, 0f, 2f), Vector3.forward, Vector3.up)
            };

            foreach ((Vector3 position, Vector3 input, Vector3 expectedIntent) in cases)
            {
                relativePosition.SetValue(stance, position);
                object[] arguments =
                {
                    character,
                    input,
                    input,
                    input
                };
                bool overridden = (bool)InvokePrivateResult(
                    hooks,
                    "ApplyTraversalAnimationInputOverride",
                    arguments);
                Assert.That(overridden, Is.True, $"Expected blocked edge override at {position}");
                Assert.That((Vector3)arguments[2], Is.EqualTo(Vector3.zero));
                Assert.That((Vector3)arguments[1], Is.EqualTo(expectedIntent));
            }

            exitMethod.Invoke(stance, new object[] { freeClimb, token });
        }

        [Test]
        public void FocusedClimbDiagnostics_CanStillFollowTraversalNetworkLoggingWhenForceIsDisabled()
        {
            bool previousForce = NetworkTraversalDebug.ForceClimbDiagnostics;
            NetworkTraversalDebug.ForceClimbDiagnostics = false;
            try
            {
                NetworkTraversalManager manager = CreateManager();
                SetPrivateField(manager, "m_LogNetworkMessages", false);
                Assert.That(manager.DiagnosticsEnabled, Is.False);

                SetPrivateField(manager, "m_LogNetworkMessages", true);
                Assert.That(manager.DiagnosticsEnabled, Is.True);
            }
            finally
            {
                NetworkTraversalDebug.ForceClimbDiagnostics = previousForce;
            }
        }

        [Test]
        public async Task DisablingController_InvalidatesPendingAuthoritativeReplacement()
        {
            NetworkTraversalController controller = CreateRemoteController(
                121,
                out _,
                out TraversalStance stance);
            TraverseInteractive active = CreateInteractive("Disable Active Traversal");
            TraverseInteractive replacement = CreateInteractive("Disable Replacement Traversal");
            SetPrivateField(
                active,
                "m_Motion",
                Track(ScriptableObject.CreateInstance<MotionInteractive>()));
            SetPrivateField(
                replacement,
                "m_Motion",
                Track(ScriptableObject.CreateInstance<MotionInteractive>()));

            MethodInfo enterMethod = typeof(TraversalStance).GetMethod(
                "OnTraverseEnter",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(enterMethod, Is.Not.Null);
            await (Task<TraversalToken>)enterMethod.Invoke(stance, new object[] { active });

            string replacementId = BuildTraverseId(replacement);
            var apply = (Task<bool>)InvokePrivateResult(
                controller,
                "BeginOrJoinClientAuthoritativeStateApply",
                TraversalActionType.EnterTraverseInteractive,
                true,
                StableHashUtility.GetStableHash(replacementId),
                replacementId,
                replacement,
                string.Empty,
                string.Empty,
                0u,
                0u,
                91u,
                21u,
                false,
                default(NetworkTraversalSnapshot));
            Assert.That(apply.IsCompleted, Is.False);

            controller.enabled = false;
            for (int i = 0; i < 20 && !apply.IsCompleted; i++)
            {
                await Task.Yield();
            }

            Assert.That(apply.IsCompleted, Is.True);
            Assert.That(await apply, Is.False);
            Assert.That(
                GetPrivateField<object>(controller, "m_ClientAuthoritativeStateApply"),
                Is.Null);
            Assert.That(stance.Traverse, Is.Not.SameAs(replacement));
        }

        private NetworkTraversalManager CreateManager()
        {
            m_ManagerObject = new GameObject("Traversal Replication Test Manager");
            return m_ManagerObject.AddComponent<NetworkTraversalManager>();
        }

        private NetworkTraversalController CreateRemoteController(
            uint networkId,
            out Character character,
            out TraversalStance stance)
        {
            return CreateController(
                networkId,
                isLocalClient: false,
                out character,
                out stance);
        }

        private NetworkTraversalController CreateController(
            uint networkId,
            bool isLocalClient,
            out Character character,
            out TraversalStance stance)
        {
            GameObject gameObject = Track(new GameObject($"Traversal Controller {networkId}"));
            character = gameObject.AddComponent<Character>();
            NetworkCharacter networkCharacter = gameObject.AddComponent<NetworkCharacter>();
            networkCharacter.SetManualNetworkId(networkId);
            NetworkTraversalController controller = gameObject.AddComponent<NetworkTraversalController>();
            controller.Initialize(false, isLocalClient);
            stance = character.Combat.RequestStance<TraversalStance>();
            Assert.That(stance, Is.Not.Null);
            return controller;
        }

        private NetworkTraversalController CreateHostController(
            uint networkId,
            NetworkPredictionBackend predictionBackend,
            bool hostUsesClientPrediction = false)
        {
            GameObject gameObject = Track(new GameObject($"Traversal Host Controller {networkId}"));
            Character character = gameObject.AddComponent<Character>();
            NetworkCharacter networkCharacter = gameObject.AddComponent<NetworkCharacter>();
            SetPrivateField(networkCharacter, "m_PredictionBackend", predictionBackend);
            SetPrivateField(networkCharacter, "m_HostOwnerUsesClientPrediction", hostUsesClientPrediction);
            networkCharacter.SetManualNetworkId(networkId);
            NetworkTraversalController controller = gameObject.AddComponent<NetworkTraversalController>();

            networkCharacter.InitializeNetworkRole(isServer: true, isOwner: true, isHost: true);
            controller.Initialize(isServer: true, isLocalClient: true);
            Assert.That(character.Combat.RequestStance<TraversalStance>(), Is.Not.Null);
            return controller;
        }

        private TraverseInteractive CreateInteractive(string name)
        {
            return Track(new GameObject(name)).AddComponent<TraverseInteractive>();
        }

        private TraverseLink CreateLink(string name)
        {
            return Track(new GameObject(name)).AddComponent<TraverseLink>();
        }

        private NetworkTraversalSnapshot CreateActiveInteractiveSnapshot(
            NetworkTraversalController controller,
            TraverseInteractive traverse,
            uint stateVersion)
        {
            string traverseId = BuildTraverseId(traverse);
            return new NetworkTraversalSnapshot
            {
                NetworkId = controller.NetworkId,
                ServerTime = stateVersion,
                IsTraversing = true,
                TraverseHash = StableHashUtility.GetStableHash(traverseId),
                TraverseIdString = traverseId,
                StateVersion = stateVersion,
                Kind = TraversalSnapshotKind.ActiveInteractive,
                HasRelativePose = true,
                RelativePosition = Vector3.zero,
                RelativeRotation = Quaternion.identity
            };
        }

        private static NetworkTraversalRequest CreateStartRequest(
            NetworkTraversalController controller,
            Traverse traverse,
            uint correlationId)
        {
            string traverseId = BuildTraverseId(traverse);
            return new NetworkTraversalRequest
            {
                RequestId = (ushort)Mathf.Clamp((int)correlationId, 1, ushort.MaxValue),
                ActorNetworkId = controller.NetworkId,
                TargetNetworkId = controller.NetworkId,
                CorrelationId = correlationId,
                Action = traverse is TraverseLink
                    ? TraversalActionType.RunTraverseLink
                    : TraversalActionType.EnterTraverseInteractive,
                TraverseHash = StableHashUtility.GetStableHash(traverseId),
                TraverseIdString = traverseId
            };
        }

        private static string BuildTraverseId(Traverse traverse)
        {
            return (string)InvokePrivateStaticResult(
                typeof(NetworkTraversalController),
                "BuildTraverseId",
                traverse);
        }

        private static bool TryResolveTraverseForRequest(
            NetworkTraversalController controller,
            NetworkTraversalRequest request,
            out TraversalRejectionReason rejection)
        {
            MethodInfo method = typeof(NetworkTraversalController).GetMethod(
                "TryResolveTraverseForRequest",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            object[] arguments = { request, null, TraversalRejectionReason.None };
            bool resolved = (bool)method.Invoke(controller, arguments);
            rejection = (TraversalRejectionReason)arguments[2];
            return resolved;
        }

        private static NetworkTraversalResponse RoundTrip(NetworkTraversalResponse value)
        {
            using BitPacker packer = BitPackerPool.Get();
            PurrNetTraversalValuePackers.Write(packer, value);
            packer.ResetPositionAndMode(true);
            NetworkTraversalResponse result = default;
            PurrNetTraversalValuePackers.Read(packer, ref result);
            return result;
        }

        private static NetworkTraversalBroadcast RoundTrip(NetworkTraversalBroadcast value)
        {
            using BitPacker packer = BitPackerPool.Get();
            PurrNetTraversalValuePackers.Write(packer, value);
            packer.ResetPositionAndMode(true);
            NetworkTraversalBroadcast result = default;
            PurrNetTraversalValuePackers.Read(packer, ref result);
            return result;
        }

        private static NetworkTraversalSnapshot RoundTrip(NetworkTraversalSnapshot value)
        {
            using BitPacker packer = BitPackerPool.Get();
            PurrNetTraversalValuePackers.Write(packer, value);
            packer.ResetPositionAndMode(true);
            NetworkTraversalSnapshot result = default;
            PurrNetTraversalValuePackers.Read(packer, ref result);
            return result;
        }

        private static T GetPrivateField<T>(object instance, string fieldName)
        {
            FieldInfo field = FindField(instance.GetType(), fieldName);
            Assert.That(field, Is.Not.Null, $"Missing field {fieldName}");
            return (T)field.GetValue(instance);
        }

        private static void SetPrivateField(object instance, string fieldName, object value)
        {
            FieldInfo field = FindField(instance.GetType(), fieldName);
            Assert.That(field, Is.Not.Null, $"Missing field {fieldName}");
            field.SetValue(instance, value);
        }

        private static void InvokePrivate(object instance, string methodName)
        {
            MethodInfo method = instance.GetType().GetMethod(
                methodName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null, $"Missing method {methodName}");
            method.Invoke(instance, null);
        }

        private static object InvokePrivateResult(
            object instance,
            string methodName,
            params object[] arguments)
        {
            MethodInfo method = instance.GetType().GetMethod(
                methodName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null, $"Missing method {methodName}");
            return method.Invoke(instance, arguments);
        }

        private static object InvokePrivateStaticResult(
            System.Type type,
            string methodName,
            params object[] arguments)
        {
            MethodInfo method = type.GetMethod(
                methodName,
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null, $"Missing method {methodName}");
            return method.Invoke(null, arguments);
        }

        private static FieldInfo FindField(System.Type type, string fieldName)
        {
            while (type != null)
            {
                FieldInfo field = type.GetField(
                    fieldName,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field != null) return field;
                type = type.BaseType;
            }

            return null;
        }

        private static object GetField(object instance, string fieldName)
        {
            FieldInfo field = FindField(instance.GetType(), fieldName);
            Assert.That(field, Is.Not.Null, $"Missing field {fieldName}");
            return field.GetValue(instance);
        }

        private static object GetProperty(object instance, string propertyName)
        {
            PropertyInfo property = instance.GetType().GetProperty(
                propertyName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(property, Is.Not.Null, $"Missing property {propertyName}");
            return property.GetValue(instance);
        }

        private static void SetField(object instance, string fieldName, object value)
        {
            FieldInfo field = FindField(instance.GetType(), fieldName);
            Assert.That(field, Is.Not.Null, $"Missing field {fieldName}");
            field.SetValue(instance, value);
        }

        private T Track<T>(T value) where T : UnityEngine.Object
        {
            m_Cleanup.Add(value);
            return value;
        }
    }
}
#endif
