using System;
using System.Net;
using NUnit.Framework;

namespace Arawn.GameCreator2.Networking.Transport.PurrNet.Lobby.Tests
{
    public sealed class PurrNetLanDiscoveryTests
    {
        [Test]
        public void AdvertisementRoundTripPreservesBoundedMetadataAndSource()
        {
            Guid id = Guid.NewGuid();
            var advertisement = new PurrNetLanAdvertisement(
                id,
                "Friendly Server",
                "com.example.game",
                "1.2.3",
                7,
                5000,
                2,
                8,
                true,
                true);

            Assert.That(
                PurrNetLanDiscovery.TryEncodeAdvertisement(
                    advertisement,
                    out byte[] bytes,
                    out string error),
                Is.True,
                error);
            Assert.That(bytes.Length, Is.LessThanOrEqualTo(
                PurrNetLanDiscovery.MaximumDatagramBytes));

            var source = new IPEndPoint(IPAddress.Parse("192.168.10.42"), 47777);
            Assert.That(
                PurrNetLanDiscovery.TryDecode(
                    bytes,
                    bytes.Length,
                    source,
                    out PurrNetLanPacket packet),
                Is.True);
            Assert.That(packet.Kind, Is.EqualTo(PurrNetLanPacketKind.Advertisement));
            Assert.That(packet.Source, Is.EqualTo(source));
            Assert.That(packet.Advertisement.SessionId, Is.EqualTo(id));
            Assert.That(packet.Advertisement.SessionName, Is.EqualTo("Friendly Server"));
            Assert.That(packet.Advertisement.ProductId, Is.EqualTo("com.example.game"));
            Assert.That(packet.Advertisement.BuildId, Is.EqualTo("1.2.3"));
            Assert.That(packet.Advertisement.ProtocolVersion, Is.EqualTo(7));
            Assert.That(packet.Advertisement.GamePort, Is.EqualTo(5000));
            Assert.That(packet.Advertisement.PlayerCount, Is.EqualTo(2));
            Assert.That(packet.Advertisement.MaxPlayers, Is.EqualTo(8));
            Assert.That(packet.Advertisement.IsOpen, Is.True);
            Assert.That(packet.Advertisement.IsVisible, Is.True);
        }

        [Test]
        public void DecoderRejectsTruncatedAndOversizedPayloads()
        {
            var advertisement = new PurrNetLanAdvertisement(
                Guid.NewGuid(),
                "Server",
                "product",
                "build",
                1,
                5000,
                1,
                4,
                true,
                true);
            Assert.That(
                PurrNetLanDiscovery.TryEncodeAdvertisement(
                    advertisement,
                    out byte[] bytes,
                    out string error),
                Is.True,
                error);

            var source = new IPEndPoint(IPAddress.Loopback, 47777);
            Assert.That(
                PurrNetLanDiscovery.TryDecode(
                    bytes,
                    bytes.Length - 1,
                    source,
                    out _),
                Is.False);

            byte[] oversized = new byte[PurrNetLanDiscovery.MaximumDatagramBytes + 1];
            Array.Copy(bytes, oversized, bytes.Length);
            Assert.That(
                PurrNetLanDiscovery.TryDecode(
                    oversized,
                    oversized.Length,
                    source,
                    out _),
                Is.False);

            // Name length starts at byte 33 in protocol version 1. A hostile
            // length over the 128-byte bound must be rejected before allocation.
            byte[] hostileLength = (byte[])bytes.Clone();
            hostileLength[33] = 0xff;
            hostileLength[34] = 0x7f;
            Assert.That(
                PurrNetLanDiscovery.TryDecode(
                    hostileLength,
                    hostileLength.Length,
                    source,
                    out _),
                Is.False);
        }

        [Test]
        public void QueryIsSmallAndStrictlyDecoded()
        {
            byte[] query = PurrNetLanDiscovery.EncodeQuery();
            var source = new IPEndPoint(IPAddress.Loopback, 47777);

            Assert.That(query.Length, Is.EqualTo(6));
            Assert.That(
                PurrNetLanDiscovery.TryDecode(
                    query,
                    query.Length,
                    source,
                    out PurrNetLanPacket packet),
                Is.True);
            Assert.That(packet.Kind, Is.EqualTo(PurrNetLanPacketKind.Query));

            Array.Resize(ref query, query.Length + 1);
            Assert.That(
                PurrNetLanDiscovery.TryDecode(
                    query,
                    query.Length,
                    source,
                    out _),
                Is.False);
        }
    }
}
