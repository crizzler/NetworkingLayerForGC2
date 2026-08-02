using System;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.VisualScripting;

namespace Arawn.GameCreator2.Networking
{
    [Title("On Network Client Disconnected")]
    [Description("Executed when a client disconnects from the active network session")]

    [Category("Network/Lifecycle/On Network Client Disconnected")]

    [Keywords("Network", "Client", "Disconnected", "Left", "Lifecycle")]
    [Image(typeof(IconPlayer), ColorTheme.Type.Red)]
    [Serializable]
    public sealed class EventNetworkClientDisconnected : Event
    {
        protected override void OnEnable(Trigger trigger)
        {
            base.OnEnable(trigger);
            NetworkLifecycleEvents.ClientDisconnected += OnClientDisconnected;
        }

        protected override void OnDisable(Trigger trigger)
        {
            NetworkLifecycleEvents.ClientDisconnected -= OnClientDisconnected;
            base.OnDisable(trigger);
        }

        private void OnClientDisconnected(NetworkTransportBridge source, uint clientId)
        {
            _ = m_Trigger.Execute(Self);
        }
    }
}
