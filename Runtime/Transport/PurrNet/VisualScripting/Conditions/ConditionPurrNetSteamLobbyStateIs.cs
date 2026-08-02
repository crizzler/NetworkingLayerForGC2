using System;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.VisualScripting;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Transport.PurrNet
{
    [Title("PurrNet Steam Lobby State Is")]
    [Description("Returns true when the PurrNet Steam lobby coordinator has the selected state")]

    [Category("Network/PurrNet/Steam Lobby/State Is")]

    [Parameter("State", "The expected PurrNet Steam lobby session state")]

    [Keywords("Network", "PurrNet", "Steam", "Lobby", "State", "Ready", "Hosting", "Connected", "Error")]
    [Image(typeof(IconSignal), ColorTheme.Type.Blue)]
    [Serializable]
    public sealed class ConditionPurrNetSteamLobbyStateIs : Condition
    {
        [SerializeField]
        private PurrNetSteamLobbySessionState m_State =
            PurrNetSteamLobbySessionState.Ready;

        protected override string Summary => $"PurrNet Steam Lobby State is {m_State}";

        protected override bool Run(Args args)
        {
            return PurrNetVisualScriptingSupport.TryResolveSteamLobbyNetwork(
                       args.Self,
                       out PurrNetSteamLobbyNetwork lobbyNetwork) &&
                   lobbyNetwork.State == m_State;
        }
    }
}
