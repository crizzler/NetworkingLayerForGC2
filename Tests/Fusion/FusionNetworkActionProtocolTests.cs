using System;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Transport.Fusion.Tests
{
    public sealed class FusionNetworkActionProtocolTests
    {
        [Test]
        public void ActionRequest_WireRoundTrip_PreservesTypedPayloadAndIdentity()
        {
            var expected = new NetworkActionRequest
            {
                RequestId = 17,
                ActorNetworkId = 1001,
                CorrelationId = 2002,
                TargetNetworkId = 3003,
                EndpointHash = -3903,
                ActionHash = -4004,
                ActionId = "door.open/\u4f53\ud83d\ude42",
                SchemaVersion = 5,
                Payload = NetworkActionPayload.FromVector3(new Vector3(1.5f, -2f, 9.25f)),
                ExpectedRevision = 77,
                ClientTime = 12.5f
            };

            NetworkActionRequest actual = RoundTrip(expected);

            Assert.That(actual.RequestId, Is.EqualTo(expected.RequestId));
            Assert.That(actual.ActorNetworkId, Is.EqualTo(expected.ActorNetworkId));
            Assert.That(actual.CorrelationId, Is.EqualTo(expected.CorrelationId));
            Assert.That(actual.TargetNetworkId, Is.EqualTo(expected.TargetNetworkId));
            Assert.That(actual.EndpointHash, Is.EqualTo(expected.EndpointHash));
            Assert.That(actual.ActionHash, Is.EqualTo(expected.ActionHash));
            Assert.That(actual.ActionId, Is.EqualTo(expected.ActionId));
            Assert.That(actual.SchemaVersion, Is.EqualTo(expected.SchemaVersion));
            Assert.That(actual.Payload, Is.EqualTo(expected.Payload));
            Assert.That(actual.ExpectedRevision, Is.EqualTo(expected.ExpectedRevision));
            Assert.That(actual.ClientTime, Is.EqualTo(expected.ClientTime));
        }

        [Test]
        public void ActionResponse_WireRoundTrip_PreservesCanonicalResult()
        {
            var expected = new NetworkActionResponse
            {
                RequestId = 18,
                ActorNetworkId = 1002,
                CorrelationId = 2003,
                TargetNetworkId = 3004,
                EndpointHash = -3904,
                ActionHash = 4005,
                ActionId = "chest.amount",
                Authorized = false,
                RejectReason = NetworkActionRejectReason.RevisionConflict,
                CanonicalPayload = NetworkActionPayload.FromNumber(42.75f),
                Revision = 9,
                ServerTime = 31.25f
            };

            NetworkActionResponse actual = RoundTrip(expected);

            Assert.That(actual.RequestId, Is.EqualTo(expected.RequestId));
            Assert.That(actual.ActorNetworkId, Is.EqualTo(expected.ActorNetworkId));
            Assert.That(actual.CorrelationId, Is.EqualTo(expected.CorrelationId));
            Assert.That(actual.TargetNetworkId, Is.EqualTo(expected.TargetNetworkId));
            Assert.That(actual.EndpointHash, Is.EqualTo(expected.EndpointHash));
            Assert.That(actual.ActionHash, Is.EqualTo(expected.ActionHash));
            Assert.That(actual.ActionId, Is.EqualTo(expected.ActionId));
            Assert.That(actual.Authorized, Is.EqualTo(expected.Authorized));
            Assert.That(actual.RejectReason, Is.EqualTo(expected.RejectReason));
            Assert.That(actual.CanonicalPayload, Is.EqualTo(expected.CanonicalPayload));
            Assert.That(actual.Revision, Is.EqualTo(expected.Revision));
            Assert.That(actual.ServerTime, Is.EqualTo(expected.ServerTime));
        }

        [Test]
        public void ActionBroadcast_WireRoundTrip_PreservesReliabilityAndSnapshotMetadata()
        {
            var expected = new NetworkActionBroadcast
            {
                RequestId = 19,
                ActorNetworkId = 1003,
                CorrelationId = 2004,
                TargetNetworkId = 3005,
                EndpointHash = -3905,
                ActionHash = 4006,
                ActionId = "lever.label",
                SchemaVersion = 7,
                EffectKind = NetworkActionEffectKind.PersistentState,
                RecipientPolicy = NetworkActionRecipientPolicy.TargetOwner,
                Reliable = false,
                Payload = NetworkActionPayload.FromString("alpha \ud83d\ude80"),
                Revision = 21,
                AuthorityEpoch = 6,
                ServerTime = 44.5f,
                IsSnapshot = true
            };

            NetworkActionBroadcast actual = RoundTrip(expected);

            Assert.That(actual.RequestId, Is.EqualTo(expected.RequestId));
            Assert.That(actual.ActorNetworkId, Is.EqualTo(expected.ActorNetworkId));
            Assert.That(actual.CorrelationId, Is.EqualTo(expected.CorrelationId));
            Assert.That(actual.TargetNetworkId, Is.EqualTo(expected.TargetNetworkId));
            Assert.That(actual.EndpointHash, Is.EqualTo(expected.EndpointHash));
            Assert.That(actual.ActionHash, Is.EqualTo(expected.ActionHash));
            Assert.That(actual.ActionId, Is.EqualTo(expected.ActionId));
            Assert.That(actual.SchemaVersion, Is.EqualTo(expected.SchemaVersion));
            Assert.That(actual.EffectKind, Is.EqualTo(expected.EffectKind));
            Assert.That(actual.RecipientPolicy, Is.EqualTo(expected.RecipientPolicy));
            Assert.That(actual.Reliable, Is.EqualTo(expected.Reliable));
            Assert.That(actual.Payload, Is.EqualTo(expected.Payload));
            Assert.That(actual.Revision, Is.EqualTo(expected.Revision));
            Assert.That(actual.AuthorityEpoch, Is.EqualTo(expected.AuthorityEpoch));
            Assert.That(actual.ServerTime, Is.EqualTo(expected.ServerTime));
            Assert.That(actual.IsSnapshot, Is.EqualTo(expected.IsSnapshot));
        }

        [Test]
        public void ActionSnapshot_WireRoundTrip_PreservesNestedEntriesAndEmptyArrays()
        {
            var entry = new NetworkActionBroadcast
            {
                TargetNetworkId = 77,
                EndpointHash = 78,
                ActionHash = 88,
                ActionId = "door.state",
                SchemaVersion = 1,
                EffectKind = NetworkActionEffectKind.PersistentState,
                RecipientPolicy = NetworkActionRecipientPolicy.RelevantObservers,
                Reliable = true,
                Payload = NetworkActionPayload.FromBoolean(true),
                Revision = 3,
                AuthorityEpoch = 4,
                ServerTime = 5f,
                IsSnapshot = true
            };
            var expected = new NetworkActionSnapshot
            {
                Entries = new[] { entry },
                AuthorityEpoch = 4,
                ServerTime = 5f
            };

            NetworkActionSnapshot actual = RoundTrip(expected);
            Assert.That(actual.Entries, Has.Length.EqualTo(1));
            Assert.That(actual.Entries[0].ActionId, Is.EqualTo(entry.ActionId));
            Assert.That(actual.Entries[0].EndpointHash, Is.EqualTo(entry.EndpointHash));
            Assert.That(actual.Entries[0].Payload, Is.EqualTo(entry.Payload));
            Assert.That(actual.AuthorityEpoch, Is.EqualTo(expected.AuthorityEpoch));
            Assert.That(actual.ServerTime, Is.EqualTo(expected.ServerTime));

            NetworkActionSnapshot empty = RoundTrip(new NetworkActionSnapshot
            {
                Entries = Array.Empty<NetworkActionBroadcast>(),
                AuthorityEpoch = 9,
                ServerTime = 10f
            });
            Assert.That(empty.Entries, Is.Empty);
        }

        [Test]
        public void NetworkActions_ModuleId_IsUniqueAndBridgeUsesIt()
        {
            ushort[] moduleIds = typeof(FusionModuleIds)
                .GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(field => field.IsLiteral && field.FieldType == typeof(ushort))
                .Select(field => (ushort)field.GetRawConstantValue())
                .ToArray();

            Assert.That(FusionModuleIds.NetworkActions, Is.EqualTo(13));
            Assert.That(moduleIds.Distinct().Count(), Is.EqualTo(moduleIds.Length),
                "Every Fusion gameplay module must have a unique wire id.");

            PropertyInfo moduleId = typeof(FusionNetworkActionTransportBridge).GetProperty(
                "ModuleId", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(moduleId, Is.Not.Null);
            var gameObject = new GameObject("Fusion Action Bridge Module Test");
            try
            {
                FusionNetworkActionTransportBridge bridge =
                    gameObject.AddComponent<FusionNetworkActionTransportBridge>();
                Assert.That((ushort)moduleId.GetValue(bridge),
                    Is.EqualTo(FusionModuleIds.NetworkActions));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void FusionWizard_CreatesAndValidatesNetworkActionInfrastructure()
        {
            string shared = ReadSource(
                "Editor/GC2SceneSetupShared.cs");
            StringAssert.Contains("typeof(NetworkActionManager)", shared);

            string wizard = ReadSource(
                "Editor/Transport/Fusion/FusionSceneSetupWizard.cs");
            StringAssert.Contains("FusionNetworkActionBridgeType", wizard);
            StringAssert.Contains("Fusion Network Actions Bridge", wizard);
            StringAssert.Contains("EnsureCoreBridges", wizard);

            string validation = ReadSource(
                "Editor/Transport/Fusion/FusionSceneSetupValidation.cs");
            StringAssert.Contains("ValidateDuplicate<NetworkActionManager>", validation);
            StringAssert.Contains("RequireSingle<NetworkActionManager>", validation);
            StringAssert.Contains("FusionNetworkActionTransportBridge", validation);
        }

        [Test]
        public void ActionBridge_GatesClientRequestsUntilGameplaySnapshotIsReady()
        {
            string actionBridge = ReadSource(
                "Runtime/Transport/Fusion/Actions/FusionNetworkActionTransportBridge.cs");
            StringAssert.Contains("bridge.IsLocalGameplayReady", actionBridge);
            StringAssert.Contains("RejectLocalActionRequest", actionBridge);
            StringAssert.Contains("NetworkActionRejectReason.NotRunning", actionBridge);

            string transportBridge = ReadSource(
                "Runtime/Transport/Fusion/FusionTransportBridge.cs");
            StringAssert.Contains("public override bool IsLocalGameplayReady", transportBridge);
            StringAssert.Contains(
                "m_LocalSnapshotCompletedEpoch == m_AuthorityEpoch",
                transportBridge);
        }

        private static T RoundTrip<T>(T value)
        {
            return FusionWireSerializer.Deserialize<T>(
                FusionWireSerializer.Serialize(value));
        }

        private static string ReadSource(string relativePath)
        {
            string path = Path.Combine(
                Application.dataPath,
                "Arawn/NetworkingLayerForGC2",
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.That(File.Exists(path), Is.True, $"Missing source file: {path}");
            return File.ReadAllText(path);
        }
    }
}
