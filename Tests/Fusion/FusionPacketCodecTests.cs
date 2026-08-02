using System;
using System.IO;
using System.Reflection;
using Arawn.GameCreator2.Networking.Editor;
using NUnit.Framework;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Transport.Fusion.Tests
{
    public sealed class FusionPacketCodecTests
    {
        [Test]
        public void WriterReader_RoundTripsPrimitiveAndUnityValues()
        {
            var writer = new FusionPacketWriter();
            writer.WriteByte(0xA5);
            writer.WriteBool(true);
            writer.WriteInt16(-1234);
            writer.WriteUInt16(65000);
            writer.WriteInt32(-123456789);
            writer.WriteUInt32(4000000000);
            writer.WriteInt64(-1234567890123456789);
            writer.WriteUInt64(17000000000000000000);
            writer.WriteSingle(123.25f);
            writer.WriteDouble(-9876.125);
            writer.WriteString(null);
            writer.WriteString(string.Empty);
            writer.WriteString("Fusion 世界 🚀");
            writer.WriteByteArray(null);
            writer.WriteByteArray(Array.Empty<byte>());
            writer.WriteByteArray(new byte[] { 1, 2, 3, 255 });
            writer.WriteVector2(new Vector2(1.5f, -2.25f));
            writer.WriteVector3(new Vector3(3f, 4f, 5f));
            writer.WriteQuaternion(new Quaternion(0.1f, 0.2f, 0.3f, 0.4f));
            writer.WriteColor(new Color(0.1f, 0.25f, 0.5f, 1f));

            var reader = new FusionPacketReader(writer.ToArray());
            Assert.AreEqual(0xA5, reader.ReadByte());
            Assert.IsTrue(reader.ReadBool());
            Assert.AreEqual(-1234, reader.ReadInt16());
            Assert.AreEqual(65000, reader.ReadUInt16());
            Assert.AreEqual(-123456789, reader.ReadInt32());
            Assert.AreEqual(4000000000, reader.ReadUInt32());
            Assert.AreEqual(-1234567890123456789, reader.ReadInt64());
            Assert.AreEqual(17000000000000000000, reader.ReadUInt64());
            Assert.AreEqual(123.25f, reader.ReadSingle());
            Assert.AreEqual(-9876.125, reader.ReadDouble());
            Assert.IsNull(reader.ReadString());
            Assert.AreEqual(string.Empty, reader.ReadString());
            Assert.AreEqual("Fusion 世界 🚀", reader.ReadString());
            Assert.IsNull(reader.ReadByteArray());
            CollectionAssert.IsEmpty(reader.ReadByteArray());
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 255 }, reader.ReadByteArray());
            Assert.AreEqual(new Vector2(1.5f, -2.25f), reader.ReadVector2());
            Assert.AreEqual(new Vector3(3f, 4f, 5f), reader.ReadVector3());
            Assert.AreEqual(new Quaternion(0.1f, 0.2f, 0.3f, 0.4f), reader.ReadQuaternion());
            Assert.AreEqual(new Color(0.1f, 0.25f, 0.5f, 1f), reader.ReadColor());
            Assert.IsTrue(reader.End);
        }

        [Test]
        public void Envelope_RoundTripsAndUsesStableLittleEndianHeader()
        {
            byte[] payload = { 9, 8, 7, 6 };
            var source = new FusionPacketEnvelope(
                0x01020304,
                FusionPacketDirection.FromAuthority,
                0x1122,
                0x3344,
                0x55667788,
                payload);

            byte[] encoded = FusionPacketCodec.Encode(source);

            CollectionAssert.AreEqual(
                new byte[]
                {
                    0x47, 0x46, 0x43, 0x32, // GFC2 magic
                    0x01, 0x00,             // protocol v1
                    0x04, 0x03, 0x02, 0x01,
                    0x02,
                    0x22, 0x11,
                    0x44, 0x33,
                    0x88, 0x77, 0x66, 0x55,
                    0x04, 0x00, 0x00, 0x00
                },
                new ArraySegment<byte>(encoded, 0, 23));

            Assert.IsTrue(FusionPacketCodec.TryDecode(encoded, out FusionPacketEnvelope decoded, out string error), error);
            Assert.AreEqual(source.AuthorityEpoch, decoded.AuthorityEpoch);
            Assert.AreEqual(source.Direction, decoded.Direction);
            Assert.AreEqual(source.ModuleId, decoded.ModuleId);
            Assert.AreEqual(source.MessageType, decoded.MessageType);
            Assert.AreEqual(source.Sequence, decoded.Sequence);
            CollectionAssert.AreEqual(payload, decoded.Payload.ToArray());
        }

        [Test]
        public void Decoder_RejectsTruncatedUnknownAndLengthMismatchedPackets()
        {
            Assert.IsFalse(
                FusionPacketCodec.TryDecode(new byte[] { 1, 2, 3 }, out _, out string truncated));
            Assert.IsNotEmpty(truncated);

            byte[] valid = FusionPacketCodec.Encode(
                new FusionPacketEnvelope(
                    1,
                    FusionPacketDirection.ToAuthority,
                    1,
                    2,
                    3,
                    new byte[] { 4 }));

            valid[4] = 0xFF;
            valid[5] = 0x7F;
            Assert.IsFalse(FusionPacketCodec.TryDecode(valid, out _, out string version));
            StringAssert.Contains("version", version.ToLowerInvariant());

            valid = FusionPacketCodec.Encode(
                new FusionPacketEnvelope(
                    1,
                    FusionPacketDirection.ToAuthority,
                    1,
                    2,
                    3,
                    new byte[] { 4 }));
            valid[19] = 2;
            Assert.IsFalse(FusionPacketCodec.TryDecode(valid, out _, out string length));
            StringAssert.Contains("length", length.ToLowerInvariant());
        }

        [Test]
        public void Writer_EnforcesOneMiBPacketSafetyLimit()
        {
            var writer = new FusionPacketWriter();
            Assert.Throws<InvalidOperationException>(
                () => writer.WriteRawBytes(new byte[FusionProtocol.MaximumPacketLength + 1]));
        }

        [Test]
        public void Envelope_AcceptsMaximumPayloadAndRejectsOneByteMore()
        {
            byte[] maximum = new byte[FusionProtocol.MaximumPayloadLength];
            byte[] encoded = FusionPacketCodec.Encode(
                new FusionPacketEnvelope(
                    1,
                    FusionPacketDirection.ToAuthority,
                    1,
                    1,
                    1,
                    maximum));

            Assert.AreEqual(FusionProtocol.MaximumPacketLength, encoded.Length);
            Assert.IsTrue(FusionPacketCodec.TryDecode(encoded, out _, out string error), error);

            Assert.Throws<ArgumentOutOfRangeException>(
                () => FusionPacketCodec.Encode(
                    new FusionPacketEnvelope(
                        1,
                        FusionPacketDirection.ToAuthority,
                        1,
                        1,
                        1,
                        new byte[FusionProtocol.MaximumPayloadLength + 1])));
        }

        [Test]
        public void RuntimeAssemblyDefinition_HasNoNinjutsuOrPurrNetDependency()
        {
            const string path =
                "Assets/Arawn/NetworkingLayerForGC2/Runtime/Transport/Fusion/" +
                "Arawn.GameCreator2.Networking.Transport.Fusion.asmdef";
            string text = File.ReadAllText(path);

            StringAssert.DoesNotContain("Ninjutsu", text);
            StringAssert.DoesNotContain("PurrNet", text);
            StringAssert.Contains("\"Fusion.Unity\"", text);
        }

        [Test]
        public void FusionTransportDirectory_ActivatesGenericWizardSuppression()
        {
            MethodInfo method = typeof(GC2NetworkingDefineSymbols).GetMethod(
                "IsTransportIntegrationInstalled",
                BindingFlags.NonPublic | BindingFlags.Static);

            Assert.NotNull(method);
            Assert.IsTrue((bool)method.Invoke(null, null));
        }
    }
}
