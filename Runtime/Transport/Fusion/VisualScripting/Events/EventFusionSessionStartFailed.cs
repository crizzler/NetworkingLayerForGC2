using System;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.VisualScripting;
using Event = GameCreator.Runtime.VisualScripting.Event;

namespace Arawn.GameCreator2.Networking.Transport.Fusion
{
    [Title("On Fusion Session Start Failed")]
    [Description("Executed when the Fusion session bootstrap observes a failed start or join")]

    [Category("Network/Fusion/Session/On Session Start Failed")]

    [Keywords("Network", "Fusion", "Photon", "Session", "Start", "Join", "Failed", "Error")]
    [Image(typeof(IconChip), ColorTheme.Type.Red, typeof(OverlayCross))]
    [Serializable]
    public sealed class EventFusionSessionStartFailed : Event
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

            m_Source.SessionObservedStartFailed -= OnSessionStartFailed;
            m_Source.SessionObservedStartFailed += OnSessionStartFailed;
        }

        protected override void OnDisable(Trigger trigger)
        {
            base.OnDisable(trigger);
            if (m_Source != null)
            {
                m_Source.SessionObservedStartFailed -= OnSessionStartFailed;
                m_Source = null;
            }
        }

        private void OnSessionStartFailed(FusionSessionFailureInfo failure)
        {
            FusionVisualScriptingSupport.DispatchNextFrame(
                m_Trigger,
                Self,
                nameof(EventFusionSessionStartFailed));
        }
    }
}
