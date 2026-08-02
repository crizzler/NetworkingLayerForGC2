using System;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.VisualScripting;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Transport.Fusion
{
    [Title("Is Local Fusion Logical Owner")]
    [Description("Returns true if the local peer is the target identity's gameplay owner")]

    [Category("Network/Fusion/Authority/Is Local Logical Owner")]

    [Parameter("Target", "The Game Object containing or belonging to a Fusion Network Identity")]

    [Keywords("Network", "Fusion", "Photon", "Logical", "Owner", "Local", "Authority")]
    [Image(typeof(IconPlayer), ColorTheme.Type.Blue, typeof(OverlayTick))]
    [Serializable]
    public sealed class ConditionFusionIsLocalLogicalOwner : Condition
    {
        [SerializeField]
        private PropertyGetGameObject m_Target = GetGameObjectSelf.Create();

        protected override string Summary => $"is Local Fusion Logical Owner: {m_Target}";

        protected override bool Run(Args args)
        {
            return FusionVisualScriptingSupport.IsLocalLogicalOwner(m_Target, args);
        }
    }
}
