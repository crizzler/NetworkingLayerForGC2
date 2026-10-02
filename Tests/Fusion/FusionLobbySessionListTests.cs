using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using Arawn.GameCreator2.Networking.Lobby;
using Fusion;
using NUnit.Framework;
using UnityEngine;
using Assert = NUnit.Framework.Assert;
using Object = UnityEngine.Object;

namespace Arawn.GameCreator2.Networking.Transport.Fusion.Tests
{
    /// <summary>
    /// Regression coverage for the Fusion lobby session-list pipeline behind
    /// <see cref="FusionLobbyService.OnSessionListUpdated"/>. A reported "session list is always
    /// empty" symptom can come from the callback guard (runner/generation), the mapping and
    /// compatibility filtering in <c>MapSessions</c>, or the lobby/metadata published by the
    /// create flow. EditMode cannot join a real Photon lobby, so these tests drive the callback
    /// boundary with synthetic runners and <see cref="SessionInfo"/> snapshots.
    /// NOTE: sessions created through <c>FusionSessionBootstrap</c> default start options (demo
    /// overlays, GC2 start-session instructions) are published in Fusion's default Photon lobby,
    /// while this service discovers and creates in its serialized custom lobby. Those flows only
    /// see each other when both sides configure the same custom lobby name.
    /// </summary>
    public sealed class FusionLobbySessionListTests
    {
        private const BindingFlags InstanceNonPublic =
            BindingFlags.Instance | BindingFlags.NonPublic;

        private const BindingFlags StaticNonPublic =
            BindingFlags.Static | BindingFlags.NonPublic;

        private const string TestProduct = "gc2-lobby-test-product";
        private const string TestBuild = "gc2-lobby-test-build";
        private const int TestProtocol = 7;

        private static readonly ConstructorInfo SessionInfoConstructor =
            typeof(SessionInfo).GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic,
                null,
                new[] { typeof(NetworkRunner) },
                null);

        private static readonly MethodInfo MarkerInitialize =
            typeof(FusionLobbyDiscoveryRunnerMarker).GetMethod(
                "Initialize",
                InstanceNonPublic);

        private static readonly MethodInfo BuildSessionProperties =
            typeof(FusionLobbyService).GetMethod(
                "BuildSessionProperties",
                InstanceNonPublic);

        [Test]
        public void SessionList_MatchingSnapshot_PopulatesSessionsAndRaisesSessionsChanged()
        {
            NetworkLobbyCompatibilityProfile compatibility = CreateProfile();
            using (DiscoveryContext context = DiscoveryContext.Create(
                compatibility,
                new NetworkLobbyQuery(string.Empty, NetworkLobbyTopology.ClientServer)))
            {
                int changedCount = 0;
                context.Service.SessionsChanged += () => changedCount++;

                context.Receive(new List<SessionInfo>
                {
                    CreateSession(
                        "room-alpha",
                        CreateCompatibleProperties(
                            compatibility,
                            "Alpha Room",
                            NetworkLobbyTopology.ClientServer),
                        region: "eu",
                        playerCount: 2,
                        maxPlayers: 8)
                });

                Assert.AreEqual(1, changedCount, "ReplaceSessions must raise SessionsChanged.");
                Assert.AreEqual(1, context.Service.Sessions.Count);
                NetworkLobbyEntry entry = context.Service.Sessions[0];
                Assert.AreEqual("room-alpha", entry.Id);
                Assert.AreEqual("room-alpha", entry.JoinCode);
                Assert.AreEqual("Alpha Room", entry.Name);
                Assert.AreEqual("eu", entry.Region);
                Assert.AreEqual(NetworkLobbyTopology.ClientServer, entry.Topology);
                Assert.AreEqual(2, entry.PlayerCount);
                Assert.AreEqual(8, entry.MaxPlayers);
                Assert.IsTrue(entry.IsCompatible);
                Assert.IsTrue(entry.CanJoin);
            }
        }

        [Test]
        public void SessionList_DisplayNameFallsBackToTheSessionIdWhenMetadataIsAbsent()
        {
            NetworkLobbyCompatibilityProfile compatibility = CreateProfile();
            using (DiscoveryContext context = DiscoveryContext.Create(
                compatibility,
                new NetworkLobbyQuery(string.Empty, NetworkLobbyTopology.ClientServer)))
            {
                Dictionary<string, SessionProperty> properties =
                    CreateCompatibleProperties(
                        compatibility,
                        "unused",
                        NetworkLobbyTopology.ClientServer);
                properties.Remove(NetworkLobbyCompatibilityProfile.DisplayNameKey);

                context.Receive(new List<SessionInfo>
                {
                    CreateSession("room-beta", properties)
                });

                Assert.AreEqual(1, context.Service.Sessions.Count);
                Assert.AreEqual("room-beta", context.Service.Sessions[0].Name);
            }
        }

        [TestCase("product")]
        [TestCase("build")]
        [TestCase("protocol")]
        [TestCase("missing-topology")]
        [TestCase("wrong-topology")]
        [TestCase("region")]
        [TestCase("no-properties")]
        public void SessionList_IncompatibleSnapshots_AreHiddenUnlessTheQueryIncludesThem(
            string mismatchKind)
        {
            NetworkLobbyCompatibilityProfile compatibility = CreateProfile();
            Dictionary<string, SessionProperty> properties = CreateCompatibleProperties(
                compatibility,
                "Remote Room",
                NetworkLobbyTopology.ClientServer);
            string queryRegion = string.Empty;
            string sessionRegion = string.Empty;

            switch (mismatchKind)
            {
                case "product":
                    properties[NetworkLobbyCompatibilityProfile.ProductKey] =
                        SessionProperty.Convert(TestProduct + "-other");
                    break;
                case "build":
                    properties[NetworkLobbyCompatibilityProfile.BuildKey] =
                        SessionProperty.Convert(TestBuild + "-other");
                    break;
                case "protocol":
                    properties[NetworkLobbyCompatibilityProfile.ProtocolKey] =
                        SessionProperty.Convert(TestProtocol + 1);
                    break;
                case "missing-topology":
                    properties.Remove(NetworkLobbyCompatibilityProfile.TopologyKey);
                    break;
                case "wrong-topology":
                    properties[NetworkLobbyCompatibilityProfile.TopologyKey] =
                        SessionProperty.Convert("shared");
                    break;
                case "region":
                    queryRegion = "eu";
                    sessionRegion = "us";
                    break;
                case "no-properties":
                    // Sessions created outside the lobby service (or by older builds) carry
                    // no compatibility metadata and must be treated as incompatible.
                    properties = null;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(mismatchKind),
                        mismatchKind,
                        "Unknown mismatch kind.");
            }

            using (DiscoveryContext hidden = DiscoveryContext.Create(
                compatibility,
                new NetworkLobbyQuery(
                    queryRegion,
                    NetworkLobbyTopology.ClientServer,
                    includeIncompatible: false)))
            {
                int changedCount = 0;
                hidden.Service.SessionsChanged += () => changedCount++;

                hidden.Receive(new List<SessionInfo>
                {
                    CreateSession("remote-room", properties, sessionRegion)
                });

                Assert.AreEqual(
                    0,
                    hidden.Service.Sessions.Count,
                    $"A {mismatchKind} mismatch must be hidden by default.");
                Assert.AreEqual(
                    1,
                    changedCount,
                    "The replacement snapshot must still notify listeners that the list changed.");
            }

            using (DiscoveryContext shown = DiscoveryContext.Create(
                compatibility,
                new NetworkLobbyQuery(
                    queryRegion,
                    NetworkLobbyTopology.ClientServer,
                    includeIncompatible: true)))
            {
                shown.Receive(new List<SessionInfo>
                {
                    CreateSession("remote-room", properties, sessionRegion)
                });

                Assert.AreEqual(
                    1,
                    shown.Service.Sessions.Count,
                    $"A {mismatchKind} mismatch must remain visible when incompatible " +
                    "sessions are requested.");
                NetworkLobbyEntry entry = shown.Service.Sessions[0];
                Assert.IsFalse(entry.IsCompatible);
                Assert.IsFalse(entry.CanJoin);
                Assert.IsNotEmpty(
                    entry.CompatibilityMessage,
                    "Incompatible entries must explain why they cannot be joined.");
            }
        }

        [Test]
        public void SessionList_ClosedOrFullSessions_RemainListedButNotJoinable()
        {
            NetworkLobbyCompatibilityProfile compatibility = CreateProfile();
            using (DiscoveryContext context = DiscoveryContext.Create(
                compatibility,
                new NetworkLobbyQuery(string.Empty, NetworkLobbyTopology.ClientServer)))
            {
                context.Receive(new List<SessionInfo>
                {
                    CreateSession(
                        "closed-room",
                        CreateCompatibleProperties(
                            compatibility,
                            "Closed Room",
                            NetworkLobbyTopology.ClientServer),
                        isOpen: false),
                    CreateSession(
                        "full-room",
                        CreateCompatibleProperties(
                            compatibility,
                            "Full Room",
                            NetworkLobbyTopology.ClientServer),
                        playerCount: 8,
                        maxPlayers: 8)
                });

                Assert.AreEqual(2, context.Service.Sessions.Count);
                foreach (NetworkLobbyEntry entry in context.Service.Sessions)
                {
                    Assert.IsTrue(entry.IsCompatible);
                    Assert.IsFalse(
                        entry.CanJoin,
                        $"{entry.Id} must not be joinable while closed or full.");
                }
            }
        }

        [Test]
        public void SessionList_Sorting_PrefersCompatibleJoinableAndPopulatedSessions()
        {
            NetworkLobbyCompatibilityProfile compatibility = CreateProfile();
            Dictionary<string, SessionProperty> incompatible = CreateCompatibleProperties(
                compatibility,
                "Gamma Room",
                NetworkLobbyTopology.ClientServer);
            incompatible[NetworkLobbyCompatibilityProfile.ProductKey] =
                SessionProperty.Convert(TestProduct + "-other");

            using (DiscoveryContext context = DiscoveryContext.Create(
                compatibility,
                new NetworkLobbyQuery(
                    string.Empty,
                    NetworkLobbyTopology.ClientServer,
                    includeIncompatible: true)))
            {
                context.Receive(new List<SessionInfo>
                {
                    CreateSession("gamma-room", incompatible, playerCount: 5),
                    CreateSession(
                        "alpha-room",
                        CreateCompatibleProperties(
                            compatibility,
                            "Alpha Room",
                            NetworkLobbyTopology.ClientServer),
                        playerCount: 1),
                    CreateSession(
                        "beta-room",
                        CreateCompatibleProperties(
                            compatibility,
                            "Beta Room",
                            NetworkLobbyTopology.ClientServer),
                        playerCount: 3)
                });

                Assert.AreEqual(3, context.Service.Sessions.Count);
                Assert.AreEqual("beta-room", context.Service.Sessions[0].Id);
                Assert.AreEqual("alpha-room", context.Service.Sessions[1].Id);
                Assert.AreEqual("gamma-room", context.Service.Sessions[2].Id);
            }
        }

        [Test]
        public void SessionList_PropertyKeysWrittenByTheCreateFlow_AreReadBackByMapping()
        {
            NetworkLobbyCompatibilityProfile compatibility = CreateProfile();
            Assert.IsNotNull(BuildSessionProperties);

            using (DiscoveryContext context = DiscoveryContext.Create(
                compatibility,
                new NetworkLobbyQuery(string.Empty, NetworkLobbyTopology.ClientServer)))
            {
                var properties = (Dictionary<string, SessionProperty>)BuildSessionProperties.Invoke(
                    context.Service,
                    new object[] { "Round Trip Room", NetworkLobbyTopology.ClientServer });

                context.Receive(new List<SessionInfo>
                {
                    CreateSession("round-trip-room", properties)
                });

                Assert.AreEqual(
                    1,
                    context.Service.Sessions.Count,
                    "A session created with the service's own properties must map as " +
                    "compatible; a key mismatch would silently filter every session.");
                NetworkLobbyEntry entry = context.Service.Sessions[0];
                Assert.IsTrue(entry.IsCompatible);
                Assert.IsTrue(entry.CanJoin);
                Assert.AreEqual("Round Trip Room", entry.Name);
                Assert.AreEqual(NetworkLobbyTopology.ClientServer, entry.Topology);
            }
        }

        [Test]
        public void SessionList_SharedTopology_RoundTripsThroughMapping()
        {
            NetworkLobbyCompatibilityProfile compatibility = CreateProfile();

            using (DiscoveryContext context = DiscoveryContext.Create(
                compatibility,
                new NetworkLobbyQuery(string.Empty, NetworkLobbyTopology.Shared)))
            {
                var properties = (Dictionary<string, SessionProperty>)BuildSessionProperties.Invoke(
                    context.Service,
                    new object[] { "Shared Room", NetworkLobbyTopology.Shared });

                context.Receive(new List<SessionInfo>
                {
                    CreateSession("shared-room", properties)
                });

                Assert.AreEqual(1, context.Service.Sessions.Count);
                Assert.IsTrue(context.Service.Sessions[0].IsCompatible);
                Assert.AreEqual(
                    NetworkLobbyTopology.Shared,
                    context.Service.Sessions[0].Topology);
            }
        }

        [Test]
        public void SessionList_NullSnapshot_ClearsExistingSessionsAndNotifies()
        {
            NetworkLobbyCompatibilityProfile compatibility = CreateProfile();
            using (DiscoveryContext context = DiscoveryContext.Create(
                compatibility,
                new NetworkLobbyQuery(string.Empty, NetworkLobbyTopology.ClientServer)))
            {
                int changedCount = 0;
                context.Service.SessionsChanged += () => changedCount++;

                context.Receive(new List<SessionInfo>
                {
                    CreateSession(
                        "room-alpha",
                        CreateCompatibleProperties(
                            compatibility,
                            "Alpha Room",
                            NetworkLobbyTopology.ClientServer))
                });
                context.Receive(null);

                Assert.AreEqual(0, context.Service.Sessions.Count);
                Assert.AreEqual(2, changedCount);
            }
        }

        [Test]
        public void SessionList_CallbackFromAnotherRunner_IsIgnored()
        {
            NetworkLobbyCompatibilityProfile compatibility = CreateProfile();
            using (DiscoveryContext context = DiscoveryContext.Create(
                compatibility,
                new NetworkLobbyQuery(string.Empty, NetworkLobbyTopology.ClientServer)))
            {
                var foreignObject = new GameObject("Foreign Lobby Runner");
                try
                {
                    NetworkRunner foreignRunner = foreignObject.AddComponent<NetworkRunner>();
                    int changedCount = 0;
                    context.Service.SessionsChanged += () => changedCount++;

                    context.Service.OnSessionListUpdated(
                        foreignRunner,
                        new List<SessionInfo>
                        {
                            CreateSession(
                                "room-alpha",
                                CreateCompatibleProperties(
                                    compatibility,
                                    "Alpha Room",
                                    NetworkLobbyTopology.ClientServer))
                        });

                    Assert.AreEqual(0, context.Service.Sessions.Count);
                    Assert.AreEqual(0, changedCount);
                }
                finally
                {
                    Object.DestroyImmediate(foreignObject);
                }
            }
        }

        [Test]
        public void SessionList_CallbackWithStaleDiscoveryGeneration_IsIgnored()
        {
            NetworkLobbyCompatibilityProfile compatibility = CreateProfile();
            using (DiscoveryContext context = DiscoveryContext.Create(
                compatibility,
                new NetworkLobbyQuery(string.Empty, NetworkLobbyTopology.ClientServer),
                generation: 7))
            {
                int changedCount = 0;
                context.Service.SessionsChanged += () => changedCount++;

                context.SetMarkerGeneration(8);
                context.Receive(new List<SessionInfo>
                {
                    CreateSession(
                        "room-alpha",
                        CreateCompatibleProperties(
                            compatibility,
                            "Alpha Room",
                            NetworkLobbyTopology.ClientServer))
                });
                Assert.AreEqual(
                    0,
                    context.Service.Sessions.Count,
                    "A snapshot from a superseded discovery runner must be discarded.");
                Assert.AreEqual(0, changedCount);

                context.SetMarkerGeneration(7);
                context.Receive(new List<SessionInfo>
                {
                    CreateSession(
                        "room-alpha",
                        CreateCompatibleProperties(
                            compatibility,
                            "Alpha Room",
                            NetworkLobbyTopology.ClientServer))
                });
                Assert.AreEqual(
                    1,
                    context.Service.Sessions.Count,
                    "The current discovery runner must still be accepted.");
                Assert.AreEqual(1, changedCount);
            }
        }

        [Test]
        public void SessionList_NullRunner_IsIgnored()
        {
            NetworkLobbyCompatibilityProfile compatibility = CreateProfile();
            using (DiscoveryContext context = DiscoveryContext.Create(
                compatibility,
                new NetworkLobbyQuery(string.Empty, NetworkLobbyTopology.ClientServer)))
            {
                context.Service.OnSessionListUpdated(
                    null,
                    new List<SessionInfo>
                    {
                        CreateSession(
                            "room-alpha",
                            CreateCompatibleProperties(
                                compatibility,
                                "Alpha Room",
                                NetworkLobbyTopology.ClientServer))
                    });

                Assert.AreEqual(0, context.Service.Sessions.Count);
            }
        }

        [Test]
        public void LobbyService_CustomLobbyName_NormalizesBlankToTheDefaultPhotonLobby()
        {
            var serviceObject = new GameObject("Lobby Custom Lobby Name Test");
            try
            {
                FusionLobbyService service = serviceObject.AddComponent<FusionLobbyService>();
                Assert.AreEqual(
                    FusionSessionStartOptions.DefaultCustomLobbyName,
                    service.CustomLobbyName,
                    "The serialized default must match the project's documented custom lobby.");
                Assert.AreEqual("gc2-networking", FusionSessionStartOptions.DefaultCustomLobbyName);

                SetField(service, "m_CustomLobbyName", "  Trimmed Lobby  ");
                Assert.AreEqual("Trimmed Lobby", service.CustomLobbyName);

                SetField(service, "m_CustomLobbyName", "   ");
                Assert.IsNull(
                    service.CustomLobbyName,
                    "Blank selects Fusion's default Photon lobby for discovery and creation.");

                SetField(service, "m_CustomLobbyName", null);
                Assert.IsNull(service.CustomLobbyName);
            }
            finally
            {
                Object.DestroyImmediate(serviceObject);
            }
        }

        [Test]
        public void SessionBootstrap_DefaultStartOptions_PublishInTheSharedCustomLobby()
        {
            var bootstrapObject = new GameObject("Fusion Session Bootstrap Lobby Test");
            try
            {
                FusionSessionBootstrap bootstrap =
                    bootstrapObject.AddComponent<FusionSessionBootstrap>();
                MethodInfo createOptions = typeof(FusionSessionBootstrap).GetMethod(
                    "CreateDefaultStartOptions",
                    InstanceNonPublic);
                Assert.IsNotNull(createOptions);

                var options = (FusionSessionStartOptions)createOptions.Invoke(
                    bootstrap,
                    new object[] { "Run It" });
                Assert.AreEqual(
                    FusionSessionStartOptions.DefaultCustomLobbyName,
                    options.CustomLobbyName,
                    "Default bootstrap starts must be discoverable by the shared lobby browser.");
                Assert.AreEqual("Run It", options.SessionName);

                SetField(bootstrap, "m_CustomLobbyName", "  Trimmed Lobby  ");
                options = (FusionSessionStartOptions)createOptions.Invoke(
                    bootstrap,
                    new object[] { "Run It" });
                Assert.AreEqual("Trimmed Lobby", options.CustomLobbyName);

                SetField(bootstrap, "m_CustomLobbyName", "   ");
                options = (FusionSessionStartOptions)createOptions.Invoke(
                    bootstrap,
                    new object[] { "Run It" });
                Assert.IsNull(
                    options.CustomLobbyName,
                    "Blank restores Photon's default lobby for deliberate isolation.");
            }
            finally
            {
                Object.DestroyImmediate(bootstrapObject);
            }
        }

        [Test]
        public void SessionStartInstruction_CarriesTheResolvedBootstrapCustomLobby()
        {
            var bootstrapObject = new GameObject("Fusion Session Instruction Lobby Test");
            try
            {
                FusionSessionBootstrap bootstrap =
                    bootstrapObject.AddComponent<FusionSessionBootstrap>();
                MethodInfo createOptions = typeof(InstructionFusionStartSession).GetMethod(
                    "CreateStartOptions",
                    StaticNonPublic);
                Assert.IsNotNull(
                    createOptions,
                    "Fusion start-session instructions must build their options in one place.");

                var options = (FusionSessionStartOptions)createOptions.Invoke(
                    null,
                    new object[] { bootstrap, "room", "eu", false });
                Assert.AreEqual(
                    FusionSessionStartOptions.DefaultCustomLobbyName,
                    options.CustomLobbyName,
                    "Instruction-started sessions must land in the shared custom lobby.");
                Assert.AreEqual("eu", options.Region);

                SetField(bootstrap, "m_CustomLobbyName", "custom-partition");
                options = (FusionSessionStartOptions)createOptions.Invoke(
                    null,
                    new object[] { bootstrap, "room", "eu", false });
                Assert.AreEqual("custom-partition", options.CustomLobbyName);
            }
            finally
            {
                Object.DestroyImmediate(bootstrapObject);
            }
        }

        [Test]
        public void LobbyService_DiscoveryCreateAndJoin_BindToTheSameSerializedCustomLobby()
        {
            string source = File.ReadAllText(Path.Combine(
                Application.dataPath,
                "Arawn/NetworkingLayerForGC2/Runtime/Transport/Fusion/FusionLobbyService.cs"));

            Assert.AreEqual(
                2,
                Regex.Matches(source, "customLobbyName: CustomLobbyName").Count,
                "Create and join must publish/target the serialized custom lobby.");
            Assert.IsTrue(
                Regex.IsMatch(
                    source,
                    @"JoinSessionLobby\(\s*[^;]*?CustomLobbyName",
                    RegexOptions.Singleline),
                "Discovery must browse the same custom lobby the service creates sessions in; " +
                "discovering a different lobby makes every browse result empty.");
            Assert.IsTrue(
                Regex.IsMatch(
                    source,
                    @"ResolveSessionLobby\(\s*m_ActiveQuery\.Topology\s*,\s*CustomLobbyName\s*\)"),
                "Fusion ignores the lobby ID for the prebuilt ClientServer/Shared lobbies; " +
                "custom discovery must select SessionLobby.Custom.");
        }

        [Test]
        public void LobbyService_DiscoveryJoinsTheCustomPhotonLobbyWhenConfigured()
        {
            MethodInfo resolveLobby = typeof(FusionLobbyService).GetMethod(
                "ResolveSessionLobby",
                StaticNonPublic);
            Assert.IsNotNull(resolveLobby);

            Assert.AreEqual(
                SessionLobby.Custom,
                (SessionLobby)resolveLobby.Invoke(
                    null,
                    new object[] { NetworkLobbyTopology.Shared, "gc2-networking" }),
                "Fusion ignores the lobby ID for the prebuilt Shared/ClientServer lobbies; " +
                "custom discovery must join SessionLobby.Custom or the list stays empty.");
            Assert.AreEqual(
                SessionLobby.Custom,
                (SessionLobby)resolveLobby.Invoke(
                    null,
                    new object[] { NetworkLobbyTopology.ClientServer, "gc2-networking" }));
            Assert.AreEqual(
                SessionLobby.Shared,
                (SessionLobby)resolveLobby.Invoke(
                    null,
                    new object[] { NetworkLobbyTopology.Shared, null }));
            Assert.AreEqual(
                SessionLobby.ClientServer,
                (SessionLobby)resolveLobby.Invoke(
                    null,
                    new object[] { NetworkLobbyTopology.ClientServer, null }));
        }

        private static NetworkLobbyCompatibilityProfile CreateProfile()
        {
            var profile = new NetworkLobbyCompatibilityProfile();
            SetField(profile, "m_ProductId", TestProduct);
            SetField(profile, "m_BuildId", TestBuild);
            SetField(profile, "m_ProtocolVersion", TestProtocol);
            SetField(profile, "m_RequireBuildMatch", true);
            return profile;
        }

        private static Dictionary<string, SessionProperty> CreateCompatibleProperties(
            NetworkLobbyCompatibilityProfile compatibility,
            string displayName,
            NetworkLobbyTopology topology)
        {
            return new Dictionary<string, SessionProperty>
            {
                [NetworkLobbyCompatibilityProfile.ProductKey] =
                    SessionProperty.Convert(compatibility.ProductId),
                [NetworkLobbyCompatibilityProfile.BuildKey] =
                    SessionProperty.Convert(compatibility.BuildId),
                [NetworkLobbyCompatibilityProfile.ProtocolKey] =
                    SessionProperty.Convert(compatibility.ProtocolVersion),
                [NetworkLobbyCompatibilityProfile.DisplayNameKey] =
                    SessionProperty.Convert(displayName),
                [NetworkLobbyCompatibilityProfile.TopologyKey] =
                    SessionProperty.Convert(
                        topology == NetworkLobbyTopology.Shared
                            ? "shared"
                            : "client-server")
            };
        }

        private static SessionInfo CreateSession(
            string name,
            Dictionary<string, SessionProperty> properties,
            string region = "",
            int playerCount = 1,
            int maxPlayers = 8,
            bool isOpen = true,
            bool isVisible = true)
        {
            Assert.IsNotNull(SessionInfoConstructor);
            var session = (SessionInfo)SessionInfoConstructor.Invoke(new object[] { null });
            SetProperty(session, "Name", name);
            SetProperty(session, "Region", region);
            SetProperty(session, "PlayerCount", playerCount);
            SetProperty(session, "MaxPlayers", maxPlayers);
            // Fusion's public IsOpen/IsVisible setters require a live runner and silently
            // no-op for synthetic snapshots, so seed the internal fields behind the getters.
            SetField(session, "_isOpen", isOpen);
            SetField(session, "_isVisible", isVisible);
            SetProperty(
                session,
                "Properties",
                new ReadOnlyDictionary<string, SessionProperty>(
                    properties ?? new Dictionary<string, SessionProperty>()));
            return session;
        }

        private static void SetProperty(object target, string name, object value)
        {
            PropertyInfo property = target.GetType().GetProperty(
                name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            MethodInfo setter = property?.GetSetMethod(true);
            Assert.IsNotNull(setter, $"Missing setter for {target.GetType().Name}.{name}.");
            setter.Invoke(target, new[] { value });
        }

        private static void SetField(object target, string name, object value)
        {
            FieldInfo field = target.GetType().GetField(name, InstanceNonPublic);
            Assert.IsNotNull(field, $"Missing field {target.GetType().Name}.{name}.");
            field.SetValue(target, value);
        }

        /// <summary>
        /// Owns a <see cref="FusionLobbyService"/> and a synthetic discovery runner whose marker
        /// generation matches the service, exactly like <c>CreateDiscoveryRunner</c> produces.
        /// </summary>
        private sealed class DiscoveryContext : IDisposable
        {
            private readonly GameObject m_ServiceObject;
            private readonly GameObject m_RunnerObject;

            private DiscoveryContext(
                FusionLobbyService service,
                NetworkRunner runner,
                GameObject serviceObject,
                GameObject runnerObject)
            {
                Service = service;
                Runner = runner;
                m_ServiceObject = serviceObject;
                m_RunnerObject = runnerObject;
            }

            public FusionLobbyService Service { get; }
            public NetworkRunner Runner { get; }

            public static DiscoveryContext Create(
                NetworkLobbyCompatibilityProfile compatibility,
                NetworkLobbyQuery query,
                int generation = 7)
            {
                var serviceObject = new GameObject("Fusion Lobby Session List Test Service");
                var runnerObject = new GameObject("Fusion Lobby Session List Test Runner");
                NetworkRunner runner = runnerObject.AddComponent<NetworkRunner>();
                FusionLobbyService service = serviceObject.AddComponent<FusionLobbyService>();
                runnerObject.AddComponent<FusionLobbyDiscoveryRunnerMarker>();

                SetField(service, "m_DiscoveryRunner", runner);
                SetField(service, "m_DiscoveryGeneration", generation);
                SetField(service, "m_ActiveQuery", query);
                SetField(service, "m_Compatibility", compatibility);

                var context = new DiscoveryContext(
                    service,
                    runner,
                    serviceObject,
                    runnerObject);
                context.SetMarkerGeneration(generation);
                return context;
            }

            public void SetMarkerGeneration(int generation)
            {
                FusionLobbyDiscoveryRunnerMarker marker =
                    Runner.GetComponent<FusionLobbyDiscoveryRunnerMarker>();
                Assert.IsNotNull(marker);
                Assert.IsNotNull(MarkerInitialize);
                MarkerInitialize.Invoke(marker, new object[] { generation });
            }

            public void Receive(List<SessionInfo> sessions)
            {
                Service.OnSessionListUpdated(Runner, sessions);
            }

            public void Dispose()
            {
                Object.DestroyImmediate(m_ServiceObject);
                Object.DestroyImmediate(m_RunnerObject);
            }
        }
    }
}
