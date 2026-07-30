#if GC2_STATS
using Arawn.GameCreator2.Networking.Stats;
using PurrNet.Packing;

namespace Arawn.GameCreator2.Networking.Stats.Transport.PurrNet
{
    public struct GC2StatModifyRequestPacket : IPackedAuto
    {
        public NetworkStatModifyRequest request;
    }

    public struct GC2StatModifyResponsePacket : IPackedAuto
    {
        public NetworkStatModifyResponse response;
    }

    public struct GC2AttributeModifyRequestPacket : IPackedAuto
    {
        public NetworkAttributeModifyRequest request;
    }

    public struct GC2AttributeModifyResponsePacket : IPackedAuto
    {
        public NetworkAttributeModifyResponse response;
    }

    public struct GC2StatusEffectRequestPacket : IPackedAuto
    {
        public NetworkStatusEffectRequest request;
    }

    public struct GC2StatusEffectResponsePacket : IPackedAuto
    {
        public NetworkStatusEffectResponse response;
    }

    public struct GC2StatModifierRequestPacket : IPackedAuto
    {
        public NetworkStatModifierRequest request;
    }

    public struct GC2StatModifierResponsePacket : IPackedAuto
    {
        public NetworkStatModifierResponse response;
    }

    public struct GC2ClearStatusEffectsRequestPacket : IPackedAuto
    {
        public NetworkClearStatusEffectsRequest request;
    }

    public struct GC2ClearStatusEffectsResponsePacket : IPackedAuto
    {
        public NetworkClearStatusEffectsResponse response;
    }

    public struct GC2StatChangeBroadcastPacket : IPackedAuto
    {
        public NetworkStatChangeBroadcast broadcast;
    }

    public struct GC2AttributeChangeBroadcastPacket : IPackedAuto
    {
        public NetworkAttributeChangeBroadcast broadcast;
    }

    public struct GC2StatusEffectBroadcastPacket : IPackedAuto
    {
        public NetworkStatusEffectBroadcast broadcast;
    }

    public struct GC2StatModifierBroadcastPacket : IPackedAuto
    {
        public NetworkStatModifierBroadcast broadcast;
    }

    public struct GC2StatsSnapshotPacket : IPackedAuto
    {
        public NetworkStatsSnapshot snapshot;
    }

    public struct GC2StatsDeltaPacket : IPackedAuto
    {
        public NetworkStatsDelta delta;
    }
}
#endif
