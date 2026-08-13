using System;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.VisualScripting;
using UnityEngine;

namespace Arawn.GameCreator2.Networking
{
    [Title("Network Action Is")]
    [Description("Checks the Network Action currently executing this GC2 list")]
    [Category("Network/Actions/Current Action Is")]
    [Image(typeof(IconSignal), ColorTheme.Type.Blue)]
    [Serializable]
    public sealed class ConditionNetworkActionIs : Condition
    {
        [SerializeField] private NetworkActionDefinition m_Action;
        protected override string Summary => m_Action != null
            ? $"Network Action is {m_Action.name}"
            : "Network Action is <none>";
        protected override bool Run(Args args) =>
            NetworkActionContext.HasCurrent &&
            NetworkActionVisualScriptingSupport.Matches(
                m_Action, NetworkActionContext.Current);
    }

    [Title("Network Action Rejection Is")]
    [Description("Checks the rejection reason in the current Network Action context")]
    [Category("Network/Actions/Rejection Is")]
    [Image(typeof(IconSignal), ColorTheme.Type.Red)]
    [Serializable]
    public sealed class ConditionNetworkActionRejectionIs : Condition
    {
        [SerializeField] private NetworkActionRejectReason m_Reason =
            NetworkActionRejectReason.NotAuthorized;
        protected override string Summary => $"Network Action rejection is {m_Reason}";
        protected override bool Run(Args args) =>
            NetworkActionContext.HasCurrent &&
            NetworkActionContext.Current.RejectReason == m_Reason;
    }

    [Title("Network Action Is Snapshot")]
    [Description("Checks whether the current action applies persistent late-join state")]
    [Category("Network/Actions/Is Snapshot")]
    [Image(typeof(IconSignal), ColorTheme.Type.Teal)]
    [Serializable]
    public sealed class ConditionNetworkActionIsSnapshot : Condition
    {
        protected override string Summary => "Network Action is Snapshot";
        protected override bool Run(Args args) =>
            NetworkActionContext.HasCurrent && NetworkActionContext.Current.IsSnapshot;
    }

    [Title("Network Object State Equals")]
    [Description("Checks the latest authority-confirmed persistent state for an endpoint")]
    [Category("Network/Actions/Object State Equals")]
    [Image(typeof(IconCubeSolid), ColorTheme.Type.Teal)]
    [Serializable]
    public sealed class ConditionNetworkObjectStateEquals : Condition
    {
        [SerializeField] private PropertyGetGameObject m_Target = GetGameObjectSelf.Create();
        [SerializeField] private NetworkActionDefinition m_Action;
        [SerializeField] private NetworkActionPropertyPayload m_Value = new();

        protected override string Summary => m_Action != null
            ? $"Network State {m_Action.name} equals Value"
            : "Network Object State equals Value";

        protected override bool Run(Args args)
        {
            NetworkActionManager manager = NetworkActionManager.Instance;
            GameObject target = m_Target.Get(args);
            NetworkActionEndpoint endpoint = target != null
                ? target.GetComponent<NetworkActionEndpoint>() ??
                  target.GetComponentInParent<NetworkActionEndpoint>() ??
                  target.GetComponentInChildren<NetworkActionEndpoint>(true)
                : null;
            return manager != null && endpoint != null && m_Action != null &&
                   manager.TryGetPersistentState(
                       endpoint, m_Action, out NetworkActionPayload current, out _) &&
                   current.Equals(m_Value.Get(m_Action, args));
        }
    }
}
