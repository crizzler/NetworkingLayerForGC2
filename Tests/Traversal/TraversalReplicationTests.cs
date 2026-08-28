#if GC2_TRAVERSAL
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Arawn.GameCreator2.Networking;
using Arawn.GameCreator2.Networking.TestUtilities;
using Arawn.GameCreator2.Networking.Traversal.Transport.PurrNet;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.Traversal;
using GameCreator.Runtime.VisualScripting;
using NUnit.Framework;
using PurrNet.Packing;
using UnityEngine;
using UnityEngine.TestTools;

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
        [System.Serializable]
        private sealed class CountingInstruction : Instruction
        {
            public static int RunCount { get; private set; }

            public static void Reset()
            {
                RunCount = 0;
            }

            protected override Task Run(Args args)
            {
                RunCount++;
                return DefaultResult;
            }
        }

        [System.Serializable]
        private sealed class AuthenticatedRemoteOwnerPoseDriver :
            UnitDriverNetworkServer,
            INetworkAuthenticatedRemoteOwnerPoseContext
        {
            public bool IsApplyingAuthenticatedRemoteOwnerPose { get; set; }
        }

        private GameObject m_ManagerObject;
        private readonly List<UnityEngine.Object> m_Cleanup = new();

        [TearDown]
        public void TearDown()
        {
            bool previousIgnore = LogAssert.ignoreFailingMessages;
            LogAssert.ignoreFailingMessages = true;
            try
            {
                for (int i = m_Cleanup.Count - 1; i >= 0; i--)
                {
                    if (m_Cleanup[i] != null) Object.DestroyImmediate(m_Cleanup[i]);
                }

                if (m_ManagerObject != null) Object.DestroyImmediate(m_ManagerObject);
            }
            finally
            {
                LogAssert.ignoreFailingMessages = previousIgnore;
            }

            m_Cleanup.Clear();
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
        public async Task InteractiveSnapshot_ExactIdentityReplacesActiveAToB()
        {
            NetworkTraversalController controller = CreateRemoteController(
                101,
                out Character character,
                out TraversalStance stance);
            TraverseInteractive traverseA = CreateInteractive("Snapshot Traverse A");
            TraverseInteractive traverseB = CreateInteractive("Snapshot Traverse B");

            int enterCount = 0;
            int exitCount = 0;
            stance.EventMotionEnter += () => enterCount++;
            stance.EventMotionExit += () => exitCount++;

            controller.ReceiveFullSnapshot(CreateActiveInteractiveSnapshot(controller, traverseA, 1));
            await WaitForClientApplyToSettle(controller);
            Assert.That(stance.Traverse, Is.SameAs(traverseA));

            Vector3 transportRoot = new Vector3(8f, 3f, -4f);
            character.transform.position = transportRoot;
            NetworkTraversalSnapshot replacementSnapshot = CreateActiveInteractiveSnapshot(
                controller,
                traverseB,
                2);
            replacementSnapshot.RelativePosition = new Vector3(0f, 0f, 0.75f);
            controller.ReceiveFullSnapshot(replacementSnapshot);
            await WaitForClientApplyToSettle(controller);

            Assert.That(stance.Traverse, Is.SameAs(traverseB));
            Assert.That(
                GetProperty(stance, "RelativePosition"),
                Is.EqualTo(replacementSnapshot.RelativePosition),
                "The traversal snapshot must still converge the observer's semantic ledge pose");
            Assert.That(
                character.transform.position,
                Is.EqualTo(transportRoot),
                "A remote traversal snapshot must not teleport the root owned by movement replication");
            Assert.That(
                character.Gestures.IsPlaying,
                Is.False,
                "A late/persistent snapshot must not replay an historical connection gesture");
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
                await WaitForClientApplyToSettle(controller);
                Assert.That(GetPrivateField<uint>(controller, "m_LastAppliedStateVersion"), Is.EqualTo(1));
            }
            finally
            {
                TraversalStance.NetworkForceCancelValidator = previousForceCancelValidator;
            }
        }

        [Test]
        public async Task ActiveLinkSnapshot_CancelsStaleInteractiveWithoutReplayingLink()
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
            await WaitForClientApplyToSettle(controller);
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
        public async Task UnresolvedSnapshot_SameVersionRemainsRetryableUntilTargetSpawns()
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
            await WaitForClientApplyToSettle(controller);

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
        public async Task FaultedAuthoritativeTraversal_ClearsItsExactHalfEnteredStance()
        {
            NetworkTraversalController controller = CreateRemoteController(
                118,
                out _,
                out TraversalStance stance);
            TraverseInteractive failedTraverse = CreateInteractive("Faulted Authoritative Traverse");

            MethodInfo enterMethod = typeof(TraversalStance).GetMethod(
                "OnTraverseEnter",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(enterMethod, Is.Not.Null);
            TraversalToken failedToken = await (Task<TraversalToken>)enterMethod.Invoke(
                stance,
                new object[] { failedTraverse });
            Assert.That(stance.Traverse, Is.SameAs(failedTraverse));

            bool recovered = (bool)InvokePrivateResult(
                controller,
                "TryRecoverFailedAuthoritativeTraversal",
                stance,
                failedTraverse,
                failedToken,
                TraversalActionType.EnterTraverseInteractive);

            Assert.That(recovered, Is.True);
            Assert.That(failedToken.IsCancelled, Is.True);
            Assert.That(stance.Traverse, Is.Null);
            Assert.That(stance.NetworkSnapshotToken, Is.Null);
            Assert.That((bool)GetProperty(stance, "AllowMovement"), Is.True);
        }

        [Test]
        public async Task FaultedAuthoritativeTraversal_StaleTaskCannotClearNewerTraverse()
        {
            NetworkTraversalController controller = CreateRemoteController(
                119,
                out _,
                out TraversalStance stance);
            TraverseInteractive staleTraverse = CreateInteractive("Stale Faulted Traverse");
            TraverseInteractive currentTraverse = CreateInteractive("Current Authoritative Traverse");

            MethodInfo enterMethod = typeof(TraversalStance).GetMethod(
                "OnTraverseEnter",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(enterMethod, Is.Not.Null);
            TraversalToken staleToken = await (Task<TraversalToken>)enterMethod.Invoke(
                stance,
                new object[] { staleTraverse });

            Assert.That(stance.NetworkClearSnapshot(), Is.True);
            Assert.That(stance.NetworkRestoreInteractiveSnapshot(currentTraverse, Vector3.zero), Is.True);
            TraversalToken currentToken = stance.NetworkSnapshotToken;

            bool recovered = (bool)InvokePrivateResult(
                controller,
                "TryRecoverFailedAuthoritativeTraversal",
                stance,
                staleTraverse,
                staleToken,
                TraversalActionType.EnterTraverseInteractive);

            Assert.That(recovered, Is.False);
            Assert.That(stance.Traverse, Is.SameAs(currentTraverse));
            Assert.That(stance.NetworkSnapshotToken, Is.SameAs(currentToken));
            Assert.That(currentToken.IsCancelled, Is.False);
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
        public async Task RemoteLiveInteractiveEnter_UsesSnapshotShellWithoutRunningGameplayMotion()
        {
            NetworkTraversalController controller = CreateRemoteController(
                124,
                out Character character,
                out TraversalStance stance);
            TraverseInteractive interactive = CreateInteractive("Remote Live Enter Traverse");
            MotionInteractive motion = Track(ScriptableObject.CreateInstance<MotionInteractive>());
            SetPrivateField(interactive, "m_Motion", motion);
            SetPrivateField(
                motion,
                "m_OnStart",
                new RunInstructionsList(new CountingInstruction()));

            CountingInstruction.Reset();
            try
            {
                controller.ReceiveTraversalChangeBroadcast(
                    CreateInteractiveEnterBroadcast(controller, interactive, 31, 3101));
                await WaitForClientApplyToSettle(controller);
                await Task.Yield();

                Assert.That(stance.Traverse, Is.SameAs(interactive));
                Assert.That(
                    (bool)GetProperty(stance, "AllowMovement"),
                    Is.False,
                    "An observer must not run local traversal input against an owner's interactive state");
                Assert.That(
                    GetPrivateField<bool>(controller, "m_IsSnapshotRestoredTraversal"),
                    Is.True,
                    "A live observer enter must use the same presentation shell as a late-join snapshot");
                Assert.That(stance.NetworkSnapshotToken, Is.Not.Null);
                Assert.That(stance.NetworkSnapshotToken.IsCancelled, Is.False);
                Assert.That(
                    character.Driver.UpdateKinematics,
                    Is.True,
                    "A RemoteClient must not start MotionInteractive's full update loop");
                Assert.That(
                    CountingInstruction.RunCount,
                    Is.Zero,
                    "A live observer enter must not execute MotionInteractive m_OnStart gameplay instructions");
                Assert.That(
                    GetPrivateField<uint>(controller, "m_LastAppliedStateVersion"),
                    Is.EqualTo(31));
            }
            finally
            {
                CountingInstruction.Reset();
            }
        }

        [Test]
        public async Task RemoteLiveInteractiveReplacement_PresentsAuthoredTransitionWithoutTeleport()
        {
            NetworkTraversalController controller = CreateRemoteController(
                127,
                out Character character,
                out TraversalStance stance);
            TraverseInteractive source = CreateInteractive("Remote Transition Source");
            TraverseInteractive target = CreateInteractive("Remote Transition Target");
            MotionInteractive sourceMotion = Track(
                ScriptableObject.CreateInstance<MotionInteractive>());
            MotionInteractive targetMotion = Track(
                ScriptableObject.CreateInstance<MotionInteractive>());
            SetPrivateField(sourceMotion, "m_Anchor", Anchor.Center);
            SetPrivateField(targetMotion, "m_Anchor", Anchor.Center);
            SetPrivateField(source, "m_Motion", sourceMotion);
            SetPrivateField(target, "m_Motion", targetMotion);
            target.transform.position = Vector3.right * 2f;

            AnimationClip exitClip = CreateTestClip("Remote Ledge Exit", 0.75f);
            AnimationClip enterClip = CreateTestClip("Remote Ledge Enter", 0.8f);
            SetAllTransitionClips(sourceMotion.m_ExitAnimations, exitClip);
            SetAllTransitionClips(targetMotion.m_EnterAnimations, enterClip);
            SetPrivateField(
                targetMotion,
                "m_OnStart",
                new RunInstructionsList(new CountingInstruction()));

            controller.ReceiveFullSnapshot(
                CreateActiveInteractiveSnapshot(controller, source, 30));
            await WaitForClientApplyToSettle(controller);
            Assert.That(stance.Traverse, Is.SameAs(source));

            Vector3 transportRoot = new Vector3(0f, 0.5f, 0f);
            character.transform.position = transportRoot;
            CountingInstruction.Reset();
            try
            {
                controller.ReceiveTraversalChangeBroadcast(
                    CreateInteractiveEnterBroadcast(controller, target, 31, 3102));
                await WaitForClientApplyToSettle(controller);

                Assert.That(stance.Traverse, Is.SameAs(target));
                Assert.That(
                    (bool)GetProperty(stance, "AllowMovement"),
                    Is.False,
                    "The connection presentation must remain an input-free observer shell");
                Assert.That(
                    character.Driver.UpdateKinematics,
                    Is.True,
                    "The observer must not start MotionInteractive's gameplay update loop");
                Assert.That(
                    Vector3.Distance(character.transform.position, transportRoot),
                    Is.LessThan(0.01f),
                    "Starting the presentation gesture must not write or teleport the remote root");
                Assert.That(CountingInstruction.RunCount, Is.Zero);
                Assert.That(
                    character.Gestures.IsPlaying,
                    Is.True,
                    "The live A-to-B observer replacement must start its local transition gesture");
                Assert.That(HasActiveGestureClip(character, exitClip), Is.True);
                Assert.That(
                    HasActiveGestureClip(character, enterClip),
                    Is.False,
                    "The target enter gesture must not start before the authored exit overlap");
                Assert.That(
                    await WaitForActiveGestureClip(character, enterClip, 2f),
                    Is.True,
                    "The target enter gesture must start after the source exit gesture");

                NetworkTraversalSnapshot matchingSnapshot = CreateActiveInteractiveSnapshot(
                    controller,
                    target,
                    31);
                matchingSnapshot.RelativePosition = new Vector3(0f, 0f, 0.5f);
                controller.ReceiveFullSnapshot(matchingSnapshot);
                await Task.Yield();

                Assert.That(
                    GetProperty(stance, "RelativePosition"),
                    Is.EqualTo(matchingSnapshot.RelativePosition),
                    "The matching snapshot still updates semantic traversal state");
                Assert.That(
                    Vector3.Distance(character.transform.position, transportRoot),
                    Is.LessThan(0.01f),
                    "The matching full snapshot must leave Fusion/PurrNet root interpolation intact");
            }
            finally
            {
                CountingInstruction.Reset();
            }
        }

        [Test]
        public async Task RemoteSnapshotFirstReplacement_DefersGestureUntilMatchingLiveBroadcast()
        {
            NetworkTraversalController controller = CreateRemoteController(
                128,
                out Character character,
                out TraversalStance stance);
            TraverseInteractive source = CreateInteractive("Snapshot First Source");
            TraverseInteractive target = CreateInteractive("Snapshot First Target");
            MotionInteractive sourceMotion = Track(
                ScriptableObject.CreateInstance<MotionInteractive>());
            MotionInteractive targetMotion = Track(
                ScriptableObject.CreateInstance<MotionInteractive>());
            SetPrivateField(sourceMotion, "m_Anchor", Anchor.Center);
            SetPrivateField(targetMotion, "m_Anchor", Anchor.Center);
            SetPrivateField(source, "m_Motion", sourceMotion);
            SetPrivateField(target, "m_Motion", targetMotion);
            target.transform.position = Vector3.up * 2f;

            AnimationClip exitClip = CreateTestClip("Snapshot First Exit", 0.5f);
            AnimationClip enterClip = CreateTestClip("Snapshot First Enter", 0.65f);
            SetAllTransitionClips(sourceMotion.m_ExitAnimations, exitClip);
            SetAllTransitionClips(targetMotion.m_EnterAnimations, enterClip);

            controller.ReceiveFullSnapshot(
                CreateActiveInteractiveSnapshot(controller, source, 40));
            await WaitForClientApplyToSettle(controller);

            Vector3 transportRoot = new Vector3(0f, 0.5f, 0f);
            character.transform.position = transportRoot;
            controller.ReceiveFullSnapshot(
                CreateActiveInteractiveSnapshot(controller, target, 41));
            await WaitForClientApplyToSettle(controller);

            Assert.That(stance.Traverse, Is.SameAs(target));
            Assert.That(
                Vector3.Distance(character.transform.position, transportRoot),
                Is.LessThan(0.01f));
            Assert.That(
                character.Gestures.IsPlaying,
                Is.False,
                "A persistent snapshot alone must not replay an historical transition");

            controller.ReceiveTraversalChangeBroadcast(
                CreateInteractiveEnterBroadcast(controller, target, 41, 4101));
            await Task.Yield();

            Assert.That(
                HasActiveGestureClip(character, exitClip),
                Is.True,
                "The matching live broadcast must recover the deferred source exit gesture");
            Assert.That(
                HasActiveGestureClip(character, enterClip),
                Is.False,
                "The deferred target enter gesture must preserve source-before-target ordering");
            Assert.That(
                await WaitForActiveGestureClip(character, enterClip, 2f),
                Is.True,
                "The matching live broadcast must recover the deferred target enter gesture");
            Assert.That(
                Vector3.Distance(character.transform.position, transportRoot),
                Is.LessThan(0.01f));
        }

        [Test]
        public async Task RemoteLiveEnterAndSnapshotFirst_ConvergeToEquivalentPresentationState()
        {
            NetworkTraversalController liveFirstController = CreateRemoteController(
                125,
                out Character liveFirstCharacter,
                out TraversalStance liveFirstStance);
            NetworkTraversalController snapshotFirstController = CreateRemoteController(
                126,
                out Character snapshotFirstCharacter,
                out TraversalStance snapshotFirstStance);
            TraverseInteractive interactive = CreateInteractive("Remote Enter Ordering Traverse");
            MotionInteractive motion = Track(ScriptableObject.CreateInstance<MotionInteractive>());
            SetPrivateField(interactive, "m_Motion", motion);

            const uint stateVersion = 41;
            NetworkTraversalSnapshot liveFirstSnapshot = CreateActiveInteractiveSnapshot(
                liveFirstController,
                interactive,
                stateVersion);
            NetworkTraversalSnapshot snapshotFirstSnapshot = CreateActiveInteractiveSnapshot(
                snapshotFirstController,
                interactive,
                stateVersion);
            Vector3 authoritativeRelativePosition = new Vector3(0f, 0f, 0.75f);
            liveFirstSnapshot.RelativePosition = authoritativeRelativePosition;
            snapshotFirstSnapshot.RelativePosition = authoritativeRelativePosition;

            liveFirstController.ReceiveTraversalChangeBroadcast(
                CreateInteractiveEnterBroadcast(
                    liveFirstController,
                    interactive,
                    stateVersion,
                    4101));
            await WaitForClientApplyToSettle(liveFirstController);
            liveFirstController.ReceiveFullSnapshot(liveFirstSnapshot);

            snapshotFirstController.ReceiveFullSnapshot(snapshotFirstSnapshot);
            await WaitForClientApplyToSettle(snapshotFirstController);
            snapshotFirstController.ReceiveTraversalChangeBroadcast(
                CreateInteractiveEnterBroadcast(
                    snapshotFirstController,
                    interactive,
                    stateVersion,
                    4102));
            await Task.Yield();

            Assert.That(liveFirstStance.Traverse, Is.SameAs(interactive));
            Assert.That(snapshotFirstStance.Traverse, Is.SameAs(interactive));
            Assert.That(
                GetProperty(liveFirstStance, "RelativePosition"),
                Is.EqualTo(GetProperty(snapshotFirstStance, "RelativePosition")));
            Assert.That(
                GetProperty(liveFirstStance, "RelativePosition"),
                Is.EqualTo(authoritativeRelativePosition));
            Assert.That(
                (bool)GetProperty(liveFirstStance, "AllowMovement"),
                Is.False);
            Assert.That(
                (bool)GetProperty(snapshotFirstStance, "AllowMovement"),
                Is.False);
            Assert.That(
                GetPrivateField<bool>(liveFirstController, "m_IsSnapshotRestoredTraversal"),
                Is.True);
            Assert.That(
                GetPrivateField<bool>(snapshotFirstController, "m_IsSnapshotRestoredTraversal"),
                Is.True);
            Assert.That(
                GetPrivateField<uint>(liveFirstController, "m_LastAppliedStateVersion"),
                Is.EqualTo(stateVersion));
            Assert.That(
                GetPrivateField<uint>(snapshotFirstController, "m_LastAppliedStateVersion"),
                Is.EqualTo(stateVersion));
            Assert.That(liveFirstStance.NetworkSnapshotToken, Is.Not.Null);
            Assert.That(snapshotFirstStance.NetworkSnapshotToken, Is.Not.Null);
            Assert.That(liveFirstStance.NetworkSnapshotToken.IsCancelled, Is.False);
            Assert.That(snapshotFirstStance.NetworkSnapshotToken.IsCancelled, Is.False);
            Assert.That(liveFirstCharacter.Driver.UpdateKinematics, Is.True);
            Assert.That(snapshotFirstCharacter.Driver.UpdateKinematics, Is.True);
        }

        [Test]
        public async Task RepeatedInteractiveSnapshot_DoesNotPullActiveLocalOwnerBackToStalePose()
        {
            NetworkTraversalController controller = CreateController(
                123,
                isLocalClient: true,
                out Character character,
                out TraversalStance stance);
            controller.Initialize(isServer: false, isLocalClient: true);

            TraverseInteractive interactive = CreateInteractive("Owner Repeated Snapshot Traverse");
            MotionInteractive motion = Track(ScriptableObject.CreateInstance<MotionInteractive>());
            SetPrivateField(interactive, "m_Motion", motion);

            NetworkTraversalSnapshot snapshot = CreateActiveInteractiveSnapshot(
                controller,
                interactive,
                8);
            controller.ReceiveFullSnapshot(snapshot);
            Assert.That(stance.Traverse, Is.SameAs(interactive));

            for (int i = 0;
                 i < 20 && GetPrivateField<object>(controller, "m_ClientAuthoritativeStateApply") != null;
                 i++)
            {
                await Task.Yield();
            }
            Assert.That(
                GetPrivateField<object>(controller, "m_ClientAuthoritativeStateApply"),
                Is.Null,
                "The initial authoritative entry must finish before exercising routine snapshots");

            PropertyInfo relativePosition = typeof(TraversalStance).GetProperty(
                "RelativePosition",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(relativePosition, Is.Not.Null);

            Vector3 liveOwnerRelative = new Vector3(0f, 0f, 0.925f);
            Vector3 liveOwnerRoot = new Vector3(4f, 5f, 6f);
            relativePosition.SetValue(stance, liveOwnerRelative);
            character.transform.position = liveOwnerRoot;

            snapshot.ServerTime += 5f;
            snapshot.RelativePosition = Vector3.zero;
            controller.ReceiveFullSnapshot(snapshot);

            Assert.That(
                (Vector3)relativePosition.GetValue(stance),
                Is.EqualTo(liveOwnerRelative),
                "A routine same-version snapshot must not rewind the owner's authored traversal pose");
            Assert.That(
                character.transform.position,
                Is.EqualTo(liveOwnerRoot),
                "A routine same-version snapshot must not teleport the predicting owner");
        }

        [Test]
        public async Task NewerServerInteractiveSnapshot_CorrectsLocallyClearedState()
        {
            NetworkTraversalController controller = CreateRemoteController(
                111,
                out _,
                out TraversalStance stance);
            TraverseInteractive authoritative = CreateInteractive("Authoritative Reentry Traverse");

            controller.ReceiveFullSnapshot(
                CreateActiveInteractiveSnapshot(controller, authoritative, 1));
            await WaitForClientApplyToSettle(controller);
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
            await WaitForClientApplyToSettle(controller);
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
        public void PurrDictionBackendLabel_WithCapableHostDriver_IsAccepted()
        {
            NetworkTraversalManager manager = CreateManager();
            manager.OnSendTraversalRequest = _ => { };
            manager.OnResolveRequestRouteStatusForActor = actorNetworkId =>
                actorNetworkId == 114 ? TraversalRouteStatus.Ready : TraversalRouteStatus.ControllerNotReady;
            NetworkTraversalController controller = CreateHostController(
                114,
                NetworkPredictionBackend.PurrDiction,
                hostUsesClientPrediction: true);

            object[] arguments = { TraversalRouteStatus.Unknown };
            bool accepted = (bool)InvokePrivateResult(
                controller,
                "CanAcceptPatchedRequest",
                arguments);

            Assert.That(accepted, Is.True);
            Assert.That(arguments[0], Is.EqualTo(TraversalRouteStatus.Ready));
        }

        [Test]
        public async Task TrustedServerRequest_DoesNotRejectCapablePurrDictionLabel()
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

            Assert.That(
                response.RejectionReason,
                Is.Not.EqualTo(TraversalRejectionReason.UnsupportedPredictionBackend));
        }

        [Test]
        public void PurrDictionBackendLabel_WithoutMotionCapability_RemainsFailClosed()
        {
            NetworkTraversalController controller = CreateHostController(
                118,
                NetworkPredictionBackend.PurrDiction,
                hostUsesClientPrediction: true);
            Character character = controller.GetComponent<Character>();
            character.Kernel.ChangeDriver(character, new UnitDriverNetworkRemote());

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
            character.Kernel.ChangeDriver(character, new UnitDriverNetworkClient());
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
            Character character = EditModeLifecycle.AddComponent<Character>(gameObject);
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
        public void ClientPredictionReplay_PreservesPullUpAddPositionDisplacement()
        {
            AssertClientPredictionReplaysExternalTraversalDisplacement(useSetPosition: false);
        }

        [Test]
        public void ClientPredictionReplay_PreservesInteractiveSetPositionDisplacement()
        {
            AssertClientPredictionReplaysExternalTraversalDisplacement(useSetPosition: true);
        }

        private void AssertClientPredictionReplaysExternalTraversalDisplacement(
            bool useSetPosition)
        {
            GameObject gameObject = Track(new GameObject(
                useSetPosition
                    ? "Interactive SetPosition Prediction Replay"
                    : "PullUp AddPosition Prediction Replay"));
            Character character = EditModeLifecycle.AddComponent<Character>(gameObject);
            var driver = new UnitDriverNetworkClient();
            driver.OnStartup(character);

            try
            {
                driver.UpdateKinematics = false;

                // Sequence 0 is the server acknowledgement baseline.
                SetPrivateField(driver, "m_InputAccumulator", 1f);
                driver.ProcessLocalInput(Vector2.zero, null);
                System.Array initialHistory =
                    GetPrivateField<System.Array>(driver, "m_PredictionHistory");
                int historyStart = GetPrivateField<int>(driver, "m_PredictionHistoryStart");
                object baselineState = initialHistory.GetValue(historyStart);
                Vector3 baselinePosition = (Vector3)GetField(baselineState, "position");

                Vector3 beforeExternalWrite = gameObject.transform.position;
                if (useSetPosition)
                {
                    Vector3 targetRoot = beforeExternalWrite + new Vector3(0.75f, 0f, 0f);
                    float halfHeight = character.Motion.Height * 0.5f;
                    driver.SetPosition(targetRoot - Vector3.up * halfHeight);
                }
                else
                {
                    driver.AddPosition(new Vector3(0.75f, 0f, 0f));
                }

                Vector3 externalDelta = gameObject.transform.position - beforeExternalWrite;
                Assert.That(externalDelta.x, Is.GreaterThan(0.5f));

                // Sequence 1 captures the authored Traversal displacement. Sequence 2 proves
                // that consuming the pending delta clears it instead of replaying it twice.
                SetPrivateField(driver, "m_InputAccumulator", 1f);
                driver.ProcessLocalInput(Vector2.zero, null);
                SetPrivateField(driver, "m_InputAccumulator", 1f);
                driver.ProcessLocalInput(Vector2.zero, null);
                Assert.That(GetPrivateField<int>(driver, "m_PredictionHistoryCount"), Is.EqualTo(3));

                Vector3 correctedBaseline = baselinePosition + new Vector3(0.2f, 0f, 0f);
                NetworkPositionState serverState = NetworkPositionState.Create(
                    correctedBaseline,
                    gameObject.transform.eulerAngles.y,
                    0f,
                    lastInput: 0,
                    isGrounded: true,
                    isJumping: false,
                    moveVelocity: Vector3.zero);

                driver.ApplyServerState(serverState);

                Assert.That(
                    gameObject.transform.position.x,
                    Is.EqualTo(correctedBaseline.x + externalDelta.x).Within(0.015f),
                    "Reconciliation must replay the Traversal root delta exactly once");
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
            Character character = EditModeLifecycle.AddComponent<Character>(gameObject);
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
        public void ServerSimulation_SequencedTraversalDirectionSurvivesAClampedOwnerPose()
        {
            GameObject gameObject = Track(new GameObject("Sequenced Traversal Edge Intent"));
            Character character = EditModeLifecycle.AddComponent<Character>(gameObject);
            var motion = new UnitMotionNetworkController { IsServer = true };
            character.Kernel.ChangeMotion(character, motion);
            var driver = new UnitDriverNetworkServer();
            driver.OnStartup(character);

            try
            {
                driver.UpdateKinematics = false;
                Vector3 attemptedDirection = new Vector3(3.25f, 0f, -1.5f);
                NetworkInputState heldInput = NetworkInputState.Create(
                    Vector2.zero,
                    sequence: 1,
                    deltaTime: 0.05f,
                    ownerAuthorityPosition: gameObject.transform.position);
                heldInput.SetTraversalPresentationDirection(attemptedDirection);

                driver.QueueInput(heldInput);
                NetworkPositionState heldState = driver.ProcessInputs();

                Assert.That(
                    driver.WorldMoveDirection,
                    Is.EqualTo(heldInput.GetTraversalPresentationDirection()));
                Assert.That(motion.TryGetTraversalPresentationDirection(out Vector3 retained), Is.True);
                Assert.That(retained, Is.EqualTo(heldInput.GetTraversalPresentationDirection()));
                Assert.That(
                    heldState.GetMoveVelocity(),
                    Is.EqualTo(heldInput.GetTraversalPresentationDirection()),
                    "The regular authoritative state must carry attempted edge input to observers");

                NetworkInputState releasedInput = NetworkInputState.Create(
                    Vector2.zero,
                    sequence: 2,
                    deltaTime: 0.05f,
                    ownerAuthorityPosition: gameObject.transform.position);
                releasedInput.SetTraversalPresentationDirection(Vector3.zero);
                driver.QueueInput(releasedInput);
                NetworkPositionState releasedState = driver.ProcessInputs();

                Assert.That(driver.WorldMoveDirection, Is.EqualTo(Vector3.zero));
                Assert.That(motion.TryGetTraversalPresentationDirection(out _), Is.False);
                Assert.That(releasedState.GetMoveVelocity(), Is.EqualTo(Vector3.zero));
            }
            finally
            {
                driver.OnDispose(character);
            }
        }

        [TestCase(Anchor.Crown)]
        [TestCase(Anchor.Center)]
        [TestCase(Anchor.Feet)]
        public void OwnerAuthorityRelativePose_RoundTripsRootWithoutSkinWidthDrift(Anchor anchor)
        {
            const float skinWidth = 0.08f;
            GameObject characterObject = Track(new GameObject($"Owner Pose {anchor} Character"));
            Character character = EditModeLifecycle.AddComponent<Character>(characterObject);
            var driver = new UnitDriverNetworkServer();
            SetPrivateField(driver, "m_SkinWidth", skinWidth);
            SetPrivateField(character.Kernel, "m_Driver", driver);
            driver.OnStartup(character);
            InvokePrivateResult(character.Combat, "OnStartup", character);

            Assert.That(character.Driver.SkinWidth, Is.EqualTo(skinWidth).Within(0.0001f));

            TraverseInteractive interactive = CreateInteractive($"Owner Pose {anchor} Traverse");
            MotionInteractive motion = Track(ScriptableObject.CreateInstance<MotionInteractive>());
            SetPrivateField(motion, "m_Anchor", anchor);
            SetPrivateField(interactive, "m_Motion", motion);
            SetPrivateField(interactive, "m_Width", 4f);
            SetPrivateField(interactive, "m_PositionA", -2f);
            SetPrivateField(interactive, "m_PositionB", 2f);

            // Free-climb traversal uses local Z as its vertical axis. With an identity rotation,
            // the erroneous world-space skin offset would land on local Y and be hidden by the
            // traversal plane clamp, so preserve the production orientation in this regression.
            interactive.transform.SetPositionAndRotation(
                new Vector3(4f, 2f, -3f),
                Quaternion.Euler(-90f, 0f, 0f));

            Vector3 expectedRelative = new Vector3(0.5f, 0f, 1.25f);
            Vector3 expectedAnchor = interactive.Transform.TransformPoint(expectedRelative);
            float halfHeight = character.Motion.Height * 0.5f;
            Vector3 anchorOffset = anchor switch
            {
                Anchor.Crown => Vector3.up * halfHeight,
                Anchor.Center => Vector3.zero,
                Anchor.Feet => Vector3.down * halfHeight,
                _ => throw new System.ArgumentOutOfRangeException(nameof(anchor), anchor, null)
            };
            Vector3 ownerRoot = expectedAnchor - anchorOffset;
            character.transform.position = ownerRoot;
            Physics.SyncTransforms();

            TraversalStance stance = character.Combat.RequestStance<TraversalStance>();
            Assert.That(
                stance.NetworkRestoreInteractiveSnapshot(interactive, Vector3.zero),
                Is.True);

            InvokePrivateStaticResult(
                typeof(NetworkTraversalManager),
                "SyncTraversalRelativePositionFromOwnerAuthority",
                character,
                ownerRoot);

            Vector3 actualRelative = (Vector3)GetProperty(stance, "RelativePosition");
            Assert.That(
                Vector3.Distance(actualRelative, expectedRelative),
                Is.LessThan(0.0001f),
                "Accepted owner roots must not store CharacterPosition's skin-width offset");

            Vector3 driverOffset = anchor switch
            {
                Anchor.Crown => Vector3.down * character.Motion.Height,
                Anchor.Center => Vector3.down * halfHeight,
                Anchor.Feet => Vector3.zero,
                _ => throw new System.ArgumentOutOfRangeException(nameof(anchor), anchor, null)
            };
            character.Driver.SetPosition(
                interactive.Transform.TransformPoint(actualRelative) + driverOffset,
                teleport: true);

            Assert.That(
                Vector3.Distance(character.transform.position, ownerRoot),
                Is.LessThan(0.0001f),
                "The next server MotionInteractive update must not move the accepted owner root");
        }

        [TestCase(Anchor.Crown)]
        [TestCase(Anchor.Center)]
        [TestCase(Anchor.Feet)]
        public void OwnerAuthorityPose_AllowsAuthoredSurfaceAndRejectsOffSurfaceSpoofs(
            Anchor anchor)
        {
            CreateManager();
            GameObject characterObject = Track(new GameObject(
                $"Owner Surface Validation {anchor} Character"));
            Character character = EditModeLifecycle.AddComponent<Character>(characterObject);
            var driver = new UnitDriverNetworkServer();
            SetPrivateField(character.Kernel, "m_Driver", driver);
            driver.OnStartup(character);
            InvokePrivateResult(character.Combat, "OnStartup", character);

            TraverseInteractive interactive = CreateInteractive(
                $"Owner Surface Validation {anchor} Traverse");
            MotionInteractive motion = Track(ScriptableObject.CreateInstance<MotionInteractive>());
            SetPrivateField(motion, "m_Anchor", anchor);
            SetPrivateField(interactive, "m_Motion", motion);
            SetPrivateField(interactive, "m_Width", 2f);
            SetPrivateField(interactive, "m_PositionA", -1f);
            SetPrivateField(interactive, "m_PositionB", 1f);
            interactive.transform.SetPositionAndRotation(
                new Vector3(4f, 2f, -3f),
                Quaternion.Euler(-70f, 25f, 8f));
            interactive.transform.localScale = new Vector3(0.8f, 1.2f, 1.5f);

            TraversalStance stance = character.Combat.RequestStance<TraversalStance>();
            Assert.That(
                stance.NetworkRestoreInteractiveSnapshot(interactive, Vector3.zero),
                Is.True);

            float halfHeight = character.Motion.Height * 0.5f;
            Vector3 anchorOffset = anchor switch
            {
                Anchor.Crown => Vector3.up * halfHeight,
                Anchor.Center => Vector3.zero,
                Anchor.Feet => Vector3.down * halfHeight,
                _ => throw new System.ArgumentOutOfRangeException(nameof(anchor), anchor, null)
            };

            Vector3 ToOwnerRoot(Vector3 localAnchor)
            {
                return interactive.Transform.TransformPoint(localAnchor) - anchorOffset;
            }

            void AssertAllowed(Vector3 root)
            {
                NetworkInputState input = NetworkInputState.Create(
                    Vector2.zero,
                    sequence: 1,
                    deltaTime: 1f / 60f,
                    ownerAuthorityPosition: root);
                Vector3 quantizedRoot = input.GetOwnerAuthorityPosition();
                Assert.That(
                    NetworkOwnerMotionAuthorityHooks.TryGetPositionRejection(
                        character,
                        quantizedRoot,
                        out string rejection),
                    Is.False,
                    rejection);
                Assert.That(
                    NetworkOwnerMotionAuthorityHooks.TryGetExternalRootWriteAllowance(
                        character,
                        quantizedRoot,
                        out string allowance),
                    Is.True);
                StringAssert.StartsWith("traversal-interactive:", allowance);
            }

            void AssertRejected(Vector3 root)
            {
                NetworkInputState input = NetworkInputState.Create(
                    Vector2.zero,
                    sequence: 2,
                    deltaTime: 1f / 60f,
                    ownerAuthorityPosition: root);
                Vector3 quantizedRoot = input.GetOwnerAuthorityPosition();
                Assert.That(
                    NetworkOwnerMotionAuthorityHooks.TryGetPositionRejection(
                        character,
                        quantizedRoot,
                        out string rejection),
                    Is.True);
                StringAssert.StartsWith(
                    "traversal-interactive-off-surface:",
                    rejection);
                Assert.That(
                    NetworkOwnerMotionAuthorityHooks.TryGetExternalRootWriteAllowance(
                        character,
                        quantizedRoot,
                        out _),
                    Is.False,
                    "An off-surface owner pose must never reach the absolute-root branch");
            }

            foreach (Vector3 validLocalAnchor in new[]
                     {
                         new Vector3(0.4f, 0f, 0.95f),
                         new Vector3(-1f, 0f, -1f),
                         new Vector3(1f, 0f, 1f)
                     })
            {
                AssertAllowed(ToOwnerRoot(validLocalAnchor));
            }

            Vector3 validRailRoot = ToOwnerRoot(new Vector3(0.4f, 0f, 0.95f));
            AssertAllowed(validRailRoot + interactive.Transform.up * 0.01f);

            Vector3 offPlaneRoot = validRailRoot + interactive.Transform.up * 0.05f;
            Vector3 outsideWidthRoot = ToOwnerRoot(new Vector3(1.0625f, 0f, 0.5f));
            Vector3 outsideEndRoot = ToOwnerRoot(new Vector3(0f, 0f, 1.034f));
            foreach (Vector3 spoofedRoot in new[]
                     {
                         offPlaneRoot,
                         outsideWidthRoot,
                         outsideEndRoot
                     })
            {
                AssertRejected(spoofedRoot);
            }

            SetPrivateField(interactive, "m_Width", -1f);
            Assert.That(
                NetworkOwnerMotionAuthorityHooks.TryGetPositionRejection(
                    character,
                    validRailRoot,
                    out _),
                Is.True,
                "Malformed authored bounds must fail closed instead of widening authority");
        }

        [Test]
        public void OwnerAuthorityPose_HostInteractiveTransition_AcceptsSamplesAndPreservesDestination()
        {
            CreateManager();
            NetworkTraversalController controller = CreateHostController(
                904,
                NetworkPredictionBackend.BuiltIn);
            Character character = controller.GetComponent<Character>();
            TraversalStance stance = character.Combat.RequestStance<TraversalStance>();

            TraverseInteractive destination = CreateInteractive(
                "Host Transition Destination");
            MotionInteractive motion = Track(
                ScriptableObject.CreateInstance<MotionInteractive>());
            SetPrivateField(motion, "m_Anchor", Anchor.Feet);
            SetPrivateField(destination, "m_Motion", motion);
            SetPrivateField(destination, "m_Width", 2f);
            SetPrivateField(destination, "m_PositionA", -1f);
            SetPrivateField(destination, "m_PositionB", 1f);
            destination.transform.position = Vector3.up * 2f;

            Vector3 destinationRelative = new Vector3(0.25f, 0f, 0.5f);
            Assert.That(
                stance.NetworkRestoreInteractiveSnapshot(
                    destination,
                    destinationRelative),
                Is.True);
            SetTraversalTransitionState(stance, true);

            Vector3 intermediateRoot = character.transform.position + Vector3.up * 0.35f;
            Assert.That(
                NetworkOwnerMotionAuthorityHooks.TryGetPositionRejection(
                    character,
                    intermediateRoot,
                    out string rejection),
                Is.False,
                rejection);
            Assert.That(
                NetworkOwnerMotionAuthorityHooks.TryGetExternalRootWriteAllowance(
                    character,
                    intermediateRoot,
                    out string allowance),
                Is.True);
            StringAssert.StartsWith("traversal-interactive-transition:", allowance);

            NetworkOwnerMotionAuthorityHooks.NotifyPositionAccepted(
                character,
                intermediateRoot);
            Assert.That(
                GetTraversalRelativePosition(stance),
                Is.EqualTo(destinationRelative),
                "An accepted Host transition sample must not overwrite GC2's stored " +
                "destination pose while MotionInteractive is still easing toward it");
        }

        [Test]
        public void OwnerAuthorityPose_ConnectedClientInteractiveTransition_RemainsRejected()
        {
            CreateManager();
            GameObject characterObject = Track(new GameObject(
                "Connected Client Transition Server Replica"));
            Character character = EditModeLifecycle.AddComponent<Character>(characterObject);
            NetworkCharacter networkCharacter =
                EditModeLifecycle.AddComponent<NetworkCharacter>(characterObject);
            networkCharacter.SetManualNetworkId(905);
            NetworkTraversalController controller =
                EditModeLifecycle.AddComponent<NetworkTraversalController>(characterObject);
            EditModeLifecycle.InitializeNetworkRole(
                networkCharacter,
                isServer: true,
                isOwner: false,
                isHost: true,
                hasAuthenticatedPlayerOwner: true);
            controller.Initialize(isServer: true, isLocalClient: false);

            TraverseInteractive destination = CreateInteractive(
                "Connected Client Transition Destination");
            MotionInteractive motion = Track(
                ScriptableObject.CreateInstance<MotionInteractive>());
            SetPrivateField(motion, "m_Anchor", Anchor.Feet);
            SetPrivateField(destination, "m_Motion", motion);
            TraversalStance stance = character.Combat.RequestStance<TraversalStance>();
            Assert.That(
                stance.NetworkRestoreInteractiveSnapshot(destination, Vector3.zero),
                Is.True);
            SetTraversalTransitionState(stance, true);

            Assert.That(networkCharacter.IsServerInstance, Is.True);
            Assert.That(networkCharacter.IsOwnerInstance, Is.False);
            Assert.That(
                NetworkOwnerMotionAuthorityHooks.TryGetPositionRejection(
                    character,
                    character.transform.position + Vector3.up * 0.35f,
                    out string rejection),
                Is.True);
            StringAssert.StartsWith(
                "traversal-interactive-transition:",
                rejection,
                "A connected-client replica outside the authenticated Shared apply context " +
                "must not reuse the transition-pose path");
        }

        [Test]
        public void OwnerAuthorityPose_AuthenticatedSharedTransitionScope_AcceptsOnlyPlayerSampleAndPreservesDestination()
        {
            CreateManager();
            GameObject characterObject = Track(new GameObject(
                "Authenticated Shared Transition Server Replica"));
            Character character = EditModeLifecycle.AddComponent<Character>(characterObject);
            NetworkCharacter networkCharacter =
                EditModeLifecycle.AddComponent<NetworkCharacter>(characterObject);
            networkCharacter.SetManualNetworkId(907);
            NetworkTraversalController controller =
                EditModeLifecycle.AddComponent<NetworkTraversalController>(characterObject);
            EditModeLifecycle.InitializeNetworkRole(
                networkCharacter,
                isServer: true,
                isOwner: false,
                isHost: true,
                hasAuthenticatedPlayerOwner: true);
            controller.Initialize(isServer: true, isLocalClient: false);

            var driver = new AuthenticatedRemoteOwnerPoseDriver();
            SetPrivateField(character.Kernel, "m_Driver", driver);
            driver.OnStartup(character);

            SetPrivateField(
                networkCharacter,
                "m_ActorType",
                NetworkCharacterActorType.PlayerOwned);

            TraverseInteractive source = CreateInteractive(
                "Authenticated Shared Transition Source");
            MotionInteractive sourceMotion = Track(
                ScriptableObject.CreateInstance<MotionInteractive>());
            SetPrivateField(sourceMotion, "m_Anchor", Anchor.Feet);
            SetPrivateField(source, "m_Motion", sourceMotion);

            TraverseInteractive destination = CreateInteractive(
                "Authenticated Shared Transition Destination");
            MotionInteractive motion = Track(
                ScriptableObject.CreateInstance<MotionInteractive>());
            SetPrivateField(motion, "m_Anchor", Anchor.Feet);
            SetPrivateField(destination, "m_Motion", motion);
            SetPrivateField(destination, "m_Width", 2f);
            SetPrivateField(destination, "m_PositionA", -1f);
            SetPrivateField(destination, "m_PositionB", 1f);
            destination.transform.position = Vector3.up * 2f;

            TraversalStance stance = character.Combat.RequestStance<TraversalStance>();
            Vector3 destinationRelative = new Vector3(0.15f, 0f, 0.65f);
            Assert.That(
                stance.NetworkRestoreInteractiveSnapshot(
                    destination,
                    destinationRelative),
                Is.True);
            SetTraversalTransitionState(stance, true);

            Vector3 transitionStartRoot = character.transform.position;
            Vector3 transitionTargetRoot = transitionStartRoot +
                (destination.CalculateStartPosition(character) -
                 motion.CharacterPosition(character));
            Assert.That(networkCharacter.IsPlayerOwnedActor, Is.True);
            Assert.That(networkCharacter.HasAuthenticatedPlayerOwner, Is.True);
            Assert.That(character.Driver, Is.SameAs(driver));
            Assert.That(character.Driver, Is.AssignableTo<INetworkServerOwnerMotionAuthority>());
            Assert.That(
                Vector3.Distance(transitionStartRoot, transitionTargetRoot),
                Is.GreaterThan(Traverse.MIN_DISTANCE_TRANSITION));
            InvokePrivateResult(
                controller,
                "ArmServerRemoteInteractiveTransitionAuthorization",
                source,
                destination,
                4701u);
            InvokePrivateResult(controller, "OpenServerOwnerMotionWindow", 4701u);
            object authorization = GetPrivateField<object>(
                controller,
                "m_ServerRemoteInteractiveTransitionAuthorization");
            FieldInfo authorizationCorrelation = FindField(
                authorization.GetType(),
                "CorrelationId");
            FieldInfo authorizationStart = FindField(
                authorization.GetType(),
                "StartRootPosition");
            FieldInfo authorizationTarget = FindField(
                authorization.GetType(),
                "TargetRootPosition");
            Assert.That(
                (uint)authorizationCorrelation.GetValue(authorization),
                Is.EqualTo(4701u),
                "The transition corridor must arm for the authenticated remote player.");
            Assert.That(
                GetPrivateField<bool>(controller, "m_ServerOwnerMotionWindowOpen"),
                Is.True);
            Assert.That(
                GetPrivateField<uint>(controller, "m_ServerOwnerMotionOperationId"),
                Is.EqualTo(4701u));

            Vector3 intermediateRoot = Vector3.Lerp(
                transitionStartRoot,
                transitionTargetRoot,
                0.25f);
            Vector3 authorizedStart = (Vector3)authorizationStart.GetValue(authorization);
            Vector3 authorizedTarget = (Vector3)authorizationTarget.GetValue(authorization);
            Assert.That(
                (bool)InvokePrivateResult(
                    controller,
                    "AllowsAuthenticatedRemoteInteractiveTransitionPose",
                    destination,
                    intermediateRoot),
                Is.True,
                "The synthetic server fixture must have the same correlated corridor and " +
                "owner-motion window that the real Shared request opens before testing the " +
                $"transport-owned apply scope. expected={transitionStartRoot:F3}->" +
                $"{transitionTargetRoot:F3} authorized={authorizedStart:F3}->" +
                $"{authorizedTarget:F3} sample={intermediateRoot:F3}");
            Assert.That(
                NetworkOwnerMotionAuthorityHooks.TryGetPositionRejection(
                    character,
                    intermediateRoot,
                    out _),
                Is.True,
                "A server replica has no transition authority outside the transport-owned scope");

            driver.IsApplyingAuthenticatedRemoteOwnerPose = true;
            Assert.That(
                NetworkOwnerMotionAuthorityHooks.TryGetPositionRejection(
                    character,
                    intermediateRoot,
                    out string scopedRejection),
                Is.False,
                scopedRejection);
            Assert.That(
                NetworkOwnerMotionAuthorityHooks.TryGetExternalRootWriteAllowance(
                    character,
                    intermediateRoot,
                    out string scopedAllowance),
                Is.True);
            StringAssert.StartsWith(
                "traversal-interactive-transition:",
                scopedAllowance);
            NetworkOwnerMotionAuthorityHooks.NotifyPositionAccepted(
                character,
                intermediateRoot);
            Assert.That(
                GetTraversalRelativePosition(stance),
                Is.EqualTo(destinationRelative),
                "The Shared master's accepted intermediate pose must not replace GC2's " +
                "authored destination while the transition is easing");

            SetPrivateField(
                networkCharacter,
                "m_ActorType",
                NetworkCharacterActorType.NPC);
            Assert.That(
                NetworkOwnerMotionAuthorityHooks.TryGetPositionRejection(
                    character,
                    intermediateRoot,
                    out _),
                Is.True,
                "Even an asserted transport scope must not authorize an explicitly classified NPC");

            SetPrivateField(
                networkCharacter,
                "m_ActorType",
                NetworkCharacterActorType.PlayerOwned);
            SetPrivateField(
                networkCharacter,
                "m_RuntimeHasAuthenticatedPlayerOwner",
                false);
            Assert.That(
                NetworkOwnerMotionAuthorityHooks.TryGetPositionRejection(
                    character,
                    intermediateRoot,
                    out _),
                Is.True,
                "The scoped context must not authorize an unauthenticated player object");

            SetPrivateField(
                networkCharacter,
                "m_RuntimeHasAuthenticatedPlayerOwner",
                true);

            Vector3 onSurfaceOutsideCorridor =
                transitionTargetRoot + Vector3.right * 0.5f;
            Assert.That(
                NetworkOwnerMotionAuthorityHooks.TryGetPositionRejection(
                    character,
                    onSurfaceOutsideCorridor,
                    out _),
                Is.True);
            Assert.That(
                NetworkOwnerMotionAuthorityHooks.TryGetExternalRootWriteAllowance(
                    character,
                    onSurfaceOutsideCorridor,
                    out _),
                Is.False,
                "Even a geometrically valid target-surface pose must not bypass the exact " +
                "operation corridor through the allowance hook while the transition is active");
            SetTraversalTransitionState(stance, false);

            Vector3 delayedTransitionRoot = Vector3.Lerp(
                transitionStartRoot,
                transitionTargetRoot,
                0.75f);
            Assert.That(
                NetworkOwnerMotionAuthorityHooks.TryGetPositionRejection(
                    character,
                    transitionTargetRoot,
                    out string finalPreflightRejection),
                Is.False,
                finalPreflightRejection);
            Assert.That(
                (bool)InvokePrivateResult(
                    controller,
                    "AllowsAuthenticatedRemoteInteractiveTransitionPose",
                    destination,
                    delayedTransitionRoot),
                Is.True,
                "A merely preflighted endpoint must not consume the operation before the " +
                "movement backend actually accepts it");
            Assert.That(
                NetworkOwnerMotionAuthorityHooks.TryGetPositionRejection(
                    character,
                    delayedTransitionRoot,
                    out string delayedRejection),
                Is.False,
                delayedRejection);
            Assert.That(
                NetworkOwnerMotionAuthorityHooks.TryGetExternalRootWriteAllowance(
                    character,
                    delayedTransitionRoot,
                    out string delayedAllowance),
                Is.True);
            StringAssert.StartsWith(
                "traversal-interactive-transition:",
                delayedAllowance,
                "A latency-delayed pose from the correlated Shared transition must retain " +
                "its absolute-root semantics after the authority's local GC2 flag clears");
            NetworkOwnerMotionAuthorityHooks.NotifyPositionAccepted(
                character,
                delayedTransitionRoot);
            Assert.That(
                GetTraversalRelativePosition(stance),
                Is.EqualTo(destinationRelative),
                "A delayed intermediate Shared pose must not replace the stored destination");

            Assert.That(
                NetworkOwnerMotionAuthorityHooks.TryGetPositionRejection(
                    character,
                    transitionTargetRoot,
                    out string finalAcceptedRejection),
                Is.False,
                finalAcceptedRejection);
            NetworkOwnerMotionAuthorityHooks.NotifyPositionAccepted(
                character,
                transitionTargetRoot);
            Assert.That(
                (bool)InvokePrivateResult(
                    controller,
                    "AllowsAuthenticatedRemoteInteractiveTransitionPose",
                    destination,
                    transitionTargetRoot),
                Is.False,
                "The correlated corridor must close only after the backend commits its endpoint");

            Assert.That(
                NetworkOwnerMotionAuthorityHooks.TryGetPositionRejection(
                    character,
                    delayedTransitionRoot + Vector3.right * 0.5f,
                    out string corridorRejection),
                Is.True,
                "The correlated operation must authorize only the authored transition corridor");
            StringAssert.StartsWith(
                "traversal-interactive-off-surface:",
                corridorRejection);

            Assert.That(
                NetworkOwnerMotionAuthorityHooks.TryGetPositionRejection(
                    character,
                    character.transform.position + Vector3.one * 50f,
                    out string offSurfaceRejection),
                Is.True,
                "The Shared scope must not become a general off-surface teleport path");
            StringAssert.StartsWith(
                "traversal-interactive-off-surface:",
                offSurfaceRejection);

            SetTraversalTransitionState(stance, true);
            driver.IsApplyingAuthenticatedRemoteOwnerPose = false;
            Assert.That(
                NetworkOwnerMotionAuthorityHooks.TryGetPositionRejection(
                    character,
                    intermediateRoot,
                    out _),
                Is.True,
                "The authenticated Shared allowance must disappear immediately after the sample");
        }

        [Test]
        public void ServerExitSnapshot_DebouncesUntilInteractiveReplacementFrameCompletes()
        {
            CreateManager();
            NetworkTraversalController controller = CreateHostController(
                906,
                NetworkPredictionBackend.BuiltIn);
            IEnumerator exitSnapshot = (IEnumerator)InvokePrivateResult(
                controller,
                "BroadcastServerTraversalExitSnapshotNextFrame",
                "source-traverse");

            Assert.That(exitSnapshot.MoveNext(), Is.True);
            Assert.That(
                exitSnapshot.Current,
                Is.Null,
                "The first yield must defer the snapshot into the replacement frame");
            Assert.That(exitSnapshot.MoveNext(), Is.True);
            Assert.That(
                exitSnapshot.Current,
                Is.Null,
                "The replacement frame must finish before a durable detached snapshot can be " +
                "broadcast; entering the target during that frame cancels this coroutine");
        }

        [Test]
        public async Task LedgeAnimationOverride_HoldsIntentPoseAtBoundaryAndUsesOnlyShortReleaseMemory()
        {
            GameObject characterObject = Track(new GameObject("Ledge Animation Character"));
            Character character = EditModeLifecycle.AddComponent<Character>(characterObject);
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
            int characterKey = character.GetLegacyInstanceId();
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

            Assert.That(character.Driver.SkinWidth, Is.GreaterThanOrEqualTo(0.079f));
            relativePosition.SetValue(stance, new Vector3(0f, 0f, 0.925f));
            object[] skinInsetBoundaryArguments =
            {
                character,
                Vector3.zero,
                -movingSpeed,
                -movingSpeed
            };
            bool skinInsetBoundaryOverride = (bool)InvokePrivateResult(
                hooks,
                "ApplyTraversalAnimationInputOverride",
                skinInsetBoundaryArguments);
            Assert.That(
                skinInsetBoundaryOverride,
                Is.True,
                "A controller clamped within its skin width must still select the authored edge pose");
            Assert.That((Vector3)skinInsetBoundaryArguments[2], Is.EqualTo(Vector3.zero));
            Assert.That((Vector3)skinInsetBoundaryArguments[1], Is.EqualTo(Vector3.right));

            exitMethod.Invoke(stance, new object[] { ledge, token });
        }

        [Test]
        public async Task LedgeAnimationOverride_MapsBlockedVerticalInputToForwardAndBackwardEdgeIntent()
        {
            GameObject characterObject = Track(new GameObject("Vertical Ledge Animation Character"));
            Character character = EditModeLifecycle.AddComponent<Character>(characterObject);
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
            Character character = EditModeLifecycle.AddComponent<Character>(characterObject);
            NetworkCharacter networkCharacter = EditModeLifecycle.AddComponent<NetworkCharacter>(characterObject);
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
                // The production terminal rail reports an oblique attempted direction at B.
                // Its local-Y component can be slightly larger than local Z even though the
                // owner is holding right. The authored boundary and outward Z component must
                // therefore select Edge Right on every non-owner role.
                (new Vector3(0f, 0f, 1f), new Vector3(0f, 0.737f, 0.676f), Vector3.right),
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
            Character character = EditModeLifecycle.AddComponent<Character>(characterObject);
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
        public void TraversalMoveDirection_ServerOwnerCoalescesRepeatedRenderCalls()
        {
            GameObject characterObject = Track(new GameObject("Host Traversal Direction"));
            Character character = EditModeLifecycle.AddComponent<Character>(characterObject);
            var motion = new UnitMotionNetworkController
            {
                IsServer = true
            };
            motion.OnStartup(character);

            var broadcasts = new List<NetworkMotionCommand>();
            motion.OnBroadcastCommand += broadcasts.Add;

            for (int i = 0; i < 8; i++)
            {
                motion.MoveToDirection(Vector3.right, Space.World, 9);
            }

            Assert.That(
                broadcasts.Count,
                Is.EqualTo(1),
                "A Host must apply traversal every render call without flooding ReliableOrdered commands");
            Assert.That(
                broadcasts[0].commandType,
                Is.EqualTo(NetworkMotionCommandType.MoveToDirection));
            Assert.That(broadcasts[0].GetVelocity(), Is.EqualTo(Vector3.right));
            Assert.That(
                motion.TryGetTraversalPresentationDirection(out Vector3 presentationDirection),
                Is.True,
                "Coalescing the wire command must not discard the Host's local traversal presentation");
            Assert.That(presentationDirection, Is.EqualTo(Vector3.right));

            motion.StopToDirection(9);
            motion.MoveToDirection(Vector3.right, Space.World, 9);

            Assert.That(broadcasts.Count, Is.EqualTo(3));
            Assert.That(broadcasts[1].commandType, Is.EqualTo(NetworkMotionCommandType.StopDirection));
            Assert.That(broadcasts[2].commandType, Is.EqualTo(NetworkMotionCommandType.MoveToDirection));
        }

        [Test]
        public void TraversalMoveDirection_SendGatePreservesChangesHeartbeatAndStopStart()
        {
            GameObject characterObject = Track(new GameObject("Traversal Direction Send Gate"));
            Character character = EditModeLifecycle.AddComponent<Character>(characterObject);
            var motion = new UnitMotionNetworkController();
            motion.OnStartup(character);

            bool Gate(Vector3 velocity, float now)
            {
                return (bool)InvokePrivateResult(
                    motion,
                    "ShouldSendMoveDirectionCommandAtTime",
                    velocity,
                    Space.World,
                    9,
                    now);
            }

            Assert.That(Gate(Vector3.right, 0f), Is.True, "The first direction must send immediately");
            Assert.That(Gate(Vector3.right, 0.049f), Is.False, "Identical render calls must coalesce");
            Assert.That(Gate(Vector3.up, 0.05f), Is.True, "A changed direction sends at the minimum interval");
            Assert.That(Gate(Vector3.up, 0.169f), Is.False, "Constant input waits for its heartbeat");
            Assert.That(Gate(Vector3.up, 0.17f), Is.True, "Constant traversal input sends its heartbeat");
            Assert.That(Gate(Vector3.zero, 0.171f), Is.True, "Stop must bypass the interval");
            Assert.That(Gate(Vector3.right, 0.172f), Is.True, "Restart must bypass the interval");
        }

        [Test]
        public void TraversalMoveDirection_PassiveServerReplicaCannotOverrideClientOwner()
        {
            GameObject bridgeObject = Track(new GameObject("Traversal Motion Test Bridge"));
            TraversalMotionTestTransportBridge bridge =
                bridgeObject.AddComponent<TraversalMotionTestTransportBridge>();

            GameObject characterObject = Track(new GameObject("Client-Owned Server Traversal Replica"));
            Character character = EditModeLifecycle.AddComponent<Character>(characterObject);
            NetworkCharacter networkCharacter = EditModeLifecycle.AddComponent<NetworkCharacter>(characterObject);
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

            NetworkMotionCommand nextOwnerCommand = NetworkMotionCommand.CreateMoveToDirection(
                Vector3.forward,
                true,
                9,
                78);
            result = (NetworkMotionResult)InvokePrivateResult(
                motion,
                "ProcessValidatedClientCommand",
                nextOwnerCommand);

            Assert.That(result.approved, Is.True);
            Assert.That(
                broadcastCount,
                Is.EqualTo(2),
                "Authority must forward every distinct command already throttled by its connected owner");
            Assert.That(broadcast.sequenceNumber, Is.EqualTo(78));
            Assert.That(broadcast.GetVelocity(), Is.EqualTo(Vector3.forward));
        }

        [Test]
        public async Task FreeClimbAnimationOverride_MapsAllBlockedEdgesToIntentPlane()
        {
            GameObject characterObject = Track(new GameObject("Free Climb Edge Animation Character"));
            Character character = EditModeLifecycle.AddComponent<Character>(characterObject);
            NetworkCharacter networkCharacter = EditModeLifecycle.AddComponent<NetworkCharacter>(characterObject);
            networkCharacter.SetManualNetworkId(903);
            EditModeLifecycle.InitializeNetworkRole(networkCharacter, isServer: false, isOwner: true);

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
        public void FocusedClimbDiagnostics_DoNotEnableVerboseNetworkLogging()
        {
            bool previousForce = NetworkTraversalDebug.ForceClimbDiagnostics;
            NetworkTraversalDebug.ForceClimbDiagnostics = false;
            try
            {
                NetworkTraversalManager manager = CreateManager();
                SetPrivateField(manager, "m_LogNetworkMessages", false);
                SetPrivateField(manager, "m_LogFocusedClimbDiagnostics", false);
                Assert.That(manager.DiagnosticsEnabled, Is.False);
                Assert.That(manager.FocusedClimbDiagnosticsEnabled, Is.False);

                SetPrivateField(manager, "m_LogFocusedClimbDiagnostics", true);
                Assert.That(manager.DiagnosticsEnabled, Is.False);
                Assert.That(manager.FocusedClimbDiagnosticsEnabled, Is.True);

                SetPrivateField(manager, "m_LogNetworkMessages", true);
                Assert.That(manager.DiagnosticsEnabled, Is.True);
                Assert.That(manager.FocusedClimbDiagnosticsEnabled, Is.True);

                SetPrivateField(manager, "m_LogNetworkMessages", false);
                SetPrivateField(manager, "m_LogFocusedClimbDiagnostics", false);
                NetworkTraversalDebug.ForceClimbDiagnostics = true;
                Assert.That(manager.DiagnosticsEnabled, Is.False);
                Assert.That(manager.FocusedClimbDiagnosticsEnabled, Is.True);
            }
            finally
            {
                NetworkTraversalDebug.ForceClimbDiagnostics = previousForce;
            }
        }

        [Test]
        public void RegisterController_RepeatedSameInstanceDoesNotLogAgain()
        {
            NetworkTraversalManager manager = CreateManager();
            NetworkTraversalController controller = CreateRemoteController(
                321,
                out _,
                out _);

            manager.UnregisterController(321);
            SetPrivateField(manager, "m_LogNetworkMessages", true);
            int registrationLogs = 0;
            void CountRegistrationLog(string condition, string stackTrace, LogType type)
            {
                if (type == LogType.Log &&
                    condition == "[NetworkTraversalManager] Registered controller for NetworkId=321")
                {
                    registrationLogs++;
                }
            }

            Application.logMessageReceived += CountRegistrationLog;
            try
            {
                manager.RegisterController(321, controller);
                manager.RegisterController(321, controller);

                Assert.That(
                    registrationLogs,
                    Is.EqualTo(1),
                    "Repeated registration of the same controller must not log twice");
                Assert.That(manager.GetController(321), Is.SameAs(controller));
            }
            finally
            {
                Application.logMessageReceived -= CountRegistrationLog;
                SetPrivateField(manager, "m_LogNetworkMessages", false);
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
            return EditModeLifecycle.AddComponent<NetworkTraversalManager>(m_ManagerObject);
        }

        private static async Task WaitForClientApplyToSettle(
            NetworkTraversalController controller)
        {
            for (int i = 0; i < 100; i++)
            {
                if (GetPrivateField<object>(controller, "m_ClientAuthoritativeStateApply") == null)
                {
                    return;
                }

                await Task.Yield();
            }

            Assert.That(
                GetPrivateField<object>(controller, "m_ClientAuthoritativeStateApply"),
                Is.Null,
                "The authoritative traversal apply did not settle within 100 EditMode yields.");
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
            character = EditModeLifecycle.AddComponent<Character>(gameObject);
            NetworkCharacter networkCharacter = EditModeLifecycle.AddComponent<NetworkCharacter>(gameObject);
            networkCharacter.SetManualNetworkId(networkId);
            NetworkTraversalController controller = EditModeLifecycle.AddComponent<NetworkTraversalController>(gameObject);
            EditModeLifecycle.InitializeNetworkRole(networkCharacter,
                isServer: false,
                isOwner: isLocalClient);
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
            Character character = EditModeLifecycle.AddComponent<Character>(gameObject);
            NetworkCharacter networkCharacter = EditModeLifecycle.AddComponent<NetworkCharacter>(gameObject);
            SetPrivateField(networkCharacter, "m_PredictionBackend", predictionBackend);
            SetPrivateField(networkCharacter, "m_HostOwnerUsesClientPrediction", hostUsesClientPrediction);
            networkCharacter.SetManualNetworkId(networkId);
            NetworkTraversalController controller = EditModeLifecycle.AddComponent<NetworkTraversalController>(gameObject);

            EditModeLifecycle.InitializeNetworkRole(networkCharacter, isServer: true, isOwner: true, isHost: true);
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

        private AnimationClip CreateTestClip(string name, float duration)
        {
            AnimationClip clip = Track(new AnimationClip { name = name });
            clip.SetCurve(
                "__NetworkTraversalTestVisual",
                typeof(Transform),
                "localPosition.x",
                AnimationCurve.Linear(0f, 0f, duration, 1f));
            Assert.That(clip.length, Is.EqualTo(duration).Within(0.001f));
            return clip;
        }

        private static void SetAllTransitionClips(object transitions, AnimationClip clip)
        {
            SetPrivateField(transitions, "m_Forward", clip);
            SetPrivateField(transitions, "m_Backward", clip);
            SetPrivateField(transitions, "m_Left", clip);
            SetPrivateField(transitions, "m_Right", clip);
            SetPrivateField(transitions, "m_Upward", clip);
            SetPrivateField(transitions, "m_Downward", clip);
        }

        private static bool HasActiveGestureClip(Character character, AnimationClip clip)
        {
            if (character?.Gestures == null || clip == null) return false;
            IList active = GetPrivateField<IList>(character.Gestures, "m_ActiveList");
            foreach (object gesture in active)
            {
                if ((int)GetProperty(gesture, "AnimationClipHash") == clip.GetHashCode())
                {
                    return true;
                }
            }

            return false;
        }

        private static async Task<bool> WaitForActiveGestureClip(
            Character character,
            AnimationClip clip,
            float timeoutSeconds)
        {
            float deadline = Time.realtimeSinceStartup + timeoutSeconds;
            while (Time.realtimeSinceStartup < deadline)
            {
                if (HasActiveGestureClip(character, clip)) return true;
                await Task.Yield();
            }

            return HasActiveGestureClip(character, clip);
        }

        private static Vector3 GetTraversalRelativePosition(TraversalStance stance)
        {
            PropertyInfo property = typeof(TraversalStance).GetProperty(
                "RelativePosition",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(property, Is.Not.Null);
            return (Vector3)property.GetValue(stance);
        }

        private static void SetTraversalTransitionState(
            TraversalStance stance,
            bool inTransition)
        {
            PropertyInfo property = typeof(TraversalStance).GetProperty(
                "InInteractiveTransition",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(property, Is.Not.Null);
            property.SetValue(stance, inTransition);
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

        private static NetworkTraversalBroadcast CreateInteractiveEnterBroadcast(
            NetworkTraversalController controller,
            TraverseInteractive traverse,
            uint stateVersion,
            uint correlationId)
        {
            string traverseId = BuildTraverseId(traverse);
            return new NetworkTraversalBroadcast
            {
                NetworkId = controller.NetworkId,
                ActorNetworkId = controller.NetworkId,
                CorrelationId = correlationId,
                Action = TraversalActionType.EnterTraverseInteractive,
                TraverseHash = StableHashUtility.GetStableHash(traverseId),
                TraverseIdString = traverseId,
                IsTraversing = true,
                StateVersion = stateVersion,
                ServerTime = stateVersion,
                ArgsSelfNetworkId = controller.NetworkId,
                ArgsTargetNetworkId = controller.NetworkId
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
