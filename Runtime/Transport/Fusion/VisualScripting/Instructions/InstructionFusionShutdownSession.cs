using System;
using System.Threading.Tasks;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.VisualScripting;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Transport.Fusion
{
    [Version(1, 0, 0)]

    [Title("Shutdown Fusion Session")]
    [Description("Safely shuts down the current Photon Fusion session through its session bootstrap")]

    [Category("Network/Fusion/Session/Shutdown Session")]

    [Parameter("Context", "A Game Object associated with the Fusion scene setup")]
    [Parameter("Wait Until Complete", "Wait for Fusion to finish shutting down before continuing")]

    [Keywords("Network", "Fusion", "Photon", "Session", "Shutdown", "Disconnect", "Stop")]
    [Image(typeof(IconExit), ColorTheme.Type.Red)]
    [Serializable]
    public sealed class InstructionFusionShutdownSession : Instruction
    {
        [SerializeField]
        private PropertyGetGameObject m_Context = GetGameObjectSelf.Create();

        [SerializeField]
        private bool m_WaitUntilComplete = true;

        public override string Title => "Shutdown Fusion Session";

        protected override Task Run(Args args)
        {
            GameObject context = m_Context.Get(args) ?? args.Self;
            if (!FusionVisualScriptingSupport.TryResolveBootstrap(context, out var bootstrap))
            {
                FusionVisualScriptingSupport.LogMissingBootstrap(context);
                return DefaultResult;
            }

            return FusionVisualScriptingSupport.CompleteOrObserve(
                bootstrap.ShutdownAsync(),
                m_WaitUntilComplete,
                context,
                "Shutdown Session");
        }
    }
}
