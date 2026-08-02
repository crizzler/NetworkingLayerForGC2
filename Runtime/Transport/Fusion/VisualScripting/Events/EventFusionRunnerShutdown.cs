using System;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.VisualScripting;
using Event = GameCreator.Runtime.VisualScripting.Event;

namespace Arawn.GameCreator2.Networking.Transport.Fusion
{
    [Title("On Fusion Runner Shutdown")]
    [Description("Executed when the bound Photon Fusion runner reports shutdown")]

    [Category("Network/Fusion/Runner/On Runner Shutdown")]

    [Keywords("Network", "Fusion", "Photon", "Runner", "Shutdown", "Reason")]
    [Image(typeof(IconExit), ColorTheme.Type.Red, typeof(OverlayBolt))]
    [Serializable]
    public sealed class EventFusionRunnerShutdown : Event
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

            m_Source.RunnerObservedShutdown -= OnRunnerShutdown;
            m_Source.RunnerObservedShutdown += OnRunnerShutdown;
        }

        protected override void OnDisable(Trigger trigger)
        {
            base.OnDisable(trigger);
            if (m_Source != null)
            {
                m_Source.RunnerObservedShutdown -= OnRunnerShutdown;
                m_Source = null;
            }
        }

        private void OnRunnerShutdown(FusionRunnerShutdownInfo shutdown)
        {
            FusionVisualScriptingSupport.DispatchNextFrame(
                m_Trigger,
                Self,
                nameof(EventFusionRunnerShutdown));
        }
    }
}
