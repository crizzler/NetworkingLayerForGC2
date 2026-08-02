using System;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.VisualScripting;

namespace Arawn.GameCreator2.Networking
{
    [Title("On Network Session Stopped")]
    [Description("Executed when the active transport reports that a network session has stopped")]

    [Category("Network/Lifecycle/On Network Session Stopped")]

    [Keywords("Network", "Session", "Stopped", "Disconnected", "Lifecycle")]
    [Image(typeof(IconSignal), ColorTheme.Type.Red)]
    [Serializable]
    public sealed class EventNetworkSessionStopped : Event
    {
        protected override void OnEnable(Trigger trigger)
        {
            base.OnEnable(trigger);
            NetworkLifecycleEvents.SessionStopped += OnSessionStopped;
        }

        protected override void OnDisable(Trigger trigger)
        {
            NetworkLifecycleEvents.SessionStopped -= OnSessionStopped;
            base.OnDisable(trigger);
        }

        private void OnSessionStopped(NetworkTransportBridge source)
        {
            _ = m_Trigger.Execute(Self);
        }
    }
}
