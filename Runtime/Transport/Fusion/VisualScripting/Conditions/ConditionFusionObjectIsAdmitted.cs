using System;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.VisualScripting;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Transport.Fusion
{
    [Title("Fusion Object Is Admitted")]
    [Description("Returns true if the target identity passed the Fusion authority-spawn admission boundary")]

    [Category("Network/Fusion/Authority/Object Is Admitted")]

    [Parameter("Target", "The Game Object containing or belonging to a Fusion Network Identity")]

    [Keywords("Network", "Fusion", "Photon", "Object", "Identity", "Admission", "Authority")]
    [Image(typeof(IconShieldSolid), ColorTheme.Type.Green, typeof(OverlayTick))]
    [Serializable]
    public sealed class ConditionFusionObjectIsAdmitted : Condition
    {
        [SerializeField]
        private PropertyGetGameObject m_Target = GetGameObjectSelf.Create();

        protected override string Summary => $"Fusion Object is Admitted: {m_Target}";

        protected override bool Run(Args args)
        {
            return FusionVisualScriptingSupport.IsObjectAdmitted(m_Target, args);
        }
    }
}
