using System;
using GameCreator.Runtime.Common;
using UnityEngine;

namespace Arawn.GameCreator2.Networking
{
    [Title("Network Interaction Approved")]
    [Description("Returns whether the latest network interaction response or broadcast was approved")]
    [Category("Network/Core/Interaction/Approved")]
    [Image(typeof(IconCheckSolid), ColorTheme.Type.Green)]
    [Serializable]
    [HideLabelsInEditor]
    public sealed class GetBoolNetworkInteractionApproved : PropertyTypeGetBool
    {
        public override bool Get(Args args) =>
            NetworkInteractionVisualScriptingSupport.Context.Approved;
        public override string String => "Network Interaction Approved";
    }

    [Title("Network Interaction Event Type")]
    [Description("Returns whether the latest interaction payload was an approval, rejection, or observer broadcast")]
    [Category("Network/Core/Interaction/Event Type")]
    [Image(typeof(IconSignal), ColorTheme.Type.Blue)]
    [Serializable]
    [HideLabelsInEditor]
    public sealed class GetStringNetworkInteractionEventType : PropertyTypeGetString
    {
        public override string Get(Args args) =>
            NetworkInteractionVisualScriptingSupport.Context.EventType.ToString();
        public override string String => "Network Interaction Event Type";
    }

    [Title("Network Interaction Type")]
    [Description("Returns the semantic type of the latest network interaction payload")]
    [Category("Network/Core/Interaction/Interaction Type")]
    [Image(typeof(IconSignal), ColorTheme.Type.Blue)]
    [Serializable]
    [HideLabelsInEditor]
    public sealed class GetStringNetworkInteractionType : PropertyTypeGetString
    {
        public override string Get(Args args) =>
            NetworkInteractionVisualScriptingSupport.Context.InteractionType.ToString();
        public override string String => "Network Interaction Type";
    }

    [Title("Network Interaction Reject Reason")]
    [Description("Returns the rejection reason of the latest network interaction response")]
    [Category("Network/Core/Interaction/Reject Reason")]
    [Image(typeof(IconMessage), ColorTheme.Type.Red)]
    [Serializable]
    [HideLabelsInEditor]
    public sealed class GetStringNetworkInteractionRejectReason : PropertyTypeGetString
    {
        public override string Get(Args args) =>
            NetworkInteractionVisualScriptingSupport.Context.RejectReason.ToString();
        public override string String => "Network Interaction Reject Reason";
    }

    [Title("Network Interaction Request ID")]
    [Description("Returns the request ID from the latest network interaction payload")]
    [Category("Network/Core/Interaction/Request ID")]
    [Image(typeof(IconID), ColorTheme.Type.Purple)]
    [Serializable]
    [HideLabelsInEditor]
    public sealed class GetDecimalNetworkInteractionRequestId : PropertyTypeGetDecimal
    {
        public override double Get(Args args) =>
            NetworkInteractionVisualScriptingSupport.Context.RequestId;
        public override string String => "Network Interaction Request ID";
    }

    [Title("Network Interaction Actor ID")]
    [Description("Returns the security actor ID from the latest network interaction payload")]
    [Category("Network/Core/Interaction/Actor ID")]
    [Image(typeof(IconID), ColorTheme.Type.Purple)]
    [Serializable]
    [HideLabelsInEditor]
    public sealed class GetDecimalNetworkInteractionActorId : PropertyTypeGetDecimal
    {
        public override double Get(Args args) =>
            NetworkInteractionVisualScriptingSupport.Context.ActorNetworkId;
        public override string String => "Network Interaction Actor ID";
    }

    [Title("Network Interaction Correlation ID")]
    [Description("Returns the correlation ID from the latest network interaction payload")]
    [Category("Network/Core/Interaction/Correlation ID")]
    [Image(typeof(IconID), ColorTheme.Type.Purple)]
    [Serializable]
    [HideLabelsInEditor]
    public sealed class GetDecimalNetworkInteractionCorrelationId : PropertyTypeGetDecimal
    {
        public override double Get(Args args) =>
            NetworkInteractionVisualScriptingSupport.Context.CorrelationId;
        public override string String => "Network Interaction Correlation ID";
    }

    [Title("Network Interaction Character ID")]
    [Description("Returns the interacting Character network ID from the latest interaction payload")]
    [Category("Network/Core/Interaction/Character ID")]
    [Image(typeof(IconID), ColorTheme.Type.Purple)]
    [Serializable]
    [HideLabelsInEditor]
    public sealed class GetDecimalNetworkInteractionCharacterId : PropertyTypeGetDecimal
    {
        public override double Get(Args args) =>
            NetworkInteractionVisualScriptingSupport.Context.CharacterNetworkId;
        public override string String => "Network Interaction Character ID";
    }

    [Title("Network Interaction Target ID")]
    [Description("Returns the target network ID from the latest interaction payload, or 0 for a stable scene target")]
    [Category("Network/Core/Interaction/Target ID")]
    [Image(typeof(IconID), ColorTheme.Type.Purple)]
    [Serializable]
    [HideLabelsInEditor]
    public sealed class GetDecimalNetworkInteractionTargetId : PropertyTypeGetDecimal
    {
        public override double Get(Args args) =>
            NetworkInteractionVisualScriptingSupport.Context.TargetNetworkId;
        public override string String => "Network Interaction Target ID";
    }

    [Title("Network Interaction Target Hash")]
    [Description("Returns the stable scene-target hash from the latest interaction payload")]
    [Category("Network/Core/Interaction/Target Hash")]
    [Image(typeof(IconID), ColorTheme.Type.Purple)]
    [Serializable]
    [HideLabelsInEditor]
    public sealed class GetDecimalNetworkInteractionTargetHash : PropertyTypeGetDecimal
    {
        public override double Get(Args args) =>
            NetworkInteractionVisualScriptingSupport.Context.TargetHash;
        public override string String => "Network Interaction Target Hash";
    }

    [Title("Network Interaction Result Data")]
    [Description("Returns the authority-provided integer result from the latest interaction payload")]
    [Category("Network/Core/Interaction/Result Data")]
    [Image(typeof(IconNumber), ColorTheme.Type.Yellow)]
    [Serializable]
    [HideLabelsInEditor]
    public sealed class GetDecimalNetworkInteractionResultData : PropertyTypeGetDecimal
    {
        public override double Get(Args args) =>
            NetworkInteractionVisualScriptingSupport.Context.ResultData;
        public override string String => "Network Interaction Result Data";
    }

    [Title("Network Interaction Server Time")]
    [Description("Returns the authority timestamp from the latest interaction broadcast, or 0 for a response")]
    [Category("Network/Core/Interaction/Server Time")]
    [Image(typeof(IconClock), ColorTheme.Type.Blue)]
    [Serializable]
    [HideLabelsInEditor]
    public sealed class GetDecimalNetworkInteractionServerTime : PropertyTypeGetDecimal
    {
        public override double Get(Args args) =>
            NetworkInteractionVisualScriptingSupport.Context.ServerTime;
        public override string String => "Network Interaction Server Time";
    }

    [Title("Network Interaction Actor")]
    [Description("Returns the locally resolved actor from the latest network interaction payload")]
    [Category("Network/Core/Interaction/Actor")]
    [Image(typeof(IconPlayer), ColorTheme.Type.Green)]
    [Serializable]
    [HideLabelsInEditor]
    public sealed class GetGameObjectNetworkInteractionActor : PropertyTypeGetGameObject
    {
        public override GameObject Get(Args args) =>
            NetworkInteractionVisualScriptingSupport.ResolveLastActorObject();

        public override GameObject Get(GameObject gameObject) =>
            NetworkInteractionVisualScriptingSupport.ResolveLastActorObject();

        public override string String => "Network Interaction Actor";
    }

    [Title("Network Interaction Target")]
    [Description("Best-effort local resolution of the target from the latest network interaction payload")]
    [Category("Network/Core/Interaction/Target")]
    [Image(typeof(IconCubeSolid), ColorTheme.Type.Yellow)]
    [Serializable]
    [HideLabelsInEditor]
    public sealed class GetGameObjectNetworkInteractionTarget : PropertyTypeGetGameObject
    {
        public override GameObject Get(Args args) =>
            NetworkInteractionVisualScriptingSupport.ResolveLastTargetObject();

        public override GameObject Get(GameObject gameObject) =>
            NetworkInteractionVisualScriptingSupport.ResolveLastTargetObject();

        public override string String => "Network Interaction Target";
    }
}
