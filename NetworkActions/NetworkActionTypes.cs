using System;
using UnityEngine;

namespace Arawn.GameCreator2.Networking
{
    public readonly struct NetworkActionEndpointKey : IEquatable<NetworkActionEndpointKey>
    {
        public readonly uint NetworkId;
        public readonly int EndpointHash;

        public NetworkActionEndpointKey(uint networkId, int endpointHash)
        {
            NetworkId = networkId;
            EndpointHash = endpointHash;
        }

        public bool IsValid => NetworkId != 0 && EndpointHash != 0;
        public bool Equals(NetworkActionEndpointKey other) =>
            NetworkId == other.NetworkId && EndpointHash == other.EndpointHash;
        public override bool Equals(object obj) =>
            obj is NetworkActionEndpointKey other && Equals(other);
        public override int GetHashCode() => unchecked(((int)NetworkId * 397) ^ EndpointHash);
        public override string ToString() => $"{NetworkId}:{EndpointHash}";
    }

    public enum NetworkActionPayloadType : byte
    {
        None = 0,
        Boolean = 1,
        Number = 2,
        String = 3,
        Vector3 = 4
    }

    public enum NetworkActionEffectKind : byte
    {
        TransientEvent = 0,
        PersistentState = 1
    }

    public enum NetworkActionAuthorityPolicy : byte
    {
        AuthorityOnly = 0,
        OwnerRequest = 1,
        AnyAuthenticatedRequest = 2
    }

    public enum NetworkActionRecipientPolicy : byte
    {
        RelevantObservers = 0,
        AllClients = 1,
        Requester = 2,
        TargetOwner = 3
    }

    public enum NetworkActionRejectReason : byte
    {
        None = 0,
        NotRunning = 1,
        NotAuthority = 2,
        SecurityViolation = 3,
        NotAuthorized = 4,
        ActorNotFound = 5,
        TargetNotFound = 6,
        ActionNotFound = 7,
        SchemaMismatch = 8,
        InvalidPayload = 9,
        OutOfRange = 10,
        LineOfSightBlocked = 11,
        ConditionsFailed = 12,
        Cooldown = 13,
        RevisionConflict = 14,
        HandlerRejected = 15,
        Timeout = 16,
        TransportUnavailable = 17
    }

    [Serializable]
    public struct NetworkActionPayload : IEquatable<NetworkActionPayload>
    {
        public NetworkActionPayloadType Type;
        public bool BooleanValue;
        public float NumberValue;
        public string StringValue;
        public Vector3 Vector3Value;

        public static NetworkActionPayload None => new()
        {
            Type = NetworkActionPayloadType.None,
            StringValue = string.Empty
        };

        public static NetworkActionPayload FromBoolean(bool value) => new()
        {
            Type = NetworkActionPayloadType.Boolean,
            BooleanValue = value,
            StringValue = string.Empty
        };

        public static NetworkActionPayload FromNumber(float value) => new()
        {
            Type = NetworkActionPayloadType.Number,
            NumberValue = value,
            StringValue = string.Empty
        };

        public static NetworkActionPayload FromString(string value) => new()
        {
            Type = NetworkActionPayloadType.String,
            StringValue = value ?? string.Empty
        };

        public static NetworkActionPayload FromVector3(Vector3 value) => new()
        {
            Type = NetworkActionPayloadType.Vector3,
            Vector3Value = value,
            StringValue = string.Empty
        };

        public bool Equals(NetworkActionPayload other)
        {
            return Type == other.Type &&
                   BooleanValue == other.BooleanValue &&
                   NumberValue.Equals(other.NumberValue) &&
                   string.Equals(StringValue ?? string.Empty, other.StringValue ?? string.Empty,
                       StringComparison.Ordinal) &&
                   Vector3Value == other.Vector3Value;
        }

        public override bool Equals(object obj) =>
            obj is NetworkActionPayload other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = (int)Type;
                hash = (hash * 397) ^ BooleanValue.GetHashCode();
                hash = (hash * 397) ^ NumberValue.GetHashCode();
                hash = (hash * 397) ^ (StringValue?.GetHashCode() ?? 0);
                hash = (hash * 397) ^ Vector3Value.GetHashCode();
                return hash;
            }
        }
    }

    [Serializable]
    public struct NetworkActionRequest
    {
        public ushort RequestId;
        public uint ActorNetworkId;
        public uint CorrelationId;
        public uint TargetNetworkId;
        public int EndpointHash;
        public int ActionHash;
        public string ActionId;
        public ushort SchemaVersion;
        public NetworkActionPayload Payload;
        public uint ExpectedRevision;
        public float ClientTime;
    }

    [Serializable]
    public struct NetworkActionResponse
    {
        public ushort RequestId;
        public uint ActorNetworkId;
        public uint CorrelationId;
        public uint TargetNetworkId;
        public int EndpointHash;
        public int ActionHash;
        public string ActionId;
        public bool Authorized;
        public NetworkActionRejectReason RejectReason;
        public NetworkActionPayload CanonicalPayload;
        public uint Revision;
        public float ServerTime;
    }

    [Serializable]
    public struct NetworkActionBroadcast
    {
        public ushort RequestId;
        public uint ActorNetworkId;
        public uint CorrelationId;
        public uint TargetNetworkId;
        public int EndpointHash;
        public int ActionHash;
        public string ActionId;
        public ushort SchemaVersion;
        public NetworkActionEffectKind EffectKind;
        public NetworkActionRecipientPolicy RecipientPolicy;
        public bool Reliable;
        public NetworkActionPayload Payload;
        public uint Revision;
        public uint AuthorityEpoch;
        public float ServerTime;
        public bool IsSnapshot;
    }

    [Serializable]
    public struct NetworkActionSnapshot
    {
        public NetworkActionBroadcast[] Entries;
        public uint AuthorityEpoch;
        public float ServerTime;
    }

    public interface INetworkActionAuthorityHandler
    {
        bool TryValidateAndNormalize(
            NetworkActionEndpoint endpoint,
            NetworkActionDefinition definition,
            in NetworkActionRequest request,
            ref NetworkActionPayload canonicalPayload,
            out NetworkActionRejectReason rejectReason);

        void OnAuthorityCommitted(
            NetworkActionEndpoint endpoint,
            NetworkActionDefinition definition,
            in NetworkActionBroadcast broadcast);
    }
}
