#if GC2_QUESTS
using System;
using GameCreator.Runtime.Quests;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Quests
{
    [CreateAssetMenu(
        fileName = "Network Quest Profile",
        menuName = "Game Creator/Network/Quests/Network Quest Profile"
    )]
    public sealed class NetworkQuestProfile : ScriptableObject
    {
        [Header("Bindings")]
        [SerializeField] private NetworkQuestBinding[] m_Quests = Array.Empty<NetworkQuestBinding>();

        [Header("Runtime")]
        [SerializeField] private bool m_SnapshotOnLateJoin = true;
        [SerializeField] private bool m_ReplayQuestMethodsOnRemoteClients;

        public NetworkQuestBinding[] Quests => m_Quests ?? Array.Empty<NetworkQuestBinding>();
        public bool SnapshotOnLateJoin => m_SnapshotOnLateJoin;
        public bool ReplayQuestMethodsOnRemoteClients => m_ReplayQuestMethodsOnRemoteClients;
        public int ProfileHash => Animator.StringToHash(name);

        public bool TryGetBinding(
            Quest quest,
            int questHash,
            out NetworkQuestBinding binding)
        {
            binding = default;
            NetworkQuestBinding[] bindings = Quests;
            for (int i = 0; i < bindings.Length; i++)
            {
                Quest candidate = bindings[i].Quest;
                if (candidate == null) continue;

                int candidateHash = candidate.Id.Hash;
                if (quest != null && candidateHash == quest.Id.Hash)
                {
                    binding = bindings[i];
                    return true;
                }

                if (questHash != 0 && candidateHash == questHash)
                {
                    binding = bindings[i];
                    return true;
                }
            }

            return false;
        }

        public bool IsQuestAllowed(
            Quest quest,
            int questHash,
            QuestActionType action,
            NetworkQuestShareMode shareMode,
            bool requireClientWrite,
            bool requireAutoForward,
            out NetworkQuestBinding binding)
        {
            binding = default;
            NetworkQuestActionMask actionMask = GetActionMask(action);
            NetworkQuestBinding[] bindings = Quests;
            for (int i = 0; i < bindings.Length; i++)
            {
                NetworkQuestBinding candidateBinding = bindings[i];
                Quest candidate = candidateBinding.Quest;
                if (candidate == null) continue;

                int candidateHash = candidate.Id.Hash;
                bool matchesQuest = quest != null && candidateHash == quest.Id.Hash;
                bool matchesHash = questHash != 0 && candidateHash == questHash;
                if (!matchesQuest && !matchesHash) continue;

                if (candidateBinding.ShareMode != shareMode) continue;
                if (requireClientWrite && !candidateBinding.AllowClientWrites) continue;
                if (requireAutoForward && !candidateBinding.AutoForwardJournalChanges) continue;
                if ((candidateBinding.AllowedActions & actionMask) == 0) continue;

                binding = candidateBinding;
                return true;
            }

            return false;
        }

        public static NetworkQuestActionMask GetActionMask(QuestActionType action)
        {
            return action switch
            {
                QuestActionType.ActivateQuest => NetworkQuestActionMask.ActivateQuest,
                QuestActionType.DeactivateQuest => NetworkQuestActionMask.DeactivateQuest,
                QuestActionType.ActivateTask => NetworkQuestActionMask.ActivateTask,
                QuestActionType.DeactivateTask => NetworkQuestActionMask.DeactivateTask,
                QuestActionType.CompleteTask => NetworkQuestActionMask.CompleteTask,
                QuestActionType.AbandonTask => NetworkQuestActionMask.AbandonTask,
                QuestActionType.FailTask => NetworkQuestActionMask.FailTask,
                QuestActionType.SetTaskValue => NetworkQuestActionMask.SetTaskValue,
                QuestActionType.TrackQuest => NetworkQuestActionMask.TrackQuest,
                QuestActionType.UntrackQuest => NetworkQuestActionMask.UntrackQuest,
                QuestActionType.UntrackAll => NetworkQuestActionMask.UntrackAll,
                _ => NetworkQuestActionMask.None
            };
        }
    }
}
#endif
