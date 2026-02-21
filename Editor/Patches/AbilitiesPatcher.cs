using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace Arawn.EnemyMasses.Editor.Integration.GameCreator2.Patches
{
    /// <summary>
    /// Patcher for DaimahouGames Abilities system to enable true server-authoritative networking.
    /// This patches the third-party code to add server authority hooks.
    /// Users must apply this patch manually - it is not applied automatically.
    /// </summary>
    public static class AbilitiesPatcher
    {
        // ════════════════════════════════════════════════════════════════════════════════════════
        // CONSTANTS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        private const string PATCH_MARKER = "// [GC2_NETWORK_PATCH_v1]";
        private const string PATCH_VERSION = "1.0.0";
        
        // Relative paths from Assets folder
        private const string CASTER_PATH = "Plugins/DaimahouGames/Packages/Abilities/Runtime/Pawns/Features/Caster.cs";
        private const string RUNTIME_ABILITY_PATH = "Plugins/DaimahouGames/Packages/Abilities/Runtime/Classes/RuntimeAbility.cs";
        
        // Backup folder
        private const string BACKUP_FOLDER = "Plugins/EnemyMasses/Editor/GameCreator2/Patches/Backups";
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // PUBLIC METHODS (Menu items are in GC2PatchManager)
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        public static void PatchAbilities()
        {
            if (!ValidateAbilitiesExist())
            {
                EditorUtility.DisplayDialog(
                    "Abilities Not Found",
                    "Could not find DaimahouGames Abilities package.\n\n" +
                    "Please ensure the Abilities package is installed at:\n" +
                    "Assets/Plugins/DaimahouGames/Packages/Abilities/",
                    "OK");
                return;
            }
            
            if (IsPatched())
            {
                EditorUtility.DisplayDialog(
                    "Already Patched",
                    "The Abilities system has already been patched for server authority.\n\n" +
                    "If you want to re-apply the patch, please unpatch first using:\n" +
                    "Tools > Game Creator 2 Networking > Patches > Unpatch Abilities",
                    "OK");
                return;
            }
            
            bool confirm = EditorUtility.DisplayDialog(
                "Patch Abilities for Server Authority",
                "This will modify the DaimahouGames Abilities source code to add server-authoritative networking hooks.\n\n" +
                "Changes:\n" +
                "• Caster.Cast() will check for network authority before executing\n" +
                "• Learn/UnLearn methods will have network hooks\n" +
                "• A backup will be created before patching\n\n" +
                "This patch is OPTIONAL. Without it, the networking solution uses interception-based validation " +
                "which is less secure but doesn't modify third-party code.\n\n" +
                "Do you want to continue?",
                "Apply Patch",
                "Cancel");
            
            if (!confirm) return;
            
            try
            {
                // Create backups first
                CreateBackups();
                
                // Apply patches
                bool casterPatched = PatchCaster();
                
                if (casterPatched)
                {
                    AssetDatabase.Refresh();
                    
                    EditorUtility.DisplayDialog(
                        "Patch Applied Successfully",
                        "The Abilities system has been patched for server authority.\n\n" +
                        "Backups have been saved to:\n" +
                        $"Assets/{BACKUP_FOLDER}/\n\n" +
                        "You can unpatch at any time using:\n" +
                        "Tools > Game Creator 2 Networking > Patches > Unpatch Abilities",
                        "OK");
                    
                    Debug.Log("[GC2 Networking] Abilities patch applied successfully.");
                }
                else
                {
                    EditorUtility.DisplayDialog(
                        "Patch Failed",
                        "Failed to apply the patch. The source files may have been modified " +
                        "in a way that's incompatible with this patcher.\n\n" +
                        "Please check the Console for details.",
                        "OK");
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[GC2 Networking] Failed to patch Abilities: {e}");
                EditorUtility.DisplayDialog(
                    "Patch Error",
                    $"An error occurred while patching:\n\n{e.Message}\n\n" +
                    "Check the Console for details.",
                    "OK");
            }
        }
        
        public static void UnpatchAbilities()
        {
            if (!IsPatched())
            {
                EditorUtility.DisplayDialog(
                    "Not Patched",
                    "The Abilities system is not currently patched.",
                    "OK");
                return;
            }
            
            bool confirm = EditorUtility.DisplayDialog(
                "Unpatch Abilities",
                "This will restore the original DaimahouGames Abilities source code from backups.\n\n" +
                "Do you want to continue?",
                "Restore Original",
                "Cancel");
            
            if (!confirm) return;
            
            try
            {
                bool restored = RestoreFromBackups();
                
                if (restored)
                {
                    AssetDatabase.Refresh();
                    
                    EditorUtility.DisplayDialog(
                        "Unpatch Successful",
                        "The Abilities system has been restored to its original state.\n\n" +
                        "The networking solution will now use interception-based validation.",
                        "OK");
                    
                    Debug.Log("[GC2 Networking] Abilities patch removed, original files restored.");
                }
                else
                {
                    EditorUtility.DisplayDialog(
                        "Restore Failed",
                        "Could not find backup files to restore.\n\n" +
                        "You may need to reinstall the DaimahouGames Abilities package.",
                        "OK");
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[GC2 Networking] Failed to unpatch Abilities: {e}");
                EditorUtility.DisplayDialog(
                    "Unpatch Error",
                    $"An error occurred while unpatching:\n\n{e.Message}",
                    "OK");
            }
        }
        
        public static void CheckPatchStatus()
        {
            if (!ValidateAbilitiesExist())
            {
                EditorUtility.DisplayDialog(
                    "Status: Not Installed",
                    "DaimahouGames Abilities package is not installed.",
                    "OK");
                return;
            }
            
            bool patched = IsPatched();
            bool hasBackups = HasBackups();
            
            string status = patched ? "PATCHED" : "NOT PATCHED";
            string backupStatus = hasBackups ? "Backups available" : "No backups found";
            
            EditorUtility.DisplayDialog(
                $"Patch Status: {status}",
                $"DaimahouGames Abilities: {status}\n" +
                $"Backup Status: {backupStatus}\n\n" +
                (patched 
                    ? "Server-authoritative networking hooks are active.\n" +
                      "Ability casts are validated on the server before execution."
                    : "Using interception-based validation.\n" +
                      "Apply the patch for enhanced security."),
                "OK");
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // VALIDATION
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        private static bool ValidateAbilitiesExist()
        {
            string casterFullPath = Path.Combine(Application.dataPath, CASTER_PATH);
            return File.Exists(casterFullPath);
        }
        
        public static bool IsPatched()
        {
            string casterFullPath = Path.Combine(Application.dataPath, CASTER_PATH);
            if (!File.Exists(casterFullPath)) return false;
            
            string content = File.ReadAllText(casterFullPath);
            return content.Contains(PATCH_MARKER);
        }
        
        private static bool HasBackups()
        {
            string backupFolder = Path.Combine(Application.dataPath, BACKUP_FOLDER);
            string casterBackup = Path.Combine(backupFolder, "Caster.cs.backup");
            return File.Exists(casterBackup);
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // BACKUP & RESTORE
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        private static void CreateBackups()
        {
            string backupFolder = Path.Combine(Application.dataPath, BACKUP_FOLDER);
            Directory.CreateDirectory(backupFolder);
            
            // Backup Caster.cs
            string casterFullPath = Path.Combine(Application.dataPath, CASTER_PATH);
            string casterBackup = Path.Combine(backupFolder, "Caster.cs.backup");
            
            if (File.Exists(casterFullPath))
            {
                File.Copy(casterFullPath, casterBackup, overwrite: true);
                Debug.Log($"[GC2 Networking] Backed up Caster.cs to {casterBackup}");
            }
            
            // Save patch info
            string patchInfoPath = Path.Combine(backupFolder, "patch_info.txt");
            File.WriteAllText(patchInfoPath, 
                $"Patch Version: {PATCH_VERSION}\n" +
                $"Patch Date: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
                $"Unity Version: {Application.unityVersion}\n");
        }
        
        private static bool RestoreFromBackups()
        {
            string backupFolder = Path.Combine(Application.dataPath, BACKUP_FOLDER);
            
            // Restore Caster.cs
            string casterFullPath = Path.Combine(Application.dataPath, CASTER_PATH);
            string casterBackup = Path.Combine(backupFolder, "Caster.cs.backup");
            
            if (File.Exists(casterBackup))
            {
                File.Copy(casterBackup, casterFullPath, overwrite: true);
                Debug.Log($"[GC2 Networking] Restored Caster.cs from backup");
                return true;
            }
            
            return false;
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // PATCHING
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        private static bool PatchCaster()
        {
            string casterFullPath = Path.Combine(Application.dataPath, CASTER_PATH);
            string content = File.ReadAllText(casterFullPath);
            
            // Normalize line endings to Unix style for pattern matching
            content = NormalizeLineEndings(content);
            
            StringBuilder sb = new StringBuilder();
            
            // Add patch marker and network hooks at the top
            string patchedUsings = @"using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DaimahouGames.Runtime.Core;
using DaimahouGames.Runtime.Core.Common;
using DaimahouGames.Runtime.Pawns;
using GameCreator.Runtime.Common;
using UnityEngine;

" + PATCH_MARKER + @"
// This file has been patched for GC2 Networking server authority.
// Do not modify the patched sections manually.
// Use Tools > Game Creator 2 Networking > Patches > Unpatch Abilities to restore.

namespace DaimahouGames.Runtime.Abilities
{";

            // Replace the original namespace declaration with patched version
            string originalUsings = @"using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DaimahouGames.Runtime.Core;
using DaimahouGames.Runtime.Core.Common;
using DaimahouGames.Runtime.Pawns;
using GameCreator.Runtime.Common;
using UnityEngine;

namespace DaimahouGames.Runtime.Abilities
{";

            if (!content.Contains(originalUsings))
            {
                Debug.LogError("[GC2 Networking] Could not find expected using statements in Caster.cs. The file may have been modified.");
                return false;
            }
            
            content = content.Replace(originalUsings, patchedUsings);
            
            // Add static network authority hooks after the class declaration
            string classDeclaration = @"    [Serializable]
    public sealed class Caster : Feature
    {
        //============================================================================================================||";
            
            string patchedClassDeclaration = @"    [Serializable]
    public sealed class Caster : Feature
    {
        //============================================================================================================||
        
        // [GC2_NETWORK_PATCH] Static hooks for server-authoritative networking
        // These are set by NetworkAbilitiesController when networking is active
        
        /// <summary>
        /// When set, this delegate is called to validate if a cast should proceed.
        /// Return true to allow local execution, false to block and wait for server.
        /// </summary>
        public static Func<Caster, Ability, ExtendedArgs, bool> NetworkCastValidator;
        
        /// <summary>
        /// When set, this delegate is called before Learn executes.
        /// Return true to allow local execution, false to block and wait for server.
        /// </summary>
        public static Func<Caster, Ability, int, bool> NetworkLearnValidator;
        
        /// <summary>
        /// When set, this delegate is called before UnLearn executes.
        /// Return true to allow local execution, false to block and wait for server.
        /// </summary>
        public static Func<Caster, Ability, bool> NetworkUnLearnValidator;
        
        /// <summary>
        /// When set, this delegate is called after a cast completes (success or failure).
        /// </summary>
        public static Action<Caster, Ability, bool> NetworkCastCompleted;
        
        /// <summary>
        /// Returns true if networking hooks are active.
        /// </summary>
        public static bool IsNetworkingActive => NetworkCastValidator != null;
        
        // [GC2_NETWORK_PATCH_END]";
            
            if (!content.Contains(classDeclaration))
            {
                Debug.LogError("[GC2 Networking] Could not find expected class declaration in Caster.cs. The file may have been modified.");
                return false;
            }
            
            content = content.Replace(classDeclaration, patchedClassDeclaration);
            
            // Patch the Cast method
            string originalCastMethod = @"        public async Task<bool> Cast(Ability ability, ExtendedArgs args)
        {
            if (!CanCancel()) return false;

            CastAbilityMessage.Send(ability);
            
            args.ChangeSelf(GameObject);
            args.Set(new AbiltySource(GameObject));
            args.Set(GetRuntimeAbility(ability));

            var success = m_CastState.TryEnter(args);

            await m_CastState.WaitUntilComplete();
            return success;
        }";
            
            string patchedCastMethod = @"        public async Task<bool> Cast(Ability ability, ExtendedArgs args)
        {
            if (!CanCancel()) return false;
            
            // [GC2_NETWORK_PATCH] Server authority check
            if (NetworkCastValidator != null)
            {
                bool allowLocalExecution = NetworkCastValidator.Invoke(this, ability, args);
                if (!allowLocalExecution)
                {
                    // Networking will handle this cast - don't execute locally
                    return false;
                }
            }
            // [GC2_NETWORK_PATCH_END]

            CastAbilityMessage.Send(ability);
            
            args.ChangeSelf(GameObject);
            args.Set(new AbiltySource(GameObject));
            args.Set(GetRuntimeAbility(ability));

            var success = m_CastState.TryEnter(args);

            await m_CastState.WaitUntilComplete();
            
            // [GC2_NETWORK_PATCH] Notify completion
            NetworkCastCompleted?.Invoke(this, ability, success);
            // [GC2_NETWORK_PATCH_END]
            
            return success;
        }";
            
            if (!content.Contains(originalCastMethod))
            {
                Debug.LogError("[GC2 Networking] Could not find expected Cast method in Caster.cs. The file may have been modified.");
                return false;
            }
            
            content = content.Replace(originalCastMethod, patchedCastMethod);
            
            // Patch the Learn method
            string originalLearnMethod = @"        public void Learn(Ability ability, int slot)
        {
            if (ability == null) return;
            if (slot < 0 || slot >= m_AbilitySlots.Count) return;
            if (this.m_AbilitySlots[slot].Ability == ability) return;

            m_AbilitySlots[slot] = new KnownAbility(ability);
            LearnAbilityMessage.Send(ability);
        }";
            
            string patchedLearnMethod = @"        public void Learn(Ability ability, int slot)
        {
            if (ability == null) return;
            if (slot < 0 || slot >= m_AbilitySlots.Count) return;
            if (this.m_AbilitySlots[slot].Ability == ability) return;
            
            // [GC2_NETWORK_PATCH] Server authority check
            if (NetworkLearnValidator != null)
            {
                bool allowLocalExecution = NetworkLearnValidator.Invoke(this, ability, slot);
                if (!allowLocalExecution)
                {
                    // Networking will handle this - don't execute locally
                    return;
                }
            }
            // [GC2_NETWORK_PATCH_END]

            m_AbilitySlots[slot] = new KnownAbility(ability);
            LearnAbilityMessage.Send(ability);
        }
        
        // [GC2_NETWORK_PATCH] Server-side direct learn (bypasses validation)
        /// <summary>
        /// Directly learns an ability without network validation.
        /// Only call this from server-side code or when applying server state.
        /// </summary>
        public void LearnDirect(Ability ability, int slot)
        {
            if (ability == null) return;
            if (slot < 0 || slot >= m_AbilitySlots.Count) return;

            m_AbilitySlots[slot] = new KnownAbility(ability);
            LearnAbilityMessage.Send(ability);
        }
        // [GC2_NETWORK_PATCH_END]";
            
            if (!content.Contains(originalLearnMethod))
            {
                Debug.LogError("[GC2 Networking] Could not find expected Learn method in Caster.cs. The file may have been modified.");
                return false;
            }
            
            content = content.Replace(originalLearnMethod, patchedLearnMethod);
            
            // Patch the UnLearn(Ability) method
            string originalUnLearnMethod = @"        public void UnLearn(Ability ability)
        {
            if (ability == null) return;

            var slot = this.m_AbilitySlots.FindIndex(x => x.Ability == ability);
            if (slot < 0) return;
            
            if (this.m_AbilitySlots[slot].Ability != ability) return;
            
            this.m_AbilitySlots[slot] = KnownAbility.None;
            UnLearnAbilityMessage.Send(ability);
        }";
            
            string patchedUnLearnMethod = @"        public void UnLearn(Ability ability)
        {
            if (ability == null) return;

            var slot = this.m_AbilitySlots.FindIndex(x => x.Ability == ability);
            if (slot < 0) return;
            
            if (this.m_AbilitySlots[slot].Ability != ability) return;
            
            // [GC2_NETWORK_PATCH] Server authority check
            if (NetworkUnLearnValidator != null)
            {
                bool allowLocalExecution = NetworkUnLearnValidator.Invoke(this, ability);
                if (!allowLocalExecution)
                {
                    // Networking will handle this - don't execute locally
                    return;
                }
            }
            // [GC2_NETWORK_PATCH_END]
            
            this.m_AbilitySlots[slot] = KnownAbility.None;
            UnLearnAbilityMessage.Send(ability);
        }
        
        // [GC2_NETWORK_PATCH] Server-side direct unlearn (bypasses validation)
        /// <summary>
        /// Directly unlearns an ability without network validation.
        /// Only call this from server-side code or when applying server state.
        /// </summary>
        public void UnLearnDirect(Ability ability)
        {
            if (ability == null) return;

            var slot = this.m_AbilitySlots.FindIndex(x => x.Ability == ability);
            if (slot < 0) return;
            
            this.m_AbilitySlots[slot] = KnownAbility.None;
            UnLearnAbilityMessage.Send(ability);
        }
        // [GC2_NETWORK_PATCH_END]";
            
            if (!content.Contains(originalUnLearnMethod))
            {
                Debug.LogError("[GC2 Networking] Could not find expected UnLearn method in Caster.cs. The file may have been modified.");
                return false;
            }
            
            content = content.Replace(originalUnLearnMethod, patchedUnLearnMethod);
            
            // Write the patched file
            File.WriteAllText(casterFullPath, content);
            Debug.Log("[GC2 Networking] Successfully patched Caster.cs");
            
            return true;
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // HELPERS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Normalizes line endings to Unix style (LF) and removes BOM.
        /// This ensures pattern matching works regardless of file encoding.
        /// </summary>
        private static string NormalizeLineEndings(string content)
        {
            // Remove BOM if present
            if (content.Length > 0 && content[0] == '\uFEFF')
            {
                content = content.Substring(1);
            }
            
            // Normalize CRLF to LF
            return content.Replace("\r\n", "\n").Replace("\r", "\n");
        }
    }
}
