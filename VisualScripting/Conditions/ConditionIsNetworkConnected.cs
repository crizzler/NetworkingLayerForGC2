using System;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.VisualScripting;
using UnityEngine;

namespace Arawn.GameCreator2.Networking
{
    /// <summary>
    /// Returns true if this instance is connected to a network session
    /// (as server, host, or client).
    /// </summary>
    [Title("Is Network Connected")]
    [Description("Returns true if currently connected to a network session")]

    [Category("Network/General/Is Network Connected")]

    [Keywords("Network", "Connected", "Online", "Session", "Active")]

    [Image(typeof(IconSignal), ColorTheme.Type.Blue)]
    [Serializable]
    public class ConditionIsNetworkConnected : Condition
    {
        // PROPERTIES: ----------------------------------------------------------------------------

        protected override string Summary => "is Network Connected";

        // RUN METHOD: ----------------------------------------------------------------------------

        protected override bool Run(Args args)
        {
            if (!NetworkTransportBridge.HasActive) return false;

            var bridge = NetworkTransportBridge.Active;
            return bridge.IsServer || bridge.IsClient;
        }
    }
}
