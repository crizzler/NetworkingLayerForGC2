#if GC2_DIALOGUE
using Arawn.GameCreator2.Networking.Dialogue;
using PurrNet.Packing;

namespace Arawn.GameCreator2.Networking.Dialogue.Transport.PurrNet
{
    public struct GC2DialogueRequestPacket : IPackedAuto
    {
        public NetworkDialogueRequest request;
    }

    public struct GC2DialogueResponsePacket : IPackedAuto
    {
        public NetworkDialogueResponse response;
    }

    public struct GC2DialogueBroadcastPacket : IPackedAuto
    {
        public NetworkDialogueBroadcast broadcast;
    }

    public struct GC2DialogueSnapshotPacket : IPackedAuto
    {
        public NetworkDialogueSnapshot snapshot;
    }
}
#endif
