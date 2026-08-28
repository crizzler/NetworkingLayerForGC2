using System.Collections.Generic;
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
        public void RagdollBodyCollector_UsesConfiguredAnimatorAfterGc2DetachesItsHierarchy()
        {
            var root = new GameObject("Detached Ragdoll Character");
            var model = new GameObject("Detached Animator Model");
            root.SetActive(false);

            try
            {
                Character character = root.AddComponent<Character>();
                model.transform.SetParent(root.transform, false);
                Animator animator = model.AddComponent<Animator>();
                character.Animim.Animator = animator;

                var bone = new GameObject("Dynamic Bone");
                bone.transform.SetParent(model.transform, false);
                Rigidbody dynamicBody = bone.AddComponent<Rigidbody>();
                dynamicBody.isKinematic = false;

                // This is the hierarchy change performed by GC2's RagdollDefault.StartRagdoll.
                model.transform.SetParent(null, true);
                Assert.That(
                    root.GetComponentsInChildren<Rigidbody>(true),
                    Is.Empty,
                    "The test must reproduce the Character-subtree blind spot first.");

                var bodies = new List<Rigidbody> { root.AddComponent<Rigidbody>() };
                InvokeRagdollBodyCollector(character, bodies);

                Assert.That(bodies, Has.Count.EqualTo(1));
                Assert.That(bodies[0], Is.SameAs(dynamicBody));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(model);
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void DemoDiagnostics_DetachedAnimatorReportsDynamicBoneAndProtectedState()
        {
            var root = new GameObject("Detached Ragdoll Diagnostics");
            var model = new GameObject("Detached Animator Model");
            root.SetActive(false);

            object observation = null;
            try
            {
                Character character = root.AddComponent<Character>();
                CharacterController controller = root.AddComponent<CharacterController>();
                controller.enabled = false;
                controller.detectCollisions = false;
                root.AddComponent<NetworkRagdollPhysicsGuard>();

                model.transform.SetParent(root.transform, false);
                Animator animator = model.AddComponent<Animator>();
                character.Animim.Animator = animator;

                var bone = new GameObject("Dynamic Bone");
                bone.transform.SetParent(model.transform, false);
                Rigidbody dynamicBody = bone.AddComponent<Rigidbody>();
                dynamicBody.isKinematic = false;

                SetRagdollSystem(character.Ragdoll, new RagdollDefault());
                SetRagdollState(character.Ragdoll, true);
                animator.enabled = false;
                model.transform.SetParent(null, true);

                NetworkCharacter networkCharacter = root.AddComponent<NetworkCharacter>();
                SetField(networkCharacter, "m_Character", character);

                System.Type observationType = typeof(NetworkRagdollDemoUI).GetNestedType(
                    "ActorObservation",
                    BindingFlags.NonPublic);
                Assert.That(observationType, Is.Not.Null);
                ConstructorInfo constructor = observationType.GetConstructor(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null,
                    new[] { typeof(NetworkCharacter) },
                    null);
                Assert.That(constructor, Is.Not.Null);
                observation = constructor.Invoke(new object[] { networkCharacter });

                NetworkRagdollDemoUI ui = root.AddComponent<NetworkRagdollDemoUI>();
                MethodInfo capture = typeof(NetworkRagdollDemoUI).GetMethod(
                    "CaptureDiagnostics",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.That(capture, Is.Not.Null);
                object diagnostics = capture.Invoke(ui, new[] { observation });

                Assert.That(GetField<int>(diagnostics, "RigidbodyCount"), Is.EqualTo(1));
                Assert.That(
                    GetField<int>(diagnostics, "DynamicRigidbodyCount"),
                    Is.EqualTo(1));
                Assert.That(GetProperty<bool>(diagnostics, "RootPhysicsSuspended"), Is.True);
                Assert.That(GetProperty<bool>(diagnostics, "IsExpectedState"), Is.True);
            }
            finally
            {
                (observation as System.IDisposable)?.Dispose();
                UnityEngine.Object.DestroyImmediate(model);
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

        private static void InvokeRagdollBodyCollector(
            Character character,
            List<Rigidbody> destination)
        {
            MethodInfo method = typeof(NetworkRagdollPhysicsGuard).GetMethod(
                "CollectRagdollRigidbodies",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null, "Missing shared ragdoll Rigidbody collector");
            method.Invoke(null, new object[] { character, destination });
        }

        private static void SetRagdollSystem(Ragdoll ragdoll, TRagdollSystem system)
        {
            FieldInfo field = typeof(Ragdoll).GetField(
                "m_Ragdoll",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null);
            field.SetValue(ragdoll, system);
        }

        private static void SetRagdollState(Ragdoll ragdoll, bool value)
        {
            PropertyInfo property = typeof(Ragdoll).GetProperty(
                nameof(Ragdoll.IsRagdoll),
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(property, Is.Not.Null);
            property.SetValue(ragdoll, value);
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
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, $"Missing field {fieldName}");
            field.SetValue(target, value);
        }

        private static T GetField<T>(object target, string fieldName)
        {
            FieldInfo field = target.GetType().GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, $"Missing field {fieldName}");
            return (T)field.GetValue(target);
        }

        private static T GetProperty<T>(object target, string propertyName)
        {
            PropertyInfo property = target.GetType().GetProperty(
                propertyName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(property, Is.Not.Null, $"Missing property {propertyName}");
            return (T)property.GetValue(target);
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
