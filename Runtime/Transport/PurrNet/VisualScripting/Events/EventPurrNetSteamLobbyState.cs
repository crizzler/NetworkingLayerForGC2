using System;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.VisualScripting;
using UnityEngine;
using Event = GameCreator.Runtime.VisualScripting.Event;

namespace Arawn.GameCreator2.Networking.Transport.PurrNet
{
    [Title("On PurrNet Steam Lobby State")]
    [Description("Executed once when the PurrNet Steam lobby coordinator enters the selected state")]

    [Category("Network/PurrNet/Steam Lobby/On State")]

    [Parameter("State", "The PurrNet Steam lobby state that starts this Trigger")]

    [Keywords("Network", "PurrNet", "Steam", "Lobby", "State", "Changed", "Hosting", "Connected", "Error")]
    [Image(typeof(IconSignal), ColorTheme.Type.Blue, typeof(OverlayBolt))]
    [Serializable]
    public sealed class EventPurrNetSteamLobbyState : Event
    {
        [SerializeField]
        private PurrNetSteamLobbySessionState m_State =
            PurrNetSteamLobbySessionState.Connected;

        [NonSerialized] private PurrNetSteamLobbyNetwork m_Source;
        [NonSerialized] private PurrNetSteamLobbySessionState m_LastState;

        protected override void OnEnable(Trigger trigger)
        {
            base.OnEnable(trigger);
            if (!PurrNetVisualScriptingSupport.TryResolveSteamLobbyNetwork(
                    trigger.gameObject,
                    out m_Source))
            {
                return;
            }

            m_LastState = m_Source.State;
            m_Source.StateChanged -= OnStateChanged;
            m_Source.StateChanged += OnStateChanged;
        }

        protected override void OnDisable(Trigger trigger)
        {
            if (m_Source != null)
            {
                m_Source.StateChanged -= OnStateChanged;
                m_Source = null;
            }
            base.OnDisable(trigger);
        }

        private void OnStateChanged()
        {
            if (m_Source == null) return;
            PurrNetSteamLobbySessionState current = m_Source.State;
            if (current == m_LastState) return;
            m_LastState = current;
            if (current != m_State) return;

            PurrNetVisualScriptingSupport.DispatchNextFrame(
                m_Trigger,
                Self,
                nameof(EventPurrNetSteamLobbyState));
        }
    }
}
