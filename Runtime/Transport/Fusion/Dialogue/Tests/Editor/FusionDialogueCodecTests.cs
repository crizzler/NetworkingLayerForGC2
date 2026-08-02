#if GC2_DIALOGUE
using NUnit.Framework;

namespace Arawn.GameCreator2.Networking.Dialogue.Transport.Fusion.Tests
{
    public sealed class FusionDialogueCodecTests
    {
        [Test]
        public void Snapshot_CollectionsRoundTrip()
        {
            var expected = new NetworkDialogueSnapshot
            {
                NetworkId = 3,
                DialogueHash = 44,
                DialogueIdString = "dialogue/你好",
                VisitedNodeIds = new[] { 1, 5, 8 },
                VisitedTagIds = new[] { "intro", "完了" }
            };

            byte[] payload = FusionValueCodec.Encode(
                expected,
                (writer, value) => writer.Write(value));

            Assert.That(
                FusionValueCodec.TryDecode(
                    payload,
                    (FusionValueReader reader, ref NetworkDialogueSnapshot value) =>
                        reader.Read(ref value),
                    out NetworkDialogueSnapshot actual),
                Is.True);
            Assert.That(actual.VisitedNodeIds, Is.EqualTo(expected.VisitedNodeIds));
            Assert.That(actual.VisitedTagIds, Is.EqualTo(expected.VisitedTagIds));
        }
    }
}
#endif
