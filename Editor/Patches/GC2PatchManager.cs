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
            { "Quests", new QuestsPatcher() },
            { "Dialogue", new DialoguePatcher() },
            { "Traversal", new TraversalPatcher() },
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
        
        [MenuItem("Game Creator/Networking Layer/Patches/Status Overview...", false, 50)]
        public static void ShowStatusWindow()
        {
            PatchStatusWindow.ShowWindow();
        }
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // CORE MENU ITEMS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        [MenuItem("Game Creator/Networking Layer/Patches/Core/Patch (Server Authority)", false, 90)]
        public static void PatchCore() => ApplyPatch("Core");
        
        [MenuItem("Game Creator/Networking Layer/Patches/Core/Unpatch", false, 91)]
        public static void UnpatchCore() => RemovePatch("Core");
        
        [MenuItem("Game Creator/Networking Layer/Patches/Core/Check Status", false, 92)]
        public static void CheckCoreStatus() => ShowStatus("Core");
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // ABILITIES MENU ITEMS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        [MenuItem("Game Creator/Networking Layer/Patches/Abilities/Patch (Server Authority)", false, 100)]
        public static void PatchAbilities() => ApplyPatch("Abilities");
        
        [MenuItem("Game Creator/Networking Layer/Patches/Abilities/Unpatch", false, 101)]
        public static void UnpatchAbilities() => RemovePatch("Abilities");
        
        [MenuItem("Game Creator/Networking Layer/Patches/Abilities/Check Status", false, 102)]
        public static void CheckAbilitiesStatus() => ShowStatus("Abilities");
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // STATS MENU ITEMS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        [MenuItem("Game Creator/Networking Layer/Patches/Stats/Patch (Server Authority)", false, 110)]
        public static void PatchStats() => ApplyPatch("Stats");
        
        [MenuItem("Game Creator/Networking Layer/Patches/Stats/Unpatch", false, 111)]
        public static void UnpatchStats() => RemovePatch("Stats");
        
        [MenuItem("Game Creator/Networking Layer/Patches/Stats/Check Status", false, 112)]
        public static void CheckStatsStatus() => ShowStatus("Stats");
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // INVENTORY MENU ITEMS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        [MenuItem("Game Creator/Networking Layer/Patches/Inventory/Patch (Server Authority)", false, 120)]
        public static void PatchInventory() => ApplyPatch("Inventory");
        
        [MenuItem("Game Creator/Networking Layer/Patches/Inventory/Unpatch", false, 121)]
        public static void UnpatchInventory() => RemovePatch("Inventory");
        
        [MenuItem("Game Creator/Networking Layer/Patches/Inventory/Check Status", false, 122)]
        public static void CheckInventoryStatus() => ShowStatus("Inventory");

        // ════════════════════════════════════════════════════════════════════════════════════════
        // QUESTS MENU ITEMS
        // ════════════════════════════════════════════════════════════════════════════════════════

        [MenuItem("Game Creator/Networking Layer/Patches/Quests/Patch (Server Authority)", false, 125)]
        public static void PatchQuests() => ApplyPatch("Quests");

        [MenuItem("Game Creator/Networking Layer/Patches/Quests/Unpatch", false, 126)]
        public static void UnpatchQuests() => RemovePatch("Quests");

        [MenuItem("Game Creator/Networking Layer/Patches/Quests/Check Status", false, 127)]
        public static void CheckQuestsStatus() => ShowStatus("Quests");

        // ════════════════════════════════════════════════════════════════════════════════════════
        // DIALOGUE MENU ITEMS
        // ════════════════════════════════════════════════════════════════════════════════════════

        [MenuItem("Game Creator/Networking Layer/Patches/Dialogue/Patch (Server Authority)", false, 128)]
        public static void PatchDialogue() => ApplyPatch("Dialogue");

        [MenuItem("Game Creator/Networking Layer/Patches/Dialogue/Unpatch", false, 129)]
        public static void UnpatchDialogue() => RemovePatch("Dialogue");

        [MenuItem("Game Creator/Networking Layer/Patches/Dialogue/Check Status", false, 130)]
        public static void CheckDialogueStatus() => ShowStatus("Dialogue");

        // ════════════════════════════════════════════════════════════════════════════════════════
        // TRAVERSAL MENU ITEMS
        // ════════════════════════════════════════════════════════════════════════════════════════

        [MenuItem("Game Creator/Networking Layer/Patches/Traversal/Patch (Server Authority)", false, 133)]
        public static void PatchTraversal() => ApplyPatch("Traversal");

        [MenuItem("Game Creator/Networking Layer/Patches/Traversal/Unpatch", false, 134)]
        public static void UnpatchTraversal() => RemovePatch("Traversal");

        [MenuItem("Game Creator/Networking Layer/Patches/Traversal/Check Status", false, 135)]
        public static void CheckTraversalStatus() => ShowStatus("Traversal");
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // MELEE MENU ITEMS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        [MenuItem("Game Creator/Networking Layer/Patches/Melee/Patch (Server Authority)", false, 130)]
        public static void PatchMelee() => ApplyPatch("Melee");
        
        [MenuItem("Game Creator/Networking Layer/Patches/Melee/Unpatch", false, 131)]
        public static void UnpatchMelee() => RemovePatch("Melee");
        
        [MenuItem("Game Creator/Networking Layer/Patches/Melee/Check Status", false, 132)]
        public static void CheckMeleeStatus() => ShowStatus("Melee");
        
        // ════════════════════════════════════════════════════════════════════════════════════════
        // SHOOTER MENU ITEMS
        // ════════════════════════════════════════════════════════════════════════════════════════
        
        [MenuItem("Game Creator/Networking Layer/Patches/Shooter/Patch (Server Authority)", false, 140)]
        public static void PatchShooter() => ApplyPatch("Shooter");
        
        [MenuItem("Game Creator/Networking Layer/Patches/Shooter/Unpatch", false, 141)]
        public static void UnpatchShooter() => RemovePatch("Shooter");
        
        [MenuItem("Game Creator/Networking Layer/Patches/Shooter/Check Status", false, 142)]
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

            if (!patcher.TryValidateVersionCompatibility(out string compatibilityMessage))
            {
                bool continueAnyway = EditorUtility.DisplayDialog(
                    "Version Compatibility Warning",
                    $"{patcher.DisplayName} may be incompatible with the detected package version.\n\n" +
                    $"{compatibilityMessage}\n\n" +
                    "You can continue patching, but hook insertion may fail and auto-rollback will run.",
                    "Continue Anyway",
                    "Cancel");

                if (!continueAnyway)
                {
                    return;
                }
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
                EditorUtility.DisplayDialog(
                    "Patch Applied Successfully",
                    $"The {patcher.DisplayName} system has been patched.\n\n" +
                    "Backups have been saved. You can unpatch at any time using:\n" +
                    $"Game Creator > Networking Layer > Patches > {moduleName} > Unpatch",
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

            bool isPatched = patcher.IsPatched();
            bool hasBackups = patcher.HasBackups();

            if (!isPatched && !hasBackups)
            {
                EditorUtility.DisplayDialog(
                    "Not Patched",
                    $"The {patcher.DisplayName} system is not currently patched and no backups were found.",
                    "OK");
                return;
            }

            string body = isPatched
                ? $"This will restore the original Game Creator 2 {patcher.DisplayName} source code from backups.\n\n"
                : $"The {patcher.DisplayName} patch markers are missing or incomplete, but backups exist.\n" +
                  "A restore can recover from this partial patch state.\n\n";

            bool confirm = EditorUtility.DisplayDialog(
                $"Unpatch {patcher.DisplayName}",
                body +
                "Do you want to continue?",
                "Restore Original",
                "Cancel");
            
            if (!confirm) return;
            
            bool success = patcher.RemovePatch();
            
            if (success)
            {
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
        private readonly Dictionary<string, PatchStatus> m_StatusCache = new();
        private bool m_IsOperationRunning;
        private string m_OperationLabel = string.Empty;
        private double m_NextStatusRefreshTime;

        private const double STATUS_REFRESH_INTERVAL_SECONDS = 1.0d;
        
        public static void ShowWindow()
        {
            var window = GetWindow<PatchStatusWindow>("GC2 Patch Status");
            window.minSize = new Vector2(400, 300);
            window.Show();
        }

        private void OnEnable()
        {
            RefreshStatuses(force: true);
        }

        private void OnFocus()
        {
            RefreshStatuses(force: true);
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

            if (m_IsOperationRunning)
            {
                EditorGUILayout.HelpBox(
                    $"{m_OperationLabel}\nPlease wait until the operation completes.",
                    MessageType.Info);
                EditorGUILayout.Space(6);
            }
            
            m_ScrollPosition = EditorGUILayout.BeginScrollView(m_ScrollPosition);
            
            foreach (var patcher in GC2PatchManager.GetAllPatchers())
            {
                if (!m_StatusCache.TryGetValue(patcher.ModuleName, out PatchStatus status))
                {
                    status = patcher.GetStatus();
                    m_StatusCache[patcher.ModuleName] = status;
                }

                DrawModuleStatus(patcher.ModuleName, status);
                EditorGUILayout.Space(5);
            }
            
            EditorGUILayout.EndScrollView();
            
            EditorGUILayout.Space(10);
            
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Refresh", GUILayout.Height(25)))
            {
                RefreshStatuses(force: true);
                Repaint();
            }
            EditorGUILayout.EndHorizontal();
        }

        private void RefreshStatuses(bool force = false)
        {
            if (m_IsOperationRunning) return;

            double now = EditorApplication.timeSinceStartup;
            if (!force && now < m_NextStatusRefreshTime)
            {
                return;
            }

            m_StatusCache.Clear();
            foreach (var patcher in GC2PatchManager.GetAllPatchers())
            {
                m_StatusCache[patcher.ModuleName] = patcher.GetStatus();
            }

            m_NextStatusRefreshTime = now + STATUS_REFRESH_INTERVAL_SECONDS;
        }

        private void QueuePatchOperation(string moduleName, bool applyPatch)
        {
            if (m_IsOperationRunning) return;

            m_IsOperationRunning = true;
            m_OperationLabel = applyPatch
                ? $"Patching {moduleName}..."
                : $"Unpatching {moduleName}...";
            Repaint();

            EditorApplication.delayCall += () =>
            {
                try
                {
                    string methodName = applyPatch ? $"Patch{moduleName}" : $"Unpatch{moduleName}";
                    var method = typeof(GC2PatchManager).GetMethod(methodName);
                    method?.Invoke(null, null);
                }
                finally
                {
                    m_IsOperationRunning = false;
                    m_OperationLabel = string.Empty;
                    RefreshStatuses(force: true);
                    Repaint();
                }
            };
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
                
                GUI.enabled = !status.IsPatched && !m_IsOperationRunning;
                if (GUILayout.Button("Patch", GUILayout.Width(80)))
                {
                    QueuePatchOperation(moduleName, applyPatch: true);
                    GUIUtility.ExitGUI();
                }
                
                GUI.enabled = status.IsPatched && !m_IsOperationRunning;
                if (GUILayout.Button("Unpatch", GUILayout.Width(80)))
                {
                    QueuePatchOperation(moduleName, applyPatch: false);
                    GUIUtility.ExitGUI();
                }
                
                GUI.enabled = true;
                
                EditorGUILayout.EndHorizontal();
            }
            
            EditorGUILayout.EndVertical();
        }
    }
}
