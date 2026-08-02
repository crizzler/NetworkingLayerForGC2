using System;
using Fusion;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.VisualScripting;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Transport.Fusion
{
    /// <summary>
    /// Deliberately limits visual scripting to the session roles supported by the
    /// Networking Layer bootstrap. Native Fusion modes remain an implementation detail.
    /// </summary>
    public enum FusionVisualSessionMode
    {
        Host = 0,
        Client = 1,
        Shared = 2
    }

    [Title("Fusion Session Mode Is")]
    [Description("Returns true when the active Fusion session uses the selected supported role")]

    [Category("Network/Fusion/Session/Mode Is")]

    [Parameter("Mode", "Host, Client, or Shared session role")]

    [Keywords("Network", "Fusion", "Photon", "Session", "Mode", "Host", "Client", "Shared")]
    [Image(typeof(IconChip), ColorTheme.Type.Blue)]
    [Serializable]
    public sealed class ConditionFusionSessionModeIs : Condition
    {
        [SerializeField] private FusionVisualSessionMode m_Mode;

        protected override string Summary => $"Fusion Session Mode is {m_Mode}";

        protected override bool Run(Args args)
        {
            if (!FusionVisualScriptingSupport.TryGetActiveSession(
                    args.Self,
                    out FusionSessionSnapshot session))
            {
                return false;
            }

            return m_Mode switch
            {
                FusionVisualSessionMode.Host => session.GameMode == GameMode.Host,
                FusionVisualSessionMode.Client => session.GameMode == GameMode.Client,
                FusionVisualSessionMode.Shared => session.GameMode == GameMode.Shared,
                _ => false
            };
        }
    }
}
