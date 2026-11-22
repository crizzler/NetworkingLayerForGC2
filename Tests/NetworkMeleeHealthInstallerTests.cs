using System;
using System.IO;
using System.Linq;
using Arawn.GameCreator2.Networking.Editor;
using GameCreator.Runtime.Characters;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Arawn.GameCreator2.Networking.Tests
{
    [Category("GC2Networking.MeleeHealth")]
    public sealed class NetworkMeleeHealthInstallerTests
    {
        [TestCase("Fusion", "FusionPlayerSpawner", "FusionMeleeTransportBridge", "FusionStatsTransportBridge")]
        [TestCase("PurrNet", "PurrNetDemoPlayerSpawner", "PurrNetMeleeTransportBridge", "PurrNetStatsTransportBridge")]
        public void InstalledDemo_WiresReplicatedHealthOnTheNpcAndSpawnedPlayers(
            string transport, string spawnerType, string meleeBridgeType, string statsBridgeType)
        {
            string path = NetworkMeleeHealthDemoBuilder.ScenePath(transport);
            Assert.That(File.Exists(path), Is.True, "Run with --prepare-targeting-fixtures to extract both Melee installers.");
            string package = $"Assets/Arawn/NetworkingLayerForGC2/Demo/{transport}/Packages/Melee/Package.unitypackage";
            Assert.That(NetworkPackageSigning.IsPackageSigned(Path.GetFullPath(package), out string signature), Is.True, signature);
            var entries = NetworkPackageSigning.ReadEntries(Path.GetFullPath(package));
            foreach (string assetPath in new[] { path, Path.ChangeExtension(path, null) + "/NavMesh-MeleeHealth.asset" })
            {
                var entry = entries.Single(item => item.Pathname == assetPath);
                Assert.That(entry.MetaGuid, Is.EqualTo(entry.Guid), assetPath);
                Assert.That(entry.Asset, Is.EqualTo(File.ReadAllBytes(assetPath)), assetPath);
                Assert.That(entry.AssetMeta, Is.EqualTo(File.ReadAllBytes(assetPath + ".meta")), assetPath);
            }

            var scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
            try
            {
                GameObject[] roots = scene.GetRootGameObjects();
                MonoBehaviour[] components = roots.SelectMany(root => root.GetComponentsInChildren<MonoBehaviour>(true)).ToArray();
                Assert.That(components.All(component => component != null), Is.True, "The scene contains missing scripts.");

                var damageBridge = Find(components, "NetworkMeleeStatsDamageBridge");
                var health = ReadReference(damageBridge, "m_HealthAttribute");
                Assert.That(health, Is.Not.Null);
                Assert.That(ReadReference(Find(components, "NetworkMeleeHealthDemoMonitor"), "m_Health"), Is.SameAs(health));
                Assert.That(ReadReference(Find(components, "NavMeshSurface"), "m_NavMeshData"), Is.Not.Null);
                Find(components, "NetworkMeleeManager");
                Find(components, "NetworkStatsManager");
                Find(components, statsBridgeType);

                var meleeBridge = new SerializedObject(Find(components, meleeBridgeType));
                var weapons = meleeBridge.FindProperty("m_RegisterWeapons");
                Assert.That(weapons.arraySize, Is.GreaterThan(0));
                Assert.That(weapons.GetArrayElementAtIndex(0).objectReferenceValue, Is.Not.Null);

                GameObject npc = roots.Single(root => root.name == NetworkMeleeHealthDemoBuilder.PracticeNpcName);
                Assert.That(npc.GetComponent<NetworkCharacter>().IsServerAuthoritativeNPC, Is.True);
                Assert.That(npc.GetComponent<Character>().Driver, Is.TypeOf<UnitDriverNavmeshNetworkServer>());
                Assert.That(npc.GetComponent<Character>().IsPlayer, Is.False);
                var playerReference = ReadReference(Find(components, spawnerType), "m_PlayerPrefab");
                Assert.That(playerReference, Is.Not.Null);
                GameObject player = playerReference is Component component ? component.gameObject : (GameObject)playerReference;

                foreach (GameObject target in new[] { npc, player })
                {
                    MonoBehaviour[] targetComponents = target.GetComponents<MonoBehaviour>();
                    Find(targetComponents, "NetworkMeleeController");
                    Find(targetComponents, "NetworkStatsController");
                    var traits = Find(targetComponents, "Traits");
                    var traitsClass = ReadReference(traits, "m_Class");
                    Assert.That(traitsClass, Is.Not.Null, target.name);
                    Assert.That(AssetDatabase.GetDependencies(AssetDatabase.GetAssetPath(traitsClass)),
                        Does.Contain(AssetDatabase.GetAssetPath(health)), target.name);
                }
            }
            finally
            {
                EditorSceneManager.CloseScene(scene, true);
            }
        }

        [Test]
        public void Wizard_UsesExplicitHealthAndPreservesExistingHealthAndFallbackOnRerun()
        {
            var bridgeType = Type.GetType("Arawn.GameCreator2.Networking.Stats.Melee.NetworkMeleeStatsDamageBridge, Arawn.GameCreator2.Networking.Stats.Melee");
            var healthType = Type.GetType("GameCreator.Runtime.Stats.Attribute, GameCreator.Runtime.Stats");
            Assert.That(bridgeType, Is.Not.Null);
            Assert.That(healthType, Is.Not.Null);
            var actor = new GameObject("Melee damage bridge");
            var health = ScriptableObject.CreateInstance(healthType);
            try
            {
                Component bridge = actor.AddComponent(bridgeType);
                var serialized = new SerializedObject(bridge);
                serialized.FindProperty("m_FallbackHealthAttributeId").stringValue = "vitality";
                serialized.ApplyModifiedPropertiesWithoutUndo();

                MeleeStatsWizardHealth.Apply(bridge, bridge, health, edited: true);
                MeleeStatsWizardHealth.Apply(bridge, bridge, null, edited: false);
                Assert.That(ReadReference(bridge, "m_HealthAttribute"), Is.SameAs(health));

                MeleeStatsWizardHealth.Apply(bridge, bridge, null, edited: true);
                Assert.That(ReadReference(bridge, "m_HealthAttribute"), Is.Null);
                Assert.That(new SerializedObject(bridge).FindProperty("m_FallbackHealthAttributeId").stringValue, Is.EqualTo("vitality"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(actor);
                UnityEngine.Object.DestroyImmediate(health);
            }
        }

        private static MonoBehaviour Find(MonoBehaviour[] components, string typeName) =>
            components.Single(component => component != null && component.GetType().Name == typeName);

        private static UnityEngine.Object ReadReference(UnityEngine.Object target, string property) =>
            new SerializedObject(target).FindProperty(property).objectReferenceValue;
    }
}
