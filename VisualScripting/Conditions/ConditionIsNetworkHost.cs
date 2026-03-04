using System;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.VisualScripting;
using UnityEngine;

namespace Arawn.GameCreator2.Networking
{
    /// <summary>
    /// Returns true if this instance is running as both server and client (host mode).
    /// </summary>
    [Title("Is Network Host")]
    [Description("Returns true if this instance is running as a host (server + client)")]

    [Category("Network/General/Is Network Host")]

    [Keywords("Network", "Host", "Server", "Client", "Listen")]

    [Image(typeof(IconSignal), ColorTheme.Type.Yellow)]
    [Serializable]
    public class ConditionIsNetworkHost : Condition
    {
        // PROPERTIES: ----------------------------------------------------------------------------

        protected override string Summary => "is Network Host";

        // RUN METHOD: ----------------------------------------------------------------------------

        protected override bool Run(Args args)
        {
#if UNITY_NETCODE
            var nm = Unity.Netcode.NetworkManager.Singleton;
            if (nm != null)
                return nm.IsHost;
#endif

            return false;
        }
    }
}
