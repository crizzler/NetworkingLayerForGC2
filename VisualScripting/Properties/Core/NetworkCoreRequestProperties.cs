using System;
using GameCreator.Runtime.Common;

namespace Arawn.GameCreator2.Networking
{
    [Title("Network Core Request Has Result")]
    [Description("Returns true after a Core visual-scripting request has completed")]
    [Category("Network/Core/Request/Has Result")]
    [Image(typeof(IconSignal), ColorTheme.Type.Blue)]
    [Serializable]
    [HideLabelsInEditor]
    public sealed class GetBoolNetworkCoreRequestHasResult : PropertyTypeGetBool
    {
        public override bool Get(Args args) =>
            NetworkCoreVisualScriptingContext.HasCurrent ||
            NetworkCoreVisualScriptingContext.HasResult;
        public override string String => "Network Core Request Has Result";
    }

    [Title("Network Core Request Approved")]
    [Description("Returns whether the latest completed Core visual-scripting request was approved")]
    [Category("Network/Core/Request/Approved")]
    [Image(typeof(IconCheckSolid), ColorTheme.Type.Green)]
    [Serializable]
    [HideLabelsInEditor]
    public sealed class GetBoolNetworkCoreRequestApproved : PropertyTypeGetBool
    {
        public override bool Get(Args args)
        {
            return (NetworkCoreVisualScriptingContext.HasCurrent ||
                    NetworkCoreVisualScriptingContext.HasResult) &&
                   NetworkCoreVisualScriptingContext.CurrentResult.Approved;
        }

        public override string String => "Network Core Request Approved";
    }

    [Title("Network Core Poise Is Broken")]
    [Description("Returns the poise-broken value from the latest completed Core poise request")]
    [Category("Network/Core/Request/Poise Is Broken")]
    [Image(typeof(IconHeartBeat), ColorTheme.Type.Red)]
    [Serializable]
    [HideLabelsInEditor]
    public sealed class GetBoolNetworkCorePoiseIsBroken : PropertyTypeGetBool
    {
        public override bool Get(Args args)
        {
            return (NetworkCoreVisualScriptingContext.HasCurrent ||
                    NetworkCoreVisualScriptingContext.HasResult) &&
                   NetworkCoreVisualScriptingContext.CurrentResult.IsPoiseBroken;
        }

        public override string String => "Network Core Poise Is Broken";
    }

    [Title("Network Core Request Operation")]
    [Description("Returns the operation name of the latest Core visual-scripting request")]
    [Category("Network/Core/Request/Operation")]
    [Image(typeof(IconInstructions), ColorTheme.Type.Blue)]
    [Serializable]
    [HideLabelsInEditor]
    public sealed class GetStringNetworkCoreRequestOperation : PropertyTypeGetString
    {
        public override string Get(Args args)
        {
            return NetworkCoreVisualScriptingContext.CurrentResult.Operation.ToString();
        }

        public override string String => "Network Core Request Operation";
    }

    [Title("Network Core Request Reject Reason")]
    [Description("Returns the rejection reason from the latest completed Core visual-scripting request")]
    [Category("Network/Core/Request/Reject Reason")]
    [Image(typeof(IconMessage), ColorTheme.Type.Red)]
    [Serializable]
    [HideLabelsInEditor]
    public sealed class GetStringNetworkCoreRequestRejectReason : PropertyTypeGetString
    {
        public override string Get(Args args)
        {
            return NetworkCoreVisualScriptingContext.HasCurrent ||
                   NetworkCoreVisualScriptingContext.HasResult
                ? NetworkCoreVisualScriptingContext.CurrentResult.RejectReason
                : string.Empty;
        }

        public override string String => "Network Core Request Reject Reason";
    }

    [Title("Network Core Request Character ID")]
    [Description("Returns the Character network ID targeted by the latest Core visual-scripting request")]
    [Category("Network/Core/Request/Character ID")]
    [Image(typeof(IconID), ColorTheme.Type.Purple)]
    [Serializable]
    [HideLabelsInEditor]
    public sealed class GetDecimalNetworkCoreRequestCharacterId : PropertyTypeGetDecimal
    {
        public override double Get(Args args)
        {
            return NetworkCoreVisualScriptingContext.CurrentResult.CharacterNetworkId;
        }

        public override string String => "Network Core Request Character ID";
    }

    [Title("Network Core Prop Instance ID")]
    [Description("Returns the server-assigned prop instance ID from the latest completed Core prop request")]
    [Category("Network/Core/Request/Prop Instance ID")]
    [Image(typeof(IconTennis), ColorTheme.Type.Yellow)]
    [Serializable]
    [HideLabelsInEditor]
    public sealed class GetDecimalNetworkCorePropInstanceId : PropertyTypeGetDecimal
    {
        public override double Get(Args args)
        {
            return NetworkCoreVisualScriptingContext.HasCurrent ||
                   NetworkCoreVisualScriptingContext.HasResult
                ? NetworkCoreVisualScriptingContext.CurrentResult.PropInstanceId
                : 0d;
        }

        public override string String => "Network Core Prop Instance ID";
    }

    [Title("Network Core Current Poise")]
    [Description("Returns the authoritative poise value from the latest completed Core poise request")]
    [Category("Network/Core/Request/Current Poise")]
    [Image(typeof(IconHeartBeat), ColorTheme.Type.Yellow)]
    [Serializable]
    [HideLabelsInEditor]
    public sealed class GetDecimalNetworkCoreCurrentPoise : PropertyTypeGetDecimal
    {
        public override double Get(Args args)
        {
            return NetworkCoreVisualScriptingContext.HasCurrent ||
                   NetworkCoreVisualScriptingContext.HasResult
                ? NetworkCoreVisualScriptingContext.CurrentResult.CurrentPoise
                : 0d;
        }

        public override string String => "Network Core Current Poise";
    }

    [Title("Network Core Approved Invincibility Duration")]
    [Description("Returns the server-approved duration from the latest completed Core invincibility request")]
    [Category("Network/Core/Request/Approved Invincibility Duration")]
    [Image(typeof(IconShieldSolid), ColorTheme.Type.Blue)]
    [Serializable]
    [HideLabelsInEditor]
    public sealed class GetDecimalNetworkCoreApprovedInvincibilityDuration : PropertyTypeGetDecimal
    {
        public override double Get(Args args)
        {
            return NetworkCoreVisualScriptingContext.HasCurrent ||
                   NetworkCoreVisualScriptingContext.HasResult
                ? NetworkCoreVisualScriptingContext.CurrentResult.ApprovedInvincibilityDuration
                : 0d;
        }

        public override string String => "Network Core Approved Invincibility Duration";
    }
}
