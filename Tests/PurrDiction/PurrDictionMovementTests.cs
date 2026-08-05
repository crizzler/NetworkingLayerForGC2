using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Transport.PurrNet.PurrDiction.Tests
{
    /// <summary>
    /// Pure input-history regressions for the optional PurrDiction backend. No prediction manager
    /// or transport session is required, so failures point at tick-input semantics rather than
    /// network timing.
    /// </summary>
    public sealed class PurrDictionMovementTests
    {
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
        public void DirectionalExtrapolation_HoldsContinuousIntentButClearsEveryOneShot()
        {
            PurrDictionNetworkCharacterController controller =
                CreateInactiveComponent<PurrDictionNetworkCharacterController>("Directional Extrapolation");
            var input = new GC2PurrDictionInput
            {
                moveDirection = new Vector3(0.25f, 0f, -0.75f),
                rotationY = 217f,
                rootMotionDelta = new Vector3(1f, 2f, 3f),
                rootMotionWeight = 0.8f,
                gravityInfluence = 0.35f,
                flags = GC2PurrDictionInput.FLAG_JUMP |
                        GC2PurrDictionInput.FLAG_RESET_VERTICAL |
                        GC2PurrDictionInput.FLAG_UPDATE_KINEMATICS |
                        GC2PurrDictionInput.FLAG_FORCE_GROUNDED,
                externalPose = CreateExternalPose()
            };

            GC2PurrDictionInput extrapolated = InvokeByRef(
                controller,
                "ModifyExtrapolatedInput",
                input);

            Assert.That(extrapolated.moveDirection, Is.EqualTo(input.moveDirection));
            Assert.That(extrapolated.rotationY, Is.EqualTo(input.rotationY));
            Assert.That(extrapolated.gravityInfluence, Is.EqualTo(input.gravityInfluence));
            Assert.That(
                extrapolated.flags,
                Is.EqualTo(
                    GC2PurrDictionInput.FLAG_UPDATE_KINEMATICS |
                    GC2PurrDictionInput.FLAG_FORCE_GROUNDED));
            Assert.That(extrapolated.rootMotionDelta, Is.EqualTo(Vector3.zero));
            Assert.That(extrapolated.rootMotionWeight, Is.Zero);
            Assert.That(extrapolated.externalPose.HasCommand, Is.False);
            Assert.That(extrapolated.externalPose.sequence, Is.Zero);
            Assert.That(extrapolated.externalPose.sourceTick, Is.Zero);
        }

        [Test]
        public void NavMeshExtrapolation_DoesNotReplayCommandRootMotionOrExternalPose()
        {
            PurrDictionNetworkNavmeshController controller =
                CreateInactiveComponent<PurrDictionNetworkNavmeshController>("NavMesh Extrapolation");
            var input = GC2PurrDictionNavMeshInput.Create(
                commandType: 7,
                sequence: 52,
                target: new Vector3(8f, 0f, -4f),
                flags: GC2PurrDictionNavMeshInput.FLAG_STOP_IMMEDIATE);
            input.rootMotionDelta = new Vector3(0.5f, 0f, 1.25f);
            input.rootMotionWeight = 1f;
            input.externalPose = CreateExternalPose();

            GC2PurrDictionNavMeshInput extrapolated = InvokeByRef(
                controller,
                "ModifyExtrapolatedInput",
                input);

            Assert.That(extrapolated.HasCommand, Is.False);
            Assert.That(extrapolated.commandType, Is.Zero);
            Assert.That(extrapolated.sequence, Is.Zero);
            Assert.That(extrapolated.target, Is.EqualTo(Vector3.zero));
            Assert.That(extrapolated.flags, Is.Zero);
            Assert.That(extrapolated.rootMotionDelta, Is.EqualTo(Vector3.zero));
            Assert.That(extrapolated.rootMotionWeight, Is.Zero);
            Assert.That(extrapolated.externalPose.HasCommand, Is.False);
            Assert.That(extrapolated.externalPose.sequence, Is.Zero);
        }

        [Test]
        public void DirectionalSanitizer_NormalizesContinuousInputAndDropsMalformedExternalPose()
        {
            PurrDictionNetworkCharacterController controller =
                CreateInactiveComponent<PurrDictionNetworkCharacterController>("Directional Sanitizer");
            SetPrivateFieldInHierarchy(
                controller,
                "m_EnableServerSecurityValidation",
                false);
            var input = new GC2PurrDictionInput
            {
                moveDirection = new Vector3(2f, 0f, 0f),
                rotationY = -30f,
                rootMotionDelta = new Vector3(8f, 0f, 0f),
                rootMotionWeight = 4f,
                gravityInfluence = 3f,
                flags = GC2PurrDictionInput.FLAG_UPDATE_KINEMATICS,
                externalPose = new PurrDictionExternalPoseCommand
                {
                    sequence = 0,
                    sourceTick = 3,
                    flags = PurrDictionExternalPoseCommand.FLAG_SCALE,
                    scale = Vector3.one
                }
            };

            GC2PurrDictionInput sanitized = InvokeByRef(controller, "SanitizeInput", input);

            Assert.That(sanitized.moveDirection, Is.EqualTo(Vector3.right));
            Assert.That(sanitized.rotationY, Is.EqualTo(330f).Within(0.001f));
            Assert.That(sanitized.gravityInfluence, Is.EqualTo(1f));
            Assert.That(sanitized.rootMotionDelta.magnitude, Is.EqualTo(4f).Within(0.001f));
            Assert.That(sanitized.rootMotionWeight, Is.EqualTo(1f));
            Assert.That(sanitized.externalPose.HasCommand, Is.False);
            Assert.That(sanitized.externalPose.sequence, Is.Zero);
        }

        [Test]
        public void PendingExternalPose_AddScaleCoalescesUsingGc2AdditiveSemantics()
        {
            Type pendingType = typeof(PurrDictionExternalPoseCommand).Assembly.GetType(
                "Arawn.GameCreator2.Networking.Transport.PurrNet.PurrDiction." +
                "PurrDictionPendingExternalPose");
            Assert.That(pendingType, Is.Not.Null);
            object pending = Activator.CreateInstance(
                pendingType,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                Array.Empty<object>(),
                null);

            Invoke(pending, "QueueScale", new Vector3(2f, 3f, 4f), true);
            Invoke(pending, "QueueScale", new Vector3(0.5f, -1f, 2f), false);

            MethodInfo consume = GetMethod(pending, "TryConsume");
            object[] arguments = { 91UL, default(PurrDictionExternalPoseCommand) };
            Assert.That((bool)consume.Invoke(pending, arguments), Is.True);
            PurrDictionExternalPoseCommand command =
                (PurrDictionExternalPoseCommand)arguments[1];

            Assert.That(command.HasScale, Is.True);
            Assert.That(command.ScaleIsAbsolute, Is.True);
            Assert.That(command.scale, Is.EqualTo(new Vector3(2.5f, 2f, 6f)));
            Assert.That(command.sourceTick, Is.EqualTo(91u));
            Assert.That(command.sequence, Is.Not.Zero);
            Assert.That((bool)consume.Invoke(
                pending,
                new object[] { 92UL, default(PurrDictionExternalPoseCommand) }), Is.False);
        }

        private T CreateInactiveComponent<T>(string name) where T : Component
        {
            var gameObject = new GameObject(name);
            gameObject.SetActive(false);
            m_Cleanup.Add(gameObject);
            return gameObject.AddComponent<T>();
        }

        private static PurrDictionExternalPoseCommand CreateExternalPose()
        {
            return new PurrDictionExternalPoseCommand
            {
                sequence = 12,
                sourceTick = 44,
                flags = PurrDictionExternalPoseCommand.FLAG_POSITION |
                        PurrDictionExternalPoseCommand.FLAG_POSITION_ABSOLUTE |
                        PurrDictionExternalPoseCommand.FLAG_ROTATION |
                        PurrDictionExternalPoseCommand.FLAG_ROTATION_ABSOLUTE |
                        PurrDictionExternalPoseCommand.FLAG_SCALE |
                        PurrDictionExternalPoseCommand.FLAG_SCALE_ABSOLUTE |
                        PurrDictionExternalPoseCommand.FLAG_TELEPORT,
                position = new Vector3(4f, 5f, 6f),
                rotation = Quaternion.Euler(0f, 80f, 0f),
                scale = new Vector3(1.5f, 1.5f, 1.5f)
            };
        }

        private static T InvokeByRef<T>(object target, string methodName, T input)
        {
            object[] arguments = { input };
            GetMethod(target, methodName).Invoke(target, arguments);
            return (T)arguments[0];
        }

        private static void Invoke(object target, string methodName, params object[] arguments)
        {
            GetMethod(target, methodName).Invoke(target, arguments);
        }

        private static void SetPrivateFieldInHierarchy(object target, string fieldName, object value)
        {
            for (Type type = target.GetType(); type != null; type = type.BaseType)
            {
                FieldInfo field = type.GetField(
                    fieldName,
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (field == null) continue;
                field.SetValue(target, value);
                return;
            }

            Assert.Fail($"Missing field {fieldName}");
        }

        private static MethodInfo GetMethod(object target, string methodName)
        {
            MethodInfo method = target.GetType().GetMethod(
                methodName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null, $"Missing method {methodName}");
            return method;
        }
    }
}
