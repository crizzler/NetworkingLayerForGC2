using System;
using System.Threading.Tasks;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.VisualScripting;
using UnityEngine;

namespace Arawn.GameCreator2.Networking
{
    [Version(1, 0, 0)]
    [Title("Request Network Action")]
    [Description("Requests an allow-listed, typed Network Action from gameplay authority")]
    [Category("Network/Actions/Request Network Action")]
    [Parameter("Actor", "Locally owned Network Character making the request")]
    [Parameter("Target", "Network Action Endpoint that allow-lists the action")]
    [Parameter("Action", "Registered action contract and authority policy")]
    [Parameter("Payload", "Typed payload selected by the action definition")]
    [Keywords("Network", "Action", "RPC", "Event", "Door", "Authority")]
    [Image(typeof(IconSignal), ColorTheme.Type.Blue, typeof(OverlayBolt))]
    [Serializable]
    public class InstructionNetworkRequestAction : Instruction
    {
        [SerializeField] protected PropertyGetGameObject m_Actor = GetGameObjectPlayer.Create();
        [SerializeField] protected PropertyGetGameObject m_Target = GetGameObjectSelf.Create();
        [SerializeField] protected NetworkActionDefinition m_Action;
        [SerializeField] protected NetworkActionPropertyPayload m_Payload = new();
        [SerializeField] protected bool m_WaitForResponse;
        [SerializeField] protected PropertyGetDecimal m_Timeout = new(5d);

        public override string Title => m_Action != null
            ? $"Request Network Action {m_Action.name}"
            : "Request Network Action";

        protected override Task Run(Args args)
        {
            NetworkActionManager manager = NetworkActionManager.Instance;
            if (manager == null || m_Action == null ||
                !NetworkActionVisualScriptingSupport.TryResolve(
                    m_Actor, m_Target, args, out uint actorNetworkId, out var endpoint))
            {
                return Task.CompletedTask;
            }

            NetworkActionPayload payload = m_Payload.Get(m_Action, args);
            if (!m_WaitForResponse)
            {
                manager.RequestAction(actorNetworkId, endpoint, m_Action, payload);
                return Task.CompletedTask;
            }

            var completion = new TaskCompletionSource<NetworkActionResponse>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            bool sent = manager.RequestAction(
                actorNetworkId,
                endpoint,
                m_Action,
                payload,
                callback: response => completion.TrySetResult(response),
                timeout: Mathf.Max(0.1f, (float)m_Timeout.Get(args)));
            return sent
                ? NetworkActionVisualScriptingSupport.WaitForResponse(
                    completion.Task, Mathf.Max(0f, (float)m_Timeout.Get(args)))
                : Task.CompletedTask;
        }
    }

    [Version(1, 0, 0)]
    [Title("Set Network Object State")]
    [Description("Requests a versioned persistent state change with late-join synchronization")]
    [Category("Network/Actions/Set Network Object State")]
    [Keywords("Network", "State", "Door", "Lever", "Open", "Persistent", "Snapshot")]
    [Image(typeof(IconCubeSolid), ColorTheme.Type.Teal, typeof(OverlayBolt))]
    [Serializable]
    public sealed class InstructionNetworkSetObjectState : InstructionNetworkRequestAction
    {
        public override string Title => m_Action != null
            ? $"Set Network State {m_Action.name}"
            : "Set Network Object State";

        protected override Task Run(Args args)
        {
            if (m_Action != null &&
                m_Action.EffectKind != NetworkActionEffectKind.PersistentState)
            {
                Debug.LogWarning(
                    $"[{nameof(InstructionNetworkSetObjectState)}] '{m_Action.name}' is not " +
                    "configured as Persistent State.");
                return Task.CompletedTask;
            }
            return base.Run(args);
        }
    }

    [Version(1, 0, 0)]
    [Title("Apply Network Boolean Rotation")]
    [Description("Applies one of two local rotations from the current confirmed boolean action payload")]
    [Category("Network/Actions/Apply Boolean Rotation")]
    [Parameter("Target", "Visual transform to rotate")]
    [Parameter("False Rotation", "Local rotation used when the payload is false")]
    [Parameter("True Rotation", "Local rotation used when the payload is true")]
    [Keywords("Network", "Action", "Door", "Open", "Close", "Rotation", "State")]
    [Image(typeof(IconRotation), ColorTheme.Type.Teal, typeof(OverlayBolt))]
    [Serializable]
    public sealed class InstructionNetworkApplyBooleanRotation : Instruction
    {
        [SerializeField] private PropertyGetGameObject m_Target = GetGameObjectTarget.Create();
        [SerializeField] private PropertyGetRotation m_FalseRotation =
            new(Quaternion.identity);
        [SerializeField] private PropertyGetRotation m_TrueRotation =
            new(Quaternion.Euler(0f, 90f, 0f));

        public override string Title => $"Apply Network Boolean Rotation to {m_Target}";

        protected override Task Run(Args args)
        {
            if (!NetworkActionStateApplicationSupport.TryGetPayload(
                    NetworkActionPayloadType.Boolean,
                    out NetworkActionPayload payload))
                return Task.CompletedTask;
            GameObject target = m_Target.Get(args);
            if (target == null) return Task.CompletedTask;
            if (!NetworkActionStateApplicationSupport.CanSetTransform(target))
            {
                NetworkActionStateApplicationSupport.WarnUnsafeTransform(target);
                return Task.CompletedTask;
            }
            target.transform.localRotation = payload.BooleanValue
                ? m_TrueRotation.Get(args)
                : m_FalseRotation.Get(args);
            return Task.CompletedTask;
        }
    }
}
