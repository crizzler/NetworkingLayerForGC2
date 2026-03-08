#if GC2_TRAVERSAL
using System;

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
        Exception = 9
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
    }

    [Serializable]
    public struct NetworkTraversalSnapshot
    {
        public uint NetworkId;
        public float ServerTime;

        public bool IsTraversing;
        public int TraverseHash;
        public string TraverseIdString;
    }
}
#endif
