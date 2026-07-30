using JetBrains.Annotations;
using PurrNet.Modules;
using PurrNet.Packing;
using PurrNet.Utils;

namespace Arawn.GameCreator2.Networking.Transport.PurrNet
{
    [UsedImplicitly]
    public static class PurrNetVariableValuePackers
    {
        [RegisterPackers]
        static void Register()
        {
            Hasher.PrepareType(typeof(NetworkVariableRequest));
            Hasher.PrepareType(typeof(NetworkVariableResponse));
            Hasher.PrepareType(typeof(NetworkVariableBroadcast));
            Hasher.PrepareType(typeof(NetworkVariableSnapshot));
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkVariableRequest value)
        {
            packer.Write(value.RequestId);
            packer.Write(value.ActorNetworkId);
            packer.Write(value.CorrelationId);
            packer.Write(value.TargetNetworkId);
            packer.Write((byte)value.Scope);
            packer.Write((byte)value.Operation);
            packer.Write(value.ProfileHash);
            packer.Write(value.VariableHash);
            packer.Write(value.VariableName);
            packer.Write(value.Index);
            packer.Write(value.IndexTo);
            packer.Write(value.SerializedValue);
            packer.Write(value.ClientTime);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkVariableRequest value)
        {
            byte scope = 0;
            byte operation = 0;

            packer.Read(ref value.RequestId);
            packer.Read(ref value.ActorNetworkId);
            packer.Read(ref value.CorrelationId);
            packer.Read(ref value.TargetNetworkId);
            packer.Read(ref scope);
            packer.Read(ref operation);
            packer.Read(ref value.ProfileHash);
            packer.Read(ref value.VariableHash);
            packer.Read(ref value.VariableName);
            packer.Read(ref value.Index);
            packer.Read(ref value.IndexTo);
            packer.Read(ref value.SerializedValue);
            packer.Read(ref value.ClientTime);

            value.Scope = (NetworkVariableScope)scope;
            value.Operation = (NetworkVariableOperation)operation;
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkVariableResponse value)
        {
            packer.Write(value.RequestId);
            packer.Write(value.ActorNetworkId);
            packer.Write(value.CorrelationId);
            packer.Write(value.TargetNetworkId);
            packer.Write(value.Authorized);
            packer.Write((byte)value.RejectReason);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkVariableResponse value)
        {
            byte reason = 0;

            packer.Read(ref value.RequestId);
            packer.Read(ref value.ActorNetworkId);
            packer.Read(ref value.CorrelationId);
            packer.Read(ref value.TargetNetworkId);
            packer.Read(ref value.Authorized);
            packer.Read(ref reason);

            value.RejectReason = (NetworkVariableRejectReason)reason;
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkVariableBroadcast value)
        {
            packer.Write(value.ActorNetworkId);
            packer.Write(value.TargetNetworkId);
            packer.Write((byte)value.Scope);
            packer.Write((byte)value.Operation);
            packer.Write(value.ProfileHash);
            packer.Write(value.VariableHash);
            packer.Write(value.VariableName);
            packer.Write(value.Index);
            packer.Write(value.IndexTo);
            packer.Write(value.SerializedValue);
            packer.Write(value.ServerTime);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkVariableBroadcast value)
        {
            byte scope = 0;
            byte operation = 0;

            packer.Read(ref value.ActorNetworkId);
            packer.Read(ref value.TargetNetworkId);
            packer.Read(ref scope);
            packer.Read(ref operation);
            packer.Read(ref value.ProfileHash);
            packer.Read(ref value.VariableHash);
            packer.Read(ref value.VariableName);
            packer.Read(ref value.Index);
            packer.Read(ref value.IndexTo);
            packer.Read(ref value.SerializedValue);
            packer.Read(ref value.ServerTime);

            value.Scope = (NetworkVariableScope)scope;
            value.Operation = (NetworkVariableOperation)operation;
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkVariableSnapshot value)
        {
            packer.Write(value.ServerTime);
            packer.WriteList(value.Changes);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkVariableSnapshot value)
        {
            packer.Read(ref value.ServerTime);
            packer.ReadArray(ref value.Changes);
        }
    }
}
