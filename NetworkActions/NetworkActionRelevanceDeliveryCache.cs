using System;
using System.Collections.Generic;

namespace Arawn.GameCreator2.Networking
{
    /// <summary>
    /// Authority-side delivery memory for persistent actions whose audience is determined by
    /// dynamic observer relevance. A removed entry deliberately means "not currently an
    /// observer", so entering relevance again produces one current-state catch-up delta.
    /// </summary>
    public sealed class NetworkActionRelevanceDeliveryCache
    {
        private readonly struct ActionKey : IEquatable<ActionKey>
        {
            public readonly uint TargetNetworkId;
            public readonly int EndpointHash;
            public readonly int ActionHash;
            public readonly string ActionId;

            public ActionKey(in NetworkActionBroadcast broadcast)
            {
                TargetNetworkId = broadcast.TargetNetworkId;
                EndpointHash = broadcast.EndpointHash;
                ActionHash = broadcast.ActionHash;
                ActionId = broadcast.ActionId ?? string.Empty;
            }

            public bool Equals(ActionKey other) =>
                TargetNetworkId == other.TargetNetworkId &&
                EndpointHash == other.EndpointHash &&
                ActionHash == other.ActionHash &&
                string.Equals(ActionId, other.ActionId, StringComparison.Ordinal);

            public override bool Equals(object obj) => obj is ActionKey other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = (int)TargetNetworkId;
                    hash = (hash * 397) ^ EndpointHash;
                    hash = (hash * 397) ^ ActionHash;
                    hash = (hash * 397) ^ StringComparer.Ordinal.GetHashCode(ActionId);
                    return hash;
                }
            }
        }

        private readonly struct DeliveredVersion
        {
            public readonly uint AuthorityEpoch;
            public readonly uint Revision;

            public DeliveredVersion(in NetworkActionBroadcast broadcast)
            {
                AuthorityEpoch = broadcast.AuthorityEpoch;
                Revision = broadcast.Revision;
            }
        }

        private readonly Dictionary<uint, Dictionary<ActionKey, DeliveredVersion>> m_ByClient =
            new(16);

        public int ClientCount => m_ByClient.Count;

        public static bool IsTracked(in NetworkActionBroadcast broadcast) =>
            broadcast.EffectKind == NetworkActionEffectKind.PersistentState &&
            broadcast.RecipientPolicy == NetworkActionRecipientPolicy.RelevantObservers;

        /// <summary>
        /// True on first relevance/re-entry or when authority has a strictly newer canonical
        /// version than the one last delivered to this client.
        /// </summary>
        public bool NeedsDelivery(uint clientId, in NetworkActionBroadcast broadcast)
        {
            if (!IsTracked(in broadcast)) return false;
            if (!m_ByClient.TryGetValue(clientId, out var actions)) return true;
            if (!actions.TryGetValue(new ActionKey(in broadcast), out var delivered)) return true;
            return IsNewer(
                broadcast.AuthorityEpoch,
                broadcast.Revision,
                delivered.AuthorityEpoch,
                delivered.Revision);
        }

        public void MarkDelivered(uint clientId, in NetworkActionBroadcast broadcast)
        {
            if (!IsTracked(in broadcast)) return;
            if (!m_ByClient.TryGetValue(clientId, out var actions))
            {
                actions = new Dictionary<ActionKey, DeliveredVersion>(16);
                m_ByClient.Add(clientId, actions);
            }

            actions[new ActionKey(in broadcast)] = new DeliveredVersion(in broadcast);
        }

        /// <summary>
        /// Drops membership for this action. If the client becomes relevant again, its current
        /// authoritative state is sent even when the revision did not change while it was away.
        /// </summary>
        public void MarkIrrelevant(uint clientId, in NetworkActionBroadcast broadcast)
        {
            if (!IsTracked(in broadcast) ||
                !m_ByClient.TryGetValue(clientId, out var actions)) return;
            actions.Remove(new ActionKey(in broadcast));
            if (actions.Count == 0) m_ByClient.Remove(clientId);
        }

        public void RemoveClient(uint clientId) => m_ByClient.Remove(clientId);

        /// <summary>
        /// Forgets delivery history for a terminal endpoint generation. A later object may reuse
        /// the same transport ID and authored endpoint hash with a revision that starts at one;
        /// retaining the old revision would incorrectly suppress that new object's initial state.
        /// </summary>
        public void RemoveEndpoint(NetworkActionEndpointKey endpoint)
        {
            if (!endpoint.IsValid || m_ByClient.Count == 0) return;

            var emptyClients = new List<uint>();
            foreach (KeyValuePair<uint, Dictionary<ActionKey, DeliveredVersion>> client in m_ByClient)
            {
                var remove = new List<ActionKey>();
                foreach (ActionKey action in client.Value.Keys)
                {
                    if (action.TargetNetworkId == endpoint.NetworkId &&
                        action.EndpointHash == endpoint.EndpointHash)
                    {
                        remove.Add(action);
                    }
                }

                for (int i = 0; i < remove.Count; i++) client.Value.Remove(remove[i]);
                if (client.Value.Count == 0) emptyClients.Add(client.Key);
            }

            for (int i = 0; i < emptyClients.Count; i++) m_ByClient.Remove(emptyClients[i]);
        }

        public void Clear() => m_ByClient.Clear();

        private static bool IsNewer(
            uint candidateEpoch,
            uint candidateRevision,
            uint deliveredEpoch,
            uint deliveredRevision)
        {
            // Epochs and revisions are monotonic for the lifetime represented by this cache.
            // Session restart clears the cache, and authority code avoids emitting zero, so a
            // plain comparison is both easier to audit and correct across a normal wrap reset.
            if (candidateEpoch != deliveredEpoch) return candidateEpoch > deliveredEpoch;
            return candidateRevision > deliveredRevision;
        }
    }
}
