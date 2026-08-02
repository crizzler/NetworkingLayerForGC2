using System;
using GameCreator.Runtime.Common;

namespace Arawn.GameCreator2.Networking
{
    [Title("Local Network Client ID")]
    [Description("Returns the active transport's local client ID, or -1 when unavailable")]

    [Category("Network/General/Local Network Client ID")]

    [Image(typeof(IconID), ColorTheme.Type.Green)]
    [Serializable]
    [HideLabelsInEditor]
    public sealed class GetDecimalNetworkLocalClientId : PropertyTypeGetDecimal
    {
        private const double Unavailable = -1d;

        public override double Get(Args args)
        {
            return NetworkTransportBridge.HasActive &&
                   NetworkTransportBridge.Active.TryGetLocalClientId(out uint clientId)
                ? clientId
                : Unavailable;
        }

        public override string String => "Local Network Client ID";
    }
}
