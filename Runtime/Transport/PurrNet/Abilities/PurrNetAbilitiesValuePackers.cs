#if GC2_ABILITIES
using Arawn.GameCreator2.Networking;
using JetBrains.Annotations;
using PurrNet.Modules;
using PurrNet.Packing;
using PurrNet.Utils;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Abilities.Transport.PurrNet
{
    [UsedImplicitly]
    public static class PurrNetAbilitiesValuePackers
    {
        [RegisterPackers]
        static void Register()
        {
            Hasher.PrepareType(typeof(NetworkAbilityCastRequest));
            Hasher.PrepareType(typeof(NetworkAbilityCastResponse));
            Hasher.PrepareType(typeof(NetworkAbilityCastBroadcast));
            Hasher.PrepareType(typeof(NetworkAbilityEffectBroadcast));
            Hasher.PrepareType(typeof(NetworkProjectileSpawnBroadcast));
            Hasher.PrepareType(typeof(NetworkProjectileEventBroadcast));
            Hasher.PrepareType(typeof(NetworkImpactSpawnBroadcast));
            Hasher.PrepareType(typeof(NetworkImpactHitBroadcast));
            Hasher.PrepareType(typeof(NetworkCooldownRequest));
            Hasher.PrepareType(typeof(NetworkCooldownResponse));
            Hasher.PrepareType(typeof(NetworkCooldownBroadcast));
            Hasher.PrepareType(typeof(NetworkAbilityLearnRequest));
            Hasher.PrepareType(typeof(NetworkAbilityLearnResponse));
            Hasher.PrepareType(typeof(NetworkAbilityLearnBroadcast));
            Hasher.PrepareType(typeof(NetworkCastCancelRequest));
            Hasher.PrepareType(typeof(NetworkCastCancelResponse));
            Hasher.PrepareType(typeof(NetworkAbilityStateRequest));
            Hasher.PrepareType(typeof(NetworkAbilityStateResponse));
            Hasher.PrepareType(typeof(NetworkAbilitySlotEntry));
            Hasher.PrepareType(typeof(NetworkCooldownEntry));
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkAbilityCastRequest value)
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

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkAbilityCastRequest value)
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

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkAbilityCastResponse value)
        {
            packer.Write(value.RequestId);
            packer.Write(value.ActorNetworkId);
            packer.Write(value.CorrelationId);
            packer.Write(value.CastInstanceId);
            packer.Write(value.Approved);
            packer.Write((byte)value.RejectReason);
            packer.Write(value.CooldownEndTime);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkAbilityCastResponse value)
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

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkAbilityCastBroadcast value)
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

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkAbilityCastBroadcast value)
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

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkAbilityEffectBroadcast value)
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

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkAbilityEffectBroadcast value)
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

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkProjectileSpawnBroadcast value)
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

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkProjectileSpawnBroadcast value)
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

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkProjectileEventBroadcast value)
        {
            packer.Write(value.ProjectileId);
            packer.Write((byte)value.EventType);
            WriteVector3(packer, value.Position);
            packer.Write(value.HitTargetNetworkId);
            packer.Write(value.ServerTime);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkProjectileEventBroadcast value)
        {
            byte eventType = 0;

            packer.Read(ref value.ProjectileId);
            packer.Read(ref eventType);
            ReadVector3(packer, ref value.Position);
            packer.Read(ref value.HitTargetNetworkId);
            packer.Read(ref value.ServerTime);

            value.EventType = (ProjectileEventType)eventType;
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkImpactSpawnBroadcast value)
        {
            packer.Write(value.ImpactId);
            packer.Write(value.CastInstanceId);
            packer.Write(value.ImpactHash);
            WriteVector3(packer, value.Position);
            WriteQuaternion(packer, value.Rotation);
            packer.Write(value.ServerTime);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkImpactSpawnBroadcast value)
        {
            packer.Read(ref value.ImpactId);
            packer.Read(ref value.CastInstanceId);
            packer.Read(ref value.ImpactHash);
            ReadVector3(packer, ref value.Position);
            ReadQuaternion(packer, ref value.Rotation);
            packer.Read(ref value.ServerTime);
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkImpactHitBroadcast value)
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

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkImpactHitBroadcast value)
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

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkCooldownRequest value)
        {
            packer.Write(value.RequestId);
            packer.Write(value.ActorNetworkId);
            packer.Write(value.CorrelationId);
            packer.Write(value.CasterNetworkId);
            packer.Write(value.AbilityIdHash);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkCooldownRequest value)
        {
            packer.Read(ref value.RequestId);
            packer.Read(ref value.ActorNetworkId);
            packer.Read(ref value.CorrelationId);
            packer.Read(ref value.CasterNetworkId);
            packer.Read(ref value.AbilityIdHash);
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkCooldownResponse value)
        {
            packer.Write(value.RequestId);
            packer.Write(value.ActorNetworkId);
            packer.Write(value.CorrelationId);
            packer.Write(value.IsOnCooldown);
            packer.Write(value.CooldownEndTime);
            packer.Write(value.TotalDuration);
            packer.Write(value.TimedOut);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkCooldownResponse value)
        {
            packer.Read(ref value.RequestId);
            packer.Read(ref value.ActorNetworkId);
            packer.Read(ref value.CorrelationId);
            packer.Read(ref value.IsOnCooldown);
            packer.Read(ref value.CooldownEndTime);
            packer.Read(ref value.TotalDuration);
            packer.Read(ref value.TimedOut);
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkCooldownBroadcast value)
        {
            packer.Write(value.CharacterNetworkId);
            packer.Write(value.AbilityIdHash);
            packer.Write(value.CooldownEndTime);
            packer.Write(value.TotalDuration);
            packer.Write((byte)value.Reason);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkCooldownBroadcast value)
        {
            byte reason = 0;

            packer.Read(ref value.CharacterNetworkId);
            packer.Read(ref value.AbilityIdHash);
            packer.Read(ref value.CooldownEndTime);
            packer.Read(ref value.TotalDuration);
            packer.Read(ref reason);

            value.Reason = (CooldownChangeReason)reason;
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkAbilityLearnRequest value)
        {
            packer.Write(value.RequestId);
            packer.Write(value.ActorNetworkId);
            packer.Write(value.CorrelationId);
            packer.Write(value.CharacterNetworkId);
            packer.Write(value.AbilityIdHash);
            packer.Write(unchecked((byte)value.Slot));
            packer.Write(value.IsLearning);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkAbilityLearnRequest value)
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

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkAbilityLearnResponse value)
        {
            packer.Write(value.RequestId);
            packer.Write(value.ActorNetworkId);
            packer.Write(value.CorrelationId);
            packer.Write(value.Approved);
            packer.Write((byte)value.RejectReason);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkAbilityLearnResponse value)
        {
            byte reason = 0;

            packer.Read(ref value.RequestId);
            packer.Read(ref value.ActorNetworkId);
            packer.Read(ref value.CorrelationId);
            packer.Read(ref value.Approved);
            packer.Read(ref reason);

            value.RejectReason = (AbilityLearnRejectReason)reason;
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkAbilityLearnBroadcast value)
        {
            packer.Write(value.CharacterNetworkId);
            packer.Write(value.AbilityIdHash);
            packer.Write(unchecked((byte)value.Slot));
            packer.Write(value.IsLearned);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkAbilityLearnBroadcast value)
        {
            byte slot = 0;

            packer.Read(ref value.CharacterNetworkId);
            packer.Read(ref value.AbilityIdHash);
            packer.Read(ref slot);
            packer.Read(ref value.IsLearned);

            value.Slot = unchecked((sbyte)slot);
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkCastCancelRequest value)
        {
            packer.Write(value.RequestId);
            packer.Write(value.ActorNetworkId);
            packer.Write(value.CorrelationId);
            packer.Write(value.CasterNetworkId);
            packer.Write(value.CastInstanceId);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkCastCancelRequest value)
        {
            packer.Read(ref value.RequestId);
            packer.Read(ref value.ActorNetworkId);
            packer.Read(ref value.CorrelationId);
            packer.Read(ref value.CasterNetworkId);
            packer.Read(ref value.CastInstanceId);
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkCastCancelResponse value)
        {
            packer.Write(value.RequestId);
            packer.Write(value.ActorNetworkId);
            packer.Write(value.CorrelationId);
            packer.Write(value.Approved);
            packer.Write(value.CastInstanceId);
            packer.Write(value.TimedOut);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkCastCancelResponse value)
        {
            packer.Read(ref value.RequestId);
            packer.Read(ref value.ActorNetworkId);
            packer.Read(ref value.CorrelationId);
            packer.Read(ref value.Approved);
            packer.Read(ref value.CastInstanceId);
            packer.Read(ref value.TimedOut);
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkAbilityStateRequest value)
        {
            packer.Write(value.RequestId);
            packer.Write(value.ActorNetworkId);
            packer.Write(value.CorrelationId);
            packer.Write(value.CharacterNetworkId);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkAbilityStateRequest value)
        {
            packer.Read(ref value.RequestId);
            packer.Read(ref value.ActorNetworkId);
            packer.Read(ref value.CorrelationId);
            packer.Read(ref value.CharacterNetworkId);
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkAbilityStateResponse value)
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

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkAbilityStateResponse value)
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

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkAbilitySlotEntry value)
        {
            packer.Write(value.SlotIndex);
            packer.Write(value.AbilityHash);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkAbilitySlotEntry value)
        {
            packer.Read(ref value.SlotIndex);
            packer.Read(ref value.AbilityHash);
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkCooldownEntry value)
        {
            packer.Write(value.AbilityHash);
            packer.Write(value.EndTime);
            packer.Write(value.TotalDuration);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkCooldownEntry value)
        {
            packer.Read(ref value.AbilityHash);
            packer.Read(ref value.EndTime);
            packer.Read(ref value.TotalDuration);
        }

        private static void WriteTargets8(
            BitPacker packer,
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
            BitPacker packer,
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
