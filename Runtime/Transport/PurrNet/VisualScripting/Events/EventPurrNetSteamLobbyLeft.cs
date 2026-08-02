using System;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.VisualScripting;
using Event = GameCreator.Runtime.VisualScripting.Event;

namespace Arawn.GameCreator2.Networking.Transport.PurrNet
{
    [Title("On PurrNet Steam Lobby Left")]
    [Description("Executed after PurrNet and the Steam lobby are left explicitly or rolled back after a session failure")]

    [Category("Network/PurrNet/Steam Lobby/On Lobby Left")]

    [Keywords("Network", "PurrNet", "Steam", "Lobby", "Left", "Leave", "Disconnected")]
    [Image(typeof(IconExit), ColorTheme.Type.Red)]
    [Serializable]
    public sealed class EventPurrNetSteamLobbyLeft : Event
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

            m_Source.OnDisconnected.RemoveListener(OnLeft);
            m_Source.OnDisconnected.AddListener(OnLeft);
        }

        protected override void OnDisable(Trigger trigger)
        {
            if (m_Source != null)
            {
                m_Source.OnDisconnected.RemoveListener(OnLeft);
                m_Source = null;
            }
            base.OnDisable(trigger);
        }

        private void OnLeft()
        {
            PurrNetVisualScriptingSupport.DispatchNextFrame(
                m_Trigger,
                Self,
                nameof(EventPurrNetSteamLobbyLeft));
        }
    }
}
