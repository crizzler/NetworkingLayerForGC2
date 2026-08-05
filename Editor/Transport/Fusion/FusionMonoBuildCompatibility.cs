using System;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Arawn.GameCreator2.Networking.Transport.Fusion.Editor
{
    /// <summary>
    /// Protects Fusion's woven RPC entry points from UnityLinker metadata stripping in
    /// Mono players. IL2CPP is deliberately left untouched because the observed access
    /// failure is specific to Mono's runtime visibility checks.
    /// </summary>
    internal static class FusionMonoBuildCompatibility
    {
        private const string SettingsPath =
            "Project Settings > Player > Other Settings > Optimization > " +
            "Managed Stripping Level";

        internal static bool IsCompatible(
            ScriptingImplementation scriptingBackend,
            ManagedStrippingLevel strippingLevel)
        {
            return scriptingBackend != ScriptingImplementation.Mono2x ||
                   strippingLevel == ManagedStrippingLevel.Disabled;
        }

        internal static bool TryGetActiveBuildTargetIssue(out string issue)
        {
            return TryGetIssue(EditorUserBuildSettings.activeBuildTarget, out issue);
        }

        internal static bool TryGetIssue(BuildTarget buildTarget, out string issue)
        {
            int subtarget = BuildPipeline.GetBuildTargetGroup(buildTarget) ==
                            BuildTargetGroup.Standalone
                ? (int)EditorUserBuildSettings.standaloneBuildSubtarget
                : 0;
            return TryGetIssue(buildTarget, subtarget, out issue);
        }

        internal static bool TryGetIssue(BuildReport report, out string issue)
        {
            if (report == null)
            {
                issue = string.Empty;
                return false;
            }

            return TryGetIssue(
                report.summary.platform,
                report.summary.platformGroup == BuildTargetGroup.Standalone
                    ? (int)report.summary.GetSubtarget<StandaloneBuildSubtarget>()
                    : 0,
                out issue);
        }

        private static bool TryGetIssue(
            BuildTarget buildTarget,
            int subtarget,
            out string issue)
        {
            issue = string.Empty;
            if (!TryGetSettings(
                    buildTarget,
                    subtarget,
                    out _,
                    out ScriptingImplementation scriptingBackend,
                    out ManagedStrippingLevel strippingLevel))
            {
                return false;
            }

            if (IsCompatible(scriptingBackend, strippingLevel)) return false;

            issue =
                $"Fusion Mono player build compatibility is not configured for " +
                $"'{buildTarget}'. Managed Stripping Level is '{strippingLevel}', but " +
                "Fusion 2.1.1 woven RPC entry points require 'Disabled' when using Mono. " +
                "UnityLinker removes verification metadata from the woven assembly and " +
                "the resulting player fails remote RPC sends with MethodAccessException. " +
                $"Set {SettingsPath} to Disabled for this target, or use the fix button " +
                "on the Fusion Scene Setup Wizard Review page, then make a clean player " +
                "build. IL2CPP stripping settings are not changed by this requirement.";
            return true;
        }

        internal static bool ConfigureActiveBuildTarget()
        {
            BuildTarget buildTarget = EditorUserBuildSettings.activeBuildTarget;
            if (!TryGetSettings(
                    buildTarget,
                    (int)EditorUserBuildSettings.standaloneBuildSubtarget,
                    out NamedBuildTarget namedBuildTarget,
                    out ScriptingImplementation scriptingBackend,
                    out ManagedStrippingLevel strippingLevel) ||
                scriptingBackend != ScriptingImplementation.Mono2x ||
                strippingLevel == ManagedStrippingLevel.Disabled)
            {
                return false;
            }

            PlayerSettings.SetManagedStrippingLevel(
                namedBuildTarget,
                ManagedStrippingLevel.Disabled);
            Debug.Log(
                $"[Fusion Setup] Set Mono Managed Stripping Level to Disabled for " +
                $"'{buildTarget}'. Rebuild the player so Fusion RPC verification metadata " +
                "is preserved.");
            return true;
        }

        internal static bool SceneUsesFusionTransport(Scene scene)
        {
            if (!scene.IsValid() || !scene.isLoaded) return false;

            foreach (GameObject root in scene.GetRootGameObjects())
            {
                if (root == null) continue;
                if (root.GetComponentInChildren<FusionTransportBridge>(true) != null ||
                    root.GetComponentInChildren<FusionSessionBootstrap>(true) != null)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TryGetSettings(
            BuildTarget buildTarget,
            int subtarget,
            out NamedBuildTarget namedBuildTarget,
            out ScriptingImplementation scriptingBackend,
            out ManagedStrippingLevel strippingLevel)
        {
            namedBuildTarget = default;
            scriptingBackend = default;
            strippingLevel = default;

            try
            {
                BuildTargetGroup group = BuildPipeline.GetBuildTargetGroup(buildTarget);
                if (group == BuildTargetGroup.Unknown) return false;

                namedBuildTarget = group == BuildTargetGroup.Standalone &&
                                   subtarget == (int)StandaloneBuildSubtarget.Server
                    ? NamedBuildTarget.Server
                    : NamedBuildTarget.FromBuildTargetGroup(group);
                scriptingBackend = PlayerSettings.GetScriptingBackend(namedBuildTarget);
                strippingLevel = PlayerSettings.GetManagedStrippingLevel(namedBuildTarget);
                return true;
            }
            catch (Exception exception)
            {
                Debug.LogWarning(
                    $"[Fusion Setup] Could not inspect player build compatibility for " +
                    $"'{buildTarget}': {exception.Message}");
                return false;
            }
        }
    }

    /// <summary>
    /// Fails only builds that actually contain an Arawn Fusion transport scene. The
    /// scene-processing callback uses the actual scenes supplied to the build, including
    /// custom BuildPlayerOptions lists, so unrelated PurrNet-only builds are not blocked.
    /// </summary>
    public sealed class FusionMonoPlayerBuildGuard : IProcessSceneWithReport
    {
        public int callbackOrder => -1000;

        public void OnProcessScene(Scene scene, BuildReport report)
        {
            if (report == null ||
                !FusionMonoBuildCompatibility.SceneUsesFusionTransport(scene) ||
                !FusionMonoBuildCompatibility.TryGetIssue(report, out string issue))
            {
                return;
            }

            throw new BuildFailedException(issue);
        }
    }
}
