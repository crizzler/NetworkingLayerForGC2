#if GC2_QUESTS
using NUnit.Framework;

namespace Arawn.GameCreator2.Networking.Quests.Transport.Fusion.Tests
{
    public sealed class FusionQuestsCodecTests
    {
        [Test]
        public void Snapshot_WithUnicodeAndCollections_RoundTrips()
        {
            var expected = new NetworkQuestsSnapshot
            {
                NetworkId = 22,
                ServerTime = 17.5f,
                QuestEntries = new[]
                {
                    new NetworkQuestSnapshotEntry
                    {
                        QuestHash = 123,
                        QuestIdString = "任務-α",
                        State = 2
                    }
                },
                TaskEntries = new[]
                {
                    new NetworkTaskSnapshotEntry
                    {
                        QuestHash = 123,
                        QuestIdString = "任務-α",
                        TaskId = 4,
                        State = 1,
                        Value = 3f
                    }
                }
            };

            byte[] payload = FusionValueCodec.Encode(
                expected,
                (writer, value) => writer.Write(value));

            Assert.That(
                FusionValueCodec.TryDecode(
                    payload,
                    (FusionValueReader reader, ref NetworkQuestsSnapshot value) =>
                        reader.Read(ref value),
                    out NetworkQuestsSnapshot actual),
                Is.True);
            Assert.That(actual.NetworkId, Is.EqualTo(22));
            Assert.That(actual.QuestEntries, Has.Length.EqualTo(1));
            Assert.That(actual.QuestEntries[0].QuestIdString, Is.EqualTo("任務-α"));
            Assert.That(actual.TaskEntries, Has.Length.EqualTo(1));
        }
    }
}
#endif
