using System;
using System.Collections.Generic;
using Arawn.EnemyMasses.Editor.Integration.GameCreator2.Patches;
using Arawn.GameCreator2.Networking.Editor;
using Arawn.GameCreator2.Networking.Security;
using Arawn.GameCreator2.Networking.Transport.PurrNet;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Common.UnityUI;
using GameCreator.Runtime.Variables;
using PurrNet;
using PurrNet.Transports;
using UnityEditor;
#if UNITY_2021_2_OR_NEWER
using UnityEditor.Build;
#endif
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Arawn.GameCreator2.Networking.Transport.PurrNet.Editor
{
    /// <summary>
    /// Scene setup wizard for projects that want to run Game Creator 2 systems over PurrNet.
    /// It creates the shared GC2 networking core, variables, selected module managers, selected
    /// PurrNet bridges, and optional demo runtime UI/spawner helpers. Safe to re-run.
    /// </summary>
    public sealed class PurrNetSceneSetupWizard : EditorWindow
    {
        private enum TransportChoice
        {
            UDP,
            PurrTransport,
            WebTransport,
            Local,
            ExistingOrManual
        }

        private enum LobbySetupMode
        {
            Direct = 0,
            Lan = 1,
            RoomCode = 2
        }

        private enum WizardPage
        {
            ProjectShape = 0,
            Modules = 1,
            Transport = 2,
            Infrastructure = 3,
            SpawningAndUI = 4,
            Review = 5
        }

        private enum ProjectTemplate
        {
            Custom,
            ShooterGame,
            MeleeActionRPG,
            CoopAdventure,
            MMORPG,
            SlowSimulation,
            FullIntegrationSandbox
        }

        private enum PurrDictionMovementAdapter
        {
            Unsupported,
            Directional,
            NavMesh
        }

        private const string GENERATED_ASSET_FOLDER = "Assets/Arawn/NetworkingLayerForGC2/Generated";

        private const string SWORD_WEAPON_PATH =
            "Assets/Plugins/GameCreator/Installs/Melee.Sword@1.1.3/Sword_Weapon.asset";
        private const string AK_WEAPON_PATH =
            "Assets/Plugins/GameCreator/Installs/Shooter.Weapons@1.1.4/Weapons/AK_Weapon.asset";
        private const string AK_WEAPON_PREFAB_PATH =
            "Assets/Plugins/GameCreator/Installs/Shooter.Weapons@1.1.4/Prefabs/AK_Weapon.prefab";
        private const string AK_WEAPON_HANDLE_PATH =
            "Assets/Plugins/GameCreator/Installs/Shooter.Weapons@1.1.4/Handles/Ak_Weapon_Handle.asset";
        private const string HEALTH_ATTRIBUTE_PATH =
            "Assets/Plugins/GameCreator/Installs/Stats.Classes@1.3.7/_Stats/Health/HP.asset";
        private const string ABILITY_FIREBALL_SPRAY_PATH =
            "Assets/Plugins/GameCreator/Installs/Abilities.Examples@1.6.0/Abilities/FireballSpray/A_FireballSpray.asset";
        private const string ABILITY_FIREBALL_NOVA_PATH =
            "Assets/Plugins/GameCreator/Installs/Abilities.Examples@1.6.0/Abilities/FireballNova/A_FireballNova.asset";
        private const string PROJECTILE_FIREBALL_SPRAY_PATH =
            "Assets/Plugins/GameCreator/Installs/Abilities.Examples@1.6.0/Abilities/FireballSpray/M_FireballSpray.asset";
        private const string PROJECTILE_FIREBALL_NOVA_PATH =
            "Assets/Plugins/GameCreator/Installs/Abilities.Examples@1.6.0/Abilities/FireballNova/M_Fireball - Nova.asset";
        private const string IMPACT_FIREBALL_PATH =
            "Assets/Plugins/GameCreator/Installs/Abilities.Examples@1.6.0/Abilities/Fireball/I_Fireball.asset";
        private const string IMPACT_BLAST_PATH =
            "Assets/Plugins/GameCreator/Installs/Abilities.Examples@1.6.0/Abilities/Blast/I_Blast.asset";

        private const string NETWORK_MELEE_MANAGER_TYPE =
            "Arawn.GameCreator2.Networking.Melee.NetworkMeleeManager, Arawn.GameCreator2.Networking.Melee";
        private const string NETWORK_MELEE_CONTROLLER_TYPE =
            "Arawn.GameCreator2.Networking.Melee.NetworkMeleeController, Arawn.GameCreator2.Networking.Melee";
        private const string PURRNET_MELEE_BRIDGE_TYPE =
            "Arawn.GameCreator2.Networking.Melee.Transport.PurrNet.PurrNetMeleeTransportBridge, Arawn.GameCreator2.Networking.Melee.Transport.PurrNet";
        private const string NETWORK_STATS_MANAGER_TYPE =
            "Arawn.GameCreator2.Networking.Stats.NetworkStatsManager, Arawn.GameCreator2.Networking.Stats";
        private const string NETWORK_STATS_CONTROLLER_TYPE =
            "Arawn.GameCreator2.Networking.Stats.NetworkStatsController, Arawn.GameCreator2.Networking.Stats";
        private const string NETWORK_MELEE_STATS_DAMAGE_BRIDGE_TYPE =
            "Arawn.GameCreator2.Networking.Stats.Melee.NetworkMeleeStatsDamageBridge, Arawn.GameCreator2.Networking.Stats.Melee";
        private const string NETWORK_SHOOTER_STATS_DAMAGE_BRIDGE_TYPE =
            "Arawn.GameCreator2.Networking.Stats.Shooter.NetworkShooterStatsDamageBridge, Arawn.GameCreator2.Networking.Stats.Shooter";
        private const string PURRNET_STATS_BRIDGE_TYPE =
            "Arawn.GameCreator2.Networking.Stats.Transport.PurrNet.PurrNetStatsTransportBridge, Arawn.GameCreator2.Networking.Stats.Transport.PurrNet";
        private const string NETWORK_INVENTORY_MANAGER_TYPE =
            "Arawn.GameCreator2.Networking.Inventory.NetworkInventoryManager, Arawn.GameCreator2.Networking.Inventory";
        private const string NETWORK_INVENTORY_CONTROLLER_TYPE =
            "Arawn.GameCreator2.Networking.Inventory.NetworkInventoryController, Arawn.GameCreator2.Networking.Inventory";
        private const string PURRNET_INVENTORY_BRIDGE_TYPE =
            "Arawn.GameCreator2.Networking.Inventory.Transport.PurrNet.PurrNetInventoryTransportBridge, Arawn.GameCreator2.Networking.Inventory.Transport.PurrNet";
        private const string NETWORK_QUESTS_MANAGER_TYPE =
            "Arawn.GameCreator2.Networking.Quests.NetworkQuestsManager, Arawn.GameCreator2.Networking.Quests";
        private const string NETWORK_QUESTS_CONTROLLER_TYPE =
            "Arawn.GameCreator2.Networking.Quests.NetworkQuestsController, Arawn.GameCreator2.Networking.Quests";
        private const string PURRNET_QUESTS_BRIDGE_TYPE =
            "Arawn.GameCreator2.Networking.Quests.Transport.PurrNet.PurrNetQuestsTransportBridge, Arawn.GameCreator2.Networking.Quests.Transport.PurrNet";
        private const string NETWORK_DIALOGUE_MANAGER_TYPE =
            "Arawn.GameCreator2.Networking.Dialogue.NetworkDialogueManager, Arawn.GameCreator2.Networking.Dialogue";
        private const string NETWORK_DIALOGUE_CONTROLLER_TYPE =
            "Arawn.GameCreator2.Networking.Dialogue.NetworkDialogueController, Arawn.GameCreator2.Networking.Dialogue";
        private const string DIALOGUE_COMPONENT_TYPE =
            "GameCreator.Runtime.Dialogue.Dialogue, GameCreator.Runtime.Dialogue";
        private const string PURRNET_DIALOGUE_BRIDGE_TYPE =
            "Arawn.GameCreator2.Networking.Dialogue.Transport.PurrNet.PurrNetDialogueTransportBridge, Arawn.GameCreator2.Networking.Dialogue.Transport.PurrNet";
        private const string NETWORK_TRAVERSAL_MANAGER_TYPE =
            "Arawn.GameCreator2.Networking.Traversal.NetworkTraversalManager, Arawn.GameCreator2.Networking.Traversal";
        private const string NETWORK_TRAVERSAL_CONTROLLER_TYPE =
            "Arawn.GameCreator2.Networking.Traversal.NetworkTraversalController, Arawn.GameCreator2.Networking.Traversal";
        private const string PURRNET_TRAVERSAL_BRIDGE_TYPE =
            "Arawn.GameCreator2.Networking.Traversal.Transport.PurrNet.PurrNetTraversalTransportBridge, Arawn.GameCreator2.Networking.Traversal.Transport.PurrNet";
        private const string NETWORK_ABILITIES_CONTROLLER_TYPE =
            "Arawn.GameCreator2.Networking.NetworkAbilitiesController, Arawn.GameCreator2.Networking.Abilities";
        private const string PURRNET_ABILITIES_BRIDGE_TYPE =
            "Arawn.GameCreator2.Networking.Abilities.Transport.PurrNet.PurrNetAbilitiesTransportBridge, Arawn.GameCreator2.Networking.Abilities.Transport.PurrNet";
        private const string NETWORK_SHOOTER_MANAGER_TYPE =
            "Arawn.GameCreator2.Networking.Shooter.NetworkShooterManager, Arawn.GameCreator2.Networking.Shooter";
        private const string NETWORK_SHOOTER_CONTROLLER_TYPE =
            "Arawn.GameCreator2.Networking.Shooter.NetworkShooterController, Arawn.GameCreator2.Networking.Shooter";
        private const string PURRNET_SHOOTER_BRIDGE_TYPE =
            "Arawn.GameCreator2.Networking.Shooter.Transport.PurrNet.PurrNetShooterTransportBridge, Arawn.GameCreator2.Networking.Shooter.Transport.PurrNet";
        private const string PURRDICTION_DEFINE = "ARAWN_GC2_PURRDICTION";
        private const string PURRDICTION_MANAGER_TYPE =
            "PurrNet.Prediction.PredictionManager, PurrNet.Prediction";
        private const string PURRDICTION_CHARACTER_CONTROLLER_TYPE =
            "Arawn.GameCreator2.Networking.Transport.PurrNet.PurrDiction.PurrDictionNetworkCharacterController, Arawn.GameCreator2.Networking.Transport.PurrNet.PurrDiction";
        private const string PURRDICTION_NAVMESH_CONTROLLER_TYPE =
            "Arawn.GameCreator2.Networking.Transport.PurrNet.PurrDiction.PurrDictionNetworkNavmeshController, Arawn.GameCreator2.Networking.Transport.PurrNet.PurrDiction";
        private const string PURRNET_LOBBY_SERVICE_TYPE =
            "Arawn.GameCreator2.Networking.Transport.PurrNet.Lobby.PurrNetLobbyService, " +
            "Arawn.GameCreator2.Networking.Transport.PurrNet";
        private const string NETWORK_LOBBY_UI_TYPE =
            "Arawn.GameCreator2.Networking.Lobby.NetworkLobbyCanvasUI, " +
            "Arawn.GameCreator2.Networking.Lobby";

        private TransportChoice m_Transport = TransportChoice.UDP;
        private WizardPage m_Page = WizardPage.ProjectShape;
        private ProjectTemplate m_ProjectTemplate = ProjectTemplate.Custom;
        private int m_ExpectedPlayers = 4;
        private string m_Address = "127.0.0.1";
        private ushort m_Port = 5000;

        private bool m_ModuleStats;
        private bool m_ModuleInventory;
        private bool m_ModuleMelee;
        private bool m_ModuleShooter;
        private bool m_ModuleQuests;
        private bool m_ModuleDialogue;
        private bool m_ModuleTraversal;
        private bool m_ModuleAbilities;

        private bool m_CreateNetworkManager = true;
        private bool m_ClearStartFlags = true;
        private bool m_AssignDefaultRules = true;
        private bool m_SetTickRate = true;
        private int m_TickRate = 60;
        private NetworkPredictionBackend m_PredictionBackend = NetworkPredictionBackend.BuiltIn;
        private bool m_CreateCoreManagers = true;
        private bool m_CreateCoreBridges = true;
        private bool m_CreateModuleManagers = true;
        private bool m_CreateModuleBridges = true;
        private bool m_RegisterInstalledDemoAssets = true;
        private bool m_CreateMeleeStatsDamageBridge = true;
        private bool m_CreateShooterStatsDamageBridge = true;

        private bool m_CreatePlayerSpawner = true;
        private GameObject m_PlayerPrefab;
        private bool m_ConfigurePlayerPrefab = true;
        private bool m_ConfigurePlayerPrefabKernel = true;
        private bool m_PlayerPrefabUsesLocalVariables;
        private NetworkVariableProfile m_PlayerVariableProfile;
        private bool m_PlayerUsesNetworkInstructionClips;
        private readonly List<AnimationClip> m_PlayerPreRegisteredAnimationClips = new();
        private NetworkPrefabs m_NetworkPrefabs;
        private bool m_CreateNetworkPrefabsAsset = true;
        private bool m_CreateCharacterSelectionUI = false;
        private readonly List<GameObject> m_CharacterSelectionPrefabs = new();

        private NetworkSessionProfile m_SessionProfile;
        private bool m_CreateSessionProfileAsset = true;
        private NetworkSessionPreset m_SessionPreset = NetworkSessionPreset.Standard;
        private NetworkSessionProfile m_CustomSessionProfileDraft;

        private bool m_CreateDemoCanvasUI = true;
        private bool m_CreateLobbyUI;
        private LobbySetupMode m_LobbyMode = LobbySetupMode.Lan;
        private bool m_CreateControlsUI = true;
        private bool m_CreateChatUI = false;
        private string m_UITitle = "PurrNet";
        private string m_UISubtitle = "Game Creator 2 Networking";

        private bool m_ParentUnderRoot = true;
        private string m_RootName = "PurrNet Session";

        private Vector2 m_Scroll;

        [MenuItem("Game Creator/Networking Layer/PurrNet Scene Setup Wizard", priority = 1)]
        public static void Open()
        {
            var window = GetWindow<PurrNetSceneSetupWizard>(true, "PurrNet Scene Setup Wizard");
            window.minSize = new Vector2(600f, 560f);
            window.Show();
        }

        private void OnGUI()
        {
            m_Scroll = EditorGUILayout.BeginScrollView(m_Scroll);

            DrawHeader();
            DrawCurrentPage();

            EditorGUILayout.EndScrollView();

            DrawNavigation();
        }

        private void OnDisable()
        {
            if (m_CustomSessionProfileDraft == null) return;
            DestroyImmediate(m_CustomSessionProfileDraft);
            m_CustomSessionProfileDraft = null;
        }

        private void DrawHeader()
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("PurrNet Scene Setup", EditorStyles.boldLabel);
            EditorGUILayout.LabelField($"Step {(int)m_Page + 1} of 6 - {PageTitle(m_Page)}", EditorStyles.miniBoldLabel);
            EditorGUILayout.HelpBox(PageHelp(m_Page), MessageType.Info);

            string[] pageNames =
            {
                "1 Project",
                "2 Modules",
                "3 Transport",
                "4 Core",
                "5 Scene",
                "6 Review"
            };

            EditorGUI.BeginChangeCheck();
            int next = GUILayout.Toolbar((int)m_Page, pageNames);
            if (EditorGUI.EndChangeCheck())
            {
                m_Page = (WizardPage)next;
                GUI.FocusControl(null);
            }
        }

        private void DrawCurrentPage()
        {
            EditorGUILayout.Space(8);

            switch (m_Page)
            {
                case WizardPage.ProjectShape:
                    DrawProjectShapeSection();
                    break;
                case WizardPage.Modules:
                    DrawModuleSection();
                    break;
                case WizardPage.Transport:
                    DrawTransportSection();
                    break;
                case WizardPage.Infrastructure:
                    DrawInfrastructureSection();
                    break;
                case WizardPage.SpawningAndUI:
                    DrawSpawnerSection();
                    DrawRuntimeUISection();
                    DrawHierarchySection();
                    break;
                case WizardPage.Review:
                    DrawReviewSection();
                    break;
            }
        }

        private void DrawNavigation()
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.BeginHorizontal();

            using (new EditorGUI.DisabledScope(m_Page == WizardPage.ProjectShape))
            {
                if (GUILayout.Button(new GUIContent("Back", "Go to the previous setup step."), GUILayout.Height(32f)))
                {
                    m_Page--;
                    GUI.FocusControl(null);
                }
            }

            if (m_Page != WizardPage.Review)
            {
                if (GUILayout.Button(new GUIContent("Next", "Continue to the next setup step."), GUILayout.Height(32f)))
                {
                    m_Page++;
                    GUI.FocusControl(null);
                }
            }
            else
            {
                if (GUILayout.Button(
                        new GUIContent(
                            "Create / Update PurrNet Setup",
                            "Apply this configuration to the active scene. Existing matching objects are reused."),
                        GUILayout.Height(32f)))
                {
                    RunSetup();
                }
            }

            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space(4);
        }

        private void DrawProjectShapeSection()
        {
            EditorGUILayout.LabelField("Project Shape", EditorStyles.boldLabel);

            EditorGUI.BeginChangeCheck();
            m_ProjectTemplate = (ProjectTemplate)EditorGUILayout.EnumPopup(
                new GUIContent(
                    "Project Template",
                    "Choose the closest game type. Non-Custom templates immediately apply recommended module, tick-rate, and session preset values. Custom leaves the current settings unchanged."),
                m_ProjectTemplate);

            m_ExpectedPlayers = EditorGUILayout.IntSlider(
                new GUIContent(
                    "Expected Players",
                    "Approximate simultaneous players in one session. Changing this reapplies recommendations for non-Custom templates; Custom keeps manual settings untouched."),
                m_ExpectedPlayers,
                1,
                256);
            if (EditorGUI.EndChangeCheck() && m_ProjectTemplate != ProjectTemplate.Custom)
            {
                ApplyProjectTemplate(m_ProjectTemplate);
            }

            EditorGUILayout.HelpBox(GetTemplateDescription(m_ProjectTemplate), MessageType.None);

            EditorGUILayout.Space(8);
            EditorGUILayout.HelpBox(
                "Recommended workflow: pick the closest non-Custom project shape and the wizard applies sensible defaults immediately. " +
                "Choose Custom when you want to keep full manual control, then use Next to review each page.",
                MessageType.Info);
        }

        private void DrawTransportSection()
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Transport", EditorStyles.boldLabel);

            m_Transport = (TransportChoice)EditorGUILayout.EnumPopup(
                new GUIContent("Transport Type", "Concrete PurrNet transport attached to the NetworkManager."),
                m_Transport);

            using (new EditorGUI.DisabledScope(m_Transport == TransportChoice.Local || m_Transport == TransportChoice.ExistingOrManual))
            {
                m_Address = EditorGUILayout.TextField(
                    new GUIContent("Default Address", "Client-side default address written into supported transports and demo UI."),
                    m_Address);
                int port = EditorGUILayout.IntField(
                    new GUIContent("Default Port", "UDP/WebTransport listen + connect port."),
                    m_Port);
                m_Port = (ushort)Mathf.Clamp(port, 1, ushort.MaxValue);
            }
        }

        private void DrawModuleSection()
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("GC2 Modules Over PurrNet", EditorStyles.boldLabel);

            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.ToggleLeft(
                    new GUIContent(
                        "Core + Variables + Animation/Motion",
                        "Always enabled. These are the base systems used by character ownership, variable sync, animation sync, motion snapshots, and module bridges."),
                    true);
            }

            m_ModuleStats = EditorGUILayout.ToggleLeft(
                new GUIContent("Stats", "Adds NetworkStatsManager and PurrNet Stats Bridge. Enable when attributes, vitality, damage, or stat changes must be authoritative and synchronized."),
                m_ModuleStats);
            m_ModuleInventory = EditorGUILayout.ToggleLeft(
                new GUIContent("Inventory", "Adds NetworkInventoryManager and PurrNet Inventory Bridge. Enable when bags, equipment, item add/remove, or inventory changes must sync."),
                m_ModuleInventory);
            if (m_ModuleInventory)
            {
                DrawInventoryRequirements();
            }
            m_ModuleMelee = EditorGUILayout.ToggleLeft(
                new GUIContent("Melee", "Adds NetworkMeleeManager and PurrNet Melee Bridge. Enable for networked GC2 melee inputs, skill broadcasts, hit validation, reactions, and melee root motion."),
                m_ModuleMelee);
            if (m_ModuleMelee)
            {
                DrawMeleePatchRequirement();
            }
            m_ModuleShooter = EditorGUILayout.ToggleLeft(
                new GUIContent("Shooter", "Adds NetworkShooterManager and PurrNet Shooter Bridge. Enable for aiming, weapon state, shots, reloads, bullet VFX, and impact events."),
                m_ModuleShooter);
            if (m_ModuleShooter)
            {
                DrawShooterPatchRequirements();
                DrawShooterRegistrationWarning();
                DrawShooterPhysicsSimulationWarning();
            }
            m_ModuleQuests = EditorGUILayout.ToggleLeft(
                new GUIContent("Quests", "Adds NetworkQuestsManager and PurrNet Quests Bridge. Enable when quest sharing, task completion, or quest state must be replicated."),
                m_ModuleQuests);
            m_ModuleDialogue = EditorGUILayout.ToggleLeft(
                new GUIContent("Dialogue", "Adds NetworkDialogueManager and PurrNet Dialogue Bridge. Enable for networked dialogue play, continue, and option selection."),
                m_ModuleDialogue);
            m_ModuleTraversal = EditorGUILayout.ToggleLeft(
                new GUIContent("Traversal", "Adds NetworkTraversalManager and PurrNet Traversal Bridge. Enable for networked traversal actions and MotionLink-style interactions."),
                m_ModuleTraversal);
            if (m_ModuleTraversal)
            {
                DrawTraversalPatchRequirement();
            }
            m_ModuleAbilities = EditorGUILayout.ToggleLeft(
                new GUIContent("Abilities", "Adds Network Abilities Controller and PurrNet Abilities Bridge. Enable for the Daimahou/GC2 abilities integration, projectiles, and impacts."),
                m_ModuleAbilities);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(new GUIContent("Select All Optional Modules", "Turns on every optional module. Useful for a full integration sandbox or broad prototype.")))
            {
                SetAllOptionalModules(true);
            }
            if (GUILayout.Button(new GUIContent("Clear Optional Modules", "Turns off every optional module while keeping required Core, Variables, Animation, and Motion.")))
            {
                SetAllOptionalModules(false);
            }
            EditorGUILayout.EndHorizontal();
        }

        private void DrawInventoryRequirements()
        {
            DrawInventoryPatchRequirement();

            InventorySceneSetupTools.ValidationSummary summary =
                InventorySceneSetupTools.ValidateOpenScenes();

            if (summary.UnsafeManagerCount > 0)
            {
                EditorGUILayout.HelpBox(
                    "Allow Unvalidated Owned Client Adds is enabled on an Inventory manager. " +
                    "This trusted-co-op compatibility option lets an owning client create registered " +
                    "Item types without a project validator. Disable it for secure server authority.",
                    MessageType.Warning);
            }

            if (summary.DuplicateIdCount > 0 || summary.UnresolvedItemCount > 0)
            {
                EditorGUILayout.HelpBox(
                    $"Inventory pickup validation found {summary.DuplicateIdCount} duplicate stable " +
                    $"ID(s) and {summary.UnresolvedItemCount} source(s) without a fixed Item. " +
                    "Every NetworkInventoryPickupSource must have one unique ID and a server-resolvable Item.",
                    MessageType.Error);
            }
            else if (summary.ImplicitIdCount > 0)
            {
                EditorGUILayout.HelpBox(
                    $"{summary.ImplicitIdCount} Inventory pickup source(s) still derive their ID from " +
                    "the scene hierarchy. This is valid only while every peer uses the identical scene. " +
                    "Run the conversion tool to serialize explicit stable IDs.",
                    MessageType.Warning);
            }

            if (summary.LegacyStockPickupCount > 0)
            {
                EditorGUILayout.HelpBox(
                    $"{summary.LegacyStockPickupCount} stock pickup-shaped Add Item trigger(s) have no " +
                    "NetworkInventoryPickupSource. Clients cannot authoritatively consume these objects.",
                    MessageType.Warning);
                if (GUILayout.Button("Convert Stock Scene Pickups"))
                {
                    InventorySceneSetupTools.ConvertStockScenePickups(true);
                }
            }

            if (GUILayout.Button("Validate Inventory Scene Setup"))
            {
                InventorySceneSetupTools.ValidateOpenScenesMenu();
            }
        }

        private void DrawInventoryPatchRequirement()
        {
            var patcher = new InventoryPatcher();
            var patchStatus = patcher.GetStatus();
            bool sourceAvailable = patchStatus.IsInstalled;
            bool isApplied = sourceAvailable && patchStatus.IsPatched;
            MessageType messageType = !sourceAvailable
                ? MessageType.Error
                : isApplied
                    ? MessageType.Info
                    : MessageType.Warning;

            string status = !sourceAvailable
                ? "GC2 Inventory source files were not found. Install Game Creator 2 Inventory before enabling Inventory networking."
                : isApplied
                    ? $"Required GC2 Inventory networking patch is applied ({patcher.PatchVersion}; " +
                      $"{(patchStatus.HasBackups ? "Backups Available" : "no backups found")})."
                    : $"Required GC2 Inventory networking patch is missing or stale. Apply {patcher.PatchVersion} before using client bag operations.";
            EditorGUILayout.HelpBox(status, messageType);

            using (new EditorGUI.DisabledScope(!sourceAvailable || isApplied))
            {
                if (GUILayout.Button(
                        new GUIContent(
                            "Apply Required Inventory Patch",
                            "Installs the Inventory 3.0 semantic hooks for Add/Remove/Move, split, transfer, use/drop, wealth, merchant, crafting, and async Add Item instructions.")))
                {
                    bool applied = EnsureInventoryPatchAppliedWithPrompt(
                        "PurrNet Scene Setup Wizard",
                        out string reportLine);
                    if (!string.IsNullOrEmpty(reportLine))
                    {
                        if (applied) Debug.Log($"[PurrNetSceneSetupWizard] {reportLine}");
                        else Debug.LogWarning($"[PurrNetSceneSetupWizard] {reportLine}");
                    }
                }
            }
        }

        private static bool EnsureInventoryPatchAppliedWithPrompt(
            string setupSource,
            out string reportLine)
        {
            reportLine = null;
            var patcher = new InventoryPatcher();
            if (!patcher.ValidateFilesExist())
            {
                EditorUtility.DisplayDialog(
                    "Required GC2 Inventory Patch",
                    $"{setupSource} selected Inventory networking, but the GC2 Inventory source files were not found.",
                    "OK");
                reportLine = "Setup cancelled because GC2 Inventory source files were not found.";
                return false;
            }

            if (patcher.IsPatched())
            {
                reportLine = $"GC2 Inventory networking patch {patcher.PatchVersion} is already applied.";
                return true;
            }

            bool apply = EditorUtility.DisplayDialog(
                "Required GC2 Inventory Patch",
                $"{setupSource} requires Inventory patch {patcher.PatchVersion}.\n\n" +
                "The patch routes native Grid/List Add, Remove, Move/stack, Split, transfer, Use, " +
                "Drop, Wealth, merchant, crafting, and Add Item instruction operations through " +
                "server authority. It also makes request-backed Add Item instructions wait for " +
                "the confirmed revision before the instruction list continues.\n\n" +
                "Backups are created and can be restored from the Inventory patch menu.",
                "Apply Required Patch",
                "Cancel Setup");
            if (!apply)
            {
                reportLine = "Setup cancelled before applying the required GC2 Inventory patch.";
                return false;
            }

            if (!patcher.TryValidateVersionCompatibility(out string compatibilityMessage))
            {
                bool applyAnyway = EditorUtility.DisplayDialog(
                    "Inventory Version Compatibility Warning",
                    $"{compatibilityMessage}\n\nThe patcher verifies the hook structure and rolls back on failure.",
                    "Apply Anyway",
                    "Cancel Setup");
                if (!applyAnyway)
                {
                    reportLine = "Setup cancelled before applying the required GC2 Inventory patch.";
                    return false;
                }
            }

            if (patcher.ApplyPatch())
            {
                reportLine = $"Applied required GC2 Inventory networking patch {patcher.PatchVersion}.";
                return true;
            }

            EditorUtility.DisplayDialog(
                "Inventory Patch Failed",
                "The GC2 Inventory networking patch could not be applied. Partial changes were rolled back.\n\n" +
                "Check the Unity Console or apply it from Game Creator > Networking Layer > Patches > Inventory.",
                "OK");
            reportLine = "ERROR: Failed to apply the required GC2 Inventory networking patch.";
            return false;
        }

        private void DrawMeleePatchRequirement()
        {
            bool sourceAvailable = IsMeleeSourceAvailable();
            bool isApplied = IsMeleePatchApplied();
            MessageType messageType = !sourceAvailable
                ? MessageType.Error
                : isApplied
                    ? MessageType.Info
                    : MessageType.Warning;

            EditorGUILayout.HelpBox(GetMeleePatchStatusText(), messageType);

            using (new EditorGUI.DisabledScope(!sourceAvailable || isApplied))
            {
                if (GUILayout.Button(
                        new GUIContent(
                            "Apply Required Melee Patch",
                            "Applies the mandatory AttackSkill interception and authoritative MeleeStance hooks.")))
                {
                    bool applied = EnsureMeleePatchAppliedWithPrompt(
                        "PurrNet Scene Setup Wizard",
                        out string reportLine);

                    if (!string.IsNullOrEmpty(reportLine))
                    {
                        if (applied) Debug.Log($"[PurrNetSceneSetupWizard] {reportLine}");
                        else Debug.LogWarning($"[PurrNetSceneSetupWizard] {reportLine}");
                    }
                }
            }
        }

        private static bool IsMeleeSourceAvailable()
        {
            return new MeleePatcher().ValidateFilesExist();
        }

        private static bool IsMeleePatchApplied()
        {
            var patcher = new MeleePatcher();
            return patcher.ValidateFilesExist() && patcher.IsPatched();
        }

        private static string GetMeleePatchStatusText()
        {
            var patcher = new MeleePatcher();
            if (!patcher.ValidateFilesExist())
            {
                return "GC2 Melee source files were not found. Install Game Creator 2 Melee before enabling Melee networking.";
            }

            return patcher.IsPatched()
                ? "Required GC2 Melee networking patch is applied."
                : "Required GC2 Melee networking patch is missing or stale. Melee setup is blocked until the exact-once AttackSkill patch is applied.";
        }

        private static bool EnsureMeleePatchAppliedWithPrompt(string setupSource, out string reportLine)
        {
            reportLine = null;
            var patcher = new MeleePatcher();

            if (!patcher.ValidateFilesExist())
            {
                EditorUtility.DisplayDialog(
                    "Required GC2 Melee Patch",
                    $"{setupSource} detected that Melee networking is selected, but the GC2 Melee source files were not found.\n\n" +
                    "Install Game Creator 2 Melee before creating a PurrNet Melee networking setup.",
                    "OK");
                reportLine = "Setup cancelled because GC2 Melee source files were not found.";
                return false;
            }

            if (patcher.IsPatched())
            {
                reportLine = "GC2 Melee networking patch is already applied.";
                return true;
            }

            bool applyRequiredPatch = EditorUtility.DisplayDialog(
                "Required GC2 Melee Patch",
                $"{setupSource} detected Game Creator 2 Melee.\n\n" +
                "This patch is REQUIRED for server-authoritative Melee. It intercepts each validated AttackSkill strike before triggers, Skill.OnHit instructions, damage, poise, and reactions execute. The server then applies damage and the authored target reaction exactly once.\n\n" +
                "The legacy Network Melee Hit condition remains compatible, but it is no longer required on every Skill.\n\n" +
                "A backup is created before patching and the patch can be reverted from:\n" +
                "Game Creator > Networking Layer > Patches > Melee > Unpatch",
                "Apply Required Patch",
                "Cancel Setup");

            if (!applyRequiredPatch)
            {
                reportLine = "Setup cancelled before applying the required GC2 Melee networking patch.";
                return false;
            }

            if (!patcher.TryValidateVersionCompatibility(out string compatibilityMessage))
            {
                bool applyAnyway = EditorUtility.DisplayDialog(
                    "Melee Version Compatibility Warning",
                    $"{patcher.DisplayName} may be incompatible with the detected GC2 Melee version.\n\n" +
                    $"{compatibilityMessage}\n\n" +
                    "The patcher verifies the resulting hook structure and rolls back on failure.",
                    "Apply Anyway",
                    "Cancel Setup");
                if (!applyAnyway)
                {
                    reportLine = "Setup cancelled before applying the required GC2 Melee networking patch.";
                    return false;
                }
            }

            if (patcher.ApplyPatch())
            {
                reportLine = "Applied required GC2 Melee networking patch.";
                return true;
            }

            EditorUtility.DisplayDialog(
                "Melee Patch Failed",
                "The GC2 Melee networking patch could not be applied. Partial changes were rolled back.\n\n" +
                "Check the Unity Console, or run it manually from:\n" +
                "Game Creator > Networking Layer > Patches > Melee > Patch (Server Authority)",
                "OK");
            reportLine = "ERROR: Failed to apply required GC2 Melee networking patch.";
            return false;
        }

        private void DrawShooterPatchRequirements()
        {
            DrawShooterPatchRequirement();
            DrawShooterSightPatchRequirement();
        }

        private void DrawShooterPatchRequirement()
        {
            bool sourceAvailable = IsShooterSourceAvailable();
            bool isApplied = IsShooterPatchApplied();
            MessageType messageType = !sourceAvailable ? MessageType.Error : (isApplied ? MessageType.Info : MessageType.Warning);

            EditorGUILayout.HelpBox(GetShooterPatchStatusText(), messageType);

            using (new EditorGUI.DisabledScope(!sourceAvailable || isApplied))
            {
                if (GUILayout.Button(
                        new GUIContent(
                            "Apply Required Shooter Patch",
                            "Applies the mandatory GC2 Shooter hooks used by the PurrNet shooter bridge for aim point, trigger, reload, shot, and hit synchronization.")))
                {
                    bool applied = EnsureShooterPatchAppliedWithPrompt(
                        "PurrNet Scene Setup Wizard",
                        out string reportLine);

                    if (!string.IsNullOrEmpty(reportLine))
                    {
                        if (applied) Debug.Log($"[PurrNetSceneSetupWizard] {reportLine}");
                        else Debug.LogWarning($"[PurrNetSceneSetupWizard] {reportLine}");
                    }
                }
            }
        }

        private static bool IsShooterSourceAvailable()
        {
            return new ShooterPatcher().ValidateFilesExist();
        }

        private static bool IsShooterPatchApplied()
        {
            var patcher = new ShooterPatcher();
            return patcher.ValidateFilesExist() && patcher.IsPatched();
        }

        private static string GetShooterPatchStatusText()
        {
            var patcher = new ShooterPatcher();
            if (!patcher.ValidateFilesExist())
            {
                return "GC2 Shooter source files were not found. Install Game Creator 2 Shooter before enabling Shooter networking.";
            }

            return patcher.IsPatched()
                ? "Required GC2 Shooter networking patch is applied, including cancellable hit validation and native shield-outcome capture."
                : "Required GC2 Shooter networking patch is missing or stale. Notification-only or pre-outcome patches are unsafe; Shooter setup is blocked until the current patch is applied.";
        }

        private static bool EnsureShooterPatchAppliedWithPrompt(string setupSource, out string reportLine)
        {
            reportLine = null;

            var patcher = new ShooterPatcher();
            if (!patcher.ValidateFilesExist())
            {
                EditorUtility.DisplayDialog(
                    "Required GC2 Shooter Patch",
                    $"{setupSource} detected that Shooter networking is selected, but the GC2 Shooter source files were not found.\n\n" +
                    "Install Game Creator 2 Shooter before creating a PurrNet Shooter networking setup.",
                    "OK");

                reportLine = "Setup cancelled because GC2 Shooter source files were not found.";
                return false;
            }

            if (patcher.IsPatched())
            {
                reportLine = "GC2 Shooter networking patch is already applied.";
                return true;
            }

            bool applyRequiredPatch = EditorUtility.DisplayDialog(
                "Required GC2 Shooter Patch",
                $"{setupSource} detected Game Creator 2 Shooter.\n\n" +
                "This patch is REQUIRED before creating a PurrNet Shooter networking setup. " +
                "Continuing without it leaves GC2 Shooter aim, trigger, reload, shot, and hit paths running without the network hooks the bridge depends on.\n\n" +
                "The aim hook is especially important: it lets the Networking Layer resolve the authoritative aim point through NetworkAimPointResolver. Without it, remote players can fail to sync where they are actually aiming, even if weapon state and animation appear to replicate.\n\n" +
                "The patch keeps GC2 owning the shooter lifecycle. It adds small hooks for ShooterStance trigger/reload, WeaponData shot validation, a cancellable ShooterWeapon hit validator before native side effects, post-hit Block/Parry/Break outcome capture, and Aim point resolution. Existing NetworkHitDetected-only patches are migrated because a notification cannot stop client damage.\n\n" +
                "A backup is created before patching and the patch can be reverted from:\n" +
                "Game Creator > Networking Layer > Patches > Shooter > Unpatch",
                "Apply Required Patch",
                "Cancel Setup");

            if (!applyRequiredPatch)
            {
                reportLine = "Setup cancelled before applying the required GC2 Shooter networking patch.";
                return false;
            }

            if (!patcher.TryValidateVersionCompatibility(out string compatibilityMessage))
            {
                bool applyAnyway = EditorUtility.DisplayDialog(
                    "Shooter Version Compatibility Warning",
                    $"{patcher.DisplayName} may be incompatible with the detected GC2 Shooter version.\n\n" +
                    $"{compatibilityMessage}\n\n" +
                    "The patcher searches for method structure instead of line numbers and auto-rolls back on failure, " +
                    "but a large GC2 Shooter update may still require a patcher update.\n\n" +
                    "Shooter networking setup cannot continue until the required shooter hooks are applied.",
                    "Apply Anyway",
                    "Cancel Setup");

                if (!applyAnyway)
                {
                    reportLine = "Setup cancelled before applying the required GC2 Shooter networking patch.";
                    return false;
                }
            }

            bool success = patcher.ApplyPatch();
            if (success)
            {
                reportLine = "Applied required GC2 Shooter networking patch.";
                return true;
            }

            EditorUtility.DisplayDialog(
                "Shooter Patch Failed",
                "The GC2 Shooter networking patch could not be applied. The patcher rolled back any partial changes.\n\n" +
                "Check the Unity Console for the exact insertion failure. You can also run it manually from:\n" +
                "Game Creator > Networking Layer > Patches > Shooter > Patch (Server Authority)\n\n" +
                "Networked Shooter setup cannot continue until this patch is applied.",
                "OK");

            reportLine = "ERROR: Failed to apply required GC2 Shooter networking patch.";
            return false;
        }

        private void DrawShooterSightPatchRequirement()
        {
            bool sourceAvailable = ShooterSightPatchRequirement.IsShooterSightSourceAvailable();
            bool isApplied = ShooterSightPatchRequirement.IsApplied();
            MessageType messageType = isApplied ? MessageType.Info : MessageType.Warning;

            EditorGUILayout.HelpBox(ShooterSightPatchRequirement.GetStatusText(), messageType);

            using (new EditorGUI.DisabledScope(!sourceAvailable || isApplied))
            {
                if (GUILayout.Button(
                        new GUIContent(
                            "Apply Required Shooter Sight Patch",
                            "Applies the mandatory GC2 Shooter Sight.Enter/Exit hook used to suppress local-only sight instructions on remote network replicas.")))
                {
                    bool applied = ShooterSightPatchRequirement.EnsureAppliedWithPrompt(
                        "PurrNet Scene Setup Wizard",
                        out string reportLine);

                    if (!string.IsNullOrEmpty(reportLine))
                    {
                        if (applied) Debug.Log($"[PurrNetSceneSetupWizard] {reportLine}");
                        else Debug.LogWarning($"[PurrNetSceneSetupWizard] {reportLine}");
                    }
                }
            }
        }

        private void DrawTraversalPatchRequirement()
        {
            bool sourceAvailable = IsTraversalSourceAvailable();
            bool isApplied = IsTraversalPatchApplied();
            MessageType messageType = !sourceAvailable ? MessageType.Error : (isApplied ? MessageType.Info : MessageType.Warning);

            EditorGUILayout.HelpBox(GetTraversalPatchStatusText(), messageType);

            using (new EditorGUI.DisabledScope(!sourceAvailable || isApplied))
            {
                if (GUILayout.Button(
                        new GUIContent(
                            "Apply Required Traversal Patch",
                            "Applies the mandatory GC2 Traversal hooks used by the PurrNet traversal bridge for ledges, free climb, links, and traversal stance actions.")))
                {
                    bool applied = EnsureTraversalPatchAppliedWithPrompt(
                        "PurrNet Scene Setup Wizard",
                        out string reportLine);

                    if (!string.IsNullOrEmpty(reportLine))
                    {
                        if (applied) Debug.Log($"[PurrNetSceneSetupWizard] {reportLine}");
                        else Debug.LogWarning($"[PurrNetSceneSetupWizard] {reportLine}");
                    }
                }
            }
        }

        private static bool IsTraversalSourceAvailable()
        {
            return new TraversalPatcher().ValidateFilesExist();
        }

        private static bool IsTraversalPatchApplied()
        {
            var patcher = new TraversalPatcher();
            return patcher.ValidateFilesExist() && patcher.IsPatched();
        }

        private static string GetTraversalPatchStatusText()
        {
            var patcher = new TraversalPatcher();
            if (!patcher.ValidateFilesExist())
            {
                return "GC2 Traversal source files were not found. Install Game Creator 2 Traversal before enabling Traversal networking.";
            }

            return patcher.IsPatched()
                ? "Required GC2 Traversal networking patch is applied."
                : "Required GC2 Traversal networking patch is not applied. Traversal networking setup will be blocked until it is applied.";
        }

        private static bool EnsureTraversalPatchAppliedWithPrompt(string setupSource, out string reportLine)
        {
            reportLine = null;

            var patcher = new TraversalPatcher();
            if (!patcher.ValidateFilesExist())
            {
                EditorUtility.DisplayDialog(
                    "Required GC2 Traversal Patch",
                    $"{setupSource} detected that Traversal networking is selected, but the GC2 Traversal source files were not found.\n\n" +
                    "Install Game Creator 2 Traversal before creating a PurrNet Traversal networking setup.",
                    "OK");

                reportLine = "Setup cancelled because GC2 Traversal source files were not found.";
                return false;
            }

            if (patcher.IsPatched())
            {
                reportLine = "GC2 Traversal networking patch is already applied.";
                return true;
            }

            bool applyRequiredPatch = EditorUtility.DisplayDialog(
                "Required GC2 Traversal Patch",
                $"{setupSource} detected Game Creator 2 Traversal.\n\n" +
                "This patch is REQUIRED before creating a PurrNet Traversal networking setup. " +
                "Continuing without it leaves GC2 Traversal running local-only entry, exit, edge connection, and stance action paths, so ledge climb and free climb can desync over the network.\n\n" +
                "The patch keeps GC2 owning the traversal lifecycle. It adds small hooks that let the Networking Layer validate TraverseLink, TraverseInteractive, MotionInteractive edge connections, and TraversalStance actions through NetworkTraversalPatchHooks.\n\n" +
                "Without this patch, connected clients can appear broken on the host or other clients: wrong climb animations, missing ledge-to-ledge traversal, stale free-climb state, and server corrections during traversal.\n\n" +
                "A backup is created before patching and the patch can be reverted from:\n" +
                "Game Creator > Networking Layer > Patches > Traversal > Unpatch",
                "Apply Required Patch",
                "Cancel Setup");

            if (!applyRequiredPatch)
            {
                reportLine = "Setup cancelled before applying the required GC2 Traversal networking patch.";
                return false;
            }

            if (!patcher.TryValidateVersionCompatibility(out string compatibilityMessage))
            {
                bool applyAnyway = EditorUtility.DisplayDialog(
                    "Traversal Version Compatibility Warning",
                    $"{patcher.DisplayName} may be incompatible with the detected GC2 Traversal version.\n\n" +
                    $"{compatibilityMessage}\n\n" +
                    "The patcher searches for method structure instead of line numbers and auto-rolls back on failure, " +
                    "but a large GC2 Traversal update may still require a patcher update.\n\n" +
                    "Traversal networking setup cannot continue until the required traversal hooks are applied.",
                    "Apply Anyway",
                    "Cancel Setup");

                if (!applyAnyway)
                {
                    reportLine = "Setup cancelled before applying the required GC2 Traversal networking patch.";
                    return false;
                }
            }

            bool success = patcher.ApplyPatch();
            if (success)
            {
                reportLine = "Applied required GC2 Traversal networking patch.";
                return true;
            }

            EditorUtility.DisplayDialog(
                "Traversal Patch Failed",
                "The GC2 Traversal networking patch could not be applied. The patcher rolled back any partial changes.\n\n" +
                "Check the Unity Console for the exact insertion failure. You can also run it manually from:\n" +
                "Game Creator > Networking Layer > Patches > Traversal > Patch (Server Authority)\n\n" +
                "Networked Traversal setup cannot continue until this patch is applied.",
                "OK");

            reportLine = "ERROR: Failed to apply required GC2 Traversal networking patch.";
            return false;
        }

        private void DrawInfrastructureSection()
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Scene Infrastructure", EditorStyles.boldLabel);

            m_CreateNetworkManager = EditorGUILayout.ToggleLeft(
                new GUIContent("Create / Reuse NetworkManager", "Ensures a PurrNet NetworkManager exists."),
                m_CreateNetworkManager);
            m_ClearStartFlags = EditorGUILayout.ToggleLeft(
                new GUIContent("Clear NetworkManager auto-start flags", "Keeps startup controlled by the runtime demo UI."),
                m_ClearStartFlags);
            m_AssignDefaultRules = EditorGUILayout.ToggleLeft(
                new GUIContent("Assign default NetworkRules + VisibilityRules", "Uses PurrNet ServerStrict and AlwaysVisible defaults when fields are empty."),
                m_AssignDefaultRules);

            m_SetTickRate = EditorGUILayout.ToggleLeft(
                new GUIContent("Set NetworkManager tick rate", "A 60Hz default gives smoother low-player-count GC2 character motion than PurrNet's lower default."),
                m_SetTickRate);
            using (new EditorGUI.DisabledScope(!m_SetTickRate))
            {
                m_TickRate = EditorGUILayout.IntSlider(
                    new GUIContent(
                        "Tick Rate",
                        "PurrNet simulation tick rate for this scene. Higher values feel smoother for action games but cost more CPU/network bandwidth; lower values fit large or slow simulations."),
                    m_TickRate,
                    10,
                    120);
            }

            DrawPredictionBackendSection();

            m_CreateCoreManagers = EditorGUILayout.ToggleLeft(
                new GUIContent(
                    "Create / Reuse Core Managers",
                    "Creates security, core, animation, motion, and variable managers. Disable only if you already maintain custom scene objects for these systems."),
                m_CreateCoreManagers);
            m_CreateCoreBridges = EditorGUILayout.ToggleLeft(
                new GUIContent(
                    "Create / Reuse Core PurrNet Bridges",
                    "Creates the core transport, variable, and animation/motion bridges. Disable only if you want to wire bridge objects manually."),
                m_CreateCoreBridges);
            m_CreateModuleManagers = EditorGUILayout.ToggleLeft(
                new GUIContent(
                    "Create / Reuse Selected Module Managers",
                    "Creates managers for the selected GC2 modules. Usually keep enabled so the selected bridges have matching runtime managers."),
                m_CreateModuleManagers);
            m_CreateModuleBridges = EditorGUILayout.ToggleLeft(
                new GUIContent(
                    "Create / Reuse Selected Module Bridges",
                    "Creates PurrNet bridge components for the selected GC2 modules. Disable only if you want a manager-only scene or custom transport wiring."),
                m_CreateModuleBridges);
            m_RegisterInstalledDemoAssets = EditorGUILayout.ToggleLeft(
                new GUIContent("Register installed demo weapons/abilities when available", "Adds Sword, AK, and example ability assets to matching PurrNet bridges if those GC2 installs exist."),
                m_RegisterInstalledDemoAssets);

            using (new EditorGUI.DisabledScope(!m_ModuleMelee || !m_ModuleStats))
            {
                m_CreateMeleeStatsDamageBridge = EditorGUILayout.ToggleLeft(
                    new GUIContent("Add Melee -> Stats damage bridge", "Only used when both Melee and Stats are selected."),
                    m_CreateMeleeStatsDamageBridge);
            }

            using (new EditorGUI.DisabledScope(!m_ModuleShooter || !m_ModuleStats))
            {
                m_CreateShooterStatsDamageBridge = EditorGUILayout.ToggleLeft(
                    new GUIContent(
                        "Add Shooter -> Stats damage bridge",
                        "Applies validated Shooter damage to GC2 Stats while reactions remain authoritative and separate."),
                    m_CreateShooterStatsDamageBridge);
            }

            EditorGUILayout.Space(4);
            m_SessionProfile = (NetworkSessionProfile)EditorGUILayout.ObjectField(
                new GUIContent("Session Profile", "Optional NetworkSessionProfile assigned to PurrNetTransportBridge."),
                m_SessionProfile,
                typeof(NetworkSessionProfile),
                false);
            m_CreateSessionProfileAsset = EditorGUILayout.ToggleLeft(
                new GUIContent("Create Session Profile asset if none assigned", $"Creates one in {GENERATED_ASSET_FOLDER}."),
                m_CreateSessionProfileAsset);
            using (new EditorGUI.DisabledScope(!m_CreateSessionProfileAsset || m_SessionProfile != null))
            {
                m_SessionPreset = (NetworkSessionPreset)EditorGUILayout.EnumPopup(
                    new GUIContent(
                        "Session Preset",
                        "Preset used when the wizard creates a NetworkSessionProfile. Duel favors responsive low-player combat; Standard is balanced; Massive is cheaper for larger sessions."),
                    m_SessionPreset);
            }

            if (m_SessionPreset == NetworkSessionPreset.Custom)
            {
                DrawCustomSessionProfileSection();
            }
        }

        private void DrawPredictionBackendSection()
        {
            bool purrDictionInstalled = IsPurrDictionInstalled();
            if (!purrDictionInstalled)
            {
                m_PredictionBackend = NetworkPredictionBackend.BuiltIn;
                return;
            }

            EditorGUILayout.Space(4);
            m_PredictionBackend = (NetworkPredictionBackend)EditorGUILayout.EnumPopup(
                new GUIContent(
                    "Prediction Backend",
                    "Built-in uses the GC2 networking layer's transport-agnostic prediction. PurrDiction uses PurrNet's PurrDiction stack for supported directional and point-click player prefabs."),
                m_PredictionBackend);

            if (m_PredictionBackend != NetworkPredictionBackend.PurrDiction) return;

            if (IsTraversalEnabledForSetupOrScene())
            {
                EditorGUILayout.HelpBox(
                    "Traversal currently requires the Built-in prediction backend. The PurrDiction adapters " +
                    "do not expose INetworkOwnerMotionAuthority, so allowing this combination would let " +
                    "movement reconciliation overwrite traversal-driven poses.",
                    MessageType.Error);
            }

            bool hasDefine = HasScriptingDefine(PURRDICTION_DEFINE);
            bool compiled = IsPurrDictionIntegrationCompiled();

            if (!hasDefine)
            {
                EditorGUILayout.HelpBox(
                    $"PurrDiction is installed, but {PURRDICTION_DEFINE} is not defined for the active build target. Add it, let Unity recompile, then run setup again.",
                    MessageType.Warning);

                if (GUILayout.Button($"Add {PURRDICTION_DEFINE} Define"))
                {
                    AddScriptingDefine(PURRDICTION_DEFINE);
                }
            }
            else if (!compiled)
            {
                EditorGUILayout.HelpBox(
                    "The PurrDiction integration define is set, but the optional integration assembly has not compiled yet. Wait for Unity to finish recompiling, then run setup.",
                    MessageType.Info);
            }
            else
            {
                EditorGUILayout.HelpBox(
                    "PurrDiction will own player movement prediction. Existing GC2 module bridges remain enabled for gameplay state sync.",
                    MessageType.Info);
            }
        }

        private bool ValidatePredictionBackendSelection()
        {
            if (m_PredictionBackend != NetworkPredictionBackend.PurrDiction) return true;

            if (IsTraversalEnabledForSetupOrScene())
            {
                EditorUtility.DisplayDialog(
                    "Traversal Requires Built-in Prediction",
                    "PurrDiction cannot currently be used with the Traversal module because its movement " +
                    "adapters do not implement INetworkOwnerMotionAuthority. Select Built-in prediction, or " +
                    "disable Traversal, to prevent traversal pose and transform desynchronization.",
                    "OK");
                return false;
            }

            if (!IsPurrDictionInstalled())
            {
                EditorUtility.DisplayDialog(
                    "PurrDiction Backend",
                    "PurrDiction was selected, but PurrDiction is not installed or has not compiled. Select Built-in prediction or install PurrDiction first.",
                    "OK");
                return false;
            }

            if (!HasScriptingDefine(PURRDICTION_DEFINE))
            {
                EditorUtility.DisplayDialog(
                    "PurrDiction Backend",
                    $"{PURRDICTION_DEFINE} is required before the optional PurrDiction integration assembly can compile. Add the define from the wizard, wait for Unity to recompile, then run setup again.",
                    "OK");
                return false;
            }

            if (!IsPurrDictionIntegrationCompiled())
            {
                EditorUtility.DisplayDialog(
                    "PurrDiction Backend",
                    "The PurrDiction integration assembly has not compiled yet. Wait for Unity to finish recompiling, then run setup again.",
                    "OK");
                return false;
            }

            if (!ValidatePurrDictionPrefabCompatibility())
            {
                return false;
            }

            return true;
        }

        private bool IsTraversalEnabledForSetupOrScene()
        {
            return m_ModuleTraversal || IsSceneBridgeConfigured(PURRNET_TRAVERSAL_BRIDGE_TYPE);
        }

        private static bool IsSceneBridgeConfigured(string bridgeTypeName)
        {
            Type bridgeType = Type.GetType(bridgeTypeName);
            return bridgeType != null && FindSceneComponent(bridgeType) != null;
        }

        private bool ValidatePurrDictionPrefabCompatibility()
        {
            if (!m_ConfigurePlayerPrefab) return true;

            List<GameObject> prefabs = GetSpawnablePlayerPrefabs();
            for (int i = 0; i < prefabs.Count; i++)
            {
                GameObject prefab = prefabs[i];
                PurrDictionMovementAdapter adapter = DetectPurrDictionMovementAdapter(
                    prefab,
                    m_ConfigurePlayerPrefabKernel);

                if (adapter != PurrDictionMovementAdapter.Unsupported) continue;

                string prefabName = prefab != null ? prefab.name : "<missing prefab>";
                EditorUtility.DisplayDialog(
                    "PurrDiction Movement Adapter",
                    $"PurrDiction was selected, but '{prefabName}' uses a movement style that does not have a GC2 PurrDiction adapter yet.\n\n" +
                    "Use Built-in prediction for this prefab, or switch it to a supported Directional or Point Click player setup.",
                    "OK");
                return false;
            }

            return true;
        }

        private static bool IsPurrDictionInstalled()
        {
            return Type.GetType(PURRDICTION_MANAGER_TYPE) != null;
        }

        private static bool IsPurrDictionIntegrationCompiled()
        {
            return Type.GetType(PURRDICTION_CHARACTER_CONTROLLER_TYPE) != null &&
                   Type.GetType(PURRDICTION_NAVMESH_CONTROLLER_TYPE) != null;
        }

        private string GetPurrDictionMovementAdapterSummary()
        {
            if (!m_ConfigurePlayerPrefab) return "Manual prefab setup";

            List<GameObject> prefabs = GetSpawnablePlayerPrefabs();
            if (prefabs.Count == 0) return "No player prefab";

            bool hasDirectional = false;
            bool hasNavMesh = false;
            bool hasUnsupported = false;

            for (int i = 0; i < prefabs.Count; i++)
            {
                switch (DetectPurrDictionMovementAdapter(prefabs[i], m_ConfigurePlayerPrefabKernel))
                {
                    case PurrDictionMovementAdapter.Directional:
                        hasDirectional = true;
                        break;
                    case PurrDictionMovementAdapter.NavMesh:
                        hasNavMesh = true;
                        break;
                    default:
                        hasUnsupported = true;
                        break;
                }
            }

            if (hasUnsupported) return "Unsupported movement style detected";
            if (hasDirectional && hasNavMesh) return "Directional + NavMesh";
            if (hasNavMesh) return GetPurrDictionMovementAdapterLabel(PurrDictionMovementAdapter.NavMesh);
            if (hasDirectional) return GetPurrDictionMovementAdapterLabel(PurrDictionMovementAdapter.Directional);
            return GetPurrDictionMovementAdapterLabel(PurrDictionMovementAdapter.Unsupported);
        }

        private static string GetPurrDictionMovementAdapterLabel(PurrDictionMovementAdapter adapter)
        {
            return adapter switch
            {
                PurrDictionMovementAdapter.Directional => "Directional CharacterController",
                PurrDictionMovementAdapter.NavMesh => "Point Click NavMesh",
                _ => "Unsupported"
            };
        }

        private static PurrDictionMovementAdapter DetectPurrDictionMovementAdapter(
            GameObject prefabRoot,
            bool kernelPreparationEnabled)
        {
            if (prefabRoot == null) return PurrDictionMovementAdapter.Unsupported;

            Character character = prefabRoot.GetComponent<Character>();
            if (character == null)
            {
                return kernelPreparationEnabled
                    ? PurrDictionMovementAdapter.Directional
                    : PurrDictionMovementAdapter.Unsupported;
            }

            IUnitPlayer player = character.Player;
            if (player == null)
            {
                return kernelPreparationEnabled
                    ? PurrDictionMovementAdapter.Directional
                    : PurrDictionMovementAdapter.Unsupported;
            }

            return DetectPurrDictionMovementAdapter(player);
        }

        private static PurrDictionMovementAdapter DetectPurrDictionMovementAdapter(IUnitPlayer player)
        {
            if (player == null) return PurrDictionMovementAdapter.Unsupported;

            if (player is UnitPlayerPointClick || player is UnitPlayerPointClickNetwork)
            {
                return PurrDictionMovementAdapter.NavMesh;
            }

            if (player is UnitPlayerTank || player is UnitPlayerTankNetwork ||
                player is UnitPlayerFollowPointer || player is UnitPlayerFollowPointerNetwork)
            {
                return PurrDictionMovementAdapter.Unsupported;
            }

            if (player is UnitPlayerDirectional || player is UnitPlayerDirectionalNetwork)
            {
                return PurrDictionMovementAdapter.Directional;
            }

            return PurrDictionMovementAdapter.Unsupported;
        }

        private static bool HasScriptingDefine(string define)
        {
            if (string.IsNullOrEmpty(define)) return false;

#if UNITY_2021_2_OR_NEWER
            NamedBuildTarget buildTarget = NamedBuildTarget.FromBuildTargetGroup(
                EditorUserBuildSettings.selectedBuildTargetGroup);
            string symbols = PlayerSettings.GetScriptingDefineSymbols(buildTarget);
#else
            BuildTargetGroup group = EditorUserBuildSettings.selectedBuildTargetGroup;
            string symbols = PlayerSettings.GetScriptingDefineSymbolsForGroup(group);
#endif
            string[] parts = symbols.Split(';');
            for (int i = 0; i < parts.Length; i++)
            {
                if (string.Equals(parts[i].Trim(), define, StringComparison.Ordinal)) return true;
            }

            return false;
        }

        private static void AddScriptingDefine(string define)
        {
            if (string.IsNullOrEmpty(define) || HasScriptingDefine(define)) return;

#if UNITY_2021_2_OR_NEWER
            NamedBuildTarget buildTarget = NamedBuildTarget.FromBuildTargetGroup(
                EditorUserBuildSettings.selectedBuildTargetGroup);
            string symbols = PlayerSettings.GetScriptingDefineSymbols(buildTarget);
            string targetName = buildTarget.ToString();
#else
            BuildTargetGroup group = EditorUserBuildSettings.selectedBuildTargetGroup;
            string symbols = PlayerSettings.GetScriptingDefineSymbolsForGroup(group);
            string targetName = group.ToString();
#endif
            string updated = string.IsNullOrWhiteSpace(symbols)
                ? define
                : $"{symbols};{define}";

#if UNITY_2021_2_OR_NEWER
            PlayerSettings.SetScriptingDefineSymbols(buildTarget, updated);
#else
            PlayerSettings.SetScriptingDefineSymbolsForGroup(group, updated);
#endif
            EditorUtility.DisplayDialog(
                "PurrDiction Backend",
                $"{define} was added for {targetName}. Wait for Unity to recompile, then reopen/run the wizard.",
                "OK");
        }

        private void DrawCustomSessionProfileSection()
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Custom Session Settings", EditorStyles.boldLabel);

            if (m_SessionProfile != null)
            {
                EditorGUILayout.HelpBox(
                    "An explicit Session Profile asset is assigned above. These fields edit that asset directly. Clear the assigned asset if you want the wizard to generate a new custom profile.",
                    MessageType.Info);
            }
            else if (!m_CreateSessionProfileAsset)
            {
                EditorGUILayout.HelpBox(
                    "Custom values are visible for planning, but no Session Profile asset will be generated while 'Create Session Profile asset if none assigned' is disabled.",
                    MessageType.Warning);
            }
            else
            {
                EditorGUILayout.HelpBox(
                    "These values will be written into the generated NetworkSessionProfile asset. Start conservative, then tighten rates only where the project needs more responsiveness.",
                    MessageType.Info);
            }

            NetworkSessionProfile profile = m_SessionProfile != null
                ? m_SessionProfile
                : GetOrCreateCustomSessionProfileDraft();

            if (profile == null) return;

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(
                    new GUIContent(
                        "Load Duel Defaults",
                        "Starts the custom values from the Duel preset. Use for low-player-count combat where responsiveness matters most.")))
            {
                ApplyPresetToCustomSessionProfile(profile, NetworkSessionPreset.Duel);
            }
            if (GUILayout.Button(
                    new GUIContent(
                        "Load Standard Defaults",
                        "Starts the custom values from the Standard preset. Use as a balanced baseline for most co-op or action projects.")))
            {
                ApplyPresetToCustomSessionProfile(profile, NetworkSessionPreset.Standard);
            }
            if (GUILayout.Button(
                    new GUIContent(
                        "Load Massive Defaults",
                        "Starts the custom values from the Massive preset. Use for larger sessions where bandwidth and CPU matter more than very high update rates.")))
            {
                ApplyPresetToCustomSessionProfile(profile, NetworkSessionPreset.Massive);
            }
            EditorGUILayout.EndHorizontal();

            var so = new SerializedObject(profile);
            EditorGUI.BeginChangeCheck();

            SetSessionProfilePresetProperties(so, NetworkSessionPreset.Custom, false);

            DrawCustomSessionGeneralFields(so);
            DrawCustomSessionTierFields(so, "near", "Near Tier", "nearby characters that need the most responsive and complete synchronization");
            DrawCustomSessionTierFields(so, "mid", "Mid Tier", "characters at medium distance where animation/combat usually matters but expensive detail can be reduced");
            DrawCustomSessionTierFields(so, "far", "Far Tier", "distant characters where cheap visual approximation is usually enough");

            if (EditorGUI.EndChangeCheck())
            {
                so.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(profile);
            }
            else
            {
                so.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        private static void DrawCustomSessionGeneralFields(SerializedObject so)
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Simulation", EditorStyles.miniBoldLabel);
            DrawProfileProperty(
                so,
                "serverSimulationRate",
                "Server Simulation Rate",
                "Server-side simulation rate for authoritative character motion. Higher feels tighter but costs more CPU.");
            DrawProfileProperty(
                so,
                "serverStateBroadcastRate",
                "State Broadcast Rate",
                "How often the server sends character state snapshots. Higher reduces remote latency but increases bandwidth.");
            DrawProfileProperty(
                so,
                "relevanceUpdateRate",
                "Relevance Update Rate",
                "How often characters recalculate near/mid/far relevance. Higher adapts faster to movement but costs more CPU.");

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Input and Reconciliation", EditorStyles.miniBoldLabel);
            DrawProfileProperty(
                so,
                "inputSendRate",
                "Input Send Rate",
                "How often local clients send movement input. Higher improves responsiveness but costs bandwidth.");
            DrawProfileProperty(
                so,
                "inputRedundancy",
                "Input Redundancy",
                "How many recent inputs are repeated per packet to survive packet loss. Higher is safer but larger.");
            DrawProfileProperty(
                so,
                "reconciliationThreshold",
                "Reconciliation Threshold",
                "Minimum prediction error before correction starts. Lower is stricter but can show more correction.");
            DrawProfileProperty(
                so,
                "maxReconciliationDistance",
                "Max Reconciliation Distance",
                "Large prediction errors beyond this distance can snap instead of smoothly correcting.");
            DrawProfileProperty(
                so,
                "reconciliationSpeed",
                "Reconciliation Speed",
                "How quickly clients smooth toward server authority. Higher corrects faster but can feel sharper.");

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Anti-Cheat", EditorStyles.miniBoldLabel);
            DrawProfileProperty(
                so,
                "maxSpeedMultiplier",
                "Max Speed Multiplier",
                "Allowed speed tolerance before movement is suspicious. Lower is stricter; higher allows more latency and root-motion bursts.");
            DrawProfileProperty(
                so,
                "violationThreshold",
                "Violation Threshold",
                "Number of suspicious samples tolerated before enforcement. Higher reduces false positives but delays response.");

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Relevance and Culling", EditorStyles.miniBoldLabel);
            DrawProfileProperty(
                so,
                "nearDistance",
                "Near Distance",
                "Distance up to which the Near tier is used. Increase for combat games where detail matters at longer range.");
            DrawProfileProperty(
                so,
                "midDistance",
                "Mid Distance",
                "Distance up to which the Mid tier is used. Must stay greater than Near Distance.");
            DrawProfileProperty(
                so,
                "requireObserverCharacterForRelevance",
                "Require Observer Character",
                "When enabled, clients without a representative character do not receive character state. Useful for large sessions.");
            DrawProfileProperty(
                so,
                "enableDistanceCulling",
                "Enable Distance Culling",
                "Stops or slows state delivery beyond Cull Distance. Useful for large worlds and MMO-like sessions.");
            DrawProfileProperty(
                so,
                "cullDistance",
                "Cull Distance",
                "Distance beyond which culling applies when enabled. Keep above Mid Distance.");
            DrawProfileProperty(
                so,
                "culledKeepAliveRate",
                "Culled Keepalive Rate",
                "Optional low-frequency state rate for culled characters. Set 0 to fully suppress regular updates beyond cull distance.");
        }

        private static void DrawCustomSessionTierFields(
            SerializedObject so,
            string propertyName,
            string label,
            string description)
        {
            SerializedProperty tier = so.FindProperty(propertyName);
            if (tier == null) return;

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField(label, EditorStyles.miniBoldLabel);
            EditorGUILayout.HelpBox($"Controls {description}.", MessageType.None);

            DrawNestedProfileProperty(
                tier,
                "stateApplyRate",
                "State Apply Rate",
                "How often remote drivers apply received states for this tier. Higher is smoother and more expensive.");
            DrawNestedProfileProperty(
                tier,
                "interpolationDelay",
                "Interpolation Delay",
                "Buffered delay before rendering remote motion. Higher tolerates jitter; lower feels more immediate.");
            DrawNestedProfileProperty(
                tier,
                "maxExtrapolationTime",
                "Max Extrapolation Time",
                "How long a remote character may extrapolate when snapshots are late. Higher hides loss but can drift.");
            DrawNestedProfileProperty(
                tier,
                "snapDistance",
                "Snap Distance",
                "Distance error that triggers a hard snap for this tier. Higher avoids snaps but can show larger smoothing errors.");
            DrawNestedProfileProperty(
                tier,
                "syncIK",
                "Sync IK",
                "Synchronizes IK state for this tier. Disable at distance to reduce bandwidth and CPU.");
            DrawNestedProfileProperty(
                tier,
                "syncAnimation",
                "Sync Animation",
                "Synchronizes animation states and gestures for this tier. Keep enabled where attacks, aiming, or visible emotes matter.");
            DrawNestedProfileProperty(
                tier,
                "syncCore",
                "Sync Core",
                "Synchronizes core character state such as props, ragdoll, invincibility, poise, and busy state for this tier.");
            DrawNestedProfileProperty(
                tier,
                "syncCombat",
                "Sync Combat",
                "Synchronizes combat-facing data for this tier. Keep enabled where hits, reactions, and combat feedback must remain visible.");
            DrawNestedProfileProperty(
                tier,
                "animationStateRate",
                "Animation State Rate",
                "Rate limit for continuous animation state synchronization in this tier.");
            DrawNestedProfileProperty(
                tier,
                "animationGestureRate",
                "Animation Gesture Rate",
                "Rate limit for gesture/one-shot animation synchronization in this tier.");
        }

        private void DrawSpawnerSection()
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Player Spawning", EditorStyles.boldLabel);

            m_CreatePlayerSpawner = EditorGUILayout.ToggleLeft(
                new GUIContent("Create / Reuse PurrNet Player Spawner", "Adds PurrNetDemoPlayerSpawner with four default spawn points."),
                m_CreatePlayerSpawner);
            using (new EditorGUI.DisabledScope(!m_CreatePlayerSpawner))
            {
                m_PlayerPrefab = (GameObject)EditorGUILayout.ObjectField(
                    new GUIContent(
                        "Player Prefab",
                        "Optional prefab spawned by PurrNet. Assigning one is recommended because the wizard can register it and prepare the prefab with the selected player-side networking components."),
                    m_PlayerPrefab,
                    typeof(GameObject),
                    false);

                using (new EditorGUI.DisabledScope(m_PlayerPrefab == null))
                {
                    m_ConfigurePlayerPrefab = EditorGUILayout.ToggleLeft(
                        new GUIContent(
                            "Prepare referenced Player Prefab",
                            "When enabled, the wizard edits the prefab asset, adds NetworkIdentity, NetworkCharacter, PurrNet auto-init, and selected module controllers, and authors Character.IsPlayer=false so only the spawned local owner becomes the GC2 player at runtime."),
                        m_ConfigurePlayerPrefab);

                    using (new EditorGUI.DisabledScope(!m_ConfigurePlayerPrefab))
                    {
                        m_ConfigurePlayerPrefabKernel = EditorGUILayout.ToggleLeft(
                            new GUIContent(
                                "Use network-ready GC2 character units",
                                "Replaces the prefab's GC2 Player, Motion, Driver, Facing, and Animim units with the networking layer variants used by the demo scenes. Disable if you want to configure the Character kernel manually."),
                            m_ConfigurePlayerPrefabKernel);

                        m_PlayerPrefabUsesLocalVariables = EditorGUILayout.ToggleLeft(
                            new GUIContent(
                                "Player uses local GC2 Variables",
                                "Adds NetworkVariableController to the player prefab. Enable only when the player prefab has Local Name/List Variables that need networked writes or snapshots."),
                            m_PlayerPrefabUsesLocalVariables);

                        using (new EditorGUI.DisabledScope(!m_PlayerPrefabUsesLocalVariables))
                        {
                            m_PlayerVariableProfile = (NetworkVariableProfile)EditorGUILayout.ObjectField(
                                new GUIContent(
                                    "Player Variable Profile",
                                    "Optional NetworkVariableProfile for the player's Local Name/List Variables. You can leave this empty and assign the profile on the prefab later."),
                                m_PlayerVariableProfile,
                                typeof(NetworkVariableProfile),
                                false);
                        }

                        DrawPlayerPreRegisteredClipsSection();
                    }
                }

                m_NetworkPrefabs = (NetworkPrefabs)EditorGUILayout.ObjectField(
                    new GUIContent(
                        "NetworkPrefabs Asset",
                        "Optional PurrNet NetworkPrefabs ScriptableObject asset assigned to the NetworkManager. This is a registry/list of spawnable network prefabs, not the player prefab itself."),
                    m_NetworkPrefabs,
                    typeof(NetworkPrefabs),
                    false);
                m_CreateNetworkPrefabsAsset = EditorGUILayout.ToggleLeft(
                    new GUIContent(
                        "Create NetworkPrefabs asset if missing",
                        $"Creates a PurrNet NetworkPrefabs ScriptableObject asset in {GENERATED_ASSET_FOLDER} when a Player Prefab is assigned and no asset is selected."),
                    m_CreateNetworkPrefabsAsset);

            }

            EditorGUILayout.HelpBox(
                "Recommended: assign your GC2 Player Prefab here. The Player Prefab is the character prefab that gets spawned. The NetworkPrefabs Asset is PurrNet's ScriptableObject registry that lists which prefabs may be spawned over the network. " +
                "If prefab preparation is enabled, the wizard will add the transport/core components and selected module controllers to that prefab asset. " +
                "For example, selecting Stats + Melee adds NetworkStatsController and NetworkMeleeController. NetworkVariableController is added only when 'Player uses local GC2 Variables' is enabled. " +
                "Prepared spawned player prefabs are authored with Character.IsPlayer off; NetworkCharacter enables it only for the local owner at runtime.",
                MessageType.Info);
        }

        private void DrawCharacterSelectionPrefabsSection()
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Selectable Player Prefabs", EditorStyles.miniBoldLabel);

            EditorGUILayout.HelpBox(
                "Assign every player prefab that should appear in the generated character selection menu. If this list is empty, the wizard uses the Player Prefab above as the only selectable option.",
                MessageType.Info);

            for (int i = 0; i < m_CharacterSelectionPrefabs.Count; i++)
            {
                EditorGUILayout.BeginHorizontal();
                m_CharacterSelectionPrefabs[i] = (GameObject)EditorGUILayout.ObjectField(
                    new GUIContent(
                        $"Selectable {i + 1}",
                        "GC2 player prefab shown as one option in the generated character selection menu."),
                    m_CharacterSelectionPrefabs[i],
                    typeof(GameObject),
                    false);

                if (GUILayout.Button(
                        new GUIContent("Remove", "Removes this selectable prefab slot from the wizard list."),
                        GUILayout.Width(78f)))
                {
                    m_CharacterSelectionPrefabs.RemoveAt(i);
                    i--;
                }
                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(
                    new GUIContent("Add Selectable Prefab", "Adds another selectable player prefab slot.")))
            {
                m_CharacterSelectionPrefabs.Add(null);
            }

            using (new EditorGUI.DisabledScope(m_PlayerPrefab == null || m_CharacterSelectionPrefabs.Contains(m_PlayerPrefab)))
            {
                if (GUILayout.Button(
                        new GUIContent("Add Player Prefab", "Adds the Player Prefab above as a selectable character option.")))
                {
                    m_CharacterSelectionPrefabs.Add(m_PlayerPrefab);
                }
            }
            EditorGUILayout.EndHorizontal();
        }

        private void DrawPlayerPreRegisteredClipsSection()
        {
            EditorGUILayout.Space(4);
            m_PlayerUsesNetworkInstructionClips = EditorGUILayout.ToggleLeft(
                new GUIContent(
                    "Player uses Network Dash / Gesture clips",
                    "Enable when this player can run InstructionNetworkCharacterNavigationDash or InstructionNetworkCharacterGesture with clips referenced only by Visual Scripting assets. The wizard adds those clips to NetworkCharacter.Pre Registered Clips."),
                m_PlayerUsesNetworkInstructionClips);

            using (new EditorGUI.DisabledScope(!m_PlayerUsesNetworkInstructionClips))
            {
                EditorGUILayout.HelpBox(
                    "Add dash, roll, dodge, leap, emote, or other gesture clips that are referenced by Network Dash or Network Play Gesture instructions. " +
                    "Pre-registration lets remote peers resolve the broadcast clip hash even when the clip is not discoverable from the character Animator.",
                    MessageType.Info);

                for (int i = 0; i < m_PlayerPreRegisteredAnimationClips.Count; i++)
                {
                    EditorGUILayout.BeginHorizontal();
                    m_PlayerPreRegisteredAnimationClips[i] = (AnimationClip)EditorGUILayout.ObjectField(
                        new GUIContent(
                            $"Clip {i + 1}",
                            "AnimationClip to add to NetworkCharacter.Pre Registered Clips on the prepared player prefab."),
                        m_PlayerPreRegisteredAnimationClips[i],
                        typeof(AnimationClip),
                        false);

                    if (GUILayout.Button(
                            new GUIContent(
                                "Remove",
                                "Removes this clip from the wizard list. Existing clips already on the prefab are not removed."),
                            GUILayout.Width(78f)))
                    {
                        m_PlayerPreRegisteredAnimationClips.RemoveAt(i);
                        i--;
                    }
                    EditorGUILayout.EndHorizontal();
                }

                if (GUILayout.Button(
                        new GUIContent(
                            "Add Pre Registered Clip",
                            "Adds another AnimationClip slot for Network Dash, Network Play Gesture, or similar networked gesture instructions.")))
                {
                    m_PlayerPreRegisteredAnimationClips.Add(null);
                }
            }
        }

        private List<GameObject> GetSelectablePlayerPrefabs()
        {
            var prefabs = new List<GameObject>();

            if (m_CreateCharacterSelectionUI)
            {
                AddUniquePrefab(prefabs, m_CharacterSelectionPrefabs);
            }

            if (prefabs.Count == 0)
            {
                AddUniquePrefab(prefabs, m_PlayerPrefab);
            }

            return prefabs;
        }

        private List<GameObject> GetSpawnablePlayerPrefabs()
        {
            List<GameObject> prefabs = GetSelectablePlayerPrefabs();
            AddUniquePrefab(prefabs, m_PlayerPrefab);
            return prefabs;
        }

        private static void AddUniquePrefab(List<GameObject> prefabs, IEnumerable<GameObject> candidates)
        {
            if (prefabs == null || candidates == null) return;

            foreach (GameObject candidate in candidates)
            {
                AddUniquePrefab(prefabs, candidate);
            }
        }

        private static void AddUniquePrefab(List<GameObject> prefabs, GameObject candidate)
        {
            if (prefabs == null || candidate == null || prefabs.Contains(candidate)) return;
            prefabs.Add(candidate);
        }

        private static string GetPrefabDisplayName(GameObject prefab, int index)
        {
            return prefab != null
                ? ObjectNames.NicifyVariableName(prefab.name)
                : $"Character {index + 1}";
        }

        private void DrawRuntimeUISection()
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Runtime UI", EditorStyles.boldLabel);

            m_CreateCharacterSelectionUI = EditorGUILayout.ToggleLeft(
                new GUIContent("Create / Reuse PurrNet Character Selection UI", "Adds PurrNetDemoCharacterSelection, a selection canvas, GC2 ButtonInstructions buttons, and configures the PurrNet player spawner to wait for the selected prefab."),
                m_CreateCharacterSelectionUI);
            if (m_CreateCharacterSelectionUI)
            {
                DrawCharacterSelectionPrefabsSection();
            }

            m_CreateDemoCanvasUI = EditorGUILayout.ToggleLeft(
                new GUIContent("Create / Reuse PurrNet Demo UI", "Adds the UGUI host/join overlay used by the PurrNet demo scenes."),
                m_CreateDemoCanvasUI);
            bool createLobbyUI = EditorGUILayout.ToggleLeft(
                new GUIContent(
                    "Create / Reuse Multiplayer Lobby UI",
                    "Adds the shared GC2 lobby menu and a PurrNet Direct, LAN discovery, or room-code service."),
                m_CreateLobbyUI);
            if (createLobbyUI && !m_CreateLobbyUI)
            {
                m_CreateDemoCanvasUI = false;
            }
            m_CreateLobbyUI = createLobbyUI;
            if (m_CreateLobbyUI)
            {
                LobbySetupMode lobbyMode = (LobbySetupMode)EditorGUILayout.EnumPopup(
                    new GUIContent(
                        "Lobby Connection",
                        "LAN discovers UDP hosts on the same network. Direct uses address/port. Room Code uses PurrTransport and requires a production relay deployment."),
                    m_LobbyMode);
                if (lobbyMode != m_LobbyMode)
                {
                    m_LobbyMode = lobbyMode;
                    if (m_LobbyMode == LobbySetupMode.RoomCode)
                    {
                        m_Transport = TransportChoice.PurrTransport;
                    }
                    else if (m_Transport == TransportChoice.PurrTransport)
                    {
                        m_Transport = TransportChoice.UDP;
                    }
                }

                if (m_LobbyMode == LobbySetupMode.RoomCode)
                {
                    EditorGUILayout.HelpBox(
                        "PurrTransport room codes do not provide a public browser. The bundled " +
                        "Purr relay endpoint is development-only; production games must use a " +
                        "self-hosted relay endpoint.",
                        MessageType.Warning);
                }
            }
            m_CreateControlsUI = EditorGUILayout.ToggleLeft(
                new GUIContent("Create / Reuse PurrNet Demo Controls UI", "Adds a small UGUI panel summarizing the configured modules and startup controls."),
                m_CreateControlsUI);
            m_CreateChatUI = EditorGUILayout.ToggleLeft(
                new GUIContent("Create / Reuse PurrNet Chat Box UI", "Adds a lower-left UGUI chat box. Messages are relayed through PurrNet, so it works with UDP, SteamTransport, WebTransport, Local, or any other PurrNet transport."),
                m_CreateChatUI);

            if (m_CreateCharacterSelectionUI && m_CreateDemoCanvasUI)
            {
                EditorGUILayout.HelpBox(
                    "The Character Selection UI already includes Host, Join, and Stop buttons. Disable PurrNet Demo UI if you do not want duplicate connection controls.",
                    MessageType.Info);
            }

            if (m_CreateLobbyUI && m_CreateDemoCanvasUI)
            {
                EditorGUILayout.HelpBox(
                    "Multiplayer Lobby UI replaces the simple PurrNet Demo UI connection " +
                    "controls. Disable one of them to avoid duplicate session starts.",
                    MessageType.Warning);
            }

            using (new EditorGUI.DisabledScope(!m_CreateCharacterSelectionUI && !m_CreateDemoCanvasUI && !m_CreateLobbyUI && !m_CreateControlsUI && !m_CreateChatUI))
            {
                m_UITitle = EditorGUILayout.TextField(
                    new GUIContent("UI Title", "Title shown by the generated runtime connection UI and controls panel."),
                    m_UITitle);
                m_UISubtitle = EditorGUILayout.TextField(
                    new GUIContent("UI Subtitle", "Subtitle shown by the generated runtime connection UI and controls panel."),
                    m_UISubtitle);
            }
        }

        private void DrawHierarchySection()
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Hierarchy", EditorStyles.boldLabel);
            m_ParentUnderRoot = EditorGUILayout.ToggleLeft(
                new GUIContent("Parent generated objects under a root", "Keeps the scene hierarchy organized."),
                m_ParentUnderRoot);
            using (new EditorGUI.DisabledScope(!m_ParentUnderRoot))
            {
                m_RootName = EditorGUILayout.TextField(
                    new GUIContent("Root Name", "Scene hierarchy object that receives generated managers, bridges, UI, and spawner objects."),
                    m_RootName);
            }
        }

        private void DrawReviewSection()
        {
            EditorGUILayout.LabelField("Review", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(BuildSummary(), MessageType.None);

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Selected Modules", EditorStyles.miniBoldLabel);
            EditorGUILayout.LabelField($"Core + Variables + Animation/Motion: Yes");
            EditorGUILayout.LabelField($"Stats: {YesNo(m_ModuleStats)}");
            EditorGUILayout.LabelField($"Inventory: {YesNo(m_ModuleInventory)}");
            if (m_ModuleInventory)
            {
                DrawInventoryRequirements();
            }
            EditorGUILayout.LabelField($"Melee: {YesNo(m_ModuleMelee)}");
            if (m_ModuleMelee)
            {
                DrawMeleePatchRequirement();
            }
            EditorGUILayout.LabelField($"Shooter: {YesNo(m_ModuleShooter)}");
            if (m_ModuleShooter || IsSceneBridgeConfigured(PURRNET_SHOOTER_BRIDGE_TYPE))
            {
                DrawShooterPatchRequirements();
                DrawShooterRegistrationWarning();
                DrawShooterPhysicsSimulationWarning();
            }
            EditorGUILayout.LabelField($"Quests: {YesNo(m_ModuleQuests)}");
            EditorGUILayout.LabelField($"Dialogue: {YesNo(m_ModuleDialogue)}");
            EditorGUILayout.LabelField($"Traversal: {YesNo(m_ModuleTraversal)}");
            if (IsTraversalEnabledForSetupOrScene())
            {
                DrawTraversalPatchRequirement();
            }
            EditorGUILayout.LabelField($"Abilities: {YesNo(m_ModuleAbilities)}");

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Generated Scene Objects", EditorStyles.miniBoldLabel);
            EditorGUILayout.LabelField($"NetworkManager: {YesNo(m_CreateNetworkManager)}");
            EditorGUILayout.LabelField($"Core managers: {YesNo(m_CreateCoreManagers)}");
            EditorGUILayout.LabelField($"Core bridges: {YesNo(m_CreateCoreBridges)}");
            EditorGUILayout.LabelField($"Module managers: {YesNo(m_CreateModuleManagers)}");
            EditorGUILayout.LabelField($"Module bridges: {YesNo(m_CreateModuleBridges)}");
            EditorGUILayout.LabelField($"Player spawner: {YesNo(m_CreatePlayerSpawner)}");
            EditorGUILayout.LabelField($"Player prefab: {(m_PlayerPrefab != null ? m_PlayerPrefab.name : "None")}");
            EditorGUILayout.LabelField($"Prepare player prefab: {YesNo(m_PlayerPrefab != null && m_ConfigurePlayerPrefab)}");
            DrawLegacyCoreControllerWarning();
            EditorGUILayout.LabelField($"Network-ready Character units: {YesNo(m_PlayerPrefab != null && m_ConfigurePlayerPrefab && m_ConfigurePlayerPrefabKernel)}");
            EditorGUILayout.LabelField($"Prediction backend: {m_PredictionBackend}");
            if (m_PredictionBackend == NetworkPredictionBackend.PurrDiction)
            {
                EditorGUILayout.LabelField($"PurrDiction movement adapter: {GetPurrDictionMovementAdapterSummary()}");
                EditorGUILayout.LabelField($"PurrDiction installed: {YesNo(IsPurrDictionInstalled())}");
                EditorGUILayout.LabelField($"PurrDiction integration compiled: {YesNo(IsPurrDictionIntegrationCompiled())}");
                EditorGUILayout.LabelField($"PredictionManager will be created: Yes");
            }
            EditorGUILayout.LabelField($"Player local variables: {YesNo(m_PlayerPrefab != null && m_ConfigurePlayerPrefab && m_PlayerPrefabUsesLocalVariables)}");
            EditorGUILayout.LabelField($"Pre-registered animation clips: {(m_PlayerUsesNetworkInstructionClips ? GetValidPlayerPreRegisteredAnimationClips().Count.ToString() : "No")}");
            EditorGUILayout.LabelField($"Character Selection UI: {YesNo(m_CreateCharacterSelectionUI)}");
            if (m_CreateCharacterSelectionUI)
            {
                EditorGUILayout.LabelField($"Selectable prefabs: {GetSelectablePlayerPrefabs().Count}");
            }
            EditorGUILayout.LabelField($"Demo UI: {YesNo(m_CreateDemoCanvasUI)}");
            EditorGUILayout.LabelField($"Multiplayer Lobby UI: {YesNo(m_CreateLobbyUI)}");
            if (m_CreateLobbyUI)
            {
                EditorGUILayout.LabelField($"Lobby connection: {m_LobbyMode}");
            }
            EditorGUILayout.LabelField($"Controls UI: {YesNo(m_CreateControlsUI)}");
            EditorGUILayout.LabelField($"Chat UI: {YesNo(m_CreateChatUI)}");

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Network Defaults", EditorStyles.miniBoldLabel);
            EditorGUILayout.LabelField($"Transport: {m_Transport}");
            EditorGUILayout.LabelField($"Address: {m_Address}");
            EditorGUILayout.LabelField($"Port: {m_Port}");
            EditorGUILayout.LabelField($"Tick rate: {(m_SetTickRate ? m_TickRate.ToString() : "unchanged")}");
            EditorGUILayout.LabelField($"Session preset: {m_SessionPreset}");
            EditorGUILayout.LabelField($"Expected players: {m_ExpectedPlayers}");
            EditorGUILayout.LabelField($"Project template: {m_ProjectTemplate}");

            DrawCurrentSceneConfigurationWarnings();

            EditorGUILayout.Space(8);
            EditorGUILayout.HelpBox(
                "This page is only a review. Use Back or the step tabs above to change anything before applying the setup.",
                MessageType.Info);
        }

        private void DrawLegacyCoreControllerWarning()
        {
            List<GameObject> prefabs = GetSpawnablePlayerPrefabs();
            int legacyControllerCount = 0;
            var affectedPrefabs = new List<string>();
            for (int i = 0; i < prefabs.Count; i++)
            {
                GameObject prefab = prefabs[i];
                if (prefab == null) continue;

                NetworkCoreController[] controllers =
                    prefab.GetComponentsInChildren<NetworkCoreController>(true);
                if (controllers == null || controllers.Length == 0) continue;

                legacyControllerCount += controllers.Length;
                affectedPrefabs.Add(prefab.name);
            }

            if (legacyControllerCount == 0) return;

            EditorGUILayout.HelpBox(
                $"The spawnable Player Prefab set contains {legacyControllerCount} legacy per-character " +
                $"NetworkCoreController component(s) in {string.Join(", ", affectedPrefabs)}. " +
                "Core is now owned by NetworkCoreManager. " +
                (m_ConfigurePlayerPrefab
                    ? "Prefab preparation will remove the legacy component(s)."
                    : "Remove them from the prefab or enable prefab preparation."),
                MessageType.Warning);
        }

        private void DrawShooterRegistrationWarning()
        {
            Type bridgeType = Type.GetType(PURRNET_SHOOTER_BRIDGE_TYPE);
            Component bridge = bridgeType != null ? FindSceneComponent(bridgeType) : null;
            if (bridge != null)
            {
                var serializedBridge = new SerializedObject(bridge);
                SerializedProperty structured = serializedBridge.FindProperty("m_WeaponRegistrations");
                SerializedProperty legacy = serializedBridge.FindProperty("m_RegisterWeapons");
                if (HasRegisteredShooterWeapon(structured, true))
                {
                    return;
                }

                if (HasRegisteredShooterWeapon(legacy, false))
                {
                    EditorGUILayout.HelpBox(
                        "PurrNet Shooter Bridge is using only the hidden legacy Weapon/Prefab/Handle " +
                        "arrays. Migrate those entries to Weapon Registrations so null entries, duplicate " +
                        "hashes, and mismatched assets can be validated reliably.",
                        MessageType.Warning);
                    return;
                }
            }

            // Applying the wizard will add the bundled demo weapon only when module bridges are
            // also being configured. Do not hide the warning for a manager-only/manual setup.
            if (m_CreateModuleBridges && m_RegisterInstalledDemoAssets &&
                LoadAsset(AK_WEAPON_PATH) != null)
            {
                return;
            }

            EditorGUILayout.HelpBox(
                "Shooter is enabled but no weapon registration is configured. Remote weapon models and " +
                "authored shot/impact effects require a Weapon Registration on PurrNet Shooter Bridge. " +
                "You can continue and author the registrations after setup.",
                MessageType.Warning);
        }

        private static void DrawShooterPhysicsSimulationWarning()
        {
            if (Physics.simulationMode != SimulationMode.Script) return;

            EditorGUILayout.HelpBox(
                "Unity 3D Physics Simulation Mode is Script. Ordinary Shooter Rigidbody targets will " +
                "receive confirmed impulses but cannot move unless an active system calls " +
                "PhysicsScene.Simulate. Built-in PurrNet scenes require Project Settings > Physics > " +
                "Auto Simulation. Script mode is valid only when PurrDiction or another physics owner " +
                "is actively simulating this scene.",
                MessageType.Warning);
        }

        private static bool HasRegisteredShooterWeapon(
            SerializedProperty registrations,
            bool structured)
        {
            if (registrations == null || !registrations.isArray) return false;

            for (int i = 0; i < registrations.arraySize; i++)
            {
                SerializedProperty entry = registrations.GetArrayElementAtIndex(i);
                SerializedProperty weapon = structured
                    ? entry.FindPropertyRelative("Weapon")
                    : entry;
                if (weapon != null && weapon.objectReferenceValue != null) return true;
            }

            return false;
        }

        private void DrawCurrentSceneConfigurationWarnings()
        {
            if (PurrNetInstallCompatibility.TryGetMixedLiteNetLibLayout(
                    out string compatibilityMessage,
                    out _))
            {
                EditorGUILayout.HelpBox(compatibilityMessage, MessageType.Error);
            }

            NetworkManager manager = FindSceneComponent<NetworkManager>();
            RawNetManager rawManager = FindSceneComponent<RawNetManager>();

            if (manager == null)
            {
                EditorGUILayout.Space(8);
                EditorGUILayout.LabelField("Current Scene Validation", EditorStyles.miniBoldLabel);

                if (rawManager != null)
                {
                    EditorGUILayout.HelpBox(
                        $"The scene contains PurrNet.RawNetManager '{rawManager.name}', but the GC2 " +
                        "PurrNet integration requires PurrNet.NetworkManager (Add Component > " +
                        "PurrNet > Network Manager). RawNetManager is a separate, incompatible " +
                        "type. Keep Create / Reuse NetworkManager enabled and apply this wizard, " +
                        "then transfer/check the intended transport and startup settings before " +
                        "disabling or removing the old Raw Net Manager.",
                        MessageType.Error);
                }
                else
                {
                    EditorGUILayout.HelpBox(
                        "No PurrNet.NetworkManager exists in the loaded scene. Keep Create / Reuse " +
                        "NetworkManager enabled and apply the wizard from the Review page.",
                        MessageType.Warning);
                }

                return;
            }

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Current Scene Validation", EditorStyles.miniBoldLabel);

            if (rawManager != null)
            {
                EditorGUILayout.HelpBox(
                    $"This scene also contains PurrNet.RawNetManager '{rawManager.name}'. GC2 bridges " +
                    $"use PurrNet.NetworkManager '{manager.name}', not RawNetManager. Verify that the " +
                    "Raw Net Manager is intentional and that it is not auto-starting a competing transport.",
                    MessageType.Warning);
            }

            DrawTransportConfigurationWarnings(manager);
            DrawBridgeManagerReferenceWarnings(manager);
            DrawDemoUIAutoStartWarning(manager);
        }

        private void DrawTransportConfigurationWarnings(NetworkManager manager)
        {
            GenericTransport[] attachedTransports = manager.GetComponents<GenericTransport>();
            var enabledTransports = new List<GenericTransport>();
            for (int i = 0; i < attachedTransports.Length; i++)
            {
                GenericTransport transport = attachedTransports[i];
                if (transport != null && transport.isActiveAndEnabled)
                {
                    enabledTransports.Add(transport);
                }
            }

            if (enabledTransports.Count > 1)
            {
                EditorGUILayout.HelpBox(
                    $"NetworkManager '{manager.name}' has {enabledTransports.Count} enabled transports " +
                    $"({string.Join(", ", enabledTransports.ConvertAll(item => item.GetType().Name))}). " +
                    "Keep only the intended transport enabled to avoid ambiguous startup behavior.",
                    MessageType.Warning);
            }

            GenericTransport selected = manager.transport;
            if (selected == null)
            {
                EditorGUILayout.HelpBox(
                    $"NetworkManager '{manager.name}' has no selected transport.",
                    MessageType.Warning);
                return;
            }

            if (!selected.isActiveAndEnabled || !enabledTransports.Contains(selected))
            {
                EditorGUILayout.HelpBox(
                    $"NetworkManager '{manager.name}' selects '{selected.GetType().Name}', but that " +
                    "transport is disabled or is not attached to the NetworkManager object.",
                    MessageType.Warning);
            }

            if (m_Transport != TransportChoice.ExistingOrManual &&
                !MatchesChoice(selected, m_Transport))
            {
                EditorGUILayout.HelpBox(
                    $"The current scene selects '{selected.GetType().Name}', while this setup will use " +
                    $"'{m_Transport}'. Applying the wizard will update the selected transport.",
                    MessageType.Warning);
            }
        }

        private static void DrawBridgeManagerReferenceWarnings(NetworkManager manager)
        {
            MonoBehaviour[] behaviours = FindSceneComponents<MonoBehaviour>();
            var missing = new List<string>();
            var mismatched = new List<string>();

            for (int i = 0; i < behaviours.Length; i++)
            {
                MonoBehaviour behaviour = behaviours[i];
                if (!IsPurrNetTransportBridge(behaviour)) continue;

                var serializedBridge = new SerializedObject(behaviour);
                SerializedProperty managerProperty = serializedBridge.FindProperty("m_NetworkManager");
                if (managerProperty == null ||
                    managerProperty.propertyType != SerializedPropertyType.ObjectReference)
                {
                    continue;
                }

                var referencedManager = managerProperty.objectReferenceValue as NetworkManager;
                string bridgeName = $"{behaviour.name} ({behaviour.GetType().Name})";
                if (referencedManager == null)
                {
                    missing.Add(bridgeName);
                }
                else if (referencedManager != manager)
                {
                    mismatched.Add(bridgeName);
                }
            }

            if (missing.Count > 0)
            {
                EditorGUILayout.HelpBox(
                    "PurrNet bridge NetworkManager references are not explicitly assigned: " +
                    $"{string.Join(", ", missing)}. Assign them to '{manager.name}' or enable their " +
                    "corresponding bridge setup before applying the wizard.",
                    MessageType.Warning);
            }

            if (mismatched.Count > 0)
            {
                EditorGUILayout.HelpBox(
                    $"PurrNet bridges reference a different NetworkManager than '{manager.name}': " +
                    $"{string.Join(", ", mismatched)}. Use one manager consistently for the base and " +
                    "module bridges.",
                    MessageType.Warning);
            }
        }

        private static bool IsPurrNetTransportBridge(MonoBehaviour behaviour)
        {
            if (behaviour == null) return false;
            if (behaviour is PurrNetTransportBridge) return true;

            Type type = behaviour.GetType();
            string typeNamespace = type.Namespace ?? string.Empty;
            return type.Name.EndsWith("TransportBridge", StringComparison.Ordinal) &&
                   typeNamespace.Contains(".Transport.PurrNet");
        }

        private static void DrawDemoUIAutoStartWarning(NetworkManager manager)
        {
            if (FindSceneComponent<PurrNetDemoCanvasUI>() == null) return;
            if (manager.startServerFlags == StartFlags.None &&
                manager.startClientFlags == StartFlags.None)
            {
                return;
            }

            EditorGUILayout.HelpBox(
                $"The scene contains PurrNet Demo UI, but NetworkManager '{manager.name}' still has " +
                $"auto-start flags (server={manager.startServerFlags}, client={manager.startClientFlags}). " +
                "Clear both flags so the Host and Join buttons exclusively control startup.",
                MessageType.Warning);
        }

        private bool RunSetup()
        {
            try
            {
                if (!ValidatePredictionBackendSelection())
                {
                    return false;
                }

                string inventoryPatchReport = null;
                if (m_ModuleInventory &&
                    !EnsureInventoryPatchAppliedWithPrompt(
                        "PurrNet Scene Setup Wizard",
                        out inventoryPatchReport))
                {
                    if (!string.IsNullOrEmpty(inventoryPatchReport))
                    {
                        Debug.LogWarning($"[PurrNetSceneSetupWizard] {inventoryPatchReport}");
                    }
                    return false;
                }

                if (m_ModuleInventory && !string.IsNullOrEmpty(inventoryPatchReport))
                {
                    Debug.Log($"[PurrNetSceneSetupWizard] {inventoryPatchReport}");
                }

                string meleePatchReport = null;
                if (m_ModuleMelee &&
                    !EnsureMeleePatchAppliedWithPrompt(
                        "PurrNet Scene Setup Wizard",
                        out meleePatchReport))
                {
                    if (!string.IsNullOrEmpty(meleePatchReport))
                    {
                        Debug.LogWarning($"[PurrNetSceneSetupWizard] {meleePatchReport}");
                    }
                    return false;
                }

                if (m_ModuleMelee && !string.IsNullOrEmpty(meleePatchReport))
                {
                    Debug.Log($"[PurrNetSceneSetupWizard] {meleePatchReport}");
                }

                string traversalPatchReport = null;
                if (m_ModuleTraversal &&
                    !EnsureTraversalPatchAppliedWithPrompt(
                        "PurrNet Scene Setup Wizard",
                        out traversalPatchReport))
                {
                    if (!string.IsNullOrEmpty(traversalPatchReport))
                    {
                        Debug.LogWarning($"[PurrNetSceneSetupWizard] {traversalPatchReport}");
                    }
                    return false;
                }

                if (m_ModuleTraversal && !string.IsNullOrEmpty(traversalPatchReport))
                {
                    Debug.Log($"[PurrNetSceneSetupWizard] {traversalPatchReport}");
                }

                string shooterPatchReport = null;
                if (m_ModuleShooter &&
                    !EnsureShooterPatchAppliedWithPrompt(
                        "PurrNet Scene Setup Wizard",
                        out shooterPatchReport))
                {
                    if (!string.IsNullOrEmpty(shooterPatchReport))
                    {
                        Debug.LogWarning($"[PurrNetSceneSetupWizard] {shooterPatchReport}");
                    }
                    return false;
                }

                if (m_ModuleShooter && !string.IsNullOrEmpty(shooterPatchReport))
                {
                    Debug.Log($"[PurrNetSceneSetupWizard] {shooterPatchReport}");
                }

                string shooterSightPatchReport = null;
                if (m_ModuleShooter &&
                    !ShooterSightPatchRequirement.EnsureAppliedWithPrompt(
                        "PurrNet Scene Setup Wizard",
                        out shooterSightPatchReport))
                {
                    if (!string.IsNullOrEmpty(shooterSightPatchReport))
                    {
                        Debug.LogWarning($"[PurrNetSceneSetupWizard] {shooterSightPatchReport}");
                    }
                    return false;
                }

                if (m_ModuleShooter && !string.IsNullOrEmpty(shooterSightPatchReport))
                {
                    Debug.Log($"[PurrNetSceneSetupWizard] {shooterSightPatchReport}");
                }

                Undo.IncrementCurrentGroup();
                Undo.SetCurrentGroupName("PurrNet Scene Setup");
                int group = Undo.GetCurrentGroup();

                GameObject root = ResolveRoot();
                string playerPrefabReport = EnsurePlayerPrefabSetup();
                if (!string.IsNullOrEmpty(playerPrefabReport))
                {
                    Debug.Log(playerPrefabReport);
                }

                NetworkManager manager = FindOrCreateNetworkManager(root);

                if (manager != null)
                {
                    if (m_CreateLobbyUI && m_LobbyMode == LobbySetupMode.RoomCode)
                    {
                        // Room-code sessions are implemented by PurrTransport. Enforce the
                        // matching transport again at Apply time so navigating back to the
                        // Transport page cannot leave a stale incompatible selection.
                        m_Transport = TransportChoice.PurrTransport;
                    }
                    EnsureTransport(manager);
                    ConfigureNetworkManager(manager);
                }

                EnsurePurrDictionPredictionManager(root);

                NetworkSessionProfile sessionProfile = ResolveOrCreateSessionProfile();
                PurrNetTransportBridge coreBridge = null;

                if (m_CreateCoreBridges)
                {
                    coreBridge = EnsureCoreTransportBridge(root, manager, sessionProfile);
                    EnsureCoreFeatureTransportBridge(root, manager);
                    EnsureVariableBridge(root, manager, coreBridge);
                    EnsureAnimationMotionBridge(root, manager);
                }

                if (m_CreateCoreManagers)
                {
                    EnsureCoreManagers(root);
                }

                if (m_CreateModuleManagers)
                {
                    EnsureSelectedModuleManagers(root);
                }

                if (m_CreateModuleBridges)
                {
                    EnsureSelectedModuleBridges(root, manager, coreBridge);
                }

                PurrNetDemoCharacterSelection characterSelection = null;
                if (m_CreateCharacterSelectionUI)
                {
                    characterSelection = EnsureCharacterSelectionUI(root, manager);
                }

                if (m_CreatePlayerSpawner)
                {
                    EnsurePlayerSpawner(root, characterSelection);
                }

                if (m_CreateDemoCanvasUI)
                {
                    EnsureDemoCanvasUI(root, manager);
                }

                if (m_CreateLobbyUI)
                {
                    EnsureLobbyUI(root, manager);
                }

                if (m_CreateControlsUI)
                {
                    EnsureControlsUI(root);
                }

                if (m_CreateChatUI)
                {
                    EnsureChatUI(root, manager);
                }

                Undo.CollapseUndoOperations(group);

                if (m_ModuleInventory)
                {
                    int convertedPickups = InventorySceneSetupTools.ConvertStockScenePickups(false);
                    if (convertedPickups > 0)
                    {
                        Debug.Log(
                            $"[PurrNetSceneSetupWizard] Converted or updated {convertedPickups} " +
                            "stock Inventory pickup(s) with authoritative scene identities.");
                    }
                }

                var activeScene = EditorSceneManager.GetActiveScene();
                EditorSceneManager.MarkSceneDirty(activeScene);
                AssetDatabase.SaveAssets();

                EditorUtility.DisplayDialog(
                    "PurrNet Scene Setup",
                    "Scene updated. The selected GC2 modules now have their PurrNet managers and bridges in the scene.",
                    "OK");
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                EditorUtility.DisplayDialog("PurrNet Scene Setup", $"Setup failed: {ex.Message}", "OK");
                return false;
            }
        }

        private GameObject ResolveRoot()
        {
            if (!m_ParentUnderRoot) return null;

            var root = GameObject.Find(m_RootName);
            if (root != null) return root;

            root = new GameObject(m_RootName);
            Undo.RegisterCreatedObjectUndo(root, "Create PurrNet Root");
            return root;
        }

        private NetworkManager FindOrCreateNetworkManager(GameObject root)
        {
            var existing = FindSceneComponent<NetworkManager>();
            if (existing != null) return existing;
            if (!m_CreateNetworkManager) return null;

            var go = CreateChild("NetworkManager", root);
            return Undo.AddComponent<NetworkManager>(go);
        }

        private void EnsureTransport(NetworkManager manager)
        {
            if (manager == null || m_Transport == TransportChoice.ExistingOrManual) return;

            if (manager.transport != null && MatchesChoice(manager.transport, m_Transport))
            {
                ApplyTransportAddressPort(manager.transport);
                return;
            }

            Type type = TransportTypeFor(m_Transport);
            if (type == null) return;

            var transport = manager.GetComponent(type) as GenericTransport;
            if (transport == null)
            {
                transport = Undo.AddComponent(manager.gameObject, type) as GenericTransport;
            }

            if (transport == null) return;

            ApplyTransportAddressPort(transport);

            var managerSO = new SerializedObject(manager);
            var transportField = managerSO.FindProperty("_transport");
            if (transportField != null) transportField.objectReferenceValue = transport;
            managerSO.ApplyModifiedProperties();
            EditorUtility.SetDirty(manager);
        }

        private void ConfigureNetworkManager(NetworkManager manager)
        {
            if (manager == null) return;

            var managerSO = new SerializedObject(manager);

            if (m_ClearStartFlags)
            {
                Undo.RecordObject(manager, "Clear PurrNet Start Flags");
                manager.startServerFlags = StartFlags.None;
                manager.startClientFlags = StartFlags.None;
            }

            if (m_SetTickRate)
            {
                var tickRateField = managerSO.FindProperty("_tickRate");
                if (tickRateField != null) tickRateField.intValue = m_TickRate;
            }

            if (m_AssignDefaultRules)
            {
                AssignDefaultRulesAndVisibility(managerSO);
            }

            NetworkPrefabs prefabs = ResolveOrCreateNetworkPrefabs(manager);
            if (prefabs != null)
            {
                var prefabsField = managerSO.FindProperty("_networkPrefabs");
                if (prefabsField != null) prefabsField.objectReferenceValue = prefabs;
            }

            managerSO.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(manager);
        }

        private PurrNetTransportBridge EnsureCoreTransportBridge(
            GameObject root,
            NetworkManager manager,
            NetworkSessionProfile sessionProfile)
        {
            var bridge = FindOrCreateComponent<PurrNetTransportBridge>("PurrNet Transport Bridge", root);

            var so = new SerializedObject(bridge);
            AssignObjectReference(so, "m_NetworkManager", manager);
            AssignObjectReference(so, "m_GlobalSessionProfile", sessionProfile);
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(bridge);

            return bridge;
        }

        private void EnsureCoreManagers(GameObject root)
        {
            GC2SceneSetupShared.EnsureCoreManagers(
                root,
                (type, objectName, parent) =>
                    EnsureComponentByType(type.AssemblyQualifiedName, objectName, parent));
        }

        private void EnsureCoreFeatureTransportBridge(GameObject root, NetworkManager manager)
        {
            var bridge = FindOrCreateComponent<PurrNetCoreTransportBridge>("PurrNet Core Bridge", root);
            var so = new SerializedObject(bridge);
            AssignObjectReference(so, "m_NetworkManager", manager);
            SetBool(so, "m_LogNetworkMessages", false);
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(bridge);
        }

        private void EnsureVariableBridge(GameObject root, NetworkManager manager, PurrNetTransportBridge coreBridge)
        {
            var bridge = FindOrCreateComponent<PurrNetVariableTransportBridge>("PurrNet Variable Bridge", root);
            var so = new SerializedObject(bridge);
            AssignObjectReference(so, "m_NetworkManager", manager);
            AssignObjectReference(so, "m_CoreBridge", coreBridge);
            SetBool(so, "m_LogNetworkMessages", false);
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(bridge);
        }

        private void EnsureAnimationMotionBridge(GameObject root, NetworkManager manager)
        {
            var bridge = FindOrCreateComponent<PurrNetAnimationMotionTransportBridge>("PurrNet Animation Motion Bridge", root);
            var so = new SerializedObject(bridge);
            AssignObjectReference(so, "m_NetworkManager", manager);
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(bridge);
        }

        private void EnsurePurrDictionPredictionManager(GameObject root)
        {
            if (m_PredictionBackend != NetworkPredictionBackend.PurrDiction) return;

            Component manager = EnsureComponentByType(
                PURRDICTION_MANAGER_TYPE,
                "PurrDiction Prediction Manager",
                root);

            if (manager != null)
            {
                EditorUtility.SetDirty(manager);
            }
        }

        private void EnsureSelectedModuleManagers(GameObject root)
        {
            if (m_ModuleMelee) EnsureComponentByType(NETWORK_MELEE_MANAGER_TYPE, "Network Melee Manager", root);
            if (m_ModuleShooter) EnsureComponentByType(NETWORK_SHOOTER_MANAGER_TYPE, "Network Shooter Manager", root);
            if (m_ModuleStats) EnsureComponentByType(NETWORK_STATS_MANAGER_TYPE, "Network Stats Manager", root);
            if (m_ModuleInventory) EnsureComponentByType(NETWORK_INVENTORY_MANAGER_TYPE, "Network Inventory Manager", root);
            if (m_ModuleQuests) ConfigureLogSilenced(EnsureComponentByType(NETWORK_QUESTS_MANAGER_TYPE, "Network Quests Manager", root));
            if (m_ModuleDialogue) ConfigureLogSilenced(EnsureComponentByType(NETWORK_DIALOGUE_MANAGER_TYPE, "Network Dialogue Manager", root));
            if (m_ModuleTraversal) ConfigureLogSilenced(EnsureComponentByType(NETWORK_TRAVERSAL_MANAGER_TYPE, "Network Traversal Manager", root));
            if (m_ModuleAbilities) ConfigureLogSilenced(EnsureComponentByType(NETWORK_ABILITIES_CONTROLLER_TYPE, "Network Abilities Controller", root));

            if (m_ModuleMelee && m_ModuleStats && m_CreateMeleeStatsDamageBridge)
            {
                Component bridge = EnsureComponentByType(
                    NETWORK_MELEE_STATS_DAMAGE_BRIDGE_TYPE,
                    "Network Melee Stats Damage Bridge",
                    root);

                if (bridge != null)
                {
                    var so = new SerializedObject(bridge);
                    var healthAttribute = LoadAsset(HEALTH_ATTRIBUTE_PATH);
                    if (healthAttribute != null) AssignObjectReference(so, "m_HealthAttribute", healthAttribute);
                    so.ApplyModifiedPropertiesWithoutUndo();
                    EditorUtility.SetDirty(bridge);
                }
            }

            if (m_ModuleShooter && m_ModuleStats && m_CreateShooterStatsDamageBridge)
            {
                Component bridge = EnsureComponentByType(
                    NETWORK_SHOOTER_STATS_DAMAGE_BRIDGE_TYPE,
                    "Network Shooter Stats Damage Bridge",
                    root);

                if (bridge != null)
                {
                    var so = new SerializedObject(bridge);
                    var healthAttribute = LoadAsset(HEALTH_ATTRIBUTE_PATH);
                    if (healthAttribute != null) AssignObjectReference(so, "m_HealthAttribute", healthAttribute);
                    so.ApplyModifiedPropertiesWithoutUndo();
                    EditorUtility.SetDirty(bridge);
                }
            }
        }

        private void EnsureSelectedModuleBridges(
            GameObject root,
            NetworkManager manager,
            PurrNetTransportBridge coreBridge)
        {
            if (m_ModuleMelee) ConfigureMeleeBridge(EnsureComponentByType(PURRNET_MELEE_BRIDGE_TYPE, "PurrNet Melee Bridge", root), manager, coreBridge);
            if (m_ModuleShooter) ConfigureShooterBridge(EnsureComponentByType(PURRNET_SHOOTER_BRIDGE_TYPE, "PurrNet Shooter Bridge", root), manager, coreBridge);
            if (m_ModuleStats) ConfigureNetworkManagerOnlyBridge(EnsureComponentByType(PURRNET_STATS_BRIDGE_TYPE, "PurrNet Stats Bridge", root), manager);
            if (m_ModuleInventory) ConfigureCoreAwareBridge(EnsureComponentByType(PURRNET_INVENTORY_BRIDGE_TYPE, "PurrNet Inventory Bridge", root), manager, coreBridge);
            if (m_ModuleQuests) ConfigureNetworkManagerOnlyBridge(EnsureComponentByType(PURRNET_QUESTS_BRIDGE_TYPE, "PurrNet Quests Bridge", root), manager, logProperty: "m_LogNetworkMessages");
            if (m_ModuleDialogue) ConfigureNetworkManagerOnlyBridge(EnsureComponentByType(PURRNET_DIALOGUE_BRIDGE_TYPE, "PurrNet Dialogue Bridge", root), manager, logProperty: "m_LogNetworkMessages");
            if (m_ModuleTraversal) ConfigureNetworkManagerOnlyBridge(EnsureComponentByType(PURRNET_TRAVERSAL_BRIDGE_TYPE, "PurrNet Traversal Bridge", root), manager, logProperty: "m_LogNetworkMessages");
            if (m_ModuleAbilities) ConfigureAbilitiesBridge(EnsureComponentByType(PURRNET_ABILITIES_BRIDGE_TYPE, "PurrNet Abilities Bridge", root), manager, coreBridge);
        }

        private void ConfigureMeleeBridge(Component bridge, NetworkManager manager, PurrNetTransportBridge coreBridge)
        {
            if (bridge == null) return;

            var so = new SerializedObject(bridge);
            AssignObjectReference(so, "m_NetworkManager", manager);
            AssignObjectReference(so, "m_CoreBridge", coreBridge);
            SetBool(so, "m_LogBlockPackets", false);
            SetBool(so, "m_LogSkillPackets", false);

            if (m_RegisterInstalledDemoAssets)
            {
                AssignObjectReferenceArrayIfAny(so.FindProperty("m_RegisterWeapons"), LoadExistingAssets(SWORD_WEAPON_PATH));
            }

            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(bridge);
        }

        private void ConfigureShooterBridge(Component bridge, NetworkManager manager, PurrNetTransportBridge coreBridge)
        {
            if (bridge == null) return;

            var so = new SerializedObject(bridge);
            AssignObjectReference(so, "m_NetworkManager", manager);
            AssignObjectReference(so, "m_CoreBridge", coreBridge);
            SetBool(so, "m_LogDiagnostics", false);

            if (m_RegisterInstalledDemoAssets)
            {
                AssignShooterWeaponRegistration(
                    so.FindProperty("m_WeaponRegistrations"),
                    LoadAsset(AK_WEAPON_PATH),
                    LoadAsset(AK_WEAPON_PREFAB_PATH),
                    LoadAsset(AK_WEAPON_HANDLE_PATH));
            }

            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(bridge);
        }

        private void ConfigureAbilitiesBridge(Component bridge, NetworkManager manager, PurrNetTransportBridge coreBridge)
        {
            if (bridge == null) return;

            var so = new SerializedObject(bridge);
            AssignObjectReference(so, "m_NetworkManager", manager);
            AssignObjectReference(so, "m_CoreBridge", coreBridge);
            SetBool(so, "m_LogNetworkMessages", false);

            if (m_RegisterInstalledDemoAssets)
            {
                AssignObjectReferenceArrayIfAny(so.FindProperty("m_RegisterAbilities"), LoadExistingAssets(ABILITY_FIREBALL_SPRAY_PATH, ABILITY_FIREBALL_NOVA_PATH));
                AssignObjectReferenceArrayIfAny(so.FindProperty("m_RegisterProjectiles"), LoadExistingAssets(PROJECTILE_FIREBALL_SPRAY_PATH, PROJECTILE_FIREBALL_NOVA_PATH));
                AssignObjectReferenceArrayIfAny(so.FindProperty("m_RegisterImpacts"), LoadExistingAssets(IMPACT_FIREBALL_PATH, IMPACT_BLAST_PATH));
            }

            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(bridge);
        }

        private static void ConfigureCoreAwareBridge(Component bridge, NetworkManager manager, PurrNetTransportBridge coreBridge)
        {
            if (bridge == null) return;

            var so = new SerializedObject(bridge);
            AssignObjectReference(so, "m_NetworkManager", manager);
            AssignObjectReference(so, "m_CoreBridge", coreBridge);
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(bridge);
        }

        private static void ConfigureNetworkManagerOnlyBridge(
            Component bridge,
            NetworkManager manager,
            string logProperty = null)
        {
            if (bridge == null) return;

            var so = new SerializedObject(bridge);
            AssignObjectReference(so, "m_NetworkManager", manager);
            if (!string.IsNullOrEmpty(logProperty)) SetBool(so, logProperty, false);
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(bridge);
        }

        private void EnsurePlayerSpawner(GameObject root, PurrNetDemoCharacterSelection characterSelection)
        {
            var spawner = FindOrCreateComponent<PurrNetDemoPlayerSpawner>("PurrNet Player Spawner", root);
            List<GameObject> playerPrefabs = GetSelectablePlayerPrefabs();
            GameObject defaultPrefab = playerPrefabs.Count > 0 ? playerPrefabs[0] : m_PlayerPrefab;

            float spawnY = 1f;
            var character = defaultPrefab != null ? defaultPrefab.GetComponent<Character>() : null;
            if (character?.Motion != null) spawnY = character.Motion.Height * 0.5f;

            var spawnRoot = spawner.transform.Find("Spawn Points");
            if (spawnRoot == null)
            {
                var spawnRootGo = CreateChild("Spawn Points", spawner.gameObject);
                spawnRoot = spawnRootGo.transform;
            }

            var spawnPoints = EnsureSpawnPoints(spawnRoot, spawnY);

            var so = new SerializedObject(spawner);
            AssignObjectReference(so, "m_PlayerPrefab", defaultPrefab);
            AssignObjectReference(so, "m_CharacterSelection", characterSelection);
            SetBool(so, "m_WaitForCharacterSelection", characterSelection != null);
            SetFloat(so, "m_SelectionWaitTimeout", 8f);
            SetBool(so, "m_IgnoreNetworkRules", false);
            AssignGameObjectList(so, "m_PlayerPrefabs", playerPrefabs);

            var pointsProp = so.FindProperty("m_SpawnPoints");
            if (pointsProp != null)
            {
                pointsProp.arraySize = spawnPoints.Count;
                for (int i = 0; i < spawnPoints.Count; i++)
                {
                    pointsProp.GetArrayElementAtIndex(i).objectReferenceValue = spawnPoints[i];
                }
            }

            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(spawner);
        }

        private string EnsurePlayerPrefabSetup()
        {
            if (!m_ConfigurePlayerPrefab) return null;

            List<GameObject> prefabs = GetSpawnablePlayerPrefabs();
            if (prefabs.Count == 0) return null;

            var reports = new List<string>();
            for (int i = 0; i < prefabs.Count; i++)
            {
                string report = EnsurePlayerPrefabSetup(prefabs[i]);
                if (!string.IsNullOrEmpty(report)) reports.Add(report);
            }

            return reports.Count > 0 ? string.Join("\n", reports) : null;
        }

        private string EnsurePlayerPrefabSetup(GameObject playerPrefab)
        {
            if (playerPrefab == null) return null;

            string prefabPath = AssetDatabase.GetAssetPath(playerPrefab);
            if (string.IsNullOrEmpty(prefabPath) ||
                !prefabPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            {
                return "[PurrNetSceneSetupWizard] Player Prefab preparation skipped: assign a prefab asset from the Project window.";
            }

            GameObject prefabRoot = PrefabUtility.LoadPrefabContents(prefabPath);
            if (prefabRoot == null)
            {
                return $"[PurrNetSceneSetupWizard] Player Prefab preparation skipped: could not load '{prefabPath}'.";
            }

            var changes = new List<string>(12);
            try
            {
                if (EnsurePrefabComponent<NetworkIdentity>(prefabRoot, out _))
                    changes.Add("NetworkIdentity");

                if (EnsurePrefabComponent<Character>(prefabRoot, out Character character))
                    changes.Add("Character");

                if (character != null)
                {
                    // PurrNet-spawned player prefabs must not be authored as GC2's global
                    // player. NetworkCharacter flips IsPlayer only for the local owner after
                    // PurrNet resolves ownership, so remote replicas do not consume input.
                    if (character.IsPlayer)
                    {
                        character.IsPlayer = false;
                        changes.Add("Character.IsPlayer=false");
                    }

                    if (m_ConfigurePlayerPrefabKernel && ConfigureNetworkReadyCharacterKernel(character))
                    {
                        changes.Add("network-ready GC2 Character units");
                    }
                }

                bool addedNetworkCharacter = EnsurePrefabComponent<NetworkCharacter>(prefabRoot, out NetworkCharacter networkCharacter);
                if (addedNetworkCharacter) changes.Add("NetworkCharacter");
                if (ConfigureNetworkCharacterForSpawnedPlayer(networkCharacter)) changes.Add("NetworkCharacter settings");
                if (ConfigurePlayerPreRegisteredAnimationClips(networkCharacter)) changes.Add("pre-registered animation clips");

                NetworkCoreController[] legacyCoreControllers =
                    prefabRoot.GetComponentsInChildren<NetworkCoreController>(true);
                if (legacyCoreControllers != null && legacyCoreControllers.Length > 0)
                {
                    for (int i = 0; i < legacyCoreControllers.Length; i++)
                    {
                        if (legacyCoreControllers[i] != null)
                        {
                            UnityEngine.Object.DestroyImmediate(legacyCoreControllers[i], true);
                        }
                    }

                    changes.Add($"removed {legacyCoreControllers.Length} legacy NetworkCoreController component(s)");
                }

                if (EnsurePrefabComponent<PurrNetNetworkCharacterAuto>(prefabRoot, out _))
                    changes.Add("PurrNetNetworkCharacterAuto");

                EnsurePurrDictionPlayerPrefabSetup(prefabRoot, changes);

                if (m_PlayerPrefabUsesLocalVariables)
                {
                    bool addedVariableController = EnsurePrefabComponent(prefabRoot, out NetworkVariableController variableController);
                    if (addedVariableController) changes.Add("NetworkVariableController");
                    if (ConfigurePlayerVariableController(variableController, networkCharacter)) changes.Add("NetworkVariableController settings");
                }

                EnsureSelectedPlayerPrefabModuleControllers(prefabRoot, changes);

                if (changes.Count > 0)
                {
                    PrefabUtility.SaveAsPrefabAsset(prefabRoot, prefabPath);
                    AssetDatabase.ImportAsset(prefabPath);
                    return $"[PurrNetSceneSetupWizard] Prepared Player Prefab '{prefabPath}': {string.Join(", ", changes)}.";
                }

                return $"[PurrNetSceneSetupWizard] Player Prefab '{prefabPath}' already has the selected networking setup.";
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(prefabRoot);
            }
        }

        private void EnsureSelectedPlayerPrefabModuleControllers(GameObject prefabRoot, List<string> changes)
        {
            if (prefabRoot == null || changes == null) return;

            if (m_ModuleStats)
            {
                EnsurePrefabComponentByType(prefabRoot, NETWORK_STATS_CONTROLLER_TYPE, "NetworkStatsController", changes);
            }

            if (m_ModuleInventory)
            {
                EnsurePrefabComponentByType(prefabRoot, NETWORK_INVENTORY_CONTROLLER_TYPE, "NetworkInventoryController", changes);
            }

            if (m_ModuleMelee)
            {
                EnsurePrefabComponentByType(prefabRoot, NETWORK_MELEE_CONTROLLER_TYPE, "NetworkMeleeController", changes);
            }

            if (m_ModuleShooter)
            {
                EnsurePrefabComponentByType(prefabRoot, NETWORK_SHOOTER_CONTROLLER_TYPE, "NetworkShooterController", changes);
            }

            if (m_ModuleQuests)
            {
                EnsurePrefabComponentByType(prefabRoot, NETWORK_QUESTS_CONTROLLER_TYPE, "NetworkQuestsController", changes);
            }

            if (m_ModuleDialogue && HasComponentByType(prefabRoot, DIALOGUE_COMPONENT_TYPE))
            {
                EnsurePrefabComponentByType(prefabRoot, NETWORK_DIALOGUE_CONTROLLER_TYPE, "NetworkDialogueController", changes);
            }

            if (m_ModuleTraversal)
            {
                EnsurePrefabComponentByType(prefabRoot, NETWORK_TRAVERSAL_CONTROLLER_TYPE, "NetworkTraversalController", changes);
            }
        }

        private void EnsurePurrDictionPlayerPrefabSetup(GameObject prefabRoot, List<string> changes)
        {
            if (prefabRoot == null || changes == null) return;
            if (m_PredictionBackend != NetworkPredictionBackend.PurrDiction) return;

            PurrDictionMovementAdapter adapter = DetectPurrDictionMovementAdapter(prefabRoot, m_ConfigurePlayerPrefabKernel);
            if (adapter == PurrDictionMovementAdapter.Unsupported)
            {
                Debug.LogWarning(
                    "[PurrNetSceneSetupWizard] PurrDiction was selected, but the Player Prefab movement style is unsupported. " +
                    "Leaving the prefab without a PurrDiction movement adapter; use Built-in prediction for this movement style.",
                    prefabRoot);
                return;
            }

            string desiredType = adapter == PurrDictionMovementAdapter.NavMesh
                ? PURRDICTION_NAVMESH_CONTROLLER_TYPE
                : PURRDICTION_CHARACTER_CONTROLLER_TYPE;
            string desiredName = adapter == PurrDictionMovementAdapter.NavMesh
                ? "PurrDictionNetworkNavmeshController"
                : "PurrDictionNetworkCharacterController";
            string otherType = adapter == PurrDictionMovementAdapter.NavMesh
                ? PURRDICTION_CHARACTER_CONTROLLER_TYPE
                : PURRDICTION_NAVMESH_CONTROLLER_TYPE;
            string otherName = adapter == PurrDictionMovementAdapter.NavMesh
                ? "PurrDictionNetworkCharacterController"
                : "PurrDictionNetworkNavmeshController";

            RemovePrefabComponentByType(prefabRoot, otherType, otherName, changes);
            EnsurePrefabComponentByType(
                prefabRoot,
                desiredType,
                desiredName,
                changes);

            Type predictedTransformType = Type.GetType("PurrNet.Prediction.PredictedTransform, PurrNet.Prediction");
            if (predictedTransformType != null && prefabRoot.GetComponent(predictedTransformType) != null)
            {
                Debug.LogWarning(
                    "[PurrNetSceneSetupWizard] Player Prefab already has PurrDiction PredictedTransform. " +
                    "The GC2 PurrDiction backend owns transform prediction itself; remove the extra PredictedTransform to avoid double transform writes.",
                    prefabRoot);
            }
        }

        private static bool ConfigureNetworkReadyCharacterKernel(Character character)
        {
            return GC2SceneSetupShared.ConfigureNetworkReadyCharacterKernel(
                character,
                applyWithUndo: false);
        }

        private bool ConfigureNetworkCharacterForSpawnedPlayer(NetworkCharacter networkCharacter)
        {
            if (networkCharacter == null) return false;

            var so = new SerializedObject(networkCharacter);
            bool changed = false;
            changed |= SetEnumIndexIfPresent(so, "m_NPCMode", (int)NetworkCharacter.NPCSyncMode.ServerAuthoritative);
            changed |= SetBoolIfPresent(so, "m_UseNetworkMotion", true);
            changed |= SetBoolIfPresent(so, "m_UseAnimationSync", true);
            changed |= SetBoolIfPresent(so, "m_UseCoreNetworking", true);
            changed |= SetBoolIfPresent(so, "m_HostOwnerUsesClientPrediction", true);
            changed |= SetEnumIndexIfPresent(so, "m_PredictionBackend", (int)m_PredictionBackend);

            if (changed)
            {
                so.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(networkCharacter);
            }

            return changed;
        }

        private bool ConfigurePlayerPreRegisteredAnimationClips(NetworkCharacter networkCharacter)
        {
            if (networkCharacter == null || !m_PlayerUsesNetworkInstructionClips) return false;

            List<AnimationClip> clips = GetValidPlayerPreRegisteredAnimationClips();
            if (clips.Count == 0) return false;

            var so = new SerializedObject(networkCharacter);
            SerializedProperty prop = so.FindProperty("m_PreRegisteredAnimationClips");
            if (prop == null || !prop.isArray) return false;

            bool changed = AddUniqueObjectReferences(prop, clips);
            if (changed)
            {
                so.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(networkCharacter);
            }
            else
            {
                so.ApplyModifiedPropertiesWithoutUndo();
            }

            return changed;
        }

        private List<AnimationClip> GetValidPlayerPreRegisteredAnimationClips()
        {
            var clips = new List<AnimationClip>(m_PlayerPreRegisteredAnimationClips.Count);
            for (int i = 0; i < m_PlayerPreRegisteredAnimationClips.Count; i++)
            {
                AnimationClip clip = m_PlayerPreRegisteredAnimationClips[i];
                if (clip == null || clips.Contains(clip)) continue;
                clips.Add(clip);
            }

            return clips;
        }

        private bool ConfigurePlayerVariableController(
            NetworkVariableController controller,
            NetworkCharacter networkCharacter)
        {
            if (controller == null) return false;

            var so = new SerializedObject(controller);
            bool changed = false;

            if (m_PlayerVariableProfile != null)
            {
                changed |= SetObjectReferenceIfPresent(so, "m_Profile", m_PlayerVariableProfile);
            }

            var localNameVariables = controller.GetComponent<LocalNameVariables>();
            if (localNameVariables != null)
            {
                changed |= SetObjectReferenceIfPresent(so, "m_LocalNameVariables", localNameVariables);
            }

            var localListVariables = controller.GetComponent<LocalListVariables>();
            if (localListVariables != null)
            {
                changed |= SetObjectReferenceIfPresent(so, "m_LocalListVariables", localListVariables);
            }

            if (networkCharacter != null)
            {
                changed |= SetObjectReferenceIfPresent(so, "m_NetworkCharacter", networkCharacter);
            }

            changed |= SetBoolIfPresent(so, "m_AutoFindComponents", true);

            if (changed)
            {
                so.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(controller);
            }

            return changed;
        }

        private static bool EnsurePrefabComponent<T>(GameObject root, out T component) where T : Component
        {
            component = root != null ? root.GetComponent<T>() : null;
            if (component != null) return false;

            component = root != null ? root.AddComponent<T>() : null;
            return component != null;
        }

        private static bool EnsurePrefabComponentByType(
            GameObject root,
            string typeName,
            string componentName,
            List<string> changes)
        {
            Type type = Type.GetType(typeName);
            if (type == null || !typeof(Component).IsAssignableFrom(type))
            {
                Debug.LogWarning($"[PurrNetSceneSetupWizard] Could not add {componentName} to Player Prefab. Type not found: {typeName}");
                return false;
            }

            if (root.GetComponent(type) != null) return false;

            root.AddComponent(type);
            changes.Add(componentName);
            return true;
        }

        private static bool RemovePrefabComponentByType(
            GameObject root,
            string typeName,
            string componentName,
            List<string> changes)
        {
            Type type = Type.GetType(typeName);
            if (root == null || type == null || !typeof(Component).IsAssignableFrom(type)) return false;

            Component component = root.GetComponent(type);
            if (component == null) return false;

            UnityEngine.Object.DestroyImmediate(component, true);
            changes?.Add($"removed {componentName}");
            return true;
        }

        private static bool HasComponentByType(GameObject root, string typeName)
        {
            Type type = Type.GetType(typeName);
            return type != null && typeof(Component).IsAssignableFrom(type) && root.GetComponent(type) != null;
        }

        private void EnsureDemoCanvasUI(GameObject root, NetworkManager manager)
        {
            var ui = FindOrCreateComponent<PurrNetDemoCanvasUI>("PurrNet Demo UI", root);

            var so = new SerializedObject(ui);
            AssignObjectReference(so, "m_NetworkManager", manager);
            SetString(so, "m_DefaultAddress", m_Address);
            SetInt(so, "m_DefaultPort", m_Port);
            SetString(so, "m_Title", m_UITitle);
            SetString(so, "m_Subtitle", m_UISubtitle);
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(ui);
        }

        private void EnsureLobbyUI(GameObject root, NetworkManager manager)
        {
            Component service = EnsureComponentByTypeOnGameObject(
                manager != null ? manager.gameObject : null,
                PURRNET_LOBBY_SERVICE_TYPE,
                "PurrNet Lobby Service");
            if (service == null) return;

            var serviceObject = new SerializedObject(service);
            AssignObjectReference(serviceObject, "m_NetworkManager", manager);
            SetInt(serviceObject, "m_Mode", (int)m_LobbyMode);
            SetString(serviceObject, "m_DefaultAddress", m_Address);
            SetInt(serviceObject, "m_DefaultPort", m_Port);
            serviceObject.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(service);

            Component ui = EnsureComponentByType(
                NETWORK_LOBBY_UI_TYPE,
                "GC2 Multiplayer Lobby UI",
                root);
            if (ui != null)
            {
                var uiObject = new SerializedObject(ui);
                AssignObjectReference(uiObject, "m_ServiceBehaviour", service);
                SetString(uiObject, "m_Title", m_UITitle);
                SetString(uiObject, "m_Subtitle", m_UISubtitle);
                SetString(uiObject, "m_DefaultAddress", m_Address);
                SetInt(uiObject, "m_DefaultPort", m_Port);
                SetInt(uiObject, "m_DefaultTopology", 0);
                uiObject.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(ui);
            }

            PurrNetDemoCanvasUI directUi = FindSceneComponent<PurrNetDemoCanvasUI>();
            if (directUi != null && directUi.enabled)
            {
                Undo.RecordObject(directUi, "Disable duplicate PurrNet demo session UI");
                directUi.enabled = false;
                EditorUtility.SetDirty(directUi);
            }
        }

        private void EnsureControlsUI(GameObject root)
        {
            GameObject canvasGo = GameObject.Find("PurrNet Demo Controls UI");
            if (canvasGo == null)
            {
                canvasGo = CreateChild("PurrNet Demo Controls UI", root);
            }

            var canvas = canvasGo.GetComponent<Canvas>();
            if (canvas == null) canvas = Undo.AddComponent<Canvas>(canvasGo);
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 990;

            var scaler = canvasGo.GetComponent<CanvasScaler>();
            if (scaler == null) scaler = Undo.AddComponent<CanvasScaler>(canvasGo);
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;

            if (canvasGo.GetComponent<GraphicRaycaster>() == null)
            {
                Undo.AddComponent<GraphicRaycaster>(canvasGo);
            }

            GameObject panel = canvasGo.transform.Find("Controls Panel")?.gameObject;
            if (panel == null)
            {
                panel = CreateUIChild("Controls Panel", canvasGo.transform);
            }

            var panelRect = panel.GetComponent<RectTransform>();
            panelRect.anchorMin = new Vector2(1f, 1f);
            panelRect.anchorMax = new Vector2(1f, 1f);
            panelRect.pivot = new Vector2(1f, 1f);
            panelRect.anchoredPosition = new Vector2(-24f, -24f);
            panelRect.sizeDelta = new Vector2(340f, 168f);

            var panelImage = panel.GetComponent<Image>();
            if (panelImage == null) panelImage = Undo.AddComponent<Image>(panel);
            panelImage.color = new Color(0.08f, 0.09f, 0.11f, 0.88f);

            GameObject textGo = panel.transform.Find("Controls Text")?.gameObject;
            if (textGo == null)
            {
                textGo = CreateUIChild("Controls Text", panel.transform);
            }

            var textRect = textGo.GetComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(14f, 12f);
            textRect.offsetMax = new Vector2(-14f, -12f);

            var text = textGo.GetComponent<Text>();
            if (text == null) text = Undo.AddComponent<Text>(textGo);
            Font runtimeFont = GetBuiltinRuntimeFont();
            if (runtimeFont != null) text.font = runtimeFont;
            text.fontSize = 13;
            text.color = new Color(0.92f, 0.94f, 0.97f, 1f);
            text.alignment = TextAnchor.UpperLeft;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.text = BuildControlsText();

            EnsureEventSystem();
            EditorUtility.SetDirty(canvasGo);
        }

        private PurrNetDemoCharacterSelection EnsureCharacterSelectionUI(GameObject root, NetworkManager manager)
        {
            PurrNetDemoCharacterSelection selection = FindOrCreateComponent<PurrNetDemoCharacterSelection>(
                "Character Selection Controller",
                root);

            List<GameObject> playerPrefabs = GetSelectablePlayerPrefabs();
            int characterCount = Mathf.Max(1, playerPrefabs.Count);

            GameObject canvasGo = GameObject.Find("PurrNet Character Selection UI");
            if (canvasGo == null)
            {
                canvasGo = CreateChild("PurrNet Character Selection UI", root);
            }

            var canvas = EnsureComponent<Canvas>(canvasGo);
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 970;

            var scaler = EnsureComponent<CanvasScaler>(canvasGo);
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            EnsureComponent<GraphicRaycaster>(canvasGo);

            GameObject selectionRoot = EnsureUIChild("Selection Root", canvasGo.transform);
            SetOffsets(selectionRoot.GetComponent<RectTransform>(), Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);

            Image background = EnsureImage(selectionRoot, new Color(0.02f, 0.025f, 0.03f, 0.64f));
            if (background != null) background.raycastTarget = false;

            float panelHeight = Mathf.Clamp(420f + characterCount * 54f, 540f, 760f);
            GameObject panel = EnsureUIChild("Selection Panel", selectionRoot.transform);
            SetRect(
                panel.GetComponent<RectTransform>(),
                new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f),
                Vector2.zero,
                new Vector2(520f, panelHeight));
            EnsureImage(panel, new Color(0.07f, 0.08f, 0.10f, 0.94f));

            Text title = EnsureText(
                EnsureUIChild("Title", panel.transform),
                string.IsNullOrWhiteSpace(m_UITitle) ? "Character Selection" : m_UITitle,
                24,
                FontStyle.Bold,
                new Color(0.93f, 0.95f, 0.98f, 1f),
                TextAnchor.MiddleCenter);
            SetRect(title.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -30f), new Vector2(-48f, 48f));

            Text selectedText = EnsureText(
                EnsureUIChild("Selected Text", panel.transform),
                playerPrefabs.Count > 0 ? $"Selected: {GetPrefabDisplayName(playerPrefabs[0], 0)}" : "Selected: none",
                15,
                FontStyle.Bold,
                new Color(0.74f, 0.79f, 0.88f, 1f),
                TextAnchor.MiddleCenter);
            SetRect(selectedText.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -76f), new Vector2(-48f, 34f));

            var highlights = new List<UnityEngine.Object>(characterCount);
            float buttonStartY = 122f;
            for (int i = 0; i < characterCount; i++)
            {
                GameObject prefab = i < playerPrefabs.Count ? playerPrefabs[i] : null;
                string label = GetPrefabDisplayName(prefab, i);
                ButtonInstructions button = EnsureCharacterSelectionButton(
                    panel.transform,
                    $"Character Button {i + 1}",
                    label,
                    new Vector2(0f, 1f),
                    new Vector2(1f, 1f),
                    new Vector2(0.5f, 1f),
                    new Vector2(0f, -buttonStartY - 52f * i),
                    new Vector2(-48f, 44f),
                    selection.gameObject,
                    NetworkDemoCharacterSelectionAction.SelectCharacter,
                    i,
                    new Color(0.08f, 0.09f, 0.10f, 0.92f));

                if (button != null && button.targetGraphic != null)
                {
                    highlights.Add(button.targetGraphic);
                }
            }

            InputField addressInput = EnsureChatInput(
                panel.transform,
                "Address Field",
                "Address",
                new Vector2(0f, 0f),
                new Vector2(0.68f, 0f),
                new Vector2(24f, 108f),
                new Vector2(-8f, 148f));
            if (string.IsNullOrWhiteSpace(addressInput.text)) addressInput.text = m_Address;

            InputField portInput = EnsureChatInput(
                panel.transform,
                "Port Field",
                "Port",
                new Vector2(0.68f, 0f),
                new Vector2(1f, 0f),
                new Vector2(8f, 108f),
                new Vector2(-24f, 148f));
            portInput.contentType = InputField.ContentType.IntegerNumber;
            if (string.IsNullOrWhiteSpace(portInput.text)) portInput.text = m_Port.ToString();

            ButtonInstructions hostButton = EnsureCharacterSelectionButton(
                panel.transform,
                "Host Button",
                "Host",
                new Vector2(0f, 0f),
                new Vector2(0.5f, 0f),
                new Vector2(0.5f, 0f),
                new Vector2(12f, 76f),
                new Vector2(-36f, 42f),
                selection.gameObject,
                NetworkDemoCharacterSelectionAction.StartHost,
                0,
                new Color(0.22f, 0.48f, 0.34f, 1f));

            ButtonInstructions joinButton = EnsureCharacterSelectionButton(
                panel.transform,
                "Join Button",
                "Join",
                new Vector2(0.5f, 0f),
                new Vector2(1f, 0f),
                new Vector2(0.5f, 0f),
                new Vector2(-12f, 76f),
                new Vector2(-36f, 42f),
                selection.gameObject,
                NetworkDemoCharacterSelectionAction.JoinServer,
                0,
                new Color(0.22f, 0.42f, 0.78f, 1f));

            Text statusText = EnsureText(
                EnsureUIChild("Status Text", panel.transform),
                "Choose a character.",
                13,
                FontStyle.Normal,
                new Color(0.72f, 0.76f, 0.84f, 1f),
                TextAnchor.UpperCenter);
            SetOffsets(statusText.rectTransform, Vector2.zero, new Vector2(1f, 0f), new Vector2(24f, 14f), new Vector2(-24f, 66f));
            statusText.horizontalOverflow = HorizontalWrapMode.Wrap;
            statusText.verticalOverflow = VerticalWrapMode.Truncate;

            GameObject inGameRoot = EnsureUIChild("In Game Root", canvasGo.transform);
            SetOffsets(inGameRoot.GetComponent<RectTransform>(), Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);

            GameObject inGamePanel = EnsureUIChild("In Game Panel", inGameRoot.transform);
            SetRect(
                inGamePanel.GetComponent<RectTransform>(),
                new Vector2(0f, 1f),
                new Vector2(0f, 1f),
                new Vector2(0f, 1f),
                new Vector2(24f, -24f),
                new Vector2(430f, 96f));
            EnsureImage(inGamePanel, new Color(0.07f, 0.08f, 0.10f, 0.88f));

            Text inGameStatus = EnsureText(
                EnsureUIChild("Status Text", inGamePanel.transform),
                "Connected",
                13,
                FontStyle.Bold,
                new Color(0.93f, 0.95f, 0.98f, 1f),
                TextAnchor.MiddleLeft);
            SetOffsets(inGameStatus.rectTransform, Vector2.zero, Vector2.one, new Vector2(14f, 12f), new Vector2(-112f, -12f));
            inGameStatus.horizontalOverflow = HorizontalWrapMode.Wrap;

            EnsureCharacterSelectionButton(
                inGamePanel.transform,
                "Stop Button",
                "Stop",
                new Vector2(1f, 0.5f),
                new Vector2(1f, 0.5f),
                new Vector2(1f, 0.5f),
                new Vector2(-62f, 0f),
                new Vector2(84f, 42f),
                selection.gameObject,
                NetworkDemoCharacterSelectionAction.StopSession,
                0,
                new Color(0.62f, 0.23f, 0.24f, 1f));

            var so = new SerializedObject(selection);
            AssignObjectReference(so, "m_NetworkManager", manager);
            AssignGameObjectList(so, "m_PlayerPrefabs", playerPrefabs);
            AssignStringArray(so, "m_DisplayNames", playerPrefabs);
            AssignObjectReference(so, "m_SelectionRoot", selectionRoot);
            AssignObjectReference(so, "m_InGameRoot", inGameRoot);
            AssignObjectReference(so, "m_AddressInput", addressInput);
            AssignObjectReference(so, "m_PortInput", portInput);
            AssignObjectReference(so, "m_StatusText", statusText);
            AssignObjectReferenceArray(so.FindProperty("m_StatusTexts"), new UnityEngine.Object[] { statusText, inGameStatus });
            AssignObjectReference(so, "m_SelectedText", selectedText);
            AssignObjectReferenceArray(so.FindProperty("m_SelectionHighlights"), highlights);
            SetInt(so, "m_SelectedIndex", 0);
            SetString(so, "m_DefaultAddress", m_Address);
            SetInt(so, "m_DefaultPort", m_Port);
            SetFloat(so, "m_PublishSelectionTimeout", 8f);
            so.ApplyModifiedPropertiesWithoutUndo();

            if (hostButton != null) EditorUtility.SetDirty(hostButton);
            if (joinButton != null) EditorUtility.SetDirty(joinButton);

            EnsureEventSystem();
            EditorUtility.SetDirty(selection);
            EditorUtility.SetDirty(canvasGo);

            return selection;
        }

        private void EnsureChatUI(GameObject root, NetworkManager manager)
        {
            GameObject canvasGo = GameObject.Find("PurrNet Chat UI");
            if (canvasGo == null)
            {
                canvasGo = CreateChild("PurrNet Chat UI", root);
            }

            var chat = canvasGo.GetComponent<PurrNetChatBoxUI>();
            if (chat == null) chat = Undo.AddComponent<PurrNetChatBoxUI>(canvasGo);

            var canvas = EnsureComponent<Canvas>(canvasGo);
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 980;

            var scaler = EnsureComponent<CanvasScaler>(canvasGo);
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            EnsureComponent<GraphicRaycaster>(canvasGo);

            GameObject panel = EnsureUIChild("Chat Panel", canvasGo.transform);
            SetRect(
                panel.GetComponent<RectTransform>(),
                new Vector2(0f, 0f),
                new Vector2(0f, 0f),
                new Vector2(0f, 0f),
                new Vector2(24f, 24f),
                new Vector2(430f, 260f));
            EnsureImage(panel, new Color(0.06f, 0.07f, 0.09f, 0.88f));

            GameObject header = EnsureUIChild("Header", panel.transform);
            SetRect(
                header.GetComponent<RectTransform>(),
                new Vector2(0f, 1f),
                new Vector2(1f, 1f),
                new Vector2(0.5f, 1f),
                Vector2.zero,
                new Vector2(0f, 36f));
            EnsureImage(header, new Color(0.10f, 0.11f, 0.14f, 0.96f));

            Text title = EnsureText(
                EnsureUIChild("Title", header.transform),
                "Chat",
                15,
                FontStyle.Bold,
                new Color(0.93f, 0.95f, 0.98f, 1f),
                TextAnchor.MiddleLeft);
            SetOffsets(title.rectTransform, Vector2.zero, Vector2.one, new Vector2(14f, 0f), new Vector2(-104f, 0f));

            Text status = EnsureText(
                EnsureUIChild("Status", header.transform),
                "Offline",
                11,
                FontStyle.Bold,
                new Color(0.66f, 0.69f, 0.76f, 1f),
                TextAnchor.MiddleRight);
            SetOffsets(status.rectTransform, new Vector2(1f, 0f), Vector2.one, new Vector2(-96f, 0f), new Vector2(-14f, 0f));

            ScrollRect scroll = EnsureChatScrollArea(panel.transform, out RectTransform messageContent, out Text messageText);

            InputField nameField = EnsureChatInput(
                panel.transform,
                "Name Field",
                "Player",
                new Vector2(0f, 0f),
                new Vector2(0f, 0f),
                new Vector2(12f, 12f),
                new Vector2(132f, 42f));
            nameField.characterLimit = 24;
            if (string.IsNullOrWhiteSpace(nameField.text)) nameField.text = "Player";

            InputField messageField = EnsureChatInput(
                panel.transform,
                "Message Field",
                "Type message",
                new Vector2(0f, 0f),
                new Vector2(1f, 0f),
                new Vector2(142f, 12f),
                new Vector2(-82f, 42f));
            messageField.characterLimit = 160;
            messageField.lineType = InputField.LineType.SingleLine;

            Button sendButton = EnsureChatButton(
                panel.transform,
                "Send Button",
                "Send",
                new Vector2(1f, 0f),
                new Vector2(1f, 0f),
                new Vector2(-72f, 12f),
                new Vector2(-12f, 42f));
            EnsurePersistentSendListener(sendButton, chat);

            var so = new SerializedObject(chat);
            AssignObjectReference(so, "m_NetworkManager", manager);
            SetString(so, "m_Title", "Chat");
            SetString(so, "m_DefaultDisplayName", "Player");
            SetInt(so, "m_MaxVisibleMessages", 64);
            SetInt(so, "m_MaxMessageLength", 160);
            SetFloat(so, "m_MinSendInterval", 0.25f);
            SetEnumIndexIfPresent(so, "m_Channel", (int)Channel.ReliableOrdered);
            SetInt(so, "m_SortingOrder", 980);
            SetBool(so, "m_CreateMissingUI", false);
            SetBool(so, "m_DebugLog", true);
            AssignObjectReference(so, "m_Canvas", canvas);
            AssignObjectReference(so, "m_Panel", panel);
            AssignObjectReference(so, "m_HeaderText", title);
            AssignObjectReference(so, "m_StatusText", status);
            AssignObjectReference(so, "m_MessageText", messageText);
            AssignObjectReference(so, "m_MessageContent", messageContent);
            AssignObjectReference(so, "m_ScrollRect", scroll);
            AssignObjectReference(so, "m_NameField", nameField);
            AssignObjectReference(so, "m_MessageField", messageField);
            AssignObjectReference(so, "m_SendButton", sendButton);
            so.ApplyModifiedPropertiesWithoutUndo();

            EnsureEventSystem();
            EditorUtility.SetDirty(canvasGo);
            EditorUtility.SetDirty(chat);
        }

        private static T EnsureComponent<T>(GameObject gameObject) where T : Component
        {
            if (gameObject == null) return null;

            T component = gameObject.GetComponent<T>();
            return component != null ? component : Undo.AddComponent<T>(gameObject);
        }

        private static GameObject EnsureUIChild(string name, Transform parent)
        {
            Transform existing = parent != null ? parent.Find(name) : null;
            if (existing != null) return existing.gameObject;

            return CreateUIChild(name, parent);
        }

        private static void SetRect(
            RectTransform rect,
            Vector2 anchorMin,
            Vector2 anchorMax,
            Vector2 pivot,
            Vector2 anchoredPosition,
            Vector2 sizeDelta)
        {
            if (rect == null) return;

            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.pivot = pivot;
            rect.anchoredPosition = anchoredPosition;
            rect.sizeDelta = sizeDelta;
        }

        private static void SetOffsets(
            RectTransform rect,
            Vector2 anchorMin,
            Vector2 anchorMax,
            Vector2 offsetMin,
            Vector2 offsetMax)
        {
            if (rect == null) return;

            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.offsetMin = offsetMin;
            rect.offsetMax = offsetMax;
        }

        private static Image EnsureImage(GameObject gameObject, Color color)
        {
            if (gameObject == null) return null;

            EnsureComponent<CanvasRenderer>(gameObject);
            Image image = EnsureComponent<Image>(gameObject);
            image.color = color;
            return image;
        }

        private static Text EnsureText(
            GameObject gameObject,
            string content,
            int fontSize,
            FontStyle fontStyle,
            Color color,
            TextAnchor alignment)
        {
            if (gameObject == null) return null;

            EnsureComponent<CanvasRenderer>(gameObject);
            Text text = EnsureComponent<Text>(gameObject);
            text.text = content;
            text.fontSize = fontSize;
            text.fontStyle = fontStyle;
            text.color = color;
            text.alignment = alignment;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;

            Font runtimeFont = GetBuiltinRuntimeFont();
            if (runtimeFont != null) text.font = runtimeFont;

            return text;
        }

        private static ScrollRect EnsureChatScrollArea(
            Transform parent,
            out RectTransform messageContent,
            out Text messageText)
        {
            GameObject root = EnsureUIChild("Messages", parent);
            SetOffsets(
                root.GetComponent<RectTransform>(),
                Vector2.zero,
                Vector2.one,
                new Vector2(12f, 52f),
                new Vector2(-12f, -46f));
            EnsureImage(root, new Color(0.03f, 0.035f, 0.045f, 0.50f));

            ScrollRect scroll = EnsureComponent<ScrollRect>(root);
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;

            GameObject viewport = EnsureUIChild("Viewport", root.transform);
            RectTransform viewportRect = viewport.GetComponent<RectTransform>();
            SetOffsets(
                viewportRect,
                Vector2.zero,
                Vector2.one,
                new Vector2(8f, 6f),
                new Vector2(-8f, -6f));
            Image viewportImage = EnsureImage(viewport, new Color(0f, 0f, 0f, 0f));
            if (viewportImage != null) viewportImage.raycastTarget = false;

            Mask staleMask = viewport.GetComponent<Mask>();
            if (staleMask != null) staleMask.enabled = false;
            EnsureComponent<RectMask2D>(viewport);

            GameObject content = EnsureUIChild("Content", viewport.transform);
            messageContent = content.GetComponent<RectTransform>();
            SetRect(
                messageContent,
                new Vector2(0f, 1f),
                new Vector2(1f, 1f),
                new Vector2(0.5f, 1f),
                Vector2.zero,
                new Vector2(0f, 132f));

            GameObject textGo = EnsureUIChild("Text", content.transform);
            messageText = EnsureText(
                textGo,
                string.Empty,
                13,
                FontStyle.Normal,
                new Color(0.93f, 0.95f, 0.98f, 1f),
                TextAnchor.UpperLeft);
            SetOffsets(messageText.rectTransform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            messageText.supportRichText = true;
            messageText.horizontalOverflow = HorizontalWrapMode.Wrap;
            messageText.verticalOverflow = VerticalWrapMode.Overflow;

            scroll.viewport = viewportRect;
            scroll.content = messageContent;

            return scroll;
        }

        private static InputField EnsureChatInput(
            Transform parent,
            string name,
            string placeholderText,
            Vector2 anchorMin,
            Vector2 anchorMax,
            Vector2 offsetMin,
            Vector2 offsetMax)
        {
            GameObject root = EnsureUIChild(name, parent);
            SetOffsets(root.GetComponent<RectTransform>(), anchorMin, anchorMax, offsetMin, offsetMax);
            Image image = EnsureImage(root, new Color(0.14f, 0.15f, 0.19f, 0.98f));

            InputField input = EnsureComponent<InputField>(root);
            input.targetGraphic = image;

            Text text = EnsureText(
                EnsureUIChild("Text", root.transform),
                string.Empty,
                13,
                FontStyle.Normal,
                new Color(0.93f, 0.95f, 0.98f, 1f),
                TextAnchor.MiddleLeft);
            SetOffsets(text.rectTransform, Vector2.zero, Vector2.one, new Vector2(10f, 2f), new Vector2(-10f, -2f));
            text.supportRichText = false;

            Text placeholder = EnsureText(
                EnsureUIChild("Placeholder", root.transform),
                placeholderText,
                13,
                FontStyle.Italic,
                new Color(0.66f, 0.69f, 0.76f, 1f),
                TextAnchor.MiddleLeft);
            SetOffsets(placeholder.rectTransform, Vector2.zero, Vector2.one, new Vector2(10f, 2f), new Vector2(-10f, -2f));

            input.textComponent = text;
            input.placeholder = placeholder;
            return input;
        }

        private static Button EnsureChatButton(
            Transform parent,
            string name,
            string label,
            Vector2 anchorMin,
            Vector2 anchorMax,
            Vector2 offsetMin,
            Vector2 offsetMax)
        {
            GameObject root = EnsureUIChild(name, parent);
            SetOffsets(root.GetComponent<RectTransform>(), anchorMin, anchorMax, offsetMin, offsetMax);
            Color buttonColor = new Color(0.26f, 0.46f, 0.82f, 1f);
            Image image = EnsureImage(root, buttonColor);

            Button button = EnsureComponent<Button>(root);
            button.targetGraphic = image;

            ColorBlock colors = button.colors;
            colors.normalColor = buttonColor;
            colors.highlightedColor = Color.Lerp(buttonColor, Color.white, 0.12f);
            colors.pressedColor = Color.Lerp(buttonColor, Color.black, 0.16f);
            colors.selectedColor = colors.highlightedColor;
            colors.disabledColor = new Color(buttonColor.r, buttonColor.g, buttonColor.b, 0.45f);
            colors.fadeDuration = 0.10f;
            button.colors = colors;

            Text labelText = EnsureText(
                EnsureUIChild("Label", root.transform),
                label,
                13,
                FontStyle.Bold,
                Color.white,
                TextAnchor.MiddleCenter);
            SetOffsets(labelText.rectTransform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);

            return button;
        }

        private static ButtonInstructions EnsureCharacterSelectionButton(
            Transform parent,
            string name,
            string label,
            Vector2 anchorMin,
            Vector2 anchorMax,
            Vector2 pivot,
            Vector2 anchoredPosition,
            Vector2 sizeDelta,
            GameObject target,
            NetworkDemoCharacterSelectionAction action,
            int characterIndex,
            Color color)
        {
            GameObject root = EnsureUIChild(name, parent);
            SetRect(root.GetComponent<RectTransform>(), anchorMin, anchorMax, pivot, anchoredPosition, sizeDelta);

            Image image = EnsureImage(root, color);

            Button plainButton = root.GetComponent<Button>();
            if (plainButton != null && plainButton is not ButtonInstructions)
            {
                Undo.DestroyObjectImmediate(plainButton);
            }

            ButtonInstructions button = root.GetComponent<ButtonInstructions>();
            if (button == null) button = Undo.AddComponent<ButtonInstructions>(root);
            button.targetGraphic = image;

            ColorBlock colors = button.colors;
            colors.normalColor = color;
            colors.highlightedColor = Color.Lerp(color, Color.white, 0.14f);
            colors.pressedColor = Color.Lerp(color, Color.black, 0.18f);
            colors.selectedColor = colors.highlightedColor;
            colors.disabledColor = new Color(color.r, color.g, color.b, 0.45f);
            colors.fadeDuration = 0.10f;
            button.colors = colors;

            Text labelText = EnsureText(
                EnsureUIChild("Label", root.transform),
                label,
                14,
                FontStyle.Bold,
                Color.white,
                TextAnchor.MiddleCenter);
            SetOffsets(labelText.rectTransform, Vector2.zero, Vector2.one, new Vector2(10f, 2f), new Vector2(-10f, -2f));
            labelText.horizontalOverflow = HorizontalWrapMode.Wrap;
            labelText.verticalOverflow = VerticalWrapMode.Truncate;

            ConfigureCharacterSelectionInstruction(button, target, action, characterIndex);
            EditorUtility.SetDirty(root);
            EditorUtility.SetDirty(button);
            return button;
        }

        private static void ConfigureCharacterSelectionInstruction(
            ButtonInstructions button,
            GameObject target,
            NetworkDemoCharacterSelectionAction action,
            int characterIndex)
        {
            if (button == null) return;

            var so = new SerializedObject(button);
            SerializedProperty list = so.FindProperty("m_Instructions");
            SerializedProperty instructions = list?.FindPropertyRelative("m_Instructions");
            if (instructions == null || !instructions.isArray) return;

            instructions.arraySize = 1;
            SerializedProperty item = instructions.GetArrayElementAtIndex(0);
            item.managedReferenceValue = new InstructionNetworkDemoCharacterSelection(target, action, characterIndex);
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void EnsurePersistentSendListener(Button button, PurrNetChatBoxUI chat)
        {
            if (button == null || chat == null) return;

            int count = button.onClick.GetPersistentEventCount();
            for (int i = 0; i < count; i++)
            {
                if (button.onClick.GetPersistentTarget(i) == chat &&
                    button.onClick.GetPersistentMethodName(i) == nameof(PurrNetChatBoxUI.SendCurrentMessage))
                {
                    return;
                }
            }

            UnityEventTools.AddPersistentListener(button.onClick, chat.SendCurrentMessage);
            EditorUtility.SetDirty(button);
        }

        private static Font GetBuiltinRuntimeFont()
        {
            Font font = TryGetBuiltinFont("LegacyRuntime.ttf");
            return font != null ? font : TryGetBuiltinFont("Arial.ttf");
        }

        private static Font TryGetBuiltinFont(string fontName)
        {
            try
            {
                return Resources.GetBuiltinResource<Font>(fontName);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private NetworkSessionProfile ResolveOrCreateSessionProfile()
        {
            if (m_SessionProfile != null)
            {
                ApplyWizardSessionProfileSettings(m_SessionProfile);
                return m_SessionProfile;
            }

            var existingBridge = FindSceneComponent<PurrNetTransportBridge>();
            if (existingBridge != null && existingBridge.GlobalSessionProfile != null)
            {
                NetworkSessionProfile existingProfile = existingBridge.GlobalSessionProfile;
                ApplyWizardSessionProfileSettings(existingProfile);
                m_SessionProfile = existingProfile;
                return existingProfile;
            }

            if (!m_CreateSessionProfileAsset) return null;

            EnsureAssetFolder(GENERATED_ASSET_FOLDER);

            string assetPath = AssetDatabase.GenerateUniqueAssetPath($"{GENERATED_ASSET_FOLDER}/PurrNetSceneSessionProfile.asset");
            var profile = CreateInstance<NetworkSessionProfile>();
            ApplyWizardSessionProfileSettings(profile);
            AssetDatabase.CreateAsset(profile, assetPath);
            EditorUtility.SetDirty(profile);
            m_SessionProfile = profile;
            return profile;
        }

        private NetworkSessionProfile GetOrCreateCustomSessionProfileDraft()
        {
            if (m_CustomSessionProfileDraft != null) return m_CustomSessionProfileDraft;

            m_CustomSessionProfileDraft = CreateInstance<NetworkSessionProfile>();
            m_CustomSessionProfileDraft.hideFlags = HideFlags.HideAndDontSave;

            var so = new SerializedObject(m_CustomSessionProfileDraft);
            SetSessionProfilePresetProperties(so, NetworkSessionPreset.Custom, false);
            so.ApplyModifiedPropertiesWithoutUndo();

            return m_CustomSessionProfileDraft;
        }

        private void ApplyWizardSessionProfileSettings(NetworkSessionProfile profile)
        {
            if (profile == null) return;

            if (m_SessionPreset == NetworkSessionPreset.Custom)
            {
                NetworkSessionProfile source = m_SessionProfile == null
                    ? m_CustomSessionProfileDraft
                    : null;

                if (source != null && source != profile)
                {
                    EditorJsonUtility.FromJsonOverwrite(EditorJsonUtility.ToJson(source), profile);
                }

                var so = new SerializedObject(profile);
                SetSessionProfilePresetProperties(so, NetworkSessionPreset.Custom, false);
                so.ApplyModifiedPropertiesWithoutUndo();
            }
            else
            {
                profile.ApplyPreset(m_SessionPreset);
            }

            EditorUtility.SetDirty(profile);
        }

        private static void ApplyPresetToCustomSessionProfile(
            NetworkSessionProfile profile,
            NetworkSessionPreset preset)
        {
            if (profile == null) return;

            Undo.RecordObject(profile, $"Load {preset} Session Defaults");
            profile.ApplyPreset(preset);

            var so = new SerializedObject(profile);
            SetSessionProfilePresetProperties(so, NetworkSessionPreset.Custom, false);
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(profile);
        }

        private NetworkPrefabs ResolveOrCreateNetworkPrefabs(NetworkManager manager)
        {
            List<GameObject> spawnablePrefabs = GetSpawnablePlayerPrefabs();
            NetworkPrefabs prefabs = m_NetworkPrefabs;
            if (prefabs == null && manager != null)
            {
                prefabs = GetObjectReference<NetworkPrefabs>(new SerializedObject(manager), "_networkPrefabs");
            }

            if (prefabs == null && spawnablePrefabs.Count > 0 && m_CreateNetworkPrefabsAsset)
            {
                EnsureAssetFolder(GENERATED_ASSET_FOLDER);
                string assetPath = AssetDatabase.GenerateUniqueAssetPath($"{GENERATED_ASSET_FOLDER}/PurrNetSceneNetworkPrefabs.asset");
                prefabs = CreateInstance<NetworkPrefabs>();
                prefabs.autoGenerate = false;
                prefabs.networkOnly = true;
                prefabs.poolByDefault = false;
                AssetDatabase.CreateAsset(prefabs, assetPath);
                m_NetworkPrefabs = prefabs;
            }

            if (prefabs != null)
            {
                for (int i = 0; i < spawnablePrefabs.Count; i++)
                {
                    EnsureNetworkPrefabsContains(prefabs, spawnablePrefabs[i]);
                }
            }

            return prefabs;
        }

        private static void EnsureNetworkPrefabsContains(NetworkPrefabs prefabs, GameObject prefab)
        {
            if (prefabs == null || prefab == null) return;

            for (int i = 0; i < prefabs.prefabs.Count; i++)
            {
                if (prefabs.prefabs[i].prefab == prefab) return;
            }

            string assetPath = AssetDatabase.GetAssetPath(prefab);
            if (string.IsNullOrEmpty(assetPath))
            {
                Debug.LogWarning("[PurrNetSceneSetupWizard] Player Prefab is not a project asset; it was not added to NetworkPrefabs.");
                return;
            }

            Undo.RecordObject(prefabs, "Add Player Prefab to NetworkPrefabs");
            prefabs.prefabs.Add(new NetworkPrefabs.UserPrefabData
            {
                guid = AssetDatabase.AssetPathToGUID(assetPath),
                prefab = prefab,
                pooled = false,
                warmupCount = 0
            });
            prefabs.Refresh();
            EditorUtility.SetDirty(prefabs);
        }

        private static List<Transform> EnsureSpawnPoints(Transform spawnRoot, float spawnY)
        {
            var positions = new[]
            {
                new Vector3(-2f, spawnY, -2f),
                new Vector3( 2f, spawnY, -2f),
                new Vector3(-2f, spawnY,  2f),
                new Vector3( 2f, spawnY,  2f)
            };

            var points = new List<Transform>(positions.Length);
            for (int i = 0; i < positions.Length; i++)
            {
                string name = $"Spawn Point {i + 1}";
                Transform point = spawnRoot.Find(name);
                if (point == null)
                {
                    var pointGo = CreateChild(name, spawnRoot.gameObject);
                    point = pointGo.transform;
                }

                point.SetPositionAndRotation(positions[i], Quaternion.identity);
                points.Add(point);
            }

            return points;
        }

        private static void AssignDefaultRulesAndVisibility(SerializedObject managerSO)
        {
            var rulesProp = managerSO.FindProperty("_networkRules");
            if (rulesProp != null && rulesProp.objectReferenceValue == null)
            {
                rulesProp.objectReferenceValue = LoadFirstAsset(
                    new[]
                    {
                        "Assets/PurrNet/Defaults/NetworkRules/ServerStrict.asset",
                        "Assets/PurrNet/Defaults/NetworkRules/ServerOwner.asset",
                    },
                    "t:NetworkRules");
            }

            var visProp = managerSO.FindProperty("_visibilityRules");
            if (visProp != null && visProp.objectReferenceValue == null)
            {
                visProp.objectReferenceValue = LoadFirstAsset(
                    new[]
                    {
                        "Assets/PurrNet/Defaults/VisibilityRules/AlwaysVisible.asset",
                        "Assets/PurrNet/Defaults/VisibilityRules/DefaultDistance.asset",
                    },
                    "t:NetworkVisibilityRuleSet");
            }
        }

        private Component EnsureComponentByType(string typeName, string objectName, GameObject root)
        {
            Type type = Type.GetType(typeName);
            if (type == null)
            {
                Debug.LogWarning($"[PurrNetSceneSetupWizard] Type not found: {typeName}. Is the module installed and compiled?");
                return null;
            }

            var existing = FindSceneComponent(type);
            if (existing != null) return existing;

            GameObject go = GameObject.Find(objectName);
            if (go == null)
            {
                go = CreateChild(objectName, root);
            }

            var component = go.GetComponent(type);
            if (component != null) return component;

            component = Undo.AddComponent(go, type);
            return component;
        }

        private static Component EnsureComponentByTypeOnGameObject(
            GameObject target,
            string typeName,
            string componentName)
        {
            if (target == null) return null;

            Type type = Type.GetType(typeName);
            if (type == null || !typeof(Component).IsAssignableFrom(type))
            {
                Debug.LogWarning(
                    $"[PurrNetSceneSetupWizard] Could not add {componentName}. Type not found: {typeName}");
                return null;
            }

            Component component = target.GetComponent(type);
            return component != null ? component : Undo.AddComponent(target, type);
        }

        private T FindOrCreateComponent<T>(string objectName, GameObject root) where T : Component
        {
            var existing = FindSceneComponent<T>();
            if (existing != null) return existing;

            GameObject go = GameObject.Find(objectName);
            if (go == null)
            {
                go = CreateChild(objectName, root);
            }

            var component = go.GetComponent<T>();
            if (component != null) return component;

            return Undo.AddComponent<T>(go);
        }

        private static T FindSceneComponent<T>() where T : Component
        {
#if UNITY_2023_1_OR_NEWER
            var components = UnityEngine.Object.FindObjectsByType<T>(FindObjectsInactive.Include, FindObjectsSortMode.None);
#else
            var components = UnityEngine.Object.FindObjectsOfType<T>(true);
#endif
            return components != null && components.Length > 0 ? components[0] : null;
        }

        private static T[] FindSceneComponents<T>() where T : Component
        {
#if UNITY_2023_1_OR_NEWER
            T[] components = UnityEngine.Object.FindObjectsByType<T>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);
#else
            T[] components = UnityEngine.Object.FindObjectsOfType<T>(true);
#endif
            if (components == null || components.Length == 0) return Array.Empty<T>();

            var sceneComponents = new List<T>(components.Length);
            for (int i = 0; i < components.Length; i++)
            {
                T component = components[i];
                if (component != null && component.gameObject.scene.IsValid())
                {
                    sceneComponents.Add(component);
                }
            }

            return sceneComponents.ToArray();
        }

        private static Component FindSceneComponent(Type type)
        {
            if (type == null) return null;

#if UNITY_2023_1_OR_NEWER
            var objects = UnityEngine.Object.FindObjectsByType(type, FindObjectsInactive.Include, FindObjectsSortMode.None);
#else
            var objects = UnityEngine.Object.FindObjectsOfType(type, true);
#endif
            if (objects == null) return null;

            for (int i = 0; i < objects.Length; i++)
            {
                if (objects[i] is Component component && component.gameObject.scene.IsValid())
                {
                    return component;
                }
            }

            return null;
        }

        private static GameObject CreateChild(string name, GameObject parent)
        {
            var go = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(go, $"Create {name}");
            if (parent != null) Undo.SetTransformParent(go.transform, parent.transform, $"Parent {name}");
            return go;
        }

        private static GameObject CreateUIChild(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            Undo.RegisterCreatedObjectUndo(go, $"Create {name}");
            Undo.SetTransformParent(go.transform, parent, $"Parent {name}");
            return go;
        }

        private static bool MatchesChoice(GenericTransport t, TransportChoice choice)
        {
            if (t == null) return false;
            Type type = t.GetType();
            switch (choice)
            {
                case TransportChoice.UDP: return type == typeof(UDPTransport);
                case TransportChoice.PurrTransport: return type == typeof(PurrTransport);
                case TransportChoice.WebTransport: return type == typeof(WebTransport);
                case TransportChoice.Local: return type == typeof(LocalTransport);
                case TransportChoice.ExistingOrManual: return true;
                default: return false;
            }
        }

        private static Type TransportTypeFor(TransportChoice choice)
        {
            switch (choice)
            {
                case TransportChoice.UDP: return typeof(UDPTransport);
                case TransportChoice.PurrTransport: return typeof(PurrTransport);
                case TransportChoice.WebTransport: return typeof(WebTransport);
                case TransportChoice.Local: return typeof(LocalTransport);
                default: return null;
            }
        }

        private void ApplyTransportAddressPort(GenericTransport transport)
        {
            if (transport == null) return;

            var so = new SerializedObject(transport);
            var addressProp = so.FindProperty("_address");
            var portProp = so.FindProperty("_serverPort");

            if (addressProp != null && addressProp.propertyType == SerializedPropertyType.String)
            {
                addressProp.stringValue = m_Address;
            }

            if (portProp != null && portProp.propertyType == SerializedPropertyType.Integer)
            {
                portProp.intValue = m_Port;
            }

            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(transport);
        }

        private static void ConfigureLogSilenced(Component component)
        {
            if (component == null) return;

            var so = new SerializedObject(component);
            SetBool(so, "m_LogNetworkMessages", false);
            SetBool(so, "m_DebugLog", false);
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(component);
        }

        private static void AssignObjectReference(SerializedObject so, string propertyName, UnityEngine.Object value)
        {
            if (so == null || string.IsNullOrEmpty(propertyName)) return;

            var prop = so.FindProperty(propertyName);
            if (prop != null && prop.propertyType == SerializedPropertyType.ObjectReference)
            {
                prop.objectReferenceValue = value;
            }
        }

        private static void DrawProfileProperty(
            SerializedObject so,
            string propertyName,
            string label,
            string tooltip)
        {
            if (so == null) return;

            SerializedProperty prop = so.FindProperty(propertyName);
            if (prop == null) return;

            EditorGUILayout.PropertyField(prop, new GUIContent(label, tooltip));
        }

        private static void DrawNestedProfileProperty(
            SerializedProperty parent,
            string propertyName,
            string label,
            string tooltip)
        {
            if (parent == null) return;

            SerializedProperty prop = parent.FindPropertyRelative(propertyName);
            if (prop == null) return;

            EditorGUILayout.PropertyField(prop, new GUIContent(label, tooltip));
        }

        private static void SetSessionProfilePresetProperties(
            SerializedObject so,
            NetworkSessionPreset preset,
            bool autoApplyPreset)
        {
            if (so == null) return;

            SetEnumIndexIfPresent(so, "m_Preset", (int)preset);
            SetBoolIfPresent(so, "m_AutoApplyPreset", autoApplyPreset);
        }

        private static bool SetObjectReferenceIfPresent(
            SerializedObject so,
            string propertyName,
            UnityEngine.Object value)
        {
            if (so == null || string.IsNullOrEmpty(propertyName)) return false;

            var prop = so.FindProperty(propertyName);
            if (prop == null || prop.propertyType != SerializedPropertyType.ObjectReference) return false;
            if (prop.objectReferenceValue == value) return false;

            prop.objectReferenceValue = value;
            return true;
        }

        private static void AssignObjectReferenceArray(SerializedProperty prop, IReadOnlyList<UnityEngine.Object> values)
        {
            if (prop == null || !prop.isArray || values == null) return;

            prop.arraySize = values.Count;
            for (int i = 0; i < values.Count; i++)
            {
                prop.GetArrayElementAtIndex(i).objectReferenceValue = values[i];
            }
        }

        private static void AssignObjectReferenceArrayIfAny(SerializedProperty prop, IReadOnlyList<UnityEngine.Object> values)
        {
            if (values == null || values.Count == 0) return;
            AssignObjectReferenceArray(prop, values);
        }

        private static void AssignShooterWeaponRegistration(
            SerializedProperty registrations,
            UnityEngine.Object weapon,
            UnityEngine.Object modelPrefab,
            UnityEngine.Object handle)
        {
            if (registrations == null || !registrations.isArray || weapon == null) return;

            int targetIndex = -1;
            int emptyIndex = -1;
            for (int i = 0; i < registrations.arraySize; i++)
            {
                SerializedProperty candidate = registrations.GetArrayElementAtIndex(i);
                SerializedProperty candidateWeapon = candidate.FindPropertyRelative("Weapon");
                if (candidateWeapon == null) continue;

                if (candidateWeapon.objectReferenceValue == weapon)
                {
                    targetIndex = i;
                    break;
                }

                if (emptyIndex < 0 && candidateWeapon.objectReferenceValue == null)
                {
                    emptyIndex = i;
                }
            }

            if (targetIndex < 0) targetIndex = emptyIndex;
            if (targetIndex < 0)
            {
                targetIndex = registrations.arraySize;
                registrations.InsertArrayElementAtIndex(targetIndex);
            }

            SerializedProperty entry = registrations.GetArrayElementAtIndex(targetIndex);
            SerializedProperty weaponProperty = entry.FindPropertyRelative("Weapon");
            SerializedProperty prefabProperty = entry.FindPropertyRelative("ModelPrefab");
            SerializedProperty handleProperty = entry.FindPropertyRelative("Handle");

            if (weaponProperty != null) weaponProperty.objectReferenceValue = weapon;
            if (prefabProperty != null) prefabProperty.objectReferenceValue = modelPrefab;
            if (handleProperty != null) handleProperty.objectReferenceValue = handle;
        }

        private static void AssignGameObjectList(
            SerializedObject so,
            string propertyName,
            IReadOnlyList<GameObject> values)
        {
            if (so == null || string.IsNullOrEmpty(propertyName) || values == null) return;

            SerializedProperty prop = so.FindProperty(propertyName);
            if (prop == null || !prop.isArray) return;

            prop.arraySize = values.Count;
            for (int i = 0; i < values.Count; i++)
            {
                prop.GetArrayElementAtIndex(i).objectReferenceValue = values[i];
            }
        }

        private static void AssignStringArray(
            SerializedObject so,
            string propertyName,
            IReadOnlyList<GameObject> prefabs)
        {
            if (so == null || string.IsNullOrEmpty(propertyName) || prefabs == null) return;

            SerializedProperty prop = so.FindProperty(propertyName);
            if (prop == null || !prop.isArray) return;

            prop.arraySize = prefabs.Count;
            for (int i = 0; i < prefabs.Count; i++)
            {
                prop.GetArrayElementAtIndex(i).stringValue = GetPrefabDisplayName(prefabs[i], i);
            }
        }

        private static bool AddUniqueObjectReferences(
            SerializedProperty prop,
            IReadOnlyList<UnityEngine.Object> values)
        {
            if (prop == null || !prop.isArray || values == null || values.Count == 0) return false;

            bool changed = false;
            for (int i = 0; i < values.Count; i++)
            {
                UnityEngine.Object value = values[i];
                if (value == null || ContainsObjectReference(prop, value)) continue;

                int index = prop.arraySize;
                prop.arraySize++;
                prop.GetArrayElementAtIndex(index).objectReferenceValue = value;
                changed = true;
            }

            return changed;
        }

        private static bool ContainsObjectReference(SerializedProperty prop, UnityEngine.Object value)
        {
            if (prop == null || !prop.isArray || value == null) return false;

            for (int i = 0; i < prop.arraySize; i++)
            {
                if (prop.GetArrayElementAtIndex(i).objectReferenceValue == value) return true;
            }

            return false;
        }

        private static T GetObjectReference<T>(SerializedObject so, string propertyName) where T : UnityEngine.Object
        {
            if (so == null) return null;

            var prop = so.FindProperty(propertyName);
            return prop != null ? prop.objectReferenceValue as T : null;
        }

        private static void SetBool(SerializedObject so, string propertyName, bool value)
        {
            var prop = so.FindProperty(propertyName);
            if (prop != null && prop.propertyType == SerializedPropertyType.Boolean)
            {
                prop.boolValue = value;
            }
        }

        private static bool SetBoolIfPresent(SerializedObject so, string propertyName, bool value)
        {
            if (so == null) return false;

            var prop = so.FindProperty(propertyName);
            if (prop == null || prop.propertyType != SerializedPropertyType.Boolean) return false;
            if (prop.boolValue == value) return false;

            prop.boolValue = value;
            return true;
        }

        private static bool SetEnumIndexIfPresent(SerializedObject so, string propertyName, int value)
        {
            if (so == null) return false;

            var prop = so.FindProperty(propertyName);
            if (prop == null || prop.propertyType != SerializedPropertyType.Enum) return false;
            if (prop.enumValueIndex == value) return false;

            prop.enumValueIndex = value;
            return true;
        }

        private static void SetString(SerializedObject so, string propertyName, string value)
        {
            var prop = so.FindProperty(propertyName);
            if (prop != null && prop.propertyType == SerializedPropertyType.String)
            {
                prop.stringValue = value;
            }
        }

        private static void SetInt(SerializedObject so, string propertyName, int value)
        {
            var prop = so.FindProperty(propertyName);
            if (prop != null && prop.propertyType == SerializedPropertyType.Integer)
            {
                prop.intValue = value;
            }
        }

        private static void SetFloat(SerializedObject so, string propertyName, float value)
        {
            var prop = so.FindProperty(propertyName);
            if (prop != null && prop.propertyType == SerializedPropertyType.Float)
            {
                prop.floatValue = value;
            }
        }

        private static UnityEngine.Object LoadAsset(string path)
        {
            return string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
        }

        private static List<UnityEngine.Object> LoadExistingAssets(params string[] paths)
        {
            var assets = new List<UnityEngine.Object>();
            if (paths == null) return assets;

            for (int i = 0; i < paths.Length; i++)
            {
                var asset = LoadAsset(paths[i]);
                if (asset != null) assets.Add(asset);
            }

            return assets;
        }

        private static UnityEngine.Object LoadFirstAsset(string[] knownPaths, string searchFilter)
        {
            foreach (var path in knownPaths)
            {
                var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
                if (asset != null) return asset;
            }

            string[] guids = AssetDatabase.FindAssets(searchFilter);
            foreach (var guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
                if (asset != null) return asset;
            }

            return null;
        }

        private static void EnsureAssetFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder)) return;

            string[] parts = folder.Split('/');
            if (parts.Length == 0 || parts[0] != "Assets")
            {
                throw new InvalidOperationException($"Generated asset folder must be under Assets: {folder}");
            }

            string current = "Assets";
            for (int i = 1; i < parts.Length; i++)
            {
                string next = $"{current}/{parts[i]}";
                if (!AssetDatabase.IsValidFolder(next))
                {
                    AssetDatabase.CreateFolder(current, parts[i]);
                }

                current = next;
            }
        }

        private static void EnsureEventSystem()
        {
            EventSystem existing = FindSceneComponent<EventSystem>();
            if (existing != null)
            {
                EnsureCompatibleInputModule(existing.gameObject);
                return;
            }

            var eventSystem = new GameObject("EventSystem");
            Undo.RegisterCreatedObjectUndo(eventSystem, "Create EventSystem");
            Undo.AddComponent<EventSystem>(eventSystem);
            EnsureCompatibleInputModule(eventSystem);
        }

        private static void EnsureCompatibleInputModule(GameObject eventSystem)
        {
            if (eventSystem == null) return;

            Type inputSystemModuleType = Type.GetType(
                "UnityEngine.InputSystem.UI.InputSystemUIInputModule, Unity.InputSystem");

            if (inputSystemModuleType != null)
            {
                if (eventSystem.GetComponent(inputSystemModuleType) == null)
                {
                    Undo.AddComponent(eventSystem, inputSystemModuleType);
                }

                StandaloneInputModule[] legacyModules = eventSystem.GetComponents<StandaloneInputModule>();
                for (int i = 0; i < legacyModules.Length; i++)
                {
                    Undo.DestroyObjectImmediate(legacyModules[i]);
                }

                return;
            }

            if (eventSystem.GetComponent<BaseInputModule>() == null)
            {
                Undo.AddComponent<StandaloneInputModule>(eventSystem);
            }
        }

        private void SetAllOptionalModules(bool enabled)
        {
            m_ModuleStats = enabled;
            m_ModuleInventory = enabled;
            m_ModuleMelee = enabled;
            m_ModuleShooter = enabled;
            m_ModuleQuests = enabled;
            m_ModuleDialogue = enabled;
            m_ModuleTraversal = enabled;
            m_ModuleAbilities = enabled;
        }

        private void ApplyProjectTemplate(ProjectTemplate template)
        {
            if (template == ProjectTemplate.Custom) return;

            SetAllOptionalModules(false);

            m_CreateNetworkManager = true;
            m_ClearStartFlags = true;
            m_AssignDefaultRules = true;
            m_SetTickRate = true;
            m_CreateCoreManagers = true;
            m_CreateCoreBridges = true;
            m_CreateModuleManagers = true;
            m_CreateModuleBridges = true;
            m_RegisterInstalledDemoAssets = true;
            m_CreatePlayerSpawner = true;
            m_CreateNetworkPrefabsAsset = true;
            m_CreateSessionProfileAsset = true;
            m_CreateDemoCanvasUI = true;
            m_CreateControlsUI = true;
            m_ParentUnderRoot = true;
            m_Transport = TransportChoice.UDP;

            switch (template)
            {
                case ProjectTemplate.ShooterGame:
                    m_ModuleShooter = true;
                    m_ModuleStats = true;
                    m_SessionPreset = m_ExpectedPlayers <= 8 ? NetworkSessionPreset.Duel : NetworkSessionPreset.Standard;
                    m_TickRate = m_ExpectedPlayers <= 16 ? 60 : 40;
                    m_UITitle = "PurrNet Shooter";
                    m_UISubtitle = "GC2 Shooter Networking";
                    break;

                case ProjectTemplate.MeleeActionRPG:
                    m_ModuleMelee = true;
                    m_ModuleStats = true;
                    m_ModuleInventory = true;
                    m_ModuleQuests = true;
                    m_SessionPreset = m_ExpectedPlayers <= 8 ? NetworkSessionPreset.Duel : NetworkSessionPreset.Standard;
                    m_TickRate = m_ExpectedPlayers <= 16 ? 60 : 40;
                    m_UITitle = "PurrNet Action RPG";
                    m_UISubtitle = "Melee, Stats, Inventory";
                    break;

                case ProjectTemplate.CoopAdventure:
                    m_ModuleStats = true;
                    m_ModuleInventory = true;
                    m_ModuleMelee = true;
                    m_ModuleShooter = true;
                    m_ModuleQuests = true;
                    m_ModuleDialogue = true;
                    m_ModuleTraversal = true;
                    m_SessionPreset = m_ExpectedPlayers <= 8 ? NetworkSessionPreset.Standard : NetworkSessionPreset.Massive;
                    m_TickRate = m_ExpectedPlayers <= 8 ? 60 : 30;
                    m_UITitle = "PurrNet Co-op";
                    m_UISubtitle = "GC2 Co-op Adventure";
                    break;

                case ProjectTemplate.MMORPG:
                    m_ModuleStats = true;
                    m_ModuleInventory = true;
                    m_ModuleMelee = true;
                    m_ModuleQuests = true;
                    m_ModuleDialogue = true;
                    m_SessionPreset = NetworkSessionPreset.Massive;
                    m_TickRate = m_ExpectedPlayers <= 64 ? 30 : 20;
                    m_UITitle = "PurrNet MMO";
                    m_UISubtitle = "Large Session GC2 Networking";
                    break;

                case ProjectTemplate.SlowSimulation:
                    m_ModuleStats = true;
                    m_ModuleInventory = true;
                    m_ModuleQuests = true;
                    m_ModuleDialogue = true;
                    m_SessionPreset = NetworkSessionPreset.Massive;
                    m_TickRate = 20;
                    m_UITitle = "PurrNet Simulation";
                    m_UISubtitle = "Low Frequency GC2 Sync";
                    break;

                case ProjectTemplate.FullIntegrationSandbox:
                    SetAllOptionalModules(true);
                    m_SessionPreset = m_ExpectedPlayers <= 8 ? NetworkSessionPreset.Standard : NetworkSessionPreset.Massive;
                    m_TickRate = m_ExpectedPlayers <= 8 ? 60 : 30;
                    m_UITitle = "PurrNet Full Integration";
                    m_UISubtitle = "Every GC2 Networking Module";
                    break;

                case ProjectTemplate.Custom:
                default:
                    break;
            }

            m_CreateMeleeStatsDamageBridge = m_ModuleMelee && m_ModuleStats;
            m_CreateShooterStatsDamageBridge = m_ModuleShooter && m_ModuleStats;
        }

        private static string GetTemplateDescription(ProjectTemplate template)
        {
            switch (template)
            {
                case ProjectTemplate.ShooterGame:
                    return "Fast combat template. Enables Shooter + Stats, keeps high tick rates for aiming, firing, reloads, impacts, and responsive character motion.";
                case ProjectTemplate.MeleeActionRPG:
                    return "Action RPG template. Enables Melee + Stats + Inventory + Quests for skill attacks, reactions, damage, equipment, and progression workflows.";
                case ProjectTemplate.CoopAdventure:
                    return "Balanced co-op template. Enables combat, traversal, inventory, quests, and dialogue for a broad 2-8 player adventure prototype.";
                case ProjectTemplate.MMORPG:
                    return "Large-session template. Enables persistent RPG systems and uses cheaper session defaults to avoid overwhelming bandwidth and CPU as player count rises.";
                case ProjectTemplate.SlowSimulation:
                    return "Low-frequency template. Enables stateful gameplay modules while favoring lower tick rates and large-session relevance for slow-paced projects.";
                case ProjectTemplate.FullIntegrationSandbox:
                    return "Everything-on template. Useful for testing the whole integration or building a demo scene before trimming modules for production.";
                case ProjectTemplate.Custom:
                default:
                    return "Manual setup mode. Changing Expected Players does not apply recommendations and the current module, transport, tick-rate, and session settings stay untouched.";
            }
        }

        private static string PageTitle(WizardPage page)
        {
            switch (page)
            {
                case WizardPage.ProjectShape: return "Project Shape";
                case WizardPage.Modules: return "GC2 Modules";
                case WizardPage.Transport: return "Transport";
                case WizardPage.Infrastructure: return "Core Infrastructure";
                case WizardPage.SpawningAndUI: return "Scene Helpers";
                case WizardPage.Review: return "Review";
                default: return page.ToString();
            }
        }

        private static string PageHelp(WizardPage page)
        {
            switch (page)
            {
                case WizardPage.ProjectShape:
                    return "Start with the kind of game you are building. The wizard can then fill sensible defaults so you only review the important parts.";
                case WizardPage.Modules:
                    return "Choose which Game Creator 2 modules should send gameplay state over PurrNet. Fewer modules means less scene noise and less network traffic.";
                case WizardPage.Transport:
                    return "Choose the PurrNet transport and default connection address. Most local tests should keep UDP, 127.0.0.1, and port 5000.";
                case WizardPage.Infrastructure:
                    return "Review generated managers, bridges, session profile, and tick-rate defaults. The recommended values are safe for most projects.";
                case WizardPage.SpawningAndUI:
                    return "Assign your player prefab and choose optional helper UI. These helpers make the scene testable immediately but can be removed later.";
                case WizardPage.Review:
                    return "Final read-only summary before applying the scene changes.";
                default:
                    return string.Empty;
            }
        }

        private static string YesNo(bool value)
        {
            return value ? "Yes" : "No";
        }

        private string BuildSummary()
        {
            return "Will create/reuse: NetworkManager, PurrNet Transport Bridge, PurrNet Variable Bridge, " +
                   "PurrNet Animation Motion Bridge, Network Security Manager, Network Core Manager, " +
                   "Network Animation Manager, Network Motion Manager, Network Variable Manager" +
                   SelectedModuleSummary(prefix: ", selected modules: ");
        }

        private string BuildControlsText()
        {
            return $"{m_UITitle}\n" +
                   $"{m_UISubtitle}\n\n" +
                   "Runtime UI: Host / Join / Disconnect\n" +
                   $"Character Selection UI: {YesNo(m_CreateCharacterSelectionUI)}\n" +
                   $"Chat UI: {YesNo(m_CreateChatUI)}\n" +
                   $"Modules: Core, Variables{SelectedModuleSummary(prefix: ", ")}\n" +
                   (m_PlayerPrefab == null ? "Spawner: assign a Player Prefab\n" : $"Spawner: {m_PlayerPrefab.name}\n");
        }

        private string SelectedModuleSummary(string prefix)
        {
            var modules = new List<string>();
            if (m_ModuleStats) modules.Add("Stats");
            if (m_ModuleInventory) modules.Add("Inventory");
            if (m_ModuleMelee) modules.Add("Melee");
            if (m_ModuleShooter) modules.Add("Shooter");
            if (m_ModuleQuests) modules.Add("Quests");
            if (m_ModuleDialogue) modules.Add("Dialogue");
            if (m_ModuleTraversal) modules.Add("Traversal");
            if (m_ModuleAbilities) modules.Add("Abilities");
            return modules.Count == 0 ? string.Empty : prefix + string.Join(", ", modules);
        }
    }
}
