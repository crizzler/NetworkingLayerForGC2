using UnityEditor;

namespace Arawn.EnemyMasses.Editor.Integration.GameCreator2.Patches
{
    /// <summary>
    /// Backward-compatible facade that forwards to the canonical Abilities patcher path.
    /// </summary>
    [System.Obsolete("Use AbilitiesPatcherImpl via GC2PatchManager.")]
    public static class AbilitiesPatcher
    {
        private static AbilitiesPatcherImpl CreateImpl() => new AbilitiesPatcherImpl();

        public static void PatchAbilities()
        {
            GC2PatchManager.PatchAbilities();
        }

        public static void UnpatchAbilities()
        {
            GC2PatchManager.UnpatchAbilities();
        }

        public static void CheckPatchStatus()
        {
            var status = CreateImpl().GetStatus();
            EditorUtility.DisplayDialog(
                $"{status.DisplayName} Patch Status",
                $"Installed: {status.IsInstalled}\n" +
                $"Patched: {status.IsPatched}\n" +
                $"Backups: {status.HasBackups}\n" +
                $"Patch Version: {status.PatchVersion}",
                "OK");
        }

        public static bool IsPatched()
        {
            return CreateImpl().IsPatched();
        }

        public static bool ValidateAbilitiesExist()
        {
            return CreateImpl().ValidateFilesExist();
        }

        public static bool HasBackups()
        {
            return CreateImpl().HasBackups();
        }
    }
}
