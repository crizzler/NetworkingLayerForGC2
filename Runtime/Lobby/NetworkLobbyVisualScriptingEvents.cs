using System;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.VisualScripting;
using UnityEngine;
using Event = GameCreator.Runtime.VisualScripting.Event;

namespace Arawn.GameCreator2.Networking.Lobby
{
    [Serializable]
    public abstract class NetworkLobbyEvent : Event
    {
        [SerializeField]
        [Tooltip("Optional lobby service component. Empty uses the first active service in the scene.")]
        private MonoBehaviour m_ServiceBehaviour;

        protected INetworkLobbyService Service { get; private set; }

        protected override void OnEnable(Trigger trigger)
        {
            base.OnEnable(trigger);
            Service = NetworkLobbyServiceUtility.Resolve(m_ServiceBehaviour);
            Subscribe(Service);
        }

        protected override void OnDisable(Trigger trigger)
        {
            Unsubscribe(Service);
            Service = null;
            base.OnDisable(trigger);
        }

        protected abstract void Subscribe(INetworkLobbyService service);
        protected abstract void Unsubscribe(INetworkLobbyService service);

        protected void ExecuteTrigger()
        {
            _ = m_Trigger.Execute(Self);
        }
    }

    [Title("On Network Lobby State Changed")]
    [Description("Executed whenever the active lobby service changes state or status")]
    [Category("Network/Lobby/On State Changed")]
    [Keywords("Network", "Lobby", "State", "Changed", "Status")]
    [Image(typeof(IconSignal), ColorTheme.Type.Blue)]
    [Serializable]
    public sealed class EventNetworkLobbyStateChanged : NetworkLobbyEvent
    {
        protected override void Subscribe(INetworkLobbyService service)
        {
            if (service != null) service.StateChanged += ExecuteTrigger;
        }

        protected override void Unsubscribe(INetworkLobbyService service)
        {
            if (service != null) service.StateChanged -= ExecuteTrigger;
        }
    }

    [Title("On Network Lobby Connected")]
    [Description("Executed when the active lobby service enters the Connected state")]
    [Category("Network/Lobby/On Connected")]
    [Keywords("Network", "Lobby", "Connected", "Joined", "Created")]
    [Image(typeof(IconSignal), ColorTheme.Type.Green)]
    [Serializable]
    public sealed class EventNetworkLobbyConnected : NetworkLobbyEvent
    {
        private bool m_WasConnected;

        protected override void Subscribe(INetworkLobbyService service)
        {
            m_WasConnected = service?.State == NetworkLobbyState.Connected;
            if (service != null) service.StateChanged += OnStateChanged;
        }

        protected override void Unsubscribe(INetworkLobbyService service)
        {
            if (service != null) service.StateChanged -= OnStateChanged;
            m_WasConnected = false;
        }

        private void OnStateChanged()
        {
            bool connected = Service?.State == NetworkLobbyState.Connected;
            if (connected && !m_WasConnected) ExecuteTrigger();
            m_WasConnected = connected;
        }
    }

    [Title("On Network Lobby Sessions Changed")]
    [Description("Executed whenever the active lobby service replaces its browsed session list")]
    [Category("Network/Lobby/On Sessions Changed")]
    [Keywords("Network", "Lobby", "Sessions", "Changed", "Refresh", "Browse")]
    [Image(typeof(IconRefresh), ColorTheme.Type.Blue)]
    [Serializable]
    public sealed class EventNetworkLobbySessionsChanged : NetworkLobbyEvent
    {
        protected override void Subscribe(INetworkLobbyService service)
        {
            if (service != null) service.SessionsChanged += ExecuteTrigger;
        }

        protected override void Unsubscribe(INetworkLobbyService service)
        {
            if (service != null) service.SessionsChanged -= ExecuteTrigger;
        }
    }
}
