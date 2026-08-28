using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Arawn.GameCreator2.Networking.Editor;
using Arawn.GameCreator2.Networking.TestUtilities;
using Arawn.GameCreator2.Networking.Transport.Fusion.Editor;
using Fusion;
using GameCreator.Runtime.Characters;
using NUnit.Framework;
using Assert = NUnit.Framework.Assert;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;
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
            StringAssert.Contains("\"define\": \"FUSION_FORCE_WEAVING\"", assemblyDefinition);

            string validation = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Editor/Transport/Fusion/" +
                "FusionSceneSetupValidation.cs");
            StringAssert.Contains("ValidateRuntimeAssemblyDefinition(report)", validation);
            StringAssert.Contains("if (!definition.allowUnsafeCode)", validation);
            StringAssert.Contains("ForceWeavingDefine", validation);
            StringAssert.Contains("AssetDatabase.FindAssets(\"t:asmdef\")", validation);
            StringAssert.Contains(
                "Fusion weave the transport RPC assembly immediately after import",
                validation);
        }

        [Test]
        public void FusionWizard_NormalizesOnlyItsRuntimeWeaveRegistration()
        {
            const string runtimeAssembly =
                "Arawn.GameCreator2.Networking.Transport.Fusion";
            Type wizardType = RequireEditorType(
                "Arawn.GameCreator2.Networking.Transport.Fusion.Editor." +
                "FusionSceneSetupWizard");
            MethodInfo ensureEntry = wizardType.GetMethod(
                "EnsureRuntimeAssemblyWeaveEntry",
                StaticNonPublic);
            Assert.NotNull(ensureEntry);

            var empty = new NetworkProjectConfig { AssembliesToWeave = null };
            Assert.IsTrue((bool)ensureEntry.Invoke(null, new object[] { empty }));
            CollectionAssert.AreEqual(
                new[] { runtimeAssembly },
                empty.AssembliesToWeave);

            var missing = new NetworkProjectConfig
            {
                AssembliesToWeave = new[] { "Fusion.Unity", "Customer.Transport" }
            };
            Assert.IsTrue((bool)ensureEntry.Invoke(null, new object[] { missing }));
            CollectionAssert.AreEqual(
                new[] { "Fusion.Unity", "Customer.Transport", runtimeAssembly },
                missing.AssembliesToWeave);

            var exact = new NetworkProjectConfig
            {
                AssembliesToWeave = new[] { "Fusion.Unity", runtimeAssembly }
            };
            Assert.IsFalse((bool)ensureEntry.Invoke(null, new object[] { exact }));
            CollectionAssert.AreEqual(
                new[] { "Fusion.Unity", runtimeAssembly },
                exact.AssembliesToWeave);

            var caseVariants = new NetworkProjectConfig
            {
                AssembliesToWeave = new[]
                {
                    "Fusion.Unity",
                    runtimeAssembly.ToLowerInvariant(),
                    "Customer.Transport",
                    runtimeAssembly,
                    "Customer.Transport"
                }
            };
            Assert.IsTrue((bool)ensureEntry.Invoke(null, new object[] { caseVariants }));
            CollectionAssert.AreEqual(
                new[]
                {
                    "Fusion.Unity",
                    runtimeAssembly,
                    "Customer.Transport",
                    "Customer.Transport"
                },
                caseVariants.AssembliesToWeave,
                "Only the Arawn entry should be canonicalized/deduplicated; unrelated customer " +
                "entries and ordering must be preserved.");
        }

        [Test]
        public void FusionWizard_RepairsProjectConfigWithoutPreflightDeadlock()
        {
            string validation = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Editor/Transport/Fusion/" +
                "FusionSceneSetupValidation.cs");
            string validateProjectConfig = ExtractMethodBody(
                validation,
                "private static void ValidateProjectConfig(");
            StringAssert.Contains("requireAppliedInfrastructure", validateProjectConfig);
            StringAssert.Contains("FusionSetupIssueSeverity.Warning", validateProjectConfig);
            StringAssert.Contains("FusionSetupIssueSeverity.Info", validateProjectConfig);
            StringAssert.Contains("StringComparer.OrdinalIgnoreCase", validateProjectConfig);
            StringAssert.Contains(
                "Create / Update Scene Setup will repair it automatically",
                validateProjectConfig);

            string wizard = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Editor/Transport/Fusion/" +
                "FusionSceneSetupWizard.cs");
            string runSetup = ExtractMethodBody(
                wizard,
                "private bool RunSetup(bool showDialogs)");
            StringAssert.Contains(
                "bool weaveRegistrationChanged = ApplyFusionProjectConfiguration();",
                runSetup);
            StringAssert.Contains("ILWeaverUtils.RunWeaver();", runSetup);
            AssertAppearsBefore(
                runSetup,
                "assetTransaction.Commit();",
                "ILWeaverUtils.RunWeaver();");
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
                "FusionCharacterInputUtility.HasSharedTransientInput(input)",
                submitInput);
            StringAssert.Contains(
                "input.HasContinuousOwnerPose",
                submitInput,
                "Replaceable MotionInteractive poses must travel with latest-state intent.");
            StringAssert.Contains("EnqueueSharedCharacterTransient(input)", submitInput);
            StringAssert.Contains("TrySendQueuedSharedCharacterTransient()", submitInput);
            string routeContinuous = ExtractDeclaredMethodBody(
                identity,
                "RPC_SubmitSharedCharacterInput");
            StringAssert.Contains("IFusionSharedCharacterEndpoint endpoint", routeContinuous);
            StringAssert.Contains("endpoint?.AcceptSharedCharacterInput", routeContinuous);
            StringAssert.DoesNotContain(
                "FusionNativeNetworkCharacterMotor",
                routeContinuous);
            string routeTransient = ExtractDeclaredMethodBody(
                identity,
                "RPC_SubmitSharedCharacterTransient");
            StringAssert.Contains("IFusionSharedCharacterEndpoint endpoint", routeTransient);
            StringAssert.Contains("endpoint?.AcceptSharedCharacterTransient", routeTransient);
            StringAssert.DoesNotContain(
                "FusionNativeNetworkCharacterMotor",
                routeTransient);
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
            StringAssert.Contains("!m_Identity.TransportAdmitted", acceptInput);
            StringAssert.Contains("!m_Identity.HasAuthorityAdmission", acceptInput);
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
            StringAssert.Contains("Source = source", acceptTransient);
            StringAssert.Contains("source != m_Identity.LogicalOwner", acceptTransient);
            AssertAppearsBefore(
                acceptTransient,
                "source != m_Identity.LogicalOwner",
                "m_SharedTransientQueue.Enqueue");
            StringAssert.Contains("!m_Identity.TransportAdmitted", acceptTransient);
            StringAssert.Contains("!m_Identity.HasAuthorityAdmission", acceptTransient);

            string fixedShared = ExtractDeclaredMethodBody(motor, "FixedUpdateShared");
            StringAssert.Contains("sharedPayloadTick = input.SourceTick", fixedShared);
            StringAssert.Contains("input.SourceTick = m_LatestSharedTrustedTick", fixedShared);
            StringAssert.Contains("m_SharedTransientQueue.Dequeue()", fixedShared);
            StringAssert.Contains("input.SourceTick = transient.TrustedTick", fixedShared);
            StringAssert.Contains("sharedInputSource = transient.Source", fixedShared);
            StringAssert.Contains(
                "CanApplyAuthenticatedSharedRemoteOwnerPose",
                fixedShared);
            AssertAppearsBefore(
                fixedShared,
                "m_Driver.SetAuthenticatedRemoteOwnerPoseApplication(",
                "m_Driver.Simulate");
            AssertAppearsBefore(
                fixedShared,
                "m_Driver.Simulate",
                "SetAuthenticatedRemoteOwnerPoseApplication(false)");
            StringAssert.Contains("finally", fixedShared);
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

            string resetShared = ExtractDeclaredMethodBody(
                motor,
                "ResetSharedRuntimeState");
            StringAssert.Contains(
                "m_LatestSharedInputSource = PlayerRef.Invalid",
                resetShared);
            StringAssert.Contains("m_SharedTransientQueue.Clear()", resetShared);

            string authenticatedSharedPose = ExtractDeclaredMethodBody(
                motor,
                "CanApplyAuthenticatedSharedRemoteOwnerPose");
            StringAssert.Contains("input.HasOwnerPose", authenticatedSharedPose);
            StringAssert.Contains("Runner.GameMode == GameMode.Shared", authenticatedSharedPose);
            StringAssert.Contains("HasStateAuthority", authenticatedSharedPose);
            StringAssert.Contains("!IsLocalLogicalOwner", authenticatedSharedPose);
            StringAssert.Contains("source == m_Identity.LogicalOwner", authenticatedSharedPose);
            StringAssert.Contains("m_Identity.TransportAdmitted", authenticatedSharedPose);
            StringAssert.Contains("m_Identity.HasAuthorityAdmission", authenticatedSharedPose);
            StringAssert.Contains(
                "m_NetworkCharacter.HasAuthenticatedPlayerOwner",
                authenticatedSharedPose);
            StringAssert.Contains("m_NetworkCharacter.IsPlayerOwnedActor", authenticatedSharedPose);

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
                "m_Character.InitializeNetworkRole(",
                refreshRole,
                "A graphical Shared master must not apply dedicated-server renderer " +
                "optimizations to remote players.");
            StringAssert.Contains("hasAuthenticatedPlayerOwner);", refreshRole);
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
                "sharedPump.SimulateSharedLogicalOwnerProxyTick",
                runnerTick);
            StringAssert.Contains("restorePredictedPose: true", runnerTick);

            string runnerRender = ExtractMethodBody(
                router,
                "public override void Render()");
            StringAssert.Contains("!playerObject.IsInSimulation", runnerRender);
            StringAssert.Contains("sharedPump.RenderSharedLogicalOwnerProxy()", runnerRender);

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
                "IFusionSharedCharacterRunnerPump",
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

            string[] fusionPrefabs = GetFusionDemoPrefabAssetPaths();

            int networkCharacterPrefabCount = 0;
            foreach (string assetPath in fusionPrefabs)
            {
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
                NetworkCharacter character =
                    prefab != null
                        ? prefab.GetComponentInChildren<NetworkCharacter>(true)
                        : null;
                if (character == null ||
                    character.ActorType == NetworkCharacterActorType.NPC)
                {
                    continue;
                }

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
        public void FusionWizard_DefaultsToNativeMovementAndExposesOptionalBackends()
        {
            string wizard = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Editor/Transport/Fusion/" +
                "FusionSceneSetupWizard.cs");

            StringAssert.Contains(
                "m_PredictionBackend = NetworkPredictionBackend.FusionNative",
                wizard);

            string projectPage = ExtractMethodBody(wizard, "private void DrawProjectPage()");
            StringAssert.Contains("DrawPredictionBackendSelector();", projectPage);

            string predictionSelector = ExtractMethodBody(
                wizard,
                "private void DrawPredictionBackendSelector()");
            StringAssert.Contains("\"Fusion Native (Recommended)\"", predictionSelector);
            StringAssert.Contains(
                "\"Fusion Advanced KCC (Optional Addon)\"",
                predictionSelector);
            StringAssert.Contains("\"Built-in Legacy\"", predictionSelector);
            StringAssert.Contains(
                "_ => NetworkPredictionBackend.FusionNative",
                predictionSelector);
            StringAssert.Contains(
                "2 => NetworkPredictionBackend.BuiltIn",
                predictionSelector);

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
        public void FusionTransportBridge_CentralizesCharacterInputThroughEndpointContract()
        {
            string bridge = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Runtime/Transport/Fusion/" +
                "FusionTransportBridge.cs");
            string onInput = ExtractMethodBody(
                bridge,
                "public void OnInput(NetworkRunner runner, NetworkInput input)");

            StringAssert.Contains("runner.TryGetPlayerObject(runner.LocalPlayer", onInput);
            StringAssert.Contains(
                "IFusionCharacterInputEndpoint endpoint",
                onInput);
            StringAssert.Contains("endpoint.TryConsumeNetworkInput(runner, input)", onInput);
            StringAssert.DoesNotContain(
                "FusionNativeNetworkCharacterMotor",
                onInput,
                "Runner input collection must remain open to optional Fusion backends.");
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
        public void FusionKccBackend_UsesPublicOptionalTransportContracts()
        {
            Assert.AreEqual(3, (int)NetworkPredictionBackend.FusionKCC);
            Assert.AreEqual(
                0,
                (int)FusionKccSharedAuthorityMode.OwnerMovementAuthority);
            Assert.AreEqual(
                1,
                (int)FusionKccSharedAuthorityMode.SharedMasterMovementAuthority);

            Assert.IsTrue(typeof(IFusionCharacterInputEndpoint).IsPublic);
            Assert.IsTrue(typeof(IFusionSharedCharacterEndpoint).IsPublic);
            Assert.IsTrue(typeof(IFusionSharedCharacterRunnerPump).IsPublic);
            Assert.IsTrue(typeof(IFusionKccRuntimeAdapter).IsPublic);
            Assert.IsTrue(typeof(FusionKccCharacterBackend).IsPublic);

            Assert.IsTrue(
                typeof(INetworkCharacterPredictionBackend).IsAssignableFrom(
                    typeof(FusionKccCharacterBackend)));
            Assert.IsTrue(
                typeof(INetworkAuthoritativePoseProvider).IsAssignableFrom(
                    typeof(FusionKccCharacterBackend)));
            Assert.IsTrue(
                typeof(IFusionCharacterInputEndpoint).IsAssignableFrom(
                    typeof(FusionNativeNetworkCharacterMotor)));
            Assert.IsTrue(
                typeof(IFusionSharedCharacterEndpoint).IsAssignableFrom(
                    typeof(FusionNativeNetworkCharacterMotor)));
            Assert.IsTrue(
                typeof(IFusionSharedCharacterRunnerPump).IsAssignableFrom(
                    typeof(FusionNativeNetworkCharacterMotor)));

            string contracts = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Runtime/Transport/Fusion/" +
                "FusionCharacterRuntimeContracts.cs");
            string backend = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Runtime/Transport/Fusion/" +
                "FusionKccCharacterBackend.cs");
            StringAssert.DoesNotContain("using Fusion.Addons.KCC", contracts);
            StringAssert.DoesNotContain("using Fusion.Addons.KCC", backend);
            StringAssert.Contains(
                "m_SharedAuthorityMode =\n" +
                "            FusionKccSharedAuthorityMode.OwnerMovementAuthority",
                backend,
                "Owner movement authority must remain the safe Shared-mode default.");
            StringAssert.Contains("MonoBehaviour m_RuntimeAdapter", backend);
            StringAssert.Contains("ResolveAdapter", backend);

            string createDriver = ExtractDeclaredMethodBody(backend, "CreateDriver");
            StringAssert.Contains("if (driver == null)", createDriver);
            StringAssert.Contains(
                "RestoreBuiltInControllerForFallback()",
                createDriver,
                "Removing or omitting optional KCC code must leave Built-in fallback movable.");
            string restoreFallback = ExtractDeclaredMethodBody(
                backend,
                "RestoreBuiltInControllerForFallback");
            StringAssert.Contains(
                "GetComponent<CharacterController>()",
                restoreFallback);
            StringAssert.Contains("controller.enabled = true", restoreFallback);
        }

        [Test]
        public void FusionCharacterInputUtility_ClassifiesOnlyTransientSharedPayloads()
        {
            Assert.IsFalse(FusionCharacterInputUtility.HasSharedTransientInput(default));

            var continuousOwnerPose = new FusionNativeCharacterInput
            {
                Flags = FusionNativeCharacterInput.FlagOwnerPose |
                        FusionNativeCharacterInput.FlagContinuousOwnerPose,
                OwnerPosition = Vector3.one
            };
            Assert.IsFalse(
                FusionCharacterInputUtility.HasSharedTransientInput(continuousOwnerPose));

            continuousOwnerPose.Flags = FusionNativeCharacterInput.FlagOwnerPose;
            Assert.IsTrue(
                FusionCharacterInputUtility.HasSharedTransientInput(continuousOwnerPose));

            Assert.IsTrue(FusionCharacterInputUtility.HasSharedTransientInput(
                new FusionNativeCharacterInput
                {
                    Flags = FusionNativeCharacterInput.FlagJump
                }));
            Assert.IsTrue(FusionCharacterInputUtility.HasSharedTransientInput(
                new FusionNativeCharacterInput
                {
                    Flags = FusionNativeCharacterInput.FlagResetVerticalVelocity
                }));
            Assert.IsTrue(FusionCharacterInputUtility.HasSharedTransientInput(
                new FusionNativeCharacterInput
                {
                    Flags = FusionNativeCharacterInput.FlagCollisionChanged
                }));
            Assert.IsTrue(FusionCharacterInputUtility.HasSharedTransientInput(
                new FusionNativeCharacterInput
                {
                    RootMotionDelta = Vector3.forward * 0.01f
                }));
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
            StringAssert.Contains("!IsLocalLogicalOwner ||", motor);
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
            StringAssert.Contains("RestoreRemoteProxySimulationRootForPresentation()", render);
            StringAssert.Contains("m_PresentationRoot", render);
            StringAssert.Contains("!IsLocalLogicalOwner ||", render);
            StringAssert.Contains("NetworkTRSP.Render(", render);
            StringAssert.Contains("transform,", render);
            StringAssert.Contains(
                "transform,\n                        false,\n                        false,\n                        false,",
                render,
                "The Character root uses world space. Fusion's third NetworkTRSP.Render " +
                "boolean must remain false for both owners and remote proxies.");
            StringAssert.DoesNotContain(
                "locallySimulatedOwner ? false : true",
                render,
                "The local flag must not be inverted for remote proxies.");
            StringAssert.DoesNotContain(
                "else if (!locallySimulatedOwner)",
                render,
                "Ordinary local owners must not be excluded from Fusion root rendering.");
            StringAssert.Contains(
                "if (!HasStateAuthority && !locallySimulatedOwner)",
                render,
                "A proxy without a safe visual root must retain the legacy root-render fallback.");

            string restoreRemoteRoot = ExtractDeclaredMethodBody(
                motor,
                "RestoreRemoteProxySimulationRootForPresentation");
            StringAssert.Contains("IsLocalLogicalOwner || HasStateAuthority", restoreRemoteRoot);
            StringAssert.Contains("NativeState.TRSPData", restoreRemoteRoot);
            StringAssert.Contains("transform.SetPositionAndRotation", restoreRemoteRoot);
            StringAssert.Contains("Physics.SyncTransforms()", restoreRemoteRoot);
            StringAssert.DoesNotContain("NetworkTRSP.Render", restoreRemoteRoot);

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
                "TryGetExternalRootWriteAllowance");
            StringAssert.Contains(
                "NetworkOwnerMotionAuthorityHooks.TryGetExternalRootWriteAllowance",
                applyOwnerPose,
                "Validated GC2 Traversal poses must use the same absolute root semantics in " +
                "Fusion state that the owner already used locally.");
            StringAssert.Contains("if (useAuthorizedAbsoluteRootWrite)", applyOwnerPose);
            StringAssert.Contains("SetRootPosition(target)", applyOwnerPose);
            AssertAppearsBefore(
                applyOwnerPose,
                "TryGetExternalRootWriteAllowance",
                "m_Controller.Move(requestedDelta)");
            StringAssert.Contains("CollisionFlags collisionFlags", applyOwnerPose);
            StringAssert.Contains("residualDistance > applicationTolerance", applyOwnerPose);
            StringAssert.Contains("m_LastAcceptedOwnerPoseTick = int.MinValue", applyOwnerPose);
            StringAssert.Contains("LogOwnerPoseCollisionBlocked", applyOwnerPose);
        }

        [Test]
        public void FusionNativeOwnerPose_AuthorizedTraversalCrossesAnotherCharacterController()
        {
            var characterObject = new GameObject("Fusion Traversal Owner");
            var blockerObject = new GameObject("Connected Player Controller");
            FusionNativeCharacterDriver driver = null;
            Func<Character, Vector3, string> allowAbsolute = null;

            try
            {
                Vector3 start = new Vector3(0f, 100f, 0f);
                Vector3 target = new Vector3(2f, 100f, 0f);
                characterObject.transform.position = start;
                Character character = EditModeLifecycle.AddComponent<Character>(characterObject);

                driver = new FusionNativeCharacterDriver();
                driver.OnStartup(character);
                driver.OpenServerOwnerMotionWindow(1f, 73u);

                CharacterController blocker = blockerObject.AddComponent<CharacterController>();
                blocker.height = 2f;
                blocker.radius = 0.35f;
                blocker.center = Vector3.up;
                blockerObject.transform.position = new Vector3(1f, 100f, 0f);
                Physics.SyncTransforms();

                MethodInfo tryApplyOwnerPose = typeof(FusionNativeCharacterDriver).GetMethod(
                    "TryApplyOwnerPose",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.That(tryApplyOwnerPose, Is.Not.Null);

                bool ordinaryAccepted = (bool)tryApplyOwnerPose.Invoke(
                    driver,
                    new object[] { target, 0, 1f / 60f, true });
                Assert.That(ordinaryAccepted, Is.False);
                Assert.That(
                    character.transform.position.x,
                    Is.LessThan(1f),
                    "Without a gameplay allowance the authoritative CharacterController sweep " +
                    "must remain blocked by another player capsule.");

                CharacterController ownerController =
                    characterObject.GetComponent<CharacterController>();
                ownerController.enabled = false;
                characterObject.transform.position = start;
                ownerController.enabled = true;
                Physics.SyncTransforms();

                allowAbsolute = (candidate, _) => candidate == character
                    ? "test-interactive-traversal"
                    : string.Empty;
                NetworkOwnerMotionAuthorityHooks.ExternalRootWriteAllowanceRequested +=
                    allowAbsolute;

                driver.CloseServerOwnerMotionWindow();
                bool rejectedWithoutWindow = (bool)tryApplyOwnerPose.Invoke(
                    driver,
                    new object[] { target, 1, 1f / 60f, true });
                Assert.That(rejectedWithoutWindow, Is.False);
                Assert.That(
                    character.transform.position,
                    Is.EqualTo(start),
                    "A gameplay root-write allowance must not bypass the server-issued " +
                    "owner-motion window.");

                driver.OpenServerOwnerMotionWindow(1f, 74u);
                bool traversalAccepted = (bool)tryApplyOwnerPose.Invoke(
                    driver,
                    new object[] { target, 1, 1f / 60f, true });
                Assert.That(traversalAccepted, Is.True);
                Assert.That(character.transform.position, Is.EqualTo(target));
            }
            finally
            {
                if (allowAbsolute != null)
                {
                    NetworkOwnerMotionAuthorityHooks.ExternalRootWriteAllowanceRequested -=
                        allowAbsolute;
                }

                driver?.OnDispose(characterObject.GetComponent<Character>());

                bool previousIgnore = LogAssert.ignoreFailingMessages;
                LogAssert.ignoreFailingMessages = true;
                try
                {
                    UnityEngine.Object.DestroyImmediate(blockerObject);
                    UnityEngine.Object.DestroyImmediate(characterObject);
                }
                finally
                {
                    LogAssert.ignoreFailingMessages = previousIgnore;
                }
            }
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
            StringAssert.Contains(
                "m_IsApplyingAuthenticatedRemoteOwnerPose = false",
                resetTransient);
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
                "FusionCharacterInputUtility.HasSharedTransientInput(input)",
                transientClassifier,
                "Fusion Native must use the transport-neutral transient classifier shared " +
                "with optional movement backends.");

            string runtimeContracts = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Runtime/Transport/Fusion/" +
                "FusionCharacterRuntimeContracts.cs");
            string sharedTransientClassifier = ExtractDeclaredMethodBody(
                runtimeContracts,
                "HasSharedTransientInput");
            StringAssert.Contains(
                "input.HasOwnerPose && !input.HasContinuousOwnerPose",
                sharedTransientClassifier,
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
            StringAssert.DoesNotContain(
                "StartHostLocalLinkMotionAnimation",
                motionEnter,
                "MotionLink.Run owns its animation/gesture start; Host loopback must not " +
                "prestart the same PullUp animation from EventMotionEnter.");
            StringAssert.DoesNotContain(
                "StartHostLocalLinkMotionAnimation",
                traversal,
                "The obsolete Host-only TraverseLink prestart helper must not return.");

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
                "FusionCharacterInputUtility.HasSharedTransientInput(input)",
                motor,
                "Fusion Native and optional movement backends share the same continuous " +
                "traversal/transient-input classification.");

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

            string render = ExtractMethodBody(motor, "public override void Render()");
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
        public void FusionTraversalDemoPlayers_AreInTheGlobalRuntimePrefabTable()
        {
            string[] prefabPaths = GetFusionDemoPrefabAssetPaths()
                .Where(path => string.Equals(
                    Path.GetFileName(path),
                    "FusionDemoPlayer-Traversal.prefab",
                    StringComparison.Ordinal))
                .ToArray();
            Assert.IsNotEmpty(
                prefabPaths,
                "No installed or legacy Fusion Traversal demo player prefab was found.");

            NetworkProjectConfig config = NetworkProjectConfig.Global;
            Assert.NotNull(config);
            Assert.NotNull(config.PrefabTable);

            foreach (string prefabPath in prefabPaths)
            {
                string assetGuid = AssetDatabase.AssetPathToGUID(prefabPath);
                Assert.IsNotEmpty(assetGuid, $"Prefab asset was not found: {prefabPath}");

                var networkGuid = new NetworkObjectGuid(assetGuid);
                NetworkPrefabId prefabId = config.PrefabTable.GetId(networkGuid);
                Assert.IsTrue(
                    prefabId.IsValid,
                    $"Fusion's global prefab table cannot resolve {prefabPath} ({networkGuid}).");
            }
        }

        [Test]
        public void FusionServerTime_UsesConfirmedClockForClientsAndWaitsForRuntimeConfiguration()
        {
            string bridge = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Runtime/Transport/Fusion/" +
                "FusionTransportBridge.cs");
            StringAssert.Contains("if (!IsRunnerTimeReady) return Time.time;", bridge);
            StringAssert.Contains("if (IsServer) return m_Runner.SimulationTime;", bridge);
            StringAssert.Contains(
                "m_Runner.LatestServerTick.Raw * m_Runner.DeltaTime",
                bridge,
                "Non-authority peers must derive timestamps from Fusion's latest confirmed " +
                "server tick instead of their prediction-ahead simulation tick.");
            StringAssert.Contains("return latestServerTime > 0f", bridge);
            StringAssert.DoesNotContain(
                "IsRunnerTimeReady ? m_Runner.SimulationTime : Time.time",
                bridge,
                "A predicted client clock must not be exposed as confirmed server time.");
            StringAssert.Contains("m_Runner.Tick.Raw > 0", bridge);
            AssertAppearsBefore(
                bridge,
                "m_Runner.Tick.Raw > 0",
                "private double GetLagCompensationServerTime()");
        }

        [Test]
        public void FusionDemoPlayers_ArePreparedForFusionNativeMovement()
        {
            string[] fusionPrefabs = GetFusionDemoPrefabAssetPaths()
                .Where(path => path.IndexOf(
                    "/GC2NetworkingLayerFusionTransport.AdvancedKCCExamples@",
                    StringComparison.Ordinal) < 0)
                .ToArray();

            int networkCharacterPrefabCount = 0;
            foreach (string assetPath in fusionPrefabs)
            {
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
                NetworkCharacter character =
                    prefab != null
                        ? prefab.GetComponentInChildren<NetworkCharacter>(true)
                        : null;
                if (character == null ||
                    character.ActorType == NetworkCharacterActorType.NPC)
                {
                    continue;
                }

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
        [NUnit.Framework.Category("GC2Networking.FreeFlow")]
        public void FusionWizard_PreparesAndValidatesServerNpcsAndBotSlots()
        {
            string source = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Editor/Transport/Fusion/" +
                "FusionSceneSetupWizard.cs");

            StringAssert.Contains("Server-Owned NPCs and Bot Slots", source);
            StringAssert.Contains("private string PrepareNpcPrefab(GameObject prefab)", source);
            StringAssert.Contains("typeof(NetworkObject)", source);
            StringAssert.Contains("NetworkObjectFlags.MasterClientObject", source);
            StringAssert.Contains("NetworkNpcSetupEditorUtility.ConfigureServerNpc(", source);
            StringAssert.Contains("NetworkNpcSetupEditorUtility.ValidateServerNpc(", source);
            StringAssert.Contains("m_NpcAuthorityRootPaths.Count == 0", source);
            StringAssert.Contains("NetworkPredictionBackend.FusionNative", source);
            StringAssert.Contains("NetworkPredictionBackend.FusionKCC", source);
            StringAssert.Contains("FusionBotSlotCoordinator", source);
            StringAssert.Contains("FusionBotSlotStateReplicator", source);
            StringAssert.Contains("m_BotSlotCoordinator", source);
            StringAssert.Contains("EnsureFusionPrefabLabel(m_NpcPrefab)", source);
            StringAssert.Contains("Server-authoritative Free Flow Combat", source);
            StringAssert.Contains("NetworkFreeFlowCombatAdapterType", source);
            StringAssert.Contains(
                "m_ModuleMelee && m_EnableFreeFlowCombat",
                source);
            StringAssert.Contains("IsFreeFlowCombatAvailable", source);
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
        public void FusionOwnership_UsesAdmittedIdentityWhileRuntimeRoleReinitializes()
        {
            string transportSource = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Runtime/Transport/Fusion/" +
                "FusionTransportBridge.cs");
            string verifyOwnership = ExtractMethodBody(
                transportSource,
                "public override bool TryVerifyActorOwnership(");

            StringAssert.Contains(
                "character.ActorType == NetworkCharacterActorType.NPC",
                verifyOwnership,
                "Explicit NPCs must never acquire player ownership through a Fusion identity.");
            StringAssert.Contains("identity.TransportAdmitted", verifyOwnership);
            StringAssert.Contains("registry.IsAdmitted(identity)", verifyOwnership);
            StringAssert.Contains("identity.TryGetLogicalOwnerClientId", verifyOwnership);
            StringAssert.DoesNotContain(
                "!character.IsPlayerOwnedActor",
                verifyOwnership,
                "Authenticated ownership must survive the transient role reset used during " +
                "Fusion re-admission and Shared-master migration.");
        }

        [Test]
        public void FusionTraversal_LocalOwnerRefreshesPoseWindowOnHostAndSharedMaster()
        {
            string traversalSource = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Traversal/NetworkTraversalController.cs");
            string refresh = ExtractDeclaredMethodBody(
                traversalSource,
                "RefreshLocalTraversalPoseAuthority");

            StringAssert.Contains("!m_IsLocalClient || m_IsRemoteClient", refresh);
            StringAssert.Contains("ActivateLocalTraversalPoseAuthority", refresh);
            StringAssert.DoesNotContain(
                "m_IsServer ||",
                refresh,
                "A locally owned Host or Shared-master character still requires the owner " +
                "pose window used by Fusion Native's single-writer traversal path.");
        }

        [Test]
        public void SharedAuthorityMigration_QuarantinesUntilFusionTransfersStateAuthority()
        {
            string registrySource = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Runtime/Transport/Fusion/" +
                "FusionAuthoritySpawnRegistry.cs");
            string rebuild = ExtractDeclaredMethodBody(
                registrySource,
                "RebuildAfterAuthorityChange");
            string validate = ExtractDeclaredMethodBody(
                registrySource,
                "ValidateAllIdentities");
            string quarantine = ExtractDeclaredMethodBody(
                registrySource,
                "ShouldQuarantineDuringAuthorityMigration");

            StringAssert.Contains("m_PendingAuthorityMigrationIds.Add", rebuild);
            StringAssert.Contains("m_AuthorityMigrationDeadline", rebuild);
            StringAssert.Contains("m_PendingAuthorityMigrationIds.Contains", validate);
            StringAssert.Contains("ShouldQuarantineDuringAuthorityMigration", validate);
            StringAssert.Contains("identity.SetTransportAdmission(false)", validate);
            StringAssert.Contains("identity.HasAuthorityAdmission", quarantine);
            StringAssert.Contains("HasSafeAuthorityFlags", quarantine);
            StringAssert.DoesNotContain(
                "TryIssueAuthorityAdmission(false)",
                quarantine,
                "The replicated trust marker must survive the bounded Fusion authority-transfer " +
                "window while local gameplay admission remains fail-closed.");
        }

        [Test]
        public void MotionRejection_RespondsImmediatelyInsteadOfMasqueradingAsTimeout()
        {
            string managerSource = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Runtime/Motion/NetworkMotionManager.cs");
            string receive = ExtractMethodBody(
                managerSource,
                "public void ReceiveCommand(");
            string reject = ExtractDeclaredMethodBody(
                managerSource,
                "SendRejectedResult");

            StringAssert.Contains("SendRejectedResult", receive);
            StringAssert.Contains("NetworkMotionResult.REJECT_NOT_ALLOWED", receive);
            StringAssert.Contains("NetworkMotionResult.Rejected", reject);
            StringAssert.Contains("message.Command.sequenceNumber", reject);
            StringAssert.Contains("SendResultToClient?.Invoke", reject);
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

        [Test]
        public void FusionClimbSmoke_CoversHostObservationAndSharedOwnerProgress()
        {
            string bootstrap = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Runtime/Transport/Fusion/Traversal/" +
                "FusionTraversalClimbSmokeBootstrap.cs");
            StringAssert.Contains("fusion-traversal-climb", bootstrap);
            StringAssert.Contains("AttachSettleSeconds", bootstrap);
            StringAssert.Contains("ShortcutPlayer.Instance != actor.gameObject", bootstrap);
            StringAssert.Contains("_ = interactive.Enter(", bootstrap);
            StringAssert.Contains("new KeyboardState(Key.W)", bootstrap);
            StringAssert.Contains("Application.targetFrameRate = 60", bootstrap);
            StringAssert.Contains("ObserverResultGraceSeconds", bootstrap);
            StringAssert.Contains("SharedAuthorityResultGraceSeconds", bootstrap);
            StringAssert.Contains(
                "#if GC2_TRAVERSAL && (UNITY_EDITOR || DEVELOPMENT_BUILD)",
                bootstrap);
            StringAssert.Contains("actor.transform.position.y - climbStart.y", bootstrap);
            StringAssert.Contains("RunHostObserver()", bootstrap);
            StringAssert.Contains("remoteObserver ??= FindRemotePlayer()", bootstrap);
            StringAssert.Contains("--gc2-network-smoke-region", bootstrap);
            StringAssert.Contains("new FusionSessionStartOptions(session, region)", bootstrap);
            StringAssert.Contains("observer-host-climb-fall-smooth", bootstrap);
            StringAssert.Contains("GetPresentedPosition(actor)", bootstrap);
            StringAssert.Contains("ActiveRemotePresentationTarget", bootstrap);
            StringAssert.Contains("observer-host-disconnected", bootstrap);
            StringAssert.Contains("reverseDistance", bootstrap);
            StringAssert.Contains("maxReverseStep", bootstrap);
            StringAssert.Contains("RequiredObserverFallDistance", bootstrap);
            StringAssert.Contains("stance.TryCancel(", bootstrap);
            StringAssert.Contains("host-owner-detach-requested", bootstrap);
            StringAssert.Contains("observer-host-detached", bootstrap);
            StringAssert.Contains("observer-host-climb-fall-smooth", bootstrap);
            StringAssert.Contains("fallReverseDistance", bootstrap);
            StringAssert.Contains("maxFallFrameStep", bootstrap);
            StringAssert.Contains("idleOwnerDrift", bootstrap);
            StringAssert.Contains("RequiredConnectedOwnerMoveDistance", bootstrap);
            StringAssert.Contains("connected-owner-move-start", bootstrap);
            StringAssert.Contains("ShortcutPlayer.Instance == localOwner.gameObject", bootstrap);
            StringAssert.Contains("player.InjectInput(Vector2.up)", bootstrap);
            StringAssert.Contains("connectedOwnerInputHealthy", bootstrap);
            StringAssert.Contains("connectedOwnerMoveDistance", bootstrap);
            StringAssert.DoesNotContain(
                "controller.RequestEnterTraverseInteractive(interactive)",
                bootstrap,
                "The smoke must enter through GC2's patched TraverseInteractive path.");

            string builder = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Runtime/Transport/Fusion/Traversal/Editor/" +
                "FusionTraversalClimbSmokeBuilder.cs");
            StringAssert.Contains("FusionClimbDemo.unity", builder);

            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string smokeScript = File.ReadAllText(Path.Combine(
                projectRoot,
                "Tools/run_unity_network_smoke.sh"));
            StringAssert.Contains("fusion-shared-climb", smokeScript);
            StringAssert.Contains("fusion-host-climb", smokeScript);
            StringAssert.Contains("GC2_NETWORK_SMOKE_PHOTON_REGION:-asia", smokeScript);
            StringAssert.Contains("--gc2-network-ci-trace", smokeScript);

            string workflow = File.ReadAllText(Path.Combine(
                projectRoot,
                ".github/workflows/unity-network-smoke.yml"));
            StringAssert.Contains("fusion-shared-climb", workflow);
            StringAssert.Contains("fusion-host-climb", workflow);
        }

        [Test]
        public void FusionCoverSmoke_ExercisesExactCoverTransitionRecoveryAndPresentation()
        {
            string bootstrap = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Runtime/Transport/Fusion/Traversal/" +
                "FusionTraversalCoverSmokeBootstrap.cs");
            StringAssert.Contains("fusion-traversal-cover", bootstrap);
            StringAssert.Contains(
                "#if GC2_TRAVERSAL && (UNITY_EDITOR || DEVELOPMENT_BUILD)",
                bootstrap,
                "The regression harness must remain absent from release players.");
            StringAssert.Contains("FindCover(\"Cover_Low\")", bootstrap);
            StringAssert.Contains("FindCover(\"Cover_High\")", bootstrap);
            StringAssert.Contains("_ = lowCover.Enter(", bootstrap);
            StringAssert.Contains("new KeyboardState(Key.D)", bootstrap);
            StringAssert.Contains("motion.RequestTeleport(", bootstrap);
            StringAssert.Contains("CalculateCharacterRootStart", bootstrap);
            StringAssert.Contains("CoverConnectionGraceSeconds", bootstrap);
            StringAssert.Contains("stance.TryCancel(", bootstrap);
            StringAssert.Contains("GetPresentedPosition(actor)", bootstrap);
            StringAssert.Contains("ActiveRemotePresentationTarget", bootstrap);
            StringAssert.Contains("maxReverseStep", bootstrap);
            StringAssert.Contains("maxFrameStep", bootstrap);
            StringAssert.Contains("Application.logMessageReceived += CaptureTraversalFailure", bootstrap);
            StringAssert.Contains("MissingReferenceException", bootstrap);
            StringAssert.Contains("collider2", bootstrap);
            StringAssert.Contains("authoritative traversal task failed", bootstrap);
            StringAssert.Contains("player.InjectInput(Vector2.up, sendJump)", bootstrap);
            StringAssert.Contains("ShortcutPlayer.Instance != actor.gameObject", bootstrap);
            StringAssert.Contains("host-cover-transition-detach-move-jump", bootstrap);
            StringAssert.Contains("observer-cover-smooth-detach-local-input", bootstrap);

            string builder = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Runtime/Transport/Fusion/Traversal/Editor/" +
                "FusionTraversalClimbSmokeBuilder.cs");
            StringAssert.Contains("BuildCoverNetworkSmokePlayer", builder);
            StringAssert.Contains("TraversalExamples@1.0.1", builder);
            StringAssert.Contains("TraversalExamples@1.0.0", builder);
            AssertAppearsBefore(
                builder,
                "LoadAssetAtPath<SceneAsset>(SceneVersion101)",
                "LoadAssetAtPath<SceneAsset>(SceneVersion100)");

            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string smokeScript = File.ReadAllText(Path.Combine(
                projectRoot,
                "Tools/run_unity_network_smoke.sh"));
            StringAssert.Contains("fusion-host-cover", smokeScript);
            StringAssert.Contains("fusion-traversal-cover", smokeScript);
            StringAssert.Contains("BuildCoverNetworkSmokePlayer", smokeScript);
            StringAssert.Contains("--pristine-gc2-root", smokeScript);
            StringAssert.Contains("GC2_NETWORK_PRISTINE_ROOT", smokeScript);

            string workflow = File.ReadAllText(Path.Combine(
                projectRoot,
                ".github/workflows/unity-network-smoke.yml"));
            Assert.GreaterOrEqual(
                Regex.Matches(workflow, "fusion-host-cover").Count,
                2,
                "The manual choice and CI matrix must both expose the Cover regression smoke.");
        }

        [Test]
        public void FusionLedgeTransitionSmoke_ProvesHostProxyUsesAnimatedTransportPose()
        {
            string bootstrap = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Runtime/Transport/Fusion/Traversal/" +
                "FusionTraversalLedgeTransitionSmokeBootstrap.cs");
            StringAssert.Contains("fusion-traversal-ledge-transition", bootstrap);
            StringAssert.Contains(
                "#if GC2_TRAVERSAL && (UNITY_EDITOR || DEVELOPMENT_BUILD)",
                bootstrap,
                "The regression harness must remain absent from release players.");
            StringAssert.Contains("new(0f, 6f, -0.05f)", bootstrap);
            StringAssert.Contains("new(0f, 8f, -0.05f)", bootstrap);
            StringAssert.Contains("new(0f, 10f, -0.05f)", bootstrap);
            StringAssert.Contains("MotionName = \"Motion_Ledge_Climb\"", bootstrap);
            StringAssert.Contains("_ = source.Enter(", bootstrap);
            Assert.GreaterOrEqual(
                Regex.Matches(bootstrap, @"stance\.TryJump\(\);").Count,
                2,
                "Both ledge changes must use GC2's patched authoritative connection route.");
            StringAssert.DoesNotContain("middle.Enter(", bootstrap);
            StringAssert.DoesNotContain("top.Enter(", bootstrap);
            StringAssert.Contains("--gc2-network-smoke-ready", bootstrap);
            StringAssert.Contains(
                "CreateSharedWithExactSessionNameAsync",
                bootstrap);
            StringAssert.Contains("JoinSharedAsync", bootstrap);
            StringAssert.Contains("fusion-shared-master", bootstrap);
            StringAssert.Contains("fusion-shared-client", bootstrap);
            StringAssert.Contains("transport.IsLocalGameplayReady", bootstrap);
            StringAssert.Contains("!IsSharedTransition ||", bootstrap);
            StringAssert.Contains("transport.IsServer", bootstrap);
            StringAssert.Contains("actor.IsServerInstance", bootstrap);
            StringAssert.Contains("!actor.IsOwnerInstance", bootstrap);
            StringAssert.Contains("actor.HasAuthenticatedPlayerOwner", bootstrap);
            StringAssert.Contains("identity.TransportAdmitted", bootstrap);
            StringAssert.Contains("identity.HasAuthorityAdmission", bootstrap);
            StringAssert.Contains("identity.IsLogicalAuthority", bootstrap);
            StringAssert.Contains("identity.TryGetLogicalOwnerClientId", bootstrap);
            StringAssert.Contains("transport.IsClientReady(ownerClientId)", bootstrap);
            StringAssert.Contains("controller.IsServer", bootstrap);
            StringAssert.Contains("OwnerGameplayReadySettleSeconds", bootstrap);
            StringAssert.Contains("SetupTeleportRetrySeconds", bootstrap);
            StringAssert.Contains("MaxSetupTeleportAttempts = 3", bootstrap);
            StringAssert.Contains("requestAttempt != teleportAttempt && !result.approved", bootstrap);
            StringAssert.Contains("shared-owner-source-position-no-response", bootstrap);
            StringAssert.Contains("sourceEnterRequested = TryRequestSourceEntry(actor, source)", bootstrap);
            StringAssert.Contains("gravity cannot advance", bootstrap);
            StringAssert.Contains("OwnerSourceSettledMarkerSuffix", bootstrap);
            StringAssert.Contains("IsInteractiveTransitionActive(stance)", bootstrap);
            StringAssert.Contains("expectedSource", bootstrap);
            StringAssert.Contains("IsMarkerForActor", bootstrap);
            StringAssert.Contains(".observer-finished", bootstrap);
            StringAssert.Contains("observer-source-ready", bootstrap);
            StringAssert.Contains("observer-transition-sample", bootstrap);
            StringAssert.Contains("observer-transition-complete", bootstrap);
            StringAssert.Contains("RequiredIntermediateSamples = 8", bootstrap);
            StringAssert.Contains("ReverseCorrectionCount == 0", bootstrap);
            StringAssert.Contains("LargeTeleportCount == 0", bootstrap);
            StringAssert.Contains("MaximumFrameStep = 0.25f", bootstrap);
            StringAssert.Contains("FinalConvergenceTolerance", bootstrap);
            StringAssert.Contains("GetPresentedPosition(actor)", bootstrap);
            StringAssert.Contains("TryGetCommittedRemotePresentationPosition", bootstrap);
            StringAssert.Contains("ActiveRemotePresentationTarget", bootstrap);
            StringAssert.Contains("ShortcutPlayer.Instance == actor.gameObject", bootstrap);
            StringAssert.Contains("player.InjectInput(Vector2.up)", bootstrap);
            StringAssert.Contains(
                "observer-host-ledge-transitions-smooth-local-owner-healthy",
                bootstrap);
            StringAssert.Contains(
                "observer-shared-owner-ledge-transitions-smooth-local-owner-healthy",
                bootstrap);
            StringAssert.Contains(
                "shared-owner-center-low-middle-top-transitioned",
                bootstrap);

            string ownerFlow = ExtractDeclaredMethodBody(bootstrap, "RunTransitionOwner");
            AssertAppearsBefore(
                ownerFlow,
                "!transport.IsLocalGameplayReady",
                "motion.RequestTeleport(");
            AssertAppearsBefore(
                ownerFlow,
                "OwnerSourceSettledMarkerSuffix",
                "stance.TryJump();");
            string observerFlow = ExtractDeclaredMethodBody(bootstrap, "RunObserver");
            AssertAppearsBefore(
                observerFlow,
                "remoteOwnerGameplayReady",
                "WriteMarker(m_ReadyPath");
            AssertAppearsBefore(
                observerFlow,
                "OwnerSourceSettledMarkerSuffix",
                "TransitionMetrics.Begin(");

            string traversal = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Traversal/NetworkTraversalController.cs");
            StringAssert.Contains("remote-interactive-transition-presentation", traversal);
            StringAssert.Contains("remote-interactive-transition-exit-started", traversal);
            StringAssert.Contains("remote-interactive-transition-enter-started", traversal);
            StringAssert.Contains("rootPose=transport", traversal);
            StringAssert.Contains(
                "authoritative-interactive-transition-started",
                traversal);
            string jumpConnection = ExtractDeclaredMethodBody(
                traversal,
                "TryStartInteractiveJumpConnectionAsync");
            AssertAppearsBefore(
                jumpConnection,
                "TraceAuthoritativeInteractiveTransitionStart",
                "Traverse.ChangeTo");
            string authoritativeApply = ExtractDeclaredMethodBody(
                traversal,
                "ApplyAuthoritativeActionAsync");
            AssertAppearsBefore(
                authoritativeApply,
                "TraceAuthoritativeInteractiveTransitionStart",
                "Task interactiveTask = transitionSource");

            string nativeMotor = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Runtime/Transport/Fusion/" +
                "FusionNativeNetworkCharacterMotor.cs");
            StringAssert.Contains("TryGetCommittedRemotePresentationPosition", nativeMotor);
            StringAssert.Contains("m_PresentationWorldPosition", nativeMotor);
            StringAssert.Contains(
                "authority-authenticated-owner-pose-applied",
                nativeMotor);

            string builder = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Runtime/Transport/Fusion/Traversal/Editor/" +
                "FusionTraversalClimbSmokeBuilder.cs");
            StringAssert.Contains("BuildLedgeTransitionNetworkSmokePlayer", builder);
            StringAssert.Contains("Fusion Traversal Ledge Transition", builder);

            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string smokeScript = File.ReadAllText(Path.Combine(
                projectRoot,
                "Tools/run_unity_network_smoke.sh"));
            StringAssert.Contains("fusion-host-ledge-transition", smokeScript);
            StringAssert.Contains("fusion-shared-ledge-transition", smokeScript);
            StringAssert.Contains("fusion-traversal-ledge-transition", smokeScript);
            StringAssert.Contains("BuildLedgeTransitionNetworkSmokePlayer", smokeScript);
            StringAssert.Contains("stage=remote-interactive-transition-presentation", smokeScript);
            StringAssert.Contains("stage=remote-interactive-transition-exit-started", smokeScript);
            StringAssert.Contains("stage=remote-interactive-transition-enter-started", smokeScript);
            StringAssert.Contains("Traverse@Climb_ExitU", smokeScript);
            StringAssert.Contains("Traverse@Climb_EnterD", smokeScript);
            StringAssert.Contains("rootPose=transport", smokeScript);
            StringAssert.Contains("teleportKeys=", smokeScript);
            StringAssert.Contains("firstIntermediateSamples", smokeScript);
            StringAssert.Contains("reverseCorrectionCount", smokeScript);
            StringAssert.Contains("largeTeleportCount", smokeScript);
            StringAssert.Contains(
                "stage=authority-authenticated-owner-pose-applied",
                smokeScript);
            StringAssert.Contains(
                "stage=authoritative-interactive-transition-started",
                smokeScript);
            StringAssert.Contains("owner-pose-gameplay-rejection", smokeScript);
            StringAssert.Contains(
                "active && /owner-pose-gameplay-rejection",
                smokeScript);

            string workflow = File.ReadAllText(Path.Combine(
                projectRoot,
                ".github/workflows/unity-network-smoke.yml"));
            Assert.GreaterOrEqual(
                Regex.Matches(workflow, "fusion-host-ledge-transition").Count,
                2,
                "The manual choice and CI matrix must both expose the ledge transition smoke.");
            Assert.GreaterOrEqual(
                Regex.Matches(workflow, "fusion-shared-ledge-transition").Count,
                2,
                "The manual choice and CI matrix must both expose the inverse Shared ledge transition smoke.");
        }

        [Test]
        public void FusionLedgeReconnectSmoke_ProvesLiveFirstAndSnapshotReconnectEdgeRight()
        {
            string bootstrap = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Runtime/Transport/Fusion/Traversal/" +
                "FusionTraversalLedgeReconnectSmokeBootstrap.cs");
            StringAssert.Contains("fusion-traversal-ledge-reconnect", bootstrap);
            StringAssert.Contains(
                "#if GC2_TRAVERSAL && (UNITY_EDITOR || DEVELOPMENT_BUILD)",
                bootstrap,
                "The regression harness must remain absent from release players.");
            StringAssert.Contains("ExactLedgeFixtureLocalPosition", bootstrap);
            StringAssert.Contains("new Vector3(2.55f, 4f, 1.5f)", bootstrap);
            StringAssert.Contains("--gc2-network-smoke-ready", bootstrap);
            StringAssert.Contains("first-observer-ready", bootstrap);
            StringAssert.Contains("first-observer-active-before-ready", bootstrap);
            StringAssert.Contains("RequiredDisconnectGapSeconds", bootstrap);
            StringAssert.Contains("HostReconnectCompletionGraceSeconds", bootstrap);
            StringAssert.Contains("ReconnectObservationGraceSeconds", bootstrap);
            StringAssert.Contains("HostStartLedgeFraction", bootstrap);
            StringAssert.Contains("BlockerLedgeFraction", bootstrap);
            StringAssert.Contains(
                "!transport.IsLocalGameplayReady",
                bootstrap,
                "The connected blocker setup request must wait until Fusion has consumed and " +
                "acknowledged its initial gameplay snapshot.");
            StringAssert.Contains(
                "UnityObjectSearch.FindAny<FusionTransportBridge>()",
                bootstrap,
                "The smoke harness must use the package's unordered Unity object search boundary.");
            StringAssert.DoesNotContain(
                "FindFirstObjectByType",
                bootstrap,
                "The reconnect smoke must not depend on obsolete Unity instance-ID ordering.");
            StringAssert.Contains("first-blocker-position-requested", bootstrap);
            StringAssert.Contains("first-blocker-enter-requested", bootstrap);
            StringAssert.Contains("UpdateOverlapObservation", bootstrap);
            StringAssert.Contains("CombinedCharacterRadius", bootstrap);
            StringAssert.Contains("HostStartedLeftOfBlocker", bootstrap);
            StringAssert.Contains("HostCrossedBlocker", bootstrap);
            StringAssert.Contains("overlap.IsProven", bootstrap);
            StringAssert.Contains("Intent-X", bootstrap);
            StringAssert.Contains("Speed-XY", bootstrap);
            StringAssert.Contains("RequiredIntentX", bootstrap);
            StringAssert.Contains("MaximumSpeedXY", bootstrap);
            StringAssert.Contains("new KeyboardState(Key.D)", bootstrap);
            StringAssert.Contains(
                "(!enterRequested &&",
                bootstrap,
                "The setup-position guard must stop constraining the harness after the " +
                "owner begins moving away from the rail center.");
            Assert.AreEqual(
                1,
                Regex.Matches(bootstrap, @"m_SmokeCamera\.rotation\s*=").Count,
                "The synthetic camera must be oriented once, not fed back from the " +
                "Traversal-controlled Character rotation on every held-input frame.");

            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string smokeScript = File.ReadAllText(Path.Combine(
                projectRoot,
                "Tools/run_unity_network_smoke.sh"));
            StringAssert.Contains("fusion-host-ledge-reconnect", smokeScript);
            StringAssert.Contains("fusion-traversal-ledge-reconnect", smokeScript);
            StringAssert.Contains("--gc2-network-smoke-connection-phase first", smokeScript);
            StringAssert.Contains("--gc2-network-smoke-connection-phase reconnect", smokeScript);
            StringAssert.Contains("stage=remote-interactive-presentation-start", smokeScript);
            StringAssert.Contains("snapshot=True", smokeScript);
            StringAssert.Contains("first-observer-edge-right", smokeScript);
            StringAssert.Contains("reconnect-observer-edge-right", smokeScript);
            StringAssert.Contains("hostCrossedBlocker", smokeScript);
            StringAssert.Contains("blockerActiveLedgeTraverse", smokeScript);
            StringAssert.Contains("stage=first-blocker-enter-requested", smokeScript);

            string workflow = File.ReadAllText(Path.Combine(
                projectRoot,
                ".github/workflows/unity-network-smoke.yml"));
            Assert.GreaterOrEqual(
                Regex.Matches(workflow, "fusion-host-ledge-reconnect").Count,
                2,
                "The manual choice and CI matrix must both expose the ledge reconnect regression smoke.");
        }

        [Test]
        public void FusionPullUpSmoke_ProvesBoundaryTrajectoryAndConnectedOwnerHealth()
        {
            string bootstrap = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Runtime/Transport/Fusion/Traversal/" +
                "FusionTraversalPullUpSmokeBootstrap.cs");
            StringAssert.Contains("fusion-traversal-pullup", bootstrap);
            StringAssert.Contains(
                "#if GC2_TRAVERSAL && (UNITY_EDITOR || DEVELOPMENT_BUILD)",
                bootstrap,
                "The PullUp regression harness must remain absent from release players.");
            StringAssert.Contains("private const int AttemptsRequired = 3", bootstrap);
            StringAssert.Contains("new(-5f, 0f, 7f)", bootstrap);
            StringAssert.Contains("new(-5f, 8f, 7f)", bootstrap);
            StringAssert.Contains("candidate.ContinueB is not TraverseLink candidatePullUp", bootstrap);
            StringAssert.Contains("PullUpMotionName = \"Motion_PullUp\"", bootstrap);
            StringAssert.Contains("TraverseLinkTypeWarpToTarget", bootstrap);
            StringAssert.Contains("motion.RequestTeleport(", bootstrap);
            StringAssert.Contains("ToDriverPosition(actor.Character, targetRoot)", bootstrap);
            StringAssert.Contains("_ = freeClimb.Enter(ShortcutPlayer.Instance", bootstrap);
            StringAssert.Contains("new KeyboardState(Key.W)", bootstrap);
            StringAssert.Contains("--gc2-network-smoke-ready", bootstrap);
            StringAssert.Contains("WriteReadyMarker()", bootstrap);
            StringAssert.Contains("IsReadyMarkerPresent()", bootstrap);

            StringAssert.Contains("TryGetStanceRelativePosition(stance", bootstrap);
            StringAssert.Contains("relativePosition.z >=", bootstrap);
            StringAssert.Contains(
                "freeClimb.PositionB - BoundaryOffsetFromPositionB",
                bootstrap,
                "Boundary sampling must use GC2's authored TraversalStance relative position, " +
                "because collision sweeps may leave the Fusion root below PositionB.");
            AssertAppearsBefore(
                bootstrap,
                "CaptureBoundary(metrics, current)",
                "BeginLinkTrajectory(metrics, actor.Character, pullUp, current)");
            StringAssert.Contains("pullup-boundary-reached", bootstrap);
            StringAssert.Contains("pullup-link-enter", bootstrap);
            StringAssert.Contains("pullup-link-exit", bootstrap);
            StringAssert.Contains("pullup-attempt-result", bootstrap);

            StringAssert.Contains("private const float MaximumHostPreEnterDrop = 0.2f", bootstrap);
            StringAssert.Contains("private const float MaximumHostPreEnterReverseStep = 0.12f", bootstrap);
            StringAssert.Contains("MaximumHostBoundaryAlignmentError", bootstrap);
            StringAssert.Contains("boundaryAlignmentError", bootstrap);
            StringAssert.Contains("private const float GroundTeleportMargin = 0.5f", bootstrap);
            StringAssert.Contains("private const float AirLaunchMargin = 0.25f", bootstrap);
            StringAssert.Contains("preEnterDrop", bootstrap);
            StringAssert.Contains("minimumPreEnterY", bootstrap);
            StringAssert.Contains("entryRollback", bootstrap);
            StringAssert.Contains("entryWarpDistance", bootstrap);
            StringAssert.Contains("linkWarpPosition", bootstrap);
            StringAssert.Contains("landingSurfaceResolved", bootstrap);
            StringAssert.Contains("TryCalculateLandingBounds(", bootstrap);
            StringAssert.Contains("CaptureFinalLanding(", bootstrap);
            StringAssert.Contains("pullup-boundary-coalesced", bootstrap);
            StringAssert.Contains("WriteAttemptAcknowledgement(attemptIndex)", bootstrap);
            StringAssert.Contains("IsAttemptAcknowledgementPresent(attemptIndex)", bootstrap);
            StringAssert.Contains("groundTeleportCount", bootstrap);
            StringAssert.Contains("airLaunchCount", bootstrap);
            StringAssert.Contains("maxRollbackFromHighWater", bootstrap);
            StringAssert.Contains("maxLateralDeviation", bootstrap);
            StringAssert.Contains("duplicateTransitionCount", bootstrap);
            StringAssert.Contains("CharactersUsingCount", bootstrap);
            StringAssert.Contains("GetPresentedPosition(actor)", bootstrap);
            StringAssert.Contains("ActiveRemotePresentationTarget", bootstrap);
            StringAssert.Contains("RequiredConnectedOwnerMoveDistance", bootstrap);
            StringAssert.Contains("ShortcutPlayer.Instance == localOwner.gameObject", bootstrap);
            StringAssert.Contains("connectedOwnerInputHealthy", bootstrap);
            StringAssert.Contains("connectedOwnerTraversalMovementTested", bootstrap);
            StringAssert.Contains("host-pullup-trajectory-stable", bootstrap);
            StringAssert.Contains(
                "observer-host-pullup-stable-local-owner-healthy",
                bootstrap);

            string builder = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Runtime/Transport/Fusion/Traversal/Editor/" +
                "FusionTraversalClimbSmokeBuilder.cs");
            StringAssert.Contains("BuildPullUpNetworkSmokePlayer", builder);
            StringAssert.Contains("Fusion Traversal PullUp", builder);

            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string smokeScript = File.ReadAllText(Path.Combine(
                projectRoot,
                "Tools/run_unity_network_smoke.sh"));
            StringAssert.Contains("fusion-host-pullup", smokeScript);
            StringAssert.Contains("authority_final", smokeScript);
            StringAssert.Contains("client_final", smokeScript);
            StringAssert.Contains("final pose divergence", smokeScript);
            StringAssert.Contains("fusion-traversal-pullup", smokeScript);
            StringAssert.Contains("BuildPullUpNetworkSmokePlayer", smokeScript);
            StringAssert.Contains("host-pullup-trajectory-stable", smokeScript);
            StringAssert.Contains("groundTeleportCount", smokeScript);
            StringAssert.Contains("duplicateTransitionCount", smokeScript);
            StringAssert.Contains("stage=pullup-boundary-reached", smokeScript);
            StringAssert.Contains("--gc2-network-smoke-ready \"$ready_marker\"", smokeScript);

            string workflow = File.ReadAllText(Path.Combine(
                projectRoot,
                ".github/workflows/unity-network-smoke.yml"));
            Assert.GreaterOrEqual(
                Regex.Matches(workflow, "fusion-host-pullup").Count,
                2,
                "The manual choice and CI matrix must both expose the PullUp regression smoke.");
        }

        [Test]
        public void NetworkCiTrace_IsOptInAndCompiledOutOfReleasePlayers()
        {
            string trace = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Runtime/Utilities/NetworkCiTrace.cs");
            StringAssert.Contains("--gc2-network-ci-trace", trace);
            StringAssert.Contains("GC2_NETWORK_CI_TRACE", trace);
            StringAssert.Contains("[Conditional(\"UNITY_EDITOR\")]", trace);
            StringAssert.Contains("[Conditional(\"DEVELOPMENT_BUILD\")]", trace);
            StringAssert.Contains("#if !UNITY_EDITOR && !DEVELOPMENT_BUILD", trace);
            StringAssert.Contains("HasTraversalActivity", trace);
            StringAssert.Contains("TraversalTraceWindowActive", trace);
            StringAssert.Contains("TraversalTraceLingerSeconds = 30f", trace);
            StringAssert.Contains("--gc2-network-trace-role", trace);
            StringAssert.Contains("--gc2-network-trace-frame-rate", trace);
            StringAssert.Contains("GC2_NETWORK_CI_FRAME_RATE", trace);
            StringAssert.Contains("DefaultTraceFrameRate = 60", trace);
            StringAssert.Contains("Application.isEditor", trace);
            StringAssert.Contains("Application.targetFrameRate = targetFrameRate", trace);
            StringAssert.Contains("parsed <= 0", trace);
            StringAssert.Contains("actor-entered", trace);
            StringAssert.Contains("actor-exited", trace);
            StringAssert.Contains("LogOption.NoStacktrace", trace);

            string motionTypes = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Runtime/Motion/NetworkMotionTypes.cs");
            StringAssert.Contains("#if UNITY_EDITOR || DEVELOPMENT_BUILD", motionTypes);
            StringAssert.Contains(
                "return NetworkCiTrace.Enabled && s_ActiveManagerCount > 0;",
                motionTypes);
            StringAssert.Contains("#else\n                return false;", motionTypes);

            string traversal = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Traversal/NetworkTraversalController.cs");
            StringAssert.Contains("NetworkCiTrace.Log(", traversal);

            string motor = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Runtime/Transport/Fusion/" +
                "FusionNativeNetworkCharacterMotor.cs");
            StringAssert.Contains("authority-owner-pose-accepted", motor);
            StringAssert.Contains("owner-reconciled", motor);
            StringAssert.Contains("render-sample", motor);
            StringAssert.Contains("post-render-root-write", motor);
            StringAssert.Contains("teleportKeys=", motor);
            StringAssert.Contains("simulation-sample", motor);
            StringAssert.Contains("owner-payload", motor);
            StringAssert.Contains("NetworkCiTrace.TraversalTraceWindowActive", motor);

            string bridge = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Runtime/Transport/Fusion/" +
                "FusionTransportBridge.cs");
            StringAssert.Contains("unity-heartbeat", bridge);
            StringAssert.Contains("tick-stalled", bridge);
            StringAssert.Contains("tick-recovered", bridge);
            StringAssert.Contains("authority-input-missing", bridge);
            StringAssert.Contains("TryConsumeNetworkInput-returned-false", bridge);
            StringAssert.Contains("shortcutMatches=", bridge);

            string player = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Runtime/Motion/" +
                "UnitPlayerDirectionalNetwork.cs");
            StringAssert.Contains("gc2-player-input", player);
            StringAssert.Contains("character-not-controllable", player);
            StringAssert.Contains("network-input-sink-missing", player);

            string autoRole = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Runtime/Transport/Fusion/" +
                "FusionNetworkCharacterAuto.cs");
            StringAssert.Contains("role-refreshed", autoRole);

            string manager = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Traversal/NetworkTraversalManager.cs");
            StringAssert.Contains("NetworkCiTrace.Enabled", manager);
        }

        [Test]
        public void FusionNativeTraversalPresentation_SmoothsSnapshotUnderrunsWithoutExtrapolation()
        {
            string motor = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Runtime/Transport/Fusion/" +
                "FusionNativeNetworkCharacterMotor.cs");
            string smoothing = ExtractDeclaredMethodBody(
                motor,
                "ApplyRemoteSnapshotUnderrunPresentation");
            StringAssert.Contains("IsLocalLogicalOwner", smoothing);
            StringAssert.Contains("TryGetSnapshotsBuffers", smoothing);
            StringAssert.Contains("MotionFlagTraversalPresentation", smoothing);
            StringAssert.Contains("RemoteTraversalPresentationLingerSeconds", smoothing);
            StringAssert.Contains("traversalPresentationWindow", smoothing);
            StringAssert.Contains("IsRemoteSnapshotTeleportBoundary", smoothing);
            StringAssert.Contains("m_LastRemoteSnapshotTeleportKey", smoothing);
            StringAssert.Contains("selectedTeleportKey", smoothing);
            StringAssert.Contains("collapsedMovingPair", smoothing);
            StringAssert.Contains("QueueRemoteSnapshotPresentationError", smoothing);
            StringAssert.Contains(
                "previousPresentedPosition,\n                    basePosition",
                smoothing,
                "The hard-snap envelope must be measured against the authoritative base pose.");
            StringAssert.Contains(
                "if (!correctionWithinSmoothEnvelope)",
                smoothing);
            StringAssert.Contains(
                "ClearRemoteSnapshotPresentationError();",
                smoothing,
                "An over-envelope authoritative correction must discard stale smoothing error.");
            StringAssert.Contains("LimitRemoteTraversalPresentationStep", smoothing);
            StringAssert.Contains(
                "presentedPosition - basePosition",
                smoothing,
                "A limited frame must retain its residual as presentation-only error so " +
                "the next frame continues converging instead of snapping.");
            StringAssert.Contains("renderTarget.SetPositionAndRotation", smoothing);
            StringAssert.Contains("snapshot-underrun-entered", smoothing);
            StringAssert.Contains("snapshot-underrun-recovered", smoothing);
            StringAssert.DoesNotContain(
                "basePosition + replicatedVelocity",
                smoothing,
                "The observer fallback must not extrapolate beyond trusted snapshots.");

            string queue = ExtractDeclaredMethodBody(
                motor,
                "QueueRemoteSnapshotPresentationError");
            StringAssert.Contains(
                "previousPresentedPosition - basePosition",
                queue);
            StringAssert.Contains("maxReconciliationDistance", queue);

            StringAssert.Contains("presentationOnly=True extrapolation=False", motor);
            StringAssert.DoesNotContain(
                "NetworkOwnerMotionAuthorityHooks.IsContinuousOwnerPose",
                smoothing,
                "Remote smoothing must follow Fusion's pose timeline rather than a separately delivered GC2 traversal state.");

            string limiter = ExtractDeclaredMethodBody(
                motor,
                "LimitRemoteTraversalPresentationStep");
            StringAssert.Contains("!traversalPresentationWindow", limiter);
            StringAssert.Contains("teleportBoundary", limiter);
            StringAssert.Contains("!correctionWithinSmoothEnvelope", limiter);
            StringAssert.Contains("Vector3.MoveTowards", limiter);
            StringAssert.Contains(
                "RemoteTraversalMaximumPresentationFrameStep",
                limiter);
            Assert.GreaterOrEqual(
                CountOccurrences(motor, "ResetRemoteSnapshotPresentation();"),
                6,
                "Spawn, despawn, reset, disable, destroy, and invalid-target paths must " +
                "discard stale presentation state.");
        }

        [Test]
        public void FusionNativeTraversalPresentation_FrameLimiterIsBoundedAndConvergent()
        {
            Type motorType = typeof(FusionNativeNetworkCharacterMotor);
            FieldInfo limitField = motorType.GetField(
                "RemoteTraversalMaximumPresentationFrameStep",
                BindingFlags.Static | BindingFlags.NonPublic);
            MethodInfo limiter = motorType.GetMethod(
                "LimitRemoteTraversalPresentationStep",
                BindingFlags.Static | BindingFlags.NonPublic);
            MethodInfo envelope = motorType.GetMethod(
                "IsRemoteTraversalCorrectionWithinSmoothEnvelope",
                BindingFlags.Static | BindingFlags.NonPublic);
            MethodInfo teleportBoundary = motorType.GetMethod(
                "IsRemoteSnapshotTeleportBoundary",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(limitField, Is.Not.Null);
            Assert.That(limiter, Is.Not.Null);
            Assert.That(envelope, Is.Not.Null);
            Assert.That(teleportBoundary, Is.Not.Null);

            float limit = (float)limitField.GetRawConstantValue();
            Assert.That(limit, Is.GreaterThan(0f).And.LessThanOrEqualTo(0.25f));

            Vector3 previous = Vector3.zero;
            Vector3 candidate = new Vector3(1.1f, 0.7f, -0.2f);
            Vector3 Invoke(
                bool hasPrevious,
                bool traversalWindow,
                bool teleportBoundary,
                bool withinEnvelope,
                Vector3 from,
                Vector3 target)
            {
                return (Vector3)limiter.Invoke(
                    null,
                    new object[]
                    {
                        from,
                        target,
                        hasPrevious,
                        traversalWindow,
                        teleportBoundary,
                        withinEnvelope
                    });
            }

            Assert.That(
                Invoke(true, false, false, true, previous, candidate),
                Is.EqualTo(candidate),
                "Ordinary non-traversal presentation must remain on Fusion's normal path.");
            Assert.That(
                Invoke(true, true, true, true, previous, candidate),
                Is.EqualTo(candidate),
                "A real Fusion teleport-key boundary must snap immediately.");
            Assert.That(
                Invoke(false, true, false, true, previous, candidate),
                Is.EqualTo(candidate),
                "The first observed pose has no prior presentation point to bridge.");
            Assert.That(
                Invoke(true, true, false, false, previous, candidate),
                Is.EqualTo(candidate),
                "Corrections outside the reconciliation envelope must remain hard snaps.");
            Assert.That(
                (bool)envelope.Invoke(
                    null,
                    new object[] { Vector3.zero, Vector3.right * 10f, 3f }),
                Is.False,
                "The hard-snap envelope must measure the authoritative base, not an already " +
                "smoothed candidate that happens to remain near the previous presentation.");
            Assert.That(
                (bool)teleportBoundary.Invoke(
                    null,
                    new object[] { 8, 8, 7, true }),
                Is.True,
                "A skipped old/new snapshot pair must still expose the changed teleport key.");
            Assert.That(
                (bool)teleportBoundary.Invoke(
                    null,
                    new object[] { 8, 8, 8, true }),
                Is.False);

            Vector3 current = previous;
            float previousRemaining = Vector3.Distance(current, candidate);
            for (int i = 0; i < 32 && current != candidate; i++)
            {
                Vector3 next = Invoke(
                    true,
                    true,
                    false,
                    true,
                    current,
                    candidate);
                float step = Vector3.Distance(current, next);
                float remaining = Vector3.Distance(next, candidate);
                Assert.That(step, Is.LessThanOrEqualTo(limit + 0.00001f));
                Assert.That(remaining, Is.LessThan(previousRemaining));
                Assert.That(
                    Vector3.Dot(next - current, candidate - current),
                    Is.GreaterThanOrEqualTo(0f),
                    "The limiter must never overshoot or reverse away from the received pose.");
                current = next;
                previousRemaining = remaining;
            }

            Assert.That(current, Is.EqualTo(candidate));
        }

        private static string[] GetFusionDemoPrefabAssetPaths()
        {
            var roots = new List<string>();
            string legacyRoot = Path.Combine(
                Application.dataPath,
                "Arawn/NetworkingLayerForGC2/Demo/Fusion");
            if (Directory.Exists(legacyRoot)) roots.Add(legacyRoot);

            string installsRoot = Path.Combine(
                Application.dataPath,
                "Plugins/GameCreator/Installs");
            if (Directory.Exists(installsRoot))
            {
                roots.AddRange(Directory
                    .EnumerateDirectories(installsRoot, "*", SearchOption.TopDirectoryOnly)
                    .Where(path => Path.GetFileName(path).StartsWith(
                        "GC2NetworkingLayerFusionTransport.",
                        StringComparison.Ordinal)));
            }

            return roots
                .Distinct(StringComparer.Ordinal)
                .SelectMany(root => Directory.EnumerateFiles(
                    root,
                    "*.prefab",
                    SearchOption.AllDirectories))
                .Select(path => "Assets" + path
                    .Substring(Application.dataPath.Length)
                    .Replace('\\', '/'))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
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
