using PurrNet.Packing;

namespace Arawn.GameCreator2.Networking.Transport.PurrNet
{
    public struct GC2NetworkActionRequestPacket : IPackedAuto
    {
        public NetworkActionRequest Request;
    }

    public struct GC2NetworkActionResponsePacket : IPackedAuto
    {
        public NetworkActionResponse Response;
    }

    public struct GC2NetworkActionBroadcastPacket : IPackedAuto
    {
        public NetworkActionBroadcast Broadcast;
    }

    public struct GC2NetworkActionSnapshotPacket : IPackedAuto
    {
        public NetworkActionSnapshot Snapshot;
    }
}
