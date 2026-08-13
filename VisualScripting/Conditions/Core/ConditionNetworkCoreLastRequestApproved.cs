using System;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.VisualScripting;

namespace Arawn.GameCreator2.Networking
{
    [Title("Network Core Last Request Approved")]
    [Description("Returns true when the latest completed Core visual-scripting request was approved")]
    [Category("Network/Core/Last Request Approved")]
    [Keywords("Network", "Core", "Request", "Response", "Approved", "Rejected")]
    [Image(typeof(IconCheckSolid), ColorTheme.Type.Green)]
    [Serializable]
    public sealed class ConditionNetworkCoreLastRequestApproved : Condition
    {
        protected override string Summary => "last Network Core request was approved";

        protected override bool Run(Args args)
        {
            return (NetworkCoreVisualScriptingContext.HasCurrent ||
                    NetworkCoreVisualScriptingContext.HasResult) &&
                   NetworkCoreVisualScriptingContext.CurrentResult.Approved;
        }
    }
}
