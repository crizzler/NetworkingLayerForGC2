using System;
using GameCreator.Runtime.Common;

namespace Arawn.GameCreator2.Networking
{
    [Title("Network Lifecycle Is Authority")]
    [Description("Returns the authority value from the latest logical-authority-changed event")]
    [Category("Network/Lifecycle/Last Event Is Authority")]
    [Image(typeof(IconSignal), ColorTheme.Type.Yellow)]
    [Serializable]
    [HideLabelsInEditor]
    public sealed class GetBoolNetworkLifecycleAuthority : PropertyTypeGetBool
    {
        public override bool Get(Args args)
        {
            return NetworkLifecycleEvents.LastLogicalAuthority;
        }

        public override string String => "Network Event Is Authority";
    }
}
