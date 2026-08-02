#if GC2_ABILITIES
using System;
using System.Collections.Generic;
using DaimahouGames.Runtime.Abilities;
using DaimahouGames.Runtime.Pawns;
using UnityEngine;

namespace Arawn.GameCreator2.Networking
{
    /// <summary>
    /// Complete, transport-neutral state for one registered ability caster.
    /// </summary>
    [Serializable]
    public struct NetworkAbilityCharacterSnapshot
    {
        public NetworkAbilityStateResponse State;
        public NetworkAbilitySlotEntry[] Slots;
        public NetworkCooldownEntry[] Cooldowns;
        public NetworkAbilityCastBroadcast[] ActiveCasts;
    }

    /// <summary>
    /// Complete ability state used for late join and authority promotion.
    /// </summary>
    [Serializable]
    public struct NetworkAbilitiesFullSnapshot
    {
        public float ServerTime;
        public NetworkAbilityCharacterSnapshot[] Characters;
    }

    public partial class NetworkAbilitiesController
    {
        private const int SnapshotAbilitySlotCount = 16;
        private readonly Dictionary<uint, NetworkAbilityCastBroadcast> m_ReplicatedActiveCasts = new(32);

        /// <summary>
        /// Captures ability slots, active cooldowns, and in-progress cast presentation for
        /// the supplied registered character IDs. This method is transport agnostic.
        /// </summary>
        public NetworkAbilitiesFullSnapshot CaptureFullSnapshot(
            IReadOnlyCollection<uint> characterNetworkIds)
        {
            float serverTime = GetServerTime?.Invoke() ?? Time.time;
            var characters = new List<NetworkAbilityCharacterSnapshot>(
                characterNetworkIds?.Count ?? 0);

            if (characterNetworkIds != null)
            {
                foreach (uint characterNetworkId in characterNetworkIds)
                {
                    if (characterNetworkId == 0) continue;
                    Pawn pawn = GetPawnByNetworkId?.Invoke(characterNetworkId);
                    Caster caster = pawn != null ? pawn.GetFeature<Caster>() : null;
                    if (caster == null) continue;

                    var slots = new NetworkAbilitySlotEntry[SnapshotAbilitySlotCount];
                    for (int slot = 0; slot < slots.Length; slot++)
                    {
                        Ability ability = caster.GetSlottedAbility(slot);
                        slots[slot] = new NetworkAbilitySlotEntry
                        {
                            SlotIndex = (byte)slot,
                            AbilityHash = ability != null ? ability.ID.Hash : 0
                        };
                    }

                    var cooldowns = new List<NetworkCooldownEntry>();
                    foreach (KeyValuePair<(uint, int), CooldownData> pair in m_Cooldowns)
                    {
                        if (pair.Key.Item1 != characterNetworkId ||
                            pair.Value.EndTime <= serverTime)
                        {
                            continue;
                        }

                        cooldowns.Add(new NetworkCooldownEntry
                        {
                            AbilityHash = pair.Key.Item2,
                            EndTime = pair.Value.EndTime,
                            TotalDuration = pair.Value.TotalDuration
                        });
                    }

                    var casts = new List<NetworkAbilityCastBroadcast>();
                    if (m_IsServer)
                    {
                        foreach (KeyValuePair<uint, ActiveCast> pair in m_ActiveCasts)
                        {
                            ActiveCast cast = pair.Value;
                            if (cast == null ||
                                cast.CasterNetworkId != characterNetworkId ||
                                (cast.State != AbilityCastState.Started &&
                                 cast.State != AbilityCastState.Triggered))
                            {
                                continue;
                            }

                            casts.Add(new NetworkAbilityCastBroadcast
                            {
                                CasterNetworkId = cast.CasterNetworkId,
                                CastInstanceId = cast.CastInstanceId,
                                AbilityIdHash = cast.AbilityIdHash,
                                ServerTime = cast.StartTime,
                                TargetType = cast.TargetType,
                                TargetPosition = cast.TargetPosition,
                                TargetNetworkId = cast.TargetNetworkId,
                                CastState = cast.State
                            });
                        }
                    }
                    else
                    {
                        foreach (NetworkAbilityCastBroadcast cast in m_ReplicatedActiveCasts.Values)
                        {
                            if (cast.CasterNetworkId == characterNetworkId) casts.Add(cast);
                        }
                    }

                    NetworkAbilityCastBroadcast currentCast =
                        casts.Count > 0 ? casts[0] : default;
                    characters.Add(new NetworkAbilityCharacterSnapshot
                    {
                        State = new NetworkAbilityStateResponse
                        {
                            CharacterNetworkId = characterNetworkId,
                            SlotCount = (byte)Mathf.Min(byte.MaxValue, slots.Length),
                            CooldownCount = (byte)Mathf.Min(byte.MaxValue, cooldowns.Count),
                            IsCasting = casts.Count > 0,
                            CurrentCastId = currentCast.CastInstanceId,
                            CurrentCastAbilityHash = currentCast.AbilityIdHash
                        },
                        Slots = slots,
                        Cooldowns = cooldowns.ToArray(),
                        ActiveCasts = casts.ToArray()
                    });
                }
            }

            return new NetworkAbilitiesFullSnapshot
            {
                ServerTime = serverTime,
                Characters = characters.ToArray()
            };
        }

        /// <summary>
        /// Applies a complete snapshot to a client replica or to a newly promoted logical
        /// authority. In-progress casts are retained as replicated presentation state; they
        /// are not restarted as authoritative execution tasks during promotion.
        /// </summary>
        public void ApplyFullSnapshot(NetworkAbilitiesFullSnapshot snapshot)
        {
            NetworkAbilityCharacterSnapshot[] characters = snapshot.Characters;
            if (characters == null) return;

            for (int i = 0; i < characters.Length; i++)
            {
                NetworkAbilityCharacterSnapshot character = characters[i];
                uint characterNetworkId = character.State.CharacterNetworkId;
                if (characterNetworkId == 0) continue;

                Pawn pawn = GetPawnByNetworkId?.Invoke(characterNetworkId);
                Caster caster = pawn != null ? pawn.GetFeature<Caster>() : null;
                if (caster == null) continue;

                NetworkAbilitySlotEntry[] slots = character.Slots;
                if (slots != null)
                {
                    for (int slotIndex = 0; slotIndex < slots.Length; slotIndex++)
                    {
                        NetworkAbilitySlotEntry slot = slots[slotIndex];
                        if (slot.AbilityHash == 0)
                        {
                            if (caster.GetSlottedAbility(slot.SlotIndex) != null)
                            {
                                caster.UnLearn(slot.SlotIndex);
                            }
                            continue;
                        }

                        Ability ability = GetAbilityByHash?.Invoke(slot.AbilityHash);
                        if (ability != null) caster.Learn(ability, slot.SlotIndex);
                    }
                }

                RemoveCooldownsForCharacter(characterNetworkId);
                NetworkCooldownEntry[] cooldowns = character.Cooldowns;
                if (cooldowns != null)
                {
                    for (int cooldownIndex = 0; cooldownIndex < cooldowns.Length; cooldownIndex++)
                    {
                        NetworkCooldownEntry cooldown = cooldowns[cooldownIndex];
                        if (cooldown.AbilityHash == 0 ||
                            cooldown.EndTime <= (GetServerTime?.Invoke() ?? Time.time))
                        {
                            continue;
                        }

                        m_Cooldowns[(characterNetworkId, cooldown.AbilityHash)] = new CooldownData
                        {
                            EndTime = cooldown.EndTime,
                            TotalDuration = cooldown.TotalDuration
                        };

                        if (m_IsClient)
                        {
                            Cooldowns localCooldowns = caster.Get<Cooldowns>();
                            Ability ability = GetAbilityByHash?.Invoke(cooldown.AbilityHash);
                            float remaining = cooldown.EndTime -
                                              (GetServerTime?.Invoke() ?? Time.time);
                            if (localCooldowns != null && ability != null && remaining > 0f)
                            {
                                localCooldowns.AddCooldown(ability.ID, remaining);
                            }
                        }
                    }
                }

                RemoveReplicatedCastsForCharacter(characterNetworkId);
                NetworkAbilityCastBroadcast[] activeCasts = character.ActiveCasts;
                if (activeCasts != null)
                {
                    for (int castIndex = 0; castIndex < activeCasts.Length; castIndex++)
                    {
                        NetworkAbilityCastBroadcast cast = activeCasts[castIndex];
                        if (cast.CastInstanceId == 0) continue;

                        if (m_IsClient)
                        {
                            NetworkAbilityCastBroadcast replay = cast;
                            replay.CastState = AbilityCastState.Started;
                            ReceiveCastBroadcast(replay);
                        }

                        // Visual replay intentionally uses Started, but the replicated cache must
                        // retain the authority's exact phase for a later Shared-master promotion.
                        m_ReplicatedActiveCasts[cast.CastInstanceId] = cast;
                    }
                }

                if (m_IsServer)
                {
                    m_CasterStates[characterNetworkId] = new CasterState
                    {
                        NetworkId = characterNetworkId,
                        Pawn = pawn,
                        Caster = caster
                    };
                }
            }
        }

        private void RemoveCooldownsForCharacter(uint characterNetworkId)
        {
            var remove = new List<(uint, int)>();
            foreach ((uint, int) key in m_Cooldowns.Keys)
            {
                if (key.Item1 == characterNetworkId) remove.Add(key);
            }
            for (int i = 0; i < remove.Count; i++) m_Cooldowns.Remove(remove[i]);
        }

        private void RemoveReplicatedCastsForCharacter(uint characterNetworkId)
        {
            var remove = new List<uint>();
            foreach (KeyValuePair<uint, NetworkAbilityCastBroadcast> pair in m_ReplicatedActiveCasts)
            {
                if (pair.Value.CasterNetworkId == characterNetworkId) remove.Add(pair.Key);
            }
            for (int i = 0; i < remove.Count; i++) m_ReplicatedActiveCasts.Remove(remove[i]);
        }
    }
}
#endif
