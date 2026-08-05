using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Arawn.GameCreator2.Networking.Security;
using Arawn.GameCreator2.Networking.Transport.Fusion;
using Fusion;
using Fusion.Editor;
using Fusion.Photon.Realtime;
using GameCreator.Runtime.Characters;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Arawn.GameCreator2.Networking.Transport.Fusion.Editor
{
    internal enum FusionSetupIssueSeverity
    {
        Info,
        Warning,
        Error
    }

    internal readonly struct FusionSetupIssue
    {
        public FusionSetupIssue(FusionSetupIssueSeverity severity, string message, UnityEngine.Object context = null)
        {
            Severity = severity;
            Message = message;
            Context = context;
        }

        public FusionSetupIssueSeverity Severity { get; }
        public string Message { get; }
        public UnityEngine.Object Context { get; }
    }

    internal sealed class FusionSetupReport
    {
        private readonly List<FusionSetupIssue> m_Issues = new();

        public IReadOnlyList<FusionSetupIssue> Issues => m_Issues;
        public bool HasErrors => m_Issues.Any(issue => issue.Severity == FusionSetupIssueSeverity.Error);
        public bool HasWarnings => m_Issues.Any(issue => issue.Severity == FusionSetupIssueSeverity.Warning);

        public void Add(FusionSetupIssueSeverity severity, string message, UnityEngine.Object context = null)
        {
            m_Issues.Add(new FusionSetupIssue(severity, message, context));
        }
    }

    /// <summary>
    /// Read-only validation shared by the Fusion setup wizard and editor tests.
    /// It deliberately identifies third-party integrations by namespace so the
    /// Arawn Fusion editor assembly never references Ninjutsu or PurrNet assemblies.
    /// </summary>
    internal static class FusionSceneSetupValidation
    {
        internal const string RuntimeAssemblyName =
            "Arawn.GameCreator2.Networking.Transport.Fusion";

        internal const string RuntimeAssemblyDefinitionPath =
            "Assets/Arawn/NetworkingLayerForGC2/Runtime/Transport/Fusion/" +
            "Arawn.GameCreator2.Networking.Transport.Fusion.asmdef";

        private const string FusionBuildInfoPath = "Assets/Photon/Fusion/build_info.txt";

        [Serializable]
        private sealed class AssemblyDefinitionData
        {
            public string name;
            public bool allowUnsafeCode;
        }

        public static FusionSetupReport Validate(
            GameObject playerPrefab,
            bool requireAppliedInfrastructure = false,
            NetworkPredictionBackend? expectedBackend = null)
        {
            var report = new FusionSetupReport();

            ValidateSdk(report);
            ValidatePhotonSettings(report);
            ValidateRuntimeAssemblyDefinition(report);
            ValidateMonoPlayerBuildCompatibility(report);
            ValidateProjectConfig(report);
            ValidateSceneOwners(report);
            ValidateSceneCharacters(report);
            ValidateModuleRegistrations(report);
            ValidateInventoryRuntimePickups(report);
            ValidatePlayerPrefab(report, playerPrefab, expectedBackend);
            if (requireAppliedInfrastructure)
            {
                ValidateRequiredInfrastructure(report);
            }

            return report;
        }

        internal static FusionSetupReport ValidatePlayerPrefabOnly(GameObject playerPrefab)
        {
            var report = new FusionSetupReport();
            ValidatePlayerPrefab(report, playerPrefab, null);
            return report;
        }

        private static void ValidateSdk(FusionSetupReport report)
        {
            Type runnerType = typeof(NetworkRunner);
            if (runnerType.Assembly == null)
            {
                report.Add(FusionSetupIssueSeverity.Error, "Photon Fusion runtime could not be loaded.");
                return;
            }

            if (!File.Exists(FusionBuildInfoPath))
            {
                report.Add(
                    FusionSetupIssueSeverity.Error,
                    $"Fusion build information was not found at '{FusionBuildInfoPath}'. " +
                    "This integration targets Fusion 2.1.1.");
                return;
            }

            string buildInfo;
            try
            {
                buildInfo = File.ReadAllText(FusionBuildInfoPath);
            }
            catch (Exception exception)
            {
                report.Add(
                    FusionSetupIssueSeverity.Error,
                    $"Fusion build information could not be read: {exception.Message}");
                return;
            }

            if (buildInfo.IndexOf("2.1.1", StringComparison.OrdinalIgnoreCase) < 0)
            {
                report.Add(
                    FusionSetupIssueSeverity.Error,
                    "The installed Fusion SDK does not report version 2.1.1. " +
                    "Re-run transport compatibility tests before shipping.");
            }
        }

        private static void ValidatePhotonSettings(FusionSetupReport report)
        {
            try
            {
                string appId = PhotonAppSettings.Global?.AppSettings?.AppIdFusion;
                if (string.IsNullOrWhiteSpace(appId))
                {
                    report.Add(
                        FusionSetupIssueSeverity.Warning,
                        "Photon Fusion App Id is empty. Offline scene generation is allowed, " +
                        "but live session buttons remain unavailable.");
                }
            }
            catch (Exception exception)
            {
                report.Add(
                    FusionSetupIssueSeverity.Warning,
                    $"Photon App Settings could not be loaded: {exception.Message}");
            }
        }

        private static void ValidateProjectConfig(FusionSetupReport report)
        {
            NetworkProjectConfig config;
            try
            {
                config = NetworkProjectConfig.Global;
            }
            catch (Exception exception)
            {
                report.Add(
                    FusionSetupIssueSeverity.Error,
                    $"NetworkProjectConfig could not be loaded: {exception.Message}");
                return;
            }

            if (config == null)
            {
                report.Add(FusionSetupIssueSeverity.Error, "NetworkProjectConfig is missing.");
                return;
            }

            if (config.PeerMode != NetworkProjectConfig.PeerModes.Single)
            {
                report.Add(
                    FusionSetupIssueSeverity.Warning,
                    "Fusion Peer Mode is Multiple. The GC2 transport supports one runner per process " +
                    "and the wizard will set Peer Mode to Single.");
            }

            if (config.Simulation.InputTransferMode !=
                SimulationConfig.InputTransferModes.Redundancy)
            {
                report.Add(
                    FusionSetupIssueSeverity.Error,
                    "Fusion Input Transfer Mode must be Redundancy. GC2 native movement, " +
                    "Traversal and root-motion prediction require tick-complete input history; " +
                    "Latest State causes connected-owner rollback stutter. Re-run the Fusion " +
                    "Scene Setup Wizard to repair NetworkProjectConfig.");
            }

            int tickRate = config.Simulation.TickRateSelection.Client;
            if (tickRate > 32)
            {
                report.Add(
                    FusionSetupIssueSeverity.Warning,
                    $"Fusion tick rate is {tickRate}. Shared-compatible setup requires at most 32 Hz.");
            }

            NetworkConfiguration.ReliableDataTransfers required =
                NetworkConfiguration.ReliableDataTransfers.ClientToServer |
                NetworkConfiguration.ReliableDataTransfers.ClientToClientWithServerProxy;
            if ((config.Network.ReliableDataTransferModes & required) != required)
            {
                report.Add(
                    FusionSetupIssueSeverity.Warning,
                    "Reliable data transfer is not enabled for both client-to-server and " +
                    "client-to-client-via-proxy delivery.");
            }

            if (config.AssembliesToWeave == null ||
                !config.AssembliesToWeave.Contains(RuntimeAssemblyName, StringComparer.Ordinal))
            {
                report.Add(
                    FusionSetupIssueSeverity.Error,
                    $"'{RuntimeAssemblyName}' is missing from Assemblies To Weave. " +
                    "Fusion RPC routing cannot work until the assembly is woven.");
            }
        }

        private static void ValidateRuntimeAssemblyDefinition(FusionSetupReport report)
        {
            string assetPath = FindRuntimeAssemblyDefinitionPath();
            if (string.IsNullOrEmpty(assetPath))
            {
                report.Add(
                    FusionSetupIssueSeverity.Error,
                    $"The '{RuntimeAssemblyName}' assembly definition could not be found.");
                return;
            }

            string fullPath = Path.GetFullPath(
                Path.Combine(Application.dataPath, "..", assetPath));
            AssemblyDefinitionData definition;

            try
            {
                definition = JsonUtility.FromJson<AssemblyDefinitionData>(
                    File.ReadAllText(fullPath));
            }
            catch (Exception exception)
            {
                report.Add(
                    FusionSetupIssueSeverity.Error,
                    $"Fusion runtime assembly definition could not be read: {exception.Message}");
                return;
            }

            if (definition == null ||
                !string.Equals(definition.name, RuntimeAssemblyName, StringComparison.Ordinal))
            {
                report.Add(
                    FusionSetupIssueSeverity.Error,
                    $"'{assetPath}' does not define the expected " +
                    $"'{RuntimeAssemblyName}' assembly.");
                return;
            }

            if (!definition.allowUnsafeCode)
            {
                report.Add(
                    FusionSetupIssueSeverity.Error,
                    $"'{RuntimeAssemblyName}' must enable Allow Unsafe Code. Fusion's IL weaver " +
                    "requires it for generated RPC entry points; recompile and rebuild the player " +
                    "after enabling it.");
            }
        }

        private static void ValidateMonoPlayerBuildCompatibility(FusionSetupReport report)
        {
            if (FusionMonoBuildCompatibility.TryGetActiveBuildTargetIssue(out string issue))
            {
                report.Add(FusionSetupIssueSeverity.Error, issue);
            }
        }

        private static string FindRuntimeAssemblyDefinitionPath()
        {
            string knownFullPath = Path.GetFullPath(
                Path.Combine(Application.dataPath, "..", RuntimeAssemblyDefinitionPath));
            if (File.Exists(knownFullPath)) return RuntimeAssemblyDefinitionPath;

            // Asset Store folders may be moved within Assets. Resolve by the assembly's
            // declared name instead of making the default install path a requirement.
            string[] assemblyDefinitionGuids = AssetDatabase.FindAssets("t:asmdef");
            for (int i = 0; i < assemblyDefinitionGuids.Length; i++)
            {
                string assetPath = AssetDatabase.GUIDToAssetPath(assemblyDefinitionGuids[i]);
                if (string.IsNullOrEmpty(assetPath)) continue;

                try
                {
                    string fullPath = Path.GetFullPath(
                        Path.Combine(Application.dataPath, "..", assetPath));
                    AssemblyDefinitionData candidate =
                        JsonUtility.FromJson<AssemblyDefinitionData>(File.ReadAllText(fullPath));
                    if (candidate != null &&
                        string.Equals(candidate.name, RuntimeAssemblyName, StringComparison.Ordinal))
                    {
                        return assetPath;
                    }
                }
                catch
                {
                    // Another package's malformed/unreadable asmdef is unrelated. If it is
                    // ours, the final not-found diagnostic still blocks setup safely.
                }
            }

            return string.Empty;
        }

        private static void ValidateSceneOwners(FusionSetupReport report)
        {
            NetworkRunner[] runners = FindSceneComponents<NetworkRunner>();
            if (runners.Length > 1)
            {
                report.Add(
                    FusionSetupIssueSeverity.Error,
                    $"The active scene contains {runners.Length} NetworkRunner components. " +
                    "Keep exactly one runner/session owner.");
            }

            FusionSessionBootstrap[] bootstraps = FindSceneComponents<FusionSessionBootstrap>();
            if (bootstraps.Length > 1)
            {
                report.Add(
                    FusionSetupIssueSeverity.Error,
                    $"The active scene contains {bootstraps.Length} FusionSessionBootstrap components.");
            }

            foreach (FusionSessionBootstrap bootstrap in bootstraps)
            {
                MonoBehaviour authenticationProvider =
                    bootstrap.AuthenticationProviderBehaviour;
                if (authenticationProvider != null &&
                    authenticationProvider is not IFusionAuthenticationProvider)
                {
                    report.Add(
                        FusionSetupIssueSeverity.Error,
                        $"FusionSessionBootstrap '{bootstrap.name}' authentication provider " +
                        $"'{authenticationProvider.name}' does not implement " +
                        $"{nameof(IFusionAuthenticationProvider)}.",
                        bootstrap);
                }
            }

            MonoBehaviour[] behaviours = FindSceneComponents<MonoBehaviour>();
            int photonBootstrapCount = behaviours.Count(behaviour =>
                behaviour != null &&
                behaviour.isActiveAndEnabled &&
                behaviour.GetType().FullName == "Fusion.FusionBootstrap");
            int ownerCount = runners.Length + bootstraps.Length + photonBootstrapCount;
            if (ownerCount == 0)
            {
                report.Add(
                    FusionSetupIssueSeverity.Warning,
                    "No Fusion runner/session owner is configured. The wizard can create the " +
                    "Arawn FusionSessionBootstrap.");
            }
            else if (ownerCount > 1)
            {
                report.Add(
                    FusionSetupIssueSeverity.Error,
                    $"The scene has {ownerCount} runner/session owners " +
                    $"({runners.Length} runner, {bootstraps.Length} Arawn bootstrap, " +
                    $"{photonBootstrapCount} Fusion bootstrap). Keep exactly one.");
            }

            FusionTransportBridge[] bridges = FindSceneComponents<FusionTransportBridge>();
            if (bridges.Length > 1)
            {
                report.Add(
                    FusionSetupIssueSeverity.Error,
                    $"The active scene contains {bridges.Length} FusionTransportBridge components.");
            }

            FusionAuthoritySpawnRegistry[] registries =
                FindSceneComponents<FusionAuthoritySpawnRegistry>();
            if (registries.Length > 1)
            {
                report.Add(
                    FusionSetupIssueSeverity.Error,
                    $"The active scene contains {registries.Length} " +
                    "FusionAuthoritySpawnRegistry components.");
            }

            ValidateDuplicate<FusionRpcRouter>(report, "FusionRpcRouter");
            ValidateDuplicate<FusionPlayerSpawner>(report, "FusionPlayerSpawner");
            ValidateDuplicate<NetworkSecurityManager>(report, "NetworkSecurityManager");
            ValidateDuplicate<NetworkCoreManager>(report, "NetworkCoreManager");
            ValidateDuplicate<NetworkAnimationManager>(report, "NetworkAnimationManager");
            ValidateDuplicate<NetworkMotionManager>(report, "NetworkMotionManager");
            ValidateDuplicate<NetworkVariableManager>(report, "NetworkVariableManager");

            ValidateDuplicateType(
                report,
                "Arawn.GameCreator2.Networking.Transport.Fusion.FusionCoreTransportBridge, " +
                RuntimeAssemblyName,
                "Fusion Core Bridge");
            ValidateDuplicateType(
                report,
                "Arawn.GameCreator2.Networking.Transport.Fusion.FusionVariableTransportBridge, " +
                RuntimeAssemblyName,
                "Fusion Variable Bridge");
            ValidateDuplicateType(
                report,
                "Arawn.GameCreator2.Networking.Transport.Fusion.FusionAnimationMotionTransportBridge, " +
                RuntimeAssemblyName,
                "Fusion Animation/Motion Bridge");

            foreach (MonoBehaviour behaviour in behaviours)
            {
                if (!behaviour.isActiveAndEnabled) continue;

                Type type = behaviour.GetType();
                string typeNamespace = type.Namespace ?? string.Empty;

                if (typeNamespace.StartsWith(
                        "NinjutsuGames.FusionNetwork",
                        StringComparison.Ordinal))
                {
                    report.Add(
                        FusionSetupIssueSeverity.Error,
                        $"Active Ninjutsu Fusion component conflicts with the server-authoritative " +
                        $"Arawn transport: {type.FullName}.",
                        behaviour);
                }

                if (typeNamespace.Contains(".Transport.PurrNet", StringComparison.Ordinal) &&
                    type.Name.EndsWith("TransportBridge", StringComparison.Ordinal))
                {
                    report.Add(
                        FusionSetupIssueSeverity.Error,
                        $"Active PurrNet transport bridge conflicts with Fusion: {type.FullName}.",
                        behaviour);
                }
            }
        }

        private static void ValidateSceneCharacters(FusionSetupReport report)
        {
            foreach (NetworkCharacter networkCharacter in FindSceneComponents<NetworkCharacter>())
            {
                if (networkCharacter == null || !networkCharacter.isActiveAndEnabled) continue;
                foreach (Component component in
                         networkCharacter.GetComponentsInChildren<Component>(true))
                {
                    if (!IsComponentActive(component) ||
                        !IsCompetingMovementComponent(component))
                    {
                        continue;
                    }
                    report.Add(
                        FusionSetupIssueSeverity.Error,
                        $"GC2 network character '{networkCharacter.name}' contains the competing " +
                        $"Fusion movement synchronizer {component.GetType().FullName}.",
                        component);
                }
            }
        }

        private static void ValidateRequiredInfrastructure(FusionSetupReport report)
        {
            FusionTransportBridge transport = RequireSingle<FusionTransportBridge>(
                report,
                "FusionTransportBridge");
            FusionRpcRouter router = RequireSingle<FusionRpcRouter>(report, "FusionRpcRouter");
            FusionAuthoritySpawnRegistry registry =
                RequireSingle<FusionAuthoritySpawnRegistry>(
                    report,
                    "FusionAuthoritySpawnRegistry");
            FusionPlayerSpawner spawner = RequireSingle<FusionPlayerSpawner>(
                report,
                "FusionPlayerSpawner");

            RequireSingle<NetworkSecurityManager>(report, "NetworkSecurityManager");
            RequireSingle<NetworkCoreManager>(report, "NetworkCoreManager");
            RequireSingle<NetworkAnimationManager>(report, "NetworkAnimationManager");
            RequireSingle<NetworkMotionManager>(report, "NetworkMotionManager");
            RequireSingle<NetworkVariableManager>(report, "NetworkVariableManager");

            Component coreBridge = RequireSingleType(
                report,
                "Arawn.GameCreator2.Networking.Transport.Fusion.FusionCoreTransportBridge, " +
                RuntimeAssemblyName,
                "Fusion Core Bridge");
            Component variableBridge = RequireSingleType(
                report,
                "Arawn.GameCreator2.Networking.Transport.Fusion.FusionVariableTransportBridge, " +
                RuntimeAssemblyName,
                "Fusion Variable Bridge");
            Component animationMotionBridge = RequireSingleType(
                report,
                "Arawn.GameCreator2.Networking.Transport.Fusion." +
                "FusionAnimationMotionTransportBridge, " + RuntimeAssemblyName,
                "Fusion Animation/Motion Bridge");

            NetworkRunner[] runners = FindSceneComponents<NetworkRunner>();
            FusionSessionBootstrap[] bootstraps = FindSceneComponents<FusionSessionBootstrap>();
            MonoBehaviour[] behaviours = FindSceneComponents<MonoBehaviour>();
            int photonBootstrapCount = behaviours.Count(behaviour =>
                behaviour != null &&
                behaviour.isActiveAndEnabled &&
                behaviour.GetType().FullName == "Fusion.FusionBootstrap");
            if (runners.Length + bootstraps.Length + photonBootstrapCount == 0)
            {
                report.Add(
                    FusionSetupIssueSeverity.Error,
                    "The applied setup has no Fusion runner/session owner.");
            }

            if (transport != null)
            {
                ValidateObjectReference(
                    report,
                    transport,
                    "m_RpcRouter",
                    router,
                    "Fusion transport RPC router");
                ValidateNonNullObjectReference(
                    report,
                    transport,
                    "m_GlobalSessionProfile",
                    "Fusion transport session profile");

                if (bootstraps.Length == 1)
                {
                    ValidateObjectReference(
                        report,
                        transport,
                        "m_SessionBootstrap",
                        bootstraps[0],
                        "Fusion transport session bootstrap");
                    ValidateObjectReference(
                        report,
                        bootstraps[0],
                        "m_TransportBridge",
                        transport,
                        "Fusion session bootstrap transport");
                }
                else if (runners.Length == 1)
                {
                    ValidateObjectReference(
                        report,
                        transport,
                        "m_Runner",
                        runners[0],
                        "Fusion transport runner");
                }
                else if (photonBootstrapCount == 1)
                {
                    ValidateBoolean(
                        report,
                        transport,
                        "m_AutoBindSingleRunner",
                        true,
                        "Fusion transport automatic runner binding");
                }
            }

            ValidateObjectReference(
                report,
                coreBridge,
                "m_TransportBridge",
                transport,
                "Fusion Core Bridge transport");
            ValidateObjectReference(
                report,
                variableBridge,
                "m_TransportBridge",
                transport,
                "Fusion Variable Bridge transport");
            ValidateObjectReference(
                report,
                animationMotionBridge,
                "m_TransportBridge",
                transport,
                "Fusion Animation/Motion Bridge transport");

            if (registry != null)
            {
                ValidateObjectReference(
                    report,
                    registry,
                    "m_TransportBridge",
                    transport,
                    "Fusion authority registry transport");
            }

            if (spawner != null)
            {
                ValidateObjectReference(
                    report,
                    spawner,
                    "m_TransportBridge",
                    transport,
                    "Fusion player spawner transport");
                ValidateObjectReference(
                    report,
                    spawner,
                    "m_SpawnRegistry",
                    registry,
                    "Fusion player spawner authority registry");
                ValidateNonNullObjectReference(
                    report,
                    spawner,
                    "m_PlayerPrefab",
                    "Fusion player spawner default prefab");
                ValidateNonEmptyObjectArray(
                    report,
                    spawner,
                    "m_SpawnPoints",
                    "Fusion player spawner spawn points");
            }
        }

        private static T RequireSingle<T>(FusionSetupReport report, string label)
            where T : Component
        {
            T[] components = FindSceneComponents<T>();
            if (components.Length == 0)
            {
                report.Add(
                    FusionSetupIssueSeverity.Error,
                    $"The applied setup is missing its required {label} component.");
                return null;
            }
            return components.Length == 1 ? components[0] : null;
        }

        private static Component RequireSingleType(
            FusionSetupReport report,
            string assemblyQualifiedTypeName,
            string label)
        {
            Type type = Type.GetType(assemblyQualifiedTypeName);
            if (type == null || !typeof(Component).IsAssignableFrom(type))
            {
                report.Add(
                    FusionSetupIssueSeverity.Error,
                    $"The required {label} runtime type is unavailable.");
                return null;
            }

            Component[] components = FindSceneComponents(type);
            if (components.Length == 0)
            {
                report.Add(
                    FusionSetupIssueSeverity.Error,
                    $"The applied setup is missing its required {label} component.");
                return null;
            }
            return components.Length == 1 ? components[0] : null;
        }

        private static void ValidateObjectReference(
            FusionSetupReport report,
            Component owner,
            string propertyName,
            UnityEngine.Object expected,
            string label)
        {
            if (owner == null || expected == null) return;
            var serialized = new SerializedObject(owner);
            SerializedProperty property = serialized.FindProperty(propertyName);
            if (property == null ||
                property.propertyType != SerializedPropertyType.ObjectReference ||
                property.objectReferenceValue != expected)
            {
                report.Add(
                    FusionSetupIssueSeverity.Error,
                    $"{label} is not wired to the required scene component.",
                    owner);
            }
        }

        private static void ValidateNonNullObjectReference(
            FusionSetupReport report,
            Component owner,
            string propertyName,
            string label)
        {
            if (owner == null) return;
            var serialized = new SerializedObject(owner);
            SerializedProperty property = serialized.FindProperty(propertyName);
            if (property == null ||
                property.propertyType != SerializedPropertyType.ObjectReference ||
                property.objectReferenceValue == null)
            {
                report.Add(
                    FusionSetupIssueSeverity.Error,
                    $"{label} is not assigned.",
                    owner);
            }
        }

        private static void ValidateNonEmptyObjectArray(
            FusionSetupReport report,
            Component owner,
            string propertyName,
            string label)
        {
            if (owner == null) return;
            var serialized = new SerializedObject(owner);
            SerializedProperty property = serialized.FindProperty(propertyName);
            bool valid = property != null && property.isArray && property.arraySize > 0;
            if (valid)
            {
                for (int i = 0; i < property.arraySize; i++)
                {
                    SerializedProperty element = property.GetArrayElementAtIndex(i);
                    if (element.propertyType != SerializedPropertyType.ObjectReference ||
                        element.objectReferenceValue == null)
                    {
                        valid = false;
                        break;
                    }
                }
            }

            if (!valid)
            {
                report.Add(
                    FusionSetupIssueSeverity.Error,
                    $"{label} must contain at least one valid reference.",
                    owner);
            }
        }

        private static void ValidateBoolean(
            FusionSetupReport report,
            Component owner,
            string propertyName,
            bool expected,
            string label)
        {
            if (owner == null) return;
            var serialized = new SerializedObject(owner);
            SerializedProperty property = serialized.FindProperty(propertyName);
            if (property == null ||
                property.propertyType != SerializedPropertyType.Boolean ||
                property.boolValue != expected)
            {
                report.Add(
                    FusionSetupIssueSeverity.Error,
                    $"{label} must be {(expected ? "enabled" : "disabled")}.",
                    owner);
            }
        }

        private static void ValidateModuleRegistrations(FusionSetupReport report)
        {
            ValidateObjectRegistrationArray(
                report,
                "Arawn.GameCreator2.Networking.Melee.Transport.Fusion." +
                "FusionMeleeTransportBridge, " +
                "Arawn.GameCreator2.Networking.Melee.Transport.Fusion",
                "m_RegisterWeapons",
                "Melee weapons");

            ValidateShooterRegistrations(report);

            const string abilitiesBridge =
                "Arawn.GameCreator2.Networking.Abilities.Transport.Fusion." +
                "FusionAbilitiesTransportBridge, " +
                "Arawn.GameCreator2.Networking.Abilities.Transport.Fusion";
            Type abilitiesType = Type.GetType(abilitiesBridge);
            if (abilitiesType != null)
            {
                foreach (Component bridge in FindSceneComponents(abilitiesType))
                {
                    if (!IsComponentActive(bridge)) continue;
                    var serialized = new SerializedObject(bridge);
                    int registered = 0;
                    registered += ValidateObjectArray(
                        report, bridge, serialized.FindProperty("m_RegisterAbilities"), "Ability");
                    registered += ValidateObjectArray(
                        report, bridge, serialized.FindProperty("m_RegisterProjectiles"), "Projectile");
                    registered += ValidateObjectArray(
                        report, bridge, serialized.FindProperty("m_RegisterImpacts"), "Impact");
                    if (registered == 0)
                    {
                        report.Add(
                            FusionSetupIssueSeverity.Warning,
                            "The Fusion Abilities bridge has no Ability, Projectile, or Impact " +
                            "asset registrations. Runtime state can only resolve assets registered " +
                            "elsewhere.",
                            bridge);
                    }
                }
            }
        }

        private static void ValidateObjectRegistrationArray(
            FusionSetupReport report,
            string typeName,
            string propertyName,
            string label)
        {
            Type type = Type.GetType(typeName);
            if (type == null) return;

            foreach (Component bridge in FindSceneComponents(type))
            {
                if (!IsComponentActive(bridge)) continue;
                var serialized = new SerializedObject(bridge);
                int count = ValidateObjectArray(
                    report,
                    bridge,
                    serialized.FindProperty(propertyName),
                    label);
                if (count == 0)
                {
                    report.Add(
                        FusionSetupIssueSeverity.Warning,
                        $"The Fusion bridge has no explicit {label} registrations. Assets " +
                        "discovered from spawned character controllers remain available, but " +
                        "late-join snapshots cannot resolve any other assets.",
                        bridge);
                }
            }
        }

        private static int ValidateObjectArray(
            FusionSetupReport report,
            Component owner,
            SerializedProperty array,
            string label)
        {
            if (array == null || !array.isArray) return 0;

            int nonNull = 0;
            var assets = new HashSet<UnityEngine.Object>();
            var hashes = new Dictionary<int, UnityEngine.Object>();
            for (int i = 0; i < array.arraySize; i++)
            {
                UnityEngine.Object asset =
                    array.GetArrayElementAtIndex(i).objectReferenceValue;
                if (asset == null) continue;
                nonNull++;

                if (!assets.Add(asset))
                {
                    report.Add(
                        FusionSetupIssueSeverity.Error,
                        $"{label} contains the duplicate asset '{asset.name}'.",
                        owner);
                    continue;
                }

                if (!TryGetAssetHash(asset, out int hash)) continue;
                if (hashes.TryGetValue(hash, out UnityEngine.Object existing) &&
                    existing != asset)
                {
                    report.Add(
                        FusionSetupIssueSeverity.Error,
                        $"{label} assets '{existing.name}' and '{asset.name}' share network " +
                        $"hash {hash}. Assign unique GC2 asset IDs.",
                        owner);
                }
                else
                {
                    hashes[hash] = asset;
                }
            }
            return nonNull;
        }

        private static void ValidateShooterRegistrations(FusionSetupReport report)
        {
            const string shooterBridge =
                "Arawn.GameCreator2.Networking.Shooter.Transport.Fusion." +
                "FusionShooterTransportBridge, " +
                "Arawn.GameCreator2.Networking.Shooter.Transport.Fusion";
            Type type = Type.GetType(shooterBridge);
            if (type == null) return;

            foreach (Component bridge in FindSceneComponents(type))
            {
                if (!IsComponentActive(bridge)) continue;
                var serialized = new SerializedObject(bridge);
                SerializedProperty registrations =
                    serialized.FindProperty("m_WeaponRegistrations");
                int valid = 0;
                var assets = new HashSet<UnityEngine.Object>();
                var hashes = new Dictionary<int, UnityEngine.Object>();

                if (registrations != null && registrations.isArray)
                {
                    for (int i = 0; i < registrations.arraySize; i++)
                    {
                        SerializedProperty entry = registrations.GetArrayElementAtIndex(i);
                        UnityEngine.Object weapon =
                            entry.FindPropertyRelative("Weapon")?.objectReferenceValue;
                        UnityEngine.Object model =
                            entry.FindPropertyRelative("ModelPrefab")?.objectReferenceValue;
                        if (weapon == null)
                        {
                            if (model != null)
                            {
                                report.Add(
                                    FusionSetupIssueSeverity.Error,
                                    $"Shooter registration {i} assigns model '{model.name}' " +
                                    "without a ShooterWeapon.",
                                    bridge);
                            }
                            continue;
                        }

                        valid++;
                        if (!assets.Add(weapon))
                        {
                            report.Add(
                                FusionSetupIssueSeverity.Error,
                                $"Shooter weapon '{weapon.name}' is registered more than once.",
                                bridge);
                        }

                        if (TryGetAssetHash(weapon, out int hash))
                        {
                            if (hashes.TryGetValue(hash, out UnityEngine.Object existing) &&
                                existing != weapon)
                            {
                                report.Add(
                                    FusionSetupIssueSeverity.Error,
                                    $"Shooter weapons '{existing.name}' and '{weapon.name}' " +
                                    $"share network hash {hash}.",
                                    bridge);
                            }
                            else
                            {
                                hashes[hash] = weapon;
                            }
                        }

                        if (model == null)
                        {
                            report.Add(
                                FusionSetupIssueSeverity.Warning,
                                $"Shooter weapon '{weapon.name}' has no replicated model prefab " +
                                "mapping. Remote equip visuals may be unavailable.",
                                bridge);
                        }
                    }
                }

                if (valid == 0)
                {
                    report.Add(
                        FusionSetupIssueSeverity.Warning,
                        "The Fusion Shooter bridge has no weapon registrations. Configure " +
                        "ShooterWeapon, model prefab, and handle mappings before using weapons.",
                        bridge);
                }
            }
        }

        private static void ValidateInventoryRuntimePickups(FusionSetupReport report)
        {
            const string pickupSourceName =
                "Arawn.GameCreator2.Networking.Inventory.NetworkInventoryPickupSource, " +
                "Arawn.GameCreator2.Networking.Inventory";
            const string adapterName =
                "Arawn.GameCreator2.Networking.Inventory.Transport.Fusion." +
                "FusionInventoryRuntimePickupIdentityAdapter, " +
                "Arawn.GameCreator2.Networking.Inventory.Transport.Fusion";

            Type pickupSourceType = Type.GetType(pickupSourceName);
            Type adapterType = Type.GetType(adapterName);
            if (pickupSourceType == null) return;

            foreach (Component source in FindSceneComponents(pickupSourceType))
            {
                if (!IsComponentActive(source)) continue;
                Component adapter =
                    adapterType != null ? source.GetComponent(adapterType) : null;
                NetworkObject networkObject = source.GetComponent<NetworkObject>();

                foreach (MonoBehaviour behaviour in source.GetComponents<MonoBehaviour>())
                {
                    if (behaviour == null || !behaviour.isActiveAndEnabled) continue;
                    string fullName = behaviour.GetType().FullName ?? string.Empty;
                    if (fullName.Contains(
                            "PurrNetInventoryRuntimePickupIdentityAdapter",
                            StringComparison.Ordinal))
                    {
                        report.Add(
                            FusionSetupIssueSeverity.Error,
                            $"Inventory pickup '{source.name}' has an active PurrNet runtime " +
                            "identity adapter.",
                            behaviour);
                    }
                }

                if (networkObject == null && adapter == null) continue;
                if (networkObject == null || adapter == null)
                {
                    report.Add(
                        FusionSetupIssueSeverity.Error,
                        $"Runtime Inventory pickup '{source.name}' requires both a Fusion " +
                        "NetworkObject and FusionInventoryRuntimePickupIdentityAdapter.",
                        source);
                    continue;
                }

                FusionNetworkIdentity identity =
                    source.GetComponent<FusionNetworkIdentity>();
                if (identity == null)
                {
                    report.Add(
                        FusionSetupIssueSeverity.Error,
                        $"Runtime Inventory pickup '{source.name}' has no " +
                        "FusionNetworkIdentity.",
                        source);
                }

                NetworkObjectFlags flags = networkObject.Flags;
                if ((flags & NetworkObjectFlags.MasterClientObject) == 0 ||
                    (flags & NetworkObjectFlags.AllowStateAuthorityOverride) != 0 ||
                    (flags & NetworkObjectFlags.DestroyWhenStateAuthorityLeaves) != 0)
                {
                    report.Add(
                        FusionSetupIssueSeverity.Error,
                        $"Runtime Inventory pickup '{source.name}' must be a persistent, " +
                        "non-overridable Master Client Object.",
                        source);
                }
            }
        }

        private static bool TryGetAssetHash(UnityEngine.Object asset, out int hash)
        {
            hash = 0;
            if (asset == null) return false;
            try
            {
                const BindingFlags flags =
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                object id = asset.GetType().GetProperty("Id", flags)?.GetValue(asset) ??
                            asset.GetType().GetField("Id", flags)?.GetValue(asset);
                if (id == null) return false;
                object value = id.GetType().GetProperty("Hash", flags)?.GetValue(id) ??
                               id.GetType().GetField("Hash", flags)?.GetValue(id);
                if (value is int intHash)
                {
                    hash = intHash;
                    return true;
                }
            }
            catch
            {
                // Optional packages can expose a different ID shape; duplicate object
                // references are still validated even when no stable hash is readable.
            }
            return false;
        }

        private static void ValidatePlayerPrefab(
            FusionSetupReport report,
            GameObject playerPrefab,
            NetworkPredictionBackend? expectedBackend)
        {
            if (playerPrefab == null)
            {
                report.Add(
                    FusionSetupIssueSeverity.Error,
                    "No player prefab is assigned. The centralized Host/Shared authority " +
                    "spawner requires at least one prepared player prefab.");
                return;
            }

            string path = AssetDatabase.GetAssetPath(playerPrefab);
            if (string.IsNullOrEmpty(path) ||
                !path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            {
                report.Add(
                    FusionSetupIssueSeverity.Error,
                    "The selected player must be a prefab asset.",
                    playerPrefab);
                return;
            }

            if (!path.StartsWith("Assets/", StringComparison.Ordinal))
            {
                report.Add(
                    FusionSetupIssueSeverity.Error,
                    "The selected player prefab must be an editable project asset under " +
                    "Assets/. Package prefabs cannot be prepared or included in setup Undo.",
                    playerPrefab);
                return;
            }

            foreach (Component component in playerPrefab.GetComponentsInChildren<Component>(true))
            {
                if (component == null || !IsComponentActive(component)) continue;

                Type type = component.GetType();
                string typeNamespace = type.Namespace ?? string.Empty;
                string fullName = type.FullName ?? type.Name;

                if (typeNamespace.StartsWith(
                        "NinjutsuGames.FusionNetwork",
                        StringComparison.Ordinal))
                {
                    report.Add(
                        FusionSetupIssueSeverity.Error,
                        $"Player prefab contains a Ninjutsu networking component: {fullName}.",
                        playerPrefab);
                }

                if (typeNamespace.Contains(".Transport.PurrNet", StringComparison.Ordinal) ||
                    fullName == "PurrNet.NetworkIdentity")
                {
                    report.Add(
                        FusionSetupIssueSeverity.Error,
                        $"Player prefab contains a PurrNet networking component: {fullName}.",
                        playerPrefab);
                }

                if (IsCompetingMovementComponent(component))
                {
                    report.Add(
                        FusionSetupIssueSeverity.Error,
                        $"Player prefab contains a competing Fusion movement synchronizer: {fullName}.",
                        playerPrefab);
                }
            }

            NetworkObject networkObject = playerPrefab.GetComponent<NetworkObject>();
            if (networkObject == null)
            {
                report.Add(
                    FusionSetupIssueSeverity.Warning,
                    "The player prefab has no Fusion NetworkObject. The wizard will add it.",
                    playerPrefab);
            }
            else
            {
                NetworkObjectFlags flags = networkObject.Flags;
                if ((flags & NetworkObjectFlags.MasterClientObject) == 0 ||
                    (flags & NetworkObjectFlags.AllowStateAuthorityOverride) != 0 ||
                    (flags & NetworkObjectFlags.DestroyWhenStateAuthorityLeaves) != 0)
                {
                    report.Add(
                        FusionSetupIssueSeverity.Warning,
                        "The player prefab is not configured as a persistent, non-overridable " +
                        "Master Client Object. The wizard will correct its authority flags.",
                        playerPrefab);
                }

                if (!networkObject.EnableInterpolation)
                {
                    report.Add(
                        FusionSetupIssueSeverity.Warning,
                        "The Fusion player prefab has NetworkObject interpolation disabled. " +
                        "The wizard will enable it for Fusion-native character presentation.",
                        playerPrefab);
                }

                if (!NetworkProjectConfigUtilities.TryGetPrefabId(path, out _))
                {
                    report.Add(
                        FusionSetupIssueSeverity.Warning,
                        "The player prefab is not registered in the Fusion prefab table. " +
                        "The wizard will label it and rebuild the table.",
                        playerPrefab);
                }
            }

            NetworkCharacter networkCharacter = playerPrefab.GetComponent<NetworkCharacter>();
            bool expectsNativeMotor = expectedBackend == NetworkPredictionBackend.FusionNative;
            if (networkCharacter != null)
            {
                var serialized = new SerializedObject(networkCharacter);
                SerializedProperty authorityOwnerPrediction = serialized.FindProperty(
                    "m_HostOwnerUsesClientPrediction");
                if (authorityOwnerPrediction != null && !authorityOwnerPrediction.boolValue)
                {
                    report.Add(
                        FusionSetupIssueSeverity.Warning,
                        "The Fusion player prefab has authority-owner prediction disabled. " +
                        "A Host-owned GC2 character can rotate but cannot consume directional " +
                        "movement input. The wizard will enable it.",
                        playerPrefab);
                }


                SerializedProperty backend = serialized.FindProperty("m_PredictionBackend");
                NetworkPredictionBackend serializedBackend = backend != null
                    ? (NetworkPredictionBackend)backend.enumValueIndex
                    : NetworkPredictionBackend.BuiltIn;
                NetworkPredictionBackend desiredBackend =
                    expectedBackend ?? serializedBackend;
                expectsNativeMotor = desiredBackend == NetworkPredictionBackend.FusionNative;
                if (backend == null || serializedBackend != desiredBackend)
                {
                    report.Add(
                        FusionSetupIssueSeverity.Warning,
                        $"The Fusion player prefab uses {serializedBackend}, but the current " +
                        $"wizard selection is {desiredBackend}. The wizard will update it.",
                        playerPrefab);
                }
            }

            FusionNativeNetworkCharacterMotor nativeMotor =
                playerPrefab.GetComponent<FusionNativeNetworkCharacterMotor>();
            if (expectsNativeMotor && (nativeMotor == null || !nativeMotor.enabled))
            {
                report.Add(
                    FusionSetupIssueSeverity.Warning,
                    "The Fusion player prefab has no enabled " +
                    "FusionNativeNetworkCharacterMotor. The wizard will add it.",
                    playerPrefab);
            }
            else if (!expectsNativeMotor && nativeMotor != null)
            {
                report.Add(
                    FusionSetupIssueSeverity.Warning,
                    "The player prefab uses Built-in Legacy movement but still contains a " +
                    "FusionNativeNetworkCharacterMotor. The wizard will remove the inactive " +
                    "native TRSP so it cannot compete for Fusion's main transform slot.",
                    playerPrefab);
            }

            if (expectsNativeMotor && nativeMotor != null && networkObject != null)
            {
                Transform configuredPresentationRoot =
                    nativeMotor.ListenHostPresentationVisualRoot;
                Transform mannequin = playerPrefab.GetComponent<Character>()?
                    .Animim?.Mannequin;
                Transform effectivePresentationRoot = configuredPresentationRoot != null
                    ? configuredPresentationRoot
                    : mannequin;
                if (!FusionNativeNetworkCharacterMotor.IsSafePresentationVisualRoot(
                        playerPrefab.transform,
                        effectivePresentationRoot))
                {
                    report.Add(
                        FusionSetupIssueSeverity.Warning,
                        "Fusion Native cannot safely interpolate this prefab's visuals on a " +
                        "listen host because no visual-only direct child is configured. The " +
                        "authoritative root will remain correct but remote host presentation " +
                        "will be tick-stepped until a safe GC2 Mannequin is assigned.",
                        playerPrefab);
                }

                if ((networkObject.Flags & NetworkObjectFlags.HasMainNetworkTRSP) == 0)
                {
                    report.Add(
                        FusionSetupIssueSeverity.Warning,
                        "The Fusion-native motor has not been baked as the NetworkObject's " +
                        "main NetworkTRSP. The wizard will rebuild the prefab metadata.",
                        playerPrefab);
                }

                NetworkBehaviour firstBehaviour =
                    networkObject.NetworkedBehaviours?.FirstOrDefault();
                if (firstBehaviour != nativeMotor)
                {
                    report.Add(
                        FusionSetupIssueSeverity.Warning,
                        "FusionNativeNetworkCharacterMotor is not the prefab's first baked " +
                        "NetworkBehaviour/main TRSP. Rebuild the Fusion prefab table before play.",
                        playerPrefab);
                }
            }
        }

        private static bool IsCompetingMovementComponent(Component component)
        {
            if (component == null) return false;
            if (component is NetworkTRSP &&
                component is not FusionNativeNetworkCharacterMotor)
            {
                return true;
            }

            Type type = component.GetType();
            string typeNamespace = type.Namespace ?? string.Empty;
            return typeNamespace.StartsWith("Fusion", StringComparison.Ordinal) &&
                   (type.Name.Contains("NetworkTransform", StringComparison.Ordinal) ||
                    type.Name.Contains("KCC", StringComparison.OrdinalIgnoreCase) ||
                    type.Name.Contains("NetworkRigidbody", StringComparison.Ordinal) ||
                    type.Name.Contains("NetworkCharacterController", StringComparison.Ordinal) ||
                    type.Name.Contains("NetworkTRSP", StringComparison.Ordinal) ||
                    type.Name.Contains("NetworkMecanimAnimator", StringComparison.Ordinal));
        }

        private static void ValidateDuplicate<T>(
            FusionSetupReport report,
            string label)
            where T : Component
        {
            int count = FindSceneComponents<T>().Length;
            if (count <= 1) return;
            report.Add(
                FusionSetupIssueSeverity.Error,
                $"The active scene contains {count} {label} components. Keep exactly one.");
        }

        private static void ValidateDuplicateType(
            FusionSetupReport report,
            string assemblyQualifiedTypeName,
            string label)
        {
            Type type = Type.GetType(assemblyQualifiedTypeName);
            if (type == null || !typeof(Component).IsAssignableFrom(type)) return;

#if UNITY_2023_1_OR_NEWER
            UnityEngine.Object[] objects = UnityEngine.Object.FindObjectsByType(
                type,
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);
#else
            UnityEngine.Object[] objects = UnityEngine.Object.FindObjectsOfType(type, true);
#endif
            Scene activeScene = SceneManager.GetActiveScene();
            int count = objects
                .OfType<Component>()
                .Count(component =>
                    component != null &&
                    component.gameObject.scene.IsValid() &&
                    component.gameObject.scene == activeScene);
            if (count <= 1) return;

            report.Add(
                FusionSetupIssueSeverity.Error,
                $"The active scene contains {count} {label} components. Keep exactly one.");
        }

        private static bool IsComponentActive(Component component)
        {
            if (component == null) return false;
            if (component is UnityEngine.Behaviour behaviour && !behaviour.enabled) return false;

            Transform current = component.transform;
            while (current != null)
            {
                if (!current.gameObject.activeSelf) return false;
                current = current.parent;
            }

            return true;
        }

        internal static T[] FindSceneComponents<T>() where T : Component
        {
#if UNITY_2023_1_OR_NEWER
            T[] components = UnityEngine.Object.FindObjectsByType<T>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);
#else
            T[] components = UnityEngine.Object.FindObjectsOfType<T>(true);
#endif
            if (components == null || components.Length == 0) return Array.Empty<T>();

            Scene activeScene = SceneManager.GetActiveScene();
            return components
                .Where(component =>
                    component != null &&
                    component.gameObject.scene.IsValid() &&
                    component.gameObject.scene == activeScene)
                .ToArray();
        }

        private static Component[] FindSceneComponents(Type type)
        {
            if (type == null || !typeof(Component).IsAssignableFrom(type))
                return Array.Empty<Component>();

#if UNITY_2023_1_OR_NEWER
            UnityEngine.Object[] objects = UnityEngine.Object.FindObjectsByType(
                type,
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);
#else
            UnityEngine.Object[] objects = UnityEngine.Object.FindObjectsOfType(type, true);
#endif
            Scene activeScene = SceneManager.GetActiveScene();
            return objects
                .OfType<Component>()
                .Where(component =>
                    component != null &&
                    component.gameObject.scene.IsValid() &&
                    component.gameObject.scene == activeScene)
                .ToArray();
        }
    }
}
