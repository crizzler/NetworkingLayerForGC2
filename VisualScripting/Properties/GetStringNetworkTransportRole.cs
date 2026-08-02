using System;
using GameCreator.Runtime.Common;

namespace Arawn.GameCreator2.Networking
{
    [Title("Network Transport Role")]
    [Description("Returns the role currently reported by the active network transport")]

    [Category("Network/General/Network Transport Role")]

    [Image(typeof(IconSignal), ColorTheme.Type.Blue)]
    [Serializable]
    [HideLabelsInEditor]
    public sealed class GetStringNetworkTransportRole : PropertyTypeGetString
    {
        public override string Get(Args args)
        {
            return NetworkTransportBridge.HasActive
                ? NetworkTransportBridge.Active.Role.ToString()
                : NetworkTransportRole.Offline.ToString();
        }

        public override string String => "Network Transport Role";
    }
}
