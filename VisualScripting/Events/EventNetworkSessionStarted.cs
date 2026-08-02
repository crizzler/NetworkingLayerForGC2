using System;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.VisualScripting;

namespace Arawn.GameCreator2.Networking
{
    [Title("On Network Session Started")]
    [Description("Executed when the active transport reports that a network session has started")]

    [Category("Network/Lifecycle/On Network Session Started")]

    [Keywords("Network", "Session", "Started", "Connected", "Lifecycle")]
    [Image(typeof(IconSignal), ColorTheme.Type.Green)]
    [Serializable]
    public sealed class EventNetworkSessionStarted : Event
    {
        protected override void OnEnable(Trigger trigger)
        {
            base.OnEnable(trigger);
            NetworkLifecycleEvents.SessionStarted += OnSessionStarted;
        }

        protected override void OnDisable(Trigger trigger)
        {
            NetworkLifecycleEvents.SessionStarted -= OnSessionStarted;
            base.OnDisable(trigger);
        }

        private void OnSessionStarted(NetworkTransportBridge source)
        {
            _ = m_Trigger.Execute(Self);
        }
    }
}
