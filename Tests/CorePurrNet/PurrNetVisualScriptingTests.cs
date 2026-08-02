using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Arawn.GameCreator2.Networking.Transport.PurrNet;
using GameCreator.Runtime.Common;
using NUnit.Framework;
using PurrNet.Transports;
using UnityEngine;
using Gc2CategoryAttribute = GameCreator.Runtime.Common.CategoryAttribute;
using PurrNetNetworkManager = PurrNet.NetworkManager;

namespace Arawn.GameCreator2.Networking.CorePurrNet.Tests
{
    public sealed class PurrNetVisualScriptingTests
    {
        private const string VisualScriptingNamespace =
            "Arawn.GameCreator2.Networking.Transport.PurrNet";

        private static readonly (Type Type, string Category)[] ExpectedTypes =
        {
            (typeof(InstructionPurrNetStartSession),
                "Network/PurrNet/Session/Start Session"),
            (typeof(InstructionPurrNetShutdownSession),
                "Network/PurrNet/Session/Shutdown Session"),
            (typeof(InstructionPurrNetSteamHostLobby),
                "Network/PurrNet/Steam Lobby/Host Lobby"),
            (typeof(InstructionPurrNetSteamJoinLobby),
                "Network/PurrNet/Steam Lobby/Join Lobby"),
            (typeof(InstructionPurrNetSteamLeaveLobby),
                "Network/PurrNet/Steam Lobby/Leave Lobby"),
            (typeof(InstructionPurrNetSteamOpenInviteOverlay),
                "Network/PurrNet/Steam Lobby/Open Invite Overlay"),

            (typeof(ConditionPurrNetConnectionStateIs),
                "Network/PurrNet/Connection/State Is"),
            (typeof(ConditionPurrNetLocalPlayerReady),
                "Network/PurrNet/Player/Local Player Is Ready"),
            (typeof(ConditionPurrNetSteamAvailable),
                "Network/PurrNet/Steam Lobby/Is Available"),
            (typeof(ConditionPurrNetSteamReady),
                "Network/PurrNet/Steam Lobby/Is Ready"),
            (typeof(ConditionPurrNetSteamBusy),
                "Network/PurrNet/Steam Lobby/Is Busy"),
            (typeof(ConditionPurrNetSteamHasLobby),
                "Network/PurrNet/Steam Lobby/Has Lobby"),
            (typeof(ConditionPurrNetSteamLobbyStateIs),
                "Network/PurrNet/Steam Lobby/State Is"),

            (typeof(EventPurrNetClientConnectionState),
                "Network/PurrNet/Connection/On Client State"),
            (typeof(EventPurrNetServerConnectionState),
                "Network/PurrNet/Connection/On Server State"),
            (typeof(EventPurrNetHostReady),
                "Network/PurrNet/Session/On Host Ready"),
            (typeof(EventPurrNetSteamReady),
                "Network/PurrNet/Steam Lobby/On Steam Ready"),
            (typeof(EventPurrNetSteamLobbyCreated),
                "Network/PurrNet/Steam Lobby/On Lobby Created"),
            (typeof(EventPurrNetSteamSessionConnected),
                "Network/PurrNet/Steam Lobby/On Session Connected"),
            (typeof(EventPurrNetSteamLobbyLeft),
                "Network/PurrNet/Steam Lobby/On Lobby Left"),
            (typeof(EventPurrNetSteamLobbyError),
                "Network/PurrNet/Steam Lobby/On Error"),
            (typeof(EventPurrNetSteamLobbyState),
                "Network/PurrNet/Steam Lobby/On State"),

            (typeof(GetBoolPurrNetLocalPlayerReady),
                "Network/PurrNet/Player/Local Player Is Ready"),
            (typeof(GetBoolPurrNetTransportSupported),
                "Network/PurrNet/Connection/Transport Is Supported"),
            (typeof(GetBoolPurrNetSteamAvailable),
                "Network/PurrNet/Steam Lobby/Is Available"),
            (typeof(GetBoolPurrNetSteamReady),
                "Network/PurrNet/Steam Lobby/Is Ready"),
            (typeof(GetBoolPurrNetSteamBusy),
                "Network/PurrNet/Steam Lobby/Is Busy"),
            (typeof(GetBoolPurrNetSteamHasLobby),
                "Network/PurrNet/Steam Lobby/Has Lobby"),
            (typeof(GetBoolPurrNetSteamSessionConnected),
                "Network/PurrNet/Steam Lobby/Session Is Connected"),

            (typeof(GetStringPurrNetServerConnectionState),
                "Network/PurrNet/Connection/Server State"),
            (typeof(GetStringPurrNetClientConnectionState),
                "Network/PurrNet/Connection/Client State"),
            (typeof(GetStringPurrNetTransportType),
                "Network/PurrNet/Connection/Transport Type"),
            (typeof(GetStringPurrNetSteamLobbyState),
                "Network/PurrNet/Steam Lobby/State"),
            (typeof(GetStringPurrNetSteamLobbyId),
                "Network/PurrNet/Steam Lobby/Lobby ID"),
            (typeof(GetStringPurrNetLocalSteamId),
                "Network/PurrNet/Steam Lobby/Local Steam ID"),
            (typeof(GetStringPurrNetSteamLobbyStatus),
                "Network/PurrNet/Steam Lobby/Status"),
            (typeof(GetStringPurrNetSteamLobbyLastError),
                "Network/PurrNet/Steam Lobby/Last Error")
        };

        [Test]
        public void VisualScriptingTypes_HaveCompleteGc2DiscoveryMetadata()
        {
            Type[] discovered = typeof(InstructionPurrNetStartSession).Assembly
                .GetTypes()
                .Where(IsConcreteVisualScriptingType)
                .OrderBy(type => type.FullName)
                .ToArray();
            Type[] expected = ExpectedTypes
                .Select(entry => entry.Type)
                .OrderBy(type => type.FullName)
                .ToArray();

            CollectionAssert.AreEqual(
                expected,
                discovered,
                "Update this coverage table whenever a PurrNet GC2 visual scripting type is added or removed.");

            foreach ((Type type, string expectedCategory) in ExpectedTypes)
            {
                TitleAttribute title = type.GetCustomAttribute<TitleAttribute>(false);
                Gc2CategoryAttribute category =
                    type.GetCustomAttribute<Gc2CategoryAttribute>(false);

                Assert.That(title, Is.Not.Null, $"{type.FullName} needs a GC2 Title attribute.");
                Assert.That(title.Title, Is.Not.Empty, $"{type.FullName} has an empty GC2 title.");
                Assert.That(category, Is.Not.Null, $"{type.FullName} needs a GC2 Category attribute.");
                Assert.That(category.ToString(), Is.EqualTo(expectedCategory), type.FullName);
                Assert.That(type.IsSerializable, Is.True, $"{type.FullName} must remain serializable.");
            }
        }

        [Test]
        public void SteamLobbyIds_RemainLosslessStringProperties()
        {
            Type propertyType = typeof(GetStringPurrNetSteamLobbyId);
            MethodInfo getter = propertyType.GetMethod(
                nameof(GetStringPurrNetSteamLobbyId.Get),
                BindingFlags.Instance | BindingFlags.Public,
                null,
                new[] { typeof(Args) },
                null);
            FieldInfo joinLobbyId = typeof(InstructionPurrNetSteamJoinLobby).GetField(
                "m_LobbyId",
                BindingFlags.Instance | BindingFlags.NonPublic);
            PropertyInfo coordinatorLobbyId = typeof(PurrNetSteamLobbyNetwork).GetProperty(
                nameof(PurrNetSteamLobbyNetwork.CurrentLobbyId),
                BindingFlags.Instance | BindingFlags.Public);

            Assert.That(typeof(PropertyTypeGetString).IsAssignableFrom(propertyType), Is.True);
            Assert.That(getter, Is.Not.Null);
            Assert.That(getter.ReturnType, Is.EqualTo(typeof(string)));
            Assert.That(joinLobbyId, Is.Not.Null);
            Assert.That(joinLobbyId.FieldType, Is.EqualTo(typeof(PropertyGetString)));
            Assert.That(coordinatorLobbyId, Is.Not.Null);
            Assert.That(coordinatorLobbyId.PropertyType, Is.EqualTo(typeof(string)));
        }

        [Test]
        public void VisualScriptingAssembly_HasNoOptionalSteamSdkDependency()
        {
            Assembly assembly = typeof(InstructionPurrNetSteamHostLobby).Assembly;
            string[] references = assembly
                .GetReferencedAssemblies()
                .Select(reference => reference.Name)
                .ToArray();

            Assert.That(
                references.Any(reference => reference.StartsWith(
                    "Steamworks",
                    StringComparison.OrdinalIgnoreCase)),
                Is.False,
                "The always-compiled PurrNet assembly must not reference Steamworks.NET.");
            Assert.That(
                references.Any(reference => string.Equals(
                    reference,
                    "PurrNet.Steam.Runtime",
                    StringComparison.OrdinalIgnoreCase)),
                Is.False,
                "SteamTransport remains an optional PurrNet package.");
            Assert.That(
                references.Any(reference => string.Equals(
                    reference,
                    "Arawn.GameCreator2.Networking.Transport.PurrNet.Steam",
                    StringComparison.OrdinalIgnoreCase)),
                Is.False,
                "The GC2 Inspector entries must communicate through the transport-neutral lobby coordinator.");

            foreach ((Type type, _) in ExpectedTypes)
            {
                foreach (Type memberType in GetDeclaredMemberTypes(type))
                {
                    Assert.That(
                        ContainsOptionalSteamType(memberType),
                        Is.False,
                        $"{type.FullName} exposes optional Steam type {memberType.FullName}.");
                }
            }
        }

        [Test]
        public void Properties_WithNoContext_ReturnSafeFallbacks()
        {
            Assert.That(
                UnityEngine.Object.FindObjectsByType<PurrNetNetworkManager>(
                    FindObjectsInactive.Include,
                    FindObjectsSortMode.None),
                Is.Empty,
                "This fallback test requires no scene PurrNet NetworkManager.");
            Assert.That(
                UnityEngine.Object.FindObjectsByType<PurrNetSteamLobbyNetwork>(
                    FindObjectsInactive.Include,
                    FindObjectsSortMode.None),
                Is.Empty,
                "This fallback test requires no scene Steam lobby coordinator.");

            Args args = Args.EMPTY;

            Assert.That(new GetBoolPurrNetLocalPlayerReady().Get(args), Is.False);
            Assert.That(new GetBoolPurrNetTransportSupported().Get(args), Is.False);
            Assert.That(new GetBoolPurrNetSteamAvailable().Get(args), Is.False);
            Assert.That(new GetBoolPurrNetSteamReady().Get(args), Is.False);
            Assert.That(new GetBoolPurrNetSteamBusy().Get(args), Is.False);
            Assert.That(new GetBoolPurrNetSteamHasLobby().Get(args), Is.False);
            Assert.That(new GetBoolPurrNetSteamSessionConnected().Get(args), Is.False);

            Assert.That(
                new GetStringPurrNetServerConnectionState().Get(args),
                Is.EqualTo(ConnectionState.Disconnected.ToString()));
            Assert.That(
                new GetStringPurrNetClientConnectionState().Get(args),
                Is.EqualTo(ConnectionState.Disconnected.ToString()));
            Assert.That(new GetStringPurrNetTransportType().Get(args), Is.Empty);
            Assert.That(
                new GetStringPurrNetSteamLobbyState().Get(args),
                Is.EqualTo(PurrNetSteamLobbySessionState.Unavailable.ToString()));
            Assert.That(new GetStringPurrNetSteamLobbyId().Get(args), Is.Empty);
            Assert.That(new GetStringPurrNetLocalSteamId().Get(args), Is.Empty);
            Assert.That(new GetStringPurrNetSteamLobbyStatus().Get(args), Is.Empty);
            Assert.That(new GetStringPurrNetSteamLobbyLastError().Get(args), Is.Empty);
        }

        private static bool IsConcreteVisualScriptingType(Type type)
        {
            if (type == null || type.IsAbstract || type.Namespace != VisualScriptingNamespace)
            {
                return false;
            }

            return type.Name.StartsWith("InstructionPurrNet", StringComparison.Ordinal) ||
                   type.Name.StartsWith("ConditionPurrNet", StringComparison.Ordinal) ||
                   type.Name.StartsWith("EventPurrNet", StringComparison.Ordinal) ||
                   type.Name.StartsWith("GetBoolPurrNet", StringComparison.Ordinal) ||
                   type.Name.StartsWith("GetStringPurrNet", StringComparison.Ordinal);
        }

        private static IEnumerable<Type> GetDeclaredMemberTypes(Type type)
        {
            const BindingFlags Flags = BindingFlags.Instance |
                                       BindingFlags.Static |
                                       BindingFlags.Public |
                                       BindingFlags.NonPublic |
                                       BindingFlags.DeclaredOnly;

            foreach (FieldInfo field in type.GetFields(Flags)) yield return field.FieldType;
            foreach (PropertyInfo property in type.GetProperties(Flags))
            {
                yield return property.PropertyType;
            }
            foreach (EventInfo eventInfo in type.GetEvents(Flags)) yield return eventInfo.EventHandlerType;
            foreach (ConstructorInfo constructor in type.GetConstructors(Flags))
            {
                foreach (ParameterInfo parameter in constructor.GetParameters())
                {
                    yield return parameter.ParameterType;
                }
            }
            foreach (MethodInfo method in type.GetMethods(Flags))
            {
                yield return method.ReturnType;
                foreach (ParameterInfo parameter in method.GetParameters())
                {
                    yield return parameter.ParameterType;
                }
            }
        }

        private static bool ContainsOptionalSteamType(Type type)
        {
            if (type == null) return false;
            if (type.HasElementType) return ContainsOptionalSteamType(type.GetElementType());

            string fullName = type.FullName ?? string.Empty;
            if (fullName.StartsWith("Steamworks.", StringComparison.Ordinal) ||
                fullName.StartsWith("PurrNet.Steam.", StringComparison.Ordinal) ||
                fullName.StartsWith(
                    "Arawn.GameCreator2.Networking.Transport.PurrNet.Steam.",
                    StringComparison.Ordinal))
            {
                return true;
            }

            return type.IsGenericType && type
                .GetGenericArguments()
                .Any(ContainsOptionalSteamType);
        }
    }
}
