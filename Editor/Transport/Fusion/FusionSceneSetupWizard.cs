using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Arawn.EnemyMasses.Editor.Integration.GameCreator2.Patches;
using Arawn.GameCreator2.Networking.Editor;
using Arawn.GameCreator2.Networking.Security;
using Arawn.GameCreator2.Networking.Transport.Fusion;
using Fusion;
using Fusion.Editor;
using Fusion.Photon.Realtime;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Variables;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Arawn.GameCreator2.Networking.Transport.Fusion.Editor
{
    /// <summary>
    /// Creates a complete, server-authoritative GC2 networking scene backed by Photon Fusion.
    /// The workflow intentionally mirrors the PurrNet wizard and is safe to run repeatedly.
    /// </summary>
    public sealed class FusionSceneSetupWizard : EditorWindow
    {
        private enum WizardPage
        {
            ProjectShape = 0,
            Modules = 1,
            FusionSession = 2,
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
            FullIntegrationSandbox
        }

        private enum DefaultLaunchMode
        {
            Host,
            Shared
        }

        private enum RunnerSetup
        {
            CreateOrReuseArawnBootstrap,
            ExistingOrManual
        }

        private const string GeneratedRootFolder =
            "Assets/Arawn/NetworkingLayerForGC2/Generated";
        private const string GeneratedFolder = GeneratedRootFolder + "/Fusion";
        private const string SessionProfilePath =
            GeneratedFolder + "/NetworkSessionProfile.asset";
        private const string FusionProjectConfigPath =
            "Assets/Photon/Fusion/Resources/NetworkProjectConfig.fusion";
        private const string RuntimeAssemblyName =
            FusionSceneSetupValidation.RuntimeAssemblyName;
        private const string ProjectRuntimeAssemblyName =
            FusionSceneSetupValidation.ProjectRuntimeAssemblyName;
        private const string FusionLobbyServiceType =
            "Arawn.GameCreator2.Networking.Transport.Fusion.FusionLobbyService, " +
            RuntimeAssemblyName;
        private const string NetworkLobbyUiType =
            "Arawn.GameCreator2.Networking.Lobby.NetworkLobbyCanvasUI, " +
            "Arawn.GameCreator2.Networking.Lobby";
        private const string SwordWeaponPath =
            "Assets/Plugins/GameCreator/Installs/Melee.Sword@1.1.3/Sword_Weapon.asset";
        private const string AkWeaponPath =
            "Assets/Plugins/GameCreator/Installs/Shooter.Weapons@1.1.4/Weapons/AK_Weapon.asset";
        private const string AkWeaponPrefabPath =
            "Assets/Plugins/GameCreator/Installs/Shooter.Weapons@1.1.4/Prefabs/AK_Weapon.prefab";
        private const string AkWeaponHandlePath =
            "Assets/Plugins/GameCreator/Installs/Shooter.Weapons@1.1.4/Handles/Ak_Weapon_Handle.asset";
        private const string AbilityFireballSprayPath =
            "Assets/Plugins/GameCreator/Installs/Abilities.Examples@1.6.0/Abilities/FireballSpray/A_FireballSpray.asset";
        private const string AbilityFireballNovaPath =
            "Assets/Plugins/GameCreator/Installs/Abilities.Examples@1.6.0/Abilities/FireballNova/A_FireballNova.asset";
        private const string ProjectileFireballSprayPath =
            "Assets/Plugins/GameCreator/Installs/Abilities.Examples@1.6.0/Abilities/FireballSpray/M_FireballSpray.asset";
        private const string ProjectileFireballNovaPath =
            "Assets/Plugins/GameCreator/Installs/Abilities.Examples@1.6.0/Abilities/FireballNova/M_Fireball - Nova.asset";
        private const string ImpactFireballPath =
            "Assets/Plugins/GameCreator/Installs/Abilities.Examples@1.6.0/Abilities/Fireball/I_Fireball.asset";
        private const string ImpactBlastPath =
            "Assets/Plugins/GameCreator/Installs/Abilities.Examples@1.6.0/Abilities/Blast/I_Blast.asset";

        private const string FusionCoreBridgeType =
            "Arawn.GameCreator2.Networking.Transport.Fusion.FusionCoreTransportBridge, " +
            "Arawn.GameCreator2.Networking.Transport.Fusion";
        private const string FusionVariableBridgeType =
            "Arawn.GameCreator2.Networking.Transport.Fusion.FusionVariableTransportBridge, " +
            "Arawn.GameCreator2.Networking.Transport.Fusion";
        private const string FusionAnimationMotionBridgeType =
            "Arawn.GameCreator2.Networking.Transport.Fusion.FusionAnimationMotionTransportBridge, " +
            "Arawn.GameCreator2.Networking.Transport.Fusion";

        private const string NetworkStatsManagerType =
            "Arawn.GameCreator2.Networking.Stats.NetworkStatsManager, Arawn.GameCreator2.Networking.Stats";
        private const string NetworkStatsControllerType =
            "Arawn.GameCreator2.Networking.Stats.NetworkStatsController, Arawn.GameCreator2.Networking.Stats";
        private const string FusionStatsBridgeType =
            "Arawn.GameCreator2.Networking.Stats.Transport.Fusion.FusionStatsTransportBridge, " +
            "Arawn.GameCreator2.Networking.Stats.Transport.Fusion";

        private const string NetworkInventoryManagerType =
            "Arawn.GameCreator2.Networking.Inventory.NetworkInventoryManager, Arawn.GameCreator2.Networking.Inventory";
        private const string NetworkInventoryControllerType =
            "Arawn.GameCreator2.Networking.Inventory.NetworkInventoryController, Arawn.GameCreator2.Networking.Inventory";
        private const string FusionInventoryBridgeType =
            "Arawn.GameCreator2.Networking.Inventory.Transport.Fusion.FusionInventoryTransportBridge, " +
            "Arawn.GameCreator2.Networking.Inventory.Transport.Fusion";

        private const string NetworkMeleeManagerType =
            "Arawn.GameCreator2.Networking.Melee.NetworkMeleeManager, Arawn.GameCreator2.Networking.Melee";
        private const string NetworkMeleeControllerType =
            "Arawn.GameCreator2.Networking.Melee.NetworkMeleeController, Arawn.GameCreator2.Networking.Melee";
        private const string FusionMeleeBridgeType =
            "Arawn.GameCreator2.Networking.Melee.Transport.Fusion.FusionMeleeTransportBridge, " +
            "Arawn.GameCreator2.Networking.Melee.Transport.Fusion";

        private const string NetworkShooterManagerType =
            "Arawn.GameCreator2.Networking.Shooter.NetworkShooterManager, Arawn.GameCreator2.Networking.Shooter";
        private const string NetworkShooterControllerType =
            "Arawn.GameCreator2.Networking.Shooter.NetworkShooterController, Arawn.GameCreator2.Networking.Shooter";
        private const string FusionShooterBridgeType =
            "Arawn.GameCreator2.Networking.Shooter.Transport.Fusion.FusionShooterTransportBridge, " +
            "Arawn.GameCreator2.Networking.Shooter.Transport.Fusion";

        private const string NetworkQuestsManagerType =
            "Arawn.GameCreator2.Networking.Quests.NetworkQuestsManager, Arawn.GameCreator2.Networking.Quests";
        private const string NetworkQuestsControllerType =
            "Arawn.GameCreator2.Networking.Quests.NetworkQuestsController, Arawn.GameCreator2.Networking.Quests";
        private const string FusionQuestsBridgeType =
            "Arawn.GameCreator2.Networking.Quests.Transport.Fusion.FusionQuestsTransportBridge, " +
            "Arawn.GameCreator2.Networking.Quests.Transport.Fusion";

        private const string NetworkDialogueManagerType =
            "Arawn.GameCreator2.Networking.Dialogue.NetworkDialogueManager, Arawn.GameCreator2.Networking.Dialogue";
        private const string NetworkDialogueControllerType =
            "Arawn.GameCreator2.Networking.Dialogue.NetworkDialogueController, Arawn.GameCreator2.Networking.Dialogue";
        private const string DialogueComponentType =
            "GameCreator.Runtime.Dialogue.Dialogue, GameCreator.Runtime.Dialogue";
        private const string FusionDialogueBridgeType =
            "Arawn.GameCreator2.Networking.Dialogue.Transport.Fusion.FusionDialogueTransportBridge, " +
            "Arawn.GameCreator2.Networking.Dialogue.Transport.Fusion";

        private const string NetworkTraversalManagerType =
            "Arawn.GameCreator2.Networking.Traversal.NetworkTraversalManager, Arawn.GameCreator2.Networking.Traversal";
        private const string NetworkTraversalControllerType =
            "Arawn.GameCreator2.Networking.Traversal.NetworkTraversalController, Arawn.GameCreator2.Networking.Traversal";
        private const string FusionTraversalBridgeType =
            "Arawn.GameCreator2.Networking.Traversal.Transport.Fusion.FusionTraversalTransportBridge, " +
            "Arawn.GameCreator2.Networking.Traversal.Transport.Fusion";

        private const string NetworkAbilitiesControllerType =
            "Arawn.GameCreator2.Networking.NetworkAbilitiesController, Arawn.GameCreator2.Networking.Abilities";
        private const string FusionAbilitiesBridgeType =
            "Arawn.GameCreator2.Networking.Abilities.Transport.Fusion.FusionAbilitiesTransportBridge, " +
            "Arawn.GameCreator2.Networking.Abilities.Transport.Fusion";

        private WizardPage m_Page;
        private ProjectTemplate m_Template = ProjectTemplate.Custom;
        private int m_ExpectedPlayers = 4;
        private NetworkSessionPreset m_SessionPreset = NetworkSessionPreset.Standard;
        private NetworkPredictionBackend m_PredictionBackend = NetworkPredictionBackend.FusionNative;
        private FusionKccSharedAuthorityMode m_KccSharedAuthorityMode =
            FusionKccSharedAuthorityMode.OwnerMovementAuthority;

        private bool m_ModuleStats;
        private bool m_ModuleInventory;
        private bool m_ModuleMelee;
        private bool m_ModuleShooter;
        private bool m_ModuleQuests;
        private bool m_ModuleDialogue;
        private bool m_ModuleTraversal;
        private bool m_ModuleAbilities;
        private bool m_RegisterInstalledDemoAssets = true;

        private RunnerSetup m_RunnerSetup = RunnerSetup.CreateOrReuseArawnBootstrap;
        private DefaultLaunchMode m_DefaultLaunchMode = DefaultLaunchMode.Shared;
        private string m_SessionName = "GC2-Fusion";
        private string m_Region = string.Empty;
        private bool m_ForcePhotonRelay;
        private int m_TickRate = 32;
        private bool m_CreateSceneManager = true;
        private bool m_ApplyProjectConfiguration = true;

        private bool m_CreateCoreManagers = true;
        private bool m_CreateCoreBridges = true;
        private bool m_CreateModuleManagers = true;
        private bool m_CreateModuleBridges = true;
        private bool m_CreateSessionProfile = true;

        private GameObject m_PlayerPrefab;
        private bool m_ConfigurePlayerPrefab = true;
        private bool m_ConfigurePlayerKernel = true;
        private bool m_PlayerUsesLocalVariables;
        private bool m_CreatePlayerSpawner = true;
        private bool m_CreateDemoUI = true;
        private bool m_CreateLobbyUI;
        private bool m_CreateCharacterSelection;
        private readonly List<GameObject> m_CharacterSelectionPrefabs = new();
        private bool m_CreateControlsUI = true;
        private bool m_CreateChatUI;
        private string m_RootName = "Fusion Session";

        private Vector2 m_Scroll;
        private bool m_KccDefineRefreshQueued;

        [MenuItem("Game Creator/Networking Layer/Fusion Scene Setup Wizard", priority = 2)]
        public static void Open()
        {
            FusionSceneSetupWizard window =
                GetWindow<FusionSceneSetupWizard>(true, "Fusion Scene Setup Wizard");
            window.minSize = new Vector2(660f, 600f);
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

        private void DrawHeader()
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Photon Fusion Transport Setup", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                $"Step {(int)m_Page + 1} of 6 — {PageTitle(m_Page)}",
                EditorStyles.miniBoldLabel);
            EditorGUILayout.HelpBox(PageHelp(m_Page), MessageType.Info);

            string[] labels =
            {
                "1 Project",
                "2 Modules",
                "3 Fusion",
                "4 Core",
                "5 Scene",
                "6 Review"
            };

            EditorGUI.BeginChangeCheck();
            int next = GUILayout.Toolbar((int)m_Page, labels);
            if (EditorGUI.EndChangeCheck())
            {
                m_Page = (WizardPage)next;
                GUI.FocusControl(null);
            }

            EditorGUILayout.Space(8);
        }

        private void DrawCurrentPage()
        {
            switch (m_Page)
            {
                case WizardPage.ProjectShape:
                    DrawProjectPage();
                    break;
                case WizardPage.Modules:
                    DrawModulesPage();
                    break;
                case WizardPage.FusionSession:
                    DrawFusionPage();
                    break;
                case WizardPage.Infrastructure:
                    DrawInfrastructurePage();
                    break;
                case WizardPage.SpawningAndUI:
                    DrawScenePage();
                    break;
                case WizardPage.Review:
                    DrawReviewPage();
                    break;
            }
        }

        private void DrawNavigation()
        {
            EditorGUILayout.Space(6);
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(m_Page == WizardPage.ProjectShape))
                {
                    if (GUILayout.Button("Back", GUILayout.Width(110), GUILayout.Height(30)))
                    {
                        m_Page--;
                        m_Scroll = Vector2.zero;
                    }
                }

                GUILayout.FlexibleSpace();

                if (m_Page != WizardPage.Review)
                {
                    if (GUILayout.Button("Next", GUILayout.Width(110), GUILayout.Height(30)))
                    {
                        m_Page++;
                        m_Scroll = Vector2.zero;
                    }
                }
                else
                {
                    FusionSetupReport report = FusionSceneSetupValidation.Validate(
                        m_PlayerPrefab,
                        false,
                        m_PredictionBackend,
                        m_KccSharedAuthorityMode);
                    bool runnerSetupConflict = TryGetRunnerSetupConflict(out _);
                    bool modulePreflightError =
                        HasSelectedModulePreflightErrors(out _);
                    using (new EditorGUI.DisabledScope(
                               report.HasErrors ||
                               runnerSetupConflict ||
                               modulePreflightError))
                    {
                        if (GUILayout.Button(
                                "Create / Update Scene Setup",
                                GUILayout.Width(260),
                                GUILayout.Height(36)))
                        {
                            RunSetup();
                        }
                    }
                }
            }

            EditorGUILayout.Space(8);
        }

        private void DrawProjectPage()
        {
            EditorGUILayout.LabelField("Project Shape", EditorStyles.boldLabel);

            EditorGUI.BeginChangeCheck();
            ProjectTemplate template =
                (ProjectTemplate)EditorGUILayout.EnumPopup("Project Template", m_Template);
            if (EditorGUI.EndChangeCheck())
            {
                m_Template = template;
                ApplyTemplate(template);
            }

            m_ExpectedPlayers = EditorGUILayout.IntSlider("Expected Players", m_ExpectedPlayers, 2, 200);
            m_SessionPreset =
                (NetworkSessionPreset)EditorGUILayout.EnumPopup("Session Preset", m_SessionPreset);
            DrawPredictionBackendSelector();
            m_RootName = EditorGUILayout.TextField("Scene Root Name", m_RootName);

            EditorGUILayout.Space(8);
            EditorGUILayout.HelpBox(
                "The generated transport always supports both Host and Shared sessions. " +
                "Templates only select GC2 modules and recommended defaults.",
                MessageType.None);
        }

        private void DrawPredictionBackendSelector()
        {
            QueueKccDefineRefreshIfNeeded();
            GUIContent label = new GUIContent(
                "Prediction Backend",
                "Fusion Native runs GC2 movement inside Fusion ticks. Fusion Advanced KCC " +
                "uses Photon's optional Advanced KCC addon through a GC2 driver. Built-in " +
                "Legacy retains the transport-neutral RPC snapshot path for compatibility.");

            bool kccAvailable = FusionKccEditorIntegration.TryGetAvailableExtension(
                out _,
                out string unavailableReason);
            if (kccAvailable)
            {
                if (m_PredictionBackend != NetworkPredictionBackend.FusionNative &&
                    m_PredictionBackend != NetworkPredictionBackend.FusionKCC &&
                    m_PredictionBackend != NetworkPredictionBackend.BuiltIn)
                {
                    m_PredictionBackend = NetworkPredictionBackend.FusionNative;
                }

                int choice = m_PredictionBackend switch
                {
                    NetworkPredictionBackend.FusionKCC => 1,
                    NetworkPredictionBackend.BuiltIn => 2,
                    _ => 0
                };
                choice = EditorGUILayout.Popup(
                    label,
                    choice,
                    new[]
                    {
                        "Fusion Native (Recommended)",
                        "Fusion Advanced KCC (Optional Addon)",
                        "Built-in Legacy"
                    });
                m_PredictionBackend = choice switch
                {
                    1 => NetworkPredictionBackend.FusionKCC,
                    2 => NetworkPredictionBackend.BuiltIn,
                    _ => NetworkPredictionBackend.FusionNative
                };
            }
            else
            {
                // A serialized KCC selection must not leave the wizard in an unselectable state
                // after the optional addon is removed. The prefab itself is left untouched until
                // the user explicitly applies another backend.
                if (m_PredictionBackend == NetworkPredictionBackend.FusionKCC ||
                    (m_PredictionBackend != NetworkPredictionBackend.FusionNative &&
                     m_PredictionBackend != NetworkPredictionBackend.BuiltIn))
                {
                    m_PredictionBackend = NetworkPredictionBackend.FusionNative;
                }

                int predictionChoice =
                    m_PredictionBackend == NetworkPredictionBackend.BuiltIn ? 1 : 0;
                predictionChoice = EditorGUILayout.Popup(
                    label,
                    predictionChoice,
                    new[] { "Fusion Native (Recommended)", "Built-in Legacy" });
                m_PredictionBackend = predictionChoice == 0
                    ? NetworkPredictionBackend.FusionNative
                    : NetworkPredictionBackend.BuiltIn;

                using (new EditorGUI.DisabledScope(true))
                {
                    EditorGUILayout.ToggleLeft(
                        "Fusion Advanced KCC (Optional Addon)",
                        false);
                }
                EditorGUILayout.HelpBox(unavailableReason, MessageType.Info);
            }

            if (m_PredictionBackend != NetworkPredictionBackend.FusionKCC) return;

            int authorityChoice = m_KccSharedAuthorityMode ==
                                  FusionKccSharedAuthorityMode.SharedMasterMovementAuthority
                ? 1
                : 0;
            authorityChoice = EditorGUILayout.Popup(
                new GUIContent(
                    "KCC Shared Authority",
                    "Select who advances KCC movement in Fusion Shared Mode. This does not " +
                    "change Host/Client authority."),
                authorityChoice,
                new[]
                {
                    "Owner Movement Authority (Recommended)",
                    "Shared Master Movement Authority"
                });
            m_KccSharedAuthorityMode = authorityChoice == 1
                ? FusionKccSharedAuthorityMode.SharedMasterMovementAuthority
                : FusionKccSharedAuthorityMode.OwnerMovementAuthority;
            EditorGUILayout.HelpBox(
                m_KccSharedAuthorityMode == FusionKccSharedAuthorityMode.OwnerMovementAuthority
                    ? "In Shared Mode each player owns, predicts, and advances its own KCC. " +
                      "This is Photon's native Shared authority model and the recommended option."
                    : "In Shared Mode the Shared master advances movement from validated owner " +
                      "intent. Choose this only when the project requires centralized movement " +
                      "authority; it adds an extra routing step for remote owners.",
                MessageType.None);
        }

        private void QueueKccDefineRefreshIfNeeded()
        {
            if (m_KccDefineRefreshQueued ||
                !FusionKccEditorIntegration.IsApiInstalled ||
                GC2NetworkingDefineSymbols.IsFusionKccSymbolDefinedForCurrentBuildTarget())
            {
                return;
            }

            m_KccDefineRefreshQueued = true;
            EditorApplication.delayCall += () =>
            {
                if (this == null) return;
                if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                {
                    m_KccDefineRefreshQueued = false;
                    Repaint();
                    return;
                }

                GC2NetworkingDefineSymbols.RefreshNow();
                m_KccDefineRefreshQueued = false;
                Repaint();
            };
        }

        private void DrawModulesPage()
        {
            EditorGUILayout.LabelField("GC2 Modules", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Core, Variables, Animation, and Motion are always configured. Optional modules " +
                "are isolated in their own assemblies and are added only when installed.",
                MessageType.None);

            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.ToggleLeft("Core + Variables + Animation/Motion", true);
            }

            m_ModuleStats = DrawModuleToggle("Stats", NetworkStatsManagerType, m_ModuleStats);
            m_ModuleInventory = DrawInventoryModuleToggle(m_ModuleInventory);
            m_ModuleMelee = DrawModuleToggle("Melee", NetworkMeleeManagerType, m_ModuleMelee);
            m_ModuleShooter =
                DrawModuleToggle("Shooter", NetworkShooterManagerType, m_ModuleShooter);
            m_ModuleQuests = DrawModuleToggle("Quests", NetworkQuestsManagerType, m_ModuleQuests);
            m_ModuleDialogue =
                DrawModuleToggle("Dialogue", NetworkDialogueManagerType, m_ModuleDialogue);
            m_ModuleTraversal =
                DrawModuleToggle("Traversal", NetworkTraversalManagerType, m_ModuleTraversal);
            m_ModuleAbilities =
                DrawModuleToggle("Abilities", NetworkAbilitiesControllerType, m_ModuleAbilities);
            m_RegisterInstalledDemoAssets = EditorGUILayout.ToggleLeft(
                new GUIContent(
                    "Register installed GC2 example assets",
                    "Appends known installed Melee, Shooter, and Abilities example assets " +
                    "without replacing custom bridge registrations."),
                m_RegisterInstalledDemoAssets);

            DrawPatchStatus();
        }

        private void DrawFusionPage()
        {
            EditorGUILayout.LabelField("Fusion Session", EditorStyles.boldLabel);

            m_RunnerSetup = (RunnerSetup)EditorGUILayout.EnumPopup("Runner Setup", m_RunnerSetup);
            m_DefaultLaunchMode =
                (DefaultLaunchMode)EditorGUILayout.EnumPopup("Demo Default Mode", m_DefaultLaunchMode);
            m_SessionName = EditorGUILayout.TextField(
                new GUIContent(
                    "Session Name / Shared Prefix",
                    "Host/Client uses this exact Photon session name. Create Shared keeps it " +
                    "as a readable prefix and generates a unique join code at runtime. The " +
                    "runtime UI also offers Create Shared With Exact ID for caller-chosen codes."),
                m_SessionName);
            m_Region = FusionRegionCatalog.DrawPopup(
                new GUIContent(
                    "Region",
                    "Best Region asks Photon to choose automatically. Direct session-name " +
                    "joins require every peer to use the same region."),
                m_Region);
            if (string.IsNullOrWhiteSpace(m_Region))
            {
                EditorGUILayout.HelpBox(
                    "Best Region can resolve differently on each device. Select the same " +
                    "explicit region for direct named-session joins, or advertise the " +
                    "creator's resolved region through a lobby/invite service.",
                    MessageType.Warning);
            }
            m_ForcePhotonRelay = EditorGUILayout.ToggleLeft(
                new GUIContent(
                    "Force Photon Relay in Host/Client mode",
                    "Disables NAT punch-through for Host/Client sessions. " +
                    "Shared Mode already communicates through Photon Relay."),
                m_ForcePhotonRelay);
            if (m_DefaultLaunchMode == DefaultLaunchMode.Shared)
            {
                EditorGUILayout.HelpBox(
                    "Shared Mode always uses Photon Cloud Relay; the force-relay option is " +
                    "retained for Host/Client starts made by the same bootstrap. Create Shared " +
                    "generates a unique backend session name; clients must use the resulting " +
                    "Fusion Session Name (join code), not only this prefix. Create Shared With " +
                    "Exact ID uses the entered value verbatim and fails if it is already in use.",
                    MessageType.Info);
            }
            m_TickRate = EditorGUILayout.IntSlider("Dual-Mode Tick Rate", m_TickRate, 10, 32);
            m_CreateSceneManager = EditorGUILayout.ToggleLeft(
                "Create/reuse NetworkSceneManagerDefault",
                m_CreateSceneManager);
            m_ApplyProjectConfiguration = true;
            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.ToggleLeft(
                    "Apply required NetworkProjectConfig changes",
                    m_ApplyProjectConfiguration);
            }

            EditorGUILayout.Space(8);
            string appId = null;
            try
            {
                appId = PhotonAppSettings.Global?.AppSettings?.AppIdFusion;
            }
            catch
            {
                // Validation below renders the actionable message.
            }

            EditorGUILayout.HelpBox(
                string.IsNullOrWhiteSpace(appId)
                    ? "Fusion App Id: Missing. Scene generation is available; live session controls will be disabled."
                    : "Fusion App Id: Configured.",
                string.IsNullOrWhiteSpace(appId) ? MessageType.Warning : MessageType.Info);

            EditorGUILayout.HelpBox(
                "Shared uses the current master as the logical GC2 server. All transport-managed " +
                "gameplay objects remain master-owned; player ownership is stored separately.",
                MessageType.Info);
        }

        private void DrawInfrastructurePage()
        {
            EditorGUILayout.LabelField("Core Infrastructure", EditorStyles.boldLabel);

            m_CreateCoreManagers = true;
            m_CreateCoreBridges = true;
            m_CreateModuleManagers = true;
            m_CreateModuleBridges = true;
            m_CreateSessionProfile = true;
            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.ToggleLeft("Create/reuse core managers", m_CreateCoreManagers);
                EditorGUILayout.ToggleLeft("Create/reuse core Fusion bridges", m_CreateCoreBridges);
                EditorGUILayout.ToggleLeft(
                    "Create/reuse selected module managers",
                    m_CreateModuleManagers);
                EditorGUILayout.ToggleLeft(
                    "Create/reuse selected module Fusion bridges",
                    m_CreateModuleBridges);
                EditorGUILayout.ToggleLeft(
                    "Create/reuse deterministic Session Profile",
                    m_CreateSessionProfile);
            }

            EditorGUILayout.Space(8);
            EditorGUILayout.HelpBox(
                "Generated infrastructure includes NetworkSecurityManager, FusionTransportBridge, " +
                "FusionRpcRouter, readiness/authority handling, and the selected GC2 managers.",
                MessageType.None);
        }

        private void DrawScenePage()
        {
            EditorGUILayout.LabelField("Spawning and UI", EditorStyles.boldLabel);

            m_PlayerPrefab =
                (GameObject)EditorGUILayout.ObjectField("Player Prefab", m_PlayerPrefab, typeof(GameObject), false);
            m_ConfigurePlayerPrefab = true;
            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.ToggleLeft(
                    "Prepare player prefab when assigned",
                    m_ConfigurePlayerPrefab);
            }

            using (new EditorGUI.DisabledScope(!m_ConfigurePlayerPrefab || m_PlayerPrefab == null))
            {
                m_ConfigurePlayerKernel =
                    EditorGUILayout.ToggleLeft("Author network-ready GC2 Character kernel", m_ConfigurePlayerKernel);
                m_PlayerUsesLocalVariables =
                    EditorGUILayout.ToggleLeft("Add NetworkVariableController", m_PlayerUsesLocalVariables);
            }

            m_CreatePlayerSpawner = true;
            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.ToggleLeft(
                    "Create/reuse authority-only player spawner",
                    m_CreatePlayerSpawner);
            }

            if (m_RunnerSetup == RunnerSetup.ExistingOrManual)
            {
                m_CreateDemoUI = false;
                m_CreateLobbyUI = false;
                using (new EditorGUI.DisabledScope(true))
                {
                    EditorGUILayout.ToggleLeft(
                        "Create/reuse Host + Shared demo UI (Arawn bootstrap only)",
                        false);
                    EditorGUILayout.ToggleLeft(
                        "Create/reuse Photon lobby UI (Arawn bootstrap only)",
                        false);
                }
                EditorGUILayout.HelpBox(
                    "The existing runner owns session launch and shutdown UI. Character " +
                    "selection, controls, and chat helpers can still be generated.",
                    MessageType.None);
            }
            else
            {
                m_CreateDemoUI =
                    EditorGUILayout.ToggleLeft("Create/reuse Host + Shared demo UI", m_CreateDemoUI);
                bool createLobbyUI = EditorGUILayout.ToggleLeft(
                    new GUIContent(
                        "Create/reuse Photon multiplayer lobby UI",
                        "Adds the shared GC2 lobby menu backed by Fusion's native Photon session matchmaking."),
                    m_CreateLobbyUI);
                if (createLobbyUI && !m_CreateLobbyUI)
                {
                    m_CreateDemoUI = false;
                }
                m_CreateLobbyUI = createLobbyUI;
                if (m_CreateLobbyUI && m_CreateDemoUI)
                {
                    EditorGUILayout.HelpBox(
                        "Photon Lobby UI replaces the simple Host + Shared demo controls. " +
                        "Disable one of them to avoid duplicate session starts.",
                        MessageType.Warning);
                }
            }
            m_CreateCharacterSelection =
                EditorGUILayout.ToggleLeft("Create/reuse character selection helper", m_CreateCharacterSelection);
            if (m_CreateCharacterSelection)
            {
                DrawCharacterSelectionPrefabs();
            }
            m_CreateControlsUI =
                EditorGUILayout.ToggleLeft("Create controls help UI", m_CreateControlsUI);
            m_CreateChatUI =
                EditorGUILayout.ToggleLeft("Create chat UI when available", m_CreateChatUI);
        }

        private void DrawReviewPage()
        {
            EditorGUILayout.LabelField("Review", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(BuildSummary(), MessageType.None);

            FusionSetupReport report = FusionSceneSetupValidation.Validate(
                m_PlayerPrefab,
                false,
                m_PredictionBackend,
                m_KccSharedAuthorityMode);
            bool runnerSetupConflict = TryGetRunnerSetupConflict(out string runnerConflict);
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Validation", EditorStyles.boldLabel);

            if (FusionMonoBuildCompatibility.TryGetActiveBuildTargetIssue(out _))
            {
                if (GUILayout.Button("Fix Mono Build Compatibility (Disable Managed Stripping)"))
                {
                    FusionMonoBuildCompatibility.ConfigureActiveBuildTarget();
                    GUI.FocusControl(null);
                    Repaint();
                }
                EditorGUILayout.Space(4);
            }

            if (report.Issues.Count == 0)
            {
                EditorGUILayout.HelpBox("No configuration issues detected.", MessageType.Info);
            }
            else
            {
                foreach (FusionSetupIssue issue in report.Issues)
                {
                    MessageType messageType = issue.Severity switch
                    {
                        FusionSetupIssueSeverity.Error => MessageType.Error,
                        FusionSetupIssueSeverity.Warning => MessageType.Warning,
                        _ => MessageType.Info
                    };
                    EditorGUILayout.HelpBox(issue.Message, messageType);
                }
            }

            if (runnerSetupConflict)
            {
                EditorGUILayout.HelpBox(runnerConflict, MessageType.Error);
            }

            if (HasSelectedModulePreflightErrors(out string modulePreflight))
            {
                EditorGUILayout.HelpBox(modulePreflight, MessageType.Error);
            }
            else if (m_ModuleInventory)
            {
                InventorySceneSetupTools.ValidationSummary inventory =
                    InventorySceneSetupTools.ValidateScene(SceneManager.GetActiveScene());
                if (inventory.HasWarnings)
                {
                    EditorGUILayout.HelpBox(inventory.ToReport(), MessageType.Warning);
                }
            }

            if (HasDisableableTransportConflicts())
            {
                EditorGUILayout.Space(6);
                if (GUILayout.Button("Disable Conflicting Scene Transport Components"))
                {
                    DisableConflictingSceneComponents();
                }
            }

            if (report.HasErrors ||
                runnerSetupConflict ||
                HasSelectedModulePreflightErrors(out _))
            {
                EditorGUILayout.HelpBox(
                    "Resolve blocking errors before applying. Selecting a different Arawn " +
                    "movement backend explicitly converts its known Native/KCC components; " +
                    "unrelated Ninjutsu, PurrNet, and custom movement components are never " +
                    "silently removed.",
                    MessageType.Error);
            }
        }

        internal void ConfigureForDemoGeneration(
            GameObject playerPrefab,
            IEnumerable<GameObject> characterSelectionPrefabs,
            string sessionName,
            bool stats,
            bool inventory,
            bool melee,
            bool shooter,
            bool quests,
            bool dialogue,
            bool traversal,
            bool abilities,
            bool playerUsesLocalVariables,
            bool createCharacterSelection,
            bool createChat,
            bool createControlsUI = true,
            NetworkPredictionBackend predictionBackend =
                NetworkPredictionBackend.FusionNative,
            FusionKccSharedAuthorityMode kccSharedAuthorityMode =
                FusionKccSharedAuthorityMode.OwnerMovementAuthority)
        {
            m_Page = WizardPage.Review;
            m_Template = ProjectTemplate.Custom;
            m_ExpectedPlayers = 4;
            m_SessionPreset = NetworkSessionPreset.Standard;
            m_PredictionBackend = predictionBackend switch
            {
                NetworkPredictionBackend.FusionNative => predictionBackend,
                NetworkPredictionBackend.FusionKCC => predictionBackend,
                NetworkPredictionBackend.BuiltIn => predictionBackend,
                _ => NetworkPredictionBackend.FusionNative
            };
            m_KccSharedAuthorityMode = kccSharedAuthorityMode;

            m_ModuleStats = stats;
            m_ModuleInventory = inventory;
            m_ModuleMelee = melee;
            m_ModuleShooter = shooter;
            m_ModuleQuests = quests;
            m_ModuleDialogue = dialogue;
            m_ModuleTraversal = traversal;
            m_ModuleAbilities = abilities;
            m_RegisterInstalledDemoAssets = true;

            m_RunnerSetup = RunnerSetup.CreateOrReuseArawnBootstrap;
            m_DefaultLaunchMode = DefaultLaunchMode.Shared;
            m_SessionName = string.IsNullOrWhiteSpace(sessionName)
                ? "GC2-Fusion-Demo"
                : sessionName.Trim();
            m_Region = string.Empty;
            m_ForcePhotonRelay = false;
            m_TickRate = 32;
            m_CreateSceneManager = true;
            m_ApplyProjectConfiguration = true;

            m_CreateCoreManagers = true;
            m_CreateCoreBridges = true;
            m_CreateModuleManagers = true;
            m_CreateModuleBridges = true;
            m_CreateSessionProfile = true;

            m_PlayerPrefab = playerPrefab;
            m_ConfigurePlayerPrefab = true;
            m_ConfigurePlayerKernel = true;
            m_PlayerUsesLocalVariables = playerUsesLocalVariables;
            m_CreatePlayerSpawner = true;
            m_CreateDemoUI = true;
            m_CreateLobbyUI = false;
            m_CreateCharacterSelection = createCharacterSelection;
            m_CreateControlsUI = createControlsUI;
            m_CreateChatUI = createChat;
            m_RootName = "Fusion Session";

            m_CharacterSelectionPrefabs.Clear();
            if (characterSelectionPrefabs != null)
            {
                foreach (GameObject prefab in characterSelectionPrefabs)
                {
                    if (prefab != null && !m_CharacterSelectionPrefabs.Contains(prefab))
                    {
                        m_CharacterSelectionPrefabs.Add(prefab);
                    }
                }
            }

            if (createCharacterSelection &&
                playerPrefab != null &&
                !m_CharacterSelectionPrefabs.Contains(playerPrefab))
            {
                m_CharacterSelectionPrefabs.Insert(0, playerPrefab);
            }
        }

        internal bool RunSetupForAutomation()
        {
            return RunSetup(showDialogs: false);
        }

        private bool RunSetup()
        {
            return RunSetup(showDialogs: true);
        }

        private bool RunSetup(bool showDialogs)
        {
            FusionSetupReport preflight = FusionSceneSetupValidation.Validate(
                m_PlayerPrefab,
                false,
                m_PredictionBackend,
                m_KccSharedAuthorityMode);
            bool runnerSetupConflict =
                TryGetRunnerSetupConflict(out string runnerConflict);
            bool modulePreflightError =
                HasSelectedModulePreflightErrors(out string modulePreflight);
            if (preflight.HasErrors || runnerSetupConflict || modulePreflightError)
            {
                string message = !string.IsNullOrEmpty(runnerConflict)
                    ? runnerConflict
                    : !string.IsNullOrEmpty(modulePreflight)
                        ? modulePreflight
                        : "Resolve the blocking validation errors shown on the Review page.";
                if (showDialogs)
                {
                    EditorUtility.DisplayDialog("Fusion Scene Setup", message, "OK");
                }
                else
                {
                    Debug.LogError($"[FusionSceneSetupWizard] {message}");
                }
                return false;
            }

            if (!EnsureRequiredPatches()) return false;

            // Inventory's Arawn assemblies are intentionally constrained by the authority-patch
            // symbol. Applying the patch schedules a Unity compilation; do not enter the scene/
            // asset transaction until those assemblies are actually available.
            if (HasSelectedModulePreflightErrors(out string postPatchModulePreflight))
            {
                string message = postPatchModulePreflight +
                                 "\n\nIf an authority patch was just applied, let Unity finish " +
                                 "compiling and run the wizard again.";
                if (showDialogs)
                {
                    EditorUtility.DisplayDialog("Fusion Scene Setup", message, "OK");
                }
                else
                {
                    Debug.LogError($"[FusionSceneSetupWizard] {message}");
                }
                return false;
            }

            int undoGroup = -1;
            FusionSetupAssetUndo.Transaction assetTransaction = null;
            try
            {
                Undo.IncrementCurrentGroup();
                Undo.SetCurrentGroupName("Fusion Scene Setup");
                undoGroup = Undo.GetCurrentGroup();
                assetTransaction = FusionSetupAssetUndo.Begin(
                    GetAssetUndoPaths(),
                    "Fusion Scene Setup");

                GameObject root = ResolveRoot();

                if (m_ConfigurePlayerPrefab)
                {
                    foreach (GameObject prefab in GetConfiguredPlayerPrefabs())
                    {
                        string prefabReport = PreparePlayerPrefab(prefab);
                        if (!string.IsNullOrEmpty(prefabReport)) Debug.Log(prefabReport);
                    }
                }

                FusionTransportBridge transport =
                    FindOrCreateComponent<FusionTransportBridge>("Fusion Transport Bridge", root);
                FusionRpcRouter router =
                    FindOrCreateComponent<FusionRpcRouter>("Fusion RPC Router", root);

                FusionSessionBootstrap bootstrap = null;
                if (m_RunnerSetup == RunnerSetup.CreateOrReuseArawnBootstrap)
                {
                    bootstrap =
                        FindOrCreateComponent<FusionSessionBootstrap>("Fusion Session Bootstrap", root);
                    ConfigureBootstrap(bootstrap, transport, router);
                }

                if (m_CreateSceneManager)
                {
                    NetworkRunner existingRunner =
                        FusionSceneSetupValidation.FindSceneComponents<NetworkRunner>()
                            .FirstOrDefault();
                    if (bootstrap == null && existingRunner != null &&
                        existingRunner.GetComponent<INetworkSceneManager>() == null)
                    {
                        Undo.AddComponent<NetworkSceneManagerDefault>(existingRunner.gameObject);
                    }
                }

                NetworkSessionProfile profile = ResolveOrCreateSessionProfile();
                ConfigureTransport(transport, profile, bootstrap, router);

                EnsureCoreManagers(root);
                EnsureCoreBridges(root, transport);
                EnsureSelectedModuleManagers(root);
                EnsureSelectedModuleBridges(root, transport);

                FusionPlayerSpawner spawner = null;
                if (m_CreatePlayerSpawner)
                {
                    spawner = EnsurePlayerSpawner(root, transport);
                }

                EnsureOptionalSceneHelpers(root, bootstrap, spawner);

                bool weaveRegistrationChanged = ApplyFusionProjectConfiguration();

                ConvertInventoryPickupsIfSelected();

                EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
                AssetDatabase.SaveAssets();

                FusionSetupReport postflight =
                    FusionSceneSetupValidation.Validate(
                        m_PlayerPrefab,
                        true,
                        m_PredictionBackend,
                        m_KccSharedAuthorityMode);
                bool postModuleError =
                    HasSelectedModulePreflightErrors(out string postModuleReport);
                bool postflightFailed = postflight.HasErrors || postModuleError;
                if (postflightFailed)
                {
                    string validationErrors = string.Join(
                        "\n",
                        postflight.Issues
                            .Where(issue => issue.Severity == FusionSetupIssueSeverity.Error)
                            .Select(issue => "• " + issue.Message));
                    throw new InvalidOperationException(
                        "Post-setup validation failed; all Fusion setup changes were rolled back." +
                        (string.IsNullOrEmpty(validationErrors)
                            ? string.Empty
                            : "\n" + validationErrors) +
                        (string.IsNullOrEmpty(postModuleReport)
                            ? string.Empty
                            : "\n" + postModuleReport));
                }
                assetTransaction.Commit();
                Undo.CollapseUndoOperations(undoGroup);
                if (weaveRegistrationChanged)
                {
                    // Saving the Fusion config normally refreshes its weaver dependency.
                    // Request a clean weave explicitly as well so batch/editor automation and
                    // projects with the automatic trigger disabled receive generated RPC routes.
                    ILWeaverUtils.RunWeaver();
                    Debug.Log(
                        $"[FusionSceneSetupWizard] Registered required Fusion assemblies " +
                        $"('{RuntimeAssemblyName}'" +
                        (m_PredictionBackend == NetworkPredictionBackend.FusionKCC
                            ? $", '{ProjectRuntimeAssemblyName}'"
                            : string.Empty) +
                        ") for " +
                        "Fusion weaving and requested a clean script compilation.");
                }
                if (showDialogs)
                {
                    EditorUtility.DisplayDialog(
                        "Fusion Scene Setup",
                        "Scene updated. The selected GC2 modules now use the " +
                        "server-authoritative Fusion transport.",
                        "OK");
                }
                return true;
            }
            catch (Exception exception)
            {
                if (undoGroup >= 0)
                {
                    Undo.RevertAllDownToGroup(undoGroup);
                }
                assetTransaction?.Rollback();
                Debug.LogException(exception);
                if (showDialogs)
                {
                    EditorUtility.DisplayDialog(
                        "Fusion Scene Setup",
                        $"Setup failed: {exception.Message}",
                        "OK");
                }
                return false;
            }
        }

        private IEnumerable<string> GetAssetUndoPaths()
        {
            var paths = new HashSet<string>(StringComparer.Ordinal)
            {
                GeneratedRootFolder,
                GeneratedRootFolder + ".meta",
                GeneratedFolder,
                GeneratedFolder + ".meta",
                SessionProfilePath,
                SessionProfilePath + ".meta",
                FusionProjectConfigPath,
                FusionProjectConfigPath + ".meta"
            };

            try
            {
                string globalConfigPath =
                    FusionGlobalScriptableObjectUtils.GetGlobalAssetPath<NetworkProjectConfigAsset>();
                if (!string.IsNullOrWhiteSpace(globalConfigPath))
                {
                    paths.Add(globalConfigPath);
                    paths.Add(globalConfigPath + ".meta");
                }
            }
            catch
            {
                // Validation reports an unreadable/ambiguous global config. Keep the default
                // path in the transaction so a normal first-time Fusion install remains safe.
            }

            foreach (GameObject prefab in GetConfiguredPlayerPrefabs())
            {
                string path = AssetDatabase.GetAssetPath(prefab);
                if (string.IsNullOrEmpty(path)) continue;
                paths.Add(path);
                paths.Add(path + ".meta");
            }

            // Fusion's prefab-table rebuild normalizes labels on every prefab, not only the
            // selected player prefab. Preserve all prefab meta files so the one wizard Undo
            // group can restore any label adjustments exactly.
            foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { "Assets" }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.IsNullOrEmpty(path)) paths.Add(path + ".meta");
            }

            foreach (string guid in AssetDatabase.FindAssets(
                         "t:NetworkProjectConfigAsset",
                         new[] { "Assets" }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path)) continue;
                paths.Add(path);
                paths.Add(path + ".meta");
            }

            return paths;
        }

        private GameObject ResolveRoot()
        {
            GameObject root = FindSceneGameObject(m_RootName);
            if (root != null) return root;

            root = new GameObject(m_RootName);
            Undo.RegisterCreatedObjectUndo(root, "Create Fusion Session Root");
            return root;
        }

        private NetworkSessionProfile ResolveOrCreateSessionProfile()
        {
            if (!m_CreateSessionProfile) return null;

            EnsureAssetFolder(GeneratedFolder);
            NetworkSessionProfile profile =
                AssetDatabase.LoadAssetAtPath<NetworkSessionProfile>(SessionProfilePath);
            if (profile == null)
            {
                profile = CreateInstance<NetworkSessionProfile>();
                profile.ApplyPreset(m_SessionPreset);
                AssetDatabase.CreateAsset(profile, SessionProfilePath);
                Undo.RegisterCreatedObjectUndo(
                    profile,
                    "Create Fusion Network Session Profile");
            }
            else if (profile.Preset != m_SessionPreset)
            {
                Undo.RecordObject(profile, "Update Fusion Network Session Profile");
                profile.ApplyPreset(m_SessionPreset);
                EditorUtility.SetDirty(profile);
            }

            return profile;
        }

        private void ConfigureBootstrap(
            FusionSessionBootstrap bootstrap,
            FusionTransportBridge transport,
            FusionRpcRouter router)
        {
            if (bootstrap == null) return;

            var serialized = new SerializedObject(bootstrap);
            SetString(serialized, "m_DefaultSessionName", m_SessionName);
            SetString(serialized, "m_SessionName", m_SessionName);
            SetString(serialized, "m_Region", m_Region);
            SetBool(serialized, "m_ForcePhotonRelay", m_ForcePhotonRelay);
            SetInt(serialized, "m_PlayerCount", m_ExpectedPlayers);
            SetInt(serialized, "m_MaxPlayers", m_ExpectedPlayers);
            SetEnum(
                serialized,
                "m_DefaultLaunchMode",
                (int)(m_DefaultLaunchMode == DefaultLaunchMode.Shared
                    ? FusionDefaultLaunchMode.Shared
                    : FusionDefaultLaunchMode.Host));
            SetEnum(serialized, "m_DefaultMode", (int)m_DefaultLaunchMode);
            SetObject(serialized, "m_Transport", transport);
            SetObject(serialized, "m_TransportBridge", transport);
            SetObject(serialized, "m_RpcRouter", router);
            SetBool(serialized, "m_CreateDefaultSceneManager", m_CreateSceneManager);
            serialized.ApplyModifiedProperties();
            EditorUtility.SetDirty(bootstrap);
        }

        private static void ConfigureTransport(
            FusionTransportBridge transport,
            NetworkSessionProfile profile,
            FusionSessionBootstrap bootstrap,
            FusionRpcRouter router)
        {
            if (transport == null) return;

            var serialized = new SerializedObject(transport);
            SetObject(serialized, "m_GlobalSessionProfile", profile);
            SetObject(serialized, "m_SessionBootstrap", bootstrap);
            SetObject(serialized, "m_RpcRouter", router);
            NetworkRunner runner =
                FusionSceneSetupValidation.FindSceneComponents<NetworkRunner>()
                    .SingleOrDefault();
            if (bootstrap == null && runner != null)
            {
                SetObject(serialized, "m_Runner", runner);
            }
            else if (bootstrap == null)
            {
                // An external FusionBootstrap creates its runner at runtime, so explicit
                // binding is impossible during scene setup.
                SetBool(serialized, "m_AutoBindSingleRunner", true);
            }
            serialized.ApplyModifiedProperties();
            EditorUtility.SetDirty(transport);
        }

        private static void EnsureCoreManagers(GameObject root)
        {
            GC2SceneSetupShared.EnsureCoreManagers(
                root,
                (type, objectName, parent) =>
                    EnsureComponentByType(type.AssemblyQualifiedName, objectName, parent));
        }

        private static void EnsureCoreBridges(GameObject root, FusionTransportBridge transport)
        {
            ConfigureBridge(
                EnsureComponentByType(FusionCoreBridgeType, "Fusion Core Bridge", root),
                transport);
            ConfigureBridge(
                EnsureComponentByType(FusionVariableBridgeType, "Fusion Variable Bridge", root),
                transport);
            ConfigureBridge(
                EnsureComponentByType(
                    FusionAnimationMotionBridgeType,
                    "Fusion Animation Motion Bridge",
                    root),
                transport);
        }

        private void EnsureSelectedModuleManagers(GameObject root)
        {
            if (m_ModuleStats)
                EnsureComponentByType(NetworkStatsManagerType, "Network Stats Manager", root);
            if (m_ModuleInventory)
                EnsureComponentByType(NetworkInventoryManagerType, "Network Inventory Manager", root);
            if (m_ModuleMelee)
                EnsureComponentByType(NetworkMeleeManagerType, "Network Melee Manager", root);
            if (m_ModuleShooter)
                EnsureComponentByType(NetworkShooterManagerType, "Network Shooter Manager", root);
            if (m_ModuleQuests)
                EnsureComponentByType(NetworkQuestsManagerType, "Network Quests Manager", root);
            if (m_ModuleDialogue)
                EnsureComponentByType(NetworkDialogueManagerType, "Network Dialogue Manager", root);
            if (m_ModuleTraversal)
                EnsureComponentByType(NetworkTraversalManagerType, "Network Traversal Manager", root);
            if (m_ModuleAbilities)
                EnsureComponentByType(NetworkAbilitiesControllerType, "Network Abilities Controller", root);
        }

        private void EnsureSelectedModuleBridges(GameObject root, FusionTransportBridge transport)
        {
            if (m_ModuleStats)
                ConfigureBridge(
                    EnsureComponentByType(FusionStatsBridgeType, "Fusion Stats Bridge", root),
                    transport);
            if (m_ModuleInventory)
                ConfigureBridge(
                    EnsureComponentByType(FusionInventoryBridgeType, "Fusion Inventory Bridge", root),
                    transport);
            if (m_ModuleMelee)
            {
                Component bridge =
                    EnsureComponentByType(FusionMeleeBridgeType, "Fusion Melee Bridge", root);
                ConfigureBridge(bridge, transport);
                ConfigureMeleeDemoRegistrations(bridge);
            }
            if (m_ModuleShooter)
            {
                Component bridge =
                    EnsureComponentByType(FusionShooterBridgeType, "Fusion Shooter Bridge", root);
                ConfigureBridge(bridge, transport);
                ConfigureShooterDemoRegistration(bridge);
            }
            if (m_ModuleQuests)
                ConfigureBridge(
                    EnsureComponentByType(FusionQuestsBridgeType, "Fusion Quests Bridge", root),
                    transport);
            if (m_ModuleDialogue)
                ConfigureBridge(
                    EnsureComponentByType(FusionDialogueBridgeType, "Fusion Dialogue Bridge", root),
                    transport);
            if (m_ModuleTraversal)
                ConfigureBridge(
                    EnsureComponentByType(FusionTraversalBridgeType, "Fusion Traversal Bridge", root),
                    transport);
            if (m_ModuleAbilities)
            {
                Component bridge =
                    EnsureComponentByType(FusionAbilitiesBridgeType, "Fusion Abilities Bridge", root);
                ConfigureBridge(bridge, transport);
                ConfigureAbilitiesDemoRegistrations(bridge);
            }
        }

        private FusionPlayerSpawner EnsurePlayerSpawner(
            GameObject root,
            FusionTransportBridge transport)
        {
            FusionPlayerSpawner spawner =
                FindOrCreateComponent<FusionPlayerSpawner>("Fusion Player Spawner", root);
            FusionAuthoritySpawnRegistry spawnRegistry =
                FindOrCreateComponent<FusionAuthoritySpawnRegistry>(
                    "Fusion Authority Spawn Registry",
                    root);
            var registrySerialized = new SerializedObject(spawnRegistry);
            SetObject(registrySerialized, "m_TransportBridge", transport);
            registrySerialized.ApplyModifiedProperties();
            EditorUtility.SetDirty(spawnRegistry);

            Transform spawnRoot = spawner.transform.Find("Spawn Points");
            if (spawnRoot == null)
            {
                spawnRoot = CreateChild("Spawn Points", spawner.gameObject).transform;
            }

            List<Transform> points = EnsureSpawnPoints(spawnRoot);
            var serialized = new SerializedObject(spawner);
            NetworkObject defaultPrefab =
                m_PlayerPrefab != null ? m_PlayerPrefab.GetComponent<NetworkObject>() : null;
            SetObject(serialized, "m_PlayerPrefab", defaultPrefab);
            SetObjectArray(
                serialized,
                "m_PlayerPrefabs",
                m_CreateCharacterSelection
                    ? GetConfiguredPlayerPrefabs()
                        .Select(prefab => prefab.GetComponent<NetworkObject>())
                        .Where(prefab => prefab != null)
                        .Cast<UnityEngine.Object>()
                        .ToArray()
                    : Array.Empty<UnityEngine.Object>());
            SetObject(serialized, "m_CharacterSelection", null);
            SetBool(serialized, "m_WaitForCharacterSelection", false);
            SetObject(serialized, "m_Transport", transport);
            SetObject(serialized, "m_TransportBridge", transport);
            SetObject(serialized, "m_SpawnRegistry", spawnRegistry);
            SetTransformArray(serialized, "m_SpawnPoints", points);
            serialized.ApplyModifiedProperties();
            EditorUtility.SetDirty(spawner);
            return spawner;
        }

        private void EnsureOptionalSceneHelpers(
            GameObject root,
            FusionSessionBootstrap bootstrap,
            FusionPlayerSpawner spawner)
        {
            if (m_CreateDemoUI && bootstrap != null)
            {
                Component ui = EnsureComponentByType(
                    "Arawn.GameCreator2.Networking.Transport.Fusion.FusionDemoSessionUI, " +
                    RuntimeAssemblyName,
                    "Fusion Demo UI",
                    root);
                ConfigureHelper(ui, bootstrap, spawner);
                EnsureEventSystem();
            }

            if (m_CreateLobbyUI && bootstrap != null)
            {
                Component service = EnsureComponentByType(
                    FusionLobbyServiceType,
                    "Fusion Lobby Service",
                    root);
                if (service != null)
                {
                    var serviceObject = new SerializedObject(service);
                    SetObject(serviceObject, "m_SessionBootstrap", bootstrap);
                    serviceObject.ApplyModifiedProperties();
                    EditorUtility.SetDirty(service);
                }

                Component lobbyUi = EnsureComponentByType(
                    NetworkLobbyUiType,
                    "GC2 Multiplayer Lobby UI",
                    root);
                if (lobbyUi != null)
                {
                    var uiObject = new SerializedObject(lobbyUi);
                    SetObject(uiObject, "m_ServiceBehaviour", service);
                    SetString(uiObject, "m_Title", "Fusion Multiplayer");
                    SetString(uiObject, "m_Subtitle", "Game Creator 2 Networking");
                    SetString(uiObject, "m_DefaultSessionName", m_SessionName);
                    SetString(uiObject, "m_DefaultRegion", m_Region);
                    SetInt(uiObject, "m_DefaultMaxPlayers", m_ExpectedPlayers);
                    SetInt(
                        uiObject,
                        "m_DefaultTopology",
                        m_DefaultLaunchMode == DefaultLaunchMode.Shared ? 1 : 0);
                    uiObject.ApplyModifiedProperties();
                    EditorUtility.SetDirty(lobbyUi);
                }

#if UNITY_2023_1_OR_NEWER
                FusionDemoSessionUI directUi = UnityEngine.Object.FindFirstObjectByType<
                    FusionDemoSessionUI>(FindObjectsInactive.Include);
#else
                FusionDemoSessionUI directUi = UnityEngine.Object.FindObjectOfType<
                    FusionDemoSessionUI>(true);
#endif
                if (directUi != null && directUi.enabled)
                {
                    Undo.RecordObject(directUi, "Disable duplicate Fusion demo session UI");
                    directUi.enabled = false;
                    EditorUtility.SetDirty(directUi);
                }

                EnsureEventSystem();
            }

            if (m_CreateCharacterSelection)
            {
                Component selection = EnsureComponentByType(
                    "Arawn.GameCreator2.Networking.Transport.Fusion.FusionDemoCharacterSelection, " +
                    RuntimeAssemblyName,
                    "Fusion Character Selection",
                    root);
                ConfigureHelper(selection, bootstrap, spawner);
                if (selection != null && spawner != null)
                {
                    var serialized = new SerializedObject(spawner);
                    SetObject(serialized, "m_CharacterSelection", selection);
                    SetBool(serialized, "m_WaitForCharacterSelection", true);
                    serialized.ApplyModifiedProperties();
                    EditorUtility.SetDirty(spawner);
                }
            }

            if (m_CreateControlsUI)
            {
                EnsureComponentByType(
                    "Arawn.GameCreator2.Networking.Transport.Fusion.FusionDemoControlsUI, " +
                    RuntimeAssemblyName,
                    "Fusion Controls UI",
                    root);
            }

            if (m_CreateChatUI)
            {
                Component chat = EnsureComponentByType(
                    "Arawn.GameCreator2.Networking.Transport.Fusion.FusionChatBoxUI, " +
                    RuntimeAssemblyName,
                    "Fusion Chat UI",
                    root);
                ConfigureHelper(chat, bootstrap, spawner);
            }
        }

        private static void ConfigureHelper(
            Component helper,
            FusionSessionBootstrap bootstrap,
            FusionPlayerSpawner spawner)
        {
            if (helper == null) return;

            var serialized = new SerializedObject(helper);
            SetObject(serialized, "m_Bootstrap", bootstrap);
            SetObject(serialized, "m_SessionBootstrap", bootstrap);
            SetObject(serialized, "m_PlayerSpawner", spawner);
            serialized.ApplyModifiedProperties();
            EditorUtility.SetDirty(helper);
        }

        private string PreparePlayerPrefab(GameObject prefab)
        {
            string path = AssetDatabase.GetAssetPath(prefab);
            if (string.IsNullOrEmpty(path)) return null;

            IFusionKccEditorSetupExtension kccExtension = null;
            bool hasKccExtension = FusionKccEditorIntegration.TryGetAvailableExtension(
                out kccExtension,
                out string kccUnavailableReason);
            if (m_PredictionBackend == NetworkPredictionBackend.FusionKCC &&
                !hasKccExtension)
            {
                throw new InvalidOperationException(
                    "Fusion Advanced KCC was selected, but its optional setup extension is " +
                    $"unavailable: {kccUnavailableReason}");
            }

            GameObject root = PrefabUtility.LoadPrefabContents(path);
            var changes = new List<string>();

            try
            {
                if (EnsurePrefabComponent<NetworkObject>(root, out NetworkObject networkObject))
                    changes.Add("NetworkObject");

                NetworkObjectFlags desired = networkObject.Flags |
                                             NetworkObjectFlags.MasterClientObject;
                desired &= ~NetworkObjectFlags.AllowStateAuthorityOverride;
                desired &= ~NetworkObjectFlags.DestroyWhenStateAuthorityLeaves;
                if (m_PredictionBackend == NetworkPredictionBackend.FusionNative)
                {
                    // Fusion's prefab baker normally derives this bit. Set it immediately as
                    // well so a prefab prepared and launched in the same editor update already
                    // exposes the native motor as its main spatial TRSP.
                    desired |= NetworkObjectFlags.HasMainNetworkTRSP;
                }
                else
                {
                    // Advanced KCC owns a nested NetworkObject/TRSP and Built-in Legacy has no
                    // Fusion TRSP on the GC2 root. Do not retain a stale native bake marker.
                    desired &= ~NetworkObjectFlags.HasMainNetworkTRSP;
                }
                if (networkObject.Flags != desired)
                {
                    networkObject.Flags = desired;
                    changes.Add("master-owned authority flags");
                }

                if (!networkObject.EnableInterpolation)
                {
                    networkObject.EnableInterpolation = true;
                    changes.Add("Fusion render interpolation");
                }

                if (EnsurePrefabComponent<Character>(root, out Character character))
                    changes.Add("Character");
                if (character != null && character.IsPlayer)
                {
                    character.IsPlayer = false;
                    changes.Add("Character.IsPlayer=false");
                }

                if (character != null && m_ConfigurePlayerKernel &&
                    ConfigureNetworkReadyCharacterKernel(character))
                {
                    changes.Add("network-ready GC2 Character kernel");
                }

                if (EnsurePrefabComponent<NetworkCharacter>(root, out NetworkCharacter networkCharacter))
                    changes.Add("NetworkCharacter");
                if (ConfigureNetworkCharacter(networkCharacter))
                    changes.Add("NetworkCharacter authority settings");

                if (EnsurePrefabComponent<FusionNetworkIdentity>(root, out _))
                    changes.Add("FusionNetworkIdentity");
                if (m_PredictionBackend == NetworkPredictionBackend.FusionNative)
                {
                    RemoveOptionalKccSetup(root, kccExtension, changes);
                    RemoveKccBackendProxies(root, changes, "Fusion Native");

                    if (EnsurePrefabComponent<FusionNativeNetworkCharacterMotor>(
                            root,
                            out FusionNativeNetworkCharacterMotor nativeMotor))
                    {
                        changes.Add("FusionNativeNetworkCharacterMotor");
                    }
                    if (!nativeMotor.enabled)
                    {
                        nativeMotor.enabled = true;
                        changes.Add("enabled FusionNativeNetworkCharacterMotor");
                    }
                    RemoveDuplicateNativeMotors(root, nativeMotor, changes);

                    Transform mannequin = character?.Animim?.Mannequin;
                    Transform presentationRoot = mannequin != null &&
                                                 FusionNativeNetworkCharacterMotor
                                                     .IsSafePresentationVisualRoot(
                                                         root.transform,
                                                         mannequin)
                        ? mannequin
                        : null;
                    var motorSerialized = new SerializedObject(nativeMotor);
                    SerializedProperty presentationProperty = motorSerialized.FindProperty(
                        "m_ListenHostPresentationVisualRoot");
                    if (presentationProperty != null &&
                        presentationProperty.objectReferenceValue != presentationRoot)
                    {
                        presentationProperty.objectReferenceValue = presentationRoot;
                        motorSerialized.ApplyModifiedPropertiesWithoutUndo();
                        changes.Add(presentationRoot != null
                            ? "listen-host visual presentation root"
                            : "cleared unsafe listen-host presentation root");
                    }
                }
                else if (m_PredictionBackend == NetworkPredictionBackend.FusionKCC)
                {
                    RemoveNativeMotors(root, changes, "Fusion Advanced KCC");
                    FusionKccCharacterBackend kccBackend =
                        EnsureSingleKccBackendProxy(root, changes);
                    if (ConfigureKccBackend(kccBackend))
                    {
                        changes.Add("Fusion KCC Shared authority policy");
                    }

                    var options = new FusionKccEditorSetupOptions(
                        m_KccSharedAuthorityMode);
                    if (!kccExtension.ConfigurePlayerPrefab(
                            root,
                            options,
                            changes,
                            out string setupError))
                    {
                        throw new InvalidOperationException(
                            string.IsNullOrWhiteSpace(setupError)
                                ? "The optional Fusion Advanced KCC setup extension failed " +
                                  $"to configure '{path}'."
                                : setupError);
                    }
                }
                else
                {
                    RemoveOptionalKccSetup(root, kccExtension, changes);
                    RemoveKccBackendProxies(root, changes, "Built-in Legacy");
                    RemoveNativeMotors(root, changes, "Built-in Legacy");
                }
                if (EnsurePrefabComponent<FusionNetworkCharacterAuto>(root, out _))
                    changes.Add("FusionNetworkCharacterAuto");

                NetworkCoreController[] legacy =
                    root.GetComponentsInChildren<NetworkCoreController>(true);
                if (legacy.Length > 0)
                {
                    foreach (NetworkCoreController controller in legacy)
                    {
                        if (controller != null) DestroyImmediate(controller, true);
                    }
                    changes.Add($"removed {legacy.Length} legacy NetworkCoreController component(s)");
                }

                if (m_PlayerUsesLocalVariables &&
                    EnsurePrefabComponent<NetworkVariableController>(root, out _))
                {
                    changes.Add("NetworkVariableController");
                }

                EnsureSelectedPrefabControllers(root, changes);

                if (changes.Count > 0)
                {
                    PrefabUtility.SaveAsPrefabAsset(root, path);
                    AssetDatabase.ImportAsset(path);
                }
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }

            EnsureFusionPrefabLabel(prefab);
            return changes.Count == 0
                ? $"[FusionSceneSetupWizard] Player prefab '{path}' already has the selected setup."
                : $"[FusionSceneSetupWizard] Prepared '{path}': {string.Join(", ", changes)}.";
        }

        private void RemoveOptionalKccSetup(
            GameObject root,
            IFusionKccEditorSetupExtension extension,
            IList<string> changes)
        {
            if (extension == null)
            {
                // If the customer removed Advanced KCC, the define-guarded adapter types no
                // longer exist and TypeCache cannot create the typed cleanup extension. The
                // KCC-independent ownership marker remains available, so conversion can remove a
                // wizard-created object without ever deleting an adopted customer hierarchy.
                FusionKccCharacterBackend backend =
                    root != null ? root.GetComponent<FusionKccCharacterBackend>() : null;
                FusionKccSetupMarker[] setupMarkers = root != null
                    ? root.GetComponentsInChildren<FusionKccSetupMarker>(true)
                    : Array.Empty<FusionKccSetupMarker>();
                if (setupMarkers.Length > 1)
                {
                    throw new InvalidOperationException(
                        $"'{root.name}' contains multiple Fusion KCC ownership markers. " +
                        "Reinstall KCC and normalize the prefab before converting it.");
                }

                FusionKccSetupMarker retainedMarker = setupMarkers.SingleOrDefault();
                Transform orphanedMotor = retainedMarker != null
                    ? retainedMarker.transform
                    : root != null
                        ? root.transform.Find("Fusion KCC Motor")
                        : null;
                bool hadKccArtifacts = backend != null || orphanedMotor != null;
                bool restoredRootControllerSnapshot = false;
                if (orphanedMotor != null)
                {
                    FusionKccSetupMarker marker =
                        retainedMarker != null
                            ? retainedMarker
                            : orphanedMotor.GetComponent<FusionKccSetupMarker>();
                    if (marker == null || !marker.MotorObjectCreatedByWizard)
                    {
                        throw new InvalidOperationException(
                            $"'{root.name}' contains a custom/adopted Fusion KCC Motor. " +
                            "Advanced KCC is unavailable, so the wizard cannot safely remove " +
                            "that customer's hierarchy. Reinstall KCC to convert it or remove " +
                            "the custom motor manually.");
                    }

                    bool restoreRootController = marker.HasRootControllerSnapshot &&
                                                 marker.HadRootCharacterController;
                    bool rootControllerWasEnabled =
                        marker.OriginalRootCharacterControllerEnabled;
                    restoredRootControllerSnapshot = marker.HasRootControllerSnapshot;

                    CharacterController originalController =
                        root.GetComponent<CharacterController>();
                    if (restoreRootController && originalController == null)
                    {
                        throw new InvalidOperationException(
                            $"'{root.name}' originally had a CharacterController, but that " +
                            "component is now missing. The wizard stopped before removing the " +
                            "orphaned KCC motor.");
                    }

                    foreach (GameObject processorObject in
                             marker.WizardOwnedProcessorObjects ?? Array.Empty<GameObject>())
                    {
                        if (processorObject == null) continue;
                        if (processorObject.transform.parent == orphanedMotor) continue;
                        throw new InvalidOperationException(
                            $"'{root.name}' has a wizard-owned KCC processor object " +
                            $"('{processorObject.name}') outside its recorded motor hierarchy. " +
                            "The wizard stopped before deleting anything. Reinstall KCC and " +
                            "normalize the prefab first.");
                    }

                    DestroyImmediate(orphanedMotor.gameObject, true);
                    changes?.Add(
                        "removed the orphaned wizard-owned Fusion KCC Motor after addon removal");

                    if (restoreRootController && originalController != null &&
                        originalController.enabled != rootControllerWasEnabled)
                    {
                        originalController.enabled = rootControllerWasEnabled;
                        EditorUtility.SetDirty(originalController);
                        changes?.Add(
                            rootControllerWasEnabled
                                ? "restored the enabled CharacterController after KCC addon removal"
                                : "restored the disabled CharacterController after KCC addon removal");
                    }
                }

                CharacterController controller =
                    root != null ? root.GetComponent<CharacterController>() : null;
                if (hadKccArtifacts && !restoredRootControllerSnapshot && controller != null &&
                    !controller.enabled)
                {
                    controller.enabled = true;
                    EditorUtility.SetDirty(controller);
                    changes?.Add(
                        "re-enabled the CharacterController after KCC addon removal");
                }
                return;
            }
            if (extension.RemoveFromPlayerPrefab(root, changes, out string error)) return;

            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(error)
                    ? $"The optional Fusion Advanced KCC setup extension failed to remove its " +
                      $"components from '{root.name}'."
                    : error);
        }

        private static FusionKccCharacterBackend EnsureSingleKccBackendProxy(
            GameObject root,
            IList<string> changes)
        {
            FusionKccCharacterBackend backend =
                root.GetComponent<FusionKccCharacterBackend>();
            if (backend == null)
            {
                backend = root.AddComponent<FusionKccCharacterBackend>();
                changes.Add("FusionKccCharacterBackend");
            }
            if (!backend.enabled)
            {
                backend.enabled = true;
                changes.Add("enabled FusionKccCharacterBackend");
            }

            FusionKccCharacterBackend[] all =
                root.GetComponentsInChildren<FusionKccCharacterBackend>(true);
            int removed = 0;
            foreach (FusionKccCharacterBackend candidate in all)
            {
                if (candidate == null || candidate == backend) continue;
                DestroyImmediate(candidate, true);
                removed++;
            }
            if (removed > 0)
            {
                changes.Add($"removed {removed} duplicate FusionKccCharacterBackend component(s)");
            }

            return backend;
        }

        private bool ConfigureKccBackend(FusionKccCharacterBackend backend)
        {
            if (backend == null) return false;
            var serialized = new SerializedObject(backend);
            bool changed = SetEnum(
                serialized,
                "m_SharedAuthorityMode",
                (int)m_KccSharedAuthorityMode);
            if (!changed) return false;

            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(backend);
            return true;
        }

        private static void RemoveKccBackendProxies(
            GameObject root,
            IList<string> changes,
            string selectedBackend)
        {
            FusionKccCharacterBackend[] backends =
                root.GetComponentsInChildren<FusionKccCharacterBackend>(true);
            foreach (FusionKccCharacterBackend backend in backends)
            {
                if (backend != null) DestroyImmediate(backend, true);
            }
            if (backends.Length > 0)
            {
                changes.Add(
                    $"removed {backends.Length} FusionKccCharacterBackend component(s) for " +
                    selectedBackend);
            }
        }

        private static void RemoveNativeMotors(
            GameObject root,
            IList<string> changes,
            string selectedBackend)
        {
            FusionNativeNetworkCharacterMotor[] motors =
                root.GetComponentsInChildren<FusionNativeNetworkCharacterMotor>(true);
            foreach (FusionNativeNetworkCharacterMotor motor in motors)
            {
                if (motor != null) DestroyImmediate(motor, true);
            }
            if (motors.Length > 0)
            {
                changes.Add(
                    $"removed {motors.Length} FusionNativeNetworkCharacterMotor component(s) " +
                    $"for {selectedBackend}");
            }
        }

        private static void RemoveDuplicateNativeMotors(
            GameObject root,
            FusionNativeNetworkCharacterMotor retainedMotor,
            IList<string> changes)
        {
            FusionNativeNetworkCharacterMotor[] motors =
                root.GetComponentsInChildren<FusionNativeNetworkCharacterMotor>(true);
            int removed = 0;
            foreach (FusionNativeNetworkCharacterMotor motor in motors)
            {
                if (motor == null || motor == retainedMotor) continue;
                DestroyImmediate(motor, true);
                removed++;
            }
            if (removed > 0)
            {
                changes.Add(
                    $"removed {removed} duplicate FusionNativeNetworkCharacterMotor component(s)");
            }
        }

        private void EnsureSelectedPrefabControllers(GameObject root, List<string> changes)
        {
            if (m_ModuleStats)
                EnsurePrefabComponentByType(root, NetworkStatsControllerType, "NetworkStatsController", changes);
            if (m_ModuleInventory)
                EnsurePrefabComponentByType(
                    root,
                    NetworkInventoryControllerType,
                    "NetworkInventoryController",
                    changes);
            if (m_ModuleMelee)
                EnsurePrefabComponentByType(root, NetworkMeleeControllerType, "NetworkMeleeController", changes);
            if (m_ModuleShooter)
                EnsurePrefabComponentByType(
                    root,
                    NetworkShooterControllerType,
                    "NetworkShooterController",
                    changes);
            if (m_ModuleQuests)
                EnsurePrefabComponentByType(root, NetworkQuestsControllerType, "NetworkQuestsController", changes);
            if (m_ModuleDialogue && HasComponentByType(root, DialogueComponentType))
                EnsurePrefabComponentByType(
                    root,
                    NetworkDialogueControllerType,
                    "NetworkDialogueController",
                    changes);
            if (m_ModuleTraversal)
                EnsurePrefabComponentByType(
                    root,
                    NetworkTraversalControllerType,
                    "NetworkTraversalController",
                    changes);
        }

        private static bool ConfigureNetworkReadyCharacterKernel(Character character)
        {
            return GC2SceneSetupShared.ConfigureNetworkReadyCharacterKernel(
                character,
                applyWithUndo: true);
        }

        private bool ConfigureNetworkCharacter(NetworkCharacter character)
        {
            if (character == null) return false;

            var serialized = new SerializedObject(character);
            bool changed = false;
            changed |= SetEnum(serialized, "m_NPCMode", (int)NetworkCharacter.NPCSyncMode.ServerAuthoritative);
            changed |= SetBool(serialized, "m_UseNetworkMotion", true);
            changed |= SetBool(serialized, "m_UseAnimationSync", true);
            changed |= SetBool(serialized, "m_UseCoreNetworking", true);
            changed |= SetBool(serialized, "m_HostOwnerUsesClientPrediction", true);
            changed |= SetEnum(
                serialized,
                "m_PredictionBackend",
                (int)m_PredictionBackend);
            if (changed)
            {
                serialized.ApplyModifiedProperties();
                EditorUtility.SetDirty(character);
            }
            return changed;
        }

        private bool ApplyFusionProjectConfiguration()
        {
            var config = new NetworkProjectConfig();
            EditorJsonUtility.FromJsonOverwrite(
                EditorJsonUtility.ToJson(NetworkProjectConfig.Global),
                config);
            config.PeerMode = NetworkProjectConfig.PeerModes.Single;
            config.Simulation.PlayerCount = m_ExpectedPlayers;
            // GC2 traversal/root-motion poses participate in Fusion prediction and rollback.
            // LatestState drops historical inputs and makes State Authority advance a fast
            // animation in multi-tick jumps, so redundant input history is mandatory.
            config.Simulation.InputTransferMode =
                SimulationConfig.InputTransferModes.Redundancy;

            TickRate.Selection selection = config.Simulation.TickRateSelection;
            selection.Client = Mathf.Clamp(m_TickRate, 10, 32);
            selection.ClientSendInterval = 1;
            selection.ServerTickInterval = 1;
            selection.ServerSendInterval = 1;
            config.Simulation.TickRateSelection = selection;

            config.Network.ReliableDataTransferModes =
                NetworkConfiguration.ReliableDataTransfers.ClientToServer |
                NetworkConfiguration.ReliableDataTransfers.ClientToClientWithServerProxy;

            bool weaveRegistrationChanged = EnsureRuntimeAssemblyWeaveEntry(config);
            if (m_PredictionBackend == NetworkPredictionBackend.FusionKCC)
            {
                // Advanced KCC and the optional adapter intentionally compile into the project
                // assembly, because Photon's addon has no runtime asmdef. It contains Networked
                // state and RPCs and therefore must be explicitly woven.
                weaveRegistrationChanged |= EnsureAssemblyWeaveEntry(
                    config,
                    ProjectRuntimeAssemblyName);
            }

            NetworkProjectConfigUtilities.SaveGlobalConfig(config);
            NetworkProjectConfig.UnloadGlobal();

            foreach (GameObject prefab in GetConfiguredPlayerPrefabs())
            {
                EnsureFusionPrefabLabel(prefab);
            }
            NetworkProjectConfigUtilities.RebuildPrefabTable();
            return weaveRegistrationChanged;
        }

        private static bool EnsureRuntimeAssemblyWeaveEntry(NetworkProjectConfig config)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));

            string[] current = config.AssembliesToWeave ?? Array.Empty<string>();
            var normalized = new List<string>(current.Length + 1);
            bool found = false;

            foreach (string assemblyName in current)
            {
                if (string.Equals(
                        assemblyName,
                        RuntimeAssemblyName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    if (!found)
                    {
                        normalized.Add(RuntimeAssemblyName);
                        found = true;
                    }

                    // Drop additional case variants of our own entry. Do not normalize or
                    // deduplicate entries owned by Fusion or another customer integration.
                    continue;
                }

                normalized.Add(assemblyName);
            }

            if (!found) normalized.Add(RuntimeAssemblyName);

            string[] result = normalized.ToArray();
            bool changed = !current.SequenceEqual(result, StringComparer.Ordinal);
            config.AssembliesToWeave = result;
            return changed;
        }

        private static bool EnsureAssemblyWeaveEntry(
            NetworkProjectConfig config,
            string requiredAssemblyName)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            if (string.IsNullOrWhiteSpace(requiredAssemblyName))
                throw new ArgumentException(
                    "A non-empty Fusion weave assembly name is required.",
                    nameof(requiredAssemblyName));

            string[] current = config.AssembliesToWeave ?? Array.Empty<string>();
            var normalized = new List<string>(current.Length + 1);
            bool found = false;
            foreach (string assemblyName in current)
            {
                if (string.Equals(
                        assemblyName,
                        requiredAssemblyName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    if (!found)
                    {
                        normalized.Add(requiredAssemblyName);
                        found = true;
                    }
                    continue;
                }

                normalized.Add(assemblyName);
            }
            if (!found) normalized.Add(requiredAssemblyName);

            string[] result = normalized.ToArray();
            bool changed = !current.SequenceEqual(result, StringComparer.Ordinal);
            config.AssembliesToWeave = result;
            return changed;
        }

        private bool EnsureRequiredPatches()
        {
            if (m_ModuleInventory && !EnsurePatch(new InventoryPatcher())) return false;
            if (m_ModuleMelee && !EnsurePatch(new MeleePatcher())) return false;
            if (m_ModuleTraversal && !EnsurePatch(new TraversalPatcher())) return false;
            if (m_ModuleShooter && !EnsurePatch(new ShooterPatcher())) return false;

            if (m_ModuleShooter &&
                !ShooterSightPatchRequirement.EnsureAppliedWithPrompt(
                    "Fusion Scene Setup Wizard",
                    out string sightReport))
            {
                if (!string.IsNullOrEmpty(sightReport)) Debug.LogWarning(sightReport);
                return false;
            }

            GC2NetworkingDefineSymbols.RefreshNow(false);
            return true;
        }

        private static bool EnsurePatch(GC2PatcherBase patcher)
        {
            if (patcher == null) return false;
            if (!patcher.ValidateFilesExist())
            {
                EditorUtility.DisplayDialog(
                    $"Missing {patcher.ModuleName} Patch Sources",
                    $"{patcher.DisplayName} cannot validate or apply its required authority " +
                    "patch because the expected GC2 source files are unavailable. Install a " +
                    "supported source package before configuring Fusion.",
                    "OK");
                return false;
            }
            if (patcher.IsPatched()) return true;

            bool apply = EditorUtility.DisplayDialog(
                $"Required {patcher.ModuleName} Networking Patch",
                $"{patcher.DisplayName} requires its server-authority hook patch before " +
                "the Fusion transport can be configured.\n\n" +
                patcher.PatchDescription,
                "Apply Required Patch",
                "Cancel Setup");
            if (!apply) return false;

            if (!patcher.TryValidateVersionCompatibility(out string compatibility))
            {
                bool continueAnyway = EditorUtility.DisplayDialog(
                    $"{patcher.ModuleName} Version Compatibility",
                    compatibility,
                    "Apply Anyway",
                    "Cancel Setup");
                if (!continueAnyway) return false;
            }

            return patcher.ApplyPatch();
        }

        private void ConvertInventoryPickupsIfSelected()
        {
            if (!m_ModuleInventory) return;
            InventorySceneSetupTools.ConvertStockScenePickups(
                SceneManager.GetActiveScene(),
                false);
        }

        private static void ConfigureBridge(Component bridge, FusionTransportBridge transport)
        {
            if (bridge == null) return;

            var serialized = new SerializedObject(bridge);
            SetObject(serialized, "m_TransportBridge", transport);
            SetObject(serialized, "m_FusionTransportBridge", transport);
            SetObject(serialized, "m_Transport", transport);
            serialized.ApplyModifiedProperties();
            EditorUtility.SetDirty(bridge);

            // Setup runs in edit mode. Calling a module's runtime Configure method here can
            // bind immediately and initialize scene/prefab controllers before their GC2
            // runtime dependencies exist (Inventory is especially sensitive to this).
            // Serialized references are sufficient; OnEnable performs the real binding.
            if (!Application.isPlaying) return;

            MethodInfo configure = bridge.GetType().GetMethod(
                "Configure",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new[] { typeof(FusionTransportBridge) },
                null);
            configure?.Invoke(bridge, new object[] { transport });
        }

        private void ConfigureMeleeDemoRegistrations(Component bridge)
        {
            if (!m_RegisterInstalledDemoAssets || bridge == null) return;
            var serialized = new SerializedObject(bridge);
            if (!AppendObjectReferences(
                    serialized.FindProperty("m_RegisterWeapons"),
                    LoadExistingAssets(SwordWeaponPath)))
            {
                return;
            }

            serialized.ApplyModifiedProperties();
            EditorUtility.SetDirty(bridge);
        }

        private void ConfigureShooterDemoRegistration(Component bridge)
        {
            if (!m_RegisterInstalledDemoAssets || bridge == null) return;
            UnityEngine.Object weapon = AssetDatabase.LoadMainAssetAtPath(AkWeaponPath);
            if (weapon == null) return;

            var serialized = new SerializedObject(bridge);
            SerializedProperty registrations =
                serialized.FindProperty("m_WeaponRegistrations");
            if (registrations == null || !registrations.isArray) return;

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
            bool changed = SetObjectReference(
                entry.FindPropertyRelative("Weapon"),
                weapon);
            changed |= SetObjectReference(
                entry.FindPropertyRelative("ModelPrefab"),
                AssetDatabase.LoadMainAssetAtPath(AkWeaponPrefabPath));
            changed |= SetObjectReference(
                entry.FindPropertyRelative("Handle"),
                AssetDatabase.LoadMainAssetAtPath(AkWeaponHandlePath));
            if (!changed) return;

            serialized.ApplyModifiedProperties();
            EditorUtility.SetDirty(bridge);
        }

        private void ConfigureAbilitiesDemoRegistrations(Component bridge)
        {
            if (!m_RegisterInstalledDemoAssets || bridge == null) return;
            var serialized = new SerializedObject(bridge);
            bool changed = AppendObjectReferences(
                serialized.FindProperty("m_RegisterAbilities"),
                LoadExistingAssets(AbilityFireballSprayPath, AbilityFireballNovaPath));
            changed |= AppendObjectReferences(
                serialized.FindProperty("m_RegisterProjectiles"),
                LoadExistingAssets(
                    ProjectileFireballSprayPath,
                    ProjectileFireballNovaPath));
            changed |= AppendObjectReferences(
                serialized.FindProperty("m_RegisterImpacts"),
                LoadExistingAssets(ImpactFireballPath, ImpactBlastPath));
            if (!changed) return;

            serialized.ApplyModifiedProperties();
            EditorUtility.SetDirty(bridge);
        }

        private static List<UnityEngine.Object> LoadExistingAssets(params string[] paths)
        {
            var result = new List<UnityEngine.Object>();
            if (paths == null) return result;
            for (int i = 0; i < paths.Length; i++)
            {
                UnityEngine.Object asset = AssetDatabase.LoadMainAssetAtPath(paths[i]);
                if (asset != null && !result.Contains(asset)) result.Add(asset);
            }
            return result;
        }

        private static bool AppendObjectReferences(
            SerializedProperty array,
            IReadOnlyList<UnityEngine.Object> values)
        {
            if (array == null || !array.isArray || values == null || values.Count == 0)
                return false;

            var combined = new List<UnityEngine.Object>();
            for (int i = 0; i < array.arraySize; i++)
            {
                UnityEngine.Object existing =
                    array.GetArrayElementAtIndex(i).objectReferenceValue;
                if (existing != null && !combined.Contains(existing)) combined.Add(existing);
            }

            int before = combined.Count;
            for (int i = 0; i < values.Count; i++)
            {
                UnityEngine.Object value = values[i];
                if (value != null && !combined.Contains(value)) combined.Add(value);
            }
            if (combined.Count == before && array.arraySize == before) return false;

            array.arraySize = combined.Count;
            for (int i = 0; i < combined.Count; i++)
            {
                array.GetArrayElementAtIndex(i).objectReferenceValue = combined[i];
            }
            return true;
        }

        private static bool SetObjectReference(
            SerializedProperty property,
            UnityEngine.Object value)
        {
            if (property == null ||
                property.propertyType != SerializedPropertyType.ObjectReference ||
                value == null ||
                property.objectReferenceValue == value)
            {
                return false;
            }

            property.objectReferenceValue = value;
            return true;
        }

        private static Component EnsureComponentByType(
            string typeName,
            string objectName,
            GameObject root)
        {
            Type type = Type.GetType(typeName);
            if (type == null || !typeof(Component).IsAssignableFrom(type))
            {
                Debug.LogWarning(
                    $"[FusionSceneSetupWizard] Optional type is unavailable: {typeName}");
                return null;
            }

            Component existing = FindSceneComponent(type);
            if (existing != null) return existing;

            GameObject target = FindSceneGameObject(objectName) ?? CreateChild(objectName, root);
            return target.GetComponent(type) ?? Undo.AddComponent(target, type);
        }

        private static T FindOrCreateComponent<T>(string objectName, GameObject root)
            where T : Component
        {
            T[] components = FusionSceneSetupValidation.FindSceneComponents<T>();
            if (components.Length > 0) return components[0];

            GameObject target = FindSceneGameObject(objectName) ?? CreateChild(objectName, root);
            return target.GetComponent<T>() ?? Undo.AddComponent<T>(target);
        }

        private static Component FindSceneComponent(Type type)
        {
#if UNITY_2023_1_OR_NEWER
            UnityEngine.Object[] objects = UnityEngine.Object.FindObjectsByType(
                type,
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);
#else
            UnityEngine.Object[] objects = UnityEngine.Object.FindObjectsOfType(type, true);
#endif
            return objects
                .OfType<Component>()
                .FirstOrDefault(component =>
                    component.gameObject.scene.IsValid() &&
                    component.gameObject.scene == SceneManager.GetActiveScene());
        }

        private static GameObject FindSceneGameObject(string name)
        {
#if UNITY_2023_1_OR_NEWER
            GameObject[] objects = UnityEngine.Object.FindObjectsByType<GameObject>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);
#else
            GameObject[] objects = Resources.FindObjectsOfTypeAll<GameObject>();
#endif
            return objects.FirstOrDefault(
                item =>
                    item != null &&
                    item.scene.IsValid() &&
                    item.scene == SceneManager.GetActiveScene() &&
                    item.name == name);
        }

        private static GameObject CreateChild(string name, GameObject parent)
        {
            var child = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(child, $"Create {name}");
            if (parent != null)
            {
                Undo.SetTransformParent(child.transform, parent.transform, $"Parent {name}");
            }
            return child;
        }

        private static List<Transform> EnsureSpawnPoints(Transform root)
        {
            Vector3[] positions =
            {
                new(-4f, 1f, -4f),
                new(4f, 1f, -4f),
                new(-4f, 1f, 4f),
                new(4f, 1f, 4f)
            };
            var result = new List<Transform>(positions.Length);

            for (int i = 0; i < positions.Length; i++)
            {
                string name = $"Spawn Point {i + 1}";
                Transform point = root.Find(name);
                if (point == null)
                {
                    GameObject child = CreateChild(name, root.gameObject);
                    point = child.transform;
                    point.localPosition = positions[i];
                }
                result.Add(point);
            }

            return result;
        }

        private static void EnsureEventSystem()
        {
            if (FusionSceneSetupValidation.FindSceneComponents<EventSystem>().Length > 0) return;

            var go = new GameObject("EventSystem");
            Undo.RegisterCreatedObjectUndo(go, "Create EventSystem");
            Undo.AddComponent<EventSystem>(go);

            Type inputSystemType = Type.GetType(
                "UnityEngine.InputSystem.UI.InputSystemUIInputModule, Unity.InputSystem");
            if (inputSystemType != null)
            {
                Undo.AddComponent(go, inputSystemType);
            }
            else
            {
                Undo.AddComponent<StandaloneInputModule>(go);
            }
        }

        private static bool EnsurePrefabComponent<T>(GameObject root, out T component)
            where T : Component
        {
            component = root.GetComponent<T>();
            if (component != null) return false;
            component = root.AddComponent<T>();
            return true;
        }

        private static void EnsurePrefabComponentByType(
            GameObject root,
            string typeName,
            string label,
            ICollection<string> changes)
        {
            Type type = Type.GetType(typeName);
            if (type == null || !typeof(Component).IsAssignableFrom(type)) return;
            if (root.GetComponent(type) != null) return;

            root.AddComponent(type);
            changes.Add(label);
        }

        private static bool HasComponentByType(GameObject root, string typeName)
        {
            Type type = Type.GetType(typeName);
            return type != null && root.GetComponent(type) != null;
        }

        private static void EnsureFusionPrefabLabel(GameObject prefab)
        {
            string[] labels = AssetDatabase.GetLabels(prefab);
            if (labels.Contains(NetworkProjectConfigImporter.FusionPrefabTag, StringComparer.Ordinal))
                return;

            AssetDatabase.SetLabels(
                prefab,
                labels.Concat(new[] { NetworkProjectConfigImporter.FusionPrefabTag })
                    .Distinct(StringComparer.Ordinal)
                    .ToArray());
            EditorUtility.SetDirty(prefab);
        }

        private static void EnsureAssetFolder(string path)
        {
            string[] parts = path.Split('/');
            string current = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                {
                    AssetDatabase.CreateFolder(current, parts[i]);
                }
                current = next;
            }
        }

        private bool HasDisableableTransportConflicts()
        {
            return FusionSceneSetupValidation.FindSceneComponents<MonoBehaviour>()
                .Any(IsCompetingSceneComponent);
        }

        private bool HasSelectedModulePreflightErrors(out string message)
        {
            var errors = new List<string>();
            bool canBootstrapInventoryPatch =
                m_ModuleInventory && CanBootstrapInventoryAuthorityPatch();
            if (string.IsNullOrWhiteSpace(m_RootName))
            {
                errors.Add("The Fusion Session scene root name cannot be empty.");
            }
            if (m_RunnerSetup == RunnerSetup.CreateOrReuseArawnBootstrap &&
                string.IsNullOrWhiteSpace(m_SessionName))
            {
                errors.Add("The Arawn Fusion bootstrap requires a non-empty session name.");
            }
            if (m_CreatePlayerSpawner && m_PlayerPrefab == null)
            {
                errors.Add(
                    "The authority-only player spawner requires a primary Player Prefab.");
            }
            RequireSelectedBridge(m_ModuleStats, FusionStatsBridgeType, "Stats", errors);
            RequireSelectedBridge(
                m_ModuleInventory,
                FusionInventoryBridgeType,
                "Inventory",
                errors,
                canBootstrapInventoryPatch);
            RequireSelectedBridge(m_ModuleMelee, FusionMeleeBridgeType, "Melee", errors);
            RequireSelectedBridge(m_ModuleShooter, FusionShooterBridgeType, "Shooter", errors);
            RequireSelectedBridge(m_ModuleQuests, FusionQuestsBridgeType, "Quests", errors);
            RequireSelectedBridge(m_ModuleDialogue, FusionDialogueBridgeType, "Dialogue", errors);
            RequireSelectedBridge(m_ModuleTraversal, FusionTraversalBridgeType, "Traversal", errors);
            RequireSelectedBridge(m_ModuleAbilities, FusionAbilitiesBridgeType, "Abilities", errors);

            if (m_CreateCharacterSelection)
            {
                foreach (GameObject prefab in GetConfiguredPlayerPrefabs())
                {
                    if (prefab == m_PlayerPrefab) continue;
                    FusionSetupReport prefabReport =
                        FusionSceneSetupValidation.ValidatePlayerPrefabOnly(
                            prefab,
                            m_PredictionBackend,
                            m_KccSharedAuthorityMode);
                    if (prefabReport.HasErrors)
                    {
                        errors.Add(
                            $"Character selection prefab '{prefab.name}' has blocking " +
                            "Fusion/Ninjutsu configuration errors.");
                    }
                }
            }

            if (m_ModuleInventory)
            {
                InventorySceneSetupTools.ValidationSummary inventory =
                    InventorySceneSetupTools.ValidateScene(SceneManager.GetActiveScene());
                if (inventory.HasErrors)
                {
                    errors.Add(inventory.ToReport());
                }

                const string runtimePickupAdapter =
                    "Arawn.GameCreator2.Networking.Inventory.Transport.Fusion." +
                    "FusionInventoryRuntimePickupIdentityAdapter, " +
                    "Arawn.GameCreator2.Networking.Inventory.Transport.Fusion";
                if (Type.GetType(runtimePickupAdapter) == null &&
                    !canBootstrapInventoryPatch)
                {
                    errors.Add(
                        "The authoritative Fusion runtime-pickup identity adapter is unavailable.");
                }
            }

            message = errors.Count == 0
                ? null
                : "Selected module preflight failed:\n• " + string.Join("\n• ", errors);
            return errors.Count != 0;
        }

        private static void RequireSelectedBridge(
            bool selected,
            string typeName,
            string moduleName,
            ICollection<string> errors,
            bool allowUnavailableUntilPatch = false)
        {
            if (!selected) return;

            Type type = Type.GetType(typeName);
            if (type == null)
            {
                if (allowUnavailableUntilPatch) return;
                errors.Add(
                    $"{moduleName} is selected, but its Fusion bridge assembly is unavailable.");
                return;
            }

#if UNITY_2023_1_OR_NEWER
            UnityEngine.Object[] objects = UnityEngine.Object.FindObjectsByType(
                type,
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);
#else
            UnityEngine.Object[] objects = UnityEngine.Object.FindObjectsOfType(type, true);
#endif
            int sceneCount = objects
                .OfType<Component>()
                .Count(component =>
                    component.gameObject.scene.IsValid() &&
                    component.gameObject.scene == SceneManager.GetActiveScene());
            if (sceneCount > 1)
            {
                errors.Add(
                    $"{moduleName} has {sceneCount} Fusion bridge registrations in the scene; " +
                    "keep exactly one.");
            }
        }

        private static bool CanBootstrapInventoryAuthorityPatch()
        {
            if (Type.GetType(FusionInventoryBridgeType) != null) return false;
            var patcher = new InventoryPatcher();
            return patcher.ValidateFilesExist() && !patcher.IsPatched();
        }

        private bool TryGetRunnerSetupConflict(out string message)
        {
            message = null;
            NetworkRunner[] runners =
                FusionSceneSetupValidation.FindSceneComponents<NetworkRunner>();
            FusionSessionBootstrap[] bootstraps =
                FusionSceneSetupValidation.FindSceneComponents<FusionSessionBootstrap>();
            bool hasPhotonBootstrap =
                FusionSceneSetupValidation.FindSceneComponents<MonoBehaviour>()
                    .Any(behaviour =>
                        behaviour != null &&
                        behaviour.isActiveAndEnabled &&
                        behaviour.GetType().FullName == "Fusion.FusionBootstrap");

            if (m_RunnerSetup == RunnerSetup.CreateOrReuseArawnBootstrap)
            {
                if (runners.Length > 0 || hasPhotonBootstrap)
                {
                    message =
                        "A runner or Photon FusionBootstrap already owns the session. Select " +
                        "Existing Or Manual, or disable/remove that owner before creating the " +
                        "Arawn bootstrap.";
                    return true;
                }

                return false;
            }

            if (bootstraps.Length > 0)
            {
                message =
                    "Existing Or Manual runner mode cannot coexist with an Arawn " +
                    "FusionSessionBootstrap. Select the Arawn mode or disable/remove it.";
                return true;
            }

            if (runners.Length == 0 && !hasPhotonBootstrap)
            {
                message =
                    "Existing Or Manual runner mode needs one NetworkRunner or one active " +
                    "Photon FusionBootstrap in the scene.";
                return true;
            }

            return false;
        }

        private bool IsCompetingSceneComponent(MonoBehaviour behaviour)
        {
            if (behaviour == null || !behaviour.isActiveAndEnabled) return false;
            Type type = behaviour.GetType();
            string typeNamespace = type.Namespace ?? string.Empty;
            if (typeNamespace.StartsWith(
                    "NinjutsuGames.FusionNetwork",
                    StringComparison.Ordinal))
            {
                return true;
            }

            if (typeNamespace.Contains(".Transport.PurrNet", StringComparison.Ordinal) &&
                type.Name.EndsWith("TransportBridge", StringComparison.Ordinal))
            {
                return true;
            }

            bool isSelectedAdvancedKcc =
                m_PredictionBackend == NetworkPredictionBackend.FusionKCC &&
                IsKnownAdvancedKccIntegrationComponent(behaviour);
            if (isSelectedAdvancedKcc) return false;

            // An adopted customer KCC setup is parked, not removed, when the prefab is
            // converted to another backend. Its processors may be referenced as sibling
            // components or provider GameObjects, so they are not necessarily descendants of
            // the marked motor. Preserve those exact recorded references instead of disabling
            // them as competing scene movement components.
            if (IsParkedCustomerAdvancedKccComponent(behaviour)) return false;

            if (behaviour is NetworkTRSP &&
                behaviour is not FusionNativeNetworkCharacterMotor &&
                behaviour.GetComponentInParent<NetworkCharacter>(true) != null)
            {
                return true;
            }

            bool isFusionMovement =
                typeNamespace.StartsWith("Fusion", StringComparison.Ordinal) &&
                (type.Name.Contains("NetworkTransform", StringComparison.Ordinal) ||
                 type.Name.Contains("KCC", StringComparison.OrdinalIgnoreCase) ||
                 type.Name.Contains("NetworkRigidbody", StringComparison.Ordinal) ||
                 type.Name.Contains("NetworkCharacterController", StringComparison.Ordinal) ||
                 type.Name.Contains("NetworkTRSP", StringComparison.Ordinal) ||
                 type.Name.Contains("NetworkMecanimAnimator", StringComparison.Ordinal));
            return isFusionMovement &&
                   behaviour.GetComponentInParent<NetworkCharacter>(true) != null;
        }

        private static bool IsParkedCustomerAdvancedKccComponent(Component component)
        {
            if (component == null) return false;
            string typeNamespace = component.GetType().Namespace ?? string.Empty;
            if (!typeNamespace.StartsWith("Fusion.Addons.KCC", StringComparison.Ordinal) &&
                !typeNamespace.StartsWith(
                    "Arawn.GameCreator2.Networking.Transport.Fusion.KCC",
                    StringComparison.Ordinal))
            {
                return false;
            }

            NetworkCharacter networkCharacter =
                component.GetComponentInParent<NetworkCharacter>(true);
            if (networkCharacter == null) return false;

            Transform current = component.transform;
            while (current != null)
            {
                FusionKccSetupMarker marker =
                    current.GetComponent<FusionKccSetupMarker>();
                if (marker != null && marker.AdoptedSetupParked) return true;
                if (current.gameObject == networkCharacter.gameObject) break;
                current = current.parent;
            }

            foreach (FusionKccSetupMarker marker in
                     networkCharacter.GetComponentsInChildren<FusionKccSetupMarker>(true))
            {
                if (marker == null || !marker.AdoptedSetupParked) continue;
                foreach (UnityEngine.Object processorObject in
                         marker.OriginalKccProcessorObjects ??
                         Array.Empty<UnityEngine.Object>())
                {
                    if (processorObject == component ||
                        processorObject == component.gameObject)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool IsKnownAdvancedKccIntegrationComponent(Component component)
        {
            if (component == null) return false;
            Type type = component.GetType();
            string typeNamespace = type.Namespace ?? string.Empty;
            if (typeNamespace.StartsWith(
                    "Arawn.GameCreator2.Networking.Transport.Fusion.KCC",
                    StringComparison.Ordinal))
            {
                return true;
            }
            if (!typeNamespace.StartsWith("Fusion.Addons.KCC", StringComparison.Ordinal))
                return false;

            NetworkCharacter networkCharacter =
                component.GetComponentInParent<NetworkCharacter>(true);
            if (networkCharacter != null)
            {
                Component[] customerKccMotors = networkCharacter
                    .GetComponentsInChildren<Component>(true)
                    .Where(candidate =>
                        candidate != null &&
                        string.Equals(
                            candidate.GetType().FullName,
                            "Fusion.Addons.KCC.KCC",
                            StringComparison.Ordinal))
                    .ToArray();
                if (customerKccMotors.Length == 1 &&
                    customerKccMotors[0].transform.parent == networkCharacter.transform)
                {
                    return true;
                }
            }

            Transform current = component.transform;
            while (current != null)
            {
                foreach (Component sibling in current.GetComponents<Component>())
                {
                    if (sibling == null) continue;
                    string siblingNamespace = sibling.GetType().Namespace ?? string.Empty;
                    if (siblingNamespace.StartsWith(
                            "Arawn.GameCreator2.Networking.Transport.Fusion.KCC",
                            StringComparison.Ordinal))
                    {
                        return true;
                    }
                }

                if (networkCharacter != null &&
                    current.gameObject == networkCharacter.gameObject)
                {
                    break;
                }
                current = current.parent;
            }

            return false;
        }

        private void DisableConflictingSceneComponents()
        {
            foreach (MonoBehaviour behaviour in
                     FusionSceneSetupValidation.FindSceneComponents<MonoBehaviour>()
                         .Where(IsCompetingSceneComponent))
            {
                Undo.RecordObject(behaviour, "Disable Competing Network Transport");
                behaviour.enabled = false;
                EditorUtility.SetDirty(behaviour);
            }

            EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        }

        private static bool DrawModuleToggle(string label, string typeName, bool value)
        {
            bool installed = Type.GetType(typeName) != null;
            using (new EditorGUI.DisabledScope(!installed))
            {
                value = EditorGUILayout.ToggleLeft(
                    installed ? label : $"{label} (not installed)",
                    installed && value);
            }
            return installed && value;
        }

        private static bool DrawInventoryModuleToggle(bool value)
        {
            bool compiled = Type.GetType(NetworkInventoryManagerType) != null;
            var patcher = new InventoryPatcher();
            bool patchSourcesAvailable = patcher.ValidateFilesExist();
            bool installed = compiled || patchSourcesAvailable;
            using (new EditorGUI.DisabledScope(!installed))
            {
                string suffix = compiled
                    ? string.Empty
                    : patchSourcesAvailable
                        ? " (authority patch required)"
                        : " (not installed)";
                value = EditorGUILayout.ToggleLeft(
                    "Inventory" + suffix,
                    installed && value);
            }
            return installed && value;
        }

        private void DrawPatchStatus()
        {
            EditorGUILayout.Space(8);
            if (m_ModuleInventory && !GC2NetworkingDefineSymbols.IsInventoryAuthorityPatchApplied())
            {
                EditorGUILayout.HelpBox(
                    "Inventory authority patch will be required before Apply.",
                    MessageType.Warning);
            }
            if (m_ModuleMelee && !GC2NetworkingDefineSymbols.IsMeleeAuthorityPatchApplied())
            {
                EditorGUILayout.HelpBox(
                    "Melee authority patch will be required before Apply.",
                    MessageType.Warning);
            }
            if (m_ModuleTraversal && !GC2NetworkingDefineSymbols.IsTraversalAuthorityPatchApplied())
            {
                EditorGUILayout.HelpBox(
                    "Traversal authority patch will be required before Apply.",
                    MessageType.Warning);
            }
            if (m_ModuleShooter && !ShooterSightPatchRequirement.IsApplied())
            {
                EditorGUILayout.HelpBox(
                    ShooterSightPatchRequirement.GetStatusText(),
                    MessageType.Warning);
            }
        }

        private void DrawCharacterSelectionPrefabs()
        {
            EditorGUI.indentLevel++;
            EditorGUILayout.LabelField("Selectable Player Prefabs", EditorStyles.miniBoldLabel);
            for (int i = 0; i < m_CharacterSelectionPrefabs.Count; i++)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    m_CharacterSelectionPrefabs[i] =
                        (GameObject)EditorGUILayout.ObjectField(
                            $"Character {i + 1}",
                            m_CharacterSelectionPrefabs[i],
                            typeof(GameObject),
                            false);
                    if (GUILayout.Button("Remove", GUILayout.Width(68f)))
                    {
                        m_CharacterSelectionPrefabs.RemoveAt(i);
                        i--;
                    }
                }
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Add Character Prefab"))
                {
                    m_CharacterSelectionPrefabs.Add(null);
                }

                using (new EditorGUI.DisabledScope(
                           m_PlayerPrefab == null ||
                           m_CharacterSelectionPrefabs.Contains(m_PlayerPrefab)))
                {
                    if (GUILayout.Button("Add Primary Player"))
                    {
                        m_CharacterSelectionPrefabs.Add(m_PlayerPrefab);
                    }
                }
            }

            EditorGUILayout.HelpBox(
                "Every selected prefab is prepared as a persistent Master Client Object. " +
                "Clients submit only an index; the Host/Shared Master validates and spawns it.",
                MessageType.None);
            EditorGUI.indentLevel--;
        }

        private List<GameObject> GetConfiguredPlayerPrefabs()
        {
            var result = new List<GameObject>();
            if (m_PlayerPrefab != null) result.Add(m_PlayerPrefab);
            if (m_CreateCharacterSelection)
            {
                foreach (GameObject prefab in m_CharacterSelectionPrefabs)
                {
                    if (prefab != null && !result.Contains(prefab)) result.Add(prefab);
                }
            }

            return result;
        }

        private void ApplyTemplate(ProjectTemplate template)
        {
            m_ModuleStats = false;
            m_ModuleInventory = false;
            m_ModuleMelee = false;
            m_ModuleShooter = false;
            m_ModuleQuests = false;
            m_ModuleDialogue = false;
            m_ModuleTraversal = false;
            m_ModuleAbilities = false;

            switch (template)
            {
                case ProjectTemplate.ShooterGame:
                    m_ModuleStats = true;
                    m_ModuleInventory = true;
                    m_ModuleShooter = true;
                    m_SessionPreset = NetworkSessionPreset.Duel;
                    break;
                case ProjectTemplate.MeleeActionRPG:
                    m_ModuleStats = true;
                    m_ModuleInventory = true;
                    m_ModuleMelee = true;
                    m_ModuleQuests = true;
                    m_ModuleDialogue = true;
                    m_SessionPreset = NetworkSessionPreset.Standard;
                    break;
                case ProjectTemplate.CoopAdventure:
                    m_ModuleStats = true;
                    m_ModuleInventory = true;
                    m_ModuleMelee = true;
                    m_ModuleShooter = true;
                    m_ModuleQuests = true;
                    m_ModuleDialogue = true;
                    m_ModuleTraversal = true;
                    m_SessionPreset = NetworkSessionPreset.Standard;
                    break;
                case ProjectTemplate.MMORPG:
                    m_ModuleStats = true;
                    m_ModuleInventory = true;
                    m_ModuleMelee = true;
                    m_ModuleShooter = true;
                    m_ModuleQuests = true;
                    m_ModuleDialogue = true;
                    m_ModuleTraversal = true;
                    m_ModuleAbilities = true;
                    m_SessionPreset = NetworkSessionPreset.Massive;
                    break;
                case ProjectTemplate.FullIntegrationSandbox:
                    m_ModuleStats = true;
                    m_ModuleInventory = true;
                    m_ModuleMelee = true;
                    m_ModuleShooter = true;
                    m_ModuleQuests = true;
                    m_ModuleDialogue = true;
                    m_ModuleTraversal = true;
                    m_ModuleAbilities = true;
                    m_SessionPreset = NetworkSessionPreset.Standard;
                    break;
            }
        }

        private string BuildSummary()
        {
            return
                $"Root: {m_RootName}\n" +
                $"Template: {m_Template}\n" +
                $"Runner: {m_RunnerSetup}\n" +
                $"Default demo mode: {m_DefaultLaunchMode}\n" +
                $"Session: {m_SessionName}\n" +
                $"Region: {(string.IsNullOrWhiteSpace(m_Region) ? "Best Region" : m_Region.Trim())}\n" +
                $"Force Photon Relay (Host/Client): {YesNo(m_ForcePhotonRelay)}\n" +
                $"Expected players: {m_ExpectedPlayers}\n" +
                $"Tick rate: {m_TickRate} Hz\n" +
                $"Prediction backend: {m_PredictionBackend}\n" +
                (m_PredictionBackend == NetworkPredictionBackend.FusionKCC
                    ? $"KCC Shared authority: {m_KccSharedAuthorityMode}\n"
                    : string.Empty) +
                $"Session profile: {m_SessionPreset}\n" +
                $"Generated profile path: {SessionProfilePath}\n" +
                $"Scene manager ownership: {(m_CreateSceneManager ? "Wizard/default manager" : "External owner")}\n" +
                $"Player prefab: {(m_PlayerPrefab != null ? m_PlayerPrefab.name : "None")}\n" +
                $"Prepare prefab + authority flags: {YesNo(m_ConfigurePlayerPrefab)}\n" +
                $"Network-ready Character kernel: {YesNo(m_ConfigurePlayerKernel)}\n" +
                $"Local Variables controller: {YesNo(m_PlayerUsesLocalVariables)}\n" +
                $"Modules: Core, Variables, Animation/Motion" +
                $"{SelectedModulesSummary()}\n" +
                $"Append installed example registrations: {YesNo(m_RegisterInstalledDemoAssets)}\n" +
                "Core managers/bridges + security: Yes\n" +
                "Project config: PeerMode.Single, required reliable modes, weave list, " +
                $"prefab table, {m_TickRate} Hz\n" +
                "Mono player compatibility: Managed Stripping Level Disabled\n" +
                $"Player spawner: {YesNo(m_CreatePlayerSpawner)}\n" +
                $"Selectable player prefabs: {GetConfiguredPlayerPrefabs().Count}\n" +
                $"Character selection: {YesNo(m_CreateCharacterSelection)}\n" +
                $"Demo UI: {YesNo(m_CreateDemoUI)}\n" +
                $"Photon Lobby UI: {YesNo(m_CreateLobbyUI)}\n" +
                $"Controls UI: {YesNo(m_CreateControlsUI)}\n" +
                $"Chat UI: {YesNo(m_CreateChatUI)}\n" +
                $"Inventory pickup conversion: {YesNo(m_ModuleInventory)}";
        }

        private string SelectedModulesSummary()
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
            return modules.Count == 0 ? string.Empty : ", " + string.Join(", ", modules);
        }

        private static string YesNo(bool value) => value ? "Yes" : "No";

        private static string PageTitle(WizardPage page)
        {
            return page switch
            {
                WizardPage.ProjectShape => "Project Shape",
                WizardPage.Modules => "GC2 Modules",
                WizardPage.FusionSession => "Fusion Session",
                WizardPage.Infrastructure => "Core Infrastructure",
                WizardPage.SpawningAndUI => "Spawning and UI",
                _ => "Review"
            };
        }

        private static string PageHelp(WizardPage page)
        {
            return page switch
            {
                WizardPage.ProjectShape =>
                    "Choose project scale and GC2 networking defaults.",
                WizardPage.Modules =>
                    "Select installed GC2 modules that receive Fusion bridges.",
                WizardPage.FusionSession =>
                    "Configure the direct Photon runner for Host and Shared sessions.",
                WizardPage.Infrastructure =>
                    "Choose managers, bridges, and session-profile assets.",
                WizardPage.SpawningAndUI =>
                    "Prepare player prefabs and optional demo helpers.",
                _ =>
                    "Review all changes and resolve conflicts before applying."
            };
        }

        private static void SetObject(
            SerializedObject serialized,
            string propertyName,
            UnityEngine.Object value)
        {
            SerializedProperty property = serialized?.FindProperty(propertyName);
            if (property != null &&
                property.propertyType == SerializedPropertyType.ObjectReference)
            {
                property.objectReferenceValue = value;
            }
        }

        private static void SetString(
            SerializedObject serialized,
            string propertyName,
            string value)
        {
            SerializedProperty property = serialized?.FindProperty(propertyName);
            if (property != null && property.propertyType == SerializedPropertyType.String)
            {
                property.stringValue = value ?? string.Empty;
            }
        }

        private static void SetInt(
            SerializedObject serialized,
            string propertyName,
            int value)
        {
            SerializedProperty property = serialized?.FindProperty(propertyName);
            if (property != null && property.propertyType == SerializedPropertyType.Integer)
            {
                property.intValue = value;
            }
        }

        private static bool SetBool(
            SerializedObject serialized,
            string propertyName,
            bool value)
        {
            SerializedProperty property = serialized?.FindProperty(propertyName);
            if (property == null || property.propertyType != SerializedPropertyType.Boolean)
                return false;
            if (property.boolValue == value) return false;
            property.boolValue = value;
            return true;
        }

        private static bool SetEnum(
            SerializedObject serialized,
            string propertyName,
            int value)
        {
            SerializedProperty property = serialized?.FindProperty(propertyName);
            if (property == null || property.propertyType != SerializedPropertyType.Enum)
                return false;
            if (property.enumValueIndex == value) return false;
            property.enumValueIndex = value;
            return true;
        }

        private static void SetTransformArray(
            SerializedObject serialized,
            string propertyName,
            IReadOnlyList<Transform> values)
        {
            SerializedProperty property = serialized?.FindProperty(propertyName);
            if (property == null || !property.isArray) return;

            property.arraySize = values.Count;
            for (int i = 0; i < values.Count; i++)
            {
                property.GetArrayElementAtIndex(i).objectReferenceValue = values[i];
            }
        }

        private static void SetObjectArray(
            SerializedObject serialized,
            string propertyName,
            IReadOnlyList<UnityEngine.Object> values)
        {
            SerializedProperty property = serialized?.FindProperty(propertyName);
            if (property == null || !property.isArray) return;

            property.arraySize = values.Count;
            for (int i = 0; i < values.Count; i++)
            {
                property.GetArrayElementAtIndex(i).objectReferenceValue = values[i];
            }
        }

    }
}
