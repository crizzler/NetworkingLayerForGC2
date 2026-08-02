using System;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.VisualScripting;
using Event = GameCreator.Runtime.VisualScripting.Event;

namespace Arawn.GameCreator2.Networking.Transport.PurrNet
{
    [Title("On PurrNet Steam Ready")]
    [Description("Executed when the Steam provider used by PurrNet Steam Lobby Network finishes initialization")]

    [Category("Network/PurrNet/Steam Lobby/On Steam Ready")]

    [Keywords("Network", "PurrNet", "Steam", "Lobby", "Ready", "Initialized")]
    [Image(typeof(IconSignal), ColorTheme.Type.Green, typeof(OverlayTick))]
    [Serializable]
    public sealed class EventPurrNetSteamReady : Event
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

            m_Source.OnSteamReady.RemoveListener(OnReady);
            m_Source.OnSteamReady.AddListener(OnReady);
        }

        protected override void OnDisable(Trigger trigger)
        {
            if (m_Source != null)
            {
                m_Source.OnSteamReady.RemoveListener(OnReady);
                m_Source = null;
            }
            base.OnDisable(trigger);
        }

        private void OnReady()
        {
            PurrNetVisualScriptingSupport.DispatchNextFrame(
                m_Trigger,
                Self,
                nameof(EventPurrNetSteamReady));
        }
    }
}
