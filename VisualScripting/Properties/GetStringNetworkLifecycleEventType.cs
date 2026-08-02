using System;
using GameCreator.Runtime.Common;

namespace Arawn.GameCreator2.Networking
{
    [Title("Network Lifecycle Event Type")]
    [Description("Returns the type of the latest transport-neutral network lifecycle event")]
    [Category("Network/Lifecycle/Last Event Type")]
    [Image(typeof(IconSignal), ColorTheme.Type.Blue)]
    [Serializable]
    [HideLabelsInEditor]
    public sealed class GetStringNetworkLifecycleEventType : PropertyTypeGetString
    {
        public override string Get(Args args)
        {
            return NetworkLifecycleEvents.LastEventType.ToString();
        }

        public override string String => "Network Lifecycle Event Type";
    }
}
