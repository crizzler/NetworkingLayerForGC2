using System;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.VisualScripting;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Transport.Fusion
{
    [Title("Fusion Session Name Is")]
    [Description("Returns true when the active Photon Fusion session uses the exact supplied session ID or join code")]

    [Category("Network/Fusion/Session/Session Name Is")]

    [Parameter("Session Name", "Exact Photon session ID or join code to compare using ordinal, case-sensitive matching")]

    [Keywords("Network", "Fusion", "Photon", "Session", "Name", "ID", "Code", "Equals")]
    [Image(typeof(IconString), ColorTheme.Type.Blue, typeof(OverlayTick))]
    [Serializable]
    public sealed class ConditionFusionSessionNameIs : Condition
    {
        [SerializeField]
        private PropertyGetString m_SessionName = new PropertyGetString(string.Empty);

        protected override string Summary => $"Fusion Session Name is {m_SessionName}";

        protected override bool Run(Args args)
        {
            return FusionVisualScriptingSupport.ActiveSessionNameEquals(
                args.Self,
                m_SessionName.Get(args));
        }
    }
}
