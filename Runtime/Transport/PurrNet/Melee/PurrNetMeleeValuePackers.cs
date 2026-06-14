#if GC2_MELEE
using JetBrains.Annotations;
using PurrNet.Modules;
using PurrNet.Packing;
using PurrNet.Utils;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Melee.Transport.PurrNet
{
    [UsedImplicitly]
    public static class PurrNetMeleeValuePackers
    {
        [RegisterPackers]
        static void Register()
        {
            Hasher.PrepareType(typeof(NetworkMeleeHitRequest));
            Hasher.PrepareType(typeof(NetworkMeleeHitResponse));
            Hasher.PrepareType(typeof(NetworkMeleeHitBroadcast));
            Hasher.PrepareType(typeof(NetworkBlockRequest));
            Hasher.PrepareType(typeof(NetworkBlockResponse));
            Hasher.PrepareType(typeof(NetworkBlockBroadcast));
            Hasher.PrepareType(typeof(NetworkSkillRequest));
            Hasher.PrepareType(typeof(NetworkSkillResponse));
            Hasher.PrepareType(typeof(NetworkSkillBroadcast));
            Hasher.PrepareType(typeof(NetworkChargeRequest));
            Hasher.PrepareType(typeof(NetworkChargeResponse));
            Hasher.PrepareType(typeof(NetworkChargeBroadcast));
            Hasher.PrepareType(typeof(NetworkReactionBroadcast));
            Hasher.PrepareType(typeof(NetworkMeleeWeaponState));
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkMeleeHitRequest value)
        {
            packer.Write(value.RequestId);
            packer.Write(value.ActorNetworkId);
            packer.Write(value.CorrelationId);
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

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkMeleeHitRequest value)
        {
            packer.Read(ref value.RequestId);
            packer.Read(ref value.ActorNetworkId);
            packer.Read(ref value.CorrelationId);
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

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkMeleeHitResponse value)
        {
            packer.Write(value.RequestId);
            packer.Write(value.ActorNetworkId);
            packer.Write(value.CorrelationId);
            packer.Write(value.Validated);
            packer.Write((byte)value.RejectionReason);
            packer.Write(value.Damage);
            packer.Write(value.PoiseBroken);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkMeleeHitResponse value)
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

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkMeleeHitBroadcast value)
        {
            packer.Write(value.AttackerNetworkId);
            packer.Write(value.TargetNetworkId);
            WriteVector3(packer, value.HitPoint);
            WriteVector3(packer, value.StrikeDirection);
            packer.Write(value.SkillHash);
            packer.Write(value.BlockResult);
            packer.Write(value.PoiseBroken);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkMeleeHitBroadcast value)
        {
            packer.Read(ref value.AttackerNetworkId);
            packer.Read(ref value.TargetNetworkId);
            ReadVector3(packer, ref value.HitPoint);
            ReadVector3(packer, ref value.StrikeDirection);
            packer.Read(ref value.SkillHash);
            packer.Read(ref value.BlockResult);
            packer.Read(ref value.PoiseBroken);
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkBlockRequest value)
        {
            packer.Write(value.RequestId);
            packer.Write(value.ActorNetworkId);
            packer.Write(value.CorrelationId);
            packer.Write(value.ClientTimestamp);
            packer.Write((byte)value.Action);
            packer.Write(value.ShieldHash);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkBlockRequest value)
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

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkBlockResponse value)
        {
            packer.Write(value.RequestId);
            packer.Write(value.ActorNetworkId);
            packer.Write(value.CorrelationId);
            packer.Write(value.Validated);
            packer.Write((byte)value.RejectionReason);
            packer.Write(value.ServerBlockStartTime);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkBlockResponse value)
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

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkBlockBroadcast value)
        {
            packer.Write(value.CharacterNetworkId);
            packer.Write((byte)value.Action);
            packer.Write(value.ServerTimestamp);
            packer.Write(value.ShieldHash);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkBlockBroadcast value)
        {
            byte action = 0;
            packer.Read(ref value.CharacterNetworkId);
            packer.Read(ref action);
            packer.Read(ref value.ServerTimestamp);
            packer.Read(ref value.ShieldHash);
            value.Action = (NetworkBlockAction)action;
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkSkillRequest value)
        {
            packer.Write(value.RequestId);
            packer.Write(value.ActorNetworkId);
            packer.Write(value.CorrelationId);
            packer.Write(value.TargetNetworkId);
            packer.Write(value.SkillHash);
            packer.Write(value.WeaponHash);
            packer.Write(value.ComboNodeId);
            packer.Write(value.InputKey);
            packer.Write(value.IsChargeRelease);
            packer.Write(value.ChargeDuration);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkSkillRequest value)
        {
            packer.Read(ref value.RequestId);
            packer.Read(ref value.ActorNetworkId);
            packer.Read(ref value.CorrelationId);
            packer.Read(ref value.TargetNetworkId);
            packer.Read(ref value.SkillHash);
            packer.Read(ref value.WeaponHash);
            packer.Read(ref value.ComboNodeId);
            packer.Read(ref value.InputKey);
            packer.Read(ref value.IsChargeRelease);
            packer.Read(ref value.ChargeDuration);
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkSkillResponse value)
        {
            packer.Write(value.RequestId);
            packer.Write(value.ActorNetworkId);
            packer.Write(value.CorrelationId);
            packer.Write(value.Validated);
            packer.Write((byte)value.RejectionReason);
            packer.Write(value.ComboNodeId);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkSkillResponse value)
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

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkSkillBroadcast value)
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

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkSkillBroadcast value)
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

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkChargeRequest value)
        {
            packer.Write(value.RequestId);
            packer.Write(value.ActorNetworkId);
            packer.Write(value.CorrelationId);
            packer.Write(value.ClientTimestamp);
            packer.Write(value.InputKey);
            packer.Write(value.WeaponHash);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkChargeRequest value)
        {
            packer.Read(ref value.RequestId);
            packer.Read(ref value.ActorNetworkId);
            packer.Read(ref value.CorrelationId);
            packer.Read(ref value.ClientTimestamp);
            packer.Read(ref value.InputKey);
            packer.Read(ref value.WeaponHash);
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkChargeResponse value)
        {
            packer.Write(value.RequestId);
            packer.Write(value.ActorNetworkId);
            packer.Write(value.CorrelationId);
            packer.Write(value.Validated);
            packer.Write(value.ServerChargeStartTime);
            packer.Write(value.ChargeSkillHash);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkChargeResponse value)
        {
            packer.Read(ref value.RequestId);
            packer.Read(ref value.ActorNetworkId);
            packer.Read(ref value.CorrelationId);
            packer.Read(ref value.Validated);
            packer.Read(ref value.ServerChargeStartTime);
            packer.Read(ref value.ChargeSkillHash);
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkChargeBroadcast value)
        {
            packer.Write(value.CharacterNetworkId);
            packer.Write(value.ChargeStarted);
            packer.Write(value.ChargeSkillHash);
            packer.Write(value.ServerTimestamp);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkChargeBroadcast value)
        {
            packer.Read(ref value.CharacterNetworkId);
            packer.Read(ref value.ChargeStarted);
            packer.Read(ref value.ChargeSkillHash);
            packer.Read(ref value.ServerTimestamp);
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkReactionBroadcast value)
        {
            packer.Write(value.CharacterNetworkId);
            packer.Write(value.FromNetworkId);
            packer.Write(value.ReactionHash);
            packer.Write(value.Direction);
            packer.Write(value.DirectionY);
            packer.Write(value.Power);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkReactionBroadcast value)
        {
            packer.Read(ref value.CharacterNetworkId);
            packer.Read(ref value.FromNetworkId);
            packer.Read(ref value.ReactionHash);
            packer.Read(ref value.Direction);
            packer.Read(ref value.DirectionY);
            packer.Read(ref value.Power);
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkMeleeWeaponState value)
        {
            packer.Write(value.WeaponHash);
            packer.Write(value.ShieldFlags);
            packer.Write(value.BlockTiming);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkMeleeWeaponState value)
        {
            packer.Read(ref value.WeaponHash);
            packer.Read(ref value.ShieldFlags);
            packer.Read(ref value.BlockTiming);
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
    }
}
#endif
