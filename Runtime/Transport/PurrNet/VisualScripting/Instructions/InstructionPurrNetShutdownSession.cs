using System;
using System.Threading.Tasks;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.VisualScripting;
using PurrNet;
using PurrNet.Transports;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Transport.PurrNet
{
    public enum PurrNetSessionShutdownOperation
    {
        StopAll = 0,
        StopServer = 1,
        StopClient = 2
    }

    [Version(1, 0, 0)]

    [Title("Shutdown PurrNet Session")]
    [Description("Stops all or one side of the current PurrNet session through the scene NetworkManager")]

    [Category("Network/PurrNet/Session/Shutdown Session")]

    [Parameter("Context", "A Game Object associated with the intended PurrNet scene setup")]
    [Parameter("Operation", "Stop All, Stop Server, or Stop Client")]
    [Parameter("Wait Until Disconnected", "Wait for every requested PurrNet connection half to stop before continuing the Instruction List")]
    [Parameter("Timeout", "Maximum seconds to wait. A non-positive value uses 30 seconds")]

    [Keywords("Network", "PurrNet", "Session", "Leave", "Shutdown", "Disconnect", "Stop")]
    [Image(typeof(IconExit), ColorTheme.Type.Red)]
    [Serializable]
    public sealed class InstructionPurrNetShutdownSession : Instruction
    {
        [SerializeField]
        private PropertyGetGameObject m_Context = GetGameObjectSelf.Create();

        [SerializeField]
        private PurrNetSessionShutdownOperation m_Operation =
            PurrNetSessionShutdownOperation.StopAll;

        [SerializeField]
        private bool m_WaitUntilDisconnected = true;

        [SerializeField]
        private PropertyGetDecimal m_Timeout = new PropertyGetDecimal(30d);

        public override string Title => $"PurrNet {GetOperationTitle(m_Operation)}";

        protected override Task Run(Args args)
        {
            GameObject context = m_Context.Get(args) ?? args.Self;
            if (!PurrNetVisualScriptingSupport.TryResolveNetworkManager(
                    context,
                    out NetworkManager manager))
            {
                PurrNetVisualScriptingSupport.LogMissingManager(context);
                return DefaultResult;
            }

            bool stopServer = m_Operation != PurrNetSessionShutdownOperation.StopClient;
            bool stopClient = m_Operation != PurrNetSessionShutdownOperation.StopServer;
            Task operation = PurrNetVisualScriptingSupport.StopAndWaitAsync(
                manager,
                () => Stop(manager, stopServer, stopClient),
                stopServer,
                stopClient,
                m_Timeout.Get(args));

            return PurrNetVisualScriptingSupport.CompleteOrObserve(
                operation,
                m_WaitUntilDisconnected,
                context,
                GetOperationTitle(m_Operation));
        }

        private static void Stop(
            NetworkManager manager,
            bool stopServer,
            bool stopClient)
        {
            // StopClient must run even while clientState is still Disconnected: it also
            // cancels PurrNet's one-frame delayed StartClient coroutine.
            if (stopClient) manager.StopClient();
            if (stopServer && manager.serverState != ConnectionState.Disconnected)
            {
                manager.StopServer();
            }
        }

        private static string GetOperationTitle(PurrNetSessionShutdownOperation operation)
        {
            return operation switch
            {
                PurrNetSessionShutdownOperation.StopAll => "Stop All",
                PurrNetSessionShutdownOperation.StopServer => "Stop Server",
                PurrNetSessionShutdownOperation.StopClient => "Stop Client",
                _ => operation.ToString()
            };
        }
    }
}
