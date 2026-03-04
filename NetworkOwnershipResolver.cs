using System.Collections.Generic;

namespace Arawn.GameCreator2.Networking
{
    public interface INetworkOwnershipResolver
    {
        bool TryResolveOwnerClientId(uint actorNetworkId, out uint ownerClientId);
        bool TryResolveActorNetworkIdForEntity(uint entityNetworkId, out uint actorNetworkId);
        bool TryResolveOwnerClientIdForEntity(uint entityNetworkId, out uint ownerClientId);
        bool ValidateOwnership(uint senderClientId, uint actorNetworkId, out uint resolvedOwnerClientId);

        void RegisterEntityOwner(uint entityNetworkId, uint ownerClientId);
        void RegisterEntityActor(uint entityNetworkId, uint actorNetworkId);
        void UnregisterEntity(uint entityNetworkId);
        void Clear();
    }

    /// <summary>
    /// Resolves sender-to-actor ownership using transport character ownership and module owner maps.
    /// </summary>
    public class NetworkOwnershipResolver : INetworkOwnershipResolver
    {
        private readonly Dictionary<uint, uint> m_EntityOwnerByNetworkId = new Dictionary<uint, uint>(128);
        private readonly Dictionary<uint, uint> m_EntityActorByNetworkId = new Dictionary<uint, uint>(128);

        public bool TryResolveOwnerClientId(uint actorNetworkId, out uint ownerClientId)
        {
            ownerClientId = 0;
            if (actorNetworkId == 0) return false;

            NetworkTransportBridge bridge = NetworkTransportBridge.Active;
            if (bridge != null && bridge.TryGetCharacterOwner(actorNetworkId, out ownerClientId))
            {
                return true;
            }

            return m_EntityOwnerByNetworkId.TryGetValue(actorNetworkId, out ownerClientId);
        }

        public bool TryResolveActorNetworkIdForEntity(uint entityNetworkId, out uint actorNetworkId)
        {
            actorNetworkId = 0;
            if (entityNetworkId == 0) return false;

            if (m_EntityActorByNetworkId.TryGetValue(entityNetworkId, out actorNetworkId) && actorNetworkId != 0)
            {
                return true;
            }

            actorNetworkId = entityNetworkId;
            return true;
        }

        public bool TryResolveOwnerClientIdForEntity(uint entityNetworkId, out uint ownerClientId)
        {
            ownerClientId = 0;
            if (entityNetworkId == 0) return false;

            if (m_EntityOwnerByNetworkId.TryGetValue(entityNetworkId, out ownerClientId))
            {
                return true;
            }

            if (!TryResolveActorNetworkIdForEntity(entityNetworkId, out uint actorNetworkId))
            {
                return false;
            }

            return TryResolveOwnerClientId(actorNetworkId, out ownerClientId);
        }

        public bool ValidateOwnership(uint senderClientId, uint actorNetworkId, out uint resolvedOwnerClientId)
        {
            resolvedOwnerClientId = 0;
            if (senderClientId == 0 || actorNetworkId == 0)
            {
                return false;
            }

            if (!TryResolveOwnerClientId(actorNetworkId, out resolvedOwnerClientId))
            {
                return false;
            }

            return senderClientId == resolvedOwnerClientId;
        }

        public void RegisterEntityOwner(uint entityNetworkId, uint ownerClientId)
        {
            if (entityNetworkId == 0 || ownerClientId == 0) return;
            m_EntityOwnerByNetworkId[entityNetworkId] = ownerClientId;
        }

        public void RegisterEntityActor(uint entityNetworkId, uint actorNetworkId)
        {
            if (entityNetworkId == 0 || actorNetworkId == 0) return;
            m_EntityActorByNetworkId[entityNetworkId] = actorNetworkId;

            if (TryResolveOwnerClientId(actorNetworkId, out uint ownerClientId) && ownerClientId != 0)
            {
                m_EntityOwnerByNetworkId[entityNetworkId] = ownerClientId;
            }
        }

        public void UnregisterEntity(uint entityNetworkId)
        {
            if (entityNetworkId == 0) return;
            m_EntityOwnerByNetworkId.Remove(entityNetworkId);
            m_EntityActorByNetworkId.Remove(entityNetworkId);
        }

        public void Clear()
        {
            m_EntityOwnerByNetworkId.Clear();
            m_EntityActorByNetworkId.Clear();
        }
    }
}
