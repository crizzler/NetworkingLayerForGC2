using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using GameCreator.Runtime.Common;
using NUnit.Framework;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Transport.Fusion.Tests
{
    /// <summary>
    /// Protects the GC2 PropertyGetString contract used by the Fusion demo chat.
    /// The scene and installer checks prevent the string-to-property migration from
    /// silently resetting existing authored display names.
    /// </summary>
    public sealed class FusionChatDisplayNamePropertyTests
    {
        private const BindingFlags InstanceNonPublic =
            BindingFlags.Instance | BindingFlags.NonPublic;

        private const string RuntimeSourcePath =
            "Assets/Arawn/NetworkingLayerForGC2/Runtime/Transport/Fusion/" +
            "FusionChatBoxUI.cs";
        private const string RuntimeMetaPath = RuntimeSourcePath + ".meta";
        private const string InstallPrefix =
            "GC2NetworkingLayerFusionTransport.CoreExamples@";
        private const string SceneName =
            "Requires GC2 Code Demos - FusionCharacterSelectionDemo And Chat.unity";
        private const string PackagePath =
            "Assets/Arawn/NetworkingLayerForGC2/Demo/Fusion/Packages/Core/" +
            "Package.unitypackage";
        private const string InstallerDescriptorPath =
            "Assets/Arawn/NetworkingLayerForGC2/Demo/Fusion/Packages/Core/" +
            "GC2NetworkingLayerFusionTransport.CoreExamples.asset";

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
            FieldInfo defaultNameProperty = typeof(FusionChatBoxUI).GetField(
                "m_DefaultDisplayNameProperty",
                InstanceNonPublic);
            Assert.That(defaultNameProperty, Is.Not.Null);
            Assert.That(defaultNameProperty.FieldType, Is.EqualTo(typeof(PropertyGetString)),
                "Fusion chat should expose GC2's string-property picker, not a raw string.");
            Assert.That(
                defaultNameProperty.GetCustomAttribute<SerializeField>(),
                Is.Not.Null);
            InspectorNameAttribute inspectorName =
                defaultNameProperty.GetCustomAttribute<InspectorNameAttribute>();
            Assert.That(inspectorName, Is.Not.Null);
            Assert.That(inspectorName.displayName, Is.EqualTo("Default Display Name"));

            FieldInfo legacyName = typeof(FusionChatBoxUI).GetField(
                "m_DefaultDisplayName",
                InstanceNonPublic);
            Assert.That(legacyName, Is.Not.Null,
                "Keep the legacy field so existing scene and prefab YAML can migrate.");
            Assert.That(legacyName.FieldType, Is.EqualTo(typeof(string)));
            Assert.That(legacyName.GetCustomAttribute<SerializeField>(), Is.Not.Null);
            Assert.That(legacyName.GetCustomAttribute<HideInInspector>(), Is.Not.Null);
            Assert.That(
                typeof(ISerializationCallbackReceiver).IsAssignableFrom(typeof(FusionChatBoxUI)),
                Is.True,
                "The legacy scalar requires a serialization callback migration.");
            FieldInfo migrationFlag = FindDisplayNameMigrationFlag(typeof(FusionChatBoxUI));
            Assert.That(migrationFlag, Is.Not.Null,
                "A serialized migration flag prevents legacy values from overwriting a " +
                "designer-selected GC2 property on every deserialize.");
            Assert.That(migrationFlag.FieldType, Is.EqualTo(typeof(bool)));
            Assert.That(migrationFlag.GetCustomAttribute<SerializeField>(), Is.Not.Null);

            var gameObject = new GameObject("Fusion Chat Property Test");
            gameObject.SetActive(false);
            try
            {
                FusionChatBoxUI chat = gameObject.AddComponent<FusionChatBoxUI>();

                defaultNameProperty.SetValue(
                    chat,
                    new PropertyGetString("Authored Fusion Captain"));
                legacyName.SetValue(chat, "Player");
                migrationFlag.SetValue(chat, true);
                ((ISerializationCallbackReceiver)chat).OnAfterDeserialize();
                var authored = (PropertyGetString)defaultNameProperty.GetValue(chat);
                Assert.That(authored.Get(chat), Is.EqualTo("Authored Fusion Captain"),
                    "A completed migration must preserve a designer-authored non-Player value.");

                legacyName.SetValue(chat, "Legacy Fusion Captain");
                migrationFlag.SetValue(chat, false);
                ((ISerializationCallbackReceiver)chat).OnAfterDeserialize();
                var migrated = (PropertyGetString)defaultNameProperty.GetValue(chat);
                Assert.That(migrated.Get(chat), Is.EqualTo("Legacy Fusion Captain"),
                    "Existing customer YAML must migrate its authored scalar value.");
                Assert.That(migrationFlag.GetValue(chat), Is.True);

                defaultNameProperty.SetValue(
                    chat,
                    new PropertyGetString(new ContextDisplayName("Fusion Designer")));

                MethodInfo awake = typeof(FusionChatBoxUI).GetMethod("Awake", InstanceNonPublic);
                Assert.That(awake, Is.Not.Null);
                awake.Invoke(chat, null);

                FieldInfo resolvedName = typeof(FusionChatBoxUI).GetField(
                    "m_DisplayName",
                    InstanceNonPublic);
                Assert.That(resolvedName, Is.Not.Null);
                Assert.That(resolvedName.GetValue(chat), Is.EqualTo("Fusion Designer"),
                    "Awake must evaluate the GC2 property in this component's context.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
            }

        }

        [Test]
        public void CharacterSelectionChatDemo_PreservesAuthoredPlayerConstantInProperty()
        {
            string scenePath = FindInstalledScene(InstallPrefix, SceneName);
            string scene = ReadAsset(scenePath);
            string component = ReadComponentByScriptGuid(scene, ReadMetaGuid(RuntimeMetaPath));

            AssertConstantPropertyValue(component, scene, "Player");
        }

        [Test]
        public void CoreInstallerPackage_ContainsCurrentMigratedChatScene()
        {
            string scenePath = FindInstalledScene(InstallPrefix, SceneName);
            Dictionary<string, byte[]> payloads = ReadUnityPackageAssetPayloads(
                ProjectPath(PackagePath));
            Assert.That(payloads.TryGetValue(scenePath, out byte[] packaged), Is.True,
                "The Fusion Core installer no longer contains its chat demo scene.");

            byte[] current = File.ReadAllBytes(ProjectPath(scenePath));
            Assert.That(packaged.SequenceEqual(current), Is.True,
                "The Fusion Core Package.unitypackage contains the pre-migration scene. " +
                "Rebuild it after serializing PropertyGetString.");
        }

        [Test]
        public void CoreInstaller_IsVersion110WithoutStaleInstalledRoots()
        {
            AssertInstallerVersion110(InstallerDescriptorPath);
            AssertOnlyInstalledRoot(InstallPrefix + "1.1.0", InstallPrefix);
        }

        [Test]
        public void RemoteChatRendering_EscapesRichTextInNamesAndMessages()
        {
            var gameObject = new GameObject("Fusion Chat Escaping Test");
            gameObject.SetActive(false);
            try
            {
                FusionChatBoxUI chat = gameObject.AddComponent<FusionChatBoxUI>();
                var writer = new FusionPacketWriter();
                writer.WriteUInt32(42u);
                writer.WriteString("<b>A&B</b>");
                writer.WriteString("<color=red>Alert & Ready</color>");
                writer.WriteSingle(1f);

                MethodInfo receive = typeof(FusionChatBoxUI).GetMethod(
                    "ReceiveChatBroadcast",
                    InstanceNonPublic);
                Assert.That(receive, Is.Not.Null);
                receive.Invoke(chat, new object[]
                {
                    new ReadOnlyMemory<byte>(writer.ToArray())
                });

                FieldInfo linesField = typeof(FusionChatBoxUI).GetField(
                    "m_Lines",
                    InstanceNonPublic);
                Assert.That(linesField, Is.Not.Null);
                var lines = (List<string>)linesField.GetValue(chat);
                Assert.That(lines, Has.Count.EqualTo(1));
                Assert.That(
                    lines[0],
                    Is.EqualTo(
                        "&lt;b&gt;A&amp;B&lt;/b&gt; [42]: " +
                        "&lt;color=red&gt;Alert &amp; Ready&lt;/color&gt;"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
            }

            string source = ReadAsset(RuntimeSourcePath);
            StringAssert.Contains("EscapeRichText(name)", source);
            StringAssert.Contains("EscapeRichText(text)", source);
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

        private static string ReadAsset(string assetPath)
        {
            string path = ProjectPath(assetPath);
            Assert.That(File.Exists(path), Is.True, $"Asset is missing: {assetPath}");
            return File.ReadAllText(path);
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

        private static void AssertInstallerVersion110(string descriptorPath)
        {
            string descriptor = ReadAsset(descriptorPath).Replace("\r\n", "\n");
            StringAssert.IsMatch(
                @"(?m)^    m_Version:\n      major: 1\n      minor: 1\n      patch: 0$",
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
                "Remove stale installed package versions after the 1.1.0 migration.");
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
