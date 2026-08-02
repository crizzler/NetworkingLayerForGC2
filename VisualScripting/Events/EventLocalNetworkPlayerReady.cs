using System;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.VisualScripting;
using UnityEngine;
using Event = GameCreator.Runtime.VisualScripting.Event;

namespace Arawn.GameCreator2.Networking
{
    [Title("On Local Network Player Ready")]
    [Description("Executed when the locally owned Network Character is initialized and ready")]

    [Category("Network/Lifecycle/On Local Network Player Ready")]

    [Keywords("Network", "Local", "Player", "Ready", "Spawned", "Lifecycle")]
    [Image(typeof(IconPlayer), ColorTheme.Type.Green)]
    [Serializable]
    public sealed class EventLocalNetworkPlayerReady : Event
    {
        protected override void OnEnable(Trigger trigger)
        {
            base.OnEnable(trigger);
            NetworkLifecycleEvents.LocalPlayerReady += OnLocalPlayerReady;
        }

        protected override void OnDisable(Trigger trigger)
        {
            NetworkLifecycleEvents.LocalPlayerReady -= OnLocalPlayerReady;
            base.OnDisable(trigger);
        }

        private void OnLocalPlayerReady(NetworkTransportBridge source, GameObject player)
        {
            _ = m_Trigger.Execute(player != null ? player : Self);
        }
    }
}
