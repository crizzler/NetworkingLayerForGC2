using System.Reflection;
using Arawn.GameCreator2.Networking.Transport.PurrNet;
using NUnit.Framework;

namespace Arawn.GameCreator2.Networking.CorePurrNet.Tests
{
    public sealed class PurrNetNetworkCharacterAutoTests
    {
        private static readonly MethodInfo OwnerDecisionMethod =
            typeof(PurrNetNetworkCharacterAuto).GetMethod(
                "TryResolveInitializationOwner",
                BindingFlags.NonPublic | BindingFlags.Static);

        [Test]
        public void ResolvedIdentityOwner_TakesPriorityOverOwnerMode()
        {
            (bool canInitialize, bool isOwner) nonOwner = Decide(
                identityApplicable: true,
                identityReady: true,
                identityOwner: false,
                waitForIdentityOwner: true,
                allowOwnerModeFallback: false,
                PurrNetNetworkCharacterAuto.OwnerMode.Everyone,
                isServer: true,
                isClient: true,
                isHost: true);

            Assert.That(nonOwner.canInitialize, Is.True);
            Assert.That(nonOwner.isOwner, Is.False);

            (bool canInitialize, bool isOwner) owner = Decide(
                identityApplicable: true,
                identityReady: true,
                identityOwner: true,
                waitForIdentityOwner: true,
                allowOwnerModeFallback: false,
                PurrNetNetworkCharacterAuto.OwnerMode.HostOnly,
                isServer: false,
                isClient: true,
                isHost: false);

            Assert.That(owner.canInitialize, Is.True);
            Assert.That(owner.isOwner, Is.True);
        }

        [Test]
        public void PendingIdentityOwner_BeforeTimeout_DefersInitialization()
        {
            (bool canInitialize, bool isOwner) decision = Decide(
                identityApplicable: true,
                identityReady: false,
                identityOwner: false,
                waitForIdentityOwner: true,
                allowOwnerModeFallback: false,
                PurrNetNetworkCharacterAuto.OwnerMode.HostOnly,
                isServer: true,
                isClient: true,
                isHost: true);

            Assert.That(decision.canInitialize, Is.False);
            Assert.That(decision.isOwner, Is.False);
        }

        [Test]
        public void PendingIdentityOwner_AfterTimeout_UsesHostOnlyFallback()
        {
            (bool canInitialize, bool isOwner) host = Decide(
                identityApplicable: true,
                identityReady: false,
                identityOwner: false,
                waitForIdentityOwner: true,
                allowOwnerModeFallback: true,
                PurrNetNetworkCharacterAuto.OwnerMode.HostOnly,
                isServer: true,
                isClient: true,
                isHost: true);

            (bool canInitialize, bool isOwner) joiningClient = Decide(
                identityApplicable: true,
                identityReady: false,
                identityOwner: false,
                waitForIdentityOwner: true,
                allowOwnerModeFallback: true,
                PurrNetNetworkCharacterAuto.OwnerMode.HostOnly,
                isServer: false,
                isClient: true,
                isHost: false);

            (bool canInitialize, bool isOwner) dedicatedServer = Decide(
                identityApplicable: true,
                identityReady: false,
                identityOwner: false,
                waitForIdentityOwner: true,
                allowOwnerModeFallback: true,
                PurrNetNetworkCharacterAuto.OwnerMode.HostOnly,
                isServer: true,
                isClient: false,
                isHost: false);

            Assert.That(host.canInitialize, Is.True);
            Assert.That(host.isOwner, Is.True);
            Assert.That(joiningClient.canInitialize, Is.True);
            Assert.That(joiningClient.isOwner, Is.False);
            Assert.That(dedicatedServer.canInitialize, Is.True);
            Assert.That(dedicatedServer.isOwner, Is.True);
        }

        [Test]
        public void PendingIdentityOwner_WhenWaitDisabled_UsesOwnerModeImmediately()
        {
            (bool canInitialize, bool isOwner) decision = Decide(
                identityApplicable: true,
                identityReady: false,
                identityOwner: false,
                waitForIdentityOwner: false,
                allowOwnerModeFallback: false,
                PurrNetNetworkCharacterAuto.OwnerMode.Everyone,
                isServer: false,
                isClient: true,
                isHost: false);

            Assert.That(decision.canInitialize, Is.True);
            Assert.That(decision.isOwner, Is.True);
        }

        [Test]
        public void MissingIdentity_UsesOwnerModeImmediately()
        {
            (bool canInitialize, bool isOwner) decision = Decide(
                identityApplicable: false,
                identityReady: false,
                identityOwner: false,
                waitForIdentityOwner: true,
                allowOwnerModeFallback: false,
                PurrNetNetworkCharacterAuto.OwnerMode.HostOnly,
                isServer: true,
                isClient: true,
                isHost: true);

            Assert.That(decision.canInitialize, Is.True);
            Assert.That(decision.isOwner, Is.True);
        }

        private static (bool canInitialize, bool isOwner) Decide(
            bool identityApplicable,
            bool identityReady,
            bool identityOwner,
            bool waitForIdentityOwner,
            bool allowOwnerModeFallback,
            PurrNetNetworkCharacterAuto.OwnerMode ownerMode,
            bool isServer,
            bool isClient,
            bool isHost)
        {
            Assert.That(OwnerDecisionMethod, Is.Not.Null);

            object[] arguments =
            {
                identityApplicable,
                identityReady,
                identityOwner,
                waitForIdentityOwner,
                allowOwnerModeFallback,
                ownerMode,
                isServer,
                isClient,
                isHost,
                false
            };

            bool canInitialize = (bool)OwnerDecisionMethod.Invoke(null, arguments);
            return (canInitialize, (bool)arguments[9]);
        }
    }
}
