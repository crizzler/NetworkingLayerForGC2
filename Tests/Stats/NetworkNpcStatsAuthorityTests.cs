#if GC2_STATS
using System.Collections.Generic;
using System.Reflection;
using Arawn.GameCreator2.Networking.TestUtilities;
using GameCreator.Runtime.Characters;
using NUnit.Framework;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Stats.Tests
{
    public sealed class NetworkNpcStatsAuthorityTests
    {
        private readonly List<GameObject> m_Cleanup = new();

        [TearDown]
        public void TearDown()
        {
            for (int i = m_Cleanup.Count - 1; i >= 0; i--)
            {
                if (m_Cleanup[i] != null) Object.DestroyImmediate(m_Cleanup[i]);
            }
            m_Cleanup.Clear();
        }

        [Test]
        public void ClientDeterministicNpc_RejectsDurableStatsBeforeMutation()
        {
            NetworkStatsController stats = CreateNpcStatsController(
                NetworkCharacter.NPCSyncMode.ClientSideDeterministic);
            int rejections = 0;
            StatRejectionReason reason = StatRejectionReason.None;
            stats.OnModificationRejected += (rejection, _) =>
            {
                rejections++;
                reason = rejection;
            };

            stats.RequestStatModify(default, StatModificationType.SetBase, 10f);

            Assert.That(stats.CanUseDurableGameplayState, Is.False);
            Assert.That(rejections, Is.EqualTo(1));
            Assert.That(reason, Is.EqualTo(StatRejectionReason.NotAuthorized));
        }

        [Test]
        public void ServerAuthoritativeNpc_RemainsEligibleForDurableStats()
        {
            NetworkStatsController stats = CreateNpcStatsController(
                NetworkCharacter.NPCSyncMode.ServerAuthoritative);

            Assert.That(stats.CanUseDurableGameplayState, Is.True);
        }

        private NetworkStatsController CreateNpcStatsController(
            NetworkCharacter.NPCSyncMode syncMode)
        {
            var root = new GameObject($"Stats NPC {syncMode}");
            m_Cleanup.Add(root);
            EditModeLifecycle.AddComponent<Character>(root);

            NetworkCharacter networkCharacter = root.AddComponent<NetworkCharacter>();
            SetField(networkCharacter, "m_ActorType", NetworkCharacterActorType.NPC);
            SetField(networkCharacter, "m_NPCMode", syncMode);
            EditModeLifecycle.Invoke(networkCharacter, "Awake");

            NetworkStatsController stats = root.AddComponent<NetworkStatsController>();
            EditModeLifecycle.Invoke(stats, "Awake");
            return stats;
        }

        private static void SetField(object target, string name, object value)
        {
            FieldInfo field = target.GetType().GetField(
                name,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, $"Missing field {name}");
            field.SetValue(target, value);
        }
    }
}
#endif
