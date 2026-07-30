#if GC2_TRAVERSAL
using System;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Traversal
{
    public enum TraversalActionType : byte
    {
        RunTraverseLink = 0,
        EnterTraverseInteractive = 1,
        TryCancel = 2,
        ForceCancel = 3,
        TryJump = 4,
        TryAction = 5,
        TryStateEnter = 6,
        TryStateExit = 7
    }

    public enum TraversalRejectionReason : byte
    {
        None = 0,
        NotAuthorized = 1,
        SecurityViolation = 2,
        ProtocolMismatch = 3,
        RateLimitExceeded = 4,
        TargetNotFound = 5,
        InvalidAction = 6,
        InvalidState = 7,
        IdentityMismatch = 8,
        Exception = 9,
        RouteUnavailable = 10,
        ControllerNotReady = 11,
        StartNotAcknowledged = 12,
        StaleState = 13,
        UnsupportedPredictionBackend = 14,
        PatchRequired = 15,
        RuntimeNotReady = 16,
        UnresolvedMotion = 17,
        UnusableMotion = 18,
        StartTimeout = 19
    }

    /// <summary>
    /// Describes whether an owner traversal request can reach the authoritative server.
    /// A route must explicitly report Ready; unknown or partially initialized routes fail closed.
    /// </summary>
    public enum TraversalRouteStatus : byte
    {
        Unknown = 0,
        Ready = 1,
        ManagerUnavailable = 2,
        TransportUnavailable = 3,
        ClientNotRunning = 4,
        ServerNotRunning = 5,
        LocalPlayerNotReady = 6,
        ControllerNotReady = 7,
        PatchRequired = 8,
        UnsupportedPredictionBackend = 9
    }

    /// <summary>
    /// Identifies which active traversal state is safe to reconstruct from a persistent snapshot.
    /// Traverse links are transient; interactive traverses can be restored for late joiners.
    /// </summary>
    public enum TraversalSnapshotKind : byte
    {
        None = 0,
        ActiveLink = 1,
        ActiveInteractive = 2
    }

    public static class NetworkTraversalVersion
    {
        /// <summary>
        /// Wrap-safe monotonic version comparison. Version zero is reserved for unversioned state.
        /// </summary>
        public static bool IsNewer(uint candidate, uint baseline)
        {
            if (candidate == baseline || candidate == 0) return false;
            if (baseline == 0) return true;
            return unchecked((int)(candidate - baseline)) > 0;
        }
    }

    /// <summary>
    /// Optional override for focused climb diagnostics. Enable this temporarily when a capture
    /// is needed. The channel only focuses characters while they use Free Climb or Ledge Climb
    /// and rate-limits continuous telemetry.
    /// </summary>
    public static class NetworkTraversalDebug
    {
        public static bool ForceClimbDiagnostics = false;
    }

    [Serializable]
    public struct NetworkTraversalRequest
    {
        public ushort RequestId;
        public uint ActorNetworkId;
        public uint CorrelationId;
        public uint TargetNetworkId;

        public TraversalActionType Action;

        public int TraverseHash;
        public string TraverseIdString;

        public int ActionIdHash;
        public string ActionIdString;

        public int StateIdHash;
        public string StateIdString;

        public uint ArgsSelfNetworkId;
        public uint ArgsTargetNetworkId;
    }

    [Serializable]
    public struct NetworkTraversalResponse
    {
        public ushort RequestId;
        public uint ActorNetworkId;
        public uint CorrelationId;

        public TraversalActionType Action;
        public bool Authorized;
        public bool Applied;
        public TraversalRejectionReason RejectionReason;

        public int TraverseHash;
        public string TraverseIdString;

        public int ActionIdHash;
        public string ActionIdString;

        public int StateIdHash;
        public string StateIdString;

        public uint ArgsSelfNetworkId;
        public uint ArgsTargetNetworkId;

        public bool IsTraversing;
        public uint StateVersion;
        public string Error;
    }

    [Serializable]
    public struct NetworkTraversalBroadcast
    {
        public uint NetworkId;
        public uint ActorNetworkId;
        public uint CorrelationId;

        public TraversalActionType Action;

        public int TraverseHash;
        public string TraverseIdString;

        public int ActionIdHash;
        public string ActionIdString;

        public int StateIdHash;
        public string StateIdString;

        public uint ArgsSelfNetworkId;
        public uint ArgsTargetNetworkId;

        public bool IsTraversing;
        public float ServerTime;
        public uint StateVersion;
    }

    [Serializable]
    public struct NetworkTraversalSnapshot
    {
        public uint NetworkId;
        public float ServerTime;

        public bool IsTraversing;
        public int TraverseHash;
        public string TraverseIdString;

        public uint StateVersion;
        public TraversalSnapshotKind Kind;
        public bool HasRelativePose;
        public Vector3 RelativePosition;
        public Quaternion RelativeRotation;
    }
}
#endif
