using System;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.VisualScripting;

namespace Arawn.GameCreator2.Networking.Transport.PurrNet
{
    [Title("PurrNet Steam Lobby Is Available")]
    [Description("Returns true when an available Steam lobby provider is attached to the PurrNet coordinator")]

    [Category("Network/PurrNet/Steam Lobby/Is Available")]

    [Keywords("Network", "PurrNet", "Steam", "Lobby", "Available", "Installed", "Provider")]
    [Image(typeof(IconSignal), ColorTheme.Type.Green)]
    [Serializable]
    public sealed class ConditionPurrNetSteamAvailable : Condition
    {
        protected override string Summary => "PurrNet Steam Lobby is Available";

        protected override bool Run(Args args)
        {
            return PurrNetVisualScriptingSupport.TryResolveSteamLobbyNetwork(
                       args.Self,
                       out PurrNetSteamLobbyNetwork lobbyNetwork) &&
                   lobbyNetwork.IsAvailable;
        }
    }
}
