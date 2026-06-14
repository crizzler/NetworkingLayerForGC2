#if GC2_DIALOGUE
using Arawn.GameCreator2.Networking.Dialogue;
using JetBrains.Annotations;
using PurrNet.Modules;
using PurrNet.Packing;
using PurrNet.Utils;

namespace Arawn.GameCreator2.Networking.Dialogue.Transport.PurrNet
{
    [UsedImplicitly]
    public static class PurrNetDialogueValuePackers
    {
        [RegisterPackers]
        static void Register()
        {
            Hasher.PrepareType(typeof(NetworkDialogueRequest));
            Hasher.PrepareType(typeof(NetworkDialogueResponse));
            Hasher.PrepareType(typeof(NetworkDialogueBroadcast));
            Hasher.PrepareType(typeof(NetworkDialogueSnapshot));
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkDialogueRequest value)
        {
            packer.Write(value.RequestId);
            packer.Write(value.ActorNetworkId);
            packer.Write(value.CorrelationId);
            packer.Write(value.TargetNetworkId);
            packer.Write((byte)value.Action);
            packer.Write(value.DialogueHash);
            packer.Write(value.DialogueIdString);
            packer.Write(value.ChoiceNodeId);
            packer.Write(value.SelfNetworkId);
            packer.Write(value.ArgsTargetNetworkId);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkDialogueRequest value)
        {
            byte action = 0;

            packer.Read(ref value.RequestId);
            packer.Read(ref value.ActorNetworkId);
            packer.Read(ref value.CorrelationId);
            packer.Read(ref value.TargetNetworkId);
            packer.Read(ref action);
            packer.Read(ref value.DialogueHash);
            packer.Read(ref value.DialogueIdString);
            packer.Read(ref value.ChoiceNodeId);
            packer.Read(ref value.SelfNetworkId);
            packer.Read(ref value.ArgsTargetNetworkId);

            value.Action = (DialogueActionType)action;
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkDialogueResponse value)
        {
            packer.Write(value.RequestId);
            packer.Write(value.ActorNetworkId);
            packer.Write(value.CorrelationId);
            packer.Write(value.TargetNetworkId);
            packer.Write((byte)value.Action);
            packer.Write(value.Authorized);
            packer.Write(value.Applied);
            packer.Write((byte)value.RejectionReason);
            packer.Write(value.DialogueHash);
            packer.Write(value.DialogueIdString);
            packer.Write(value.CurrentNodeId);
            packer.Write(value.IsPlaying);
            packer.Write(value.IsVisited);
            packer.Write(value.Error);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkDialogueResponse value)
        {
            byte action = 0;
            byte rejection = 0;

            packer.Read(ref value.RequestId);
            packer.Read(ref value.ActorNetworkId);
            packer.Read(ref value.CorrelationId);
            packer.Read(ref value.TargetNetworkId);
            packer.Read(ref action);
            packer.Read(ref value.Authorized);
            packer.Read(ref value.Applied);
            packer.Read(ref rejection);
            packer.Read(ref value.DialogueHash);
            packer.Read(ref value.DialogueIdString);
            packer.Read(ref value.CurrentNodeId);
            packer.Read(ref value.IsPlaying);
            packer.Read(ref value.IsVisited);
            packer.Read(ref value.Error);

            value.Action = (DialogueActionType)action;
            value.RejectionReason = (DialogueRejectionReason)rejection;
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkDialogueBroadcast value)
        {
            packer.Write(value.NetworkId);
            packer.Write(value.ActorNetworkId);
            packer.Write(value.CorrelationId);
            packer.Write((byte)value.Action);
            packer.Write(value.DialogueHash);
            packer.Write(value.DialogueIdString);
            packer.Write(value.CurrentNodeId);
            packer.Write(value.ChoiceNodeId);
            packer.Write(value.IsPlaying);
            packer.Write(value.IsVisited);
            packer.Write(value.ServerTime);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkDialogueBroadcast value)
        {
            byte action = 0;

            packer.Read(ref value.NetworkId);
            packer.Read(ref value.ActorNetworkId);
            packer.Read(ref value.CorrelationId);
            packer.Read(ref action);
            packer.Read(ref value.DialogueHash);
            packer.Read(ref value.DialogueIdString);
            packer.Read(ref value.CurrentNodeId);
            packer.Read(ref value.ChoiceNodeId);
            packer.Read(ref value.IsPlaying);
            packer.Read(ref value.IsVisited);
            packer.Read(ref value.ServerTime);

            value.Action = (DialogueActionType)action;
        }

        [UsedByIL]
        public static void Write(this BitPacker packer, NetworkDialogueSnapshot value)
        {
            packer.Write(value.NetworkId);
            packer.Write(value.ServerTime);
            packer.Write(value.DialogueHash);
            packer.Write(value.DialogueIdString);
            packer.Write(value.IsPlaying);
            packer.Write(value.IsVisited);
            packer.Write(value.CurrentNodeId);
            packer.WriteList(value.VisitedNodeIds);
            packer.WriteList(value.VisitedTagIds);
        }

        [UsedByIL]
        public static void Read(this BitPacker packer, ref NetworkDialogueSnapshot value)
        {
            packer.Read(ref value.NetworkId);
            packer.Read(ref value.ServerTime);
            packer.Read(ref value.DialogueHash);
            packer.Read(ref value.DialogueIdString);
            packer.Read(ref value.IsPlaying);
            packer.Read(ref value.IsVisited);
            packer.Read(ref value.CurrentNodeId);
            packer.ReadArray(ref value.VisitedNodeIds);
            packer.ReadArray(ref value.VisitedTagIds);
        }
    }
}
#endif
