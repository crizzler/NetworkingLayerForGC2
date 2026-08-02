using System;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.VisualScripting;
using Event = GameCreator.Runtime.VisualScripting.Event;

namespace Arawn.GameCreator2.Networking.Transport.Fusion
{
    [Title("On Fusion Session Started")]
    [Description("Executed after the Fusion session bootstrap observes a successful session start")]

    [Category("Network/Fusion/Session/On Session Started")]

    [Keywords("Network", "Fusion", "Photon", "Session", "Started", "Connected")]
    [Image(typeof(IconChip), ColorTheme.Type.Green, typeof(OverlayBolt))]
    [Serializable]
    public sealed class EventFusionSessionStarted : Event
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

            m_Source.SessionObservedStarted -= OnSessionStarted;
            m_Source.SessionObservedStarted += OnSessionStarted;
        }

        protected override void OnDisable(Trigger trigger)
        {
            base.OnDisable(trigger);
            if (m_Source != null)
            {
                m_Source.SessionObservedStarted -= OnSessionStarted;
                m_Source = null;
            }
        }

        private void OnSessionStarted(FusionSessionSnapshot snapshot)
        {
            FusionVisualScriptingSupport.DispatchNextFrame(
                m_Trigger,
                Self,
                nameof(EventFusionSessionStarted));
        }
    }
}
