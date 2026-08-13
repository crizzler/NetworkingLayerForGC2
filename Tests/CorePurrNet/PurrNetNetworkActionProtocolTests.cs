using System;
using System.IO;
using System.Reflection;
using Arawn.GameCreator2.Networking.Transport.PurrNet;
using NUnit.Framework;
using PurrNet;
using PurrNet.Packing;
using PurrNet.Transports;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.CorePurrNet.Tests
{
    public sealed class PurrNetNetworkActionProtocolTests
    {
        [Test]
        public void ActionRequest_PackerRoundTrip_PreservesEveryField()
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

            NetworkActionRequest actual = RoundTrip(
                expected,
                PurrNetNetworkActionValuePackers.Write,
                PurrNetNetworkActionValuePackers.Read);

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
        public void ActionResponse_PackerRoundTrip_PreservesCanonicalResult()
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

            NetworkActionResponse actual = RoundTrip(
                expected,
                PurrNetNetworkActionValuePackers.Write,
                PurrNetNetworkActionValuePackers.Read);

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
        public void ActionBroadcast_PackerRoundTrip_PreservesReliabilityAndSnapshotMetadata()
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

            NetworkActionBroadcast actual = RoundTrip(
                expected,
                PurrNetNetworkActionValuePackers.Write,
                PurrNetNetworkActionValuePackers.Read);

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
        public void ActionSnapshot_PackerRoundTrip_PreservesEntriesAndEmptyArrays()
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

            NetworkActionSnapshot actual = RoundTrip(
                expected,
                PurrNetNetworkActionValuePackers.Write,
                PurrNetNetworkActionValuePackers.Read);
            Assert.That(actual.Entries, Has.Length.EqualTo(1));
            Assert.That(actual.Entries[0].ActionId, Is.EqualTo(entry.ActionId));
            Assert.That(actual.Entries[0].EndpointHash, Is.EqualTo(entry.EndpointHash));
            Assert.That(actual.Entries[0].Payload, Is.EqualTo(entry.Payload));
            Assert.That(actual.AuthorityEpoch, Is.EqualTo(expected.AuthorityEpoch));
            Assert.That(actual.ServerTime, Is.EqualTo(expected.ServerTime));

            NetworkActionSnapshot empty = RoundTrip(
                new NetworkActionSnapshot
                {
                    Entries = Array.Empty<NetworkActionBroadcast>(),
                    AuthorityEpoch = 9,
                    ServerTime = 10f
                },
                PurrNetNetworkActionValuePackers.Write,
                PurrNetNetworkActionValuePackers.Read);
            Assert.That(empty.Entries, Is.Empty);
        }

        [Test]
        public void ActionStrings_PackerRejectsOversizedAndMalformedUnicode()
        {
            var oversizedAction = new NetworkActionRequest
            {
                ActionId = new string(
                    'a',
                    NetworkActionManager.MaxActionIdCharacters + 1),
                Payload = NetworkActionPayload.None
            };
            using (BitPacker packer = BitPackerPool.Get())
            {
                Assert.Throws<ArgumentOutOfRangeException>(() =>
                    PurrNetNetworkActionValuePackers.Write(packer, oversizedAction));
            }

            NetworkActionPayload oversizedPayload = NetworkActionPayload.FromString(
                new string(
                    'b',
                    NetworkActionManager.MaxPayloadStringCharacters + 1));
            using (BitPacker packer = BitPackerPool.Get())
            {
                Assert.Throws<ArgumentOutOfRangeException>(() =>
                    PurrNetNetworkActionValuePackers.Write(packer, oversizedPayload));
            }

            var malformedUnicode = new NetworkActionRequest
            {
                ActionId = "invalid-\ud800",
                Payload = NetworkActionPayload.None
            };
            using (BitPacker packer = BitPackerPool.Get())
            {
                Assert.Throws<ArgumentException>(() =>
                    PurrNetNetworkActionValuePackers.Write(packer, malformedUnicode));
            }
        }

        [Test]
        public void ActionPayload_PackerRejectsMalformedUtf8OnRead()
        {
            using BitPacker packer = BitPackerPool.Get();
            packer.Write((byte)NetworkActionPayloadType.String);
            packer.Write(false);
            packer.Write(0f);
            packer.Write(true);
            packer.Write((ushort)2);
            packer.WriteBytes(new byte[] { 0xc3, 0x28 });
            packer.ResetPositionAndMode(true);

            NetworkActionPayload value = default;
            Assert.Throws<InvalidDataException>(() =>
                PurrNetNetworkActionValuePackers.Read(packer, ref value));
        }

        [Test]
        public void ActionPackers_DoNotExposeAPurrNetGlobalStringWriterCandidate()
        {
            // PurrNet ILPP treats any static (BitPacker, T) method as a global Packer<T>
            // writer, including private helpers. A bounded ActionId helper with a string
            // second parameter would therefore replace PurrNet's authentication string
            // writer while leaving its normal reader installed.
            MethodInfo[] candidates = typeof(PurrNetNetworkActionValuePackers).GetMethods(
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            foreach (MethodInfo method in candidates)
            {
                ParameterInfo[] parameters = method.GetParameters();
                bool looksLikeGlobalStringWriter =
                    parameters.Length == 2 &&
                    parameters[0].ParameterType == typeof(BitPacker) &&
                    parameters[1].ParameterType == typeof(string) &&
                    method.ReturnType == typeof(void);
                Assert.That(looksLikeGlobalStringWriter, Is.False,
                    $"{method.Name} would be registered as PurrNet's global string writer.");
            }
        }

        [Test]
        public void ActionSnapshot_PackerRejectsEntryCountsAboveProtocolLimit()
        {
            var oversized = new NetworkActionSnapshot
            {
                Entries = new NetworkActionBroadcast[
                    PurrNetNetworkActionValuePackers.MaxSnapshotEntries + 1]
            };
            using (BitPacker packer = BitPackerPool.Get())
            {
                Assert.Throws<ArgumentOutOfRangeException>(() =>
                    PurrNetNetworkActionValuePackers.Write(packer, oversized));
            }

            using (BitPacker packer = BitPackerPool.Get())
            {
                packer.Write(true);
                packer.Write((ushort)(
                    PurrNetNetworkActionValuePackers.MaxSnapshotEntries + 1));
                packer.ResetPositionAndMode(true);

                NetworkActionSnapshot decoded = default;
                Assert.Throws<InvalidDataException>(() =>
                    PurrNetNetworkActionValuePackers.Read(packer, ref decoded));
            }
        }

        [Test]
        public void BridgeChannelPolicy_UsesReliableForStateAndConfiguredFlagForTransientEvents()
        {
            var gameObject = new GameObject("PurrNet Action Bridge Channel Test");
            try
            {
                PurrNetNetworkActionTransportBridge bridge =
                    gameObject.AddComponent<PurrNetNetworkActionTransportBridge>();
                MethodInfo resolve = typeof(PurrNetNetworkActionTransportBridge).GetMethod(
                    "ResolveChannel", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.That(resolve, Is.Not.Null);

                var persistent = new NetworkActionBroadcast
                {
                    EffectKind = NetworkActionEffectKind.PersistentState,
                    Reliable = false
                };
                var reliableEvent = new NetworkActionBroadcast
                {
                    EffectKind = NetworkActionEffectKind.TransientEvent,
                    Reliable = true
                };
                var unreliableEvent = new NetworkActionBroadcast
                {
                    EffectKind = NetworkActionEffectKind.TransientEvent,
                    Reliable = false
                };

                Assert.That(InvokeResolve(resolve, bridge, persistent),
                    Is.EqualTo(Channel.ReliableOrdered));
                Assert.That(InvokeResolve(resolve, bridge, reliableEvent),
                    Is.EqualTo(Channel.ReliableOrdered));
                Assert.That(InvokeResolve(resolve, bridge, unreliableEvent),
                    Is.EqualTo(Channel.UnreliableSequenced));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void PurrNetWizard_CreatesAndConfiguresNetworkActionBridge()
        {
            string path = Path.Combine(
                Application.dataPath,
                "Arawn/NetworkingLayerForGC2/Editor/Transport/PurrNet/" +
                "PurrNetSceneSetupWizard.cs");
            Assert.That(File.Exists(path), Is.True, $"Missing wizard source: {path}");
            string source = File.ReadAllText(path);

            StringAssert.Contains("EnsureNetworkActionBridge(root, manager, coreBridge)", source);
            StringAssert.Contains("FindOrCreateComponent<PurrNetNetworkActionTransportBridge>", source);
            StringAssert.Contains("PurrNet Network Actions Bridge", source);
            StringAssert.Contains("AssignObjectReference(so, \"m_NetworkManager\", manager)", source);
            StringAssert.Contains("AssignObjectReference(so, \"m_CoreBridge\", coreBridge)", source);
        }

        [Test]
        public void AuthoritySenderFilter_RejectsPeerEvenOnHostCodePath()
        {
            MethodInfo filter = typeof(PurrNetNetworkActionTransportBridge).GetMethod(
                "IsAuthoritativeSender",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(filter, Is.Not.Null);

            Assert.That((bool)filter.Invoke(null, new object[] { PlayerID.Server }), Is.True);
            Assert.That((bool)filter.Invoke(
                null, new object[] { new PlayerID(7, false) }), Is.False);
        }

        private delegate void ReadValue<T>(BitPacker packer, ref T value);

        private static T RoundTrip<T>(
            T value,
            Action<BitPacker, T> write,
            ReadValue<T> read)
        {
            using BitPacker packer = BitPackerPool.Get();
            write.Invoke(packer, value);
            packer.ResetPositionAndMode(true);

            T result = default;
            read.Invoke(packer, ref result);
            return result;
        }

        private static Channel InvokeResolve(
            MethodInfo method,
            PurrNetNetworkActionTransportBridge bridge,
            NetworkActionBroadcast broadcast)
        {
            return (Channel)method.Invoke(bridge, new object[] { broadcast });
        }
    }
}
