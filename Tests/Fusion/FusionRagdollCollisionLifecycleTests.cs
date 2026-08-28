using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Transport.Fusion.Tests
{
    [Category("GC2Networking.Ragdoll")]
    public sealed class FusionRagdollCollisionLifecycleTests
    {
        [Test]
        public void FusionNative_UsesSharedGuardAndSuspendsEveryRagdollSimulationPhase()
        {
            string source = ReadSource(
                "Arawn/NetworkingLayerForGC2/Runtime/Transport/Fusion/" +
                "FusionNativeCharacterDriver.cs");

            StringAssert.Contains(
                "NetworkRagdollPhysicsGuard.Register(character, m_Controller)",
                source);
            StringAssert.DoesNotContain(
                "NetworkRagdollPhysicsGuard.Unregister",
                source,
                "The shared CharacterController registration must survive Fusion role swaps.");

            string update = ExtractMethod(source, "public override void OnUpdate()");
            AssertAppearsBefore(
                update,
                "Character.Ragdoll?.IsRagdoll == true",
                "RefreshControllerShape()");

            string simulate = ExtractMethod(source, "internal void Simulate(");
            AssertAppearsBefore(
                simulate,
                "Character.Ragdoll?.IsRagdoll == true",
                "RefreshControllerShape()");
            StringAssert.Contains("!m_Controller.enabled", simulate);
            StringAssert.Contains("m_MoveVelocity = Vector3.zero", simulate);
        }

        [Test]
        public void FusionAdvancedKcc_RagdollUsesImmediateLocalShapeOverride()
        {
            string driver = ReadSource(
                "Arawn/NetworkingLayerForGC2.FusionAdvancedKCC/Runtime/" +
                "FusionKccCharacterDriver.cs");
            string motor = ReadSource(
                "Arawn/NetworkingLayerForGC2.FusionAdvancedKCC/Runtime/" +
                "FusionKccMotorBody.cs");

            StringAssert.Contains("EventBeforeStartRagdoll", driver);
            StringAssert.Contains("EventAfterStartRecover", driver);
            StringAssert.Contains("if (m_RagdollCollisionSuspended) return", driver);
            StringAssert.Contains(
                "m_Motor?.SetRagdollCollisionSuspended(true)",
                driver);
            StringAssert.DoesNotContain(
                "SetCollisionEnabled(false)",
                driver,
                "Ragdoll suspension must be immediate and local, not a delayed network input.");

            string setter = ExtractMethod(
                motor,
                "internal void SetRagdollCollisionSuspended(bool suspended)");
            StringAssert.Contains("m_RagdollCollisionSuspended = suspended", setter);
            StringAssert.Contains("SynchronizeSimulationCapsule()", setter);

            string synchronize = ExtractMethod(
                motor,
                "private void SynchronizeSimulationCapsule()");
            StringAssert.Contains(
                "m_CollisionEnabled && !m_RagdollCollisionSuspended",
                synchronize);
            StringAssert.Contains("EKCCShape.None", synchronize);

            StringAssert.Contains(
                "m_RagdollCollisionSuspended ||",
                motor,
                "The pre-IsRagdoll callback window must already suppress KCC root simulation.");

            string prepare = ExtractMethod(motor, "private void PrepareKccTick(");
            StringAssert.Contains(
                "collisionEnabled && !m_RagdollCollisionSuspended",
                prepare);
            StringAssert.Contains(
                "collisionChanged || m_RagdollCollisionSuspended",
                prepare,
                "Every Fusion tick must reassert Shape.None while physical ragdoll is active.");
            AssertAppearsBefore(
                prepare,
                "m_CollisionEnabled = collisionEnabled",
                "effectiveCollisionEnabled",
                "The semantic collision value must remain independent from the local override.");

            string fixedUpdate = ExtractMethod(
                motor,
                "public override void FixedUpdateNetwork()");
            AssertAppearsBefore(
                fixedUpdate,
                "if (IsRagdollActive)",
                "m_Kcc.ManualFixedUpdate()",
                "KCC fixed simulation must remain frozen through the get-up phase.");

            string render = ExtractMethod(motor, "private void RenderInternal()");
            AssertAppearsBefore(
                render,
                "if (IsRagdollActive)",
                "m_Kcc.ManualRenderUpdate()",
                "KCC render simulation must not overwrite the GC2 ragdoll/recovery root.");

            string sharedOwnerPump = ExtractMethod(
                motor,
                "public void SimulateSharedLogicalOwnerProxyTick(");
            StringAssert.Contains("IsRagdollActive", sharedOwnerPump);
        }

        private static string ReadSource(string assetRelativePath)
        {
            string path = Path.Combine(Application.dataPath, assetRelativePath);
            Assert.That(File.Exists(path), Is.True, $"Missing source contract: {path}");
            return File.ReadAllText(path);
        }

        private static string ExtractMethod(string source, string signature)
        {
            int signatureIndex = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.That(signatureIndex, Is.GreaterThanOrEqualTo(0), signature);

            int openBrace = source.IndexOf('{', signatureIndex);
            Assert.That(openBrace, Is.GreaterThanOrEqualTo(0), signature);
            int depth = 0;
            for (int i = openBrace; i < source.Length; ++i)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}' && --depth == 0)
                {
                    return source.Substring(signatureIndex, i - signatureIndex + 1);
                }
            }

            Assert.Fail($"Unclosed method contract: {signature}");
            return string.Empty;
        }

        private static void AssertAppearsBefore(
            string source,
            string first,
            string second,
            string message = null)
        {
            int firstIndex = source.IndexOf(first, StringComparison.Ordinal);
            int secondIndex = source.IndexOf(second, StringComparison.Ordinal);
            Assert.That(firstIndex, Is.GreaterThanOrEqualTo(0), first);
            Assert.That(secondIndex, Is.GreaterThanOrEqualTo(0), second);
            Assert.That(firstIndex, Is.LessThan(secondIndex), message ?? $"{first} before {second}");
        }
    }
}
