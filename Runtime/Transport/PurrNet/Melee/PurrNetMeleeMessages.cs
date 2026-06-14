#if GC2_MELEE
using PurrNet.Packing;

namespace Arawn.GameCreator2.Networking.Melee.Transport.PurrNet
{
    public struct GC2MeleeHitRequestPacket : IPackedAuto
    {
        public NetworkMeleeHitRequest request;
    }

    public struct GC2MeleeHitResponsePacket : IPackedAuto
    {
        public NetworkMeleeHitResponse response;
    }

    public struct GC2MeleeHitBroadcastPacket : IPackedAuto
    {
        public NetworkMeleeHitBroadcast broadcast;
    }

    public struct GC2MeleeBlockRequestPacket : IPackedAuto
    {
        public NetworkBlockRequest request;
    }

    public struct GC2MeleeBlockResponsePacket : IPackedAuto
    {
        public NetworkBlockResponse response;
    }

    public struct GC2MeleeBlockBroadcastPacket : IPackedAuto
    {
        public NetworkBlockBroadcast broadcast;
    }

    public struct GC2MeleeSkillRequestPacket : IPackedAuto
    {
        public NetworkSkillRequest request;
    }

    public struct GC2MeleeSkillResponsePacket : IPackedAuto
    {
        public NetworkSkillResponse response;
    }

    public struct GC2MeleeSkillBroadcastPacket : IPackedAuto
    {
        public NetworkSkillBroadcast broadcast;
    }

    public struct GC2MeleeChargeRequestPacket : IPackedAuto
    {
        public NetworkChargeRequest request;
    }

    public struct GC2MeleeChargeResponsePacket : IPackedAuto
    {
        public NetworkChargeResponse response;
    }

    public struct GC2MeleeChargeBroadcastPacket : IPackedAuto
    {
        public NetworkChargeBroadcast broadcast;
    }

    public struct GC2MeleeReactionBroadcastPacket : IPackedAuto
    {
        public NetworkReactionBroadcast broadcast;
    }

    public struct GC2MeleeWeaponStatePacket : IPackedAuto
    {
        public uint characterNetworkId;
        public NetworkMeleeWeaponState state;
    }
}
#endif
