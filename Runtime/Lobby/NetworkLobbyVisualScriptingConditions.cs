using System;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.VisualScripting;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Lobby
{
    [Serializable]
    public abstract class NetworkLobbyCondition : Condition
    {
        [SerializeField]
        [Tooltip("Optional GameObject with the lobby service. Empty searches the active scene.")]
        private PropertyGetGameObject m_Service = new PropertyGetGameObject();

        protected INetworkLobbyService ResolveService(Args args)
        {
            return NetworkLobbyServiceUtility.Resolve(m_Service.Get(args));
        }
    }

    [Title("Is Network Lobby Connected")]
    [Description("Returns true when the active lobby service is connected to a session")]
    [Category("Network/Lobby/Is Connected")]
    [Keywords("Network", "Lobby", "Connected", "Session")]
    [Image(typeof(IconSignal), ColorTheme.Type.Green)]
    [Serializable]
    public sealed class ConditionNetworkLobbyConnected : NetworkLobbyCondition
    {
        protected override string Summary => "is Lobby Connected";

        protected override bool Run(Args args)
        {
            return ResolveService(args)?.State == NetworkLobbyState.Connected;
        }
    }

    [Title("Network Lobby State")]
    [Description("Compares the active lobby service with the selected state")]
    [Category("Network/Lobby/State")]
    [Parameter("State", "Required lobby state")]
    [Keywords("Network", "Lobby", "State", "Busy", "Offline", "Error")]
    [Image(typeof(IconCondition), ColorTheme.Type.Blue)]
    [Serializable]
    public sealed class ConditionNetworkLobbyState : NetworkLobbyCondition
    {
        [SerializeField] private NetworkLobbyState m_State = NetworkLobbyState.Connected;

        protected override string Summary => $"Lobby State is {m_State}";

        protected override bool Run(Args args)
        {
            return ResolveService(args)?.State == m_State;
        }
    }

    [Title("Is Network Lobby Busy")]
    [Description("Returns true while the lobby is initializing, browsing, creating, joining, or leaving")]
    [Category("Network/Lobby/Is Busy")]
    [Keywords("Network", "Lobby", "Busy", "Loading", "Joining")]
    [Image(typeof(IconTimer), ColorTheme.Type.Yellow)]
    [Serializable]
    public sealed class ConditionNetworkLobbyBusy : NetworkLobbyCondition
    {
        protected override string Summary => "is Lobby Busy";

        protected override bool Run(Args args)
        {
            INetworkLobbyService service = ResolveService(args);
            return service != null && NetworkLobbyServiceUtility.IsBusy(service.State);
        }
    }

    [Title("Network Lobby Has Sessions")]
    [Description("Returns true when the active lobby service has one or more listed sessions")]
    [Category("Network/Lobby/Has Sessions")]
    [Keywords("Network", "Lobby", "Sessions", "Browse", "Available")]
    [Image(typeof(IconListFirst), ColorTheme.Type.Blue)]
    [Serializable]
    public sealed class ConditionNetworkLobbyHasSessions : NetworkLobbyCondition
    {
        protected override string Summary => "Lobby has Sessions";

        protected override bool Run(Args args)
        {
            return (ResolveService(args)?.Sessions?.Count ?? 0) > 0;
        }
    }

    [Title("Network Lobby Supports Capability")]
    [Description("Returns true when the active lobby provider supports the selected operation")]
    [Category("Network/Lobby/Supports Capability")]
    [Parameter("Capability", "Required provider capability")]
    [Keywords("Network", "Lobby", "Capability", "Supports", "Provider")]
    [Image(typeof(IconCheckSolid), ColorTheme.Type.Blue)]
    [Serializable]
    public sealed class ConditionNetworkLobbyCapability : NetworkLobbyCondition
    {
        [SerializeField] private NetworkLobbyCapabilities m_Capability =
            NetworkLobbyCapabilities.Create;

        protected override string Summary => $"Lobby supports {m_Capability}";

        protected override bool Run(Args args)
        {
            return NetworkLobbyServiceUtility.HasCapability(
                ResolveService(args),
                m_Capability);
        }
    }
}
