using System;
using System.Threading.Tasks;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.VisualScripting;
using UnityEngine;

namespace Arawn.GameCreator2.Networking
{
    [Version(1, 0, 0)]
    [Title("Network Set Invincibility")]
    [Description("Requests a server-authoritative invincibility duration; use zero to cancel")]
    [Category("Network/Core/Combat/Set Invincibility")]
    [Parameter("Character", "The initialized Network Character whose invincibility changes")]
    [Parameter("Duration", "Duration in seconds, or zero to cancel invincibility")]
    [Parameter("Wait for Response", "Waits for authoritative approval or rejection before continuing")]
    [Keywords("Network", "Character", "Combat", "Invincible", "Invulnerability")]
    [Image(typeof(IconShieldSolid), ColorTheme.Type.Blue)]
    [Serializable]
    public sealed class InstructionNetworkCoreSetInvincibility : TInstructionNetworkCoreRequest
    {
        [SerializeField] private PropertyGetDecimal m_Duration = new PropertyGetDecimal(1f);

        public override string Title => $"Network Set Invincibility on {m_Character} for {m_Duration}s";

        protected override Task Run(Args args)
        {
            float duration = (float)m_Duration.Get(args);

            return SendRequest<NetworkInvincibilityResponse>(
                args,
                nameof(InstructionNetworkCoreSetInvincibility),
                NetworkCoreRequestChannel.Invincibility,
                NetworkCoreVisualScriptingOperation.SetInvincibility,
                (manager, characterNetworkId, callback) =>
                    manager.RequestSetInvincibility(characterNetworkId, duration, callback),
                NetworkCoreVisualScriptingContext.CompleteRequest);
        }
    }
}
