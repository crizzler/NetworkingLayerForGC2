using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Arawn.NetworkingCore.LagCompensation;
using Fusion;
using NUnit.Framework;
using Assert = NUnit.Framework.Assert;
using Object = UnityEngine.Object;
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
        public void SessionBootstrap_UniqueSharedSessionNamesTrimPrefixAndUseFullGuids()
        {
            const string expectedPrefix = "Friendly Room-";

            string first = FusionSessionBootstrap.GenerateUniqueSharedSessionName(
                "  Friendly Room  ");
            string second = FusionSessionBootstrap.GenerateUniqueSharedSessionName(
                "Friendly Room");

            StringAssert.StartsWith(expectedPrefix, first);
            StringAssert.StartsWith(expectedPrefix, second);
            Assert.AreEqual(expectedPrefix.Length + 32, first.Length);
            Assert.AreEqual(expectedPrefix.Length + 32, second.Length);
            Assert.IsTrue(Guid.TryParseExact(
                first.Substring(expectedPrefix.Length),
                "N",
                out _));
            Assert.IsTrue(Guid.TryParseExact(
                second.Substring(expectedPrefix.Length),
                "N",
                out _));
            Assert.AreNotEqual(first, second);
        }

        [Test]
        public void SessionBootstrap_UniqueSharedSessionNamesBoundLongPrefixes()
        {
            string requestedPrefix = new string('x', 96);
            string expectedPrefix = new string('x', 64) + "-";

            string generated = FusionSessionBootstrap.GenerateUniqueSharedSessionName(
                requestedPrefix);

            StringAssert.StartsWith(expectedPrefix, generated);
            Assert.AreEqual(expectedPrefix.Length + 32, generated.Length);
            Assert.IsTrue(Guid.TryParseExact(
                generated.Substring(expectedPrefix.Length),
                "N",
                out _));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase(" \t\r\n ")]
        public void SessionBootstrap_UniqueSharedSessionNamesRejectBlankPrefixes(
            string requestedPrefix)
        {
            Assert.Throws<ArgumentException>(() =>
                FusionSessionBootstrap.GenerateUniqueSharedSessionName(requestedPrefix));
        }

        [Test]
        public void SessionBootstrap_CreateOrJoinSharedOverloadsRemainPublic()
        {
            Type bootstrap = typeof(FusionSessionBootstrap);

            MethodInfo stringOverload = bootstrap.GetMethod(
                nameof(FusionSessionBootstrap.CreateOrJoinSharedAsync),
                new[] { typeof(string) });
            MethodInfo optionsOverload = bootstrap.GetMethod(
                nameof(FusionSessionBootstrap.CreateOrJoinSharedAsync),
                new[] { typeof(FusionSessionStartOptions) });

            Assert.NotNull(stringOverload);
            Assert.NotNull(optionsOverload);
        }

        [Test]
        public void SessionBootstrap_ExactSharedCreateOverloadsRemainPublic()
        {
            Type bootstrap = typeof(FusionSessionBootstrap);

            MethodInfo stringOverload = bootstrap.GetMethod(
                nameof(FusionSessionBootstrap.CreateSharedWithExactSessionNameAsync),
                new[] { typeof(string) });
            MethodInfo optionsOverload = bootstrap.GetMethod(
                nameof(FusionSessionBootstrap.CreateSharedWithExactSessionNameAsync),
                new[] { typeof(FusionSessionStartOptions) });

            Assert.NotNull(stringOverload);
            Assert.NotNull(optionsOverload);
        }

        [Test]
        public void SessionBootstrap_ExactSharedCreateUsesCollisionClaimAndNameGenerator()
        {
            string source = File.ReadAllText(Path.Combine(
                Application.dataPath,
                "Arawn/NetworkingLayerForGC2/Runtime/Transport/Fusion/" +
                "FusionSessionBootstrap.cs"));

            StringAssert.Contains("ExactSharedCreationClaimPropertyKey", source);
            StringAssert.Contains("startGameSessionName = null", source);
            StringAssert.Contains("sessionNameGenerator = () => exactSessionName", source);
            StringAssert.Contains("SessionNameGenerator = sessionNameGenerator", source);
        }

        [Test]
        public void FusionLobby_SharedCreateUsesOptionalExactIdOrGeneratesUniqueId()
        {
            MethodInfo resolver = typeof(FusionLobbyService).GetMethod(
                "ResolveSharedCreateSessionName",
                StaticNonPublic);
            Assert.NotNull(resolver);

            object[] exactArguments = { "Friendly Room", "  EDL234  ", false };
            string exact = (string)resolver.Invoke(null, exactArguments);
            Assert.AreEqual("EDL234", exact);
            Assert.IsTrue((bool)exactArguments[2]);

            object[] firstGeneratedArguments = { "Friendly Room", "  ", false };
            object[] secondGeneratedArguments = { "Friendly Room", string.Empty, false };
            string firstGenerated = (string)resolver.Invoke(null, firstGeneratedArguments);
            string secondGenerated = (string)resolver.Invoke(null, secondGeneratedArguments);
            StringAssert.StartsWith("Friendly Room-", firstGenerated);
            StringAssert.StartsWith("Friendly Room-", secondGenerated);
            Assert.AreNotEqual(firstGenerated, secondGenerated);
            Assert.IsFalse((bool)firstGeneratedArguments[2]);
            Assert.IsFalse((bool)secondGeneratedArguments[2]);
        }

        [Test]
        public void SessionBootstrap_ReplacingSharedSessionNamePreservesOtherOptions()
        {
            var authentication = new Photon.Realtime.AuthenticationValues("test-user");
            var properties = new Dictionary<string, SessionProperty>
            {
                ["gc2n"] = SessionProperty.Convert("Friendly Room")
            };
            var source = new FusionSessionStartOptions(
                "requested-name",
                "eu",
                authentication,
                forcePhotonRelay: true,
                isOpen: false,
                isVisible: true,
                customLobbyName: "custom-lobby",
                sessionProperties: properties,
                maxPlayers: 8);
            MethodInfo copyMethod = typeof(FusionSessionBootstrap).GetMethod(
                "CopyOptionsWithSessionName",
                StaticNonPublic);
            Assert.NotNull(copyMethod);

            var copy = (FusionSessionStartOptions)copyMethod.Invoke(
                null,
                new object[] { source, "generated-name" });

            Assert.AreEqual("generated-name", copy.SessionName);
            Assert.AreEqual("eu", copy.Region);
            Assert.AreSame(authentication, copy.AuthenticationValues);
            Assert.IsTrue(copy.ForcePhotonRelay);
            Assert.AreEqual(false, copy.IsOpen);
            Assert.AreEqual(true, copy.IsVisible);
            Assert.AreEqual("custom-lobby", copy.CustomLobbyName);
            Assert.AreEqual(8, copy.MaxPlayers);
            Assert.AreEqual(1, copy.SessionProperties.Count);
            Assert.AreEqual(
                "Friendly Room",
                copy.SessionProperties["gc2n"].PropertyValue);
        }

        [Test]
        public void SharedCreationFrontends_ExposeTheSingleGeneratedBackendName()
        {
            string runtimeRoot = Path.Combine(
                Application.dataPath,
                "Arawn/NetworkingLayerForGC2/Runtime/Transport/Fusion");
            string lobby = File.ReadAllText(Path.Combine(runtimeRoot, "FusionLobbyService.cs"));
            string demoUi = File.ReadAllText(Path.Combine(runtimeRoot, "FusionDemoSessionUI.cs"));
            string sharedLobbyUi = File.ReadAllText(Path.Combine(
                Application.dataPath,
                "Arawn/NetworkingLayerForGC2/Runtime/Lobby/NetworkLobbyCanvasUI.cs"));

            StringAssert.Contains("FusionSessionBootstrap.GenerateUniqueSharedSessionName(", lobby);
            StringAssert.Contains(
                "m_SessionBootstrap.CreateSharedWithExactSessionNameAsync(options)",
                lobby);
            StringAssert.DoesNotContain("? m_SessionBootstrap.CreateSharedAsync(options)", lobby);
            StringAssert.Contains("Join code: {sessionName}", lobby);
            StringAssert.Contains("request.JoinCode", lobby);

            StringAssert.Contains("FusionSessionBootstrap.GenerateUniqueSharedSessionName(prefix)", demoUi);
            StringAssert.Contains(
                "m_SessionBootstrap.CreateSharedWithExactSessionNameAsync(name)",
                demoUi);
            StringAssert.Contains("public void CreateSharedExact()", demoUi);
            StringAssert.Contains("SetSessionName(sessionName)", demoUi);
            StringAssert.Contains("Join code: {sessionName}", demoUi);

            StringAssert.Contains(
                "m_ConnectedJoinCodeField.text = m_Service.CurrentSessionId",
                sharedLobbyUi);
            StringAssert.Contains("GUIUtility.systemCopyBuffer = sessionId", sharedLobbyUi);
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
