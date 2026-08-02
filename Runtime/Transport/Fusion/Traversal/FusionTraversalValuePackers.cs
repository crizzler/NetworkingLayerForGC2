#if GC2_TRAVERSAL
using System;
using Arawn.GameCreator2.Networking.Traversal;
using Arawn.GameCreator2.Networking.Transport.Fusion;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Traversal.Transport.Fusion
{

    public sealed class FusionValueWriter
    {
        private readonly FusionPacketWriter m_Writer;

        public FusionValueWriter(int capacity = 128)
        {
            m_Writer = new FusionPacketWriter(capacity);
        }

        public byte[] ToArray() => m_Writer.ToArray();
        public void Write(byte value) => m_Writer.WriteByte(value);
        public void Write(sbyte value) => m_Writer.WriteSByte(value);
        public void Write(bool value) => m_Writer.WriteBool(value);
        public void Write(short value) => m_Writer.WriteInt16(value);
        public void Write(ushort value) => m_Writer.WriteUInt16(value);
        public void Write(int value) => m_Writer.WriteInt32(value);
        public void Write(uint value) => m_Writer.WriteUInt32(value);
        public void Write(long value) => m_Writer.WriteInt64(value);
        public void Write(ulong value) => m_Writer.WriteUInt64(value);
        public void Write(float value) => m_Writer.WriteSingle(value);
        public void Write(double value) => m_Writer.WriteDouble(value);
        public void Write(string value) => m_Writer.WriteString(value);
        public void Write(Vector3 value) => m_Writer.WriteVector3(value);
        public void Write(Quaternion value) => m_Writer.WriteQuaternion(value);

    }

    public sealed class FusionValueReader
    {
        private const int MaxCollectionElements = 16384;
        private FusionPacketReader m_Reader;

        public FusionValueReader(ReadOnlyMemory<byte> payload)
        {
            m_Reader = new FusionPacketReader(payload);
        }

        public bool End => m_Reader.End;
        public void Read(ref byte value) => value = m_Reader.ReadByte();
        public void Read(ref sbyte value) => value = m_Reader.ReadSByte();
        public void Read(ref bool value) => value = m_Reader.ReadBool();
        public void Read(ref short value) => value = m_Reader.ReadInt16();
        public void Read(ref ushort value) => value = m_Reader.ReadUInt16();
        public void Read(ref int value) => value = m_Reader.ReadInt32();
        public void Read(ref uint value) => value = m_Reader.ReadUInt32();
        public void Read(ref long value) => value = m_Reader.ReadInt64();
        public void Read(ref ulong value) => value = m_Reader.ReadUInt64();
        public void Read(ref float value) => value = m_Reader.ReadSingle();
        public void Read(ref double value) => value = m_Reader.ReadDouble();
        public void Read(ref string value) => value = m_Reader.ReadString();
        public void Read(ref Vector3 value) => value = m_Reader.ReadVector3();
        public void Read(ref Quaternion value) => value = m_Reader.ReadQuaternion();

        private int ReadArrayCount()
        {
            int count = m_Reader.ReadInt32();
            if (count < -1 || count > MaxCollectionElements)
            {
                throw new InvalidOperationException($"Invalid Fusion module collection length: {count}");
            }
            return count;
        }

    }

    public delegate void FusionReadValue<T>(FusionValueReader reader, ref T value);

    public static class FusionValueCodec
    {
        public static byte[] Encode<T>(T value, Action<FusionValueWriter, T> write)
        {
            if (write == null) throw new ArgumentNullException(nameof(write));
            var writer = new FusionValueWriter();
            write(writer, value);
            return writer.ToArray();
        }

        public static bool TryDecode<T>(
            ReadOnlyMemory<byte> payload,
            FusionReadValue<T> read,
            out T value)
        {
            value = default;
            if (read == null) return false;

            try
            {
                var reader = new FusionValueReader(payload);
                read(reader, ref value);
                return reader.End;
            }
            catch (Exception)
            {
                value = default;
                return false;
            }
        }
    }

    public static class FusionTraversalValuePackers
    {
        public static void Write(this FusionValueWriter packer, NetworkTraversalRequest value)
        {
            packer.Write(value.RequestId);
            packer.Write(value.ActorNetworkId);
            packer.Write(value.CorrelationId);
            packer.Write(value.TargetNetworkId);
            packer.Write((byte)value.Action);
            packer.Write(value.TraverseHash);
            packer.Write(value.TraverseIdString);
            packer.Write(value.ActionIdHash);
            packer.Write(value.ActionIdString);
            packer.Write(value.StateIdHash);
            packer.Write(value.StateIdString);
            packer.Write(value.ArgsSelfNetworkId);
            packer.Write(value.ArgsTargetNetworkId);
        }

        public static void Read(this FusionValueReader packer, ref NetworkTraversalRequest value)
        {
            byte action = 0;

            packer.Read(ref value.RequestId);
            packer.Read(ref value.ActorNetworkId);
            packer.Read(ref value.CorrelationId);
            packer.Read(ref value.TargetNetworkId);
            packer.Read(ref action);
            packer.Read(ref value.TraverseHash);
            packer.Read(ref value.TraverseIdString);
            packer.Read(ref value.ActionIdHash);
            packer.Read(ref value.ActionIdString);
            packer.Read(ref value.StateIdHash);
            packer.Read(ref value.StateIdString);
            packer.Read(ref value.ArgsSelfNetworkId);
            packer.Read(ref value.ArgsTargetNetworkId);

            value.Action = (TraversalActionType)action;
        }

        public static void Write(this FusionValueWriter packer, NetworkTraversalResponse value)
        {
            packer.Write(value.RequestId);
            packer.Write(value.ActorNetworkId);
            packer.Write(value.CorrelationId);
            packer.Write((byte)value.Action);
            packer.Write(value.Authorized);
            packer.Write(value.Applied);
            packer.Write((byte)value.RejectionReason);
            packer.Write(value.TraverseHash);
            packer.Write(value.TraverseIdString);
            packer.Write(value.ActionIdHash);
            packer.Write(value.ActionIdString);
            packer.Write(value.StateIdHash);
            packer.Write(value.StateIdString);
            packer.Write(value.ArgsSelfNetworkId);
            packer.Write(value.ArgsTargetNetworkId);
            packer.Write(value.IsTraversing);
            packer.Write(value.StateVersion);
            packer.Write(value.Error);
        }

        public static void Read(this FusionValueReader packer, ref NetworkTraversalResponse value)
        {
            byte action = 0;
            byte rejection = 0;

            packer.Read(ref value.RequestId);
            packer.Read(ref value.ActorNetworkId);
            packer.Read(ref value.CorrelationId);
            packer.Read(ref action);
            packer.Read(ref value.Authorized);
            packer.Read(ref value.Applied);
            packer.Read(ref rejection);
            packer.Read(ref value.TraverseHash);
            packer.Read(ref value.TraverseIdString);
            packer.Read(ref value.ActionIdHash);
            packer.Read(ref value.ActionIdString);
            packer.Read(ref value.StateIdHash);
            packer.Read(ref value.StateIdString);
            packer.Read(ref value.ArgsSelfNetworkId);
            packer.Read(ref value.ArgsTargetNetworkId);
            packer.Read(ref value.IsTraversing);
            packer.Read(ref value.StateVersion);
            packer.Read(ref value.Error);

            value.Action = (TraversalActionType)action;
            value.RejectionReason = (TraversalRejectionReason)rejection;
        }

        public static void Write(this FusionValueWriter packer, NetworkTraversalBroadcast value)
        {
            packer.Write(value.NetworkId);
            packer.Write(value.ActorNetworkId);
            packer.Write(value.CorrelationId);
            packer.Write((byte)value.Action);
            packer.Write(value.TraverseHash);
            packer.Write(value.TraverseIdString);
            packer.Write(value.ActionIdHash);
            packer.Write(value.ActionIdString);
            packer.Write(value.StateIdHash);
            packer.Write(value.StateIdString);
            packer.Write(value.ArgsSelfNetworkId);
            packer.Write(value.ArgsTargetNetworkId);
            packer.Write(value.IsTraversing);
            packer.Write(value.ServerTime);
            packer.Write(value.StateVersion);
        }

        public static void Read(this FusionValueReader packer, ref NetworkTraversalBroadcast value)
        {
            byte action = 0;

            packer.Read(ref value.NetworkId);
            packer.Read(ref value.ActorNetworkId);
            packer.Read(ref value.CorrelationId);
            packer.Read(ref action);
            packer.Read(ref value.TraverseHash);
            packer.Read(ref value.TraverseIdString);
            packer.Read(ref value.ActionIdHash);
            packer.Read(ref value.ActionIdString);
            packer.Read(ref value.StateIdHash);
            packer.Read(ref value.StateIdString);
            packer.Read(ref value.ArgsSelfNetworkId);
            packer.Read(ref value.ArgsTargetNetworkId);
            packer.Read(ref value.IsTraversing);
            packer.Read(ref value.ServerTime);
            packer.Read(ref value.StateVersion);

            value.Action = (TraversalActionType)action;
        }

        public static void Write(this FusionValueWriter packer, NetworkTraversalSnapshot value)
        {
            packer.Write(value.NetworkId);
            packer.Write(value.ServerTime);
            packer.Write(value.IsTraversing);
            packer.Write(value.TraverseHash);
            packer.Write(value.TraverseIdString);
            packer.Write(value.StateVersion);
            packer.Write((byte)value.Kind);
            packer.Write(value.HasRelativePose);
            packer.Write(value.RelativePosition);
            packer.Write(value.RelativeRotation);
        }

        public static void Read(this FusionValueReader packer, ref NetworkTraversalSnapshot value)
        {
            byte kind = 0;

            packer.Read(ref value.NetworkId);
            packer.Read(ref value.ServerTime);
            packer.Read(ref value.IsTraversing);
            packer.Read(ref value.TraverseHash);
            packer.Read(ref value.TraverseIdString);
            packer.Read(ref value.StateVersion);
            packer.Read(ref kind);
            packer.Read(ref value.HasRelativePose);
            packer.Read(ref value.RelativePosition);
            packer.Read(ref value.RelativeRotation);

            value.Kind = (TraversalSnapshotKind)kind;
        }
    }
}
#endif
