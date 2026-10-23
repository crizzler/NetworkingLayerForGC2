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
using Assert = NUnit.Framework.Assert;

namespace Arawn.GameCreator2.Networking.Tests
{
    [Category("GC2Networking.Malbers")]
    public sealed class MalbersExamplesInstallerTests
    {
        private const string StateBridgeTypeName =
            "Arawn.GameCreator2.Networking.Malbers.NetworkMalbersStateBridge";
        private const string DemoHudTypeName =
            "Arawn.GameCreator2.Networking.Malbers.NetworkMalbersDemoHud";

        private static readonly string[] s_InstallRoots =
        {
            "Assets/Plugins/GameCreator/Installs/" +
            "GC2NetworkingLayerFusionTransport.MalbersExamples@1.0.0",
            "Assets/Plugins/GameCreator/Installs/" +
            "GC2NetworkingLayerPurrNetTransport.MalbersExamples@1.0.0"
        };

        [Test]
        public void MalbersDemoPackages_ContainPlayerNpcSceneAndInstaller()
        {
            if (!HasMalbersDependency())
            {
                Assert.Ignore("Malbers Animal Controller is not installed in this project.");
            }

            foreach (string installRoot in s_InstallRoots)
            {
                if (!AssetDatabase.IsValidFolder(installRoot))
                {
                    Assert.Ignore(
                        $"The generated Malbers install root is missing: '{installRoot}'. " +
                        "Run the matching Rebuild ... Malbers Installer menu command.");
                }

                string[] prefabs = AssetDatabase.FindAssets("t:Prefab", new[] { installRoot })
                    .Select(AssetDatabase.GUIDToAssetPath)
                    .ToArray();
                Assert.GreaterOrEqual(
                    prefabs.Length,
                    2,
                    $"Expected a player and NPC prefab under '{installRoot}'.");

                string playerPath = prefabs.FirstOrDefault(
                    path => Path.GetFileName(path).Contains("Player", StringComparison.Ordinal));
                string npcPath = prefabs.FirstOrDefault(
                    path => Path.GetFileName(path).Contains("Npc", StringComparison.Ordinal));
                Assert.IsNotNull(playerPath, $"No player prefab under '{installRoot}'.");
                Assert.IsNotNull(npcPath, $"No NPC prefab under '{installRoot}'.");

                AssertPrefab(playerPath, expectNpc: false);
                AssertPrefab(npcPath, expectNpc: true);

                string[] scenes = AssetDatabase.FindAssets("t:Scene", new[] { installRoot })
                    .Select(AssetDatabase.GUIDToAssetPath)
                    .ToArray();
                Assert.AreEqual(
                    1,
                    scenes.Length,
                    $"Expected exactly one Malbers demo scene under '{installRoot}'.");

                AssertScene(scenes[0]);
            }
        }

        [Test]
        public void MalbersDemoInstallers_AreDiscoverableByTheGc2Installer()
        {
            if (!HasMalbersDependency())
            {
                Assert.Ignore("Malbers Animal Controller is not installed in this project.");
            }

            string[] installers =
            {
                "Assets/Arawn/NetworkingLayerForGC2/Demo/Fusion/Packages/Malbers/" +
                "GC2NetworkingLayerFusionTransport.MalbersExamples.asset",
                "Assets/Arawn/NetworkingLayerForGC2/Demo/PurrNet/Packages/Malbers/" +
                "GC2NetworkingLayerPurrNetTransport.MalbersExamples.asset"
            };

            foreach (string path in installers)
            {
                Assert.IsNotNull(
                    AssetDatabase.LoadMainAssetAtPath(path),
                    $"Missing GC2 installer asset: '{path}'.");
                string package = Path.Combine(
                    Path.GetDirectoryName(path) ?? string.Empty,
                    "Package.unitypackage").Replace('\\', '/');
                Assert.IsTrue(
                    File.Exists(package),
                    $"Missing installer package: '{package}'.");
            }
        }

        [Test]
        public void MalbersDemoScenes_ConfigureASinglePlayerSpawnerPrefab()
        {
            if (!HasMalbersDependency())
            {
                Assert.Ignore("Malbers Animal Controller is not installed in this project.");
            }

            foreach (string installRoot in s_InstallRoots)
            {
                if (!AssetDatabase.IsValidFolder(installRoot))
                {
                    Assert.Ignore(
                        $"The generated Malbers install root is missing: '{installRoot}'.");
                }

                string scenePath = AssetDatabase.FindAssets("t:Scene", new[] { installRoot })
                    .Select(AssetDatabase.GUIDToAssetPath)
                    .FirstOrDefault();
                Assert.IsNotNull(scenePath, $"No scene under '{installRoot}'.");

                Scene scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);
                try
                {
                    MonoBehaviour spawner = scene.GetRootGameObjects()
                        .SelectMany(root => root.GetComponentsInChildren<MonoBehaviour>(true))
                        .FirstOrDefault(component =>
                            component != null &&
                            component.GetType().Name.Contains("PlayerSpawner", StringComparison.Ordinal));
                    Assert.IsNotNull(spawner, $"No player spawner in '{scenePath}'.");

                    var serialized = new SerializedObject(spawner);
                    SerializedProperty prefab = serialized.FindProperty("m_PlayerPrefab");
                    Assert.IsNotNull(prefab, $"Spawner has no m_PlayerPrefab: '{scenePath}'.");
                    Assert.IsNotNull(
                        prefab.objectReferenceValue,
                        $"Spawner m_PlayerPrefab was not remapped to the Malbers player: '{scenePath}'.");

                    SerializedProperty prefabs = serialized.FindProperty("m_PlayerPrefabs");
                    Assert.IsNotNull(prefabs, $"Spawner has no m_PlayerPrefabs: '{scenePath}'.");
                    Assert.AreEqual(
                        1,
                        prefabs.arraySize,
                        $"Spawner must expose exactly one Malbers player prefab: '{scenePath}'.");
                }
                finally
                {
                    EditorSceneManager.CloseScene(scene, true);
                }
            }
        }

        private static void AssertPrefab(string path, bool expectNpc)
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            Assert.IsNotNull(prefab, $"Prefab is not importable: '{path}'.");

            Character character = prefab.GetComponent<Character>();
            Assert.IsNotNull(character, $"Prefab has no GC2 Character: '{path}'.");
            Assert.IsFalse(
                character.IsPlayer,
                $"Network prefabs must not author the GC2 player flag: '{path}'.");

            NetworkCharacter networkCharacter = prefab.GetComponent<NetworkCharacter>();
            Assert.IsNotNull(networkCharacter, $"Prefab has no NetworkCharacter: '{path}'.");
            Assert.AreEqual(
                expectNpc ? NetworkCharacterActorType.NPC : NetworkCharacterActorType.PlayerOwned,
                networkCharacter.ActorType,
                $"Unexpected actor type on '{path}'.");

            Assert.IsNotNull(
                FindComponentByTypeName(prefab, StateBridgeTypeName),
                $"Prefab has no Malbers state bridge: '{path}'.");
            Assert.IsNotNull(
                FindComponentByTypeName(prefab, "FusionNetworkIdentity") ??
                FindComponentByTypeName(prefab, "PurrNet.NetworkIdentity"),
                $"Prefab has no transport identity: '{path}'.");
        }

        private static void AssertScene(string path)
        {
            Scene scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
            try
            {
                GameObject hud = scene.GetRootGameObjects()
                    .FirstOrDefault(root => root.name == "Malbers Demo HUD");
                Assert.IsNotNull(hud, $"Malbers demo scene has no HUD: '{path}'.");
                Assert.IsNotNull(
                    FindComponentByTypeName(hud, DemoHudTypeName),
                    $"Malbers demo HUD is missing its component: '{path}'.");

                GameObject npcs = scene.GetRootGameObjects()
                    .FirstOrDefault(root => root.name == "Malbers Network NPCs");
                Assert.IsNotNull(npcs, $"Malbers demo scene has no NPC root: '{path}'.");
                Assert.GreaterOrEqual(
                    npcs.transform.childCount,
                    1,
                    $"Malbers demo scene has no NPC instances: '{path}'.");
            }
            finally
            {
                EditorSceneManager.CloseScene(scene, true);
            }
        }

        private static MonoBehaviour FindComponentByTypeName(GameObject root, string typeName)
        {
            return root.GetComponentsInChildren<MonoBehaviour>(true)
                .FirstOrDefault(component =>
                    component != null &&
                    (string.Equals(component.GetType().FullName, typeName, StringComparison.Ordinal) ||
                     string.Equals(component.GetType().Name, typeName, StringComparison.Ordinal)));
        }

        private static bool HasMalbersDependency()
        {
            return AssetDatabase.LoadMainAssetAtPath(
                NetworkMalbersDemoEditorUtility.MalbersPlayerScene) != null;
        }
    }
}
