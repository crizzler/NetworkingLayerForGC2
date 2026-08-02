#if GC2_DIALOGUE
using System;
using Arawn.GameCreator2.Networking.Dialogue;
using Arawn.GameCreator2.Networking.Transport.Fusion;

namespace Arawn.GameCreator2.Networking.Dialogue.Transport.Fusion
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

        public void WriteList(int[] values)
        {
            if (values == null) { Write(-1); return; }
            Write(values.Length);
            for (int i = 0; i < values.Length; i++) Write(values[i]);
        }

        public void WriteList(string[] values)
        {
            if (values == null) { Write(-1); return; }
            Write(values.Length);
            for (int i = 0; i < values.Length; i++) Write(values[i]);
        }

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

        private int ReadArrayCount()
        {
            int count = m_Reader.ReadInt32();
            if (count < -1 || count > MaxCollectionElements)
            {
                throw new InvalidOperationException($"Invalid Fusion module collection length: {count}");
            }
            return count;
        }

        public void ReadArray(ref int[] values)
        {
            int count = ReadArrayCount();
            if (count < 0) { values = null; return; }
            values = new int[count];
            for (int i = 0; i < count; i++) Read(ref values[i]);
        }

        public void ReadArray(ref string[] values)
        {
            int count = ReadArrayCount();
            if (count < 0) { values = null; return; }
            values = new string[count];
            for (int i = 0; i < count; i++) Read(ref values[i]);
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

    public static class FusionDialogueValuePackers
    {
        public static void Write(this FusionValueWriter packer, NetworkDialogueRequest value)
        {
            packer.Write(value.RequestId);
            packer.Write(value.ActorNetworkId);
            packer.Write(value.CorrelationId);
            packer.Write(value.TargetNetworkId);
            packer.Write((byte)value.Action);
            packer.Write(value.DialogueHash);
            packer.Write(value.DialogueIdString);
            packer.Write(value.ChoiceNodeId);
            packer.Write(value.SelfNetworkId);
            packer.Write(value.ArgsTargetNetworkId);
        }

        public static void Read(this FusionValueReader packer, ref NetworkDialogueRequest value)
        {
            byte action = 0;

            packer.Read(ref value.RequestId);
            packer.Read(ref value.ActorNetworkId);
            packer.Read(ref value.CorrelationId);
            packer.Read(ref value.TargetNetworkId);
            packer.Read(ref action);
            packer.Read(ref value.DialogueHash);
            packer.Read(ref value.DialogueIdString);
            packer.Read(ref value.ChoiceNodeId);
            packer.Read(ref value.SelfNetworkId);
            packer.Read(ref value.ArgsTargetNetworkId);

            value.Action = (DialogueActionType)action;
        }

        public static void Write(this FusionValueWriter packer, NetworkDialogueResponse value)
        {
            packer.Write(value.RequestId);
            packer.Write(value.ActorNetworkId);
            packer.Write(value.CorrelationId);
            packer.Write(value.TargetNetworkId);
            packer.Write((byte)value.Action);
            packer.Write(value.Authorized);
            packer.Write(value.Applied);
            packer.Write((byte)value.RejectionReason);
            packer.Write(value.DialogueHash);
            packer.Write(value.DialogueIdString);
            packer.Write(value.CurrentNodeId);
            packer.Write(value.IsPlaying);
            packer.Write(value.IsVisited);
            packer.Write(value.Error);
        }

        public static void Read(this FusionValueReader packer, ref NetworkDialogueResponse value)
        {
            byte action = 0;
            byte rejection = 0;

            packer.Read(ref value.RequestId);
            packer.Read(ref value.ActorNetworkId);
            packer.Read(ref value.CorrelationId);
            packer.Read(ref value.TargetNetworkId);
            packer.Read(ref action);
            packer.Read(ref value.Authorized);
            packer.Read(ref value.Applied);
            packer.Read(ref rejection);
            packer.Read(ref value.DialogueHash);
            packer.Read(ref value.DialogueIdString);
            packer.Read(ref value.CurrentNodeId);
            packer.Read(ref value.IsPlaying);
            packer.Read(ref value.IsVisited);
            packer.Read(ref value.Error);

            value.Action = (DialogueActionType)action;
            value.RejectionReason = (DialogueRejectionReason)rejection;
        }

        public static void Write(this FusionValueWriter packer, NetworkDialogueBroadcast value)
        {
            packer.Write(value.NetworkId);
            packer.Write(value.ActorNetworkId);
            packer.Write(value.CorrelationId);
            packer.Write((byte)value.Action);
            packer.Write(value.DialogueHash);
            packer.Write(value.DialogueIdString);
            packer.Write(value.CurrentNodeId);
            packer.Write(value.ChoiceNodeId);
            packer.Write(value.IsPlaying);
            packer.Write(value.IsVisited);
            packer.Write(value.ServerTime);
        }

        public static void Read(this FusionValueReader packer, ref NetworkDialogueBroadcast value)
        {
            byte action = 0;

            packer.Read(ref value.NetworkId);
            packer.Read(ref value.ActorNetworkId);
            packer.Read(ref value.CorrelationId);
            packer.Read(ref action);
            packer.Read(ref value.DialogueHash);
            packer.Read(ref value.DialogueIdString);
            packer.Read(ref value.CurrentNodeId);
            packer.Read(ref value.ChoiceNodeId);
            packer.Read(ref value.IsPlaying);
            packer.Read(ref value.IsVisited);
            packer.Read(ref value.ServerTime);

            value.Action = (DialogueActionType)action;
        }

        public static void Write(this FusionValueWriter packer, NetworkDialogueSnapshot value)
        {
            packer.Write(value.NetworkId);
            packer.Write(value.ServerTime);
            packer.Write(value.DialogueHash);
            packer.Write(value.DialogueIdString);
            packer.Write(value.IsPlaying);
            packer.Write(value.IsVisited);
            packer.Write(value.CurrentNodeId);
            packer.WriteList(value.VisitedNodeIds);
            packer.WriteList(value.VisitedTagIds);
        }

        public static void Read(this FusionValueReader packer, ref NetworkDialogueSnapshot value)
        {
            packer.Read(ref value.NetworkId);
            packer.Read(ref value.ServerTime);
            packer.Read(ref value.DialogueHash);
            packer.Read(ref value.DialogueIdString);
            packer.Read(ref value.IsPlaying);
            packer.Read(ref value.IsVisited);
            packer.Read(ref value.CurrentNodeId);
            packer.ReadArray(ref value.VisitedNodeIds);
            packer.ReadArray(ref value.VisitedTagIds);
        }
    }
}
#endif
