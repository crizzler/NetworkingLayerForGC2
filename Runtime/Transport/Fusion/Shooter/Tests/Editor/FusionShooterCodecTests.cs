#if GC2_SHOOTER
using NUnit.Framework;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Shooter.Transport.Fusion.Tests
{
    public sealed class FusionShooterCodecTests
    {
        [Test]
        public void ShotRequest_RoundTrips()
        {
            var expected = new NetworkShotRequest
            {
                RequestId = 5,
                ActorNetworkId = 12,
                CorrelationId = 99,
                ShooterNetworkId = 12,
                MuzzlePosition = new Vector3(4f, 5f, 6f),
                ShotDirection = Vector3.forward,
                WeaponHash = 12345,
                TotalProjectiles = 1
            };

            byte[] payload = FusionValueCodec.Encode(
                expected,
                (writer, value) => writer.Write(value));

            Assert.That(
                FusionValueCodec.TryDecode(
                    payload,
                    (FusionValueReader reader, ref NetworkShotRequest value) =>
                        reader.Read(ref value),
                    out NetworkShotRequest actual),
                Is.True);
            Assert.That(actual.RequestId, Is.EqualTo(expected.RequestId));
            Assert.That(actual.WeaponHash, Is.EqualTo(expected.WeaponHash));
            Assert.That(actual.MuzzlePosition, Is.EqualTo(expected.MuzzlePosition));
        }

        [Test]
        public void ShotRequest_RejectsTrailingData()
        {
            var writer = new FusionValueWriter();
            writer.Write(default(NetworkShotRequest));
            writer.Write((byte)0xFF);

            Assert.That(
                FusionValueCodec.TryDecode(
                    writer.ToArray(),
                    (FusionValueReader reader, ref NetworkShotRequest value) =>
                        reader.Read(ref value),
                    out _),
                Is.False);
        }

        [Test]
        public void SightSwitchBroadcast_UsesStableLittleEndianFieldOrder()
        {
            var value = new NetworkSightSwitchBroadcast
            {
                CharacterNetworkId = 0x01020304,
                WeaponHash = 0x11223344,
                NewSightHash = 0x55667788
            };

            byte[] payload = FusionValueCodec.Encode(
                value,
                (writer, broadcast) => writer.Write(broadcast));

            CollectionAssert.AreEqual(
                new byte[]
                {
                    0x04, 0x03, 0x02, 0x01,
                    0x44, 0x33, 0x22, 0x11,
                    0x88, 0x77, 0x66, 0x55
                },
                payload);
        }
    }
}
#endif
