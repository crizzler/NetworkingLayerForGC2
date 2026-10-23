using System;
using System.IO;
using System.Linq;
using Arawn.GameCreator2.Networking.Editor;
using GameCreator.Runtime.Characters;
using NUnit.Framework;
using UnityEngine;
using Assert = NUnit.Framework.Assert;

namespace Arawn.GameCreator2.Networking.Tests
{
    [Category("GC2Networking.Malbers")]
    public sealed class NetworkMalbersIntegrationTests
    {
        private const string MainPackageRoot = "Arawn/NetworkingLayerForGC2";
        private const string MalbersCompanionRoot = "Arawn/NetworkingLayerForGC2.Malbers";

        private sealed class UnrelatedDriverType
        {
        }

        private sealed class DerivedNetworkDriver : UnitDriverNetworkClient
        {
        }

        [Test]
        public void MotionModeValues_AreAppendOnly()
        {
            Assert.AreEqual(0, (int)NetworkMalbersMotionMode.Inactive);
            Assert.AreEqual(1, (int)NetworkMalbersMotionMode.NativeAuthority);
            Assert.AreEqual(2, (int)NetworkMalbersMotionMode.NetworkPresentation);
        }

        [Test]
        public void Policy_ResolvesBuiltInNetworkDriversAsPresentation()
        {
            Assert.AreEqual(
                NetworkMalbersMotionMode.NetworkPresentation,
                NetworkMalbersIntegrationPolicy.ResolveMode(
                    true,
                    NetworkCharacter.NetworkRole.Server,
                    typeof(UnitDriverNetworkServer)));

            Assert.AreEqual(
                NetworkMalbersMotionMode.NetworkPresentation,
                NetworkMalbersIntegrationPolicy.ResolveMode(
                    true,
                    NetworkCharacter.NetworkRole.LocalClient,
                    typeof(UnitDriverNetworkClient)));

            Assert.AreEqual(
                NetworkMalbersMotionMode.NetworkPresentation,
                NetworkMalbersIntegrationPolicy.ResolveMode(
                    true,
                    NetworkCharacter.NetworkRole.RemoteClient,
                    typeof(UnitDriverNetworkRemote)));

            Assert.AreEqual(
                NetworkMalbersMotionMode.NetworkPresentation,
                NetworkMalbersIntegrationPolicy.ResolveMode(
                    true,
                    NetworkCharacter.NetworkRole.Server,
                    typeof(UnitDriverNavmeshNetworkServer)));

            Assert.AreEqual(
                NetworkMalbersMotionMode.NetworkPresentation,
                NetworkMalbersIntegrationPolicy.ResolveMode(
                    true,
                    NetworkCharacter.NetworkRole.LocalClient,
                    typeof(DerivedNetworkDriver)));
        }

        [Test]
        public void Policy_ReturnsInactiveBeforeRoleResolutionOrForUnknownDrivers()
        {
            Assert.AreEqual(
                NetworkMalbersMotionMode.Inactive,
                NetworkMalbersIntegrationPolicy.ResolveMode(
                    false,
                    NetworkCharacter.NetworkRole.Server,
                    typeof(UnitDriverNetworkServer)));

            Assert.AreEqual(
                NetworkMalbersMotionMode.Inactive,
                NetworkMalbersIntegrationPolicy.ResolveMode(
                    true,
                    NetworkCharacter.NetworkRole.None,
                    typeof(UnitDriverNetworkServer)));

            Assert.AreEqual(
                NetworkMalbersMotionMode.Inactive,
                NetworkMalbersIntegrationPolicy.ResolveMode(
                    true,
                    NetworkCharacter.NetworkRole.Server,
                    driverType: null));

            Assert.AreEqual(
                NetworkMalbersMotionMode.Inactive,
                NetworkMalbersIntegrationPolicy.ResolveMode(
                    true,
                    NetworkCharacter.NetworkRole.Server,
                    typeof(UnrelatedDriverType)));
        }

        [Test]
        public void Policy_RecognizesMalbersNativeDriverNameAndHierarchy()
        {
            Assert.IsTrue(NetworkMalbersIntegrationPolicy.IsNativeDriverTypeName(
                NetworkMalbersIntegrationPolicy.MalbersNativeDriverTypeName));
            Assert.IsFalse(NetworkMalbersIntegrationPolicy.IsNativeDriverTypeName(
                typeof(UnitDriverNetworkClient).FullName));
            Assert.IsFalse(NetworkMalbersIntegrationPolicy.IsNativeDriverHierarchy(
                typeof(UnrelatedDriverType)));

            Type nativeDriverType = NetworkMalbersIntegrationPolicy.FindLoadedType(
                NetworkMalbersIntegrationPolicy.MalbersNativeDriverTypeName);
            if (nativeDriverType == null)
            {
                Assert.Ignore("Malbers Animal Controller GC2 integration is not installed.");
            }

            Assert.IsTrue(NetworkMalbersIntegrationPolicy.IsNativeDriverHierarchy(nativeDriverType));
            Assert.AreEqual(
                NetworkMalbersMotionMode.NativeAuthority,
                NetworkMalbersIntegrationPolicy.ResolveMode(
                    true,
                    NetworkCharacter.NetworkRole.Server,
                    nativeDriverType));
        }

        [Test]
        public void Policy_OnlyTreatsNetworkingAssembliesAsPresentationDrivers()
        {
            Assert.IsTrue(NetworkMalbersIntegrationPolicy.IsNetworkLayerDriverType(
                typeof(UnitDriverNetworkClient)));
            Assert.IsTrue(NetworkMalbersIntegrationPolicy.IsNetworkLayerDriverType(
                typeof(UnitDriverNetworkServer)));
            Assert.IsTrue(NetworkMalbersIntegrationPolicy.IsNetworkLayerDriverType(
                typeof(UnitDriverNetworkRemote)));
            Assert.IsFalse(NetworkMalbersIntegrationPolicy.IsNetworkLayerDriverType(
                typeof(UnrelatedDriverType)));
            Assert.IsTrue(NetworkMalbersIntegrationPolicy.IsNetworkLayerDriverHierarchy(
                typeof(DerivedNetworkDriver)));
            Assert.IsFalse(NetworkMalbersIntegrationPolicy.IsNetworkLayerDriverHierarchy(
                typeof(UnrelatedDriverType)));
        }

        [Test]
        public void HasMalbersAnimal_IsFalseForAPlainGameObject()
        {
            GameObject host = new GameObject("MalbersPlainHost");
            try
            {
                Assert.IsFalse(NetworkMalbersIntegrationPolicy.HasMalbersAnimal(host));
                Assert.IsFalse(NetworkMalbersIntegrationPolicy.HasMalbersAnimal(null));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void OptionalBridge_LoadsAndStaysInactiveWithoutAMalbersAnimal()
        {
            Type bridgeType = NetworkMalbersIntegrationPolicy.FindMotorBridgeType();
            if (bridgeType == null)
            {
                Assert.Ignore("Optional Malbers bridge is not compiled in this project.");
            }

            GameObject host = new GameObject("MalbersBridgeProbe");
            try
            {
                Component bridge = host.AddComponent(bridgeType);
                Assert.IsNotNull(bridge);

                var modeProperty = bridgeType.GetProperty(
                    "Mode",
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.Public);
                Assert.IsNotNull(modeProperty);
                Assert.AreEqual(
                    NetworkMalbersMotionMode.Inactive,
                    modeProperty.GetValue(bridge));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void PresentationAxis_NormalizesAndClampsVelocity()
        {
            GameObject host = new GameObject("MalbersAxisHost");
            try
            {
                Vector3 fullForward = NetworkMalbersIntegrationPolicy.ComputePresentationAxis(
                    Vector3.forward * 5f,
                    host.transform,
                    5f,
                    1f,
                    false);
                Assert.That(fullForward.z, Is.EqualTo(1f).Within(0.001f));
                Assert.That(fullForward.x, Is.EqualTo(0f).Within(0.001f));

                Vector3 halfSpeed = NetworkMalbersIntegrationPolicy.ComputePresentationAxis(
                    Vector3.forward * 2.5f,
                    host.transform,
                    5f,
                    1f,
                    false);
                Assert.That(halfSpeed.z, Is.EqualTo(0.5f).Within(0.001f));

                Vector3 clamped = NetworkMalbersIntegrationPolicy.ComputePresentationAxis(
                    Vector3.forward * 50f,
                    host.transform,
                    5f,
                    1f,
                    false);
                Assert.That(clamped.magnitude, Is.EqualTo(1f).Within(0.001f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void PresentationAxis_IsExpressedInCharacterLocalSpace()
        {
            GameObject host = new GameObject("MalbersAxisRotatedHost");
            try
            {
                host.transform.rotation = Quaternion.Euler(0f, 90f, 0f);
                Vector3 local = NetworkMalbersIntegrationPolicy.ComputePresentationAxis(
                    Vector3.forward * 2f,
                    host.transform,
                    2f,
                    1f,
                    false);

                Assert.That(local.x, Is.EqualTo(-1f).Within(0.001f));
                Assert.That(local.z, Is.EqualTo(0f).Within(0.001f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void PresentationAxis_RespectsSuspensionAndZeroVelocity()
        {
            GameObject host = new GameObject("MalbersAxisSuspendedHost");
            try
            {
                Assert.AreEqual(
                    Vector3.zero,
                    NetworkMalbersIntegrationPolicy.ComputePresentationAxis(
                        Vector3.forward * 5f,
                        host.transform,
                        5f,
                        1f,
                        true));

                Assert.AreEqual(
                    Vector3.zero,
                    NetworkMalbersIntegrationPolicy.ComputePresentationAxis(
                        Vector3.zero,
                        host.transform,
                        5f,
                        1f,
                        false));

                Assert.AreEqual(
                    Vector3.zero,
                    NetworkMalbersIntegrationPolicy.ComputePresentationAxis(
                        Vector3.forward * 5f,
                        null,
                        5f,
                        1f,
                        false));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void MalbersCompanion_IsAsmdefFreeAndFullyDefineGuarded()
        {
            string optionalRoot = AssetPath(MalbersCompanionRoot);
            Assert.IsTrue(
                Directory.Exists(optionalRoot),
                "The optional Malbers companion source root is missing.");

            string[] assemblyDefinitions = Directory.GetFiles(
                optionalRoot,
                "*.asmdef",
                SearchOption.AllDirectories);
            Assert.IsEmpty(
                assemblyDefinitions,
                "The Malbers companion must compile into Assembly-CSharp so the main " +
                "Networking Layer assembly keeps no compile-time Malbers dependency.");

            string[] sources = Directory.GetFiles(
                optionalRoot,
                "*.cs",
                SearchOption.AllDirectories);
            Assert.IsNotEmpty(sources);

            foreach (string path in sources)
            {
                string source = File.ReadAllText(path);
                StringAssert.StartsWith(
                    "#if ARAWN_GC2_MALBERS",
                    source.TrimStart(),
                    $"Optional Malbers source is not guarded: {path}");
                StringAssert.EndsWith(
                    "#endif",
                    source.TrimEnd(),
                    $"Optional Malbers source does not close its feature guard: {path}");
            }

            string combined = string.Join("\n", sources.Select(File.ReadAllText));
            StringAssert.Contains("class NetworkMalbersMotorBridge : MonoBehaviour", combined);
            StringAssert.Contains("DisablePosition", combined);
            StringAssert.Contains("DisableRotation", combined);
            StringAssert.Contains("SetInputAxis(axis)", combined);
            StringAssert.Contains("Rotate_at_Direction", combined);
            StringAssert.Contains("IInputSource", combined);
            StringAssert.Contains("MInput", combined);
            StringAssert.Contains("MAnimalAIControl", combined);
            StringAssert.Contains("MAnimalBrain", combined);
            StringAssert.Contains("UnitPlayerTankNetwork", combined);
            StringAssert.Contains("RestoreNetworkCompatiblePlayerUnit", combined);
            StringAssert.Contains("m_InstalledPlayerUnit", combined);
            StringAssert.Contains("ReferenceEquals(m_Character.Player, installedPlayerUnit)", combined);
            StringAssert.Contains("EventChangeDriver", combined);
            StringAssert.Contains("EventChangePlayer", combined);
            StringAssert.Contains("EventChangeFacing", combined);
            StringAssert.Contains("ReassertPresentationPreInputPriority", combined);
            StringAssert.Contains("private void LateUpdate()", combined);
            StringAssert.Contains("m_ReassertPreInputAtEndOfFrame", combined);
            StringAssert.Contains("IsNetworkLayerDriverHierarchy(driver.GetType())", combined);
            StringAssert.Contains("m_LastDriver", combined);
            StringAssert.Contains("NetworkRagdollPhysicsGuard.Register(m_Character, m_Rigidbody)", combined);
            StringAssert.Contains("NetworkRagdollPhysicsGuard.Adopt(", combined);
            StringAssert.Contains("replaceActiveBaseline: true", combined);
            StringAssert.Contains("TryGetRigidbodyBaseline", combined);
            StringAssert.Contains("ApplyPendingRigidbodyBaseline", combined);
            StringAssert.Contains("SetIntendedRigidbodyState", combined);
            StringAssert.Contains("m_CachedRigidbodyDetectCollisions", combined);
            StringAssert.Contains("CompletePendingRigidbodyRestore", combined);
            StringAssert.Contains("SubscribeRagdollEvents", combined);
            StringAssert.Contains("UnsubscribeRagdollEvents", combined);
            StringAssert.Contains("preserveCachedRigidbodyState", combined);
            StringAssert.Contains("private void OnDestroy()", combined);
            StringAssert.Contains("EventAfterStartRecover", combined);
            StringAssert.Contains("m_SuppressMalbersAi", combined);
            StringAssert.Contains("m_SuppressInputSourcesOnParents", combined);
            StringAssert.Contains("RegisterAdditionalInputSource", combined);
            StringAssert.Contains("WarnIfMalbersPlatformMovementIsActive", combined);
            StringAssert.Contains("DefaultExecutionOrder(10000)", combined);
            StringAssert.DoesNotContain(": TUnitDriver", combined);

            string linker = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2.Malbers/Runtime/link.xml");
            StringAssert.Contains(
                "Arawn.GameCreator2.Networking.Malbers.NetworkMalbersMotorBridge",
                linker);
            StringAssert.Contains("preserve=\"all\"", linker);
        }

        [Test]
        public void MainAssembly_HasNoCompileTimeMalbersReference()
        {
            string packageRoot = AssetPath(MainPackageRoot);
            string[] sources = Directory.GetFiles(
                    packageRoot,
                    "*.cs",
                    SearchOption.AllDirectories)
                .Where(path => !path.Contains("/Tests/", StringComparison.Ordinal))
                .ToArray();

            Assert.IsNotEmpty(sources);
            foreach (string path in sources)
            {
                string source = File.ReadAllText(path);
                StringAssert.DoesNotContain(
                    "using MalbersAnimations",
                    source,
                    $"Main package source acquires a compile-time Malbers reference: {path}");
                StringAssert.DoesNotContain(
                    "global::MalbersAnimations",
                    source,
                    $"Main package source acquires a compile-time Malbers reference: {path}");
            }
        }

        [Test]
        public void NetworkCharacter_AttachesTheOptionalBridgeByTypeName()
        {
            string initialization = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Runtime/Character/NetworkCharacter.Initialization.cs");
            StringAssert.Contains("TryAttachMalbersMotorBridge();", initialization);

            string partial = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Runtime/Character/NetworkCharacter.Malbers.cs");
            StringAssert.Contains(
                "NetworkMalbersIntegrationPolicy.HasMalbersAnimal(gameObject)",
                partial);
            StringAssert.Contains(
                "NetworkMalbersIntegrationPolicy.EnsureMotorBridge(gameObject)",
                partial);
            StringAssert.Contains(
                "NetworkMalbersIntegrationPolicy.EnsureStateBridge(gameObject)",
                partial);
            StringAssert.Contains("could not be attached", partial);
            StringAssert.Contains("ARAWN_GC2_MALBERS", partial);
        }

        [Test]
        public void DefineSymbols_ManageTheMalbersCompanionSymbol()
        {
            Assert.AreEqual("ARAWN_GC2_MALBERS", GC2NetworkingDefineSymbols.SYMBOL_MALBERS);

            string source = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Editor/DefineSymbols/GC2NetworkingDefineSymbols.cs");
            StringAssert.Contains(
                "ManageSymbol(symbolList, SYMBOL_MALBERS, IsMalbersIntegrationInstalled());",
                source);
            StringAssert.Contains("public static bool IsMalbersIntegrationInstalled()", source);
            StringAssert.Contains(
                "NetworkMalbersIntegrationPolicy",
                source);
            StringAssert.Contains(
                ".MalbersNativeDriverTypeName",
                source);
            StringAssert.Contains("NotifyMalbersAssetsDeleted", source);
            StringAssert.Contains("class GC2NetworkingMalbersAssetPostprocessor", source);
            StringAssert.Contains("class GC2NetworkingMalbersDeletionProcessor", source);
        }

        [Test]
        public void RagdollGuard_RegistersRootRigidbody()
        {
            string source = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Runtime/Motion/NetworkRagdollPhysicsGuard.cs");
            StringAssert.Contains("public static void Register(Character character, Rigidbody body)", source);
            StringAssert.Contains("private void RegisterRigidbody(Rigidbody body)", source);
            StringAssert.Contains("state.WasKinematic", source);
            StringAssert.Contains("state.WasUseGravity", source);
            StringAssert.Contains("state.WasDetectingCollisions", source);
        }

        /// <summary>
        /// A Malbers rig authors UnitDriverMAnimal/UnitFacingMAnimal/UnitAnimimMAnimal and relies on
        /// NetworkCharacter swapping in the role driver at runtime. Every setup tool that rewrites a
        /// GC2 kernel must therefore bail out before it replaces a single unit.
        /// </summary>
        [Test]
        public void SetupTools_KeepTheMalbersCharacterKernel()
        {
            AssertGuardPrecedesFirstKernelWrite(
                "Arawn/NetworkingLayerForGC2/Editor/GameCreator2NetworkingSetupWizard.cs",
                "scene setup wizard");

            AssertGuardPrecedesFirstKernelWrite(
                "Arawn/NetworkingLayerForGC2/Editor/GC2SceneSetupShared.cs",
                "shared transport wizard helper");

            string menu = ReadAssetSource(
                "Arawn/NetworkingLayerForGC2/Editor/NetworkCharacterMenuItems.cs");
            StringAssert.Contains(
                "NetworkMalbersIntegrationPolicy.HasMalbersAnimal(character.gameObject)",
                menu,
                "The NetworkCharacter menu tools must not replace Malbers kernel units.");
        }

        private static void AssertGuardPrecedesFirstKernelWrite(
            string relativePath,
            string toolName)
        {
            string source = ReadAssetSource(relativePath);

            int guard = source.IndexOf(
                "NetworkMalbersIntegrationPolicy.HasMalbersAnimal",
                StringComparison.Ordinal);
            int firstWrite = source.IndexOf("managedReferenceValue", StringComparison.Ordinal);

            Assert.GreaterOrEqual(guard, 0, $"The {toolName} has no Malbers kernel guard.");
            Assert.GreaterOrEqual(firstWrite, 0, $"The {toolName} no longer writes a GC2 kernel.");
            Assert.Less(
                guard,
                firstWrite,
                $"The {toolName} must check for a Malbers character before replacing kernel units.");
        }

        private static string AssetPath(string relativePath)
        {
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            return Path.Combine(projectRoot, "Assets", relativePath);
        }

        private static string ReadAssetSource(string relativePath)
        {
            string path = AssetPath(relativePath);
            Assert.IsTrue(File.Exists(path), $"Expected source file was not found: {path}");
            return File.ReadAllText(path);
        }
    }
}
