using System;
using System.Collections.Generic;
using UnityEngine;

namespace Arawn.GameCreator2.Networking
{
    /// <summary>The kind of interaction payload most recently exposed to gameplay.</summary>
    public enum NetworkInteractionEventType
    {
        None = 0,
        Approved = 1,
        Rejected = 2,
        Broadcast = 3
    }

    /// <summary>
    /// Transport-neutral interaction notifications and latest-payload context. The Core
    /// controller raises these only after a response or authority broadcast is accepted from
    /// the active transport. Observers are notified; they never execute the interaction again.
    /// </summary>
    public static class NetworkInteractionEvents
    {
        private const int MaxObjectHints = 64;

        private readonly struct ObjectHints
        {
            public ObjectHints(GameObject actor, GameObject target)
            {
                Actor = actor;
                Target = target;
            }

            public GameObject Actor { get; }
            public GameObject Target { get; }
        }

        private static readonly Dictionary<uint, ObjectHints> s_ObjectHints = new(MaxObjectHints);
        private static readonly Queue<uint> s_ObjectHintOrder = new(MaxObjectHints);

        public static event Action<NetworkInteractionResponse> Approved;
        public static event Action<NetworkInteractionResponse> Rejected;
        public static event Action<NetworkInteractionBroadcast> BroadcastReceived;

        public static NetworkInteractionEventType LastEventType { get; private set; }
        public static NetworkInteractionResponse LastResponse { get; private set; }
        public static NetworkInteractionBroadcast LastBroadcast { get; private set; }
        public static bool LastApproved { get; private set; }
        public static InteractionRejectReason LastRejectReason { get; private set; }
        public static InteractionType LastInteractionType { get; private set; }
        public static ushort LastRequestId { get; private set; }
        public static uint LastActorNetworkId { get; private set; }
        public static uint LastCorrelationId { get; private set; }
        public static uint LastCharacterNetworkId { get; private set; }
        public static uint LastTargetNetworkId { get; private set; }
        public static int LastTargetHash { get; private set; }
        public static int LastResultData { get; private set; }
        public static float LastServerTime { get; private set; }
        public static GameObject LastActorObject { get; private set; }
        public static GameObject LastTargetObject { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            Approved = null;
            Rejected = null;
            BroadcastReceived = null;
            LastEventType = NetworkInteractionEventType.None;
            LastResponse = default;
            LastBroadcast = default;
            LastApproved = false;
            LastRejectReason = InteractionRejectReason.None;
            LastInteractionType = InteractionType.Generic;
            LastRequestId = 0;
            LastActorNetworkId = 0;
            LastCorrelationId = 0;
            LastCharacterNetworkId = 0;
            LastTargetNetworkId = 0;
            LastTargetHash = 0;
            LastResultData = 0;
            LastServerTime = 0f;
            LastActorObject = null;
            LastTargetObject = null;
            s_ObjectHints.Clear();
            s_ObjectHintOrder.Clear();
        }

        internal static void SetLocalObjectHints(
            uint correlationId,
            GameObject actor,
            GameObject target)
        {
            if (correlationId == 0) return;

            if (!s_ObjectHints.ContainsKey(correlationId))
            {
                s_ObjectHintOrder.Enqueue(correlationId);
            }

            s_ObjectHints[correlationId] = new ObjectHints(actor, target);
            while (s_ObjectHintOrder.Count > MaxObjectHints)
            {
                uint oldest = s_ObjectHintOrder.Dequeue();
                s_ObjectHints.Remove(oldest);
            }
        }

        internal static void RaiseResponse(NetworkInteractionResponse response)
        {
            SetResponseContext(response);
            InvokeSafely(response.Approved ? Approved : Rejected, response);
        }

        internal static void RaiseBroadcast(NetworkInteractionBroadcast broadcast)
        {
            SetBroadcastContext(broadcast);
            InvokeSafely(BroadcastReceived, broadcast);
        }

        internal static void SetResponseContext(NetworkInteractionResponse response)
        {
            LastEventType = response.Approved
                ? NetworkInteractionEventType.Approved
                : NetworkInteractionEventType.Rejected;
            LastResponse = response;
            LastBroadcast = default;
            LastApproved = response.Approved;
            LastRejectReason = response.RejectReason;
            LastInteractionType = response.InteractionType;
            LastRequestId = response.RequestId;
            LastActorNetworkId = response.ActorNetworkId;
            LastCorrelationId = response.CorrelationId;
            LastCharacterNetworkId = response.CharacterNetworkId;
            LastTargetNetworkId = response.TargetNetworkId;
            LastTargetHash = response.TargetHash;
            LastResultData = response.ResultData;
            LastServerTime = 0f;
            ApplyObjectHints(response.CorrelationId);
        }

        internal static void SetBroadcastContext(NetworkInteractionBroadcast broadcast)
        {
            LastEventType = NetworkInteractionEventType.Broadcast;
            LastResponse = default;
            LastBroadcast = broadcast;
            LastApproved = true;
            LastRejectReason = InteractionRejectReason.None;
            LastInteractionType = broadcast.InteractionType;
            LastRequestId = broadcast.RequestId;
            LastActorNetworkId = broadcast.ActorNetworkId;
            LastCorrelationId = broadcast.CorrelationId;
            LastCharacterNetworkId = broadcast.CharacterNetworkId;
            LastTargetNetworkId = broadcast.TargetNetworkId;
            LastTargetHash = broadcast.TargetHash;
            LastResultData = broadcast.ResultData;
            LastServerTime = broadcast.ServerTime;
            ApplyObjectHints(broadcast.CorrelationId);
        }

        private static void ApplyObjectHints(uint correlationId)
        {
            if (correlationId != 0 && s_ObjectHints.TryGetValue(correlationId, out ObjectHints hints))
            {
                LastActorObject = hints.Actor;
                LastTargetObject = hints.Target;
                return;
            }

            LastActorObject = null;
            LastTargetObject = null;
        }

        private static void InvokeSafely<T>(Action<T> listeners, T payload)
        {
            if (listeners == null) return;

            Delegate[] invocationList = listeners.GetInvocationList();
            for (int i = 0; i < invocationList.Length; i++)
            {
                try
                {
                    ((Action<T>)invocationList[i]).Invoke(payload);
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                }
            }
        }
    }
}
