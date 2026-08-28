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
        private static readonly MethodInfo NpcIdentityDecisionMethod =
            typeof(PurrNetNetworkCharacterAuto).GetMethod(
                "ShouldDeferExplicitNpcUntilIdentityReady",
                BindingFlags.NonPublic | BindingFlags.Static);
        private static readonly MethodInfo LegacyCompatibilityDecisionMethod =
            typeof(PurrNetNetworkCharacterAuto).GetMethod(
                "ShouldUseLegacyCompatibilityInitialization",
                BindingFlags.NonPublic | BindingFlags.Static);

        [Test]
        [Category("GC2Networking.FreeFlow")]
        public void ExplicitNpc_WithPurrNetIdentity_DefersUntilReplicatedIdentitySpawns()
        {
            Assert.That(NpcIdentityDecisionMethod, Is.Not.Null);

            Assert.That(
                DeferNpcIdentity(
                    NetworkCharacterActorType.NPC,
                    NetworkCharacter.NPCSyncMode.ServerAuthoritative,
                    hasNetworkIdentity: true,
                    identitySpawned: false,
                    identityIdUsable: false),
                Is.True,
                "An observer must not register an explicit NPC under its local GC2 hash " +
                "before PurrNet supplies the shared object id.");
            Assert.That(
                DeferNpcIdentity(
                    NetworkCharacterActorType.NPC,
                    NetworkCharacter.NPCSyncMode.ServerAuthoritative,
                    hasNetworkIdentity: true,
                    identitySpawned: true,
                    identityIdUsable: false),
                Is.True,
                "A spawned identity without a usable replicated object id must still defer.");
            Assert.That(
                DeferNpcIdentity(
                    NetworkCharacterActorType.NPC,
                    NetworkCharacter.NPCSyncMode.ServerAuthoritative,
                    hasNetworkIdentity: true,
                    identitySpawned: true,
                    identityIdUsable: true),
                Is.False);
            Assert.That(
                DeferNpcIdentity(
                    NetworkCharacterActorType.PlayerOwned,
                    NetworkCharacter.NPCSyncMode.ServerAuthoritative,
                    hasNetworkIdentity: true,
                    identitySpawned: false,
                    identityIdUsable: false),
                Is.False,
                "Player ownership has its separate replicated-owner readiness path.");
            Assert.That(
                DeferNpcIdentity(
                    NetworkCharacterActorType.NPC,
                    NetworkCharacter.NPCSyncMode.ClientSideDeterministic,
                    hasNetworkIdentity: true,
                    identitySpawned: false,
                    identityIdUsable: false),
                Is.False,
                "Cosmetic client-deterministic NPCs do not author durable state and must not " +
                "wait forever for an intentionally absent network spawn.");
            Assert.That(
                DeferNpcIdentity(
                    NetworkCharacterActorType.NPC,
                    NetworkCharacter.NPCSyncMode.ServerAuthoritative,
                    hasNetworkIdentity: false,
                    identitySpawned: false,
                    identityIdUsable: false),
                Is.False,
                "Custom PurrNet NPC integrations without NetworkIdentity retain the normal " +
                    "transport/manual-id path.");

            Assert.That(LegacyCompatibilityDecisionMethod, Is.Not.Null);
            Assert.That(
                UseLegacyCompatibilityInitialization(
                    NetworkCharacterActorType.LegacyAutomatic,
                    identityApplicable: true,
                    identityReady: false),
                Is.True,
                "An unresolved legacy character must retain the compatibility overload's " +
                "authored-player classification during its Owner Mode fallback.");
            Assert.That(
                UseLegacyCompatibilityInitialization(
                    NetworkCharacterActorType.PlayerOwned,
                    identityApplicable: true,
                    identityReady: false),
                Is.False,
                "Explicit actor types must use transport-authenticated role initialization.");
        }

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

        private static bool DeferNpcIdentity(
            NetworkCharacterActorType actorType,
            NetworkCharacter.NPCSyncMode npcSyncMode,
            bool hasNetworkIdentity,
            bool identitySpawned,
            bool identityIdUsable)
        {
            return (bool)NpcIdentityDecisionMethod.Invoke(
                null,
                new object[]
                {
                    actorType,
                    npcSyncMode,
                    hasNetworkIdentity,
                    identitySpawned,
                    identityIdUsable
                });
        }

        private static bool UseLegacyCompatibilityInitialization(
            NetworkCharacterActorType actorType,
            bool identityApplicable,
            bool identityReady)
        {
            return (bool)LegacyCompatibilityDecisionMethod.Invoke(
                null,
                new object[] { actorType, identityApplicable, identityReady });
        }
    }
}
