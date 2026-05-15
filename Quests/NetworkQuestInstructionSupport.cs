#if GC2_QUESTS
using GameCreator.Runtime.Common;
using GameCreator.Runtime.Quests;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Quests
{
    public static class NetworkQuestInstructionUtility
    {
        public static bool TryGetController(
            PropertyGetGameObject journal,
            Args args,
            string instructionName,
            out NetworkQuestsController controller)
        {
            controller = null;
            GameObject gameObject = journal.Get(args);
            if (gameObject == null)
            {
                Debug.LogWarning($"[{instructionName}] Journal GameObject resolved to null.");
                return false;
            }

            controller = gameObject.GetComponent<NetworkQuestsController>();
            if (controller == null)
            {
                Debug.LogWarning($"[{instructionName}] '{gameObject.name}' has no NetworkQuestsController.");
                return false;
            }

            return true;
        }

        public static int GetTaskId(PickTask task)
        {
            return task != null ? task.TaskId : TasksTree.NODE_INVALID;
        }
    }
}
#endif
