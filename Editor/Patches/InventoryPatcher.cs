using UnityEngine;

namespace Arawn.EnemyMasses.Editor.Integration.GameCreator2.Patches
{
    /// <summary>
    /// Patcher implementation for GC2 Inventory module.
    /// Adds network validation hooks to TBagContent for server-authoritative inventory modifications.
    /// </summary>
    public class InventoryPatcher : GC2PatcherBase
    {
        public override string ModuleName => "Inventory";
        public override string PatchVersion => "2.1.0-inventory";
        public override string DisplayName => "Inventory (Game Creator 2)";
        
        public override string PatchDescription =>
            "This will modify the Game Creator 2 Inventory source code to add\n" +
            "server-authoritative networking hooks.\n\n" +
            "TBagContent.Add/Remove/Move methods will have network validation.\n" +
            "TBagWealth.Add/Remove methods will have network hooks.";
        
        protected override string[] FilesToPatch => new[]
        {
            "Plugins/GameCreator/Packages/Inventory/Runtime/Classes/Bag/Content/TBagContent.cs",
            "Plugins/GameCreator/Packages/Inventory/Runtime/Classes/Bag/Wealth/BagWealth.cs"
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
                    "NetworkAddValidator",
                    "NetworkRemoveValidator",
                    "NetworkMoveValidator",
                    "NetworkDropValidator",
                    "NetworkUseValidator",
                    "UseDirect(",
                    "DropDirect("
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

            return base.GetRequiredPatchTokens(relativePath);
        }

        protected override System.Collections.Generic.Dictionary<string, int> GetRequiredPatchTokenCounts(string relativePath)
        {
            if (relativePath.EndsWith("TBagContent.cs"))
            {
                return new System.Collections.Generic.Dictionary<string, int>
                {
                    { "NetworkUseValidator.Invoke", 1 },
                    { "NetworkDropValidator.Invoke", 1 },
                    { "NetworkAddValidator.Invoke", 1 },
                    { "NetworkRemoveValidator.Invoke", 1 },
                    { "NetworkMoveValidator.Invoke", 1 }
                };
            }

            if (relativePath.EndsWith("BagWealth.cs"))
            {
                return new System.Collections.Generic.Dictionary<string, int>
                {
                    { "NetworkSetValidator.Invoke", 1 },
                    { "NetworkAddValidator.Invoke", 1 }
                };
            }

            return base.GetRequiredPatchTokenCounts(relativePath);
        }
        
        protected override bool PatchFile(string relativePath)
        {
            string content = ReadFile(relativePath);

            ExistingPatchState existingPatchState = PrepareContentForPatch(relativePath, ref content);
            if (existingPatchState == ExistingPatchState.SkipAlreadyPatched) return true;
            if (existingPatchState == ExistingPatchState.Failed) return false;
            
            if (relativePath.EndsWith("TBagContent.cs"))
            {
                return PatchTBagContent(relativePath, content);
            }
            else if (relativePath.EndsWith("BagWealth.cs"))
            {
                return PatchBagWealth(relativePath, content);
            }
            
            return false;
        }
        
        private bool PatchTBagContent(string relativePath, string content)
        {
            // Add using statements and patch marker
            string originalUsings = @"using System;
using System.Collections.Generic;
using GameCreator.Runtime.Common;
using UnityEngine;

namespace GameCreator.Runtime.Inventory
{
    [Serializable]
    public abstract class TBagContent : IBagContent
    {";

            string patchedUsings = @"using System;
using System.Collections.Generic;
using GameCreator.Runtime.Common;
using UnityEngine;

" + PatchMarker + @"
// This file has been patched for GC2 Networking server authority.
// Do not modify the patched sections manually.
// Use Game Creator > Networking Layer > Patches > Inventory > Unpatch to restore.

namespace GameCreator.Runtime.Inventory
{
    [Serializable]
    public abstract class TBagContent : IBagContent
    {
        // [GC2_NETWORK_PATCH] Static hooks for server-authoritative networking
        
        /// <summary>Validates if adding an item should proceed locally.</summary>
        public static Func<TBagContent, RuntimeItem, Vector2Int, bool, bool> NetworkAddValidator;
        
        /// <summary>Validates if removing an item should proceed locally.</summary>
        public static Func<TBagContent, RuntimeItem, bool> NetworkRemoveValidator;
        
        /// <summary>Validates if moving an item should proceed locally.</summary>
        public static Func<TBagContent, Vector2Int, Vector2Int, bool, bool> NetworkMoveValidator;
        
        /// <summary>Validates if dropping an item should proceed locally.</summary>
        public static Func<TBagContent, RuntimeItem, Vector3, bool> NetworkDropValidator;
        
        /// <summary>Validates if using an item should proceed locally.</summary>
        public static Func<TBagContent, RuntimeItem, bool> NetworkUseValidator;
        
        /// <summary>Returns true if networking hooks are active.</summary>
        public static bool IsNetworkingActive => NetworkAddValidator != null;
        
        // [GC2_NETWORK_PATCH_END]
";

            if (!TryReplaceRequired(
                    ref content,
                    originalUsings,
                    patchedUsings,
                    "[GC2 Networking] Could not find expected using statements in TBagContent.cs."))
            {
                return false;
            }
            
            // Patch the Use(RuntimeItem) method
            string originalUse = @"        public virtual bool Use(RuntimeItem runtimeItem)
        {
            if (!this.Contains(runtimeItem)) return false;
            if (!runtimeItem.CanUse()) return false;

            _ = runtimeItem.Use();
            if (runtimeItem.Item.Usage.ConsumeWhenUse) this.Remove(runtimeItem);
            
            this.EventUse?.Invoke(runtimeItem);
            return true;
        }";
            
            string patchedUse = @"        public virtual bool Use(RuntimeItem runtimeItem)
        {
            if (!this.Contains(runtimeItem)) return false;
            if (!runtimeItem.CanUse()) return false;
            
            // [GC2_NETWORK_PATCH] Server authority check
            if (NetworkUseValidator != null && !NetworkUseValidator.Invoke(this, runtimeItem))
            {
                return false; // Network will handle this
            }
            // [GC2_NETWORK_PATCH_END]

            _ = runtimeItem.Use();
            if (runtimeItem.Item.Usage.ConsumeWhenUse) this.Remove(runtimeItem);
            
            this.EventUse?.Invoke(runtimeItem);
            return true;
        }
        
        // [GC2_NETWORK_PATCH] Server-side direct use (bypasses validation)
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

            if (!TryReplaceRequired(
                    ref content,
                    originalUse,
                    patchedUse,
                    "[GC2 Networking] Could not find expected Use method in TBagContent.cs."))
            {
                return false;
            }
            
            // Patch the Drop method
            string originalDrop = @"        public GameObject Drop(RuntimeItem runtimeItem, Vector3 point)
        {
            if (runtimeItem == null) return null;
            if (!this.Contains(runtimeItem)) return null;
            
            if (this.Bag.Wearer == null) return null;
            if (!runtimeItem.Item.HasPrefab) return null;
            if (!runtimeItem.Item.CanDrop) return null;

            RuntimeItem removeRuntimeItem = this.Remove(runtimeItem);
            return Item.Drop(removeRuntimeItem, point, Quaternion.identity);
        }";
            
            string patchedDrop = @"        public GameObject Drop(RuntimeItem runtimeItem, Vector3 point)
        {
            if (runtimeItem == null) return null;
            if (!this.Contains(runtimeItem)) return null;
            
            if (this.Bag.Wearer == null) return null;
            if (!runtimeItem.Item.HasPrefab) return null;
            if (!runtimeItem.Item.CanDrop) return null;
            
            // [GC2_NETWORK_PATCH] Server authority check
            if (NetworkDropValidator != null && !NetworkDropValidator.Invoke(this, runtimeItem, point))
            {
                return null; // Network will handle this
            }
            // [GC2_NETWORK_PATCH_END]

            RuntimeItem removeRuntimeItem = this.Remove(runtimeItem);
            return Item.Drop(removeRuntimeItem, point, Quaternion.identity);
        }
        
        // [GC2_NETWORK_PATCH] Server-side direct drop (bypasses validation)
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

            if (!TryReplaceRequired(
                    ref content,
                    originalDrop,
                    patchedDrop,
                    "[GC2 Networking] Could not find expected Drop method in TBagContent.cs."))
            {
                return false;
            }
            
            // Add network-aware ExecuteEventAdd/Remove methods after the protected methods
            string originalProtectedMethods = @"        protected void ExecuteEventChange()
        {
            this.EventChange?.Invoke();
        }

        protected void ExecuteEventAdd(RuntimeItem runtimeItem)
        {
            this.EventAdd?.Invoke(runtimeItem);
        }

        protected void ExecuteEventRemove(RuntimeItem runtimeItem)
        {
            this.EventRemove?.Invoke(runtimeItem);
        }";
            
            string patchedProtectedMethods = @"        protected void ExecuteEventChange()
        {
            this.EventChange?.Invoke();
        }

        protected void ExecuteEventAdd(RuntimeItem runtimeItem)
        {
            this.EventAdd?.Invoke(runtimeItem);
        }

        protected void ExecuteEventRemove(RuntimeItem runtimeItem)
        {
            this.EventRemove?.Invoke(runtimeItem);
        }
        
        // [GC2_NETWORK_PATCH] Network validation helpers
        
        /// <summary>Server-side: Check if add operation should be validated by network.</summary>
        protected bool ShouldValidateNetworkAdd(RuntimeItem runtimeItem, Vector2Int position, bool allowStack)
        {
            return NetworkAddValidator != null && !NetworkAddValidator.Invoke(this, runtimeItem, position, allowStack);
        }
        
        /// <summary>Server-side: Check if remove operation should be validated by network.</summary>
        protected bool ShouldValidateNetworkRemove(RuntimeItem runtimeItem)
        {
            return NetworkRemoveValidator != null && !NetworkRemoveValidator.Invoke(this, runtimeItem);
        }
        
        /// <summary>Server-side: Check if move operation should be validated by network.</summary>
        protected bool ShouldValidateNetworkMove(Vector2Int positionA, Vector2Int positionB, bool allowStack)
        {
            return NetworkMoveValidator != null && !NetworkMoveValidator.Invoke(this, positionA, positionB, allowStack);
        }
        
        // [GC2_NETWORK_PATCH_END]";

            if (!TryReplaceRequired(
                    ref content,
                    originalProtectedMethods,
                    patchedProtectedMethods,
                    "[GC2 Networking] Could not find expected protected methods in TBagContent.cs."))
            {
                return false;
            }
            
            WriteFile(relativePath, content);
            Debug.Log($"[GC2 Networking] Patched {relativePath}");
            return true;
        }
        
        private bool PatchBagWealth(string relativePath, string content)
        {
            // Add using statements and patch marker
            string originalUsings = @"using System;
using System.Collections.Generic;
using GameCreator.Runtime.Common;
using UnityEngine;

namespace GameCreator.Runtime.Inventory
{
    [Serializable]
    public class BagWealth : IBagWealth
    {";

            string patchedUsings = @"using System;
using System.Collections.Generic;
using GameCreator.Runtime.Common;
using UnityEngine;

" + PatchMarker + @"
// This file has been patched for GC2 Networking server authority.
// Do not modify the patched sections manually.
// Use Game Creator > Networking Layer > Patches > Inventory > Unpatch to restore.

namespace GameCreator.Runtime.Inventory
{
    [Serializable]
    public class BagWealth : IBagWealth
    {
        // [GC2_NETWORK_PATCH] Static hooks for server-authoritative networking
        
        /// <summary>Validates if adding currency should proceed locally.</summary>
        public static Func<BagWealth, IdString, int, bool> NetworkAddValidator;
        
        /// <summary>Validates if setting currency should proceed locally.</summary>
        public static Func<BagWealth, IdString, int, bool> NetworkSetValidator;
        
        /// <summary>Returns true if networking hooks are active.</summary>
        public static bool IsNetworkingActive => NetworkAddValidator != null;
        
        // [GC2_NETWORK_PATCH_END]
";

            if (!TryReplaceRequired(
                    ref content,
                    originalUsings,
                    patchedUsings,
                    "[GC2 Networking] Could not find expected using statements in BagWealth.cs."))
            {
                return false;
            }
            
            // Patch the Set method
            string originalSet = @"        public void Set(IdString currencyID, int value)
        {
            int prevAmount = this.Get(currencyID);
            this.m_Currencies[currencyID] = value;
            int newAmount = this.Get(currencyID);
            
            this.EventChange?.Invoke(currencyID, prevAmount, newAmount);
        }";
            
            string patchedSet = @"        public void Set(IdString currencyID, int value)
        {
            // [GC2_NETWORK_PATCH] Server authority check
            if (NetworkSetValidator != null && !NetworkSetValidator.Invoke(this, currencyID, value))
            {
                return; // Network will handle this
            }
            // [GC2_NETWORK_PATCH_END]
            
            int prevAmount = this.Get(currencyID);
            this.m_Currencies[currencyID] = value;
            int newAmount = this.Get(currencyID);
            
            this.EventChange?.Invoke(currencyID, prevAmount, newAmount);
        }
        
        // [GC2_NETWORK_PATCH] Server-side direct set (bypasses validation)
        public void SetDirect(IdString currencyID, int value)
        {
            int prevAmount = this.Get(currencyID);
            this.m_Currencies[currencyID] = value;
            int newAmount = this.Get(currencyID);
            this.EventChange?.Invoke(currencyID, prevAmount, newAmount);
        }
        // [GC2_NETWORK_PATCH_END]";

            if (!TryReplaceRequired(
                    ref content,
                    originalSet,
                    patchedSet,
                    "[GC2 Networking] Could not find expected Set method in BagWealth.cs."))
            {
                return false;
            }
            
            // Patch the Add method
            string originalAdd = @"        public void Add(IdString currencyID, int value)
        {
            value = Mathf.Max(this.Get(currencyID) + value, 0);
            this.Set(currencyID, value);
        }";
            
            string patchedAdd = @"        public void Add(IdString currencyID, int value)
        {
            // [GC2_NETWORK_PATCH] Server authority check
            if (NetworkAddValidator != null && !NetworkAddValidator.Invoke(this, currencyID, value))
            {
                return; // Network will handle this
            }
            // [GC2_NETWORK_PATCH_END]
            
            value = Mathf.Max(this.Get(currencyID) + value, 0);
            this.SetDirect(currencyID, value); // Use direct to avoid double validation
        }
        
        // [GC2_NETWORK_PATCH] Server-side direct add (bypasses validation)
        public void AddDirect(IdString currencyID, int value)
        {
            value = Mathf.Max(this.Get(currencyID) + value, 0);
            this.SetDirect(currencyID, value);
        }
        // [GC2_NETWORK_PATCH_END]";

            if (!TryReplaceRequired(
                    ref content,
                    originalAdd,
                    patchedAdd,
                    "[GC2 Networking] Could not find expected Add method in BagWealth.cs."))
            {
                return false;
            }
            
            WriteFile(relativePath, content);
            Debug.Log($"[GC2 Networking] Patched {relativePath}");
            return true;
        }
    }
}
