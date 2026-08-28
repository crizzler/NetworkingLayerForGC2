using System;
using System.IO;
using System.Reflection;
using Arawn.GameCreator2.Networking.Transport.PurrNet.Editor;
using NUnit.Framework;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Transport.PurrNet.Editor.Tests
{
    public sealed class PurrNetWizardPredictionTests
    {
        private const BindingFlags STATIC_NON_PUBLIC =
            BindingFlags.Static | BindingFlags.NonPublic;

        [Test]
        public void TraversalCapabilityGate_RequiresOwnerAndServerContracts()
        {
            MethodInfo capabilityGate = typeof(PurrNetSceneSetupWizard).GetMethod(
                "HasTraversalMotionAuthorityCapabilities",
                STATIC_NON_PUBLIC);

            Assert.That(capabilityGate, Is.Not.Null);
            Assert.That(InvokeCapabilityGate(capabilityGate, null), Is.False);
            Assert.That(InvokeCapabilityGate(capabilityGate, typeof(OwnerOnlyCapability)), Is.False);
            Assert.That(InvokeCapabilityGate(capabilityGate, typeof(ServerOnlyCapability)), Is.False);
            Assert.That(InvokeCapabilityGate(capabilityGate, typeof(CompleteCapability)), Is.True);
        }

        [Test]
        [NUnit.Framework.Category("GC2Networking.FreeFlow")]
        public void Wizard_PreparesAndValidatesServerNpcsAndBotSlots()
        {
            string path = Path.Combine(
                Application.dataPath,
                "Arawn/NetworkingLayerForGC2/Editor/Transport/PurrNet/" +
                "PurrNetSceneSetupWizard.cs");
            string source = File.ReadAllText(path);

            StringAssert.Contains("Server-Owned NPCs and Bot Slots", source);
            StringAssert.Contains("private string EnsureNpcPrefabSetup()", source);
            StringAssert.Contains("EnsurePrefabComponent<NetworkIdentity>", source);
            StringAssert.Contains("NetworkNpcSetupEditorUtility.ConfigureServerNpc(", source);
            StringAssert.Contains("NetworkNpcSetupEditorUtility.ValidateServerNpc(", source);
            StringAssert.Contains("m_NpcAuthorityRootPaths.Count == 0", source);
            StringAssert.Contains("NetworkPredictionBackend.BuiltIn", source);
            StringAssert.Contains("PurrDictionNetworkCharacterController", source);
            StringAssert.Contains("PurrDictionNetworkNavmeshController", source);
            StringAssert.Contains("PurrNetBotSlotCoordinator", source);
            StringAssert.Contains("m_BotSlotCoordinator", source);
            StringAssert.Contains("prefabs.prefabs.Add", source);
            StringAssert.Contains("Server-authoritative Free Flow Combat", source);
            StringAssert.Contains("NETWORK_FREE_FLOW_COMBAT_ADAPTER_TYPE", source);
            StringAssert.Contains(
                "m_ModuleMelee && m_EnableFreeFlowCombat",
                source);
            StringAssert.Contains("HasFreeFlowPreflightError", source);
        }

        private static bool InvokeCapabilityGate(MethodInfo method, Type candidate)
        {
            return (bool)method.Invoke(null, new object[] { candidate });
        }

        private sealed class OwnerOnlyCapability : INetworkOwnerMotionAuthority
        {
            public void OpenOwnerMotionWindow(float durationSeconds) { }
        }

        private sealed class ServerOnlyCapability : INetworkServerOwnerMotionAuthority
        {
            public void OpenServerOwnerMotionWindow(float durationSeconds, uint operationId = 0) { }
            public void CloseServerOwnerMotionWindow(float graceSeconds = 0f) { }
        }

        private sealed class CompleteCapability :
            INetworkOwnerMotionAuthority,
            INetworkServerOwnerMotionAuthority
        {
            public void OpenOwnerMotionWindow(float durationSeconds) { }
            public void OpenServerOwnerMotionWindow(float durationSeconds, uint operationId = 0) { }
            public void CloseServerOwnerMotionWindow(float graceSeconds = 0f) { }
        }
    }
}
