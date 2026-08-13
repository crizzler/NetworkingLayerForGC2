using System;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.VisualScripting;
using Event = GameCreator.Runtime.VisualScripting.Event;

namespace Arawn.GameCreator2.Networking
{
    [Title("On Network Interaction Approved")]
    [Description("Executed for the local requester when the authority approves an interaction")]
    [Category("Network/Core/Interaction/On Approved")]
    [Keywords("Network", "Core", "Interaction", "Approved", "Accepted", "Response")]
    [Image(typeof(IconSignal), ColorTheme.Type.Green)]
    [Serializable]
    public sealed class EventNetworkInteractionApproved : Event
    {
        [UnityEngine.SerializeField] private InteractionType m_InteractionType = InteractionType.Use;
        [UnityEngine.SerializeField] private PropertyGetGameObject m_Target =
            GetGameObjectSelf.Create();
        protected override void OnEnable(Trigger trigger)
        {
            base.OnEnable(trigger);
            NetworkInteractionEvents.Approved += OnApproved;
        }

        protected override void OnDisable(Trigger trigger)
        {
            NetworkInteractionEvents.Approved -= OnApproved;
            base.OnDisable(trigger);
        }

        private void OnApproved(NetworkInteractionResponse response)
        {
            if (!NetworkInteractionVisualScriptingSupport.Matches(
                    m_InteractionType, m_Target, Self, response)) return;
            NetworkInteractionVisualScriptingSupport.DispatchNextFrame(
                m_Trigger,
                Self,
                response,
                nameof(EventNetworkInteractionApproved));
        }
    }

    [Title("On Network Interaction Rejected")]
    [Description("Executed for the local requester when the authority rejects or times out an interaction")]
    [Category("Network/Core/Interaction/On Rejected")]
    [Keywords("Network", "Core", "Interaction", "Rejected", "Denied", "Timeout", "Response")]
    [Image(typeof(IconSignal), ColorTheme.Type.Red)]
    [Serializable]
    public sealed class EventNetworkInteractionRejected : Event
    {
        [UnityEngine.SerializeField] private InteractionType m_InteractionType = InteractionType.Use;
        [UnityEngine.SerializeField] private PropertyGetGameObject m_Target =
            GetGameObjectSelf.Create();
        protected override void OnEnable(Trigger trigger)
        {
            base.OnEnable(trigger);
            NetworkInteractionEvents.Rejected += OnRejected;
        }

        protected override void OnDisable(Trigger trigger)
        {
            NetworkInteractionEvents.Rejected -= OnRejected;
            base.OnDisable(trigger);
        }

        private void OnRejected(NetworkInteractionResponse response)
        {
            if (!NetworkInteractionVisualScriptingSupport.Matches(
                    m_InteractionType, m_Target, Self, response)) return;
            NetworkInteractionVisualScriptingSupport.DispatchNextFrame(
                m_Trigger,
                Self,
                response,
                nameof(EventNetworkInteractionRejected));
        }
    }

    [Title("On Network Interaction Broadcast")]
    [Description("Executed on observing clients after the authority completes an interaction; this does not execute the interaction again")]
    [Category("Network/Core/Interaction/On Broadcast")]
    [Keywords("Network", "Core", "Interaction", "Broadcast", "Observer", "RPC")]
    [Image(typeof(IconSignal), ColorTheme.Type.Blue)]
    [Serializable]
    public sealed class EventNetworkInteractionBroadcast : Event
    {
        [UnityEngine.SerializeField] private InteractionType m_InteractionType = InteractionType.Use;
        [UnityEngine.SerializeField] private PropertyGetGameObject m_Target =
            GetGameObjectSelf.Create();
        protected override void OnEnable(Trigger trigger)
        {
            base.OnEnable(trigger);
            NetworkInteractionEvents.BroadcastReceived += OnBroadcast;
        }

        protected override void OnDisable(Trigger trigger)
        {
            NetworkInteractionEvents.BroadcastReceived -= OnBroadcast;
            base.OnDisable(trigger);
        }

        private void OnBroadcast(NetworkInteractionBroadcast broadcast)
        {
            if (!NetworkInteractionVisualScriptingSupport.Matches(
                    m_InteractionType, m_Target, Self, broadcast)) return;
            NetworkInteractionVisualScriptingSupport.DispatchNextFrame(
                m_Trigger,
                Self,
                broadcast,
                nameof(EventNetworkInteractionBroadcast));
        }
    }
}
