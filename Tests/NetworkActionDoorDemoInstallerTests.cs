using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using GameCreator.Runtime.Characters;
using GameCreator.Runtime.Common;
using GameCreator.Runtime.VisualScripting;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Arawn.GameCreator2.Networking.Tests
{
    public sealed class NetworkActionDoorDemoInstallerTests
    {
        private const BindingFlags InstanceFields =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static readonly InstallerSpec[] Cases =
        {
            new(
                "Fusion",
                "GC2NetworkingLayerFusionTransport.CoreExamples",
                "Requires GC2 Core Demos - FusionCoreVariablesDemo.unity",
                "Requires GC2 Core Demos - FusionRagdollDemo.unity",
                "FusionDemoPlayer-CoreAndVariables 3.prefab",
                "FusionDemo_DoorOpenState.asset",
                "Assets/Arawn/NetworkingLayerForGC2/Demo/Fusion/Packages/Core/" +
                "Package.unitypackage",
                "Assets/Arawn/NetworkingLayerForGC2/Demo/Fusion/Packages/Core/" +
                "GC2NetworkingLayerFusionTransport.CoreExamples.asset",
                "Arawn.GameCreator2.Networking.Transport.Fusion.FusionNetworkIdentity, " +
                "Arawn.GameCreator2.Networking.Transport.Fusion",
                "Arawn.GameCreator2.Networking.Transport.Fusion." +
                "FusionNetworkActionTransportBridge, " +
                "Arawn.GameCreator2.Networking.Transport.Fusion",
                "Arawn.GameCreator2.Networking.Transport.Fusion.FusionPlayerSpawner, " +
                "Arawn.GameCreator2.Networking.Transport.Fusion",
                5),
            new(
                "PurrNet",
                "GC2NetworkingLayerPurrNetTransport.CoreExamples",
                "Requires GC2 Core Demos - PurrNetCoreVariablesDemo.unity",
                "Requires GC2 Core Demos - PurrNetRagdollDemo.unity",
                "PurrNetDemoPlayer-CoreAndVariables 3.prefab",
                "PurrNetDemo_DoorOpenState.asset",
                "Assets/Arawn/NetworkingLayerForGC2/Demo/PurrNet/Packages/Core/" +
                "Package.unitypackage",
                "Assets/Arawn/NetworkingLayerForGC2/Demo/PurrNet/Packages/Core/" +
                "GC2NetworkingLayerPurrNetTransport.CoreExamples.asset",
                "PurrNet.NetworkIdentity, PurrNet.Runtime",
                "Arawn.GameCreator2.Networking.Transport.PurrNet." +
                "PurrNetNetworkActionTransportBridge, " +
                "Arawn.GameCreator2.Networking.Transport.PurrNet",
                "Arawn.GameCreator2.Networking.Transport.PurrNet.PurrNetDemoPlayerSpawner, " +
                "Arawn.GameCreator2.Networking.Transport.PurrNet",
                8)
        };

        public static IEnumerable<InstallerSpec> InstallerCases => Cases;

        [TestCaseSource(nameof(InstallerCases))]
        [NUnit.Framework.Category("GC2Networking.Ragdoll")]
        public void CoreInstaller_IsVersion110AndContainsNoStaleRoot(InstallerSpec spec)
        {
            string descriptor = File.ReadAllText(ProjectPath(spec.DescriptorPath))
                .Replace("\r\n", "\n");
            StringAssert.Contains(
                "m_Version:\n      major: 1\n      minor: 1\n      patch: 0",
                descriptor);

            string installs = ProjectPath("Assets/Plugins/GameCreator/Installs");
            string[] roots = Directory.GetDirectories(installs, spec.InstallId + "@*")
                .Select(Path.GetFileName)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
            CollectionAssert.AreEqual(
                new[] { spec.InstallId + "@1.1.0" },
                roots,
                $"{spec.Transport} Core Examples contains a stale installed version.");
        }

        [TestCaseSource(nameof(InstallerCases))]
        public void CoreScene_AuthorsReplicatedDoorWithoutAnotherIdentity(InstallerSpec spec)
        {
            Scene scene = EditorSceneManager.OpenScene(spec.ScenePath, OpenSceneMode.Additive);
            try
            {
                NetworkActionManager[] managers = Find<NetworkActionManager>(scene);
                Assert.That(managers, Has.Length.EqualTo(1));

                Type bridgeType = ResolveType(spec.ActionBridgeType);
                Assert.That(Find(scene, bridgeType), Has.Length.EqualTo(1));

                NetworkActionEndpoint[] endpoints = Find<NetworkActionEndpoint>(scene);
                Assert.That(endpoints, Has.Length.EqualTo(1));
                NetworkActionEndpoint endpoint = endpoints[0];
                Assert.That(endpoint.name, Is.EqualTo("Replicated Door Endpoint"));
                Assert.That(endpoint.EndpointId, Is.Not.Null.And.Not.Empty);
                Assert.That(endpoint.EndpointHash, Is.Not.Zero,
                    "The packaged endpoint needs a stable authored identity in player builds.");

                Type identityType = ResolveType(spec.IdentityType);
                Component[] identities = Find(scene, identityType);
                Assert.That(identities, Has.Length.EqualTo(1),
                    "The door must reuse the existing Scene Network Variables identity.");
                Assert.That(identities[0].name, Is.EqualTo("Scene Network Variables"));
                Assert.That(endpoint.GetComponent(identityType), Is.Null,
                    "The endpoint must not add a second transport identity.");
                Assert.That(endpoint.GetComponentInParent(identityType),
                    Is.SameAs(identities[0]));

                Assert.That(FindGameObject(scene, "Door Panel").GetComponent<Collider>(),
                    Is.Not.Null,
                    "The replicated door panel should still participate in gameplay collision.");
                Assert.That(FindGameObject(scene, "Door Frame Left").GetComponent<Collider>(),
                    Is.Null,
                    "A decorative hinge-frame collider would reject valid line-of-sight requests.");
                Assert.That(FindGameObject(scene, "Door Frame Right").GetComponent<Collider>(),
                    Is.Null);
                Assert.That(FindGameObject(scene, "Door Frame Top").GetComponent<Collider>(),
                    Is.Null);

                Assert.That(endpoint.Actions.Count, Is.EqualTo(1));
                NetworkActionBinding binding = endpoint.Actions[0];
                NetworkActionDefinition action = binding.Definition;
                AssertDoorDefinition(action);
                AssertApplyRotationList(binding, "m_OnApplied", endpoint.gameObject);
                AssertApplyRotationList(binding, "m_OnSnapshotApplied", endpoint.gameObject);

                NetworkActionDoorDemoUI[] demoUis = Find<NetworkActionDoorDemoUI>(scene);
                Assert.That(demoUis, Has.Length.EqualTo(1));
                NetworkActionDoorDemoUI demoUi = demoUis[0];
                Assert.That(ReadField<NetworkActionEndpoint>(demoUi, "m_DoorEndpoint"),
                    Is.SameAs(endpoint));
                Assert.That(ReadField<NetworkActionDefinition>(demoUi, "m_DoorState"),
                    Is.SameAs(action));

                Actions open = ReadField<Actions>(demoUi, "m_OpenActions");
                Actions close = ReadField<Actions>(demoUi, "m_CloseActions");
                Assert.That(open, Is.Not.Null);
                Assert.That(close, Is.Not.Null.And.Not.SameAs(open));
                AssertAbsoluteStateAction(open, endpoint, action, true);
                AssertAbsoluteStateAction(close, endpoint, action, false);
            }
            finally
            {
                EditorSceneManager.CloseScene(scene, true);
            }
        }

        [TestCaseSource(nameof(InstallerCases))]
        public void CorePackage_ContainsCurrentDoorSceneAndDefinition(InstallerSpec spec)
        {
            Dictionary<string, byte[]> payloads = ReadUnityPackageAssetPayloads(
                ProjectPath(spec.PackagePath));
            AssertCurrentPayload(payloads, spec.ScenePath);
            AssertCurrentPayload(payloads, spec.ActionPath);

            Assert.That(payloads.Keys.Any(path => path.StartsWith(
                    "Assets/Plugins/GameCreator/Installs/" + spec.InstallId + "@1.0.1",
                    StringComparison.Ordinal)),
                Is.False,
                "The rebuilt package must not preserve 1.0.1 pathnames.");
        }

        [TestCaseSource(nameof(InstallerCases))]
        [NUnit.Framework.Category("GC2Networking.Ragdoll")]
        public void CoreRagdollScene_UsesAuthoritativeGc2ActionsAndConfiguredPlayer(
            InstallerSpec spec)
        {
            Scene scene = EditorSceneManager.OpenScene(
                spec.RagdollScenePath,
                OpenSceneMode.Additive);
            try
            {
                NetworkRagdollDemoUI[] demoUis = Find<NetworkRagdollDemoUI>(scene);
                Assert.That(demoUis, Has.Length.EqualTo(1));
                NetworkRagdollDemoUI demoUi = demoUis[0];

                Actions start = ReadField<Actions>(demoUi, "m_StartActions");
                Actions recover = ReadField<Actions>(demoUi, "m_RecoverActions");
                AssertNetworkRagdollAction<InstructionNetworkCoreStartRagdoll>(start);
                AssertNetworkRagdollAction<InstructionNetworkCoreRecoverRagdoll>(recover);

                Type spawnerType = ResolveType(spec.SpawnerType);
                Component[] spawners = Find(scene, spawnerType);
                Assert.That(spawners, Has.Length.EqualTo(1));
                object configuredPrefab = ReadField<object>(spawners[0], "m_PlayerPrefab");
                GameObject configuredPlayer = configuredPrefab switch
                {
                    GameObject gameObject => gameObject,
                    Component component => component.gameObject,
                    _ => null
                };
                Assert.That(configuredPlayer, Is.Not.Null);
                Assert.That(
                    AssetDatabase.GetAssetPath(configuredPlayer),
                    Is.EqualTo(spec.PlayerPrefabPath));

                foreach (string removedName in new[]
                         {
                             "Scene Network Variables",
                             "Variable Values UI",
                             "Network Actions Door Demo",
                             "Network Actions Door Demo UI",
                             "Network Action Manager",
                             "Network Variable Manager"
                         })
                {
                    Assert.That(
                        FindOptionalGameObject(scene, removedName),
                        Is.Null,
                        $"The focused ragdoll scene retained '{removedName}'.");
                }
            }
            finally
            {
                EditorSceneManager.CloseScene(scene, true);
            }

            GameObject player = AssetDatabase.LoadAssetAtPath<GameObject>(
                spec.PlayerPrefabPath);
            Assert.That(player, Is.Not.Null);
            Character character = player.GetComponent<Character>();
            NetworkCharacter networkCharacter = player.GetComponent<NetworkCharacter>();
            Assert.That(character, Is.Not.Null);
            Assert.That(networkCharacter, Is.Not.Null);
            Assert.That(networkCharacter.ActorType,
                Is.EqualTo(NetworkCharacterActorType.PlayerOwned));
            Assert.That(networkCharacter.RagdollMode,
                Is.EqualTo(NetworkCharacter.RemoteSystemMode.Synchronized));
            Assert.That(ReadField<bool>(networkCharacter, "m_UseCoreNetworking"), Is.True);

            RagdollDefault ragdoll = character.Ragdoll.Get<RagdollDefault>();
            Assert.That(ragdoll, Is.Not.Null,
                "The demo player must use GC2's physical RagdollDefault strategy.");
            BoneRack rack = ReadField<BoneRack>(ragdoll, "m_BoneRack");
            Assert.That(rack, Is.Not.Null);
            Assert.That(rack.Skeleton, Is.Not.Null);
            Assert.That(ReadField<AnimationClip>(ragdoll, "m_RecoverFaceDown"), Is.Not.Null);
            Assert.That(ReadField<AnimationClip>(ragdoll, "m_RecoverFaceUp"), Is.Not.Null);
        }

        [TestCaseSource(nameof(InstallerCases))]
        [NUnit.Framework.Category("GC2Networking.Ragdoll")]
        public void CorePackage_ContainsExactCurrentRagdollPayload(InstallerSpec spec)
        {
            Dictionary<string, byte[]> payloads = ReadUnityPackageAssetPayloads(
                ProjectPath(spec.PackagePath));
            AssertCurrentPayload(payloads, spec.RagdollScenePath);
            AssertCurrentPayload(payloads, spec.PlayerPrefabPath);
            Assert.That(payloads, Has.Count.EqualTo(spec.ExpectedAssetPayloadCount));
            Assert.That(payloads.Keys.All(path => path.StartsWith(
                    spec.Root + "/",
                    StringComparison.Ordinal)),
                Is.True,
                "The Core Examples archive must contain only its exact install-root assets.");
            Assert.That(payloads.Keys.Any(path => path.Contains(
                    spec.InstallId + "@1.0.2",
                    StringComparison.Ordinal)),
                Is.False);
        }

        private static void AssertNetworkRagdollAction<TInstruction>(Actions actions)
            where TInstruction : TInstructionNetworkCoreRequest
        {
            Assert.That(actions, Is.Not.Null);
            InstructionList instructions = ReadField<InstructionList>(actions, "m_Instructions");
            Assert.That(instructions.Length, Is.EqualTo(1));
            Assert.That(instructions.Get(0), Is.TypeOf<TInstruction>());
            TInstruction instruction = (TInstruction)instructions.Get(0);
            Assert.That(ReadField<bool>(instruction, "m_WaitForResponse"), Is.True);

            PropertyGetGameObject target = ReadField<PropertyGetGameObject>(
                instruction,
                "m_Character");
            object property = ReadField<object>(target, "m_Property");
            Assert.That(property, Is.TypeOf<GetGameObjectLocalNetworkPlayer>(),
                "The demo Actions must target the authenticated local Network Character.");
        }

        private static void AssertDoorDefinition(NetworkActionDefinition action)
        {
            Assert.That(action, Is.Not.Null);
            Assert.That(action.ActionId, Is.EqualTo("gc2.demo.door.open"));
            Assert.That(action.SchemaVersion, Is.EqualTo(1));
            Assert.That(action.PayloadType, Is.EqualTo(NetworkActionPayloadType.Boolean));
            Assert.That(action.EffectKind, Is.EqualTo(NetworkActionEffectKind.PersistentState));
            Assert.That(action.AuthorityPolicy,
                Is.EqualTo(NetworkActionAuthorityPolicy.OwnerRequest));
            Assert.That(action.RecipientPolicy,
                Is.EqualTo(NetworkActionRecipientPolicy.RelevantObservers));
            Assert.That(action.Reliable, Is.True);
            Assert.That(action.IncludeInLateJoinSnapshot, Is.True);
            Assert.That(action.MaximumDistance, Is.EqualTo(4f).Within(0.001f));
            Assert.That(action.RequireLineOfSight, Is.True);
            Assert.That(action.InitialPayload,
                Is.EqualTo(NetworkActionPayload.FromBoolean(false)));
        }

        private static void AssertApplyRotationList(
            NetworkActionBinding binding,
            string fieldName,
            GameObject endpoint)
        {
            RunInstructionsList runner = ReadField<RunInstructionsList>(binding, fieldName);
            InstructionList instructions = ReadField<InstructionList>(runner, "m_Instructions");
            Assert.That(instructions.Length, Is.EqualTo(1));
            Assert.That(instructions.Get(0), Is.TypeOf<InstructionNetworkApplyBooleanRotation>());
            var rotation = (InstructionNetworkApplyBooleanRotation)instructions.Get(0);
            PropertyGetGameObject target = ReadField<PropertyGetGameObject>(rotation, "m_Target");
            Assert.That(target.Get(new Args(endpoint, endpoint)), Is.SameAs(endpoint));
        }

        private static void AssertAbsoluteStateAction(
            Actions actions,
            NetworkActionEndpoint endpoint,
            NetworkActionDefinition action,
            bool expected)
        {
            InstructionList instructions = ReadField<InstructionList>(actions, "m_Instructions");
            Assert.That(instructions.Length, Is.EqualTo(1));
            Assert.That(instructions.Get(0), Is.TypeOf<InstructionNetworkSetObjectState>());
            var instruction = (InstructionNetworkSetObjectState)instructions.Get(0);
            Assert.That(ReadField<NetworkActionDefinition>(instruction, "m_Action"),
                Is.SameAs(action));
            Assert.That(ReadField<bool>(instruction, "m_WaitForResponse"), Is.True);

            var args = new Args(actions.gameObject, actions.gameObject);
            PropertyGetGameObject target =
                ReadField<PropertyGetGameObject>(instruction, "m_Target");
            Assert.That(target.Get(args), Is.SameAs(endpoint.gameObject));
            NetworkActionPropertyPayload payload =
                ReadField<NetworkActionPropertyPayload>(instruction, "m_Payload");
            Assert.That(payload.Get(action, args),
                Is.EqualTo(NetworkActionPayload.FromBoolean(expected)));
        }

        private static void AssertCurrentPayload(
            IReadOnlyDictionary<string, byte[]> payloads,
            string assetPath)
        {
            Assert.That(payloads.TryGetValue(assetPath, out byte[] packaged), Is.True,
                $"Installer payload does not contain '{assetPath}'.");
            byte[] current = File.ReadAllBytes(ProjectPath(assetPath));
            Assert.That(packaged.SequenceEqual(current), Is.True,
                $"Installer payload contains a stale '{assetPath}'.");
        }

        private static T ReadField<T>(object target, string fieldName)
        {
            Type type = target.GetType();
            while (type != null)
            {
                FieldInfo field = type.GetField(fieldName, InstanceFields);
                if (field != null) return (T)field.GetValue(target);
                type = type.BaseType;
            }
            throw new MissingFieldException(target.GetType().FullName, fieldName);
        }

        private static T[] Find<T>(Scene scene) where T : Component =>
            scene.GetRootGameObjects()
                .SelectMany(root => root.GetComponentsInChildren<T>(true))
                .ToArray();

        private static Component[] Find(Scene scene, Type type) =>
            scene.GetRootGameObjects()
                .SelectMany(root => root.GetComponentsInChildren(type, true))
                .Cast<Component>()
                .ToArray();

        private static GameObject FindGameObject(Scene scene, string name)
        {
            GameObject result = FindOptionalGameObject(scene, name);
            Assert.That(result, Is.Not.Null, $"Missing generated object '{name}'.");
            return result;
        }

        private static GameObject FindOptionalGameObject(Scene scene, string name)
        {
            return scene.GetRootGameObjects()
                .SelectMany(root => root.GetComponentsInChildren<Transform>(true))
                .FirstOrDefault(candidate => candidate.name == name)
                ?.gameObject;
        }

        private static Type ResolveType(string name) => Type.GetType(name, false) ??
            throw new TypeLoadException($"Could not resolve '{name}'.");

        private static string ProjectPath(string assetPath)
        {
            string root = Directory.GetParent(Application.dataPath)?.FullName;
            Assert.That(root, Is.Not.Null.And.Not.Empty);
            return Path.Combine(root, assetPath.Replace('/', Path.DirectorySeparatorChar));
        }

        private static Dictionary<string, byte[]> ReadUnityPackageAssetPayloads(
            string packagePath)
        {
            var pathnames = new Dictionary<string, string>(StringComparer.Ordinal);
            var assets = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            using FileStream file = File.OpenRead(packagePath);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            var header = new byte[512];

            while (true)
            {
                int headerBytes = ReadFully(gzip, header, header.Length);
                if (headerBytes == 0 || header.All(value => value == 0)) break;
                if (headerBytes != header.Length)
                    throw new InvalidDataException("Incomplete Unity package TAR header.");

                string entryName = Encoding.UTF8.GetString(header, 0, 100)
                    .TrimEnd('\0').TrimStart('.', '/');
                string sizeText = Encoding.ASCII.GetString(header, 124, 12)
                    .Trim('\0', ' ');
                long size = string.IsNullOrEmpty(sizeText)
                    ? 0L
                    : Convert.ToInt64(sizeText, 8);
                string[] segments = entryName.Split('/');
                bool capture = segments.Length == 2 &&
                               (segments[1] == "pathname" || segments[1] == "asset");

                if (capture)
                {
                    Assert.That(size, Is.LessThanOrEqualTo(int.MaxValue));
                    var content = new byte[(int)size];
                    if (ReadFully(gzip, content, content.Length) != content.Length)
                        throw new EndOfStreamException($"Incomplete '{entryName}' entry.");

                    if (segments[1] == "pathname")
                    {
                        pathnames[segments[0]] = Encoding.UTF8.GetString(content)
                            .TrimEnd('\0', '\r', '\n');
                    }
                    else assets[segments[0]] = content;
                }
                else
                {
                    Skip(gzip, size);
                }

                Skip(gzip, (512L - size % 512L) % 512L);
            }

            var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, string> entry in pathnames)
                if (assets.TryGetValue(entry.Key, out byte[] content))
                    result[entry.Value] = content;
            return result;
        }

        private static int ReadFully(Stream stream, byte[] buffer, int count)
        {
            int total = 0;
            while (total < count)
            {
                int read = stream.Read(buffer, total, count - total);
                if (read <= 0) break;
                total += read;
            }
            return total;
        }

        private static void Skip(Stream stream, long count)
        {
            var buffer = new byte[8192];
            while (count > 0)
            {
                int read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, count));
                if (read <= 0) throw new EndOfStreamException("Incomplete Unity package entry.");
                count -= read;
            }
        }

        public sealed class InstallerSpec
        {
            public string Transport { get; }
            public string InstallId { get; }
            public string SceneName { get; }
            public string RagdollSceneName { get; }
            public string PlayerPrefabName { get; }
            public string ActionName { get; }
            public string PackagePath { get; }
            public string DescriptorPath { get; }
            public string IdentityType { get; }
            public string ActionBridgeType { get; }
            public string SpawnerType { get; }
            public int ExpectedAssetPayloadCount { get; }
            public string Root =>
                "Assets/Plugins/GameCreator/Installs/" + InstallId + "@1.1.0";
            public string ScenePath => Root + "/" + SceneName;
            public string RagdollScenePath => Root + "/" + RagdollSceneName;
            public string PlayerPrefabPath => Root + "/" + PlayerPrefabName;
            public string ActionPath => Root + "/" + ActionName;

            public InstallerSpec(
                string transport,
                string installId,
                string sceneName,
                string ragdollSceneName,
                string playerPrefabName,
                string actionName,
                string packagePath,
                string descriptorPath,
                string identityType,
                string actionBridgeType,
                string spawnerType,
                int expectedAssetPayloadCount)
            {
                Transport = transport;
                InstallId = installId;
                SceneName = sceneName;
                RagdollSceneName = ragdollSceneName;
                PlayerPrefabName = playerPrefabName;
                ActionName = actionName;
                PackagePath = packagePath;
                DescriptorPath = descriptorPath;
                IdentityType = identityType;
                ActionBridgeType = actionBridgeType;
                SpawnerType = spawnerType;
                ExpectedAssetPayloadCount = expectedAssetPayloadCount;
            }

            public override string ToString() => Transport;
        }
    }
}
