using System;
using System.Threading.Tasks;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.VisualScripting;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Transport.PurrNet
{
    [Version(1, 0, 0)]

    [Title("Leave PurrNet Steam Lobby")]
    [Description("Stops PurrNet and leaves the current Steam lobby")]

    [Category("Network/PurrNet/Steam Lobby/Leave Lobby")]

    [Parameter("Context", "A Game Object associated with the PurrNet Steam lobby setup")]
    [Parameter("Wait Until Complete", "Wait until PurrNet has stopped and the lobby coordinator is ready or unavailable")]
    [Parameter("Timeout", "Maximum seconds to wait. A non-positive value uses 30 seconds")]

    [Keywords("Network", "PurrNet", "Steam", "Lobby", "Leave", "Disconnect", "Stop")]
    [Image(typeof(IconExit), ColorTheme.Type.Red)]
    [Serializable]
    public sealed class InstructionPurrNetSteamLeaveLobby : Instruction
    {
        [SerializeField]
        private PropertyGetGameObject m_Context = GetGameObjectSelf.Create();

        [SerializeField]
        private bool m_WaitUntilComplete = true;

        [SerializeField]
        private PropertyGetDecimal m_Timeout = new PropertyGetDecimal(30d);

        public override string Title => "Leave PurrNet Steam Lobby";

        protected override Task Run(Args args)
        {
            GameObject context = m_Context.Get(args) ?? args.Self;
            if (!PurrNetVisualScriptingSupport.TryResolveSteamLobbyNetwork(
                    context,
                    out PurrNetSteamLobbyNetwork lobbyNetwork))
            {
                PurrNetVisualScriptingSupport.LogMissingSteamLobbyNetwork(context);
                return DefaultResult;
            }

            Task operation = PurrNetVisualScriptingSupport.RunSteamOperationAsync(
                lobbyNetwork,
                lobbyNetwork.Leave,
                state => state == PurrNetSteamLobbySessionState.Ready ||
                         state == PurrNetSteamLobbySessionState.Unavailable,
                m_Timeout.Get(args),
                "leave");
            return PurrNetVisualScriptingSupport.CompleteOrObserve(
                operation,
                m_WaitUntilComplete,
                context,
                "Leave Steam Lobby");
        }
    }
}
