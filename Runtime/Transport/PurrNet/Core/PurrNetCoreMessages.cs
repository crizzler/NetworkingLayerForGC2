using PurrNet.Packing;

namespace Arawn.GameCreator2.Networking.Transport.PurrNet
{
    public struct GC2CoreRagdollRequestPacket : IPackedAuto { public NetworkRagdollRequest Value; }
    public struct GC2CoreRagdollResponsePacket : IPackedAuto { public NetworkRagdollResponse Value; }
    public struct GC2CoreRagdollBroadcastPacket : IPackedAuto { public NetworkRagdollBroadcast Value; }

    public struct GC2CorePropRequestPacket : IPackedAuto { public NetworkPropRequest Value; }
    public struct GC2CorePropResponsePacket : IPackedAuto { public NetworkPropResponse Value; }
    public struct GC2CorePropBroadcastPacket : IPackedAuto { public NetworkPropBroadcast Value; }

    public struct GC2CoreInvincibilityRequestPacket : IPackedAuto { public NetworkInvincibilityRequest Value; }
    public struct GC2CoreInvincibilityResponsePacket : IPackedAuto { public NetworkInvincibilityResponse Value; }
    public struct GC2CoreInvincibilityBroadcastPacket : IPackedAuto { public NetworkInvincibilityBroadcast Value; }

    public struct GC2CorePoiseRequestPacket : IPackedAuto { public NetworkPoiseRequest Value; }
    public struct GC2CorePoiseResponsePacket : IPackedAuto { public NetworkPoiseResponse Value; }
    public struct GC2CorePoiseBroadcastPacket : IPackedAuto { public NetworkPoiseBroadcast Value; }

    public struct GC2CoreBusyRequestPacket : IPackedAuto { public NetworkBusyRequest Value; }
    public struct GC2CoreBusyResponsePacket : IPackedAuto { public NetworkBusyResponse Value; }
    public struct GC2CoreBusyBroadcastPacket : IPackedAuto { public NetworkBusyBroadcast Value; }

    public struct GC2CoreInteractionRequestPacket : IPackedAuto { public NetworkInteractionRequest Value; }
    public struct GC2CoreInteractionResponsePacket : IPackedAuto { public NetworkInteractionResponse Value; }
    public struct GC2CoreInteractionBroadcastPacket : IPackedAuto { public NetworkInteractionBroadcast Value; }
    public struct GC2CoreInteractionFocusPacket : IPackedAuto { public NetworkInteractionFocusBroadcast Value; }

    public struct GC2CoreSnapshotPacket : IPackedAuto { public NetworkCoreSnapshot Value; }
}
