using System;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.VisualScripting;

namespace Arawn.GameCreator2.Networking.Transport.PurrNet
{
    [Title("PurrNet Steam Lobby Is Busy")]
    [Description("Returns true while the PurrNet Steam lobby coordinator is creating, joining, starting, or leaving")]

    [Category("Network/PurrNet/Steam Lobby/Is Busy")]

    [Keywords("Network", "PurrNet", "Steam", "Lobby", "Busy", "Starting", "Joining", "Leaving")]
    [Image(typeof(IconClock), ColorTheme.Type.Yellow)]
    [Serializable]
    public sealed class ConditionPurrNetSteamBusy : Condition
    {
        protected override string Summary => "PurrNet Steam Lobby is Busy";

        protected override bool Run(Args args)
        {
            return PurrNetVisualScriptingSupport.TryResolveSteamLobbyNetwork(
                       args.Self,
                       out PurrNetSteamLobbyNetwork lobbyNetwork) &&
                   lobbyNetwork.IsBusy;
        }
    }
}
