using System;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.VisualScripting;
using PurrNet;
using PurrNet.Transports;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Transport.PurrNet
{
    [Title("PurrNet Connection State Is")]
    [Description("Returns true when the selected PurrNet connection side has the expected native state")]

    [Category("Network/PurrNet/Connection/State Is")]

    [Parameter("Side", "Observe the PurrNet server or client connection side")]
    [Parameter("State", "Connecting, Connected, Disconnecting, or Disconnected")]

    [Keywords("Network", "PurrNet", "Connection", "State", "Server", "Client", "Connecting", "Disconnected")]
    [Image(typeof(IconSignal), ColorTheme.Type.Blue)]
    [Serializable]
    public sealed class ConditionPurrNetConnectionStateIs : Condition
    {
        [SerializeField]
        private PurrNetConnectionSide m_Side = PurrNetConnectionSide.Client;

        [SerializeField]
        private ConnectionState m_State = ConnectionState.Connected;

        protected override string Summary => $"PurrNet {m_Side} State is {m_State}";

        protected override bool Run(Args args)
        {
            if (!PurrNetVisualScriptingSupport.TryResolveNetworkManager(
                    args.Self,
                    out NetworkManager manager))
            {
                return false;
            }

            ConnectionState current = m_Side == PurrNetConnectionSide.Server
                ? manager.serverState
                : manager.clientState;
            return current == m_State;
        }
    }
}
