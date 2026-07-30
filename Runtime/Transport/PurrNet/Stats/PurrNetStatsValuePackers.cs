#if GC2_STATS
using Arawn.GameCreator2.Networking.Stats;
using JetBrains.Annotations;
using PurrNet.Modules;
using PurrNet.Packing;
using PurrNet.Utils;

namespace Arawn.GameCreator2.Networking.Stats.Transport.PurrNet
{
    [UsedImplicitly]
    public static class PurrNetStatsValuePackers
    {
        [RegisterPackers]
        static void Register()
        {
            Hasher.PrepareType(typeof(NetworkStatModifyRequest));
            Hasher.PrepareType(typeof(NetworkStatModifyResponse));
            Hasher.PrepareType(typeof(NetworkAttributeModifyRequest));
            Hasher.PrepareType(typeof(NetworkAttributeModifyResponse));
            Hasher.PrepareType(typeof(NetworkStatusEffectRequest));
            Hasher.PrepareType(typeof(NetworkStatusEffectResponse));
            Hasher.PrepareType(typeof(NetworkStatModifierRequest));
            Hasher.PrepareType(typeof(NetworkStatModifierResponse));
            Hasher.PrepareType(typeof(NetworkClearStatusEffectsRequest));
            Hasher.PrepareType(typeof(NetworkClearStatusEffectsResponse));
            Hasher.PrepareType(typeof(NetworkStatChangeBroadcast));
            Hasher.PrepareType(typeof(NetworkAttributeChangeBroadcast));
            Hasher.PrepareType(typeof(NetworkStatusEffectBroadcast));
            Hasher.PrepareType(typeof(NetworkStatModifierBroadcast));
            Hasher.PrepareType(typeof(NetworkStatsSnapshot));
            Hasher.PrepareType(typeof(NetworkStatsDelta));
            Hasher.PrepareType(typeof(NetworkStatValue));
            Hasher.PrepareType(typeof(NetworkAttributeValue));
            Hasher.PrepareType(typeof(NetworkStatusEffectValue));
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkStatModifyRequest value)
        {
            packer.Write(value.RequestId);
            packer.Write(value.ActorNetworkId);
            packer.Write(value.CorrelationId);
            packer.Write(value.TargetNetworkId);
            packer.Write(value.StatHash);
            packer.Write((byte)value.ModificationType);
            packer.Write(value.Value);
            packer.Write((byte)value.Source);
            packer.Write(value.SourceHash);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkStatModifyRequest value)
        {
            byte modificationType = 0;
            byte source = 0;
            packer.Read(ref value.RequestId);
            packer.Read(ref value.ActorNetworkId);
            packer.Read(ref value.CorrelationId);
            packer.Read(ref value.TargetNetworkId);
            packer.Read(ref value.StatHash);
            packer.Read(ref modificationType);
            packer.Read(ref value.Value);
            packer.Read(ref source);
            packer.Read(ref value.SourceHash);
            value.ModificationType = (StatModificationType)modificationType;
            value.Source = (StatModificationSource)source;
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkStatModifyResponse value)
        {
            packer.Write(value.RequestId);
            packer.Write(value.ActorNetworkId);
            packer.Write(value.CorrelationId);
            packer.Write(value.Authorized);
            packer.Write((byte)value.RejectionReason);
            packer.Write(value.NewValue);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkStatModifyResponse value)
        {
            byte reason = 0;
            packer.Read(ref value.RequestId);
            packer.Read(ref value.ActorNetworkId);
            packer.Read(ref value.CorrelationId);
            packer.Read(ref value.Authorized);
            packer.Read(ref reason);
            packer.Read(ref value.NewValue);
            value.RejectionReason = (StatRejectionReason)reason;
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkAttributeModifyRequest value)
        {
            packer.Write(value.RequestId);
            packer.Write(value.ActorNetworkId);
            packer.Write(value.CorrelationId);
            packer.Write(value.TargetNetworkId);
            packer.Write(value.AttributeHash);
            packer.Write((byte)value.ModificationType);
            packer.Write(value.Value);
            packer.Write((byte)value.Source);
            packer.Write(value.SourceHash);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkAttributeModifyRequest value)
        {
            byte modificationType = 0;
            byte source = 0;
            packer.Read(ref value.RequestId);
            packer.Read(ref value.ActorNetworkId);
            packer.Read(ref value.CorrelationId);
            packer.Read(ref value.TargetNetworkId);
            packer.Read(ref value.AttributeHash);
            packer.Read(ref modificationType);
            packer.Read(ref value.Value);
            packer.Read(ref source);
            packer.Read(ref value.SourceHash);
            value.ModificationType = (AttributeModificationType)modificationType;
            value.Source = (StatModificationSource)source;
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkAttributeModifyResponse value)
        {
            packer.Write(value.RequestId);
            packer.Write(value.ActorNetworkId);
            packer.Write(value.CorrelationId);
            packer.Write(value.Authorized);
            packer.Write((byte)value.RejectionReason);
            packer.Write(value.NewValue);
            packer.Write(value.MaxValue);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkAttributeModifyResponse value)
        {
            byte reason = 0;
            packer.Read(ref value.RequestId);
            packer.Read(ref value.ActorNetworkId);
            packer.Read(ref value.CorrelationId);
            packer.Read(ref value.Authorized);
            packer.Read(ref reason);
            packer.Read(ref value.NewValue);
            packer.Read(ref value.MaxValue);
            value.RejectionReason = (StatRejectionReason)reason;
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkStatusEffectRequest value)
        {
            packer.Write(value.RequestId);
            packer.Write(value.ActorNetworkId);
            packer.Write(value.CorrelationId);
            packer.Write(value.TargetNetworkId);
            packer.Write(value.StatusEffectHash);
            packer.Write((byte)value.Action);
            packer.Write(value.Amount);
            packer.Write((byte)value.Source);
            packer.Write(value.SourceHash);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkStatusEffectRequest value)
        {
            byte action = 0;
            byte source = 0;
            packer.Read(ref value.RequestId);
            packer.Read(ref value.ActorNetworkId);
            packer.Read(ref value.CorrelationId);
            packer.Read(ref value.TargetNetworkId);
            packer.Read(ref value.StatusEffectHash);
            packer.Read(ref action);
            packer.Read(ref value.Amount);
            packer.Read(ref source);
            packer.Read(ref value.SourceHash);
            value.Action = (StatusEffectAction)action;
            value.Source = (StatModificationSource)source;
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkStatusEffectResponse value)
        {
            packer.Write(value.RequestId);
            packer.Write(value.ActorNetworkId);
            packer.Write(value.CorrelationId);
            packer.Write(value.Authorized);
            packer.Write((byte)value.RejectionReason);
            packer.Write(value.CurrentStackCount);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkStatusEffectResponse value)
        {
            byte reason = 0;
            packer.Read(ref value.RequestId);
            packer.Read(ref value.ActorNetworkId);
            packer.Read(ref value.CorrelationId);
            packer.Read(ref value.Authorized);
            packer.Read(ref reason);
            packer.Read(ref value.CurrentStackCount);
            value.RejectionReason = (StatRejectionReason)reason;
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkStatModifierRequest value)
        {
            packer.Write(value.RequestId);
            packer.Write(value.ActorNetworkId);
            packer.Write(value.CorrelationId);
            packer.Write(value.TargetNetworkId);
            packer.Write(value.StatHash);
            packer.Write((byte)value.Action);
            packer.Write((byte)value.ModifierType);
            packer.Write(value.Value);
            packer.Write((byte)value.Source);
            packer.Write(value.SourceHash);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkStatModifierRequest value)
        {
            byte action = 0;
            byte modifierType = 0;
            byte source = 0;
            packer.Read(ref value.RequestId);
            packer.Read(ref value.ActorNetworkId);
            packer.Read(ref value.CorrelationId);
            packer.Read(ref value.TargetNetworkId);
            packer.Read(ref value.StatHash);
            packer.Read(ref action);
            packer.Read(ref modifierType);
            packer.Read(ref value.Value);
            packer.Read(ref source);
            packer.Read(ref value.SourceHash);
            value.Action = (ModifierAction)action;
            value.ModifierType = (NetworkModifierType)modifierType;
            value.Source = (StatModificationSource)source;
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkStatModifierResponse value)
        {
            packer.Write(value.RequestId);
            packer.Write(value.ActorNetworkId);
            packer.Write(value.CorrelationId);
            packer.Write(value.Authorized);
            packer.Write((byte)value.RejectionReason);
            packer.Write(value.NewStatValue);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkStatModifierResponse value)
        {
            byte reason = 0;
            packer.Read(ref value.RequestId);
            packer.Read(ref value.ActorNetworkId);
            packer.Read(ref value.CorrelationId);
            packer.Read(ref value.Authorized);
            packer.Read(ref reason);
            packer.Read(ref value.NewStatValue);
            value.RejectionReason = (StatRejectionReason)reason;
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkClearStatusEffectsRequest value)
        {
            packer.Write(value.RequestId);
            packer.Write(value.ActorNetworkId);
            packer.Write(value.CorrelationId);
            packer.Write(value.TargetNetworkId);
            packer.Write(value.TypeMask);
            packer.Write((byte)value.Source);
            packer.Write(value.SourceHash);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkClearStatusEffectsRequest value)
        {
            byte source = 0;
            packer.Read(ref value.RequestId);
            packer.Read(ref value.ActorNetworkId);
            packer.Read(ref value.CorrelationId);
            packer.Read(ref value.TargetNetworkId);
            packer.Read(ref value.TypeMask);
            packer.Read(ref source);
            packer.Read(ref value.SourceHash);
            value.Source = (StatModificationSource)source;
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkClearStatusEffectsResponse value)
        {
            packer.Write(value.RequestId);
            packer.Write(value.ActorNetworkId);
            packer.Write(value.CorrelationId);
            packer.Write(value.Authorized);
            packer.Write((byte)value.RejectionReason);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkClearStatusEffectsResponse value)
        {
            byte reason = 0;
            packer.Read(ref value.RequestId);
            packer.Read(ref value.ActorNetworkId);
            packer.Read(ref value.CorrelationId);
            packer.Read(ref value.Authorized);
            packer.Read(ref reason);
            value.RejectionReason = (StatRejectionReason)reason;
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkStatChangeBroadcast value)
        {
            packer.Write(value.NetworkId);
            packer.Write(value.StatHash);
            packer.Write(value.NewBaseValue);
            packer.Write(value.NewComputedValue);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkStatChangeBroadcast value)
        {
            packer.Read(ref value.NetworkId);
            packer.Read(ref value.StatHash);
            packer.Read(ref value.NewBaseValue);
            packer.Read(ref value.NewComputedValue);
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkAttributeChangeBroadcast value)
        {
            packer.Write(value.NetworkId);
            packer.Write(value.AttributeHash);
            packer.Write(value.NewValue);
            packer.Write(value.MaxValue);
            packer.Write(value.Change);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkAttributeChangeBroadcast value)
        {
            packer.Read(ref value.NetworkId);
            packer.Read(ref value.AttributeHash);
            packer.Read(ref value.NewValue);
            packer.Read(ref value.MaxValue);
            packer.Read(ref value.Change);
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkStatusEffectBroadcast value)
        {
            packer.Write(value.NetworkId);
            packer.Write(value.StatusEffectHash);
            packer.Write((byte)value.Action);
            packer.Write(value.StackCount);
            packer.Write(value.RemainingDuration);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkStatusEffectBroadcast value)
        {
            byte action = 0;
            packer.Read(ref value.NetworkId);
            packer.Read(ref value.StatusEffectHash);
            packer.Read(ref action);
            packer.Read(ref value.StackCount);
            packer.Read(ref value.RemainingDuration);
            value.Action = (StatusEffectAction)action;
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkStatModifierBroadcast value)
        {
            packer.Write(value.NetworkId);
            packer.Write(value.StatHash);
            packer.Write((byte)value.Action);
            packer.Write((byte)value.ModifierType);
            packer.Write(value.Value);
            packer.Write(value.NewStatValue);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkStatModifierBroadcast value)
        {
            byte action = 0;
            byte modifierType = 0;
            packer.Read(ref value.NetworkId);
            packer.Read(ref value.StatHash);
            packer.Read(ref action);
            packer.Read(ref modifierType);
            packer.Read(ref value.Value);
            packer.Read(ref value.NewStatValue);
            value.Action = (ModifierAction)action;
            value.ModifierType = (NetworkModifierType)modifierType;
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkStatsSnapshot value)
        {
            packer.Write(value.NetworkId);
            packer.Write(value.Timestamp);
            packer.WriteList(value.Stats);
            packer.WriteList(value.Attributes);
            packer.WriteList(value.StatusEffects);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkStatsSnapshot value)
        {
            packer.Read(ref value.NetworkId);
            packer.Read(ref value.Timestamp);
            packer.ReadArray(ref value.Stats);
            packer.ReadArray(ref value.Attributes);
            packer.ReadArray(ref value.StatusEffects);
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkStatValue value)
        {
            packer.Write(value.StatHash);
            packer.Write(value.BaseValue);
            packer.Write(value.ComputedValue);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkStatValue value)
        {
            packer.Read(ref value.StatHash);
            packer.Read(ref value.BaseValue);
            packer.Read(ref value.ComputedValue);
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkAttributeValue value)
        {
            packer.Write(value.AttributeHash);
            packer.Write(value.CurrentValue);
            packer.Write(value.MaxValue);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkAttributeValue value)
        {
            packer.Read(ref value.AttributeHash);
            packer.Read(ref value.CurrentValue);
            packer.Read(ref value.MaxValue);
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkStatusEffectValue value)
        {
            packer.Write(value.StatusEffectHash);
            packer.Write(value.StackCount);
            packer.Write(value.RemainingDuration);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkStatusEffectValue value)
        {
            packer.Read(ref value.StatusEffectHash);
            packer.Read(ref value.StackCount);
            packer.Read(ref value.RemainingDuration);
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkStatsDelta value)
        {
            packer.Write(value.NetworkId);
            packer.Write(value.Timestamp);
            packer.Write(value.StatChangeMask);
            packer.Write(value.AttributeChangeMask);
            packer.WriteList(value.ChangedStats);
            packer.WriteList(value.ChangedAttributes);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkStatsDelta value)
        {
            packer.Read(ref value.NetworkId);
            packer.Read(ref value.Timestamp);
            packer.Read(ref value.StatChangeMask);
            packer.Read(ref value.AttributeChangeMask);
            packer.ReadArray(ref value.ChangedStats);
            packer.ReadArray(ref value.ChangedAttributes);
        }
    }
}
#endif
