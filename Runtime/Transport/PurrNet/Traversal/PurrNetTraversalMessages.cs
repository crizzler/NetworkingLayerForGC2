#if GC2_TRAVERSAL
using Arawn.GameCreator2.Networking.Traversal;
using PurrNet.Packing;

namespace Arawn.GameCreator2.Networking.Traversal.Transport.PurrNet
{
    public struct GC2TraversalRequestPacket : IPackedAuto
    {
        public NetworkTraversalRequest request;
    }

    public struct GC2TraversalResponsePacket : IPackedAuto
    {
        public NetworkTraversalResponse response;
    }

    public struct GC2TraversalBroadcastPacket : IPackedAuto
    {
        public NetworkTraversalBroadcast broadcast;
    }

    public struct GC2TraversalSnapshotPacket : IPackedAuto
    {
        public NetworkTraversalSnapshot snapshot;
    }
}
#endif
