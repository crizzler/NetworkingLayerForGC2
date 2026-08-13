using System;
using GameCreator.Runtime.Common;
using UnityEngine;

namespace Arawn.GameCreator2.Networking
{
    [Title("Network Action Approved")]
    [Description("Whether the current Network Action context is authority-approved")]
    [Category("Network/Actions/Approved")]
    [Image(typeof(IconCheckSolid), ColorTheme.Type.Green)]
    [Serializable]
    public sealed class GetBoolNetworkActionApproved : PropertyTypeGetBool
    {
        public override bool Get(Args args) =>
            NetworkActionContext.HasCurrent && NetworkActionContext.Current.IsApproved;
        public override string String => "Network Action Approved";
    }

    [Title("Network Action Is Snapshot")]
    [Description("Whether the current Network Action is applying a late-join snapshot")]
    [Category("Network/Actions/Is Snapshot")]
    [Image(typeof(IconSignal), ColorTheme.Type.Teal)]
    [Serializable]
    public sealed class GetBoolNetworkActionIsSnapshot : PropertyTypeGetBool
    {
        public override bool Get(Args args) =>
            NetworkActionContext.HasCurrent && NetworkActionContext.Current.IsSnapshot;
        public override string String => "Network Action Is Snapshot";
    }

    [Title("Network Action Boolean Payload")]
    [Description("Boolean payload in the current Network Action context")]
    [Category("Network/Actions/Payload Boolean")]
    [Image(typeof(IconToggleOn), ColorTheme.Type.Blue)]
    [Serializable]
    public sealed class GetBoolNetworkActionPayload : PropertyTypeGetBool
    {
        public override bool Get(Args args) => NetworkActionContext.HasCurrent &&
            NetworkActionContext.Current.Payload.Type == NetworkActionPayloadType.Boolean &&
            NetworkActionContext.Current.Payload.BooleanValue;
        public override string String => "Network Action Boolean Payload";
    }

    [Title("Network Action ID")]
    [Description("Stable ID of the Network Action currently executing")]
    [Category("Network/Actions/Action ID")]
    [Image(typeof(IconID), ColorTheme.Type.Blue)]
    [Serializable]
    public sealed class GetStringNetworkActionId : PropertyTypeGetString
    {
        public override string Get(Args args) => NetworkActionContext.HasCurrent
            ? NetworkActionContext.Current.ActionId
            : string.Empty;
        public override string String => "Network Action ID";
    }

    [Title("Network Action Rejection Reason")]
    [Description("Authority rejection reason in the current Network Action context")]
    [Category("Network/Actions/Rejection Reason")]
    [Image(typeof(IconMessage), ColorTheme.Type.Red)]
    [Serializable]
    public sealed class GetStringNetworkActionRejectReason : PropertyTypeGetString
    {
        public override string Get(Args args) => NetworkActionContext.HasCurrent
            ? NetworkActionContext.Current.RejectReason.ToString()
            : NetworkActionRejectReason.None.ToString();
        public override string String => "Network Action Rejection Reason";
    }

    [Title("Network Action String Payload")]
    [Description("String payload in the current Network Action context")]
    [Category("Network/Actions/Payload String")]
    [Image(typeof(IconString), ColorTheme.Type.Blue)]
    [Serializable]
    public sealed class GetStringNetworkActionPayload : PropertyTypeGetString
    {
        public override string Get(Args args) => NetworkActionContext.HasCurrent &&
            NetworkActionContext.Current.Payload.Type == NetworkActionPayloadType.String
                ? NetworkActionContext.Current.Payload.StringValue ?? string.Empty
                : string.Empty;
        public override string String => "Network Action String Payload";
    }

    [Title("Network Action Actor ID")]
    [Category("Network/Actions/Actor ID")]
    [Image(typeof(IconID), ColorTheme.Type.Purple)]
    [Serializable]
    public sealed class GetDecimalNetworkActionActorId : PropertyTypeGetDecimal
    {
        public override double Get(Args args) => NetworkActionContext.HasCurrent
            ? NetworkActionContext.Current.ActorNetworkId : 0d;
        public override string String => "Network Action Actor ID";
    }

    [Title("Network Action Target ID")]
    [Category("Network/Actions/Target ID")]
    [Image(typeof(IconID), ColorTheme.Type.Purple)]
    [Serializable]
    public sealed class GetDecimalNetworkActionTargetId : PropertyTypeGetDecimal
    {
        public override double Get(Args args) => NetworkActionContext.HasCurrent
            ? NetworkActionContext.Current.TargetNetworkId : 0d;
        public override string String => "Network Action Target ID";
    }

    [Title("Network Action Revision")]
    [Category("Network/Actions/Revision")]
    [Image(typeof(IconNumber), ColorTheme.Type.Teal)]
    [Serializable]
    public sealed class GetDecimalNetworkActionRevision : PropertyTypeGetDecimal
    {
        public override double Get(Args args) => NetworkActionContext.HasCurrent
            ? NetworkActionContext.Current.Revision : 0d;
        public override string String => "Network Action Revision";
    }

    [Title("Network Action Server Time")]
    [Category("Network/Actions/Server Time")]
    [Image(typeof(IconClock), ColorTheme.Type.Blue)]
    [Serializable]
    public sealed class GetDecimalNetworkActionServerTime : PropertyTypeGetDecimal
    {
        public override double Get(Args args) => NetworkActionContext.HasCurrent
            ? NetworkActionContext.Current.ServerTime : 0d;
        public override string String => "Network Action Server Time";
    }

    [Title("Network Action Number Payload")]
    [Category("Network/Actions/Payload Number")]
    [Image(typeof(IconNumber), ColorTheme.Type.Blue)]
    [Serializable]
    public sealed class GetDecimalNetworkActionPayload : PropertyTypeGetDecimal
    {
        public override double Get(Args args) => NetworkActionContext.HasCurrent &&
            NetworkActionContext.Current.Payload.Type == NetworkActionPayloadType.Number
                ? NetworkActionContext.Current.Payload.NumberValue : 0d;
        public override string String => "Network Action Number Payload";
    }

    [Title("Network Action Vector Payload")]
    [Category("Network/Actions/Payload Vector")]
    [Image(typeof(IconVector3), ColorTheme.Type.Blue)]
    [Serializable]
    public sealed class GetPositionNetworkActionPayload : PropertyTypeGetPosition
    {
        public override Vector3 Get(Args args) => NetworkActionContext.HasCurrent &&
            NetworkActionContext.Current.Payload.Type == NetworkActionPayloadType.Vector3
                ? NetworkActionContext.Current.Payload.Vector3Value : Vector3.zero;
        public override string String => "Network Action Vector Payload";
    }

    [Title("Network Action Actor")]
    [Category("Network/Actions/Actor")]
    [Image(typeof(IconPlayer), ColorTheme.Type.Green)]
    [Serializable]
    public sealed class GetGameObjectNetworkActionActor : PropertyTypeGetGameObject
    {
        public override GameObject Get(Args args) => NetworkActionContext.HasCurrent
            ? NetworkActionContext.Current.Actor : null;
        public override GameObject Get(GameObject gameObject) => NetworkActionContext.HasCurrent
            ? NetworkActionContext.Current.Actor : null;
        public override string String => "Network Action Actor";
    }

    [Title("Network Action Target")]
    [Category("Network/Actions/Target")]
    [Image(typeof(IconCubeSolid), ColorTheme.Type.Yellow)]
    [Serializable]
    public sealed class GetGameObjectNetworkActionTarget : PropertyTypeGetGameObject
    {
        public override GameObject Get(Args args) => NetworkActionContext.HasCurrent
            ? NetworkActionContext.Current.Target : null;
        public override GameObject Get(GameObject gameObject) => NetworkActionContext.HasCurrent
            ? NetworkActionContext.Current.Target : null;
        public override string String => "Network Action Target";
    }
}
