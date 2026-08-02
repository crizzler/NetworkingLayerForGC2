using System;
using System.Linq;
using System.Reflection;
using Arawn.GameCreator2.Networking.Transport.Fusion;
using NUnit.Framework;

namespace Arawn.GameCreator2.Networking.Transport.Fusion.Tests
{
    public sealed class FusionVisualScriptingSessionTests
    {
        [Test]
        public void StartSession_NewConnectionOptions_DefaultToBootstrapSettings()
        {
            var instruction = new InstructionFusionStartSession();
            FieldInfo regionSource = typeof(InstructionFusionStartSession).GetField(
                "m_RegionSource",
                BindingFlags.Instance | BindingFlags.NonPublic);
            FieldInfo relayPolicy = typeof(InstructionFusionStartSession).GetField(
                "m_RelayPolicy",
                BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.That(regionSource, Is.Not.Null);
            Assert.That(relayPolicy, Is.Not.Null);
            Assert.That(
                regionSource.GetValue(instruction),
                Is.EqualTo(FusionSessionRegionSource.BootstrapDefault));
            Assert.That(
                relayPolicy.GetValue(instruction),
                Is.EqualTo(FusionSessionRelayPolicy.BootstrapDefault));
        }

        [TestCase(
            typeof(ConditionFusionSessionLifecycleStateIs),
            "Network/Fusion/Session/Lifecycle State Is")]
        [TestCase(
            typeof(ConditionFusionConnectionIsRelayed),
            "Network/Fusion/Connection/Is Relayed")]
        [TestCase(
            typeof(EventFusionSessionLifecycleStateChanged),
            "Network/Fusion/Session/On Lifecycle State Changed")]
        [TestCase(
            typeof(GetBoolFusionConnectionIsRelayed),
            "Network/Fusion/Connection/Is Relayed")]
        public void NewSessionEntries_HaveStableGc2DiscoveryMetadata(
            Type type,
            string expectedCategory)
        {
            CustomAttributeData title = type.CustomAttributes.SingleOrDefault(attribute =>
                attribute.AttributeType.FullName ==
                "GameCreator.Runtime.Common.TitleAttribute");
            CustomAttributeData category = type.CustomAttributes.SingleOrDefault(attribute =>
                attribute.AttributeType.FullName ==
                "GameCreator.Runtime.Common.CategoryAttribute");

            Assert.That(type.IsSerializable, Is.True);
            Assert.That(title, Is.Not.Null);
            Assert.That(title.ConstructorArguments[0].Value as string, Is.Not.Empty);
            Assert.That(category, Is.Not.Null);
            Assert.That(
                category.ConstructorArguments[0].Value as string,
                Is.EqualTo(expectedCategory));
        }
    }
}
