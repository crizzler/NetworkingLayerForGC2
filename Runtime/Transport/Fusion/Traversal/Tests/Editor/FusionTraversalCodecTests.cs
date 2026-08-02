#if GC2_TRAVERSAL
using NUnit.Framework;

namespace Arawn.GameCreator2.Networking.Traversal.Transport.Fusion.Tests
{
    public sealed class FusionTraversalCodecTests
    {
        [Test]
        public void Request_RoundTripsIdentifiers()
        {
            var expected = new NetworkTraversalRequest
            {
                RequestId = 12,
                ActorNetworkId = 31,
                CorrelationId = 77,
                TargetNetworkId = 32,
                TraverseHash = 1001,
                TraverseIdString = "ladder/北",
                ActionIdHash = 1002,
                ActionIdString = "PullUp",
                StateIdHash = 1003,
                StateIdString = "Climbing"
            };

            byte[] payload = FusionValueCodec.Encode(
                expected,
                (writer, value) => writer.Write(value));

            Assert.That(
                FusionValueCodec.TryDecode(
                    payload,
                    (FusionValueReader reader, ref NetworkTraversalRequest value) =>
                        reader.Read(ref value),
                    out NetworkTraversalRequest actual),
                Is.True);
            Assert.That(actual.ActorNetworkId, Is.EqualTo(31));
            Assert.That(actual.TraverseIdString, Is.EqualTo("ladder/北"));
            Assert.That(actual.ActionIdString, Is.EqualTo("PullUp"));
        }

        [Test]
        public void Request_UsesStableFieldOrderAndUtf8LengthPrefixes()
        {
            var value = new NetworkTraversalRequest
            {
                RequestId = 0x1234,
                ActorNetworkId = 0x01020304,
                CorrelationId = 0x11223344,
                TargetNetworkId = 0x55667788,
                Action = TraversalActionType.TryJump,
                TraverseHash = 1,
                TraverseIdString = "A",
                ActionIdHash = 2,
                ActionIdString = string.Empty,
                StateIdHash = 3,
                StateIdString = null,
                ArgsSelfNetworkId = 4,
                ArgsTargetNetworkId = 5
            };

            byte[] payload = FusionValueCodec.Encode(
                value,
                (writer, request) => writer.Write(request));

            CollectionAssert.AreEqual(
                new byte[]
                {
                    0x34, 0x12,
                    0x04, 0x03, 0x02, 0x01,
                    0x44, 0x33, 0x22, 0x11,
                    0x88, 0x77, 0x66, 0x55,
                    0x04,
                    0x01, 0x00, 0x00, 0x00,
                    0x01, 0x00, 0x00, 0x00, 0x41,
                    0x02, 0x00, 0x00, 0x00,
                    0x00, 0x00, 0x00, 0x00,
                    0x03, 0x00, 0x00, 0x00,
                    0xFF, 0xFF, 0xFF, 0xFF,
                    0x04, 0x00, 0x00, 0x00,
                    0x05, 0x00, 0x00, 0x00
                },
                payload);
        }
    }
}
#endif
