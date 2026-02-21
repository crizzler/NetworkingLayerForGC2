using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Arawn.EnemyMasses.Editor.Integration.GameCreator2.Patches
{
    /// <summary>
    /// Unified patch manager for all GC2 networking modules.
    /// Provides menu items and status window for managing patches.
    /// </summary>
    public static class GC2PatchManager
    {
        // ════════════════════════════════════════════════════════════════════════════════════════
        // PATCHER REGISTRY
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        private static readonly Dictionary<string, GC2PatcherBase> s_Patchers = new()
        {
            { "Core", new CorePatcher() },
            { "Abilities", new AbilitiesPatcherImpl() },
            { "Stats", new StatsPatcher() },
            { "Inventory", new InventoryPatcher() },
            { "Melee", new MeleePatcher() },
            { "Shooter", new ShooterPatcher() }
        };
        
        /// <summary>
        /// Get all registered patchers.
        /// </summary>
        public static IEnumerable<GC2PatcherBase> GetAllPatchers() => s_Patchers.Values;
        
        /// <summary>
        /// Get a specific patcher by module name.
        /// </summary>
        public static GC2PatcherBase GetPatcher(string moduleName)
        {
            return s_Patchers.TryGetValue(moduleName, out var patcher) ? patcher : null;
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // STATUS WINDOW
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        [MenuItem("Tools/Game Creator 2 Networking/Patches/Status Overview...", false, 50)]
        public static void ShowStatusWindow()
        {
            PatchStatusWindow.ShowWindow();
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // CORE MENU ITEMS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        [MenuItem("Tools/Game Creator 2 Networking/Patches/Core/Patch (Server Authority)", false, 90)]
        public static void PatchCore() => ApplyPatch("Core");
        
        [MenuItem("Tools/Game Creator 2 Networking/Patches/Core/Unpatch", false, 91)]
        public static void UnpatchCore() => RemovePatch("Core");
        
        [MenuItem("Tools/Game Creator 2 Networking/Patches/Core/Check Status", false, 92)]
        public static void CheckCoreStatus() => ShowStatus("Core");
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // ABILITIES MENU ITEMS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        [MenuItem("Tools/Game Creator 2 Networking/Patches/Abilities/Patch (Server Authority)", false, 100)]
        public static void PatchAbilities() => ApplyPatch("Abilities");
        
        [MenuItem("Tools/Game Creator 2 Networking/Patches/Abilities/Unpatch", false, 101)]
        public static void UnpatchAbilities() => RemovePatch("Abilities");
        
        [MenuItem("Tools/Game Creator 2 Networking/Patches/Abilities/Check Status", false, 102)]
        public static void CheckAbilitiesStatus() => ShowStatus("Abilities");
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // STATS MENU ITEMS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        [MenuItem("Tools/Game Creator 2 Networking/Patches/Stats/Patch (Server Authority)", false, 110)]
        public static void PatchStats() => ApplyPatch("Stats");
        
        [MenuItem("Tools/Game Creator 2 Networking/Patches/Stats/Unpatch", false, 111)]
        public static void UnpatchStats() => RemovePatch("Stats");
        
        [MenuItem("Tools/Game Creator 2 Networking/Patches/Stats/Check Status", false, 112)]
        public static void CheckStatsStatus() => ShowStatus("Stats");
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // INVENTORY MENU ITEMS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        [MenuItem("Tools/Game Creator 2 Networking/Patches/Inventory/Patch (Server Authority)", false, 120)]
        public static void PatchInventory() => ApplyPatch("Inventory");
        
        [MenuItem("Tools/Game Creator 2 Networking/Patches/Inventory/Unpatch", false, 121)]
        public static void UnpatchInventory() => RemovePatch("Inventory");
        
        [MenuItem("Tools/Game Creator 2 Networking/Patches/Inventory/Check Status", false, 122)]
        public static void CheckInventoryStatus() => ShowStatus("Inventory");
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // MELEE MENU ITEMS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        [MenuItem("Tools/Game Creator 2 Networking/Patches/Melee/Patch (Server Authority)", false, 130)]
        public static void PatchMelee() => ApplyPatch("Melee");
        
        [MenuItem("Tools/Game Creator 2 Networking/Patches/Melee/Unpatch", false, 131)]
        public static void UnpatchMelee() => RemovePatch("Melee");
        
        [MenuItem("Tools/Game Creator 2 Networking/Patches/Melee/Check Status", false, 132)]
        public static void CheckMeleeStatus() => ShowStatus("Melee");
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // SHOOTER MENU ITEMS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        [MenuItem("Tools/Game Creator 2 Networking/Patches/Shooter/Patch (Server Authority)", false, 140)]
        public static void PatchShooter() => ApplyPatch("Shooter");
        
        [MenuItem("Tools/Game Creator 2 Networking/Patches/Shooter/Unpatch", false, 141)]
        public static void UnpatchShooter() => RemovePatch("Shooter");
        
        [MenuItem("Tools/Game Creator 2 Networking/Patches/Shooter/Check Status", false, 142)]
        public static void CheckShooterStatus() => ShowStatus("Shooter");
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // COMMON OPERATIONS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        private static void ApplyPatch(string moduleName)
        {
            var patcher = GetPatcher(moduleName);
            if (patcher == null)
            {
                EditorUtility.DisplayDialog("Error", $"Patcher for {moduleName} not found.", "OK");
                return;
            }
            
            if (!patcher.ValidateFilesExist())
            {
                EditorUtility.DisplayDialog(
                    $"{patcher.DisplayName} Not Found",
                    $"Could not find Game Creator 2 {patcher.DisplayName} package.\n\n" +
                    $"Please ensure the package is installed.",
                    "OK");
                return;
            }
            
            if (patcher.IsPatched())
            {
                EditorUtility.DisplayDialog(
                    "Already Patched",
                    $"The {patcher.DisplayName} system has already been patched.\n\n" +
                    $"If you want to re-apply the patch, please unpatch first.",
                    "OK");
                return;
            }
            
            bool confirm = EditorUtility.DisplayDialog(
                $"Patch {patcher.DisplayName} for Server Authority",
                $"{patcher.PatchDescription}\n\n" +
                "Changes:\n" +
                "• Adds network validation hooks to core methods\n" +
                "• Clients cannot bypass validation by calling methods directly\n" +
                "• A backup will be created before patching\n\n" +
                "This patch is OPTIONAL. Without it, the networking solution uses\n" +
                "interception-based validation which is less secure but doesn't\n" +
                "modify third-party code.\n\n" +
                "Do you want to continue?",
                "Apply Patch",
                "Cancel");
            
            if (!confirm) return;
            
            bool success = patcher.ApplyPatch();
            
            if (success)
            {
                AssetDatabase.Refresh();
                
                EditorUtility.DisplayDialog(
                    "Patch Applied Successfully",
                    $"The {patcher.DisplayName} system has been patched.\n\n" +
                    "Backups have been saved. You can unpatch at any time using:\n" +
                    $"Tools > Game Creator 2 Networking > Patches > {moduleName} > Unpatch",
                    "OK");
            }
            else
            {
                EditorUtility.DisplayDialog(
                    "Patch Failed",
                    "Failed to apply the patch. The source files may have been\n" +
                    "modified in a way that's incompatible with this patcher.\n\n" +
                    "Please check the Console for details.",
                    "OK");
            }
        }
        
        private static void RemovePatch(string moduleName)
        {
            var patcher = GetPatcher(moduleName);
            if (patcher == null)
            {
                EditorUtility.DisplayDialog("Error", $"Patcher for {moduleName} not found.", "OK");
                return;
            }
            
            if (!patcher.IsPatched())
            {
                EditorUtility.DisplayDialog(
                    "Not Patched",
                    $"The {patcher.DisplayName} system is not currently patched.",
                    "OK");
                return;
            }
            
            bool confirm = EditorUtility.DisplayDialog(
                $"Unpatch {patcher.DisplayName}",
                $"This will restore the original Game Creator 2 {patcher.DisplayName} source code from backups.\n\n" +
                "Do you want to continue?",
                "Restore Original",
                "Cancel");
            
            if (!confirm) return;
            
            bool success = patcher.RemovePatch();
            
            if (success)
            {
                AssetDatabase.Refresh();
                
                EditorUtility.DisplayDialog(
                    "Unpatch Successful",
                    $"The {patcher.DisplayName} system has been restored to its original state.\n\n" +
                    "The networking solution will now use interception-based validation.",
                    "OK");
            }
            else
            {
                EditorUtility.DisplayDialog(
                    "Restore Failed",
                    "Could not find backup files to restore.\n\n" +
                    $"You may need to reinstall the Game Creator 2 {patcher.DisplayName} package.",
                    "OK");
            }
        }
        
        private static void ShowStatus(string moduleName)
        {
            var patcher = GetPatcher(moduleName);
            if (patcher == null)
            {
                EditorUtility.DisplayDialog("Error", $"Patcher for {moduleName} not found.", "OK");
                return;
            }
            
            var status = patcher.GetStatus();
            
            string installed = status.IsInstalled ? "✓ Installed" : "✗ Not Found";
            string patched = status.IsPatched ? "✓ PATCHED" : "○ Not Patched";
            string backups = status.HasBackups ? "✓ Available" : "○ None";
            
            EditorUtility.DisplayDialog(
                $"{status.DisplayName} Patch Status",
                $"Package: {installed}\n" +
                $"Patch Status: {patched}\n" +
                $"Backups: {backups}\n" +
                $"Patch Version: {status.PatchVersion}\n\n" +
                (status.IsPatched
                    ? "Server-authoritative networking hooks are active."
                    : "Using interception-based validation."),
                "OK");
        }
    }
    
    /// <summary>
    /// Editor window showing status of all patches.
    /// </summary>
    public class PatchStatusWindow : EditorWindow
    {
        private Vector2 m_ScrollPosition;
        
        public static void ShowWindow()
        {
            var window = GetWindow<PatchStatusWindow>("GC2 Patch Status");
            window.minSize = new Vector2(400, 300);
            window.Show();
        }
        
        private void OnGUI()
        {
            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("Game Creator 2 Networking Patches", EditorStyles.boldLabel);
            EditorGUILayout.Space(5);
            
            EditorGUILayout.HelpBox(
                "These optional patches add server-authoritative hooks directly into GC2 source code. " +
                "This provides enhanced security but modifies third-party code.\n\n" +
                "Without patches, the networking solution uses interception-based validation.",
                MessageType.Info);
            
            EditorGUILayout.Space(10);
            
            m_ScrollPosition = EditorGUILayout.BeginScrollView(m_ScrollPosition);
            
            foreach (var patcher in GC2PatchManager.GetAllPatchers())
            {
                var status = patcher.GetStatus();
                DrawModuleStatus(patcher.ModuleName, status);
                EditorGUILayout.Space(5);
            }
            
            EditorGUILayout.EndScrollView();
            
            EditorGUILayout.Space(10);
            
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Refresh", GUILayout.Height(25)))
            {
                Repaint();
            }
            EditorGUILayout.EndHorizontal();
        }
        
        private void DrawModuleStatus(string moduleName, PatchStatus status)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            
            EditorGUILayout.BeginHorizontal();
            
            // Status icon
            string icon = !status.IsInstalled ? "⚠" : (status.IsPatched ? "✓" : "○");
            Color color = !status.IsInstalled ? Color.yellow : (status.IsPatched ? Color.green : Color.gray);
            
            var oldColor = GUI.color;
            GUI.color = color;
            EditorGUILayout.LabelField(icon, GUILayout.Width(20));
            GUI.color = oldColor;
            
            // Module name
            EditorGUILayout.LabelField(status.DisplayName, EditorStyles.boldLabel);
            
            GUILayout.FlexibleSpace();
            
            // Status text
            string statusText = !status.IsInstalled ? "Not Installed" : (status.IsPatched ? "Patched" : "Not Patched");
            EditorGUILayout.LabelField(statusText, GUILayout.Width(100));
            
            EditorGUILayout.EndHorizontal();
            
            if (status.IsInstalled)
            {
                EditorGUILayout.BeginHorizontal();
                GUILayout.Space(25);
                
                GUI.enabled = !status.IsPatched;
                if (GUILayout.Button("Patch", GUILayout.Width(80)))
                {
                    var method = typeof(GC2PatchManager).GetMethod($"Patch{moduleName}");
                    method?.Invoke(null, null);
                    Repaint();
                }
                
                GUI.enabled = status.IsPatched;
                if (GUILayout.Button("Unpatch", GUILayout.Width(80)))
                {
                    var method = typeof(GC2PatchManager).GetMethod($"Unpatch{moduleName}");
                    method?.Invoke(null, null);
                    Repaint();
                }
                
                GUI.enabled = true;
                
                EditorGUILayout.EndHorizontal();
            }
            
            EditorGUILayout.EndVertical();
        }
    }
}
