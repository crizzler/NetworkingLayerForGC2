using System;
using System.Text;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Transport.Fusion
{
    public static class FusionProtocol
    {
        public const uint Magic = 0x32434647; // "GFC2" in little-endian byte order.
        public const ushort Version = 1;
        public const ushort TransportModuleId = 0;
        /// <summary>
        /// Module IDs 1-9 are reserved for small, transport-owned demo and bootstrap
        /// helpers. GC2 gameplay modules start at 10.
        /// </summary>
        public const ushort DemoCharacterSelectionModuleId = 1;
        public const ushort DemoChatModuleId = 2;
        public const int EnvelopeHeaderLength = 23;
        public const int RpcPayloadLimit = 384;
        /// <summary>Total encoded envelope limit, including the fixed protocol header.</summary>
        public const int MaximumPacketLength = 1024 * 1024;
        public const int MaximumPayloadLength = MaximumPacketLength - EnvelopeHeaderLength;
        public const int ReorderWindow = 64;
        public const float ReorderTimeoutSeconds = 5f;
    }

    public enum FusionPacketDirection : byte
    {
        ToAuthority = 1,
        FromAuthority = 2
    }

    internal enum FusionTransportMessageType : ushort
    {
        CharacterInput = 1,
        CharacterState = 2,
        SceneReady = 3,
        GameplayReady = 4,
        AuthorityAnnouncement = 5,
        ResyncRequest = 6,
        SnapshotComplete = 7,
        SnapshotAcknowledged = 8
    }

    public readonly struct FusionModuleMessage
    {
        public ushort ModuleId { get; }
        public ushort MessageType { get; }
        public uint SenderClientId { get; }
        public ReadOnlyMemory<byte> Payload { get; }
        public bool FromAuthority { get; }
        public uint AuthorityEpoch { get; }
        public uint Sequence { get; }

        public FusionModuleMessage(
            ushort moduleId,
            ushort messageType,
            uint senderClientId,
            ReadOnlyMemory<byte> payload,
            bool fromAuthority,
            uint authorityEpoch,
            uint sequence)
        {
            ModuleId = moduleId;
            MessageType = messageType;
            SenderClientId = senderClientId;
            Payload = payload;
            FromAuthority = fromAuthority;
            AuthorityEpoch = authorityEpoch;
            Sequence = sequence;
        }
    }

    public readonly struct FusionPacketEnvelope
    {
        public uint AuthorityEpoch { get; }
        public FusionPacketDirection Direction { get; }
        public ushort ModuleId { get; }
        public ushort MessageType { get; }
        public uint Sequence { get; }
        public ReadOnlyMemory<byte> Payload { get; }

        public FusionPacketEnvelope(
            uint authorityEpoch,
            FusionPacketDirection direction,
            ushort moduleId,
            ushort messageType,
            uint sequence,
            ReadOnlyMemory<byte> payload)
        {
            AuthorityEpoch = authorityEpoch;
            Direction = direction;
            ModuleId = moduleId;
            MessageType = messageType;
            Sequence = sequence;
            Payload = payload;
        }
    }

    /// <summary>
    /// Allocation-conscious, deterministic little-endian writer used by every Fusion module.
    /// Strings and byte arrays are nullable and length-prefixed with a signed 32-bit length.
    /// </summary>
    public sealed class FusionPacketWriter
    {
        private byte[] m_Buffer;
        private int m_Length;

        public FusionPacketWriter(int capacity = 128)
        {
            m_Buffer = new byte[Mathf.Max(16, capacity)];
        }

        public int Length => m_Length;

        public void WriteByte(byte value)
        {
            EnsureCapacity(1);
            m_Buffer[m_Length++] = value;
        }

        public void WriteSByte(sbyte value) => WriteByte(unchecked((byte)value));
        public void WriteBool(bool value) => WriteByte(value ? (byte)1 : (byte)0);
        public void WriteBoolean(bool value) => WriteBool(value);

        public void WriteInt16(short value)
        {
            EnsureCapacity(2);
            m_Buffer[m_Length++] = (byte)value;
            m_Buffer[m_Length++] = (byte)(value >> 8);
        }

        public void WriteUInt16(ushort value)
        {
            EnsureCapacity(2);
            m_Buffer[m_Length++] = (byte)value;
            m_Buffer[m_Length++] = (byte)(value >> 8);
        }

        public void WriteInt32(int value)
        {
            EnsureCapacity(4);
            m_Buffer[m_Length++] = (byte)value;
            m_Buffer[m_Length++] = (byte)(value >> 8);
            m_Buffer[m_Length++] = (byte)(value >> 16);
            m_Buffer[m_Length++] = (byte)(value >> 24);
        }

        public void WriteUInt32(uint value)
        {
            EnsureCapacity(4);
            m_Buffer[m_Length++] = (byte)value;
            m_Buffer[m_Length++] = (byte)(value >> 8);
            m_Buffer[m_Length++] = (byte)(value >> 16);
            m_Buffer[m_Length++] = (byte)(value >> 24);
        }

        public void WriteInt64(long value) => WriteUInt64(unchecked((ulong)value));

        public void WriteUInt64(ulong value)
        {
            EnsureCapacity(8);
            for (int i = 0; i < 8; i++)
            {
                m_Buffer[m_Length++] = (byte)(value >> (i * 8));
            }
        }

        public void WriteSingle(float value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            if (!BitConverter.IsLittleEndian) Array.Reverse(bytes);
            WriteRawBytes(bytes);
        }

        public void WriteFloat(float value) => WriteSingle(value);

        public void WriteDouble(double value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            if (!BitConverter.IsLittleEndian) Array.Reverse(bytes);
            WriteRawBytes(bytes);
        }

        public void WriteString(string value)
        {
            if (value == null)
            {
                WriteInt32(-1);
                return;
            }

            byte[] bytes = Encoding.UTF8.GetBytes(value);
            WriteInt32(bytes.Length);
            WriteRawBytes(bytes);
        }

        public void WriteByteArray(byte[] value)
        {
            if (value == null)
            {
                WriteInt32(-1);
                return;
            }

            WriteInt32(value.Length);
            WriteRawBytes(value);
        }

        public void WriteBytes(byte[] value) => WriteByteArray(value);

        public void WriteRawBytes(byte[] value)
        {
            if (value == null || value.Length == 0) return;
            WriteRawBytes(new ReadOnlySpan<byte>(value));
        }

        public void WriteRawBytes(ReadOnlySpan<byte> value)
        {
            EnsureCapacity(value.Length);
            value.CopyTo(new Span<byte>(m_Buffer, m_Length, value.Length));
            m_Length += value.Length;
        }

        public void WriteVector2(Vector2 value)
        {
            WriteSingle(value.x);
            WriteSingle(value.y);
        }

        public void WriteVector3(Vector3 value)
        {
            WriteSingle(value.x);
            WriteSingle(value.y);
            WriteSingle(value.z);
        }

        public void WriteQuaternion(Quaternion value)
        {
            WriteSingle(value.x);
            WriteSingle(value.y);
            WriteSingle(value.z);
            WriteSingle(value.w);
        }

        public void WriteColor(Color value)
        {
            WriteSingle(value.r);
            WriteSingle(value.g);
            WriteSingle(value.b);
            WriteSingle(value.a);
        }

        public byte[] ToArray()
        {
            if (m_Length == 0) return Array.Empty<byte>();
            byte[] result = new byte[m_Length];
            Buffer.BlockCopy(m_Buffer, 0, result, 0, m_Length);
            return result;
        }

        private void EnsureCapacity(int additionalBytes)
        {
            if (additionalBytes < 0 || m_Length > FusionProtocol.MaximumPacketLength - additionalBytes)
            {
                throw new InvalidOperationException(
                    $"Fusion packet exceeds the {FusionProtocol.MaximumPacketLength}-byte safety limit.");
            }

            int required = m_Length + additionalBytes;
            if (required <= m_Buffer.Length) return;

            int next = m_Buffer.Length;
            while (next < required)
            {
                next = Math.Min(FusionProtocol.MaximumPacketLength, next * 2);
                if (next < required && next == FusionProtocol.MaximumPacketLength)
                {
                    throw new InvalidOperationException(
                        $"Fusion packet exceeds the {FusionProtocol.MaximumPacketLength}-byte safety limit.");
                }
            }

            Array.Resize(ref m_Buffer, next);
        }
    }

    /// <summary>
    /// Bounds-checked counterpart to <see cref="FusionPacketWriter"/>. Invalid or truncated
    /// payloads throw <see cref="FormatException"/> and are rejected by the transport boundary.
    /// </summary>
    public sealed class FusionPacketReader
    {
        private readonly ReadOnlyMemory<byte> m_Data;
        private int m_Position;

        public FusionPacketReader(ReadOnlyMemory<byte> data)
        {
            if (data.Length > FusionProtocol.MaximumPacketLength)
            {
                throw new FormatException(
                    $"Fusion packet exceeds the {FusionProtocol.MaximumPacketLength}-byte safety limit.");
            }

            m_Data = data;
        }

        public FusionPacketReader(byte[] data) : this(
            data == null ? ReadOnlyMemory<byte>.Empty : new ReadOnlyMemory<byte>(data))
        {
        }

        public int Position => m_Position;
        public int Remaining => m_Data.Length - m_Position;
        public bool End => m_Position == m_Data.Length;

        public byte ReadByte()
        {
            Require(1);
            return m_Data.Span[m_Position++];
        }

        public sbyte ReadSByte() => unchecked((sbyte)ReadByte());

        public bool ReadBool()
        {
            byte value = ReadByte();
            if (value > 1) throw new FormatException("Invalid encoded Boolean.");
            return value != 0;
        }

        public bool ReadBoolean() => ReadBool();

        public short ReadInt16() => unchecked((short)ReadUInt16());

        public ushort ReadUInt16()
        {
            Require(2);
            ReadOnlySpan<byte> span = m_Data.Span;
            uint value = (uint)(span[m_Position] | (span[m_Position + 1] << 8));
            m_Position += 2;
            return (ushort)value;
        }

        public int ReadInt32() => unchecked((int)ReadUInt32());

        public uint ReadUInt32()
        {
            Require(4);
            ReadOnlySpan<byte> span = m_Data.Span;
            uint value =
                span[m_Position] |
                ((uint)span[m_Position + 1] << 8) |
                ((uint)span[m_Position + 2] << 16) |
                ((uint)span[m_Position + 3] << 24);
            m_Position += 4;
            return value;
        }

        public long ReadInt64() => unchecked((long)ReadUInt64());

        public ulong ReadUInt64()
        {
            Require(8);
            ReadOnlySpan<byte> span = m_Data.Span;
            ulong value = 0;
            for (int i = 0; i < 8; i++)
            {
                value |= (ulong)span[m_Position + i] << (i * 8);
            }

            m_Position += 8;
            return value;
        }

        public float ReadSingle()
        {
            byte[] bytes = ReadRawBytes(4);
            if (!BitConverter.IsLittleEndian) Array.Reverse(bytes);
            return BitConverter.ToSingle(bytes, 0);
        }

        public float ReadFloat() => ReadSingle();

        public double ReadDouble()
        {
            byte[] bytes = ReadRawBytes(8);
            if (!BitConverter.IsLittleEndian) Array.Reverse(bytes);
            return BitConverter.ToDouble(bytes, 0);
        }

        public string ReadString()
        {
            int length = ReadLength();
            if (length < 0) return null;
            if (length == 0) return string.Empty;
            Require(length);
            string result = Encoding.UTF8.GetString(m_Data.Span.Slice(m_Position, length));
            m_Position += length;
            return result;
        }

        public byte[] ReadByteArray()
        {
            int length = ReadLength();
            if (length < 0) return null;
            return ReadRawBytes(length);
        }

        public byte[] ReadBytes() => ReadByteArray();

        public byte[] ReadRawBytes(int length)
        {
            Require(length);
            if (length == 0) return Array.Empty<byte>();
            byte[] result = m_Data.Slice(m_Position, length).ToArray();
            m_Position += length;
            return result;
        }

        public ReadOnlyMemory<byte> ReadRawMemory(int length)
        {
            Require(length);
            ReadOnlyMemory<byte> result = m_Data.Slice(m_Position, length);
            m_Position += length;
            return result;
        }

        public Vector2 ReadVector2() => new Vector2(ReadSingle(), ReadSingle());
        public Vector3 ReadVector3() => new Vector3(ReadSingle(), ReadSingle(), ReadSingle());
        public Quaternion ReadQuaternion() => new Quaternion(ReadSingle(), ReadSingle(), ReadSingle(), ReadSingle());
        public Color ReadColor() => new Color(ReadSingle(), ReadSingle(), ReadSingle(), ReadSingle());

        private int ReadLength()
        {
            int length = ReadInt32();
            if (length < -1 || length > FusionProtocol.MaximumPayloadLength)
            {
                throw new FormatException($"Invalid encoded collection length {length}.");
            }

            return length;
        }

        private void Require(int length)
        {
            if (length < 0 || length > Remaining)
            {
                throw new FormatException(
                    $"Fusion packet is truncated at offset {m_Position}; requested {length}, remaining {Remaining}.");
            }
        }
    }

    public static class FusionPacketCodec
    {
        public static byte[] Encode(FusionPacketEnvelope envelope)
        {
            if (envelope.Payload.Length > FusionProtocol.MaximumPayloadLength)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(envelope),
                    $"Payload exceeds {FusionProtocol.MaximumPayloadLength} bytes.");
            }

            var writer = new FusionPacketWriter(32 + envelope.Payload.Length);
            writer.WriteUInt32(FusionProtocol.Magic);
            writer.WriteUInt16(FusionProtocol.Version);
            writer.WriteUInt32(envelope.AuthorityEpoch);
            writer.WriteByte((byte)envelope.Direction);
            writer.WriteUInt16(envelope.ModuleId);
            writer.WriteUInt16(envelope.MessageType);
            writer.WriteUInt32(envelope.Sequence);
            writer.WriteInt32(envelope.Payload.Length);
            writer.WriteRawBytes(envelope.Payload.Span);
            return writer.ToArray();
        }

        public static bool TryDecode(
            ReadOnlyMemory<byte> data,
            out FusionPacketEnvelope envelope,
            out string error)
        {
            envelope = default;
            error = null;

            try
            {
                var reader = new FusionPacketReader(data);
                if (reader.ReadUInt32() != FusionProtocol.Magic)
                {
                    error = "Invalid protocol magic.";
                    return false;
                }

                ushort version = reader.ReadUInt16();
                if (version != FusionProtocol.Version)
                {
                    error = $"Unsupported protocol version {version}; expected {FusionProtocol.Version}.";
                    return false;
                }

                uint epoch = reader.ReadUInt32();
                FusionPacketDirection direction = (FusionPacketDirection)reader.ReadByte();
                if (direction != FusionPacketDirection.ToAuthority &&
                    direction != FusionPacketDirection.FromAuthority)
                {
                    error = $"Invalid packet direction {(byte)direction}.";
                    return false;
                }

                ushort moduleId = reader.ReadUInt16();
                ushort messageType = reader.ReadUInt16();
                uint sequence = reader.ReadUInt32();
                int payloadLength = reader.ReadInt32();
                if (payloadLength < 0 ||
                    payloadLength > FusionProtocol.MaximumPayloadLength ||
                    payloadLength != reader.Remaining)
                {
                    error = $"Invalid payload length {payloadLength}; remaining {reader.Remaining}.";
                    return false;
                }

                envelope = new FusionPacketEnvelope(
                    epoch,
                    direction,
                    moduleId,
                    messageType,
                    sequence,
                    reader.ReadRawMemory(payloadLength));
                return true;
            }
            catch (Exception exception) when (
                exception is FormatException ||
                exception is ArgumentException ||
                exception is InvalidOperationException)
            {
                error = exception.Message;
                return false;
            }
        }
    }
}
