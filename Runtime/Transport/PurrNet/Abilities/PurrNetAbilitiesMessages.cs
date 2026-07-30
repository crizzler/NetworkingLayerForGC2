#if GC2_ABILITIES
using Arawn.GameCreator2.Networking;
using PurrNet.Packing;

namespace Arawn.GameCreator2.Networking.Abilities.Transport.PurrNet
{
    public struct GC2AbilityCastRequestPacket : IPackedAuto
    {
        public NetworkAbilityCastRequest request;
    }

    public struct GC2AbilityCastResponsePacket : IPackedAuto
    {
        public NetworkAbilityCastResponse response;
    }

    public struct GC2AbilityCastBroadcastPacket : IPackedAuto
    {
        public NetworkAbilityCastBroadcast broadcast;
    }

    public struct GC2AbilityEffectBroadcastPacket : IPackedAuto
    {
        public NetworkAbilityEffectBroadcast broadcast;
    }

    public struct GC2AbilityProjectileSpawnPacket : IPackedAuto
    {
        public NetworkProjectileSpawnBroadcast broadcast;
    }

    public struct GC2AbilityProjectileEventPacket : IPackedAuto
    {
        public NetworkProjectileEventBroadcast broadcast;
    }

    public struct GC2AbilityImpactSpawnPacket : IPackedAuto
    {
        public NetworkImpactSpawnBroadcast broadcast;
    }

    public struct GC2AbilityImpactHitPacket : IPackedAuto
    {
        public NetworkImpactHitBroadcast broadcast;
    }

    public struct GC2AbilityCooldownRequestPacket : IPackedAuto
    {
        public NetworkCooldownRequest request;
    }

    public struct GC2AbilityCooldownResponsePacket : IPackedAuto
    {
        public NetworkCooldownResponse response;
    }

    public struct GC2AbilityCooldownBroadcastPacket : IPackedAuto
    {
        public NetworkCooldownBroadcast broadcast;
    }

    public struct GC2AbilityLearnRequestPacket : IPackedAuto
    {
        public NetworkAbilityLearnRequest request;
    }

    public struct GC2AbilityLearnResponsePacket : IPackedAuto
    {
        public NetworkAbilityLearnResponse response;
    }

    public struct GC2AbilityLearnBroadcastPacket : IPackedAuto
    {
        public NetworkAbilityLearnBroadcast broadcast;
    }

    public struct GC2AbilityCancelRequestPacket : IPackedAuto
    {
        public NetworkCastCancelRequest request;
    }

    public struct GC2AbilityCancelResponsePacket : IPackedAuto
    {
        public NetworkCastCancelResponse response;
    }
}
#endif
