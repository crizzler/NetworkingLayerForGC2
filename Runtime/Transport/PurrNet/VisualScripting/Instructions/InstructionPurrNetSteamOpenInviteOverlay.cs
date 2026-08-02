using System;
using System.Threading.Tasks;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.VisualScripting;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Transport.PurrNet
{
    [Version(1, 0, 0)]

    [Title("Open PurrNet Steam Invite Overlay")]
    [Description("Opens Steam's invite overlay for the current PurrNet Steam lobby")]

    [Category("Network/PurrNet/Steam Lobby/Open Invite Overlay")]

    [Parameter("Context", "A Game Object associated with the PurrNet Steam lobby setup")]

    [Keywords("Network", "PurrNet", "Steam", "Lobby", "Invite", "Overlay", "Friends")]
    [Image(typeof(IconBust), ColorTheme.Type.Blue, typeof(OverlayBolt))]
    [Serializable]
    public sealed class InstructionPurrNetSteamOpenInviteOverlay : Instruction
    {
        [SerializeField]
        private PropertyGetGameObject m_Context = GetGameObjectSelf.Create();

        public override string Title => "Open PurrNet Steam Invite Overlay";

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

            lobbyNetwork.OpenInviteOverlay();
            return DefaultResult;
        }
    }
}
