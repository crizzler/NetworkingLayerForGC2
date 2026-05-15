using System;
using System.Collections.Generic;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.Variables;
using UnityEngine;

namespace Arawn.GameCreator2.Networking
{
    [AddComponentMenu("Game Creator/Network/Variables/Network Variable Controller")]
    [DisallowMultipleComponent]
    public sealed class NetworkVariableController : MonoBehaviour
    {
        [Header("Bindings")]
        [SerializeField] private NetworkVariableProfile m_Profile;
        [SerializeField] private LocalNameVariables m_LocalNameVariables;
        [SerializeField] private LocalListVariables m_LocalListVariables;
        [SerializeField] private NetworkCharacter m_NetworkCharacter;

        [Header("Runtime")]
        [SerializeField] private bool m_AutoFindComponents = true;

        [Header("Debug")]
        [SerializeField] private bool m_LogNetworkMessages;

        private bool m_IsApplyingNetworkChange;
        private uint m_RegisteredNetworkId;
        private uint m_TransportNetworkId;
        private bool m_IsTransportController;
        private bool m_HasTransportOwner;
        private uint m_TransportOwnerClientId = NetworkTransportBridge.InvalidClientId;
        private ushort m_NextRequestId = 1;

        private Action<string> m_NameCallback;
        private Action<ListVariableRuntime.Change, int> m_ListCallback;

        public NetworkVariableProfile Profile => m_Profile;
        public LocalNameVariables LocalNameVariables => m_LocalNameVariables;
        public LocalListVariables LocalListVariables => m_LocalListVariables;
        public NetworkCharacter NetworkCharacter => m_NetworkCharacter;
        public uint NetworkId => ResolveNetworkId();
        public uint ActorNetworkId => m_NetworkCharacter != null ? m_NetworkCharacter.NetworkId : 0u;

        private void Awake()
        {
            if (m_AutoFindComponents)
            {
                if (m_LocalNameVariables == null) m_LocalNameVariables = GetComponent<LocalNameVariables>();
                if (m_LocalListVariables == null) m_LocalListVariables = GetComponent<LocalListVariables>();
                if (m_NetworkCharacter == null) m_NetworkCharacter = GetComponent<NetworkCharacter>();
            }

            m_NameCallback = HandleLocalNameChanged;
            m_ListCallback = HandleLocalListChanged;
        }

        private void OnEnable()
        {
            RegisterVariableCallbacks();
            TryRegisterWithManager();
        }

        private void Start()
        {
            TryRegisterWithManager();
        }

        private void Update()
        {
            TryRegisterWithManager();
        }

        private void OnDisable()
        {
            UnregisterVariableCallbacks();
            UnregisterFromManager();
        }

        public void ApplyTransportNetworkIdentity(
            uint networkId,
            bool isController,
            uint ownerClientId = NetworkTransportBridge.InvalidClientId,
            bool registerWithManager = true)
        {
            if (networkId == 0) return;

            bool changed = m_TransportNetworkId != networkId;
            m_TransportNetworkId = networkId;
            m_IsTransportController = isController;
            m_HasTransportOwner = NetworkTransportBridge.IsValidClientId(ownerClientId);
            m_TransportOwnerClientId = m_HasTransportOwner
                ? ownerClientId
                : NetworkTransportBridge.InvalidClientId;

            if (changed && m_RegisteredNetworkId != 0 && m_RegisteredNetworkId != NetworkId)
            {
                UnregisterFromManager();
            }

            if (registerWithManager)
            {
                TryRegisterWithManager();
            }
        }

        public void ClearTransportNetworkIdentity(uint networkId)
        {
            if (networkId != 0 && m_TransportNetworkId != networkId) return;

            bool registeredByTransport = m_RegisteredNetworkId != 0 &&
                                         m_NetworkCharacter == null &&
                                         m_RegisteredNetworkId == m_TransportNetworkId;

            m_TransportNetworkId = 0;
            m_IsTransportController = false;
            m_HasTransportOwner = false;
            m_TransportOwnerClientId = NetworkTransportBridge.InvalidClientId;

            if (registeredByTransport)
            {
                UnregisterFromManager();
            }
        }

        public bool TryGetOwnerClientId(out uint ownerClientId)
        {
            ownerClientId = m_TransportOwnerClientId;
            return m_HasTransportOwner && NetworkTransportBridge.IsValidClientId(ownerClientId);
        }

        public bool RequestSetLocalName(string variableName, object value, uint actorNetworkId = 0)
        {
            if (m_Profile == null || !m_Profile.IsLocalNameAllowed(variableName, true))
            {
                LogWarning($"Local name variable '{variableName}' is not enabled for network writes.");
                return false;
            }

            if (!NetworkVariableSerializer.TrySerialize(value, out string serialized))
            {
                LogWarning($"Local name variable '{variableName}' value type is not supported.");
                return false;
            }

            return SendRequest(new NetworkVariableRequest
            {
                Scope = NetworkVariableScope.LocalName,
                Operation = NetworkVariableOperation.Set,
                TargetNetworkId = NetworkId,
                ProfileHash = m_Profile.ProfileHash,
                VariableHash = NetworkVariableProfile.GetVariableHash(variableName),
                VariableName = variableName,
                SerializedValue = serialized,
                ClientTime = Time.time
            }, actorNetworkId);
        }

        public bool RequestSetLocalList(int index, object value, uint actorNetworkId = 0)
        {
            return RequestLocalList(NetworkVariableOperation.Set, index, 0, value, true, actorNetworkId);
        }

        public bool RequestInsertLocalList(int index, object value, uint actorNetworkId = 0)
        {
            return RequestLocalList(NetworkVariableOperation.Insert, index, 0, value, true, actorNetworkId);
        }

        public bool RequestPushLocalList(object value, uint actorNetworkId = 0)
        {
            return RequestLocalList(NetworkVariableOperation.Push, -1, 0, value, true, actorNetworkId);
        }

        public bool RequestRemoveLocalList(int index, uint actorNetworkId = 0)
        {
            return RequestLocalList(NetworkVariableOperation.Remove, index, 0, null, false, actorNetworkId);
        }

        public bool RequestClearLocalList(uint actorNetworkId = 0)
        {
            return RequestLocalList(NetworkVariableOperation.Clear, -1, 0, null, false, actorNetworkId);
        }

        public bool RequestMoveLocalList(int sourceIndex, int destinationIndex, uint actorNetworkId = 0)
        {
            return RequestLocalList(NetworkVariableOperation.Move, sourceIndex, destinationIndex, null, false, actorNetworkId);
        }

        public bool TryApplyBroadcast(NetworkVariableBroadcast broadcast, out NetworkVariableRejectReason rejectReason)
        {
            rejectReason = NetworkVariableRejectReason.None;

            if (m_Profile == null)
            {
                rejectReason = NetworkVariableRejectReason.ProfileNotFound;
                return false;
            }

            if (broadcast.ProfileHash != m_Profile.ProfileHash)
            {
                rejectReason = NetworkVariableRejectReason.ProfileNotFound;
                return false;
            }

            m_IsApplyingNetworkChange = true;
            try
            {
                switch (broadcast.Scope)
                {
                    case NetworkVariableScope.LocalName:
                        return ApplyLocalName(broadcast, out rejectReason);

                    case NetworkVariableScope.LocalList:
                        return ApplyLocalList(broadcast, out rejectReason);

                    default:
                        rejectReason = NetworkVariableRejectReason.InvalidOperation;
                        return false;
                }
            }
            finally
            {
                m_IsApplyingNetworkChange = false;
            }
        }

        public NetworkVariableBroadcast[] BuildSnapshot(float serverTime)
        {
            if (m_Profile == null) return Array.Empty<NetworkVariableBroadcast>();

            var changes = new List<NetworkVariableBroadcast>();
            uint networkId = NetworkId;
            if (networkId == 0) return Array.Empty<NetworkVariableBroadcast>();

            uint actorNetworkId = ActorNetworkId != 0 ? ActorNetworkId : networkId;
            int profileHash = m_Profile.ProfileHash;

            if (m_LocalNameVariables != null)
            {
                var bindings = m_Profile.LocalNameVariables;
                for (int i = 0; i < bindings.Length; i++)
                {
                    string variableName = bindings[i].Name;
                    if (string.IsNullOrWhiteSpace(variableName)) continue;
                    if (!m_LocalNameVariables.Exists(variableName)) continue;
                    if (!NetworkVariableSerializer.TrySerialize(m_LocalNameVariables.Get(variableName), out string serialized)) continue;

                    changes.Add(new NetworkVariableBroadcast
                    {
                        ActorNetworkId = actorNetworkId,
                        TargetNetworkId = networkId,
                        Scope = NetworkVariableScope.LocalName,
                        Operation = NetworkVariableOperation.Set,
                        ProfileHash = profileHash,
                        VariableHash = NetworkVariableProfile.GetVariableHash(variableName),
                        VariableName = variableName,
                        SerializedValue = serialized,
                        ServerTime = serverTime
                    });
                }
            }

            if (m_Profile.AllowsLocalList && m_LocalListVariables != null)
            {
                changes.Add(new NetworkVariableBroadcast
                {
                    ActorNetworkId = actorNetworkId,
                    TargetNetworkId = networkId,
                    Scope = NetworkVariableScope.LocalList,
                    Operation = NetworkVariableOperation.Clear,
                    ProfileHash = profileHash,
                    VariableHash = m_Profile.LocalListHash,
                    ServerTime = serverTime
                });

                int count = m_LocalListVariables.Count;
                for (int i = 0; i < count; i++)
                {
                    if (!NetworkVariableSerializer.TrySerialize(m_LocalListVariables.Get(i), out string serialized)) continue;
                    changes.Add(new NetworkVariableBroadcast
                    {
                        ActorNetworkId = actorNetworkId,
                        TargetNetworkId = networkId,
                        Scope = NetworkVariableScope.LocalList,
                        Operation = NetworkVariableOperation.Push,
                        ProfileHash = profileHash,
                        VariableHash = m_Profile.LocalListHash,
                        Index = i,
                        SerializedValue = serialized,
                        ServerTime = serverTime
                    });
                }
            }

            return changes.ToArray();
        }

        private bool RequestLocalList(
            NetworkVariableOperation operation,
            int index,
            int indexTo,
            object value,
            bool requiresValue,
            uint actorNetworkId = 0)
        {
            if (m_Profile == null || !m_Profile.IsLocalListAllowed(true))
            {
                LogWarning("Local list variable is not enabled for network writes.");
                return false;
            }

            string serialized = null;
            if (requiresValue && !NetworkVariableSerializer.TrySerialize(value, out serialized))
            {
                LogWarning("Local list variable value type is not supported.");
                return false;
            }

            return SendRequest(new NetworkVariableRequest
            {
                Scope = NetworkVariableScope.LocalList,
                Operation = operation,
                TargetNetworkId = NetworkId,
                ProfileHash = m_Profile.ProfileHash,
                VariableHash = m_Profile.LocalListHash,
                Index = index,
                IndexTo = indexTo,
                SerializedValue = serialized,
                ClientTime = Time.time
            }, actorNetworkId);
        }

        private bool SendRequest(NetworkVariableRequest request, uint actorNetworkId = 0)
        {
            uint networkId = NetworkId;
            if (networkId == 0)
            {
                LogWarning("Cannot send variable request before the controller has a network id.");
                return false;
            }

            var manager = NetworkVariableManager.Instance;
            if (manager == null)
            {
                LogWarning("Cannot send variable request because NetworkVariableManager is missing.");
                return false;
            }

            ushort requestId = NextRequestId();
            uint actorId = actorNetworkId != 0
                ? actorNetworkId
                : ActorNetworkId != 0 ? ActorNetworkId : networkId;
            request.RequestId = requestId;
            request.ActorNetworkId = actorId;
            request.TargetNetworkId = request.TargetNetworkId == 0 ? networkId : request.TargetNetworkId;
            request.CorrelationId = NetworkCorrelation.Compose(actorId, requestId);

            Log($"request {request.Scope}/{request.Operation} actor={request.ActorNetworkId} target={request.TargetNetworkId} variable={request.VariableName} index={request.Index}");
            manager.SendVariableRequest(request);
            return true;
        }

        private bool ApplyLocalName(NetworkVariableBroadcast broadcast, out NetworkVariableRejectReason rejectReason)
        {
            rejectReason = NetworkVariableRejectReason.None;
            if (m_LocalNameVariables == null)
            {
                rejectReason = NetworkVariableRejectReason.VariableNotFound;
                return false;
            }

            string variableName = string.IsNullOrEmpty(broadcast.VariableName)
                ? ResolveLocalName(broadcast.VariableHash)
                : broadcast.VariableName;

            if (!m_Profile.IsLocalNameAllowed(variableName, false))
            {
                rejectReason = NetworkVariableRejectReason.VariableNotAllowed;
                return false;
            }

            if (!m_LocalNameVariables.Exists(variableName))
            {
                rejectReason = NetworkVariableRejectReason.VariableNotFound;
                return false;
            }

            if (!NetworkVariableSerializer.TryDeserialize(broadcast.SerializedValue, out object value))
            {
                rejectReason = NetworkVariableRejectReason.UnsupportedValue;
                return false;
            }

            m_LocalNameVariables.Set(variableName, value);
            Log($"applied LocalName '{variableName}'={broadcast.SerializedValue}");
            return true;
        }

        private bool ApplyLocalList(NetworkVariableBroadcast broadcast, out NetworkVariableRejectReason rejectReason)
        {
            rejectReason = NetworkVariableRejectReason.None;
            if (m_LocalListVariables == null)
            {
                rejectReason = NetworkVariableRejectReason.VariableNotFound;
                return false;
            }

            if (!m_Profile.IsLocalListAllowed(false) || broadcast.VariableHash != m_Profile.LocalListHash)
            {
                rejectReason = NetworkVariableRejectReason.VariableNotAllowed;
                return false;
            }

            switch (broadcast.Operation)
            {
                case NetworkVariableOperation.Set:
                case NetworkVariableOperation.Insert:
                case NetworkVariableOperation.Push:
                    if (!NetworkVariableSerializer.TryDeserialize(broadcast.SerializedValue, out object value))
                    {
                        rejectReason = NetworkVariableRejectReason.UnsupportedValue;
                        return false;
                    }

                    ApplyLocalListValueOperation(broadcast.Operation, broadcast.Index, value);
                    return true;

                case NetworkVariableOperation.Remove:
                    if (broadcast.Index < 0 || broadcast.Index >= m_LocalListVariables.Count)
                    {
                        rejectReason = NetworkVariableRejectReason.InvalidOperation;
                        return false;
                    }

                    m_LocalListVariables.Remove(broadcast.Index);
                    return true;

                case NetworkVariableOperation.Clear:
                    m_LocalListVariables.Clear();
                    return true;

                case NetworkVariableOperation.Move:
                    if (broadcast.Index < 0 || broadcast.Index >= m_LocalListVariables.Count ||
                        broadcast.IndexTo < 0 || broadcast.IndexTo >= m_LocalListVariables.Count)
                    {
                        rejectReason = NetworkVariableRejectReason.InvalidOperation;
                        return false;
                    }

                    m_LocalListVariables.Move(broadcast.Index, broadcast.IndexTo);
                    return true;

                default:
                    rejectReason = NetworkVariableRejectReason.InvalidOperation;
                    return false;
            }
        }

        private void ApplyLocalListValueOperation(NetworkVariableOperation operation, int index, object value)
        {
            switch (operation)
            {
                case NetworkVariableOperation.Set:
                    m_LocalListVariables.Set(index, value);
                    break;
                case NetworkVariableOperation.Insert:
                    m_LocalListVariables.Insert(index, value);
                    break;
                case NetworkVariableOperation.Push:
                    m_LocalListVariables.Push(value);
                    break;
            }
        }

        private string ResolveLocalName(int variableHash)
        {
            var bindings = m_Profile.LocalNameVariables;
            for (int i = 0; i < bindings.Length; i++)
            {
                string variableName = bindings[i].Name;
                if (NetworkVariableProfile.GetVariableHash(variableName) == variableHash)
                {
                    return variableName;
                }
            }

            return null;
        }

        private void RegisterVariableCallbacks()
        {
            if (m_NameCallback != null && m_LocalNameVariables != null)
            {
                m_LocalNameVariables.Register(m_NameCallback);
            }

            if (m_ListCallback != null && m_LocalListVariables != null)
            {
                m_LocalListVariables.Register(m_ListCallback);
            }
        }

        private void UnregisterVariableCallbacks()
        {
            if (m_NameCallback != null && m_LocalNameVariables != null)
            {
                m_LocalNameVariables.Unregister(m_NameCallback);
            }

            if (m_ListCallback != null && m_LocalListVariables != null)
            {
                m_LocalListVariables.Unregister(m_ListCallback);
            }
        }

        private void HandleLocalNameChanged(string variableName)
        {
            if (TryPublishAuthoritativeLocalNameChange(variableName)) return;

            if (ShouldForwardLocalChanges())
            {
                RequestSetLocalName(variableName, m_LocalNameVariables.Get(variableName));
                return;
            }

            if (TryResolveSceneWriteActorNetworkId(out uint actorNetworkId))
            {
                RequestSetLocalName(variableName, m_LocalNameVariables.Get(variableName), actorNetworkId);
            }
        }

        private void HandleLocalListChanged(ListVariableRuntime.Change change, int index)
        {
            uint actorNetworkId = 0;
            if (!ShouldForwardLocalChanges() &&
                !TryResolveSceneWriteActorNetworkId(out actorNetworkId))
            {
                return;
            }

            switch (change)
            {
                case ListVariableRuntime.Change.Set:
                    RequestSetLocalList(index, m_LocalListVariables.Get(index), actorNetworkId);
                    break;
                case ListVariableRuntime.Change.Insert:
                    RequestInsertLocalList(index, m_LocalListVariables.Get(index), actorNetworkId);
                    break;
                case ListVariableRuntime.Change.Remove:
                    RequestRemoveLocalList(index, actorNetworkId);
                    break;
            }
        }

        private bool ShouldForwardLocalChanges()
        {
            if (m_IsApplyingNetworkChange) return false;
            if (m_Profile == null || !m_Profile.AutoSendOwnerLocalChanges) return false;
            if (m_NetworkCharacter != null) return m_NetworkCharacter.IsOwnerInstance;
            return m_IsTransportController;
        }

        private bool TryPublishAuthoritativeLocalNameChange(string variableName)
        {
            if (m_IsApplyingNetworkChange) return false;
            if (m_Profile == null || !m_Profile.AutoSendOwnerLocalChanges) return false;
            if (m_NetworkCharacter != null || !m_IsTransportController) return false;

            NetworkVariableManager manager = NetworkVariableManager.Instance;
            if (manager == null || !manager.IsServer) return false;

            return manager.PublishAuthoritativeLocalNameChange(this, variableName);
        }

        private bool TryResolveSceneWriteActorNetworkId(out uint actorNetworkId)
        {
            actorNetworkId = 0;

            if (m_IsApplyingNetworkChange) return false;
            if (m_Profile == null || !m_Profile.AutoSendOwnerLocalChanges) return false;
            if (m_NetworkCharacter != null || m_IsTransportController) return false;

            NetworkCharacter actor = ShortcutPlayer.Get<NetworkCharacter>();
            if (actor == null || !actor.IsOwnerInstance || actor.NetworkId == 0) return false;

            actorNetworkId = actor.NetworkId;
            return true;
        }

        private void TryRegisterWithManager()
        {
            uint networkId = NetworkId;
            if (networkId == 0)
            {
                UnregisterFromManager();
                return;
            }

            if (networkId == m_RegisteredNetworkId) return;

            UnregisterFromManager();

            var manager = NetworkVariableManager.Instance;
            if (manager == null) return;

            manager.RegisterController(networkId, this);
            m_RegisteredNetworkId = networkId;
        }

        private void UnregisterFromManager()
        {
            if (m_RegisteredNetworkId == 0) return;

            var manager = NetworkVariableManager.Instance;
            if (manager != null)
            {
                manager.UnregisterController(m_RegisteredNetworkId, this);
            }

            m_RegisteredNetworkId = 0;
        }

        private uint ResolveNetworkId()
        {
            uint characterNetworkId = m_NetworkCharacter != null ? m_NetworkCharacter.NetworkId : 0u;
            return characterNetworkId != 0 ? characterNetworkId : m_TransportNetworkId;
        }

        private ushort NextRequestId()
        {
            ushort requestId = m_NextRequestId++;
            if (m_NextRequestId == 0) m_NextRequestId = 1;
            return requestId == 0 ? (ushort)1 : requestId;
        }

        private void Log(string message)
        {
            if (!m_LogNetworkMessages) return;
            Debug.Log($"[NetworkVariables][Controller] {name}: {message}", this);
        }

        private void LogWarning(string message)
        {
            Debug.LogWarning($"[NetworkVariables][Controller] {name}: {message}", this);
        }
    }
}
