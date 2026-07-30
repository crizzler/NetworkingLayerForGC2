#if GC2_INVENTORY
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.Inventory;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Inventory
{
    /// <summary>
    /// Semantic-operation routing used by the Inventory 3.0 patch. Primitive GC2 mutations are
    /// only performed inside an authoritative mutation scope, which prevents composite operations
    /// (move, transfer, merchant and crafting) from recursively sending more requests.
    /// </summary>
    public partial class NetworkInventoryController
    {
        private readonly Dictionary<ulong, TaskCompletionSource<NetworkContentAddResponse>>
            m_PendingAsyncAdds = new(8);
        private readonly Dictionary<ulong, NetworkContentAddResponse>
            m_DeferredAsyncAddResponses = new(4);

        private readonly Dictionary<ulong, TaskCompletionSource<NetworkContentSplitResponse>>
            m_PendingAsyncSplits = new(4);
        private readonly Dictionary<ulong, NetworkContentSplitResponse>
            m_DeferredAsyncSplitResponses = new(4);

        public async Task<NetworkContentAddResponse> RequestAddItemAsync(
            Item item,
            Vector2Int position,
            bool allowStack,
            InventoryModificationSource source = InventoryModificationSource.Direct,
            int sourceHash = 0)
        {
            if (item == null)
            {
                return RejectedAdd(InventoryRejectionReason.ItemNotFound);
            }

            if (m_IsRemoteClient)
            {
                return RejectedAdd(InventoryRejectionReason.NotOwner);
            }

            NetworkContentAddRequest request = CreateAddRequest(item, position, allowStack, source, sourceHash);
            ulong key = GetPendingKey(request.ActorNetworkId, request.CorrelationId, request.RequestId);
            var completion = new TaskCompletionSource<NetworkContentAddResponse>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            m_PendingAdds[key] = new PendingContentAdd { Request = request, SentTime = Time.time };
            m_PendingAsyncAdds[key] = completion;
            OnContentAddRequested?.Invoke(request);

            if (m_IsServer)
            {
                NetworkContentAddResponse response;
                using (EnterNetworkMutationScope())
                {
                    response = ProcessContentAddRequest(request, NetworkId);
                }

                response.ActorNetworkId = request.ActorNetworkId;
                response.CorrelationId = request.CorrelationId;
                ReceiveContentAddResponse(response);
            }
            else
            {
                NetworkInventoryManager manager = NetworkInventoryManager.Instance;
                if (manager == null || manager.OnSendContentAddRequest == null)
                {
                    NetworkContentAddResponse response = RejectedAdd(InventoryRejectionReason.NotAuthorized);
                    response.RequestId = request.RequestId;
                    response.ActorNetworkId = request.ActorNetworkId;
                    response.CorrelationId = request.CorrelationId;
                    ReceiveContentAddResponse(response);
                }
                else
                {
                    manager.SendContentAddRequest(request);
                }
            }

            return await AwaitAddResponseAsync(key, completion.Task);
        }

        private NetworkContentAddRequest CreateAddRequest(
            Item item,
            Vector2Int position,
            bool allowStack,
            InventoryModificationSource source,
            int sourceHash)
        {
            ushort requestId = GetNextRequestId();
            return new NetworkContentAddRequest
            {
                RequestId = requestId,
                ActorNetworkId = NetworkId,
                CorrelationId = NetworkCorrelation.Compose(NetworkId, requestId),
                TargetBagNetworkId = NetworkId,
                ItemHash = item.ID.Hash,
                ItemIdString = item.ID.String,
                Position = position,
                AllowStack = allowStack,
                Source = source,
                SourceHash = sourceHash
            };
        }

        private static NetworkContentAddResponse RejectedAdd(InventoryRejectionReason reason)
        {
            return new NetworkContentAddResponse
            {
                Authorized = false,
                RejectionReason = reason,
                ResultPosition = TBagContent.INVALID
            };
        }

        private async Task<NetworkContentAddResponse> AwaitAddResponseAsync(
            ulong key,
            Task<NetworkContentAddResponse> responseTask)
        {
            float timeoutSeconds = Mathf.Max(0.25f,
                NetworkInventoryManager.Instance?.RequestTimeoutSeconds ?? 5f);
            Task timeout = Task.Delay(TimeSpan.FromSeconds(timeoutSeconds));
            if (await Task.WhenAny(responseTask, timeout) == responseTask)
            {
                return await responseTask;
            }

            m_PendingAsyncAdds.Remove(key);
            m_DeferredAsyncAddResponses.Remove(key);
            m_PendingAdds.Remove(key);
            return RejectedAdd(InventoryRejectionReason.RequestTimeout);
        }

        internal void CompleteAsyncAdd(NetworkContentAddResponse response)
        {
            ulong key = GetPendingKey(response.ActorNetworkId, response.CorrelationId, response.RequestId);
            if (!m_PendingAsyncAdds.TryGetValue(key, out var completion)) return;
            if (!m_IsServer && response.Authorized && response.StateVersion != 0 &&
                (!m_HasAppliedStateVersion ||
                 unchecked((int)(response.StateVersion - m_LastAppliedStateVersion)) > 0))
            {
                m_DeferredAsyncAddResponses[key] = response;
                return;
            }
            m_PendingAsyncAdds.Remove(key);
            m_DeferredAsyncAddResponses.Remove(key);
            completion.TrySetResult(response);
        }

        internal NetworkInventoryInterceptResult RoutePatchedAddType(
            Item item, Vector2Int position, bool allowStack)
        {
            if (IsApplyingNetworkMutation) return NetworkInventoryInterceptResult.Unhandled;
            if (item == null) return NetworkInventoryInterceptResult.HandledFailure;

            if (m_IsServer)
            {
                // Preserve GC2's RuntimeItem return contract. The native AddType call executes
                // once and OnLocalItemAdded emits the authoritative broadcast afterwards.
                return NetworkInventoryInterceptResult.Unhandled;
            }

            if (!m_IsLocalClient)
            {
                NetworkInventoryPatchHooks.WarnProxyMutation(this, "Add Item");
                return NetworkInventoryInterceptResult.HandledFailure;
            }
            if (!HasClientTransportRoute(
                    NetworkInventoryManager.Instance?.OnSendContentAddRequest, "Add item"))
                return NetworkInventoryInterceptResult.HandledFailure;
            RequestAddItem(item, position, allowStack);
            return NetworkInventoryInterceptResult.HandledSuccess;
        }

        internal async Task<NetworkInventoryInterceptResult> RoutePatchedInstructionAddAsync(Item item)
        {
            NetworkContentAddResponse response = await RequestAddItemAsync(
                item, TBagContent.INVALID, true, InventoryModificationSource.Direct, 0);
            return response.Authorized
                ? NetworkInventoryInterceptResult.HandledSuccess
                : NetworkInventoryInterceptResult.HandledFailure;
        }

        internal NetworkInventoryInterceptResult RoutePatchedRemoveType(Item item)
        {
            if (IsApplyingNetworkMutation) return NetworkInventoryInterceptResult.Unhandled;
            RuntimeItem runtimeItem = FindFirstRuntimeItem(item);
            if (runtimeItem == null) return NetworkInventoryInterceptResult.HandledFailure;

            if (m_IsServer)
            {
                // Preserve GC2's removed RuntimeItem return value. The native removal executes
                // once and OnLocalItemRemoved emits the authoritative broadcast afterwards.
                return NetworkInventoryInterceptResult.Unhandled;
            }

            if (!m_IsLocalClient)
            {
                NetworkInventoryPatchHooks.WarnProxyMutation(this, "Remove Item");
                return NetworkInventoryInterceptResult.HandledFailure;
            }
            if (!HasClientTransportRoute(
                    NetworkInventoryManager.Instance?.OnSendContentRemoveRequest, "Remove item"))
                return NetworkInventoryInterceptResult.HandledFailure;
            RequestRemoveItem(runtimeItem);
            return NetworkInventoryInterceptResult.HandledSuccess;
        }

        private NetworkContentRemoveRequest CreateRemoveRequest(RuntimeItem runtimeItem)
        {
            ushort requestId = GetNextRequestId();
            return new NetworkContentRemoveRequest
            {
                RequestId = requestId,
                ActorNetworkId = NetworkId,
                CorrelationId = NetworkCorrelation.Compose(NetworkId, requestId),
                TargetBagNetworkId = NetworkId,
                RuntimeIdHash = runtimeItem.RuntimeID.Hash,
                UsePosition = false,
                Source = InventoryModificationSource.Direct
            };
        }

        internal RuntimeItem FindFirstRuntimeItem(Item item)
        {
            if (item == null) return null;
            foreach (Cell cell in m_Bag.Content.CellList)
            {
                if (cell == null || cell.Available || cell.Item != item) continue;
                return cell.Peek();
            }
            return null;
        }

        internal NetworkInventoryInterceptResult RoutePatchedMove(
            Vector2Int from, Vector2Int to, bool allowStack)
        {
            if (IsApplyingNetworkMutation) return NetworkInventoryInterceptResult.Unhandled;
            if (m_IsServer)
            {
                ushort requestId = GetNextRequestId();
                var request = new NetworkContentMoveRequest
                {
                    RequestId = requestId,
                    ActorNetworkId = NetworkId,
                    CorrelationId = NetworkCorrelation.Compose(NetworkId, requestId),
                    TargetBagNetworkId = NetworkId,
                    FromPosition = from,
                    ToPosition = to,
                    AllowStack = allowStack
                };
                NetworkContentMoveResponse response;
                using (EnterNetworkMutationScope())
                {
                    response = ProcessContentMoveRequest(request, NetworkId);
                }
                return response.Authorized
                    ? NetworkInventoryInterceptResult.HandledSuccess
                    : NetworkInventoryInterceptResult.HandledFailure;
            }

            if (!m_IsLocalClient)
            {
                NetworkInventoryPatchHooks.WarnProxyMutation(this, "Move/stack");
                return NetworkInventoryInterceptResult.HandledFailure;
            }
            if (!HasClientTransportRoute(
                    NetworkInventoryManager.Instance?.OnSendContentMoveRequest, "Move/stack"))
                return NetworkInventoryInterceptResult.HandledFailure;
            RequestMoveItem(from, to, allowStack);
            return NetworkInventoryInterceptResult.HandledSuccess;
        }

        internal bool RoutePatchedUse(RuntimeItem runtimeItem)
        {
            if (IsApplyingNetworkMutation || m_IsServer) return true;
            if (!m_IsLocalClient || runtimeItem == null)
            {
                if (!m_IsLocalClient) NetworkInventoryPatchHooks.WarnProxyMutation(this, "Use Item");
                return false;
            }
            if (!HasClientTransportRoute(
                    NetworkInventoryManager.Instance?.OnSendContentUseRequest, "Use item"))
                return false;
            RequestUseItem(runtimeItem);
            return false;
        }

        internal bool RoutePatchedPrimitiveAdd(RuntimeItem runtimeItem)
        {
            if (IsApplyingNetworkMutation || m_IsServer) return true;
            if (m_IsLocalClient)
            {
                OnOperationRejected?.Invoke(
                    InventoryRejectionReason.SecurityViolation,
                    "Client-created RuntimeItem add");
            }
            return false;
        }

        internal bool RoutePatchedPrimitiveRemove(RuntimeItem runtimeItem)
        {
            if (IsApplyingNetworkMutation || m_IsServer) return true;
            if (runtimeItem != null &&
                m_SocketAttachPrimitiveRemovePassthrough.Remove(runtimeItem.RuntimeID.Hash))
            {
                // BagEquipment mutates the socket first and removes its attachment immediately
                // afterwards. The socket event already submitted the semantic request; allow
                // only that exact follow-up primitive so it cannot create a second request.
                return true;
            }
            if (!m_IsLocalClient || runtimeItem == null)
            {
                if (!m_IsLocalClient) NetworkInventoryPatchHooks.WarnProxyMutation(this, "Remove RuntimeItem");
                return false;
            }
            if (!HasClientTransportRoute(
                    NetworkInventoryManager.Instance?.OnSendContentRemoveRequest, "Remove RuntimeItem"))
                return false;
            RequestRemoveItem(runtimeItem);
            return false;
        }

        internal bool RoutePatchedDrop(RuntimeItem runtimeItem, Vector3 point)
        {
            if (IsApplyingNetworkMutation || m_IsServer) return true;
            if (!m_IsLocalClient || runtimeItem == null)
            {
                if (!m_IsLocalClient) NetworkInventoryPatchHooks.WarnProxyMutation(this, "Drop Item");
                return false;
            }
            if (!HasClientTransportRoute(
                    NetworkInventoryManager.Instance?.OnSendContentDropRequest, "Drop item"))
                return false;
            RequestDropItem(runtimeItem, point);
            return false;
        }

        internal bool RoutePatchedWealth(IdString currencyId, int value, bool set)
        {
            if (IsApplyingNetworkMutation || m_IsServer) return true;
            if (!m_IsLocalClient)
            {
                NetworkInventoryPatchHooks.WarnProxyMutation(this, "Wealth change");
                return false;
            }
            if (!HasClientTransportRoute(
                    NetworkInventoryManager.Instance?.OnSendWealthRequest, "Wealth change"))
                return false;

            // GC2 implements Subtract by forwarding a negative value to Add. Normalize that
            // representation at the trust boundary so the server only accepts positive Add and
            // Subtract operands and cannot be tricked by sign inversions or Int32.MinValue.
            if (!set && value == int.MinValue)
            {
                NetworkInventoryPatchHooks.WarnProxyMutation(this, "Invalid wealth operand");
                return false;
            }

            WealthAction action = set
                ? WealthAction.Set
                : value < 0 ? WealthAction.Subtract : WealthAction.Add;
            int operand = !set && value < 0 ? -value : value;

            ushort requestId = GetNextRequestId();
            var request = new NetworkWealthRequest
            {
                RequestId = requestId,
                ActorNetworkId = NetworkId,
                CorrelationId = NetworkCorrelation.Compose(NetworkId, requestId),
                TargetBagNetworkId = NetworkId,
                CurrencyHash = currencyId.Hash,
                CurrencyIdString = currencyId.String,
                Value = operand,
                Action = action,
                Source = InventoryModificationSource.Direct
            };
            m_PendingWealth[GetPendingKey(request.ActorNetworkId, request.CorrelationId, request.RequestId)] =
                new PendingWealth
                {
                    Request = request,
                    OriginalValue = m_Bag.Wealth.Get(currencyId),
                    SentTime = Time.time
                };
            OnWealthRequested?.Invoke(request);
            NetworkInventoryManager.Instance?.SendWealthRequest(request);
            return false;
        }

        internal NetworkInventoryInterceptResult RoutePatchedSplit(Vector2Int sourcePosition, int amount)
        {
            if (IsApplyingNetworkMutation) return NetworkInventoryInterceptResult.Unhandled;
            if (amount <= 0) return NetworkInventoryInterceptResult.HandledFailure;

            if (m_IsServer)
            {
                NetworkContentSplitRequest request = CreateSplitRequest(sourcePosition, amount);
                NetworkContentSplitResponse response = ProcessContentSplitRequest(request, NetworkId);
                return response.Authorized
                    ? NetworkInventoryInterceptResult.HandledSuccess
                    : NetworkInventoryInterceptResult.HandledFailure;
            }

            if (!m_IsLocalClient)
            {
                NetworkInventoryPatchHooks.WarnProxyMutation(this, "Split stack");
                return NetworkInventoryInterceptResult.HandledFailure;
            }
            if (!HasClientTransportRoute(
                    NetworkInventoryManager.Instance?.OnSendContentSplitRequest, "Split stack"))
                return NetworkInventoryInterceptResult.HandledFailure;
            _ = RequestSplitAsync(sourcePosition, amount);
            return NetworkInventoryInterceptResult.HandledSuccess;
        }

        public async Task<NetworkContentSplitResponse> RequestSplitAsync(
            Vector2Int sourcePosition, int amount)
        {
            NetworkContentSplitRequest request = CreateSplitRequest(sourcePosition, amount);
            ulong key = GetPendingKey(request.ActorNetworkId, request.CorrelationId, request.RequestId);
            var completion = new TaskCompletionSource<NetworkContentSplitResponse>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            m_PendingAsyncSplits[key] = completion;

            if (m_IsServer)
            {
                ReceiveContentSplitResponse(ProcessContentSplitRequest(request, NetworkId));
            }
            else if (m_IsLocalClient && NetworkInventoryManager.Instance?.OnSendContentSplitRequest != null)
            {
                NetworkInventoryManager.Instance.SendContentSplitRequest(request);
            }
            else
            {
                ReceiveContentSplitResponse(new NetworkContentSplitResponse
                {
                    RequestId = request.RequestId,
                    ActorNetworkId = request.ActorNetworkId,
                    CorrelationId = request.CorrelationId,
                    Authorized = false,
                    RejectionReason = InventoryRejectionReason.NotAuthorized,
                    SourcePosition = sourcePosition,
                    ResultPosition = TBagContent.INVALID
                });
            }

            float timeoutSeconds = Mathf.Max(0.25f,
                NetworkInventoryManager.Instance?.RequestTimeoutSeconds ?? 5f);
            Task timeout = Task.Delay(TimeSpan.FromSeconds(timeoutSeconds));
            if (await Task.WhenAny(completion.Task, timeout) == completion.Task)
                return await completion.Task;

            m_PendingAsyncSplits.Remove(key);
            m_DeferredAsyncSplitResponses.Remove(key);
            return new NetworkContentSplitResponse
            {
                RequestId = request.RequestId,
                ActorNetworkId = request.ActorNetworkId,
                CorrelationId = request.CorrelationId,
                Authorized = false,
                RejectionReason = InventoryRejectionReason.RequestTimeout,
                SourcePosition = sourcePosition,
                ResultPosition = TBagContent.INVALID
            };
        }

        private NetworkContentSplitRequest CreateSplitRequest(Vector2Int sourcePosition, int amount)
        {
            ushort requestId = GetNextRequestId();
            return new NetworkContentSplitRequest
            {
                RequestId = requestId,
                ActorNetworkId = NetworkId,
                CorrelationId = NetworkCorrelation.Compose(NetworkId, requestId),
                TargetBagNetworkId = NetworkId,
                SourcePosition = sourcePosition,
                Amount = amount
            };
        }

        public NetworkContentSplitResponse ProcessContentSplitRequest(
            NetworkContentSplitRequest request, uint clientNetworkId)
        {
            var rejected = new NetworkContentSplitResponse
            {
                RequestId = request.RequestId,
                ActorNetworkId = request.ActorNetworkId,
                CorrelationId = request.CorrelationId,
                Authorized = false,
                RejectionReason = InventoryRejectionReason.InvalidOperation,
                SourcePosition = request.SourcePosition,
                ResultPosition = TBagContent.INVALID
            };

            if (!m_IsServer) return rejected;
            Cell source = m_Bag.Content.GetContent(request.SourcePosition);
            if (source == null || source.Available || source.Count <= 1 || request.Amount <= 0 ||
                request.Amount >= source.Count)
            {
                return rejected;
            }

            if (!m_Bag.Content.CanAddType(source.Item, false))
            {
                rejected.RejectionReason = InventoryRejectionReason.InsufficientSpace;
                return rejected;
            }

            Vector2Int resultPosition = TBagContent.INVALID;
            using (EnterNetworkMutationScope())
            {
                for (int i = 0; i < request.Amount; i++)
                {
                    RuntimeItem removed = m_Bag.Content.Remove(request.SourcePosition);
                    if (removed == null) break;
                    Vector2Int placed = i == 0
                        ? m_Bag.Content.Add(removed, false)
                        : (m_Bag.Content.Add(removed, resultPosition, true)
                            ? resultPosition
                            : TBagContent.INVALID);

                    if (placed == TBagContent.INVALID)
                    {
                        m_Bag.Content.Add(removed, request.SourcePosition, true);
                        break;
                    }

                    resultPosition = placed;
                }
            }

            if (resultPosition == TBagContent.INVALID) return rejected;
            RebuildRuntimeItemMap();
            CacheCurrentSyncState();
            NetworkInventorySnapshot snapshot = GetFullSnapshot();
            NetworkCell sourceNetworkCell = default;
            NetworkCell resultNetworkCell = default;
            if (snapshot.Cells != null)
            {
                for (int i = 0; i < snapshot.Cells.Length; i++)
                {
                    if (snapshot.Cells[i].Position == request.SourcePosition)
                        sourceNetworkCell = snapshot.Cells[i];
                    if (snapshot.Cells[i].Position == resultPosition)
                        resultNetworkCell = snapshot.Cells[i];
                }
            }
            NetworkInventoryManager.Instance?.BroadcastItemSplit(new NetworkItemSplitBroadcast
            {
                BagNetworkId = NetworkId,
                StateVersion = snapshot.StateVersion,
                SourcePosition = request.SourcePosition,
                ResultPosition = resultPosition,
                SourceCell = sourceNetworkCell,
                ResultCell = resultNetworkCell
            });

            return new NetworkContentSplitResponse
            {
                RequestId = request.RequestId,
                ActorNetworkId = request.ActorNetworkId,
                CorrelationId = request.CorrelationId,
                Authorized = true,
                RejectionReason = InventoryRejectionReason.None,
                StateVersion = snapshot.StateVersion,
                SourcePosition = request.SourcePosition,
                ResultPosition = resultPosition
            };
        }

        internal void ReceiveContentSplitResponse(NetworkContentSplitResponse response)
        {
            ulong key = GetPendingKey(response.ActorNetworkId, response.CorrelationId, response.RequestId);
            if (m_PendingAsyncSplits.TryGetValue(key, out var completion))
            {
                if (!m_IsServer && response.Authorized && response.StateVersion != 0 &&
                    !HasAppliedStateVersion(response.StateVersion))
                {
                    m_DeferredAsyncSplitResponses[key] = response;
                }
                else
                {
                    m_PendingAsyncSplits.Remove(key);
                    m_DeferredAsyncSplitResponses.Remove(key);
                    completion.TrySetResult(response);
                }
            }
            if (!response.Authorized)
                OnOperationRejected?.Invoke(response.RejectionReason, "Split item stack");
        }

        internal NetworkInventoryInterceptResult RoutePatchedTransfer(
            NetworkInventoryController destination, RuntimeItem runtimeItem, int amount)
        {
            if (IsApplyingNetworkMutation || destination?.IsApplyingNetworkMutation == true)
                return NetworkInventoryInterceptResult.Unhandled;
            if (destination == null || runtimeItem == null || amount <= 0)
                return NetworkInventoryInterceptResult.HandledFailure;

            ushort requestId = GetNextRequestId();
            var request = new NetworkTransferRequest
            {
                RequestId = requestId,
                ActorNetworkId = m_IsLocalClient ? NetworkId : destination.NetworkId,
                CorrelationId = NetworkCorrelation.Compose(
                    m_IsLocalClient ? NetworkId : destination.NetworkId, requestId),
                SourceBagNetworkId = NetworkId,
                DestinationBagNetworkId = destination.NetworkId,
                RuntimeIdHash = runtimeItem.RuntimeID.Hash,
                DestinationPosition = TBagContent.INVALID,
                AllowStack = true,
                Amount = amount,
                Source = InventoryModificationSource.Trade
            };

            if (m_IsServer)
            {
                NetworkTransferResponse response;
                using (EnterNetworkMutationScope())
                using (destination.EnterNetworkMutationScope())
                {
                    response = ProcessTransferRequest(request, destination, NetworkId);
                }
                return response.Authorized
                    ? NetworkInventoryInterceptResult.HandledSuccess
                    : NetworkInventoryInterceptResult.HandledFailure;
            }

            if (!m_IsLocalClient && !destination.m_IsLocalClient)
                return NetworkInventoryInterceptResult.HandledFailure;
            if (!HasClientTransportRoute(
                    NetworkInventoryManager.Instance?.OnSendTransferRequest, "Transfer item"))
                return NetworkInventoryInterceptResult.HandledFailure;
            NetworkInventoryManager.Instance.SendTransferRequest(request);
            return NetworkInventoryInterceptResult.HandledSuccess;
        }

        internal NetworkInventoryInterceptResult RoutePatchedCombine(Vector2Int a, Vector2Int b)
        {
            if (IsApplyingNetworkMutation) return NetworkInventoryInterceptResult.Unhandled;
            ushort requestId = GetNextRequestId();
            var request = new NetworkCombineRequest
            {
                RequestId = requestId,
                ActorNetworkId = NetworkId,
                CorrelationId = NetworkCorrelation.Compose(NetworkId, requestId),
                BagNetworkId = NetworkId,
                PositionA = a,
                PositionB = b
            };

            if (m_IsServer)
            {
                return NetworkInventoryManager.Instance?.ProcessCombineLocally(request).Authorized == true
                    ? NetworkInventoryInterceptResult.HandledSuccess
                    : NetworkInventoryInterceptResult.HandledFailure;
            }

            if (!m_IsLocalClient) return NetworkInventoryInterceptResult.HandledFailure;
            if (!HasClientTransportRoute(
                    NetworkInventoryManager.Instance?.OnSendCombineRequest, "Combine items"))
                return NetworkInventoryInterceptResult.HandledFailure;
            NetworkInventoryManager.Instance.SendCombineRequest(request);
            return NetworkInventoryInterceptResult.HandledSuccess;
        }

        internal NetworkInventoryInterceptResult RoutePatchedSocketAttach(
            RuntimeItem parent,
            RuntimeItem attachment,
            IdString socketId)
        {
            if (IsApplyingNetworkMutation) return NetworkInventoryInterceptResult.Unhandled;
            if (parent == null || attachment == null)
                return NetworkInventoryInterceptResult.HandledFailure;

            // Preserve GC2's synchronous return value for authoritative native callers.
            if (m_IsServer) return NetworkInventoryInterceptResult.Unhandled;

            if (!m_IsLocalClient)
            {
                NetworkInventoryPatchHooks.WarnProxyMutation(this, "socket attachment");
                return NetworkInventoryInterceptResult.HandledFailure;
            }

            if (!HasClientTransportRoute(
                    NetworkInventoryManager.Instance?.OnSendSocketRequest,
                    "Attach inventory socket"))
            {
                return NetworkInventoryInterceptResult.HandledFailure;
            }

            RequestAttachToSocket(parent, attachment, socketId);
            return NetworkInventoryInterceptResult.HandledSuccess;
        }

        internal NetworkInventoryInterceptResult RoutePatchedSocketDetach(
            RuntimeItem parent,
            IdString socketId)
        {
            if (IsApplyingNetworkMutation) return NetworkInventoryInterceptResult.Unhandled;
            if (parent == null || string.IsNullOrEmpty(socketId.String))
                return NetworkInventoryInterceptResult.HandledFailure;

            if (m_IsServer) return NetworkInventoryInterceptResult.Unhandled;

            if (!m_IsLocalClient)
            {
                NetworkInventoryPatchHooks.WarnProxyMutation(this, "socket detachment");
                return NetworkInventoryInterceptResult.HandledFailure;
            }

            if (!HasClientTransportRoute(
                    NetworkInventoryManager.Instance?.OnSendSocketRequest,
                    "Detach inventory socket"))
            {
                return NetworkInventoryInterceptResult.HandledFailure;
            }

            RequestDetachFromSocket(parent, socketId);
            return NetworkInventoryInterceptResult.HandledSuccess;
        }

        internal bool HasClientTransportRoute(Delegate route, string operation)
        {
            if (m_IsServer || route != null) return true;
            OnOperationRejected?.Invoke(InventoryRejectionReason.NotAuthorized, operation);
            if (m_LogRejections)
            {
                Debug.LogWarning(
                    $"[NetworkInventoryController] {operation} was rejected because no Inventory " +
                    "client-to-server transport route is installed.",
                    this);
            }
            return false;
        }

        public bool TryServerGrantItem(
            Item item,
            Vector2Int position = default,
            bool allowStack = true,
            InventoryModificationSource source = InventoryModificationSource.Admin,
            int sourceHash = 0)
        {
            return TryServerGrantItem(
                item, position, allowStack, source, sourceHash, out _);
        }

        internal bool TryServerGrantItem(
            Item item,
            Vector2Int position,
            bool allowStack,
            InventoryModificationSource source,
            int sourceHash,
            out NetworkContentAddResponse response)
        {
            response = RejectedAdd(InventoryRejectionReason.NotAuthorized);
            if (!m_IsServer || item == null) return false;
            if (position == default) position = TBagContent.INVALID;
            NetworkContentAddRequest request = CreateAddRequest(item, position, allowStack, source, sourceHash);
            using (EnterNetworkMutationScope())
            {
                response = ProcessContentAddRequest(request, NetworkId);
                return response.Authorized;
            }
        }

        internal bool TryServerRemoveRuntimeItem(long runtimeIdHash)
        {
            RuntimeItem item = FindRuntimeItem(runtimeIdHash);
            if (!m_IsServer || item == null) return false;
            NetworkContentRemoveRequest request = CreateRemoveRequest(item);
            using (EnterNetworkMutationScope())
            {
                return ProcessContentRemoveRequest(request, NetworkId).Authorized;
            }
        }

        public bool TryServerGrantRuntimeItem(
            NetworkRuntimeItem networkItem,
            Vector2Int position,
            bool allowStack = true)
        {
            if (!m_IsServer || networkItem.ItemHash == 0) return false;
            RuntimeItem runtimeItem = ReconstructRuntimeItem(networkItem);
            if (runtimeItem == null) return false;

            Vector2Int result;
            using (EnterNetworkMutationScope())
            {
                result = position.x >= 0 && position.y >= 0
                    ? (m_Bag.Content.Add(runtimeItem, position, allowStack) ? position : TBagContent.INVALID)
                    : m_Bag.Content.Add(runtimeItem, allowStack);
            }
            if (result == TBagContent.INVALID) return false;

            TrackRuntimeItemRecursive(runtimeItem);
            uint stateVersion = GetAuthoritativeStateVersion();
            var broadcast = new NetworkItemAddedBroadcast
            {
                BagNetworkId = NetworkId,
                Item = ConvertToNetworkItem(runtimeItem),
                Position = result,
                StackCount = m_Bag.Content.GetContent(result)?.Count ?? 1,
                StateVersion = stateVersion
            };
            NetworkInventoryManager.Instance?.BroadcastItemAdded(broadcast);
            CacheCurrentSyncState();
            return true;
        }

        internal RuntimeItem FindRuntimeItem(long runtimeIdHash)
        {
            return m_RuntimeItemMap.TryGetValue(runtimeIdHash, out RuntimeItem item) ? item : null;
        }

        internal NetworkRuntimeItem ToNetworkRuntimeItem(RuntimeItem item)
        {
            return ConvertToNetworkItem(item);
        }

        internal void BroadcastAuthoritativeSnapshot()
        {
            RebuildRuntimeItemMap();
            CacheCurrentSyncState();
            NetworkInventoryManager.Instance?.BroadcastFullSnapshot(GetFullSnapshot());
        }

        internal uint CurrentAuthoritativeStateVersion => GetAuthoritativeStateVersion();

        /// <summary>
        /// Diagnostic generation incremented only after a full authoritative snapshot has
        /// completed structural reconciliation. The generated Inventory regression scene uses
        /// this to distinguish a real recovery snapshot from a timeout that happened to leave
        /// the same local objects in place.
        /// </summary>
        internal uint FullSnapshotApplyCount => m_FullSnapshotApplyCount;

        internal async void ReceiveTransactionResponse(
            bool authorized,
            InventoryRejectionReason reason,
            string operation,
            uint stateVersion = 0)
        {
            if (!authorized)
            {
                OnOperationRejected?.Invoke(reason, operation);
                return;
            }

            if (await WaitForAppliedStateVersionAsync(stateVersion)) return;
            OnOperationRejected?.Invoke(InventoryRejectionReason.RequestTimeout,
                $"{operation} confirmation");
        }

        /// <summary>
        /// Waits until a confirmed semantic operation's authoritative bag revision has actually
        /// converged locally. Transaction UI uses this instead of treating receipt of the response
        /// packet as completion.
        /// </summary>
        internal async Task<bool> WaitForAppliedStateVersionAsync(uint stateVersion)
        {
            if (m_IsServer || stateVersion == 0 || HasAppliedStateVersion(stateVersion)) return true;

            float timeoutSeconds = Mathf.Max(0.25f,
                NetworkInventoryManager.Instance?.RequestTimeoutSeconds ?? 5f);
            float deadline = Time.realtimeSinceStartup + timeoutSeconds;
            while (this != null && !HasAppliedStateVersion(stateVersion) &&
                   Time.realtimeSinceStartup < deadline)
            {
                await Task.Delay(10);
            }

            if (this != null && HasAppliedStateVersion(stateVersion)) return true;
            if (this != null) RequestAuthoritativeResync(stateVersion);
            return false;
        }

        private bool TryAcceptMutationVersion(uint incomingVersion)
        {
            if (incomingVersion == 0) return true;
            if (!m_HasAppliedStateVersion) return true;

            int delta = unchecked((int)(incomingVersion - m_LastAppliedStateVersion));
            if (delta <= 0) return false;
            if (delta > 1)
            {
                RequestAuthoritativeResync(incomingVersion);
                return false;
            }

            return true;
        }

        private bool FinishMutationVersion(uint incomingVersion, bool converged, string operation)
        {
            if (!converged)
            {
                RequestAuthoritativeResync(incomingVersion);
                if (m_LogRejections)
                {
                    Debug.LogWarning(
                        $"[NetworkInventoryController] Could not apply confirmed {operation} at " +
                        $"state version {incomingVersion}; requested a full resync.",
                        this);
                }
                return false;
            }

            CacheCurrentSyncState();
            RecordAppliedStateVersion(incomingVersion);
            return true;
        }

        private void RequestAuthoritativeResync(uint incomingVersion)
        {
            NetworkInventoryManager.Instance?.SendResyncRequest(new NetworkInventoryResyncRequest
            {
                ActorNetworkId = NetworkId,
                CorrelationId = NetworkCorrelation.Compose(NetworkId, incomingVersion),
                BagNetworkId = NetworkId,
                LastAppliedStateVersion = m_HasAppliedStateVersion ? m_LastAppliedStateVersion : 0u
            });
        }

        internal bool HasAppliedStateVersion(uint version)
        {
            if (version == 0 || m_IsServer) return true;
            return m_HasAppliedStateVersion &&
                   unchecked((int)(m_LastAppliedStateVersion - version)) >= 0;
        }

        private void TryCompleteDeferredAsyncAdds()
        {
            if (!m_HasAppliedStateVersion) return;

            if (m_DeferredAsyncAddResponses.Count > 0)
            {
                s_SharedKeyBuffer.Clear();
                foreach (var entry in m_DeferredAsyncAddResponses)
                {
                    if (entry.Value.StateVersion != 0 &&
                        unchecked((int)(entry.Value.StateVersion - m_LastAppliedStateVersion)) > 0)
                    {
                        continue;
                    }
                    s_SharedKeyBuffer.Add(entry.Key);
                }

                for (int i = 0; i < s_SharedKeyBuffer.Count; i++)
                {
                    ulong key = s_SharedKeyBuffer[i];
                    if (!m_DeferredAsyncAddResponses.TryGetValue(key, out var response) ||
                        !m_PendingAsyncAdds.TryGetValue(key, out var completion)) continue;
                    m_DeferredAsyncAddResponses.Remove(key);
                    m_PendingAsyncAdds.Remove(key);
                    completion.TrySetResult(response);
                }
            }

            if (m_DeferredAsyncSplitResponses.Count > 0)
            {
                s_SharedKeyBuffer.Clear();
                foreach (var entry in m_DeferredAsyncSplitResponses)
                {
                    if (entry.Value.StateVersion != 0 &&
                        unchecked((int)(entry.Value.StateVersion - m_LastAppliedStateVersion)) > 0)
                    {
                        continue;
                    }
                    s_SharedKeyBuffer.Add(entry.Key);
                }

                for (int i = 0; i < s_SharedKeyBuffer.Count; i++)
                {
                    ulong key = s_SharedKeyBuffer[i];
                    if (!m_DeferredAsyncSplitResponses.TryGetValue(key, out var response) ||
                        !m_PendingAsyncSplits.TryGetValue(key, out var completion)) continue;
                    m_DeferredAsyncSplitResponses.Remove(key);
                    m_PendingAsyncSplits.Remove(key);
                    completion.TrySetResult(response);
                }
            }
        }
    }
}
#endif
