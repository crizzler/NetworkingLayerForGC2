#if GC2_INVENTORY
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Arawn.GameCreator2.Networking.Security;
using GameCreator.Runtime.Inventory;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Inventory
{
    public partial class NetworkInventoryManager
    {
        public Action<NetworkContentSplitRequest> OnSendContentSplitRequest;
        public Action<uint, NetworkContentSplitResponse> OnSendContentSplitResponse;
        public Action<NetworkItemSplitBroadcast> OnBroadcastItemSplit;
        public Action<NetworkInventoryResyncRequest> OnSendResyncRequest;

        private ushort m_NextSemanticRequestId = 1;

        private sealed class PendingMerchantTransaction
        {
            public NetworkMerchantRequest Request;
            public TaskCompletionSource<NetworkMerchantResponse> Completion;
        }

        private sealed class PendingCraftingTransaction
        {
            public NetworkCraftingRequest Request;
            public TaskCompletionSource<NetworkCraftingResponse> Completion;
        }

        private readonly Dictionary<ulong, PendingMerchantTransaction>
            m_PendingMerchantTransactions = new(8);
        private readonly Dictionary<ulong, PendingCraftingTransaction>
            m_PendingCraftingTransactions = new(8);

        public void SendContentSplitRequest(NetworkContentSplitRequest request)
        {
            OnSendContentSplitRequest?.Invoke(request);
        }

        public void SendContentSplitResponse(uint targetClientId, NetworkContentSplitResponse response)
        {
            OnSendContentSplitResponse?.Invoke(targetClientId, response);
        }

        public void BroadcastItemSplit(NetworkItemSplitBroadcast broadcast)
        {
            OnBroadcastItemSplit?.Invoke(broadcast);
            if (!m_IsServer) ReceiveItemSplitBroadcast(broadcast);
        }

        public void SendResyncRequest(NetworkInventoryResyncRequest request)
        {
            OnSendResyncRequest?.Invoke(request);
        }

        public void ReceiveContentSplitRequest(NetworkContentSplitRequest request, ulong clientId)
        {
            if (!m_IsServer) return;
            uint senderClientId = GetSenderClientId(clientId);
            NetworkContentSplitResponse response = new NetworkContentSplitResponse
            {
                RequestId = request.RequestId,
                ActorNetworkId = request.ActorNetworkId,
                CorrelationId = request.CorrelationId,
                Authorized = false,
                RejectionReason = InventoryRejectionReason.SecurityViolation,
                SourcePosition = request.SourcePosition,
                ResultPosition = TBagContent.INVALID
            };

            if (!SecurityIntegration.ValidateModuleRequest(
                    senderClientId,
                    BuildContext(request.ActorNetworkId, request.CorrelationId),
                    "Inventory",
                    nameof(NetworkContentSplitRequest)) ||
                !ValidateTargetOwnership(senderClientId, request.ActorNetworkId,
                    request.TargetBagNetworkId, nameof(NetworkContentSplitRequest)))
            {
                SendContentSplitResponse(senderClientId, response);
                return;
            }

            if (!CheckRateLimit(clientId))
            {
                response.RejectionReason = InventoryRejectionReason.RateLimitExceeded;
                SendContentSplitResponse(senderClientId, response);
                return;
            }

            try
            {
                NetworkInventoryController controller = GetController(request.TargetBagNetworkId);
                if (controller == null)
                {
                    response.RejectionReason = InventoryRejectionReason.BagNotFound;
                }
                else
                {
                    response = controller.ProcessContentSplitRequest(request, senderClientId);
                }
                SendContentSplitResponse(senderClientId, response);
            }
            finally
            {
                DecrementPendingRequests(clientId);
            }
        }

        public void ReceiveContentSplitResponse(NetworkContentSplitResponse response, uint targetNetworkId)
        {
            GetController(targetNetworkId)?.ReceiveContentSplitResponse(response);
        }

        public void ReceiveItemSplitBroadcast(NetworkItemSplitBroadcast broadcast)
        {
            NetworkInventoryController controller = GetController(broadcast.BagNetworkId);
            if (controller == null)
            {
                QueuePendingPersistentState(broadcast.BagNetworkId, PendingPersistentStateKind.Delta,
                    new NetworkInventoryDelta
                    {
                        BagNetworkId = broadcast.BagNetworkId,
                        StateVersion = broadcast.StateVersion,
                        Timestamp = Time.time,
                        ChangeMask = 1u,
                        ChangedCells = new[] { broadcast.SourceCell, broadcast.ResultCell },
                        ChangedEquipment = Array.Empty<NetworkEquipmentSlot>(),
                        ChangedWealth = Array.Empty<NetworkWealthEntry>()
                    });
                return;
            }

            controller.ReceiveDelta(new NetworkInventoryDelta
            {
                BagNetworkId = broadcast.BagNetworkId,
                StateVersion = broadcast.StateVersion,
                Timestamp = Time.time,
                ChangeMask = 1u,
                ChangedCells = new[] { broadcast.SourceCell, broadcast.ResultCell },
                ChangedEquipment = Array.Empty<NetworkEquipmentSlot>(),
                ChangedWealth = Array.Empty<NetworkWealthEntry>()
            });
        }

        public void ReceiveResyncRequest(NetworkInventoryResyncRequest request, ulong clientId)
        {
            if (!m_IsServer) return;
            uint senderClientId = GetSenderClientId(clientId);
            if (!SecurityIntegration.ValidateModuleRequest(
                    senderClientId,
                    BuildContext(request.ActorNetworkId, request.CorrelationId),
                    "Inventory",
                    nameof(NetworkInventoryResyncRequest)))
            {
                return;
            }

            NetworkInventoryController controller = GetController(request.BagNetworkId);
            if (controller == null) return;
            if (!ValidateTargetOwnership(senderClientId, request.ActorNetworkId,
                    request.BagNetworkId, nameof(NetworkInventoryResyncRequest))) return;
            SendSnapshotToClient(clientId, controller.GetFullSnapshot());
        }

        /// <summary>
        /// Requests one server-authoritative merchant transaction. The returned task completes
        /// only after the confirmed client-bag revision has been applied locally.
        /// </summary>
        public async Task<NetworkMerchantResponse> RequestMerchantAsync(
            Merchant merchant,
            Bag clientBag,
            RuntimeItem runtimeItem,
            MerchantAction action)
        {
            if (!NetworkInventoryController.TryResolveForBag(clientBag, out var clientController) ||
                !NetworkInventoryController.TryResolveForBag(merchant?.Bag, out var merchantController) ||
                runtimeItem == null)
            {
                return RejectMerchant(default, InventoryRejectionReason.BagNotFound);
            }

            if (!clientController.IsServer && !clientController.IsLocalClient)
                return RejectMerchant(default, InventoryRejectionReason.NotOwner);

            ushort requestId = NextSemanticRequestId();
            var request = new NetworkMerchantRequest
            {
                RequestId = requestId,
                ActorNetworkId = clientController.NetworkId,
                CorrelationId = NetworkCorrelation.Compose(clientController.NetworkId, requestId),
                ClientBagNetworkId = clientController.NetworkId,
                MerchantNetworkId = merchantController.NetworkId,
                RuntimeIdHash = runtimeItem.RuntimeID.Hash,
                Action = action,
                Amount = 1
            };

            if (!m_IsServer && !clientController.HasClientTransportRoute(
                    OnSendMerchantRequest, "Merchant transaction"))
            {
                return RejectMerchant(request, InventoryRejectionReason.NotAuthorized);
            }

            ulong key = GetSemanticPendingKey(
                request.ActorNetworkId, request.CorrelationId, request.RequestId);
            var completion = new TaskCompletionSource<NetworkMerchantResponse>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            m_PendingMerchantTransactions[key] = new PendingMerchantTransaction
            {
                Request = request,
                Completion = completion
            };

            if (m_IsServer)
            {
                ReceiveMerchantResponse(ProcessMerchantLocally(request), request.ActorNetworkId);
            }
            else
            {
                SendMerchantRequest(request);
            }

            return await AwaitMerchantResponseAsync(key, request, completion.Task);
        }

        /// <summary>
        /// Requests one server-authoritative craft. Completion means the confirmed bag revision
        /// is visible locally, not merely that the response packet arrived.
        /// </summary>
        public Task<NetworkCraftingResponse> RequestCraftAsync(Item item, Bag input, Bag output)
        {
            return RequestCraftingAsync(
                item, null, input, output, 1f, CraftingAction.Craft);
        }

        /// <summary>Requests server-authoritative dismantling by Item type.</summary>
        public Task<NetworkCraftingResponse> RequestDismantleAsync(
            Item item, Bag input, Bag output, float chance)
        {
            return RequestCraftingAsync(
                item, null, input, output, chance, CraftingAction.Dismantle);
        }

        /// <summary>Requests server-authoritative dismantling of one exact RuntimeItem.</summary>
        public Task<NetworkCraftingResponse> RequestDismantleAsync(
            RuntimeItem runtimeItem, Bag input, Bag output, float chance)
        {
            return RequestCraftingAsync(
                runtimeItem?.Item, runtimeItem, input, output, chance, CraftingAction.Dismantle);
        }

        private async Task<NetworkCraftingResponse> RequestCraftingAsync(
            Item item,
            RuntimeItem runtimeItem,
            Bag input,
            Bag output,
            float chance,
            CraftingAction action)
        {
            if (item == null ||
                !NetworkInventoryController.TryResolveForBag(input, out var inputController) ||
                !NetworkInventoryController.TryResolveForBag(output, out var outputController))
            {
                return RejectCrafting(default, InventoryRejectionReason.BagNotFound);
            }

            if (!inputController.IsServer && !inputController.IsLocalClient)
                return RejectCrafting(default, InventoryRejectionReason.NotOwner);

            ushort requestId = NextSemanticRequestId();
            var request = new NetworkCraftingRequest
            {
                RequestId = requestId,
                ActorNetworkId = inputController.NetworkId,
                CorrelationId = NetworkCorrelation.Compose(inputController.NetworkId, requestId),
                InputBagNetworkId = inputController.NetworkId,
                OutputBagNetworkId = outputController.NetworkId,
                ItemHash = item.ID.Hash,
                ItemIdString = item.ID.String,
                RuntimeIdHash = runtimeItem?.RuntimeID.Hash ?? 0,
                Action = action,
                Chance = chance
            };

            if (!m_IsServer && !inputController.HasClientTransportRoute(
                    OnSendCraftingRequest, "Crafting transaction"))
            {
                return RejectCrafting(request, InventoryRejectionReason.NotAuthorized);
            }

            ulong key = GetSemanticPendingKey(
                request.ActorNetworkId, request.CorrelationId, request.RequestId);
            var completion = new TaskCompletionSource<NetworkCraftingResponse>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            m_PendingCraftingTransactions[key] = new PendingCraftingTransaction
            {
                Request = request,
                Completion = completion
            };

            if (m_IsServer)
            {
                ReceiveCraftingResponse(ProcessCraftingLocally(request), request.ActorNetworkId);
            }
            else
            {
                SendCraftingRequest(request);
            }

            return await AwaitCraftingResponseAsync(key, request, completion.Task);
        }

        public void ReceiveMerchantRequest(NetworkMerchantRequest request, ulong clientId)
        {
            if (!m_IsServer) return;
            uint senderClientId = GetSenderClientId(clientId);
            NetworkMerchantResponse response = RejectMerchant(request, InventoryRejectionReason.SecurityViolation);

            if (!ValidateTransactionRequest(senderClientId, request.ActorNetworkId,
                    request.CorrelationId, request.ClientBagNetworkId, nameof(NetworkMerchantRequest)) ||
                !ValidateTargetOwnership(senderClientId, request.ActorNetworkId,
                    request.MerchantNetworkId, nameof(NetworkMerchantRequest)))
            {
                SendMerchantResponse(senderClientId, response);
                return;
            }

            if ((request.Action != MerchantAction.BuyFromMerchant &&
                 request.Action != MerchantAction.SellToMerchant) ||
                request.Amount != 1)
            {
                response.RejectionReason = InventoryRejectionReason.InvalidOperation;
                SendMerchantResponse(senderClientId, response);
                return;
            }

            if (!CheckRateLimit(clientId))
            {
                response.RejectionReason = InventoryRejectionReason.RateLimitExceeded;
                SendMerchantResponse(senderClientId, response);
                return;
            }

            try
            {
                if (CustomMerchantValidator != null)
                {
                    var validation = CustomMerchantValidator(request, senderClientId);
                    if (!validation.allowed)
                    {
                        response.RejectionReason = validation.reason;
                        SendMerchantResponse(senderClientId, response);
                        return;
                    }
                }

                response = ProcessMerchantLocally(request);
                SendMerchantResponse(senderClientId, response);
            }
            finally
            {
                DecrementPendingRequests(clientId);
            }
        }

        public void ReceiveMerchantResponse(NetworkMerchantResponse response, uint targetNetworkId)
        {
            _ = CompleteMerchantResponseAsync(response, targetNetworkId);
        }

        public void ReceiveCraftingRequest(NetworkCraftingRequest request, ulong clientId)
        {
            if (!m_IsServer) return;
            uint senderClientId = GetSenderClientId(clientId);
            NetworkCraftingResponse response = RejectCrafting(request, InventoryRejectionReason.SecurityViolation);

            if (!ValidateTransactionRequest(senderClientId, request.ActorNetworkId,
                    request.CorrelationId, request.InputBagNetworkId, nameof(NetworkCraftingRequest)) ||
                !ValidateTargetOwnership(senderClientId, request.ActorNetworkId,
                    request.OutputBagNetworkId, nameof(NetworkCraftingRequest)))
            {
                SendCraftingResponse(senderClientId, response);
                return;
            }


            bool validAction = request.Action == CraftingAction.Craft ||
                               request.Action == CraftingAction.Dismantle;
            bool validChance = !float.IsNaN(request.Chance) &&
                               !float.IsInfinity(request.Chance) &&
                               request.Chance >= 0f && request.Chance <= 1f;
            if (!validAction || !validChance)
            {
                response.RejectionReason = InventoryRejectionReason.InvalidOperation;
                SendCraftingResponse(senderClientId, response);
                return;
            }

            if (!CheckRateLimit(clientId))
            {
                response.RejectionReason = InventoryRejectionReason.RateLimitExceeded;
                SendCraftingResponse(senderClientId, response);
                return;
            }

            try
            {
                // Dismantle probability is authored gameplay data but the legacy request carries
                // it from the client. Secure mode therefore requires an explicit project validator
                // to attest that the submitted chance matches the recipe/interaction being used.
                if (request.Action == CraftingAction.Dismantle && CustomCraftingValidator == null)
                {
                    response.RejectionReason = InventoryRejectionReason.NotAuthorized;
                    SendCraftingResponse(senderClientId, response);
                    return;
                }

                if (CustomCraftingValidator != null)
                {
                    var validation = CustomCraftingValidator(request, senderClientId);
                    if (!validation.allowed)
                    {
                        response.RejectionReason = validation.reason == InventoryRejectionReason.None
                            ? InventoryRejectionReason.NotAuthorized
                            : validation.reason;
                        SendCraftingResponse(senderClientId, response);
                        return;
                    }
                }

                response = ProcessCraftingLocally(request);
                SendCraftingResponse(senderClientId, response);
            }
            finally
            {
                DecrementPendingRequests(clientId);
            }
        }

        public void ReceiveCraftingResponse(NetworkCraftingResponse response, uint targetNetworkId)
        {
            _ = CompleteCraftingResponseAsync(response, targetNetworkId);
        }

        public void ReceiveCombineRequest(NetworkCombineRequest request, ulong clientId)
        {
            if (!m_IsServer) return;
            uint senderClientId = GetSenderClientId(clientId);
            NetworkCombineResponse response = new NetworkCombineResponse
            {
                RequestId = request.RequestId,
                ActorNetworkId = request.ActorNetworkId,
                CorrelationId = request.CorrelationId,
                Authorized = false,
                RejectionReason = InventoryRejectionReason.SecurityViolation,
                ResultPosition = TBagContent.INVALID
            };

            if (!ValidateTransactionRequest(senderClientId, request.ActorNetworkId,
                    request.CorrelationId, request.BagNetworkId, nameof(NetworkCombineRequest)))
            {
                SendCombineResponse(senderClientId, response);
                return;
            }

            if (!CheckRateLimit(clientId))
            {
                response.RejectionReason = InventoryRejectionReason.RateLimitExceeded;
                SendCombineResponse(senderClientId, response);
                return;
            }

            try
            {
                response = ProcessCombineLocally(request);
                SendCombineResponse(senderClientId, response);
            }
            finally
            {
                DecrementPendingRequests(clientId);
            }
        }

        public void ReceiveCombineResponse(NetworkCombineResponse response, uint targetNetworkId)
        {
            GetController(targetNetworkId)?.ReceiveTransactionResponse(
                response.Authorized, response.RejectionReason, "Combine items",
                response.StateVersion);
        }

        private bool ValidateTransactionRequest(
            uint senderClientId, uint actorNetworkId, uint correlationId, uint bagId, string requestType)
        {
            return SecurityIntegration.ValidateModuleRequest(
                       senderClientId,
                       BuildContext(actorNetworkId, correlationId),
                       "Inventory",
                       requestType) &&
                   ValidateTargetOwnership(senderClientId, actorNetworkId, bagId, requestType);
        }

        private void SendMerchantResponse(uint targetClientId, NetworkMerchantResponse response)
        {
            OnSendMerchantResponse?.Invoke(targetClientId, response);
        }

        private void SendCraftingResponse(uint targetClientId, NetworkCraftingResponse response)
        {
            OnSendCraftingResponse?.Invoke(targetClientId, response);
        }

        private void SendCombineResponse(uint targetClientId, NetworkCombineResponse response)
        {
            OnSendCombineResponse?.Invoke(targetClientId, response);
        }

        private async Task<NetworkMerchantResponse> AwaitMerchantResponseAsync(
            ulong key,
            NetworkMerchantRequest request,
            Task<NetworkMerchantResponse> responseTask)
        {
            Task timeout = Task.Delay(TimeSpan.FromSeconds(Mathf.Max(0.25f, RequestTimeoutSeconds)));
            if (await Task.WhenAny(responseTask, timeout) == responseTask)
                return await responseTask;

            m_PendingMerchantTransactions.Remove(key);
            return RejectMerchant(request, InventoryRejectionReason.RequestTimeout);
        }

        private async Task<NetworkCraftingResponse> AwaitCraftingResponseAsync(
            ulong key,
            NetworkCraftingRequest request,
            Task<NetworkCraftingResponse> responseTask)
        {
            Task timeout = Task.Delay(TimeSpan.FromSeconds(Mathf.Max(0.25f, RequestTimeoutSeconds)));
            if (await Task.WhenAny(responseTask, timeout) == responseTask)
                return await responseTask;

            m_PendingCraftingTransactions.Remove(key);
            return RejectCrafting(request, InventoryRejectionReason.RequestTimeout);
        }

        private async Task CompleteMerchantResponseAsync(
            NetworkMerchantResponse response, uint targetNetworkId)
        {
            uint actorId = response.ActorNetworkId != 0 ? response.ActorNetworkId : targetNetworkId;
            ulong key = GetSemanticPendingKey(actorId, response.CorrelationId, response.RequestId);
            NetworkInventoryController controller = GetController(actorId);

            if (!m_PendingMerchantTransactions.TryGetValue(key, out PendingMerchantTransaction pending))
            {
                controller?.ReceiveTransactionResponse(
                    response.Authorized, response.RejectionReason, "Merchant transaction",
                    response.StateVersion);
                return;
            }

            if (response.Authorized &&
                (controller == null || !await controller.WaitForAppliedStateVersionAsync(response.StateVersion)))
            {
                response.Authorized = false;
                response.RejectionReason = InventoryRejectionReason.RequestTimeout;
            }

            m_PendingMerchantTransactions.Remove(key);
            pending.Completion.TrySetResult(response);
            if (!response.Authorized)
            {
                controller?.ReceiveTransactionResponse(
                    false, response.RejectionReason, "Merchant transaction", 0u);
            }
        }

        private async Task CompleteCraftingResponseAsync(
            NetworkCraftingResponse response, uint targetNetworkId)
        {
            uint actorId = response.ActorNetworkId != 0 ? response.ActorNetworkId : targetNetworkId;
            ulong key = GetSemanticPendingKey(actorId, response.CorrelationId, response.RequestId);
            NetworkInventoryController controller = GetController(actorId);

            if (!m_PendingCraftingTransactions.TryGetValue(key, out PendingCraftingTransaction pending))
            {
                controller?.ReceiveTransactionResponse(
                    response.Authorized, response.RejectionReason, "Crafting transaction",
                    response.StateVersion);
                return;
            }

            if (response.Authorized &&
                (controller == null || !await controller.WaitForAppliedStateVersionAsync(response.StateVersion)))
            {
                response.Authorized = false;
                response.RejectionReason = InventoryRejectionReason.RequestTimeout;
            }

            m_PendingCraftingTransactions.Remove(key);
            pending.Completion.TrySetResult(response);
            if (!response.Authorized)
            {
                controller?.ReceiveTransactionResponse(
                    false, response.RejectionReason, "Crafting transaction", 0u);
            }
        }

        private static ulong GetSemanticPendingKey(
            uint actorNetworkId, uint correlationId, ushort requestId)
        {
            uint pendingCorrelation = correlationId != 0 ? correlationId : requestId;
            return ((ulong)actorNetworkId << 32) | pendingCorrelation;
        }

        internal void CancelPendingSemanticTransactions()
        {
            foreach (PendingMerchantTransaction pending in m_PendingMerchantTransactions.Values)
            {
                pending.Completion.TrySetResult(RejectMerchant(
                    pending.Request, InventoryRejectionReason.RequestTimeout));
            }
            m_PendingMerchantTransactions.Clear();

            foreach (PendingCraftingTransaction pending in m_PendingCraftingTransactions.Values)
            {
                pending.Completion.TrySetResult(RejectCrafting(
                    pending.Request, InventoryRejectionReason.RequestTimeout));
            }
            m_PendingCraftingTransactions.Clear();
        }

        internal NetworkInventoryInterceptResult RoutePatchedMerchant(
            Merchant merchant, Bag clientBag, RuntimeItem runtimeItem, MerchantAction action)
        {
            if (!NetworkInventoryController.TryResolveForBag(clientBag, out var clientController) ||
                !NetworkInventoryController.TryResolveForBag(merchant?.Bag, out var merchantController))
            {
                return NetworkInventoryInterceptResult.Unhandled;
            }

            if (clientController.IsApplyingNetworkMutation || merchantController.IsApplyingNetworkMutation)
                return NetworkInventoryInterceptResult.Unhandled;

            ushort requestId = NextSemanticRequestId();
            var request = new NetworkMerchantRequest
            {
                RequestId = requestId,
                ActorNetworkId = clientController.NetworkId,
                CorrelationId = NetworkCorrelation.Compose(clientController.NetworkId, requestId),
                ClientBagNetworkId = clientController.NetworkId,
                MerchantNetworkId = merchantController.NetworkId,
                RuntimeIdHash = runtimeItem?.RuntimeID.Hash ?? 0,
                Action = action,
                Amount = 1
            };

            if (m_IsServer)
            {
                return ProcessMerchantLocally(request).Authorized
                    ? NetworkInventoryInterceptResult.HandledSuccess
                    : NetworkInventoryInterceptResult.HandledFailure;
            }

            if (!clientController.IsLocalClient)
                return NetworkInventoryInterceptResult.HandledFailure;
            if (!clientController.HasClientTransportRoute(
                    OnSendMerchantRequest, "Merchant transaction"))
                return NetworkInventoryInterceptResult.HandledFailure;
            SendMerchantRequest(request);
            return NetworkInventoryInterceptResult.HandledSuccess;
        }

        internal async Task<NetworkInventoryInterceptResult> RoutePatchedMerchantAsync(
            Merchant merchant, Bag clientBag, RuntimeItem runtimeItem, MerchantAction action)
        {
            if (!NetworkInventoryController.TryResolveForBag(clientBag, out var clientController) ||
                !NetworkInventoryController.TryResolveForBag(merchant?.Bag, out var merchantController))
            {
                return NetworkInventoryInterceptResult.Unhandled;
            }

            if (clientController.IsApplyingNetworkMutation || merchantController.IsApplyingNetworkMutation)
                return NetworkInventoryInterceptResult.Unhandled;

            NetworkMerchantResponse response = await RequestMerchantAsync(
                merchant, clientBag, runtimeItem, action);
            return response.Authorized
                ? NetworkInventoryInterceptResult.HandledSuccess
                : NetworkInventoryInterceptResult.HandledFailure;
        }

        internal NetworkInventoryCraftInterceptResult RoutePatchedCraft(
            Item item, Bag input, Bag output, float chance)
        {
            return RoutePatchedCrafting(item, null, input, output, chance, CraftingAction.Craft);
        }

        internal NetworkInventoryCraftInterceptResult RoutePatchedDismantle(
            Item item, RuntimeItem runtimeItem, Bag input, Bag output, float chance)
        {
            return RoutePatchedCrafting(item, runtimeItem, input, output, chance, CraftingAction.Dismantle);
        }

        internal Task<NetworkInventoryCraftInterceptResult> RoutePatchedCraftAsync(
            Item item, Bag input, Bag output)
        {
            return RoutePatchedCraftingAsync(
                item, null, input, output, 1f, CraftingAction.Craft);
        }

        internal Task<NetworkInventoryCraftInterceptResult> RoutePatchedDismantleAsync(
            Item item, RuntimeItem runtimeItem, Bag input, Bag output, float chance)
        {
            return RoutePatchedCraftingAsync(
                item, runtimeItem, input, output, chance, CraftingAction.Dismantle);
        }

        private async Task<NetworkInventoryCraftInterceptResult> RoutePatchedCraftingAsync(
            Item item,
            RuntimeItem runtimeItem,
            Bag input,
            Bag output,
            float chance,
            CraftingAction action)
        {
            if (!NetworkInventoryController.TryResolveForBag(input, out var inputController) ||
                !NetworkInventoryController.TryResolveForBag(output, out var outputController))
            {
                return new NetworkInventoryCraftInterceptResult(
                    NetworkInventoryInterceptResult.Unhandled);
            }

            if (inputController.IsApplyingNetworkMutation || outputController.IsApplyingNetworkMutation)
            {
                return new NetworkInventoryCraftInterceptResult(
                    NetworkInventoryInterceptResult.Unhandled);
            }

            NetworkCraftingResponse response = action == CraftingAction.Craft
                ? await RequestCraftAsync(item, input, output)
                : runtimeItem != null
                    ? await RequestDismantleAsync(runtimeItem, input, output, chance)
                    : await RequestDismantleAsync(item, input, output, chance);
            if (!response.Authorized)
            {
                return new NetworkInventoryCraftInterceptResult(
                    NetworkInventoryInterceptResult.HandledFailure);
            }

            RuntimeItem craftedItem = response.CreatedItem.RuntimeIdHash != 0
                ? outputController.FindRuntimeItem(response.CreatedItem.RuntimeIdHash)
                : null;
            RuntimeItem[] dismantledItems = null;
            if (response.ReturnedItems != null)
            {
                dismantledItems = new RuntimeItem[response.ReturnedItems.Length];
                for (int i = 0; i < response.ReturnedItems.Length; i++)
                {
                    dismantledItems[i] = outputController.FindRuntimeItem(
                        response.ReturnedItems[i].RuntimeIdHash);
                }
            }

            return new NetworkInventoryCraftInterceptResult(
                NetworkInventoryInterceptResult.HandledSuccess,
                craftedItem,
                dismantledItems);
        }

        private NetworkInventoryCraftInterceptResult RoutePatchedCrafting(
            Item item, RuntimeItem runtimeItem, Bag input, Bag output, float chance, CraftingAction action)
        {
            if (!NetworkInventoryController.TryResolveForBag(input, out var inputController) ||
                !NetworkInventoryController.TryResolveForBag(output, out var outputController))
            {
                return new NetworkInventoryCraftInterceptResult(
                    NetworkInventoryInterceptResult.Unhandled);
            }

            if (inputController.IsApplyingNetworkMutation || outputController.IsApplyingNetworkMutation)
                return new NetworkInventoryCraftInterceptResult(
                    NetworkInventoryInterceptResult.Unhandled);

            ushort requestId = NextSemanticRequestId();
            var request = new NetworkCraftingRequest
            {
                RequestId = requestId,
                ActorNetworkId = inputController.NetworkId,
                CorrelationId = NetworkCorrelation.Compose(inputController.NetworkId, requestId),
                InputBagNetworkId = inputController.NetworkId,
                OutputBagNetworkId = outputController.NetworkId,
                ItemHash = item?.ID.Hash ?? 0,
                ItemIdString = item?.ID.String,
                RuntimeIdHash = runtimeItem?.RuntimeID.Hash ?? 0,
                Action = action,
                Chance = chance
            };

            if (m_IsServer)
            {
                NetworkCraftingResponse response = ProcessCraftingLocally(request);
                if (!response.Authorized)
                {
                    return new NetworkInventoryCraftInterceptResult(
                        NetworkInventoryInterceptResult.HandledFailure);
                }

                RuntimeItem craftedItem = response.CreatedItem.RuntimeIdHash != 0
                    ? outputController.FindRuntimeItem(response.CreatedItem.RuntimeIdHash)
                    : null;
                RuntimeItem[] dismantledItems = null;
                if (response.ReturnedItems != null)
                {
                    dismantledItems = new RuntimeItem[response.ReturnedItems.Length];
                    for (int i = 0; i < response.ReturnedItems.Length; i++)
                    {
                        dismantledItems[i] = outputController.FindRuntimeItem(
                            response.ReturnedItems[i].RuntimeIdHash);
                    }
                }

                return new NetworkInventoryCraftInterceptResult(
                    NetworkInventoryInterceptResult.HandledSuccess,
                    craftedItem,
                    dismantledItems);
            }

            if (!inputController.IsLocalClient)
                return new NetworkInventoryCraftInterceptResult(
                    NetworkInventoryInterceptResult.HandledFailure);
            if (!inputController.HasClientTransportRoute(
                    OnSendCraftingRequest, "Crafting transaction"))
                return new NetworkInventoryCraftInterceptResult(
                    NetworkInventoryInterceptResult.HandledFailure);
            SendCraftingRequest(request);
            return new NetworkInventoryCraftInterceptResult(
                NetworkInventoryInterceptResult.HandledSuccess);
        }

        internal NetworkMerchantResponse ProcessMerchantLocally(NetworkMerchantRequest request)
        {
            NetworkMerchantResponse response = RejectMerchant(request, InventoryRejectionReason.MerchantNotFound);
            NetworkInventoryController clientController = GetController(request.ClientBagNetworkId);
            NetworkInventoryController merchantController = GetController(request.MerchantNetworkId);
            Merchant merchant = FindMerchant(request.MerchantNetworkId);
            if (clientController == null || merchantController == null || merchant == null) return response;

            NetworkInventoryController itemOwner = request.Action == MerchantAction.BuyFromMerchant
                ? merchantController
                : clientController;
            RuntimeItem runtimeItem = itemOwner.FindRuntimeItem(request.RuntimeIdHash);
            if (runtimeItem == null)
            {
                response.RejectionReason = InventoryRejectionReason.RuntimeItemNotFound;
                return response;
            }

            int totalPrice = request.Action == MerchantAction.BuyFromMerchant
                ? merchant.GetBuyPrice(runtimeItem, clientController.Bag)
                : merchant.GetSellPrice(runtimeItem, clientController.Bag);

            bool success;
            using (clientController.EnterNetworkMutationScope())
            using (merchantController.EnterNetworkMutationScope())
            {
                success = request.Action == MerchantAction.BuyFromMerchant
                    ? merchant.SellToClient(clientController.Bag, runtimeItem)
                    : merchant.BuyFromClient(clientController.Bag, runtimeItem);
            }

            if (!success)
            {
                response.RejectionReason = request.Action == MerchantAction.BuyFromMerchant
                    ? InventoryRejectionReason.CannotBuy
                    : InventoryRejectionReason.CannotSell;
                return response;
            }

            clientController.BroadcastAuthoritativeSnapshot();
            merchantController.BroadcastAuthoritativeSnapshot();
            response.Authorized = true;
            response.RejectionReason = InventoryRejectionReason.None;
            response.TotalPrice = totalPrice;
            response.StateVersion = clientController.CurrentAuthoritativeStateVersion;
            return response;
        }

        internal NetworkCraftingResponse ProcessCraftingLocally(NetworkCraftingRequest request)
        {
            NetworkCraftingResponse response = RejectCrafting(request, InventoryRejectionReason.BagNotFound);
            NetworkInventoryController input = GetController(request.InputBagNetworkId);
            NetworkInventoryController output = GetController(request.OutputBagNetworkId);
            if (input == null || output == null) return response;
            if (!TryResolveItem(request.ItemHash, request.ItemIdString, out Item item))
            {
                response.RejectionReason = InventoryRejectionReason.ItemNotFound;
                return response;
            }

            RuntimeItem created = null;
            RuntimeItem[] returned = null;
            IDisposable outputScope = null;
            using (input.EnterNetworkMutationScope())
            {
                if (!ReferenceEquals(input, output)) outputScope = output.EnterNetworkMutationScope();
                try
                {
                    if (request.Action == CraftingAction.Craft)
                    {
                        created = Crafting.Craft(item, input.Bag, output.Bag);
                    }
                    else
                    {
                        RuntimeItem runtime = request.RuntimeIdHash != 0
                            ? input.FindRuntimeItem(request.RuntimeIdHash)
                            : null;
                        returned = runtime != null
                            ? Crafting.Dismantle(runtime, input.Bag, output.Bag, request.Chance)
                            : Crafting.Dismantle(item, input.Bag, output.Bag, request.Chance);
                    }
                }
                finally
                {
                    outputScope?.Dispose();
                }
            }

            bool success = request.Action == CraftingAction.Craft ? created != null : returned != null;
            if (!success)
            {
                response.RejectionReason = request.Action == CraftingAction.Craft
                    ? InventoryRejectionReason.CannotCraft
                    : InventoryRejectionReason.CannotDismantle;
                return response;
            }

            input.BroadcastAuthoritativeSnapshot();
            if (!ReferenceEquals(input, output)) output.BroadcastAuthoritativeSnapshot();
            response.Authorized = true;
            response.RejectionReason = InventoryRejectionReason.None;
            // Responses target the actor/input controller. Reliable-ordered output snapshots are
            // emitted before this response; this revision therefore tracks the controller the
            // requester can unambiguously await even when input and output are different bags.
            response.StateVersion = input.CurrentAuthoritativeStateVersion;
            response.CreatedItem = output.ToNetworkRuntimeItem(created);
            if (returned != null)
            {
                response.ReturnedItems = new NetworkRuntimeItem[returned.Length];
                for (int i = 0; i < returned.Length; i++)
                    response.ReturnedItems[i] = output.ToNetworkRuntimeItem(returned[i]);
            }
            return response;
        }

        internal NetworkCombineResponse ProcessCombineLocally(NetworkCombineRequest request)
        {
            NetworkCombineResponse response = new NetworkCombineResponse
            {
                RequestId = request.RequestId,
                ActorNetworkId = request.ActorNetworkId,
                CorrelationId = request.CorrelationId,
                Authorized = false,
                RejectionReason = InventoryRejectionReason.BagNotFound,
                ResultPosition = TBagContent.INVALID
            };
            NetworkInventoryController controller = GetController(request.BagNetworkId);
            if (controller == null) return response;

            bool success;
            using (controller.EnterNetworkMutationScope())
            {
                success = controller.Bag.Content.Combine(request.PositionA, request.PositionB);
                if (!success)
                {
                    success = controller.Bag.Content.Move(request.PositionA, request.PositionB, true);
                }
            }

            if (!success)
            {
                response.RejectionReason = InventoryRejectionReason.InvalidOperation;
                return response;
            }

            controller.BroadcastAuthoritativeSnapshot();
            RuntimeItem result = controller.Bag.Content.GetContent(request.PositionB)?.RootRuntimeItem;
            response.Authorized = true;
            response.RejectionReason = InventoryRejectionReason.None;
            response.ResultPosition = request.PositionB;
            response.ResultItem = controller.ToNetworkRuntimeItem(result);
            response.StateVersion = controller.CurrentAuthoritativeStateVersion;
            return response;
        }

        private NetworkMerchantResponse RejectMerchant(
            NetworkMerchantRequest request, InventoryRejectionReason reason)
        {
            return new NetworkMerchantResponse
            {
                RequestId = request.RequestId,
                ActorNetworkId = request.ActorNetworkId,
                CorrelationId = request.CorrelationId,
                Authorized = false,
                RejectionReason = reason
            };
        }

        private NetworkCraftingResponse RejectCrafting(
            NetworkCraftingRequest request, InventoryRejectionReason reason)
        {
            return new NetworkCraftingResponse
            {
                RequestId = request.RequestId,
                ActorNetworkId = request.ActorNetworkId,
                CorrelationId = request.CorrelationId,
                Authorized = false,
                RejectionReason = reason,
                ReturnedItems = Array.Empty<NetworkRuntimeItem>()
            };
        }

        private Merchant FindMerchant(uint merchantBagId)
        {
            Merchant[] merchants = FindObjectsByType<Merchant>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            for (int i = 0; i < merchants.Length; i++)
            {
                Merchant merchant = merchants[i];
                if (!NetworkInventoryController.TryResolveForBag(merchant?.Bag, out var controller)) continue;
                if (controller.NetworkId == merchantBagId) return merchant;
            }
            return null;
        }

        private static bool TryResolveItem(int itemHash, string itemIdString, out Item item)
        {
            item = null;
            if (itemHash == 0 || string.IsNullOrWhiteSpace(itemIdString)) return false;
            var id = new GameCreator.Runtime.Common.IdString(itemIdString);
            if (id.Hash != itemHash) return false;
            foreach (Item candidate in InventoryRepository.Get.Items.List)
            {
                if (candidate != null && candidate.ID.Hash == itemHash && candidate.ID.String == itemIdString)
                {
                    item = candidate;
                    return true;
                }
            }
            return false;
        }

        private ushort NextSemanticRequestId()
        {
            if (m_NextSemanticRequestId == 0) m_NextSemanticRequestId = 1;
            ushort value = m_NextSemanticRequestId++;
            if (m_NextSemanticRequestId == 0) m_NextSemanticRequestId = 1;
            return value;
        }
    }
}
#endif
