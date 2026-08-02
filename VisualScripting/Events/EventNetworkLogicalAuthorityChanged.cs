using System;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.VisualScripting;

namespace Arawn.GameCreator2.Networking
{
    [Title("On Network Authority Changed")]
    [Description("Executed when this peer gains or loses logical gameplay authority")]

    [Category("Network/Lifecycle/On Network Authority Changed")]

    [Keywords("Network", "Authority", "Server", "Master", "Changed", "Lifecycle")]
    [Image(typeof(IconSignal), ColorTheme.Type.Yellow)]
    [Serializable]
    public sealed class EventNetworkLogicalAuthorityChanged : Event
    {
        protected override void OnEnable(Trigger trigger)
        {
            base.OnEnable(trigger);
            NetworkLifecycleEvents.LogicalAuthorityChanged += OnLogicalAuthorityChanged;
        }

        protected override void OnDisable(Trigger trigger)
        {
            NetworkLifecycleEvents.LogicalAuthorityChanged -= OnLogicalAuthorityChanged;
            base.OnDisable(trigger);
        }

        private void OnLogicalAuthorityChanged(
            NetworkTransportBridge source,
            bool isAuthority,
            uint authorityEpoch)
        {
            _ = m_Trigger.Execute(Self);
        }
    }
}
