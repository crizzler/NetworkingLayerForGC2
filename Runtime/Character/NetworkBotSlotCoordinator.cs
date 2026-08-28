using System;
using System.Collections.Generic;
using UnityEngine;

namespace Arawn.GameCreator2.Networking
{
    /// <summary>
    /// Transport-neutral, authority-only bot slot transaction manager. Transports provide actor
    /// spawning/despawning and snapshot replication while this class owns idempotency, rollback,
    /// overflow behavior, and transform-only bot/human handoff.
    /// </summary>
    public abstract class NetworkBotSlotCoordinator : MonoBehaviour
    {
        private sealed class RuntimeSlot
        {
            public NetworkBotSlotDefinition Definition;
            public NetworkBotSlotState State;
            public GameObject Actor;
            public int PendingToken;
        }

        [SerializeField] private NetworkBotSlotDefinition[] m_Slots =
            Array.Empty<NetworkBotSlotDefinition>();
        [SerializeField] private bool m_FillVacantSlotsWithBots = true;

        private readonly Dictionary<uint, int> m_HumanSlots = new();
        private RuntimeSlot[] m_RuntimeSlots = Array.Empty<RuntimeSlot>();
        private NetworkBotSlotState[] m_Snapshot = Array.Empty<NetworkBotSlotState>();
        private bool m_WasAuthority;
        private int m_NextReservationToken = 1;

        public int SlotCount => m_RuntimeSlots.Length;
        public bool IsSlotAuthority => IsTransportAuthority;
        public IReadOnlyList<NetworkBotSlotState> CurrentStates => m_Snapshot;

        protected abstract bool IsTransportAuthority { get; }
        protected abstract GameObject SpawnBotActor(
            GameObject prefab,
            Vector3 position,
            Quaternion rotation,
            uint slotIdHash);
        protected abstract bool DespawnBotActor(GameObject actor);
        protected abstract uint ResolveActorNetworkId(GameObject actor);
        protected abstract GameObject ResolveActor(uint actorNetworkId);
        protected abstract void PublishAuthoritativeSnapshot(
            IReadOnlyList<NetworkBotSlotState> states);

        protected virtual void Awake()
        {
            BuildRuntimeSlots();
        }

        protected virtual void OnEnable()
        {
            if (m_RuntimeSlots.Length != (m_Slots?.Length ?? 0)) BuildRuntimeSlots();
            m_WasAuthority = false;
        }

        protected virtual void Update()
        {
            bool isAuthority = IsTransportAuthority;
            if (isAuthority && !m_WasAuthority)
            {
                OnAuthorityGained();
            }
            else if (!isAuthority && m_WasAuthority)
            {
                OnAuthorityLost();
            }

            m_WasAuthority = isAuthority;
        }

        public bool TryReserveSlotForHuman(
            uint humanClientId,
            out NetworkBotSlotReservation reservation)
        {
            reservation = default;
            if (!IsTransportAuthority ||
                !NetworkTransportBridge.IsValidClientId(humanClientId))
            {
                return false;
            }

            // Duplicate join/scene-loaded callbacks never reserve a second slot.
            if (m_HumanSlots.ContainsKey(humanClientId)) return false;

            for (int i = 0; i < m_RuntimeSlots.Length; i++)
            {
                RuntimeSlot slot = m_RuntimeSlots[i];
                if (slot.State.OccupantType != NetworkBotSlotOccupantType.Bot &&
                    slot.State.OccupantType != NetworkBotSlotOccupantType.Vacant)
                {
                    continue;
                }

                Vector3 position;
                Quaternion rotation;
                ResolveSlotPose(slot, out position, out rotation);
                if (slot.Actor != null && !DespawnBotActor(slot.Actor))
                {
                    continue;
                }

                slot.Actor = null;
                int token = NextReservationToken();
                slot.PendingToken = token;
                slot.State.OccupantType = NetworkBotSlotOccupantType.PendingHuman;
                slot.State.HumanClientId = humanClientId;
                slot.State.ActorNetworkId = 0;
                slot.State.Position = position;
                slot.State.Rotation = rotation;
                slot.State.Revision++;
                m_HumanSlots[humanClientId] = i;
                PublishSnapshot();

                reservation = new NetworkBotSlotReservation(
                    token,
                    i,
                    slot.State.SlotIdHash,
                    humanClientId,
                    position,
                    rotation);
                return true;
            }

            // No slot is not an error: callers use their existing ordinary spawn points.
            return false;
        }

        public bool CommitHumanReservation(
            NetworkBotSlotReservation reservation,
            GameObject humanActor)
        {
            if (!TryGetPendingSlot(reservation, out RuntimeSlot slot) || humanActor == null)
            {
                return false;
            }

            uint actorNetworkId = ResolveActorNetworkId(humanActor);
            if (actorNetworkId == 0) return false;

            slot.Actor = humanActor;
            slot.PendingToken = 0;
            slot.State.OccupantType = NetworkBotSlotOccupantType.Human;
            slot.State.ActorNetworkId = actorNetworkId;
            slot.State.Position = humanActor.transform.position;
            slot.State.Rotation = humanActor.transform.rotation;
            slot.State.Revision++;
            PublishSnapshot();
            return true;
        }

        public bool RollbackHumanReservation(NetworkBotSlotReservation reservation)
        {
            if (!TryGetPendingSlot(reservation, out RuntimeSlot slot)) return false;

            m_HumanSlots.Remove(reservation.HumanClientId);
            slot.PendingToken = 0;
            return ReplaceWithFreshBot(slot, reservation.Position, reservation.Rotation);
        }

        public bool ReplaceDisconnectedHumanWithBot(
            uint humanClientId,
            Vector3 lastPosition,
            Quaternion lastRotation)
        {
            if (!IsTransportAuthority ||
                !m_HumanSlots.TryGetValue(humanClientId, out int index) ||
                index < 0 || index >= m_RuntimeSlots.Length)
            {
                return false;
            }

            RuntimeSlot slot = m_RuntimeSlots[index];
            m_HumanSlots.Remove(humanClientId);
            slot.Actor = null;
            slot.PendingToken = 0;
            return ReplaceWithFreshBot(slot, lastPosition, lastRotation);
        }

        public bool TryGetHumanSlot(uint humanClientId, out NetworkBotSlotState state)
        {
            if (m_HumanSlots.TryGetValue(humanClientId, out int index) &&
                index >= 0 && index < m_RuntimeSlots.Length)
            {
                state = m_RuntimeSlots[index].State;
                GameObject actor = m_RuntimeSlots[index].Actor;
                if (actor != null)
                {
                    state.Position = actor.transform.position;
                    state.Rotation = actor.transform.rotation;
                }
                return state.OccupantType == NetworkBotSlotOccupantType.Human ||
                       state.OccupantType == NetworkBotSlotOccupantType.PendingHuman;
            }

            state = default;
            return false;
        }

        /// <summary>Applies a transport-authenticated snapshot on observers or before migration.</summary>
        public void ApplyReplicatedSnapshot(IReadOnlyList<NetworkBotSlotState> states)
        {
            if (states == null || states.Count == 0) return;
            EnsureRuntimeBuilt();

            for (int incomingIndex = 0; incomingIndex < states.Count; incomingIndex++)
            {
                NetworkBotSlotState incoming = states[incomingIndex];
                for (int i = 0; i < m_RuntimeSlots.Length; i++)
                {
                    RuntimeSlot slot = m_RuntimeSlots[i];
                    if (slot.State.SlotIdHash != incoming.SlotIdHash ||
                        incoming.Revision < slot.State.Revision)
                    {
                        continue;
                    }

                    slot.State = incoming;
                    slot.Actor = incoming.ActorNetworkId != 0
                        ? ResolveActor(incoming.ActorNetworkId)
                        : null;
                    slot.PendingToken = 0;
                    break;
                }
            }

            RebuildHumanIndex();
            CopySnapshot();
        }

        protected virtual void OnAuthorityGained()
        {
            EnsureRuntimeBuilt();
            RebuildHumanIndex();

            for (int i = 0; i < m_RuntimeSlots.Length; i++)
            {
                RuntimeSlot slot = m_RuntimeSlots[i];
                if (slot.State.ActorNetworkId != 0)
                {
                    slot.Actor = ResolveActor(slot.State.ActorNetworkId);
                }

                if (slot.State.OccupantType == NetworkBotSlotOccupantType.Human &&
                    slot.Actor != null)
                {
                    continue;
                }

                // An interrupted reservation or missing actor is rolled back to a fresh bot.
                if (NetworkTransportBridge.IsValidClientId(slot.State.HumanClientId))
                {
                    m_HumanSlots.Remove(slot.State.HumanClientId);
                }
                if (m_FillVacantSlotsWithBots)
                {
                    ResolveSlotPose(slot, out Vector3 position, out Quaternion rotation);
                    ReplaceWithFreshBot(slot, position, rotation);
                }
                else
                {
                    SetVacant(slot);
                }
            }

            PublishSnapshot();
        }

        protected virtual void OnAuthorityLost()
        {
            for (int i = 0; i < m_RuntimeSlots.Length; i++)
            {
                m_RuntimeSlots[i].PendingToken = 0;
            }
        }

        private bool ReplaceWithFreshBot(
            RuntimeSlot slot,
            Vector3 position,
            Quaternion rotation)
        {
            GameObject prefab = slot.Definition?.BotPrefab;
            if (prefab == null)
            {
                SetVacant(slot);
                PublishSnapshot();
                return false;
            }

            GameObject bot = SpawnBotActor(
                prefab,
                position,
                rotation,
                slot.State.SlotIdHash);
            if (bot == null)
            {
                SetVacant(slot);
                PublishSnapshot();
                return false;
            }

            slot.Actor = bot;
            slot.State.OccupantType = NetworkBotSlotOccupantType.Bot;
            slot.State.HumanClientId = NetworkTransportBridge.InvalidClientId;
            slot.State.ActorNetworkId = ResolveActorNetworkId(bot);
            slot.State.Position = bot.transform.position;
            slot.State.Rotation = bot.transform.rotation;
            slot.State.Revision++;
            PublishSnapshot();
            return true;
        }

        private void SetVacant(RuntimeSlot slot)
        {
            slot.Actor = null;
            slot.PendingToken = 0;
            slot.State.OccupantType = NetworkBotSlotOccupantType.Vacant;
            slot.State.HumanClientId = NetworkTransportBridge.InvalidClientId;
            slot.State.ActorNetworkId = 0;
            slot.State.Revision++;
        }

        private bool TryGetPendingSlot(
            NetworkBotSlotReservation reservation,
            out RuntimeSlot slot)
        {
            slot = null;
            if (!IsTransportAuthority || !reservation.IsValid ||
                reservation.SlotIndex < 0 ||
                reservation.SlotIndex >= m_RuntimeSlots.Length)
            {
                return false;
            }

            slot = m_RuntimeSlots[reservation.SlotIndex];
            return slot.PendingToken == reservation.Token &&
                slot.State.SlotIdHash == reservation.SlotIdHash &&
                slot.State.HumanClientId == reservation.HumanClientId &&
                slot.State.OccupantType == NetworkBotSlotOccupantType.PendingHuman;
        }

        private void ResolveSlotPose(
            RuntimeSlot slot,
            out Vector3 position,
            out Quaternion rotation)
        {
            if (slot.Actor != null)
            {
                position = slot.Actor.transform.position;
                rotation = slot.Actor.transform.rotation;
                return;
            }

            if (slot.State.Revision > 0)
            {
                position = slot.State.Position;
                rotation = slot.State.Rotation;
                return;
            }

            Transform anchor = slot.Definition?.Anchor;
            position = anchor != null ? anchor.position : transform.position;
            rotation = anchor != null ? anchor.rotation : transform.rotation;
        }

        private void BuildRuntimeSlots()
        {
            int count = m_Slots?.Length ?? 0;
            m_RuntimeSlots = new RuntimeSlot[count];
            m_Snapshot = new NetworkBotSlotState[count];
            var seenIds = new HashSet<uint>();

            for (int i = 0; i < count; i++)
            {
                NetworkBotSlotDefinition definition = m_Slots[i];
                uint slotId = definition?.SlotIdHash ?? 0;
                if (slotId == 0 || !seenIds.Add(slotId))
                {
                    Debug.LogError(
                        $"[NetworkBotSlotCoordinator] Slot {i} on '{name}' has a missing or " +
                        "duplicate stable Slot ID.",
                        this);
                }

                Transform anchor = definition?.Anchor;
                m_RuntimeSlots[i] = new RuntimeSlot
                {
                    Definition = definition,
                    State = new NetworkBotSlotState
                    {
                        SlotIdHash = slotId,
                        OccupantType = NetworkBotSlotOccupantType.Vacant,
                        HumanClientId = NetworkTransportBridge.InvalidClientId,
                        Position = anchor != null ? anchor.position : transform.position,
                        Rotation = anchor != null ? anchor.rotation : transform.rotation
                    }
                };
            }

            m_HumanSlots.Clear();
            CopySnapshot();
        }

        private void EnsureRuntimeBuilt()
        {
            if (m_RuntimeSlots.Length != (m_Slots?.Length ?? 0)) BuildRuntimeSlots();
        }

        private void RebuildHumanIndex()
        {
            m_HumanSlots.Clear();
            for (int i = 0; i < m_RuntimeSlots.Length; i++)
            {
                NetworkBotSlotState state = m_RuntimeSlots[i].State;
                if ((state.OccupantType == NetworkBotSlotOccupantType.Human ||
                     state.OccupantType == NetworkBotSlotOccupantType.PendingHuman) &&
                    NetworkTransportBridge.IsValidClientId(state.HumanClientId))
                {
                    m_HumanSlots[state.HumanClientId] = i;
                }
            }
        }

        private int NextReservationToken()
        {
            int token = m_NextReservationToken++;
            if (token == 0) token = m_NextReservationToken++;
            return token;
        }

        private void PublishSnapshot()
        {
            CopySnapshot();
            if (IsTransportAuthority)
            {
                PublishAuthoritativeSnapshot(m_Snapshot);
            }
        }

        private void CopySnapshot()
        {
            if (m_Snapshot.Length != m_RuntimeSlots.Length)
            {
                m_Snapshot = new NetworkBotSlotState[m_RuntimeSlots.Length];
            }

            for (int i = 0; i < m_RuntimeSlots.Length; i++)
            {
                m_Snapshot[i] = m_RuntimeSlots[i].State;
            }
        }
    }
}
