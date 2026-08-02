#if GC2_MELEE
using System;
using Arawn.GameCreator2.Networking.Transport.Fusion;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Melee.Transport.Fusion
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

    public static class FusionMeleeValuePackers
    {
        public static void Write(this FusionValueWriter packer, NetworkMeleeHitRequest value)
        {
            packer.Write(value.RequestId);
            packer.Write(value.ActorNetworkId);
            packer.Write(value.CorrelationId);
            packer.Write(value.AttackCorrelationId);
            packer.Write(value.ClientTimestamp);
            packer.Write(value.AttackerNetworkId);
            packer.Write(value.TargetNetworkId);
            WriteVector3(packer, value.HitPoint);
            WriteVector3(packer, value.StrikeDirection);
            packer.Write(value.SkillHash);
            packer.Write(value.WeaponHash);
            packer.Write(value.ComboNodeId);
            packer.Write(value.AttackPhase);
        }

        public static void Read(this FusionValueReader packer, ref NetworkMeleeHitRequest value)
        {
            packer.Read(ref value.RequestId);
            packer.Read(ref value.ActorNetworkId);
            packer.Read(ref value.CorrelationId);
            packer.Read(ref value.AttackCorrelationId);
            packer.Read(ref value.ClientTimestamp);
            packer.Read(ref value.AttackerNetworkId);
            packer.Read(ref value.TargetNetworkId);
            ReadVector3(packer, ref value.HitPoint);
            ReadVector3(packer, ref value.StrikeDirection);
            packer.Read(ref value.SkillHash);
            packer.Read(ref value.WeaponHash);
            packer.Read(ref value.ComboNodeId);
            packer.Read(ref value.AttackPhase);
        }

        public static void Write(this FusionValueWriter packer, NetworkMeleeHitResponse value)
        {
            packer.Write(value.RequestId);
            packer.Write(value.ActorNetworkId);
            packer.Write(value.CorrelationId);
            packer.Write(value.Validated);
            packer.Write((byte)value.RejectionReason);
            packer.Write(value.Damage);
            packer.Write(value.PoiseBroken);
        }

        public static void Read(this FusionValueReader packer, ref NetworkMeleeHitResponse value)
        {
            byte reason = 0;
            packer.Read(ref value.RequestId);
            packer.Read(ref value.ActorNetworkId);
            packer.Read(ref value.CorrelationId);
            packer.Read(ref value.Validated);
            packer.Read(ref reason);
            packer.Read(ref value.Damage);
            packer.Read(ref value.PoiseBroken);
            value.RejectionReason = (MeleeHitRejectionReason)reason;
        }

        public static void Write(this FusionValueWriter packer, NetworkMeleeHitBroadcast value)
        {
            packer.Write(value.AttackerNetworkId);
            packer.Write(value.TargetNetworkId);
            WriteVector3(packer, value.HitPoint);
            WriteVector3(packer, value.StrikeDirection);
            packer.Write(value.SkillHash);
            packer.Write(value.BlockResult);
            packer.Write(value.PoiseBroken);
        }

        public static void Read(this FusionValueReader packer, ref NetworkMeleeHitBroadcast value)
        {
            packer.Read(ref value.AttackerNetworkId);
            packer.Read(ref value.TargetNetworkId);
            ReadVector3(packer, ref value.HitPoint);
            ReadVector3(packer, ref value.StrikeDirection);
            packer.Read(ref value.SkillHash);
            packer.Read(ref value.BlockResult);
            packer.Read(ref value.PoiseBroken);
        }

        public static void Write(this FusionValueWriter packer, NetworkBlockRequest value)
        {
            packer.Write(value.RequestId);
            packer.Write(value.ActorNetworkId);
            packer.Write(value.CorrelationId);
            packer.Write(value.ClientTimestamp);
            packer.Write((byte)value.Action);
            packer.Write(value.ShieldHash);
        }

        public static void Read(this FusionValueReader packer, ref NetworkBlockRequest value)
        {
            byte action = 0;
            packer.Read(ref value.RequestId);
            packer.Read(ref value.ActorNetworkId);
            packer.Read(ref value.CorrelationId);
            packer.Read(ref value.ClientTimestamp);
            packer.Read(ref action);
            packer.Read(ref value.ShieldHash);
            value.Action = (NetworkBlockAction)action;
        }

        public static void Write(this FusionValueWriter packer, NetworkBlockResponse value)
        {
            packer.Write(value.RequestId);
            packer.Write(value.ActorNetworkId);
            packer.Write(value.CorrelationId);
            packer.Write(value.Validated);
            packer.Write((byte)value.RejectionReason);
            packer.Write(value.ServerBlockStartTime);
        }

        public static void Read(this FusionValueReader packer, ref NetworkBlockResponse value)
        {
            byte reason = 0;
            packer.Read(ref value.RequestId);
            packer.Read(ref value.ActorNetworkId);
            packer.Read(ref value.CorrelationId);
            packer.Read(ref value.Validated);
            packer.Read(ref reason);
            packer.Read(ref value.ServerBlockStartTime);
            value.RejectionReason = (BlockRejectionReason)reason;
        }

        public static void Write(this FusionValueWriter packer, NetworkBlockBroadcast value)
        {
            packer.Write(value.CharacterNetworkId);
            packer.Write((byte)value.Action);
            packer.Write(value.ServerTimestamp);
            packer.Write(value.ShieldHash);
        }

        public static void Read(this FusionValueReader packer, ref NetworkBlockBroadcast value)
        {
            byte action = 0;
            packer.Read(ref value.CharacterNetworkId);
            packer.Read(ref action);
            packer.Read(ref value.ServerTimestamp);
            packer.Read(ref value.ShieldHash);
            value.Action = (NetworkBlockAction)action;
        }

        public static void Write(this FusionValueWriter packer, NetworkSkillRequest value)
        {
            packer.Write(value.RequestId);
            packer.Write(value.ActorNetworkId);
            packer.Write(value.CorrelationId);
            packer.Write(value.ClientTimestamp);
            packer.Write(value.TargetNetworkId);
            packer.Write(value.SkillHash);
            packer.Write(value.WeaponHash);
            packer.Write(value.ComboNodeId);
            packer.Write(value.PreviousComboNodeId);
            packer.Write(value.InputKey);
            packer.Write(value.IsChargeRelease);
            packer.Write(value.ChargeDuration);
        }

        public static void Read(this FusionValueReader packer, ref NetworkSkillRequest value)
        {
            packer.Read(ref value.RequestId);
            packer.Read(ref value.ActorNetworkId);
            packer.Read(ref value.CorrelationId);
            packer.Read(ref value.ClientTimestamp);
            packer.Read(ref value.TargetNetworkId);
            packer.Read(ref value.SkillHash);
            packer.Read(ref value.WeaponHash);
            packer.Read(ref value.ComboNodeId);
            packer.Read(ref value.PreviousComboNodeId);
            packer.Read(ref value.InputKey);
            packer.Read(ref value.IsChargeRelease);
            packer.Read(ref value.ChargeDuration);
        }

        public static void Write(this FusionValueWriter packer, NetworkSkillResponse value)
        {
            packer.Write(value.RequestId);
            packer.Write(value.ActorNetworkId);
            packer.Write(value.CorrelationId);
            packer.Write(value.Validated);
            packer.Write((byte)value.RejectionReason);
            packer.Write(value.ComboNodeId);
        }

        public static void Read(this FusionValueReader packer, ref NetworkSkillResponse value)
        {
            byte reason = 0;
            packer.Read(ref value.RequestId);
            packer.Read(ref value.ActorNetworkId);
            packer.Read(ref value.CorrelationId);
            packer.Read(ref value.Validated);
            packer.Read(ref reason);
            packer.Read(ref value.ComboNodeId);
            value.RejectionReason = (SkillRejectionReason)reason;
        }

        public static void Write(this FusionValueWriter packer, NetworkSkillBroadcast value)
        {
            packer.Write(value.CharacterNetworkId);
            packer.Write(value.TargetNetworkId);
            packer.Write(value.SkillHash);
            packer.Write(value.WeaponHash);
            packer.Write(value.ComboNodeId);
            packer.Write(value.ServerTimestamp);
            packer.Write(value.IsCharged);
            packer.Write(value.ChargeLevel);
        }

        public static void Read(this FusionValueReader packer, ref NetworkSkillBroadcast value)
        {
            packer.Read(ref value.CharacterNetworkId);
            packer.Read(ref value.TargetNetworkId);
            packer.Read(ref value.SkillHash);
            packer.Read(ref value.WeaponHash);
            packer.Read(ref value.ComboNodeId);
            packer.Read(ref value.ServerTimestamp);
            packer.Read(ref value.IsCharged);
            packer.Read(ref value.ChargeLevel);
        }

        public static void Write(this FusionValueWriter packer, NetworkChargeRequest value)
        {
            packer.Write(value.RequestId);
            packer.Write(value.ActorNetworkId);
            packer.Write(value.CorrelationId);
            packer.Write(value.ClientTimestamp);
            packer.Write(value.InputKey);
            packer.Write(value.WeaponHash);
        }

        public static void Read(this FusionValueReader packer, ref NetworkChargeRequest value)
        {
            packer.Read(ref value.RequestId);
            packer.Read(ref value.ActorNetworkId);
            packer.Read(ref value.CorrelationId);
            packer.Read(ref value.ClientTimestamp);
            packer.Read(ref value.InputKey);
            packer.Read(ref value.WeaponHash);
        }

        public static void Write(this FusionValueWriter packer, NetworkChargeResponse value)
        {
            packer.Write(value.RequestId);
            packer.Write(value.ActorNetworkId);
            packer.Write(value.CorrelationId);
            packer.Write(value.Validated);
            packer.Write(value.ServerChargeStartTime);
            packer.Write(value.ChargeSkillHash);
        }

        public static void Read(this FusionValueReader packer, ref NetworkChargeResponse value)
        {
            packer.Read(ref value.RequestId);
            packer.Read(ref value.ActorNetworkId);
            packer.Read(ref value.CorrelationId);
            packer.Read(ref value.Validated);
            packer.Read(ref value.ServerChargeStartTime);
            packer.Read(ref value.ChargeSkillHash);
        }

        public static void Write(this FusionValueWriter packer, NetworkChargeBroadcast value)
        {
            packer.Write(value.CharacterNetworkId);
            packer.Write(value.ChargeStarted);
            packer.Write(value.ChargeSkillHash);
            packer.Write(value.ServerTimestamp);
        }

        public static void Read(this FusionValueReader packer, ref NetworkChargeBroadcast value)
        {
            packer.Read(ref value.CharacterNetworkId);
            packer.Read(ref value.ChargeStarted);
            packer.Read(ref value.ChargeSkillHash);
            packer.Read(ref value.ServerTimestamp);
        }

        public static void Write(this FusionValueWriter packer, NetworkReactionBroadcast value)
        {
            packer.Write(value.CharacterNetworkId);
            packer.Write(value.FromNetworkId);
            packer.Write(value.Sequence);
            packer.Write(value.ReactionHash);
            packer.Write((byte)value.PlaybackKind);
            packer.Write(value.Direction);
            packer.Write(value.DirectionY);
            packer.Write(value.Power);
        }

        public static void Read(this FusionValueReader packer, ref NetworkReactionBroadcast value)
        {
            packer.Read(ref value.CharacterNetworkId);
            packer.Read(ref value.FromNetworkId);
            packer.Read(ref value.Sequence);
            packer.Read(ref value.ReactionHash);
            byte playbackKind = 0;
            packer.Read(ref playbackKind);
            value.PlaybackKind = (NetworkReactionPlaybackKind)playbackKind;
            packer.Read(ref value.Direction);
            packer.Read(ref value.DirectionY);
            packer.Read(ref value.Power);
        }

        public static void Write(this FusionValueWriter packer, NetworkMeleeWeaponState value)
        {
            packer.Write(value.WeaponHash);
            packer.Write(value.ShieldFlags);
            packer.Write(value.BlockTiming);
        }

        public static void Read(this FusionValueReader packer, ref NetworkMeleeWeaponState value)
        {
            packer.Read(ref value.WeaponHash);
            packer.Read(ref value.ShieldFlags);
            packer.Read(ref value.BlockTiming);
        }

        public static void Write(this FusionValueWriter packer, NetworkMeleeCharacterSnapshot value)
        {
            packer.Write(value.CharacterNetworkId);
            packer.Write(value.HasWeaponState);
            packer.Write(value.WeaponState);
            packer.Write(value.HasBlockState);
            packer.Write(value.BlockState);
        }

        public static void Read(this FusionValueReader packer, ref NetworkMeleeCharacterSnapshot value)
        {
            packer.Read(ref value.CharacterNetworkId);
            packer.Read(ref value.HasWeaponState);
            packer.Read(ref value.WeaponState);
            packer.Read(ref value.HasBlockState);
            packer.Read(ref value.BlockState);
        }

        private static void WriteVector3(FusionValueWriter packer, Vector3 value)
        {
            packer.Write(value.x);
            packer.Write(value.y);
            packer.Write(value.z);
        }

        private static void ReadVector3(FusionValueReader packer, ref Vector3 value)
        {
            packer.Read(ref value.x);
            packer.Read(ref value.y);
            packer.Read(ref value.z);
        }
    }
}
#endif
