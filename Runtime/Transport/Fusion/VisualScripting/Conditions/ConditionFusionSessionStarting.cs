using System;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.VisualScripting;

namespace Arawn.GameCreator2.Networking.Transport.Fusion
{
    [Title("Fusion Session Is Starting")]
    [Description("Returns true while the resolved Photon Fusion session is starting or joining")]

    [Category("Network/Fusion/Session/Is Starting")]

    [Keywords("Network", "Fusion", "Photon", "Session", "Starting", "Joining")]
    [Image(typeof(IconClock), ColorTheme.Type.Yellow)]
    [Serializable]
    public sealed class ConditionFusionSessionStarting : Condition
    {
        protected override string Summary => "Fusion Session is Starting";

        protected override bool Run(Args args)
        {
            return FusionVisualScriptingSupport.TryResolveBootstrap(args.Self, out var bootstrap) &&
                   bootstrap.SessionLifecycleState == FusionSessionLifecycleState.Starting;
        }
    }
}
