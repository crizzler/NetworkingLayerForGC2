using System;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.VisualScripting;
using Event = GameCreator.Runtime.VisualScripting.Event;

namespace Arawn.GameCreator2.Networking.Transport.Fusion
{
    [Title("On Fusion Logical Authority Changed")]
    [Description("Executed when this peer gains or loses Fusion logical gameplay authority")]

    [Category("Network/Fusion/Authority/On Logical Authority Changed")]

    [Keywords("Network", "Fusion", "Photon", "Logical", "Authority", "Master", "Changed")]
    [Image(typeof(IconCrown), ColorTheme.Type.Purple, typeof(OverlayBolt))]
    [Serializable]
    public sealed class EventFusionLogicalAuthorityChanged : Event
    {
        [NonSerialized] private FusionTransportBridge m_Source;

        protected override void OnEnable(Trigger trigger)
        {
            base.OnEnable(trigger);
            if (!FusionVisualScriptingSupport.TryResolveBridge(
                    trigger.gameObject,
                    out m_Source))
            {
                return;
            }

            m_Source.AuthorityObservedChanged -= OnAuthorityChanged;
            m_Source.AuthorityObservedChanged += OnAuthorityChanged;
        }

        protected override void OnDisable(Trigger trigger)
        {
            base.OnDisable(trigger);
            if (m_Source != null)
            {
                m_Source.AuthorityObservedChanged -= OnAuthorityChanged;
                m_Source = null;
            }
        }

        private void OnAuthorityChanged(FusionAuthorityObservation observation)
        {
            FusionVisualScriptingSupport.DispatchNextFrame(
                m_Trigger,
                Self,
                nameof(EventFusionLogicalAuthorityChanged));
        }
    }
}
