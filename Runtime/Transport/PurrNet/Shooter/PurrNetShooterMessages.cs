#if GC2_SHOOTER
using PurrNet.Packing;

namespace Arawn.GameCreator2.Networking.Shooter.Transport.PurrNet
{
    public struct GC2ShooterShotRequestPacket : IPackedAuto
    {
        public NetworkShotRequest request;
    }

    public struct GC2ShooterShotResponsePacket : IPackedAuto
    {
        public NetworkShotResponse response;
    }

    public struct GC2ShooterShotBroadcastPacket : IPackedAuto
    {
        public NetworkShotBroadcast broadcast;
    }

    public struct GC2ShooterHitRequestPacket : IPackedAuto
    {
        public NetworkShooterHitRequest request;
    }

    public struct GC2ShooterHitResponsePacket : IPackedAuto
    {
        public NetworkShooterHitResponse response;
    }

    public struct GC2ShooterHitBroadcastPacket : IPackedAuto
    {
        public NetworkShooterHitBroadcast broadcast;
    }

    public struct GC2ShooterReloadRequestPacket : IPackedAuto
    {
        public NetworkReloadRequest request;
    }

    public struct GC2ShooterReloadResponsePacket : IPackedAuto
    {
        public NetworkReloadResponse response;
    }

    public struct GC2ShooterReloadBroadcastPacket : IPackedAuto
    {
        public NetworkReloadBroadcast broadcast;
    }

    public struct GC2ShooterWeaponStatePacket : IPackedAuto
    {
        public uint characterNetworkId;
        public NetworkWeaponState state;
    }

    public struct GC2ShooterAimStatePacket : IPackedAuto
    {
        public uint characterNetworkId;
        public NetworkAimState state;
    }
}
#endif
