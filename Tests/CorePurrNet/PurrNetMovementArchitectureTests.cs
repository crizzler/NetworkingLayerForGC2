using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Arawn.GameCreator2.Networking.Transport.PurrNet;
using GameCreator.Runtime.Characters;
using NUnit.Framework;
using PurrNet.Packing;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.CorePurrNet.Tests
{
    /// <summary>
    /// EditMode regressions for the transport-agnostic movement drivers used by the PurrNet
    /// built-in backend. These tests deliberately exercise the driver boundary without starting
    /// a PurrNet session; packet delivery and prediction timing remain PlayMode concerns.
    /// </summary>
    public sealed class PurrNetMovementArchitectureTests
    {
        private const string PresentationTypeName =
            "Arawn.GameCreator2.Networking.NetworkCharacterVisualPresentation";

        private readonly List<UnityEngine.Object> m_Cleanup = new();

        [TearDown]
        public void TearDown()
        {
            for (int i = m_Cleanup.Count - 1; i >= 0; i--)
            {
                if (m_Cleanup[i] != null) UnityEngine.Object.DestroyImmediate(m_Cleanup[i]);
            }

            m_Cleanup.Clear();
        }

        [Test]
        public void VisualPresentation_MovesOnlyValidatedMannequinAndRestoresAuthoredHierarchy()
        {
            Character character = CreateCharacter("Visual Presentation", out Transform mannequin);
            CharacterController controller = character.GetComponent<CharacterController>() ??
                                             character.gameObject.AddComponent<CharacterController>();
            character.transform.SetPositionAndRotation(
                new Vector3(4f, 2f, -3f),
                Quaternion.Euler(0f, 25f, 0f));
            mannequin.localPosition = new Vector3(0f, 0.75f, 0f);

            Vector3 authoritativePosition = character.transform.position;
            Quaternion authoritativeRotation = character.transform.rotation;
            object presentation = CreatePresentation(character, "Test");

            Assert.That(Invoke<bool>(presentation, "TryEnsure", true), Is.True);
            Transform wrapper = mannequin.parent;
            Assert.That(wrapper, Is.Not.Null);
            Assert.That(wrapper.name, Is.EqualTo("__NetworkCharacterPresentation"));
            Assert.That(wrapper.parent, Is.SameAs(character.transform));
            Assert.That(controller.transform, Is.SameAs(character.transform));

            Vector3 renderPosition = authoritativePosition + new Vector3(-1.5f, 0.25f, 2f);
            Quaternion renderRotation = Quaternion.Euler(0f, 110f, 0f);
            Assert.That(
                Invoke<bool>(presentation, "ApplyWorldPose", renderPosition, renderRotation),
                Is.True);

            AssertVector(character.transform.position, authoritativePosition);
            Assert.That(
                Quaternion.Angle(character.transform.rotation, authoritativeRotation),
                Is.LessThan(0.001f));
            AssertVector(controller.transform.position, authoritativePosition);
            AssertVector(wrapper.position, renderPosition);

            Invoke(presentation, "Dispose");

            Assert.That(mannequin.parent, Is.SameAs(character.transform));
            Assert.That(character.transform.Find("__NetworkCharacterPresentation"), Is.Null);
            AssertVector(character.transform.position, authoritativePosition);
            Assert.That(
                Quaternion.Angle(character.transform.rotation, authoritativeRotation),
                Is.LessThan(0.001f));
        }

        [Test]
        public void VisualPresentation_ResetOffsetCannotPinOwnerAtAStaleWorldPose()
        {
            Character character = CreateCharacter("Visual Presentation Cache Lifetime", out Transform mannequin);
            object presentation = CreatePresentation(character, "ClientDriver");

            Assert.That(Invoke<bool>(presentation, "TryEnsure", true), Is.True);
            Transform wrapper = mannequin.parent;
            Assert.That(wrapper.name, Is.EqualTo("__NetworkCharacterPresentation"));

            // Creating a wrapper alone must keep it parent-relative. Emulate Unity's
            // onBeforeRender callback after the predicted Character root advances.
            Vector3 firstPredictedRoot = new Vector3(1.25f, 0f, -0.5f);
            character.transform.position = firstPredictedRoot;
            Invoke(presentation, "ReapplyWorldPose");
            AssertVector(wrapper.position, firstPredictedRoot);
            AssertVector(wrapper.localPosition, Vector3.zero);

            // An explicit render pose may be held during reconciliation, but ResetOffset ends
            // that hold. A later prediction frame must not be overwritten with the completed
            // reconciliation's cached world pose.
            Vector3 correctionPose = firstPredictedRoot + new Vector3(-0.2f, 0f, 0.15f);
            Assert.That(
                Invoke<bool>(
                    presentation,
                    "ApplyWorldPose",
                    correctionPose,
                    Quaternion.Euler(0f, 15f, 0f)),
                Is.True);

            Vector3 rootWhilePoseIsHeld = firstPredictedRoot + new Vector3(0.3f, 0f, 0f);
            character.transform.position = rootWhilePoseIsHeld;
            Invoke(presentation, "ReapplyWorldPose");
            AssertVector(
                wrapper.position,
                correctionPose,
                "An explicit remote/interpolation pose must remain protected before rendering");

            Invoke(presentation, "ResetOffset");

            Vector3 nextPredictedRoot = rootWhilePoseIsHeld + new Vector3(0.75f, 0f, 0.25f);
            character.transform.position = nextPredictedRoot;
            Invoke(presentation, "ReapplyWorldPose");

            AssertVector(wrapper.position, nextPredictedRoot);
            AssertVector(wrapper.localPosition, Vector3.zero);
            Assert.That(
                Quaternion.Angle(wrapper.localRotation, Quaternion.identity),
                Is.LessThan(0.001f));

            Invoke(presentation, "Dispose");
        }

        [Test]
        public void VisualPresentation_DynamicPhysicsUnderMannequinFailsClosedAndRestoresHierarchy()
        {
            Character character = CreateCharacter("Dynamic Ragdoll Safety", out Transform mannequin);
            object presentation = CreatePresentation(character, "Test");

            Assert.That(Invoke<bool>(presentation, "TryEnsure", false), Is.True);
            Assert.That(mannequin.parent.name, Is.EqualTo("__NetworkCharacterPresentation"));

            GameObject ragdollBone = Track(new GameObject("Runtime Ragdoll Bone"));
            ragdollBone.transform.SetParent(mannequin, false);
            ragdollBone.AddComponent<Rigidbody>();

            Assert.That(
                Invoke<bool>(presentation, "TryEnsure", false),
                Is.False,
                "A Rigidbody introduced after initial validation must disable render wrapping");
            Assert.That(mannequin.parent, Is.SameAs(character.transform));
            Assert.That(character.transform.Find("__NetworkCharacterPresentation"), Is.Null);

            Invoke(presentation, "Dispose");
        }

        [Test]
        public void VisualPresentation_TransportNetworkComponentUnderMannequinFailsClosed()
        {
            Character character = CreateCharacter("Network Component Safety", out Transform mannequin);
            mannequin.gameObject.AddComponent<PurrNet.Testing.NetworkTransformTestComponent>();
            object presentation = CreatePresentation(character, "Test");

            Assert.That(
                Invoke<bool>(presentation, "TryEnsure", false),
                Is.False,
                "Transport networking components must never be moved into a render-only frame");
            Assert.That(mannequin.parent, Is.SameAs(character.transform));
            Assert.That(character.transform.Find("__NetworkCharacterPresentation"), Is.Null);

            Invoke(presentation, "Dispose");
        }

        [Test]
        public void RemoteSnapshots_RejectNonMonotonicPacketsAndRoleResetInvalidatesLatePackets()
        {
            Character character = CreateCharacter("Remote Snapshot Watermark", out Transform mannequin);
            NetworkCharacter networkCharacter = character.gameObject.AddComponent<NetworkCharacter>();
            DisableOptionalNetworkSystems(networkCharacter);
            networkCharacter.SetManualNetworkId(701);
            networkCharacter.InitializeNetworkRole(isServer: false, isOwner: false);

            Assert.That(networkCharacter.CurrentRole, Is.EqualTo(NetworkCharacter.NetworkRole.RemoteClient));
            UnitDriverNetworkRemote driver = networkCharacter.RemoteDriver;
            Assert.That(driver, Is.Not.Null);
            Assert.That(networkCharacter.ActiveDriver, Is.SameAs(driver));

            NetworkPositionState first = NetworkPositionState.Create(
                new Vector3(1f, 2f, 3f),
                15f,
                0f,
                1,
                true,
                false,
                Vector3.right);
            NetworkPositionState second = NetworkPositionState.Create(
                new Vector3(2f, 2f, 3f),
                30f,
                0f,
                2,
                true,
                false,
                Vector3.right);

            driver.AddSnapshot(first, 10f);
            driver.AddSnapshot(second, 11f);
            AssertVector(character.transform.position, second.GetPosition());

            IList buffer = GetPrivateField<IList>(driver, "m_SnapshotBuffer");
            Assert.That(buffer.Count, Is.EqualTo(2));
            driver.AddSnapshot(
                NetworkPositionState.Create(
                    new Vector3(99f, 99f, 99f),
                    250f,
                    0f,
                    3,
                    false,
                    true),
                10.5f);
            driver.AddSnapshot(
                NetworkPositionState.Create(
                    new Vector3(-99f, -99f, -99f),
                    300f,
                    0f,
                    4,
                    false,
                    true),
                11f);

            Assert.That(buffer.Count, Is.EqualTo(2));
            AssertVector(character.transform.position, second.GetPosition());
            Assert.That(GetPrivateField<float>(driver, "m_LastAcceptedSnapshotTimestamp"), Is.EqualTo(11f));

            // A reliable teleport can overtake older unreliable movement packets. Its synchronized
            // server-time watermark must prevent those packets from undoing the teleport.
            driver.SetServerTime(20f);
            driver.SetPosition(new Vector3(8f, 1f, -2f), teleport: true);
            Vector3 teleportedRoot = character.transform.position;
            Assert.That(buffer.Count, Is.Zero);
            Assert.That(GetPrivateField<float>(driver, "m_LastAcceptedSnapshotTimestamp"), Is.EqualTo(20f));
            driver.AddSnapshot(first, 19f);
            AssertVector(character.transform.position, teleportedRoot);
            Assert.That(buffer.Count, Is.Zero);

            object presentation = GetPrivateField<object>(driver, "m_VisualPresentation");
            Assert.That(Invoke<bool>(presentation, "TryEnsure", false), Is.True);
            Assert.That(mannequin.parent.name, Is.EqualTo("__NetworkCharacterPresentation"));

            networkCharacter.ResetNetworkRole();

            Assert.That(networkCharacter.CurrentRole, Is.EqualTo(NetworkCharacter.NetworkRole.None));
            Assert.That(mannequin.parent, Is.SameAs(character.transform));
            Assert.That(buffer.Count, Is.Zero);
            Assert.That(GetPrivateField<bool>(driver, "m_HasAcceptedSnapshotTimestamp"), Is.False);

            Vector3 positionAfterReset = character.transform.position;
            driver.AddSnapshot(
                NetworkPositionState.Create(
                    new Vector3(40f, 50f, 60f),
                    90f,
                    0f,
                    5,
                    true,
                    false),
                12f);

            AssertVector(character.transform.position, positionAfterReset);
            Assert.That(buffer.Count, Is.Zero);
            Assert.That(GetPrivateField<bool>(driver, "m_HasAcceptedSnapshotTimestamp"), Is.False);
        }

        [Test]
        public void BuiltInDrivers_AddScaleUsesGc2ComponentWiseAddition()
        {
            TUnitDriver[] drivers =
            {
                new UnitDriverNetworkClient(),
                new UnitDriverNetworkServer(),
                new UnitDriverNetworkRemote()
            };

            foreach (TUnitDriver driver in drivers)
            {
                GameObject characterObject = Track(new GameObject($"AddScale {driver.GetType().Name}"));
                Character character = characterObject.AddComponent<Character>();
                driver.OnStartup(character);

                try
                {
                    characterObject.transform.localScale = new Vector3(2f, 3f, 4f);
                    driver.AddScale(new Vector3(0.5f, -1f, 2f));
                    AssertVector(
                        characterObject.transform.localScale,
                        new Vector3(2.5f, 2f, 6f),
                        driver.GetType().Name);
                }
                finally
                {
                    driver.OnDispose(character);
                }
            }
        }

        [Test]
        public void InputState_PurrNetRoundTrip_PreservesTraversalPresentationDirection()
        {
            NetworkInputState expected = NetworkInputState.Create(
                new Vector2(0.25f, -0.75f),
                sequence: 42,
                deltaTime: 0.033f,
                rotationY: 137f,
                ownerAuthorityPosition: new Vector3(2.5f, 4f, -8.25f));
            expected.SetTraversalPresentationDirection(new Vector3(3.25f, -1.5f, 0.75f));

            using BitPacker packer = BitPackerPool.Get();
            GC2NetworkValuePackers.Write(packer, expected);
            packer.ResetPositionAndMode(true);
            NetworkInputState actual = default;
            GC2NetworkValuePackers.Read(packer, ref actual);

            Assert.That(actual, Is.EqualTo(expected));
            Assert.That(actual.HasOwnerAuthorityPosition, Is.True);
            Assert.That(actual.HasTraversalPresentationDirection, Is.True);
            AssertVector(
                actual.GetTraversalPresentationDirection(),
                expected.GetTraversalPresentationDirection());
        }

        [Test]
        public void ClientTeleport_ClearsPendingExternalTraversalDisplacement()
        {
            Character character = CreateCharacter("Client Traversal Teleport Barrier", out _);
            var driver = new UnitDriverNetworkClient();
            driver.OnStartup(character);

            try
            {
                // Establish an active prediction lifecycle, then emulate a GC2 Traversal link
                // advancing through AddPosition before the next network tick.
                SetPrivateField(driver, "m_HasIssuedInput", true);
                driver.AddPosition(new Vector3(0.5f, 0.25f, -0.75f));
                Assert.That(
                    GetPrivateField<Vector3>(driver, "m_PendingExternalRootTranslationForTick")
                        .sqrMagnitude,
                    Is.GreaterThan(0.0001f));

                Vector3 teleportRoot = new Vector3(8f, 3f, -2f);
                float halfHeight = character.Motion.Height * 0.5f;
                driver.SetPosition(
                    teleportRoot - Vector3.up * halfHeight,
                    teleport: true);

                AssertVector(
                    GetPrivateField<Vector3>(driver, "m_PendingExternalRootTranslationForTick"),
                    Vector3.zero);
                AssertVector(
                    GetPrivateField<Vector3>(driver, "m_PendingMovementTranslationForTick"),
                    Vector3.zero);
                AssertVector(character.transform.position, teleportRoot);
            }
            finally
            {
                driver.OnDispose(character);
            }
        }

        [Test]
        public void RemoteSnapshots_AdvanceWhileDeadWithoutWritingTheRagdollRoot()
        {
            Character character = CreateCharacter("Remote Ragdoll Root", out _);
            NetworkCharacter networkCharacter = character.gameObject.AddComponent<NetworkCharacter>();
            DisableOptionalNetworkSystems(networkCharacter);
            networkCharacter.SetManualNetworkId(702);
            networkCharacter.InitializeNetworkRole(isServer: false, isOwner: false);

            UnitDriverNetworkRemote driver = networkCharacter.RemoteDriver;
            NetworkPositionState beforeDeath = NetworkPositionState.Create(
                new Vector3(1f, 2f, 3f),
                10f,
                0f,
                1,
                true,
                false);
            NetworkPositionState whileDead = NetworkPositionState.Create(
                new Vector3(8f, 9f, 10f),
                20f,
                0f,
                2,
                false,
                false);

            driver.AddSnapshot(beforeDeath, 10f);
            AssertVector(character.transform.position, beforeDeath.GetPosition());

            character.IsDead = true;
            driver.AddSnapshot(whileDead, 11f);

            AssertVector(
                character.transform.position,
                beforeDeath.GetPosition(),
                "Network state must not compete with the ragdoll/death root writer");
            Assert.That(
                GetPrivateField<IList>(driver, "m_SnapshotBuffer").Count,
                Is.EqualTo(2),
                "The newest authority state should still be retained for recovery");

            character.IsDead = false;
            driver.OnUpdate();
            AssertVector(character.transform.position, whileDead.GetPosition());
        }

        [Test]
        public void ServerOwnerMotionExitGrace_AcceptsFinalIdlePoseAndThenFailsClosed()
        {
            Character character = CreateCharacter("Correlated Traversal Exit Grace", out _);
            var driver = new UnitDriverNetworkServer();
            driver.OnStartup(character);

            try
            {
                driver.OpenServerOwnerMotionWindow(1f, 73u);
                driver.CloseServerOwnerMotionWindow(0.5f);

                object[] terminalArguments = { null };
                bool terminalAccepted = Invoke<bool>(
                    driver,
                    "ShouldAcceptOwnerAuthorityPosition",
                    terminalArguments);
                Assert.That(terminalAccepted, Is.True);
                Assert.That(
                    terminalArguments[0],
                    Is.EqualTo("server-owner-motion-exit-grace"));

                driver.CloseServerOwnerMotionWindow(0f);

                object[] closedArguments = { null };
                bool closedAccepted = Invoke<bool>(
                    driver,
                    "ShouldAcceptOwnerAuthorityPosition",
                    closedArguments);
                Assert.That(closedAccepted, Is.False);
                Assert.That(closedArguments[0], Is.EqualTo("no-server-owner-motion-window"));
            }
            finally
            {
                driver.OnDispose(character);
            }
        }

        [Test]
        public void ServerOwnerMotionExitGrace_RequiresPreviouslyActiveServerWindow()
        {
            Character character = CreateCharacter("Inactive Traversal Exit Grace", out _);
            var driver = new UnitDriverNetworkServer();
            driver.OnStartup(character);

            try
            {
                driver.CloseServerOwnerMotionWindow(0.5f);

                object[] arguments = { null };
                bool accepted = Invoke<bool>(
                    driver,
                    "ShouldAcceptOwnerAuthorityPosition",
                    arguments);

                Assert.That(accepted, Is.False);
                Assert.That(arguments[0], Is.EqualTo("no-server-owner-motion-window"));
            }
            finally
            {
                driver.OnDispose(character);
            }
        }

        [Test]
        public void ServerOwnerMotionExitGrace_NewOpenReturnsToBusyGate()
        {
            Character character = CreateCharacter("Traversal Exit Grace Reopen", out _);
            var driver = new UnitDriverNetworkServer();
            driver.OnStartup(character);

            try
            {
                // This EditMode fixture does not initialize GC2's playable graph. The reopened
                // window must reach the ordinary idle gate, so disable graph-backed root-motion
                // sampling and model the post-traversal character explicitly.
                character.CanUseRootMotionPosition = false;
                character.CanUseRootMotionRotation = false;
                driver.OpenServerOwnerMotionWindow(1f, 74u);
                driver.CloseServerOwnerMotionWindow(0.5f);
                driver.OpenServerOwnerMotionWindow(1f, 75u);

                object[] arguments = { null };
                bool accepted = Invoke<bool>(
                    driver,
                    "ShouldAcceptOwnerAuthorityPosition",
                    arguments);

                Assert.That(accepted, Is.False);
                Assert.That(arguments[0], Is.EqualTo("not-root-motion-or-busy"));
            }
            finally
            {
                driver.OnDispose(character);
            }
        }

        [Test]
        public void ServerInputQueue_RejectsRegressionAndTeleportAcknowledgesDiscardedInputs()
        {
            Character character = CreateCharacter("Server Input Watermark", out _);
            var driver = new UnitDriverNetworkServer();
            driver.OnStartup(character);

            try
            {
                driver.QueueInput(NetworkInputState.Create(Vector2.zero, 10, 0.016f));
                driver.QueueInput(NetworkInputState.Create(Vector2.zero, 11, 0.016f));
                driver.QueueInput(NetworkInputState.Create(Vector2.zero, 12, 0.016f));
                driver.QueueInput(NetworkInputState.Create(Vector2.one, 9, 0.016f));

                Queue<NetworkInputState> queue =
                    GetPrivateField<Queue<NetworkInputState>>(driver, "m_InputBuffer");
                Assert.That(queue.Count, Is.EqualTo(3));
                Assert.That(GetPrivateField<ushort>(driver, "m_LastQueuedInput"), Is.EqualTo(12));

                driver.SetPosition(Vector3.zero, teleport: true);

                Assert.That(queue.Count, Is.Zero);
                Assert.That(driver.LastProcessedInput, Is.EqualTo(12));

                driver.QueueInput(NetworkInputState.Create(Vector2.zero, 13, 0.016f));
                Assert.That(queue.Count, Is.EqualTo(1));
            }
            finally
            {
                driver.OnDispose(character);
            }
        }

        [Test]
        public void ServerInputQueue_CapsClientSimulationTimePerServerTick()
        {
            Character character = CreateCharacter("Server Input Budget", out _);
            var driver = new UnitDriverNetworkServer();
            driver.OnStartup(character);

            try
            {
                for (ushort sequence = 0; sequence < 16; sequence++)
                {
                    driver.QueueInput(NetworkInputState.Create(
                        Vector2.one,
                        sequence,
                        0.255f));
                }

                Queue<NetworkInputState> queue =
                    GetPrivateField<Queue<NetworkInputState>>(driver, "m_InputBuffer");
                float acceptedTime = 0f;
                foreach (NetworkInputState input in queue)
                {
                    acceptedTime += input.GetDeltaTime();
                }

                Assert.That(acceptedTime, Is.LessThanOrEqualTo(0.1f));
                Assert.That(
                    GetPrivateField<ushort>(driver, "m_LastQueuedInput"),
                    Is.EqualTo(15),
                    "Over-budget inputs are consumed for acknowledgement, not simulated later");
            }
            finally
            {
                driver.OnDispose(character);
            }
        }

        [Test]
        public void RemoteServerReplica_LocalPlayerUnitCannotConsumeRemoteSequenceNumbers()
        {
            Character character = CreateCharacter("Remote Server Input Namespace", out _);
            NetworkCharacter networkCharacter = character.gameObject.AddComponent<NetworkCharacter>();
            DisableOptionalNetworkSystems(networkCharacter);
            networkCharacter.SetManualNetworkId(703);
            networkCharacter.InitializeNetworkRole(isServer: true, isOwner: false, isHost: true);

            UnitDriverNetworkServer driver = networkCharacter.ServerDriver;
            Assert.That(driver, Is.Not.Null);
            Assert.That(networkCharacter.ActiveDriver, Is.SameAs(driver));

            // A prefab-authored UnitPlayerDirectionalNetwork remains on a server replica because
            // GC2 rejects ChangePlayer(null). Its zero-input cleanup must not share the sequence
            // namespace used by packets arriving from the actual remote owner.
            Invoke(
                driver,
                "QueueLocalDirectionalInput",
                Vector2.zero,
                null,
                false,
                0.05f);

            Queue<NetworkInputState> queue =
                GetPrivateField<Queue<NetworkInputState>>(driver, "m_InputBuffer");
            Assert.That(queue.Count, Is.Zero);
            Assert.That(GetPrivateField<ushort>(driver, "m_LocalInputSequence"), Is.Zero);
            Assert.That(GetPrivateField<bool>(driver, "m_HasQueuedInputWatermark"), Is.False);

            driver.QueueInput(NetworkInputState.Create(
                Vector2.right,
                sequence: 0,
                deltaTime: 0.05f,
                rotationY: 90f));

            Assert.That(queue.Count, Is.EqualTo(1));
            NetworkPositionState state = driver.ProcessInputs();
            Assert.That(driver.LastProcessedInput, Is.Zero);
            Assert.That(Mathf.DeltaAngle(state.GetRotationY(), 90f), Is.EqualTo(0f).Within(0.1f));
        }

        [Test]
        public void StrictHostOwner_CanStillGenerateValidatedLocalServerInput()
        {
            Character character = CreateCharacter("Strict Host Local Input", out _);
            NetworkCharacter networkCharacter = character.gameObject.AddComponent<NetworkCharacter>();
            DisableOptionalNetworkSystems(networkCharacter);
            networkCharacter.SetManualNetworkId(704);
            networkCharacter.InitializeNetworkRole(isServer: true, isOwner: true, isHost: true);

            Assert.That(networkCharacter.CurrentRole, Is.EqualTo(NetworkCharacter.NetworkRole.Server));
            UnitDriverNetworkServer driver = networkCharacter.ServerDriver;

            Invoke(
                driver,
                "QueueLocalDirectionalInput",
                Vector2.right,
                null,
                false,
                0.05f);

            Queue<NetworkInputState> queue =
                GetPrivateField<Queue<NetworkInputState>>(driver, "m_InputBuffer");
            Assert.That(queue.Count, Is.EqualTo(1));
            Assert.That(GetPrivateField<ushort>(driver, "m_LocalInputSequence"), Is.EqualTo(1));
        }

        [Test]
        public void InputCadence_PreservesSixtyHertzPhaseAndFractionalMilliseconds()
        {
            Type cadenceType = typeof(UnitDriverNetworkClient).Assembly.GetType(
                "Arawn.GameCreator2.Networking.NetworkInputCadence");
            Assert.That(cadenceType, Is.Not.Null);

            MethodInfo advance = cadenceType.GetMethod(
                "Advance",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            MethodInfo quantize = cadenceType.GetMethod(
                "QuantizeElapsedSeconds",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(advance, Is.Not.Null);
            Assert.That(quantize, Is.Not.Null);

            float phase = 0f;
            int sends = 0;
            for (int frame = 0; frame < 125; frame++)
            {
                object[] arguments = { phase, 0.016f, 1f / 60f };
                if ((bool)advance.Invoke(null, arguments)) sends++;
                phase = (float)arguments[0];
            }

            Assert.That(
                sends,
                Is.InRange(119, 120),
                "Resetting the phase to zero would alias this stream down to about 62 sends");

            float remainderMilliseconds = 0f;
            float encodedSeconds = 0f;
            for (int sample = 0; sample < 60; sample++)
            {
                object[] arguments = { 1f / 60f, remainderMilliseconds };
                encodedSeconds += (float)quantize.Invoke(null, arguments);
                remainderMilliseconds = (float)arguments[1];
            }

            Assert.That(encodedSeconds, Is.EqualTo(1f).Within(0.0011f));
            Assert.That(Mathf.Abs(remainderMilliseconds), Is.LessThan(0.501f));
        }

        [Test]
        public void HostInputPacket_UsesTimeWeightedDirectionAcrossRenderFrames()
        {
            Character character = CreateCharacter("Weighted Host Input", out _);
            NetworkCharacter networkCharacter = character.gameObject.AddComponent<NetworkCharacter>();
            DisableOptionalNetworkSystems(networkCharacter);
            networkCharacter.SetManualNetworkId(706);
            networkCharacter.InitializeNetworkRole(isServer: true, isOwner: true, isHost: true);

            UnitDriverNetworkServer driver = networkCharacter.ServerDriver;
            Invoke(driver, "QueueLocalDirectionalInput", Vector2.right, null, false, 0.016f);
            Invoke(driver, "QueueLocalDirectionalInput", Vector2.left, null, false, 0.018f);

            Queue<NetworkInputState> queue =
                GetPrivateField<Queue<NetworkInputState>>(driver, "m_InputBuffer");
            Assert.That(queue.Count, Is.EqualTo(1));

            NetworkInputState input = queue.Peek();
            float expectedX = (0.016f - 0.018f) / 0.034f;
            Assert.That(input.GetInputDirection().x, Is.EqualTo(expectedX).Within(0.002f));
            Assert.That(input.GetInputDirection().y, Is.EqualTo(0f).Within(0.001f));
            Assert.That(input.GetDeltaTime(), Is.EqualTo(0.034f).Within(0.001f));
        }

        [Test]
        public void ClientReconciliation_UsesVisualWrapperWithoutChangingGc2AnimimOffsets()
        {
            Character character = CreateCharacter("Owner Reconciliation Presentation", out Transform mannequin);
            var driver = new UnitDriverNetworkClient();
            driver.OnStartup(character);

            try
            {
                Vector3 authoredPosition = new Vector3(0.15f, 0.4f, -0.2f);
                Quaternion authoredRotation = Quaternion.Euler(5f, 12f, -3f);
                character.Animim.Position = authoredPosition;
                character.Animim.Rotation = authoredRotation;

                Invoke(
                    driver,
                    "StartReconciliation",
                    new Vector3(0.25f, 0f, 0f),
                    20f,
                    0f,
                    -1);

                AssertVector(character.Animim.Position, authoredPosition);
                Assert.That(
                    Quaternion.Angle(character.Animim.Rotation, authoredRotation),
                    Is.LessThan(0.001f));
                Assert.That(mannequin.parent.name, Is.EqualTo("__NetworkCharacterPresentation"));
                AssertVector(character.transform.position, new Vector3(0.25f, 0f, 0f));
                Assert.That(driver.IsReconciling, Is.True);

                driver.ResetNetworkState();
                Assert.That(mannequin.parent, Is.SameAs(character.transform));
                AssertVector(character.Animim.Position, authoredPosition);
                Assert.That(
                    Quaternion.Angle(character.Animim.Rotation, authoredRotation),
                    Is.LessThan(0.001f));
            }
            finally
            {
                driver.OnDispose(character);
            }
        }

        [Test]
        public void AuthorizedAbsoluteOwnerPose_BypassesNativeSweepAndSchedulesProxyRefresh()
        {
            Character character = CreateCharacter("Absolute Traversal Owner Pose", out _);
            var driver = new UnitDriverNetworkServer();
            driver.OnStartup(character);
            Func<Character, Vector3, string> allowAbsolute =
                (candidate, _) => candidate == character
                    ? "test-authorized-absolute-root"
                    : string.Empty;

            UnitDriverNetworkServer.ExternalRootPositionWriteAllowanceRequested += allowAbsolute;
            try
            {
                Vector3 target = new Vector3(0.25f, 1.5f, -0.4f);
                Vector3 applied = Invoke<Vector3>(
                    driver,
                    "ApplyOwnerAuthorityRootPosition",
                    target);

                AssertVector(applied, target);
                AssertVector(character.transform.position, target);
                Assert.That(driver.OwnerAuthorityNativeMoveCount, Is.Zero);
                Assert.That(
                    GetPrivateField<bool>(driver, "m_ControllerPhysicsRefreshPending"),
                    Is.True);
            }
            finally
            {
                UnitDriverNetworkServer.ExternalRootPositionWriteAllowanceRequested -= allowAbsolute;
                driver.OnDispose(character);
            }
        }

        [Test]
        public void ServerRole_ReusingSameDriverReactivatesRemoteInputGate()
        {
            Character character = CreateCharacter("Server Driver Reactivation", out _);
            NetworkCharacter networkCharacter = character.gameObject.AddComponent<NetworkCharacter>();
            DisableOptionalNetworkSystems(networkCharacter);
            networkCharacter.SetManualNetworkId(705);
            networkCharacter.InitializeNetworkRole(isServer: true, isOwner: false, isHost: true);

            UnitDriverNetworkServer originalDriver = networkCharacter.ServerDriver;
            networkCharacter.ResetNetworkRole();

            Queue<NetworkInputState> queue =
                GetPrivateField<Queue<NetworkInputState>>(originalDriver, "m_InputBuffer");
            originalDriver.QueueInput(NetworkInputState.Create(Vector2.right, 0, 0.05f));
            Assert.That(queue.Count, Is.Zero, "Late packets must remain rejected after cleanup");

            networkCharacter.InitializeNetworkRole(isServer: true, isOwner: false, isHost: true);

            Assert.That(networkCharacter.ServerDriver, Is.SameAs(originalDriver));
            Assert.That(networkCharacter.ActiveDriver, Is.SameAs(originalDriver));
            originalDriver.QueueInput(NetworkInputState.Create(
                Vector2.right,
                sequence: 0,
                deltaTime: 0.05f,
                rotationY: 45f));
            Assert.That(queue.Count, Is.EqualTo(1), "The new lifecycle must accept remote input");

            NetworkPositionState state = originalDriver.ProcessInputs();
            Assert.That(originalDriver.LastProcessedInput, Is.Zero);
            Assert.That(Mathf.DeltaAngle(state.GetRotationY(), 45f), Is.EqualTo(0f).Within(0.1f));
        }

        private Character CreateCharacter(string name, out Transform mannequin)
        {
            GameObject characterObject = Track(new GameObject(name));
            Character character = characterObject.AddComponent<Character>();
            GameObject mannequinObject = new GameObject("Mannequin");
            mannequinObject.transform.SetParent(characterObject.transform, false);
            mannequinObject.AddComponent<MeshRenderer>();
            mannequin = mannequinObject.transform;
            character.Animim.Mannequin = mannequin;
            return character;
        }

        private T Track<T>(T value) where T : UnityEngine.Object
        {
            m_Cleanup.Add(value);
            return value;
        }

        private static object CreatePresentation(Character character, string ownerName)
        {
            Type type = typeof(UnitDriverNetworkRemote).Assembly.GetType(PresentationTypeName);
            Assert.That(type, Is.Not.Null, $"Missing {PresentationTypeName}");
            return Activator.CreateInstance(
                type,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new object[] { character, ownerName },
                null);
        }

        private static void DisableOptionalNetworkSystems(NetworkCharacter character)
        {
            SetPrivateField(character, "m_UseNetworkIK", false);
            SetPrivateField(character, "m_UseNetworkMotion", false);
            SetPrivateField(character, "m_UseLagCompensation", false);
            SetPrivateField(character, "m_UseAnimationSync", false);
            SetPrivateField(character, "m_UseCoreNetworking", false);
            SetPrivateField(character, "m_UseRelevanceTiers", false);
        }

        private static void SetPrivateField(object target, string name, object value)
        {
            FieldInfo field = target.GetType().GetField(
                name,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, $"Missing field {name}");
            field.SetValue(target, value);
        }

        private static T GetPrivateField<T>(object target, string name)
        {
            FieldInfo field = target.GetType().GetField(
                name,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, $"Missing field {name}");
            return (T)field.GetValue(target);
        }

        private static void Invoke(object target, string methodName, params object[] arguments)
        {
            GetMethod(target, methodName).Invoke(target, arguments);
        }

        private static T Invoke<T>(object target, string methodName, params object[] arguments)
        {
            return (T)GetMethod(target, methodName).Invoke(target, arguments);
        }

        private static MethodInfo GetMethod(object target, string methodName)
        {
            MethodInfo method = target.GetType().GetMethod(
                methodName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null, $"Missing method {methodName}");
            return method;
        }

        private static void AssertVector(Vector3 actual, Vector3 expected, string context = null)
        {
            Assert.That(actual.x, Is.EqualTo(expected.x).Within(0.0001f), context);
            Assert.That(actual.y, Is.EqualTo(expected.y).Within(0.0001f), context);
            Assert.That(actual.z, Is.EqualTo(expected.z).Within(0.0001f), context);
        }
    }
}

namespace PurrNet.Testing
{
    /// <summary>Test double for transport-owned state that must remain at the simulation root.</summary>
    public sealed class NetworkTransformTestComponent : MonoBehaviour
    {
    }
}
