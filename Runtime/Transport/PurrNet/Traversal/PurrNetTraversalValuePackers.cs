#if GC2_TRAVERSAL
using Arawn.GameCreator2.Networking.Traversal;
using JetBrains.Annotations;
using PurrNet.Modules;
using PurrNet.Packing;
using PurrNet.Utils;

namespace Arawn.GameCreator2.Networking.Traversal.Transport.PurrNet
{
    [UsedImplicitly]
    public static class PurrNetTraversalValuePackers
    {
        [RegisterPackers]
        static void Register()
        {
            Hasher.PrepareType(typeof(NetworkTraversalRequest));
            Hasher.PrepareType(typeof(NetworkTraversalResponse));
            Hasher.PrepareType(typeof(NetworkTraversalBroadcast));
            Hasher.PrepareType(typeof(NetworkTraversalSnapshot));
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkTraversalRequest value)
        {
            packer.Write(value.RequestId);
            packer.Write(value.ActorNetworkId);
            packer.Write(value.CorrelationId);
            packer.Write(value.TargetNetworkId);
            packer.Write((byte)value.Action);
            packer.Write(value.TraverseHash);
            packer.Write(value.TraverseIdString);
            packer.Write(value.ActionIdHash);
            packer.Write(value.ActionIdString);
            packer.Write(value.StateIdHash);
            packer.Write(value.StateIdString);
            packer.Write(value.ArgsSelfNetworkId);
            packer.Write(value.ArgsTargetNetworkId);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkTraversalRequest value)
        {
            byte action = 0;

            packer.Read(ref value.RequestId);
            packer.Read(ref value.ActorNetworkId);
            packer.Read(ref value.CorrelationId);
            packer.Read(ref value.TargetNetworkId);
            packer.Read(ref action);
            packer.Read(ref value.TraverseHash);
            packer.Read(ref value.TraverseIdString);
            packer.Read(ref value.ActionIdHash);
            packer.Read(ref value.ActionIdString);
            packer.Read(ref value.StateIdHash);
            packer.Read(ref value.StateIdString);
            packer.Read(ref value.ArgsSelfNetworkId);
            packer.Read(ref value.ArgsTargetNetworkId);

            value.Action = (TraversalActionType)action;
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkTraversalResponse value)
        {
            packer.Write(value.RequestId);
            packer.Write(value.ActorNetworkId);
            packer.Write(value.CorrelationId);
            packer.Write((byte)value.Action);
            packer.Write(value.Authorized);
            packer.Write(value.Applied);
            packer.Write((byte)value.RejectionReason);
            packer.Write(value.TraverseHash);
            packer.Write(value.TraverseIdString);
            packer.Write(value.ActionIdHash);
            packer.Write(value.ActionIdString);
            packer.Write(value.StateIdHash);
            packer.Write(value.StateIdString);
            packer.Write(value.ArgsSelfNetworkId);
            packer.Write(value.ArgsTargetNetworkId);
            packer.Write(value.IsTraversing);
            packer.Write(value.Error);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkTraversalResponse value)
        {
            byte action = 0;
            byte rejection = 0;

            packer.Read(ref value.RequestId);
            packer.Read(ref value.ActorNetworkId);
            packer.Read(ref value.CorrelationId);
            packer.Read(ref action);
            packer.Read(ref value.Authorized);
            packer.Read(ref value.Applied);
            packer.Read(ref rejection);
            packer.Read(ref value.TraverseHash);
            packer.Read(ref value.TraverseIdString);
            packer.Read(ref value.ActionIdHash);
            packer.Read(ref value.ActionIdString);
            packer.Read(ref value.StateIdHash);
            packer.Read(ref value.StateIdString);
            packer.Read(ref value.ArgsSelfNetworkId);
            packer.Read(ref value.ArgsTargetNetworkId);
            packer.Read(ref value.IsTraversing);
            packer.Read(ref value.Error);

            value.Action = (TraversalActionType)action;
            value.RejectionReason = (TraversalRejectionReason)rejection;
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkTraversalBroadcast value)
        {
            packer.Write(value.NetworkId);
            packer.Write(value.ActorNetworkId);
            packer.Write(value.CorrelationId);
            packer.Write((byte)value.Action);
            packer.Write(value.TraverseHash);
            packer.Write(value.TraverseIdString);
            packer.Write(value.ActionIdHash);
            packer.Write(value.ActionIdString);
            packer.Write(value.StateIdHash);
            packer.Write(value.StateIdString);
            packer.Write(value.ArgsSelfNetworkId);
            packer.Write(value.ArgsTargetNetworkId);
            packer.Write(value.IsTraversing);
            packer.Write(value.ServerTime);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkTraversalBroadcast value)
        {
            byte action = 0;

            packer.Read(ref value.NetworkId);
            packer.Read(ref value.ActorNetworkId);
            packer.Read(ref value.CorrelationId);
            packer.Read(ref action);
            packer.Read(ref value.TraverseHash);
            packer.Read(ref value.TraverseIdString);
            packer.Read(ref value.ActionIdHash);
            packer.Read(ref value.ActionIdString);
            packer.Read(ref value.StateIdHash);
            packer.Read(ref value.StateIdString);
            packer.Read(ref value.ArgsSelfNetworkId);
            packer.Read(ref value.ArgsTargetNetworkId);
            packer.Read(ref value.IsTraversing);
            packer.Read(ref value.ServerTime);

            value.Action = (TraversalActionType)action;
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkTraversalSnapshot value)
        {
            packer.Write(value.NetworkId);
            packer.Write(value.ServerTime);
            packer.Write(value.IsTraversing);
            packer.Write(value.TraverseHash);
            packer.Write(value.TraverseIdString);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkTraversalSnapshot value)
        {
            packer.Read(ref value.NetworkId);
            packer.Read(ref value.ServerTime);
            packer.Read(ref value.IsTraversing);
            packer.Read(ref value.TraverseHash);
            packer.Read(ref value.TraverseIdString);
        }
    }
}
#endif
