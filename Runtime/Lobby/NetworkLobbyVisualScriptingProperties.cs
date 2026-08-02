using System;
using GameCreator.Runtime.Common;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Lobby
{
    [Serializable]
    public abstract class NetworkLobbyStringProperty : PropertyTypeGetString
    {
        [SerializeField] private PropertyGetGameObject m_Service = new PropertyGetGameObject();

        protected INetworkLobbyService Resolve(Args args)
        {
            return NetworkLobbyServiceUtility.Resolve(m_Service.Get(args));
        }
    }

    [Title("Network Lobby State")]
    [Description("Returns the active lobby service state")]
    [Category("Network/Lobby/State")]
    [Image(typeof(IconSignal), ColorTheme.Type.Blue)]
    [Serializable]
    public sealed class GetStringNetworkLobbyState : NetworkLobbyStringProperty
    {
        public override string Get(Args args)
        {
            return Resolve(args)?.State.ToString() ?? NetworkLobbyState.Unavailable.ToString();
        }

        public override string String => "Network Lobby State";
    }

    [Title("Network Lobby Status")]
    [Description("Returns the provider's current human-readable lobby status")]
    [Category("Network/Lobby/Status")]
    [Image(typeof(IconMessage), ColorTheme.Type.Blue)]
    [Serializable]
    public sealed class GetStringNetworkLobbyStatus : NetworkLobbyStringProperty
    {
        public override string Get(Args args)
        {
            return Resolve(args)?.StatusMessage ?? string.Empty;
        }

        public override string String => "Network Lobby Status";
    }

    [Title("Network Lobby Last Error")]
    [Description("Returns the latest error reported by the active lobby service")]
    [Category("Network/Lobby/Last Error")]
    [Image(typeof(IconMessage), ColorTheme.Type.Red)]
    [Serializable]
    public sealed class GetStringNetworkLobbyLastError : NetworkLobbyStringProperty
    {
        public override string Get(Args args)
        {
            return Resolve(args)?.LastError ?? string.Empty;
        }

        public override string String => "Network Lobby Last Error";
    }

    [Title("Network Lobby Service Name")]
    [Description("Returns the display name of the active lobby provider")]
    [Category("Network/Lobby/Service Name")]
    [Image(typeof(IconNameVariable), ColorTheme.Type.Blue)]
    [Serializable]
    public sealed class GetStringNetworkLobbyServiceName : NetworkLobbyStringProperty
    {
        public override string Get(Args args)
        {
            return Resolve(args)?.ServiceName ?? string.Empty;
        }

        public override string String => "Network Lobby Service Name";
    }

    [Title("Current Network Lobby Session Name")]
    [Description("Returns the current connected session display name")]
    [Category("Network/Lobby/Current Session Name")]
    [Image(typeof(IconString), ColorTheme.Type.Blue)]
    [Serializable]
    public sealed class GetStringNetworkLobbyCurrentSessionName : NetworkLobbyStringProperty
    {
        public override string Get(Args args)
        {
            return Resolve(args)?.CurrentSessionName ?? string.Empty;
        }

        public override string String => "Current Network Lobby Session Name";
    }

    [Title("Current Network Lobby Session ID")]
    [Description("Returns the provider identifier of the current connected session")]
    [Category("Network/Lobby/Current Session ID")]
    [Image(typeof(IconID), ColorTheme.Type.Blue)]
    [Serializable]
    public sealed class GetStringNetworkLobbyCurrentSessionId : NetworkLobbyStringProperty
    {
        public override string Get(Args args)
        {
            return Resolve(args)?.CurrentSessionId ?? string.Empty;
        }

        public override string String => "Current Network Lobby Session ID";
    }

    [Title("Network Lobby Session Count")]
    [Description("Returns the number of sessions currently listed by the lobby provider")]
    [Category("Network/Lobby/Session Count")]
    [Image(typeof(IconListVariable), ColorTheme.Type.Blue)]
    [Serializable]
    public sealed class GetDecimalNetworkLobbySessionCount : PropertyTypeGetDecimal
    {
        [SerializeField] private PropertyGetGameObject m_Service = new PropertyGetGameObject();

        public override double Get(Args args)
        {
            return NetworkLobbyServiceUtility.Resolve(m_Service.Get(args))?.Sessions?.Count ?? 0;
        }

        public override string String => "Network Lobby Session Count";
    }

    [Title("Is Network Lobby Connected")]
    [Description("Returns true when the active lobby service is connected to a session")]
    [Category("Network/Lobby/Is Connected")]
    [Image(typeof(IconSignal), ColorTheme.Type.Green)]
    [Serializable]
    public sealed class GetBoolNetworkLobbyConnected : PropertyTypeGetBool
    {
        [SerializeField] private PropertyGetGameObject m_Service = new PropertyGetGameObject();

        public override bool Get(Args args)
        {
            return NetworkLobbyServiceUtility.Resolve(m_Service.Get(args))?.State ==
                   NetworkLobbyState.Connected;
        }

        public override string String => "Is Network Lobby Connected";
    }

    [Title("Is Network Lobby Busy")]
    [Description("Returns true while the active lobby service is performing an asynchronous operation")]
    [Category("Network/Lobby/Is Busy")]
    [Image(typeof(IconTimer), ColorTheme.Type.Yellow)]
    [Serializable]
    public sealed class GetBoolNetworkLobbyBusy : PropertyTypeGetBool
    {
        [SerializeField] private PropertyGetGameObject m_Service = new PropertyGetGameObject();

        public override bool Get(Args args)
        {
            INetworkLobbyService service = NetworkLobbyServiceUtility.Resolve(m_Service.Get(args));
            return service != null && NetworkLobbyServiceUtility.IsBusy(service.State);
        }

        public override string String => "Is Network Lobby Busy";
    }
}
