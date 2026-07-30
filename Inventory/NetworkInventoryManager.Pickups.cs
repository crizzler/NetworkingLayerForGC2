#if GC2_INVENTORY
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using GameCreator.Runtime.Inventory;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Inventory
{
    public partial class NetworkInventoryManager
    {
        public Action<NetworkPickupStateBroadcast> OnBroadcastPickupState;
        public Action<ulong, NetworkPickupStateSnapshot> OnSendPickupStateSnapshotToClient;
        public Func<NetworkPickupRequest, uint, NetworkInventoryPickupSource,
            (bool allowed, InventoryRejectionReason reason)> CustomPickupValidator;

        private readonly Dictionary<uint, NetworkInventoryPickupSource> m_PickupSources = new(64);
        private readonly Dictionary<uint, RuntimePickupRegistration> m_RuntimePickupSources = new(32);
        private readonly Dictionary<uint, NetworkPickupState> m_PendingPickupStates = new(32);
        private readonly Dictionary<ulong, TaskCompletionSource<NetworkPickupResponse>>
            m_PendingPickupResponses = new(8);

        private readonly struct RuntimePickupRegistration
        {
            public readonly NetworkInventoryPickupSource Source;
            public readonly INetworkInventoryRuntimePickupIdentity Identity;

            public RuntimePickupRegistration(
                NetworkInventoryPickupSource source,
                INetworkInventoryRuntimePickupIdentity identity)
            {
                Source = source;
                Identity = identity;
            }
        }

        public void RegisterPickupSource(NetworkInventoryPickupSource source)
        {
            if (source == null || source.PickupId == 0) return;
            if (m_PickupSources.TryGetValue(source.PickupId, out var existing) &&
                existing != null && existing != source)
            {
                Debug.LogWarning(
                    $"[NetworkInventory] Duplicate pickup id {source.PickupId} on " +
                    $"'{existing.name}' and '{source.name}'. The duplicate source is rejected.");
                return;
            }

            m_PickupSources[source.PickupId] = source;
            if (m_PendingPickupStates.TryGetValue(source.PickupId, out NetworkPickupState state))
            {
                source.ApplyState(state);
            }
        }

        public void UnregisterPickupSource(NetworkInventoryPickupSource source)
        {
            if (source == null) return;
            if (m_PickupSources.TryGetValue(source.PickupId, out var existing) && existing == source)
            {
                // Stock GC2 pickup templates commonly Destroy Self after a successful Add Item.
                // Retain the consumed tombstone even after that scene object disappears so a
                // late joiner cannot recreate and collect it again.
                NetworkPickupState state = source.GetState();
                if (state.StateVersion != 0) CachePickupState(state);
                m_PickupSources.Remove(source.PickupId);
            }
        }

        public bool RegisterRuntimePickupSource(
            uint pickupNetworkId,
            NetworkInventoryPickupSource source,
            INetworkInventoryRuntimePickupIdentity identity)
        {
            if (pickupNetworkId == 0 || source == null || identity == null ||
                !ReferenceEquals(source.RuntimeIdentity, identity)) return false;

            if (m_RuntimePickupSources.TryGetValue(pickupNetworkId, out RuntimePickupRegistration existing) &&
                existing.Identity != null && !ReferenceEquals(existing.Identity, identity))
            {
                Debug.LogWarning(
                    $"[NetworkInventory] Duplicate runtime pickup network id {pickupNetworkId}. " +
                    "Runtime pickup identities must be unique within the session.");
                return false;
            }

            m_RuntimePickupSources[pickupNetworkId] = new RuntimePickupRegistration(source, identity);
            return true;
        }

        public void UnregisterRuntimePickupSource(
            uint pickupNetworkId,
            INetworkInventoryRuntimePickupIdentity identity)
        {
            if (!m_RuntimePickupSources.TryGetValue(pickupNetworkId, out RuntimePickupRegistration existing))
                return;
            if (identity != null && !ReferenceEquals(existing.Identity, identity)) return;
            m_RuntimePickupSources.Remove(pickupNetworkId);
        }

        public async Task<NetworkPickupResponse> RequestPickupSourceAsync(
            NetworkInventoryPickupSource source, NetworkInventoryController picker)
        {
            if (source == null || picker == null)
                return RejectPickup(default, InventoryRejectionReason.InvalidOperation);

            ushort requestId = NextSemanticRequestId();
            var request = new NetworkPickupRequest
            {
                RequestId = requestId,
                ActorNetworkId = picker.NetworkId,
                CorrelationId = NetworkCorrelation.Compose(picker.NetworkId, requestId),
                PickerBagNetworkId = picker.NetworkId,
                PropNetworkId = source.RuntimeIdentity is { IsSpawned: true } runtimeIdentity
                    ? runtimeIdentity.NetworkPickupId
                    : source.PickupId,
                SourceBagNetworkId = 0,
                RuntimeIdHash = 0,
                DestinationPosition = GameCreator.Runtime.Inventory.TBagContent.INVALID
            };

            if (m_IsServer)
            {
                return TryProcessRegisteredPickup(request, picker.NetworkId, out NetworkPickupResponse response)
                    ? response
                    : RejectPickup(request, InventoryRejectionReason.ItemNotFound);
            }

            if (OnSendPickupRequest == null)
                return RejectPickup(request, InventoryRejectionReason.NotAuthorized);

            ulong key = PickupPendingKey(request.ActorNetworkId, request.CorrelationId);
            var completion = new TaskCompletionSource<NetworkPickupResponse>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            m_PendingPickupResponses[key] = completion;
            SendPickupRequest(request);

            float timeoutSeconds = Mathf.Max(0.25f, m_RequestTimeout);
            DateTime deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
            Task timeout = Task.Delay(TimeSpan.FromSeconds(timeoutSeconds));
            if (await Task.WhenAny(completion.Task, timeout) == completion.Task)
            {
                NetworkPickupResponse response = await completion.Task;
                if (!response.Authorized || response.StateVersion == 0 ||
                    picker.HasAppliedStateVersion(response.StateVersion))
                {
                    return response;
                }

                // The response and mutation use the same ReliableOrdered channel, but transport
                // dispatch can still complete the response continuation before the controller has
                // converged its authoritative add. Do not let a visual-scripting instruction move
                // on to Destroy Self until the referenced revision is actually present locally.
                while (DateTime.UtcNow < deadline)
                {
                    await Task.Yield();
                    if (picker == null)
                        return RejectPickup(request, InventoryRejectionReason.BagNotFound);
                    if (picker.HasAppliedStateVersion(response.StateVersion)) return response;
                }

                return RejectPickup(request, InventoryRejectionReason.RequestTimeout);
            }

            m_PendingPickupResponses.Remove(key);
            return RejectPickup(request, InventoryRejectionReason.RequestTimeout);
        }

        internal bool TryProcessRegisteredPickup(
            NetworkPickupRequest request,
            uint senderClientId,
            out NetworkPickupResponse response)
        {
            response = RejectPickup(request, InventoryRejectionReason.ItemNotFound);
            if (request.PropNetworkId == 0) return false;
            bool isRuntime = m_RuntimePickupSources.TryGetValue(
                request.PropNetworkId, out RuntimePickupRegistration runtimeRegistration);
            NetworkInventoryPickupSource source = isRuntime
                ? runtimeRegistration.Source
                : (m_PickupSources.TryGetValue(request.PropNetworkId, out var staticSource)
                    ? staticSource
                    : null);
            if (source == null)
            {
                // A non-zero id with no runtime item is the v3 static/runtime identity route.
                return request.RuntimeIdHash == 0;
            }

            NetworkInventoryController picker = GetController(request.PickerBagNetworkId);
            if (picker == null)
            {
                response.RejectionReason = InventoryRejectionReason.BagNotFound;
                return true;
            }

            if (CustomPickupValidator != null)
            {
                var custom = CustomPickupValidator(request, senderClientId, source);
                if (!custom.allowed)
                {
                    response.RejectionReason = custom.reason;
                    return true;
                }
            }

            if (!source.TryReserve(picker, request.ActorNetworkId, out InventoryRejectionReason reason))
            {
                response.RejectionReason = reason;
                return true;
            }

            Item sourceItem = source.Item;
            RuntimeItem stagedItem = null;
            Vector2Int stagedPosition = TBagContent.INVALID;
            NetworkContentAddResponse grantResponse = default;
            try
            {
                if (!picker.TryServerStagePickupItem(
                    sourceItem,
                    request.DestinationPosition,
                    true,
                    out stagedItem,
                    out stagedPosition))
                {
                    source.ReleaseReservation();
                    response.RejectionReason = InventoryRejectionReason.InsufficientSpace;
                    return true;
                }
            }
            catch (Exception exception)
            {
                Debug.LogException(exception, source);
                source.ReleaseReservation();
                if (stagedItem != null) picker.RollbackServerStagedPickupItem(stagedItem);
                response.RejectionReason = InventoryRejectionReason.InvalidOperation;
                return true;
            }

            if (isRuntime)
            {
                bool consumed;
                try
                {
                    consumed = source.CommitRuntime(
                        request.ActorNetworkId,
                        runtimeRegistration.Identity);
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception, source);
                    source.ReleaseReservation();
                    consumed = false;
                }

                if (!consumed)
                {
                    // No authoritative add has been published yet. Rolling the staged item back is
                    // therefore invisible to every remote peer and cannot create a transient grant.
                    picker.RollbackServerStagedPickupItem(stagedItem);
                    response.RejectionReason = InventoryRejectionReason.NotAuthorized;
                    return true;
                }
            }
            else
            {
                NetworkPickupState state = source.Commit(request.ActorNetworkId);
                BroadcastPickupState(new NetworkPickupStateBroadcast { State = state });
            }

            grantResponse = picker.CommitServerStagedPickupItem(stagedItem, stagedPosition);
            if (!grantResponse.Authorized)
            {
                // This is a broken invariant: the item was already staged and the pickup committed.
                // Fail closed and force the regular inventory recovery snapshot rather than naming
                // an unrelated item of the same type in the response.
                picker.BroadcastAuthoritativeSnapshot();
                response.RejectionReason = grantResponse.RejectionReason;
                return true;
            }

            RuntimeItem runtimeItem = picker.FindRuntimeItem(grantResponse.AssignedRuntimeId);
            if (runtimeItem == null)
            {
                picker.BroadcastAuthoritativeSnapshot();
                response.RejectionReason = InventoryRejectionReason.RuntimeItemNotFound;
                return true;
            }

            response.Authorized = true;
            response.RejectionReason = InventoryRejectionReason.None;
            response.PickedUpItem = picker.ToNetworkRuntimeItem(runtimeItem);
            response.PlacedPosition = grantResponse.ResultPosition;
            response.StateVersion = grantResponse.StateVersion;
            return true;
        }

        public void BroadcastPickupState(NetworkPickupStateBroadcast broadcast)
        {
            if (m_IsServer)
            {
                // The source may be destroyed by the next visual-scripting instruction. Cache
                // first so targeted late-join snapshots keep its authoritative tombstone.
                ApplyPickupState(broadcast.State);
                OnBroadcastPickupState?.Invoke(broadcast);
            }
            else
            {
                ReceivePickupStateBroadcast(broadcast);
            }
        }

        public void ReceivePickupStateBroadcast(NetworkPickupStateBroadcast broadcast)
        {
            ApplyPickupState(broadcast.State);
        }

        public void ReceivePickupStateSnapshot(NetworkPickupStateSnapshot snapshot)
        {
            if (snapshot.Pickups == null) return;
            for (int i = 0; i < snapshot.Pickups.Length; i++)
                ApplyPickupState(snapshot.Pickups[i]);
        }

        public void SendPickupStateSnapshot(ulong clientId)
        {
            if (!m_IsServer || OnSendPickupStateSnapshotToClient == null) return;

            var merged = new Dictionary<uint, NetworkPickupState>(m_PendingPickupStates);
            foreach (var entry in m_PickupSources)
            {
                if (entry.Value == null) continue;
                NetworkPickupState state = entry.Value.GetState();
                if (!merged.TryGetValue(entry.Key, out NetworkPickupState current) ||
                    state.StateVersion >= current.StateVersion)
                {
                    merged[entry.Key] = state;
                }
            }

            var states = new List<NetworkPickupState>(merged.Values);
            OnSendPickupStateSnapshotToClient(clientId, new NetworkPickupStateSnapshot
            {
                Timestamp = Time.time,
                Pickups = states.ToArray()
            });
        }

        internal void CompletePickupResponse(NetworkPickupResponse response)
        {
            ulong key = PickupPendingKey(response.ActorNetworkId, response.CorrelationId);
            if (!m_PendingPickupResponses.TryGetValue(key, out var completion)) return;
            m_PendingPickupResponses.Remove(key);
            completion.TrySetResult(response);
        }

        private void ApplyPickupState(NetworkPickupState state)
        {
            if (state.PickupId == 0) return;
            if (!CachePickupState(state)) return;

            if (m_PickupSources.TryGetValue(state.PickupId, out var source) && source != null)
            {
                source.ApplyState(state);
            }
        }

        private bool CachePickupState(NetworkPickupState state)
        {
            if (!m_PendingPickupStates.TryGetValue(state.PickupId, out var current) ||
                state.StateVersion >= current.StateVersion)
            {
                m_PendingPickupStates[state.PickupId] = state;
                return true;
            }

            return false;
        }

        private static NetworkPickupResponse RejectPickup(
            NetworkPickupRequest request, InventoryRejectionReason reason)
        {
            return new NetworkPickupResponse
            {
                RequestId = request.RequestId,
                ActorNetworkId = request.ActorNetworkId,
                CorrelationId = request.CorrelationId,
                Authorized = false,
                RejectionReason = reason,
                PlacedPosition = GameCreator.Runtime.Inventory.TBagContent.INVALID
            };
        }

        private static ulong PickupPendingKey(uint actorNetworkId, uint correlationId)
        {
            return ((ulong)actorNetworkId << 32) | correlationId;
        }
    }

    public partial class NetworkInventoryController
    {
        /// <summary>
        /// Mutates the authoritative bag without publishing a revision. The pickup manager commits
        /// the source/identity first, then calls <see cref="CommitServerStagedPickupItem"/>. This
        /// keeps a failed runtime-identity consumption from ever exposing a temporary item grant.
        /// </summary>
        internal bool TryServerStagePickupItem(
            Item item,
            Vector2Int position,
            bool allowStack,
            out RuntimeItem runtimeItem,
            out Vector2Int resultPosition)
        {
            runtimeItem = null;
            resultPosition = TBagContent.INVALID;
            if (!m_IsServer || m_Bag == null || item == null) return false;

            RuntimeItem candidate = new RuntimeItem(item);
            using (EnterNetworkMutationScope())
            {
                if (position.x >= 0 && position.y >= 0)
                {
                    resultPosition = m_Bag.Content.Add(candidate, position, allowStack)
                        ? position
                        : TBagContent.INVALID;
                }
                else
                {
                    resultPosition = m_Bag.Content.Add(candidate, allowStack);
                }
            }

            if (resultPosition == TBagContent.INVALID) return false;
            TrackRuntimeItemRecursive(candidate);
            runtimeItem = candidate;
            return true;
        }

        internal NetworkContentAddResponse CommitServerStagedPickupItem(
            RuntimeItem runtimeItem,
            Vector2Int stagedPosition)
        {
            if (!m_IsServer || runtimeItem == null ||
                !ContainsRuntimeItemRecursive(runtimeItem.RuntimeID.Hash))
            {
                return new NetworkContentAddResponse
                {
                    Authorized = false,
                    RejectionReason = InventoryRejectionReason.RuntimeItemNotFound,
                    ResultPosition = TBagContent.INVALID
                };
            }

            Vector2Int actualPosition = m_Bag.Content.FindPosition(runtimeItem.RuntimeID);
            if (actualPosition == TBagContent.INVALID) actualPosition = stagedPosition;
            uint stateVersion = GetAuthoritativeStateVersion();
            var broadcast = new NetworkItemAddedBroadcast
            {
                BagNetworkId = NetworkId,
                Item = ConvertToNetworkItem(runtimeItem),
                Position = actualPosition,
                StackCount = m_Bag.Content.GetContent(actualPosition)?.Count ?? 1,
                StateVersion = stateVersion
            };

            NetworkInventoryManager.Instance?.BroadcastItemAdded(broadcast);
            OnItemAdded?.Invoke(broadcast);
            CacheCurrentSyncState();

            return new NetworkContentAddResponse
            {
                Authorized = true,
                RejectionReason = InventoryRejectionReason.None,
                ResultPosition = actualPosition,
                AssignedRuntimeId = runtimeItem.RuntimeID.Hash,
                AssignedRuntimeIdString = runtimeItem.RuntimeID.String,
                StateVersion = stateVersion
            };
        }

        internal void RollbackServerStagedPickupItem(RuntimeItem runtimeItem)
        {
            if (!m_IsServer || runtimeItem == null) return;
            using (EnterNetworkMutationScope())
            {
                RuntimeItem removed = m_Bag.Content.Remove(runtimeItem);
                if (removed != null) UntrackRuntimeItemRecursive(removed);
            }
            CacheCurrentSyncState();
        }
    }
}
#endif
