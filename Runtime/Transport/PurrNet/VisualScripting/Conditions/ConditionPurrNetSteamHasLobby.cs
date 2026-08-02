using System;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.VisualScripting;

namespace Arawn.GameCreator2.Networking.Transport.PurrNet
{
    [Title("PurrNet Steam Has Lobby")]
    [Description("Returns true when the Steam provider currently reports a lobby ID")]

    [Category("Network/PurrNet/Steam Lobby/Has Lobby")]

    [Keywords("Network", "PurrNet", "Steam", "Lobby", "Member", "Joined", "Created", "ID")]
    [Image(typeof(IconBust), ColorTheme.Type.Blue, typeof(OverlayTick))]
    [Serializable]
    public sealed class ConditionPurrNetSteamHasLobby : Condition
    {
        protected override string Summary => "PurrNet Steam Has Lobby";

        protected override bool Run(Args args)
        {
            return PurrNetVisualScriptingSupport.TryResolveSteamLobbyNetwork(
                       args.Self,
                       out PurrNetSteamLobbyNetwork lobbyNetwork) &&
                   !string.IsNullOrWhiteSpace(lobbyNetwork.CurrentLobbyId);
        }
    }
}
