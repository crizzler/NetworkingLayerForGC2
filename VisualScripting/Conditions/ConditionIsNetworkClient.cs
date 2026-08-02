using System;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.VisualScripting;
using UnityEngine;

namespace Arawn.GameCreator2.Networking
{
    [Title("Is Network Client")]
    [Description("Returns true if this instance is connected as a network client, including host clients")]

    [Category("Network/General/Is Network Client")]

    [Keywords("Network", "Client", "Connected", "Player", "Peer")]
    [Image(typeof(IconSignal), ColorTheme.Type.Blue)]

    [Serializable]
    public sealed class ConditionIsNetworkClient : Condition
    {
        protected override string Summary => "is Network Client";

        protected override bool Run(Args args)
        {
            return NetworkTransportBridge.HasActive &&
                   NetworkTransportBridge.Active.IsClient;
        }
    }
}
