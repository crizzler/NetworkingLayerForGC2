using System;
using System.Threading.Tasks;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.VisualScripting;
using UnityEngine;

namespace Arawn.GameCreator2.Networking
{
    [Version(1, 0, 0)]
    [Title("Network Start Ragdoll")]
    [Description("Requests the server to put a Network Character into its replicated ragdoll state")]
    [Category("Network/Core/Ragdoll/Start Ragdoll")]
    [Parameter("Character", "The initialized Network Character to ragdoll")]
    [Parameter("Force", "Optional world-space impulse; zero starts ragdoll without an impulse")]
    [Parameter("Force Point", "World-space point at which the optional impulse is applied")]
    [Parameter("Wait for Response", "Waits for authoritative approval or rejection before continuing")]
    [Keywords("Network", "Character", "Ragdoll", "Physics", "Knockback")]
    [Image(typeof(IconSkeleton), ColorTheme.Type.Blue)]
    [Serializable]
    public sealed class InstructionNetworkCoreStartRagdoll : TInstructionNetworkCoreRequest
    {
        [SerializeField]
        private PropertyGetDirection m_Force = new PropertyGetDirection(Vector3.zero);

        [SerializeField]
        private PropertyGetPosition m_ForcePoint = new PropertyGetPosition(Vector3.zero);

        public override string Title => $"Network Start Ragdoll on {m_Character}";

        protected override Task Run(Args args)
        {
            Vector3 force = m_Force.Get(args);
            Vector3 forcePoint = m_ForcePoint.Get(args);

            return SendRequest<NetworkRagdollResponse>(
                args,
                nameof(InstructionNetworkCoreStartRagdoll),
                NetworkCoreRequestChannel.Ragdoll,
                NetworkCoreVisualScriptingOperation.StartRagdoll,
                (manager, characterNetworkId, callback) =>
                    manager.RequestStartRagdoll(
                        characterNetworkId,
                        force,
                        forcePoint,
                        callback),
                NetworkCoreVisualScriptingContext.CompleteRequest);
        }
    }

    [Version(1, 0, 0)]
    [Title("Network Recover Ragdoll")]
    [Description("Requests the server to recover a Network Character from its replicated ragdoll state")]
    [Category("Network/Core/Ragdoll/Recover Ragdoll")]
    [Parameter("Character", "The initialized Network Character to recover")]
    [Parameter("Instant", "Requests the Core API's instant recovery action")]
    [Parameter("Wait for Response", "Waits for authoritative approval or rejection before continuing")]
    [Keywords("Network", "Character", "Ragdoll", "Recover", "Stand")]
    [Image(typeof(IconSkeleton), ColorTheme.Type.Green)]
    [Serializable]
    public sealed class InstructionNetworkCoreRecoverRagdoll : TInstructionNetworkCoreRequest
    {
        [SerializeField]
        private PropertyGetBool m_Instant = new PropertyGetBool(false);

        public override string Title => $"Network Recover Ragdoll on {m_Character}";

        protected override Task Run(Args args)
        {
            bool instant = m_Instant.Get(args);

            return SendRequest<NetworkRagdollResponse>(
                args,
                nameof(InstructionNetworkCoreRecoverRagdoll),
                NetworkCoreRequestChannel.Ragdoll,
                NetworkCoreVisualScriptingOperation.RecoverRagdoll,
                (manager, characterNetworkId, callback) =>
                    manager.RequestStartRecover(characterNetworkId, instant, callback),
                NetworkCoreVisualScriptingContext.CompleteRequest);
        }
    }
}
