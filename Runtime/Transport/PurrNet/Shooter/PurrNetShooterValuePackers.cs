#if GC2_SHOOTER
using JetBrains.Annotations;
using PurrNet.Modules;
using PurrNet.Packing;
using PurrNet.Utils;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Shooter.Transport.PurrNet
{
    [UsedImplicitly]
    public static class PurrNetShooterValuePackers
    {
        [RegisterPackers]
        static void Register()
        {
            Hasher.PrepareType(typeof(NetworkShotRequest));
            Hasher.PrepareType(typeof(NetworkShotResponse));
            Hasher.PrepareType(typeof(NetworkShotBroadcast));
            Hasher.PrepareType(typeof(NetworkShooterHitRequest));
            Hasher.PrepareType(typeof(NetworkShooterHitResponse));
            Hasher.PrepareType(typeof(NetworkShooterHitBroadcast));
            Hasher.PrepareType(typeof(NetworkShooterImpactMotion));
            Hasher.PrepareType(typeof(NetworkReloadRequest));
            Hasher.PrepareType(typeof(NetworkReloadResponse));
            Hasher.PrepareType(typeof(NetworkReloadBroadcast));
            Hasher.PrepareType(typeof(NetworkWeaponState));
            Hasher.PrepareType(typeof(NetworkAimState));
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkShotRequest value)
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

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkShotRequest value)
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

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkShotResponse value)
        {
            packer.Write(value.RequestId);
            packer.Write(value.ActorNetworkId);
            packer.Write(value.CorrelationId);
            packer.Write(value.Validated);
            packer.Write((byte)value.RejectionReason);
            packer.Write(value.AmmoRemaining);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkShotResponse value)
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

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkShotBroadcast value)
        {
            packer.Write(value.ShooterNetworkId);
            WriteVector3(packer, value.MuzzlePosition);
            WriteVector3(packer, value.ShotDirection);
            packer.Write(value.WeaponHash);
            packer.Write(value.SightHash);
            WriteVector3(packer, value.HitPoint);
            packer.Write(value.DidHit);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkShotBroadcast value)
        {
            packer.Read(ref value.ShooterNetworkId);
            ReadVector3(packer, ref value.MuzzlePosition);
            ReadVector3(packer, ref value.ShotDirection);
            packer.Read(ref value.WeaponHash);
            packer.Read(ref value.SightHash);
            ReadVector3(packer, ref value.HitPoint);
            packer.Read(ref value.DidHit);
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkShooterHitRequest value)
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

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkShooterHitRequest value)
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

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkShooterHitResponse value)
        {
            packer.Write(value.RequestId);
            packer.Write(value.ActorNetworkId);
            packer.Write(value.CorrelationId);
            packer.Write(value.Validated);
            packer.Write((byte)value.RejectionReason);
            packer.Write(value.Damage);
            packer.Write((byte)value.BlockResult);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkShooterHitResponse value)
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

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkShooterHitBroadcast value)
        {
            packer.Write(value.ShooterNetworkId);
            packer.Write(value.TargetNetworkId);
            WriteVector3(packer, value.HitPoint);
            WriteVector3(packer, value.HitNormal);
            packer.Write(value.WeaponHash);
            packer.Write(value.BlockResult);
            packer.Write(value.MaterialHash);
            packer.Write(value.HasImpactMotion);
            if (value.HasImpactMotion)
            {
                packer.Write(value.ImpactMotion);
            }
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkShooterHitBroadcast value)
        {
            packer.Read(ref value.ShooterNetworkId);
            packer.Read(ref value.TargetNetworkId);
            ReadVector3(packer, ref value.HitPoint);
            ReadVector3(packer, ref value.HitNormal);
            packer.Read(ref value.WeaponHash);
            packer.Read(ref value.BlockResult);
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

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkShooterImpactMotion value)
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

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkShooterImpactMotion value)
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

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkReloadRequest value)
        {
            packer.Write(value.RequestId);
            packer.Write(value.ActorNetworkId);
            packer.Write(value.CorrelationId);
            packer.Write(value.CharacterNetworkId);
            packer.Write(value.WeaponHash);
            packer.Write(value.ClientTimestamp);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkReloadRequest value)
        {
            packer.Read(ref value.RequestId);
            packer.Read(ref value.ActorNetworkId);
            packer.Read(ref value.CorrelationId);
            packer.Read(ref value.CharacterNetworkId);
            packer.Read(ref value.WeaponHash);
            packer.Read(ref value.ClientTimestamp);
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkReloadResponse value)
        {
            packer.Write(value.RequestId);
            packer.Write(value.ActorNetworkId);
            packer.Write(value.CorrelationId);
            packer.Write(value.Validated);
            packer.Write((byte)value.RejectionReason);
            packer.Write(value.QuickReloadWindowStart);
            packer.Write(value.QuickReloadWindowEnd);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkReloadResponse value)
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

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkReloadBroadcast value)
        {
            packer.Write(value.CharacterNetworkId);
            packer.Write(value.WeaponHash);
            packer.Write(value.NewAmmoCount);
            packer.Write((byte)value.EventType);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkReloadBroadcast value)
        {
            byte eventType = 0;
            packer.Read(ref value.CharacterNetworkId);
            packer.Read(ref value.WeaponHash);
            packer.Read(ref value.NewAmmoCount);
            packer.Read(ref eventType);
            value.EventType = (ReloadEventType)eventType;
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkWeaponState value)
        {
            packer.Write(value.WeaponHash);
            packer.Write(value.SightHash);
            packer.Write(value.AmmoInMagazine);
            packer.Write(value.StateFlags);
            packer.Write(value.LeanAmount);
            packer.Write(value.LeanDecay);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkWeaponState value)
        {
            packer.Read(ref value.WeaponHash);
            packer.Read(ref value.SightHash);
            packer.Read(ref value.AmmoInMagazine);
            packer.Read(ref value.StateFlags);
            packer.Read(ref value.LeanAmount);
            packer.Read(ref value.LeanDecay);
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkAimState value)
        {
            WriteVector3(packer, value.AimPoint);
            packer.Write(value.Accuracy);
            packer.Write(value.IsAiming);
            packer.Write(value.CompressedDirection);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkAimState value)
        {
            ReadVector3(packer, ref value.AimPoint);
            packer.Read(ref value.Accuracy);
            packer.Read(ref value.IsAiming);
            packer.Read(ref value.CompressedDirection);
        }

        private static void WriteVector3(BitPacker packer, Vector3 value)
        {
            packer.Write(value.x);
            packer.Write(value.y);
            packer.Write(value.z);
        }

        private static void ReadVector3(BitPacker packer, ref Vector3 value)
        {
            packer.Read(ref value.x);
            packer.Read(ref value.y);
            packer.Read(ref value.z);
        }

        private static void WriteQuaternion(BitPacker packer, Quaternion value)
        {
            packer.Write(value.x);
            packer.Write(value.y);
            packer.Write(value.z);
            packer.Write(value.w);
        }

        private static void ReadQuaternion(BitPacker packer, ref Quaternion value)
        {
            packer.Read(ref value.x);
            packer.Read(ref value.y);
            packer.Read(ref value.z);
            packer.Read(ref value.w);
        }
    }
}
#endif
