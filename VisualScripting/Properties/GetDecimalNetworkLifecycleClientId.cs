using System;
using GameCreator.Runtime.Common;

namespace Arawn.GameCreator2.Networking
{
    [Title("Network Lifecycle Client ID")]
    [Description("Returns the client ID from the latest client-connected or client-disconnected network event")]
    [Category("Network/Lifecycle/Last Event Client ID")]
    [Image(typeof(IconID), ColorTheme.Type.Blue)]
    [Serializable]
    [HideLabelsInEditor]
    public sealed class GetDecimalNetworkLifecycleClientId : PropertyTypeGetDecimal
    {
        public override double Get(Args args)
        {
            return NetworkLifecycleEvents.HasLastClientId
                ? NetworkLifecycleEvents.LastClientId
                : -1d;
        }

        public override string String => "Network Event Client ID";
    }
}
