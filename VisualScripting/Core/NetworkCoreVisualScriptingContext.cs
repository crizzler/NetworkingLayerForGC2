using System;
using System.Threading;
using UnityEngine;

namespace Arawn.GameCreator2.Networking
{
    /// <summary>
    /// Identifies the authoritative Core request issued by a GC2 visual-scripting instruction.
    /// Explicit numeric values keep serialized comparisons stable when operations are added.
    /// </summary>
    public enum NetworkCoreVisualScriptingOperation
    {
        None = 0,
        StartRagdoll = 1,
        RecoverRagdoll = 2,
        AttachProp = 3,
        DetachProp = 4,
        DetachPropInstance = 5,
        DetachAllProps = 6,
        SetInvincibility = 7,
        DamagePoise = 8,
        ResetPoise = 9,
        SetBusy = 10
    }

    /// <summary>
    /// Transport-neutral result exposed while an On Network Core Request Completed Trigger runs.
    /// Fields that do not apply to an operation retain their default value.
    /// </summary>
    public readonly struct NetworkCoreVisualScriptingResult
    {
        public NetworkCoreVisualScriptingOperation Operation { get; }
        public uint CharacterNetworkId { get; }
        public ushort RequestId { get; }
        public uint CorrelationId { get; }
        public bool Approved { get; }
        public string RejectReason { get; }
        public int PropInstanceId { get; }
        public float CurrentPoise { get; }
        public bool IsPoiseBroken { get; }
        public float ApprovedInvincibilityDuration { get; }

        internal NetworkCoreVisualScriptingResult(
            NetworkCoreVisualScriptingOperation operation,
            uint characterNetworkId,
            ushort requestId,
            uint correlationId,
            bool approved,
            string rejectReason,
            int propInstanceId = 0,
            float currentPoise = 0f,
            bool isPoiseBroken = false,
            float approvedInvincibilityDuration = 0f)
        {
            Operation = operation;
            CharacterNetworkId = characterNetworkId;
            RequestId = requestId;
            CorrelationId = correlationId;
            Approved = approved;
            RejectReason = rejectReason ?? string.Empty;
            PropInstanceId = propInstanceId;
            CurrentPoise = currentPoise;
            IsPoiseBroken = isPoiseBroken;
            ApprovedInvincibilityDuration = approvedInvincibilityDuration;
        }
    }

    /// <summary>
    /// Shared response context for the Core visual-scripting wrappers. Custom GC2 wrappers can
    /// use the Begin/Complete methods to participate in the same Event, Condition, and Properties.
    /// </summary>
    public static class NetworkCoreVisualScriptingContext
    {
        private sealed class Holder
        {
            public NetworkCoreVisualScriptingResult Value;
        }

        private sealed class Scope : IDisposable
        {
            private readonly Holder m_Previous;
            public Scope(Holder previous) => m_Previous = previous;
            public void Dispose() => s_Current.Value = m_Previous;
        }

        private static readonly AsyncLocal<Holder> s_Current = new();
        public static event Action<NetworkCoreVisualScriptingResult> RequestCompleted;

        public static bool HasResult { get; private set; }
        public static NetworkCoreVisualScriptingResult LastResult { get; private set; }
        public static bool HasCurrent => s_Current.Value != null;
        public static NetworkCoreVisualScriptingResult CurrentResult =>
            s_Current.Value?.Value ?? LastResult;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            RequestCompleted = null;
            HasResult = false;
            LastResult = default;
            s_Current.Value = null;
        }

        public static void BeginRequest(
            NetworkCoreVisualScriptingOperation operation,
            uint characterNetworkId)
        {
            HasResult = false;
            LastResult = new NetworkCoreVisualScriptingResult(
                operation,
                characterNetworkId,
                0,
                0,
                false,
                string.Empty);
        }

        public static void CompleteRequest(
            NetworkCoreVisualScriptingOperation operation,
            uint characterNetworkId,
            NetworkRagdollResponse response)
        {
            Publish(new NetworkCoreVisualScriptingResult(
                operation,
                characterNetworkId,
                response.RequestId,
                response.CorrelationId,
                response.Approved,
                response.RejectReason.ToString()));
        }

        public static void CompleteRequest(
            NetworkCoreVisualScriptingOperation operation,
            uint characterNetworkId,
            NetworkPropResponse response)
        {
            Publish(new NetworkCoreVisualScriptingResult(
                operation,
                characterNetworkId,
                response.RequestId,
                response.CorrelationId,
                response.Approved,
                response.RejectReason.ToString(),
                response.PropInstanceId));
        }

        public static void CompleteRequest(
            NetworkCoreVisualScriptingOperation operation,
            uint characterNetworkId,
            NetworkInvincibilityResponse response)
        {
            Publish(new NetworkCoreVisualScriptingResult(
                operation,
                characterNetworkId,
                response.RequestId,
                response.CorrelationId,
                response.Approved,
                response.RejectReason.ToString(),
                approvedInvincibilityDuration: response.ApprovedDuration));
        }

        public static void CompleteRequest(
            NetworkCoreVisualScriptingOperation operation,
            uint characterNetworkId,
            NetworkPoiseResponse response)
        {
            Publish(new NetworkCoreVisualScriptingResult(
                operation,
                characterNetworkId,
                response.RequestId,
                response.CorrelationId,
                response.Approved,
                response.RejectReason.ToString(),
                currentPoise: response.CurrentPoise,
                isPoiseBroken: response.IsBroken));
        }

        public static void CompleteRequest(
            NetworkCoreVisualScriptingOperation operation,
            uint characterNetworkId,
            NetworkBusyResponse response)
        {
            Publish(new NetworkCoreVisualScriptingResult(
                operation,
                characterNetworkId,
                response.RequestId,
                response.CorrelationId,
                response.Approved,
                response.RejectReason.ToString()));
        }

        private static void Publish(NetworkCoreVisualScriptingResult result)
        {
            LastResult = result;
            HasResult = true;

            Action<NetworkCoreVisualScriptingResult> listeners = RequestCompleted;
            if (listeners == null) return;

            Delegate[] invocationList = listeners.GetInvocationList();
            for (int i = 0; i < invocationList.Length; i++)
            {
                try
                {
                    ((Action<NetworkCoreVisualScriptingResult>)invocationList[i]).Invoke(result);
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                }
            }
        }

        internal static IDisposable Push(in NetworkCoreVisualScriptingResult result)
        {
            Holder previous = s_Current.Value;
            s_Current.Value = new Holder { Value = result };
            return new Scope(previous);
        }
    }
}
