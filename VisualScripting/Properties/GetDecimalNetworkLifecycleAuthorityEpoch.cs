using System;
using GameCreator.Runtime.Common;

namespace Arawn.GameCreator2.Networking
{
    [Title("Network Lifecycle Authority Epoch")]
    [Description("Returns the epoch from the latest logical-authority-changed event")]
    [Category("Network/Lifecycle/Last Event Authority Epoch")]
    [Image(typeof(IconSignal), ColorTheme.Type.Yellow)]
    [Serializable]
    [HideLabelsInEditor]
    public sealed class GetDecimalNetworkLifecycleAuthorityEpoch : PropertyTypeGetDecimal
    {
        public override double Get(Args args)
        {
            return NetworkLifecycleEvents.LastAuthorityEpoch;
        }

        public override string String => "Network Event Authority Epoch";
    }
}
