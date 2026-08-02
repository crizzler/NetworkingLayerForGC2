using System.Collections.Generic;
using Fusion;
using NUnit.Framework;
using Assert = NUnit.Framework.Assert;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Transport.Fusion.Tests
{
    public sealed class FusionLobbyIsolationTests
    {
        [Test]
        public void SessionStartOptions_CopyLobbyPropertiesAndCapacity()
        {
            var source = new Dictionary<string, SessionProperty>
            {
                ["gc2p"] = SessionProperty.Convert("product")
            };
            var options = new FusionSessionStartOptions(
                "session-code",
                isOpen: true,
                isVisible: false,
                customLobbyName: "gc2-networking",
                sessionProperties: source,
                maxPlayers: 12);

            source.Clear();

            Assert.AreEqual(true, options.IsOpen);
            Assert.AreEqual(false, options.IsVisible);
            Assert.AreEqual("gc2-networking", options.CustomLobbyName);
            Assert.AreEqual(12, options.MaxPlayers);
            Assert.AreEqual(1, options.SessionProperties.Count);
            Assert.AreEqual("product", options.SessionProperties["gc2p"].PropertyValue);
        }

        [Test]
        public void TransportBridge_RejectsDiscoveryRunner()
        {
            var runnerObject = new GameObject("Discovery Runner");
            var bridgeObject = new GameObject("Transport Bridge");
            try
            {
                NetworkRunner runner = runnerObject.AddComponent<NetworkRunner>();
                runnerObject.AddComponent<FusionLobbyDiscoveryRunnerMarker>();
                FusionTransportBridge bridge = bridgeObject.AddComponent<FusionTransportBridge>();

                Assert.IsFalse(bridge.Bind(runner));
                Assert.IsNull(bridge.Runner);
            }
            finally
            {
                Object.DestroyImmediate(bridgeObject);
                Object.DestroyImmediate(runnerObject);
            }
        }

        [Test]
        public void SessionBootstrap_RejectsDiscoveryRunner()
        {
            var runnerObject = new GameObject("Discovery Runner");
            var bootstrapObject = new GameObject("Session Bootstrap");
            try
            {
                NetworkRunner runner = runnerObject.AddComponent<NetworkRunner>();
                runnerObject.AddComponent<FusionLobbyDiscoveryRunnerMarker>();
                FusionSessionBootstrap bootstrap =
                    bootstrapObject.AddComponent<FusionSessionBootstrap>();

                Assert.IsFalse(bootstrap.BindExistingRunner(runner));
                Assert.IsNull(bootstrap.Runner);
            }
            finally
            {
                Object.DestroyImmediate(bootstrapObject);
                Object.DestroyImmediate(runnerObject);
            }
        }
    }
}
