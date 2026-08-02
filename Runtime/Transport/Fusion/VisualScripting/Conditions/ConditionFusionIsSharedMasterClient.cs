using System;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.VisualScripting;

namespace Arawn.GameCreator2.Networking.Transport.Fusion
{
    [Title("Is Fusion Shared Master Client")]
    [Description("Returns true if this peer is the current Master Client in a Fusion Shared session")]

    [Category("Network/Fusion/Authority/Is Shared Master Client")]

    [Keywords("Network", "Fusion", "Photon", "Shared", "Master", "Authority")]
    [Image(typeof(IconCrown), ColorTheme.Type.Purple)]
    [Serializable]
    public sealed class ConditionFusionIsSharedMasterClient : Condition
    {
        protected override string Summary => "is Fusion Shared Master Client";

        protected override bool Run(Args args)
        {
            return FusionVisualScriptingSupport.IsSharedMasterClient(args.Self);
        }
    }
}
