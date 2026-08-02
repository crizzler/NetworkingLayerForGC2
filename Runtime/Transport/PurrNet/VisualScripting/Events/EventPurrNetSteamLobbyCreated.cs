using System;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.VisualScripting;
using Event = GameCreator.Runtime.VisualScripting.Event;

namespace Arawn.GameCreator2.Networking.Transport.PurrNet
{
    [Title("On PurrNet Steam Lobby Created")]
    [Description("Executed after Steam creates the lobby, before the PurrNet host finishes connecting")]

    [Category("Network/PurrNet/Steam Lobby/On Lobby Created")]

    [Keywords("Network", "PurrNet", "Steam", "Lobby", "Created", "Host")]
    [Image(typeof(IconBust), ColorTheme.Type.Green, typeof(OverlayPlus))]
    [Serializable]
    public sealed class EventPurrNetSteamLobbyCreated : Event
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

            m_Source.OnLobbyCreated.RemoveListener(OnCreated);
            m_Source.OnLobbyCreated.AddListener(OnCreated);
        }

        protected override void OnDisable(Trigger trigger)
        {
            if (m_Source != null)
            {
                m_Source.OnLobbyCreated.RemoveListener(OnCreated);
                m_Source = null;
            }
            base.OnDisable(trigger);
        }

        private void OnCreated()
        {
            PurrNetVisualScriptingSupport.DispatchNextFrame(
                m_Trigger,
                Self,
                nameof(EventPurrNetSteamLobbyCreated));
        }
    }
}
