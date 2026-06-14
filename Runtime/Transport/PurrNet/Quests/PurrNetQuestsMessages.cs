#if GC2_QUESTS
using Arawn.GameCreator2.Networking.Quests;
using PurrNet.Packing;

namespace Arawn.GameCreator2.Networking.Quests.Transport.PurrNet
{
    public struct GC2QuestRequestPacket : IPackedAuto
    {
        public NetworkQuestRequest request;
    }

    public struct GC2QuestResponsePacket : IPackedAuto
    {
        public NetworkQuestResponse response;
    }

    public struct GC2QuestBroadcastPacket : IPackedAuto
    {
        public NetworkQuestBroadcast broadcast;
    }

    public struct GC2QuestSnapshotPacket : IPackedAuto
    {
        public NetworkQuestsSnapshot snapshot;
    }
}
#endif
