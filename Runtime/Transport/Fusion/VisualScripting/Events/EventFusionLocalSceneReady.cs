using System;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.VisualScripting;
using Event = GameCreator.Runtime.VisualScripting.Event;

namespace Arawn.GameCreator2.Networking.Transport.Fusion
{
    [Title("On Fusion Local Scene Ready")]
    [Description("Executed after the Fusion bridge completes local scene readiness processing")]

    [Category("Network/Fusion/Scene/On Local Scene Ready")]

    [Keywords("Network", "Fusion", "Photon", "Scene", "Load", "Ready", "Completed")]
    [Image(typeof(IconUnity), ColorTheme.Type.Green, typeof(OverlayTick))]
    [Serializable]
    public sealed class EventFusionLocalSceneReady : Event
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

            m_Source.LocalSceneObservedCompleted -= OnLocalSceneReady;
            m_Source.LocalSceneObservedCompleted += OnLocalSceneReady;
        }

        protected override void OnDisable(Trigger trigger)
        {
            base.OnDisable(trigger);
            if (m_Source != null)
            {
                m_Source.LocalSceneObservedCompleted -= OnLocalSceneReady;
                m_Source = null;
            }
        }

        private void OnLocalSceneReady(FusionSceneLifecycleInfo scene)
        {
            FusionVisualScriptingSupport.DispatchNextFrame(
                m_Trigger,
                Self,
                nameof(EventFusionLocalSceneReady));
        }
    }
}
