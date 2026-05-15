using UnityEditor;
using UnityEngine;

namespace Arawn.EnemyMasses.Editor.Integration.GameCreator2.Patches
{
    public static class ShooterSightPatchRequirement
    {
        private const string Title = "Required GC2 Shooter Sight Patch";

        public static bool IsShooterSightSourceAvailable()
        {
            return new ShooterSightPatcher().ValidateFilesExist();
        }

        public static bool IsApplied()
        {
            var patcher = new ShooterSightPatcher();
            return patcher.ValidateFilesExist() && patcher.IsPatched();
        }

        public static string GetStatusText()
        {
            var patcher = new ShooterSightPatcher();
            if (!patcher.ValidateFilesExist())
            {
                return "GC2 Shooter Sight source file was not found. Install Game Creator 2 Shooter before enabling Shooter networking.";
            }

            return patcher.IsPatched()
                ? "Required GC2 Shooter Sight patch is applied."
                : "Required GC2 Shooter Sight patch is not applied. Shooter networking setup will be blocked until it is applied.";
        }

        public static bool EnsureAppliedWithPrompt(string setupSource, out string reportLine)
        {
            reportLine = null;

            var patcher = new ShooterSightPatcher();
            if (!patcher.ValidateFilesExist())
            {
                reportLine = "GC2 Shooter Sight source file was not found; required patch check skipped because GC2 Shooter appears to be missing.";
                return true;
            }

            if (patcher.IsPatched())
            {
                reportLine = "GC2 Shooter Sight hook patch is already applied.";
                return true;
            }

            bool applyRequiredPatch = EditorUtility.DisplayDialog(
                Title,
                $"{setupSource} detected Game Creator 2 Shooter.\n\n" +
                "This patch is REQUIRED before creating a PurrNet Shooter networking setup. " +
                "Continuing without it can leave remote aiming/sight side effects unsafe and can break Shooter networking integration.\n\n" +
                "For networked Shooter aiming, GC2 Sight.Enter and Sight.Exit must not execute " +
                "Sight.m_OnEnter and Sight.m_OnExit instructions on remote character replicas. Those " +
                "instructions commonly drive local camera FOV, crosshair, view effects and other local-only side effects.\n\n" +
                "A reflected clone of GC2 Sight.Enter/Exit is not reliable for competitive multiplayer. If GC2 updates " +
                "Sight.Enter or Sight.Exit, a reflection clone can silently drift while still compiling. That is worse " +
                "than a small explicit hook because it can produce subtle bugs: missing state exit, broken blend timing, " +
                "wrong animation layer, broken crosshair lifecycle, or IK not entering/exiting correctly.\n\n" +
                "This patch keeps GC2 owning the sight lifecycle. It only gates the unsafe side-effect bucket: " +
                "m_OnEnter/m_OnExit instruction execution on remote replicas.\n\n" +
                "A backup is created before patching and the patch can be reverted from:\n" +
                "Game Creator > Networking Layer > Patches > Shooter Sight > Unpatch",
                "Apply Required Patch",
                "Cancel Setup");

            if (!applyRequiredPatch)
            {
                reportLine = "Setup cancelled before applying the required GC2 Shooter Sight hook patch.";
                return false;
            }

            if (!patcher.TryValidateVersionCompatibility(out string compatibilityMessage))
            {
                bool applyAnyway = EditorUtility.DisplayDialog(
                    "Shooter Version Compatibility Warning",
                    $"{patcher.DisplayName} may be incompatible with the detected GC2 Shooter version.\n\n" +
                    $"{compatibilityMessage}\n\n" +
                    "The patcher searches for method structure instead of line numbers and auto-rolls back on failure, " +
                    "but a large GC2 Shooter update may still require a patcher update.\n\n" +
                    "Shooter networking setup cannot continue until the required Sight hook is applied.",
                    "Apply Anyway",
                    "Cancel Setup");

                if (!applyAnyway)
                {
                    reportLine = "Setup cancelled before applying the required GC2 Shooter Sight hook patch.";
                    return false;
                }
            }

            bool success = patcher.ApplyPatch();
            if (success)
            {
                reportLine = "Applied required GC2 Shooter Sight hook patch.";
                return true;
            }

            EditorUtility.DisplayDialog(
                "Shooter Sight Patch Failed",
                "The GC2 Shooter Sight hook patch could not be applied. The patcher rolled back any partial changes.\n\n" +
                "Check the Unity Console for the exact insertion failure. You can also run it manually from:\n" +
                "Game Creator > Networking Layer > Patches > Shooter Sight > Patch (Remote Camera Safety)\n\n" +
                "Networked Shooter setup cannot continue until this patch is applied.",
                "OK");

            reportLine = "ERROR: Failed to apply required GC2 Shooter Sight hook patch.";
            return false;
        }
    }
}
