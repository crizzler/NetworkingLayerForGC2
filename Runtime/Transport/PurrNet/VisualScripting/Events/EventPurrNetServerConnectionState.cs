using System;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.VisualScripting;
using PurrNet;
using PurrNet.Transports;
using UnityEngine;
using Event = GameCreator.Runtime.VisualScripting.Event;

namespace Arawn.GameCreator2.Networking.Transport.PurrNet
{
    [Title("On PurrNet Server Connection State")]
    [Description("Executed when PurrNet's server side enters the selected native connection state")]

    [Category("Network/PurrNet/Connection/On Server State")]

    [Parameter("State", "Connecting, Connected, Disconnecting, or Disconnected")]

    [Keywords("Network", "PurrNet", "Server", "Connection", "State", "Connected", "Disconnected")]
    [Image(typeof(IconSignal), ColorTheme.Type.Blue, typeof(OverlayBolt))]
    [Serializable]
    public sealed class EventPurrNetServerConnectionState : Event
    {
        [SerializeField]
        private ConnectionState m_State = ConnectionState.Connected;

        [NonSerialized]
        private NetworkManager m_Source;

        protected override void OnEnable(Trigger trigger)
        {
            base.OnEnable(trigger);
            if (!PurrNetVisualScriptingSupport.TryResolveNetworkManager(
                    trigger.gameObject,
                    out m_Source))
            {
                return;
            }

            m_Source.onServerConnectionState -= OnConnectionState;
            m_Source.onServerConnectionState += OnConnectionState;
        }

        protected override void OnDisable(Trigger trigger)
        {
            if (m_Source != null)
            {
                m_Source.onServerConnectionState -= OnConnectionState;
                m_Source = null;
            }
            base.OnDisable(trigger);
        }

        private void OnConnectionState(ConnectionState state)
        {
            if (state != m_State) return;
            PurrNetVisualScriptingSupport.DispatchNextFrame(
                m_Trigger,
                Self,
                nameof(EventPurrNetServerConnectionState));
        }
    }
}
