#if GC2_MELEE
using NUnit.Framework;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Melee.Transport.Fusion.Tests
{
    [NUnit.Framework.Category("GC2Networking.FreeFlow")]
    public sealed class FusionMeleeCodecTests
    {
        [Test]
        public void HitRequest_RoundTrips()
        {
            var expected = new NetworkMeleeHitRequest
            {
                RequestId = 42,
                ActorNetworkId = 7,
                CorrelationId = 91,
                AttackerNetworkId = 7,
                TargetNetworkId = 8,
                HitPoint = new Vector3(1.25f, -2f, 3.5f)
            };

            byte[] payload = FusionValueCodec.Encode(
                expected,
                (writer, value) => writer.Write(value));

            Assert.That(
                FusionValueCodec.TryDecode(
                    payload,
                    (FusionValueReader reader, ref NetworkMeleeHitRequest value) =>
                        reader.Read(ref value),
                    out NetworkMeleeHitRequest actual),
                Is.True);
            Assert.That(actual.RequestId, Is.EqualTo(expected.RequestId));
            Assert.That(actual.ActorNetworkId, Is.EqualTo(expected.ActorNetworkId));
            Assert.That(actual.HitPoint, Is.EqualTo(expected.HitPoint));
        }

        [Test]
        public void HitRequest_RejectsTruncatedPayload()
        {
            Assert.That(
                FusionValueCodec.TryDecode(
                    new byte[] { 1 },
                    (FusionValueReader reader, ref NetworkMeleeHitRequest value) =>
                        reader.Read(ref value),
                    out _),
                Is.False);
        }

        [Test]
        public void BlockRequest_UsesStableLittleEndianFieldOrder()
        {
            var value = new NetworkBlockRequest
            {
                RequestId = 0x1234,
                ActorNetworkId = 0x01020304,
                CorrelationId = 0x11223344,
                ClientTimestamp = 1f,
                Action = NetworkBlockAction.Lower,
                ShieldHash = 0x55667788
            };

            byte[] payload = FusionValueCodec.Encode(
                value,
                (writer, request) => writer.Write(request));

            CollectionAssert.AreEqual(
                new byte[]
                {
                    0x34, 0x12,
                    0x04, 0x03, 0x02, 0x01,
                    0x44, 0x33, 0x22, 0x11,
                    0x00, 0x00, 0x80, 0x3F,
                    0x01,
                    0x88, 0x77, 0x66, 0x55
                },
                payload);
        }

        [Test]
        public void SkillRequest_FreeFlowContext_RoundTrips()
        {
            var expected = new NetworkSkillRequest
            {
                RequestId = 71,
                ActorNetworkId = 72,
                CorrelationId = 73,
                ClientTimestamp = 8.5f,
                TargetNetworkId = 74,
                SkillHash = -101,
                WeaponHash = 202,
                ComboNodeId = -303,
                PreviousComboNodeId = 404,
                InputKey = 2,
                IsChargeRelease = false,
                ChargeDuration = 0f,
                ActionFlags = NetworkMeleeSkillActionFlags.FreeFlowCounter,
                ActionStateRevision = 0xF1020304u
            };

            byte[] payload = FusionValueCodec.Encode(
                expected,
                (writer, value) => writer.Write(value));

            Assert.That(
                FusionValueCodec.TryDecode(
                    payload,
                    (FusionValueReader reader, ref NetworkSkillRequest value) =>
                        reader.Read(ref value),
                    out NetworkSkillRequest actual),
                Is.True);
            Assert.That(actual.ActionFlags, Is.EqualTo(expected.ActionFlags));
            Assert.That(actual.ActionStateRevision, Is.EqualTo(expected.ActionStateRevision));
            Assert.That(actual.ComboNodeId, Is.EqualTo(expected.ComboNodeId));
        }

        [Test]
        public void FreeFlowState_RoundTripsAndRejectsTrailingPayload()
        {
            var expected = new NetworkFreeFlowCombatState
            {
                CharacterNetworkId = 81,
                StateVersion = uint.MaxValue - 2,
                TargetNetworkId = 82,
                Flags = NetworkFreeFlowCombatState.AttackableFlag |
                        NetworkFreeFlowCombatState.CounterableFlag,
                ScoreBonus = 4.25f
            };
            byte[] payload = FusionValueCodec.Encode(
                expected,
                (writer, value) => writer.Write(value));

            Assert.That(
                FusionValueCodec.TryDecode(
                    payload,
                    (FusionValueReader reader, ref NetworkFreeFlowCombatState value) =>
                        reader.Read(ref value),
                    out NetworkFreeFlowCombatState actual),
                Is.True);
            Assert.That(actual.StateVersion, Is.EqualTo(expected.StateVersion));
            Assert.That(actual.Flags, Is.EqualTo(expected.Flags));
            Assert.That(actual.ScoreBonus, Is.EqualTo(expected.ScoreBonus));

            byte[] trailing = new byte[payload.Length + 1];
            payload.CopyTo(trailing, 0);
            trailing[trailing.Length - 1] = 0x7F;
            Assert.That(
                FusionValueCodec.TryDecode(
                    trailing,
                    (FusionValueReader reader, ref NetworkFreeFlowCombatState value) =>
                        reader.Read(ref value),
                    out _),
                Is.False);
        }

        [Test]
        public void CharacterSnapshot_FreeFlowState_RoundTrips()
        {
            NetworkMeleeCharacterSnapshot expected =
                NetworkMeleeCharacterSnapshot.Create(91);
            expected.HasFreeFlowState = true;
            expected.FreeFlowState = new NetworkFreeFlowCombatState
            {
                CharacterNetworkId = 91,
                StateVersion = 5,
                TargetNetworkId = 92,
                Flags = NetworkFreeFlowCombatState.AttackTokenFlag,
                ScoreBonus = 1.5f
            };

            byte[] payload = FusionValueCodec.Encode(
                expected,
                (writer, value) => writer.Write(value));
            Assert.That(
                FusionValueCodec.TryDecode(
                    payload,
                    (FusionValueReader reader, ref NetworkMeleeCharacterSnapshot value) =>
                        reader.Read(ref value),
                    out NetworkMeleeCharacterSnapshot actual),
                Is.True);
            Assert.That(actual.HasFreeFlowState, Is.True);
            Assert.That(actual.FreeFlowState.TargetNetworkId, Is.EqualTo(92));
            Assert.That(actual.FreeFlowState.HasAttackToken, Is.True);
        }
    }
}
#endif
