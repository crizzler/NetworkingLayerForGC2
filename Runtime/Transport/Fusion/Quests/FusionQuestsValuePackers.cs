#if GC2_QUESTS
using System;
using Arawn.GameCreator2.Networking.Quests;
using Arawn.GameCreator2.Networking.Transport.Fusion;

namespace Arawn.GameCreator2.Networking.Quests.Transport.Fusion
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

        public void WriteList(NetworkQuestSnapshotEntry[] values)
        {
            if (values == null) { Write(-1); return; }
            Write(values.Length);
            for (int i = 0; i < values.Length; i++) this.Write(values[i]);
        }

        public void WriteList(NetworkTaskSnapshotEntry[] values)
        {
            if (values == null) { Write(-1); return; }
            Write(values.Length);
            for (int i = 0; i < values.Length; i++) this.Write(values[i]);
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

        public void ReadArray(ref NetworkQuestSnapshotEntry[] values)
        {
            int count = ReadArrayCount();
            if (count < 0) { values = null; return; }
            values = new NetworkQuestSnapshotEntry[count];
            for (int i = 0; i < count; i++) this.Read(ref values[i]);
        }

        public void ReadArray(ref NetworkTaskSnapshotEntry[] values)
        {
            int count = ReadArrayCount();
            if (count < 0) { values = null; return; }
            values = new NetworkTaskSnapshotEntry[count];
            for (int i = 0; i < count; i++) this.Read(ref values[i]);
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

    public static class FusionQuestsValuePackers
    {
        public static void Write(this FusionValueWriter packer, NetworkQuestRequest value)
        {
            packer.Write(value.RequestId);
            packer.Write(value.ActorNetworkId);
            packer.Write(value.CorrelationId);
            packer.Write(value.TargetNetworkId);
            packer.Write(value.ProfileHash);
            packer.Write((byte)value.ShareMode);
            packer.Write(value.ScopeId);
            packer.Write((byte)value.Action);
            packer.Write(value.QuestHash);
            packer.Write(value.QuestIdString);
            packer.Write(value.TaskId);
            packer.Write(value.Value);
        }

        public static void Read(this FusionValueReader packer, ref NetworkQuestRequest value)
        {
            byte shareMode = 0;
            byte action = 0;

            packer.Read(ref value.RequestId);
            packer.Read(ref value.ActorNetworkId);
            packer.Read(ref value.CorrelationId);
            packer.Read(ref value.TargetNetworkId);
            packer.Read(ref value.ProfileHash);
            packer.Read(ref shareMode);
            packer.Read(ref value.ScopeId);
            packer.Read(ref action);
            packer.Read(ref value.QuestHash);
            packer.Read(ref value.QuestIdString);
            packer.Read(ref value.TaskId);
            packer.Read(ref value.Value);

            value.ShareMode = (NetworkQuestShareMode)shareMode;
            value.Action = (QuestActionType)action;
        }

        public static void Write(this FusionValueWriter packer, NetworkQuestResponse value)
        {
            packer.Write(value.RequestId);
            packer.Write(value.ActorNetworkId);
            packer.Write(value.CorrelationId);
            packer.Write(value.ProfileHash);
            packer.Write((byte)value.ShareMode);
            packer.Write(value.ScopeId);
            packer.Write((byte)value.Action);
            packer.Write(value.Authorized);
            packer.Write(value.Applied);
            packer.Write((byte)value.RejectionReason);
            packer.Write(value.QuestHash);
            packer.Write(value.QuestIdString);
            packer.Write(value.TaskId);
            packer.Write(value.QuestState);
            packer.Write(value.TaskState);
            packer.Write(value.IsTracking);
            packer.Write(value.TaskValue);
            packer.Write(value.Error);
        }

        public static void Read(this FusionValueReader packer, ref NetworkQuestResponse value)
        {
            byte shareMode = 0;
            byte action = 0;
            byte rejection = 0;

            packer.Read(ref value.RequestId);
            packer.Read(ref value.ActorNetworkId);
            packer.Read(ref value.CorrelationId);
            packer.Read(ref value.ProfileHash);
            packer.Read(ref shareMode);
            packer.Read(ref value.ScopeId);
            packer.Read(ref action);
            packer.Read(ref value.Authorized);
            packer.Read(ref value.Applied);
            packer.Read(ref rejection);
            packer.Read(ref value.QuestHash);
            packer.Read(ref value.QuestIdString);
            packer.Read(ref value.TaskId);
            packer.Read(ref value.QuestState);
            packer.Read(ref value.TaskState);
            packer.Read(ref value.IsTracking);
            packer.Read(ref value.TaskValue);
            packer.Read(ref value.Error);

            value.ShareMode = (NetworkQuestShareMode)shareMode;
            value.Action = (QuestActionType)action;
            value.RejectionReason = (QuestRejectionReason)rejection;
        }

        public static void Write(this FusionValueWriter packer, NetworkQuestBroadcast value)
        {
            packer.Write(value.NetworkId);
            packer.Write(value.ActorNetworkId);
            packer.Write(value.CorrelationId);
            packer.Write(value.ProfileHash);
            packer.Write((byte)value.ShareMode);
            packer.Write(value.ScopeId);
            packer.Write((byte)value.Action);
            packer.Write(value.QuestHash);
            packer.Write(value.QuestIdString);
            packer.Write(value.TaskId);
            packer.Write(value.QuestState);
            packer.Write(value.TaskState);
            packer.Write(value.IsTracking);
            packer.Write(value.TaskValue);
            packer.Write(value.ServerTime);
        }

        public static void Read(this FusionValueReader packer, ref NetworkQuestBroadcast value)
        {
            byte shareMode = 0;
            byte action = 0;

            packer.Read(ref value.NetworkId);
            packer.Read(ref value.ActorNetworkId);
            packer.Read(ref value.CorrelationId);
            packer.Read(ref value.ProfileHash);
            packer.Read(ref shareMode);
            packer.Read(ref value.ScopeId);
            packer.Read(ref action);
            packer.Read(ref value.QuestHash);
            packer.Read(ref value.QuestIdString);
            packer.Read(ref value.TaskId);
            packer.Read(ref value.QuestState);
            packer.Read(ref value.TaskState);
            packer.Read(ref value.IsTracking);
            packer.Read(ref value.TaskValue);
            packer.Read(ref value.ServerTime);

            value.ShareMode = (NetworkQuestShareMode)shareMode;
            value.Action = (QuestActionType)action;
        }

        public static void Write(this FusionValueWriter packer, NetworkQuestSnapshotEntry value)
        {
            packer.Write(value.ProfileHash);
            packer.Write((byte)value.ShareMode);
            packer.Write(value.ScopeId);
            packer.Write(value.QuestHash);
            packer.Write(value.QuestIdString);
            packer.Write(value.State);
            packer.Write(value.IsTracking);
        }

        public static void Read(this FusionValueReader packer, ref NetworkQuestSnapshotEntry value)
        {
            byte shareMode = 0;

            packer.Read(ref value.ProfileHash);
            packer.Read(ref shareMode);
            packer.Read(ref value.ScopeId);
            packer.Read(ref value.QuestHash);
            packer.Read(ref value.QuestIdString);
            packer.Read(ref value.State);
            packer.Read(ref value.IsTracking);

            value.ShareMode = (NetworkQuestShareMode)shareMode;
        }

        public static void Write(this FusionValueWriter packer, NetworkTaskSnapshotEntry value)
        {
            packer.Write(value.ProfileHash);
            packer.Write((byte)value.ShareMode);
            packer.Write(value.ScopeId);
            packer.Write(value.QuestHash);
            packer.Write(value.QuestIdString);
            packer.Write(value.TaskId);
            packer.Write(value.State);
            packer.Write(value.Value);
        }

        public static void Read(this FusionValueReader packer, ref NetworkTaskSnapshotEntry value)
        {
            byte shareMode = 0;

            packer.Read(ref value.ProfileHash);
            packer.Read(ref shareMode);
            packer.Read(ref value.ScopeId);
            packer.Read(ref value.QuestHash);
            packer.Read(ref value.QuestIdString);
            packer.Read(ref value.TaskId);
            packer.Read(ref value.State);
            packer.Read(ref value.Value);

            value.ShareMode = (NetworkQuestShareMode)shareMode;
        }

        public static void Write(this FusionValueWriter packer, NetworkQuestsSnapshot value)
        {
            packer.Write(value.NetworkId);
            packer.Write(value.ServerTime);
            packer.WriteList(value.QuestEntries);
            packer.WriteList(value.TaskEntries);
        }

        public static void Read(this FusionValueReader packer, ref NetworkQuestsSnapshot value)
        {
            packer.Read(ref value.NetworkId);
            packer.Read(ref value.ServerTime);
            packer.ReadArray(ref value.QuestEntries);
            packer.ReadArray(ref value.TaskEntries);
        }
    }
}
#endif
