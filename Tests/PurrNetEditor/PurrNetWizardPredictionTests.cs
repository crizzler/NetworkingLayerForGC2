using System;
using System.Reflection;
using Arawn.GameCreator2.Networking.Transport.PurrNet.Editor;
using NUnit.Framework;

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
