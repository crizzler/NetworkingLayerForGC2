using System.Reflection;
using GameCreator.Runtime.Characters;
using NUnit.Framework;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Tests
{
    [Category("GC2Networking.Ragdoll")]
    public sealed class NetworkRagdollPhysicsGuardTests
    {
        [TestCase(true, false)]
        [TestCase(false, true)]
        public void CharacterControllerLifecycle_IsExactAndRoleSwapIdempotent(
            bool enabledBeforeRagdoll,
            bool collisionBeforeRagdoll)
        {
            var root = new GameObject("Ragdoll Physics Guard");
            root.SetActive(false);

            try
            {
                Character character = root.AddComponent<Character>();
                CharacterController controller = root.AddComponent<CharacterController>();
                controller.enabled = enabledBeforeRagdoll;
                controller.detectCollisions = collisionBeforeRagdoll;

                NetworkRagdollPhysicsGuard.Register(character, controller);
                NetworkRagdollPhysicsGuard guard =
                    root.GetComponent<NetworkRagdollPhysicsGuard>();
                Assert.That(guard, Is.Not.Null);

                InvokeLifecycle(guard, "BeginRagdoll");
                InvokeLifecycle(guard, "BeginRagdoll");
                Assert.That(controller.enabled, Is.False);
                Assert.That(controller.detectCollisions, Is.False);

                // A new network driver assumes the role while the same physical ragdoll epoch is
                // active. Adopting the shared capsule with the next role's intent must not
                // replace the already captured pre-ragdoll baseline with inert values.
                NetworkRagdollPhysicsGuard.Adopt(
                    character,
                    controller,
                    intendedEnabled: !enabledBeforeRagdoll,
                    intendedDetectCollisions: !collisionBeforeRagdoll);
                InvokeLifecycle(guard, "BeginRagdoll");
                Assert.That(controller.enabled, Is.False);
                Assert.That(controller.detectCollisions, Is.False);

                InvokeLifecycle(guard, "BeginRecovery");
                InvokeLifecycle(guard, "BeginRecovery");
                Assert.That(controller.enabled, Is.EqualTo(enabledBeforeRagdoll));
                Assert.That(
                    controller.detectCollisions,
                    Is.EqualTo(collisionBeforeRagdoll));

                // A backend registered during the get-up phase was never part of the dynamic
                // collision epoch and must remain untouched when that epoch finishes.
                var recoveringBackend = new GameObject("Recovering Backend");
                recoveringBackend.transform.SetParent(root.transform, false);
                CharacterController recoveringController =
                    recoveringBackend.AddComponent<CharacterController>();
                recoveringController.enabled = false;
                recoveringController.detectCollisions = true;
                NetworkRagdollPhysicsGuard.Register(character, recoveringController);

                InvokeLifecycle(guard, "FinishRagdoll");
                InvokeLifecycle(guard, "FinishRagdoll");
                Assert.That(recoveringController.enabled, Is.False);
                Assert.That(recoveringController.detectCollisions, Is.True);
                Assert.That(controller.enabled, Is.EqualTo(enabledBeforeRagdoll));
                Assert.That(
                    controller.detectCollisions,
                    Is.EqualTo(collisionBeforeRagdoll));
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void BackendFamilyMigration_AdoptsIntendedControllerWithoutMidRagdollCollision()
        {
            var root = new GameObject("Ragdoll Backend Migration");
            root.SetActive(false);

            try
            {
                Character character = root.AddComponent<Character>();
                CharacterController controller = root.AddComponent<CharacterController>();
                controller.enabled = false;
                controller.detectCollisions = true;

                // A NavMesh role owns physics when the epoch starts, so the root controller is
                // deliberately absent from the active guard set.
                var navCapsule = root.AddComponent<CapsuleCollider>();
                NetworkRagdollPhysicsGuard.Register(character, navCapsule);
                NetworkRagdollPhysicsGuard guard =
                    root.GetComponent<NetworkRagdollPhysicsGuard>();
                InvokeLifecycle(guard, "BeginRagdoll");
                Assert.That(navCapsule.enabled, Is.False);

                // The observer/controller role takes over before recovery. It advertises its
                // intended normal state, but the live capsule must remain inert beside the bones.
                NetworkRagdollPhysicsGuard.Unregister(character, navCapsule);
                NetworkRagdollPhysicsGuard.Adopt(
                    character,
                    controller,
                    intendedEnabled: true,
                    intendedDetectCollisions: true);
                Assert.That(controller.enabled, Is.False);
                Assert.That(controller.detectCollisions, Is.False);

                InvokeLifecycle(guard, "BeginRecovery");
                Assert.That(controller.enabled, Is.True);
                Assert.That(controller.detectCollisions, Is.True);
                Assert.That(
                    navCapsule.enabled,
                    Is.False,
                    "An obsolete backend must not be revived during recovery.");

                InvokeLifecycle(guard, "FinishRagdoll");
                Assert.That(controller.enabled, Is.True);
                Assert.That(navCapsule.enabled, Is.False);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void DemoBaseline_RequiresExactComponentIdentityAndRestoredState()
        {
            var root = new GameObject("Ragdoll Demo Baseline");
            root.SetActive(false);

            try
            {
                Character character = root.AddComponent<Character>();
                CharacterController controller = root.AddComponent<CharacterController>();
                var agent = root.AddComponent<UnityEngine.AI.NavMeshAgent>();
                var capsule = root.AddComponent<CapsuleCollider>();
                controller.enabled = true;
                controller.detectCollisions = true;
                agent.enabled = false;
                capsule.enabled = true;

                System.Type baselineType = typeof(NetworkRagdollDemoUI).GetNestedType(
                    "RootPhysicsBaseline",
                    BindingFlags.NonPublic);
                Assert.That(baselineType, Is.Not.Null);
                MethodInfo capture = baselineType.GetMethod(
                    "Capture",
                    BindingFlags.Static | BindingFlags.Public);
                MethodInfo matches = baselineType.GetMethod(
                    "Matches",
                    BindingFlags.Instance | BindingFlags.Public,
                    null,
                    new[] { typeof(Character) },
                    null);
                Assert.That(capture, Is.Not.Null);
                Assert.That(matches, Is.Not.Null);

                object baseline = capture.Invoke(null, new object[] { character });
                Assert.That(matches.Invoke(baseline, new object[] { character }), Is.True);

                controller.detectCollisions = false;
                Assert.That(matches.Invoke(baseline, new object[] { character }), Is.False);
                controller.detectCollisions = true;

                agent.enabled = true;
                Assert.That(matches.Invoke(baseline, new object[] { character }), Is.False);
                agent.enabled = false;

                capsule.enabled = false;
                Assert.That(matches.Invoke(baseline, new object[] { character }), Is.False);
                capsule.enabled = true;
                Assert.That(matches.Invoke(baseline, new object[] { character }), Is.True);

                UnityEngine.Object.DestroyImmediate(capsule);
                CapsuleCollider replacement = root.AddComponent<CapsuleCollider>();
                replacement.enabled = true;
                Assert.That(
                    matches.Invoke(baseline, new object[] { character }),
                    Is.False,
                    "Equivalent state on a replacement component is not exact restoration.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void DemoResponseMatching_RequiresIssuedRequestAndCorrelationTuple()
        {
            var root = new GameObject("Ragdoll Demo Response Correlation");
            root.SetActive(false);

            try
            {
                NetworkRagdollDemoUI ui = root.AddComponent<NetworkRagdollDemoUI>();
                SetField(
                    ui,
                    "m_ExpectedOperation",
                    NetworkCoreVisualScriptingOperation.StartRagdoll);
                SetField(ui, "m_ExpectedCharacterId", 73u);

                InvokePrivate(
                    ui,
                    "OnRagdollRequestSent",
                    new NetworkRagdollRequest
                    {
                        RequestId = 19,
                        ActorNetworkId = 73,
                        CorrelationId = 0x490013u,
                        CharacterNetworkId = 73,
                        ActionType = RagdollActionType.StartRagdoll
                    });

                NetworkCoreVisualScriptingResult wrongRequest = CreateResult(
                    NetworkCoreVisualScriptingOperation.StartRagdoll,
                    73,
                    20,
                    0x490013u);
                InvokePrivate(ui, "OnCoreRequestCompleted", wrongRequest);
                Assert.That(GetField<bool>(ui, "m_ResponseReceived"), Is.False);

                NetworkCoreVisualScriptingResult wrongCorrelation = CreateResult(
                    NetworkCoreVisualScriptingOperation.StartRagdoll,
                    73,
                    19,
                    0x490014u);
                InvokePrivate(ui, "OnCoreRequestCompleted", wrongCorrelation);
                Assert.That(GetField<bool>(ui, "m_ResponseReceived"), Is.False);

                NetworkCoreVisualScriptingResult exact = CreateResult(
                    NetworkCoreVisualScriptingOperation.StartRagdoll,
                    73,
                    19,
                    0x490013u);
                InvokePrivate(ui, "OnCoreRequestCompleted", exact);
                Assert.That(GetField<bool>(ui, "m_ResponseReceived"), Is.True);
                NetworkCoreVisualScriptingResult accepted =
                    GetField<NetworkCoreVisualScriptingResult>(ui, "m_Response");
                Assert.That(accepted.RequestId, Is.EqualTo(19));
                Assert.That(accepted.CorrelationId, Is.EqualTo(0x490013u));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        private static NetworkCoreVisualScriptingResult CreateResult(
            NetworkCoreVisualScriptingOperation operation,
            uint characterNetworkId,
            ushort requestId,
            uint correlationId)
        {
            ConstructorInfo constructor = typeof(NetworkCoreVisualScriptingResult)
                .GetConstructor(
                    BindingFlags.Instance | BindingFlags.NonPublic,
                    null,
                    new[]
                    {
                        typeof(NetworkCoreVisualScriptingOperation),
                        typeof(uint),
                        typeof(ushort),
                        typeof(uint),
                        typeof(bool),
                        typeof(string),
                        typeof(int),
                        typeof(float),
                        typeof(bool),
                        typeof(float)
                    },
                    null);
            Assert.That(constructor, Is.Not.Null);
            return (NetworkCoreVisualScriptingResult)constructor.Invoke(new object[]
            {
                operation,
                characterNetworkId,
                requestId,
                correlationId,
                true,
                string.Empty,
                0,
                0f,
                false,
                0f
            });
        }

        private static void InvokePrivate(
            object target,
            string methodName,
            object argument)
        {
            MethodInfo method = target.GetType().GetMethod(
                methodName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null, $"Missing method {methodName}");
            method.Invoke(target, new[] { argument });
        }

        private static void SetField(object target, string fieldName, object value)
        {
            FieldInfo field = target.GetType().GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, $"Missing field {fieldName}");
            field.SetValue(target, value);
        }

        private static T GetField<T>(object target, string fieldName)
        {
            FieldInfo field = target.GetType().GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, $"Missing field {fieldName}");
            return (T)field.GetValue(target);
        }

        private static void InvokeLifecycle(
            NetworkRagdollPhysicsGuard guard,
            string methodName)
        {
            MethodInfo method = typeof(NetworkRagdollPhysicsGuard).GetMethod(
                methodName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null, $"Missing guard lifecycle method {methodName}");
            method.Invoke(guard, null);
        }
    }
}
