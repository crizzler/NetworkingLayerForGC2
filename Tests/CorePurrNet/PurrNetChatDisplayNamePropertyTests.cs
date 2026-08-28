using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Arawn.GameCreator2.Networking.Transport.PurrNet;
using Arawn.GameCreator2.Networking.Transport.PurrNet.Lobby;
using GameCreator.Runtime.Common;
using NUnit.Framework;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.CorePurrNet.Tests
{
    /// <summary>
    /// Protects the GC2 PropertyGetString contract used by the PurrNet demo chat,
    /// including the two installable scenes that already author "Player".
    /// </summary>
    public sealed class PurrNetChatDisplayNamePropertyTests
    {
        private const BindingFlags InstanceNonPublic =
            BindingFlags.Instance | BindingFlags.NonPublic;

        private const string RuntimeSourcePath =
            "Assets/Arawn/NetworkingLayerForGC2/Runtime/Transport/PurrNet/" +
            "PurrNetChatBoxUI.cs";
        private const string RuntimeMetaPath = RuntimeSourcePath + ".meta";
        private const string WizardPath =
            "Assets/Arawn/NetworkingLayerForGC2/Editor/Transport/PurrNet/" +
            "PurrNetSceneSetupWizard.cs";
        private const string CoreInstallPrefix =
            "GC2NetworkingLayerPurrNetTransport.CoreExamples@";
        private const string CoreSceneName =
            "Requires GC2 Code Demos - PurrNetCharacterSelectionDemo And Chat.unity";
        private const string LobbyInstallPrefix =
            "GC2NetworkingLayerPurrNetTransport.LobbyExamples@";
        private const string LobbySceneName =
            "Optional Staging Lobby - PurrNetLanShooterStatsDemo.unity";
        private const string CorePackagePath =
            "Assets/Arawn/NetworkingLayerForGC2/Demo/PurrNet/Packages/Core/" +
            "Package.unitypackage";
        private const string LobbyPackagePath =
            "Assets/Arawn/NetworkingLayerForGC2/Demo/PurrNet/Packages/Lobby/" +
            "Package.unitypackage";
        private const string CoreInstallerDescriptorPath =
            "Assets/Arawn/NetworkingLayerForGC2/Demo/PurrNet/Packages/Core/" +
            "GC2NetworkingLayerPurrNetTransport.CoreExamples.asset";
        private const string LobbyInstallerDescriptorPath =
            "Assets/Arawn/NetworkingLayerForGC2/Demo/PurrNet/Packages/Lobby/" +
            "GC2NetworkingLayerPurrNetTransport.LobbyExamples.asset";

        [Serializable]
        private sealed class ContextDisplayName : PropertyTypeGetString
        {
            private readonly string m_Value;

            public ContextDisplayName(string value)
            {
                m_Value = value;
            }

            public override string Get(Args args) => m_Value;
            public override string Get(GameObject gameObject) => m_Value;
            public override string String => m_Value;
        }

        [Test]
        public void ChatBox_DefaultDisplayNameUsesGc2PropertyAndRuntimeContext()
        {
            FieldInfo defaultNameProperty = typeof(PurrNetChatBoxUI).GetField(
                "m_DefaultDisplayNameProperty",
                InstanceNonPublic);
            Assert.That(defaultNameProperty, Is.Not.Null);
            Assert.That(defaultNameProperty.FieldType, Is.EqualTo(typeof(PropertyGetString)),
                "PurrNet chat should expose GC2's string-property picker, not a raw string.");
            Assert.That(
                defaultNameProperty.GetCustomAttribute<SerializeField>(),
                Is.Not.Null);
            InspectorNameAttribute inspectorName =
                defaultNameProperty.GetCustomAttribute<InspectorNameAttribute>();
            Assert.That(inspectorName, Is.Not.Null);
            Assert.That(inspectorName.displayName, Is.EqualTo("Default Display Name"));

            FieldInfo legacyName = typeof(PurrNetChatBoxUI).GetField(
                "m_DefaultDisplayName",
                InstanceNonPublic);
            Assert.That(legacyName, Is.Not.Null,
                "Keep the legacy field so existing scene and prefab YAML can migrate.");
            Assert.That(legacyName.FieldType, Is.EqualTo(typeof(string)));
            Assert.That(legacyName.GetCustomAttribute<SerializeField>(), Is.Not.Null);
            Assert.That(legacyName.GetCustomAttribute<HideInInspector>(), Is.Not.Null);
            Assert.That(
                typeof(ISerializationCallbackReceiver).IsAssignableFrom(typeof(PurrNetChatBoxUI)),
                Is.True,
                "The legacy scalar requires a serialization callback migration.");
            FieldInfo migrationFlag = FindDisplayNameMigrationFlag(typeof(PurrNetChatBoxUI));
            Assert.That(migrationFlag, Is.Not.Null,
                "A serialized migration flag prevents legacy values from overwriting a " +
                "designer-selected GC2 property on every deserialize.");
            Assert.That(migrationFlag.FieldType, Is.EqualTo(typeof(bool)));
            Assert.That(migrationFlag.GetCustomAttribute<SerializeField>(), Is.Not.Null);

            var gameObject = new GameObject("PurrNet Chat Property Test");
            gameObject.SetActive(false);
            try
            {
                PurrNetChatBoxUI chat = gameObject.AddComponent<PurrNetChatBoxUI>();

                defaultNameProperty.SetValue(
                    chat,
                    new PropertyGetString("Authored PurrNet Captain"));
                legacyName.SetValue(chat, "Player");
                migrationFlag.SetValue(chat, true);
                ((ISerializationCallbackReceiver)chat).OnAfterDeserialize();
                var authored = (PropertyGetString)defaultNameProperty.GetValue(chat);
                Assert.That(authored.Get(chat), Is.EqualTo("Authored PurrNet Captain"),
                    "A completed migration must preserve a designer-authored non-Player value.");

                legacyName.SetValue(chat, "Legacy PurrNet Captain");
                migrationFlag.SetValue(chat, false);
                ((ISerializationCallbackReceiver)chat).OnAfterDeserialize();
                var migrated = (PropertyGetString)defaultNameProperty.GetValue(chat);
                Assert.That(migrated.Get(chat), Is.EqualTo("Legacy PurrNet Captain"),
                    "Existing customer YAML must migrate its authored scalar value.");
                Assert.That(migrationFlag.GetValue(chat), Is.True);

                defaultNameProperty.SetValue(
                    chat,
                    new PropertyGetString(new ContextDisplayName("PurrNet Designer")));

                Assert.That(chat.DisplayName, Is.EqualTo("PurrNet Designer"),
                    "DisplayName must evaluate the GC2 property in this component's context.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
            }

        }

        [TestCase(CoreInstallPrefix, CoreSceneName)]
        [TestCase(LobbyInstallPrefix, LobbySceneName)]
        public void AuthoredChatDemo_PreservesPlayerConstantInProperty(
            string installPrefix,
            string sceneName)
        {
            string scenePath = FindInstalledScene(installPrefix, sceneName);
            string scene = ReadAsset(scenePath);
            string component = ReadComponentByScriptGuid(scene, ReadMetaGuid(RuntimeMetaPath));

            AssertConstantPropertyValue(component, scene, "Player");
        }

        [Test]
        public void SceneSetupWizard_AuthorsTheGc2DisplayNameProperty()
        {
            string wizard = ReadAsset(WizardPath);
            StringAssert.Contains("m_DefaultDisplayNameProperty", wizard,
                "New wizard-created chat boxes must author the GC2 property field.");
            StringAssert.DoesNotContain(
                "SetString(so, \"m_DefaultDisplayName\", \"Player\")",
                wizard,
                "The wizard must not keep writing only the hidden legacy scalar.");
        }

        [Test]
        public void StagingStartup_PreservesTheChatPropertyDisplayName()
        {
            FieldInfo propertyField = typeof(PurrNetChatBoxUI).GetField(
                "m_DefaultDisplayNameProperty",
                InstanceNonPublic);
            FieldInfo migrationFlag = FindDisplayNameMigrationFlag(typeof(PurrNetChatBoxUI));
            FieldInfo nameField = typeof(PurrNetChatBoxUI).GetField(
                "m_NameField",
                InstanceNonPublic);
            FieldInfo chatField = typeof(PurrNetStagingRoomController).GetField(
                "m_ChatBox",
                InstanceNonPublic);
            MethodInfo awake = typeof(PurrNetStagingRoomController).GetMethod(
                "Awake",
                InstanceNonPublic);
            Assert.That(propertyField, Is.Not.Null);
            Assert.That(migrationFlag, Is.Not.Null);
            Assert.That(nameField, Is.Not.Null);
            Assert.That(chatField, Is.Not.Null);
            Assert.That(awake, Is.Not.Null);

            var gameObject = new GameObject("PurrNet Staging Name Test");
            gameObject.SetActive(false);
            try
            {
                PurrNetChatBoxUI chat = gameObject.AddComponent<PurrNetChatBoxUI>();
                propertyField.SetValue(
                    chat,
                    new PropertyGetString(new ContextDisplayName("GC2 Staging Captain")));
                migrationFlag.SetValue(chat, true);

                var nameFieldObject = new GameObject(
                    "Authored Name Field",
                    typeof(RectTransform));
                nameFieldObject.transform.SetParent(gameObject.transform, false);
                Component authoredNameField = nameFieldObject.AddComponent(nameField.FieldType);
                PropertyInfo textProperty = nameField.FieldType.GetProperty("text");
                Assert.That(textProperty, Is.Not.Null);
                textProperty.SetValue(authoredNameField, "Player");
                nameField.SetValue(chat, authoredNameField);

                PurrNetStagingRoomController staging =
                    gameObject.AddComponent<PurrNetStagingRoomController>();
                chatField.SetValue(staging, chat);
                awake.Invoke(staging, null);

                Assert.That(staging.LocalDisplayName, Is.EqualTo("GC2 Staging Captain"));
                Assert.That(chat.DisplayName, Is.EqualTo("GC2 Staging Captain"),
                    "Staging startup must not replace the resolved GC2 property with Player.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void ChatInstallers_HaveExpectedVersionsWithoutStaleInstalledRoots()
        {
            AssertInstallerVersion(CoreInstallerDescriptorPath, 1, 0);
            AssertInstallerVersion(LobbyInstallerDescriptorPath, 0, 1);
            AssertOnlyInstalledRoot(CoreInstallPrefix + "1.1.0", CoreInstallPrefix);
            AssertOnlyInstalledRoot(LobbyInstallPrefix + "1.0.1", LobbyInstallPrefix);
        }

        [TestCase(CoreInstallPrefix, CoreSceneName, CorePackagePath)]
        [TestCase(LobbyInstallPrefix, LobbySceneName, LobbyPackagePath)]
        public void InstallerPackage_ContainsCurrentMigratedChatScene(
            string installPrefix,
            string sceneName,
            string packagePath)
        {
            string scenePath = FindInstalledScene(installPrefix, sceneName);
            Dictionary<string, byte[]> payloads = ReadUnityPackageAssetPayloads(
                ProjectPath(packagePath));
            Assert.That(payloads.TryGetValue(scenePath, out byte[] packaged), Is.True,
                $"The installer no longer contains '{sceneName}'.");

            byte[] current = File.ReadAllBytes(ProjectPath(scenePath));
            Assert.That(packaged.SequenceEqual(current), Is.True,
                $"{Path.GetFileName(packagePath)} contains the pre-migration '{sceneName}'. " +
                "Rebuild it after serializing PropertyGetString.");
        }

        private static void AssertConstantPropertyValue(
            string component,
            string fullScene,
            string expected)
        {
            Match property = Regex.Match(
                component,
                @"(?m)^  m_DefaultDisplayNameProperty:\r?$\n" +
                @"    m_Property:\r?$\n" +
                @"      rid: ([0-9]+)\r?$");
            Assert.That(property.Success, Is.True,
                "m_DefaultDisplayNameProperty must serialize as PropertyGetString.");

            string managedReference = ReadManagedReference(
                fullScene,
                property.Groups[1].Value);
            StringAssert.Contains(
                "type: {class: GetStringString, ns: GameCreator.Runtime.Common, " +
                "asm: GameCreator.Runtime.Core}",
                managedReference);
            StringAssert.IsMatch(
                @"(?m)^\s*m_Value:\s*" + Regex.Escape(expected) + @"\s*$",
                managedReference,
                "The migration must preserve the scene's authored display name.");
        }

        private static string ReadComponentByScriptGuid(string scene, string scriptGuid)
        {
            Match script = Regex.Match(
                scene,
                @"(?m)^  m_Script: \{fileID: 11500000, guid: " +
                Regex.Escape(scriptGuid) +
                @", type: 3\}\r?$");
            Assert.That(script.Success, Is.True,
                $"No scene component uses script GUID {scriptGuid}.");

            int start = scene.LastIndexOf(
                "--- !u!114 &",
                script.Index,
                StringComparison.Ordinal);
            Assert.That(start, Is.GreaterThanOrEqualTo(0));
            int end = scene.IndexOf("\n--- !u!", script.Index, StringComparison.Ordinal);
            return end >= 0 ? scene.Substring(start, end - start) : scene.Substring(start);
        }

        private static string ReadManagedReference(string source, string rid)
        {
            Match reference = Regex.Match(
                source,
                @"(?m)^    - rid: " + Regex.Escape(rid) + @"\r?$");
            Assert.That(reference.Success, Is.True,
                $"Managed reference {rid} is missing.");

            int end = source.IndexOf(
                "\n    - rid: ",
                reference.Index + reference.Length,
                StringComparison.Ordinal);
            return end >= 0
                ? source.Substring(reference.Index, end - reference.Index)
                : source.Substring(reference.Index);
        }

        private static string ReadMetaGuid(string metaPath)
        {
            Match guid = Regex.Match(ReadAsset(metaPath), @"(?m)^guid:\s*([a-f0-9]+)\s*$");
            Assert.That(guid.Success, Is.True, $"No GUID found in {metaPath}.");
            return guid.Groups[1].Value;
        }

        private static FieldInfo FindDisplayNameMigrationFlag(Type componentType)
        {
            return componentType
                .GetFields(InstanceNonPublic)
                .SingleOrDefault(field =>
                    field.FieldType == typeof(bool) &&
                    field.Name.IndexOf("DisplayName", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    field.Name.IndexOf("Migrat", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static string FindInstalledScene(string installPrefix, string sceneName)
        {
            const string installsRoot = "Assets/Plugins/GameCreator/Installs";
            string absoluteRoot = ProjectPath(installsRoot);
            string[] matches = Directory.GetDirectories(absoluteRoot, installPrefix + "*")
                .Select(path => Path.Combine(path, sceneName))
                .Where(File.Exists)
                .ToArray();
            Assert.That(matches, Has.Length.EqualTo(1),
                $"Expected one installed '{sceneName}' scene for {installPrefix}.");
            return installsRoot + "/" + Path.GetFileName(Path.GetDirectoryName(matches[0])) +
                   "/" + sceneName;
        }

        private static void AssertInstallerVersion(
            string descriptorPath,
            int minor,
            int patch)
        {
            string descriptor = ReadAsset(descriptorPath).Replace("\r\n", "\n");
            StringAssert.IsMatch(
                @"(?m)^    m_Version:\n      major: 1\n      minor: " + minor +
                @"\n      patch: " + patch + "$",
                descriptor);
        }

        private static void AssertOnlyInstalledRoot(string expected, string installPrefix)
        {
            string installsRoot = ProjectPath("Assets/Plugins/GameCreator/Installs");
            string[] roots = Directory.GetDirectories(installsRoot, installPrefix + "*")
                .Select(Path.GetFileName)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
            CollectionAssert.AreEqual(new[] { expected }, roots,
                "Remove stale installed package versions after installer migration.");
        }

        private static string ReadAsset(string assetPath)
        {
            string path = ProjectPath(assetPath);
            Assert.That(File.Exists(path), Is.True, $"Asset is missing: {assetPath}");
            return File.ReadAllText(path);
        }

        private static string ProjectPath(string assetPath)
        {
            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
            Assert.That(projectRoot, Is.Not.Null.And.Not.Empty);
            return Path.Combine(projectRoot, assetPath);
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
            {
                if (assets.TryGetValue(entry.Key, out byte[] content))
                    result[entry.Value] = content;
            }
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
    }
}
