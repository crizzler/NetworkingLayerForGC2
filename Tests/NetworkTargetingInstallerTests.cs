using System.IO;
using System.Linq;
using Arawn.GameCreator2.Networking.Editor;
using GameCreator.Runtime.Characters;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Tests
{
    [Category("GC2Networking.Targeting")]
    public sealed class NetworkTargetingInstallerTests
    {
        [TestCase("Fusion")] [TestCase("PurrNet")]
        public void Installer_ContainsTargetingAndOriginalMeleePayloadWithinItsOwnRoot(string transport)
        {
            string root = NetworkTargetingDemoEditorUtility.Root(transport);
            string package = $"Assets/Arawn/NetworkingLayerForGC2/Demo/{transport}/Packages/Melee/Package.unitypackage";
            var entries = NetworkPackageSigning.ReadEntries(Path.GetFullPath(package));
            Assert.That(entries.All(x => x.Pathname == root || x.Pathname.StartsWith(root + "/")), Is.True);
            string[] required = { NetworkTargetingDemoEditorUtility.ScenePath(transport), NetworkTargetingDemoEditorUtility.PlayerPath(transport),
                root + $"/Requires Melee & Stats Demos - {transport}MeleeStatsDemo.unity", root + $"/{transport}DemoPlayer-MeleeAndStats.prefab" };
            foreach (string path in required)
            {
                var entry = entries.Single(x => x.Pathname == path);
                Assert.That(entry.Asset, Is.EqualTo(File.ReadAllBytes(path)), path);
                Assert.That(entry.AssetMeta, Is.EqualTo(File.ReadAllBytes(path + ".meta")), path + ".meta");
                Assert.That(entry.MetaGuid, Is.EqualTo(entry.Guid), path);
            }
        }

        [TestCase("Fusion")] [TestCase("PurrNet")]
        public void InstalledPlayer_UsesExplicitAuthorityAndSelfCombatTargetFacing(string transport)
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(NetworkTargetingDemoEditorUtility.PlayerPath(transport));
            Assert.That(prefab, Is.Not.Null, "Prepare fixtures with --prepare-targeting-fixtures.");
            Assert.That(prefab.GetComponent<NetworkCombatTarget>(), Is.Not.Null);
            Assert.That(prefab.GetComponent<NetworkCharacter>().IsPlayerOwnedActor, Is.True);
            Assert.That(prefab.GetComponent<Character>().Facing, Is.TypeOf<UnitFacingNetworkTarget>());
            Assert.That(prefab.GetComponent<Character>().Motion, Is.TypeOf<UnitMotionNetworkController>());
        }

        [TestCase("Fusion")] [TestCase("PurrNet")]
        public void InstalledScene_HasNineExplicitNetworkNpcsStockSelectionAndTransportSpawner(string transport)
        {
            var scene = EditorSceneManager.OpenScene(NetworkTargetingDemoEditorUtility.ScenePath(transport));
            try
            {
                var roots = scene.GetRootGameObjects();
                var npcs = roots.SelectMany(x => x.GetComponentsInChildren<NetworkCharacter>(true)).ToArray();
                Assert.That(npcs.Length, Is.EqualTo(9));
                Assert.That(npcs.All(x => x.IsServerAuthoritativeNPC && !x.GetComponent<Character>().IsPlayer), Is.True);
                Assert.That(npcs.All(x => x.GetComponent<Character>().Driver is UnitDriverNavmeshNetworkServer), Is.True);
                Assert.That(roots.SelectMany(x => x.GetComponentsInChildren<NetworkTargetingDemoUI>(true)).Count(), Is.EqualTo(1));
                var components = roots.SelectMany(x => x.GetComponentsInChildren<MonoBehaviour>(true)).ToArray();
                Assert.That(components.Any(x => x != null && x.GetType().Name == transport + (transport == "Fusion" ? "PlayerSpawner" : "DemoPlayerSpawner")), Is.True);
                string yaml = File.ReadAllText(scene.path);
                Assert.That(yaml, Does.Contain("InstructionCharacterTargetsClosest"));
                Assert.That(yaml, Does.Contain("Target_Indicator"));
            }
            finally { EditorSceneManager.NewScene(NewSceneSetup.EmptyScene); }
        }

        [TestCase("Fusion")] [TestCase("PurrNet")]
        public void Descriptor_DeclaresStockTargetingAndTransportAssetDependencies(string transport)
        {
            string path = $"Assets/Arawn/NetworkingLayerForGC2/Demo/{transport}/Packages/Melee/GC2NetworkingLayer{transport}Transport.MeleeExamples.asset";
            string yaml = File.ReadAllText(path);
            Assert.That(yaml, Does.Contain("m_ID: Melee.Examples"));
            Assert.That(yaml, Does.Contain($"m_ID: GC2NetworkingLayer{transport}Transport.Assets"));
            Assert.That(yaml, Does.Contain("m_ID: Melee.Sword"));
        }
    }
}
