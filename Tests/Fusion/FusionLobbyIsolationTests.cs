using System.Collections.Generic;
using System.Reflection;
using Arawn.NetworkingCore.LagCompensation;
using Fusion;
using NUnit.Framework;
using Assert = NUnit.Framework.Assert;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Transport.Fusion.Tests
{
    public sealed class FusionLobbyIsolationTests
    {
        private const BindingFlags StaticNonPublic =
            BindingFlags.Static | BindingFlags.NonPublic;
        private const BindingFlags InstanceNonPublic =
            BindingFlags.Instance | BindingFlags.NonPublic;

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
        public void TransportBridge_ControlsAuthoritativeLagCompensationLifecycle()
        {
            if (LagCompensationManager.TryGetInitialized(out LagCompensationManager existing))
            {
                existing.Dispose();
            }

            var bridgeObject = new GameObject("Fusion lag compensation bridge");
            try
            {
                FusionTransportBridge bridge =
                    bridgeObject.AddComponent<FusionTransportBridge>();
                FieldInfo bootstrapField = typeof(FusionTransportBridge).GetField(
                    "m_LagCompensationBootstrap",
                    InstanceNonPublic);
                MethodInfo setAuthority = typeof(FusionTransportBridge).GetMethod(
                    "SetLagCompensationAuthority",
                    InstanceNonPublic);

                Assert.NotNull(bootstrapField);
                Assert.NotNull(setAuthority);

                // Edit-mode AddComponent does not invoke MonoBehaviour.Awake. Driving the
                // authority entry point verifies that it can establish the bootstrap itself.
                setAuthority.Invoke(bridge, new object[] { true });
                var bootstrap = (LagCompensationBootstrap)bootstrapField.GetValue(bridge);
                Assert.NotNull(bootstrap);
                Assert.NotNull(bootstrap.GetServerTimeFunc);
                Assert.AreSame(bridge, bootstrap.GetServerTimeFunc.Target);

                Assert.IsTrue(bootstrap.IsServer);
                Assert.IsTrue(bootstrap.IsInitialized);
                Assert.IsTrue(LagCompensationManager.IsInitialized);

                setAuthority.Invoke(bridge, new object[] { false });
                Assert.IsFalse(bootstrap.IsServer);
                Assert.IsFalse(bootstrap.IsInitialized);
                Assert.IsFalse(LagCompensationManager.IsInitialized);
            }
            finally
            {
                if (LagCompensationManager.TryGetInitialized(out LagCompensationManager manager))
                {
                    manager.Dispose();
                }
                Object.DestroyImmediate(bridgeObject);
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

        [Test]
        public void SessionBootstrap_AppIdDiagnosticDoesNotExposeFullAppId()
        {
            MethodInfo formatter = typeof(FusionSessionBootstrap).GetMethod(
                "FormatAppIdForDiagnostic",
                StaticNonPublic);
            Assert.NotNull(formatter);

            const string appId = "12345678-90ab-cdef-1234-567890abcdef";
            string diagnostic = (string)formatter.Invoke(null, new object[] { appId });

            StringAssert.StartsWith("sha256=", diagnostic);
            StringAssert.Contains(" suffix=cdef", diagnostic);
            StringAssert.DoesNotContain(appId, diagnostic);
        }

        [Test]
        public void SessionBootstrap_DiagnosticQuotingEscapesLogBreakingCharacters()
        {
            MethodInfo quote = typeof(FusionSessionBootstrap).GetMethod(
                "QuoteDiagnosticValue",
                StaticNonPublic);
            Assert.NotNull(quote);

            string diagnostic = (string)quote.Invoke(
                null,
                new object[] { "room\"name\nnext\\part" });

            Assert.AreEqual("\"room\\\"name\\nnext\\\\part\"", diagnostic);
        }

        [Test]
        public void SessionBootstrap_ResultDetailRedactsAppIdCaseInsensitively()
        {
            MethodInfo redact = typeof(FusionSessionBootstrap).GetMethod(
                "RedactExactValue",
                StaticNonPublic);
            Assert.NotNull(redact);

            const string appId = "12345678-90ab-cdef-1234-567890abcdef";
            string diagnostic = (string)redact.Invoke(
                null,
                new object[]
                {
                    $"Photon rejected app {appId.ToUpperInvariant()} during matchmaking.",
                    appId
                });

            StringAssert.Contains("<app-id-redacted>", diagnostic);
            StringAssert.DoesNotContain(appId.ToUpperInvariant(), diagnostic);
        }
    }
}
