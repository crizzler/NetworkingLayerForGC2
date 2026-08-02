using System;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.VisualScripting;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Transport.Fusion
{
    [Title("Fusion Session Is Running")]
    [Description("Returns true while the resolved Photon Fusion session is running")]

    [Category("Network/Fusion/Session/Is Running")]

    [Keywords("Network", "Fusion", "Photon", "Session", "Running", "Connected")]
    [Image(typeof(IconSignal), ColorTheme.Type.Green)]
    [Serializable]
    public sealed class ConditionFusionSessionRunning : Condition
    {
        protected override string Summary => "Fusion Session is Running";

        protected override bool Run(Args args)
        {
            return FusionVisualScriptingSupport.TryResolveBootstrap(args.Self, out var bootstrap) &&
                   bootstrap.SessionLifecycleState == FusionSessionLifecycleState.Running;
        }
    }
}
