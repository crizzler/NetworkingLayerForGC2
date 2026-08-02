using System;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.VisualScripting;
using PurrNet;
using PurrNet.Transports;
using Event = GameCreator.Runtime.VisualScripting.Event;

namespace Arawn.GameCreator2.Networking.Transport.PurrNet
{
    [Title("On PurrNet Host Ready")]
    [Description("Executed when both the server and local client halves of a PurrNet host are connected")]

    [Category("Network/PurrNet/Session/On Host Ready")]

    [Keywords("Network", "PurrNet", "Host", "Server", "Client", "Ready", "Connected")]
    [Image(typeof(IconSignal), ColorTheme.Type.Green, typeof(OverlayTick))]
    [Serializable]
    public sealed class EventPurrNetHostReady : Event
    {
        [NonSerialized] private NetworkManager m_Source;
        [NonSerialized] private bool m_WasReady;

        protected override void OnEnable(Trigger trigger)
        {
            base.OnEnable(trigger);
            if (!PurrNetVisualScriptingSupport.TryResolveNetworkManager(
                    trigger.gameObject,
                    out m_Source))
            {
                return;
            }

            m_WasReady = IsReady(m_Source);
            m_Source.onServerConnectionState -= OnConnectionState;
            m_Source.onClientConnectionState -= OnConnectionState;
            m_Source.onServerConnectionState += OnConnectionState;
            m_Source.onClientConnectionState += OnConnectionState;
        }

        protected override void OnDisable(Trigger trigger)
        {
            if (m_Source != null)
            {
                m_Source.onServerConnectionState -= OnConnectionState;
                m_Source.onClientConnectionState -= OnConnectionState;
                m_Source = null;
            }
            m_WasReady = false;
            base.OnDisable(trigger);
        }

        private void OnConnectionState(ConnectionState state)
        {
            bool isReady = IsReady(m_Source);
            if (isReady && !m_WasReady)
            {
                PurrNetVisualScriptingSupport.DispatchNextFrame(
                    m_Trigger,
                    Self,
                    nameof(EventPurrNetHostReady));
            }
            m_WasReady = isReady;
        }

        private static bool IsReady(NetworkManager manager)
        {
            return manager != null &&
                   manager.serverState == ConnectionState.Connected &&
                   manager.clientState == ConnectionState.Connected;
        }
    }
}
