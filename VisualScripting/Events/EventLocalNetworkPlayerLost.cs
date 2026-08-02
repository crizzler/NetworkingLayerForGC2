using System;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.VisualScripting;
using UnityEngine;
using Event = GameCreator.Runtime.VisualScripting.Event;

namespace Arawn.GameCreator2.Networking
{
    [Title("On Local Network Player Lost")]
    [Description("Executed when the locally owned Network Character is reset or removed")]

    [Category("Network/Lifecycle/On Local Network Player Lost")]

    [Keywords("Network", "Local", "Player", "Lost", "Despawned", "Lifecycle")]
    [Image(typeof(IconPlayer), ColorTheme.Type.Red)]
    [Serializable]
    public sealed class EventLocalNetworkPlayerLost : Event
    {
        protected override void OnEnable(Trigger trigger)
        {
            base.OnEnable(trigger);
            NetworkLifecycleEvents.LocalPlayerLost += OnLocalPlayerLost;
        }

        protected override void OnDisable(Trigger trigger)
        {
            NetworkLifecycleEvents.LocalPlayerLost -= OnLocalPlayerLost;
            base.OnDisable(trigger);
        }

        private void OnLocalPlayerLost(NetworkTransportBridge source, GameObject player)
        {
            _ = m_Trigger.Execute(player != null ? player : Self);
        }
    }
}
