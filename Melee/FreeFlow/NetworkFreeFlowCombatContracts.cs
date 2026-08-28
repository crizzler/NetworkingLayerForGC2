using System;
using System.Collections.Generic;
using GameCreator.Runtime.Melee;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Melee
{
    /// <summary>
    /// Result returned to the optional Free Flow Combat runtime before it performs automatic
    /// enemy setup. The explicit network actor classification, never GC2's local IsPlayer flag,
    /// decides which branch is legal.
    /// </summary>
    public enum NetworkFreeFlowEnemySetupMode : byte
    {
        Unmanaged = 0,
        AuthoritativeNpc = 1,
        ObserverNpc = 2,
        PlayerOwned = 3
    }

    public enum NetworkFreeFlowCounterPreparation : byte
    {
        Unmanaged = 0,
        Prepared = 1,
        Rejected = 2
    }

    [Flags]
    public enum NetworkFreeFlowSkillUse : byte
    {
        None = 0,
        TargetedAttack = 1 << 0,
        Fallback = 1 << 1,
        Counter = 1 << 2
    }

    [Flags]
    public enum NetworkMeleeSkillActionFlags : byte
    {
        None = 0,
        FreeFlowCounter = 1 << 0
    }

    /// <summary>
    /// Presentation-relevant Free Flow state owned by gameplay authority. Director queues,
    /// random choices, and AI cooldown dictionaries intentionally remain server-only.
    /// </summary>
    [Serializable]
    public struct NetworkFreeFlowCombatState
    {
        public const byte AttackableFlag = 1 << 0;
        public const byte CounterableFlag = 1 << 1;
        public const byte AttackTokenFlag = 1 << 2;

        public uint CharacterNetworkId;
        public uint StateVersion;
        public uint TargetNetworkId;
        public byte Flags;
        public float ScoreBonus;

        public bool IsAttackable => (Flags & AttackableFlag) != 0;
        public bool IsCounterable => (Flags & CounterableFlag) != 0;
        public bool HasAttackToken => (Flags & AttackTokenFlag) != 0;

        public static NetworkFreeFlowCombatState Create(uint characterNetworkId)
        {
            return new NetworkFreeFlowCombatState
            {
                CharacterNetworkId = characterNetworkId,
                StateVersion = 0,
                TargetNetworkId = 0,
                Flags = 0,
                ScoreBonus = 0f
            };
        }
    }

    public static class NetworkFreeFlowStateVersion
    {
        public static uint Next(uint value) => value == uint.MaxValue ? 1u : Math.Max(1u, value + 1u);

        public static bool IsNewer(uint candidate, uint baseline)
        {
            if (candidate == 0 || candidate == baseline) return false;
            if (baseline == 0) return true;
            return unchecked((int)(candidate - baseline)) > 0;
        }
    }

    /// <summary>
    /// Optional, inverted dependency implemented by Free Flow Combat in Assembly-CSharp when
    /// ARAWN_GC2_TRANSPORT_INTEGRATION is enabled. The networking assemblies never reference
    /// Free Flow's concrete runtime types, preserving installation without that asset.
    /// </summary>
    public interface INetworkFreeFlowCombatReceiver
    {
        bool IsNetworkFreeFlowWeapon(MeleeWeapon weapon);

        /// <summary>
        /// Reports two deliberately separate views of the authored weapon. <paramref name="registerUse"/>
        /// receives every Skill reachable through valid Combo and Free Flow paths for semantic
        /// classification. <paramref name="registerDirectPlay"/> receives only Skills referenced by
        /// instructions that genuinely start a Skill without a Combo node.
        /// </summary>
        void CollectNetworkSkills(
            MeleeWeapon weapon,
            Action<Skill, NetworkFreeFlowSkillUse> registerUse,
            Action<Skill> registerDirectPlay);

        void GetNetworkSettings(
            MeleeWeapon weapon,
            out float attackRadius,
            out float scanRadius,
            out float counterRadius,
            out float counterCooldown,
            out bool requireAttackLineOfSight,
            out bool requireCounterLineOfSight,
            out int occlusionLayerMask);

        bool TryCaptureNetworkState(
            out bool isAttackable,
            out bool isCounterable,
            out bool hasAttackToken,
            out float scoreBonus);

        void ApplyReplicatedNetworkState(
            bool isAttackable,
            bool isCounterable,
            bool hasAttackToken,
            float scoreBonus,
            GameObject selectedPlayer);

        bool TryConsumeNetworkCounterWindow();
        Vector3 GetNetworkAimPoint();
        void SetNetworkSelectedPlayer(GameObject selectedPlayer);
        void SetNetworkSimulation(bool runLocalPlayerRuntime, bool runAuthoritativeNpcSimulation);
    }
}
