using System.Collections.Generic;
using Fusion;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Transport.Fusion
{
    /// <summary>Fusion actor spawning adapter for the shared bot-slot transaction manager.</summary>
    [AddComponentMenu("Game Creator/Network/Transport/Fusion Bot Slot Coordinator")]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(FusionBotSlotStateReplicator))]
    public sealed class FusionBotSlotCoordinator : NetworkBotSlotCoordinator
    {
        [SerializeField] private FusionTransportBridge m_TransportBridge;
        [SerializeField] private FusionAuthoritySpawnRegistry m_SpawnRegistry;
        [SerializeField] private FusionBotSlotStateReplicator m_StateReplicator;

        private readonly List<NetworkBotSlotState> m_MigrationBuffer =
            new(FusionBotSlotStateReplicator.MaxSlots);

        protected override bool IsTransportAuthority =>
            ResolveBridge() != null && m_TransportBridge.IsServer;

        protected override void Awake()
        {
            ResolveReferences();
            base.Awake();
        }

        protected override void OnAuthorityGained()
        {
            ResolveReferences();
            if (m_StateReplicator != null &&
                m_StateReplicator.TryReadReplicatedState(m_MigrationBuffer))
            {
                ApplyReplicatedSnapshot(m_MigrationBuffer);
            }

            base.OnAuthorityGained();
        }

        protected override GameObject SpawnBotActor(
            GameObject prefab,
            Vector3 position,
            Quaternion rotation,
            uint slotIdHash)
        {
            ResolveReferences();
            NetworkObject networkPrefab = prefab != null
                ? prefab.GetComponent<NetworkObject>()
                : null;
            if (networkPrefab == null || m_SpawnRegistry == null) return null;

            NetworkObject spawned = m_SpawnRegistry.Spawn(
                networkPrefab,
                position,
                rotation,
                PlayerRef.Invalid,
                inputAuthority: null);
            return spawned != null ? spawned.gameObject : null;
        }

        protected override bool DespawnBotActor(GameObject actor)
        {
            ResolveReferences();
            NetworkObject networkObject = actor != null ? actor.GetComponent<NetworkObject>() : null;
            return networkObject != null &&
                networkObject.Id.IsValid &&
                m_SpawnRegistry != null &&
                m_SpawnRegistry.Despawn(networkObject.Id);
        }

        protected override uint ResolveActorNetworkId(GameObject actor)
        {
            NetworkObject networkObject = actor != null ? actor.GetComponent<NetworkObject>() : null;
            return networkObject != null && networkObject.Id.IsValid ? networkObject.Id.Raw : 0;
        }

        protected override GameObject ResolveActor(uint actorNetworkId)
        {
            ResolveReferences();
            NetworkRunner runner = m_TransportBridge != null ? m_TransportBridge.Runner : null;
            return runner != null &&
                actorNetworkId != 0 &&
                runner.TryFindObject(NetworkId.FromRaw(actorNetworkId), out NetworkObject actor)
                    ? actor.gameObject
                    : null;
        }

        protected override void PublishAuthoritativeSnapshot(
            IReadOnlyList<NetworkBotSlotState> states)
        {
            ResolveReferences();
            m_StateReplicator?.WriteAuthoritativeState(states);
        }

        private FusionTransportBridge ResolveBridge()
        {
            if (m_TransportBridge == null)
            {
                m_TransportBridge = NetworkTransportBridge.Active as FusionTransportBridge;
            }
            return m_TransportBridge;
        }

        private void ResolveReferences()
        {
            ResolveBridge();
            if (m_StateReplicator == null)
            {
                m_StateReplicator = GetComponent<FusionBotSlotStateReplicator>();
            }
            if (m_SpawnRegistry == null)
            {
                m_SpawnRegistry = GetComponentInParent<FusionAuthoritySpawnRegistry>();
            }
        }
    }
}
