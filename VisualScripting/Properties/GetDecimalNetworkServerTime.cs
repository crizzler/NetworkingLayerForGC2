using System;
using GameCreator.Runtime.Common;

namespace Arawn.GameCreator2.Networking
{
    [Title("Network Server Time")]
    [Description("Returns the active transport's synchronized server time, or 0 when no transport is active")]

    [Category("Network/General/Network Server Time")]

    [Image(typeof(IconClock), ColorTheme.Type.Yellow)]
    [Serializable]
    [HideLabelsInEditor]
    public sealed class GetDecimalNetworkServerTime : PropertyTypeGetDecimal
    {
        public override double Get(Args args)
        {
            return NetworkTransportBridge.HasActive
                ? NetworkTransportBridge.Active.ServerTime
                : 0d;
        }

        public override string String => "Network Server Time";
    }
}
