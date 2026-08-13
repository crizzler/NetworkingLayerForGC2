using System;
using System.Threading.Tasks;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.VisualScripting;
using UnityEngine;

namespace Arawn.GameCreator2.Networking
{
    [Version(1, 0, 0)]
    [Title("Network Request Interaction")]
    [Description("Requests one server-authoritative GC2 interaction and optionally waits for its response")]
    [Category("Network/Core/Interaction/Request")]
    [Parameter("Actor", "Initialized Network Character that owns the request")]
    [Parameter("Target", "GC2 interaction target")]
    [Parameter("Interaction Type", "Semantic interaction sent to the authority and observers")]
    [Parameter("Wait For Response", "Waits until the request receives a response or the local wait timeout elapses")]
    [Parameter("Wait Timeout", "Maximum local wait in seconds; the Core controller still reports any later response")]
    [Keywords("Network", "Core", "Interaction", "Interact", "RPC", "Door", "Open", "Use")]
    [Image(typeof(IconSignal), ColorTheme.Type.Blue, typeof(OverlayBolt))]
    [Serializable]
    public sealed class InstructionNetworkRequestInteraction : Instruction
    {
        [SerializeField] private PropertyGetGameObject m_Actor = GetGameObjectPlayer.Create();
        [SerializeField] private PropertyGetGameObject m_Target = GetGameObjectSelf.Create();
        [SerializeField] private InteractionType m_InteractionType = InteractionType.Use;
        [SerializeField] private bool m_WaitForResponse;
        [SerializeField] private PropertyGetDecimal m_WaitTimeout = new(8f);
        [SerializeField] private bool m_LogDiagnostics;

        public override string Title => $"Network {m_InteractionType} {m_Target}";

        protected override Task Run(Args args)
        {
            if (!NetworkInteractionVisualScriptingSupport.TryResolveManager(out NetworkCoreManager manager))
            {
                LogWarning("No initialized NetworkCoreManager is available.");
                return Task.CompletedTask;
            }

            if (!NetworkInteractionVisualScriptingSupport.TryResolveActor(
                    m_Actor,
                    args,
                    out GameObject actorObject,
                    out uint actorNetworkId))
            {
                LogWarning("Actor did not resolve to an initialized NetworkCharacter.");
                return Task.CompletedTask;
            }

            if (!NetworkInteractionVisualScriptingSupport.TryResolveTarget(
                    manager,
                    m_Target,
                    args,
                    out GameObject targetObject,
                    out uint targetNetworkId,
                    out int targetHash,
                    out Vector3 interactionPosition))
            {
                LogWarning("Target did not resolve to a GameObject.");
                return Task.CompletedTask;
            }

            TaskCompletionSource<NetworkInteractionResponse> completion =
                m_WaitForResponse
                    ? new TaskCompletionSource<NetworkInteractionResponse>(
                        TaskCreationOptions.RunContinuationsAsynchronously)
                    : null;

            bool sent = manager.TryRequestInteraction(
                actorNetworkId,
                targetNetworkId,
                targetHash,
                interactionPosition,
                m_InteractionType,
                out NetworkInteractionRequest request,
                response =>
                {
                    Log(
                        $"response request={response.RequestId} correlation={response.CorrelationId} " +
                        $"approved={response.Approved} reject={response.RejectReason} " +
                        $"type={response.InteractionType} result={response.ResultData}");
                    completion?.TrySetResult(response);
                });

            if (!sent)
            {
                LogWarning(
                    "The interaction request could not be sent. Confirm that this peer is a client " +
                    "and that a Core transport adapter is active.");
                return Task.CompletedTask;
            }

            NetworkInteractionEvents.SetLocalObjectHints(
                request.CorrelationId,
                actorObject,
                targetObject);
            Log(
                $"sent request={request.RequestId} correlation={request.CorrelationId} " +
                $"actor={actorNetworkId} target={targetNetworkId}/{targetHash} " +
                $"type={request.InteractionType}");

            if (!m_WaitForResponse) return Task.CompletedTask;

            double requestedTimeout = m_WaitTimeout.Get(args);
            float timeoutSeconds = double.IsNaN(requestedTimeout) || requestedTimeout <= 0d
                ? 0f
                : (float)Math.Min(requestedTimeout, 86400d);
            return WaitForResponse(completion.Task, timeoutSeconds);
        }

        private async Task WaitForResponse(Task responseTask, float timeoutSeconds)
        {
            if (timeoutSeconds <= 0f)
            {
                await responseTask;
                return;
            }

            Task completed = await Task.WhenAny(
                responseTask,
                Task.Delay(TimeSpan.FromSeconds(timeoutSeconds)));
            if (completed != responseTask)
            {
                LogWarning(
                    $"No interaction response arrived within {timeoutSeconds:F2}s. " +
                    "The Core response/rejection event will still fire if it arrives later.");
            }
        }

        private void Log(string message)
        {
            if (m_LogDiagnostics) Debug.Log($"[Network Interaction Instruction] {message}");
        }

        private void LogWarning(string message)
        {
            if (m_LogDiagnostics) Debug.LogWarning($"[Network Interaction Instruction] {message}");
        }
    }
}
