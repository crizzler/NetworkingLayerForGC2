using System;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.VisualScripting;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Transport.Fusion
{
    [Title("Fusion Session Lifecycle State Is")]
    [Description("Returns true when the resolved Fusion session bootstrap is in the selected lifecycle state")]

    [Category("Network/Fusion/Session/Lifecycle State Is")]

    [Parameter("State", "Offline, Runner Bound, Starting, Running, or Stopping")]

    [Keywords("Network", "Fusion", "Photon", "Session", "Lifecycle", "State")]
    [Image(typeof(IconCircleOutline), ColorTheme.Type.Blue)]
    [Serializable]
    public sealed class ConditionFusionSessionLifecycleStateIs : Condition
    {
        [SerializeField]
        private FusionSessionLifecycleState m_State = FusionSessionLifecycleState.Offline;

        protected override string Summary => $"Fusion Session Lifecycle State is {m_State}";

        protected override bool Run(Args args)
        {
            return FusionVisualScriptingSupport.TryResolveBootstrap(
                       args.Self,
                       out FusionSessionBootstrap bootstrap) &&
                   bootstrap.SessionLifecycleState == m_State;
        }
    }
}
