using System.Collections.Generic;
using GameCreator.Runtime.Characters;
using UnityEngine;

namespace Arawn.GameCreator2.Networking
{
    public partial class NetworkCoreController
    {
        private const int MaxPendingCoreCharacters = 128;
        private const int MaxPendingPropBroadcastsPerCharacter = 64;

        private readonly Dictionary<uint, NetworkCoreSnapshot> m_PendingCoreSnapshots = new(16);
        private readonly Dictionary<uint, List<NetworkPropBroadcast>> m_PendingPropBroadcasts = new(16);
        private readonly Dictionary<uint, NetworkRagdollBroadcast> m_PendingRagdollBroadcasts = new(16);
        private readonly Dictionary<uint, NetworkInvincibilityBroadcast> m_PendingInvincibilityBroadcasts = new(16);
        private readonly Dictionary<uint, NetworkPoiseBroadcast> m_PendingPoiseBroadcasts = new(16);
        private readonly Dictionary<uint, NetworkBusyBroadcast> m_PendingBusyBroadcasts = new(16);
        private readonly List<uint> m_PendingCoreRemoveBuffer = new(16);

        /// <summary>Build the persistent Core state for one authoritative character.</summary>
        public bool TryCaptureCoreSnapshot(uint characterNetworkId, out NetworkCoreSnapshot snapshot)
        {
            snapshot = default;
            Character character = GetCharacterByNetworkId?.Invoke(characterNetworkId);
            if (character == null) return false;

            float serverTime = GetServerTime?.Invoke() ?? Time.time;
            float invincibilityRemaining = 0f;
            if (character.Combat.Invincibility.IsInvincible)
            {
                float elapsed = Mathf.Max(0f,
                    character.Time.Time - character.Combat.Invincibility.StartTime);
                invincibilityRemaining = Mathf.Max(0f,
                    character.Combat.Invincibility.Duration - elapsed);
            }

            BusyLimbs busyLimbs = BusyLimbs.None;
            if (character.Busy.IsArmLeftBusy) busyLimbs |= BusyLimbs.ArmLeft;
            if (character.Busy.IsArmRightBusy) busyLimbs |= BusyLimbs.ArmRight;
            if (character.Busy.IsLegLeftBusy) busyLimbs |= BusyLimbs.LegLeft;
            if (character.Busy.IsLegRightBusy) busyLimbs |= BusyLimbs.LegRight;

            var state = new NetworkCoreState
            {
                CharacterNetworkId = characterNetworkId,
                DeltaFlags = CoreStateDeltaFlags.All,
                IsRagdoll = character.Ragdoll.IsRagdoll,
                IsInvincible = invincibilityRemaining > 0f,
                InvincibilityEndTime = serverTime + invincibilityRemaining,
                CurrentPoise = character.Combat.Poise.Current,
                MaximumPoise = character.Combat.Poise.Maximum,
                IsPoiseBroken = character.Combat.Poise.IsBroken,
                BusyLimbs = busyLimbs,
                ServerTime = serverTime
            };

            NetworkPropAttachmentState[] props = System.Array.Empty<NetworkPropAttachmentState>();
            if (m_CharacterProps.TryGetValue(characterNetworkId,
                    out List<NetworkPropAttachmentState> tracked) && tracked.Count > 0)
            {
                props = tracked.ToArray();
            }

            snapshot = new NetworkCoreSnapshot
            {
                State = state,
                Props = props
            };
            return true;
        }

        /// <summary>
        /// Apply a full-replacement snapshot. If the character has not spawned yet, the latest
        /// snapshot is retained and retried from Update.
        /// </summary>
        public bool ReceiveCoreSnapshot(NetworkCoreSnapshot snapshot)
        {
            if (m_IsServer) return false;
            uint characterNetworkId = snapshot.CharacterNetworkId;
            if (characterNetworkId == 0) return false;

            Character character = GetCharacterByNetworkId?.Invoke(characterNetworkId);
            if (character == null)
            {
                CachePendingCoreSnapshot(snapshot);
                return false;
            }

            // Reliable ordering guarantees that any persistent broadcasts already queued for
            // this character predate this full-replacement snapshot. Do this on the direct-apply
            // path too; the character may have spawned after those broadcasts were cached.
            DiscardPendingPersistentBroadcasts(characterNetworkId);
            ApplyCoreState(character, snapshot.State);
            ReconcileSnapshotProps(character, characterNetworkId, snapshot.Props);
            m_PendingCoreSnapshots.Remove(characterNetworkId);
            OnCoreSnapshotReceived?.Invoke(snapshot);
            return true;
        }

        private void ApplyCoreState(Character character, NetworkCoreState state)
        {
            if (state.IsRagdoll != character.Ragdoll.IsRagdoll)
            {
                if (state.IsRagdoll)
                {
                    _ = character.Ragdoll.StartRagdoll();
                }
                else
                {
                    _ = character.Ragdoll.StartRecover();
                }
            }

            float now = GetServerTime?.Invoke() ?? Time.time;
            float remainingInvincibility = Mathf.Max(0f, state.InvincibilityEndTime - now);
            if (state.IsInvincible && remainingInvincibility > 0f)
            {
                ApplyNetworkInvincibility(character, true, remainingInvincibility);
            }
            else
            {
                ApplyNetworkInvincibility(character, false, 0f);
            }

            ApplyNetworkPoise(
                character.Combat.Poise,
                state.CurrentPoise,
                state.MaximumPoise);

            GameCreator.Runtime.Characters.Busy.Limb gc2Limbs =
                GameCreator.Runtime.Characters.Busy.Limb.None;
            if ((state.BusyLimbs & BusyLimbs.ArmLeft) != 0)
                gc2Limbs |= GameCreator.Runtime.Characters.Busy.Limb.ArmLeft;
            if ((state.BusyLimbs & BusyLimbs.ArmRight) != 0)
                gc2Limbs |= GameCreator.Runtime.Characters.Busy.Limb.ArmRight;
            if ((state.BusyLimbs & BusyLimbs.LegLeft) != 0)
                gc2Limbs |= GameCreator.Runtime.Characters.Busy.Limb.LegLeft;
            if ((state.BusyLimbs & BusyLimbs.LegRight) != 0)
                gc2Limbs |= GameCreator.Runtime.Characters.Busy.Limb.LegRight;

            character.Busy.RemoveState(GameCreator.Runtime.Characters.Busy.Limb.Every);
            if (gc2Limbs != GameCreator.Runtime.Characters.Busy.Limb.None)
            {
                character.Busy.AddState(gc2Limbs);
            }
        }

        private static void ApplyNetworkPoise(Poise poise, float current, float maximum)
        {
            if (poise == null) return;
            float safeMaximum = IsFinite(maximum) ? Mathf.Max(0f, maximum) : poise.Maximum;
            float safeCurrent = IsFinite(current) ? current : poise.Current;
            if (!Mathf.Approximately(poise.Maximum, safeMaximum))
            {
                poise.Reset(safeMaximum);
            }

            poise.Set(safeCurrent);
        }

        private void ReconcileSnapshotProps(Character character, uint characterNetworkId,
            NetworkPropAttachmentState[] desiredProps)
        {
            if (character == null || characterNetworkId == 0) return;

            List<NetworkPropAttachmentState> current = GetOrCreatePropStates(characterNetworkId);
            desiredProps ??= System.Array.Empty<NetworkPropAttachmentState>();

            // A controller/session replacement can leave a tracker in the character hierarchy
            // after its in-memory descriptor was lost. Full replacement owns the entire set, so
            // remove those orphans before reconciling the tracked records.
            NetworkPropTracker[] trackers =
                character.GetComponentsInChildren<NetworkPropTracker>(true);
            for (int i = trackers.Length - 1; i >= 0; i--)
            {
                NetworkPropTracker tracker = trackers[i];
                if (tracker == null || tracker.CharacterNetworkId != characterNetworkId) continue;
                if (FindPropIndexByInstance(current, tracker.InstanceId) >= 0) continue;

                RemoveLocalProp(character, new NetworkPropAttachmentState
                {
                    CharacterNetworkId = characterNetworkId,
                    PropInstanceId = tracker.InstanceId,
                    PropHash = tracker.PropHash
                });
            }

            for (int i = current.Count - 1; i >= 0; i--)
            {
                NetworkPropAttachmentState existing = current[i];
                int desiredIndex = FindStateIndex(desiredProps, existing.PropInstanceId);
                if (desiredIndex >= 0 && AttachmentEquals(existing, desiredProps[desiredIndex])) continue;

                RemoveLocalProp(character, existing);
                current.RemoveAt(i);
            }

            for (int i = 0; i < desiredProps.Length; i++)
            {
                NetworkPropAttachmentState desired = desiredProps[i];
                if (desired.CharacterNetworkId != characterNetworkId || desired.PropInstanceId <= 0) continue;
                if (FindPropIndexByInstance(current, desired.PropInstanceId) >= 0) continue;

                if (ApplyPropAttach(character, desired)) current.Add(desired);
            }
        }

        private static int FindStateIndex(NetworkPropAttachmentState[] states, int instanceId)
        {
            for (int i = 0; i < states.Length; i++)
            {
                if (states[i].PropInstanceId == instanceId) return i;
            }

            return -1;
        }

        private static bool AttachmentEquals(NetworkPropAttachmentState a,
            NetworkPropAttachmentState b)
        {
            return a.CharacterNetworkId == b.CharacterNetworkId &&
                   a.PropInstanceId == b.PropInstanceId &&
                   a.PropHash == b.PropHash &&
                   a.BoneHash == b.BoneHash &&
                   a.LocalPosition == b.LocalPosition &&
                   a.RotationX == b.RotationX &&
                   a.RotationY == b.RotationY &&
                   a.RotationZ == b.RotationZ;
        }

        private void CachePendingCoreSnapshot(NetworkCoreSnapshot snapshot)
        {
            if (m_PendingCoreSnapshots.Count >= MaxPendingCoreCharacters &&
                !m_PendingCoreSnapshots.ContainsKey(snapshot.CharacterNetworkId))
            {
                uint keyToRemove = 0;
                foreach (uint key in m_PendingCoreSnapshots.Keys)
                {
                    keyToRemove = key;
                    break;
                }
                if (keyToRemove != 0) m_PendingCoreSnapshots.Remove(keyToRemove);
            }

            // A reliable ordered snapshot supersedes persistent broadcasts that arrived before it.
            DiscardPendingPersistentBroadcasts(snapshot.CharacterNetworkId);
            m_PendingCoreSnapshots[snapshot.CharacterNetworkId] = snapshot;
        }

        private void DiscardPendingPersistentBroadcasts(uint characterNetworkId)
        {
            m_PendingPropBroadcasts.Remove(characterNetworkId);
            m_PendingRagdollBroadcasts.Remove(characterNetworkId);
            m_PendingInvincibilityBroadcasts.Remove(characterNetworkId);
            m_PendingPoiseBroadcasts.Remove(characterNetworkId);
            m_PendingBusyBroadcasts.Remove(characterNetworkId);
        }

        private static void CacheLatestPersistentBroadcast<T>(
            Dictionary<uint, T> pending,
            uint characterNetworkId,
            T broadcast)
        {
            if (characterNetworkId == 0) return;
            if (!pending.ContainsKey(characterNetworkId) &&
                pending.Count >= MaxPendingCoreCharacters)
            {
                uint oldestKey = 0;
                foreach (uint key in pending.Keys)
                {
                    oldestKey = key;
                    break;
                }

                if (oldestKey != 0) pending.Remove(oldestKey);
            }

            pending[characterNetworkId] = broadcast;
        }

        private void CachePendingRagdollBroadcast(NetworkRagdollBroadcast broadcast) =>
            CacheLatestPersistentBroadcast(
                m_PendingRagdollBroadcasts,
                broadcast.CharacterNetworkId,
                broadcast);

        private void CachePendingInvincibilityBroadcast(NetworkInvincibilityBroadcast broadcast) =>
            CacheLatestPersistentBroadcast(
                m_PendingInvincibilityBroadcasts,
                broadcast.CharacterNetworkId,
                broadcast);

        private void CachePendingPoiseBroadcast(NetworkPoiseBroadcast broadcast) =>
            CacheLatestPersistentBroadcast(
                m_PendingPoiseBroadcasts,
                broadcast.CharacterNetworkId,
                broadcast);

        private void CachePendingBusyBroadcast(NetworkBusyBroadcast broadcast) =>
            CacheLatestPersistentBroadcast(
                m_PendingBusyBroadcasts,
                broadcast.CharacterNetworkId,
                broadcast);

        private void CachePendingPropBroadcast(NetworkPropBroadcast broadcast)
        {
            if (!m_PendingPropBroadcasts.TryGetValue(broadcast.CharacterNetworkId,
                    out List<NetworkPropBroadcast> pending))
            {
                if (m_PendingPropBroadcasts.Count >= MaxPendingCoreCharacters) return;
                pending = new List<NetworkPropBroadcast>();
                m_PendingPropBroadcasts.Add(broadcast.CharacterNetworkId, pending);
            }

            if (pending.Count >= MaxPendingPropBroadcastsPerCharacter)
            {
                pending.RemoveAt(0);
            }
            pending.Add(broadcast);
        }

        private void RetryPendingCoreState()
        {
            if (!m_IsClient) return;

            m_PendingCoreRemoveBuffer.Clear();
            foreach (KeyValuePair<uint, NetworkCoreSnapshot> pair in m_PendingCoreSnapshots)
            {
                Character character = GetCharacterByNetworkId?.Invoke(pair.Key);
                if (character == null) continue;

                ApplyCoreState(character, pair.Value.State);
                ReconcileSnapshotProps(character, pair.Key, pair.Value.Props);
                OnCoreSnapshotReceived?.Invoke(pair.Value);
                m_PendingCoreRemoveBuffer.Add(pair.Key);
            }

            for (int i = 0; i < m_PendingCoreRemoveBuffer.Count; i++)
                m_PendingCoreSnapshots.Remove(m_PendingCoreRemoveBuffer[i]);

            m_PendingCoreRemoveBuffer.Clear();
            foreach (KeyValuePair<uint, List<NetworkPropBroadcast>> pair in m_PendingPropBroadcasts)
            {
                if (GetCharacterByNetworkId?.Invoke(pair.Key) == null) continue;

                List<NetworkPropBroadcast> pending = pair.Value;
                for (int i = 0; i < pending.Count; i++) ReceivePropBroadcast(pending[i]);
                m_PendingCoreRemoveBuffer.Add(pair.Key);
            }

            for (int i = 0; i < m_PendingCoreRemoveBuffer.Count; i++)
                m_PendingPropBroadcasts.Remove(m_PendingCoreRemoveBuffer[i]);

            FlushLatestPersistentBroadcasts(
                m_PendingRagdollBroadcasts,
                ReceiveRagdollBroadcast);
            FlushLatestPersistentBroadcasts(
                m_PendingInvincibilityBroadcasts,
                ReceiveInvincibilityBroadcast);
            FlushLatestPersistentBroadcasts(
                m_PendingPoiseBroadcasts,
                ReceivePoiseBroadcast);
            FlushLatestPersistentBroadcasts(
                m_PendingBusyBroadcasts,
                ReceiveBusyBroadcast);
        }

        private void FlushLatestPersistentBroadcasts<T>(
            Dictionary<uint, T> pending,
            System.Action<T> apply)
        {
            m_PendingCoreRemoveBuffer.Clear();
            foreach (KeyValuePair<uint, T> pair in pending)
            {
                if (GetCharacterByNetworkId?.Invoke(pair.Key) == null) continue;
                apply.Invoke(pair.Value);
                m_PendingCoreRemoveBuffer.Add(pair.Key);
            }

            for (int i = 0; i < m_PendingCoreRemoveBuffer.Count; i++)
            {
                pending.Remove(m_PendingCoreRemoveBuffer[i]);
            }
        }
    }
}
