using System;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.VisualScripting;
using Event = GameCreator.Runtime.VisualScripting.Event;

namespace Arawn.GameCreator2.Networking.Transport.PurrNet
{
    [Title("On PurrNet Steam Session Connected")]
    [Description("Executed when the PurrNet host or client is fully connected through its Steam lobby")]

    [Category("Network/PurrNet/Steam Lobby/On Session Connected")]

    [Keywords("Network", "PurrNet", "Steam", "Lobby", "Session", "Connected", "Ready")]
    [Image(typeof(IconSignal), ColorTheme.Type.Green, typeof(OverlayBolt))]
    [Serializable]
    public sealed class EventPurrNetSteamSessionConnected : Event
    {
        [NonSerialized] private PurrNetSteamLobbyNetwork m_Source;

        protected override void OnEnable(Trigger trigger)
        {
            base.OnEnable(trigger);
            if (!PurrNetVisualScriptingSupport.TryResolveSteamLobbyNetwork(
                    trigger.gameObject,
                    out m_Source))
            {
                return;
            }

            m_Source.OnSessionConnected.RemoveListener(OnConnected);
            m_Source.OnSessionConnected.AddListener(OnConnected);
        }

        protected override void OnDisable(Trigger trigger)
        {
            if (m_Source != null)
            {
                m_Source.OnSessionConnected.RemoveListener(OnConnected);
                m_Source = null;
            }
            base.OnDisable(trigger);
        }

        private void OnConnected()
        {
            PurrNetVisualScriptingSupport.DispatchNextFrame(
                m_Trigger,
                Self,
                nameof(EventPurrNetSteamSessionConnected));
        }
    }
}
