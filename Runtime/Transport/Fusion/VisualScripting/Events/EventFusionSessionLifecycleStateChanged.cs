using System;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.VisualScripting;
using Event = GameCreator.Runtime.VisualScripting.Event;

namespace Arawn.GameCreator2.Networking.Transport.Fusion
{
    [Title("On Fusion Session Lifecycle State Changed")]
    [Description("Executed when the Fusion session bootstrap changes lifecycle state")]

    [Category("Network/Fusion/Session/On Lifecycle State Changed")]

    [Keywords("Network", "Fusion", "Photon", "Session", "Lifecycle", "State", "Changed")]
    [Image(typeof(IconCircleOutline), ColorTheme.Type.Blue, typeof(OverlayBolt))]
    [Serializable]
    public sealed class EventFusionSessionLifecycleStateChanged : Event
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

            m_Source.SessionObservedStateChanged -= OnSessionLifecycleStateChanged;
            m_Source.SessionObservedStateChanged += OnSessionLifecycleStateChanged;
        }

        protected override void OnDisable(Trigger trigger)
        {
            base.OnDisable(trigger);
            if (m_Source != null)
            {
                m_Source.SessionObservedStateChanged -= OnSessionLifecycleStateChanged;
                m_Source = null;
            }
        }

        private void OnSessionLifecycleStateChanged(FusionSessionLifecycleState state)
        {
            FusionVisualScriptingSupport.DispatchNextFrame(
                m_Trigger,
                Self,
                nameof(EventFusionSessionLifecycleStateChanged));
        }
    }
}
