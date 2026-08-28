using System.Collections.Generic;
using Fusion;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Transport.Fusion
{
    public struct FusionBotSlotReplicatedState : INetworkStruct
    {
        public uint SlotIdHash;
        public byte OccupantType;
        public uint HumanClientId;
        public uint ActorNetworkId;
        public Vector3 Position;
        public Quaternion Rotation;
        public uint Revision;
    }

    /// <summary>
    /// Replicated fixed-capacity slot membership. Keeping this on a scene NetworkObject preserves
    /// the authoritative roster through Fusion Shared master migration.
    /// </summary>
    [AddComponentMenu("Game Creator/Network/Transport/Fusion Bot Slot State Replicator")]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(NetworkObject))]
    public sealed class FusionBotSlotStateReplicator : NetworkBehaviour
    {
        public const int MaxSlots = 16;

        [SerializeField] private FusionBotSlotCoordinator m_Coordinator;

        [Networked] public int ReplicatedSlotCount { get; private set; }
        [Networked] public uint SnapshotRevision { get; private set; }
        [Networked, Capacity(MaxSlots)]
        private NetworkArray<FusionBotSlotReplicatedState> ReplicatedSlots { get; }

        private readonly List<NetworkBotSlotState> m_ReadBuffer = new(MaxSlots);
        private uint m_LastAppliedRevision;

        public override void Spawned()
        {
            ResolveCoordinator();
            ApplyReplicatedState(force: true);
        }

        public override void Render()
        {
            if (SnapshotRevision == m_LastAppliedRevision) return;
            ApplyReplicatedState(force: false);
        }

        public bool WriteAuthoritativeState(IReadOnlyList<NetworkBotSlotState> states)
        {
            if (Object == null || !Object.IsValid || !Object.HasStateAuthority || states == null)
            {
                return false;
            }

            int count = Mathf.Min(states.Count, MaxSlots);
            ReplicatedSlotCount = count;
            for (int i = 0; i < count; i++)
            {
                NetworkBotSlotState state = states[i];
                ReplicatedSlots.Set(i, new FusionBotSlotReplicatedState
                {
                    SlotIdHash = state.SlotIdHash,
                    OccupantType = (byte)state.OccupantType,
                    HumanClientId = state.HumanClientId,
                    ActorNetworkId = state.ActorNetworkId,
                    Position = state.Position,
                    Rotation = state.Rotation,
                    Revision = state.Revision
                });
            }

            SnapshotRevision++;
            m_LastAppliedRevision = SnapshotRevision;
            return true;
        }

        public bool TryReadReplicatedState(List<NetworkBotSlotState> destination)
        {
            if (destination == null || Object == null || !Object.IsValid) return false;

            destination.Clear();
            int count = Mathf.Clamp(ReplicatedSlotCount, 0, MaxSlots);
            for (int i = 0; i < count; i++)
            {
                FusionBotSlotReplicatedState state = ReplicatedSlots[i];
                destination.Add(new NetworkBotSlotState
                {
                    SlotIdHash = state.SlotIdHash,
                    OccupantType = (NetworkBotSlotOccupantType)state.OccupantType,
                    HumanClientId = state.HumanClientId,
                    ActorNetworkId = state.ActorNetworkId,
                    Position = state.Position,
                    Rotation = state.Rotation,
                    Revision = state.Revision
                });
            }

            return destination.Count > 0;
        }

        private void ApplyReplicatedState(bool force)
        {
            ResolveCoordinator();
            if (m_Coordinator == null) return;
            if (!force && SnapshotRevision == m_LastAppliedRevision) return;
            if (TryReadReplicatedState(m_ReadBuffer))
            {
                m_Coordinator.ApplyReplicatedSnapshot(m_ReadBuffer);
            }
            m_LastAppliedRevision = SnapshotRevision;
        }

        private void ResolveCoordinator()
        {
            if (m_Coordinator == null)
            {
                m_Coordinator = GetComponent<FusionBotSlotCoordinator>();
            }
        }
    }
}
