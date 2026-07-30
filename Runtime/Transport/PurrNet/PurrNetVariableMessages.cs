using PurrNet.Packing;

namespace Arawn.GameCreator2.Networking.Transport.PurrNet
{
    public struct GC2VariableRequestPacket : IPackedAuto
    {
        public NetworkVariableRequest request;
    }

    public struct GC2VariableResponsePacket : IPackedAuto
    {
        public NetworkVariableResponse response;
    }

    public struct GC2VariableBroadcastPacket : IPackedAuto
    {
        public NetworkVariableBroadcast broadcast;
    }

    public struct GC2VariableSnapshotPacket : IPackedAuto
    {
        public NetworkVariableSnapshot snapshot;
    }
}
