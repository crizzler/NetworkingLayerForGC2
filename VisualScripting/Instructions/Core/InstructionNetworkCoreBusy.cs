using System;
using System.Threading.Tasks;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.VisualScripting;
using UnityEngine;

namespace Arawn.GameCreator2.Networking
{
    [Version(1, 0, 0)]
    [Title("Network Set Busy Limbs")]
    [Description("Requests the server to set or clear replicated busy state on selected Character limbs")]
    [Category("Network/Core/Character/Set Busy Limbs")]
    [Parameter("Character", "The initialized Network Character whose busy state changes")]
    [Parameter("Limbs", "One or more limbs to set or clear")]
    [Parameter("Set Busy", "True sets the selected limbs busy; false clears them")]
    [Parameter("Timeout", "Optional server-side duration before busy state clears; zero has no timeout")]
    [Parameter("Wait for Response", "Waits for authoritative approval or rejection before continuing")]
    [Keywords("Network", "Character", "Busy", "Limbs", "Arms", "Legs")]
    [Image(typeof(IconCharacter), ColorTheme.Type.Yellow)]
    [Serializable]
    public sealed class InstructionNetworkCoreSetBusy : TInstructionNetworkCoreRequest
    {
        [SerializeField] private BusyLimbs m_Limbs = BusyLimbs.Every;
        [SerializeField] private PropertyGetBool m_SetBusy = new PropertyGetBool(true);
        [SerializeField] private PropertyGetDecimal m_Timeout = new PropertyGetDecimal(0f);

        public override string Title => $"Network Set {m_Character} Busy ({m_Limbs})";

        protected override Task Run(Args args)
        {
            bool setBusy = m_SetBusy.Get(args);
            float timeout = (float)m_Timeout.Get(args);

            return SendRequest<NetworkBusyResponse>(
                args,
                nameof(InstructionNetworkCoreSetBusy),
                NetworkCoreRequestChannel.Busy,
                NetworkCoreVisualScriptingOperation.SetBusy,
                (manager, characterNetworkId, callback) =>
                    manager.RequestSetBusy(
                        characterNetworkId,
                        m_Limbs,
                        setBusy,
                        timeout,
                        callback),
                NetworkCoreVisualScriptingContext.CompleteRequest);
        }
    }
}
