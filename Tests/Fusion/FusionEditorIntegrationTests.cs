using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Arawn.GameCreator2.Networking.Editor;
using Arawn.GameCreator2.Networking.Transport.Fusion.Editor;
using Fusion;
using GameCreator.Runtime.Characters;
using NUnit.Framework;
using Assert = NUnit.Framework.Assert;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using NetworkRole = Arawn.GameCreator2.Networking.NetworkCharacter.NetworkRole;

namespace Arawn.GameCreator2.Networking.Transport.Fusion.Tests
{
    internal sealed class FusionTestAuthoritativePoseBackend : MonoBehaviour,
        INetworkCharacterPredictionBackend,
        INetworkAuthoritativePoseProvider
    {
        public NetworkPredictionBackend Backend => NetworkPredictionBackend.FusionNative;
        public Vector3 Position { get; set; }
        public Quaternion Rotation { get; set; } = Quaternion.identity;
        public int PoseReadCount { get; private set; }

        public IUnitDriver CreateDriver(
            NetworkCharacter networkCharacter,
            NetworkRole role) => null;

        public void Initialize(
            NetworkCharacter networkCharacter,
            NetworkRole role,
            bool isServer,
            bool isOwner,
            bool isHost)
        { }

        public void ApplySessionProfile(NetworkSessionProfile profile) { }
        public void ResetBackend(NetworkCharacter networkCharacter) { }

        public bool TryGetAuthoritativePose(
            out Vector3 position,
            out Quaternion rotation)
        {
            PoseReadCount++;
            position = Position;
            rotation = Rotation;
            return true;
        }
    }

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
        public void FusionRuntimeAssembly_AllowsUnsafeCodeForWeavedRpcCalls()
        {
            string assemblyDefinition = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Runtime/Transport/Fusion/" +
                "Arawn.GameCreator2.Networking.Transport.Fusion.asmdef");

            StringAssert.Contains(
                "\"name\": \"Arawn.GameCreator2.Networking.Transport.Fusion\"",
                assemblyDefinition);
            StringAssert.Contains("\"allowUnsafeCode\": true", assemblyDefinition);

            string validation = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Editor/Transport/Fusion/" +
                "FusionSceneSetupValidation.cs");
            StringAssert.Contains("ValidateRuntimeAssemblyDefinition(report)", validation);
            StringAssert.Contains("if (!definition.allowUnsafeCode)", validation);
            StringAssert.Contains("AssetDatabase.FindAssets(\"t:asmdef\")", validation);
            StringAssert.Contains(
                "Fusion RPC routing cannot work until the assembly is woven",
                validation);
        }

        [Test]
        public void FusionMonoBuildCompatibility_IsStrictOnlyForMonoPlayers()
        {
            Type compatibilityType = RequireEditorType(
                "Arawn.GameCreator2.Networking.Transport.Fusion.Editor." +
                "FusionMonoBuildCompatibility");
            MethodInfo isCompatible = compatibilityType.GetMethod(
                "IsCompatible",
                StaticNonPublic);
            Assert.NotNull(isCompatible);

            Assert.IsTrue((bool)isCompatible.Invoke(
                null,
                new object[]
                {
                    ScriptingImplementation.Mono2x,
                    ManagedStrippingLevel.Disabled
                }));
            Assert.IsFalse((bool)isCompatible.Invoke(
                null,
                new object[]
                {
                    ScriptingImplementation.Mono2x,
                    ManagedStrippingLevel.Minimal
                }));
            Assert.IsTrue((bool)isCompatible.Invoke(
                null,
                new object[]
                {
                    ScriptingImplementation.IL2CPP,
                    ManagedStrippingLevel.High
                }));

            string compatibilitySource = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Editor/Transport/Fusion/" +
                "FusionMonoBuildCompatibility.cs");
            StringAssert.Contains("IProcessSceneWithReport", compatibilitySource);
            StringAssert.DoesNotContain("IPreprocessBuildWithReport", compatibilitySource);
            StringAssert.Contains("FusionTransportBridge", compatibilitySource);
            StringAssert.Contains("FusionSessionBootstrap", compatibilitySource);
            StringAssert.Contains("throw new BuildFailedException(issue)", compatibilitySource);

            string validation = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Editor/Transport/Fusion/" +
                "FusionSceneSetupValidation.cs");
            StringAssert.Contains("ValidateMonoPlayerBuildCompatibility(report)", validation);

            string wizard = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Editor/Transport/Fusion/" +
                "FusionSceneSetupWizard.cs");
            StringAssert.Contains("Fix Mono Build Compatibility", wizard);
            StringAssert.Contains(
                "FusionMonoBuildCompatibility.ConfigureActiveBuildTarget()",
                wizard);
        }

        [Test]
        public void FusionMonoBuildGuard_DetectsOnlyScenesWithAFusionTransportOwner()
        {
            Type compatibilityType = RequireEditorType(
                "Arawn.GameCreator2.Networking.Transport.Fusion.Editor." +
                "FusionMonoBuildCompatibility");
            MethodInfo sceneUsesFusion = compatibilityType.GetMethod(
                "SceneUsesFusionTransport",
                StaticNonPublic);
            Assert.NotNull(sceneUsesFusion);

            Scene testScene = default;
            try
            {
                testScene = EditorSceneManager.NewPreviewScene();
                Assert.IsFalse((bool)sceneUsesFusion.Invoke(null, new object[] { testScene }));

                var owner = new GameObject("Inactive Fusion build guard owner");
                owner.SetActive(false);
                SceneManager.MoveGameObjectToScene(owner, testScene);
                owner.AddComponent<FusionSessionBootstrap>();

                Assert.IsTrue(
                    (bool)sceneUsesFusion.Invoke(null, new object[] { testScene }),
                    "An inactive serialized bootstrap may be enabled at runtime and must " +
                    "still make the scene subject to the Mono compatibility guard.");
            }
            finally
            {
                if (testScene.IsValid())
                {
                    EditorSceneManager.ClosePreviewScene(testScene);
                }
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
            StringAssert.Contains("m_RpcSendFailureLatched", bridgeSource);
            StringAssert.Contains("private bool TrySendRpc(Action send, string route)", bridgeSource);
            StringAssert.Contains("catch (MethodAccessException exception)", bridgeSource);
            StringAssert.Contains("if (!m_RpcSendFailureLatched &&", bridgeSource);
            StringAssert.Contains("return m_LastRpcSendFailure", bridgeSource);
        }

        [Test]
        public void SharedCharacterInputRpc_IsHostedByNormallyWovenIdentity()
        {
            string motor = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Runtime/Transport/Fusion/" +
                "FusionNativeNetworkCharacterMotor.cs");
            string identity = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Runtime/Transport/Fusion/" +
                "FusionNetworkIdentity.cs");

            StringAssert.Contains(
                "[NetworkBehaviourWeaved(FusionNativeCharacterState.WORDS)]",
                motor,
                "The custom NetworkTRSP state allocation must remain manually woven.");
            StringAssert.Contains(
                "public int InputStateOwnerRaw",
                motor,
                "Shared acknowledgement baselines must identify the logical owner they belong to.");
            StringAssert.DoesNotContain(
                "[Rpc(",
                motor,
                "Fusion skips RPC generation on manually-woven NetworkBehaviours.");
            StringAssert.Contains(
                "m_Identity.TrySubmitSharedCharacterInput(",
                motor);
            StringAssert.Contains("AcceptSharedCharacterInput", motor);

            StringAssert.Contains("TrySubmitSharedCharacterInput", identity);
            StringAssert.Contains("RPC_SubmitSharedCharacterInput", identity);
            StringAssert.Contains("RPC_SubmitSharedCharacterTransient", identity);
            StringAssert.Contains("FlagContinuousOwnerPose", motor);
            StringAssert.Contains("HasContinuousOwnerPose", motor);
            StringAssert.Contains("RpcInvokeInfo", identity);
            StringAssert.Contains("RpcTargets.StateAuthority", identity);
            StringAssert.Contains("Channel = RpcChannel.Unreliable", identity);
            StringAssert.Contains("Channel = RpcChannel.Reliable", identity);
            StringAssert.Contains("info.Source", identity);
            StringAssert.Contains("info.Tick.Raw", identity);
            string submitInput = ExtractDeclaredMethodBody(
                identity,
                "TrySubmitSharedCharacterInput");
            StringAssert.Contains(
                "FusionNativeNetworkCharacterMotor.HasSharedTransientInput(input)",
                submitInput);
            StringAssert.Contains(
                "input.HasContinuousOwnerPose",
                submitInput,
                "Replaceable MotionInteractive poses must travel with latest-state intent.");
            StringAssert.Contains("EnqueueSharedCharacterTransient(input)", submitInput);
            StringAssert.Contains("TrySendQueuedSharedCharacterTransient()", submitInput);
            string sendTransient = ExtractDeclaredMethodBody(
                identity,
                "TrySendQueuedSharedCharacterTransient");
            StringAssert.Contains("m_SharedTransientSendBacklog.Peek()", sendTransient);
            StringAssert.Contains("RPC_SubmitSharedCharacterTransient", sendTransient);
            StringAssert.Contains("RpcSendMessageResult.Sent", sendTransient);
            StringAssert.Contains("m_SharedTransientSendBacklog.Dequeue()", sendTransient);
            string enqueueTransient = ExtractDeclaredMethodBody(
                identity,
                "EnqueueSharedCharacterTransient");
            StringAssert.Contains("SharedTransientSendBacklogCapacity", enqueueTransient);
            StringAssert.Contains("m_SharedTransientSendOverflowLatched", enqueueTransient);
            string acceptInput = ExtractDeclaredMethodBody(
                motor,
                "AcceptSharedCharacterInput");
            StringAssert.Contains(
                "source != m_Identity.LogicalOwner",
                acceptInput,
                "Shared input must be authenticated against the replicated logical owner.");
            StringAssert.Contains(
                "sourceTick <= m_LastSharedPayloadTick",
                acceptInput,
                "The owner payload remains a monotonic sequence.");
            StringAssert.Contains(
                "accepted Shared input with independent-clock offset",
                acceptInput,
                "Shared peers may accumulate a legitimate runner-tick offset without losing " +
                "all subsequent movement input.");
            StringAssert.DoesNotContain(
                "rejected Shared input payloadTick",
                acceptInput,
                "An absolute comparison between independently corrected Shared clocks must " +
                "not be an admission boundary.");
            StringAssert.Contains("SourceTick = sourceTick", acceptInput);
            StringAssert.Contains("hasContinuousOwnerPose", acceptInput);
            StringAssert.Contains("FlagContinuousOwnerPose", acceptInput);
            StringAssert.Contains(
                "OwnerPosition = hasContinuousOwnerPose ? ownerPosition : Vector3.zero",
                acceptInput);
            StringAssert.Contains("m_LatestSharedTrustedTick", acceptInput);
            StringAssert.DoesNotContain(
                "sourceTick != trustedSourceTick",
                acceptInput,
                "Fusion's Shared RPC envelope tick is not guaranteed to equal the tick " +
                "captured by the runner-level logical-owner pump.");

            string acceptTransient = ExtractDeclaredMethodBody(
                motor,
                "AcceptSharedCharacterTransient");
            StringAssert.Contains(
                "sourceTick <= m_LastQueuedSharedTransientTick",
                acceptTransient,
                "Reliable and unreliable channels require independent monotonic sequences.");
            StringAssert.DoesNotContain("m_LastSharedPayloadTick", acceptTransient);
            StringAssert.Contains(
                "SharedTransientReceiveBacklogCapacity",
                acceptTransient);
            StringAssert.Contains(
                "m_SharedTransientReceiveOverflowLatched",
                acceptTransient);
            StringAssert.Contains("m_SharedTransientQueue.Enqueue", acceptTransient);
            StringAssert.Contains("TrustedTick = trustedSourceTick", acceptTransient);

            string fixedShared = ExtractDeclaredMethodBody(motor, "FixedUpdateShared");
            StringAssert.Contains("sharedPayloadTick = input.SourceTick", fixedShared);
            StringAssert.Contains("input.SourceTick = m_LatestSharedTrustedTick", fixedShared);
            StringAssert.Contains("m_SharedTransientQueue.Dequeue()", fixedShared);
            StringAssert.Contains("input.SourceTick = transient.TrustedTick", fixedShared);
            StringAssert.Contains("if (appliedSharedTransient)", fixedShared);
            AssertAppearsBefore(
                fixedShared,
                "m_Driver.Simulate",
                "NativeState.LastAppliedSharedSourceTick = sharedPayloadTick");
            StringAssert.Contains("AdvanceSharedProcessedSourceTick", fixedShared);
            string advanceSharedTick = ExtractDeclaredMethodBody(
                motor,
                "AdvanceSharedProcessedSourceTick");
            StringAssert.Contains("LastProcessedInputTick", advanceSharedTick);
            StringAssert.Contains("latestPayloadTick", advanceSharedTick);
            StringAssert.Contains("representedTick + 1L", advanceSharedTick);

            string sharedOwnerPump = ExtractDeclaredMethodBody(
                motor,
                "SimulateSharedLogicalOwnerProxyTick");
            StringAssert.Contains(
                "owner-queued-for-reliable-send",
                sharedOwnerPump,
                "The Shared joiner log must identify when Vault/Jump/root motion enters the " +
                "reliable send path.");

            MethodInfo generatedInvoker = typeof(FusionNetworkIdentity)
                .GetMethods(BindingFlags.Static | BindingFlags.Instance |
                            BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method => method.Name.StartsWith(
                    "RPC_SubmitSharedCharacterInput@Invoker",
                    StringComparison.Ordinal));
            Assert.NotNull(
                generatedInvoker,
                "Fusion did not weave the Shared character input RPC on FusionNetworkIdentity.");

            MethodInfo generatedTransientInvoker = typeof(FusionNetworkIdentity)
                .GetMethods(BindingFlags.Static | BindingFlags.Instance |
                            BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method => method.Name.StartsWith(
                    "RPC_SubmitSharedCharacterTransient@Invoker",
                    StringComparison.Ordinal));
            Assert.NotNull(
                generatedTransientInvoker,
                "Fusion did not weave the reliable Shared transient RPC.");
        }

        [Test]
        public void SharedMasterCharacters_UseHostLikePresentationRole()
        {
            string auto = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Runtime/Transport/Fusion/" +
                "FusionNetworkCharacterAuto.cs");
            string refreshRole = ExtractDeclaredMethodBody(auto, "RefreshRole");

            StringAssert.Contains("Runner.GameMode == GameMode.Shared", refreshRole);
            StringAssert.Contains("Runner.IsSharedModeMasterClient", refreshRole);
            StringAssert.Contains(
                "m_Character.InitializeNetworkRole(isServer, isOwner, isHost)",
                refreshRole,
                "A graphical Shared master must not apply dedicated-server renderer " +
                "optimizations to remote players.");
        }

        [Test]
        public void SharedLogicalOwnerProxy_IsPumpedByRunnerSimulationBehaviour()
        {
            string router = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Runtime/Transport/Fusion/" +
                "FusionRpcRouter.cs");
            string motor = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Runtime/Transport/Fusion/" +
                "FusionNativeNetworkCharacterMotor.cs");

            string runnerTick = ExtractMethodBody(
                router,
                "public override void FixedUpdateNetwork()");
            StringAssert.Contains("!Runner.IsForward", runnerTick);
            StringAssert.Contains("TryResolveSharedLogicalOwnerProxy", runnerTick);
            StringAssert.Contains(
                "motor.SimulateSharedLogicalOwnerProxyTick",
                runnerTick);
            StringAssert.Contains("restorePredictedPose: true", runnerTick);

            string runnerRender = ExtractMethodBody(
                router,
                "public override void Render()");
            StringAssert.Contains("!playerObject.IsInSimulation", runnerRender);
            StringAssert.Contains("motor.RenderSharedLogicalOwnerProxy()", runnerRender);

            string resolveProxy = ExtractDeclaredMethodBody(
                router,
                "TryResolveSharedLogicalOwnerProxy");
            StringAssert.Contains("Runner.TryGetPlayerObject", resolveProxy);
            StringAssert.Contains(
                "IsSharedLogicalOwnerObject(playerObject, localPlayer)",
                resolveProxy);
            StringAssert.Contains("ResolveSharedLogicalOwnerObject", resolveProxy);

            string resolveLogicalOwner = ExtractDeclaredMethodBody(
                router,
                "ResolveSharedLogicalOwnerObject");
            StringAssert.Contains("Runner.GetAllNetworkObjects", resolveLogicalOwner);
            StringAssert.Contains("IsSharedLogicalOwnerObject", resolveLogicalOwner);

            string validateLogicalOwner = ExtractDeclaredMethodBody(
                router,
                "IsSharedLogicalOwnerObject");
            StringAssert.Contains("identity.TransportAdmitted", validateLogicalOwner);
            StringAssert.Contains("identity.IsOwnedBy(localPlayer)", validateLogicalOwner);
            StringAssert.Contains(
                "GetComponent<FusionNativeNetworkCharacterMotor>()",
                validateLogicalOwner);

            string proxyTick = ExtractDeclaredMethodBody(
                motor,
                "SimulateSharedLogicalOwnerProxyTick");
            StringAssert.Contains("TryInitializeNetworkState()", proxyTick);
            StringAssert.Contains("RestoreSharedPredictedSimulationPose()", proxyTick);
            StringAssert.Contains("m_LastSharedOwnerSimulationTick", proxyTick);
            StringAssert.Contains("ReconcileSharedPrediction", proxyTick);
            StringAssert.Contains("m_Driver.CaptureInput(tick)", proxyTick);
            StringAssert.Contains("TrySubmitSharedCharacterInput", proxyTick);
            AssertAppearsBefore(
                proxyTick,
                "RestoreSharedPredictedSimulationPose()",
                "ReconcileSharedPrediction");
            AssertAppearsBefore(
                proxyTick,
                "ReconcileSharedPrediction",
                "m_Driver.CaptureInput(tick)");
        }

        [TestCase(true, true, true, true, NetworkRole.LocalClient)]
        [TestCase(true, true, false, true, NetworkRole.Server)]
        [TestCase(true, true, true, false, NetworkRole.Server)]
        [TestCase(true, false, true, true, NetworkRole.Server)]
        [TestCase(false, true, false, true, NetworkRole.LocalClient)]
        public void FusionAuthorityOwner_UsesConfiguredLocalPredictionRole(
            bool isServer,
            bool isOwner,
            bool isHost,
            bool hostUsesClientPrediction,
            NetworkRole expected)
        {
            var gameObject = new GameObject("Fusion role resolution test");
            try
            {
                NetworkCharacter character = gameObject.AddComponent<NetworkCharacter>();
                FieldInfo prediction = typeof(NetworkCharacter).GetField(
                    "m_HostOwnerUsesClientPrediction",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                MethodInfo resolveRole = typeof(NetworkCharacter).GetMethod(
                    "ResolveRole",
                    BindingFlags.Instance | BindingFlags.NonPublic);

                Assert.NotNull(prediction);
                Assert.NotNull(resolveRole);
                prediction.SetValue(character, hostUsesClientPrediction);

                Assert.AreEqual(
                    expected,
                    resolveRole.Invoke(character, new object[] { isServer, isOwner, isHost }));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void FusionWizardAndDemoPlayers_EnableAuthorityOwnerPrediction()
        {
            string wizard = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Editor/Transport/Fusion/" +
                "FusionSceneSetupWizard.cs");
            StringAssert.Contains(
                "SetBool(serialized, \"m_HostOwnerUsesClientPrediction\", true)",
                wizard);

            string validation = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Editor/Transport/Fusion/" +
                "FusionSceneSetupValidation.cs");
            StringAssert.Contains(
                "The Fusion player prefab has authority-owner prediction disabled",
                validation);

            string demoRoot = Path.Combine(
                Application.dataPath,
                "Arawn/NetworkingLayerForGC2/Demo/Fusion");
            string[] fusionPrefabs = Directory
                .EnumerateFiles(demoRoot, "*.prefab", SearchOption.AllDirectories)
                .ToArray();

            int networkCharacterPrefabCount = 0;
            foreach (string prefabPath in fusionPrefabs)
            {
                string assetPath = "Assets" + prefabPath
                    .Substring(Application.dataPath.Length)
                    .Replace('\\', '/');
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
                NetworkCharacter character =
                    prefab != null
                        ? prefab.GetComponentInChildren<NetworkCharacter>(true)
                        : null;
                if (character == null) continue;

                networkCharacterPrefabCount++;
                var serialized = new SerializedObject(character);
                SerializedProperty prediction = serialized.FindProperty(
                    "m_HostOwnerUsesClientPrediction");
                Assert.NotNull(
                    prediction,
                    $"Fusion demo player is missing authority-owner prediction: {assetPath}");
                Assert.IsTrue(
                    prediction.boolValue,
                    $"Fusion demo player does not enable authority-owner prediction: {assetPath}");
            }

            Assert.Greater(networkCharacterPrefabCount, 0);
        }

        [Test]
        public void FusionWizard_DefaultsToNativeMovementAndExposesLegacyFallback()
        {
            string wizard = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Editor/Transport/Fusion/" +
                "FusionSceneSetupWizard.cs");

            StringAssert.Contains(
                "m_PredictionBackend = NetworkPredictionBackend.FusionNative",
                wizard);

            string projectPage = ExtractMethodBody(wizard, "private void DrawProjectPage()");
            StringAssert.Contains("\"Fusion Native (Recommended)\"", projectPage);
            StringAssert.Contains("\"Built-in Legacy\"", projectPage);
            StringAssert.Contains(
                "? NetworkPredictionBackend.FusionNative",
                projectPage);
            StringAssert.Contains(
                ": NetworkPredictionBackend.BuiltIn",
                projectPage);

            string preparePlayer = ExtractMethodBody(
                wizard,
                "private string PreparePlayerPrefab(GameObject prefab)");
            StringAssert.Contains("networkObject.EnableInterpolation = true", preparePlayer);
            StringAssert.Contains(
                "EnsurePrefabComponent<FusionNativeNetworkCharacterMotor>",
                preparePlayer);

            string configureNetworkCharacter = ExtractMethodBody(
                wizard,
                "private bool ConfigureNetworkCharacter(NetworkCharacter character)");
            StringAssert.Contains("\"m_PredictionBackend\"", configureNetworkCharacter);
            StringAssert.Contains(
                "(int)m_PredictionBackend",
                configureNetworkCharacter);
        }

        [Test]
        public void FusionTransportBridge_CentralizesNativeCharacterInputCollection()
        {
            string bridge = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Runtime/Transport/Fusion/" +
                "FusionTransportBridge.cs");
            string onInput = ExtractMethodBody(
                bridge,
                "public void OnInput(NetworkRunner runner, NetworkInput input)");

            StringAssert.Contains("runner.TryGetPlayerObject(runner.LocalPlayer", onInput);
            StringAssert.Contains(
                "playerObject.GetComponent<FusionNativeNetworkCharacterMotor>()",
                onInput);
            StringAssert.Contains("motor?.TryConsumeNetworkInput(runner, input)", onInput);
            Assert.AreEqual(1, CountOccurrences(bridge, "TryConsumeNetworkInput("));

            Assert.IsTrue(
                typeof(INetworkRunnerCallbacks).IsAssignableFrom(typeof(FusionTransportBridge)),
                "The transport bridge must remain the runner's centralized input callback.");

            Assert.IsFalse(
                typeof(INetworkRunnerCallbacks).IsAssignableFrom(
                    typeof(FusionNativeNetworkCharacterMotor)),
                "Per-character motors must not register as competing Fusion input callbacks.");

            string motor = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Runtime/Transport/Fusion/" +
                "FusionNativeNetworkCharacterMotor.cs");
            StringAssert.DoesNotContain("AddCallbacks(", motor);
            StringAssert.DoesNotContain("RemoveCallbacks(", motor);
        }

        [Test]
        public void FusionNativeMovement_PreservesAuthorityPoseAndUsesTickDeterminism()
        {
            string motor = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Runtime/Transport/Fusion/" +
                "FusionNativeNetworkCharacterMotor.cs");
            string driver = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Runtime/Transport/Fusion/" +
                "FusionNativeCharacterDriver.cs");

            StringAssert.Contains("input.SourceTick = tick", motor);
            StringAssert.Contains("bool stateAuthorityNpc", motor);
            StringAssert.Contains("input = m_Driver.CaptureInput(tick)", motor);
            StringAssert.Contains("IsSafePresentationVisualRoot", motor);
            StringAssert.Contains("m_PresentationRootWarningIssued", motor);
            StringAssert.Contains("HasStateAuthority && !IsLocalLogicalOwner", motor);
            StringAssert.Contains("LastAppliedSharedSourceTick", motor);
            StringAssert.Contains("appliedSharedTransient", motor);
            StringAssert.Contains("LastContinuousMove", motor);
            StringAssert.Contains("StoreContinuousInput(input)", motor);
            StringAssert.Contains("CopyToEngine(restoreMotion: false)", motor);
            StringAssert.Contains("updateProcessedInputTick = false", motor);
            StringAssert.Contains("m_HasPendingExternalPosition", motor);
            StringAssert.Contains("m_HasPendingExternalRotation", motor);
            StringAssert.Contains("m_HasPendingExternalScale", motor);
            StringAssert.Contains("IsLocalLogicalOwner && ShouldSimulateLocally", motor);
            StringAssert.Contains("EnsureFiniteEnginePose", motor);
            StringAssert.Contains("m_HasLastValidRootPose", motor);

            string externalTarget = ExtractMethodBody(
                motor,
                "internal void NotifyExternalPositionTarget(Vector3 position)");
            StringAssert.Contains("m_PendingExternalPosition = position", externalTarget);
            StringAssert.Contains(
                "m_PendingExternalPositionIsAbsolute = true",
                externalTarget);
            StringAssert.Contains(
                "m_PendingExternalPositionDelta = Vector3.zero",
                externalTarget);

            StringAssert.Contains("IsServerMotionTickAuthorized(input.SourceTick)", driver);
            StringAssert.Contains("serverAnimationAllowsRootMotion", driver);
            StringAssert.Contains("m_Controller.Move(requestedDelta)", driver);
            StringAssert.Contains("m_LastGroundedTick", driver);
            Assert.IsTrue(
                typeof(INetworkNavMeshCommandSink).IsAssignableFrom(
                    typeof(FusionNativeCharacterDriver)),
                "Fusion Native must shape Point Click/NavMesh commands into tick input.");
            StringAssert.DoesNotContain("Time.realtimeSinceStartup", driver);
            StringAssert.DoesNotContain("Character.Time.Time", driver);
            StringAssert.DoesNotContain("Character.Time.Frame", driver);

            string captureInput = ExtractMethodBody(
                driver,
                "internal FusionNativeCharacterInput CaptureInput(int tick)");
            StringAssert.Contains(": m_SampledYaw", captureInput);
            StringAssert.Contains(
                "TryGetPendingExternalOwnerPoseTarget",
                captureInput,
                "Input collection must sample GC2's retained traversal endpoint after a " +
                "prediction restore instead of sampling the historical Transform pose.");
            StringAssert.Contains("OwnerPosition = includeOwnerPose ? ownerPosition", captureInput);

            string pendingOwnerPose = ExtractDeclaredMethodBody(
                motor,
                "TryGetPendingExternalOwnerPoseTarget");
            StringAssert.Contains(
                "m_PendingExternalPositionCapturedByInput = true",
                pendingOwnerPose);

            string consumeInput = ExtractMethodBody(
                motor,
                "public bool TryConsumeNetworkInput(NetworkRunner runner, NetworkInput input)");
            StringAssert.Contains("runner.InputTick.Raw", consumeInput);
            StringAssert.DoesNotContain("runner.Tick.Raw", consumeInput);
            string setRotation = ExtractMethodBody(
                driver,
                "public override void SetRotation(Quaternion rotation)");
            StringAssert.DoesNotContain("NotifyExternalPosition", setRotation);
            StringAssert.Contains("NotifyExternalRotationChanged", setRotation);
        }

        [Test]
        public void FusionNativeRotation_ResimulationDoesNotOverwriteOwnerSampledYaw()
        {
            string driver = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Runtime/Transport/Fusion/" +
                "FusionNativeCharacterDriver.cs");

            string captureInput = ExtractMethodBody(
                driver,
                "internal FusionNativeCharacterInput CaptureInput(int tick)");
            StringAssert.Contains("Yaw =", captureInput);
            StringAssert.Contains(
                "m_SampledYaw",
                captureInput,
                "Owner input must continue to capture the latest render-frame yaw sample.");

            string simulate = ExtractMethodBody(
                driver,
                "internal void Simulate(");
            StringAssert.Contains("input.Yaw", simulate);
            StringAssert.Contains("Transform.rotation = Quaternion.Euler", simulate);
            StringAssert.DoesNotContain(
                "m_SampledYaw =",
                simulate,
                "Prediction and resimulation replay historical input and must not roll the " +
                "owner's newer render-frame yaw sample backwards.");

            string addRotation = ExtractMethodBody(
                driver,
                "public override void AddRotation(Quaternion amount)");
            StringAssert.Contains("if (!simulationTick)", addRotation);
            StringAssert.Contains("m_SampledYaw = target.eulerAngles.y", addRotation);
            StringAssert.Contains("Transform.rotation * amount", addRotation);
            int tickComposition = addRotation.IndexOf(
                "Transform.rotation * amount",
                StringComparison.Ordinal);
            Assert.GreaterOrEqual(tickComposition, 0);
            string tickAddRotation = addRotation.Substring(tickComposition);
            StringAssert.DoesNotContain(
                "m_SampledYaw =",
                tickAddRotation,
                "Tick root-motion rotation must compose from the restored simulation pose " +
                "without replacing the owner's input accumulator.");
        }

        [Test]
        public void FusionNativePresentation_RootRendersOwnersExceptDuringExternalMotion()
        {
            string motor = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Runtime/Transport/Fusion/" +
                "FusionNativeNetworkCharacterMotor.cs");
            string driver = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Runtime/Transport/Fusion/" +
                "FusionNativeCharacterDriver.cs");

            string render = ExtractMethodBody(motor, "public override void Render()");
            StringAssert.Contains("locallySimulatedOwner", render);
            StringAssert.Contains("m_Driver.RequiresSimulationRootPresentation", render);
            StringAssert.Contains("useLiveOwnerPresentation", render);
            StringAssert.Contains("ApplyLiveExternalRootPresentationPose()", render);
            StringAssert.Contains("m_PresentationRoot", render);
            StringAssert.Contains("NetworkTRSP.Render(", render);
            StringAssert.Contains("transform,", render);
            StringAssert.DoesNotContain(
                "else if (!locallySimulatedOwner)",
                render,
                "Ordinary local owners must not be excluded from Fusion root rendering.");

            StringAssert.Contains("RequiresSimulationRootPresentation", driver);
            StringAssert.Contains("IsOwnerMotionActive(CurrentTick)", driver);
            StringAssert.Contains("IsServerMotionTickAuthorized(CurrentTick)", driver);

            string openOwnerWindow = ExtractMethodBody(
                driver,
                "public void OpenOwnerMotionWindow(float durationSeconds)");
            string openServerWindow = ExtractMethodBody(
                driver,
                "public void OpenServerOwnerMotionWindow(float durationSeconds, uint operationId = 0)");
            StringAssert.DoesNotContain("PrepareForExternalRootWrite", openOwnerWindow);
            StringAssert.DoesNotContain("PrepareForExternalRootWrite", openServerWindow);

            string setPosition = ExtractMethodBody(
                driver,
                "public override void SetPosition(Vector3 position, bool teleport = false)");
            string addPosition = ExtractMethodBody(
                driver,
                "public override void AddPosition(Vector3 amount)");
            StringAssert.Contains("PrepareForExternalRootWrite", setPosition);
            StringAssert.Contains("PrepareForExternalRootWrite", addPosition);

            string prepareExternalWrite = ExtractDeclaredMethodBody(
                motor,
                "PrepareForExternalRootWrite");
            StringAssert.Contains("CopyToEngine", prepareExternalWrite);
            StringAssert.Contains(
                "RememberLiveExternalPresentationPose",
                prepareExternalWrite);
            StringAssert.Contains("useCoherentLiveRoot", prepareExternalWrite);
            StringAssert.Contains("RestorePresentationHierarchy", prepareExternalWrite);

            string livePresentation = ExtractDeclaredMethodBody(
                motor,
                "ApplyLiveExternalRootPresentationPose");
            StringAssert.Contains("m_LiveExternalPresentationPosition", livePresentation);
            StringAssert.Contains("transform.SetPositionAndRotation", livePresentation);
            StringAssert.Contains("m_RootHasRenderPose = true", livePresentation);
            StringAssert.Contains(
                "Runner.GameMode == GameMode.Shared",
                livePresentation);
            StringAssert.Contains(
                "RememberSharedPresentedPose",
                livePresentation,
                "Every Shared local owner must retain the most recent live Traversal pose " +
                "before handing presentation back to tick interpolation.");

            string liveHandoff = ExtractDeclaredMethodBody(
                motor,
                "RememberLiveExternalPresentationPose");
            StringAssert.Contains("LiveOwnerPresentationHandoffSeconds", liveHandoff);
            StringAssert.Contains("m_HasLiveExternalPresentationPose = true", liveHandoff);

            StringAssert.Contains(
                "TryGetInterpolatedAuthoritativeRenderPose",
                motor,
                "Traversal presentation must converge against the snapshot pose NetworkTRSP " +
                "will render, not against the newer simulation state.");
            string interpolatedHandoff = ExtractDeclaredMethodBody(
                motor,
                "TryGetInterpolatedAuthoritativeRenderPose");
            StringAssert.Contains("TryGetSnapshotsBuffers", interpolatedHandoff);
            StringAssert.Contains("Vector3.LerpUnclamped", interpolatedHandoff);
            StringAssert.Contains("Quaternion.SlerpUnclamped", interpolatedHandoff);
            StringAssert.Contains("fromTrsp.TeleportKey != toTrsp.TeleportKey", interpolatedHandoff);
            StringAssert.Contains("renderAlpha >= 0.5f", interpolatedHandoff);
        }

        [Test]
        public void FusionNativePresentation_SharedPredictedOwnerInterpolatesLocalPose()
        {
            string motor = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Runtime/Transport/Fusion/" +
                "FusionNativeNetworkCharacterMotor.cs");
            string render = ExtractMethodBody(motor, "public override void Render()");

            StringAssert.Contains("sharedPredictedOwner", render);
            StringAssert.Contains("RequestSharedPresentationContinuity", render);
            StringAssert.Contains(
                "RenderSharedPredictedOwner()",
                render);
            AssertAppearsBefore(
                render,
                "sharedPredictedOwner",
                "RenderSharedPredictedOwner()");

            string sharedOwnerRender = ExtractDeclaredMethodBody(
                motor,
                "RenderSharedPredictedOwner");
            StringAssert.Contains("Vector3.Lerp", sharedOwnerRender);
            StringAssert.Contains("Quaternion.Slerp", sharedOwnerRender);
            StringAssert.Contains("SetPositionAndRotation", sharedOwnerRender);
            StringAssert.Contains("BeginSharedPresentationContinuity", sharedOwnerRender);
            StringAssert.Contains("m_SharedPresentationPositionError", sharedOwnerRender);
            StringAssert.Contains("RememberSharedPresentedPose", sharedOwnerRender);
            StringAssert.Contains("DecaySharedPresentationError", sharedOwnerRender);
            StringAssert.DoesNotContain(
                "m_PresentationRoot",
                sharedOwnerRender,
                "A non-authoritative Shared owner must interpolate its Character root and " +
                "follow camera, including during Traversal.");
            StringAssert.DoesNotContain(
                "ApplyLiveExternalRootPresentationPose",
                sharedOwnerRender,
                "The live traversal root is selected before Shared interpolation so only one " +
                "presentation writer runs in a frame.");

            string fixedShared = ExtractDeclaredMethodBody(motor, "FixedUpdateShared");
            int nonStateOwner = fixedShared.IndexOf(
                "if (!localOwner) return;",
                StringComparison.Ordinal);
            Assert.GreaterOrEqual(nonStateOwner, 0);
            StringAssert.Contains(
                "SimulateSharedLogicalOwnerProxyTick(tick, restorePredictedPose: false)",
                fixedShared);

            string predictedOwner = ExtractDeclaredMethodBody(
                motor,
                "SimulateSharedLogicalOwnerProxyTick");
            StringAssert.Contains(
                "!isActiveAndEnabled",
                predictedOwner,
                "The runner-level proxy pump must respect a disabled motor.");
            AssertAppearsBefore(
                predictedOwner,
                "ReconcileSharedPrediction",
                "CaptureSharedPredictedPreviousPose");
            AssertAppearsBefore(
                predictedOwner,
                "CaptureSharedPredictedPreviousPose",
                "m_Driver.CaptureInput(tick)");
            AssertAppearsBefore(
                predictedOwner,
                "m_Driver.CaptureInput(tick)",
                "PrepareSharedLocalExternalPose(ref localInput)");
            AssertAppearsBefore(
                predictedOwner,
                "PrepareSharedLocalExternalPose(ref localInput)",
                "m_Driver.Simulate");
            AssertAppearsBefore(
                predictedOwner,
                "m_Driver.Simulate",
                "CaptureSharedPredictedCurrentPose");

            string proxyRender = ExtractDeclaredMethodBody(
                motor,
                "RenderSharedLogicalOwnerProxy");
            StringAssert.Contains("!isActiveAndEnabled", proxyRender);

            int stateOwner = fixedShared.IndexOf(
                "if (HasStateAuthority)",
                StringComparison.Ordinal);
            Assert.GreaterOrEqual(stateOwner, 0);
            string authoritativeOwner = fixedShared.Substring(0, nonStateOwner);
            StringAssert.Contains(
                "CaptureSharedPredictedPreviousStatePose",
                authoritativeOwner,
                "Shared State Authority must retain the pre-external state endpoint.");
            StringAssert.Contains(
                "PrepareSharedLocalExternalPose(ref input)",
                authoritativeOwner,
                "Shared State Authority must consume the local traversal endpoint in Simulate.");

            string prepareShared = ExtractDeclaredMethodBody(
                motor,
                "PrepareSharedLocalExternalPose");
            StringAssert.Contains("input.OwnerPosition = pendingPositionTarget", prepareShared);
            StringAssert.Contains(
                "ApplyPendingExternalPose(applyPosition: !ownerPoseOwnsPosition)",
                prepareShared);

            string reconcile = ExtractDeclaredMethodBody(
                motor,
                "ReconcileSharedPrediction");
            StringAssert.Contains("predictedRotation", reconcile);
            StringAssert.Contains("QueueSharedPresentationCorrection", reconcile);

            string beginContinuity = ExtractDeclaredMethodBody(
                motor,
                "BeginSharedPresentationContinuity");
            StringAssert.Contains(
                "m_LastSharedPresentedPosition - basePosition",
                beginContinuity);
            StringAssert.Contains(
                "m_LastSharedPresentedRotation * Quaternion.Inverse(baseRotation)",
                beginContinuity);
            StringAssert.Contains("maxReconciliationDistance", beginContinuity);

            string decayError = ExtractDeclaredMethodBody(
                motor,
                "DecaySharedPresentationError");
            StringAssert.Contains("m_Profile.reconciliationSpeed", decayError);
            StringAssert.Contains("Time.unscaledDeltaTime", decayError);
            StringAssert.Contains("Mathf.Exp", decayError);

            string resetPresentation = ExtractDeclaredMethodBody(
                motor,
                "ResetSharedPredictedPresentation");
            StringAssert.Contains("ClearSharedPresentationError", resetPresentation);

            string onDisable = ExtractDeclaredMethodBody(motor, "OnDisable");
            AssertAppearsBefore(
                onDisable,
                "RestoreSharedPredictedSimulationPose",
                "ResetSharedPredictedPresentation");
            StringAssert.Contains(
                "m_LastSharedOwnerSimulationTick = int.MinValue",
                onDisable);
        }

        [Test]
        public void FusionNativeExternalAddPosition_CoalescesToAchievedWorldTarget()
        {
            string motor = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Runtime/Transport/Fusion/" +
                "FusionNativeNetworkCharacterMotor.cs");
            string driver = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Runtime/Transport/Fusion/" +
                "FusionNativeCharacterDriver.cs");

            string addPosition = ExtractMethodBody(
                driver,
                "public override void AddPosition(Vector3 amount)");
            StringAssert.Contains("PrepareForExternalRootWrite", addPosition);
            AssertAppearsBefore(
                addPosition,
                "Vector3 requestedPosition = Transform.position + amount",
                "PrepareForExternalRootWrite");
            AssertAppearsBefore(
                addPosition,
                "PrepareForExternalRootWrite",
                "m_Controller.Move(requestedDelta)");
            StringAssert.Contains(
                "NotifyExternalPositionTarget(Transform.position)",
                addPosition);

            string notifyTarget = ExtractMethodBody(
                motor,
                "internal void NotifyExternalPositionTarget(Vector3 position)");
            StringAssert.Contains("m_PendingExternalPosition = position", notifyTarget);
            StringAssert.Contains(
                "m_PendingExternalPositionDelta = Vector3.zero",
                notifyTarget);
            StringAssert.Contains(
                "m_PendingExternalPositionIsAbsolute = true",
                notifyTarget);
            StringAssert.Contains(
                "RememberLiveExternalPresentationPose",
                notifyTarget);

            string fixedUpdate = ExtractMethodBody(
                motor,
                "public override void FixedUpdateNetwork()");
            StringAssert.Contains("ownerPoseOwnsPosition", fixedUpdate);
            StringAssert.Contains("input.HasOwnerPose", fixedUpdate);
            StringAssert.Contains(
                "ApplyPendingExternalPose(applyPosition: !ownerPoseOwnsPosition)",
                fixedUpdate);
            AssertAppearsBefore(
                fixedUpdate,
                "ApplyPendingExternalPose(applyPosition: !ownerPoseOwnsPosition)",
                "m_Driver.Simulate");

            string simulate = ExtractMethodBody(driver, "internal void Simulate(");
            StringAssert.Contains("bool hasOwnerPose = input.HasOwnerPose", simulate);
            StringAssert.Contains("if (!hasOwnerPose && m_Controller.enabled)", simulate);
            AssertAppearsBefore(simulate, "Vector3 before", "TryApplyOwnerPose");
        }

        [Test]
        public void FusionNativeOwnerPose_AcceptsAuthorizedTraversalCatchUpWithoutPartialClamp()
        {
            string driver = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Runtime/Transport/Fusion/" +
                "FusionNativeCharacterDriver.cs");
            string applyOwnerPose = ExtractDeclaredMethodBody(driver, "TryApplyOwnerPose");

            StringAssert.Contains(
                "Mathf.Max(",
                applyOwnerPose,
                "An authorized animation pose needs the reconciliation envelope when its " +
                "eased speed exceeds ordinary locomotion speed.");
            StringAssert.Contains("m_MaxOwnerPoseDistance", applyOwnerPose);
            StringAssert.Contains("maxKineticDistance", applyOwnerPose);
            StringAssert.Contains("if (distance > maxAuthorityDistance)", applyOwnerPose);
            StringAssert.Contains("return false", applyOwnerPose);
            StringAssert.DoesNotContain(
                "Vector3.MoveTowards",
                applyOwnerPose,
                "Partially applying an absolute Traversal pose leaves authority behind and " +
                "causes repeated Fusion corrections on the owning client.");
            AssertAppearsBefore(
                applyOwnerPose,
                "IsServerMotionTickAuthorized(sourceTick)",
                "maxAuthorityDistance");
            AssertAppearsBefore(
                applyOwnerPose,
                "TryGetPositionRejection",
                "maxAuthorityDistance");
            AssertAppearsBefore(
                applyOwnerPose,
                "if (distance > maxAuthorityDistance)",
                "m_Controller.Move(requestedDelta)");
            StringAssert.Contains("CollisionFlags collisionFlags", applyOwnerPose);
            StringAssert.Contains("residualDistance > applicationTolerance", applyOwnerPose);
            StringAssert.Contains("m_LastAcceptedOwnerPoseTick = int.MinValue", applyOwnerPose);
            StringAssert.Contains("LogOwnerPoseCollisionBlocked", applyOwnerPose);
        }

        [Test]
        public void FusionNativeTraversal_DoesNotHoldStalePoseOrResetCollisionOverrides()
        {
            string driver = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Runtime/Transport/Fusion/" +
                "FusionNativeCharacterDriver.cs");
            string motor = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Runtime/Transport/Fusion/" +
                "FusionNativeNetworkCharacterMotor.cs");

            string captureInput = ExtractDeclaredMethodBody(driver, "CaptureInput");
            StringAssert.Contains("hasPendingOwnerPosition &&", captureInput);
            StringAssert.Contains("RootMotionDelta = rootMotionDelta", captureInput);
            StringAssert.Contains("RootMotionWeight = rootMotionWeight", captureInput);
            StringAssert.Contains("LogOwnerMotionCapture", captureInput);

            string setRootPosition = ExtractDeclaredMethodBody(driver, "SetRootPosition");
            StringAssert.Contains("Transform.position = position", setRootPosition);
            StringAssert.Contains("Physics.SyncTransforms()", setRootPosition);
            StringAssert.DoesNotContain("m_Controller.enabled = false", setRootPosition);

            string copyToEngine = ExtractDeclaredMethodBody(motor, "CopyToEngine");
            StringAssert.Contains("transform.SetPositionAndRotation", copyToEngine);
            StringAssert.Contains("Physics.SyncTransforms()", copyToEngine);
            StringAssert.DoesNotContain("m_Controller.enabled = false", copyToEngine);

            string restoreShared = ExtractDeclaredMethodBody(
                motor,
                "RestoreSharedPredictedSimulationPose");
            StringAssert.Contains("Physics.SyncTransforms()", restoreShared);
            StringAssert.DoesNotContain("m_Controller.enabled = false", restoreShared);

            StringAssert.DoesNotContain(
                "if (m_Driver?.RequiresSimulationRootPresentation == true ||",
                motor,
                "An authorization window must not keep replaying a stale absolute warp pose " +
                "after Vault/Jump has transitioned to root motion.");
            string liveSimulationAdvance = ExtractDeclaredMethodBody(
                motor,
                "HasLocalSimulationAdvancedBeyondLivePresentationPose");
            StringAssert.Contains("m_SharedCurrentPredictedPosition", liveSimulationAdvance);
            StringAssert.Contains(
                "LiveOwnerSimulationAdvancePositionTolerance",
                liveSimulationAdvance);

            string resetTransient = ExtractDeclaredMethodBody(
                driver,
                "ResetNetworkTransientState");
            StringAssert.Contains("m_OwnerMotionUntilTick = int.MinValue", resetTransient);
            StringAssert.Contains("m_ServerMotionAuthorizations", resetTransient);
            StringAssert.Contains("m_ServerMotionAuthorizationCount = 0", resetTransient);
            StringAssert.Contains("m_SampledRootMotionVelocity = Vector3.zero", resetTransient);
            StringAssert.Contains("m_LastAcceptedOwnerPoseTick = int.MinValue", resetTransient);
            StringAssert.Contains(
                "m_WasMotionJumping = Character?.Motion?.IsJumping == true",
                resetTransient);
            StringAssert.Contains("bool groundedNow", resetTransient);
            StringAssert.Contains("m_WasGrounded = groundedNow", resetTransient);

            string spawned = ExtractMethodBody(motor, "public override void Spawned()");
            string despawned = ExtractMethodBody(
                motor,
                "public override void Despawned(NetworkRunner runner, bool hasState)");
            string identityChanged = ExtractDeclaredMethodBody(motor, "OnIdentityChanged");
            string authorityChanged = ExtractMethodBody(
                motor,
                "public void StateAuthorityChanged()");
            StringAssert.Contains("ResetNetworkTransientState", spawned);
            StringAssert.Contains("ResetNetworkTransientState", despawned);
            StringAssert.Contains("ResetNetworkTransientState", identityChanged);
            StringAssert.Contains("ResetNetworkTransientState", authorityChanged);
            StringAssert.Contains(
                "m_ResetReplicatedOwnerInputStatePending",
                identityChanged);
            StringAssert.Contains(
                "m_ResetReplicatedOwnerInputStatePending = true",
                identityChanged,
                "An owner change observed while this peer is a proxy must remain checkable if " +
                "Shared State Authority migrates here later.");
            string resetReplicatedOwner = ExtractDeclaredMethodBody(
                motor,
                "ApplyPendingReplicatedOwnerInputReset");
            StringAssert.Contains(
                "m_Driver?.ResetNetworkTransientState()",
                resetReplicatedOwner,
                "BeforeAllTicks may have restored the previous owner's motion into the driver " +
                "before the deferred replicated-state reset executes.");
            StringAssert.Contains(
                "NativeState.LastProcessedInputTick = int.MinValue",
                resetReplicatedOwner);
            StringAssert.Contains(
                "NativeState.LastAppliedSharedSourceTick = int.MinValue",
                resetReplicatedOwner);
            StringAssert.Contains(
                "NativeState.LastContinuousMove = Vector2.zero",
                resetReplicatedOwner);
            StringAssert.Contains(
                "NativeState.LastAcceptedOwnerPoseTick = int.MinValue",
                resetReplicatedOwner);
            StringAssert.Contains(
                "NativeState.InputStateOwnerRaw == currentOwnerRaw",
                resetReplicatedOwner,
                "Same-owner master migration must retain valid replicated acknowledgements.");
            StringAssert.Contains(
                "NativeState.InputStateOwnerRaw = currentOwnerRaw",
                resetReplicatedOwner,
                "A reset baseline must be stamped with the logical owner it represents.");
            AssertAppearsBefore(
                resetReplicatedOwner,
                "m_Driver?.ResetNetworkTransientState()",
                "UpdateMotionState()");
            StringAssert.Contains(
                "m_ResetReplicatedOwnerInputStatePending =",
                authorityChanged,
                "Every authority transition must schedule an owner-stamp consistency check " +
                "without relying on Fusion callback/property ordering.");
        }

        [Test]
        public void FusionNativePresentation_LagHistoryUsesAuthoritativeSimulationPose()
        {
            Assert.IsTrue(
                typeof(INetworkAuthoritativePoseProvider).IsAssignableFrom(
                    typeof(FusionNativeNetworkCharacterMotor)),
                "Fusion Native must expose its current tick pose while rendering the root.");

            string lagAdapter = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Runtime/LagCompensation/" +
                "CharacterLagCompensation.cs");
            string networkCharacter = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Runtime/Character/NetworkCharacter.cs");
            string motor = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Runtime/Transport/Fusion/" +
                "FusionNativeNetworkCharacterMotor.cs");
            StringAssert.Contains("INetworkAuthoritativePoseProvider", lagAdapter);
            StringAssert.Contains("TryGetAuthoritativePose", lagAdapter);
            StringAssert.Contains("Vector3 center = Position", lagAdapter);
            StringAssert.Contains("result.hitPoint.y - Position.y", lagAdapter);
            StringAssert.Contains("m_FallbackAuthoritativePoseProviderResolved", lagAdapter);
            StringAssert.Contains("m_NetworkCharacter.TryGetAuthoritativePose", lagAdapter);
            StringAssert.Contains(
                "m_ActivePredictionBackend is not INetworkAuthoritativePoseProvider",
                networkCharacter);

            string providePose = ExtractMethodBody(
                motor,
                "public bool TryGetAuthoritativePose(");
            StringAssert.Contains("!m_BackendInitialized", providePose);
        }

        [Test]
        public void NetworkCharacter_AuthoritativePoseDelegatesOnlyToActiveEnabledBackend()
        {
            var gameObject = new GameObject("Active pose backend isolation test");
            try
            {
                NetworkCharacter character = gameObject.AddComponent<NetworkCharacter>();
                FusionTestAuthoritativePoseBackend backend =
                    gameObject.AddComponent<FusionTestAuthoritativePoseBackend>();
                backend.Position = new Vector3(4f, 5f, 6f);
                backend.Rotation = Quaternion.Euler(0f, 75f, 0f);

                FieldInfo activeBackend = typeof(NetworkCharacter).GetField(
                    "m_ActivePredictionBackend",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(activeBackend);

                Assert.IsFalse(character.TryGetAuthoritativePose(out _, out _));
                Assert.AreEqual(0, backend.PoseReadCount);

                activeBackend.SetValue(character, backend);
                Assert.IsTrue(character.TryGetAuthoritativePose(
                    out Vector3 position,
                    out Quaternion rotation));
                Assert.AreEqual(backend.Position, position);
                Assert.AreEqual(backend.Rotation, rotation);
                Assert.AreEqual(1, backend.PoseReadCount);

                backend.enabled = false;
                Assert.IsFalse(character.TryGetAuthoritativePose(out _, out _));
                Assert.AreEqual(1, backend.PoseReadCount);

                activeBackend.SetValue(character, null);
                backend.enabled = true;
                Assert.IsFalse(character.TryGetAuthoritativePose(out _, out _));
                Assert.AreEqual(1, backend.PoseReadCount);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void FusionNativeLifecycle_PreservesForwardExternalMotionAndClearsOldQueues()
        {
            Assert.IsTrue(
                typeof(IStateAuthorityChanged).IsAssignableFrom(
                    typeof(FusionNativeNetworkCharacterMotor)));

            string motor = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Runtime/Transport/Fusion/" +
                "FusionNativeNetworkCharacterMotor.cs");
            string beforeAll = ExtractMethodBody(
                motor,
                "void IBeforeAllTicks.BeforeAllTicks(bool resimulation, int tickCount)");
            StringAssert.DoesNotContain("ClearPendingExternalChanges", beforeAll);
            StringAssert.DoesNotContain("ApplyPendingExternalPose", beforeAll);
            StringAssert.Contains("m_ForwardExternalChangesHandled = false", beforeAll);

            string fixedUpdate = ExtractMethodBody(
                motor,
                "public override void FixedUpdateNetwork()");
            StringAssert.Contains("ownerPoseOwnsPosition", fixedUpdate);
            StringAssert.Contains("ApplyPendingExternalPose", fixedUpdate);
            AssertAppearsBefore(
                fixedUpdate,
                "ApplyPendingExternalPose",
                "m_Driver.Simulate");

            string beforePrevious = ExtractMethodBody(
                motor,
                "void IBeforeCopyPreviousState.BeforeCopyPreviousState()");
            StringAssert.Contains("if (HasPendingExternalChanges) return", beforePrevious);
            StringAssert.Contains("CopyToBuffer()", beforePrevious);
            AssertAppearsBefore(
                beforePrevious,
                "if (HasPendingExternalChanges) return",
                "CopyToBuffer()");

            string afterAll = ExtractMethodBody(
                motor,
                "void IAfterAllTicks.AfterAllTicks(bool resimulation, int tickCount)");
            StringAssert.Contains("preserveUnconsumedPosition", afterAll);

            string clearPending = ExtractDeclaredMethodBody(
                motor,
                "ClearPendingExternalChanges");
            StringAssert.Contains("m_PendingExternalPositionCapturedByInput", clearPending);
            StringAssert.Contains("m_PendingExternalPositionApplied", clearPending);

            string despawned = ExtractMethodBody(
                motor,
                "public override void Despawned(NetworkRunner runner, bool hasState)");
            StringAssert.Contains("ResetSharedRuntimeState", despawned);
            StringAssert.Contains("ClearPendingExternalChanges", despawned);

            string authorityChanged = ExtractMethodBody(
                motor,
                "public void StateAuthorityChanged()");
            StringAssert.Contains("ResetSharedRuntimeState", authorityChanged);
            StringAssert.Contains("ClearPendingExternalChanges", authorityChanged);

            string resetShared = ExtractDeclaredMethodBody(motor, "ResetSharedRuntimeState");
            StringAssert.Contains(
                "m_NextSharedTransientSubmitDiagnosticTime = 0f",
                resetShared);

            string reconcileShared = ExtractDeclaredMethodBody(
                motor,
                "ReconcileSharedPrediction");
            StringAssert.Contains("Object.LastReceiveTick.Raw", reconcileShared);
            StringAssert.Contains("authoritativeStateTick", reconcileShared);
            StringAssert.Contains(
                "representedContinuousTick = NativeState.LastProcessedInputTick",
                reconcileShared,
                "Continuous prediction uses the owner-clock baseline represented by the " +
                "authoritative snapshot, not another peer's raw runner tick.");
            StringAssert.Contains(
                "acknowledgedTransientTick = NativeState.LastAppliedSharedSourceTick",
                reconcileShared,
                "Vault, Jump and root-motion samples remain replayable until an actual owner " +
                "payload acknowledgement arrives.");
            StringAssert.Contains("HasSharedTransientInput(predicted)", reconcileShared);
            StringAssert.Contains("ClearSharedTransientInput(ref replay)", reconcileShared);
            StringAssert.Contains("replay.Move = Vector2.zero", reconcileShared);
            StringAssert.Contains("if (authoritativeTeleport)", reconcileShared);
            AssertAppearsBefore(
                reconcileShared,
                "m_SharedPredictionCount = 0",
                "for (int i = 0; i < historyCountBefore; i++)");

            string transientClassifier = ExtractDeclaredMethodBody(
                motor,
                "HasSharedTransientInput");
            StringAssert.Contains(
                "input.HasOwnerPose && !input.HasContinuousOwnerPose",
                transientClassifier,
                "Continuous climb poses must not build a reliable one-shot backlog.");
        }

        [Test]
        public void FusionFreeClimb_UsesOneStateStarterAndRepairsOnlyMissingState()
        {
            string traversal = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Traversal/NetworkTraversalController.cs");
            string manager = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Traversal/NetworkTraversalManager.cs");
            string driver = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Runtime/Transport/Fusion/" +
                "FusionNativeCharacterDriver.cs");
            string motor = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Runtime/Transport/Fusion/" +
                "FusionNativeNetworkCharacterMotor.cs");

            string motionEnter = ExtractDeclaredMethodBody(
                traversal,
                "OnLocalTraversalMotionEnter");
            StringAssert.DoesNotContain(
                "StartHostLocalInteractiveMotionState",
                motionEnter,
                "MotionInteractive.Enter owns the live Climb State start; prestarting it from " +
                "EventMotionEnter creates two playables on the same layer.");

            string snapshotRestore = ExtractDeclaredMethodBody(
                traversal,
                "TryRestoreInteractiveSnapshot");
            StringAssert.Contains(
                "StartHostLocalInteractiveMotionState",
                snapshotRestore,
                "Snapshot restoration does not execute MotionInteractive.Enter and still needs " +
                "an explicit presentation state.");

            string authoritativeApply = ExtractDeclaredMethodBody(
                traversal,
                "RunClientAuthoritativeStateApplyAsync");
            StringAssert.Contains(
                "EnsureInteractiveMotionStateAfterEnterAsync",
                authoritativeApply);
            StringAssert.Contains("IsExpectedStateActive", traversal);
            StringAssert.Contains("[TraversalAnimDebug][StateRepair]", traversal);

            StringAssert.Contains("ContinuousOwnerPoseRequested", manager);
            StringAssert.Contains("IsContinuousInteractiveOwnerPose", manager);
            StringAssert.Contains(
                "NetworkOwnerMotionAuthorityHooks.IsContinuousOwnerPose(Character)",
                driver);
            StringAssert.Contains(
                "authoritative && m_Motor?.IsResimulating != true",
                driver,
                "Fusion resimulation must not rewind live TraversalStance relative state.");
            StringAssert.Contains(
                "input.HasOwnerPose && !input.HasContinuousOwnerPose",
                motor);

            StringAssert.Contains(
                "public const int EXTRA_WORDS = 17",
                motor,
                "The native state must reserve three words for persistent traversal " +
                "presentation velocity.");
            StringAssert.Contains(
                "public Vector3 TraversalPresentationVelocity",
                motor,
                "Free Climb direction must be persistent Fusion state for observers and " +
                "late joiners, not inferred from intermittent position deltas.");
            StringAssert.Contains(
                "MotionFlagTraversalPresentation",
                motor,
                "An attached idle climber needs an explicit zero direction rather than a " +
                "fallback to physical tick velocity.");

            string updateMotionState = ExtractDeclaredMethodBody(
                motor,
                "UpdateMotionState");
            StringAssert.Contains(
                "NetworkOwnerMotionAuthorityHooks.IsContinuousOwnerPose(character)",
                updateMotionState);
            StringAssert.Contains(
                "motion.TryGetTraversalPresentationDirection(out Vector3 direction)",
                updateMotionState);
            StringAssert.Contains(
                "NativeState.TraversalPresentationVelocity = direction",
                updateMotionState);

            string render = ExtractDeclaredMethodBody(motor, "Render");
            StringAssert.Contains(
                "fromState.TraversalPresentationVelocity",
                render);
            StringAssert.Contains(
                "toState.TraversalPresentationVelocity",
                render);
            StringAssert.Contains(
                "ApplyReplicatedTraversalPresentationDirection",
                render);
            StringAssert.Contains(
                "m_Driver.ApplyReplicatedMotion(presentationVelocity, grounded)",
                render,
                "Remote GC2 blend trees must consume the persistent semantic traversal " +
                "direction instead of pulse-prone per-tick displacement velocity.");

            string setExternalMoveDirection = ExtractDeclaredMethodBody(
                driver,
                "SetExternalMoveDirection");
            StringAssert.Contains(
                "m_Motor?.IsRemoteProxyRole == true",
                setExternalMoveDirection,
                "A remote MotionInteractive emits synthetic zero directions without local " +
                "input; only Fusion Render may write the remote driver's presentation motion.");
            AssertAppearsBefore(
                setExternalMoveDirection,
                "m_Motor?.IsRemoteProxyRole == true",
                "m_MoveVelocity = velocity");
            StringAssert.Contains(
                "if (preserveWhileTraversalLikeMotion)",
                setExternalMoveDirection,
                "A locally simulated host must preserve semantic Traversal presentation " +
                "separately from tick displacement.");
            StringAssert.Contains(
                "m_HasExplicitPresentationVelocity = true",
                setExternalMoveDirection,
                "An explicit zero is an active idle-climb presentation value, not an absent " +
                "override.");
            AssertAppearsBefore(
                setExternalMoveDirection,
                "if (preserveWhileTraversalLikeMotion)",
                "m_MoveVelocity = velocity");

            StringAssert.Contains(
                "m_HasExplicitPresentationVelocity",
                driver);
            StringAssert.Contains(
                "PresentationMoveVelocity",
                driver);
            StringAssert.Contains(
                "internal Vector3 SimulationVelocity => m_MoveVelocity",
                driver);
            StringAssert.Contains(
                "NativeState.Velocity = m_Driver.SimulationVelocity",
                updateMotionState,
                "Rollback must retain physical velocity while Animim consumes semantic " +
                "Traversal presentation velocity.");
        }

        [Test]
        public void FusionProjectConfig_UsesRedundantInputHistoryForNativePrediction()
        {
            string projectConfig = ReadAssetSource(
                "Photon/Fusion/Resources/NetworkProjectConfig.fusion");
            StringAssert.Contains(
                "\"InputTransferMode\": 0",
                projectConfig,
                "Fusion Redundancy is enum value zero in the installed SDK. LatestState drops " +
                "the historical owner poses required by rollback and fast traversal.");

            string wizard = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Editor/Transport/Fusion/" +
                "FusionSceneSetupWizard.cs");
            StringAssert.Contains(
                "SimulationConfig.InputTransferModes.Redundancy",
                wizard);

            string validation = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Editor/Transport/Fusion/" +
                "FusionSceneSetupValidation.cs");
            StringAssert.Contains(
                "SimulationConfig.InputTransferModes.Redundancy",
                validation);
            StringAssert.Contains(
                "Latest State causes connected-owner rollback stutter",
                validation);

            string bootstrap = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Runtime/Transport/Fusion/" +
                "FusionSessionBootstrap.cs");
            string runtimeConfig = ExtractDeclaredMethodBody(
                bootstrap,
                "EnsureRuntimeProjectConfiguration");
            StringAssert.Contains("NetworkProjectConfig.Global", runtimeConfig);
            StringAssert.Contains(
                "SimulationConfig.InputTransferModes.Redundancy",
                runtimeConfig);
            StringAssert.DoesNotContain("new NetworkProjectConfig", runtimeConfig);
            StringAssert.DoesNotContain("JsonUtility.", runtimeConfig);
            StringAssert.DoesNotContain(
                "Config =",
                ExtractMethodBody(
                    bootstrap,
                    "private async Task<StartGameResult> StartSessionAsync("),
                "StartGameArgs must use Fusion's populated global config so its runtime prefab " +
                "table remains available to the object provider.");
        }

        [Test]
        public void FusionTraversalDemoPlayer_IsInTheGlobalRuntimePrefabTable()
        {
            const string prefabPath =
                "Assets/Arawn/NetworkingLayerForGC2/Demo/Fusion/Prefabs/" +
                "FusionDemoPlayer-Traversal.prefab";
            string assetGuid = AssetDatabase.AssetPathToGUID(prefabPath);
            Assert.IsNotEmpty(assetGuid, $"Prefab asset was not found: {prefabPath}");

            NetworkProjectConfig config = NetworkProjectConfig.Global;
            Assert.NotNull(config);
            Assert.NotNull(config.PrefabTable);

            var networkGuid = new NetworkObjectGuid(assetGuid);
            NetworkPrefabId prefabId = config.PrefabTable.GetId(networkGuid);
            Assert.IsTrue(
                prefabId.IsValid,
                $"Fusion's global prefab table cannot resolve {prefabPath} ({networkGuid}).");
        }

        [Test]
        public void FusionServerTime_WaitsForSharedRuntimeConfiguration()
        {
            string bridge = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Runtime/Transport/Fusion/" +
                "FusionTransportBridge.cs");
            StringAssert.Contains("IsRunnerTimeReady ? m_Runner.SimulationTime", bridge);
            StringAssert.Contains("m_Runner.Tick.Raw > 0", bridge);
            AssertAppearsBefore(
                bridge,
                "m_Runner.Tick.Raw > 0",
                "private double GetLagCompensationServerTime()");
        }

        [Test]
        public void FusionDemoPlayers_ArePreparedForFusionNativeMovement()
        {
            string demoRoot = Path.Combine(
                Application.dataPath,
                "Arawn/NetworkingLayerForGC2/Demo/Fusion");
            string[] fusionPrefabs = Directory
                .EnumerateFiles(demoRoot, "*.prefab", SearchOption.AllDirectories)
                .ToArray();

            int networkCharacterPrefabCount = 0;
            foreach (string prefabPath in fusionPrefabs)
            {
                string assetPath = "Assets" + prefabPath
                    .Substring(Application.dataPath.Length)
                    .Replace('\\', '/');
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
                NetworkCharacter character =
                    prefab != null
                        ? prefab.GetComponentInChildren<NetworkCharacter>(true)
                        : null;
                if (character == null) continue;

                networkCharacterPrefabCount++;
                Assert.AreEqual(
                    NetworkPredictionBackend.FusionNative,
                    character.PredictionBackend,
                    $"Fusion demo player does not use Fusion-native movement: {assetPath}");

                FusionNativeNetworkCharacterMotor motor =
                    character.GetComponent<FusionNativeNetworkCharacterMotor>();
                Assert.NotNull(
                    motor,
                    $"Fusion demo player is missing its Fusion-native motor: {assetPath}");
                Assert.IsTrue(
                    motor.enabled,
                    $"Fusion demo player's Fusion-native motor is disabled: {assetPath}");
                Assert.NotNull(
                    motor.ListenHostPresentationVisualRoot,
                    $"Fusion demo player has no explicit listen-host visual root: {assetPath}");
                Assert.IsTrue(
                    FusionNativeNetworkCharacterMotor.IsSafePresentationVisualRoot(
                        character.transform,
                        motor.ListenHostPresentationVisualRoot),
                    $"Fusion demo player's listen-host root contains gameplay components or " +
                    $"is not a direct visual child: {assetPath}");

                NetworkObject networkObject = character.GetComponent<NetworkObject>();
                Assert.NotNull(
                    networkObject,
                    $"Fusion demo player is missing its NetworkObject: {assetPath}");
                Assert.IsTrue(
                    networkObject.EnableInterpolation,
                    $"Fusion demo player has NetworkObject interpolation disabled: {assetPath}");
                Assert.AreNotEqual(
                    0,
                    networkObject.Flags & NetworkObjectFlags.HasMainNetworkTRSP,
                    $"Fusion demo player's native motor is not marked as the main TRSP: " +
                    assetPath);

                IEnumerable networkedBehaviours = networkObject.NetworkedBehaviours;
                Assert.NotNull(
                    networkedBehaviours,
                    $"Fusion demo player's NetworkObject has no baked behaviours: {assetPath}");
                CollectionAssert.Contains(
                    networkedBehaviours,
                    motor,
                    $"Fusion demo player's native motor is absent from NetworkedBehaviours: " +
                    assetPath);
                Assert.AreSame(
                    motor,
                    networkObject.NetworkedBehaviours.FirstOrDefault(),
                    $"Fusion demo player's native motor is not the first/main baked behaviour: " +
                    assetPath);
            }

            Assert.Greater(networkCharacterPrefabCount, 0);
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
        public void PreSpawnIdentity_DefersAuthorityRefreshWithoutReadingNetworkedState()
        {
            var gameObject = new GameObject("Pre-Spawn Fusion Identity Test");
            try
            {
                FusionNetworkIdentity identity =
                    gameObject.AddComponent<FusionNetworkIdentity>();
                Assert.NotNull(gameObject.GetComponent<NetworkObject>());
                Assert.IsFalse(identity.IsSpawned);
                Assert.IsFalse(identity.HasAuthorityAdmission);
                Assert.IsFalse(identity.TryGetLogicalOwnerClientId(out _));

                MethodInfo refresh = typeof(FusionNetworkIdentity).GetMethod(
                    "RefreshAuthorityRole",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(refresh);

                object result = null;
                Assert.DoesNotThrow(() => result = refresh.Invoke(identity, null));
                Assert.AreEqual(false, result);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void AuthorityReadinessRecovery_GuardsSpawnRaceAndStaleSnapshot()
        {
            string transportSource = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Runtime/Transport/Fusion/" +
                "FusionTransportBridge.cs");
            string refreshIdentities = ExtractMethodBody(
                transportSource,
                "private void RefreshNetworkIdentities()");
            StringAssert.Contains("if (!identity.IsSpawned)", refreshIdentities);
            StringAssert.Contains("identity.Runner != m_Runner", refreshIdentities);
            StringAssert.Contains("catch (Exception exception)", refreshIdentities);

            string beginSnapshot = ExtractMethodBody(
                transportSource,
                "private void BeginClientSnapshot(uint clientId, bool forceSnapshot)");
            AssertAppearsBefore(
                beginSnapshot,
                "m_SnapshotInProgressClients.Remove(clientId);",
                "uint snapshotToken = ++m_NextSnapshotToken;");
            AssertAppearsBefore(
                beginSnapshot,
                "m_PendingSnapshotTokens.Remove(clientId);",
                "uint snapshotToken = ++m_NextSnapshotToken;");

            string update = ExtractMethodBody(transportSource, "private void Update()");
            StringAssert.Contains(
                "m_LocalSnapshotCompletedEpoch != m_AuthorityEpoch",
                update);
            StringAssert.Contains("SendGameplayReadyIntent();", update);

            string autoSource = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Runtime/Transport/Fusion/" +
                "FusionNetworkCharacterAuto.cs");
            string readinessUpdate = ExtractMethodBody(
                autoSource,
                "private void Update()");
            StringAssert.Contains("TryNotifyGameplayReady();", readinessUpdate);
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
        public void FusionRegionCatalog_ContainsBestRegionAndAllPublicGamingRegions()
        {
            CollectionAssert.AreEqual(
                new[]
                {
                    "",
                    "asia",
                    "au",
                    "cae",
                    "cn",
                    "eu",
                    "hk",
                    "in",
                    "jp",
                    "za",
                    "sa",
                    "kr",
                    "tr",
                    "uae",
                    "us",
                    "usw",
                    "ussc"
                },
                FusionRegionCatalog.Codes);

            Assert.AreEqual(FusionRegionCatalog.BestRegionCode, FusionRegionCatalog.Normalize(null));
            Assert.AreEqual(FusionRegionCatalog.BestRegionCode, FusionRegionCatalog.Normalize("  "));
            Assert.AreEqual("eu", FusionRegionCatalog.Normalize(" EU "));
            Assert.IsTrue(FusionRegionCatalog.IsKnown("JP"));
            Assert.IsFalse(FusionRegionCatalog.IsKnown("custom-cluster"));
        }

        [Test]
        public void FusionRegionEditors_UseTheSharedCatalogAndKeepRuntimeStringStorage()
        {
            string inspector = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Editor/Transport/Fusion/" +
                "FusionSessionBootstrapEditor.cs");
            StringAssert.Contains("FusionRegionCatalog.DrawSerializedProperty(property)", inspector);

            string catalog = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Editor/Transport/Fusion/" +
                "FusionRegionCatalog.cs");
            StringAssert.Contains("EditorGUI.BeginProperty", catalog);
            StringAssert.Contains("property.hasMultipleDifferentValues", catalog);
            StringAssert.Contains("Photon named sessions are region-scoped", inspector);

            string wizard = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Editor/Transport/Fusion/" +
                "FusionSceneSetupWizard.cs");
            StringAssert.Contains("m_Region = FusionRegionCatalog.DrawPopup(", wizard);
            StringAssert.DoesNotContain("m_Region = EditorGUILayout.TextField(", wizard);

            string runtime = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Runtime/Transport/Fusion/" +
                "FusionSessionBootstrap.cs");
            StringAssert.Contains("private string m_Region = string.Empty;", runtime);
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
            StringAssert.Contains("args.CustomPhotonAppSettings = appSettings", startCore);
            StringAssert.Contains("Best Region is automatic", source);
            StringAssert.DoesNotContain(
                "if (!string.IsNullOrWhiteSpace(options.Region))",
                startCore);
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
                "FusionSetupReport postflight",
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

        private static string ExtractDeclaredMethodBody(string source, string methodName)
        {
            string declarationPattern =
                @"(?:public|private|internal|protected)\s+" +
                @"(?:static\s+)?(?:async\s+)?" +
                @"[\w<>,.?\[\]]+\s+" +
                Regex.Escape(methodName) +
                @"\s*\(";
            Match declaration = Regex.Match(source, declarationPattern);
            Assert.IsTrue(
                declaration.Success,
                $"Method declaration was not found: {methodName}");

            return ExtractMethodBody(source, declaration.Value);
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
