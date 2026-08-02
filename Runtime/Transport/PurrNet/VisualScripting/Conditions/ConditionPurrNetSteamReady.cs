using System;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.VisualScripting;

namespace Arawn.GameCreator2.Networking.Transport.PurrNet
{
    [Title("PurrNet Steam Lobby Is Ready")]
    [Description("Returns true after the PurrNet Steam lobby provider has initialized")]

    [Category("Network/PurrNet/Steam Lobby/Is Ready")]

    [Keywords("Network", "PurrNet", "Steam", "Lobby", "Ready", "Initialized")]
    [Image(typeof(IconSignal), ColorTheme.Type.Green, typeof(OverlayTick))]
    [Serializable]
    public sealed class ConditionPurrNetSteamReady : Condition
    {
        protected override string Summary => "PurrNet Steam Lobby is Ready";

        protected override bool Run(Args args)
        {
            return PurrNetVisualScriptingSupport.TryResolveSteamLobbyNetwork(
                       args.Self,
                       out PurrNetSteamLobbyNetwork lobbyNetwork) &&
                   lobbyNetwork.IsSteamReady;
        }
    }
}
