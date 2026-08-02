using System;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.VisualScripting;

namespace Arawn.GameCreator2.Networking
{
    [Title("On Network Client Connected")]
    [Description("Executed when a client connects to the active network session")]

    [Category("Network/Lifecycle/On Network Client Connected")]

    [Keywords("Network", "Client", "Connected", "Joined", "Lifecycle")]
    [Image(typeof(IconPlayer), ColorTheme.Type.Green)]
    [Serializable]
    public sealed class EventNetworkClientConnected : Event
    {
        protected override void OnEnable(Trigger trigger)
        {
            base.OnEnable(trigger);
            NetworkLifecycleEvents.ClientConnected += OnClientConnected;
        }

        protected override void OnDisable(Trigger trigger)
        {
            NetworkLifecycleEvents.ClientConnected -= OnClientConnected;
            base.OnDisable(trigger);
        }

        private void OnClientConnected(NetworkTransportBridge source, uint clientId)
        {
            _ = m_Trigger.Execute(Self);
        }
    }
}
