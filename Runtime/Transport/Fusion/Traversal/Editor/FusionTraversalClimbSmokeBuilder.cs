using System;
using Arawn.GameCreator2.Networking.Editor;
using UnityEditor;

namespace Arawn.GameCreator2.Networking.Traversal.Transport.Fusion.Editor
{
    public static class FusionTraversalClimbSmokeBuilder
    {
        private const string SceneVersion101 =
            "Assets/Plugins/GameCreator/Installs/" +
            "GC2NetworkingLayerFusionTransport.TraversalExamples@1.0.1/" +
            "Requires Traversal Examples - FusionClimbDemo.unity";

        private const string SceneVersion100 =
            "Assets/Plugins/GameCreator/Installs/" +
            "GC2NetworkingLayerFusionTransport.TraversalExamples@1.0.0/" +
            "Requires Traversal Examples - FusionClimbDemo.unity";

        public static void BuildNetworkSmokePlayer()
        {
            NetworkEnemyShooterDemoEditorUtility.BuildStandaloneSmokePlayer(
                ResolveScene(),
                "Fusion Traversal Climb");
        }

        public static void BuildCoverNetworkSmokePlayer()
        {
            NetworkEnemyShooterDemoEditorUtility.BuildStandaloneSmokePlayer(
                ResolveScene(),
                "Fusion Traversal Cover");
        }

        public static void BuildPullUpNetworkSmokePlayer()
        {
            NetworkEnemyShooterDemoEditorUtility.BuildStandaloneSmokePlayer(
                ResolveScene(),
                "Fusion Traversal PullUp");
        }

        public static void BuildLedgeTransitionNetworkSmokePlayer()
        {
            NetworkEnemyShooterDemoEditorUtility.BuildStandaloneSmokePlayer(
                ResolveScene(),
                "Fusion Traversal Ledge Transition");
        }

        private static string ResolveScene()
        {
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(SceneVersion101) != null)
            {
                return SceneVersion101;
            }

            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(SceneVersion100) != null)
            {
                return SceneVersion100;
            }

            throw new InvalidOperationException(
                "The Fusion Traversal Examples 1.0.1 or 1.0.0 Climb demo is required " +
                "to build the Traversal network smoke player.");
        }
    }
}
