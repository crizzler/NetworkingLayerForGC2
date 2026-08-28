#if GC2_TRAVERSAL
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using GameCreator.Runtime.Traversal;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Arawn.GameCreator2.Networking.Traversal.Tests
{
    public sealed class TraversalExamplesInstallerTests
    {
        private const string FusionInstalledRoot =
            "Assets/Plugins/GameCreator/Installs/" +
            "GC2NetworkingLayerFusionTransport.TraversalExamples@1.0.0";
        private const string PurrNetInstalledRoot =
            "Assets/Plugins/GameCreator/Installs/" +
            "GC2NetworkingLayerPurrNetTransport.TraversalExamples@1.0.0";

        private const string FusionPackageRoot =
            "Assets/Plugins/GameCreator/Installs/" +
            "GC2NetworkingLayerFusionTransport.TraversalExamples@1.0.1";
        private const string PurrNetPackageRoot =
            "Assets/Plugins/GameCreator/Installs/" +
            "GC2NetworkingLayerPurrNetTransport.TraversalExamples@1.0.1";

        private const string FusionClimbSceneName =
            "Requires Traversal Examples - FusionClimbDemo.unity";
        private const string PurrNetClimbSceneName =
            "Requires Traversal Examples - PurrNetClimbDemo.unity";

        private const string FusionPackagePath =
            "Assets/Arawn/NetworkingLayerForGC2/Demo/Fusion/Packages/Traversal/Package.unitypackage";
        private const string PurrNetPackagePath =
            "Assets/Arawn/NetworkingLayerForGC2/Demo/PurrNet/Packages/Traversal/Package.unitypackage";
        private const string FusionDescriptorPath =
            "Assets/Arawn/NetworkingLayerForGC2/Demo/Fusion/Packages/Traversal/" +
            "GC2NetworkingLayerFusionTransport.TraversalExamples.asset";
        private const string PurrNetDescriptorPath =
            "Assets/Arawn/NetworkingLayerForGC2/Demo/PurrNet/Packages/Traversal/" +
            "GC2NetworkingLayerPurrNetTransport.TraversalExamples.asset";

        private static readonly FieldInfo IgnoreCollidersField = typeof(Traverse).GetField(
            "m_IgnoreColliders",
            BindingFlags.Instance | BindingFlags.NonPublic);

        [TestCase(FusionInstalledRoot + "/" + FusionClimbSceneName)]
        [TestCase(PurrNetInstalledRoot + "/" + PurrNetClimbSceneName)]
        public void ClimbDemo_CoverTraversesHaveOnlyLiveIgnoredColliders(string scenePath)
        {
            Assert.That(IgnoreCollidersField, Is.Not.Null);
            Assert.That(File.Exists(ProjectPath(scenePath)), Is.True, $"Missing scene '{scenePath}'.");

            Scene scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);
            try
            {
                Traverse[] traverses = scene.GetRootGameObjects()
                    .SelectMany(root => root.GetComponentsInChildren<Traverse>(true))
                    .ToArray();
                Assert.That(traverses, Is.Not.Empty);

                foreach (Traverse traverse in traverses)
                {
                    var ignored = IgnoreCollidersField.GetValue(traverse) as Collider[];
                    if (ignored == null) continue;

                    for (int i = 0; i < ignored.Length; i++)
                    {
                        Assert.That(
                            ignored[i] == null,
                            Is.False,
                            $"'{traverse.name}' contains a missing ignored collider at index {i}.");
                    }
                }

                Traverse coverHigh = traverses.Single(traverse => traverse.name == "Cover_High");
                var coverIgnored = (Collider[])IgnoreCollidersField.GetValue(coverHigh);
                Assert.That(coverIgnored, Is.Not.Null.And.Not.Empty);
                Assert.That(
                    coverIgnored.Any(collider =>
                        collider != null && collider.transform.IsChildOf(coverHigh.transform)),
                    Is.True,
                    "Cover_High must retain its authored nested wall and live ignored collider.");
            }
            finally
            {
                EditorSceneManager.CloseScene(scene, true);
            }
        }

        [Test]
        public void FusionTraversalInstaller_IsVersionedAndContainsExactRepairedPayload()
        {
            AssertInstallerVersion(FusionDescriptorPath);
            UnityPackageContents package = ReadUnityPackage(ProjectPath(FusionPackagePath));
            string packageClimbScene = FusionPackageRoot + "/" + FusionClimbSceneName;

            CollectionAssert.AreEquivalent(
                new[]
                {
                    FusionPackageRoot,
                    FusionPackageRoot + "/Requires Traversal Demos - FusionTraversalDemo.unity",
                    packageClimbScene
                },
                package.Pathnames);
            AssertRepairedScenePayload(
                package,
                packageClimbScene,
                FusionInstalledRoot + "/" + FusionClimbSceneName);
        }

        [Test]
        public void PurrNetTraversalInstaller_IsVersionedAndContainsExactRepairedPayload()
        {
            AssertInstallerVersion(PurrNetDescriptorPath);
            UnityPackageContents package = ReadUnityPackage(ProjectPath(PurrNetPackagePath));
            string packageClimbScene = PurrNetPackageRoot + "/" + PurrNetClimbSceneName;

            CollectionAssert.AreEquivalent(
                new[]
                {
                    PurrNetPackageRoot,
                    PurrNetPackageRoot + "/Profiles",
                    packageClimbScene,
                    PurrNetPackageRoot + "/Requires Traversal Demos - PurrNetTraversalDemo.unity",
                    PurrNetPackageRoot + "/PurrNetDemoPlayer-Traversal.prefab",
                    PurrNetPackageRoot + "/Profiles/PurrNetDemoNetworkPrefabs 42.asset",
                    PurrNetPackageRoot + "/Profiles/PurrNetDemoNetworkVariableProfile 26.asset",
                    PurrNetPackageRoot + "/Profiles/PurrNetDemoSceneNetworkVariableProfile 25.asset"
                },
                package.Pathnames);
            AssertRepairedScenePayload(
                package,
                packageClimbScene,
                PurrNetInstalledRoot + "/" + PurrNetClimbSceneName);
        }

        private static void AssertInstallerVersion(string descriptorPath)
        {
            string descriptor = File.ReadAllText(ProjectPath(descriptorPath));
            Assert.That(
                descriptor,
                Does.Match(@"m_Version:\s*\r?\n\s*major:\s*1\s*\r?\n\s*minor:\s*0\s*\r?\n\s*patch:\s*1"),
                $"'{descriptorPath}' must declare installer version 1.0.1.");
        }

        private static void AssertRepairedScenePayload(
            UnityPackageContents package,
            string packagedScenePath,
            string sourceScenePath)
        {
            Assert.That(
                package.Pathnames.Any(path => path.Contains("@1.0.0", StringComparison.Ordinal)),
                Is.False,
                "A rebuilt Traversal installer must not retain an old install-root pathname.");
            Assert.That(
                package.Assets.TryGetValue(packagedScenePath, out byte[] packagedScene),
                Is.True,
                $"Missing packaged scene '{packagedScenePath}'.");

            byte[] sourceScene = File.ReadAllBytes(ProjectPath(sourceScenePath));
            Assert.That(
                packagedScene.SequenceEqual(sourceScene),
                Is.True,
                $"Packaged scene '{packagedScenePath}' is stale relative to '{sourceScenePath}'.");

            string yaml = Encoding.UTF8.GetString(packagedScene);
            StringAssert.DoesNotContain(
                "m_RemovedGameObjects:\n    - {fileID: 3599643264105825456, " +
                "guid: 0949f0c2e3255473ebe7aaa2a29dafc4, type: 3}",
                yaml,
                "The Cover_High nested wall must not be removed while its collider is referenced.");
        }

        private static UnityPackageContents ReadUnityPackage(string packagePath)
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
                {
                    throw new InvalidDataException("Incomplete Unity package TAR header.");
                }

                string entryName = Encoding.UTF8.GetString(header, 0, 100)
                    .TrimEnd('\0')
                    .TrimStart('.', '/');
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
                    {
                        throw new EndOfStreamException($"Incomplete '{entryName}' entry.");
                    }

                    if (segments[1] == "pathname")
                    {
                        pathnames[segments[0]] = Encoding.UTF8.GetString(content)
                            .TrimEnd('\0', '\r', '\n');
                    }
                    else
                    {
                        assets[segments[0]] = content;
                    }
                }
                else
                {
                    Skip(gzip, size);
                }

                Skip(gzip, (512L - size % 512L) % 512L);
            }

            var assetsByPath = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, string> entry in pathnames)
            {
                if (assets.TryGetValue(entry.Key, out byte[] content))
                {
                    assetsByPath[entry.Value] = content;
                }
            }

            return new UnityPackageContents(pathnames.Values.ToArray(), assetsByPath);
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
                if (read <= 0)
                {
                    throw new EndOfStreamException("Incomplete Unity package entry.");
                }
                count -= read;
            }
        }

        private static string ProjectPath(string assetPath)
        {
            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
            Assert.That(projectRoot, Is.Not.Null.And.Not.Empty);
            return Path.Combine(projectRoot, assetPath.Replace('/', Path.DirectorySeparatorChar));
        }

        private sealed class UnityPackageContents
        {
            public UnityPackageContents(
                IReadOnlyCollection<string> pathnames,
                IReadOnlyDictionary<string, byte[]> assets)
            {
                Pathnames = pathnames;
                Assets = assets;
            }

            public IReadOnlyCollection<string> Pathnames { get; }
            public IReadOnlyDictionary<string, byte[]> Assets { get; }
        }
    }
}
#endif
