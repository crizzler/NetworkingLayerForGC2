using UnityEngine;

namespace Arawn.EnemyMasses.Editor.Integration.GameCreator2.Patches
{
    /// <summary>
    /// Patcher implementation for GC2 Stats module.
    /// Adds network validation hooks to RuntimeStatData for server-authoritative stat modifications.
    /// </summary>
    public class StatsPatcher : GC2PatcherBase
    {
        public override string ModuleName => "Stats";
        public override string PatchVersion => "1.0.0";
        public override string DisplayName => "Stats (Game Creator 2)";
        
        public override string PatchDescription =>
            "This will modify the Game Creator 2 Stats source code to add\n" +
            "server-authoritative networking hooks.\n\n" +
            "RuntimeStatData.Base setter will have network validation.\n" +
            "AddModifier/RemoveModifier/ClearModifiers will have network hooks.\n" +
            "RuntimeAttributeData.Value setter will have network validation.";
        
        protected override string[] FilesToPatch => new[]
        {
            "Plugins/GameCreator/Packages/Stats/Runtime/Classes/Traits/Stats/RuntimeStatData.cs",
            "Plugins/GameCreator/Packages/Stats/Runtime/Classes/Traits/Attributes/RuntimeAttributeData.cs"
        };
        
        protected override bool PatchFile(string relativePath)
        {
            string content = ReadFile(relativePath);
            
            // Check if already patched
            if (content.Contains(PatchMarker))
            {
                Debug.LogWarning($"[GC2 Networking] {relativePath} already contains patch marker.");
                return true;
            }
            
            if (relativePath.EndsWith("RuntimeStatData.cs"))
            {
                return PatchRuntimeStatData(relativePath, content);
            }
            else if (relativePath.EndsWith("RuntimeAttributeData.cs"))
            {
                return PatchRuntimeAttributeData(relativePath, content);
            }
            
            return false;
        }
        
        private bool PatchRuntimeStatData(string relativePath, string content)
        {
            // Add using statements and patch marker
            string originalUsings = @"using System;
using GameCreator.Runtime.Common;
using UnityEngine;

namespace GameCreator.Runtime.Stats
{
    public class RuntimeStatData
    {";

            string patchedUsings = @"using System;
using GameCreator.Runtime.Common;
using UnityEngine;

" + PatchMarker + @"
// This file has been patched for GC2 Networking server authority.
// Do not modify the patched sections manually.
// Use Tools > Game Creator 2 Networking > Patches > Stats > Unpatch to restore.

namespace GameCreator.Runtime.Stats
{
    public class RuntimeStatData
    {
        // [GC2_NETWORK_PATCH] Static hooks for server-authoritative networking
        
        /// <summary>Validates if a base value change should proceed locally.</summary>
        public static Func<RuntimeStatData, double, bool> NetworkBaseValidator;
        
        /// <summary>Validates if adding a modifier should proceed locally.</summary>
        public static Func<RuntimeStatData, ModifierType, double, bool> NetworkAddModifierValidator;
        
        /// <summary>Validates if removing a modifier should proceed locally.</summary>
        public static Func<RuntimeStatData, ModifierType, double, bool> NetworkRemoveModifierValidator;
        
        /// <summary>Validates if clearing modifiers should proceed locally.</summary>
        public static Func<RuntimeStatData, bool> NetworkClearModifiersValidator;
        
        /// <summary>Returns true if networking hooks are active.</summary>
        public static bool IsNetworkingActive => NetworkBaseValidator != null;
        
        // [GC2_NETWORK_PATCH_END]
";

            if (!content.Contains(originalUsings))
            {
                Debug.LogError("[GC2 Networking] Could not find expected using statements in RuntimeStatData.cs.");
                return false;
            }
            
            content = content.Replace(originalUsings, patchedUsings);
            
            // Patch the Base property setter
            string originalBaseSetter = @"        public double Base
        {
            get => this.m_Base;
            set
            {
                if (Math.Abs(this.m_Base - value) < float.Epsilon) return;

                double prevValue = this.Value;
                this.m_Base = value;

                this.EventChange?.Invoke(this.m_Stat.ID, this.Value - prevValue);
            }
        }";
            
            string patchedBaseSetter = @"        public double Base
        {
            get => this.m_Base;
            set
            {
                if (Math.Abs(this.m_Base - value) < float.Epsilon) return;
                
                // [GC2_NETWORK_PATCH] Server authority check
                if (NetworkBaseValidator != null && !NetworkBaseValidator.Invoke(this, value))
                {
                    return; // Network will handle this change
                }
                // [GC2_NETWORK_PATCH_END]

                double prevValue = this.Value;
                this.m_Base = value;

                this.EventChange?.Invoke(this.m_Stat.ID, this.Value - prevValue);
            }
        }";

            if (!content.Contains(originalBaseSetter))
            {
                Debug.LogError("[GC2 Networking] Could not find expected Base property in RuntimeStatData.cs.");
                return false;
            }
            
            content = content.Replace(originalBaseSetter, patchedBaseSetter);
            
            // Patch AddModifier method
            string originalAddModifier = @"        public void AddModifier(ModifierType type, double value)
        {
            switch (type)
            {
                case ModifierType.Constant: this.m_Modifiers.AddConstant(value); break;
                case ModifierType.Percent: this.m_Modifiers.AddPercentage(value); break;
                default: throw new ArgumentOutOfRangeException(nameof(type), type, null);
            }
            
            this.EventChange?.Invoke(this.m_Stat.ID, 0f);
        }";
            
            string patchedAddModifier = @"        public void AddModifier(ModifierType type, double value)
        {
            // [GC2_NETWORK_PATCH] Server authority check
            if (NetworkAddModifierValidator != null && !NetworkAddModifierValidator.Invoke(this, type, value))
            {
                return; // Network will handle this
            }
            // [GC2_NETWORK_PATCH_END]
            
            switch (type)
            {
                case ModifierType.Constant: this.m_Modifiers.AddConstant(value); break;
                case ModifierType.Percent: this.m_Modifiers.AddPercentage(value); break;
                default: throw new ArgumentOutOfRangeException(nameof(type), type, null);
            }
            
            this.EventChange?.Invoke(this.m_Stat.ID, 0f);
        }
        
        // [GC2_NETWORK_PATCH] Server-side direct add (bypasses validation)
        public void AddModifierDirect(ModifierType type, double value)
        {
            switch (type)
            {
                case ModifierType.Constant: this.m_Modifiers.AddConstant(value); break;
                case ModifierType.Percent: this.m_Modifiers.AddPercentage(value); break;
                default: throw new ArgumentOutOfRangeException(nameof(type), type, null);
            }
            this.EventChange?.Invoke(this.m_Stat.ID, 0f);
        }
        // [GC2_NETWORK_PATCH_END]";

            if (!content.Contains(originalAddModifier))
            {
                Debug.LogError("[GC2 Networking] Could not find expected AddModifier method in RuntimeStatData.cs.");
                return false;
            }
            
            content = content.Replace(originalAddModifier, patchedAddModifier);
            
            // Patch RemoveModifier method
            string originalRemoveModifier = @"        public bool RemoveModifier(ModifierType type, double value)
        {
            bool success = type switch
            {
                ModifierType.Constant => this.m_Modifiers.RemoveConstant(value),
                ModifierType.Percent => this.m_Modifiers.RemovePercentage(value),
                _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
            };
            
            if (success) this.EventChange?.Invoke(this.m_Stat.ID, 0f);
            return success;
        }";
            
            string patchedRemoveModifier = @"        public bool RemoveModifier(ModifierType type, double value)
        {
            // [GC2_NETWORK_PATCH] Server authority check
            if (NetworkRemoveModifierValidator != null && !NetworkRemoveModifierValidator.Invoke(this, type, value))
            {
                return false; // Network will handle this
            }
            // [GC2_NETWORK_PATCH_END]
            
            bool success = type switch
            {
                ModifierType.Constant => this.m_Modifiers.RemoveConstant(value),
                ModifierType.Percent => this.m_Modifiers.RemovePercentage(value),
                _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
            };
            
            if (success) this.EventChange?.Invoke(this.m_Stat.ID, 0f);
            return success;
        }
        
        // [GC2_NETWORK_PATCH] Server-side direct remove (bypasses validation)
        public bool RemoveModifierDirect(ModifierType type, double value)
        {
            bool success = type switch
            {
                ModifierType.Constant => this.m_Modifiers.RemoveConstant(value),
                ModifierType.Percent => this.m_Modifiers.RemovePercentage(value),
                _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
            };
            if (success) this.EventChange?.Invoke(this.m_Stat.ID, 0f);
            return success;
        }
        // [GC2_NETWORK_PATCH_END]";

            if (!content.Contains(originalRemoveModifier))
            {
                Debug.LogError("[GC2 Networking] Could not find expected RemoveModifier method in RuntimeStatData.cs.");
                return false;
            }
            
            content = content.Replace(originalRemoveModifier, patchedRemoveModifier);
            
            // Patch ClearModifiers method
            string originalClearModifiers = @"        public void ClearModifiers()
        {
            if (this.m_Modifiers.Count > 0)
            {
                this.m_Modifiers.Clear();
                this.EventChange?.Invoke(this.m_Stat.ID, 0f);
            }
        }";
            
            string patchedClearModifiers = @"        public void ClearModifiers()
        {
            if (this.m_Modifiers.Count > 0)
            {
                // [GC2_NETWORK_PATCH] Server authority check
                if (NetworkClearModifiersValidator != null && !NetworkClearModifiersValidator.Invoke(this))
                {
                    return; // Network will handle this
                }
                // [GC2_NETWORK_PATCH_END]
                
                this.m_Modifiers.Clear();
                this.EventChange?.Invoke(this.m_Stat.ID, 0f);
            }
        }
        
        // [GC2_NETWORK_PATCH] Server-side direct clear (bypasses validation)
        public void ClearModifiersDirect()
        {
            if (this.m_Modifiers.Count > 0)
            {
                this.m_Modifiers.Clear();
                this.EventChange?.Invoke(this.m_Stat.ID, 0f);
            }
        }
        // [GC2_NETWORK_PATCH_END]";

            if (!content.Contains(originalClearModifiers))
            {
                Debug.LogError("[GC2 Networking] Could not find expected ClearModifiers method in RuntimeStatData.cs.");
                return false;
            }
            
            content = content.Replace(originalClearModifiers, patchedClearModifiers);
            
            // Add SetBaseDirect method before internal methods
            string originalInternalMethod = @"        // INTERNAL METHODS: ----------------------------------------------------------------------

        internal void SetBaseWithoutNotify(double value)
        {
            this.m_Base = value;
        }";
            
            string patchedInternalMethod = @"        // [GC2_NETWORK_PATCH] Server-side direct base setter (bypasses validation)
        public void SetBaseDirect(double value)
        {
            if (Math.Abs(this.m_Base - value) < float.Epsilon) return;
            double prevValue = this.Value;
            this.m_Base = value;
            this.EventChange?.Invoke(this.m_Stat.ID, this.Value - prevValue);
        }
        // [GC2_NETWORK_PATCH_END]
        
        // INTERNAL METHODS: ----------------------------------------------------------------------

        internal void SetBaseWithoutNotify(double value)
        {
            this.m_Base = value;
        }";

            if (!content.Contains(originalInternalMethod))
            {
                Debug.LogError("[GC2 Networking] Could not find expected internal methods in RuntimeStatData.cs.");
                return false;
            }
            
            content = content.Replace(originalInternalMethod, patchedInternalMethod);
            
            WriteFile(relativePath, content);
            Debug.Log($"[GC2 Networking] Patched {relativePath}");
            return true;
        }
        
        private bool PatchRuntimeAttributeData(string relativePath, string content)
        {
            // Add using statements and patch marker
            string originalUsings = @"using System;
using GameCreator.Runtime.Common;
using UnityEngine;

namespace GameCreator.Runtime.Stats
{
    public class RuntimeAttributeData
    {";

            string patchedUsings = @"using System;
using GameCreator.Runtime.Common;
using UnityEngine;

" + PatchMarker + @"
// This file has been patched for GC2 Networking server authority.
// Do not modify the patched sections manually.
// Use Tools > Game Creator 2 Networking > Patches > Stats > Unpatch to restore.

namespace GameCreator.Runtime.Stats
{
    public class RuntimeAttributeData
    {
        // [GC2_NETWORK_PATCH] Static hooks for server-authoritative networking
        public static Func<RuntimeAttributeData, double, bool> NetworkValueValidator;
        public static bool IsNetworkingActive => NetworkValueValidator != null;
        // [GC2_NETWORK_PATCH_END]
";

            if (!content.Contains(originalUsings))
            {
                Debug.LogError("[GC2 Networking] Could not find expected using statements in RuntimeAttributeData.cs.");
                return false;
            }
            
            content = content.Replace(originalUsings, patchedUsings);
            
            // Patch the Value property - exact pattern from actual file
            string originalValueProperty = @"        public double Value
        {
            get => this.m_Value;
            set
            {
                double oldValue = this.Value;
                double newValue = Math.Clamp(value, this.MinValue, this.MaxValue);
                if (Math.Abs(this.m_Value - newValue) < float.Epsilon) return;

                this.m_Value = newValue;
                this.EventChange?.Invoke(this.m_Attribute.ID, newValue - oldValue);
            }
        }";
            
            string patchedValueProperty = @"        public double Value
        {
            get => this.m_Value;
            set
            {
                double oldValue = this.Value;
                double newValue = Math.Clamp(value, this.MinValue, this.MaxValue);
                if (Math.Abs(this.m_Value - newValue) < float.Epsilon) return;
                
                // [GC2_NETWORK_PATCH] Server authority check
                if (NetworkValueValidator != null && !NetworkValueValidator.Invoke(this, newValue))
                {
                    return; // Network will handle this change
                }
                // [GC2_NETWORK_PATCH_END]

                this.m_Value = newValue;
                this.EventChange?.Invoke(this.m_Attribute.ID, newValue - oldValue);
            }
        }
        
        // [GC2_NETWORK_PATCH] Server-side direct value setter (bypasses validation)
        public void SetValueDirect(double value)
        {
            double oldValue = this.Value;
            double newValue = Math.Clamp(value, this.MinValue, this.MaxValue);
            if (Math.Abs(this.m_Value - newValue) < float.Epsilon) return;
            this.m_Value = newValue;
            this.EventChange?.Invoke(this.m_Attribute.ID, newValue - oldValue);
        }
        // [GC2_NETWORK_PATCH_END]";

            if (!content.Contains(originalValueProperty))
            {
                Debug.LogError("[GC2 Networking] Could not find expected Value property in RuntimeAttributeData.cs.");
                return false;
            }
            
            content = content.Replace(originalValueProperty, patchedValueProperty);
            
            WriteFile(relativePath, content);
            Debug.Log($"[GC2 Networking] Patched {relativePath}");
            return true;
        }
    }
}
