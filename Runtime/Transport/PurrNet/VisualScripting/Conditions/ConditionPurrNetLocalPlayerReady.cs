using System;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.VisualScripting;
using PurrNet;

namespace Arawn.GameCreator2.Networking.Transport.PurrNet
{
    [Title("PurrNet Local Player Is Ready")]
    [Description("Returns true after PurrNet has assigned and initialized the local player")]

    [Category("Network/PurrNet/Player/Local Player Is Ready")]

    [Keywords("Network", "PurrNet", "Player", "Local", "Ready", "ID", "Connected")]
    [Image(typeof(IconPlayer), ColorTheme.Type.Green, typeof(OverlayTick))]
    [Serializable]
    public sealed class ConditionPurrNetLocalPlayerReady : Condition
    {
        protected override string Summary => "PurrNet Local Player is Ready";

        protected override bool Run(Args args)
        {
            return PurrNetVisualScriptingSupport.TryResolveNetworkManager(
                       args.Self,
                       out NetworkManager manager) &&
                   manager.isLocalPlayerReady;
        }
    }
}
