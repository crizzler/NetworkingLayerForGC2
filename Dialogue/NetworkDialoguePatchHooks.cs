#if GC2_DIALOGUE
using System;
using System.Collections.Generic;
using System.Reflection;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.Dialogue;
using UnityEngine;
using DialogueComponent = GameCreator.Runtime.Dialogue.Dialogue;

namespace Arawn.GameCreator2.Networking.Dialogue
{
    /// <summary>
    /// Runtime installer for Dialogue patch delegates.
    /// In patched mode, client-side direct Play/Stop/Continue/Choose calls are rerouted
    /// through NetworkDialogueController request APIs.
    /// </summary>
    public class NetworkDialoguePatchHooks : NetworkSingleton<NetworkDialoguePatchHooks>
    {
        private const BindingFlags STATIC_PUBLIC = BindingFlags.Public | BindingFlags.Static;

        private bool m_IsServer;
        private bool m_Installed;

        public bool IsPatchActive => m_Installed && IsDialoguePatched();

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

        public static bool IsDialoguePatched()
        {
            Type dialogueType = typeof(DialogueComponent);
            Type storyType = typeof(Story);
            Type choiceType = typeof(NodeTypeChoice);

            return
                HasPublicStaticField(dialogueType, "NetworkPlayValidator", typeof(Func<DialogueComponent, Args, bool>)) &&
                HasPublicStaticField(dialogueType, "NetworkStopValidator", typeof(Func<DialogueComponent, bool>)) &&
                HasPublicStaticField(storyType, "NetworkContinueValidator", typeof(Func<Story, bool>)) &&
                HasPublicStaticField(choiceType, "NetworkChooseValidator", typeof(Func<NodeTypeChoice, int, bool>));
        }

        private void InstallHooks()
        {
            if (m_Installed) return;

            if (!IsDialoguePatched())
            {
                Debug.LogWarning("[NetworkDialoguePatchHooks] Dialogue runtime patch markers were not detected. Using interception mode.");
                return;
            }

            SetStaticField(typeof(DialogueComponent), "NetworkPlayValidator", new Func<DialogueComponent, Args, bool>(ValidatePlay));
            SetStaticField(typeof(DialogueComponent), "NetworkStopValidator", new Func<DialogueComponent, bool>(ValidateStop));
            SetStaticField(typeof(Story), "NetworkContinueValidator", new Func<Story, bool>(ValidateContinue));
            SetStaticField(typeof(NodeTypeChoice), "NetworkChooseValidator", new Func<NodeTypeChoice, int, bool>(ValidateChoose));

            m_Installed = true;
        }

        private void UninstallHooks()
        {
            if (!m_Installed) return;

            SetStaticField(typeof(DialogueComponent), "NetworkPlayValidator", null);
            SetStaticField(typeof(DialogueComponent), "NetworkStopValidator", null);
            SetStaticField(typeof(Story), "NetworkContinueValidator", null);
            SetStaticField(typeof(NodeTypeChoice), "NetworkChooseValidator", null);

            m_Installed = false;
        }

        private bool ValidatePlay(DialogueComponent dialogue, Args args)
        {
            NetworkDialogueController controller = ResolveController(dialogue);
            return RouteClientAction(controller, () => controller.RequestPlayFromPatch(args));
        }

        private bool ValidateStop(DialogueComponent dialogue)
        {
            NetworkDialogueController controller = ResolveController(dialogue);
            return RouteClientAction(controller, controller.RequestStopFromPatch);
        }

        private bool ValidateContinue(Story story)
        {
            NetworkDialogueController controller = ResolveControllerForStory(story);
            return RouteClientAction(controller, controller.RequestContinueFromPatch);
        }

        private bool ValidateChoose(NodeTypeChoice nodeTypeChoice, int nodeId)
        {
            NetworkDialogueController controller = ResolveControllerForChoice(nodeTypeChoice);
            return RouteClientAction(controller, () => controller.RequestChooseFromPatch(nodeId));
        }

        private bool RouteClientAction(NetworkDialogueController controller, Action requestAction)
        {
            if (m_IsServer) return true;

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

            requestAction?.Invoke();
            return false;
        }

        private static NetworkDialogueController ResolveController(DialogueComponent dialogue)
        {
            return NetworkDialogueController.ResolveForDialogueComponent(dialogue);
        }

        private static NetworkDialogueController ResolveControllerForStory(Story story)
        {
            if (story == null) return null;

            DialogueComponent currentDialogue = DialogueComponent.Current;
            NetworkDialogueController currentController = ResolveController(currentDialogue);
            if (currentController != null && currentController.enabled &&
                ReferenceEquals(currentController.DialogueComponent?.Story, story))
            {
                return currentController;
            }

            NetworkDialogueController[] controllers =
                FindObjectsByType<NetworkDialogueController>(FindObjectsSortMode.None);

            for (int i = 0; i < controllers.Length; i++)
            {
                NetworkDialogueController controller = controllers[i];
                if (controller == null || !controller.enabled) continue;

                if (ReferenceEquals(controller.DialogueComponent?.Story, story))
                {
                    return controller;
                }
            }

            return null;
        }

        private static NetworkDialogueController ResolveControllerForChoice(NodeTypeChoice nodeTypeChoice)
        {
            if (nodeTypeChoice == null) return null;

            DialogueComponent currentDialogue = DialogueComponent.Current;
            NetworkDialogueController currentController = ResolveController(currentDialogue);
            if (ContainsChoiceNodeType(currentController, nodeTypeChoice))
            {
                return currentController;
            }

            NetworkDialogueController[] controllers =
                FindObjectsByType<NetworkDialogueController>(FindObjectsSortMode.None);

            for (int i = 0; i < controllers.Length; i++)
            {
                NetworkDialogueController controller = controllers[i];
                if (!ContainsChoiceNodeType(controller, nodeTypeChoice)) continue;
                return controller;
            }

            return null;
        }

        private static bool ContainsChoiceNodeType(NetworkDialogueController controller, NodeTypeChoice nodeTypeChoice)
        {
            if (controller == null || !controller.enabled || nodeTypeChoice == null)
            {
                return false;
            }

            DialogueComponent dialogue = controller.DialogueComponent;
            Content content = dialogue?.Story?.Content;
            if (content == null) return false;

            foreach (KeyValuePair<int, TreeNode> entry in content.Nodes)
            {
                Node node = content.Get(entry.Key);
                if (ReferenceEquals(node?.NodeType, nodeTypeChoice))
                {
                    return true;
                }
            }

            return false;
        }

        private static void SetStaticField(Type type, string fieldName, object value)
        {
            FieldInfo field = type.GetField(fieldName, STATIC_PUBLIC);
            if (field == null)
            {
                Debug.LogWarning($"[NetworkDialoguePatchHooks] Missing patched field {type.Name}.{fieldName}. GC2 update likely changed signatures.");
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
