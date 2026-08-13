using System;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.VisualScripting;

namespace Arawn.GameCreator2.Networking
{
    [Title("Network Interaction Type Is")]
    [Description("Compares the semantic type of the latest network interaction response or broadcast")]
    [Category("Network/Core/Interaction/Type Is")]
    [Keywords("Network", "Core", "Interaction", "Type", "Open", "Close", "Use")]
    [Image(typeof(IconSignal), ColorTheme.Type.Blue)]
    [Serializable]
    public sealed class ConditionNetworkInteractionTypeIs : Condition
    {
        [UnityEngine.SerializeField]
        private InteractionType m_InteractionType = InteractionType.Use;

        protected override string Summary => $"Network Interaction is {m_InteractionType}";

        protected override bool Run(Args args)
        {
            NetworkInteractionExecutionContext context =
                NetworkInteractionVisualScriptingSupport.Context;
            return context.EventType != NetworkInteractionEventType.None &&
                   context.InteractionType == m_InteractionType;
        }
    }

    [Title("Network Interaction Reject Reason Is")]
    [Description("Compares the rejection reason of the latest rejected network interaction")]
    [Category("Network/Core/Interaction/Reject Reason Is")]
    [Keywords("Network", "Core", "Interaction", "Rejected", "Reason", "Denied")]
    [Image(typeof(IconSignal), ColorTheme.Type.Red)]
    [Serializable]
    public sealed class ConditionNetworkInteractionRejectReasonIs : Condition
    {
        [UnityEngine.SerializeField]
        private InteractionRejectReason m_RejectReason = InteractionRejectReason.None;

        protected override string Summary => $"Network Interaction reject is {m_RejectReason}";

        protected override bool Run(Args args)
        {
            NetworkInteractionExecutionContext context =
                NetworkInteractionVisualScriptingSupport.Context;
            return context.EventType == NetworkInteractionEventType.Rejected &&
                   context.RejectReason == m_RejectReason;
        }
    }
}
