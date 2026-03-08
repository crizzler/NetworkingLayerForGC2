#if GC2_DIALOGUE
using System;

namespace Arawn.GameCreator2.Networking.Dialogue
{
    public enum DialogueActionType : byte
    {
        Play = 0,
        Stop = 1,
        Continue = 2,
        Choose = 3
    }

    public enum DialogueRejectionReason : byte
    {
        None = 0,
        NotAuthorized = 1,
        SecurityViolation = 2,
        ProtocolMismatch = 3,
        RateLimitExceeded = 4,
        TargetNotFound = 5,
        InvalidAction = 6,
        InvalidState = 7,
        IdentityMismatch = 8,
        Exception = 9
    }

    [Serializable]
    public struct NetworkDialogueRequest
    {
        public ushort RequestId;
        public uint ActorNetworkId;
        public uint CorrelationId;
        public uint TargetNetworkId;

        public DialogueActionType Action;

        public int DialogueHash;
        public string DialogueIdString;

        public int ChoiceNodeId;

        public uint SelfNetworkId;
        public uint ArgsTargetNetworkId;
    }

    [Serializable]
    public struct NetworkDialogueResponse
    {
        public ushort RequestId;
        public uint ActorNetworkId;
        public uint CorrelationId;

        public DialogueActionType Action;
        public bool Authorized;
        public bool Applied;
        public DialogueRejectionReason RejectionReason;

        public int DialogueHash;
        public string DialogueIdString;

        public int CurrentNodeId;
        public bool IsPlaying;
        public bool IsVisited;

        public string Error;
    }

    [Serializable]
    public struct NetworkDialogueBroadcast
    {
        public uint NetworkId;
        public uint ActorNetworkId;
        public uint CorrelationId;

        public DialogueActionType Action;

        public int DialogueHash;
        public string DialogueIdString;

        public int CurrentNodeId;
        public int ChoiceNodeId;

        public bool IsPlaying;
        public bool IsVisited;

        public float ServerTime;
    }

    [Serializable]
    public struct NetworkDialogueSnapshot
    {
        public uint NetworkId;
        public float ServerTime;

        public int DialogueHash;
        public string DialogueIdString;

        public bool IsPlaying;
        public bool IsVisited;
        public int CurrentNodeId;

        public int[] VisitedNodeIds;
        public string[] VisitedTagIds;
    }
}
#endif
