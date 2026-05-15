using System;
using System.Collections.Generic;
using Arawn.GameCreator2.Networking.Security;
using GameCreator.Runtime.Variables;
using UnityEngine;

namespace Arawn.GameCreator2.Networking
{
    [AddComponentMenu("Game Creator/Network/Variables/Network Variable Manager")]
    public sealed class NetworkVariableManager : NetworkSingleton<NetworkVariableManager>
    {
        private const string MODULE = "Variables";
        private const int MAX_PENDING_LOCAL_BROADCASTS_PER_TARGET = 64;

        [Header("Global Profiles")]
        [SerializeField] private NetworkVariableProfile[] m_GlobalProfiles = Array.Empty<NetworkVariableProfile>();

        [Header("Debug")]
        [SerializeField] private bool m_LogNetworkMessages;

        private bool m_IsServer;
        private readonly Dictionary<uint, NetworkVariableController> m_Controllers = new(64);
        private readonly Dictionary<uint, ushort> m_RequestCounters = new(16);
        private readonly Dictionary<uint, List<NetworkVariableBroadcast>> m_PendingLocalBroadcasts = new(16);
        private readonly List<NetworkVariableProfile> m_RuntimeProfiles = new(8);

        public Action<NetworkVariableRequest> OnSendVariableRequest;
        public Action<uint, NetworkVariableResponse> OnSendVariableResponse;
        public Action<NetworkVariableBroadcast> OnBroadcastVariableChange;
        public Action<NetworkVariableSnapshot> OnBroadcastSnapshot;
        public Action<ulong, NetworkVariableSnapshot> OnSendSnapshotToClient;

        public new static NetworkVariableManager Instance
        {
            get
            {
                if (s_Instance == null)
                {
                    s_Instance = FindFirstObjectByType<NetworkVariableManager>();
                }

                return s_Instance;
            }
        }

        public bool IsServer
        {
            get => m_IsServer;
            set
            {
                if (m_IsServer == value)
                {
                    SecurityIntegration.SetModuleServerContext(MODULE, value);
                    return;
                }

                m_IsServer = value;
                SecurityIntegration.SetModuleServerContext(MODULE, value);
                SecurityIntegration.EnsureSecurityManagerInitialized(value, () => Time.time);
            }
        }

        protected override DuplicatePolicy OnDuplicatePolicy => DuplicatePolicy.WarnOnly;

        protected override void OnSingletonAwake()
        {
            SyncConfiguredProfiles();
        }

        protected override void OnSingletonCleanup()
        {
            SecurityIntegration.SetModuleServerContext(MODULE, false);
            m_Controllers.Clear();
            m_RequestCounters.Clear();
            m_PendingLocalBroadcasts.Clear();
            m_RuntimeProfiles.Clear();
            OnSendVariableRequest = null;
            OnSendVariableResponse = null;
            OnBroadcastVariableChange = null;
            OnBroadcastSnapshot = null;
            OnSendSnapshotToClient = null;
        }

        private void OnEnable()
        {
            SecurityIntegration.SetModuleServerContext(MODULE, m_IsServer);
            SecurityIntegration.EnsureSecurityManagerInitialized(m_IsServer, () => Time.time);
            SyncConfiguredProfiles();
        }

        private void OnDisable()
        {
            SecurityIntegration.SetModuleServerContext(MODULE, false);
        }

        public void RegisterController(uint networkId, NetworkVariableController controller)
        {
            if (networkId == 0 || controller == null) return;

            if (m_Controllers.TryGetValue(networkId, out var existing) && existing != controller)
            {
                ClearControllerSecurity(networkId, existing);
            }

            m_Controllers[networkId] = controller;

            uint actorNetworkId = controller.ActorNetworkId;
            if (actorNetworkId != 0)
            {
                SecurityIntegration.RegisterEntityActor(networkId, actorNetworkId);
            }

            if (controller.TryGetOwnerClientId(out uint ownerClientId))
            {
                SecurityIntegration.RegisterEntityOwner(networkId, ownerClientId);
            }

            NetworkVariableProfile profile = controller.Profile;
            if (profile != null)
            {
                RegisterGlobalProfile(profile);
            }

            Log($"registered controller netId={networkId} name={controller.name} profile={(profile != null ? profile.name : "none")}");
            ApplyPendingLocalBroadcasts(networkId);
        }

        public void UnregisterController(uint networkId, NetworkVariableController controller)
        {
            if (networkId == 0) return;
            if (!m_Controllers.TryGetValue(networkId, out var existing)) return;
            if (controller != null && existing != controller) return;

            m_Controllers.Remove(networkId);
            ClearControllerSecurity(networkId, existing);
            Log($"unregistered controller netId={networkId}");
        }

        public void RegisterGlobalProfile(NetworkVariableProfile profile)
        {
            if (profile == null) return;
            if (m_RuntimeProfiles.Contains(profile)) return;
            m_RuntimeProfiles.Add(profile);
            Log($"registered profile {profile.name} hash={profile.ProfileHash}");
        }

        public void UnregisterGlobalProfile(NetworkVariableProfile profile)
        {
            if (profile == null) return;
            m_RuntimeProfiles.Remove(profile);
        }

        public bool PublishAuthoritativeLocalNameChange(
            NetworkVariableController controller,
            string variableName)
        {
            if (!m_IsServer || controller == null) return false;

            uint networkId = controller.NetworkId;
            if (networkId == 0) return false;

            NetworkVariableProfile profile = controller.Profile;
            LocalNameVariables localNameVariables = controller.LocalNameVariables;
            if (profile == null || localNameVariables == null) return false;

            if (!profile.IsLocalNameAllowed(variableName, false) ||
                !localNameVariables.Exists(variableName))
            {
                return false;
            }

            if (!m_Controllers.ContainsKey(networkId))
            {
                RegisterController(networkId, controller);
            }

            if (!NetworkVariableSerializer.TrySerialize(localNameVariables.Get(variableName), out string serialized))
            {
                LogWarning($"Local name variable '{variableName}' value type is not supported.");
                return false;
            }

            var broadcast = new NetworkVariableBroadcast
            {
                ActorNetworkId = controller.ActorNetworkId != 0 ? controller.ActorNetworkId : networkId,
                TargetNetworkId = networkId,
                Scope = NetworkVariableScope.LocalName,
                Operation = NetworkVariableOperation.Set,
                ProfileHash = profile.ProfileHash,
                VariableHash = NetworkVariableProfile.GetVariableHash(variableName),
                VariableName = variableName,
                SerializedValue = serialized,
                ServerTime = Time.time
            };

            OnBroadcastVariableChange?.Invoke(broadcast);
            Log($"authoritative broadcast LocalName/{variableName} target={networkId}");
            return true;
        }

        public void SendVariableRequest(NetworkVariableRequest request)
        {
            if (OnSendVariableRequest != null)
            {
                OnSendVariableRequest.Invoke(request);
                return;
            }

            if (!m_IsServer)
            {
                LogWarning("Cannot send variable request because no transport delegate is wired.");
                return;
            }

            uint senderClientId = NetworkTransportBridge.InvalidClientId;
            NetworkTransportBridge bridge = NetworkTransportBridge.Active;
            if (bridge != null && bridge.TryGetCharacterOwner(request.ActorNetworkId, out uint ownerClientId))
            {
                senderClientId = ownerClientId;
            }

            ReceiveVariableRequest(request, senderClientId);
        }

        public bool RequestSetGlobalName(uint actorNetworkId, GlobalNameVariables variables, string variableName, object value)
        {
            if (!TryFindGlobalNameProfile(variables, variableName, true, out var profile, out int variableHash))
            {
                LogWarning($"Global name variable '{variableName}' is not enabled for network writes.");
                return false;
            }

            if (!NetworkVariableSerializer.TrySerialize(value, out string serialized))
            {
                LogWarning($"Global name variable '{variableName}' value type is not supported.");
                return false;
            }

            return SendGlobalRequest(actorNetworkId, new NetworkVariableRequest
            {
                Scope = NetworkVariableScope.GlobalName,
                Operation = NetworkVariableOperation.Set,
                ProfileHash = profile.ProfileHash,
                VariableHash = variableHash,
                VariableName = variableName,
                SerializedValue = serialized,
                ClientTime = Time.time
            });
        }

        public bool RequestSetGlobalList(uint actorNetworkId, GlobalListVariables variables, int index, object value)
        {
            return RequestGlobalList(actorNetworkId, variables, NetworkVariableOperation.Set, index, 0, value, true);
        }

        public bool RequestInsertGlobalList(uint actorNetworkId, GlobalListVariables variables, int index, object value)
        {
            return RequestGlobalList(actorNetworkId, variables, NetworkVariableOperation.Insert, index, 0, value, true);
        }

        public bool RequestPushGlobalList(uint actorNetworkId, GlobalListVariables variables, object value)
        {
            return RequestGlobalList(actorNetworkId, variables, NetworkVariableOperation.Push, -1, 0, value, true);
        }

        public bool RequestRemoveGlobalList(uint actorNetworkId, GlobalListVariables variables, int index)
        {
            return RequestGlobalList(actorNetworkId, variables, NetworkVariableOperation.Remove, index, 0, null, false);
        }

        public bool RequestClearGlobalList(uint actorNetworkId, GlobalListVariables variables)
        {
            return RequestGlobalList(actorNetworkId, variables, NetworkVariableOperation.Clear, -1, 0, null, false);
        }

        public bool RequestMoveGlobalList(uint actorNetworkId, GlobalListVariables variables, int sourceIndex, int destinationIndex)
        {
            return RequestGlobalList(actorNetworkId, variables, NetworkVariableOperation.Move, sourceIndex, destinationIndex, null, false);
        }

        public void ReceiveVariableRequest(NetworkVariableRequest request, uint senderClientId)
        {
            var response = new NetworkVariableResponse
            {
                RequestId = request.RequestId,
                ActorNetworkId = request.ActorNetworkId,
                CorrelationId = request.CorrelationId,
                TargetNetworkId = request.TargetNetworkId,
                Authorized = false,
                RejectReason = NetworkVariableRejectReason.None
            };

            if (!m_IsServer)
            {
                response.RejectReason = NetworkVariableRejectReason.NotAuthorized;
                SendResponse(senderClientId, response);
                return;
            }

            if (!SecurityIntegration.ValidateModuleRequest(
                    senderClientId,
                    NetworkRequestContext.Create(request.ActorNetworkId, request.CorrelationId),
                    MODULE,
                    nameof(NetworkVariableRequest)))
            {
                response.RejectReason = NetworkVariableRejectReason.SecurityViolation;
                SendResponse(senderClientId, response);
                return;
            }

            if (IsLocalScope(request.Scope) &&
                !AllowsUnownedSceneTargetWrite(request) &&
                !SecurityIntegration.ValidateTargetEntityOwnership(
                    senderClientId,
                    request.ActorNetworkId,
                    request.TargetNetworkId,
                    MODULE,
                    nameof(NetworkVariableRequest)))
            {
                response.RejectReason = NetworkVariableRejectReason.NotAuthorized;
                SendResponse(senderClientId, response);
                return;
            }

            if (!TryBuildAuthorizedBroadcast(request, out NetworkVariableBroadcast broadcast, out NetworkVariableRejectReason rejectReason))
            {
                response.RejectReason = rejectReason;
                SendResponse(senderClientId, response);
                return;
            }

            response.Authorized = true;
            response.RejectReason = NetworkVariableRejectReason.None;
            SendResponse(senderClientId, response);

            OnBroadcastVariableChange?.Invoke(broadcast);
            Log($"broadcast {broadcast.Scope}/{broadcast.Operation} actor={broadcast.ActorNetworkId} target={broadcast.TargetNetworkId} variable={broadcast.VariableName} index={broadcast.Index}");
        }

        public void ReceiveVariableResponse(NetworkVariableResponse response)
        {
            if (response.Authorized)
            {
                Log($"response ok request={response.RequestId} actor={response.ActorNetworkId}");
            }
            else
            {
                LogWarning($"response rejected request={response.RequestId} actor={response.ActorNetworkId} reason={response.RejectReason}");
            }
        }

        public void ReceiveVariableBroadcast(NetworkVariableBroadcast broadcast)
        {
            if (m_IsServer) return;
            ApplyBroadcast(broadcast);
        }

        public void ReceiveVariableSnapshot(NetworkVariableSnapshot snapshot)
        {
            if (m_IsServer) return;

            NetworkVariableBroadcast[] changes = snapshot.Changes ?? Array.Empty<NetworkVariableBroadcast>();
            for (int i = 0; i < changes.Length; i++)
            {
                ApplyBroadcast(changes[i]);
            }
        }

        public void SendInitialState(ulong clientId)
        {
            if (!m_IsServer) return;

            var snapshot = BuildSnapshot(Time.time);
            if (snapshot.Changes == null || snapshot.Changes.Length == 0) return;

            if (OnSendSnapshotToClient != null)
            {
                OnSendSnapshotToClient.Invoke(clientId, snapshot);
            }
            else
            {
                OnBroadcastSnapshot?.Invoke(snapshot);
            }
        }

        public NetworkVariableSnapshot BuildSnapshot(float serverTime)
        {
            var changes = new List<NetworkVariableBroadcast>();

            foreach (var controller in m_Controllers.Values)
            {
                if (controller == null || controller.Profile == null || !controller.Profile.SnapshotOnLateJoin) continue;
                changes.AddRange(controller.BuildSnapshot(serverTime));
            }

            AddGlobalSnapshots(changes, serverTime);

            return new NetworkVariableSnapshot
            {
                Changes = changes.ToArray(),
                ServerTime = serverTime
            };
        }

        private bool RequestGlobalList(
            uint actorNetworkId,
            GlobalListVariables variables,
            NetworkVariableOperation operation,
            int index,
            int indexTo,
            object value,
            bool requiresValue)
        {
            if (!TryFindGlobalListProfile(variables, true, out var profile, out int variableHash))
            {
                LogWarning("Global list variable is not enabled for network writes.");
                return false;
            }

            string serialized = null;
            if (requiresValue && !NetworkVariableSerializer.TrySerialize(value, out serialized))
            {
                LogWarning("Global list variable value type is not supported.");
                return false;
            }

            return SendGlobalRequest(actorNetworkId, new NetworkVariableRequest
            {
                Scope = NetworkVariableScope.GlobalList,
                Operation = operation,
                ProfileHash = profile.ProfileHash,
                VariableHash = variableHash,
                Index = index,
                IndexTo = indexTo,
                SerializedValue = serialized,
                ClientTime = Time.time
            });
        }

        private bool SendGlobalRequest(uint actorNetworkId, NetworkVariableRequest request)
        {
            if (actorNetworkId == 0)
            {
                LogWarning("Cannot send global variable request without an actor NetworkCharacter id.");
                return false;
            }

            ushort requestId = NetworkCorrelation.ExtractRequestId(request.CorrelationId);
            if (requestId == 0)
            {
                requestId = NextRequestId(actorNetworkId);
            }

            request.RequestId = requestId;
            request.ActorNetworkId = actorNetworkId;
            request.CorrelationId = NetworkCorrelation.Compose(actorNetworkId, requestId);
            SendVariableRequest(request);
            return true;
        }

        private bool TryBuildAuthorizedBroadcast(
            NetworkVariableRequest request,
            out NetworkVariableBroadcast broadcast,
            out NetworkVariableRejectReason rejectReason)
        {
            broadcast = new NetworkVariableBroadcast
            {
                ActorNetworkId = request.ActorNetworkId,
                TargetNetworkId = request.TargetNetworkId,
                Scope = request.Scope,
                Operation = request.Operation,
                ProfileHash = request.ProfileHash,
                VariableHash = request.VariableHash,
                VariableName = request.VariableName,
                Index = request.Index,
                IndexTo = request.IndexTo,
                SerializedValue = request.SerializedValue,
                ServerTime = Time.time
            };

            rejectReason = NetworkVariableRejectReason.None;

            switch (request.Scope)
            {
                case NetworkVariableScope.LocalName:
                case NetworkVariableScope.LocalList:
                    return TryApplyLocalRequest(request, ref broadcast, out rejectReason);

                case NetworkVariableScope.GlobalName:
                case NetworkVariableScope.GlobalList:
                    return TryApplyGlobalRequest(request, ref broadcast, out rejectReason);

                default:
                    rejectReason = NetworkVariableRejectReason.InvalidOperation;
                    return false;
            }
        }

        private bool TryApplyLocalRequest(
            NetworkVariableRequest request,
            ref NetworkVariableBroadcast broadcast,
            out NetworkVariableRejectReason rejectReason)
        {
            rejectReason = NetworkVariableRejectReason.None;

            if (!m_Controllers.TryGetValue(request.TargetNetworkId, out var controller) || controller == null)
            {
                rejectReason = NetworkVariableRejectReason.ControllerNotFound;
                return false;
            }

            NetworkVariableProfile profile = controller.Profile;
            if (profile == null || profile.ProfileHash != request.ProfileHash)
            {
                rejectReason = NetworkVariableRejectReason.ProfileNotFound;
                return false;
            }

            if (request.Scope == NetworkVariableScope.LocalName)
            {
                string variableName = string.IsNullOrEmpty(request.VariableName)
                    ? ResolveLocalName(profile, request.VariableHash)
                    : request.VariableName;

                if (!profile.IsLocalNameAllowed(variableName, true))
                {
                    rejectReason = NetworkVariableRejectReason.VariableNotAllowed;
                    return false;
                }

                broadcast.VariableName = variableName;
            }
            else if (!profile.IsLocalListAllowed(true) || request.VariableHash != profile.LocalListHash)
            {
                rejectReason = NetworkVariableRejectReason.VariableNotAllowed;
                return false;
            }

            return controller.TryApplyBroadcast(broadcast, out rejectReason);
        }

        private bool AllowsUnownedSceneTargetWrite(NetworkVariableRequest request)
        {
            if (!IsLocalScope(request.Scope)) return false;
            if (!m_Controllers.TryGetValue(request.TargetNetworkId, out var controller) || controller == null)
            {
                return false;
            }

            if (controller.ActorNetworkId != 0) return false;
            if (controller.TryGetOwnerClientId(out _)) return false;

            NetworkVariableProfile profile = controller.Profile;
            if (profile == null || profile.ProfileHash != request.ProfileHash) return false;

            switch (request.Scope)
            {
                case NetworkVariableScope.LocalName:
                {
                    string variableName = string.IsNullOrEmpty(request.VariableName)
                        ? ResolveLocalName(profile, request.VariableHash)
                        : request.VariableName;

                    return profile.IsLocalNameAllowed(variableName, true);
                }

                case NetworkVariableScope.LocalList:
                    return profile.IsLocalListAllowed(true) &&
                           request.VariableHash == profile.LocalListHash;

                default:
                    return false;
            }
        }

        private bool TryApplyGlobalRequest(
            NetworkVariableRequest request,
            ref NetworkVariableBroadcast broadcast,
            out NetworkVariableRejectReason rejectReason)
        {
            rejectReason = NetworkVariableRejectReason.None;

            switch (request.Scope)
            {
                case NetworkVariableScope.GlobalName:
                    if (!TryResolveGlobalNameBinding(request.ProfileHash, request.VariableHash, true, out _, out GlobalNameVariables nameAsset, out string variableName))
                    {
                        rejectReason = NetworkVariableRejectReason.VariableNotAllowed;
                        return false;
                    }

                    broadcast.VariableName = variableName;
                    return ApplyGlobalName(nameAsset, variableName, broadcast, out rejectReason);

                case NetworkVariableScope.GlobalList:
                    if (!TryResolveGlobalListBinding(request.ProfileHash, request.VariableHash, true, out _, out GlobalListVariables listAsset))
                    {
                        rejectReason = NetworkVariableRejectReason.VariableNotAllowed;
                        return false;
                    }

                    return ApplyGlobalList(listAsset, broadcast, out rejectReason);

                default:
                    rejectReason = NetworkVariableRejectReason.InvalidOperation;
                    return false;
            }
        }

        private bool ApplyBroadcast(NetworkVariableBroadcast broadcast)
        {
            NetworkVariableRejectReason rejectReason;
            switch (broadcast.Scope)
            {
                case NetworkVariableScope.LocalName:
                case NetworkVariableScope.LocalList:
                    if (!m_Controllers.TryGetValue(broadcast.TargetNetworkId, out var controller) || controller == null)
                    {
                        QueuePendingLocalBroadcast(broadcast);
                        LogWarning($"No controller for variable broadcast target={broadcast.TargetNetworkId}; queued until controller registers.");
                        return false;
                    }

                    if (!controller.TryApplyBroadcast(broadcast, out rejectReason))
                    {
                        LogWarning($"Rejected local variable broadcast target={broadcast.TargetNetworkId} reason={rejectReason}");
                        return false;
                    }

                    return true;

                case NetworkVariableScope.GlobalName:
                    if (!TryResolveGlobalNameBinding(
                            broadcast.ProfileHash,
                            broadcast.VariableHash,
                            false,
                            out _,
                            out GlobalNameVariables nameAsset,
                            out string variableName))
                    {
                        LogWarning($"No global name binding for broadcast profile={broadcast.ProfileHash} variable={broadcast.VariableHash}");
                        return false;
                    }

                    return ApplyGlobalName(nameAsset, variableName, broadcast, out _);

                case NetworkVariableScope.GlobalList:
                    if (!TryResolveGlobalListBinding(
                            broadcast.ProfileHash,
                            broadcast.VariableHash,
                            false,
                            out _,
                            out GlobalListVariables listAsset))
                    {
                        LogWarning($"No global list binding for broadcast profile={broadcast.ProfileHash} variable={broadcast.VariableHash}");
                        return false;
                    }

                    return ApplyGlobalList(listAsset, broadcast, out _);

                default:
                    return false;
            }
        }

        private bool ApplyGlobalName(
            GlobalNameVariables asset,
            string variableName,
            NetworkVariableBroadcast broadcast,
            out NetworkVariableRejectReason rejectReason)
        {
            rejectReason = NetworkVariableRejectReason.None;

            if (asset == null || string.IsNullOrWhiteSpace(variableName) || !asset.Exists(variableName))
            {
                rejectReason = NetworkVariableRejectReason.VariableNotFound;
                return false;
            }

            if (broadcast.Operation != NetworkVariableOperation.Set)
            {
                rejectReason = NetworkVariableRejectReason.InvalidOperation;
                return false;
            }

            if (!NetworkVariableSerializer.TryDeserialize(broadcast.SerializedValue, out object value))
            {
                rejectReason = NetworkVariableRejectReason.UnsupportedValue;
                return false;
            }

            asset.Set(variableName, value);
            return true;
        }

        private bool ApplyGlobalList(
            GlobalListVariables asset,
            NetworkVariableBroadcast broadcast,
            out NetworkVariableRejectReason rejectReason)
        {
            rejectReason = NetworkVariableRejectReason.None;
            if (asset == null)
            {
                rejectReason = NetworkVariableRejectReason.VariableNotFound;
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

                    ApplyGlobalListValueOperation(asset, broadcast.Operation, broadcast.Index, value);
                    return true;

                case NetworkVariableOperation.Remove:
                    if (broadcast.Index < 0 || broadcast.Index >= asset.Count)
                    {
                        rejectReason = NetworkVariableRejectReason.InvalidOperation;
                        return false;
                    }

                    asset.Remove(broadcast.Index);
                    return true;

                case NetworkVariableOperation.Clear:
                    asset.Clear();
                    return true;

                case NetworkVariableOperation.Move:
                    if (broadcast.Index < 0 || broadcast.Index >= asset.Count ||
                        broadcast.IndexTo < 0 || broadcast.IndexTo >= asset.Count)
                    {
                        rejectReason = NetworkVariableRejectReason.InvalidOperation;
                        return false;
                    }

                    asset.Move(broadcast.Index, broadcast.IndexTo);
                    return true;

                default:
                    rejectReason = NetworkVariableRejectReason.InvalidOperation;
                    return false;
            }
        }

        private static void ApplyGlobalListValueOperation(
            GlobalListVariables asset,
            NetworkVariableOperation operation,
            int index,
            object value)
        {
            switch (operation)
            {
                case NetworkVariableOperation.Set:
                    asset.Set(index, value);
                    break;
                case NetworkVariableOperation.Insert:
                    asset.Insert(index, value);
                    break;
                case NetworkVariableOperation.Push:
                    asset.Push(value);
                    break;
            }
        }

        private void AddGlobalSnapshots(List<NetworkVariableBroadcast> changes, float serverTime)
        {
            var profiles = Profiles;
            for (int p = 0; p < profiles.Count; p++)
            {
                NetworkVariableProfile profile = profiles[p];
                if (profile == null || !profile.SnapshotOnLateJoin) continue;

                var nameBindings = profile.GlobalNameVariables;
                for (int i = 0; i < nameBindings.Length; i++)
                {
                    GlobalNameVariables asset = nameBindings[i].Variables;
                    string variableName = nameBindings[i].Name;
                    if (asset == null || string.IsNullOrWhiteSpace(variableName) || !asset.Exists(variableName)) continue;
                    if (!NetworkVariableSerializer.TrySerialize(asset.Get(variableName), out string serialized)) continue;

                    changes.Add(new NetworkVariableBroadcast
                    {
                        Scope = NetworkVariableScope.GlobalName,
                        Operation = NetworkVariableOperation.Set,
                        ProfileHash = profile.ProfileHash,
                        VariableHash = NetworkVariableProfile.GetGlobalNameBindingHash(asset, variableName),
                        VariableName = variableName,
                        SerializedValue = serialized,
                        ServerTime = serverTime
                    });
                }

                var listBindings = profile.GlobalListVariables;
                for (int i = 0; i < listBindings.Length; i++)
                {
                    GlobalListVariables asset = listBindings[i].Variables;
                    if (asset == null) continue;

                    int variableHash = NetworkVariableProfile.GetGlobalAssetHash(asset);
                    changes.Add(new NetworkVariableBroadcast
                    {
                        Scope = NetworkVariableScope.GlobalList,
                        Operation = NetworkVariableOperation.Clear,
                        ProfileHash = profile.ProfileHash,
                        VariableHash = variableHash,
                        ServerTime = serverTime
                    });

                    int count = asset.Count;
                    for (int index = 0; index < count; index++)
                    {
                        if (!NetworkVariableSerializer.TrySerialize(asset.Get(index), out string serialized)) continue;
                        changes.Add(new NetworkVariableBroadcast
                        {
                            Scope = NetworkVariableScope.GlobalList,
                            Operation = NetworkVariableOperation.Push,
                            ProfileHash = profile.ProfileHash,
                            VariableHash = variableHash,
                            Index = index,
                            SerializedValue = serialized,
                            ServerTime = serverTime
                        });
                    }
                }
            }
        }

        private bool TryFindGlobalNameProfile(
            GlobalNameVariables asset,
            string variableName,
            bool requireClientWrite,
            out NetworkVariableProfile profile,
            out int variableHash)
        {
            profile = null;
            variableHash = 0;
            if (asset == null || string.IsNullOrWhiteSpace(variableName)) return false;

            var profiles = Profiles;
            for (int i = 0; i < profiles.Count; i++)
            {
                NetworkVariableProfile candidate = profiles[i];
                if (candidate == null || !candidate.IsGlobalNameAllowed(asset, variableName, requireClientWrite)) continue;
                profile = candidate;
                variableHash = NetworkVariableProfile.GetGlobalNameBindingHash(asset, variableName);
                return true;
            }

            return false;
        }

        private bool TryFindGlobalListProfile(
            GlobalListVariables asset,
            bool requireClientWrite,
            out NetworkVariableProfile profile,
            out int variableHash)
        {
            profile = null;
            variableHash = 0;
            if (asset == null) return false;

            var profiles = Profiles;
            for (int i = 0; i < profiles.Count; i++)
            {
                NetworkVariableProfile candidate = profiles[i];
                if (candidate == null || !candidate.IsGlobalListAllowed(asset, requireClientWrite)) continue;
                profile = candidate;
                variableHash = NetworkVariableProfile.GetGlobalAssetHash(asset);
                return true;
            }

            return false;
        }

        private bool TryResolveGlobalNameBinding(
            int profileHash,
            int variableHash,
            bool requireClientWrite,
            out NetworkVariableProfile profile,
            out GlobalNameVariables asset,
            out string variableName)
        {
            profile = null;
            asset = null;
            variableName = null;

            var profiles = Profiles;
            for (int i = 0; i < profiles.Count; i++)
            {
                NetworkVariableProfile candidate = profiles[i];
                if (candidate == null) continue;
                if (!candidate.TryResolveGlobalNameAsset(profileHash, variableHash, out asset, out variableName)) continue;
                if (!candidate.IsGlobalNameAllowed(asset, variableName, requireClientWrite)) return false;

                profile = candidate;
                return true;
            }

            return false;
        }

        private bool TryResolveGlobalListBinding(
            int profileHash,
            int variableHash,
            bool requireClientWrite,
            out NetworkVariableProfile profile,
            out GlobalListVariables asset)
        {
            profile = null;
            asset = null;

            var profiles = Profiles;
            for (int i = 0; i < profiles.Count; i++)
            {
                NetworkVariableProfile candidate = profiles[i];
                if (candidate == null) continue;
                if (!candidate.TryResolveGlobalListAsset(profileHash, variableHash, out asset)) continue;
                if (!candidate.IsGlobalListAllowed(asset, requireClientWrite)) return false;

                profile = candidate;
                return true;
            }

            return false;
        }

        private string ResolveLocalName(NetworkVariableProfile profile, int variableHash)
        {
            if (profile == null) return null;

            var bindings = profile.LocalNameVariables;
            for (int i = 0; i < bindings.Length; i++)
            {
                string variableName = bindings[i].Name;
                if (NetworkVariableProfile.GetVariableHash(variableName) == variableHash) return variableName;
            }

            return null;
        }

        private void SendResponse(uint clientId, NetworkVariableResponse response)
        {
            if (NetworkTransportBridge.IsValidClientId(clientId))
            {
                OnSendVariableResponse?.Invoke(clientId, response);
            }
        }

        private bool IsLocalScope(NetworkVariableScope scope)
        {
            return scope == NetworkVariableScope.LocalName || scope == NetworkVariableScope.LocalList;
        }

        private void QueuePendingLocalBroadcast(NetworkVariableBroadcast broadcast)
        {
            uint targetNetworkId = broadcast.TargetNetworkId;
            if (targetNetworkId == 0) return;

            if (!m_PendingLocalBroadcasts.TryGetValue(targetNetworkId, out var pending))
            {
                pending = new List<NetworkVariableBroadcast>(4);
                m_PendingLocalBroadcasts[targetNetworkId] = pending;
            }

            if (pending.Count >= MAX_PENDING_LOCAL_BROADCASTS_PER_TARGET)
            {
                pending.RemoveAt(0);
            }

            pending.Add(broadcast);
        }

        private void ApplyPendingLocalBroadcasts(uint networkId)
        {
            if (networkId == 0) return;
            if (!m_PendingLocalBroadcasts.TryGetValue(networkId, out var pending) || pending.Count == 0)
            {
                return;
            }

            m_PendingLocalBroadcasts.Remove(networkId);

            for (int i = 0; i < pending.Count; i++)
            {
                ApplyBroadcast(pending[i]);
            }
        }

        private static void ClearControllerSecurity(uint networkId, NetworkVariableController controller)
        {
            if (networkId == 0 || controller == null) return;
            if (controller.ActorNetworkId == networkId) return;

            SecurityIntegration.UnregisterEntity(networkId);
        }

        private ushort NextRequestId(uint actorNetworkId)
        {
            m_RequestCounters.TryGetValue(actorNetworkId, out ushort requestId);
            requestId++;
            if (requestId == 0) requestId = 1;
            m_RequestCounters[actorNetworkId] = requestId;
            return requestId;
        }

        private void SyncConfiguredProfiles()
        {
            var profiles = m_GlobalProfiles ?? Array.Empty<NetworkVariableProfile>();
            for (int i = 0; i < profiles.Length; i++)
            {
                RegisterGlobalProfile(profiles[i]);
            }
        }

        private IReadOnlyList<NetworkVariableProfile> Profiles
        {
            get
            {
                SyncConfiguredProfiles();
                return m_RuntimeProfiles;
            }
        }

        private void Log(string message)
        {
            if (!m_LogNetworkMessages) return;
            Debug.Log($"[NetworkVariables][Manager] {message}", this);
        }

        private void LogWarning(string message)
        {
            Debug.LogWarning($"[NetworkVariables][Manager] {message}", this);
        }
    }
}
