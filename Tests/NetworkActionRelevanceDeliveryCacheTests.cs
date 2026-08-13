#if UNITY_INCLUDE_TESTS
using NUnit.Framework;

namespace Arawn.GameCreator2.Networking.Tests
{
    public sealed class NetworkActionRelevanceDeliveryCacheTests
    {
        [Test]
        public void RelevantPersistentState_IsSentOnceUntilItsVersionChanges()
        {
            var cache = new NetworkActionRelevanceDeliveryCache();
            NetworkActionBroadcast state = CreateState(3, 7);

            Assert.That(cache.NeedsDelivery(12, in state), Is.True);
            cache.MarkDelivered(12, in state);
            Assert.That(cache.NeedsDelivery(12, in state), Is.False);

            state.Revision++;
            Assert.That(cache.NeedsDelivery(12, in state), Is.True);
        }

        [Test]
        public void RelevanceExitAndDisconnect_RequireFreshCatchUp()
        {
            var cache = new NetworkActionRelevanceDeliveryCache();
            NetworkActionBroadcast state = CreateState(3, 7);
            cache.MarkDelivered(12, in state);

            cache.MarkIrrelevant(12, in state);
            Assert.That(cache.NeedsDelivery(12, in state), Is.True,
                "Re-entering relevance must receive current state even without a revision change.");

            cache.MarkDelivered(12, in state);
            cache.RemoveClient(12);
            Assert.That(cache.NeedsDelivery(12, in state), Is.True);
        }

        [Test]
        public void AuthorityEpochChange_IsANewerCanonicalVersion()
        {
            var cache = new NetworkActionRelevanceDeliveryCache();
            NetworkActionBroadcast state = CreateState(3, 99);
            cache.MarkDelivered(12, in state);
            state.AuthorityEpoch++;
            state.Revision = 1;

            Assert.That(cache.NeedsDelivery(12, in state), Is.True);
        }

        [Test]
        public void TransientAndAllClientActions_AreNotTracked()
        {
            var cache = new NetworkActionRelevanceDeliveryCache();
            NetworkActionBroadcast transient = CreateState(3, 7);
            transient.EffectKind = NetworkActionEffectKind.TransientEvent;
            NetworkActionBroadcast allClients = CreateState(3, 7);
            allClients.RecipientPolicy = NetworkActionRecipientPolicy.AllClients;

            Assert.That(cache.NeedsDelivery(12, in transient), Is.False);
            Assert.That(cache.NeedsDelivery(12, in allClients), Is.False);
        }

        [Test]
        public void TerminalEndpointRemoval_ForgetsOldGenerationRevision()
        {
            var cache = new NetworkActionRelevanceDeliveryCache();
            NetworkActionBroadcast oldGeneration = CreateState(3, 9);
            cache.MarkDelivered(12, in oldGeneration);

            cache.RemoveEndpoint(new NetworkActionEndpointKey(
                oldGeneration.TargetNetworkId, oldGeneration.EndpointHash));

            NetworkActionBroadcast replacement = CreateState(3, 1);
            Assert.That(cache.NeedsDelivery(12, in replacement), Is.True,
                "A same-prefab replacement starts at revision one and must be sent.");
        }

        private static NetworkActionBroadcast CreateState(uint epoch, uint revision) => new()
        {
            TargetNetworkId = 42,
            EndpointHash = 99,
            ActionHash = 101,
            ActionId = "door.open",
            EffectKind = NetworkActionEffectKind.PersistentState,
            RecipientPolicy = NetworkActionRecipientPolicy.RelevantObservers,
            AuthorityEpoch = epoch,
            Revision = revision
        };
    }
}
#endif
