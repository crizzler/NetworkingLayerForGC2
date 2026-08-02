using System;
using GameCreator.Runtime.Common;

namespace Arawn.GameCreator2.Networking
{
    [Title("Last Network Session Error")]
    [Description("Returns the latest session-start error reported by the active transport")]
    [Category("Network/Lifecycle/Last Session Error")]
    [Image(typeof(IconMessage), ColorTheme.Type.Red)]
    [Serializable]
    [HideLabelsInEditor]
    public sealed class GetStringNetworkLastSessionError : PropertyTypeGetString
    {
        public override string Get(Args args)
        {
            return NetworkTransportBridge.HasActive
                ? NetworkTransportBridge.Active.LastSessionError
                : string.Empty;
        }

        public override string String => "Last Network Session Error";
    }

    [Title("Last Network Session Stop Reason")]
    [Description("Returns the latest session-stop reason reported by the active transport")]
    [Category("Network/Lifecycle/Last Session Stop Reason")]
    [Image(typeof(IconMessage), ColorTheme.Type.Red)]
    [Serializable]
    [HideLabelsInEditor]
    public sealed class GetStringNetworkLastSessionStopReason : PropertyTypeGetString
    {
        public override string Get(Args args)
        {
            return NetworkTransportBridge.HasActive
                ? NetworkTransportBridge.Active.LastSessionStopReason
                : string.Empty;
        }

        public override string String => "Last Network Session Stop Reason";
    }
}
