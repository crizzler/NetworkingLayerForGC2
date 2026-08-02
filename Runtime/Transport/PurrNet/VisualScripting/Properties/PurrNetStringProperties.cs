using System;
using GameCreator.Runtime.Common;
using PurrNet;
using PurrNet.Transports;

namespace Arawn.GameCreator2.Networking.Transport.PurrNet
{
    [Title("PurrNet Server Connection State")]
    [Category("Network/PurrNet/Connection/Server State")]
    [Description("The native PurrNet server connection state")]
    [Keywords("Network", "PurrNet", "Server", "Connection", "State")]
    [Image(typeof(IconString), ColorTheme.Type.Blue)]
    [Serializable]
    [HideLabelsInEditor]
    public sealed class GetStringPurrNetServerConnectionState : PropertyTypeGetString
    {
        public override string Get(Args args)
        {
            return PurrNetVisualScriptingSupport.TryResolveNetworkManager(
                args.Self,
                out NetworkManager manager)
                ? manager.serverState.ToString()
                : ConnectionState.Disconnected.ToString();
        }

        public override string String => "PurrNet Server Connection State";
    }

    [Title("PurrNet Client Connection State")]
    [Category("Network/PurrNet/Connection/Client State")]
    [Description("The native PurrNet client connection state")]
    [Keywords("Network", "PurrNet", "Client", "Connection", "State")]
    [Image(typeof(IconString), ColorTheme.Type.Blue)]
    [Serializable]
    [HideLabelsInEditor]
    public sealed class GetStringPurrNetClientConnectionState : PropertyTypeGetString
    {
        public override string Get(Args args)
        {
            return PurrNetVisualScriptingSupport.TryResolveNetworkManager(
                args.Self,
                out NetworkManager manager)
                ? manager.clientState.ToString()
                : ConnectionState.Disconnected.ToString();
        }

        public override string String => "PurrNet Client Connection State";
    }

    [Title("PurrNet Transport Type")]
    [Category("Network/PurrNet/Connection/Transport Type")]
    [Description("The concrete transport configured on the resolved PurrNet NetworkManager")]
    [Keywords("Network", "PurrNet", "Transport", "Type", "UDP", "Steam")]
    [Image(typeof(IconString), ColorTheme.Type.Blue)]
    [Serializable]
    [HideLabelsInEditor]
    public sealed class GetStringPurrNetTransportType : PropertyTypeGetString
    {
        public override string Get(Args args)
        {
            return PurrNetVisualScriptingSupport.TryResolveNetworkManager(
                       args.Self,
                       out NetworkManager manager) &&
                   manager.transport != null
                ? manager.transport.GetType().FullName
                : string.Empty;
        }

        public override string String => "PurrNet Transport Type";
    }

    [Title("PurrNet Steam Lobby State")]
    [Category("Network/PurrNet/Steam Lobby/State")]
    [Description("The current PurrNet Steam lobby coordinator state")]
    [Keywords("Network", "PurrNet", "Steam", "Lobby", "State")]
    [Image(typeof(IconString), ColorTheme.Type.Blue)]
    [Serializable]
    [HideLabelsInEditor]
    public sealed class GetStringPurrNetSteamLobbyState : PropertyTypeGetString
    {
        public override string Get(Args args)
        {
            return PurrNetVisualScriptingSupport.TryResolveSteamLobbyNetwork(
                args.Self,
                out PurrNetSteamLobbyNetwork lobbyNetwork)
                ? lobbyNetwork.State.ToString()
                : PurrNetSteamLobbySessionState.Unavailable.ToString();
        }

        public override string String => "PurrNet Steam Lobby State";
    }

    [Title("PurrNet Steam Lobby ID")]
    [Category("Network/PurrNet/Steam Lobby/Lobby ID")]
    [Description("The current Steam lobby ID as lossless text")]
    [Keywords("Network", "PurrNet", "Steam", "Lobby", "ID", "String")]
    [Image(typeof(IconID), ColorTheme.Type.Blue)]
    [Serializable]
    [HideLabelsInEditor]
    public sealed class GetStringPurrNetSteamLobbyId : PropertyTypeGetString
    {
        public override string Get(Args args)
        {
            return PurrNetVisualScriptingSupport.TryResolveSteamLobbyNetwork(
                args.Self,
                out PurrNetSteamLobbyNetwork lobbyNetwork)
                ? lobbyNetwork.CurrentLobbyId
                : string.Empty;
        }

        public override string String => "PurrNet Steam Lobby ID";
    }

    [Title("PurrNet Local Steam ID")]
    [Category("Network/PurrNet/Steam Lobby/Local Steam ID")]
    [Description("The local user's Steam ID as lossless text")]
    [Keywords("Network", "PurrNet", "Steam", "User", "Local", "ID", "String")]
    [Image(typeof(IconID), ColorTheme.Type.Blue)]
    [Serializable]
    [HideLabelsInEditor]
    public sealed class GetStringPurrNetLocalSteamId : PropertyTypeGetString
    {
        public override string Get(Args args)
        {
            return PurrNetVisualScriptingSupport.TryResolveSteamLobbyNetwork(
                args.Self,
                out PurrNetSteamLobbyNetwork lobbyNetwork)
                ? lobbyNetwork.LocalSteamId
                : string.Empty;
        }

        public override string String => "PurrNet Local Steam ID";
    }

    [Title("PurrNet Steam Lobby Status")]
    [Category("Network/PurrNet/Steam Lobby/Status")]
    [Description("The current human-readable status from PurrNet Steam Lobby Network")]
    [Keywords("Network", "PurrNet", "Steam", "Lobby", "Status", "Message")]
    [Image(typeof(IconMessage), ColorTheme.Type.Blue)]
    [Serializable]
    [HideLabelsInEditor]
    public sealed class GetStringPurrNetSteamLobbyStatus : PropertyTypeGetString
    {
        public override string Get(Args args)
        {
            return PurrNetVisualScriptingSupport.TryResolveSteamLobbyNetwork(
                args.Self,
                out PurrNetSteamLobbyNetwork lobbyNetwork)
                ? lobbyNetwork.StatusMessage
                : string.Empty;
        }

        public override string String => "PurrNet Steam Lobby Status";
    }

    [Title("PurrNet Steam Lobby Last Error")]
    [Category("Network/PurrNet/Steam Lobby/Last Error")]
    [Description("The most recent fatal or non-fatal PurrNet Steam lobby error")]
    [Keywords("Network", "PurrNet", "Steam", "Lobby", "Error", "Message", "Last")]
    [Image(typeof(IconMessage), ColorTheme.Type.Red)]
    [Serializable]
    [HideLabelsInEditor]
    public sealed class GetStringPurrNetSteamLobbyLastError : PropertyTypeGetString
    {
        public override string Get(Args args)
        {
            return PurrNetVisualScriptingSupport.TryResolveSteamLobbyNetwork(
                args.Self,
                out PurrNetSteamLobbyNetwork lobbyNetwork)
                ? lobbyNetwork.LastError
                : string.Empty;
        }

        public override string String => "PurrNet Steam Lobby Last Error";
    }
}
