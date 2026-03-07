#if UNITY_EDITOR
using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Common;
using GameCreator.Editor.Characters;
using Arawn.GameCreator2.Networking.Security;

#if UNITY_NETCODE
using Unity.Netcode;
using Unity.Netcode.Transports.SinglePlayer;
using Unity.Netcode.Transports.UTP;
#endif

namespace Arawn.GameCreator2.Networking.Editor
{
    /// <summary>
    /// Scene setup wizard for GC2 networking layer.
    /// Creates practical defaults without enforcing a rigid pipeline.
    /// </summary>
    public class GameCreator2NetworkingSetupWizard : EditorWindow
    {
        private enum SetupStep
        {
            Scenario = 0,
            Assets = 1,
            Scene = 2,
            Player = 3,
            Output = 4
        }

        private enum TargetGameType
        {
            CompetitiveDuel,
            TeamCompetitive,
            CoopPve,
            LargeScaleBattle,
            SandboxPrototype
        }

#if UNITY_NETCODE
        private enum NetcodeTransportMode
        {
            UnityTransport,
            SinglePlayerTransport
        }
#endif

        private const string DEFAULT_OUTPUT_FOLDER = "Assets/GameCreator2Networking";
        private const string FOOTSTEPS_PATH = RuntimePaths.CHARACTERS + "Assets/3D/Footsteps.asset";
        private const string RTC_PATH = RuntimePaths.CHARACTERS + "Assets/Controllers/CompleteLocomotion.controller";

        private TargetGameType m_TargetGameType = TargetGameType.TeamCompetitive;
        private int m_MaxPlayers = 16;

        private bool m_CreateSessionProfile = true;
        private bool m_CreateOffMeshLinkRegistry = true;
        private bool m_CreateAnimationRegistry = false;

        private bool m_CreateNetworkManager = true;
        private bool m_CreateTransportBridge = true;
        private bool m_CreateSecurityManager = true;
        private bool m_AssignAssetsToSceneComponents = true;

        private bool m_CreateNetworkPlayer = true;
        private bool m_AssignProfileOverrideToCreatedPlayer = false;
        private bool m_HostOwnerUsesClientPrediction = false;
        private bool m_UseNetworkDriverUnit = true;
        private bool m_UseNetworkFacingUnit = true;
        private bool m_UseNetworkAnimimUnit = true;
        private bool m_ParentUnderSetupRoot = true;
        private bool m_CreateTestInfrastructure = false;
        private string m_SetupRootName = "GC2 Network Setup";

        private string m_OutputFolderPath = DEFAULT_OUTPUT_FOLDER;
        private Vector2 m_Scroll;
        private SetupStep m_CurrentStep = SetupStep.Scenario;

        private const int TOTAL_STEPS = 5;

#if UNITY_NETCODE
        private NetcodeTransportMode m_TransportMode = NetcodeTransportMode.UnityTransport;
#endif

        private static readonly GUIContent GUI_GAME_TYPE = new GUIContent(
            "Game Type",
            "High-level project style used to pick default networking posture and profile recommendation.\n\n" +
            "Competitive Duel: 1v1/very small competitive fights (ex: fighting-game duel flow, CS2 Wingman-style intensity).\n" +
            "Team Competitive: structured team PvP (ex: Valorant/CS2 5v5 style).\n" +
            "Coop PvE: players vs AI encounter focus (ex: Left 4 Dead 2 / Deep Rock style sessions).\n" +
            "Large Scale Battle: many simultaneous players and heavy relevance filtering (ex: Battlefield/Planetside style scale).\n" +
            "Sandbox Prototype: experimentation phase with mixed requirements."
        );

        private static readonly GUIContent GUI_MAX_PLAYERS = new GUIContent(
            "Max Players",
            "Expected peak players per session. Higher values bias recommendations toward lower far-tier rates and reduced expensive sync."
        );

        private static readonly GUIContent GUI_CREATE_PROFILE = new GUIContent(
            "Create / Configure Network Session Profile",
            "Generates (or updates) a Session Profile asset and applies recommended preset/tuning for selected game type and player count."
        );

        private static readonly GUIContent GUI_CREATE_OFFMESH = new GUIContent(
            "Create Off-Mesh Link Registry",
            "Creates a default Off-Mesh traversal registry used by OffMeshLink network controllers."
        );

        private static readonly GUIContent GUI_CREATE_ANIM_REG = new GUIContent(
            "Create Animation Registry",
            "Creates a Network Animation Registry asset for stable animation ID mapping when you use animation command sync."
        );

        private static readonly GUIContent GUI_CREATE_NET_MANAGER = new GUIContent(
            "Ensure NetworkManager + Selected Transport",
            "Creates/reuses scene NetworkManager and ensures the selected transport exists and is assigned in NetworkConfig."
        );

        private static readonly GUIContent GUI_CREATE_BRIDGE = new GUIContent(
            "Ensure Netcode Transport Bridge",
            "Creates/reuses NetcodeGameObjectsTransportBridge and (if profile exists) assigns Global Session Profile."
        );

        private static readonly GUIContent GUI_CREATE_SECURITY_MANAGER = new GUIContent(
            "Ensure Network Security Manager",
            "Creates/reuses NetworkSecurityManager so module security validation and rate limiting can initialize safely in server mode."
        );

        private static readonly GUIContent GUI_TRANSPORT_MODE = new GUIContent(
            "Transport Mode",
            "Select which NGO transport to create/assign on the NetworkManager.\n\n" +
            "UnityTransport: real network sockets (LAN/internet multiplayer).\n" +
            "SinglePlayerTransport: local host-only transport for offline testing with no remote clients."
        );

        private static readonly GUIContent GUI_ASSIGN_SCENE_ASSETS = new GUIContent(
            "Assign created registry assets to matching scene components",
            "Finds compatible scene components and binds created Off-Mesh/Animation registries automatically."
        );

        private static readonly GUIContent GUI_PARENT_ROOT = new GUIContent(
            "Parent newly created objects under a setup root",
            "Keeps generated objects organized under one root for scene hygiene."
        );

        private static readonly GUIContent GUI_SETUP_ROOT_NAME = new GUIContent(
            "Setup Root Name",
            "Name of the parent object used when grouping generated setup objects."
        );

        private static readonly GUIContent GUI_CREATE_TEST_INFRASTRUCTURE = new GUIContent(
            "Create Test Infrastructure (NetworkTestRunner)",
            "Adds a NetworkTestRunner helper to the scene that exercises security validation, " +
            "sequence tracking, and relevance-tier logic at runtime for debugging and QA."
        );

        private static readonly GUIContent GUI_CREATE_PLAYER = new GUIContent(
            "Create / Ensure GC2 Network Player object",
            "Creates (or reuses) a player template object with Character + NetworkCharacter and network-ready kernel defaults."
        );

        private static readonly GUIContent GUI_ASSIGN_PROFILE_OVERRIDE = new GUIContent(
            "Assign session profile override on created player",
            "Writes the generated Session Profile into the player's per-character override field."
        );

        private static readonly GUIContent GUI_HOST_CLIENT_PREDICTION = new GUIContent(
            "Enable host-owner client prediction (fairness default is OFF)",
            "When ON, host-owned character uses LocalClient prediction role. When OFF, host-owned character stays server-authoritative for stricter fairness."
        );

        private static readonly GUIContent GUI_USE_NETWORK_DRIVER_UNIT = new GUIContent(
            "Use UnitDriverNetworkClient as authored kernel Driver",
            "Sets Character kernel driver to UnitDriverNetworkClient on created/reused player template."
        );

        private static readonly GUIContent GUI_USE_NETWORK_FACING_UNIT = new GUIContent(
            "Use UnitFacingNetworkPivot as authored Facing unit",
            "Sets Character kernel facing to UnitFacingNetworkPivot for server-authoritative facing synchronization."
        );

        private static readonly GUIContent GUI_USE_NETWORK_ANIMIM_UNIT = new GUIContent(
            "Use UnitAnimimNetworkKinematic as authored Animim unit",
            "Sets Character kernel animim to UnitAnimimNetworkKinematic for server-authoritative locomotion parameter sync."
        );

        private static readonly GUIContent GUI_OUTPUT_FOLDER = new GUIContent(
            "Asset Folder",
            "Project-relative folder under Assets where generated registry/profile assets will be stored."
        );

        [MenuItem("Tools/Game Creator 2/Networking/Setup Wizard", priority = 0)]
        public static void OpenWizard()
        {
            var window = GetWindow<GameCreator2NetworkingSetupWizard>(true, "GC2 Networking Setup Wizard");
            window.minSize = new Vector2(720f, 760f);
            window.maxSize = new Vector2(960f, 1000f);
            window.Show();
        }

        private void OnGUI()
        {
            m_Scroll = EditorGUILayout.BeginScrollView(m_Scroll);

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Game Creator 2 Networking Setup", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Creates scene-level networking scaffolding and optional assets for the GC2 networking layer. " +
                "You can run this multiple times; existing objects/assets are reused when possible.",
                MessageType.Info
            );

            DrawStepHeader();
            DrawCurrentStepSection();

            EditorGUILayout.EndScrollView();
            DrawNavigationButtons();
        }

        private void DrawStepHeader()
        {
            int stepNumber = (int)m_CurrentStep + 1;
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField(
                $"Step {stepNumber}/{TOTAL_STEPS}: {GetStepTitle(m_CurrentStep)}",
                EditorStyles.boldLabel
            );
        }

        private void DrawCurrentStepSection()
        {
            switch (m_CurrentStep)
            {
                case SetupStep.Scenario:
                    DrawScenarioSection();
                    break;
                case SetupStep.Assets:
                    DrawAssetSection();
                    break;
                case SetupStep.Scene:
                    DrawSceneSection();
                    break;
                case SetupStep.Player:
                    DrawPlayerSection();
                    break;
                case SetupStep.Output:
                    DrawOutputSection();
                    DrawRecommendationSection();
                    break;
                default:
                    DrawScenarioSection();
                    break;
            }
        }

        private void DrawNavigationButtons()
        {
            EditorGUILayout.Space(8);
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(m_CurrentStep == SetupStep.Scenario))
                {
                    if (GUILayout.Button("Back", GUILayout.Height(30), GUILayout.Width(110)))
                    {
                        m_CurrentStep = (SetupStep)Mathf.Max((int)SetupStep.Scenario, (int)m_CurrentStep - 1);
                        m_Scroll = Vector2.zero;
                        GUI.FocusControl(null);
                    }
                }

                GUILayout.FlexibleSpace();

                if (m_CurrentStep != SetupStep.Output)
                {
                    if (GUILayout.Button("Next", GUILayout.Height(30), GUILayout.Width(110)))
                    {
                        m_CurrentStep = (SetupStep)Mathf.Min((int)SetupStep.Output, (int)m_CurrentStep + 1);
                        m_Scroll = Vector2.zero;
                        GUI.FocusControl(null);
                    }
                }
                else
                {
                    if (GUILayout.Button("Create / Update Scene Setup", GUILayout.Height(36), GUILayout.Width(280)))
                    {
                        RunSetup();
                    }
                }
            }
            EditorGUILayout.Space(8);
        }

        private static string GetStepTitle(SetupStep step)
        {
            switch (step)
            {
                case SetupStep.Scenario: return "Scenario";
                case SetupStep.Assets: return "Assets to Generate";
                case SetupStep.Scene: return "Scene Objects";
                case SetupStep.Player: return "Optional Player Template";
                case SetupStep.Output: return "Output and Review";
                default: return "Scenario";
            }
        }

        private void DrawScenarioSection()
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("1) Scenario", EditorStyles.boldLabel);

            m_TargetGameType = (TargetGameType)EditorGUILayout.EnumPopup(GUI_GAME_TYPE, m_TargetGameType);
            m_MaxPlayers = EditorGUILayout.IntSlider(GUI_MAX_PLAYERS, Mathf.Max(2, m_MaxPlayers), 2, 128);

            EditorGUILayout.HelpBox(GetGameTypeContext(m_TargetGameType), MessageType.None);
        }

        private void DrawAssetSection()
        {
            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("2) Assets to Generate", EditorStyles.boldLabel);

            m_CreateSessionProfile = DrawToggleWithGuidance(
                GUI_CREATE_PROFILE,
                m_CreateSessionProfile,
                "You want reproducible, team-shared tuning and scale-aware defaults (recommended in most projects).",
                "You already maintain mature profile assets manually and do not want this wizard to touch them."
            );

            m_CreateOffMeshLinkRegistry = DrawToggleWithGuidance(
                GUI_CREATE_OFFMESH,
                m_CreateOffMeshLinkRegistry,
                "Your characters use NavMesh off-mesh traversal and you want explicit traversal type defaults.",
                "Your game never uses off-mesh links, or your project already has a curated registry you do not want replaced."
            );

            m_CreateAnimationRegistry = DrawToggleWithGuidance(
                GUI_CREATE_ANIM_REG,
                m_CreateAnimationRegistry,
                "You rely on networked animation commands and want deterministic ID mapping for clips/states.",
                "Animation sync is minimal/cosmetic-only or you already have a hand-managed animation registry."
            );
        }

        private void DrawSceneSection()
        {
            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("3) Scene Objects", EditorStyles.boldLabel);

#if UNITY_NETCODE
            m_CreateNetworkManager = DrawToggleWithGuidance(
                GUI_CREATE_NET_MANAGER,
                m_CreateNetworkManager,
                "This scene should be playable as an NGO session and you want a valid transport explicitly assigned.",
                "Another bootstrap scene or framework owns NetworkManager lifecycle and this scene must stay manager-free."
            );

            using (new EditorGUI.DisabledScope(!m_CreateNetworkManager))
            {
                m_TransportMode = (NetcodeTransportMode)EditorGUILayout.EnumPopup(GUI_TRANSPORT_MODE, m_TransportMode);
                EditorGUILayout.HelpBox(GetTransportModeContext(m_TransportMode), MessageType.Info);
            }

            m_CreateTransportBridge = DrawToggleWithGuidance(
                GUI_CREATE_BRIDGE,
                m_CreateTransportBridge,
                "You use NGO and want movement transport wired through the bridge abstraction (recommended).",
                "You are testing a different custom transport bridge and do not want an NGO bridge instance in-scene."
            );
#else
            EditorGUILayout.HelpBox(
                "UNITY_NETCODE is not enabled. Netcode-specific scene setup options are hidden.",
                MessageType.Warning
            );
#endif

            m_CreateSecurityManager = DrawToggleWithGuidance(
                GUI_CREATE_SECURITY_MANAGER,
                m_CreateSecurityManager,
                "You want server-side security validation, rate limiting, and violation tracking available by default.",
                "Your project creates and configures NetworkSecurityManager through a custom bootstrap pipeline."
            );

            m_AssignAssetsToSceneComponents = DrawToggleWithGuidance(
                GUI_ASSIGN_SCENE_ASSETS,
                m_AssignAssetsToSceneComponents,
                "You want immediate wiring and fewer missed references after generation.",
                "You intentionally manage references manually per prefab/scene and want zero automatic rebinding."
            );

            m_ParentUnderSetupRoot = DrawToggleWithGuidance(
                GUI_PARENT_ROOT,
                m_ParentUnderSetupRoot,
                "You want clean hierarchy organization and easy cleanup/migration of generated objects.",
                "Your studio has strict scene hierarchy conventions that do not allow generated parent roots."
            );

            using (new EditorGUI.DisabledScope(!m_ParentUnderSetupRoot))
            {
                m_SetupRootName = EditorGUILayout.TextField(GUI_SETUP_ROOT_NAME, m_SetupRootName);
            }

            EditorGUILayout.Space(6);
            m_CreateTestInfrastructure = DrawToggleWithGuidance(
                GUI_CREATE_TEST_INFRASTRUCTURE,
                m_CreateTestInfrastructure,
                "You want a runtime test helper in the scene to validate security, sequence tracking, and relevance-tier logic during QA.",
                "This is a production build or you already have your own test harness."
            );
        }

        private void DrawPlayerSection()
        {
            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("4) Optional Player Template", EditorStyles.boldLabel);

            m_CreateNetworkPlayer = DrawToggleWithGuidance(
                GUI_CREATE_PLAYER,
                m_CreateNetworkPlayer,
                "You want a ready-to-test in-scene player template with network defaults.",
                "Your production player is prefab-driven and must be created by your own spawn pipeline only."
            );

            using (new EditorGUI.DisabledScope(!m_CreateNetworkPlayer))
            {
                m_AssignProfileOverrideToCreatedPlayer = DrawToggleWithGuidance(
                    GUI_ASSIGN_PROFILE_OVERRIDE,
                    m_AssignProfileOverrideToCreatedPlayer,
                    "This player needs per-character tuning that should be pinned to a specific profile asset.",
                    "You prefer global bridge profile control to avoid per-character override drift."
                );
            }

            m_HostOwnerUsesClientPrediction = DrawToggleWithGuidance(
                GUI_HOST_CLIENT_PREDICTION,
                m_HostOwnerUsesClientPrediction,
                "You prioritize host input feel in friend-hosted sessions and accept some fairness tradeoff.",
                "You need strict competitive fairness where host should not get a prediction/latency advantage."
            );

            m_UseNetworkDriverUnit = DrawToggleWithGuidance(
                GUI_USE_NETWORK_DRIVER_UNIT,
                m_UseNetworkDriverUnit,
                "You want the template to visibly use network driver units in authoring and prefab defaults.",
                "You deliberately keep a non-network authored driver and rely only on runtime swapping."
            );

            m_UseNetworkFacingUnit = DrawToggleWithGuidance(
                GUI_USE_NETWORK_FACING_UNIT,
                m_UseNetworkFacingUnit,
                "Facing direction affects gameplay (backstabs, cone attacks, aim authority) and should be server-authoritative.",
                "Facing is purely cosmetic and you want to minimize complexity/cost."
            );

            m_UseNetworkAnimimUnit = DrawToggleWithGuidance(
                GUI_USE_NETWORK_ANIMIM_UNIT,
                m_UseNetworkAnimimUnit,
                "Animation locomotion state affects gameplay timing/validation and needs authoritative sync.",
                "Animation state is cosmetic and local derivation from movement is enough."
            );
        }

        private void DrawOutputSection()
        {
            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("5) Output", EditorStyles.boldLabel);

            using (new EditorGUILayout.HorizontalScope())
            {
                m_OutputFolderPath = EditorGUILayout.TextField(GUI_OUTPUT_FOLDER, m_OutputFolderPath);
                if (GUILayout.Button("Select...", GUILayout.Width(80f)))
                {
                    string absPath = EditorUtility.OpenFolderPanel("Select Output Folder", Application.dataPath, string.Empty);
                    if (string.IsNullOrEmpty(absPath)) return;

                    string assetsPath = Application.dataPath.Replace("\\", "/");
                    absPath = absPath.Replace("\\", "/");

                    if (!absPath.StartsWith(assetsPath, StringComparison.OrdinalIgnoreCase))
                    {
                        EditorUtility.DisplayDialog(
                            "Invalid Folder",
                            "Please select a folder under this project's Assets directory.",
                            "OK"
                        );
                        return;
                    }

                    m_OutputFolderPath = "Assets" + absPath.Substring(assetsPath.Length);
                }
            }
        }

        private static bool DrawToggleWithGuidance(
            GUIContent content,
            bool value,
            string enableWhen,
            string disableWhen
        )
        {
            bool newValue = EditorGUILayout.ToggleLeft(content, value);

            EditorGUI.indentLevel++;
            EditorGUILayout.LabelField(
                $"Enable when: {enableWhen}\nKeep OFF when: {disableWhen}",
                EditorStyles.wordWrappedMiniLabel
            );
            EditorGUI.indentLevel--;
            EditorGUILayout.Space(2);

            return newValue;
        }

        private static string GetGameTypeContext(TargetGameType type)
        {
            switch (type)
            {
                case TargetGameType.CompetitiveDuel:
                    return "Example fit: 1v1 or tiny-queue competitive loops (fighting-game duel cadence, CS2 Wingman-style rounds).\n" +
                        "Choose this when tight responsiveness and strict consistency matter more than large-scale throughput.";

                case TargetGameType.TeamCompetitive:
                    return "Example fit: structured team PvP (Valorant/CS2 5v5 style, arena objective modes).\n" +
                        "Choose this for balanced fidelity + scalability in common competitive match sizes.";

                case TargetGameType.CoopPve:
                    return "Example fit: players vs AI sessions (Left 4 Dead 2 / Deep Rock style co-op runs).\n" +
                        "Choose this when fairness is still important but anti-cheat strictness can be slightly relaxed.";

                case TargetGameType.LargeScaleBattle:
                    return "Example fit: high-population battlefields (Battlefield-scale ticket matches, Planetside-like zones).\n" +
                        "Choose this when throughput and relevance culling are mandatory for playability.";

                case TargetGameType.SandboxPrototype:
                default:
                    return "Example fit: prototyping and mixed-mode sandboxes (experimental mechanics before final target mode).\n" +
                        "Choose this when requirements are still fluid and you need safe default scaffolding.";
            }
        }

#if UNITY_NETCODE
        private static string GetTransportModeContext(NetcodeTransportMode mode)
        {
            switch (mode)
            {
                case NetcodeTransportMode.SinglePlayerTransport:
                    return "SinglePlayerTransport (local-only):\n" +
                        "Enable when: you are running offline/local simulation and do not need remote peers.\n" +
                        "Keep OFF when: you need LAN/internet clients, dedicated server flow, or true multiplayer transport behavior.\n" +
                        "Constraint: it only supports Host startup (not pure Client or pure Server).";

                case NetcodeTransportMode.UnityTransport:
                default:
                    return "UnityTransport (real networking):\n" +
                        "Enable when: you need actual networked sessions with remote clients (LAN/internet, production-like path).\n" +
                        "Keep OFF when: your target is purely local single-player without opening/listening on network transport.\n" +
                        "Note: configure address/port and connection policy for your environment.";
            }
        }
#endif

        private void DrawRecommendationSection()
        {
            EditorGUILayout.Space(12);
            EditorGUILayout.LabelField("Recommendation Preview", EditorStyles.boldLabel);

            NetworkSessionPreset preset = GetRecommendedPreset(m_TargetGameType, m_MaxPlayers);
            string profileName = $"NetworkSessionProfile_{m_TargetGameType}_{m_MaxPlayers}P";

            var sb = new StringBuilder(220);
            sb.AppendLine($"Recommended Session Preset: {preset}");
            sb.AppendLine($"Proposed Session Profile Name: {profileName}");
            sb.AppendLine($"Max Players: {m_MaxPlayers}");
            sb.AppendLine(m_MaxPlayers > 48
                ? "Scale Profile: large-session tuning (reduced far-tier sync + distance-based fan-out culling)."
                : "Scale Profile: standard tuning.");

            EditorGUILayout.HelpBox(sb.ToString(), MessageType.None);
        }

        private void RunSetup()
        {
            if (!m_OutputFolderPath.StartsWith("Assets", StringComparison.Ordinal))
            {
                EditorUtility.DisplayDialog("Invalid Path", "Asset folder must be inside Assets.", "OK");
                return;
            }

            try
            {
                var report = new SetupReport();
                EnsureAssetFolder(m_OutputFolderPath);

                GameObject setupRoot = null;
                if (m_ParentUnderSetupRoot)
                {
                    setupRoot = GetOrCreateSetupRoot(m_SetupRootName, report);
                }

                EditorUtility.DisplayProgressBar("GC2 Networking Setup", "Creating assets...", 0.2f);

                NetworkSessionProfile sessionProfile = null;
                if (m_CreateSessionProfile)
                {
                    sessionProfile = CreateOrUpdateSessionProfile(report);
                }

                NetworkOffMeshLinkRegistry offMeshRegistry = null;
                if (m_CreateOffMeshLinkRegistry)
                {
                    offMeshRegistry = CreateOrUpdateOffMeshRegistry(report);
                }

                NetworkAnimationRegistry animationRegistry = null;
                if (m_CreateAnimationRegistry)
                {
                    animationRegistry = CreateOrUpdateAnimationRegistry(report);
                }

                EditorUtility.DisplayProgressBar("GC2 Networking Setup", "Creating scene objects...", 0.6f);

#if UNITY_NETCODE
                if (m_CreateNetworkManager)
                {
                    EnsureNetworkManager(m_TransportMode, report);
                }

                if (m_CreateTransportBridge)
                {
                    EnsureTransportBridge(setupRoot, sessionProfile, report);
                }
#else
                if (m_CreateNetworkManager || m_CreateTransportBridge)
                {
                    report.Warnings.AppendLine("- UNITY_NETCODE is not enabled; skipped NetworkManager/Bridge setup.");
                }
#endif

                if (m_CreateSecurityManager)
                {
                    EnsureSecurityManager(setupRoot, report);
                }

                if (m_CreateNetworkPlayer)
                {
                    EnsureNetworkPlayerTemplate(
                        setupRoot,
                        sessionProfile,
                        animationRegistry,
                        report
                    );
                }

                if (m_CreateTestInfrastructure)
                {
                    EnsureTestInfrastructure(setupRoot, sessionProfile, report);
                }

                if (m_AssignAssetsToSceneComponents)
                {
                    if (offMeshRegistry != null)
                    {
                        AssignOffMeshRegistryToScene(offMeshRegistry, report);
                    }

                    if (animationRegistry != null)
                    {
                        AssignAnimationRegistryToScene(animationRegistry, report);
                    }
                }

                EditorUtility.DisplayProgressBar("GC2 Networking Setup", "Finalizing...", 0.9f);

                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();

                string summary = report.ToSummary();
                Debug.Log($"[GC2NetworkingSetupWizard]\n{summary}");

                EditorUtility.DisplayDialog("GC2 Networking Setup Complete", summary, "OK");
            }
            catch (Exception exception)
            {
                Debug.LogError($"[GC2NetworkingSetupWizard] Setup failed:\n{exception}");
                EditorUtility.DisplayDialog("Setup Failed", exception.Message, "OK");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        private NetworkSessionProfile CreateOrUpdateSessionProfile(SetupReport report)
        {
            string fileName = $"NetworkSessionProfile_{m_TargetGameType}_{m_MaxPlayers}P.asset";
            string assetPath = Path.Combine(m_OutputFolderPath, fileName).Replace("\\", "/");

            var profile = AssetDatabase.LoadAssetAtPath<NetworkSessionProfile>(assetPath);
            if (profile == null)
            {
                profile = CreateInstance<NetworkSessionProfile>();
                AssetDatabase.CreateAsset(profile, assetPath);
                report.Created.AppendLine($"- Session Profile: {assetPath}");
            }
            else
            {
                report.Updated.AppendLine($"- Session Profile: {assetPath}");
            }

            profile.ApplyPreset(GetRecommendedPreset(m_TargetGameType, m_MaxPlayers));
            TuneProfileForScale(profile, m_TargetGameType, m_MaxPlayers);
            EditorUtility.SetDirty(profile);

            return profile;
        }

        private NetworkOffMeshLinkRegistry CreateOrUpdateOffMeshRegistry(SetupReport report)
        {
            string fileName = $"OffMeshLinkRegistry_{m_TargetGameType}.asset";
            string assetPath = Path.Combine(m_OutputFolderPath, fileName).Replace("\\", "/");

            var registry = AssetDatabase.LoadAssetAtPath<NetworkOffMeshLinkRegistry>(assetPath);
            if (registry == null)
            {
                registry = CreateInstance<NetworkOffMeshLinkRegistry>();
                AssetDatabase.CreateAsset(registry, assetPath);
                report.Created.AppendLine($"- Off-Mesh Registry: {assetPath}");
            }
            else
            {
                report.Updated.AppendLine($"- Off-Mesh Registry: {assetPath}");
            }

            if (registry.Entries == null || registry.Entries.Length == 0)
            {
                registry.Entries = CreateDefaultOffMeshEntries();
                report.Updated.AppendLine("- Off-Mesh Registry entries populated with defaults.");
            }

            EditorUtility.SetDirty(registry);
            return registry;
        }

        private NetworkAnimationRegistry CreateOrUpdateAnimationRegistry(SetupReport report)
        {
            string fileName = "NetworkAnimationRegistry.asset";
            string assetPath = Path.Combine(m_OutputFolderPath, fileName).Replace("\\", "/");

            var registry = AssetDatabase.LoadAssetAtPath<NetworkAnimationRegistry>(assetPath);
            if (registry == null)
            {
                registry = CreateInstance<NetworkAnimationRegistry>();
                AssetDatabase.CreateAsset(registry, assetPath);
                report.Created.AppendLine($"- Animation Registry: {assetPath}");
            }
            else
            {
                report.Updated.AppendLine($"- Animation Registry: {assetPath}");
            }

            EditorUtility.SetDirty(registry);
            return registry;
        }

#if UNITY_NETCODE
        private NetworkManager EnsureNetworkManager(NetcodeTransportMode transportMode, SetupReport report)
        {
            var manager = FindFirstObjectByType<NetworkManager>();
            if (manager == null)
            {
                var go = new GameObject("NetworkManager");
                manager = go.AddComponent<NetworkManager>();

                Undo.RegisterCreatedObjectUndo(go, "Create NetworkManager");
                report.Created.AppendLine("- Scene Object: NetworkManager at scene root");
            }
            else
            {
                report.Updated.AppendLine("- Reused existing NetworkManager.");
            }

            // NGO requires NetworkManager to not be nested in hierarchy.
            if (manager.transform.parent != null)
            {
                Undo.SetTransformParent(manager.transform, null, "Unparent NetworkManager");
                report.Updated.AppendLine("- Unparented NetworkManager to scene root (fixed nested manager warning).");
            }

            EnsureAndAssignTransport(manager, transportMode, report);
            return manager;
        }

        private static NetworkTransport EnsureAndAssignTransport(
            NetworkManager manager,
            NetcodeTransportMode transportMode,
            SetupReport report
        )
        {
            NetworkTransport selectedTransport;
            switch (transportMode)
            {
                case NetcodeTransportMode.SinglePlayerTransport:
                {
                    var singlePlayer = manager.GetComponent<SinglePlayerTransport>();
                    if (singlePlayer == null)
                    {
                        singlePlayer = manager.gameObject.AddComponent<SinglePlayerTransport>();
                        report.Updated.AppendLine("- Added SinglePlayerTransport to NetworkManager.");
                    }

                    selectedTransport = singlePlayer;
                    break;
                }

                case NetcodeTransportMode.UnityTransport:
                default:
                {
                    var unityTransport = manager.GetComponent<UnityTransport>();
                    if (unityTransport == null)
                    {
                        unityTransport = manager.gameObject.AddComponent<UnityTransport>();
                        report.Updated.AppendLine("- Added UnityTransport to NetworkManager.");
                    }

                    selectedTransport = unityTransport;
                    break;
                }
            }

            if (manager.NetworkConfig == null)
            {
                manager.NetworkConfig = new NetworkConfig();
                report.Updated.AppendLine("- Initialized NetworkManager.NetworkConfig.");
            }

            if (manager.NetworkConfig.NetworkTransport != selectedTransport)
            {
                manager.NetworkConfig.NetworkTransport = selectedTransport;
                report.Updated.AppendLine($"- Assigned {selectedTransport.GetType().Name} to NetworkManager.NetworkConfig.NetworkTransport.");
            }

            EditorUtility.SetDirty(manager);
            EditorUtility.SetDirty(selectedTransport);
            return selectedTransport;
        }

        private void EnsureTransportBridge(
            GameObject setupRoot,
            NetworkSessionProfile sessionProfile,
            SetupReport report
        )
        {
            var bridge = FindFirstObjectByType<NetcodeGameObjectsTransportBridge>();
            if (bridge == null)
            {
                var go = new GameObject("Netcode Transport Bridge");
                if (setupRoot != null) go.transform.SetParent(setupRoot.transform);

                bridge = go.AddComponent<NetcodeGameObjectsTransportBridge>();
                Undo.RegisterCreatedObjectUndo(go, "Create Netcode Transport Bridge");
                report.Created.AppendLine("- Scene Object: Netcode Transport Bridge");
            }
            else
            {
                report.Updated.AppendLine("- Reused existing Netcode Transport Bridge.");
            }

            if (sessionProfile != null)
            {
                var bridgeSo = new SerializedObject(bridge);
                SerializedProperty globalProfile = bridgeSo.FindProperty("m_GlobalSessionProfile");
                if (globalProfile != null)
                {
                    globalProfile.objectReferenceValue = sessionProfile;
                    bridgeSo.ApplyModifiedPropertiesWithoutUndo();
                    EditorUtility.SetDirty(bridge);
                    report.Updated.AppendLine("- Assigned Global Session Profile on transport bridge.");
                }
            }
        }
#endif

        private void EnsureSecurityManager(GameObject setupRoot, SetupReport report)
        {
            var securityManager = FindFirstObjectByType<NetworkSecurityManager>();
            if (securityManager == null)
            {
                var go = new GameObject("Network Security Manager");
                if (setupRoot != null) go.transform.SetParent(setupRoot.transform);

                securityManager = go.AddComponent<NetworkSecurityManager>();
                Undo.RegisterCreatedObjectUndo(go, "Create Network Security Manager");
                report.Created.AppendLine("- Scene Object: Network Security Manager");
            }
            else
            {
                report.Updated.AppendLine("- Reused existing Network Security Manager.");
            }

            EditorUtility.SetDirty(securityManager);
        }

        private void EnsureNetworkPlayerTemplate(
            GameObject setupRoot,
            NetworkSessionProfile sessionProfile,
            NetworkAnimationRegistry animationRegistry,
            SetupReport report
        )
        {
            NetworkCharacter networkCharacter = FindPlayerTemplate();
            GameObject go;

            if (networkCharacter == null)
            {
                go = new GameObject("Network Player");
                if (setupRoot != null) go.transform.SetParent(setupRoot.transform);

                var character = go.AddComponent<Character>();
                character.IsPlayer = true;
                ConfigureCharacterDefaults(character);
                ConfigureKernelForNetworkPlayer(
                    character,
                    m_UseNetworkDriverUnit,
                    m_UseNetworkFacingUnit,
                    m_UseNetworkAnimimUnit
                );

#if UNITY_NETCODE
                if (go.GetComponent<NetworkObject>() == null)
                {
                    go.AddComponent<NetworkObject>();
                }
#endif

                networkCharacter = go.AddComponent<NetworkCharacter>();
                Undo.RegisterCreatedObjectUndo(go, "Create Network Player");
                report.Created.AppendLine("- Scene Object: Network Player");
            }
            else
            {
                go = networkCharacter.gameObject;
                var character = go.GetComponent<Character>();
                if (character != null)
                {
                    character.IsPlayer = true;
                    ConfigureKernelForNetworkPlayer(
                        character,
                        m_UseNetworkDriverUnit,
                        m_UseNetworkFacingUnit,
                        m_UseNetworkAnimimUnit
                    );
                }

#if UNITY_NETCODE
                if (go.GetComponent<NetworkObject>() == null)
                {
                    go.AddComponent<NetworkObject>();
                    report.Updated.AppendLine("- Added NetworkObject to existing player template.");
                }
#endif

                report.Updated.AppendLine("- Reused existing Network Player template.");
            }

            ConfigureNetworkCharacterComponent(
                networkCharacter,
                sessionProfile,
                m_AssignProfileOverrideToCreatedPlayer,
                m_HostOwnerUsesClientPrediction
            );

            if (m_CreateAnimationRegistry && animationRegistry != null)
            {
                var animController = go.GetComponent<UnitAnimimNetworkController>();
                if (animController == null)
                {
                    animController = go.AddComponent<UnitAnimimNetworkController>();
                    report.Updated.AppendLine("- Added UnitAnimimNetworkController to player template.");
                }

                var animSo = new SerializedObject(animController);
                SerializedProperty registryProp = animSo.FindProperty("m_AnimationRegistry");
                if (registryProp != null)
                {
                    registryProp.objectReferenceValue = animationRegistry;
                    animSo.ApplyModifiedPropertiesWithoutUndo();
                    EditorUtility.SetDirty(animController);
                }
            }
        }

        private void ConfigureCharacterDefaults(Character character)
        {
            GameObject model = AssetDatabase.LoadAssetAtPath<GameObject>(CharacterEditor.MODEL_PATH);
            MaterialSoundsAsset footsteps = AssetDatabase.LoadAssetAtPath<MaterialSoundsAsset>(FOOTSTEPS_PATH);
            RuntimeAnimatorController controller = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(RTC_PATH);

            if (model != null)
            {
                character.ChangeModel(model, new Character.ChangeOptions
                {
                    controller = controller,
                    materials = footsteps,
                    offset = Vector3.zero
                });
            }

            float halfHeight = character.Motion != null ? character.Motion.Height * 0.5f : 1f;
            character.transform.position += Vector3.up * halfHeight;
        }

        private void ConfigureKernelForNetworkPlayer(
            Character character,
            bool useNetworkDriver,
            bool useNetworkFacing,
            bool useNetworkAnimim
        )
        {
            var so = new SerializedObject(character);
            SerializedProperty kernel = so.FindProperty("m_Kernel");
            if (kernel == null)
            {
                so.ApplyModifiedPropertiesWithoutUndo();
                return;
            }

            SerializedProperty player = kernel.FindPropertyRelative("m_Player");
            if (player != null)
            {
                player.managedReferenceValue = new UnitPlayerDirectionalNetwork();
            }

            SerializedProperty driver = kernel.FindPropertyRelative("m_Driver");
            if (driver != null && useNetworkDriver)
            {
                driver.managedReferenceValue = new UnitDriverNetworkClient();
            }

            SerializedProperty motion = kernel.FindPropertyRelative("m_Motion");
            if (motion != null)
            {
                motion.managedReferenceValue = new UnitMotionNetworkController();
            }

            SerializedProperty facing = kernel.FindPropertyRelative("m_Facing");
            if (facing != null && useNetworkFacing)
            {
                facing.managedReferenceValue = new UnitFacingNetworkPivot();
            }

            SerializedProperty animim = kernel.FindPropertyRelative("m_Animim");
            if (animim != null && useNetworkAnimim)
            {
                animim.managedReferenceValue = new UnitAnimimNetworkKinematic();
            }

            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(character);
        }

        private static void ConfigureNetworkCharacterComponent(
            NetworkCharacter networkCharacter,
            NetworkSessionProfile sessionProfile,
            bool assignProfileOverride,
            bool hostOwnerUsesClientPrediction
        )
        {
            var so = new SerializedObject(networkCharacter);

            SerializedProperty useMotion = so.FindProperty("m_UseNetworkMotion");
            if (useMotion != null) useMotion.boolValue = true;

            SerializedProperty hostRole = so.FindProperty("m_HostOwnerUsesClientPrediction");
            if (hostRole != null) hostRole.boolValue = hostOwnerUsesClientPrediction;

            SerializedProperty overrideProfile = so.FindProperty("m_SessionProfileOverride");
            if (overrideProfile != null && assignProfileOverride)
            {
                overrideProfile.objectReferenceValue = sessionProfile;
            }

            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(networkCharacter);
        }

        private void AssignOffMeshRegistryToScene(NetworkOffMeshLinkRegistry registry, SetupReport report)
        {
            int count = 0;

            var servers = FindObjectsByType<OffMeshLinkNetworkServer>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            for (int i = 0; i < servers.Length; i++)
            {
                var so = new SerializedObject(servers[i]);
                SerializedProperty prop = so.FindProperty("m_Registry");
                if (prop == null) continue;

                prop.objectReferenceValue = registry;
                so.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(servers[i]);
                count++;
            }

            var clients = FindObjectsByType<OffMeshLinkNetworkClient>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            for (int i = 0; i < clients.Length; i++)
            {
                var so = new SerializedObject(clients[i]);
                SerializedProperty prop = so.FindProperty("m_Registry");
                if (prop == null) continue;

                prop.objectReferenceValue = registry;
                so.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(clients[i]);
                count++;
            }

            report.Updated.AppendLine($"- Assigned Off-Mesh registry to {count} component(s).");
        }

        private void AssignAnimationRegistryToScene(NetworkAnimationRegistry registry, SetupReport report)
        {
            int count = 0;

            var controllers = FindObjectsByType<UnitAnimimNetworkController>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            for (int i = 0; i < controllers.Length; i++)
            {
                var so = new SerializedObject(controllers[i]);
                SerializedProperty prop = so.FindProperty("m_AnimationRegistry");
                if (prop == null) continue;

                prop.objectReferenceValue = registry;
                so.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(controllers[i]);
                count++;
            }

            report.Updated.AppendLine($"- Assigned Animation registry to {count} component(s).");
        }

        private NetworkCharacter FindPlayerTemplate()
        {
            var all = FindObjectsByType<NetworkCharacter>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            for (int i = 0; i < all.Length; i++)
            {
                var character = all[i].GetComponent<Character>();
                if (character == null) continue;
                if (!character.IsPlayer) continue;
                return all[i];
            }

            return null;
        }

        private static OffMeshLinkTypeEntry[] CreateDefaultOffMeshEntries()
        {
            return new[]
            {
                new OffMeshLinkTypeEntry
                {
                    Name = "Auto",
                    TraversalType = OffMeshLinkTraversalType.Auto,
                    DefaultDuration = 0.75f,
                    MovementCurve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f),
                    ArcHeight = 0f
                },
                new OffMeshLinkTypeEntry
                {
                    Name = "Jump",
                    TraversalType = OffMeshLinkTraversalType.Jump,
                    DefaultDuration = 0.9f,
                    MovementCurve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f),
                    ArcHeight = 1.2f
                },
                new OffMeshLinkTypeEntry
                {
                    Name = "Vault",
                    TraversalType = OffMeshLinkTraversalType.Vault,
                    DefaultDuration = 0.65f,
                    MovementCurve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f),
                    ArcHeight = 0.6f
                },
                new OffMeshLinkTypeEntry
                {
                    Name = "Climb",
                    TraversalType = OffMeshLinkTraversalType.Climb,
                    DefaultDuration = 1.2f,
                    MovementCurve = AnimationCurve.Linear(0f, 0f, 1f, 1f),
                    ArcHeight = 0f
                },
                new OffMeshLinkTypeEntry
                {
                    Name = "Drop",
                    TraversalType = OffMeshLinkTraversalType.Drop,
                    DefaultDuration = 0.8f,
                    MovementCurve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f),
                    ArcHeight = 0f
                },
                new OffMeshLinkTypeEntry
                {
                    Name = "Teleport",
                    TraversalType = OffMeshLinkTraversalType.Teleport,
                    DefaultDuration = 0.05f,
                    MovementCurve = AnimationCurve.Linear(0f, 0f, 1f, 1f),
                    ArcHeight = 0f
                }
            };
        }

        private static NetworkSessionPreset GetRecommendedPreset(TargetGameType gameType, int maxPlayers)
        {
            switch (gameType)
            {
                case TargetGameType.CompetitiveDuel:
                    return NetworkSessionPreset.Duel;

                case TargetGameType.LargeScaleBattle:
                    return NetworkSessionPreset.Massive;

                case TargetGameType.TeamCompetitive:
                    return maxPlayers <= 32 ? NetworkSessionPreset.Standard : NetworkSessionPreset.Massive;

                case TargetGameType.CoopPve:
                    return maxPlayers > 48 ? NetworkSessionPreset.Massive : NetworkSessionPreset.Standard;

                case TargetGameType.SandboxPrototype:
                default:
                    return maxPlayers <= 2 ? NetworkSessionPreset.Duel :
                        maxPlayers <= 48 ? NetworkSessionPreset.Standard :
                        NetworkSessionPreset.Massive;
            }
        }

        private static void TuneProfileForScale(
            NetworkSessionProfile profile,
            TargetGameType gameType,
            int maxPlayers
        )
        {
            if (maxPlayers >= 64)
            {
                profile.serverSimulationRate = Mathf.Min(profile.serverSimulationRate, 30);
                profile.serverStateBroadcastRate = Mathf.Min(profile.serverStateBroadcastRate, 12);
                profile.inputSendRate = Mathf.Min(profile.inputSendRate, 20);
                profile.inputRedundancy = Mathf.Min(profile.inputRedundancy, 2);

                profile.requireObserverCharacterForRelevance = true;
                profile.enableDistanceCulling = true;
                profile.cullDistance = Mathf.Min(profile.cullDistance, 90f);
                profile.culledKeepAliveRate = Mathf.Max(profile.culledKeepAliveRate, 1f);

                profile.far.syncIK = false;
                profile.far.syncAnimation = false;
                profile.far.syncCore = false;
                profile.far.syncCombat = false;
            }

            if (maxPlayers <= 4)
            {
                profile.serverSimulationRate = Mathf.Max(profile.serverSimulationRate, 60);
                profile.serverStateBroadcastRate = Mathf.Max(profile.serverStateBroadcastRate, 30);
                profile.inputSendRate = Mathf.Max(profile.inputSendRate, 40);
            }

            if (gameType == TargetGameType.CoopPve || gameType == TargetGameType.SandboxPrototype)
            {
                profile.maxSpeedMultiplier = Mathf.Max(profile.maxSpeedMultiplier, 1.25f);
                profile.violationThreshold = Mathf.Max(profile.violationThreshold, 6);
            }
        }

        private static void EnsureAssetFolder(string folder)
        {
            folder = folder.Replace("\\", "/");
            if (AssetDatabase.IsValidFolder(folder)) return;

            string[] parts = folder.Split('/');
            if (parts.Length == 0 || parts[0] != "Assets")
            {
                throw new InvalidOperationException($"Invalid asset folder path: {folder}");
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

        private static GameObject GetOrCreateSetupRoot(string rootName, SetupReport report)
        {
            var root = GameObject.Find(rootName);
            if (root != null)
            {
                report.Updated.AppendLine($"- Reused setup root: {rootName}");
                return root;
            }

            root = new GameObject(rootName);
            Undo.RegisterCreatedObjectUndo(root, "Create GC2 Networking Setup Root");
            report.Created.AppendLine($"- Scene Object: {rootName}");
            return root;
        }

        private static void EnsureTestInfrastructure(
            GameObject setupRoot,
            NetworkSessionProfile sessionProfile,
            SetupReport report)
        {
            const string TEST_RUNNER_NAME = "NetworkTestRunner";

            var existing = setupRoot != null
                ? setupRoot.transform.Find(TEST_RUNNER_NAME)?.gameObject
                : GameObject.Find(TEST_RUNNER_NAME);

            if (existing != null)
            {
                // Ensure the component is attached
                if (existing.GetComponent<NetworkTestRunner>() == null)
                    existing.AddComponent<NetworkTestRunner>();

                if (sessionProfile != null)
                {
                    var runner = existing.GetComponent<NetworkTestRunner>();
                    runner.sessionProfile = sessionProfile;
                }

                report.Updated.AppendLine($"- Reused {TEST_RUNNER_NAME}");
                return;
            }

            var go = new GameObject(TEST_RUNNER_NAME);
            Undo.RegisterCreatedObjectUndo(go, "Create NetworkTestRunner");

            if (setupRoot != null)
            {
                go.transform.SetParent(setupRoot.transform, false);
            }

            var testRunner = go.AddComponent<NetworkTestRunner>();
            if (sessionProfile != null)
            {
                testRunner.sessionProfile = sessionProfile;
            }

            report.Created.AppendLine($"- Scene Object: {TEST_RUNNER_NAME} (runtime test infrastructure)");
        }

        private sealed class SetupReport
        {
            public readonly StringBuilder Created = new StringBuilder();
            public readonly StringBuilder Updated = new StringBuilder();
            public readonly StringBuilder Warnings = new StringBuilder();

            public string ToSummary()
            {
                var sb = new StringBuilder(512);
                sb.AppendLine("GC2 Networking setup completed.");

                if (Created.Length > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("Created:");
                    sb.Append(Created);
                }

                if (Updated.Length > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("Updated:");
                    sb.Append(Updated);
                }

                if (Warnings.Length > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("Warnings:");
                    sb.Append(Warnings);
                }

                return sb.ToString();
            }
        }
    }
}
#endif
