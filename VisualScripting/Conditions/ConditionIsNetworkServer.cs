using System;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.VisualScripting;
using UnityEngine;

namespace Arawn.GameCreator2.Networking
{
    /// <summary>
    /// Returns true if this instance is running as a server (dedicated or host).
    /// </summary>
    [Title("Is Network Server")]
    [Description("Returns true if this instance is running as a server (including host)")]

    [Category("Network/General/Is Network Server")]

    [Keywords("Network", "Server", "Dedicated", "Host", "Authority")]

    [Image(typeof(IconSignal), ColorTheme.Type.Green)]
    [Serializable]
    public class ConditionIsNetworkServer : Condition
    {
        // PROPERTIES: ----------------------------------------------------------------------------

        protected override string Summary => "is Network Server";

        // RUN METHOD: ----------------------------------------------------------------------------

        protected override bool Run(Args args)
        {
            // Check via NetworkCharacter first (network-agnostic)
            if (args.Self != null)
            {
                var netChar = args.Self.Get<NetworkCharacter>();
                if (netChar != null)
                    return netChar.IsServerInstance;
            }

            // Fallback: check Netcode NetworkManager directly
#if UNITY_NETCODE
            var nm = Unity.Netcode.NetworkManager.Singleton;
            if (nm != null)
                return nm.IsServer;
#endif

            return false;
        }
    }
}
