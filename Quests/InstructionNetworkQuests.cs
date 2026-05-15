#if GC2_QUESTS
using System;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.Quests;
using GameCreator.Runtime.VisualScripting;
using UnityEngine;
using SystemTask = System.Threading.Tasks.Task;

namespace Arawn.GameCreator2.Networking.Quests
{
    [Version(0, 1, 0)]
    [Title("Network Quest Activate")]
    [Description("Requests a server-authoritative Quest activation on a networked Journal")]
    [Category("Network/Quests/Quest Activate")]
    [Parameter("Journal", "GameObject with Journal and NetworkQuestsController components")]
    [Parameter("Quest", "Quest asset to activate")]
    [Parameter("Share Mode", "Whether the quest state is personal, global, or party scoped")]
    [Keywords("Network", "Quest", "Activate", "Share")]
    [Image(typeof(IconQuestOutline), ColorTheme.Type.Teal, typeof(OverlayTick))]
    [Serializable]
    public sealed class InstructionNetworkQuestActivate : Instruction
    {
        [SerializeField] private PropertyGetGameObject m_Journal = GetGameObjectPlayer.Create();
        [SerializeField] private PropertyGetQuest m_Quest = new PropertyGetQuest();
        [SerializeField] private NetworkQuestShareMode m_ShareMode = NetworkQuestShareMode.Personal;
        [SerializeField] private PropertyGetString m_ScopeId = new PropertyGetString("");

        public override string Title => $"Network Activate {m_Quest}";

        protected override SystemTask Run(Args args)
        {
            if (!NetworkQuestInstructionUtility.TryGetController(m_Journal, args, nameof(InstructionNetworkQuestActivate), out var controller))
            {
                return SystemTask.CompletedTask;
            }

            Quest quest = m_Quest.Get(args);
            if (quest == null) return SystemTask.CompletedTask;

            controller.RequestQuestAction(
                QuestActionType.ActivateQuest,
                quest,
                -1,
                0d,
                m_ShareMode,
                m_ScopeId.Get(args));

            return SystemTask.CompletedTask;
        }
    }

    [Version(0, 1, 0)]
    [Title("Network Quest Share")]
    [Description("Requests a server-authoritative shared Quest activation")]
    [Category("Network/Quests/Share Quest")]
    [Parameter("Journal", "GameObject with Journal and NetworkQuestsController components")]
    [Parameter("Quest", "Quest asset to share")]
    [Parameter("Share Mode", "Global shares to all profiled journals. Party can be constrained by custom validators.")]
    [Keywords("Network", "Quest", "Share", "Party", "Global")]
    [Image(typeof(IconQuestOutline), ColorTheme.Type.Teal, typeof(OverlayPlus))]
    [Serializable]
    public sealed class InstructionNetworkQuestShare : Instruction
    {
        [SerializeField] private PropertyGetGameObject m_Journal = GetGameObjectPlayer.Create();
        [SerializeField] private PropertyGetQuest m_Quest = new PropertyGetQuest();
        [SerializeField] private NetworkQuestShareMode m_ShareMode = NetworkQuestShareMode.Global;
        [SerializeField] private PropertyGetString m_ScopeId = new PropertyGetString("");

        public override string Title => $"Network Share {m_Quest}";

        protected override SystemTask Run(Args args)
        {
            if (!NetworkQuestInstructionUtility.TryGetController(m_Journal, args, nameof(InstructionNetworkQuestShare), out var controller))
            {
                return SystemTask.CompletedTask;
            }

            Quest quest = m_Quest.Get(args);
            if (quest == null) return SystemTask.CompletedTask;

            controller.RequestQuestAction(
                QuestActionType.ActivateQuest,
                quest,
                -1,
                0d,
                m_ShareMode,
                m_ScopeId.Get(args));

            return SystemTask.CompletedTask;
        }
    }

    [Version(0, 1, 0)]
    [Title("Network Task Complete")]
    [Description("Requests a server-authoritative Task completion on a networked Journal")]
    [Category("Network/Quests/Task Complete")]
    [Parameter("Journal", "GameObject with Journal and NetworkQuestsController components")]
    [Parameter("Task", "Task identifier from the Quest")]
    [Parameter("Share Mode", "Whether the quest state is personal, global, or party scoped")]
    [Keywords("Network", "Quest", "Task", "Complete")]
    [Image(typeof(IconTaskOutline), ColorTheme.Type.Teal, typeof(OverlayTick))]
    [Serializable]
    public sealed class InstructionNetworkQuestTaskComplete : Instruction
    {
        [SerializeField] private PropertyGetGameObject m_Journal = GetGameObjectPlayer.Create();
        [SerializeField] private PickTask m_Task = new PickTask();
        [SerializeField] private NetworkQuestShareMode m_ShareMode = NetworkQuestShareMode.Personal;
        [SerializeField] private PropertyGetString m_ScopeId = new PropertyGetString("");

        public override string Title => $"Network Complete {m_Task}";

        protected override SystemTask Run(Args args)
        {
            if (!NetworkQuestInstructionUtility.TryGetController(m_Journal, args, nameof(InstructionNetworkQuestTaskComplete), out var controller))
            {
                return SystemTask.CompletedTask;
            }

            Quest quest = m_Task.Quest;
            int taskId = NetworkQuestInstructionUtility.GetTaskId(m_Task);
            if (quest == null || taskId == TasksTree.NODE_INVALID) return SystemTask.CompletedTask;

            controller.RequestQuestAction(
                QuestActionType.CompleteTask,
                quest,
                taskId,
                0d,
                m_ShareMode,
                m_ScopeId.Get(args));

            return SystemTask.CompletedTask;
        }
    }

    [Version(0, 1, 0)]
    [Title("Network Task Value")]
    [Description("Requests a server-authoritative Task value change on a networked Journal")]
    [Category("Network/Quests/Task Value")]
    [Parameter("Journal", "GameObject with Journal and NetworkQuestsController components")]
    [Parameter("Task", "Task identifier from the Quest")]
    [Parameter("Value", "Task value to set")]
    [Parameter("Share Mode", "Whether the quest state is personal, global, or party scoped")]
    [Keywords("Network", "Quest", "Task", "Value", "Progress")]
    [Image(typeof(IconTaskOutline), ColorTheme.Type.Teal, typeof(OverlayPlus))]
    [Serializable]
    public sealed class InstructionNetworkQuestTaskValue : Instruction
    {
        [SerializeField] private PropertyGetGameObject m_Journal = GetGameObjectPlayer.Create();
        [SerializeField] private PickTask m_Task = new PickTask();
        [SerializeField] private ChangeDecimal m_Value = new ChangeDecimal(1);
        [SerializeField] private NetworkQuestShareMode m_ShareMode = NetworkQuestShareMode.Personal;
        [SerializeField] private PropertyGetString m_ScopeId = new PropertyGetString("");

        public override string Title => $"Network {m_Task} {m_Value}";

        protected override SystemTask Run(Args args)
        {
            if (!NetworkQuestInstructionUtility.TryGetController(m_Journal, args, nameof(InstructionNetworkQuestTaskValue), out var controller))
            {
                return SystemTask.CompletedTask;
            }

            Journal journal = m_Journal.Get<Journal>(args);
            Quest quest = m_Task.Quest;
            int taskId = NetworkQuestInstructionUtility.GetTaskId(m_Task);
            if (journal == null || quest == null || taskId == TasksTree.NODE_INVALID) return SystemTask.CompletedTask;

            double value = m_Value.Get(journal.GetTaskValue(quest, taskId), args);
            controller.RequestQuestAction(
                QuestActionType.SetTaskValue,
                quest,
                taskId,
                value,
                m_ShareMode,
                m_ScopeId.Get(args));

            return SystemTask.CompletedTask;
        }
    }
}
#endif
