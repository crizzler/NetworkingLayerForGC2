using System;

namespace Arawn.GameCreator2.Networking
{
    public enum NetworkVariableScope : byte
    {
        LocalName = 0,
        LocalList = 1,
        GlobalName = 2,
        GlobalList = 3
    }

    public enum NetworkVariableOperation : byte
    {
        Set = 0,
        Insert = 1,
        Push = 2,
        Remove = 3,
        Clear = 4,
        Move = 5
    }

    public enum NetworkVariableRejectReason : byte
    {
        None = 0,
        SecurityViolation = 1,
        NotAuthorized = 2,
        ControllerNotFound = 3,
        ProfileNotFound = 4,
        VariableNotAllowed = 5,
        VariableNotFound = 6,
        InvalidOperation = 7,
        UnsupportedValue = 8,
        Timeout = 9
    }

    [Serializable]
    public struct NetworkVariableRequest
    {
        public ushort RequestId;
        public uint ActorNetworkId;
        public uint CorrelationId;
        public uint TargetNetworkId;

        public NetworkVariableScope Scope;
        public NetworkVariableOperation Operation;

        public int ProfileHash;
        public int VariableHash;
        public string VariableName;

        public int Index;
        public int IndexTo;
        public string SerializedValue;

        public float ClientTime;
    }

    [Serializable]
    public struct NetworkVariableResponse
    {
        public ushort RequestId;
        public uint ActorNetworkId;
        public uint CorrelationId;
        public uint TargetNetworkId;

        public bool Authorized;
        public NetworkVariableRejectReason RejectReason;
    }

    [Serializable]
    public struct NetworkVariableBroadcast
    {
        public uint ActorNetworkId;
        public uint TargetNetworkId;

        public NetworkVariableScope Scope;
        public NetworkVariableOperation Operation;

        public int ProfileHash;
        public int VariableHash;
        public string VariableName;

        public int Index;
        public int IndexTo;
        public string SerializedValue;

        public float ServerTime;
    }

    [Serializable]
    public struct NetworkVariableSnapshot
    {
        public NetworkVariableBroadcast[] Changes;
        public float ServerTime;
    }
}
