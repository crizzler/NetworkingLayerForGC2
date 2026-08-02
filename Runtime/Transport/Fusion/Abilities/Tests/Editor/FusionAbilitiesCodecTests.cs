#if GC2_ABILITIES
using NUnit.Framework;

namespace Arawn.GameCreator2.Networking.Abilities.Transport.Fusion.Tests
{
    public sealed class FusionAbilitiesCodecTests
    {
        [Test]
        public void FullSnapshot_RoundTripsNestedState()
        {
            var expected = new NetworkAbilitiesFullSnapshot
            {
                ServerTime = 20f,
                Characters = new[]
                {
                    new NetworkAbilityCharacterSnapshot
                    {
                        State = new NetworkAbilityStateResponse
                        {
                            CharacterNetworkId = 9,
                            SlotCount = 1,
                            CooldownCount = 1,
                            IsCasting = true,
                            CurrentCastId = 77,
                            CurrentCastAbilityHash = 500
                        },
                        Slots = new[]
                        {
                            new NetworkAbilitySlotEntry { SlotIndex = 0, AbilityHash = 500 }
                        },
                        Cooldowns = new[]
                        {
                            new NetworkCooldownEntry
                            {
                                AbilityHash = 500,
                                EndTime = 30f,
                                TotalDuration = 10f
                            }
                        },
                        ActiveCasts = new[]
                        {
                            new NetworkAbilityCastBroadcast
                            {
                                CasterNetworkId = 9,
                                CastInstanceId = 77,
                                AbilityIdHash = 500
                            }
                        }
                    }
                }
            };

            byte[] payload = FusionValueCodec.Encode(
                expected,
                (writer, value) => writer.Write(value));

            Assert.That(
                FusionValueCodec.TryDecode(
                    payload,
                    (FusionValueReader reader, ref NetworkAbilitiesFullSnapshot value) =>
                        reader.Read(ref value),
                    out NetworkAbilitiesFullSnapshot actual),
                Is.True);
            Assert.That(actual.Characters, Has.Length.EqualTo(1));
            Assert.That(actual.Characters[0].Slots[0].AbilityHash, Is.EqualTo(500));
            Assert.That(actual.Characters[0].Cooldowns[0].EndTime, Is.EqualTo(30f));
            Assert.That(actual.Characters[0].ActiveCasts[0].CastInstanceId, Is.EqualTo(77));
        }

        [Test]
        public void FullSnapshot_RejectsTruncatedNestedArray()
        {
            Assert.That(
                FusionValueCodec.TryDecode(
                    new byte[] { 0, 0, 0, 0, 1, 0, 0 },
                    (FusionValueReader reader, ref NetworkAbilitiesFullSnapshot value) =>
                        reader.Read(ref value),
                    out _),
                Is.False);
        }

        [Test]
        public void FullSnapshot_UsesStableNestedFieldOrder()
        {
            var value = new NetworkAbilitiesFullSnapshot
            {
                ServerTime = 1f,
                Characters = new[]
                {
                    new NetworkAbilityCharacterSnapshot
                    {
                        State = new NetworkAbilityStateResponse
                        {
                            RequestId = 0x1234,
                            ActorNetworkId = 1,
                            CorrelationId = 2,
                            CharacterNetworkId = 3
                        },
                        Slots = null,
                        Cooldowns = null,
                        ActiveCasts = null
                    }
                }
            };

            byte[] payload = FusionValueCodec.Encode(
                value,
                (writer, snapshot) => writer.Write(snapshot));

            CollectionAssert.AreEqual(
                new byte[]
                {
                    0x00, 0x00, 0x80, 0x3F,
                    0x01, 0x00, 0x00, 0x00,
                    0x34, 0x12,
                    0x01, 0x00, 0x00, 0x00,
                    0x02, 0x00, 0x00, 0x00,
                    0x03, 0x00, 0x00, 0x00,
                    0x00,
                    0x00,
                    0x00,
                    0x00, 0x00, 0x00, 0x00,
                    0x00, 0x00, 0x00, 0x00,
                    0xFF, 0xFF, 0xFF, 0xFF,
                    0xFF, 0xFF, 0xFF, 0xFF,
                    0xFF, 0xFF, 0xFF, 0xFF
                },
                payload);
        }
    }
}
#endif
