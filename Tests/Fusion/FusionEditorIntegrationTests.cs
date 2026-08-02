using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Arawn.GameCreator2.Networking.Editor;
using Fusion;
using NUnit.Framework;
using Assert = NUnit.Framework.Assert;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Arawn.GameCreator2.Networking.Transport.Fusion.Tests
{
    public sealed class FusionEditorIntegrationTests
    {
        private const BindingFlags StaticNonPublic =
            BindingFlags.Static | BindingFlags.NonPublic;

        private const string FusionEditorAssembly =
            "Arawn.GameCreator2.Networking.Transport.Fusion.Editor";

        [Test]
        public void FusionOnlyTransportDirectory_SuppressesGenericWizard()
        {
            MethodInfo hasTransportSubdirectory = typeof(GC2NetworkingDefineSymbols).GetMethod(
                "HasTransportSubdirectory",
                StaticNonPublic);

            Assert.NotNull(hasTransportSubdirectory);

            string temporaryRoot = Path.Combine(
                Path.GetTempPath(),
                $"Arawn-GC2-FusionOnly-{Guid.NewGuid():N}");
            string fusionDirectory = Path.Combine(temporaryRoot, "Fusion");

            try
            {
                Directory.CreateDirectory(fusionDirectory);
                File.WriteAllText(Path.Combine(fusionDirectory, "transport.marker"), "Fusion");

                CollectionAssert.AreEquivalent(
                    new[] { "Fusion" },
                    Directory.EnumerateDirectories(temporaryRoot).Select(Path.GetFileName));
                Assert.IsTrue((bool)hasTransportSubdirectory.Invoke(
                    null,
                    new object[] { temporaryRoot }));
            }
            finally
            {
                if (Directory.Exists(temporaryRoot))
                {
                    Directory.Delete(temporaryRoot, true);
                }
            }

            string defineSource = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Editor/DefineSymbols/" +
                "GC2NetworkingDefineSymbols.cs");
            StringAssert.Contains(
                "IsNamespacePresentCached(\"Arawn.GameCreator2.Networking.Transport.Fusion\")",
                defineSource);

            string genericWizardSource = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Editor/GameCreator2NetworkingSetupWizard.cs");
            StringAssert.Contains(
                "#if !ARAWN_GC2_TRANSPORT_INTEGRATION",
                genericWizardSource);
            StringAssert.Contains(
                "Game Creator/Networking Layer/Scene Setup Wizard",
                genericWizardSource);
        }

        [Test]
        public void AllArawnFusionAssemblyDefinitions_AreTransportIndependent()
        {
            string networkingRoot = Path.Combine(
                Application.dataPath,
                "Arawn/NetworkingLayerForGC2");

            string[] fusionAssemblyDefinitions = Directory
                .EnumerateFiles(networkingRoot, "*.asmdef", SearchOption.AllDirectories)
                .Where(path =>
                {
                    string normalized = path.Replace('\\', '/');
                    return normalized.Contains("/Transport/Fusion/", StringComparison.Ordinal) ||
                           normalized.Contains("/Editor/Transport/Fusion/", StringComparison.Ordinal);
                })
                .ToArray();

            Assert.IsNotEmpty(fusionAssemblyDefinitions);
            foreach (string path in fusionAssemblyDefinitions)
            {
                string source = File.ReadAllText(path);
                Assert.IsFalse(
                    source.Contains("Ninjutsu", StringComparison.OrdinalIgnoreCase),
                    $"Fusion assembly definition references Ninjutsu: {path}");
                Assert.IsFalse(
                    source.Contains("PurrNet", StringComparison.OrdinalIgnoreCase),
                    $"Fusion assembly definition references PurrNet: {path}");
            }
        }

        [Test]
        public void RpcRouter_UsesImmediateNonLocalStaticRoutesAndLargeReliableChannels()
        {
            MethodInfo[] routes = typeof(FusionRpcRouter)
                .GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
                .Where(method => method.Name.StartsWith("RPC_", StringComparison.Ordinal))
                .Where(method => method.CustomAttributes.Any(attribute =>
                    attribute.AttributeType.FullName == "Fusion.RpcAttribute"))
                // Fusion's IL weaver emits a generated companion method for each RPC.
                // Assert the six public protocol routes by name, then inspect the authored
                // method in each group that retains RpcAttribute.
                .GroupBy(method => method.Name, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToArray();

            Assert.AreEqual(6, routes.Length);

            int reliableLargeRouteCount = 0;
            foreach (MethodInfo route in routes)
            {
                CustomAttributeData rpc = route.CustomAttributes.SingleOrDefault(attribute =>
                    attribute.AttributeType.FullName == "Fusion.RpcAttribute");
                Assert.NotNull(rpc, $"{route.Name} has no RpcAttribute.");

                Assert.AreEqual(false, GetNamedAttributeValue<bool>(rpc, "InvokeLocal"));
                Assert.AreEqual(false, GetNamedAttributeValue<bool>(rpc, "TickAligned"));

                string channel = GetNamedAttributeValueAsString(rpc, "Channel");
                if (channel == "ReliableLargeData")
                {
                    reliableLargeRouteCount++;
                }
            }

            Assert.AreEqual(2, reliableLargeRouteCount);
            Assert.AreEqual(384, FusionProtocol.RpcPayloadLimit);

            string routerSource = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Runtime/Transport/Fusion/FusionRpcRouter.cs");
            StringAssert.Contains(
                "RPC_ToAuthorityLarge(runner, target, packet)",
                routerSource);
            StringAssert.Contains(
                "RPC_FromAuthorityLarge(runner, target, packet)",
                routerSource);

            string bridgeSource = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Runtime/Transport/Fusion/" +
                "FusionTransportBridge.cs");
            StringAssert.Contains(
                "!largeData && packet.Length > FusionProtocol.RpcPayloadLimit",
                bridgeSource);
        }

        [Test]
        public void TransportWizards_UseSharedCoreAndCharacterPreparation()
        {
            string sharedSource = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Editor/GC2SceneSetupShared.cs");
            StringAssert.Contains("EnsureCoreManagers(", sharedSource);
            StringAssert.Contains("ConfigureNetworkReadyCharacterKernel(", sharedSource);

            string fusionSource = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Editor/Transport/Fusion/" +
                "FusionSceneSetupWizard.cs");
            string purrNetSource = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Editor/Transport/PurrNet/" +
                "PurrNetSceneSetupWizard.cs");

            StringAssert.Contains(
                "GC2SceneSetupShared.EnsureCoreManagers(",
                fusionSource);
            StringAssert.Contains(
                "GC2SceneSetupShared.EnsureCoreManagers(",
                purrNetSource);
            StringAssert.Contains(
                "GC2SceneSetupShared.ConfigureNetworkReadyCharacterKernel(",
                fusionSource);
            StringAssert.Contains(
                "GC2SceneSetupShared.ConfigureNetworkReadyCharacterKernel(",
                purrNetSource);
        }

        [Test]
        public void FusionWizard_RegistersExpectedMenuAndSixPageWorkflow()
        {
            Type wizardType = RequireEditorType(
                "Arawn.GameCreator2.Networking.Transport.Fusion.Editor.FusionSceneSetupWizard");
            MethodInfo open = wizardType.GetMethod(
                "Open",
                BindingFlags.Static | BindingFlags.Public);
            Assert.NotNull(open);

            CustomAttributeData menu = open.CustomAttributes.SingleOrDefault(attribute =>
                attribute.AttributeType == typeof(MenuItem));
            Assert.NotNull(menu);
            Assert.AreEqual(
                "Game Creator/Networking Layer/Fusion Scene Setup Wizard",
                menu.ConstructorArguments[0].Value);

            Type pageType = wizardType.GetNestedType("WizardPage", BindingFlags.NonPublic);
            Assert.NotNull(pageType);
            CollectionAssert.AreEqual(
                new[]
                {
                    "ProjectShape",
                    "Modules",
                    "FusionSession",
                    "Infrastructure",
                    "SpawningAndUI",
                    "Review"
                },
                Enum.GetNames(pageType));

            MethodInfo pageTitle = wizardType.GetMethod("PageTitle", StaticNonPublic);
            Assert.NotNull(pageTitle);
            CollectionAssert.AreEqual(
                new[]
                {
                    "Project Shape",
                    "GC2 Modules",
                    "Fusion Session",
                    "Core Infrastructure",
                    "Spawning and UI",
                    "Review"
                },
                Enum.GetValues(pageType)
                    .Cast<object>()
                    .Select(value => (string)pageTitle.Invoke(null, new[] { value }))
                    .ToArray());
        }

        [Test]
        public void FusionWizard_ExposesApplyOnlyFromReviewNavigation()
        {
            string wizardSource = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Editor/Transport/Fusion/" +
                "FusionSceneSetupWizard.cs");
            string navigation = ExtractMethodBody(wizardSource, "private void DrawNavigation()");

            Assert.AreEqual(
                1,
                CountOccurrences(wizardSource, "Create / Update Scene Setup"),
                "The Apply action must have exactly one UI entry point.");

            int reviewBranch = navigation.IndexOf(
                "if (m_Page != WizardPage.Review)",
                StringComparison.Ordinal);
            int applyButton = navigation.IndexOf(
                "Create / Update Scene Setup",
                StringComparison.Ordinal);

            Assert.GreaterOrEqual(reviewBranch, 0);
            Assert.Greater(applyButton, reviewBranch);
            StringAssert.Contains("else", navigation.Substring(reviewBranch, applyButton - reviewBranch));
            StringAssert.Contains("RunSetup();", navigation.Substring(applyButton));
        }

        [Test]
        public void SharedAuthoritySources_RequireMasterOwnershipAndLogicalAdmission()
        {
            string registrySource = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Runtime/Transport/Fusion/" +
                "FusionAuthoritySpawnRegistry.cs");
            StringAssert.Contains(
                "NetworkSpawnFlags.SharedModeStateAuthMasterClient",
                registrySource);
            StringAssert.Contains(
                "(flags & NetworkObjectFlags.MasterClientObject) != 0",
                registrySource);
            StringAssert.Contains(
                "(flags & NetworkObjectFlags.AllowStateAuthorityOverride) == 0",
                registrySource);
            StringAssert.Contains(
                "(flags & NetworkObjectFlags.DestroyWhenStateAuthorityLeaves) == 0",
                registrySource);
            StringAssert.Contains("identity.HasAuthorityAdmission", registrySource);

            PropertyInfo logicalOwner = typeof(FusionNetworkIdentity).GetProperty("LogicalOwner");
            PropertyInfo admission = typeof(FusionNetworkIdentity).GetProperty("AuthorityAdmitted");
            Assert.NotNull(logicalOwner);
            Assert.NotNull(admission);
            Assert.IsTrue(logicalOwner.CustomAttributes.Any(attribute =>
                attribute.AttributeType.FullName == "Fusion.NetworkedAttribute"));
            Assert.IsTrue(admission.CustomAttributes.Any(attribute =>
                attribute.AttributeType.FullName == "Fusion.NetworkedAttribute"));
            Assert.NotNull(admission.SetMethod);
            Assert.IsTrue(admission.SetMethod.IsPrivate);

            string spawnerSource = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Runtime/Transport/Fusion/FusionPlayerSpawner.cs");
            StringAssert.Contains("identity.LogicalOwner == player", spawnerSource);
            StringAssert.Contains("identity.TransportAdmitted", spawnerSource);
            StringAssert.Contains("m_SpawnRegistry.IsAdmitted(identity)", spawnerSource);

            string fusionRuntimeRoot = Path.Combine(
                Application.dataPath,
                "Arawn/NetworkingLayerForGC2/Runtime/Transport/Fusion");
            foreach (string path in Directory.EnumerateFiles(
                         fusionRuntimeRoot,
                         "*.cs",
                         SearchOption.AllDirectories))
            {
                StringAssert.DoesNotContain(
                    "RequestStateAuthority",
                    File.ReadAllText(path),
                    $"Gameplay authority must not use Shared State Authority requests: {path}");
            }
        }

        [Test]
        public void SetupValidation_CountsConflictsOnlyInActiveScene()
        {
            Scene originalActiveScene = SceneManager.GetActiveScene();
            if (string.IsNullOrEmpty(originalActiveScene.path))
            {
                Assert.Ignore(
                    "The active Editor scene is untitled. Unity cannot open additive test " +
                    "scenes without risking the user's unsaved scene; rerun with a saved scene active.");
            }

            Scene activeTestScene = default;
            Scene foreignTestScene = default;
            string testId = Guid.NewGuid().ToString("N");
            string activeScenePath =
                $"Assets/Arawn/NetworkingLayerForGC2/Tests/Fusion/" +
                $"__FusionValidationActive-{testId}.unity";
            string foreignScenePath =
                $"Assets/Arawn/NetworkingLayerForGC2/Tests/Fusion/" +
                $"__FusionValidationForeign-{testId}.unity";

            try
            {
                activeTestScene = EditorSceneManager.NewScene(
                    NewSceneSetup.EmptyScene,
                    NewSceneMode.Additive);
                Assert.IsTrue(EditorSceneManager.SaveScene(
                    activeTestScene,
                    activeScenePath));
                foreignTestScene = EditorSceneManager.NewScene(
                    NewSceneSetup.EmptyScene,
                    NewSceneMode.Additive);
                Assert.IsTrue(EditorSceneManager.SaveScene(
                    foreignTestScene,
                    foreignScenePath));

                CreateBootstrap(activeTestScene, "Active Bootstrap A");
                CreateBootstrap(activeTestScene, "Active Bootstrap B");
                CreateBootstrap(foreignTestScene, "Foreign Bootstrap A");
                CreateBootstrap(foreignTestScene, "Foreign Bootstrap B");

                Assert.IsTrue(SceneManager.SetActiveScene(activeTestScene));

                Type validationType = RequireEditorType(
                    "Arawn.GameCreator2.Networking.Transport.Fusion.Editor." +
                    "FusionSceneSetupValidation");
                MethodInfo validate = validationType.GetMethod(
                    "Validate",
                    BindingFlags.Static | BindingFlags.Public);
                Assert.NotNull(validate);

                object report = validate.Invoke(null, new object[] { null });
                string[] messages = ReadIssueMessages(report);

                Assert.IsTrue(
                    messages.Any(message => message.Contains(
                        "active scene contains 2 FusionSessionBootstrap",
                        StringComparison.Ordinal)),
                    string.Join(Environment.NewLine, messages));
                Assert.IsFalse(messages.Any(message => message.Contains(
                    "active scene contains 4 FusionSessionBootstrap",
                    StringComparison.Ordinal)));
            }
            finally
            {
                if (originalActiveScene.IsValid() && originalActiveScene.isLoaded)
                {
                    SceneManager.SetActiveScene(originalActiveScene);
                }

                CloseTemporaryScene(activeTestScene);
                CloseTemporaryScene(foreignTestScene);
                AssetDatabase.DeleteAsset(activeScenePath);
                AssetDatabase.DeleteAsset(foreignScenePath);
            }
        }

        [Test]
        public void FullSnapshotPipeline_RequiresExplicitProducersAndFailsClosed()
        {
            string contractSource = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Runtime/Transport/Fusion/" +
                "IFusionFullSnapshotProducer.cs");
            StringAssert.Contains("public interface IFusionFullSnapshotProducer", contractSource);
            StringAssert.Contains("internal void RecordDelivery", contractSource);
            StringAssert.Contains("m_DeliveryFailed = true;", contractSource);
            StringAssert.Contains("public FusionFullSnapshotResult Complete()", contractSource);
            StringAssert.Contains("public FusionFullSnapshotResult Fail(string reason)", contractSource);

            string transportSource = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Runtime/Transport/Fusion/" +
                "FusionTransportBridge.cs");
            StringAssert.Contains(
                "Dictionary<ushort, IFusionFullSnapshotProducer> m_SnapshotProducers",
                transportSource);
            StringAssert.Contains(
                "m_ActiveSnapshotContext?.RecordDelivery(moduleId, clientId, delivered);",
                transportSource);

            string beginSnapshot = ExtractMethodBody(
                transportSource,
                "private void BeginClientSnapshot(uint clientId, bool forceSnapshot)");
            AssertAppearsBefore(
                beginSnapshot,
                "TryProduceFullSnapshots(clientId, out string snapshotFailure)",
                "ShutdownSessionForAuthorityFailure(");
            AssertAppearsBefore(
                beginSnapshot,
                "ShutdownSessionForAuthorityFailure(",
                "FusionTransportMessageType.SnapshotComplete");
            StringAssert.Contains("m_PendingSnapshotTokens.Remove(clientId);", beginSnapshot);

            string produceSnapshots = ExtractMethodBody(
                transportSource,
                "private bool TryProduceFullSnapshots(uint clientId, out string failureReason)");
            StringAssert.Contains("!m_ModuleHandlers.ContainsKey(moduleId)", produceSnapshots);
            StringAssert.Contains("!result.IsComplete", produceSnapshots);
            StringAssert.Contains(
                "result.PacketsEnqueued != context.PacketsEnqueued",
                produceSnapshots);
            StringAssert.Contains("return false;", produceSnapshots);

            string moduleSupportSource = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Runtime/Transport/Fusion/Core/" +
                "FusionModuleSupport.cs");
            StringAssert.Contains("IFusionFullSnapshotProducer", moduleSupportSource);
            StringAssert.Contains(
                "RegisterFullSnapshotProducer(this)",
                moduleSupportSource);
            StringAssert.Contains(
                "UnregisterFullSnapshotProducer(this)",
                moduleSupportSource);

            string[] baseProducerPaths =
            {
                "Core/FusionCoreTransportBridge.cs",
                "Variables/FusionVariableTransportBridge.cs",
                "AnimationMotion/FusionAnimationMotionTransportBridge.cs",
                "Stats/FusionStatsTransportBridge.cs",
                "Inventory/FusionInventoryTransportBridge.cs"
            };
            foreach (string relativePath in baseProducerPaths)
            {
                string source = ReadAssetSource(
                    "Arawn/NetworkingLayerForGC2/Runtime/Transport/Fusion/" + relativePath);
                StringAssert.Contains(
                    "ProduceFullSnapshotForClient(",
                    source,
                    $"Mandatory snapshot producer is missing: {relativePath}");
            }

            string[] directProducerPaths =
            {
                "Melee/FusionMeleeTransportBridge.cs",
                "Shooter/FusionShooterTransportBridge.cs",
                "Quests/FusionQuestsTransportBridge.cs",
                "Dialogue/FusionDialogueTransportBridge.cs",
                "Traversal/FusionTraversalTransportBridge.cs",
                "Abilities/FusionAbilitiesTransportBridge.cs",
                "FusionChatBoxUI.cs"
            };
            foreach (string relativePath in directProducerPaths)
            {
                string source = ReadAssetSource(
                    "Arawn/NetworkingLayerForGC2/Runtime/Transport/Fusion/" + relativePath);
                StringAssert.Contains(
                    "IFusionFullSnapshotProducer",
                    source,
                    $"Mandatory producer contract is missing: {relativePath}");
                StringAssert.Contains(
                    "RegisterFullSnapshotProducer(this)",
                    source,
                    $"Mandatory producer registration is missing: {relativePath}");
                StringAssert.Contains(
                    "UnregisterFullSnapshotProducer(this)",
                    source,
                    $"Mandatory producer cleanup is missing: {relativePath}");
                StringAssert.Contains(
                    "ProduceFullSnapshot(",
                    source,
                    $"Mandatory full snapshot implementation is missing: {relativePath}");
            }

            string fusionRuntimeRoot = Path.Combine(
                Application.dataPath,
                "Arawn/NetworkingLayerForGC2/Runtime/Transport/Fusion");
            foreach (string path in Directory.EnumerateFiles(
                         fusionRuntimeRoot,
                         "*.cs",
                         SearchOption.AllDirectories))
            {
                string source = File.ReadAllText(path);
                StringAssert.DoesNotContain(
                    "ClientReady +=",
                    source,
                    $"Snapshots must use explicit producers instead of an unverified event: {path}");
            }
        }

        [Test]
        public void SessionBootstrap_SerializesStartAndShutdownLifecycle()
        {
            string source = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Runtime/Transport/Fusion/" +
                "FusionSessionBootstrap.cs");
            StringAssert.Contains("Task<StartGameResult> m_ActiveStartTask", source);
            StringAssert.Contains("Task m_ActiveShutdownTask", source);
            StringAssert.Contains("bool m_ShutdownRequested", source);
            StringAssert.Contains("bool m_StartAwaitCompleted", source);

            string beginStart = ExtractMethodBody(
                source,
                "private Task<StartGameResult> BeginStartSession(");
            StringAssert.Contains(
                "m_ActiveShutdownTask != null && !m_ActiveShutdownTask.IsCompleted",
                beginStart);
            StringAssert.Contains("m_Destroying", beginStart);
            AssertAppearsBefore(beginStart, "m_ActiveShutdownTask", "m_ActiveStartTask =");
            AssertAppearsBefore(beginStart, "m_Destroying", "m_ActiveStartTask =");

            string shutdownEntry = ExtractMethodBody(source, "public Task ShutdownAsync()");
            StringAssert.Contains("m_ShutdownRequested = true;", shutdownEntry);
            AssertAppearsBefore(
                shutdownEntry,
                "!m_ActiveShutdownTask.IsCompleted",
                "m_ActiveShutdownTask = ShutdownCoreAsync();");
            AssertAppearsBefore(
                shutdownEntry,
                "return m_ActiveShutdownTask;",
                "m_ActiveShutdownTask = ShutdownCoreAsync();");

            string shutdownCore = ExtractMethodBody(
                source,
                "private async Task ShutdownCoreAsync()");
            StringAssert.Contains("Task<StartGameResult> pendingStart = m_ActiveStartTask;", shutdownCore);
            StringAssert.Contains("!m_StartAwaitCompleted", shutdownCore);
            AssertAppearsBefore(shutdownCore, "await pendingStart;", "NetworkRunner runner = m_Runner;");

            string startCore = ExtractMethodBody(
                source,
                "private async Task<StartGameResult> StartSessionAsync(");
            AssertAppearsBefore(startCore, "await runner.StartGame(args)", "m_StartAwaitCompleted = true;");
            AssertAppearsBefore(startCore, "m_StartAwaitCompleted = true;", "!m_ShutdownRequested");
            AssertAppearsBefore(startCore, "!m_ShutdownRequested", "SessionStarted?.Invoke(runner);");
            StringAssert.Contains("finally", startCore);
            StringAssert.Contains("m_StartInProgress = false;", startCore);

            string destroy = ExtractMethodBody(source, "private async void OnDestroy()");
            AssertAppearsBefore(destroy, "m_Destroying = true;", "await ShutdownAsync();");
        }

        [Test]
        public void SessionStartOptions_PreserveInviteAndRelayOverrides()
        {
            var options = new FusionSessionStartOptions(
                "steam-lobby-session",
                "eu",
                forcePhotonRelay: true);

            Assert.AreEqual("steam-lobby-session", options.SessionName);
            Assert.AreEqual("eu", options.Region);
            Assert.IsNull(options.AuthenticationValues);
            Assert.IsTrue(options.ForcePhotonRelay);
        }

        [Test]
        public void SessionBootstrap_WiresAuthenticationRegionAndRelayIntoStartGame()
        {
            string source = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Runtime/Transport/Fusion/" +
                "FusionSessionBootstrap.cs");
            string startCore = ExtractMethodBody(
                source,
                "private async Task<StartGameResult> StartSessionAsync(");

            StringAssert.Contains("CreateAuthenticationValuesAsync(startCancellation.Token)", startCore);
            StringAssert.Contains("AuthValues = authenticationValues", startCore);
            StringAssert.Contains("DisableNATPunchthrough =", startCore);
            StringAssert.Contains("gameMode != GameMode.Shared && options.ForcePhotonRelay", startCore);
            StringAssert.Contains("appSettings.FixedRegion = options.Region", startCore);
            StringAssert.Contains("NotifyAuthenticationCompletedBestEffort(", startCore);
            AssertAppearsBefore(
                startCore,
                "CreateAuthenticationValuesAsync(startCancellation.Token)",
                "await runner.StartGame(args)");

            string wizard = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Editor/Transport/Fusion/" +
                "FusionSceneSetupWizard.cs");
            StringAssert.Contains(
                "SetBool(serialized, \"m_ForcePhotonRelay\", m_ForcePhotonRelay)",
                wizard);

            string validation = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Editor/Transport/Fusion/" +
                "FusionSceneSetupValidation.cs");
            StringAssert.Contains(
                "authenticationProvider is not IFusionAuthenticationProvider",
                validation);
        }

        [Test]
        public void FusionWizard_PostflightFailureRollsBackBeforeCommit()
        {
            string source = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Editor/Transport/Fusion/" +
                "FusionSceneSetupWizard.cs");
            string runSetup = ExtractMethodBody(
                source,
                "private bool RunSetup(bool showDialogs)");

            Assert.AreEqual(
                1,
                CountOccurrences(runSetup, "assetTransaction.Commit();"),
                "The setup transaction must have one success-only commit point.");
            AssertAppearsBefore(
                runSetup,
                "FusionSceneSetupValidation.Validate(m_PlayerPrefab, true)",
                "if (postflightFailed)");
            AssertAppearsBefore(
                runSetup,
                "if (postflightFailed)",
                "throw new InvalidOperationException(");
            AssertAppearsBefore(
                runSetup,
                "throw new InvalidOperationException(",
                "assetTransaction.Commit();");
            AssertAppearsBefore(
                runSetup,
                "Undo.RevertAllDownToGroup(undoGroup);",
                "assetTransaction?.Rollback();");
        }

        [Test]
        public void FusionWizard_InventoryOperationsUseActiveSceneOverloads()
        {
            string wizardSource = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Editor/Transport/Fusion/" +
                "FusionSceneSetupWizard.cs");
            StringAssert.DoesNotContain("InventorySceneSetupTools.ValidateOpenScenes", wizardSource);
            Assert.AreEqual(
                2,
                CountOccurrences(
                    wizardSource,
                    "InventorySceneSetupTools.ValidateScene(SceneManager.GetActiveScene())"),
                "Review and preflight must both validate only the configured active scene.");

            string conversion = ExtractMethodBody(
                wizardSource,
                "private void ConvertInventoryPickupsIfSelected()");
            StringAssert.Contains("InventorySceneSetupTools.ConvertStockScenePickups(", conversion);
            AssertAppearsBefore(conversion, "SceneManager.GetActiveScene()", "false");

            string inventoryToolsSource = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Editor/InventorySceneSetupTools.cs");
            StringAssert.Contains(
                "public static ValidationSummary ValidateScene(Scene scene",
                inventoryToolsSource);
            StringAssert.Contains(
                "public static int ConvertStockScenePickups(Scene scene, bool showSummary)",
                inventoryToolsSource);
        }

        [Test]
        public void FusionDialogueBridge_RegistersAndRetainsStandaloneSceneController()
        {
            string source = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Runtime/Transport/Fusion/Dialogue/" +
                "FusionDialogueTransportBridge.cs");
            string register = ExtractMethodBody(
                source,
                "private void RegisterController(NetworkDialogueManager manager, NetworkDialogueController controller)");
            StringAssert.Contains("uint networkId = controller.NetworkId;", register);
            StringAssert.DoesNotContain("networkCharacter == null", register);

            string prune = ExtractMethodBody(
                source,
                "private void PruneControllerRegistry(NetworkDialogueManager manager)");
            StringAssert.Contains("controller.NetworkId != pair.Key", prune);
            StringAssert.DoesNotContain("character == null", prune);

            string localRole = ExtractMethodBody(
                source,
                "private static bool IsControllerLocalClient(NetworkDialogueController controller)");
            StringAssert.Contains("!controller.RequiresTargetOwnership", localRole);

            Type bridgeType = Type.GetType(
                "Arawn.GameCreator2.Networking.Dialogue.Transport.Fusion." +
                "FusionDialogueTransportBridge, " +
                "Arawn.GameCreator2.Networking.Dialogue.Transport.Fusion");
            Type managerType = Type.GetType(
                "Arawn.GameCreator2.Networking.Dialogue.NetworkDialogueManager, " +
                "Arawn.GameCreator2.Networking.Dialogue");
            Type controllerType = Type.GetType(
                "Arawn.GameCreator2.Networking.Dialogue.NetworkDialogueController, " +
                "Arawn.GameCreator2.Networking.Dialogue");
            if (bridgeType == null || managerType == null || controllerType == null)
            {
                Assert.Ignore("The optional GC2 Dialogue integration is not installed.");
            }

            GameObject managerObject = null;
            GameObject controllerObject = null;
            GameObject bridgeObject = null;
            try
            {
                managerObject = new GameObject("Fusion Dialogue Test Manager");
                controllerObject = new GameObject("Fusion Standalone Dialogue Test Controller");
                bridgeObject = new GameObject("Fusion Dialogue Test Bridge");
                managerObject.SetActive(false);
                controllerObject.SetActive(false);
                bridgeObject.SetActive(false);

                Component manager = managerObject.AddComponent(managerType);
                Component controller = controllerObject.AddComponent(controllerType);
                Component bridge = bridgeObject.AddComponent(bridgeType);

                var controllerSerialized = new SerializedObject(controller);
                SerializedProperty authorityMode =
                    controllerSerialized.FindProperty("m_AuthorityMode");
                Assert.NotNull(authorityMode);
                authorityMode.enumValueIndex = 2; // NetworkDialogueAuthorityMode.GlobalScene
                controllerSerialized.ApplyModifiedPropertiesWithoutUndo();

                PropertyInfo networkIdProperty = controllerType.GetProperty("NetworkId");
                Assert.NotNull(networkIdProperty);
                uint networkId = Convert.ToUInt32(networkIdProperty.GetValue(controller));
                Assert.AreNotEqual(0u, networkId);

                MethodInfo registerController = bridgeType.GetMethod(
                    "RegisterController",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                MethodInfo pruneControllers = bridgeType.GetMethod(
                    "PruneControllerRegistry",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                MethodInfo refreshRoles = bridgeType.GetMethod(
                    "RefreshRegisteredControllerRoles",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                MethodInfo getController = managerType.GetMethod("GetController");
                Assert.NotNull(registerController);
                Assert.NotNull(pruneControllers);
                Assert.NotNull(refreshRoles);
                Assert.NotNull(getController);

                registerController.Invoke(bridge, new object[] { manager, controller });
                Assert.AreSame(
                    controller,
                    getController.Invoke(manager, new object[] { networkId }));

                pruneControllers.Invoke(bridge, new object[] { manager });
                refreshRoles.Invoke(bridge, null);
                Assert.AreSame(
                    controller,
                    getController.Invoke(manager, new object[] { networkId }),
                    "Standalone scene controllers must survive registry pruning.");

                PropertyInfo localClientProperty =
                    controllerType.GetProperty("IsLocalClient");
                Assert.NotNull(localClientProperty);
                Assert.IsTrue(
                    (bool)localClientProperty.GetValue(controller),
                    "A GlobalScene controller is locally addressable on non-authority peers.");
            }
            finally
            {
                if (bridgeObject != null) UnityEngine.Object.DestroyImmediate(bridgeObject);
                if (controllerObject != null) UnityEngine.Object.DestroyImmediate(controllerObject);
                if (managerObject != null) UnityEngine.Object.DestroyImmediate(managerObject);
            }
        }

        [Test]
        public void FusionAssetUndo_PrunesUnchangedFilesAndRestoresCreatedDirectories()
        {
            string undoSource = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Editor/Transport/Fusion/" +
                "FusionSetupAssetUndo.cs");
            StringAssert.Contains("[SerializeField] private bool m_IsDirectory;", undoSource);
            StringAssert.Contains("public bool ContentEquals(FileSnapshot other)", undoSource);

            string commit = ExtractMethodBody(undoSource, "public void Commit()");
            AssertAppearsBefore(commit, "CaptureAll(m_Paths)", "RemoveUnchangedSnapshots(");

            string prune = ExtractMethodBody(
                undoSource,
                "private static void RemoveUnchangedSnapshots(");
            StringAssert.Contains("before[i].ContentEquals(after[i])", prune);
            StringAssert.Contains("before.RemoveAt(i);", prune);
            StringAssert.Contains("after.RemoveAt(i);", prune);

            string removeDirectory = ExtractMethodBody(
                undoSource,
                "public void RemoveCreatedDirectory()");
            StringAssert.Contains("m_Existed || !m_IsDirectory", removeDirectory);
            StringAssert.Contains("Directory.EnumerateFileSystemEntries(absolute).Any()", removeDirectory);
            StringAssert.Contains("Directory.Delete(absolute, false);", removeDirectory);

            string restore = ExtractMethodBody(
                undoSource,
                "private static void RestoreAll(IReadOnlyList<FileSnapshot> snapshots)");
            AssertAppearsBefore(restore, "RestoreContent();", "RemoveCreatedDirectory();");
            StringAssert.Contains(
                "OrderByDescending(item => item.ProjectPath.Length)",
                restore);

            string wizardSource = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Editor/Transport/Fusion/" +
                "FusionSceneSetupWizard.cs");
            string undoPaths = ExtractMethodBody(
                wizardSource,
                "private IEnumerable<string> GetAssetUndoPaths()");
            StringAssert.Contains("GeneratedFolder", undoPaths);
            StringAssert.Contains("GeneratedFolder + \".meta\"", undoPaths);
        }

        private static void CreateBootstrap(Scene scene, string name)
        {
            var gameObject = new GameObject(name);
            SceneManager.MoveGameObjectToScene(gameObject, scene);
            gameObject.AddComponent<FusionSessionBootstrap>();
        }

        private static void CloseTemporaryScene(Scene scene)
        {
            if (scene.IsValid() && scene.isLoaded)
            {
                EditorSceneManager.CloseScene(scene, true);
            }
        }

        private static Type RequireEditorType(string fullName)
        {
            Type type = Type.GetType($"{fullName}, {FusionEditorAssembly}");
            Assert.NotNull(type, $"Editor type was not found: {fullName}");
            return type;
        }

        private static string[] ReadIssueMessages(object report)
        {
            Assert.NotNull(report);
            PropertyInfo issuesProperty = report.GetType().GetProperty(
                "Issues",
                BindingFlags.Instance | BindingFlags.Public);
            Assert.NotNull(issuesProperty);

            var messages = new List<string>();
            foreach (object issue in (IEnumerable)issuesProperty.GetValue(report))
            {
                PropertyInfo messageProperty = issue.GetType().GetProperty(
                    "Message",
                    BindingFlags.Instance | BindingFlags.Public);
                Assert.NotNull(messageProperty);
                messages.Add((string)messageProperty.GetValue(issue));
            }

            return messages.ToArray();
        }

        private static T GetNamedAttributeValue<T>(
            CustomAttributeData attribute,
            string memberName)
        {
            CustomAttributeNamedArgument argument = attribute.NamedArguments.Single(
                candidate => candidate.MemberName == memberName);
            return (T)argument.TypedValue.Value;
        }

        private static string GetNamedAttributeValueAsString(
            CustomAttributeData attribute,
            string memberName)
        {
            CustomAttributeNamedArgument argument = attribute.NamedArguments.Single(
                candidate => candidate.MemberName == memberName);
            return argument.TypedValue.ArgumentType.IsEnum
                ? Enum.GetName(argument.TypedValue.ArgumentType, argument.TypedValue.Value)
                : argument.TypedValue.Value?.ToString();
        }

        private static string ReadAssetSource(string pathBelowAssets)
        {
            string fullPath = Path.Combine(
                Application.dataPath,
                pathBelowAssets.Replace('/', Path.DirectorySeparatorChar));
            Assert.IsTrue(File.Exists(fullPath), $"Source file was not found: {fullPath}");
            return File.ReadAllText(fullPath);
        }

        private static string ExtractMethodBody(string source, string signature)
        {
            int signatureIndex = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.GreaterOrEqual(signatureIndex, 0, $"Method was not found: {signature}");

            int openingBrace = source.IndexOf('{', signatureIndex + signature.Length);
            Assert.GreaterOrEqual(openingBrace, 0);

            int depth = 0;
            for (int index = openingBrace; index < source.Length; index++)
            {
                switch (source[index])
                {
                    case '{':
                        depth++;
                        break;
                    case '}':
                        depth--;
                        if (depth == 0)
                        {
                            return source.Substring(openingBrace, index - openingBrace + 1);
                        }
                        break;
                }
            }

            Assert.Fail($"Method body was not balanced: {signature}");
            return string.Empty;
        }

        private static int CountOccurrences(string source, string value)
        {
            int count = 0;
            int index = 0;
            while ((index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += value.Length;
            }

            return count;
        }

        private static void AssertAppearsBefore(
            string source,
            string earlier,
            string later)
        {
            int earlierIndex = source.IndexOf(earlier, StringComparison.Ordinal);
            int laterIndex = source.IndexOf(later, StringComparison.Ordinal);
            Assert.GreaterOrEqual(earlierIndex, 0, $"Source token was not found: {earlier}");
            Assert.GreaterOrEqual(laterIndex, 0, $"Source token was not found: {later}");
            Assert.Less(
                earlierIndex,
                laterIndex,
                $"Expected '{earlier}' to appear before '{later}'.");
        }
    }
}
