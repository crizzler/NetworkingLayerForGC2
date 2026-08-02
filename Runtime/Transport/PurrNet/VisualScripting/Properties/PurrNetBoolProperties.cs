using System;
using GameCreator.Runtime.Common;
using PurrNet;

namespace Arawn.GameCreator2.Networking.Transport.PurrNet
{
    [Title("PurrNet Local Player Is Ready")]
    [Category("Network/PurrNet/Player/Local Player Is Ready")]
    [Description("True after PurrNet has assigned and initialized the local player")]
    [Keywords("Network", "PurrNet", "Player", "Local", "Ready")]
    [Image(typeof(IconPlayer), ColorTheme.Type.Green, typeof(OverlayTick))]
    [Serializable]
    [HideLabelsInEditor]
    public sealed class GetBoolPurrNetLocalPlayerReady : PropertyTypeGetBool
    {
        public override bool Get(Args args)
        {
            return PurrNetVisualScriptingSupport.TryResolveNetworkManager(
                       args.Self,
                       out NetworkManager manager) &&
                   manager.isLocalPlayerReady;
        }

        public override string String => "PurrNet Local Player Is Ready";
    }

    [Title("PurrNet Transport Is Supported")]
    [Category("Network/PurrNet/Connection/Transport Is Supported")]
    [Description("True when the configured PurrNet transport supports the current platform")]
    [Keywords("Network", "PurrNet", "Transport", "Supported", "Platform")]
    [Image(typeof(IconSignal), ColorTheme.Type.Green)]
    [Serializable]
    [HideLabelsInEditor]
    public sealed class GetBoolPurrNetTransportSupported : PropertyTypeGetBool
    {
        public override bool Get(Args args)
        {
            return PurrNetVisualScriptingSupport.TryResolveNetworkManager(
                       args.Self,
                       out NetworkManager manager) &&
                   manager.transport != null &&
                   manager.transport.isSupported;
        }

        public override string String => "PurrNet Transport Is Supported";
    }

    [Title("PurrNet Steam Lobby Is Available")]
    [Category("Network/PurrNet/Steam Lobby/Is Available")]
    [Description("True when an available Steam lobby provider is attached")]
    [Keywords("Network", "PurrNet", "Steam", "Lobby", "Available")]
    [Image(typeof(IconSignal), ColorTheme.Type.Green)]
    [Serializable]
    [HideLabelsInEditor]
    public sealed class GetBoolPurrNetSteamAvailable : PropertyTypeGetBool
    {
        public override bool Get(Args args)
        {
            return PurrNetVisualScriptingSupport.TryResolveSteamLobbyNetwork(
                       args.Self,
                       out PurrNetSteamLobbyNetwork lobbyNetwork) &&
                   lobbyNetwork.IsAvailable;
        }

        public override string String => "PurrNet Steam Lobby Is Available";
    }

    [Title("PurrNet Steam Lobby Is Ready")]
    [Category("Network/PurrNet/Steam Lobby/Is Ready")]
    [Description("True after the attached Steam lobby provider initializes")]
    [Keywords("Network", "PurrNet", "Steam", "Lobby", "Ready", "Initialized")]
    [Image(typeof(IconSignal), ColorTheme.Type.Green, typeof(OverlayTick))]
    [Serializable]
    [HideLabelsInEditor]
    public sealed class GetBoolPurrNetSteamReady : PropertyTypeGetBool
    {
        public override bool Get(Args args)
        {
            return PurrNetVisualScriptingSupport.TryResolveSteamLobbyNetwork(
                       args.Self,
                       out PurrNetSteamLobbyNetwork lobbyNetwork) &&
                   lobbyNetwork.IsSteamReady;
        }

        public override string String => "PurrNet Steam Lobby Is Ready";
    }

    [Title("PurrNet Steam Lobby Is Busy")]
    [Category("Network/PurrNet/Steam Lobby/Is Busy")]
    [Description("True while the lobby coordinator is creating, joining, starting, or leaving")]
    [Keywords("Network", "PurrNet", "Steam", "Lobby", "Busy", "Starting")]
    [Image(typeof(IconClock), ColorTheme.Type.Yellow)]
    [Serializable]
    [HideLabelsInEditor]
    public sealed class GetBoolPurrNetSteamBusy : PropertyTypeGetBool
    {
        public override bool Get(Args args)
        {
            return PurrNetVisualScriptingSupport.TryResolveSteamLobbyNetwork(
                       args.Self,
                       out PurrNetSteamLobbyNetwork lobbyNetwork) &&
                   lobbyNetwork.IsBusy;
        }

        public override string String => "PurrNet Steam Lobby Is Busy";
    }

    [Title("PurrNet Steam Has Lobby")]
    [Category("Network/PurrNet/Steam Lobby/Has Lobby")]
    [Description("True when the Steam provider currently reports a lobby ID")]
    [Keywords("Network", "PurrNet", "Steam", "Lobby", "Joined", "Created", "ID")]
    [Image(typeof(IconBust), ColorTheme.Type.Blue, typeof(OverlayTick))]
    [Serializable]
    [HideLabelsInEditor]
    public sealed class GetBoolPurrNetSteamHasLobby : PropertyTypeGetBool
    {
        public override bool Get(Args args)
        {
            return PurrNetVisualScriptingSupport.TryResolveSteamLobbyNetwork(
                       args.Self,
                       out PurrNetSteamLobbyNetwork lobbyNetwork) &&
                   !string.IsNullOrWhiteSpace(lobbyNetwork.CurrentLobbyId);
        }

        public override string String => "PurrNet Steam Has Lobby";
    }

    [Title("PurrNet Steam Session Is Connected")]
    [Category("Network/PurrNet/Steam Lobby/Session Is Connected")]
    [Description("True when the Steam lobby coordinator reports a connected host or client session")]
    [Keywords("Network", "PurrNet", "Steam", "Lobby", "Session", "Hosting", "Connected")]
    [Image(typeof(IconSignal), ColorTheme.Type.Green, typeof(OverlayTick))]
    [Serializable]
    [HideLabelsInEditor]
    public sealed class GetBoolPurrNetSteamSessionConnected : PropertyTypeGetBool
    {
        public override bool Get(Args args)
        {
            if (!PurrNetVisualScriptingSupport.TryResolveSteamLobbyNetwork(
                    args.Self,
                    out PurrNetSteamLobbyNetwork lobbyNetwork))
            {
                return false;
            }

            return lobbyNetwork.State == PurrNetSteamLobbySessionState.Hosting ||
                   lobbyNetwork.State == PurrNetSteamLobbySessionState.Connected;
        }

        public override string String => "PurrNet Steam Session Is Connected";
    }
}
