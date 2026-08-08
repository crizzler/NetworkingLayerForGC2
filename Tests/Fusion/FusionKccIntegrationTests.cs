using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using Arawn.GameCreator2.Networking.Editor;
using Arawn.GameCreator2.Networking.Transport.Fusion.Editor;
using Fusion;
using GameCreator.Runtime.Characters;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Assert = NUnit.Framework.Assert;

namespace Arawn.GameCreator2.Networking.Transport.Fusion.Tests
{
    public sealed class FusionKccIntegrationTests
    {
        private const BindingFlags StaticNonPublic =
            BindingFlags.Static | BindingFlags.NonPublic;

        private const string AdvancedKccDemoPackageId =
            "GC2NetworkingLayerFusionTransport.AdvancedKCCExamples";
        private const string AdvancedKccDemoInstallRoot =
            "Assets/Plugins/GameCreator/Installs/" +
            AdvancedKccDemoPackageId + "@1.0.0";
        private const string AdvancedKccDemoPrefabName =
            "FusionDemoPlayer-AdvancedKCC.prefab";
        private const string AdvancedKccDemoSceneName =
            "Requires Photon Advanced KCC - FusionAdvancedKCCDemo.unity";
        private const string FusionAdvancedKccAssetRoot =
            "Arawn/NetworkingLayerForGC2.FusionAdvancedKCC";

        [Test]
        public void PredictionBackend_AppendsKccWithoutChangingSerializedValues()
        {
            Assert.AreEqual(0, (int)NetworkPredictionBackend.BuiltIn);
            Assert.AreEqual(1, (int)NetworkPredictionBackend.PurrDiction);
            Assert.AreEqual(2, (int)NetworkPredictionBackend.FusionNative);
            Assert.AreEqual(3, (int)NetworkPredictionBackend.FusionKCC);

            Assert.AreEqual(
                0,
                (int)FusionKccSharedAuthorityMode.OwnerMovementAuthority);
            Assert.AreEqual(
                1,
                (int)FusionKccSharedAuthorityMode.SharedMasterMovementAuthority);
        }

        [Test]
        public void KccCoreBoundary_IsPublicAndTransportIndependent()
        {
            Type backend = typeof(FusionKccCharacterBackend);

            Assert.IsTrue(backend.IsPublic);
            Assert.IsTrue(backend.IsSealed);
            Assert.IsTrue(typeof(INetworkCharacterPredictionBackend).IsAssignableFrom(backend));
            Assert.IsTrue(typeof(INetworkAuthoritativePoseProvider).IsAssignableFrom(backend));
            Assert.IsTrue(typeof(IFusionCharacterInputEndpoint).IsAssignableFrom(backend));
            Assert.IsTrue(typeof(IFusionSharedCharacterEndpoint).IsAssignableFrom(backend));
            Assert.IsTrue(typeof(IFusionSharedCharacterRunnerPump).IsAssignableFrom(backend));

            Assert.IsTrue(typeof(IFusionCharacterInputEndpoint).IsPublic);
            Assert.IsTrue(typeof(IFusionSharedCharacterEndpoint).IsPublic);
            Assert.IsTrue(typeof(IFusionSharedCharacterRunnerPump).IsPublic);
            Assert.IsTrue(typeof(IFusionKccRuntimeAdapter).IsPublic);
            Assert.NotNull(
                typeof(IFusionSharedCharacterEndpoint).GetProperty(
                    nameof(IFusionSharedCharacterEndpoint.LastAppliedSharedTransientSourceTick)));
            Assert.IsTrue(
                typeof(IFusionCharacterInputEndpoint)
                    .IsAssignableFrom(typeof(IFusionKccRuntimeAdapter)));
            Assert.IsTrue(
                typeof(IFusionSharedCharacterEndpoint)
                    .IsAssignableFrom(typeof(IFusionKccRuntimeAdapter)));

            Assert.AreEqual(
                typeof(IFusionKccRuntimeAdapter),
                backend.GetProperty(nameof(FusionKccCharacterBackend.RuntimeAdapter))
                    ?.PropertyType);
            Assert.AreEqual(
                typeof(FusionKccSharedAuthorityMode),
                backend.GetProperty(nameof(FusionKccCharacterBackend.SharedAuthorityMode))
                    ?.PropertyType);
            Assert.AreEqual(
                typeof(Vector3),
                backend.GetProperty(nameof(FusionKccCharacterBackend.ReplicatedKccRootScale))
                    ?.PropertyType);
            Assert.AreEqual(
                typeof(Vector3),
                backend.GetProperty(nameof(FusionKccCharacterBackend.KccTeleportFootPosition))
                    ?.PropertyType);
            Assert.AreEqual(
                typeof(Quaternion),
                backend.GetProperty(nameof(FusionKccCharacterBackend.KccTeleportRotation))
                    ?.PropertyType);
            Assert.AreEqual(
                typeof(int),
                backend.GetProperty(nameof(FusionKccCharacterBackend.KccTeleportSequence))
                    ?.PropertyType);
            Assert.AreEqual(
                typeof(NetworkBool),
                backend.GetProperty(nameof(FusionKccCharacterBackend.KccTeleportIsHard))
                    ?.PropertyType);
            foreach (string propertyName in new[]
                     {
                         nameof(FusionKccCharacterBackend.KccMotorCommandSequence),
                         nameof(FusionKccCharacterBackend.KccMotorCommandFlags),
                         nameof(FusionKccCharacterBackend.KccOwnerMotionUntilTick),
                         nameof(FusionKccCharacterBackend.KccServerMotionFromTick),
                         nameof(FusionKccCharacterBackend.KccServerMotionUntilTick)
                     })
            {
                Assert.AreEqual(typeof(int), backend.GetProperty(propertyName)?.PropertyType);
            }
            Assert.AreEqual(
                typeof(uint),
                backend.GetProperty(nameof(FusionKccCharacterBackend.KccServerMotionOperationId))
                    ?.PropertyType);

            Type marker = typeof(FusionKccSetupMarker);
            Assert.IsTrue(marker.IsPublic);
            Assert.IsTrue(marker.IsSealed);
            Assert.NotNull(marker.GetProperty(nameof(FusionKccSetupMarker.MotorObjectCreatedByWizard)));
            Assert.NotNull(marker.GetProperty(nameof(FusionKccSetupMarker.HasCustomerSnapshot)));
            Assert.NotNull(marker.GetProperty(nameof(FusionKccSetupMarker.AdoptedSetupParked)));
            Assert.NotNull(marker.GetProperty(nameof(FusionKccSetupMarker.HasRootControllerSnapshot)));
            Assert.AreEqual(
                typeof(GameObject[]),
                marker.GetProperty(nameof(FusionKccSetupMarker.WizardOwnedProcessorObjects))
                    ?.PropertyType);
            Assert.AreEqual(
                typeof(UnityEngine.Object[]),
                marker.GetProperty(nameof(FusionKccSetupMarker.OriginalKccProcessorObjects))
                    ?.PropertyType);
        }

        [Test]
        public void CoreFusionRuntime_HasNoAdvancedKccCompileDependency()
        {
            string runtimeRoot = AssetPath(
                "Arawn/NetworkingLayerForGC2/Runtime/Transport/Fusion");
            string[] runtimeSources = Directory.GetFiles(
                runtimeRoot,
                "*.cs",
                SearchOption.AllDirectories);

            Assert.IsNotEmpty(runtimeSources);
            foreach (string path in runtimeSources)
            {
                string source = File.ReadAllText(path);
                StringAssert.DoesNotContain(
                    "using Fusion.Addons.KCC",
                    source,
                    $"Core Fusion source directly references the optional addon: {path}");
                StringAssert.DoesNotContain(
                    "global::Fusion.Addons.KCC",
                    source,
                    $"Core Fusion source directly references the optional addon: {path}");
            }

            string[] assemblyDefinitions = Directory.GetFiles(
                runtimeRoot,
                "*.asmdef",
                SearchOption.AllDirectories);
            Assert.IsNotEmpty(assemblyDefinitions);
            foreach (string path in assemblyDefinitions)
            {
                string source = File.ReadAllText(path);
                StringAssert.DoesNotContain("Fusion.Addons.KCC", source);
                StringAssert.DoesNotContain("Assembly-CSharp", source);
            }

            string backendSource = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Runtime/Transport/Fusion/" +
                "FusionKccCharacterBackend.cs");
            StringAssert.Contains("ResolveAdapter(true)", backendSource);
            StringAssert.Contains("RestoreBuiltInControllerForFallback()", backendSource);
            StringAssert.Contains("requests the FusionKCC prediction backend", backendSource);
        }

        [Test]
        public void OptionalKccSources_AreAsmdefFreeAndFullyDefineGuarded()
        {
            string optionalRoot = AssetPath(FusionAdvancedKccAssetRoot);
            Assert.IsTrue(
                Directory.Exists(optionalRoot),
                "The optional KCC integration source root is missing.");

            string[] assemblyDefinitions = Directory.GetFiles(
                optionalRoot,
                "*.asmdef",
                SearchOption.AllDirectories);
            Assert.IsEmpty(
                assemblyDefinitions,
                "The typed KCC integration must compile into Assembly-CSharp so it can " +
                "reference Photon's asmdef-less addon.");

            string[] sources = Directory.GetFiles(
                optionalRoot,
                "*.cs",
                SearchOption.AllDirectories);
            Assert.IsNotEmpty(sources);

            foreach (string path in sources)
            {
                string source = File.ReadAllText(path);
                StringAssert.StartsWith(
                    "#if ARAWN_GC2_FUSION_KCC",
                    source.TrimStart(),
                    $"Optional source is not guarded from its first declaration: {path}");
                StringAssert.EndsWith(
                    "#endif",
                    source.TrimEnd(),
                    $"Optional source does not close its feature guard: {path}");
            }

            string combined = string.Join(
                "\n",
                sources.Select(File.ReadAllText));
            StringAssert.Contains("class FusionKccMotorBody", combined);
            StringAssert.Contains(": NetworkBehaviour", combined);
            StringAssert.Contains("class FusionGc2KccProcessor", combined);
            StringAssert.Contains(": KCCProcessor", combined);
            StringAssert.Contains("class FusionKccCharacterDriver", combined);
            StringAssert.Contains(": TUnitDriver", combined);
            StringAssert.Contains("IFusionKccRuntimeAdapter", combined);
        }

        [Test]
        public void OptionalKccRuntime_UsesManualFusionLifecycleOnly()
        {
            string motor = ReadFusionAdvancedKccSource(
                "Runtime/FusionKccMotorBody.cs");

            string fixedUpdate = ExtractMethodBody(
                motor,
                "public override void FixedUpdateNetwork()");
            StringAssert.Contains("PrepareKccTick(input, tick);", fixedUpdate);
            StringAssert.Contains("m_Kcc.ManualFixedUpdate();", fixedUpdate);
            StringAssert.Contains("ApplyRootFromKcc(m_Kcc.FixedData", fixedUpdate);
            AssertAppearsBefore(
                fixedUpdate,
                "PrepareKccTick(input, tick);",
                "m_Kcc.ManualFixedUpdate();");
            AssertAppearsBefore(
                fixedUpdate,
                "m_Kcc.ManualFixedUpdate();",
                "ApplyRootFromKcc(m_Kcc.FixedData");
            AssertAppearsBefore(
                fixedUpdate,
                "m_Kcc.ManualFixedUpdate();",
                "LastAppliedSharedTransientSourceTick = sharedTransientPayloadTick");

            string render = ExtractMethodBody(motor, "private void RenderInternal()");
            StringAssert.Contains("m_Driver.CaptureRenderIntent", render);
            StringAssert.Contains("m_Driver.CaptureRenderRootMotion", render);
            StringAssert.Contains("m_Kcc.ManualRenderUpdate();", render);
            StringAssert.Contains("ApplyRootFromKcc(m_Kcc.RenderData", render);
            AssertAppearsBefore(
                render,
                "m_Driver.CaptureRenderIntent",
                "m_Kcc.ManualRenderUpdate();");
            AssertAppearsBefore(
                render,
                "m_Kcc.ManualRenderUpdate();",
                "ApplyRootFromKcc(m_Kcc.RenderData");

            StringAssert.Contains("m_Kcc.SetManualUpdate(true);", motor);
            StringAssert.DoesNotContain("m_Kcc.SetManualUpdate(false);", motor);
            StringAssert.Contains(
                "Keep manual update enabled while this companion exists",
                motor);
            Assert.AreEqual(1, CountOccurrences(motor, "m_Kcc.ManualFixedUpdate();"));
            Assert.AreEqual(1, CountOccurrences(motor, "m_Kcc.ManualRenderUpdate();"));
            StringAssert.DoesNotContain("private void Update()", motor);
            StringAssert.DoesNotContain("private void FixedUpdate()", motor);
            StringAssert.DoesNotContain("private void LateUpdate()", motor);
        }

        [Test]
        public void OptionalKccRuntime_DoesNotReplayTickAddressedOneShots()
        {
            string motor = ReadFusionAdvancedKccSource(
                "Runtime/FusionKccMotorBody.cs");
            string driver = ReadFusionAdvancedKccSource(
                "Runtime/FusionKccCharacterDriver.cs");
            string processor = ReadFusionAdvancedKccSource(
                "Runtime/FusionGc2KccProcessor.cs");

            string rememberContinuous = ExtractMethodBody(
                motor,
                "private void RememberContinuousInput(FusionNativeCharacterInput input)");
            StringAssert.Contains("Flags = input.HasContinuousOwnerPose", rememberContinuous);
            StringAssert.Contains("RootMotionDelta = Vector3.zero", rememberContinuous);
            StringAssert.Contains("RootMotionWeight = 0f", rememberContinuous);
            StringAssert.Contains("JumpForce = 0f", rememberContinuous);

            string resolveInput = ExtractMethodBody(
                motor,
                "private bool TryResolveSimulationInput(");
            AssertAppearsBefore(
                resolveInput,
                "sharedTransientPayloadTick = input.SourceTick",
                "input.SourceTick = transient.TrustedTick");

            string holdContinuous = ExtractMethodBody(
                motor,
                "private bool TryHoldContinuousInput(");
            StringAssert.Contains("input = m_LastContinuousInput", holdContinuous);
            StringAssert.Contains("input.SourceTick = tick", holdContinuous);
            StringAssert.DoesNotContain("CaptureInput", holdContinuous);

            string renderIntent = ExtractMethodBody(
                driver,
                "internal FusionNativeCharacterInput CaptureRenderIntent(int tick)");
            StringAssert.Contains("Flags = 0", renderIntent);
            StringAssert.Contains("RootMotionDelta = Vector3.zero", renderIntent);
            StringAssert.Contains("RootMotionWeight = 0f", renderIntent);
            StringAssert.Contains("JumpForce = 0f", renderIntent);

            string captureInput = ExtractMethodBody(
                driver,
                "internal FusionNativeCharacterInput CaptureInput(int tick)");
            StringAssert.Contains("FlagResetVerticalVelocity", captureInput);
            StringAssert.Contains("m_ResetVerticalVelocityPending = false", captureInput);
            StringAssert.Contains("FlagCollisionChanged", captureInput);
            StringAssert.Contains("m_HasPendingCollisionChange = false", captureInput);

            string execute = ExtractMethodBody(
                processor,
                "public void Execute(");
            StringAssert.Contains("kcc.EnqueuePostProcess(PostProcessPreparedData)", execute);
            StringAssert.Contains("if (!kcc.IsInFixedUpdate || !m_HasTickCommands) return;", execute);
            StringAssert.Contains("data.JumpImpulse += m_JumpImpulse", execute);
            StringAssert.Contains("m_JumpImpulse * mass", execute);

            string postProcess = ExtractMethodBody(
                processor,
                "private void PostProcessPreparedData(");
            StringAssert.Contains("bool hasFixedCommands", postProcess);
            StringAssert.Contains(
                "data.ExternalDelta = m_TeleportFootPosition - data.BasePosition",
                postProcess);
            StringAssert.Contains(
                "Vector3 blendedDelta = Vector3.Lerp(",
                postProcess);
            StringAssert.Contains("data.ExternalDelta += blendedDelta", postProcess);
            StringAssert.Contains("ClearTickCommands();", postProcess);
        }

        [Test]
        public void OptionalKccRuntime_PreservesAuthorityAndRootFootInvariants()
        {
            string motor = ReadFusionAdvancedKccSource(
                "Runtime/FusionKccMotorBody.cs");
            string driver = ReadFusionAdvancedKccSource(
                "Runtime/FusionKccCharacterDriver.cs");

            StringAssert.Contains("Object.ReleaseStateAuthority();", motor);
            StringAssert.Contains("Object.RequestStateAuthority();", motor);
            StringAssert.Contains("source == m_Identity.LogicalOwner", motor);
            StringAssert.Contains("sourceTick <= m_LastSharedContinuousPayloadTick", motor);
            StringAssert.Contains("sourceTick <= m_LastSharedTransientPayloadTick", motor);
            StringAssert.Contains("SharedTransientCapacity = 128", motor);
            StringAssert.Contains("settings.AllowClientTeleports = false", motor);
            StringAssert.Contains("m_Kcc.ResolveCollision = m_InstalledResolveCollision", motor);
            StringAssert.Contains("collider.transform.IsChildOf(m_Root)", motor);
            StringAssert.Contains("m_PreviousResolveCollision?.Invoke", motor);
            StringAssert.Contains("m_Kcc.SynchronizeTransform(", motor);
            StringAssert.Contains("KeepMotorAtUnitWorldScale();", motor);
            StringAssert.Contains(
                "footPosition + Vector3.up * (ActiveHeight * 0.5f)",
                motor);
            StringAssert.Contains(
                "rootPosition - Vector3.up * (ActiveHeight * 0.5f)",
                motor);

            string backend = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Runtime/Transport/Fusion/" +
                "FusionKccCharacterBackend.cs");
            string teleport = ExtractMethodBody(
                backend,
                "public bool QueueAuthoritativeTeleport(");
            StringAssert.Contains("!CanApplyAuthoritativeKccCommands", teleport);
            StringAssert.Contains("ReplicatedKccRootScale", backend);
            StringAssert.Contains("KccTeleportSequence", backend);
            StringAssert.Contains("KccMotorCommandSequence", backend);
            StringAssert.Contains("QueueAuthoritativeVerticalVelocityReset", backend);
            StringAssert.Contains("QueueAuthoritativeCollision", backend);
            StringAssert.Contains("TryGetAuthoritativeMotorCommand", backend);
            StringAssert.Contains("OpenOwnerMotionWindow", backend);
            StringAssert.Contains("OpenServerOwnerMotionWindow", backend);
            StringAssert.Contains("IsServerMotionTickAuthorized", backend);

            foreach (string contract in new[]
                     {
                         "INetworkDirectionalInputSink",
                         "INetworkOwnerMotionAuthority",
                         "INetworkServerOwnerMotionAuthority",
                         "INetworkExternalMoveDirectionSink",
                         "INetworkNavMeshCommandSink"
                     })
            {
                StringAssert.Contains(contract, driver);
            }
        }

        [Test]
        public void OptionalKccRuntime_EnforcesGc2FixedTickMovementContract()
        {
            string motor = ReadFusionAdvancedKccSource(
                "Runtime/FusionKccMotorBody.cs");
            string driver = ReadFusionAdvancedKccSource(
                "Runtime/FusionKccCharacterDriver.cs");
            string processor = ReadFusionAdvancedKccSource(
                "Runtime/FusionGc2KccProcessor.cs");

            StringAssert.Contains(
                "m_Role == NetworkCharacter.NetworkRole.RemoteClient",
                motor);
            StringAssert.Contains("stateAuthorityNpc", motor);
            StringAssert.Contains("m_Driver.CaptureInput(tick)", motor);
            StringAssert.Contains("if (!m_AdapterInitialized) return false", motor);
            StringAssert.Contains("m_Kcc.PredictionError.magnitude", motor);
            StringAssert.Contains("UpdateGroundedStateAfterSimulation", motor);
            StringAssert.Contains("m_Character?.OnLand(verticalVelocityBefore)", motor);
            StringAssert.Contains("CanApplyJump(commandTick)", motor);
            StringAssert.Contains("m_Character.Motion.JumpCooldown", motor);
            StringAssert.Contains("m_Character.Jump.CanJump()", motor);
            StringAssert.Contains("ValidateRootMotion(", motor);
            StringAssert.Contains("ResolveAuthoritativeInputTick(input, tick)", motor);
            StringAssert.Contains("IsServerMotionTickAuthorized(tick)", motor);
            StringAssert.Contains("Vector3.ClampMagnitude", motor);
            StringAssert.Contains("NotifyAcceptedOwnerPoseAfterSimulation", motor);

            StringAssert.Contains("m_Motor?.OpenOwnerMotionWindow", driver);
            StringAssert.Contains("m_Motor?.OpenServerOwnerMotionWindow", driver);
            StringAssert.Contains("m_Motor?.CloseServerOwnerMotionWindow", driver);
            StringAssert.Contains("ProcessAxonometryDirection", driver);

            StringAssert.Contains("PreparePriority = 2000f", processor);
            StringAssert.Contains("IPrepareData", processor);
            StringAssert.Contains("EnqueuePostProcess(PostProcessPreparedData)", processor);
            StringAssert.Contains("data.BasePosition", processor);
            StringAssert.Contains("m_TerminalVelocity", processor);
            StringAssert.Contains("data.KinematicVelocity = m_UpdateKinematics", processor);
            StringAssert.Contains("Vector3.Lerp(", processor);
            StringAssert.Contains("ProcessAxonometryTranslation", processor);

            string configureAuthority = ExtractMethodBody(
                motor,
                "private void ConfigureKccAuthorityBehaviour()");
            StringAssert.Contains("interpolateSharedMasterInputAuthority", configureAuthority);
            StringAssert.Contains(
                "EKCCAuthorityBehavior.PredictFixed_InterpolateRender",
                configureAuthority);
            StringAssert.Contains("settings.ForcePredictedLookRotation", configureAuthority);
            StringAssert.Contains("!interpolateSharedMasterInputAuthority", configureAuthority);
        }

        [Test]
        public void DefineManager_UsesApiSignaturesAndHandlesAddonRemoval()
        {
            string source = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Editor/DefineSymbols/" +
                "GC2NetworkingDefineSymbols.cs");

            StringAssert.Contains(
                "SYMBOL_FUSION_KCC = \"ARAWN_GC2_FUSION_KCC\"",
                source);
            StringAssert.Contains("IsFusionKccApiInstalled()", source);
            StringAssert.Contains("HasRequiredFusionKccApi", source);
            StringAssert.Contains("HasRequiredFusionKccSourceApi", source);
            StringAssert.Contains("SetPosition", source);
            StringAssert.Contains("EnqueuePostProcess", source);
            StringAssert.Contains("BasePosition", source);
            StringAssert.Contains("SetManualUpdate", source);
            StringAssert.Contains("ManualFixedUpdate", source);
            StringAssert.Contains("ManualRenderUpdate", source);
            StringAssert.Contains("SynchronizeTransform", source);
            StringAssert.Contains("SetShape", source);
            StringAssert.Contains("ResolveCollision", source);
            StringAssert.Contains("Fusion.Addons.KCC.KCCUtility", source);
            StringAssert.Contains("ResolveProcessor", source);
            StringAssert.Contains(
                "public static bool ResolveProcessor(UnityEngine.Object unityObject, ",
                source);
            StringAssert.Contains("PredictionError", source);
            StringAssert.Contains("NotifyFusionKccAssetsDeleted", source);
            StringAssert.Contains("AssetPostprocessor", source);
            StringAssert.Contains("AssetModificationProcessor", source);
            StringAssert.Contains(
                "ManageSymbol(symbolList, SYMBOL_FUSION_KCC, IsFusionKccApiInstalled())",
                source);
        }

        [Test]
        public void FusionWizard_ExposesKccBackendPoliciesAndConversionHooks()
        {
            string wizard = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Editor/Transport/Fusion/" +
                "FusionSceneSetupWizard.cs");
            string integration = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Editor/Transport/Fusion/" +
                "FusionKccEditorIntegration.cs");

            StringAssert.Contains("Fusion Native (Recommended)", wizard);
            StringAssert.Contains("Fusion Advanced KCC (Optional Addon)", wizard);
            StringAssert.Contains("Built-in Legacy", wizard);
            StringAssert.Contains("Owner Movement Authority (Recommended)", wizard);
            StringAssert.Contains("Shared Master Movement Authority", wizard);
            StringAssert.Contains("EnsureSingleKccBackendProxy", wizard);
            StringAssert.Contains("RemoveOptionalKccSetup", wizard);
            StringAssert.Contains("RemoveNativeMotors", wizard);
            StringAssert.Contains("ConfigurePlayerPrefab", wizard);
            StringAssert.Contains("RemoveFromPlayerPrefab", integration);
            StringAssert.Contains("ValidatePlayerPrefab", integration);
            StringAssert.Contains("TypeCache", integration);
            StringAssert.Contains("Assembly-CSharp-Editor", integration);
        }

        [Test]
        public void FusionWizard_KccBackendRegistersProjectAssemblyForWeaving()
        {
            Type wizardType = Type.GetType(
                "Arawn.GameCreator2.Networking.Transport.Fusion.Editor." +
                "FusionSceneSetupWizard, " +
                "Arawn.GameCreator2.Networking.Transport.Fusion.Editor");
            Assert.NotNull(wizardType);

            MethodInfo ensureEntry = wizardType.GetMethod(
                "EnsureAssemblyWeaveEntry",
                StaticNonPublic);
            Assert.NotNull(ensureEntry);

            var config = new NetworkProjectConfig
            {
                AssembliesToWeave = new[] { "Fusion.Unity", "Customer.Transport" }
            };

            Assert.IsTrue((bool)ensureEntry.Invoke(
                null,
                new object[] { config, "Assembly-CSharp" }));
            CollectionAssert.AreEqual(
                new[] { "Fusion.Unity", "Customer.Transport", "Assembly-CSharp" },
                config.AssembliesToWeave);

            Assert.IsFalse((bool)ensureEntry.Invoke(
                null,
                new object[] { config, "Assembly-CSharp" }));
            CollectionAssert.AreEqual(
                new[] { "Fusion.Unity", "Customer.Transport", "Assembly-CSharp" },
                config.AssembliesToWeave);

            string wizardSource = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Editor/Transport/Fusion/" +
                "FusionSceneSetupWizard.cs");
            StringAssert.Contains(
                "m_PredictionBackend == NetworkPredictionBackend.FusionKCC",
                wizardSource);
            StringAssert.Contains("ProjectRuntimeAssemblyName", wizardSource);
        }

        [Test]
        public void OptionalKccEditorExtension_ConfiguresIdempotentlyAndCleansUp()
        {
#if ARAWN_GC2_FUSION_KCC
            Type extensionType = Type.GetType(
                "Arawn.GameCreator2.Networking.Transport.Fusion.KCC.Editor." +
                "FusionKccEditorSetupExtension, Assembly-CSharp-Editor");
            Assert.NotNull(
                extensionType,
                "The KCC define is enabled, but its Assembly-CSharp-Editor setup extension " +
                "was not compiled.");
            Assert.IsTrue(
                typeof(IFusionKccEditorSetupExtension).IsAssignableFrom(extensionType));

            var extension = (IFusionKccEditorSetupExtension)
                Activator.CreateInstance(extensionType);
            Assert.IsTrue(extension.IsAvailable, extension.UnavailableReason);

            var root = new GameObject("FusionKccTestPlayer");
            try
            {
                root.AddComponent<Character>();
                root.AddComponent<CharacterController>();
                root.AddComponent<NetworkObject>();
                root.AddComponent<NetworkCharacter>();
                root.AddComponent<FusionNetworkIdentity>();
                FusionKccCharacterBackend backend =
                    root.AddComponent<FusionKccCharacterBackend>();

                var changes = new System.Collections.Generic.List<string>();
                Assert.IsTrue(
                    extension.ConfigurePlayerPrefab(
                        root,
                        new FusionKccEditorSetupOptions(
                            FusionKccSharedAuthorityMode.OwnerMovementAuthority),
                        changes,
                        out string firstError),
                    firstError);

                Transform motor = root.transform.Find("Fusion KCC Motor");
                Assert.NotNull(motor);
                Assert.IsFalse(root.GetComponent<CharacterController>().enabled);
                Assert.NotNull(backend.RuntimeAdapterComponent);
                Assert.AreEqual(motor, backend.RuntimeAdapterComponent.transform);
                Assert.NotNull(motor.GetComponent<NetworkObject>());
                Assert.NotNull(motor.GetComponent<Rigidbody>());
                FusionKccSetupMarker marker =
                    motor.GetComponent<FusionKccSetupMarker>();
                Assert.NotNull(marker);
                Assert.IsTrue(marker.MotorObjectCreatedByWizard);
                Assert.NotNull(motor.GetComponent(
                    Type.GetType("Fusion.Addons.KCC.KCC, Assembly-CSharp")));

                Type kccProcessorType = Type.GetType(
                    "Fusion.Addons.KCC.KCCProcessor, Assembly-CSharp");
                Assert.NotNull(kccProcessorType);
                GameObject[] firstOwnedProcessorObjects =
                    marker.WizardOwnedProcessorObjects
                        .Where(processorObject => processorObject != null)
                        .ToArray();
                Assert.AreEqual(
                    4,
                    firstOwnedProcessorObjects.Length,
                    "The wizard must create one dedicated child for each required processor.");
                CollectionAssert.AllItemsAreUnique(firstOwnedProcessorObjects);
                foreach (GameObject processorObject in firstOwnedProcessorObjects)
                {
                    Assert.AreEqual(
                        motor,
                        processorObject.transform.parent,
                        "Wizard-owned processor support objects must be direct motor children.");
                    Assert.AreEqual(
                        1,
                        processorObject.GetComponents(kccProcessorType).Length,
                        "Photon permits exactly one KCCProcessor per GameObject.");
                    NetworkObject processorNetworkObject =
                        processorObject.GetComponent<NetworkObject>();
                    Assert.NotNull(processorNetworkObject);
                    Assert.AreEqual(
                        NetworkObjectFlags.MasterClientObject,
                        processorNetworkObject.Flags &
                        NetworkObjectFlags.MasterClientObject,
                        "Processor support NetworkObjects must remain on the Shared master.");
                    Assert.AreEqual(
                        (NetworkObjectFlags)0,
                        processorNetworkObject.Flags &
                        NetworkObjectFlags.HasMainNetworkTRSP,
                        "Processor support objects must not become competing transform writers.");
                }

                changes.Clear();
                Assert.IsTrue(
                    extension.ConfigurePlayerPrefab(
                        root,
                        new FusionKccEditorSetupOptions(
                            FusionKccSharedAuthorityMode.OwnerMovementAuthority),
                        changes,
                        out string secondError),
                    secondError);
                Assert.AreEqual(
                    1,
                    root.transform.Cast<Transform>().Count(
                        child => child.name == "Fusion KCC Motor"));
                CollectionAssert.AreEquivalent(
                    firstOwnedProcessorObjects,
                    marker.WizardOwnedProcessorObjects.Where(value => value != null));
                Assert.AreEqual(
                    4,
                    motor.Cast<Transform>().Count(child =>
                        child.name.StartsWith(
                            "Fusion KCC Processor - ",
                            StringComparison.Ordinal)),
                    "Repeating setup must not duplicate processor support objects.");

                var issues =
                    new System.Collections.Generic.List<FusionKccEditorValidationIssue>();
                extension.ValidatePlayerPrefab(
                    root,
                    new FusionKccEditorSetupOptions(
                        FusionKccSharedAuthorityMode.OwnerMovementAuthority,
                        requireAppliedSetup: true),
                    issues);
                Assert.IsFalse(
                    issues.Any(issue =>
                        issue.Severity == FusionKccEditorIssueSeverity.Error),
                    string.Join("\n", issues.Select(issue => issue.Message)));

                Type processorType = Type.GetType(
                    "Arawn.GameCreator2.Networking.Transport.Fusion.KCC." +
                    "FusionGc2KccProcessor, Assembly-CSharp");
                Assert.NotNull(processorType);
                UnityEngine.Behaviour requiredProcessor =
                    marker.WizardOwnedProcessorObjects
                        .Where(processorObject => processorObject != null)
                        .Select(processorObject =>
                            processorObject.GetComponent(processorType))
                        .OfType<UnityEngine.Behaviour>()
                        .SingleOrDefault();
                Assert.NotNull(requiredProcessor);
                Assert.AreEqual(
                    motor,
                    requiredProcessor.transform.parent,
                    "The GC2 processor must be instance-local to the configured KCC motor.");
                requiredProcessor.enabled = false;
                issues.Clear();
                extension.ValidatePlayerPrefab(
                    root,
                    new FusionKccEditorSetupOptions(
                        FusionKccSharedAuthorityMode.OwnerMovementAuthority,
                        requireAppliedSetup: true),
                    issues);
                Assert.IsTrue(
                    issues.Any(issue =>
                        issue.Severity == FusionKccEditorIssueSeverity.Error &&
                        issue.Message.Contains("must be enabled")),
                    "A disabled required KCC processor must be a postflight error.");

                changes.Clear();
                Assert.IsTrue(
                    extension.ConfigurePlayerPrefab(
                        root,
                        new FusionKccEditorSetupOptions(
                            FusionKccSharedAuthorityMode.OwnerMovementAuthority),
                        changes,
                        out string reenableError),
                    reenableError);
                Assert.IsTrue(requiredProcessor.enabled);

                Assert.IsTrue(
                    extension.RemoveFromPlayerPrefab(
                        root,
                        changes,
                        out string removeError),
                    removeError);
                Assert.IsNull(root.transform.Find("Fusion KCC Motor"));
                Assert.IsTrue(root.GetComponent<CharacterController>().enabled);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
#else
            Assert.Pass(
                "Advanced KCC is optional; its typed editor extension is intentionally " +
                "excluded when ARAWN_GC2_FUSION_KCC is absent.");
#endif
        }

        [Test]
        public void OptionalKccEditorExtension_AdoptsScaledMotorAndParksExactState()
        {
#if ARAWN_GC2_FUSION_KCC
            Type extensionType = Type.GetType(
                "Arawn.GameCreator2.Networking.Transport.Fusion.KCC.Editor." +
                "FusionKccEditorSetupExtension, Assembly-CSharp-Editor");
            Type kccType = Type.GetType("Fusion.Addons.KCC.KCC, Assembly-CSharp");
            Assert.NotNull(extensionType);
            Assert.NotNull(kccType);

            var extension = (IFusionKccEditorSetupExtension)
                Activator.CreateInstance(extensionType);
            var root = new GameObject("ScaledAdoptedKccPlayer");
            ScriptableObject wizard = null;
            try
            {
                Character character = root.AddComponent<Character>();
                CharacterController controller = root.AddComponent<CharacterController>();
                controller.enabled = false;
                root.AddComponent<NetworkObject>();
                root.AddComponent<NetworkCharacter>();
                root.AddComponent<FusionNetworkIdentity>();
                root.AddComponent<FusionKccCharacterBackend>();
                root.transform.localScale = new Vector3(2f, 3f, 4f);

                var customerMotor = new GameObject("Customer KCC Motor");
                customerMotor.transform.SetParent(root.transform, false);
                Vector3 originalPosition = new Vector3(1f, 2f, 3f);
                Quaternion originalRotation = Quaternion.Euler(4f, 5f, 6f);
                Vector3 originalScale = new Vector3(0.75f, 1.25f, 1.5f);
                customerMotor.transform.localPosition = originalPosition;
                customerMotor.transform.localRotation = originalRotation;
                customerMotor.transform.localScale = originalScale;
                var kcc = customerMotor.AddComponent(kccType) as MonoBehaviour;
                Assert.NotNull(kcc);

                var changes = new System.Collections.Generic.List<string>();
                Assert.IsTrue(
                    extension.ConfigurePlayerPrefab(
                        root,
                        new FusionKccEditorSetupOptions(
                            FusionKccSharedAuthorityMode.OwnerMovementAuthority),
                        changes,
                        out string configureError),
                    configureError);

                Transform configuredMotor = root.transform.Find("Fusion KCC Motor");
                Assert.AreEqual(customerMotor.transform, configuredMotor);
                float expectedLocalY = -character.Motion.Height * 0.5f /
                                       root.transform.lossyScale.y;
                Assert.AreEqual(expectedLocalY, configuredMotor.localPosition.y, 0.0001f);
                Assert.Less(
                    (configuredMotor.lossyScale - Vector3.one).sqrMagnitude,
                    0.0001f,
                    "The KCC motor must stay unit scale under a scaled GC2 root.");

                FusionKccSetupMarker marker =
                    configuredMotor.GetComponent<FusionKccSetupMarker>();
                Assert.NotNull(marker);
                Assert.IsFalse(marker.MotorObjectCreatedByWizard);
                Assert.IsTrue(marker.HasCustomerSnapshot);
                Assert.IsFalse(marker.AdoptedSetupParked);

                Assert.IsTrue(
                    extension.RemoveFromPlayerPrefab(
                        root,
                        changes,
                        out string removeError),
                    removeError);

                Assert.AreEqual("Customer KCC Motor", customerMotor.name);
                Assert.Less(
                    (customerMotor.transform.localPosition - originalPosition).sqrMagnitude,
                    0.000001f);
                Assert.Less(
                    Quaternion.Angle(customerMotor.transform.localRotation, originalRotation),
                    0.001f);
                Assert.Less(
                    (customerMotor.transform.localScale - originalScale).sqrMagnitude,
                    0.000001f);
                Assert.IsFalse(controller.enabled);
                Assert.IsFalse(kcc.enabled);
                Assert.IsTrue(marker.AdoptedSetupParked);

                Type wizardType = Type.GetType(
                    "Arawn.GameCreator2.Networking.Transport.Fusion.Editor." +
                    "FusionSceneSetupWizard, " +
                    "Arawn.GameCreator2.Networking.Transport.Fusion.Editor");
                Assert.NotNull(wizardType);
                wizard = ScriptableObject.CreateInstance(wizardType);
                FieldInfo backendField = wizardType.GetField(
                    "m_PredictionBackend",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                MethodInfo conflictMethod = wizardType.GetMethod(
                    "IsCompetingSceneComponent",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(backendField);
                Assert.NotNull(conflictMethod);
                backendField.SetValue(wizard, NetworkPredictionBackend.FusionNative);
                Assert.IsFalse(
                    (bool)conflictMethod.Invoke(wizard, new object[] { kcc }),
                    "A disabled, parked customer KCC must not block Fusion Native validation.");
            }
            finally
            {
                if (wizard != null) UnityEngine.Object.DestroyImmediate(wizard);
                UnityEngine.Object.DestroyImmediate(root);
            }
#else
            Assert.Pass(
                "Advanced KCC is optional; adopted-motor conversion is exercised only when " +
                "ARAWN_GC2_FUSION_KCC is enabled.");
#endif
        }

        [Test]
        public void OptionalKccEditorExtension_PreservesExternalProcessorsAndDeletesOnlyOwnedChildren()
        {
#if ARAWN_GC2_FUSION_KCC
            Type extensionType = Type.GetType(
                "Arawn.GameCreator2.Networking.Transport.Fusion.KCC.Editor." +
                "FusionKccEditorSetupExtension, Assembly-CSharp-Editor");
            Type kccType = Type.GetType("Fusion.Addons.KCC.KCC, Assembly-CSharp");
            Type kccProcessorType = Type.GetType(
                "Fusion.Addons.KCC.KCCProcessor, Assembly-CSharp");
            Type environmentType = Type.GetType(
                "Fusion.Addons.KCC.EnvironmentProcessor, Assembly-CSharp");
            Type groundSnapType = Type.GetType(
                "Fusion.Addons.KCC.GroundSnapProcessor, Assembly-CSharp");
            Type stepUpType = Type.GetType(
                "Fusion.Addons.KCC.StepUpProcessor, Assembly-CSharp");
            Type gc2ProcessorType = Type.GetType(
                "Arawn.GameCreator2.Networking.Transport.Fusion.KCC." +
                "FusionGc2KccProcessor, Assembly-CSharp");
            Assert.NotNull(extensionType);
            Assert.NotNull(kccType);
            Assert.NotNull(kccProcessorType);
            Assert.NotNull(environmentType);
            Assert.NotNull(groundSnapType);
            Assert.NotNull(stepUpType);
            Assert.NotNull(gc2ProcessorType);

            var extension = (IFusionKccEditorSetupExtension)
                Activator.CreateInstance(extensionType);
            var root = new GameObject("AdoptedKccExternalProcessorPlayer");
            try
            {
                root.AddComponent<Character>();
                root.AddComponent<CharacterController>();
                root.AddComponent<NetworkObject>();
                root.AddComponent<NetworkCharacter>();
                root.AddComponent<FusionNetworkIdentity>();
                root.AddComponent<FusionKccCharacterBackend>();

                var customerMotor = new GameObject("Customer KCC Motor");
                customerMotor.transform.SetParent(root.transform, false);
                var kcc = customerMotor.AddComponent(kccType) as MonoBehaviour;
                Assert.NotNull(kcc);

                var customerChild = new GameObject("Customer Motor Child");
                customerChild.transform.SetParent(customerMotor.transform, false);

                GameObject environmentObject = CreateReflectedProcessorObject(
                    root.transform,
                    "Customer Environment",
                    environmentType,
                    out Component environment);
                GameObject groundSnapObject = CreateReflectedProcessorObject(
                    root.transform,
                    "Customer Ground Snap Provider",
                    groundSnapType,
                    out Component groundSnap);
                GameObject stepUpObject = CreateReflectedProcessorObject(
                    root.transform,
                    "Customer Step Up",
                    stepUpType,
                    out Component stepUp);

                var originalProcessorReferences = new UnityEngine.Object[]
                {
                    environment,
                    groundSnapObject,
                    stepUp
                };
                SetKccProcessorReferences(kcc, originalProcessorReferences);

                var changes = new System.Collections.Generic.List<string>();
                Assert.IsTrue(
                    extension.ConfigurePlayerPrefab(
                        root,
                        new FusionKccEditorSetupOptions(
                            FusionKccSharedAuthorityMode.OwnerMovementAuthority),
                        changes,
                        out string configureError),
                    configureError);

                Transform configuredMotor = root.transform.Find("Fusion KCC Motor");
                Assert.AreEqual(customerMotor.transform, configuredMotor);
                FusionKccSetupMarker marker =
                    configuredMotor.GetComponent<FusionKccSetupMarker>();
                Assert.NotNull(marker);
                Assert.IsTrue(marker.HasCustomerSnapshot);

                UnityEngine.Object[] configuredProcessorReferences =
                    GetKccProcessorReferences(kcc);
                Assert.AreEqual(4, configuredProcessorReferences.Length);
                CollectionAssert.AreEqual(
                    originalProcessorReferences,
                    configuredProcessorReferences.Take(3).ToArray(),
                    "Resolvable customer component and provider-GameObject references must " +
                    "remain exactly as serialized.");

                GameObject[] ownedProcessorObjects =
                    marker.WizardOwnedProcessorObjects
                        .Where(processorObject => processorObject != null)
                        .ToArray();
                Assert.AreEqual(
                    1,
                    ownedProcessorObjects.Length,
                    "Only the instance-local GC2 processor should be wizard-owned when all " +
                    "official processor references are reusable customer objects.");
                GameObject ownedGc2ProcessorObject = ownedProcessorObjects.Single();
                Assert.AreEqual(configuredMotor, ownedGc2ProcessorObject.transform.parent);
                Assert.NotNull(ownedGc2ProcessorObject.GetComponent(gc2ProcessorType));
                Assert.AreEqual(
                    1,
                    ownedGc2ProcessorObject.GetComponents(kccProcessorType).Length);
                NetworkObject ownedProcessorNetworkObject =
                    ownedGc2ProcessorObject.GetComponent<NetworkObject>();
                Assert.NotNull(ownedProcessorNetworkObject);
                Assert.AreEqual(
                    NetworkObjectFlags.MasterClientObject,
                    ownedProcessorNetworkObject.Flags &
                    NetworkObjectFlags.MasterClientObject);

                foreach (GameObject externalObject in new[]
                         {
                             environmentObject,
                             groundSnapObject,
                             stepUpObject
                         })
                {
                    Assert.AreEqual(1, externalObject.GetComponents(kccProcessorType).Length);
                    Assert.NotNull(externalObject.GetComponent<NetworkObject>());
                }
                Assert.AreSame(
                    groundSnap,
                    groundSnapObject.GetComponent(groundSnapType),
                    "The provider GameObject must continue resolving to the same processor.");

                changes.Clear();
                Assert.IsTrue(
                    extension.ConfigurePlayerPrefab(
                        root,
                        new FusionKccEditorSetupOptions(
                            FusionKccSharedAuthorityMode.OwnerMovementAuthority),
                        changes,
                        out string repeatError),
                    repeatError);
                CollectionAssert.AreEqual(
                    originalProcessorReferences,
                    GetKccProcessorReferences(kcc).Take(3).ToArray());
                Assert.AreSame(
                    ownedGc2ProcessorObject,
                    marker.WizardOwnedProcessorObjects.Single(value => value != null),
                    "Repeated configuration must reuse the recorded GC2 processor child.");

                Assert.IsTrue(
                    extension.RemoveFromPlayerPrefab(
                        root,
                        changes,
                        out string removeError),
                    removeError);

                CollectionAssert.AreEqual(
                    originalProcessorReferences,
                    GetKccProcessorReferences(kcc),
                    "Converting an adopted motor away from KCC must restore the exact " +
                    "customer processor array.");
                Assert.IsTrue(
                    ownedGc2ProcessorObject == null,
                    "Only the recorded wizard-owned processor child should be deleted.");
                Assert.IsFalse(customerChild == null);
                Assert.AreEqual(customerMotor.transform, customerChild.transform.parent);
                Assert.IsFalse(environmentObject == null);
                Assert.IsFalse(groundSnapObject == null);
                Assert.IsFalse(stepUpObject == null);
                Assert.AreSame(environment, environmentObject.GetComponent(environmentType));
                Assert.AreSame(groundSnap, groundSnapObject.GetComponent(groundSnapType));
                Assert.AreSame(stepUp, stepUpObject.GetComponent(stepUpType));
                Assert.IsTrue(marker.AdoptedSetupParked);

                Type wizardType = Type.GetType(
                    "Arawn.GameCreator2.Networking.Transport.Fusion.Editor." +
                    "FusionSceneSetupWizard, " +
                    "Arawn.GameCreator2.Networking.Transport.Fusion.Editor");
                Assert.NotNull(wizardType);
                MethodInfo isCompeting = wizardType.GetMethod(
                    "IsCompetingSceneComponent",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(isCompeting);
                ScriptableObject wizard = ScriptableObject.CreateInstance(wizardType);
                try
                {
                    SetPrivateField(
                        wizard,
                        "m_PredictionBackend",
                        NetworkPredictionBackend.FusionNative);
                    foreach (MonoBehaviour parkedProcessor in new[]
                             {
                                 (MonoBehaviour)environment,
                                 (MonoBehaviour)groundSnap,
                                 (MonoBehaviour)stepUp
                             })
                    {
                        Assert.IsFalse(
                            (bool)isCompeting.Invoke(
                                wizard,
                                new object[] { parkedProcessor }),
                            "Exact customer processor refs belonging to a parked adopted " +
                            "KCC setup must not block or be disabled during conversion to " +
                            "Fusion Native.");
                    }
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(wizard);
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
#else
            Assert.Pass(
                "Advanced KCC is optional; external processor preservation is exercised only " +
                "when ARAWN_GC2_FUSION_KCC is enabled.");
#endif
        }

        [Test]
        public void FusionWizard_HasMissingAddonPrefabRecovery()
        {
            string wizardSource = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Editor/Transport/Fusion/" +
                "FusionSceneSetupWizard.cs");
            StringAssert.Contains(
                "removed the orphaned wizard-owned Fusion KCC Motor after addon removal",
                wizardSource);
            StringAssert.Contains(
                "re-enabled the CharacterController after KCC addon removal",
                wizardSource);
            StringAssert.Contains("MotorObjectCreatedByWizard", wizardSource);
            StringAssert.Contains("custom/adopted Fusion KCC Motor", wizardSource);
            StringAssert.Contains("Reinstall KCC to convert it", wizardSource);
        }

        [Test]
        public void FusionWizard_MissingAddonRecoveryUsesMarkerWithoutRootBackend()
        {
            Type wizardType = Type.GetType(
                "Arawn.GameCreator2.Networking.Transport.Fusion.Editor." +
                "FusionSceneSetupWizard, " +
                "Arawn.GameCreator2.Networking.Transport.Fusion.Editor");
            Assert.NotNull(wizardType);
            MethodInfo remove = wizardType.GetMethod(
                "RemoveOptionalKccSetup",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(remove);

            ScriptableObject wizard = ScriptableObject.CreateInstance(wizardType);
            var wizardOwnedRoot = new GameObject("WizardOwnedOrphanRoot");
            var adoptedRoot = new GameObject("AdoptedOrphanRoot");
            try
            {
                CharacterController controller =
                    wizardOwnedRoot.AddComponent<CharacterController>();
                controller.enabled = false;
                var wizardMotor = new GameObject("Renamed Wizard Motor");
                wizardMotor.transform.SetParent(wizardOwnedRoot.transform, false);
                FusionKccSetupMarker wizardMarker =
                    wizardMotor.AddComponent<FusionKccSetupMarker>();
                SetPrivateField(wizardMarker, "m_MotorObjectCreatedByWizard", true);
                SetPrivateField(wizardMarker, "m_HasRootControllerSnapshot", true);
                SetPrivateField(wizardMarker, "m_HadRootCharacterController", true);
                SetPrivateField(
                    wizardMarker,
                    "m_OriginalRootCharacterControllerEnabled",
                    true);

                var changes = new System.Collections.Generic.List<string>();
                remove.Invoke(wizard, new object[] { wizardOwnedRoot, null, changes });
                Assert.AreEqual(0, wizardOwnedRoot.transform.childCount);
                Assert.IsTrue(controller.enabled);
                Assert.IsTrue(changes.Any(change =>
                    change.Contains("orphaned wizard-owned Fusion KCC Motor")));

                var adoptedMotor = new GameObject("Renamed Customer Motor");
                adoptedMotor.transform.SetParent(adoptedRoot.transform, false);
                FusionKccSetupMarker adoptedMarker =
                    adoptedMotor.AddComponent<FusionKccSetupMarker>();
                SetPrivateField(adoptedMarker, "m_MotorObjectCreatedByWizard", false);
                SetPrivateField(adoptedMarker, "m_HasCustomerSnapshot", true);

                TargetInvocationException exception = Assert.Throws<TargetInvocationException>(
                    () => remove.Invoke(
                        wizard,
                        new object[]
                        {
                            adoptedRoot,
                            null,
                            new System.Collections.Generic.List<string>()
                        }));
                Assert.IsInstanceOf<InvalidOperationException>(exception.InnerException);
                StringAssert.Contains("custom/adopted", exception.InnerException.Message);
                Assert.AreEqual(adoptedMotor.transform, adoptedRoot.transform.GetChild(0));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(wizardOwnedRoot);
                UnityEngine.Object.DestroyImmediate(adoptedRoot);
                UnityEngine.Object.DestroyImmediate(wizard);
            }
        }

        [Test]
        public void FusionWizard_AdoptedKccSetupHasReversibleParkedConversion()
        {
            string marker = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Runtime/Transport/Fusion/" +
                "FusionKccSetupMarker.cs");
            StringAssert.Contains("m_HasCustomerSnapshot", marker);
            StringAssert.Contains("m_AdoptedSetupParked", marker);
            StringAssert.Contains("m_HasRootControllerSnapshot", marker);
            StringAssert.DoesNotContain("Fusion.Addons.KCC", marker);

            string extension = ReadFusionAdvancedKccSource(
                "Editor/FusionKccEditorSetupExtension.cs");
            StringAssert.Contains("adopted the existing customer KCC motor", extension);
            StringAssert.Contains("CaptureSetupMarker(", extension);
            StringAssert.Contains("RestoreAdoptedMotor(", extension);
            StringAssert.Contains(
                "settings.Processors = ReadObjectArray(",
                extension);
            StringAssert.Contains("kcc.enabled = false;", extension);
            StringAssert.Contains("SetMarkerParked(marker, true);", extension);
        }

        [Test]
        public void FusionAdvancedKccDemoPackage_IsInstallableAndRemainsOpaqueWithoutAddon()
        {
            const string packageAssetRoot =
                "Assets/Arawn/NetworkingLayerForGC2/Demo/Fusion/Packages/AdvancedKCC";
            string installerAssetPath = packageAssetRoot + "/" +
                                        AdvancedKccDemoPackageId + ".asset";
            string payloadAssetPath = packageAssetRoot + "/Package.unitypackage";
            string installerPath = Path.Combine(
                Directory.GetParent(Application.dataPath)?.FullName ?? string.Empty,
                installerAssetPath);
            string payloadPath = Path.Combine(
                Directory.GetParent(Application.dataPath)?.FullName ?? string.Empty,
                payloadAssetPath);

            Assert.IsTrue(
                File.Exists(installerPath),
                "The Advanced KCC Game Creator install descriptor is missing.");
            Assert.IsTrue(
                File.Exists(payloadPath),
                "The Advanced KCC demo unitypackage payload is missing.");
            Assert.Greater(
                new FileInfo(payloadPath).Length,
                0,
                "The Advanced KCC demo unitypackage is empty.");
            Assert.NotNull(
                AssetDatabase.LoadMainAssetAtPath(installerAssetPath),
                "The install descriptor must import without requiring the optional KCC addon.");

            string installer = File.ReadAllText(installerPath);
            StringAssert.Contains("m_Name: Advanced KCC Examples", installer);
            StringAssert.Contains(
                "m_Module: GC2 Networking Layer Fusion Transport",
                installer);
            StringAssert.Contains("m_Complexity: 3", installer);
            Assert.AreEqual(4, CountOccurrences(installer, "- m_ID: "));
            AssertInstallerDependency(
                installer,
                "GC2NetworkingLayerFusionTransport.Assets",
                1,
                0,
                0);
            AssertInstallerDependency(
                installer,
                "GameCreator.Characters",
                1,
                3,
                15);
            AssertInstallerDependency(
                installer,
                "GameCreator.Blockout",
                1,
                4,
                10);
            AssertInstallerDependency(
                installer,
                "GameCreator.Examples",
                1,
                9,
                26);
            StringAssert.Contains("Photon Fusion Advanced KCC", installer);
            StringAssert.Contains("2.1.0", installer);

            string packageDirectory = Path.GetDirectoryName(installerPath);
            Assert.NotNull(packageDirectory);
            string[] looseOptionalAssets = Directory
                .EnumerateFiles(packageDirectory, "*", SearchOption.AllDirectories)
                .Where(path =>
                {
                    string extension = Path.GetExtension(path);
                    return string.Equals(extension, ".cs", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(extension, ".asmdef", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(extension, ".dll", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(extension, ".prefab", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(extension, ".unity", StringComparison.OrdinalIgnoreCase);
                })
                .ToArray();
            Assert.IsEmpty(
                looseOptionalAssets,
                "Typed KCC demo content must stay inside the opt-in unitypackage so importing " +
                "the Networking Layer without KCC remains safe.");

            string[] payloadPaths = ReadUnityPackagePathnames(payloadPath);
            Assert.IsNotEmpty(payloadPaths);
            CollectionAssert.AreEquivalent(
                new[]
                {
                    AdvancedKccDemoInstallRoot,
                    AdvancedKccDemoInstallRoot + "/" + AdvancedKccDemoPrefabName,
                    AdvancedKccDemoInstallRoot + "/" + AdvancedKccDemoSceneName,
                    AdvancedKccDemoInstallRoot +
                    "/README - Fusion Advanced KCC Demo.txt",
                    AdvancedKccDemoInstallRoot + "/Materials",
                    AdvancedKccDemoInstallRoot +
                    "/Materials/KCC Course Blue.mat",
                    AdvancedKccDemoInstallRoot +
                    "/Materials/KCC Course Green.mat",
                    AdvancedKccDemoInstallRoot +
                    "/Materials/KCC Course Orange.mat"
                },
                payloadPaths,
                "The optional package should contain only its install root, prefab, scene, " +
                "README, and self-contained course materials.");
            Assert.IsTrue(
                payloadPaths.All(path => path.StartsWith(
                    AdvancedKccDemoInstallRoot,
                    StringComparison.Ordinal)),
                "The optional payload must install only into its versioned Game Creator " +
                "install folder.");
            Assert.IsFalse(
                payloadPaths.Any(path =>
                    path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
                    path.EndsWith(".asmdef", StringComparison.OrdinalIgnoreCase) ||
                    path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)),
                "The demo payload must not redistribute Photon KCC or add compiled code.");
        }

        [Test]
        public void FusionAdvancedKccDemo_InstalledAssetsMatchOptionalBackendContract()
        {
#if ARAWN_GC2_FUSION_KCC
            string prefabPath = AdvancedKccDemoInstallRoot + "/" +
                                AdvancedKccDemoPrefabName;
            string scenePath = AdvancedKccDemoInstallRoot + "/" +
                               AdvancedKccDemoSceneName;
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            Assert.NotNull(prefab, $"Advanced KCC demo prefab is missing: {prefabPath}");
            Assert.NotNull(
                AssetDatabase.LoadAssetAtPath<SceneAsset>(scenePath),
                $"Advanced KCC demo scene is missing: {scenePath}");

            string projectRoot =
                Directory.GetParent(Application.dataPath)?.FullName ?? string.Empty;
            string sceneSource = File.ReadAllText(Path.Combine(projectRoot, scenePath));
            StringAssert.Contains("m_Name: Advanced KCC Movement Course", sceneSource);
            StringAssert.Contains("m_Name: Advanced KCC Information", sceneSource);
            StringAssert.Contains("m_Name: Slope - 18 Degrees", sceneSource);
            StringAssert.Contains("m_Name: Jump Obstacle", sceneSource);
            Assert.AreEqual(
                5,
                CountOccurrences(sceneSource, "m_Name: Step "),
                "The movement course must retain all five incremental steps.");
            Assert.AreEqual(
                5,
                CountOccurrences(sceneSource, "m_Name: Collision Pillar "),
                "The movement course must retain its five collision-slalom pillars.");
            StringAssert.Contains("WASD: Move", sceneSource);
            StringAssert.Contains("Owner Movement Authority", sceneSource);
            StringAssert.Contains("m_DefaultSessionName: GC2-Fusion-Advanced-KCC", sceneSource);
            string prefabGuid = AssetDatabase.AssetPathToGUID(prefabPath);
            Assert.IsNotEmpty(prefabGuid);
            StringAssert.Contains(
                "m_PlayerPrefab: {fileID: ",
                sceneSource);
            StringAssert.Contains("guid: " + prefabGuid, sceneSource);

            string readmePath = AdvancedKccDemoInstallRoot +
                                "/README - Fusion Advanced KCC Demo.txt";
            string readme = File.ReadAllText(Path.Combine(projectRoot, readmePath));
            StringAssert.Contains("Photon Advanced KCC 2.1.0", readme);
            StringAssert.Contains("Owner Movement Authority", readme);
            StringAssert.Contains("slope, incremental steps, a jump obstacle", readme);
            StringAssert.Contains("Do not redistribute the Photon Advanced KCC addon", readme);
            foreach (string materialName in new[]
                     {
                         "KCC Course Blue.mat",
                         "KCC Course Green.mat",
                         "KCC Course Orange.mat"
                     })
            {
                Assert.NotNull(
                    AssetDatabase.LoadAssetAtPath<Material>(
                        AdvancedKccDemoInstallRoot + "/Materials/" + materialName),
                    $"Advanced KCC course material is missing: {materialName}");
            }

            Character character = prefab.GetComponent<Character>();
            NetworkCharacter networkCharacter = prefab.GetComponent<NetworkCharacter>();
            FusionKccCharacterBackend backend =
                prefab.GetComponent<FusionKccCharacterBackend>();
            Assert.NotNull(character);
            Assert.NotNull(networkCharacter);
            Assert.NotNull(backend);
            Assert.IsTrue(backend.enabled);
            Assert.AreEqual(
                NetworkPredictionBackend.FusionKCC,
                networkCharacter.PredictionBackend);
            CharacterController controller = prefab.GetComponent<CharacterController>();
            Assert.IsTrue(controller == null || !controller.enabled);
            FusionNativeNetworkCharacterMotor nativeMotor =
                prefab.GetComponent<FusionNativeNetworkCharacterMotor>();
            Assert.IsTrue(nativeMotor == null || !nativeMotor.enabled);

            Transform motor = prefab.transform.Find("Fusion KCC Motor");
            Assert.NotNull(motor);
            Assert.AreEqual(motor, backend.RuntimeAdapterComponent?.transform);
            Assert.NotNull(motor.GetComponent<NetworkObject>());
            Rigidbody rigidbody = motor.GetComponent<Rigidbody>();
            Assert.NotNull(rigidbody);
            Assert.IsTrue(rigidbody.isKinematic);
            Assert.IsFalse(rigidbody.useGravity);

            Type kccType = Type.GetType("Fusion.Addons.KCC.KCC, Assembly-CSharp");
            Type motorBodyType = Type.GetType(
                "Arawn.GameCreator2.Networking.Transport.Fusion.KCC." +
                "FusionKccMotorBody, Assembly-CSharp");
            Type processorType = Type.GetType(
                "Fusion.Addons.KCC.KCCProcessor, Assembly-CSharp");
            Assert.NotNull(kccType);
            Assert.NotNull(motorBodyType);
            Assert.NotNull(processorType);
            Assert.NotNull(motor.GetComponent(kccType));
            Assert.NotNull(motor.GetComponent(motorBodyType));

            FusionKccSetupMarker marker =
                motor.GetComponent<FusionKccSetupMarker>();
            Assert.NotNull(marker);
            Assert.IsTrue(marker.MotorObjectCreatedByWizard);
            GameObject[] processorObjects = marker.WizardOwnedProcessorObjects
                .Where(processorObject => processorObject != null)
                .ToArray();
            Assert.AreEqual(4, processorObjects.Length);
            CollectionAssert.AllItemsAreUnique(processorObjects);
            foreach (GameObject processorObject in processorObjects)
            {
                Assert.AreEqual(motor, processorObject.transform.parent);
                Assert.IsTrue(processorObject.name.StartsWith(
                    "Fusion KCC Processor - ",
                    StringComparison.Ordinal));
                Assert.AreEqual(1, processorObject.GetComponents(processorType).Length);
                NetworkObject processorNetworkObject =
                    processorObject.GetComponent<NetworkObject>();
                Assert.NotNull(processorNetworkObject);
                Assert.AreEqual(
                    NetworkObjectFlags.MasterClientObject,
                    processorNetworkObject.Flags &
                    NetworkObjectFlags.MasterClientObject);
            }
#else
            Assert.Pass(
                "The install descriptor and opaque payload are covered without KCC; typed " +
                "demo prefab validation runs only when ARAWN_GC2_FUSION_KCC is available.");
#endif
        }

        [Test]
        public void FusionKccDocumentation_IsPublishedAndLinkedFromPublicApi()
        {
            string guidePath = AssetPath("docs/FUSION-ADVANCED-KCC.md");
            string publicApiPath = AssetPath("docs/FUSION-PUBLIC-API.md");

            Assert.IsTrue(File.Exists(guidePath));
            Assert.IsTrue(File.Exists(publicApiPath));

            string guide = File.ReadAllText(guidePath);
            string publicApi = File.ReadAllText(publicApiPath);
            StringAssert.Contains("# Fusion Advanced KCC", guide);
            StringAssert.Contains("OwnerMovementAuthority", guide);
            StringAssert.Contains("SharedMasterMovementAuthority", guide);
            StringAssert.Contains(
                "https://doc.photonengine.com/fusion/current/addons/advanced-kcc/execution",
                guide);
            StringAssert.Contains(
                "https://doc.photonengine.com/fusion/current/addons/advanced-kcc/render-behavior",
                guide);
            StringAssert.Contains(
                "https://doc.photonengine.com/fusion/current/manual/network-topologies",
                guide);
            StringAssert.Contains("Assemblies To Weave", guide);
            StringAssert.Contains("Existing Fusion demo prefabs remain configured", guide);
            StringAssert.Contains("FUSION-ADVANCED-KCC.md", publicApi);
            StringAssert.Contains("FusionKccCharacterBackend", publicApi);
            StringAssert.Contains("IFusionKccRuntimeAdapter", publicApi);
            StringAssert.Contains("QueueAuthoritativeTeleport", publicApi);
            StringAssert.Contains("FusionKccSetupMarker", publicApi);
            StringAssert.Contains("IFusionKccEditorSetupExtension", publicApi);
            StringAssert.Contains("Adopting an existing KCC motor", guide);
            StringAssert.Contains("Adopted customer motor", guide);
        }

        private static string[] ReadUnityPackagePathnames(string packagePath)
        {
            var pathnames = new List<string>();
            using FileStream file = File.OpenRead(packagePath);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            var header = new byte[512];
            while (true)
            {
                int headerBytes = ReadFully(gzip, header, 0, header.Length);
                if (headerBytes == 0) break;
                if (headerBytes != header.Length)
                {
                    throw new InvalidDataException(
                        "The Unity package ended inside a TAR header.");
                }
                if (header.All(value => value == 0)) break;

                string entryName = Encoding.UTF8
                    .GetString(header, 0, 100)
                    .TrimEnd('\0');
                string sizeText = Encoding.ASCII
                    .GetString(header, 124, 12)
                    .Trim('\0', ' ');
                long size = string.IsNullOrEmpty(sizeText)
                    ? 0L
                    : Convert.ToInt64(sizeText, 8);

                if (entryName.EndsWith("/pathname", StringComparison.Ordinal))
                {
                    if (size > int.MaxValue)
                    {
                        throw new InvalidDataException(
                            "A Unity package pathname entry is unexpectedly large.");
                    }
                    var content = new byte[(int)size];
                    if (ReadFully(gzip, content, 0, content.Length) != content.Length)
                    {
                        throw new EndOfStreamException(
                            "The Unity package ended inside a pathname entry.");
                    }
                    pathnames.Add(Encoding.UTF8
                        .GetString(content)
                        .TrimEnd('\0', '\r', '\n'));
                }
                else
                {
                    SkipStreamBytes(gzip, size);
                }

                long padding = (512L - size % 512L) % 512L;
                SkipStreamBytes(gzip, padding);
            }

            return pathnames
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }

        private static int ReadFully(
            Stream stream,
            byte[] buffer,
            int offset,
            int count)
        {
            int total = 0;
            while (total < count)
            {
                int read = stream.Read(buffer, offset + total, count - total);
                if (read <= 0) break;
                total += read;
            }
            return total;
        }

        private static void SkipStreamBytes(Stream stream, long count)
        {
            if (count <= 0) return;
            var buffer = new byte[8192];
            long remaining = count;
            while (remaining > 0)
            {
                int requested = (int)Math.Min(buffer.Length, remaining);
                int read = stream.Read(buffer, 0, requested);
                if (read <= 0)
                {
                    throw new EndOfStreamException(
                        "The Unity package ended inside a TAR entry.");
                }
                remaining -= read;
            }
        }

        private static GameObject CreateReflectedProcessorObject(
            Transform parent,
            string name,
            Type processorType,
            out Component processor)
        {
            var processorObject = new GameObject(name);
            processorObject.transform.SetParent(parent, false);
            processor = processorObject.AddComponent(processorType);
            Assert.NotNull(processor);
            Assert.NotNull(
                processorObject.GetComponent<NetworkObject>(),
                "KCCProcessor.RequireComponent must add a NetworkObject to each processor " +
                "support GameObject.");
            return processorObject;
        }

        private static UnityEngine.Object[] GetKccProcessorReferences(MonoBehaviour kcc)
        {
            object settings = GetKccSettings(kcc);
            FieldInfo processors = settings.GetType().GetField(
                "Processors",
                BindingFlags.Instance | BindingFlags.Public);
            Assert.NotNull(processors);
            return processors.GetValue(settings) as UnityEngine.Object[] ??
                   Array.Empty<UnityEngine.Object>();
        }

        private static void SetKccProcessorReferences(
            MonoBehaviour kcc,
            UnityEngine.Object[] processorReferences)
        {
            object settings = GetKccSettings(kcc);
            FieldInfo processors = settings.GetType().GetField(
                "Processors",
                BindingFlags.Instance | BindingFlags.Public);
            Assert.NotNull(processors);
            processors.SetValue(settings, processorReferences);
        }

        private static object GetKccSettings(MonoBehaviour kcc)
        {
            Assert.NotNull(kcc);
            PropertyInfo settingsProperty = kcc.GetType().GetProperty(
                "Settings",
                BindingFlags.Instance | BindingFlags.Public);
            Assert.NotNull(settingsProperty);
            object settings = settingsProperty.GetValue(kcc);
            Assert.NotNull(settings);
            return settings;
        }

        private static string ReadAssetSource(string relativePath)
        {
            return File.ReadAllText(AssetPath(relativePath));
        }

        private static string ReadFusionAdvancedKccSource(string relativePath)
        {
            return ReadAssetSource(
                Path.Combine(FusionAdvancedKccAssetRoot, relativePath));
        }

        private static string AssetPath(string relativePath)
        {
            return Path.Combine(Application.dataPath, relativePath);
        }

        private static void SetPrivateField(
            object target,
            string fieldName,
            object value)
        {
            FieldInfo field = target.GetType().GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field, $"Private field was not found: {fieldName}");
            field.SetValue(target, value);
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

        private static void AssertInstallerDependency(
            string installer,
            string id,
            int major,
            int minor,
            int patch)
        {
            string token = "- m_ID: " + id;
            int start = installer.IndexOf(token, StringComparison.Ordinal);
            Assert.GreaterOrEqual(start, 0, $"Installer dependency is missing: {id}");
            int next = installer.IndexOf(
                "\n    - m_ID: ",
                start + token.Length,
                StringComparison.Ordinal);
            string block = next >= 0
                ? installer.Substring(start, next - start)
                : installer.Substring(start);
            StringAssert.Contains($"major: {major}", block);
            StringAssert.Contains($"minor: {minor}", block);
            StringAssert.Contains($"patch: {patch}", block);
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
