using PurrNet.Packing;

namespace Arawn.GameCreator2.Networking.Transport.PurrNet
{
    public struct GC2AnimationStateCommandPacket : IPackedAuto
    {
        public NetworkAnimationStateCommandMessage message;
    }

    public struct GC2AnimationGestureCommandPacket : IPackedAuto
    {
        public NetworkAnimationGestureCommandMessage message;
    }

    public struct GC2AnimationStopStateCommandPacket : IPackedAuto
    {
        public NetworkAnimationStopStateCommandMessage message;
    }

    public struct GC2AnimationStopGestureCommandPacket : IPackedAuto
    {
        public NetworkAnimationStopGestureCommandMessage message;
    }

    public struct GC2MotionCommandPacket : IPackedAuto
    {
        public NetworkMotionCommandMessage message;
    }

    public struct GC2MotionResultPacket : IPackedAuto
    {
        public NetworkMotionResultMessage message;
    }
}
