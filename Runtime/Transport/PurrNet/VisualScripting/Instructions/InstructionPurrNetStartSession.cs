using System;
using System.Threading.Tasks;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.VisualScripting;
using PurrNet;
using PurrNet.Transports;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Transport.PurrNet
{
    public enum PurrNetSessionStartOperation
    {
        StartHost = 0,
        StartServer = 1,
        StartClient = 2
    }

    [Version(1, 0, 0)]

    [Title("Start PurrNet Session")]
    [Description("Starts a PurrNet host, dedicated server, or client through the scene NetworkManager")]

    [Category("Network/PurrNet/Session/Start Session")]

    [Parameter("Context", "A Game Object associated with the intended PurrNet scene setup")]
    [Parameter("Operation", "Start Host, Start Server, or Start Client")]
    [Parameter("Address", "Optional transport address. Blank preserves the transport's configured value")]
    [Parameter("Port", "Optional server port. Zero preserves the transport's configured value")]
    [Parameter("Wait Until Connected", "Wait for every required PurrNet connection half before continuing the Instruction List")]
    [Parameter("Timeout", "Maximum seconds to wait. A non-positive value uses 30 seconds")]

    [Keywords("Network", "PurrNet", "Session", "Host", "Server", "Client", "Join", "Start", "Connect")]
    [Image(typeof(IconSignal), ColorTheme.Type.Green, typeof(OverlayBolt))]
    [Serializable]
    public sealed class InstructionPurrNetStartSession : Instruction
    {
        [SerializeField]
        private PropertyGetGameObject m_Context = GetGameObjectSelf.Create();

        [SerializeField]
        private PurrNetSessionStartOperation m_Operation =
            PurrNetSessionStartOperation.StartHost;

        [SerializeField]
        private PropertyGetString m_Address = new PropertyGetString(string.Empty);

        [SerializeField]
        private PropertyGetInteger m_Port = new PropertyGetInteger(0);

        [SerializeField]
        private bool m_WaitUntilConnected = true;

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

            if (!CanStart(manager, m_Operation, out string stateError))
            {
                Debug.LogError($"[PurrNet Visual Scripting] {stateError}", context);
                return DefaultResult;
            }

            string address = m_Address.Get(args);
            double rawPort = m_Port.Get(args);
            if (double.IsNaN(rawPort) || double.IsInfinity(rawPort) ||
                rawPort < int.MinValue || rawPort > int.MaxValue)
            {
                Debug.LogError(
                    $"[PurrNet Visual Scripting] Port '{rawPort}' is not a valid integer.",
                    context);
                return DefaultResult;
            }

            int port = (int)rawPort;
            if (!PurrNetVisualScriptingSupport.TryConfigureEndpoint(
                    manager,
                    address,
                    port,
                    out string endpointError))
            {
                Debug.LogError($"[PurrNet Visual Scripting] {endpointError}", context);
                return DefaultResult;
            }

            bool requireServer = m_Operation != PurrNetSessionStartOperation.StartClient;
            bool requireClient = m_Operation != PurrNetSessionStartOperation.StartServer;
            Task operation = PurrNetVisualScriptingSupport.StartAndWaitAsync(
                manager,
                () => Start(manager, m_Operation),
                requireServer,
                requireClient,
                m_Timeout.Get(args));

            return PurrNetVisualScriptingSupport.CompleteOrObserve(
                operation,
                m_WaitUntilConnected,
                context,
                GetOperationTitle(m_Operation));
        }

        private static bool CanStart(
            NetworkManager manager,
            PurrNetSessionStartOperation operation,
            out string error)
        {
            bool serverOffline = manager.serverState == ConnectionState.Disconnected;
            bool clientOffline = manager.clientState == ConnectionState.Disconnected;
            bool allowed = operation switch
            {
                PurrNetSessionStartOperation.StartHost => serverOffline && clientOffline,
                PurrNetSessionStartOperation.StartServer => serverOffline,
                PurrNetSessionStartOperation.StartClient => clientOffline,
                _ => false
            };

            error = allowed
                ? string.Empty
                : $"Cannot {GetOperationTitle(operation)} while PurrNet is " +
                  $"server {manager.serverState}, client {manager.clientState}.";
            return allowed;
        }

        private static void Start(
            NetworkManager manager,
            PurrNetSessionStartOperation operation)
        {
            switch (operation)
            {
                case PurrNetSessionStartOperation.StartHost:
                    manager.StartHost();
                    break;
                case PurrNetSessionStartOperation.StartServer:
                    manager.StartServer();
                    break;
                case PurrNetSessionStartOperation.StartClient:
                    manager.StartClient();
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(operation), operation, null);
            }
        }

        private static string GetOperationTitle(PurrNetSessionStartOperation operation)
        {
            return operation switch
            {
                PurrNetSessionStartOperation.StartHost => "Start Host",
                PurrNetSessionStartOperation.StartServer => "Start Server",
                PurrNetSessionStartOperation.StartClient => "Start Client",
                _ => operation.ToString()
            };
        }
    }
}
