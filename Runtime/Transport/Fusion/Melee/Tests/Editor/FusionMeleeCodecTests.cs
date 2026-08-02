#if GC2_MELEE
using NUnit.Framework;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Melee.Transport.Fusion.Tests
{
    public sealed class FusionMeleeCodecTests
    {
        [Test]
        public void HitRequest_RoundTrips()
        {
            var expected = new NetworkMeleeHitRequest
            {
                RequestId = 42,
                ActorNetworkId = 7,
                CorrelationId = 91,
                AttackerNetworkId = 7,
                TargetNetworkId = 8,
                HitPoint = new Vector3(1.25f, -2f, 3.5f)
            };

            byte[] payload = FusionValueCodec.Encode(
                expected,
                (writer, value) => writer.Write(value));

            Assert.That(
                FusionValueCodec.TryDecode(
                    payload,
                    (FusionValueReader reader, ref NetworkMeleeHitRequest value) =>
                        reader.Read(ref value),
                    out NetworkMeleeHitRequest actual),
                Is.True);
            Assert.That(actual.RequestId, Is.EqualTo(expected.RequestId));
            Assert.That(actual.ActorNetworkId, Is.EqualTo(expected.ActorNetworkId));
            Assert.That(actual.HitPoint, Is.EqualTo(expected.HitPoint));
        }

        [Test]
        public void HitRequest_RejectsTruncatedPayload()
        {
            Assert.That(
                FusionValueCodec.TryDecode(
                    new byte[] { 1 },
                    (FusionValueReader reader, ref NetworkMeleeHitRequest value) =>
                        reader.Read(ref value),
                    out _),
                Is.False);
        }

        [Test]
        public void BlockRequest_UsesStableLittleEndianFieldOrder()
        {
            var value = new NetworkBlockRequest
            {
                RequestId = 0x1234,
                ActorNetworkId = 0x01020304,
                CorrelationId = 0x11223344,
                ClientTimestamp = 1f,
                Action = NetworkBlockAction.Lower,
                ShieldHash = 0x55667788
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
                    0x00, 0x00, 0x80, 0x3F,
                    0x01,
                    0x88, 0x77, 0x66, 0x55
                },
                payload);
        }
    }
}
#endif
