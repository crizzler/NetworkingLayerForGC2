using System;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.VisualScripting;
using Event = GameCreator.Runtime.VisualScripting.Event;

namespace Arawn.GameCreator2.Networking.Transport.PurrNet
{
    [Title("On PurrNet Steam Lobby Error")]
    [Description("Executed when the PurrNet Steam lobby coordinator reports a fatal or non-fatal error")]

    [Category("Network/PurrNet/Steam Lobby/On Error")]

    [Keywords("Network", "PurrNet", "Steam", "Lobby", "Error", "Failed", "Warning")]
    [Image(typeof(IconMessage), ColorTheme.Type.Red, typeof(OverlayCross))]
    [Serializable]
    public sealed class EventPurrNetSteamLobbyError : Event
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

            m_Source.Error -= OnError;
            m_Source.Error += OnError;
        }

        protected override void OnDisable(Trigger trigger)
        {
            if (m_Source != null)
            {
                m_Source.Error -= OnError;
                m_Source = null;
            }
            base.OnDisable(trigger);
        }

        private void OnError(string message)
        {
            PurrNetVisualScriptingSupport.DispatchNextFrame(
                m_Trigger,
                Self,
                nameof(EventPurrNetSteamLobbyError));
        }
    }
}
