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

    public struct GC2ShooterQuickReloadRequestPacket : IPackedAuto
    {
        public NetworkQuickReloadRequest request;
    }

    public struct GC2ShooterReloadResponsePacket : IPackedAuto
    {
        public NetworkReloadResponse response;
    }

    public struct GC2ShooterReloadBroadcastPacket : IPackedAuto
    {
        public NetworkReloadBroadcast broadcast;
    }

    public struct GC2ShooterFixJamRequestPacket : IPackedAuto
    {
        public NetworkFixJamRequest request;
    }

    public struct GC2ShooterFixJamResponsePacket : IPackedAuto
    {
        public NetworkFixJamResponse response;
    }

    public struct GC2ShooterJamBroadcastPacket : IPackedAuto
    {
        public NetworkJamBroadcast broadcast;
    }

    public struct GC2ShooterFixJamBroadcastPacket : IPackedAuto
    {
        public NetworkFixJamBroadcast broadcast;
    }

    public struct GC2ShooterChargeStartRequestPacket : IPackedAuto
    {
        public NetworkChargeStartRequest request;
    }

    public struct GC2ShooterChargeStartResponsePacket : IPackedAuto
    {
        public NetworkChargeStartResponse response;
    }

    public struct GC2ShooterChargeCancelRequestPacket : IPackedAuto
    {
        public NetworkChargeCancelRequest request;
    }

    public struct GC2ShooterChargeBroadcastPacket : IPackedAuto
    {
        public NetworkChargeBroadcast broadcast;
    }

    public struct GC2ShooterSightSwitchRequestPacket : IPackedAuto
    {
        public NetworkSightSwitchRequest request;
    }

    public struct GC2ShooterSightSwitchResponsePacket : IPackedAuto
    {
        public NetworkSightSwitchResponse response;
    }

    public struct GC2ShooterSightSwitchBroadcastPacket : IPackedAuto
    {
        public NetworkSightSwitchBroadcast broadcast;
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

    public struct GC2ShooterCharacterSnapshotPacket : IPackedAuto
    {
        public NetworkShooterCharacterSnapshot snapshot;
    }

    public struct GC2ShooterImpactPropSnapshotPacket : IPackedAuto
    {
        public NetworkShooterImpactPropSnapshot snapshot;
    }
}
#endif
