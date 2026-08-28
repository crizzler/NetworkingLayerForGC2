using System;
using System.Reflection;
using GameCreator.Runtime.Characters;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.TestTools;

namespace Arawn.GameCreator2.Networking.Tests
{
    [Category("GC2Networking.Ragdoll")]
    public sealed class NetworkNavMeshRagdollLifecycleTests
    {
        private const BindingFlags INSTANCE_FIELDS =
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        [Test]
        public void ServerDriver_RagdollPhysicsAndRoleMigration_AreEpochSafe()
        {
            GameObject root = CreateCharacterRoot(
                "Server NavMesh Ragdoll",
                out Character character);
            var driver = new UnitDriverNavmeshNetworkServer();

            try
            {
                driver.OnStartup(character);
                NavMeshAgent agent = root.GetComponent<NavMeshAgent>();
                CapsuleCollider capsule = root.GetComponent<CapsuleCollider>();

                Assert.That(agent, Is.Not.Null);
                Assert.That(capsule, Is.Not.Null);
                Assert.That(agent.enabled, Is.True);
                Assert.That(capsule.enabled, Is.True);

                InvokeRagdollEvent(character.Ragdoll, "EventBeforeStartRagdoll");
                InvokeRagdollEvent(character.Ragdoll, "EventBeforeStartRagdoll");

                Assert.That(agent.enabled, Is.False);
                Assert.That(capsule.enabled, Is.False);
                Assert.That(GetField<bool>(driver, "m_RagdollMovementSuspended"), Is.True);

                // GC2 Default Ragdoll places the recovered root before it raises
                // EventAfterStartRecover. The disabled agent must not receive an invalid Warp.
                Vector3 recoveredRoot = new Vector3(3f, 2f, -4f);
                driver.SetPosition(recoveredRoot, true);
                Assert.That(root.transform.position, Is.EqualTo(recoveredRoot));

                InvokeRagdollEvent(character.Ragdoll, "EventAfterStartRecover");
                Assert.That(agent.enabled, Is.True);
                Assert.That(capsule.enabled, Is.True);
                Assert.That(
                    GetField<bool>(driver, "m_RagdollMovementSuspended"),
                    Is.True,
                    "Physical collision returns before the get-up gesture, but NavMesh " +
                    "simulation must remain suspended until recovery finishes.");

                InvokeRagdollEvent(character.Ragdoll, "EventAfterFinishRecover");
                Assert.That(GetField<bool>(driver, "m_RagdollMovementSuspended"), Is.False);

                // A new epoch must capture an authored-disabled baseline rather than replaying
                // the previous epoch's enabled values.
                agent.enabled = false;
                capsule.enabled = false;
                InvokeRagdollEvent(character.Ragdoll, "EventBeforeStartRagdoll");
                InvokeRagdollEvent(character.Ragdoll, "EventAfterStartRecover");
                Assert.That(agent.enabled, Is.False);
                Assert.That(capsule.enabled, Is.False);
                InvokeRagdollEvent(character.Ragdoll, "EventAfterFinishRecover");

                // Relinquishing and regaining NavMesh authority during the dynamic phase must
                // unregister the obsolete role baseline, establish the active role's intended
                // state, and keep it inert until the same epoch reaches recovery.
                agent.enabled = true;
                capsule.enabled = true;
                InvokeRagdollEvent(character.Ragdoll, "EventBeforeStartRagdoll");
                SetRagdollState(character.Ragdoll, true);
                driver.OnDispose(character);
                Assert.That(agent.enabled, Is.False);
                Assert.That(capsule.enabled, Is.False);

                driver.OnStartup(character);
                Assert.That(agent.enabled, Is.False);
                Assert.That(capsule.enabled, Is.False);
                Assert.That(GetField<bool>(driver, "m_RagdollMovementSuspended"), Is.True);

                InvokeRagdollEvent(character.Ragdoll, "EventAfterStartRecover");
                Assert.That(agent.enabled, Is.True);
                Assert.That(capsule.enabled, Is.True);

                SetRagdollState(character.Ragdoll, false);
                InvokeRagdollEvent(character.Ragdoll, "EventAfterFinishRecover");
                Assert.That(GetField<bool>(driver, "m_RagdollMovementSuspended"), Is.False);
            }
            finally
            {
                driver.OnDispose(character);
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void ClientDriver_RagdollSuppressesCommandsSnapshotsAndRootDeltasUntilFinish()
        {
            GameObject root = CreateCharacterRoot(
                "Client NavMesh Ragdoll",
                out Character character);
            var driver = new UnitDriverNavmeshNetworkClient();
            SetField(driver, "m_EnableLocalNavMesh", true);

            int commandCount = 0;
            driver.OnSendCommand += _ => commandCount++;

            try
            {
                driver.OnStartup(character);
                NavMeshAgent agent = root.GetComponent<NavMeshAgent>();
                CapsuleCollider capsule = root.GetComponent<CapsuleCollider>();
                Assert.That(agent, Is.Not.Null);
                Assert.That(capsule, Is.Not.Null);

                Vector3 initialPosition = root.transform.position;
                InvokeRagdollEvent(character.Ragdoll, "EventBeforeStartRagdoll");
                Assert.That(agent.enabled, Is.False);
                Assert.That(capsule.enabled, Is.False);

                driver.RequestMoveToPosition(new Vector3(8f, 0f, 8f));
                driver.ApplyPositionUpdate(NetworkNavMeshPositionUpdate.Create(
                    new Vector3(50f, 0f, 50f),
                    90f,
                    2,
                    4f,
                    5f));
                driver.AddPosition(Vector3.one * 10f);

                Assert.That(commandCount, Is.Zero);
                Assert.That(driver.CurrentSequence, Is.Zero);
                Assert.That(root.transform.position, Is.EqualTo(initialPosition));

                InvokeRagdollEvent(character.Ragdoll, "EventAfterStartRecover");
                Assert.That(agent.enabled, Is.True);
                Assert.That(capsule.enabled, Is.True);
                Assert.That(GetField<bool>(driver, "m_RagdollMovementSuspended"), Is.True);

                driver.ApplyPositionUpdate(NetworkNavMeshPositionUpdate.Create(
                    new Vector3(75f, 0f, 75f),
                    180f,
                    3,
                    5f,
                    5f));
                Assert.That(root.transform.position, Is.EqualTo(initialPosition));

                InvokeRagdollEvent(character.Ragdoll, "EventAfterFinishRecover");
                Assert.That(GetField<bool>(driver, "m_RagdollMovementSuspended"), Is.False);

                driver.RequestMoveToPosition(new Vector3(1f, 0f, 1f));
                Assert.That(commandCount, Is.EqualTo(1));
                Assert.That(driver.CurrentSequence, Is.EqualTo(1));
            }
            finally
            {
                bool previousIgnore = LogAssert.ignoreFailingMessages;
                LogAssert.ignoreFailingMessages = true;
                try
                {
                    driver.OnDispose(character);
                }
                finally
                {
                    LogAssert.ignoreFailingMessages = previousIgnore;
                }

                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        private static GameObject CreateCharacterRoot(string name, out Character character)
        {
            var root = new GameObject(name);
            root.SetActive(false);
            character = root.AddComponent<Character>();
            Animator animator = root.AddComponent<Animator>();
            character.Animim.Animator = animator;
            return root;
        }

        private static void InvokeRagdollEvent(Ragdoll ragdoll, string eventName)
        {
            FieldInfo eventField = typeof(Ragdoll).GetField(eventName, INSTANCE_FIELDS);
            Assert.That(eventField, Is.Not.Null, $"Missing GC2 ragdoll event {eventName}");
            ((Action) eventField.GetValue(ragdoll))?.Invoke();
        }

        private static void SetRagdollState(Ragdoll ragdoll, bool value)
        {
            PropertyInfo property = typeof(Ragdoll).GetProperty(
                nameof(Ragdoll.IsRagdoll),
                INSTANCE_FIELDS);
            Assert.That(property, Is.Not.Null);
            property.SetValue(ragdoll, value);
        }

        private static void SetField(object target, string name, object value)
        {
            FieldInfo field = target.GetType().GetField(name, INSTANCE_FIELDS);
            Assert.That(field, Is.Not.Null, $"Missing field {name}");
            field.SetValue(target, value);
        }

        private static T GetField<T>(object target, string name)
        {
            FieldInfo field = target.GetType().GetField(name, INSTANCE_FIELDS);
            Assert.That(field, Is.Not.Null, $"Missing field {name}");
            return (T) field.GetValue(target);
        }
    }
}
