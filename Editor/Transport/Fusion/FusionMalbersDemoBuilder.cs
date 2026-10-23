using System;
using System.IO;
using Arawn.GameCreator2.Networking.Editor;
using Fusion.Editor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Arawn.GameCreator2.Networking.Transport.Fusion.Editor
{
    /// <summary>
    /// Builds the optional Fusion Malbers Animal Controller demo from the installed Fusion
    /// Core example. The generated scene references the licensed Malbers package and owns
    /// only its player/NPC prefabs, scene, HUD, and installer payload.
    /// </summary>
    public static class FusionMalbersDemoBuilder
    {
        private const string SourceRoot =
            "Assets/Plugins/GameCreator/Installs/" +
            "GC2NetworkingLayerFusionTransport.CoreExamples@1.1.0";
        private const string InstallRoot =
            "Assets/Plugins/GameCreator/Installs/" +
            "GC2NetworkingLayerFusionTransport.MalbersExamples@1.0.0";

        private const string SourcePlayerName = "FusionDemoPlayer-CoreAndVariables 3.prefab";
        private const string SourceSceneName =
            "Requires GC2 Core Demos - FusionCoreVariablesDemo.unity";
        private const string PlayerName = "FusionDemoPlayer-Malbers.prefab";
        private const string NpcName = "FusionDemoNpc-Malbers.prefab";
        private const string SceneName =
            "Requires Malbers Animal Controller - FusionMalbersDemo.unity";

        private const string SourcePlayer = SourceRoot + "/" + SourcePlayerName;
        private const string CopiedPlayer = InstallRoot + "/" + SourcePlayerName;
        private const string CopiedScene = InstallRoot + "/" + SourceSceneName;
        private const string PlayerPrefab = InstallRoot + "/" + PlayerName;
        private const string NpcPrefab = InstallRoot + "/" + NpcName;
        private const string DemoScene = InstallRoot + "/" + SceneName;
        private const string Package =
            "Assets/Arawn/NetworkingLayerForGC2/Demo/Fusion/Packages/Malbers/Package.unitypackage";

        private const string SpawnerTypeName =
            "Arawn.GameCreator2.Networking.Transport.Fusion.FusionPlayerSpawner";

        [MenuItem(
            "Game Creator/Networking Layer/Demos/Rebuild Fusion Malbers Installer",
            priority = 225)]
        public static void BuildInstaller()
        {
            Build(exportInstaller: true);
        }

        /// <summary>
        /// Rebuilds the generated install root for isolated CI validation without exporting or
        /// otherwise modifying the checked-in installer archive.
        /// </summary>
        public static void BuildValidationFixture()
        {
            Build(exportInstaller: false);
        }

        /// <summary>Rebuilds the fixture and produces the single-scene standalone smoke player.</summary>
        public static void BuildNetworkSmokePlayer()
        {
            BuildValidationFixture();
            NetworkEnemyShooterDemoEditorUtility.BuildStandaloneSmokePlayer(
                DemoScene,
                "Fusion Malbers",
                replaceMaterialsForSmoke: true);
        }

        private static void Build(bool exportInstaller)
        {
            RequireDependencies();

            NetworkFreeFlowCombatDemoEditorUtility.RecreateInstallRoot(
                SourceRoot,
                InstallRoot,
                "MalbersExamples@");
            NetworkFreeFlowCombatDemoEditorUtility.RenameGeneratedAsset(CopiedScene, DemoScene);

            GameObject player = NetworkMalbersDemoEditorUtility.SaveMalbersPlayerPrefab(
                PlayerPrefab,
                configureAsNpc: false,
                CopiedPlayer);
            GameObject npc = NetworkMalbersDemoEditorUtility.CopyNpcPrefab(PlayerPrefab, NpcPrefab);

            Scene scene = EditorSceneManager.OpenScene(DemoScene, OpenSceneMode.Single);
            if (!scene.IsValid() || !scene.isLoaded)
            {
                throw new InvalidOperationException(
                    $"Could not open the generated Fusion Malbers scene '{DemoScene}'.");
            }

            player = RequireGeneratedAsset<GameObject>(PlayerPrefab);
            npc = RequireGeneratedAsset<GameObject>(NpcPrefab);
            NetworkMalbersDemoEditorUtility.ReplacePlayerReferences(scene, SourcePlayer, player);
            NetworkMalbersDemoEditorUtility.ConfigureSpawner(scene, player, SpawnerTypeName);
            NetworkMalbersDemoEditorUtility.CreateNpcInstances(
                scene,
                npc,
                "Malbers Network NPCs");
            NetworkMalbersDemoEditorUtility.RemoveCoreVariableDemoObjects(scene);
            NetworkMalbersDemoEditorUtility.AddHud(scene);
            NetworkMalbersDemoEditorUtility.ConfigureFootstepDetectors(scene);
            EditorSceneManager.SaveScene(scene);

            NetworkMalbersDemoEditorUtility.RemoveGeneratedSourcePlayer(
                InstallRoot,
                SourcePlayerName);
            NetworkMalbersDemoEditorUtility.PruneInstallRoot(
                InstallRoot,
                DemoScene,
                PlayerPrefab,
                NpcPrefab);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            NetworkProjectConfigUtilities.RebuildPrefabTable();

            if (exportInstaller)
            {
                EnsurePackageFolder();
                NetworkFreeFlowCombatDemoEditorUtility.ExportExactInstallRoot(
                    InstallRoot,
                    Package);
            }

            NetworkMalbersDemoEditorUtility.CreateInstallerAsset(
                "Assets/Arawn/NetworkingLayerForGC2/Demo/Fusion/Packages/Malbers/" +
                "GC2NetworkingLayerFusionTransport.MalbersExamples.asset",
                "Malbers Examples",
                "GC2 Networking Layer Fusion Transport",
                "Fusion Malbers Animal Controller state replication example.",
                "GC2NetworkingLayerFusionTransport.Assets");

            Debug.Log(
                exportInstaller
                    ? "[FusionMalbersDemoBuilder] Built the Malbers installer payload with one " +
                      "PlayerOwned prefab, one server NPC prefab, a scene, and the Malbers HUD."
                    : "[FusionMalbersDemoBuilder] Rebuilt the Malbers validation fixture without " +
                      "exporting its archive.");
        }

        private static void RequireDependencies()
        {
            if (AssetDatabase.LoadMainAssetAtPath(NetworkMalbersDemoEditorUtility.MalbersPlayerScene) == null)
            {
                throw new FileNotFoundException(
                    "The Malbers GC2 integration scene is missing. Install Malbers Animal " +
                    "Controller and its Game Creator 2 integration before building the demo.",
                    NetworkMalbersDemoEditorUtility.MalbersPlayerScene);
            }

            if (AssetDatabase.LoadMainAssetAtPath(SourcePlayer) == null ||
                AssetDatabase.LoadMainAssetAtPath(SourceRoot + "/" + SourceSceneName) == null)
            {
                throw new FileNotFoundException(
                    "The installed Fusion Core example is required as the demo base.",
                    SourcePlayer);
            }
        }

        private static void EnsurePackageFolder()
        {
            string folder = Path.GetDirectoryName(Package)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(folder))
                NetworkMalbersDemoEditorUtility.EnsureAssetFolder(folder);
        }

        private static T RequireGeneratedAsset<T>(string path) where T : UnityEngine.Object
        {
            T asset = AssetDatabase.LoadAssetAtPath<T>(path);
            if (asset == null)
                throw new FileNotFoundException($"Generated asset is not importable: '{path}'.", path);
            return asset;
        }
    }
}
