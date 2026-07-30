using System.Collections.Generic;
using UnityEngine;

namespace Arawn.EnemyMasses.Editor.Integration.GameCreator2.Patches
{
    /// <summary>
    /// Adds transport-neutral interception points to GC2 Inventory without taking a dependency
    /// on the Networking Layer runtime assembly.
    /// </summary>
    public class InventoryPatcher : GC2PatcherBase
    {
        public override string ModuleName => "Inventory";
        public override string PatchVersion => "3.0.0-inventory";
        public override string DisplayName => "Inventory (Game Creator 2)";

        public override string PatchDescription =>
            "Adds revisioned, server-authoritative Inventory interception hooks.\n\n" +
            "Includes Grid/List moves and stacking, Add/Remove Item, async visual-scripting " +
            "grants, UI split/transfer/move-combine, use/drop, wealth, crafting, and merchant hooks.";

        protected override string[] FilesToPatch => new[]
        {
            "Plugins/GameCreator/Packages/Inventory/Runtime/Classes/Bag/Content/TBagContent.cs",
            "Plugins/GameCreator/Packages/Inventory/Runtime/Classes/Bag/Content/BagContentGrid.cs",
            "Plugins/GameCreator/Packages/Inventory/Runtime/Classes/Bag/Content/BagContentList.cs",
            "Plugins/GameCreator/Packages/Inventory/Runtime/Classes/Bag/Wealth/BagWealth.cs",
            "Plugins/GameCreator/Packages/Inventory/Runtime/Classes/Bag/Equipment/BagEquipment.cs",
            "Plugins/GameCreator/Packages/Inventory/Runtime/VisualScripting/Instructions/InstructionInventoryAddItem.cs",
            "Plugins/GameCreator/Packages/Inventory/Runtime/UI/UnityUI/Components/BagCellUI.cs",
            "Plugins/GameCreator/Packages/Inventory/Runtime/Classes/Items/ScriptableObject/Craft/Crafting.cs",
            "Plugins/GameCreator/Packages/Inventory/Runtime/Components/Merchant.cs",
            "Plugins/GameCreator/Packages/Inventory/Runtime/UI/UnityUI/Components/CraftingItemUI.cs",
            "Plugins/GameCreator/Packages/Inventory/Runtime/UI/UnityUI/Components/DismantlingItemUI.cs",
            "Plugins/GameCreator/Packages/Inventory/Runtime/UI/UnityUI/Classes/CellMerchantUI.cs"
        };

        protected override VersionCompatibilityRequirement[] GetVersionCompatibilityRequirements()
        {
            return new[]
            {
                VersionRequirement("Plugins/GameCreator/Packages/Inventory/Editor/Version.txt", "2.8.*")
            };
        }

        protected override string[] GetRequiredPatchTokens(string relativePath)
        {
            if (relativePath.EndsWith("TBagContent.cs"))
            {
                return new[]
                {
                    "NetworkPatchRevision = 300",
                    "NetworkPatchCapabilities",
                    "NetworkAddTypeInterceptor",
                    "NetworkRemoveTypeInterceptor",
                    "NetworkMoveInterceptor",
                    "NetworkInstructionAddItemInterceptor",
                    "NetworkCellDropInterceptor",
                    "NetworkSplitInterceptor",
                    "NetworkTransferInterceptor",
                    "NetworkUseValidator",
                    "NetworkDropValidator",
                    "UseDirect(",
                    "DropDirect("
                };
            }

            if (relativePath.EndsWith("BagContentGrid.cs") ||
                relativePath.EndsWith("BagContentList.cs"))
            {
                return new[]
                {
                    "InterceptNetworkMove(",
                    "InterceptNetworkAddType(",
                    "InterceptNetworkRemoveType(",
                    "NetworkAddValidator.Invoke",
                    "NetworkRemoveValidator.Invoke"
                };
            }

            if (relativePath.EndsWith("BagWealth.cs"))
            {
                return new[]
                {
                    "NetworkAddValidator",
                    "NetworkSetValidator",
                    "SetDirect(",
                    "AddDirect("
                };
            }

            if (relativePath.EndsWith("BagEquipment.cs"))
            {
                return new[]
                {
                    "NetworkAttachInterceptor",
                    "NetworkDetachInterceptor",
                    "networkResult == NetworkInventoryInterceptResult.HandledSuccess"
                };
            }

            if (relativePath.EndsWith("InstructionInventoryAddItem.cs"))
            {
                return new[]
                {
                    "NetworkInstructionAddItemInterceptor",
                    "RunNetworkOrLocal"
                };
            }

            if (relativePath.EndsWith("BagCellUI.cs"))
            {
                return new[]
                {
                    "NetworkCellDropInterceptor",
                    "NetworkTransferInterceptor",
                    "NetworkSplitInterceptor"
                };
            }

            if (relativePath.EndsWith("Crafting.cs"))
            {
                return new[]
                {
                    "NetworkInventoryCraftInterceptResult",
                    "NetworkCraftInterceptor",
                    "NetworkDismantleItemInterceptor",
                    "NetworkDismantleRuntimeInterceptor",
                    "NetworkCraftAsyncInterceptor",
                    "NetworkDismantleItemAsyncInterceptor",
                    "NetworkDismantleRuntimeAsyncInterceptor",
                    "CraftAsync(",
                    "DismantleAsync(",
                    "    [Serializable]\n    public class Crafting"
                };
            }

            if (relativePath.EndsWith("Merchant.cs"))
            {
                return new[]
                {
                    "NetworkBuyFromClientInterceptor",
                    "NetworkSellToClientInterceptor",
                    "NetworkBuyFromClientAsyncInterceptor",
                    "NetworkSellToClientAsyncInterceptor",
                    "BuyFromClientAsync(",
                    "SellToClientAsync("
                };
            }

            if (relativePath.EndsWith("CraftingItemUI.cs"))
                return new[] { "await Crafting.CraftAsync(" };

            if (relativePath.EndsWith("DismantlingItemUI.cs"))
                return new[] { "await Crafting.DismantleAsync(" };

            if (relativePath.EndsWith("CellMerchantUI.cs"))
            {
                return new[]
                {
                    "await this.m_MerchantUI.Merchant.SellToClientAsync(",
                    "await this.m_MerchantUI.Merchant.BuyFromClientAsync(",
                    "if (success) this.EventTrade?.Invoke();"
                };
            }

            return base.GetRequiredPatchTokens(relativePath);
        }

        protected override Dictionary<string, int> GetRequiredPatchTokenCounts(string relativePath)
        {
            if (relativePath.EndsWith("TBagContent.cs"))
            {
                return new Dictionary<string, int>
                {
                    { "NetworkUseValidator.Invoke", 1 },
                    { "NetworkDropValidator.Invoke", 1 },
                    { "NetworkPatchRevision = 300", 1 }
                };
            }

            if (relativePath.EndsWith("BagContentGrid.cs") ||
                relativePath.EndsWith("BagContentList.cs"))
            {
                return new Dictionary<string, int>
                {
                    { "InterceptNetworkMove(", 1 },
                    { "InterceptNetworkAddType(", 2 },
                    { "InterceptNetworkRemoveType(", 1 },
                    { "NetworkAddValidator.Invoke", 1 },
                    { "NetworkRemoveValidator.Invoke", 1 }
                };
            }

            if (relativePath.EndsWith("BagWealth.cs"))
            {
                return new Dictionary<string, int>
                {
                    { "NetworkSetValidator.Invoke", 1 },
                    { "NetworkAddValidator.Invoke", 1 }
                };
            }

            if (relativePath.EndsWith("BagCellUI.cs"))
            {
                return new Dictionary<string, int>
                {
                    { "NetworkCellDropInterceptor.Invoke", 1 },
                    { "NetworkTransferInterceptor.Invoke", 1 },
                    { "NetworkSplitInterceptor.Invoke", 1 }
                };
            }

            if (relativePath.EndsWith("CellMerchantUI.cs"))
            {
                return new Dictionary<string, int>
                {
                    { "if (success) this.EventTrade?.Invoke();", 2 }
                };
            }

            return base.GetRequiredPatchTokenCounts(relativePath);
        }

        protected override bool PatchFile(string relativePath)
        {
            string content = ReadFile(relativePath);
            ExistingPatchState state = PrepareContentForPatch(relativePath, ref content);
            if (state == ExistingPatchState.SkipAlreadyPatched) return true;
            if (state == ExistingPatchState.Failed) return false;

            bool patched = relativePath.EndsWith("TBagContent.cs")
                ? PatchTBagContent(ref content)
                : relativePath.EndsWith("BagContentGrid.cs")
                    ? PatchBagContent(ref content, "BagContentGrid")
                    : relativePath.EndsWith("BagContentList.cs")
                        ? PatchBagContent(ref content, "BagContentList")
                        : relativePath.EndsWith("BagWealth.cs")
                            ? PatchBagWealth(ref content)
                            : relativePath.EndsWith("BagEquipment.cs")
                                ? PatchBagEquipment(ref content)
                            : relativePath.EndsWith("InstructionInventoryAddItem.cs")
                                ? PatchAddItemInstruction(ref content)
                                : relativePath.EndsWith("BagCellUI.cs")
                                    ? PatchBagCellUI(ref content)
                                    : relativePath.EndsWith("Crafting.cs")
                                        ? PatchCrafting(ref content)
                                        : relativePath.EndsWith("Merchant.cs")
                                            ? PatchMerchant(ref content)
                                            : relativePath.EndsWith("CraftingItemUI.cs")
                                                ? PatchCraftingItemUI(ref content)
                                                : relativePath.EndsWith("DismantlingItemUI.cs")
                                                    ? PatchDismantlingItemUI(ref content)
                                                    : relativePath.EndsWith("CellMerchantUI.cs") &&
                                                      PatchCellMerchantUI(ref content);

            if (!patched) return false;
            if (!EnsurePatchMarkerBeforeNamespace(ref content, ResolveNamespace(relativePath))) return false;

            WriteFile(relativePath, content);
            Debug.Log($"[GC2 Networking] Patched {relativePath}");
            return true;
        }

        private static string ResolveNamespace(string relativePath)
        {
            return relativePath.EndsWith("BagCellUI.cs") ||
                   relativePath.EndsWith("CraftingItemUI.cs") ||
                   relativePath.EndsWith("DismantlingItemUI.cs") ||
                   relativePath.EndsWith("CellMerchantUI.cs")
                ? "GameCreator.Runtime.Inventory.UnityUI"
                : "GameCreator.Runtime.Inventory";
        }

        private bool PatchBagEquipment(ref string content)
        {
            const string classDeclaration = @"    public class BagEquipment : IBagEquipment, ISerializationCallbackReceiver
    {";
            const string patchedDeclaration = @"    public class BagEquipment : IBagEquipment, ISerializationCallbackReceiver
    {
        // [GC2_NETWORK_PATCH] Semantic socket operations are intercepted before native mutation.
        public static Func<Bag, RuntimeItem, RuntimeItem, IdString, NetworkInventoryInterceptResult> NetworkAttachInterceptor;
        public static Func<Bag, RuntimeItem, IdString, NetworkInventoryInterceptResult> NetworkDetachInterceptor;
        // [GC2_NETWORK_PATCH_END]";

            if (!TryReplaceRequired(
                    ref content,
                    classDeclaration,
                    patchedDeclaration,
                    "[GC2 Networking] Could not install Inventory socket interceptors in BagEquipment.cs."))
            {
                return false;
            }

            const string attachAnchor = @"            if (!runtimeItem.Sockets.ContainsKey(socketID)) return false;

            IBagEquipment equipment = this.Bag.Equipment;";
            const string patchedAttach = @"            if (!runtimeItem.Sockets.ContainsKey(socketID)) return false;

            // [GC2_NETWORK_PATCH] Intercept the complete socket operation before mutation.
            if (NetworkAttachInterceptor != null)
            {
                NetworkInventoryInterceptResult networkResult = NetworkAttachInterceptor.Invoke(
                    this.Bag, runtimeItem, attachment, socketID);
                if (networkResult != NetworkInventoryInterceptResult.Unhandled)
                {
                    return networkResult == NetworkInventoryInterceptResult.HandledSuccess;
                }
            }
            // [GC2_NETWORK_PATCH_END]

            IBagEquipment equipment = this.Bag.Equipment;";

            if (!TryReplaceRequired(
                    ref content,
                    attachAnchor,
                    patchedAttach,
                    "[GC2 Networking] Could not patch BagEquipment.AttachTo."))
            {
                return false;
            }

            const string detachAnchor = @"            if (!runtimeItem.Sockets.ContainsKey(socketID)) return null;

            IBagEquipment equipment = this.Bag.Equipment;";
            const string patchedDetach = @"            if (!runtimeItem.Sockets.ContainsKey(socketID)) return null;

            // [GC2_NETWORK_PATCH] Intercept the complete socket operation before mutation.
            if (NetworkDetachInterceptor != null)
            {
                NetworkInventoryInterceptResult networkResult = NetworkDetachInterceptor.Invoke(
                    this.Bag, runtimeItem, socketID);
                if (networkResult != NetworkInventoryInterceptResult.Unhandled) return null;
            }
            // [GC2_NETWORK_PATCH_END]

            IBagEquipment equipment = this.Bag.Equipment;";

            return TryReplaceRequired(
                ref content,
                detachAnchor,
                patchedDetach,
                "[GC2 Networking] Could not patch BagEquipment.DetachFrom.");
        }

        private bool PatchTBagContent(ref string content)
        {
            if (!content.Contains("using System.Threading.Tasks;"))
            {
                content = content.Replace(
                    "using System.Collections.Generic;",
                    "using System.Collections.Generic;\nusing System.Threading.Tasks;");
            }

            const string classDeclaration = @"namespace GameCreator.Runtime.Inventory
{
    [Serializable]
    public abstract class TBagContent : IBagContent
    {";

            const string patchedDeclaration = @"namespace GameCreator.Runtime.Inventory
{
    /// <summary>Result returned by transport-neutral Inventory interception hooks.</summary>
    public enum NetworkInventoryInterceptResult : byte
    {
        Unhandled = 0,
        HandledSuccess = 1,
        HandledFailure = 2
    }

    [System.Flags]
    public enum NetworkInventoryPatchCapability
    {
        None = 0,
        ContentMove = 1 << 0,
        AddType = 1 << 1,
        RemoveType = 1 << 2,
        AsyncInstructionAdd = 1 << 3,
        CellDrop = 1 << 4,
        Split = 1 << 5,
        Transfer = 1 << 6,
        UseDrop = 1 << 7,
        Wealth = 1 << 8,
        Crafting = 1 << 9,
        Merchant = 1 << 10,
        Sockets = 1 << 11
    }

    [Serializable]
    public abstract class TBagContent : IBagContent
    {
        // [GC2_NETWORK_PATCH] Revisioned transport-neutral interception ABI.
        public const int NetworkPatchRevision = 300;
        public const NetworkInventoryPatchCapability NetworkPatchCapabilities =
            NetworkInventoryPatchCapability.ContentMove |
            NetworkInventoryPatchCapability.AddType |
            NetworkInventoryPatchCapability.RemoveType |
            NetworkInventoryPatchCapability.AsyncInstructionAdd |
            NetworkInventoryPatchCapability.CellDrop |
            NetworkInventoryPatchCapability.Split |
            NetworkInventoryPatchCapability.Transfer |
            NetworkInventoryPatchCapability.UseDrop |
            NetworkInventoryPatchCapability.Wealth |
            NetworkInventoryPatchCapability.Crafting |
            NetworkInventoryPatchCapability.Merchant |
            NetworkInventoryPatchCapability.Sockets;

        // Legacy validators remain for one compatibility cycle.
        public static Func<TBagContent, RuntimeItem, Vector2Int, bool, bool> NetworkAddValidator;
        public static Func<TBagContent, RuntimeItem, bool> NetworkRemoveValidator;
        public static Func<TBagContent, Vector2Int, Vector2Int, bool, bool> NetworkMoveValidator;
        public static Func<TBagContent, RuntimeItem, Vector3, bool> NetworkDropValidator;
        public static Func<TBagContent, RuntimeItem, bool> NetworkUseValidator;

        public static Func<TBagContent, Item, Vector2Int, bool, NetworkInventoryInterceptResult> NetworkAddTypeInterceptor;
        public static Func<TBagContent, Item, NetworkInventoryInterceptResult> NetworkRemoveTypeInterceptor;
        public static Func<TBagContent, Vector2Int, Vector2Int, bool, NetworkInventoryInterceptResult> NetworkMoveInterceptor;
        public static Func<Bag, Item, GameObject, Task<NetworkInventoryInterceptResult>> NetworkInstructionAddItemInterceptor;
        public static Func<TBagContent, Vector2Int, Vector2Int, bool, NetworkInventoryInterceptResult> NetworkCellDropInterceptor;
        public static Func<TBagContent, Vector2Int, int, NetworkInventoryInterceptResult> NetworkSplitInterceptor;
        public static Func<TBagContent, TBagContent, RuntimeItem, int, NetworkInventoryInterceptResult> NetworkTransferInterceptor;

        public static bool IsNetworkingActive =>
            NetworkAddValidator != null ||
            NetworkAddTypeInterceptor != null ||
            NetworkMoveInterceptor != null ||
            NetworkInstructionAddItemInterceptor != null;
        // [GC2_NETWORK_PATCH_END]";

            if (!TryReplaceRequired(
                    ref content,
                    classDeclaration,
                    patchedDeclaration,
                    "[GC2 Networking] Could not install the Inventory v3 ABI in TBagContent.cs."))
            {
                return false;
            }

            const string originalUse = @"        public virtual bool Use(RuntimeItem runtimeItem)
        {
            if (!this.Contains(runtimeItem)) return false;
            if (!runtimeItem.CanUse()) return false;

            _ = runtimeItem.Use();
            if (runtimeItem.Item.Usage.ConsumeWhenUse) this.Remove(runtimeItem);

            this.EventUse?.Invoke(runtimeItem);
            return true;
        }";

            const string patchedUse = @"        public virtual bool Use(RuntimeItem runtimeItem)
        {
            if (!this.Contains(runtimeItem)) return false;
            if (!runtimeItem.CanUse()) return false;

            // [GC2_NETWORK_PATCH] Atomic Use interception.
            if (NetworkUseValidator != null && !NetworkUseValidator.Invoke(this, runtimeItem))
            {
                return false;
            }
            // [GC2_NETWORK_PATCH_END]

            _ = runtimeItem.Use();
            if (runtimeItem.Item.Usage.ConsumeWhenUse) this.Remove(runtimeItem);

            this.EventUse?.Invoke(runtimeItem);
            return true;
        }

        // [GC2_NETWORK_PATCH] Authoritative bypass.
        public virtual bool UseDirect(RuntimeItem runtimeItem)
        {
            if (!this.Contains(runtimeItem)) return false;
            if (!runtimeItem.CanUse()) return false;

            _ = runtimeItem.Use();
            if (runtimeItem.Item.Usage.ConsumeWhenUse) this.Remove(runtimeItem);
            this.EventUse?.Invoke(runtimeItem);
            return true;
        }
        // [GC2_NETWORK_PATCH_END]";

            if (!TryReplaceRequired(ref content, originalUse, patchedUse,
                    "[GC2 Networking] Could not patch TBagContent.Use."))
            {
                return false;
            }

            const string originalDrop = @"        public GameObject Drop(RuntimeItem runtimeItem, Vector3 point)
        {
            if (runtimeItem == null) return null;
            if (!this.Contains(runtimeItem)) return null;

            if (this.Bag.Wearer == null) return null;
            if (!runtimeItem.Item.HasPrefab) return null;
            if (!runtimeItem.Item.CanDrop) return null;

            RuntimeItem removeRuntimeItem = this.Remove(runtimeItem);
            return Item.Drop(removeRuntimeItem, point, Quaternion.identity);
        }";

            const string patchedDrop = @"        public GameObject Drop(RuntimeItem runtimeItem, Vector3 point)
        {
            if (runtimeItem == null) return null;
            if (!this.Contains(runtimeItem)) return null;

            if (this.Bag.Wearer == null) return null;
            if (!runtimeItem.Item.HasPrefab) return null;
            if (!runtimeItem.Item.CanDrop) return null;

            // [GC2_NETWORK_PATCH] Atomic Drop interception.
            if (NetworkDropValidator != null && !NetworkDropValidator.Invoke(this, runtimeItem, point))
            {
                return null;
            }
            // [GC2_NETWORK_PATCH_END]

            RuntimeItem removeRuntimeItem = this.Remove(runtimeItem);
            return Item.Drop(removeRuntimeItem, point, Quaternion.identity);
        }

        // [GC2_NETWORK_PATCH] Authoritative bypass.
        public GameObject DropDirect(RuntimeItem runtimeItem, Vector3 point)
        {
            if (runtimeItem == null) return null;
            if (!this.Contains(runtimeItem)) return null;
            if (this.Bag.Wearer == null) return null;
            if (!runtimeItem.Item.HasPrefab) return null;
            if (!runtimeItem.Item.CanDrop) return null;

            RuntimeItem removeRuntimeItem = this.Remove(runtimeItem);
            return Item.Drop(removeRuntimeItem, point, Quaternion.identity);
        }
        // [GC2_NETWORK_PATCH_END]";

            if (!TryReplaceRequired(ref content, originalDrop, patchedDrop,
                    "[GC2 Networking] Could not patch TBagContent.Drop."))
            {
                return false;
            }

            const string protectedAnchor = @"        protected void ExecuteEventRemove(RuntimeItem runtimeItem)
        {
            this.EventRemove?.Invoke(runtimeItem);
        }";

            const string protectedReplacement = protectedAnchor + @"

        // [GC2_NETWORK_PATCH] Synchronous content-operation interception helpers.
        protected NetworkInventoryInterceptResult InterceptNetworkAddType(
            Item item, Vector2Int position, bool allowStack)
        {
            return NetworkAddTypeInterceptor != null
                ? NetworkAddTypeInterceptor.Invoke(this, item, position, allowStack)
                : NetworkInventoryInterceptResult.Unhandled;
        }

        protected NetworkInventoryInterceptResult InterceptNetworkRemoveType(Item item)
        {
            return NetworkRemoveTypeInterceptor != null
                ? NetworkRemoveTypeInterceptor.Invoke(this, item)
                : NetworkInventoryInterceptResult.Unhandled;
        }

        protected NetworkInventoryInterceptResult InterceptNetworkMove(
            Vector2Int positionA, Vector2Int positionB, bool allowStack)
        {
            if (NetworkMoveInterceptor != null)
            {
                return NetworkMoveInterceptor.Invoke(this, positionA, positionB, allowStack);
            }

            if (NetworkMoveValidator != null &&
                !NetworkMoveValidator.Invoke(this, positionA, positionB, allowStack))
            {
                return NetworkInventoryInterceptResult.HandledFailure;
            }

            return NetworkInventoryInterceptResult.Unhandled;
        }
        // [GC2_NETWORK_PATCH_END]";

            return TryReplaceRequired(ref content, protectedAnchor, protectedReplacement,
                "[GC2 Networking] Could not install TBagContent v3 interception helpers.");
        }

        private bool PatchBagContent(ref string content, string className)
        {
            const string moveValidationGrid = @"            if (rootPositionSource == INVALID) return false;
            if (!this.CanMove(positionA, positionB, allowStack)) return false;";
            const string moveValidationList = @"            if (!this.CanMove(positionA, positionB, allowStack)) return false;";

            string moveValidation = className == "BagContentGrid" ? moveValidationGrid : moveValidationList;
            string patchedMoveValidation = moveValidation + @"

            // [GC2_NETWORK_PATCH] Route the complete Move/stack operation before mutation.
            NetworkInventoryInterceptResult networkMove = this.InterceptNetworkMove(
                positionA, positionB, allowStack);
            if (networkMove != NetworkInventoryInterceptResult.Unhandled)
            {
                return networkMove == NetworkInventoryInterceptResult.HandledSuccess;
            }
            // [GC2_NETWORK_PATCH_END]";

            if (!TryReplaceRequired(ref content, moveValidation, patchedMoveValidation,
                    $"[GC2 Networking] Could not patch {className}.Move."))
            {
                return false;
            }

            const string rawAdd = @"        public override bool Add(RuntimeItem runtimeItem, Vector2Int position, bool allowStack)
        {
            if (runtimeItem == null) return false;
            if (this.Contains(runtimeItem)) return false;

            RuntimeItem.Bag_LastItemAttemptedAdd = runtimeItem;";

            const string patchedRawAdd = @"        public override bool Add(RuntimeItem runtimeItem, Vector2Int position, bool allowStack)
        {
            if (runtimeItem == null) return false;
            if (this.Contains(runtimeItem)) return false;

            // [GC2_NETWORK_PATCH] Guard direct RuntimeItem injection on network-managed bags.
            if (NetworkAddValidator != null &&
                !NetworkAddValidator.Invoke(this, runtimeItem, position, allowStack))
            {
                return false;
            }
            // [GC2_NETWORK_PATCH_END]

            RuntimeItem.Bag_LastItemAttemptedAdd = runtimeItem;";

            if (!TryReplaceRequired(ref content, rawAdd, patchedRawAdd,
                    $"[GC2 Networking] Could not patch {className}.Add(RuntimeItem)."))
            {
                return false;
            }

            const string rawRemoveList = @"            Cell cell = this.GetContent(position);
            if (cell == null || cell.Available) return null;

            runtimeItem ??= cell.Peek();

            this.Bag.Equipment.Unequip(runtimeItem);";

            const string patchedRawRemoveList = @"            Cell cell = this.GetContent(position);
            if (cell == null || cell.Available) return null;

            runtimeItem ??= cell.Peek();

            // [GC2_NETWORK_PATCH] Route a direct RuntimeItem removal before any local mutation.
            if (runtimeItem == null ||
                NetworkRemoveValidator != null && !NetworkRemoveValidator.Invoke(this, runtimeItem))
            {
                return null;
            }
            // [GC2_NETWORK_PATCH_END]

            this.Bag.Equipment.Unequip(runtimeItem);";

            const string rawRemoveGrid = @"            if (cell == null || cell.Available) return null;

            runtimeItem = runtimeItem != null
                ? cell.Remove(runtimeItem.RuntimeID)
                : cell.Pop();";

            const string patchedRawRemoveGrid = @"            if (cell == null || cell.Available) return null;

            runtimeItem ??= cell.Peek();

            // [GC2_NETWORK_PATCH] Route a direct RuntimeItem removal before any local mutation.
            if (runtimeItem == null ||
                NetworkRemoveValidator != null && !NetworkRemoveValidator.Invoke(this, runtimeItem))
            {
                return null;
            }
            // [GC2_NETWORK_PATCH_END]

            runtimeItem = cell.Remove(runtimeItem.RuntimeID);";

            string rawRemove = className == "BagContentGrid" ? rawRemoveGrid : rawRemoveList;
            string patchedRawRemove = className == "BagContentGrid"
                ? patchedRawRemoveGrid
                : patchedRawRemoveList;

            if (!TryReplaceRequired(ref content, rawRemove, patchedRawRemove,
                    $"[GC2 Networking] Could not patch {className}.Remove(RuntimeItem)."))
            {
                return false;
            }

            const string addTypeAtPosition = @"        public override RuntimeItem AddType(Item item, Vector2Int position, bool allowStack)
        {
            if (item == null) return null;

            RuntimeItem runtimeItem = item.CreateRuntimeItem(this.Bag.Args);";

            const string patchedAddTypeAtPosition = @"        public override RuntimeItem AddType(Item item, Vector2Int position, bool allowStack)
        {
            if (item == null) return null;

            // [GC2_NETWORK_PATCH] Add-Type is atomic; raw Add(RuntimeItem) remains a composite primitive.
            NetworkInventoryInterceptResult networkAdd = this.InterceptNetworkAddType(
                item, position, allowStack);
            if (networkAdd != NetworkInventoryInterceptResult.Unhandled) return null;
            // [GC2_NETWORK_PATCH_END]

            RuntimeItem runtimeItem = item.CreateRuntimeItem(this.Bag.Args);";

            if (!TryReplaceRequired(ref content, addTypeAtPosition, patchedAddTypeAtPosition,
                    $"[GC2 Networking] Could not patch positioned {className}.AddType."))
            {
                return false;
            }

            const string addTypeAutomatic = @"        public override RuntimeItem AddType(Item item, bool allowStack)
        {
            if (item == null) return null;

            RuntimeItem runtimeItem = item.CreateRuntimeItem(this.Bag.Args);";

            const string patchedAddTypeAutomatic = @"        public override RuntimeItem AddType(Item item, bool allowStack)
        {
            if (item == null) return null;

            // [GC2_NETWORK_PATCH] INVALID requests authoritative automatic placement.
            NetworkInventoryInterceptResult networkAdd = this.InterceptNetworkAddType(
                item, INVALID, allowStack);
            if (networkAdd != NetworkInventoryInterceptResult.Unhandled) return null;
            // [GC2_NETWORK_PATCH_END]

            RuntimeItem runtimeItem = item.CreateRuntimeItem(this.Bag.Args);";

            if (!TryReplaceRequired(ref content, addTypeAutomatic, patchedAddTypeAutomatic,
                    $"[GC2 Networking] Could not patch automatic {className}.AddType."))
            {
                return false;
            }

            const string removeType = @"        public override RuntimeItem RemoveType(Item item)
        {
            if (item == null) return null;
";

            const string patchedRemoveType = @"        public override RuntimeItem RemoveType(Item item)
        {
            if (item == null) return null;

            // [GC2_NETWORK_PATCH] Route semantic Remove Item without intercepting composite Remove calls.
            NetworkInventoryInterceptResult networkRemove = this.InterceptNetworkRemoveType(item);
            if (networkRemove != NetworkInventoryInterceptResult.Unhandled) return null;
            // [GC2_NETWORK_PATCH_END]
";

            return TryReplaceRequired(ref content, removeType, patchedRemoveType,
                $"[GC2 Networking] Could not patch {className}.RemoveType.");
        }

        private bool PatchBagWealth(ref string content)
        {
            const string classOpen = @"    public class BagWealth : IBagWealth
    {";
            const string patchedClassOpen = @"    public class BagWealth : IBagWealth
    {
        // [GC2_NETWORK_PATCH] Wealth authority hooks.
        public static System.Func<BagWealth, IdString, int, bool> NetworkAddValidator;
        public static System.Func<BagWealth, IdString, int, bool> NetworkSetValidator;
        public static bool IsNetworkingActive => NetworkAddValidator != null || NetworkSetValidator != null;
        // [GC2_NETWORK_PATCH_END]";

            if (!TryReplaceRequired(ref content, classOpen, patchedClassOpen,
                    "[GC2 Networking] Could not install BagWealth hooks."))
            {
                return false;
            }

            const string originalSet = @"        public void Set(IdString currencyID, int value)
        {
            int prevAmount = this.Get(currencyID);
            this.m_Currencies[currencyID] = value;
            int newAmount = this.Get(currencyID);

            this.EventChange?.Invoke(currencyID, prevAmount, newAmount);
        }";
            const string patchedSet = @"        public void Set(IdString currencyID, int value)
        {
            if (NetworkSetValidator != null && !NetworkSetValidator.Invoke(this, currencyID, value)) return;
            this.SetDirect(currencyID, value);
        }

        public void SetDirect(IdString currencyID, int value)
        {
            int prevAmount = this.Get(currencyID);
            this.m_Currencies[currencyID] = value;
            int newAmount = this.Get(currencyID);
            this.EventChange?.Invoke(currencyID, prevAmount, newAmount);
        }";

            if (!TryReplaceRequired(ref content, originalSet, patchedSet,
                    "[GC2 Networking] Could not patch BagWealth.Set."))
            {
                return false;
            }

            const string originalAdd = @"        public void Add(IdString currencyID, int value)
        {
            value = Mathf.Max(this.Get(currencyID) + value, 0);
            this.Set(currencyID, value);
        }";
            const string patchedAdd = @"        public void Add(IdString currencyID, int value)
        {
            if (NetworkAddValidator != null && !NetworkAddValidator.Invoke(this, currencyID, value)) return;
            this.AddDirect(currencyID, value);
        }

        public void AddDirect(IdString currencyID, int value)
        {
            value = Mathf.Max(this.Get(currencyID) + value, 0);
            this.SetDirect(currencyID, value);
        }";

            return TryReplaceRequired(ref content, originalAdd, patchedAdd,
                "[GC2 Networking] Could not patch BagWealth.Add.");
        }

        private bool PatchAddItemInstruction(ref string content)
        {
            const string originalRun = @"        protected override Task Run(Args args)
        {
            Item item = this.m_Item.Get(args);
            if (item == null) return DefaultResult;

            Bag bag = this.m_Bag.Get<Bag>(args);
            if (bag == null) return DefaultResult;

            bag.Content.AddType(item, true);
            return DefaultResult;
        }";

            const string patchedRun = @"        protected override Task Run(Args args)
        {
            Item item = this.m_Item.Get(args);
            if (item == null) return DefaultResult;

            Bag bag = this.m_Bag.Get<Bag>(args);
            if (bag == null) return DefaultResult;

            return RunNetworkOrLocal(bag, item, args.Self);
        }

        // [GC2_NETWORK_PATCH] Await server authorization before the instruction list continues.
        private static async Task RunNetworkOrLocal(Bag bag, Item item, GameObject source)
        {
            var interceptor = TBagContent.NetworkInstructionAddItemInterceptor;
            if (interceptor == null)
            {
                bag.Content.AddType(item, true);
                return;
            }

            try
            {
                NetworkInventoryInterceptResult result = await interceptor.Invoke(bag, item, source);
                if (result == NetworkInventoryInterceptResult.Unhandled)
                {
                    bag.Content.AddType(item, true);
                }
                else if (result == NetworkInventoryInterceptResult.HandledFailure)
                {
                    throw new InvalidOperationException(
                        ""The server rejected the network Inventory Add Item instruction."");
                }
            }
            catch (Exception exception)
            {
                // A configured network route fails closed. Falling back to a local grant would duplicate items.
                Debug.LogException(exception, bag);
                throw;
            }
        }
        // [GC2_NETWORK_PATCH_END]";

            return TryReplaceRequired(ref content, originalRun, patchedRun,
                "[GC2 Networking] Could not patch InstructionInventoryAddItem.Run.");
        }

        private bool PatchBagCellUI(ref string content)
        {
            const string contentAnchor = @"            IBagContent content = this.m_CellInfo.Bag.Content;

            return this.m_OnDrop switch";
            const string patchedContentAnchor = @"            IBagContent content = this.m_CellInfo.Bag.Content;

            // [GC2_NETWORK_PATCH] Route cell move/combine through server authority before mutation.
            if (content is TBagContent networkContent &&
                this.m_OnDrop != EnumOnDrop.Nothing &&
                TBagContent.NetworkCellDropInterceptor != null)
            {
                bool tryCombine = this.m_OnDrop == EnumOnDrop.MoveCombine;
                NetworkInventoryInterceptResult result = TBagContent.NetworkCellDropInterceptor.Invoke(
                    networkContent, dropCellUI.Position, this.Position, tryCombine);
                if (result != NetworkInventoryInterceptResult.Unhandled)
                {
                    return result == NetworkInventoryInterceptResult.HandledSuccess;
                }
            }
            // [GC2_NETWORK_PATCH_END]

            return this.m_OnDrop switch";

            if (!TryReplaceRequired(ref content, contentAnchor, patchedContentAnchor,
                    "[GC2 Networking] Could not patch BagCellUI move/combine."))
            {
                return false;
            }

            const string transferAnchor = @"            int times = TBagUI.TransferAmount switch
            {
                TBagUI.EnumTransferAmount.One => 1,
                TBagUI.EnumTransferAmount.Stack => this.Cell.Count,
                _ => throw new ArgumentOutOfRangeException()
            };

            for (int i = 0; i < times; ++i)";
            const string patchedTransferAnchor = @"            int times = TBagUI.TransferAmount switch
            {
                TBagUI.EnumTransferAmount.One => 1,
                TBagUI.EnumTransferAmount.Stack => this.Cell.Count,
                _ => throw new ArgumentOutOfRangeException()
            };

            // [GC2_NETWORK_PATCH] Route inter-bag transfer through server authority before mutation.
            if (this.BagUI.Bag.Content is TBagContent sourceContent &&
                bag.Content is TBagContent destinationContent &&
                TBagContent.NetworkTransferInterceptor != null)
            {
                NetworkInventoryInterceptResult result = TBagContent.NetworkTransferInterceptor.Invoke(
                    sourceContent, destinationContent, this.Cell.Peek(), times);
                if (result != NetworkInventoryInterceptResult.Unhandled) return;
            }
            // [GC2_NETWORK_PATCH_END]

            for (int i = 0; i < times; ++i)";

            if (!TryReplaceRequired(ref content, transferAnchor, patchedTransferAnchor,
                    "[GC2 Networking] Could not patch BagCellUI.SendToBag."))
            {
                return false;
            }

            const string splitAnchor = @"            int splitAmount = TBagUI.SplitAmount switch
            {
                TBagUI.EnumSplitAmount.One => 1,
                TBagUI.EnumSplitAmount.Half => this.Cell.Count / 2,
                _ => throw new ArgumentOutOfRangeException()
            };

            RuntimeItem runtimeItem = this.BagUI.Bag.Content.Remove(this.Position);";
            const string patchedSplitAnchor = @"            int splitAmount = TBagUI.SplitAmount switch
            {
                TBagUI.EnumSplitAmount.One => 1,
                TBagUI.EnumSplitAmount.Half => this.Cell.Count / 2,
                _ => throw new ArgumentOutOfRangeException()
            };

            // [GC2_NETWORK_PATCH] Route stack splitting through server authority before mutation.
            if (this.BagUI.Bag.Content is TBagContent networkContent &&
                TBagContent.NetworkSplitInterceptor != null)
            {
                NetworkInventoryInterceptResult result = TBagContent.NetworkSplitInterceptor.Invoke(
                    networkContent, this.Position, splitAmount);
                if (result != NetworkInventoryInterceptResult.Unhandled) return;
            }
            // [GC2_NETWORK_PATCH_END]

            RuntimeItem runtimeItem = this.BagUI.Bag.Content.Remove(this.Position);";

            return TryReplaceRequired(ref content, splitAnchor, patchedSplitAnchor,
                "[GC2 Networking] Could not patch BagCellUI.Split.");
        }

        private bool PatchCrafting(ref string content)
        {
            if (!content.Contains("using System.Threading.Tasks;"))
            {
                content = content.Replace(
                    "using System.Collections.Generic;",
                    "using System.Collections.Generic;\nusing System.Threading.Tasks;");
            }

            const string classOpen = @"    [Serializable]
    public class Crafting
    {";
            const string patchedClassOpen = @"    public readonly struct NetworkInventoryCraftInterceptResult
    {
        public readonly NetworkInventoryInterceptResult Status;
        public readonly RuntimeItem CraftedItem;
        public readonly RuntimeItem[] DismantledItems;

        public NetworkInventoryCraftInterceptResult(
            NetworkInventoryInterceptResult status,
            RuntimeItem craftedItem = null,
            RuntimeItem[] dismantledItems = null)
        {
            this.Status = status;
            this.CraftedItem = craftedItem;
            this.DismantledItems = dismantledItems;
        }
    }

    [Serializable]
    public class Crafting
    {
        // [GC2_NETWORK_PATCH] Semantic tinker hooks. Null preserves stock/offline behavior.
        public static Func<Item, Bag, Bag, NetworkInventoryCraftInterceptResult> NetworkCraftInterceptor;
        public static Func<Item, Bag, Bag, float, NetworkInventoryCraftInterceptResult> NetworkDismantleItemInterceptor;
        public static Func<RuntimeItem, Bag, Bag, float, NetworkInventoryCraftInterceptResult> NetworkDismantleRuntimeInterceptor;
        public static Func<Item, Bag, Bag, Task<NetworkInventoryCraftInterceptResult>> NetworkCraftAsyncInterceptor;
        public static Func<Item, Bag, Bag, float, Task<NetworkInventoryCraftInterceptResult>> NetworkDismantleItemAsyncInterceptor;
        public static Func<RuntimeItem, Bag, Bag, float, Task<NetworkInventoryCraftInterceptResult>> NetworkDismantleRuntimeAsyncInterceptor;
        // [GC2_NETWORK_PATCH_END]";

            if (!TryReplaceRequired(ref content, classOpen, patchedClassOpen,
                    "[GC2 Networking] Could not install Crafting hooks."))
            {
                return false;
            }

            const string craftValidation = @"            if (!CanCraft(item, inputBag, outputBag)) return null;
            if (!EnoughCraftingIngredients(item, inputBag)) return null;";
            const string patchedCraftValidation = craftValidation + @"

            if (NetworkCraftInterceptor != null)
            {
                NetworkInventoryCraftInterceptResult result = NetworkCraftInterceptor.Invoke(item, inputBag, outputBag);
                if (result.Status != NetworkInventoryInterceptResult.Unhandled)
                {
                    return result.Status == NetworkInventoryInterceptResult.HandledSuccess
                        ? result.CraftedItem
                        : null;
                }
            }";

            if (!TryReplaceRequired(ref content, craftValidation, patchedCraftValidation,
                    "[GC2 Networking] Could not patch Crafting.Craft."))
            {
                return false;
            }

            const string dismantleSection = @"        // DISMANTLE METHODS: ---------------------------------------------------------------------";
            const string patchedDismantleSection = @"        // [GC2_NETWORK_PATCH] UI-safe async crafting path.
        public static async Task<RuntimeItem> CraftAsync(Item item, Bag inputBag, Bag outputBag)
        {
            LastItemAttemptedCraft = item;
            if (!CanCraft(item, inputBag, outputBag)) return null;
            if (!EnoughCraftingIngredients(item, inputBag)) return null;

            if (NetworkCraftAsyncInterceptor != null)
            {
                NetworkInventoryCraftInterceptResult result = await NetworkCraftAsyncInterceptor.Invoke(
                    item, inputBag, outputBag);
                if (result.Status != NetworkInventoryInterceptResult.Unhandled)
                {
                    return result.Status == NetworkInventoryInterceptResult.HandledSuccess
                        ? result.CraftedItem
                        : null;
                }
            }

            return Craft(item, inputBag, outputBag);
        }
        // [GC2_NETWORK_PATCH_END]

        // DISMANTLE METHODS: ---------------------------------------------------------------------";

            if (!TryReplaceRequired(ref content, dismantleSection, patchedDismantleSection,
                    "[GC2 Networking] Could not install Crafting.CraftAsync."))
            {
                return false;
            }

            const string dismantleItemValidation = @"            LastItemAttemptedDismantle = item;
            if (!CanDismantle(item, inputBag, outputBag)) return null;

            RuntimeItem removeRuntimeItem = inputBag.Content.RemoveType(item);";
            const string patchedDismantleItemValidation = @"            LastItemAttemptedDismantle = item;
            if (!CanDismantle(item, inputBag, outputBag)) return null;

            if (NetworkDismantleItemInterceptor != null)
            {
                NetworkInventoryCraftInterceptResult result = NetworkDismantleItemInterceptor.Invoke(
                    item, inputBag, outputBag, chance);
                if (result.Status != NetworkInventoryInterceptResult.Unhandled)
                {
                    return result.Status == NetworkInventoryInterceptResult.HandledSuccess
                        ? result.DismantledItems
                        : null;
                }
            }

            RuntimeItem removeRuntimeItem = inputBag.Content.RemoveType(item);";

            if (!TryReplaceRequired(ref content, dismantleItemValidation, patchedDismantleItemValidation,
                    "[GC2 Networking] Could not patch Crafting.Dismantle(Item)."))
            {
                return false;
            }

            const string dismantleRuntimeValidation = @"            LastItemAttemptedDismantle = runtimeItem?.Item;
            if (!CanDismantle(runtimeItem?.Item, inputBag, outputBag)) return null;

            RuntimeItem removeRuntimeItem = inputBag.Content.Remove(runtimeItem);";
            const string patchedDismantleRuntimeValidation = @"            LastItemAttemptedDismantle = runtimeItem?.Item;
            if (!CanDismantle(runtimeItem?.Item, inputBag, outputBag)) return null;

            if (NetworkDismantleRuntimeInterceptor != null)
            {
                NetworkInventoryCraftInterceptResult result = NetworkDismantleRuntimeInterceptor.Invoke(
                    runtimeItem, inputBag, outputBag, chance);
                if (result.Status != NetworkInventoryInterceptResult.Unhandled)
                {
                    return result.Status == NetworkInventoryInterceptResult.HandledSuccess
                        ? result.DismantledItems
                        : null;
                }
            }

            RuntimeItem removeRuntimeItem = inputBag.Content.Remove(runtimeItem);";

            if (!TryReplaceRequired(ref content, dismantleRuntimeValidation, patchedDismantleRuntimeValidation,
                    "[GC2 Networking] Could not patch Crafting.Dismantle(RuntimeItem)."))
            {
                return false;
            }

            const string processDismantle = @"        private static RuntimeItem[] ProcessDismantle(RuntimeItem removedItem, Bag inputBag, Bag outputBag, float chance)";
            const string patchedProcessDismantle = @"        // [GC2_NETWORK_PATCH] UI-safe async dismantling paths.
        public static async Task<RuntimeItem[]> DismantleAsync(
            Item item, Bag inputBag, Bag outputBag, float chance)
        {
            LastItemAttemptedDismantle = item;
            if (!CanDismantle(item, inputBag, outputBag)) return null;

            if (NetworkDismantleItemAsyncInterceptor != null)
            {
                NetworkInventoryCraftInterceptResult result = await NetworkDismantleItemAsyncInterceptor.Invoke(
                    item, inputBag, outputBag, chance);
                if (result.Status != NetworkInventoryInterceptResult.Unhandled)
                {
                    return result.Status == NetworkInventoryInterceptResult.HandledSuccess
                        ? result.DismantledItems
                        : null;
                }
            }

            return Dismantle(item, inputBag, outputBag, chance);
        }

        public static async Task<RuntimeItem[]> DismantleAsync(
            RuntimeItem runtimeItem, Bag inputBag, Bag outputBag, float chance)
        {
            LastItemAttemptedDismantle = runtimeItem?.Item;
            if (!CanDismantle(runtimeItem?.Item, inputBag, outputBag)) return null;

            if (NetworkDismantleRuntimeAsyncInterceptor != null)
            {
                NetworkInventoryCraftInterceptResult result = await NetworkDismantleRuntimeAsyncInterceptor.Invoke(
                    runtimeItem, inputBag, outputBag, chance);
                if (result.Status != NetworkInventoryInterceptResult.Unhandled)
                {
                    return result.Status == NetworkInventoryInterceptResult.HandledSuccess
                        ? result.DismantledItems
                        : null;
                }
            }

            return Dismantle(runtimeItem, inputBag, outputBag, chance);
        }
        // [GC2_NETWORK_PATCH_END]

        private static RuntimeItem[] ProcessDismantle(RuntimeItem removedItem, Bag inputBag, Bag outputBag, float chance)";

            return TryReplaceRequired(ref content, processDismantle, patchedProcessDismantle,
                "[GC2 Networking] Could not install Crafting.DismantleAsync overloads.");
        }

        private bool PatchMerchant(ref string content)
        {
            if (!content.Contains("using System.Threading.Tasks;"))
            {
                content = content.Replace(
                    "using System;",
                    "using System;\nusing System.Threading.Tasks;");
            }

            const string classOpen = @"    public class Merchant : MonoBehaviour
    {";
            const string patchedClassOpen = @"    public class Merchant : MonoBehaviour
    {
        // [GC2_NETWORK_PATCH] Semantic merchant hooks. Null preserves stock/offline behavior.
        public static Func<Merchant, Bag, RuntimeItem, NetworkInventoryInterceptResult> NetworkBuyFromClientInterceptor;
        public static Func<Merchant, Bag, RuntimeItem, NetworkInventoryInterceptResult> NetworkSellToClientInterceptor;
        public static Func<Merchant, Bag, RuntimeItem, Task<NetworkInventoryInterceptResult>> NetworkBuyFromClientAsyncInterceptor;
        public static Func<Merchant, Bag, RuntimeItem, Task<NetworkInventoryInterceptResult>> NetworkSellToClientAsyncInterceptor;
        // [GC2_NETWORK_PATCH_END]";

            if (!TryReplaceRequired(ref content, classOpen, patchedClassOpen,
                    "[GC2 Networking] Could not install Merchant hooks."))
            {
                return false;
            }

            const string buyValidation = @"        public bool BuyFromClient(Bag clientBag, RuntimeItem runtimeItem)
        {
            if (!this.CanBuyFromClient(clientBag, runtimeItem)) return false;";
            const string patchedBuyValidation = buyValidation + @"

            if (NetworkBuyFromClientInterceptor != null)
            {
                NetworkInventoryInterceptResult result = NetworkBuyFromClientInterceptor.Invoke(
                    this, clientBag, runtimeItem);
                if (result != NetworkInventoryInterceptResult.Unhandled)
                {
                    return result == NetworkInventoryInterceptResult.HandledSuccess;
                }
            }";

            if (!TryReplaceRequired(ref content, buyValidation, patchedBuyValidation,
                    "[GC2 Networking] Could not patch Merchant.BuyFromClient."))
            {
                return false;
            }

            const string sellValidation = @"        public bool SellToClient(Bag clientBag, RuntimeItem runtimeItem)
        {
            if (!this.CanSellToClient(clientBag, runtimeItem)) return false;";
            const string patchedSellValidation = sellValidation + @"

            if (NetworkSellToClientInterceptor != null)
            {
                NetworkInventoryInterceptResult result = NetworkSellToClientInterceptor.Invoke(
                    this, clientBag, runtimeItem);
                if (result != NetworkInventoryInterceptResult.Unhandled)
                {
                    return result == NetworkInventoryInterceptResult.HandledSuccess;
                }
            }";

            if (!TryReplaceRequired(ref content, sellValidation, patchedSellValidation,
                    "[GC2 Networking] Could not patch Merchant.SellToClient."))
            {
                return false;
            }

            const string sellSummary = @"        /// <summary>
        /// The Merchant sells an item to the client";
            const string patchedSellSummary = @"        // [GC2_NETWORK_PATCH] UI-safe client sale path.
        public async Task<bool> BuyFromClientAsync(Bag clientBag, RuntimeItem runtimeItem)
        {
            if (!this.CanBuyFromClient(clientBag, runtimeItem)) return false;
            if (NetworkBuyFromClientAsyncInterceptor != null)
            {
                NetworkInventoryInterceptResult result = await NetworkBuyFromClientAsyncInterceptor.Invoke(
                    this, clientBag, runtimeItem);
                if (result != NetworkInventoryInterceptResult.Unhandled)
                    return result == NetworkInventoryInterceptResult.HandledSuccess;
            }

            return this.BuyFromClient(clientBag, runtimeItem);
        }
        // [GC2_NETWORK_PATCH_END]

        /// <summary>
        /// The Merchant sells an item to the client";

            if (!TryReplaceRequired(ref content, sellSummary, patchedSellSummary,
                    "[GC2 Networking] Could not install Merchant.BuyFromClientAsync."))
            {
                return false;
            }

            const string priceSummary = @"        /// <summary>
        /// The price an item is sold to the client";
            const string patchedPriceSummary = @"        // [GC2_NETWORK_PATCH] UI-safe merchant purchase path.
        public async Task<bool> SellToClientAsync(Bag clientBag, RuntimeItem runtimeItem)
        {
            if (!this.CanSellToClient(clientBag, runtimeItem)) return false;
            if (NetworkSellToClientAsyncInterceptor != null)
            {
                NetworkInventoryInterceptResult result = await NetworkSellToClientAsyncInterceptor.Invoke(
                    this, clientBag, runtimeItem);
                if (result != NetworkInventoryInterceptResult.Unhandled)
                    return result == NetworkInventoryInterceptResult.HandledSuccess;
            }

            return this.SellToClient(clientBag, runtimeItem);
        }
        // [GC2_NETWORK_PATCH_END]

        /// <summary>
        /// The price an item is sold to the client";

            return TryReplaceRequired(ref content, priceSummary, patchedPriceSummary,
                "[GC2 Networking] Could not install Merchant.SellToClientAsync.");
        }

        private bool PatchCraftingItemUI(ref string content)
        {
            const string original = @"            RuntimeItem runtimeItem = Crafting.Craft(this.RuntimeItem.Item, this.InputBag, this.OutputBag);";
            const string patched = @"            // [GC2_NETWORK_PATCH] Await authoritative completion before OnComplete.
            RuntimeItem runtimeItem = await Crafting.CraftAsync(
                this.RuntimeItem.Item, this.InputBag, this.OutputBag);
            // [GC2_NETWORK_PATCH_END]";

            return TryReplaceRequired(ref content, original, patched,
                "[GC2 Networking] Could not patch CraftingItemUI authoritative completion.");
        }

        private bool PatchDismantlingItemUI(ref string content)
        {
            const string original = @"            RuntimeItem[] runtimeItems = Crafting.Dismantle(this.RuntimeItem, this.InputBag, this.OutputBag, chance);";
            const string patched = @"            // [GC2_NETWORK_PATCH] Await authoritative completion before OnComplete.
            RuntimeItem[] runtimeItems = await Crafting.DismantleAsync(
                this.RuntimeItem, this.InputBag, this.OutputBag, chance);
            // [GC2_NETWORK_PATCH_END]";

            return TryReplaceRequired(ref content, original, patched,
                "[GC2 Networking] Could not patch DismantlingItemUI authoritative completion.");
        }

        private bool PatchCellMerchantUI(ref string content)
        {
            const string originalBuy = @"        private void Buy()
        {
            this.m_MerchantUI.Merchant.SellToClient(
                this.m_MerchantUI.ClientBag,
                this.m_RuntimeItem
            );

            this.EventTrade?.Invoke();
        }";
            const string patchedBuy = @"        // [GC2_NETWORK_PATCH] Trade events only fire after authoritative success.
        private async void Buy()
        {
            bool success = await this.m_MerchantUI.Merchant.SellToClientAsync(
                this.m_MerchantUI.ClientBag,
                this.m_RuntimeItem
            );

            if (success) this.EventTrade?.Invoke();
        }
        // [GC2_NETWORK_PATCH_END]";

            if (!TryReplaceRequired(ref content, originalBuy, patchedBuy,
                    "[GC2 Networking] Could not patch CellMerchantUI.Buy."))
            {
                return false;
            }

            const string originalSell = @"        private void Sell()
        {
            this.m_MerchantUI.Merchant.BuyFromClient(
                this.m_MerchantUI.ClientBag,
                this.m_RuntimeItem
            );

            this.EventTrade?.Invoke();
        }";
            const string patchedSell = @"        // [GC2_NETWORK_PATCH] Trade events only fire after authoritative success.
        private async void Sell()
        {
            bool success = await this.m_MerchantUI.Merchant.BuyFromClientAsync(
                this.m_MerchantUI.ClientBag,
                this.m_RuntimeItem
            );

            if (success) this.EventTrade?.Invoke();
        }
        // [GC2_NETWORK_PATCH_END]";

            return TryReplaceRequired(ref content, originalSell, patchedSell,
                "[GC2 Networking] Could not patch CellMerchantUI.Sell.");
        }
    }
}
