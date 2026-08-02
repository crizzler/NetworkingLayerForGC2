using NUnit.Framework;

namespace Arawn.GameCreator2.Networking.Transport.PurrNet.Lobby.Tests
{
    public sealed class PurrNetStagingRulesTests
    {
        [Test]
        public void ManualPolicyNeverStartsAutomatically()
        {
            Assert.That(
                PurrNetStagingRules.ShouldStartAutomatically(
                    PurrNetStagingStartPolicy.HostManual,
                    8,
                    8,
                    1,
                    8,
                    true),
                Is.False);
        }

        [Test]
        public void AutomaticPoliciesCannotBeBypassedByManualStart()
        {
            Assert.That(
                PurrNetStagingRules.AllowsManualStart(PurrNetStagingStartPolicy.HostManual),
                Is.True);
            Assert.That(
                PurrNetStagingRules.AllowsManualStart(
                    PurrNetStagingStartPolicy.AutomaticPlayerThreshold),
                Is.False);
            Assert.That(
                PurrNetStagingRules.AllowsManualStart(PurrNetStagingStartPolicy.AutomaticAllReady),
                Is.False);
        }

        [Test]
        public void BotsAreServerReadyWithoutAClientRequestPath()
        {
            Assert.That(PurrNetStagingRules.InitialReady(true), Is.True);
            Assert.That(PurrNetStagingRules.InitialReady(false), Is.False);
        }

        [Test]
        public void PlayerThresholdCanRequireEveryConnectedPlayerReady()
        {
            Assert.That(
                PurrNetStagingRules.ShouldStartAutomatically(
                    PurrNetStagingStartPolicy.AutomaticPlayerThreshold,
                    7,
                    7,
                    2,
                    8,
                    true),
                Is.False);
            Assert.That(
                PurrNetStagingRules.ShouldStartAutomatically(
                    PurrNetStagingStartPolicy.AutomaticPlayerThreshold,
                    8,
                    7,
                    2,
                    8,
                    true),
                Is.False);
            Assert.That(
                PurrNetStagingRules.ShouldStartAutomatically(
                    PurrNetStagingStartPolicy.AutomaticPlayerThreshold,
                    8,
                    8,
                    2,
                    8,
                    true),
                Is.True);
        }

        [Test]
        public void AllReadyPolicyStillHonorsMinimumPlayers()
        {
            Assert.That(
                PurrNetStagingRules.ShouldStartAutomatically(
                    PurrNetStagingStartPolicy.AutomaticAllReady,
                    1,
                    1,
                    2,
                    8,
                    true),
                Is.False);
            Assert.That(
                PurrNetStagingRules.ShouldStartAutomatically(
                    PurrNetStagingStartPolicy.AutomaticAllReady,
                    2,
                    2,
                    2,
                    8,
                    true),
                Is.True);
        }

        [Test]
        public void HostEligibilityCanRequireAllPlayersReady()
        {
            Assert.That(PurrNetStagingRules.CanHostStart(2, 1, 2, true), Is.False);
            Assert.That(PurrNetStagingRules.CanHostStart(2, 2, 2, true), Is.True);
            Assert.That(PurrNetStagingRules.CanHostStart(2, 0, 2, false), Is.True);
        }

        [Test]
        public void StrictRoomRejectsOnlyFreshNonHostLateJoins()
        {
            Assert.That(
                PurrNetStagingRules.ShouldRejectLateJoin(true, false, false, false),
                Is.True);
            Assert.That(
                PurrNetStagingRules.ShouldRejectLateJoin(true, true, false, false),
                Is.False,
                "Join-in-progress mode must admit fresh players.");
            Assert.That(
                PurrNetStagingRules.ShouldRejectLateJoin(true, false, true, false),
                Is.False,
                "PurrNet-identified reconnects must be admitted.");
            Assert.That(
                PurrNetStagingRules.ShouldRejectLateJoin(true, false, false, true),
                Is.False,
                "The listen-host loopback player must never be kicked.");
            Assert.That(
                PurrNetStagingRules.ShouldRejectLateJoin(false, false, false, false),
                Is.False,
                "Every fresh player is eligible before the match starts.");
        }

        [Test]
        public void LanAdvertisementOpenStateHonorsAdmissionAndCapacity()
        {
            Assert.That(PurrNetLobbyService.ShouldAdvertiseOpen(true, 3, 8), Is.True);
            Assert.That(PurrNetLobbyService.ShouldAdvertiseOpen(false, 3, 8), Is.False);
            Assert.That(PurrNetLobbyService.ShouldAdvertiseOpen(true, 8, 8), Is.False);
            Assert.That(PurrNetLobbyService.ShouldAdvertiseOpen(true, 0, 0), Is.False);
        }

        [Test]
        public void ServiceCapacityGuardIsAHardLimitIncludingReconnects()
        {
            Assert.That(PurrNetLobbyService.ShouldRejectCapacityOverflow(8, 8, false), Is.False);
            Assert.That(PurrNetLobbyService.ShouldRejectCapacityOverflow(9, 8, false), Is.True);
            Assert.That(PurrNetLobbyService.ShouldRejectCapacityOverflow(9, 8, true), Is.True);
            Assert.That(PurrNetLobbyService.ShouldRejectCapacityOverflow(9, 0, false), Is.False);
        }

        [Test]
        public void DisplayNameSanitizationRemovesControlsAndAppliesBounds()
        {
            Assert.That(
                PurrNetStagingRules.SanitizeDisplayName("  Alice\nThe\tGreat  ", 10, "Player"),
                Is.EqualTo("Alice The "));
            Assert.That(
                PurrNetStagingRules.SanitizeDisplayName("\r\n", 24, "Guest"),
                Is.EqualTo("Guest"));
            Assert.That(
                PurrNetStagingRules.SanitizeDisplayName(null, 24, null),
                Is.EqualTo("Player"));
        }
    }
}
