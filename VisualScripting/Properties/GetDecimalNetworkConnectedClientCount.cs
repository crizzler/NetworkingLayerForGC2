using System;
using GameCreator.Runtime.Common;

namespace Arawn.GameCreator2.Networking
{
    [Title("Connected Network Client Count")]
    [Description("Returns the number of clients currently reported by the active transport")]

    [Category("Network/General/Connected Network Client Count")]

    [Image(typeof(IconPlayer), ColorTheme.Type.Blue)]
    [Serializable]
    [HideLabelsInEditor]
    public sealed class GetDecimalNetworkConnectedClientCount : PropertyTypeGetDecimal
    {
        public override double Get(Args args)
        {
            if (!NetworkTransportBridge.HasActive) return 0d;

            return NetworkTransportBridge.Active.ConnectedClientIds?.Count ?? 0;
        }

        public override string String => "Connected Network Client Count";
    }
}
