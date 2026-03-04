using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Arawn.EnemyMasses.Editor.Integration.GameCreator2.Patches
{
    /// <summary>
    /// Base class for all GC2 module patchers.
    /// Provides common functionality for patching, unpatching, and backup management.
    /// </summary>
    public abstract class GC2PatcherBase
    {
        // ════════════════════════════════════════════════════════════════════════════════════════
        // CONSTANTS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        protected const string PATCH_MARKER_PREFIX = "// [GC2_NETWORK_PATCH_";
        protected const string BACKUP_BASE_FOLDER = "Plugins/EnemyMasses/Editor/GameCreator2/Patches/Backups";
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // ABSTRACT MEMBERS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>Module name (e.g., "Core", "Stats", "Inventory").</summary>
        public abstract string ModuleName { get; }
        
        /// <summary>Patch version string.</summary>
        public abstract string PatchVersion { get; }
        
        /// <summary>Relative path from Assets folder to the main file to patch.</summary>
        protected abstract string[] FilesToPatch { get; }
        
        /// <summary>Display name for menu and dialogs.</summary>
        public abstract string DisplayName { get; }
        
        /// <summary>Description of what the patch does.</summary>
        public abstract string PatchDescription { get; }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // PROPERTIES
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        protected string PatchMarker => $"{PATCH_MARKER_PREFIX}{ModuleName}_v1]";
        protected string BackupFolder => Path.Combine(BACKUP_BASE_FOLDER, ModuleName);
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // PUBLIC API
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Check if the module files exist.
        /// </summary>
        public bool ValidateFilesExist()
        {
            foreach (var relativePath in FilesToPatch)
            {
                string fullPath = Path.Combine(Application.dataPath, relativePath);
                if (!File.Exists(fullPath))
                {
                    return false;
                }
            }
            return true;
        }
        
        /// <summary>
        /// Check if the module is already patched.
        /// </summary>
        public bool IsPatched()
        {
            if (FilesToPatch.Length == 0) return false;

            bool hasRequiredPatchedFile = false;

            foreach (var relativePath in FilesToPatch)
            {
                if (!ShouldRequirePatchMarker(relativePath)) continue;

                string fullPath = Path.Combine(Application.dataPath, relativePath);
                if (!File.Exists(fullPath)) return false;

                string content = NormalizeLineEndings(File.ReadAllText(fullPath));
                if (!content.Contains(PatchMarker))
                {
                    return false;
                }

                hasRequiredPatchedFile = true;
                if (!VerifyPatchedFile(relativePath, content, out _))
                {
                    return false;
                }
            }

            return hasRequiredPatchedFile;
        }
        
        /// <summary>
        /// Check if backups exist.
        /// </summary>
        public bool HasBackups()
        {
            string backupFolder = Path.Combine(Application.dataPath, BackupFolder);
            if (!Directory.Exists(backupFolder)) return false;
            
            foreach (var relativePath in FilesToPatch)
            {
                string fileName = Path.GetFileName(relativePath);
                string backupPath = Path.Combine(backupFolder, fileName + ".backup");
                if (!File.Exists(backupPath)) return false;
            }
            
            return true;
        }
        
        /// <summary>
        /// Apply the patch.
        /// </summary>
        public bool ApplyPatch()
        {
            try
            {
                // Create backups first
                CreateBackups();
                
                // Apply patches to each file
                foreach (var relativePath in FilesToPatch)
                {
                    if (!PatchFile(relativePath))
                    {
                        // Rollback on failure
                        RestoreFromBackups();
                        return false;
                    }

                    if (!VerifyPatchedOutput(relativePath, out string verifyError))
                    {
                        Debug.LogError($"[GC2 Networking] Patch verification failed for {relativePath}: {verifyError}");
                        RestoreFromBackups();
                        return false;
                    }
                }
                
                // Save patch info
                SavePatchInfo();
                
                Debug.Log($"[GC2 Networking] {ModuleName} patch applied successfully.");
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"[GC2 Networking] Failed to patch {ModuleName}: {e}");
                
                // Try to rollback
                try { RestoreFromBackups(); } catch { }
                
                return false;
            }
        }
        
        /// <summary>
        /// Remove the patch and restore original files.
        /// </summary>
        public bool RemovePatch()
        {
            try
            {
                return RestoreFromBackups();
            }
            catch (Exception e)
            {
                Debug.LogError($"[GC2 Networking] Failed to unpatch {ModuleName}: {e}");
                return false;
            }
        }
        
        /// <summary>
        /// Get patch status information.
        /// </summary>
        public PatchStatus GetStatus()
        {
            return new PatchStatus
            {
                ModuleName = ModuleName,
                DisplayName = DisplayName,
                IsInstalled = ValidateFilesExist(),
                IsPatched = IsPatched(),
                HasBackups = HasBackups(),
                PatchVersion = PatchVersion
            };
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // ABSTRACT PATCH IMPLEMENTATION
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Apply patch to a specific file. Override to implement patching logic.
        /// </summary>
        protected abstract bool PatchFile(string relativePath);

        /// <summary>
        /// Runtime/editor files can opt out of strict marker requirements.
        /// </summary>
        protected virtual bool ShouldRequirePatchMarker(string relativePath)
        {
            return !relativePath.Contains("/Editor/");
        }

        /// <summary>
        /// Verifies a patched file contains required markers and balanced patch sections.
        /// Override for module-specific hook-point verification.
        /// </summary>
        protected virtual bool VerifyPatchedFile(string relativePath, string content, out string failureReason)
        {
            if (ShouldRequirePatchMarker(relativePath) && !content.Contains(PatchMarker))
            {
                failureReason = "Patch marker missing.";
                return false;
            }

            bool hasStart = content.Contains("// [GC2_NETWORK_PATCH]");
            bool hasEnd = content.Contains("// [GC2_NETWORK_PATCH_END]");
            if (hasStart != hasEnd)
            {
                failureReason = "Patch hook markers are unbalanced.";
                return false;
            }

            failureReason = null;
            return true;
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // BACKUP MANAGEMENT
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        protected void CreateBackups()
        {
            string backupFolder = Path.Combine(Application.dataPath, BackupFolder);
            Directory.CreateDirectory(backupFolder);
            
            foreach (var relativePath in FilesToPatch)
            {
                string fullPath = Path.Combine(Application.dataPath, relativePath);
                string fileName = Path.GetFileName(relativePath);
                string backupPath = Path.Combine(backupFolder, fileName + ".backup");
                
                if (File.Exists(fullPath))
                {
                    File.Copy(fullPath, backupPath, overwrite: true);
                    Debug.Log($"[GC2 Networking] Backed up {fileName}");
                }
            }
        }
        
        protected bool RestoreFromBackups()
        {
            string backupFolder = Path.Combine(Application.dataPath, BackupFolder);
            if (!Directory.Exists(backupFolder)) return false;
            
            bool anyRestored = false;
            
            foreach (var relativePath in FilesToPatch)
            {
                string fullPath = Path.Combine(Application.dataPath, relativePath);
                string fileName = Path.GetFileName(relativePath);
                string backupPath = Path.Combine(backupFolder, fileName + ".backup");
                
                if (File.Exists(backupPath))
                {
                    File.Copy(backupPath, fullPath, overwrite: true);
                    Debug.Log($"[GC2 Networking] Restored {fileName} from backup");
                    anyRestored = true;
                }
            }
            
            return anyRestored;
        }
        
        protected void SavePatchInfo()
        {
            string backupFolder = Path.Combine(Application.dataPath, BackupFolder);
            Directory.CreateDirectory(backupFolder);
            
            string patchInfoPath = Path.Combine(backupFolder, "patch_info.txt");
            File.WriteAllText(patchInfoPath,
                $"Module: {ModuleName}\n" +
                $"Patch Version: {PatchVersion}\n" +
                $"Patch Date: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
                $"Unity Version: {Application.unityVersion}\n");
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // HELPER METHODS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        protected string ReadFile(string relativePath)
        {
            string fullPath = Path.Combine(Application.dataPath, relativePath);
            string content = File.ReadAllText(fullPath);
            return NormalizeLineEndings(content);
        }

        private bool VerifyPatchedOutput(string relativePath, out string failureReason)
        {
            if (!ShouldRequirePatchMarker(relativePath))
            {
                failureReason = null;
                return true;
            }

            string fullPath = Path.Combine(Application.dataPath, relativePath);
            if (!File.Exists(fullPath))
            {
                failureReason = "Patched file missing after write.";
                return false;
            }

            string content = NormalizeLineEndings(File.ReadAllText(fullPath));
            return VerifyPatchedFile(relativePath, content, out failureReason);
        }
        
        protected void WriteFile(string relativePath, string content)
        {
            string fullPath = Path.Combine(Application.dataPath, relativePath);
            File.WriteAllText(fullPath, content);
        }
        
        /// <summary>
        /// Normalizes line endings to Unix style (LF) and removes BOM.
        /// This ensures pattern matching works regardless of file encoding.
        /// </summary>
        protected static string NormalizeLineEndings(string content)
        {
            // Remove BOM if present
            if (content.Length > 0 && content[0] == '\uFEFF')
            {
                content = content.Substring(1);
            }
            
            // Normalize CRLF to LF
            return content.Replace("\r\n", "\n").Replace("\r", "\n");
        }
        
        /// <summary>
        /// Add the patch marker comment to content.
        /// </summary>
        protected string AddPatchMarker(string content, string insertAfterPattern)
        {
            int insertIndex = content.IndexOf(insertAfterPattern);
            if (insertIndex < 0) return content;
            
            insertIndex += insertAfterPattern.Length;
            
            string markerComment = $"\n\n{PatchMarker}\n// This file has been patched for GC2 Networking server authority.\n// Do not modify the patched sections manually.\n// Use Tools > Game Creator 2 Networking > Patches > {ModuleName} > Unpatch to restore.\n";
            
            return content.Insert(insertIndex, markerComment);
        }
    }
    
    /// <summary>
    /// Patch status information.
    /// </summary>
    public struct PatchStatus
    {
        public string ModuleName;
        public string DisplayName;
        public bool IsInstalled;
        public bool IsPatched;
        public bool HasBackups;
        public string PatchVersion;
    }
}
