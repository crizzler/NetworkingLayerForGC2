#if GC2_DIALOGUE
using System;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.Dialogue;
using GameCreator.Runtime.VisualScripting;
using UnityEngine;
using SystemTask = System.Threading.Tasks.Task;

namespace Arawn.GameCreator2.Networking.Dialogue
{
    [Version(0, 1, 0)]
    [Title("Network Play Dialogue")]
    [Description("Requests a server-authoritative Dialogue play on a networked Dialogue controller")]
    [Category("Network/Dialogue/Play Dialogue")]
    [Parameter("Dialogue", "GameObject with Dialogue and NetworkDialogueController components")]
    [Keywords("Network", "Dialogue", "Play", "Conversation")]
    [Image(typeof(IconNodeText), ColorTheme.Type.Blue)]
    [Serializable]
    public sealed class InstructionNetworkDialoguePlay : Instruction
    {
        [SerializeField] private PropertyGetGameObject m_Dialogue = GetGameObjectDialogue.Create();
        [SerializeField] private PropertyGetGameObject m_Actor = GetGameObjectPlayer.Create();

        public override string Title => $"Network Play {m_Dialogue}";

        protected override SystemTask Run(Args args)
        {
            if (NetworkDialogueInstructionUtility.TryGetController(
                    m_Dialogue,
                    m_Actor,
                    args,
                    nameof(InstructionNetworkDialoguePlay),
                    out var controller,
                    out uint actorNetworkId))
            {
                controller.RequestPlay(args, actorNetworkId);
            }

            return SystemTask.CompletedTask;
        }
    }

    [Version(0, 1, 0)]
    [Title("Network Stop Dialogue")]
    [Description("Requests a server-authoritative Dialogue stop on a networked Dialogue controller")]
    [Category("Network/Dialogue/Stop Dialogue")]
    [Parameter("Dialogue", "GameObject with Dialogue and NetworkDialogueController components")]
    [Keywords("Network", "Dialogue", "Stop", "Conversation")]
    [Image(typeof(IconNodeText), ColorTheme.Type.Red, typeof(OverlayCross))]
    [Serializable]
    public sealed class InstructionNetworkDialogueStop : Instruction
    {
        [SerializeField] private PropertyGetGameObject m_Dialogue = GetGameObjectDialogue.Create();
        [SerializeField] private PropertyGetGameObject m_Actor = GetGameObjectPlayer.Create();

        public override string Title => $"Network Stop {m_Dialogue}";

        protected override SystemTask Run(Args args)
        {
            if (NetworkDialogueInstructionUtility.TryGetController(
                    m_Dialogue,
                    m_Actor,
                    args,
                    nameof(InstructionNetworkDialogueStop),
                    out var controller,
                    out uint actorNetworkId))
            {
                controller.RequestStop(actorNetworkId);
            }

            return SystemTask.CompletedTask;
        }
    }

    [Version(0, 1, 0)]
    [Title("Network Continue Dialogue")]
    [Description("Requests a server-authoritative Dialogue continue/skip on a networked Dialogue controller")]
    [Category("Network/Dialogue/Continue Dialogue")]
    [Parameter("Dialogue", "GameObject with Dialogue and NetworkDialogueController components")]
    [Keywords("Network", "Dialogue", "Continue", "Skip", "Conversation")]
    [Image(typeof(IconNodeText), ColorTheme.Type.Blue, typeof(OverlayArrowRight))]
    [Serializable]
    public sealed class InstructionNetworkDialogueContinue : Instruction
    {
        [SerializeField] private PropertyGetGameObject m_Dialogue = GetGameObjectDialogue.Create();
        [SerializeField] private PropertyGetGameObject m_Actor = GetGameObjectPlayer.Create();

        public override string Title => $"Network Continue {m_Dialogue}";

        protected override SystemTask Run(Args args)
        {
            if (NetworkDialogueInstructionUtility.TryGetController(
                    m_Dialogue,
                    m_Actor,
                    args,
                    nameof(InstructionNetworkDialogueContinue),
                    out var controller,
                    out uint actorNetworkId))
            {
                controller.RequestContinue(actorNetworkId);
            }

            return SystemTask.CompletedTask;
        }
    }

    [Version(0, 1, 0)]
    [Title("Network Dialogue Choice Index")]
    [Description("Requests a server-authoritative Dialogue choice by visible choice index")]
    [Category("Network/Dialogue/Choice Index")]
    [Parameter("Dialogue", "GameObject with Dialogue and NetworkDialogueController components")]
    [Parameter("Index", "The visible choice index, starting at 1")]
    [Keywords("Network", "Dialogue", "Choice", "Index", "Conversation")]
    [Image(typeof(IconNodeChoice), ColorTheme.Type.Blue)]
    [Serializable]
    public sealed class InstructionNetworkDialogueChoiceIndex : Instruction
    {
        [SerializeField] private PropertyGetGameObject m_Dialogue = GetGameObjectDialogue.Create();
        [SerializeField] private PropertyGetGameObject m_Actor = GetGameObjectPlayer.Create();
        [SerializeField] private PropertyGetInteger m_Index = GetDecimalInteger.Create(1);

        public override string Title => $"Network Choice Index {m_Index}";

        protected override SystemTask Run(Args args)
        {
            if (NetworkDialogueInstructionUtility.TryGetController(
                    m_Dialogue,
                    m_Actor,
                    args,
                    nameof(InstructionNetworkDialogueChoiceIndex),
                    out var controller,
                    out uint actorNetworkId))
            {
                controller.RequestChooseIndex((int)m_Index.Get(args), actorNetworkId);
            }

            return SystemTask.CompletedTask;
        }
    }
}
#endif
