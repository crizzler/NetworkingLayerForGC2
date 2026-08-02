#if GC2_ABILITIES
using System;
using Arawn.GameCreator2.Networking;
using UnityEngine;
using Arawn.GameCreator2.Networking.Transport.Fusion;

namespace Arawn.GameCreator2.Networking.Abilities.Transport.Fusion
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

        public int ReadCollectionCount() => ReadArrayCount();

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

    public static class FusionAbilitiesValuePackers
    {
        public static void Write(this FusionValueWriter packer, NetworkAbilitiesFullSnapshot value)
        {
            packer.Write(value.ServerTime);
            WriteCharacterSnapshots(packer, value.Characters);
        }

        public static void Read(this FusionValueReader packer, ref NetworkAbilitiesFullSnapshot value)
        {
            packer.Read(ref value.ServerTime);
            int count = packer.ReadCollectionCount();
            if (count < 0)
            {
                value.Characters = null;
                return;
            }

            value.Characters = new NetworkAbilityCharacterSnapshot[count];
            for (int i = 0; i < count; i++) packer.Read(ref value.Characters[i]);
        }

        public static void Write(this FusionValueWriter packer, NetworkAbilityCharacterSnapshot value)
        {
            packer.Write(value.State);
            WriteSlots(packer, value.Slots);
            WriteCooldowns(packer, value.Cooldowns);
            WriteCasts(packer, value.ActiveCasts);
        }

        public static void Read(this FusionValueReader packer, ref NetworkAbilityCharacterSnapshot value)
        {
            packer.Read(ref value.State);

            int slotCount = packer.ReadCollectionCount();
            if (slotCount >= 0)
            {
                value.Slots = new NetworkAbilitySlotEntry[slotCount];
                for (int i = 0; i < slotCount; i++) packer.Read(ref value.Slots[i]);
            }

            int cooldownCount = packer.ReadCollectionCount();
            if (cooldownCount >= 0)
            {
                value.Cooldowns = new NetworkCooldownEntry[cooldownCount];
                for (int i = 0; i < cooldownCount; i++) packer.Read(ref value.Cooldowns[i]);
            }

            int castCount = packer.ReadCollectionCount();
            if (castCount >= 0)
            {
                value.ActiveCasts = new NetworkAbilityCastBroadcast[castCount];
                for (int i = 0; i < castCount; i++) packer.Read(ref value.ActiveCasts[i]);
            }
        }

        private static void WriteCharacterSnapshots(
            FusionValueWriter packer,
            NetworkAbilityCharacterSnapshot[] values)
        {
            if (values == null) { packer.Write(-1); return; }
            packer.Write(values.Length);
            for (int i = 0; i < values.Length; i++) packer.Write(values[i]);
        }

        private static void WriteSlots(FusionValueWriter packer, NetworkAbilitySlotEntry[] values)
        {
            if (values == null) { packer.Write(-1); return; }
            packer.Write(values.Length);
            for (int i = 0; i < values.Length; i++) packer.Write(values[i]);
        }

        private static void WriteCooldowns(FusionValueWriter packer, NetworkCooldownEntry[] values)
        {
            if (values == null) { packer.Write(-1); return; }
            packer.Write(values.Length);
            for (int i = 0; i < values.Length; i++) packer.Write(values[i]);
        }

        private static void WriteCasts(FusionValueWriter packer, NetworkAbilityCastBroadcast[] values)
        {
            if (values == null) { packer.Write(-1); return; }
            packer.Write(values.Length);
            for (int i = 0; i < values.Length; i++) packer.Write(values[i]);
        }

        public static void Write(this FusionValueWriter packer, NetworkAbilityCastRequest value)
        {
            packer.Write(value.RequestId);
            packer.Write(value.ActorNetworkId);
            packer.Write(value.CorrelationId);
            packer.Write(value.CasterNetworkId);
            packer.Write(value.AbilityIdHash);
            packer.Write(value.ClientTime);
            packer.Write(value.TargetType);
            WriteVector3(packer, value.TargetPosition);
            packer.Write(value.TargetNetworkId);
            packer.Write(value.AutoConfirm);
        }

        public static void Read(this FusionValueReader packer, ref NetworkAbilityCastRequest value)
        {
            packer.Read(ref value.RequestId);
            packer.Read(ref value.ActorNetworkId);
            packer.Read(ref value.CorrelationId);
            packer.Read(ref value.CasterNetworkId);
            packer.Read(ref value.AbilityIdHash);
            packer.Read(ref value.ClientTime);
            packer.Read(ref value.TargetType);
            ReadVector3(packer, ref value.TargetPosition);
            packer.Read(ref value.TargetNetworkId);
            packer.Read(ref value.AutoConfirm);
        }

        public static void Write(this FusionValueWriter packer, NetworkAbilityCastResponse value)
        {
            packer.Write(value.RequestId);
            packer.Write(value.ActorNetworkId);
            packer.Write(value.CorrelationId);
            packer.Write(value.CastInstanceId);
            packer.Write(value.Approved);
            packer.Write((byte)value.RejectReason);
            packer.Write(value.CooldownEndTime);
        }

        public static void Read(this FusionValueReader packer, ref NetworkAbilityCastResponse value)
        {
            byte reason = 0;

            packer.Read(ref value.RequestId);
            packer.Read(ref value.ActorNetworkId);
            packer.Read(ref value.CorrelationId);
            packer.Read(ref value.CastInstanceId);
            packer.Read(ref value.Approved);
            packer.Read(ref reason);
            packer.Read(ref value.CooldownEndTime);

            value.RejectReason = (AbilityCastRejectReason)reason;
        }

        public static void Write(this FusionValueWriter packer, NetworkAbilityCastBroadcast value)
        {
            packer.Write(value.CasterNetworkId);
            packer.Write(value.CastInstanceId);
            packer.Write(value.AbilityIdHash);
            packer.Write(value.ServerTime);
            packer.Write(value.TargetType);
            WriteVector3(packer, value.TargetPosition);
            packer.Write(value.TargetNetworkId);
            packer.Write((byte)value.CastState);
        }

        public static void Read(this FusionValueReader packer, ref NetworkAbilityCastBroadcast value)
        {
            byte state = 0;

            packer.Read(ref value.CasterNetworkId);
            packer.Read(ref value.CastInstanceId);
            packer.Read(ref value.AbilityIdHash);
            packer.Read(ref value.ServerTime);
            packer.Read(ref value.TargetType);
            ReadVector3(packer, ref value.TargetPosition);
            packer.Read(ref value.TargetNetworkId);
            packer.Read(ref state);

            value.CastState = (AbilityCastState)state;
        }

        public static void Write(this FusionValueWriter packer, NetworkAbilityEffectBroadcast value)
        {
            packer.Write(value.CastInstanceId);
            packer.Write(value.ServerTime);
            packer.Write((byte)value.EffectType);
            WriteVector3(packer, value.Position);
            WriteVector3(packer, value.Direction);
            packer.Write(value.TargetCount);
            WriteTargets8(
                packer,
                value.Target0,
                value.Target1,
                value.Target2,
                value.Target3,
                value.Target4,
                value.Target5,
                value.Target6,
                value.Target7);
        }

        public static void Read(this FusionValueReader packer, ref NetworkAbilityEffectBroadcast value)
        {
            byte effectType = 0;

            packer.Read(ref value.CastInstanceId);
            packer.Read(ref value.ServerTime);
            packer.Read(ref effectType);
            ReadVector3(packer, ref value.Position);
            ReadVector3(packer, ref value.Direction);
            packer.Read(ref value.TargetCount);
            ReadTargets8(
                packer,
                ref value.Target0,
                ref value.Target1,
                ref value.Target2,
                ref value.Target3,
                ref value.Target4,
                ref value.Target5,
                ref value.Target6,
                ref value.Target7);

            value.EffectType = (AbilityEffectType)effectType;
        }

        public static void Write(this FusionValueWriter packer, NetworkProjectileSpawnBroadcast value)
        {
            packer.Write(value.ProjectileId);
            packer.Write(value.CastInstanceId);
            packer.Write(value.ProjectileHash);
            WriteVector3(packer, value.SpawnPosition);
            WriteVector3(packer, value.Direction);
            WriteVector3(packer, value.TargetPosition);
            packer.Write(value.TargetNetworkId);
            packer.Write(value.ServerTime);
        }

        public static void Read(this FusionValueReader packer, ref NetworkProjectileSpawnBroadcast value)
        {
            packer.Read(ref value.ProjectileId);
            packer.Read(ref value.CastInstanceId);
            packer.Read(ref value.ProjectileHash);
            ReadVector3(packer, ref value.SpawnPosition);
            ReadVector3(packer, ref value.Direction);
            ReadVector3(packer, ref value.TargetPosition);
            packer.Read(ref value.TargetNetworkId);
            packer.Read(ref value.ServerTime);
        }

        public static void Write(this FusionValueWriter packer, NetworkProjectileEventBroadcast value)
        {
            packer.Write(value.ProjectileId);
            packer.Write((byte)value.EventType);
            WriteVector3(packer, value.Position);
            packer.Write(value.HitTargetNetworkId);
            packer.Write(value.ServerTime);
        }

        public static void Read(this FusionValueReader packer, ref NetworkProjectileEventBroadcast value)
        {
            byte eventType = 0;

            packer.Read(ref value.ProjectileId);
            packer.Read(ref eventType);
            ReadVector3(packer, ref value.Position);
            packer.Read(ref value.HitTargetNetworkId);
            packer.Read(ref value.ServerTime);

            value.EventType = (ProjectileEventType)eventType;
        }

        public static void Write(this FusionValueWriter packer, NetworkImpactSpawnBroadcast value)
        {
            packer.Write(value.ImpactId);
            packer.Write(value.CastInstanceId);
            packer.Write(value.ImpactHash);
            WriteVector3(packer, value.Position);
            WriteQuaternion(packer, value.Rotation);
            packer.Write(value.ServerTime);
        }

        public static void Read(this FusionValueReader packer, ref NetworkImpactSpawnBroadcast value)
        {
            packer.Read(ref value.ImpactId);
            packer.Read(ref value.CastInstanceId);
            packer.Read(ref value.ImpactHash);
            ReadVector3(packer, ref value.Position);
            ReadQuaternion(packer, ref value.Rotation);
            packer.Read(ref value.ServerTime);
        }

        public static void Write(this FusionValueWriter packer, NetworkImpactHitBroadcast value)
        {
            packer.Write(value.ImpactId);
            packer.Write(value.ServerTime);
            packer.Write(value.TargetCount);
            packer.Write(value.Target0);
            packer.Write(value.Target1);
            packer.Write(value.Target2);
            packer.Write(value.Target3);
            packer.Write(value.Target4);
            packer.Write(value.Target5);
            packer.Write(value.Target6);
            packer.Write(value.Target7);
            packer.Write(value.Target8);
            packer.Write(value.Target9);
            packer.Write(value.Target10);
            packer.Write(value.Target11);
            packer.Write(value.Target12);
            packer.Write(value.Target13);
            packer.Write(value.Target14);
            packer.Write(value.Target15);
        }

        public static void Read(this FusionValueReader packer, ref NetworkImpactHitBroadcast value)
        {
            packer.Read(ref value.ImpactId);
            packer.Read(ref value.ServerTime);
            packer.Read(ref value.TargetCount);
            packer.Read(ref value.Target0);
            packer.Read(ref value.Target1);
            packer.Read(ref value.Target2);
            packer.Read(ref value.Target3);
            packer.Read(ref value.Target4);
            packer.Read(ref value.Target5);
            packer.Read(ref value.Target6);
            packer.Read(ref value.Target7);
            packer.Read(ref value.Target8);
            packer.Read(ref value.Target9);
            packer.Read(ref value.Target10);
            packer.Read(ref value.Target11);
            packer.Read(ref value.Target12);
            packer.Read(ref value.Target13);
            packer.Read(ref value.Target14);
            packer.Read(ref value.Target15);
        }

        public static void Write(this FusionValueWriter packer, NetworkCooldownRequest value)
        {
            packer.Write(value.RequestId);
            packer.Write(value.ActorNetworkId);
            packer.Write(value.CorrelationId);
            packer.Write(value.CasterNetworkId);
            packer.Write(value.AbilityIdHash);
        }

        public static void Read(this FusionValueReader packer, ref NetworkCooldownRequest value)
        {
            packer.Read(ref value.RequestId);
            packer.Read(ref value.ActorNetworkId);
            packer.Read(ref value.CorrelationId);
            packer.Read(ref value.CasterNetworkId);
            packer.Read(ref value.AbilityIdHash);
        }

        public static void Write(this FusionValueWriter packer, NetworkCooldownResponse value)
        {
            packer.Write(value.RequestId);
            packer.Write(value.ActorNetworkId);
            packer.Write(value.CorrelationId);
            packer.Write(value.IsOnCooldown);
            packer.Write(value.CooldownEndTime);
            packer.Write(value.TotalDuration);
            packer.Write(value.TimedOut);
        }

        public static void Read(this FusionValueReader packer, ref NetworkCooldownResponse value)
        {
            packer.Read(ref value.RequestId);
            packer.Read(ref value.ActorNetworkId);
            packer.Read(ref value.CorrelationId);
            packer.Read(ref value.IsOnCooldown);
            packer.Read(ref value.CooldownEndTime);
            packer.Read(ref value.TotalDuration);
            packer.Read(ref value.TimedOut);
        }

        public static void Write(this FusionValueWriter packer, NetworkCooldownBroadcast value)
        {
            packer.Write(value.CharacterNetworkId);
            packer.Write(value.AbilityIdHash);
            packer.Write(value.CooldownEndTime);
            packer.Write(value.TotalDuration);
            packer.Write((byte)value.Reason);
        }

        public static void Read(this FusionValueReader packer, ref NetworkCooldownBroadcast value)
        {
            byte reason = 0;

            packer.Read(ref value.CharacterNetworkId);
            packer.Read(ref value.AbilityIdHash);
            packer.Read(ref value.CooldownEndTime);
            packer.Read(ref value.TotalDuration);
            packer.Read(ref reason);

            value.Reason = (CooldownChangeReason)reason;
        }

        public static void Write(this FusionValueWriter packer, NetworkAbilityLearnRequest value)
        {
            packer.Write(value.RequestId);
            packer.Write(value.ActorNetworkId);
            packer.Write(value.CorrelationId);
            packer.Write(value.CharacterNetworkId);
            packer.Write(value.AbilityIdHash);
            packer.Write(unchecked((byte)value.Slot));
            packer.Write(value.IsLearning);
        }

        public static void Read(this FusionValueReader packer, ref NetworkAbilityLearnRequest value)
        {
            byte slot = 0;

            packer.Read(ref value.RequestId);
            packer.Read(ref value.ActorNetworkId);
            packer.Read(ref value.CorrelationId);
            packer.Read(ref value.CharacterNetworkId);
            packer.Read(ref value.AbilityIdHash);
            packer.Read(ref slot);
            packer.Read(ref value.IsLearning);

            value.Slot = unchecked((sbyte)slot);
        }

        public static void Write(this FusionValueWriter packer, NetworkAbilityLearnResponse value)
        {
            packer.Write(value.RequestId);
            packer.Write(value.ActorNetworkId);
            packer.Write(value.CorrelationId);
            packer.Write(value.Approved);
            packer.Write((byte)value.RejectReason);
        }

        public static void Read(this FusionValueReader packer, ref NetworkAbilityLearnResponse value)
        {
            byte reason = 0;

            packer.Read(ref value.RequestId);
            packer.Read(ref value.ActorNetworkId);
            packer.Read(ref value.CorrelationId);
            packer.Read(ref value.Approved);
            packer.Read(ref reason);

            value.RejectReason = (AbilityLearnRejectReason)reason;
        }

        public static void Write(this FusionValueWriter packer, NetworkAbilityLearnBroadcast value)
        {
            packer.Write(value.CharacterNetworkId);
            packer.Write(value.AbilityIdHash);
            packer.Write(unchecked((byte)value.Slot));
            packer.Write(value.IsLearned);
        }

        public static void Read(this FusionValueReader packer, ref NetworkAbilityLearnBroadcast value)
        {
            byte slot = 0;

            packer.Read(ref value.CharacterNetworkId);
            packer.Read(ref value.AbilityIdHash);
            packer.Read(ref slot);
            packer.Read(ref value.IsLearned);

            value.Slot = unchecked((sbyte)slot);
        }

        public static void Write(this FusionValueWriter packer, NetworkCastCancelRequest value)
        {
            packer.Write(value.RequestId);
            packer.Write(value.ActorNetworkId);
            packer.Write(value.CorrelationId);
            packer.Write(value.CasterNetworkId);
            packer.Write(value.CastInstanceId);
        }

        public static void Read(this FusionValueReader packer, ref NetworkCastCancelRequest value)
        {
            packer.Read(ref value.RequestId);
            packer.Read(ref value.ActorNetworkId);
            packer.Read(ref value.CorrelationId);
            packer.Read(ref value.CasterNetworkId);
            packer.Read(ref value.CastInstanceId);
        }

        public static void Write(this FusionValueWriter packer, NetworkCastCancelResponse value)
        {
            packer.Write(value.RequestId);
            packer.Write(value.ActorNetworkId);
            packer.Write(value.CorrelationId);
            packer.Write(value.Approved);
            packer.Write(value.CastInstanceId);
            packer.Write(value.TimedOut);
        }

        public static void Read(this FusionValueReader packer, ref NetworkCastCancelResponse value)
        {
            packer.Read(ref value.RequestId);
            packer.Read(ref value.ActorNetworkId);
            packer.Read(ref value.CorrelationId);
            packer.Read(ref value.Approved);
            packer.Read(ref value.CastInstanceId);
            packer.Read(ref value.TimedOut);
        }

        public static void Write(this FusionValueWriter packer, NetworkAbilityStateRequest value)
        {
            packer.Write(value.RequestId);
            packer.Write(value.ActorNetworkId);
            packer.Write(value.CorrelationId);
            packer.Write(value.CharacterNetworkId);
        }

        public static void Read(this FusionValueReader packer, ref NetworkAbilityStateRequest value)
        {
            packer.Read(ref value.RequestId);
            packer.Read(ref value.ActorNetworkId);
            packer.Read(ref value.CorrelationId);
            packer.Read(ref value.CharacterNetworkId);
        }

        public static void Write(this FusionValueWriter packer, NetworkAbilityStateResponse value)
        {
            packer.Write(value.RequestId);
            packer.Write(value.ActorNetworkId);
            packer.Write(value.CorrelationId);
            packer.Write(value.CharacterNetworkId);
            packer.Write(value.SlotCount);
            packer.Write(value.CooldownCount);
            packer.Write(value.IsCasting);
            packer.Write(value.CurrentCastId);
            packer.Write(value.CurrentCastAbilityHash);
        }

        public static void Read(this FusionValueReader packer, ref NetworkAbilityStateResponse value)
        {
            packer.Read(ref value.RequestId);
            packer.Read(ref value.ActorNetworkId);
            packer.Read(ref value.CorrelationId);
            packer.Read(ref value.CharacterNetworkId);
            packer.Read(ref value.SlotCount);
            packer.Read(ref value.CooldownCount);
            packer.Read(ref value.IsCasting);
            packer.Read(ref value.CurrentCastId);
            packer.Read(ref value.CurrentCastAbilityHash);
        }

        public static void Write(this FusionValueWriter packer, NetworkAbilitySlotEntry value)
        {
            packer.Write(value.SlotIndex);
            packer.Write(value.AbilityHash);
        }

        public static void Read(this FusionValueReader packer, ref NetworkAbilitySlotEntry value)
        {
            packer.Read(ref value.SlotIndex);
            packer.Read(ref value.AbilityHash);
        }

        public static void Write(this FusionValueWriter packer, NetworkCooldownEntry value)
        {
            packer.Write(value.AbilityHash);
            packer.Write(value.EndTime);
            packer.Write(value.TotalDuration);
        }

        public static void Read(this FusionValueReader packer, ref NetworkCooldownEntry value)
        {
            packer.Read(ref value.AbilityHash);
            packer.Read(ref value.EndTime);
            packer.Read(ref value.TotalDuration);
        }

        private static void WriteTargets8(
            FusionValueWriter packer,
            uint target0,
            uint target1,
            uint target2,
            uint target3,
            uint target4,
            uint target5,
            uint target6,
            uint target7)
        {
            packer.Write(target0);
            packer.Write(target1);
            packer.Write(target2);
            packer.Write(target3);
            packer.Write(target4);
            packer.Write(target5);
            packer.Write(target6);
            packer.Write(target7);
        }

        private static void ReadTargets8(
            FusionValueReader packer,
            ref uint target0,
            ref uint target1,
            ref uint target2,
            ref uint target3,
            ref uint target4,
            ref uint target5,
            ref uint target6,
            ref uint target7)
        {
            packer.Read(ref target0);
            packer.Read(ref target1);
            packer.Read(ref target2);
            packer.Read(ref target3);
            packer.Read(ref target4);
            packer.Read(ref target5);
            packer.Read(ref target6);
            packer.Read(ref target7);
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

        private static void WriteQuaternion(FusionValueWriter packer, Quaternion value)
        {
            packer.Write(value.x);
            packer.Write(value.y);
            packer.Write(value.z);
            packer.Write(value.w);
        }

        private static void ReadQuaternion(FusionValueReader packer, ref Quaternion value)
        {
            packer.Read(ref value.x);
            packer.Read(ref value.y);
            packer.Read(ref value.z);
            packer.Read(ref value.w);
        }
    }
}
#endif
