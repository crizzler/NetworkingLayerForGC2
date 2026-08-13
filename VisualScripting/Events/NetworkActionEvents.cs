using System;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.VisualScripting;
using Event = GameCreator.Runtime.VisualScripting.Event;

namespace Arawn.GameCreator2.Networking
{
    [Title("On Network Action Approved")]
    [Description("Runs for the requester after gameplay authority approves a Network Action")]
    [Category("Network/Actions/On Approved")]
    [Image(typeof(IconSignal), ColorTheme.Type.Green)]
    [Serializable]
    public sealed class EventNetworkActionApproved : Event
    {
        [UnityEngine.SerializeField] private NetworkActionDefinition m_Action;
        [UnityEngine.SerializeField] private PropertyGetGameObject m_Target =
            GetGameObjectSelf.Create();
        protected override void OnEnable(Trigger trigger)
        {
            base.OnEnable(trigger);
            NetworkActionEvents.Approved += OnEvent;
        }
        protected override void OnDisable(Trigger trigger)
        {
            NetworkActionEvents.Approved -= OnEvent;
            base.OnDisable(trigger);
        }
        private void OnEvent(NetworkActionExecutionContext context)
        {
            if (NetworkActionVisualScriptingSupport.Matches(m_Action, context) &&
                NetworkActionVisualScriptingSupport.MatchesTarget(m_Target, Self, context))
                NetworkActionVisualScriptingSupport.ExecuteTrigger(m_Trigger, Self, context);
        }
    }

    [Title("On Network Action Rejected")]
    [Description("Runs for the requester when gameplay authority rejects a Network Action")]
    [Category("Network/Actions/On Rejected")]
    [Image(typeof(IconSignal), ColorTheme.Type.Red)]
    [Serializable]
    public sealed class EventNetworkActionRejected : Event
    {
        [UnityEngine.SerializeField] private NetworkActionDefinition m_Action;
        [UnityEngine.SerializeField] private PropertyGetGameObject m_Target =
            GetGameObjectSelf.Create();
        protected override void OnEnable(Trigger trigger)
        {
            base.OnEnable(trigger);
            NetworkActionEvents.Rejected += OnEvent;
        }
        protected override void OnDisable(Trigger trigger)
        {
            NetworkActionEvents.Rejected -= OnEvent;
            base.OnDisable(trigger);
        }
        private void OnEvent(NetworkActionExecutionContext context)
        {
            if (NetworkActionVisualScriptingSupport.Matches(m_Action, context) &&
                NetworkActionVisualScriptingSupport.MatchesTarget(m_Target, Self, context))
                NetworkActionVisualScriptingSupport.ExecuteTrigger(m_Trigger, Self, context);
        }
    }

    [Title("On Network Action Applied")]
    [Description("Runs when an authority-approved action or persistent snapshot is applied locally")]
    [Category("Network/Actions/On Applied")]
    [Image(typeof(IconSignal), ColorTheme.Type.Blue)]
    [Serializable]
    public sealed class EventNetworkActionApplied : Event
    {
        [UnityEngine.SerializeField] private NetworkActionDefinition m_Action;
        [UnityEngine.SerializeField] private PropertyGetGameObject m_Target =
            GetGameObjectSelf.Create();
        protected override void OnEnable(Trigger trigger)
        {
            base.OnEnable(trigger);
            NetworkActionEvents.Applied += OnEvent;
        }
        protected override void OnDisable(Trigger trigger)
        {
            NetworkActionEvents.Applied -= OnEvent;
            base.OnDisable(trigger);
        }
        private void OnEvent(NetworkActionExecutionContext context)
        {
            if (NetworkActionVisualScriptingSupport.Matches(m_Action, context) &&
                NetworkActionVisualScriptingSupport.MatchesTarget(m_Target, Self, context))
                NetworkActionVisualScriptingSupport.ExecuteTrigger(m_Trigger, Self, context);
        }
    }
}
