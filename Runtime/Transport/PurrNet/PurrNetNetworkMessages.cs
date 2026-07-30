using PurrNet.Packing;

namespace Arawn.GameCreator2.Networking.Transport.PurrNet
{
    /// <summary>
    /// Client -> Server broadcast carrying one or more NetworkInputState samples
    /// for a specific NetworkCharacter (identified by its GC2 networkId).
    /// </summary>
    public struct GC2InputBroadcast : IPackedAuto
    {
        public uint characterNetworkId;
        public NetworkInputState[] inputs;
    }

    /// <summary>
    /// Server -> Client broadcast carrying the authoritative NetworkPositionState
    /// for a specific NetworkCharacter plus the server timestamp.
    /// </summary>
    public struct GC2StateBroadcast : IPackedAuto
    {
        public uint characterNetworkId;
        public NetworkPositionState state;
        public float serverTime;
    }
}
