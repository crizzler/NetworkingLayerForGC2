#if GC2_SHOOTER
using System;
using Arawn.GameCreator2.Networking.Transport.Fusion;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Shooter.Transport.Fusion
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

    public static class FusionShooterValuePackers
    {
        public static void Write(this FusionValueWriter packer, NetworkShotRequest value)
        {
            packer.Write(value.RequestId);
            packer.Write(value.ActorNetworkId);
            packer.Write(value.CorrelationId);
            packer.Write(value.ClientTimestamp);
            packer.Write(value.ShooterNetworkId);
            WriteVector3(packer, value.MuzzlePosition);
            WriteVector3(packer, value.ShotDirection);
            packer.Write(value.WeaponHash);
            packer.Write(value.SightHash);
            packer.Write(value.ChargeRatio);
            packer.Write(value.ProjectileIndex);
            packer.Write(value.TotalProjectiles);
        }

        public static void Read(this FusionValueReader packer, ref NetworkShotRequest value)
        {
            packer.Read(ref value.RequestId);
            packer.Read(ref value.ActorNetworkId);
            packer.Read(ref value.CorrelationId);
            packer.Read(ref value.ClientTimestamp);
            packer.Read(ref value.ShooterNetworkId);
            ReadVector3(packer, ref value.MuzzlePosition);
            ReadVector3(packer, ref value.ShotDirection);
            packer.Read(ref value.WeaponHash);
            packer.Read(ref value.SightHash);
            packer.Read(ref value.ChargeRatio);
            packer.Read(ref value.ProjectileIndex);
            packer.Read(ref value.TotalProjectiles);
        }

        public static void Write(this FusionValueWriter packer, NetworkShotResponse value)
        {
            packer.Write(value.RequestId);
            packer.Write(value.ActorNetworkId);
            packer.Write(value.CorrelationId);
            packer.Write(value.Validated);
            packer.Write((byte)value.RejectionReason);
            packer.Write(value.AmmoRemaining);
        }

        public static void Read(this FusionValueReader packer, ref NetworkShotResponse value)
        {
            byte reason = 0;
            packer.Read(ref value.RequestId);
            packer.Read(ref value.ActorNetworkId);
            packer.Read(ref value.CorrelationId);
            packer.Read(ref value.Validated);
            packer.Read(ref reason);
            packer.Read(ref value.AmmoRemaining);
            value.RejectionReason = (ShotRejectionReason)reason;
        }

        public static void Write(this FusionValueWriter packer, NetworkShotBroadcast value)
        {
            packer.Write(value.ShooterNetworkId);
            WriteVector3(packer, value.MuzzlePosition);
            WriteVector3(packer, value.ShotDirection);
            packer.Write(value.WeaponHash);
            packer.Write(value.SightHash);
            WriteVector3(packer, value.HitPoint);
            packer.Write(value.DidHit);
        }

        public static void Read(this FusionValueReader packer, ref NetworkShotBroadcast value)
        {
            packer.Read(ref value.ShooterNetworkId);
            ReadVector3(packer, ref value.MuzzlePosition);
            ReadVector3(packer, ref value.ShotDirection);
            packer.Read(ref value.WeaponHash);
            packer.Read(ref value.SightHash);
            ReadVector3(packer, ref value.HitPoint);
            packer.Read(ref value.DidHit);
        }

        public static void Write(this FusionValueWriter packer, NetworkShooterHitRequest value)
        {
            packer.Write(value.RequestId);
            packer.Write(value.SourceShotRequestId);
            packer.Write(value.ActorNetworkId);
            packer.Write(value.CorrelationId);
            packer.Write(value.ClientTimestamp);
            packer.Write(value.ShooterNetworkId);
            packer.Write(value.TargetNetworkId);
            WriteVector3(packer, value.HitPoint);
            WriteVector3(packer, value.HitNormal);
            packer.Write(value.Distance);
            packer.Write(value.WeaponHash);
            packer.Write(value.PierceIndex);
            packer.Write(value.IsCharacterHit);
            packer.Write(value.ImpactPropNetworkId);
        }

        public static void Read(this FusionValueReader packer, ref NetworkShooterHitRequest value)
        {
            packer.Read(ref value.RequestId);
            packer.Read(ref value.SourceShotRequestId);
            packer.Read(ref value.ActorNetworkId);
            packer.Read(ref value.CorrelationId);
            packer.Read(ref value.ClientTimestamp);
            packer.Read(ref value.ShooterNetworkId);
            packer.Read(ref value.TargetNetworkId);
            ReadVector3(packer, ref value.HitPoint);
            ReadVector3(packer, ref value.HitNormal);
            packer.Read(ref value.Distance);
            packer.Read(ref value.WeaponHash);
            packer.Read(ref value.PierceIndex);
            packer.Read(ref value.IsCharacterHit);
            packer.Read(ref value.ImpactPropNetworkId);
        }

        public static void Write(this FusionValueWriter packer, NetworkShooterHitResponse value)
        {
            packer.Write(value.RequestId);
            packer.Write(value.ActorNetworkId);
            packer.Write(value.CorrelationId);
            packer.Write(value.Validated);
            packer.Write((byte)value.RejectionReason);
            packer.Write(value.Damage);
            packer.Write((byte)value.BlockResult);
        }

        public static void Read(this FusionValueReader packer, ref NetworkShooterHitResponse value)
        {
            byte reason = 0;
            byte block = 0;
            packer.Read(ref value.RequestId);
            packer.Read(ref value.ActorNetworkId);
            packer.Read(ref value.CorrelationId);
            packer.Read(ref value.Validated);
            packer.Read(ref reason);
            packer.Read(ref value.Damage);
            packer.Read(ref block);
            value.RejectionReason = (HitRejectionReason)reason;
            value.BlockResult = (NetworkBlockResult)block;
        }

        public static void Write(this FusionValueWriter packer, NetworkShooterHitBroadcast value)
        {
            packer.Write(value.ShooterNetworkId);
            packer.Write(value.TargetNetworkId);
            WriteVector3(packer, value.HitPoint);
            WriteVector3(packer, value.HitNormal);
            packer.Write(value.WeaponHash);
            packer.Write(value.BlockResult);
            packer.Write(value.ReactionPower);
            packer.Write(value.MaterialHash);
            packer.Write(value.HasImpactMotion);
            if (value.HasImpactMotion)
            {
                packer.Write(value.ImpactMotion);
            }
        }

        public static void Read(this FusionValueReader packer, ref NetworkShooterHitBroadcast value)
        {
            packer.Read(ref value.ShooterNetworkId);
            packer.Read(ref value.TargetNetworkId);
            ReadVector3(packer, ref value.HitPoint);
            ReadVector3(packer, ref value.HitNormal);
            packer.Read(ref value.WeaponHash);
            packer.Read(ref value.BlockResult);
            packer.Read(ref value.ReactionPower);
            packer.Read(ref value.MaterialHash);
            packer.Read(ref value.HasImpactMotion);
            if (value.HasImpactMotion)
            {
                packer.Read(ref value.ImpactMotion);
            }
            else
            {
                value.ImpactMotion = default;
            }
        }

        public static void Write(this FusionValueWriter packer, NetworkShooterImpactMotion value)
        {
            packer.Write(value.PropNetworkId);
            WriteVector3(packer, value.StartPosition);
            WriteQuaternion(packer, value.StartRotation);
            WriteVector3(packer, value.TargetPosition);
            WriteQuaternion(packer, value.TargetRotation);
            WriteVector3(packer, value.HitPoint);
            WriteVector3(packer, value.ImpactDirection);
            packer.Write(value.StartTime);
            packer.Write(value.Duration);
            packer.Write(value.ImpactStrength);
        }

        public static void Read(this FusionValueReader packer, ref NetworkShooterImpactMotion value)
        {
            packer.Read(ref value.PropNetworkId);
            ReadVector3(packer, ref value.StartPosition);
            ReadQuaternion(packer, ref value.StartRotation);
            ReadVector3(packer, ref value.TargetPosition);
            ReadQuaternion(packer, ref value.TargetRotation);
            ReadVector3(packer, ref value.HitPoint);
            ReadVector3(packer, ref value.ImpactDirection);
            packer.Read(ref value.StartTime);
            packer.Read(ref value.Duration);
            packer.Read(ref value.ImpactStrength);
        }

        public static void Write(this FusionValueWriter packer, NetworkReloadRequest value)
        {
            packer.Write(value.RequestId);
            packer.Write(value.ActorNetworkId);
            packer.Write(value.CorrelationId);
            packer.Write(value.CharacterNetworkId);
            packer.Write(value.WeaponHash);
            packer.Write(value.ClientTimestamp);
        }

        public static void Read(this FusionValueReader packer, ref NetworkReloadRequest value)
        {
            packer.Read(ref value.RequestId);
            packer.Read(ref value.ActorNetworkId);
            packer.Read(ref value.CorrelationId);
            packer.Read(ref value.CharacterNetworkId);
            packer.Read(ref value.WeaponHash);
            packer.Read(ref value.ClientTimestamp);
        }

        public static void Write(this FusionValueWriter packer, NetworkQuickReloadRequest value)
        {
            packer.Write(value.RequestId);
            packer.Write(value.ActorNetworkId);
            packer.Write(value.CorrelationId);
            packer.Write(value.CharacterNetworkId);
            packer.Write(value.WeaponHash);
            packer.Write(value.AttemptTime);
        }

        public static void Read(this FusionValueReader packer, ref NetworkQuickReloadRequest value)
        {
            packer.Read(ref value.RequestId);
            packer.Read(ref value.ActorNetworkId);
            packer.Read(ref value.CorrelationId);
            packer.Read(ref value.CharacterNetworkId);
            packer.Read(ref value.WeaponHash);
            packer.Read(ref value.AttemptTime);
        }

        public static void Write(this FusionValueWriter packer, NetworkReloadResponse value)
        {
            packer.Write(value.RequestId);
            packer.Write(value.ActorNetworkId);
            packer.Write(value.CorrelationId);
            packer.Write(value.Validated);
            packer.Write((byte)value.RejectionReason);
            packer.Write(value.QuickReloadWindowStart);
            packer.Write(value.QuickReloadWindowEnd);
        }

        public static void Read(this FusionValueReader packer, ref NetworkReloadResponse value)
        {
            byte reason = 0;
            packer.Read(ref value.RequestId);
            packer.Read(ref value.ActorNetworkId);
            packer.Read(ref value.CorrelationId);
            packer.Read(ref value.Validated);
            packer.Read(ref reason);
            packer.Read(ref value.QuickReloadWindowStart);
            packer.Read(ref value.QuickReloadWindowEnd);
            value.RejectionReason = (ReloadRejectionReason)reason;
        }

        public static void Write(this FusionValueWriter packer, NetworkReloadBroadcast value)
        {
            packer.Write(value.CharacterNetworkId);
            packer.Write(value.WeaponHash);
            packer.Write(value.NewAmmoCount);
            packer.Write((byte)value.EventType);
        }

        public static void Read(this FusionValueReader packer, ref NetworkReloadBroadcast value)
        {
            byte eventType = 0;
            packer.Read(ref value.CharacterNetworkId);
            packer.Read(ref value.WeaponHash);
            packer.Read(ref value.NewAmmoCount);
            packer.Read(ref eventType);
            value.EventType = (ReloadEventType)eventType;
        }

        public static void Write(this FusionValueWriter packer, NetworkFixJamRequest value)
        {
            packer.Write(value.RequestId);
            packer.Write(value.ActorNetworkId);
            packer.Write(value.CorrelationId);
            packer.Write(value.CharacterNetworkId);
            packer.Write(value.WeaponHash);
            packer.Write(value.ClientTimestamp);
        }

        public static void Read(this FusionValueReader packer, ref NetworkFixJamRequest value)
        {
            packer.Read(ref value.RequestId);
            packer.Read(ref value.ActorNetworkId);
            packer.Read(ref value.CorrelationId);
            packer.Read(ref value.CharacterNetworkId);
            packer.Read(ref value.WeaponHash);
            packer.Read(ref value.ClientTimestamp);
        }

        public static void Write(this FusionValueWriter packer, NetworkFixJamResponse value)
        {
            packer.Write(value.RequestId);
            packer.Write(value.ActorNetworkId);
            packer.Write(value.CorrelationId);
            packer.Write(value.Validated);
            packer.Write((byte)value.RejectionReason);
        }

        public static void Read(this FusionValueReader packer, ref NetworkFixJamResponse value)
        {
            byte reason = 0;
            packer.Read(ref value.RequestId);
            packer.Read(ref value.ActorNetworkId);
            packer.Read(ref value.CorrelationId);
            packer.Read(ref value.Validated);
            packer.Read(ref reason);
            value.RejectionReason = (FixJamRejectionReason)reason;
        }

        public static void Write(this FusionValueWriter packer, NetworkJamBroadcast value)
        {
            packer.Write(value.CharacterNetworkId);
            packer.Write(value.WeaponHash);
        }

        public static void Read(this FusionValueReader packer, ref NetworkJamBroadcast value)
        {
            packer.Read(ref value.CharacterNetworkId);
            packer.Read(ref value.WeaponHash);
        }

        public static void Write(this FusionValueWriter packer, NetworkFixJamBroadcast value)
        {
            packer.Write(value.CharacterNetworkId);
            packer.Write(value.WeaponHash);
            packer.Write(value.Success);
        }

        public static void Read(this FusionValueReader packer, ref NetworkFixJamBroadcast value)
        {
            packer.Read(ref value.CharacterNetworkId);
            packer.Read(ref value.WeaponHash);
            packer.Read(ref value.Success);
        }

        public static void Write(this FusionValueWriter packer, NetworkChargeStartRequest value)
        {
            packer.Write(value.RequestId);
            packer.Write(value.ActorNetworkId);
            packer.Write(value.CorrelationId);
            packer.Write(value.CharacterNetworkId);
            packer.Write(value.WeaponHash);
            packer.Write(value.ClientTimestamp);
        }

        public static void Read(this FusionValueReader packer, ref NetworkChargeStartRequest value)
        {
            packer.Read(ref value.RequestId);
            packer.Read(ref value.ActorNetworkId);
            packer.Read(ref value.CorrelationId);
            packer.Read(ref value.CharacterNetworkId);
            packer.Read(ref value.WeaponHash);
            packer.Read(ref value.ClientTimestamp);
        }

        public static void Write(this FusionValueWriter packer, NetworkChargeStartResponse value)
        {
            packer.Write(value.RequestId);
            packer.Write(value.ActorNetworkId);
            packer.Write(value.CorrelationId);
            packer.Write(value.Validated);
            packer.Write((byte)value.RejectionReason);
        }

        public static void Read(this FusionValueReader packer, ref NetworkChargeStartResponse value)
        {
            byte reason = 0;
            packer.Read(ref value.RequestId);
            packer.Read(ref value.ActorNetworkId);
            packer.Read(ref value.CorrelationId);
            packer.Read(ref value.Validated);
            packer.Read(ref reason);
            value.RejectionReason = (ChargeRejectionReason)reason;
        }

        public static void Write(this FusionValueWriter packer, NetworkChargeCancelRequest value)
        {
            packer.Write(value.RequestId);
            packer.Write(value.ActorNetworkId);
            packer.Write(value.CorrelationId);
            packer.Write(value.CharacterNetworkId);
            packer.Write(value.WeaponHash);
            packer.Write(value.ClientTimestamp);
        }

        public static void Read(this FusionValueReader packer, ref NetworkChargeCancelRequest value)
        {
            packer.Read(ref value.RequestId);
            packer.Read(ref value.ActorNetworkId);
            packer.Read(ref value.CorrelationId);
            packer.Read(ref value.CharacterNetworkId);
            packer.Read(ref value.WeaponHash);
            packer.Read(ref value.ClientTimestamp);
        }

        public static void Write(this FusionValueWriter packer, NetworkChargeBroadcast value)
        {
            packer.Write(value.CharacterNetworkId);
            packer.Write(value.WeaponHash);
            packer.Write(value.ChargeRatio);
            packer.Write((byte)value.EventType);
        }

        public static void Read(this FusionValueReader packer, ref NetworkChargeBroadcast value)
        {
            byte eventType = 0;
            packer.Read(ref value.CharacterNetworkId);
            packer.Read(ref value.WeaponHash);
            packer.Read(ref value.ChargeRatio);
            packer.Read(ref eventType);
            value.EventType = (ChargeEventType)eventType;
        }

        public static void Write(this FusionValueWriter packer, NetworkSightSwitchRequest value)
        {
            packer.Write(value.RequestId);
            packer.Write(value.ActorNetworkId);
            packer.Write(value.CorrelationId);
            packer.Write(value.CharacterNetworkId);
            packer.Write(value.WeaponHash);
            packer.Write(value.NewSightHash);
            packer.Write(value.ClientTimestamp);
        }

        public static void Read(this FusionValueReader packer, ref NetworkSightSwitchRequest value)
        {
            packer.Read(ref value.RequestId);
            packer.Read(ref value.ActorNetworkId);
            packer.Read(ref value.CorrelationId);
            packer.Read(ref value.CharacterNetworkId);
            packer.Read(ref value.WeaponHash);
            packer.Read(ref value.NewSightHash);
            packer.Read(ref value.ClientTimestamp);
        }

        public static void Write(this FusionValueWriter packer, NetworkSightSwitchResponse value)
        {
            packer.Write(value.RequestId);
            packer.Write(value.ActorNetworkId);
            packer.Write(value.CorrelationId);
            packer.Write(value.Validated);
            packer.Write((byte)value.RejectionReason);
        }

        public static void Read(this FusionValueReader packer, ref NetworkSightSwitchResponse value)
        {
            byte reason = 0;
            packer.Read(ref value.RequestId);
            packer.Read(ref value.ActorNetworkId);
            packer.Read(ref value.CorrelationId);
            packer.Read(ref value.Validated);
            packer.Read(ref reason);
            value.RejectionReason = (SightSwitchRejectionReason)reason;
        }

        public static void Write(this FusionValueWriter packer, NetworkSightSwitchBroadcast value)
        {
            packer.Write(value.CharacterNetworkId);
            packer.Write(value.WeaponHash);
            packer.Write(value.NewSightHash);
        }

        public static void Read(this FusionValueReader packer, ref NetworkSightSwitchBroadcast value)
        {
            packer.Read(ref value.CharacterNetworkId);
            packer.Read(ref value.WeaponHash);
            packer.Read(ref value.NewSightHash);
        }

        public static void Write(this FusionValueWriter packer, NetworkWeaponState value)
        {
            packer.Write(value.WeaponHash);
            packer.Write(value.SightHash);
            packer.Write(value.AmmoInMagazine);
            packer.Write(value.StateFlags);
            packer.Write(value.LeanAmount);
            packer.Write(value.LeanDecay);
        }

        public static void Read(this FusionValueReader packer, ref NetworkWeaponState value)
        {
            packer.Read(ref value.WeaponHash);
            packer.Read(ref value.SightHash);
            packer.Read(ref value.AmmoInMagazine);
            packer.Read(ref value.StateFlags);
            packer.Read(ref value.LeanAmount);
            packer.Read(ref value.LeanDecay);
        }

        public static void Write(this FusionValueWriter packer, NetworkAimState value)
        {
            WriteVector3(packer, value.AimPoint);
            packer.Write(value.Accuracy);
            packer.Write(value.IsAiming);
            packer.Write(value.CompressedDirection);
        }

        public static void Read(this FusionValueReader packer, ref NetworkAimState value)
        {
            ReadVector3(packer, ref value.AimPoint);
            packer.Read(ref value.Accuracy);
            packer.Read(ref value.IsAiming);
            packer.Read(ref value.CompressedDirection);
        }

        public static void Write(this FusionValueWriter packer, NetworkShooterCharacterSnapshot value)
        {
            packer.Write(value.CharacterNetworkId);
            packer.Write(value.WeaponState);
            packer.Write(value.AimState);
            packer.Write(value.ServerTime);
        }

        public static void Read(this FusionValueReader packer, ref NetworkShooterCharacterSnapshot value)
        {
            packer.Read(ref value.CharacterNetworkId);
            packer.Read(ref value.WeaponState);
            packer.Read(ref value.AimState);
            packer.Read(ref value.ServerTime);
        }

        public static void Write(this FusionValueWriter packer, NetworkShooterImpactPropSnapshot value)
        {
            packer.Write(value.PropNetworkId);
            WriteVector3(packer, value.Position);
            WriteQuaternion(packer, value.Rotation);
            packer.Write(value.HasActiveMotion);
            if (value.HasActiveMotion)
            {
                packer.Write(value.ActiveMotion);
            }
            packer.Write(value.ServerTime);
        }

        public static void Read(this FusionValueReader packer, ref NetworkShooterImpactPropSnapshot value)
        {
            packer.Read(ref value.PropNetworkId);
            ReadVector3(packer, ref value.Position);
            ReadQuaternion(packer, ref value.Rotation);
            packer.Read(ref value.HasActiveMotion);
            if (value.HasActiveMotion)
            {
                packer.Read(ref value.ActiveMotion);
            }
            else
            {
                value.ActiveMotion = default;
            }
            packer.Read(ref value.ServerTime);
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
