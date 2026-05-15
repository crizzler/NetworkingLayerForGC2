#if GC2_QUESTS
using System;
using GameCreator.Runtime.Quests;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Quests
{
    public enum NetworkQuestShareMode : byte
    {
        Personal = 0,
        Global = 1,
        Party = 2
    }

    [Flags]
    public enum NetworkQuestActionMask
    {
        None = 0,
        ActivateQuest = 1 << 0,
        DeactivateQuest = 1 << 1,
        ActivateTask = 1 << 2,
        DeactivateTask = 1 << 3,
        CompleteTask = 1 << 4,
        AbandonTask = 1 << 5,
        FailTask = 1 << 6,
        SetTaskValue = 1 << 7,
        TrackQuest = 1 << 8,
        UntrackQuest = 1 << 9,
        UntrackAll = 1 << 10,
        All = ActivateQuest |
              DeactivateQuest |
              ActivateTask |
              DeactivateTask |
              CompleteTask |
              AbandonTask |
              FailTask |
              SetTaskValue |
              TrackQuest |
              UntrackQuest |
              UntrackAll
    }

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

    [Serializable]
    public struct NetworkQuestBinding
    {
        [SerializeField] private Quest m_Quest;
        [SerializeField] private NetworkQuestShareMode m_ShareMode;
        [SerializeField] private bool m_AllowClientWrites;
        [SerializeField] private bool m_AutoForwardJournalChanges;
        [SerializeField] private NetworkQuestActionMask m_AllowedActions;

        public Quest Quest => m_Quest;
        public NetworkQuestShareMode ShareMode => m_ShareMode;
        public bool AllowClientWrites => m_AllowClientWrites;
        public bool AutoForwardJournalChanges => m_AutoForwardJournalChanges;
        public NetworkQuestActionMask AllowedActions => m_AllowedActions;
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

        public int ProfileHash;
        public NetworkQuestShareMode ShareMode;
        public string ScopeId;

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

        public int ProfileHash;
        public NetworkQuestShareMode ShareMode;
        public string ScopeId;

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

        public int ProfileHash;
        public NetworkQuestShareMode ShareMode;
        public string ScopeId;

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
        public int ProfileHash;
        public NetworkQuestShareMode ShareMode;
        public string ScopeId;
        public int QuestHash;
        public string QuestIdString;
        public byte State;
        public bool IsTracking;
    }

    [Serializable]
    public struct NetworkTaskSnapshotEntry
    {
        public int ProfileHash;
        public NetworkQuestShareMode ShareMode;
        public string ScopeId;
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
