#if GC2_QUESTS
using System;
using System.Reflection;
using GameCreator.Runtime.Quests;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Quests
{
    /// <summary>
    /// Runtime installer for Quests patch delegates. Enables patched-mode authority on server/client.
    /// </summary>
    public class NetworkQuestsPatchHooks : NetworkSingleton<NetworkQuestsPatchHooks>
    {
        private const BindingFlags STATIC_PUBLIC = BindingFlags.Public | BindingFlags.Static;

        private bool m_IsServer;
        private bool m_Installed;

        public bool IsPatchActive => m_Installed && IsQuestsPatched();

        public void Initialize(bool isServer, bool isActive = true)
        {
            m_IsServer = isServer;
            if (isActive) InstallHooks();
            else UninstallHooks();
        }

        protected override void OnSingletonCleanup()
        {
            UninstallHooks();
        }

        public static bool IsQuestsPatched()
        {
            Type journalType = typeof(Journal);
            return
                HasPublicStaticField(journalType, "NetworkActivateQuestValidator", typeof(Func<Journal, Quest, bool>)) &&
                HasPublicStaticField(journalType, "NetworkDeactivateQuestValidator", typeof(Func<Journal, Quest, bool>)) &&
                HasPublicStaticField(journalType, "NetworkActivateTaskValidator", typeof(Func<Journal, Quest, int, bool>)) &&
                HasPublicStaticField(journalType, "NetworkDeactivateTaskValidator", typeof(Func<Journal, Quest, int, bool>)) &&
                HasPublicStaticField(journalType, "NetworkCompleteTaskValidator", typeof(Func<Journal, Quest, int, bool>)) &&
                HasPublicStaticField(journalType, "NetworkAbandonTaskValidator", typeof(Func<Journal, Quest, int, bool>)) &&
                HasPublicStaticField(journalType, "NetworkFailTaskValidator", typeof(Func<Journal, Quest, int, bool>)) &&
                HasPublicStaticField(journalType, "NetworkSetTaskValueValidator", typeof(Func<Journal, Quest, int, double, bool>)) &&
                HasPublicStaticField(journalType, "NetworkTrackQuestValidator", typeof(Func<Journal, Quest, bool>)) &&
                HasPublicStaticField(journalType, "NetworkUntrackQuestValidator", typeof(Func<Journal, Quest, bool>)) &&
                HasPublicStaticField(journalType, "NetworkUntrackAllValidator", typeof(Func<Journal, bool>));
        }

        private void InstallHooks()
        {
            if (m_Installed) return;
            if (!IsQuestsPatched())
            {
                Debug.LogWarning("[NetworkQuestsPatchHooks] Quests runtime patch markers were not detected. Using interception mode.");
                return;
            }

            SetStaticField(typeof(Journal), "NetworkActivateQuestValidator", new Func<Journal, Quest, bool>(ValidateActivateQuest));
            SetStaticField(typeof(Journal), "NetworkDeactivateQuestValidator", new Func<Journal, Quest, bool>(ValidateDeactivateQuest));
            SetStaticField(typeof(Journal), "NetworkActivateTaskValidator", new Func<Journal, Quest, int, bool>(ValidateActivateTask));
            SetStaticField(typeof(Journal), "NetworkDeactivateTaskValidator", new Func<Journal, Quest, int, bool>(ValidateDeactivateTask));
            SetStaticField(typeof(Journal), "NetworkCompleteTaskValidator", new Func<Journal, Quest, int, bool>(ValidateCompleteTask));
            SetStaticField(typeof(Journal), "NetworkAbandonTaskValidator", new Func<Journal, Quest, int, bool>(ValidateAbandonTask));
            SetStaticField(typeof(Journal), "NetworkFailTaskValidator", new Func<Journal, Quest, int, bool>(ValidateFailTask));
            SetStaticField(typeof(Journal), "NetworkSetTaskValueValidator", new Func<Journal, Quest, int, double, bool>(ValidateSetTaskValue));
            SetStaticField(typeof(Journal), "NetworkTrackQuestValidator", new Func<Journal, Quest, bool>(ValidateTrackQuest));
            SetStaticField(typeof(Journal), "NetworkUntrackQuestValidator", new Func<Journal, Quest, bool>(ValidateUntrackQuest));
            SetStaticField(typeof(Journal), "NetworkUntrackAllValidator", new Func<Journal, bool>(ValidateUntrackAll));

            m_Installed = true;
        }

        private void UninstallHooks()
        {
            if (!m_Installed) return;

            SetStaticField(typeof(Journal), "NetworkActivateQuestValidator", null);
            SetStaticField(typeof(Journal), "NetworkDeactivateQuestValidator", null);
            SetStaticField(typeof(Journal), "NetworkActivateTaskValidator", null);
            SetStaticField(typeof(Journal), "NetworkDeactivateTaskValidator", null);
            SetStaticField(typeof(Journal), "NetworkCompleteTaskValidator", null);
            SetStaticField(typeof(Journal), "NetworkAbandonTaskValidator", null);
            SetStaticField(typeof(Journal), "NetworkFailTaskValidator", null);
            SetStaticField(typeof(Journal), "NetworkSetTaskValueValidator", null);
            SetStaticField(typeof(Journal), "NetworkTrackQuestValidator", null);
            SetStaticField(typeof(Journal), "NetworkUntrackQuestValidator", null);
            SetStaticField(typeof(Journal), "NetworkUntrackAllValidator", null);

            m_Installed = false;
        }

        private bool ValidateActivateQuest(Journal journal, Quest quest)
        {
            return RouteClientAction(journal, QuestActionType.ActivateQuest, quest, -1, 0d);
        }

        private bool ValidateDeactivateQuest(Journal journal, Quest quest)
        {
            return RouteClientAction(journal, QuestActionType.DeactivateQuest, quest, -1, 0d);
        }

        private bool ValidateActivateTask(Journal journal, Quest quest, int taskId)
        {
            return RouteClientAction(journal, QuestActionType.ActivateTask, quest, taskId, 0d);
        }

        private bool ValidateDeactivateTask(Journal journal, Quest quest, int taskId)
        {
            return RouteClientAction(journal, QuestActionType.DeactivateTask, quest, taskId, 0d);
        }

        private bool ValidateCompleteTask(Journal journal, Quest quest, int taskId)
        {
            return RouteClientAction(journal, QuestActionType.CompleteTask, quest, taskId, 0d);
        }

        private bool ValidateAbandonTask(Journal journal, Quest quest, int taskId)
        {
            return RouteClientAction(journal, QuestActionType.AbandonTask, quest, taskId, 0d);
        }

        private bool ValidateFailTask(Journal journal, Quest quest, int taskId)
        {
            return RouteClientAction(journal, QuestActionType.FailTask, quest, taskId, 0d);
        }

        private bool ValidateSetTaskValue(Journal journal, Quest quest, int taskId, double value)
        {
            return RouteClientAction(journal, QuestActionType.SetTaskValue, quest, taskId, value);
        }

        private bool ValidateTrackQuest(Journal journal, Quest quest)
        {
            return RouteClientAction(journal, QuestActionType.TrackQuest, quest, -1, 0d);
        }

        private bool ValidateUntrackQuest(Journal journal, Quest quest)
        {
            return RouteClientAction(journal, QuestActionType.UntrackQuest, quest, -1, 0d);
        }

        private bool ValidateUntrackAll(Journal journal)
        {
            return RouteClientAction(journal, QuestActionType.UntrackAll, null, -1, 0d);
        }

        private bool RouteClientAction(Journal journal, QuestActionType action, Quest quest, int taskId, double value)
        {
            if (m_IsServer) return true;
            if (journal == null) return true;

            NetworkQuestsController controller = journal.GetComponent<NetworkQuestsController>();
            if (controller == null || !controller.enabled)
            {
                return true;
            }

            if (controller.IsApplyingAuthoritativeChange)
            {
                return true;
            }

            if (controller.IsRemoteClient)
            {
                return false;
            }

            controller.RequestQuestAction(action, quest, taskId, value);
            return false;
        }

        private static void SetStaticField(Type type, string fieldName, object value)
        {
            FieldInfo field = type.GetField(fieldName, STATIC_PUBLIC);
            if (field == null)
            {
                Debug.LogWarning($"[NetworkQuestsPatchHooks] Missing patched field {type.Name}.{fieldName}. GC2 update likely changed signatures.");
                return;
            }

            field.SetValue(null, value);
        }

        private static bool HasPublicStaticField(Type type, string fieldName, Type expectedFieldType)
        {
            FieldInfo field = type.GetField(fieldName, STATIC_PUBLIC);
            return field != null && expectedFieldType.IsAssignableFrom(field.FieldType);
        }
    }
}
#endif
