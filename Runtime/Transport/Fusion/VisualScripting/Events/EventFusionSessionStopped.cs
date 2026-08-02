using System;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.VisualScripting;
using Event = GameCreator.Runtime.VisualScripting.Event;

namespace Arawn.GameCreator2.Networking.Transport.Fusion
{
    [Title("On Fusion Session Stopped")]
    [Description("Executed after the Fusion session bootstrap observes a completed stop")]

    [Category("Network/Fusion/Session/On Session Stopped")]

    [Keywords("Network", "Fusion", "Photon", "Session", "Stopped", "Disconnected")]
    [Image(typeof(IconExit), ColorTheme.Type.Red)]
    [Serializable]
    public sealed class EventFusionSessionStopped : Event
    {
        [NonSerialized] private FusionSessionBootstrap m_Source;

        protected override void OnEnable(Trigger trigger)
        {
            base.OnEnable(trigger);
            if (!FusionVisualScriptingSupport.TryResolveBootstrap(
                    trigger.gameObject,
                    out m_Source))
            {
                return;
            }

            m_Source.SessionObservedStopped -= OnSessionStopped;
            m_Source.SessionObservedStopped += OnSessionStopped;
        }

        protected override void OnDisable(Trigger trigger)
        {
            base.OnDisable(trigger);
            if (m_Source != null)
            {
                m_Source.SessionObservedStopped -= OnSessionStopped;
                m_Source = null;
            }
        }

        private void OnSessionStopped(FusionSessionStopInfo stop)
        {
            FusionVisualScriptingSupport.DispatchNextFrame(
                m_Trigger,
                Self,
                nameof(EventFusionSessionStopped));
        }
    }
}
