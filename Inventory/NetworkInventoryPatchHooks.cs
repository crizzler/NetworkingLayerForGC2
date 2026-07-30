#if GC2_INVENTORY
using System;
using System.Reflection;
using System.Threading.Tasks;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.Inventory;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Inventory
{
    /// <summary>
    /// Installs the Inventory 3.0 semantic interception ABI on every peer. The exact Bag resolves
    /// its controller and role; unmanaged/offline bags retain stock GC2 behavior.
    /// </summary>
    public class NetworkInventoryPatchHooks : NetworkSingleton<NetworkInventoryPatchHooks>
    {
        private const int RequiredPatchRevision = 300;
        private bool m_Installed;
        private static float s_LastProxyWarningTime;

        public bool IsPatchActive => m_Installed && IsInventoryPatched();

        public void Initialize(bool isServer)
        {
            // Client hooks are required: they suppress native mutation and submit the semantic
            // request. The resolved controller role, not the process role, controls each call.
            InstallHooks();
        }

        public void Shutdown()
        {
            UninstallHooks();
        }

        protected override void OnSingletonCleanup()
        {
            UninstallHooks();
        }

        public static bool IsInventoryPatched()
        {
            FieldInfo revision = typeof(TBagContent).GetField(
                "NetworkPatchRevision", BindingFlags.Public | BindingFlags.Static);
            if (revision == null || !revision.IsLiteral || (int)revision.GetRawConstantValue() < RequiredPatchRevision)
                return false;

            return
                HasPublicStaticField(typeof(TBagContent), "NetworkAddTypeInterceptor",
                    typeof(Func<TBagContent, Item, Vector2Int, bool, NetworkInventoryInterceptResult>)) &&
                HasPublicStaticField(typeof(TBagContent), "NetworkRemoveTypeInterceptor",
                    typeof(Func<TBagContent, Item, NetworkInventoryInterceptResult>)) &&
                HasPublicStaticField(typeof(TBagContent), "NetworkMoveInterceptor",
                    typeof(Func<TBagContent, Vector2Int, Vector2Int, bool, NetworkInventoryInterceptResult>)) &&
                HasPublicStaticField(typeof(TBagContent), "NetworkInstructionAddItemInterceptor",
                    typeof(Func<Bag, Item, GameObject, Task<NetworkInventoryInterceptResult>>)) &&
                HasPublicStaticField(typeof(TBagContent), "NetworkCellDropInterceptor",
                    typeof(Func<TBagContent, Vector2Int, Vector2Int, bool, NetworkInventoryInterceptResult>)) &&
                HasPublicStaticField(typeof(TBagContent), "NetworkSplitInterceptor",
                    typeof(Func<TBagContent, Vector2Int, int, NetworkInventoryInterceptResult>)) &&
                HasPublicStaticField(typeof(TBagContent), "NetworkTransferInterceptor",
                    typeof(Func<TBagContent, TBagContent, RuntimeItem, int, NetworkInventoryInterceptResult>)) &&
                HasPublicStaticField(typeof(BagWealth), "NetworkAddValidator",
                    typeof(Func<BagWealth, IdString, int, bool>)) &&
                HasPublicStaticField(typeof(BagWealth), "NetworkSetValidator",
                    typeof(Func<BagWealth, IdString, int, bool>)) &&
                HasPublicStaticField(typeof(BagEquipment), "NetworkAttachInterceptor",
                    typeof(Func<Bag, RuntimeItem, RuntimeItem, IdString, NetworkInventoryInterceptResult>)) &&
                HasPublicStaticField(typeof(BagEquipment), "NetworkDetachInterceptor",
                    typeof(Func<Bag, RuntimeItem, IdString, NetworkInventoryInterceptResult>)) &&
                HasPublicStaticField(typeof(Crafting), "NetworkCraftInterceptor",
                    typeof(Func<Item, Bag, Bag, NetworkInventoryCraftInterceptResult>)) &&
                HasPublicStaticField(typeof(Crafting), "NetworkDismantleItemInterceptor",
                    typeof(Func<Item, Bag, Bag, float, NetworkInventoryCraftInterceptResult>)) &&
                HasPublicStaticField(typeof(Crafting), "NetworkDismantleRuntimeInterceptor",
                    typeof(Func<RuntimeItem, Bag, Bag, float, NetworkInventoryCraftInterceptResult>)) &&
                HasPublicStaticField(typeof(Crafting), "NetworkCraftAsyncInterceptor",
                    typeof(Func<Item, Bag, Bag, Task<NetworkInventoryCraftInterceptResult>>)) &&
                HasPublicStaticField(typeof(Crafting), "NetworkDismantleItemAsyncInterceptor",
                    typeof(Func<Item, Bag, Bag, float, Task<NetworkInventoryCraftInterceptResult>>)) &&
                HasPublicStaticField(typeof(Crafting), "NetworkDismantleRuntimeAsyncInterceptor",
                    typeof(Func<RuntimeItem, Bag, Bag, float, Task<NetworkInventoryCraftInterceptResult>>)) &&
                HasPublicStaticField(typeof(Merchant), "NetworkBuyFromClientAsyncInterceptor",
                    typeof(Func<Merchant, Bag, RuntimeItem, Task<NetworkInventoryInterceptResult>>)) &&
                HasPublicStaticField(typeof(Merchant), "NetworkSellToClientAsyncInterceptor",
                    typeof(Func<Merchant, Bag, RuntimeItem, Task<NetworkInventoryInterceptResult>>));
        }

        private void InstallHooks()
        {
            if (m_Installed) return;
            if (!IsInventoryPatched())
            {
                Debug.LogWarning(
                    "[NetworkInventoryPatchHooks] Inventory patch 3.0.0-inventory is required. " +
                    "Network-managed bags fail closed until the patch is applied.");
                return;
            }

            TBagContent.NetworkAddTypeInterceptor = InterceptAddType;
            TBagContent.NetworkRemoveTypeInterceptor = InterceptRemoveType;
            TBagContent.NetworkMoveInterceptor = InterceptMove;
            TBagContent.NetworkInstructionAddItemInterceptor = InterceptInstructionAddAsync;
            TBagContent.NetworkCellDropInterceptor = InterceptCellDrop;
            TBagContent.NetworkSplitInterceptor = InterceptSplit;
            TBagContent.NetworkTransferInterceptor = InterceptTransfer;

            // Composite server operations enter a mutation scope before reaching these primitive
            // guards. An owning client cannot inject an arbitrary RuntimeItem payload.
            TBagContent.NetworkAddValidator = ValidatePrimitiveAdd;
            TBagContent.NetworkRemoveValidator = ValidatePrimitiveRemove;
            TBagContent.NetworkMoveValidator = null;
            TBagContent.NetworkUseValidator = ValidateUse;
            TBagContent.NetworkDropValidator = ValidateDrop;
            BagWealth.NetworkAddValidator = ValidateWealthAdd;
            BagWealth.NetworkSetValidator = ValidateWealthSet;
            BagEquipment.NetworkAttachInterceptor = InterceptSocketAttach;
            BagEquipment.NetworkDetachInterceptor = InterceptSocketDetach;

            Crafting.NetworkCraftInterceptor = InterceptCraft;
            Crafting.NetworkDismantleItemInterceptor = InterceptDismantleItem;
            Crafting.NetworkDismantleRuntimeInterceptor = InterceptDismantleRuntime;
            Crafting.NetworkCraftAsyncInterceptor = InterceptCraftAsync;
            Crafting.NetworkDismantleItemAsyncInterceptor = InterceptDismantleItemAsync;
            Crafting.NetworkDismantleRuntimeAsyncInterceptor = InterceptDismantleRuntimeAsync;
            Merchant.NetworkBuyFromClientInterceptor = InterceptMerchantBuyFromClient;
            Merchant.NetworkSellToClientInterceptor = InterceptMerchantSellToClient;
            Merchant.NetworkBuyFromClientAsyncInterceptor = InterceptMerchantBuyFromClientAsync;
            Merchant.NetworkSellToClientAsyncInterceptor = InterceptMerchantSellToClientAsync;

            m_Installed = true;
        }

        private void UninstallHooks()
        {
            if (!m_Installed) return;

            TBagContent.NetworkAddTypeInterceptor = null;
            TBagContent.NetworkRemoveTypeInterceptor = null;
            TBagContent.NetworkMoveInterceptor = null;
            TBagContent.NetworkInstructionAddItemInterceptor = null;
            TBagContent.NetworkCellDropInterceptor = null;
            TBagContent.NetworkSplitInterceptor = null;
            TBagContent.NetworkTransferInterceptor = null;
            TBagContent.NetworkAddValidator = null;
            TBagContent.NetworkRemoveValidator = null;
            TBagContent.NetworkUseValidator = null;
            TBagContent.NetworkDropValidator = null;
            BagWealth.NetworkAddValidator = null;
            BagWealth.NetworkSetValidator = null;
            BagEquipment.NetworkAttachInterceptor = null;
            BagEquipment.NetworkDetachInterceptor = null;
            Crafting.NetworkCraftInterceptor = null;
            Crafting.NetworkDismantleItemInterceptor = null;
            Crafting.NetworkDismantleRuntimeInterceptor = null;
            Crafting.NetworkCraftAsyncInterceptor = null;
            Crafting.NetworkDismantleItemAsyncInterceptor = null;
            Crafting.NetworkDismantleRuntimeAsyncInterceptor = null;
            Merchant.NetworkBuyFromClientInterceptor = null;
            Merchant.NetworkSellToClientInterceptor = null;
            Merchant.NetworkBuyFromClientAsyncInterceptor = null;
            Merchant.NetworkSellToClientAsyncInterceptor = null;
            m_Installed = false;
        }

        private static NetworkInventoryInterceptResult InterceptAddType(
            TBagContent content, Item item, Vector2Int position, bool allowStack)
        {
            return NetworkInventoryController.TryResolveForContent(content, out var controller)
                ? controller.RoutePatchedAddType(item, position, allowStack)
                : NetworkInventoryInterceptResult.Unhandled;
        }

        private static NetworkInventoryInterceptResult InterceptRemoveType(TBagContent content, Item item)
        {
            return NetworkInventoryController.TryResolveForContent(content, out var controller)
                ? controller.RoutePatchedRemoveType(item)
                : NetworkInventoryInterceptResult.Unhandled;
        }

        private static NetworkInventoryInterceptResult InterceptMove(
            TBagContent content, Vector2Int from, Vector2Int to, bool allowStack)
        {
            return NetworkInventoryController.TryResolveForContent(content, out var controller)
                ? controller.RoutePatchedMove(from, to, allowStack)
                : NetworkInventoryInterceptResult.Unhandled;
        }

        private static async Task<NetworkInventoryInterceptResult> InterceptInstructionAddAsync(
            Bag bag, Item item, GameObject source)
        {
            if (!NetworkInventoryController.TryResolveForBag(bag, out var controller))
                return NetworkInventoryInterceptResult.Unhandled;

            NetworkInventoryPickupSource pickup = source != null
                ? source.GetComponentInParent<NetworkInventoryPickupSource>()
                : null;
            return pickup != null
                ? await pickup.RequestPickupAsync(controller)
                : await controller.RoutePatchedInstructionAddAsync(item);
        }

        private static NetworkInventoryInterceptResult InterceptCellDrop(
            TBagContent content, Vector2Int from, Vector2Int to, bool tryCombine)
        {
            if (!NetworkInventoryController.TryResolveForContent(content, out var controller))
                return NetworkInventoryInterceptResult.Unhandled;

            return tryCombine
                ? controller.RoutePatchedCombine(from, to)
                : controller.RoutePatchedMove(from, to, true);
        }

        private static NetworkInventoryInterceptResult InterceptSplit(
            TBagContent content, Vector2Int sourcePosition, int amount)
        {
            return NetworkInventoryController.TryResolveForContent(content, out var controller)
                ? controller.RoutePatchedSplit(sourcePosition, amount)
                : NetworkInventoryInterceptResult.Unhandled;
        }

        private static NetworkInventoryInterceptResult InterceptTransfer(
            TBagContent source,
            TBagContent destination,
            RuntimeItem runtimeItem,
            int amount)
        {
            if (!NetworkInventoryController.TryResolveForContent(source, out var sourceController) ||
                !NetworkInventoryController.TryResolveForContent(destination, out var destinationController))
            {
                return NetworkInventoryInterceptResult.Unhandled;
            }

            return sourceController.RoutePatchedTransfer(destinationController, runtimeItem, amount);
        }

        private static bool ValidateUse(TBagContent content, RuntimeItem runtimeItem)
        {
            return NetworkInventoryController.TryResolveForContent(content, out var controller)
                ? controller.RoutePatchedUse(runtimeItem)
                : true;
        }

        private static bool ValidatePrimitiveAdd(
            TBagContent content, RuntimeItem runtimeItem, Vector2Int position, bool allowStack)
        {
            return NetworkInventoryController.TryResolveForContent(content, out var controller)
                ? controller.RoutePatchedPrimitiveAdd(runtimeItem)
                : true;
        }

        private static bool ValidatePrimitiveRemove(TBagContent content, RuntimeItem runtimeItem)
        {
            return NetworkInventoryController.TryResolveForContent(content, out var controller)
                ? controller.RoutePatchedPrimitiveRemove(runtimeItem)
                : true;
        }

        private static bool ValidateDrop(TBagContent content, RuntimeItem runtimeItem, Vector3 point)
        {
            return NetworkInventoryController.TryResolveForContent(content, out var controller)
                ? controller.RoutePatchedDrop(runtimeItem, point)
                : true;
        }

        private static bool ValidateWealthAdd(BagWealth wealth, IdString currencyId, int value)
        {
            return NetworkInventoryController.TryResolveForWealth(wealth, out var controller)
                ? controller.RoutePatchedWealth(currencyId, value, false)
                : true;
        }

        private static bool ValidateWealthSet(BagWealth wealth, IdString currencyId, int value)
        {
            return NetworkInventoryController.TryResolveForWealth(wealth, out var controller)
                ? controller.RoutePatchedWealth(currencyId, value, true)
                : true;
        }

        private static NetworkInventoryInterceptResult InterceptSocketAttach(
            Bag bag,
            RuntimeItem parent,
            RuntimeItem attachment,
            IdString socketId)
        {
            return NetworkInventoryController.TryResolveForBag(bag, out var controller)
                ? controller.RoutePatchedSocketAttach(parent, attachment, socketId)
                : NetworkInventoryInterceptResult.Unhandled;
        }

        private static NetworkInventoryInterceptResult InterceptSocketDetach(
            Bag bag,
            RuntimeItem parent,
            IdString socketId)
        {
            return NetworkInventoryController.TryResolveForBag(bag, out var controller)
                ? controller.RoutePatchedSocketDetach(parent, socketId)
                : NetworkInventoryInterceptResult.Unhandled;
        }

        private static NetworkInventoryCraftInterceptResult InterceptCraft(
            Item item, Bag input, Bag output)
        {
            return NetworkInventoryManager.Instance?.RoutePatchedCraft(item, input, output, 1f) ??
                   new NetworkInventoryCraftInterceptResult(
                       NetworkInventoryInterceptResult.Unhandled);
        }

        private static NetworkInventoryCraftInterceptResult InterceptDismantleItem(
            Item item, Bag input, Bag output, float chance)
        {
            return NetworkInventoryManager.Instance?.RoutePatchedDismantle(item, null, input, output, chance) ??
                   new NetworkInventoryCraftInterceptResult(
                       NetworkInventoryInterceptResult.Unhandled);
        }

        private static NetworkInventoryCraftInterceptResult InterceptDismantleRuntime(
            RuntimeItem item, Bag input, Bag output, float chance)
        {
            return NetworkInventoryManager.Instance?.RoutePatchedDismantle(item?.Item, item, input, output, chance) ??
                   new NetworkInventoryCraftInterceptResult(
                       NetworkInventoryInterceptResult.Unhandled);
        }

        private static Task<NetworkInventoryCraftInterceptResult> InterceptCraftAsync(
            Item item, Bag input, Bag output)
        {
            return NetworkInventoryManager.Instance?.RoutePatchedCraftAsync(item, input, output) ??
                   Task.FromResult(new NetworkInventoryCraftInterceptResult(
                       NetworkInventoryInterceptResult.Unhandled));
        }

        private static Task<NetworkInventoryCraftInterceptResult> InterceptDismantleItemAsync(
            Item item, Bag input, Bag output, float chance)
        {
            return NetworkInventoryManager.Instance?.RoutePatchedDismantleAsync(
                       item, null, input, output, chance) ??
                   Task.FromResult(new NetworkInventoryCraftInterceptResult(
                       NetworkInventoryInterceptResult.Unhandled));
        }

        private static Task<NetworkInventoryCraftInterceptResult> InterceptDismantleRuntimeAsync(
            RuntimeItem item, Bag input, Bag output, float chance)
        {
            return NetworkInventoryManager.Instance?.RoutePatchedDismantleAsync(
                       item?.Item, item, input, output, chance) ??
                   Task.FromResult(new NetworkInventoryCraftInterceptResult(
                       NetworkInventoryInterceptResult.Unhandled));
        }

        private static NetworkInventoryInterceptResult InterceptMerchantBuyFromClient(
            Merchant merchant, Bag clientBag, RuntimeItem runtimeItem)
        {
            return NetworkInventoryManager.Instance?.RoutePatchedMerchant(
                merchant, clientBag, runtimeItem, MerchantAction.SellToMerchant) ??
                   NetworkInventoryInterceptResult.Unhandled;
        }

        private static NetworkInventoryInterceptResult InterceptMerchantSellToClient(
            Merchant merchant, Bag clientBag, RuntimeItem runtimeItem)
        {
            return NetworkInventoryManager.Instance?.RoutePatchedMerchant(
                merchant, clientBag, runtimeItem, MerchantAction.BuyFromMerchant) ??
                   NetworkInventoryInterceptResult.Unhandled;
        }

        private static Task<NetworkInventoryInterceptResult> InterceptMerchantBuyFromClientAsync(
            Merchant merchant, Bag clientBag, RuntimeItem runtimeItem)
        {
            return NetworkInventoryManager.Instance?.RoutePatchedMerchantAsync(
                       merchant, clientBag, runtimeItem, MerchantAction.SellToMerchant) ??
                   Task.FromResult(NetworkInventoryInterceptResult.Unhandled);
        }

        private static Task<NetworkInventoryInterceptResult> InterceptMerchantSellToClientAsync(
            Merchant merchant, Bag clientBag, RuntimeItem runtimeItem)
        {
            return NetworkInventoryManager.Instance?.RoutePatchedMerchantAsync(
                       merchant, clientBag, runtimeItem, MerchantAction.BuyFromMerchant) ??
                   Task.FromResult(NetworkInventoryInterceptResult.Unhandled);
        }

        internal static void WarnProxyMutation(NetworkInventoryController controller, string operation)
        {
            if (Time.unscaledTime - s_LastProxyWarningTime < 2f) return;
            s_LastProxyWarningTime = Time.unscaledTime;
            Debug.LogWarning(
                $"[NetworkInventoryPatchHooks] Suppressed {operation} on remote proxy bag " +
                $"{controller?.NetworkId}. Only the owner or server may mutate a network bag.");
        }

        private static bool HasPublicStaticField(Type type, string fieldName, Type expectedFieldType)
        {
            FieldInfo field = type.GetField(fieldName, BindingFlags.Public | BindingFlags.Static);
            return field != null && expectedFieldType.IsAssignableFrom(field.FieldType);
        }
    }
}
#endif
