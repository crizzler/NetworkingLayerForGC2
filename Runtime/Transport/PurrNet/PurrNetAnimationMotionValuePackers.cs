using JetBrains.Annotations;
using PurrNet.Modules;
using PurrNet.Packing;
using PurrNet.Utils;

namespace Arawn.GameCreator2.Networking.Transport.PurrNet
{
    [UsedImplicitly]
    public static class PurrNetAnimationMotionValuePackers
    {
        [RegisterPackers]
        static void Register()
        {
            Hasher.PrepareType(typeof(NetworkAnimationStateCommandMessage));
            Hasher.PrepareType(typeof(NetworkAnimationGestureCommandMessage));
            Hasher.PrepareType(typeof(NetworkAnimationStopStateCommandMessage));
            Hasher.PrepareType(typeof(NetworkAnimationStopGestureCommandMessage));
            Hasher.PrepareType(typeof(NetworkStateCommand));
            Hasher.PrepareType(typeof(NetworkGestureCommand));
            Hasher.PrepareType(typeof(NetworkStopStateCommand));
            Hasher.PrepareType(typeof(NetworkStopGestureCommand));
            Hasher.PrepareType(typeof(NetworkMotionCommandMessage));
            Hasher.PrepareType(typeof(NetworkMotionResultMessage));
            Hasher.PrepareType(typeof(NetworkMotionCommand));
            Hasher.PrepareType(typeof(NetworkMotionResult));
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkAnimationStateCommandMessage value)
        {
            packer.Write(value.CharacterNetworkId);
            packer.Write(value.Command);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkAnimationStateCommandMessage value)
        {
            packer.Read(ref value.CharacterNetworkId);
            packer.Read(ref value.Command);
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkAnimationGestureCommandMessage value)
        {
            packer.Write(value.CharacterNetworkId);
            packer.Write(value.Command);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkAnimationGestureCommandMessage value)
        {
            packer.Read(ref value.CharacterNetworkId);
            packer.Read(ref value.Command);
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkAnimationStopStateCommandMessage value)
        {
            packer.Write(value.CharacterNetworkId);
            packer.Write(value.Command);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkAnimationStopStateCommandMessage value)
        {
            packer.Read(ref value.CharacterNetworkId);
            packer.Read(ref value.Command);
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkAnimationStopGestureCommandMessage value)
        {
            packer.Write(value.CharacterNetworkId);
            packer.Write(value.Command);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkAnimationStopGestureCommandMessage value)
        {
            packer.Read(ref value.CharacterNetworkId);
            packer.Read(ref value.Command);
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkStateCommand value)
        {
            packer.Write(value.Flags);
            packer.Write(value.Layer);
            packer.Write(value.DelayIn);
            packer.Write(value.Speed);
            packer.Write(value.Weight);
            packer.Write(value.TransitionIn);
            packer.Write(value.TransitionOut);
            packer.Write(value.Duration);
            packer.Write(value.AnimationId);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkStateCommand value)
        {
            packer.Read(ref value.Flags);
            packer.Read(ref value.Layer);
            packer.Read(ref value.DelayIn);
            packer.Read(ref value.Speed);
            packer.Read(ref value.Weight);
            packer.Read(ref value.TransitionIn);
            packer.Read(ref value.TransitionOut);
            packer.Read(ref value.Duration);
            packer.Read(ref value.AnimationId);
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkGestureCommand value)
        {
            packer.Write(value.Flags);
            packer.Write(value.DelayIn);
            packer.Write(value.Duration);
            packer.Write(value.Speed);
            packer.Write(value.TransitionIn);
            packer.Write(value.TransitionOut);
            packer.Write(value.ClipHash);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkGestureCommand value)
        {
            packer.Read(ref value.Flags);
            packer.Read(ref value.DelayIn);
            packer.Read(ref value.Duration);
            packer.Read(ref value.Speed);
            packer.Read(ref value.TransitionIn);
            packer.Read(ref value.TransitionOut);
            packer.Read(ref value.ClipHash);
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkStopStateCommand value)
        {
            packer.Write(value.Layer);
            packer.Write(value.Delay);
            packer.Write(value.TransitionOut);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkStopStateCommand value)
        {
            packer.Read(ref value.Layer);
            packer.Read(ref value.Delay);
            packer.Read(ref value.TransitionOut);
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkStopGestureCommand value)
        {
            packer.Write(value.ClipHash);
            packer.Write(value.Delay);
            packer.Write(value.TransitionOut);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkStopGestureCommand value)
        {
            packer.Read(ref value.ClipHash);
            packer.Read(ref value.Delay);
            packer.Read(ref value.TransitionOut);
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkMotionCommandMessage value)
        {
            packer.Write(value.CharacterNetworkId);
            packer.Write(value.Command);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkMotionCommandMessage value)
        {
            packer.Read(ref value.CharacterNetworkId);
            packer.Read(ref value.Command);
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkMotionResultMessage value)
        {
            packer.Write(value.CharacterNetworkId);
            packer.Write(value.Result);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkMotionResultMessage value)
        {
            packer.Read(ref value.CharacterNetworkId);
            packer.Read(ref value.Result);
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkMotionCommand value)
        {
            packer.Write((byte)value.commandType);
            packer.Write(value.priority);
            packer.Write(value.sequenceNumber);
            packer.Write(value.dataX);
            packer.Write(value.dataY);
            packer.Write(value.dataZ);
            packer.Write(value.param1);
            packer.Write(value.param2);
            packer.Write(value.param3);
            packer.Write(value.targetNetworkId);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkMotionCommand value)
        {
            byte type = 0;
            packer.Read(ref type);
            packer.Read(ref value.priority);
            packer.Read(ref value.sequenceNumber);
            packer.Read(ref value.dataX);
            packer.Read(ref value.dataY);
            packer.Read(ref value.dataZ);
            packer.Read(ref value.param1);
            packer.Read(ref value.param2);
            packer.Read(ref value.param3);
            packer.Read(ref value.targetNetworkId);
            value.commandType = (NetworkMotionCommandType)type;
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkMotionResult value)
        {
            packer.Write(value.commandSequence);
            packer.Write(value.approved);
            packer.Write(value.rejectionReason);
            packer.Write(value.correctedX);
            packer.Write(value.correctedY);
            packer.Write(value.correctedZ);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkMotionResult value)
        {
            packer.Read(ref value.commandSequence);
            packer.Read(ref value.approved);
            packer.Read(ref value.rejectionReason);
            packer.Read(ref value.correctedX);
            packer.Read(ref value.correctedY);
            packer.Read(ref value.correctedZ);
        }
    }
}
