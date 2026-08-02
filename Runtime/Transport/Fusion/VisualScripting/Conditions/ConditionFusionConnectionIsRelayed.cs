using System;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.VisualScripting;

namespace Arawn.GameCreator2.Networking.Transport.Fusion
{
    [Title("Fusion Connection Is Relayed")]
    [Description("Returns true when the active Fusion connection is using Photon Relay")]

    [Category("Network/Fusion/Connection/Is Relayed")]

    [Keywords("Network", "Fusion", "Photon", "Connection", "Relayed", "Relay")]
    [Image(typeof(IconSphereOutline), ColorTheme.Type.Blue)]
    [Serializable]
    public sealed class ConditionFusionConnectionIsRelayed : Condition
    {
        protected override string Summary => "Fusion Connection is Relayed";

        protected override bool Run(Args args)
        {
            return FusionVisualScriptingSupport.IsConnectionRelayed(args.Self);
        }
    }
}
