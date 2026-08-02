using System;
using System.Threading.Tasks;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.VisualScripting;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Transport.PurrNet
{
    [Version(1, 0, 0)]

    [Title("Join PurrNet Steam Lobby")]
    [Description("Joins a Steam lobby by its decimal ID and connects its PurrNet client")]

    [Category("Network/PurrNet/Steam Lobby/Join Lobby")]

    [Parameter("Context", "A Game Object associated with the PurrNet Steam lobby setup")]
    [Parameter("Lobby ID", "The Steam lobby ID as text. Keep it as a string to preserve all 64-bit digits")]
    [Parameter("Wait Until Connected", "Wait for the PurrNet client to connect before continuing the Instruction List")]
    [Parameter("Timeout", "Maximum seconds to wait. A non-positive value uses 30 seconds")]

    [Keywords("Network", "PurrNet", "Steam", "Lobby", "Join", "Client", "Connect", "ID")]
    [Image(typeof(IconSignal), ColorTheme.Type.Green, typeof(OverlayBolt))]
    [Serializable]
    public sealed class InstructionPurrNetSteamJoinLobby : Instruction
    {
        [SerializeField]
        private PropertyGetGameObject m_Context = GetGameObjectSelf.Create();

        [SerializeField]
        private PropertyGetString m_LobbyId = new PropertyGetString(string.Empty);

        [SerializeField]
        private bool m_WaitUntilConnected = true;

        [SerializeField]
        private PropertyGetDecimal m_Timeout = new PropertyGetDecimal(45d);

        public override string Title => $"Join PurrNet Steam Lobby: {m_LobbyId}";

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

            string lobbyId = m_LobbyId.Get(args);
            Task operation = PurrNetVisualScriptingSupport.RunSteamOperationAsync(
                lobbyNetwork,
                () => lobbyNetwork.JoinLobby(lobbyId),
                state => state == PurrNetSteamLobbySessionState.Connected,
                m_Timeout.Get(args),
                "join");
            return PurrNetVisualScriptingSupport.CompleteOrObserve(
                operation,
                m_WaitUntilConnected,
                context,
                "Join Steam Lobby");
        }
    }
}
