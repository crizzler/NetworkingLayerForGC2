#if GC2_QUESTS
using System;

namespace Arawn.GameCreator2.Networking.Quests
{
    public enum QuestActionType : byte
    {
        ActivateQuest = 0,
        DeactivateQuest = 1,
        ActivateTask = 2,
        DeactivateTask = 3,
        CompleteTask = 4,
        AbandonTask = 5,
        FailTask = 6,
        SetTaskValue = 7,
        TrackQuest = 8,
        UntrackQuest = 9,
        UntrackAll = 10
    }

    public enum QuestRejectionReason : byte
    {
        None = 0,
        NotAuthorized = 1,
        SecurityViolation = 2,
        ProtocolMismatch = 3,
        RateLimitExceeded = 4,
        TargetNotFound = 5,
        QuestNotFound = 6,
        TaskNotFound = 7,
        InvalidAction = 8,
        InvalidState = 9,
        IdentityMismatch = 10,
        Exception = 11
    }

    [Serializable]
    public struct NetworkQuestRequest
    {
        public ushort RequestId;
        public uint ActorNetworkId;
        public uint CorrelationId;
        public uint TargetNetworkId;

        public QuestActionType Action;

        public int QuestHash;
        public string QuestIdString;
        public int TaskId;
        public double Value;
    }

    [Serializable]
    public struct NetworkQuestResponse
    {
        public ushort RequestId;
        public uint ActorNetworkId;
        public uint CorrelationId;

        public QuestActionType Action;
        public bool Authorized;
        public bool Applied;
        public QuestRejectionReason RejectionReason;

        public int QuestHash;
        public string QuestIdString;
        public int TaskId;

        public byte QuestState;
        public byte TaskState;
        public bool IsTracking;
        public double TaskValue;

        public string Error;
    }

    [Serializable]
    public struct NetworkQuestBroadcast
    {
        public uint NetworkId;
        public uint ActorNetworkId;
        public uint CorrelationId;

        public QuestActionType Action;

        public int QuestHash;
        public string QuestIdString;
        public int TaskId;

        public byte QuestState;
        public byte TaskState;
        public bool IsTracking;
        public double TaskValue;

        public float ServerTime;
    }

    [Serializable]
    public struct NetworkQuestSnapshotEntry
    {
        public int QuestHash;
        public string QuestIdString;
        public byte State;
        public bool IsTracking;
    }

    [Serializable]
    public struct NetworkTaskSnapshotEntry
    {
        public int QuestHash;
        public string QuestIdString;
        public int TaskId;
        public byte State;
        public double Value;
    }

    [Serializable]
    public struct NetworkQuestsSnapshot
    {
        public uint NetworkId;
        public float ServerTime;
        public NetworkQuestSnapshotEntry[] QuestEntries;
        public NetworkTaskSnapshotEntry[] TaskEntries;
    }
}
#endif
