#if GC2_DIALOGUE
using GameCreator.Runtime.Common;
using GameCreator.Runtime.Dialogue;
using UnityEngine;
using GC2Dialogue = GameCreator.Runtime.Dialogue.Dialogue;

namespace Arawn.GameCreator2.Networking.Dialogue
{
    internal static class NetworkDialogueInstructionUtility
    {
        public static bool TryGetController(
            PropertyGetGameObject dialogueProperty,
            Args args,
            string instructionName,
            out NetworkDialogueController controller)
        {
            return TryGetController(
                dialogueProperty,
                null,
                args,
                instructionName,
                out controller,
                out _);
        }

        public static bool TryGetController(
            PropertyGetGameObject dialogueProperty,
            PropertyGetGameObject actorProperty,
            Args args,
            string instructionName,
            out NetworkDialogueController controller,
            out uint actorNetworkId)
        {
            controller = null;
            actorNetworkId = ResolveActorNetworkId(actorProperty, args);

            GC2Dialogue dialogue = dialogueProperty.Get<GC2Dialogue>(args);
            if (dialogue != null)
            {
                controller = NetworkDialogueController.ResolveForDialogueComponent(dialogue);
            }

            if (controller == null)
            {
                GameObject gameObject = dialogueProperty.Get(args);
                if (gameObject != null)
                {
                    controller = gameObject.GetComponent<NetworkDialogueController>();
                }
            }

            if (controller != null) return true;

            Debug.LogWarning($"[{instructionName}] Missing NetworkDialogueController for dialogue target");
            return false;
        }

        private static uint ResolveActorNetworkId(PropertyGetGameObject actorProperty, Args args)
        {
            GameObject actor = actorProperty != null ? actorProperty.Get(args) : null;
            uint actorNetworkId = ExtractNetworkCharacterId(actor);
            if (actorNetworkId != 0) return actorNetworkId;

            actorNetworkId = ExtractNetworkCharacterId(ShortcutPlayer.Instance != null
                ? ShortcutPlayer.Instance.gameObject
                : null);
            return actorNetworkId;
        }

        private static uint ExtractNetworkCharacterId(GameObject gameObject)
        {
            if (gameObject == null) return 0;

            NetworkCharacter networkCharacter = gameObject.GetComponent<NetworkCharacter>();
            if (networkCharacter == null)
            {
                networkCharacter = gameObject.GetComponentInParent<NetworkCharacter>();
            }

            return networkCharacter != null ? networkCharacter.NetworkId : 0;
        }
    }
}
#endif
