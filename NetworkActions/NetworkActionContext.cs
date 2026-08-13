using System;
using System.Threading;
using UnityEngine;

namespace Arawn.GameCreator2.Networking
{
    public enum NetworkActionContextPhase : byte
    {
        None = 0,
        Approved = 1,
        Rejected = 2,
        AuthorityCommit = 3,
        Applied = 4,
        SnapshotApplied = 5
    }

    public readonly struct NetworkActionExecutionContext
    {
        public readonly NetworkActionContextPhase Phase;
        public readonly NetworkActionRequest Request;
        public readonly NetworkActionResponse Response;
        public readonly NetworkActionBroadcast Broadcast;
        public readonly GameObject Actor;
        public readonly GameObject Target;

        public NetworkActionExecutionContext(
            NetworkActionContextPhase phase,
            in NetworkActionRequest request,
            in NetworkActionResponse response,
            in NetworkActionBroadcast broadcast,
            GameObject actor,
            GameObject target)
        {
            Phase = phase;
            Request = request;
            Response = response;
            Broadcast = broadcast;
            Actor = actor;
            Target = target;
        }

        public bool IsValid => Phase != NetworkActionContextPhase.None;
        public bool IsApproved => Response.Authorized ||
                                  Phase == NetworkActionContextPhase.AuthorityCommit ||
                                  Phase == NetworkActionContextPhase.Applied ||
                                  Phase == NetworkActionContextPhase.SnapshotApplied;
        public bool IsSnapshot => Broadcast.IsSnapshot ||
                                  Phase == NetworkActionContextPhase.SnapshotApplied;
        public string ActionId => !string.IsNullOrEmpty(Broadcast.ActionId)
            ? Broadcast.ActionId
            : !string.IsNullOrEmpty(Response.ActionId)
                ? Response.ActionId
                : Request.ActionId ?? string.Empty;
        public int ActionHash => Broadcast.ActionHash != 0
            ? Broadcast.ActionHash
            : Response.ActionHash != 0 ? Response.ActionHash : Request.ActionHash;
        public uint ActorNetworkId => Broadcast.ActorNetworkId != 0
            ? Broadcast.ActorNetworkId
            : Response.ActorNetworkId != 0 ? Response.ActorNetworkId : Request.ActorNetworkId;
        public uint TargetNetworkId => Broadcast.TargetNetworkId != 0
            ? Broadcast.TargetNetworkId
            : Response.TargetNetworkId != 0 ? Response.TargetNetworkId : Request.TargetNetworkId;
        public uint Revision => Broadcast.Revision != 0 ? Broadcast.Revision : Response.Revision;
        public float ServerTime => Broadcast.ServerTime != 0f
            ? Broadcast.ServerTime
            : Response.ServerTime;
        public NetworkActionPayload Payload =>
            Phase == NetworkActionContextPhase.Rejected ||
            Phase == NetworkActionContextPhase.Approved
                ? Response.CanonicalPayload
                : Broadcast.Payload;
        public NetworkActionRejectReason RejectReason => Response.RejectReason;

        public static NetworkActionExecutionContext FromResponse(
            in NetworkActionResponse response,
            GameObject actor,
            GameObject target)
        {
            NetworkActionRequest request = default;
            NetworkActionBroadcast broadcast = default;
            return new NetworkActionExecutionContext(
                response.Authorized
                    ? NetworkActionContextPhase.Approved
                    : NetworkActionContextPhase.Rejected,
                in request,
                in response,
                in broadcast,
                actor,
                target);
        }

        public static NetworkActionExecutionContext FromBroadcast(
            in NetworkActionBroadcast broadcast,
            GameObject actor,
            GameObject target,
            bool authorityCommit = false)
        {
            NetworkActionRequest request = default;
            NetworkActionResponse response = default;
            NetworkActionContextPhase phase = authorityCommit
                ? NetworkActionContextPhase.AuthorityCommit
                : broadcast.IsSnapshot
                    ? NetworkActionContextPhase.SnapshotApplied
                    : NetworkActionContextPhase.Applied;
            return new NetworkActionExecutionContext(
                phase,
                in request,
                in response,
                in broadcast,
                actor,
                target);
        }
    }

    /// <summary>
    /// Per-async-flow GC2 execution context. This deliberately avoids a global LastAction value:
    /// two overlapping Instruction Lists retain their own actor, target, payload, and revision.
    /// </summary>
    public static class NetworkActionContext
    {
        private sealed class ContextHolder
        {
            public NetworkActionExecutionContext Value;
        }

        private sealed class Scope : IDisposable
        {
            private readonly ContextHolder m_Previous;
            private bool m_Disposed;

            public Scope(ContextHolder previous) => m_Previous = previous;

            public void Dispose()
            {
                if (m_Disposed) return;
                m_Disposed = true;
                s_Current.Value = m_Previous;
            }
        }

        private static readonly AsyncLocal<ContextHolder> s_Current = new();

        public static bool HasCurrent => s_Current.Value != null && s_Current.Value.Value.IsValid;
        public static NetworkActionExecutionContext Current =>
            s_Current.Value?.Value ?? default;

        public static IDisposable Push(in NetworkActionExecutionContext context)
        {
            ContextHolder previous = s_Current.Value;
            s_Current.Value = new ContextHolder { Value = context };
            return new Scope(previous);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() => s_Current.Value = null;
    }

    public static class NetworkActionEvents
    {
        public static event Action<NetworkActionExecutionContext> Approved;
        public static event Action<NetworkActionExecutionContext> Rejected;
        public static event Action<NetworkActionExecutionContext> Applied;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            Approved = null;
            Rejected = null;
            Applied = null;
        }

        internal static void RaiseResponse(in NetworkActionExecutionContext context)
        {
            InvokeSafely(context.IsApproved ? Approved : Rejected, context, context.Target);
        }

        internal static void RaiseApplied(in NetworkActionExecutionContext context)
        {
            InvokeSafely(Applied, context, context.Target);
        }

        private static void InvokeSafely(
            Action<NetworkActionExecutionContext> listeners,
            in NetworkActionExecutionContext context,
            UnityEngine.Object unityContext)
        {
            if (listeners == null) return;
            Delegate[] invocationList = listeners.GetInvocationList();
            for (int i = 0; i < invocationList.Length; i++)
            {
                try
                {
                    ((Action<NetworkActionExecutionContext>)invocationList[i]).Invoke(context);
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception, unityContext);
                }
            }
        }
    }
}
