using System;
using System.Threading.Tasks;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.VisualScripting;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Transport.PurrNet
{
    [Version(1, 0, 0)]

    [Title("Host PurrNet Steam Lobby")]
    [Description("Creates a Steam lobby and starts its PurrNet host through PurrNet Steam Lobby Network")]

    [Category("Network/PurrNet/Steam Lobby/Host Lobby")]

    [Parameter("Context", "A Game Object associated with the PurrNet Steam lobby setup")]
    [Parameter("Wait Until Connected", "Wait for both halves of the PurrNet host to connect before continuing the Instruction List")]
    [Parameter("Timeout", "Maximum seconds to wait. A non-positive value uses 30 seconds")]

    [Keywords("Network", "PurrNet", "Steam", "Lobby", "Host", "Create", "Start")]
    [Image(typeof(IconSignal), ColorTheme.Type.Green, typeof(OverlayPlus))]
    [Serializable]
    public sealed class InstructionPurrNetSteamHostLobby : Instruction
    {
        [SerializeField]
        private PropertyGetGameObject m_Context = GetGameObjectSelf.Create();

        [SerializeField]
        private bool m_WaitUntilConnected = true;

        [SerializeField]
        private PropertyGetDecimal m_Timeout = new PropertyGetDecimal(45d);

        public override string Title => "Host PurrNet Steam Lobby";

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
                lobbyNetwork.Host,
                state => state == PurrNetSteamLobbySessionState.Hosting,
                m_Timeout.Get(args),
                "host");
            return PurrNetVisualScriptingSupport.CompleteOrObserve(
                operation,
                m_WaitUntilConnected,
                context,
                "Host Steam Lobby");
        }
    }
}
