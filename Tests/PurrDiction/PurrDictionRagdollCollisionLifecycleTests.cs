using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Transport.PurrNet.PurrDiction.Tests
{
    [Category("GC2Networking.Ragdoll")]
    public sealed class PurrDictionRagdollCollisionLifecycleTests
    {
        [Test]
        public void CharacterControllerBackend_UsesSharedRoleSwapSafeGuard()
        {
            string source = ReadSource();
            string driver = ExtractType(source, "public sealed class UnitDriverPurrDiction");

            StringAssert.Contains(
                "NetworkRagdollPhysicsGuard.Register(character, m_Controller)",
                driver);
            StringAssert.DoesNotContain("EventBeforeStartRagdoll", driver);
            StringAssert.DoesNotContain("EventAfterStartRecover", driver);
            StringAssert.DoesNotContain(
                "NetworkRagdollPhysicsGuard.Unregister",
                driver,
                "The shared CharacterController registration must survive backend role swaps.");

            string simulation = ExtractMethod(
                source,
                "protected override void Simulate(GC2PurrDictionInput input");
            AssertAppearsBefore(
                simulation,
                "GameCreatorCharacter.Ragdoll?.IsRagdoll == true",
                "SetUnityState(state)",
                "PurrDiction must not resume root simulation during GC2's get-up phase.");
        }

        [Test]
        public void NavMeshBackend_GuardsAgentAndCapsuleThroughFullRecovery()
        {
            string source = ReadSource("PurrDictionNetworkNavmeshController.cs");
            string driver = ExtractType(
                source,
                "public sealed class UnitDriverPurrDictionNavmesh");

            StringAssert.Contains(
                "NetworkRagdollPhysicsGuard.Register(character, m_Agent)",
                driver);
            StringAssert.Contains(
                "NetworkRagdollPhysicsGuard.Register(character, m_Capsule)",
                driver);
            StringAssert.Contains(
                "NetworkRagdollPhysicsGuard.Unregister(character, rootController)",
                driver);
            StringAssert.Contains("EventAfterFinishRecover", driver);
            StringAssert.DoesNotContain(
                "EventAfterStartRecover",
                driver,
                "Only physical collision restores after StartRecover; commands and root " +
                "writers remain suspended until the get-up gesture finishes.");
            StringAssert.Contains("if (IsRagdollMovementSuspended) return", driver);

            string simulation = ExtractMethod(source, "protected override void Simulate(");
            AssertAppearsBefore(
                simulation,
                "GameCreatorCharacter.Ragdoll?.IsRagdoll == true",
                "SetUnityState(state)",
                "Predicted NavMesh state must not move the GC2 root during recovery.");
        }

        private static string ReadSource()
        {
            return ReadSource("PurrDictionNetworkCharacterController.cs");
        }

        private static string ReadSource(string fileName)
        {
            string path = Path.Combine(
                Application.dataPath,
                "Arawn/NetworkingLayerForGC2/Runtime/Transport/PurrNet/PurrDiction/" +
                fileName);
            Assert.That(File.Exists(path), Is.True, $"Missing source contract: {path}");
            return File.ReadAllText(path);
        }

        private static string ExtractType(string source, string signature) =>
            ExtractBlock(source, signature);

        private static string ExtractMethod(string source, string signature) =>
            ExtractBlock(source, signature);

        private static string ExtractBlock(string source, string signature)
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

            Assert.Fail($"Unclosed source contract: {signature}");
            return string.Empty;
        }

        private static void AssertAppearsBefore(
            string source,
            string first,
            string second,
            string message)
        {
            int firstIndex = source.IndexOf(first, StringComparison.Ordinal);
            int secondIndex = source.IndexOf(second, StringComparison.Ordinal);
            Assert.That(firstIndex, Is.GreaterThanOrEqualTo(0), first);
            Assert.That(secondIndex, Is.GreaterThanOrEqualTo(0), second);
            Assert.That(firstIndex, Is.LessThan(secondIndex), message);
        }
    }
}
