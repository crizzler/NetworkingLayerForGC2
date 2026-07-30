#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Editor
{
    /// <summary>
    /// Detects a common in-place PurrNet Asset Store upgrade failure where the current combined
    /// LiteNetLib NetManager is imported beside source files left behind by an older split layout.
    /// This lives in the transport-independent editor assembly so its diagnostic remains available
    /// even when the conflicting PurrNet sources fail to compile.
    /// </summary>
    [InitializeOnLoad]
    public static class PurrNetInstallCompatibility
    {
        private const string SESSION_WARNING_KEY =
            "Arawn.GC2Networking.PurrNet.MixedLiteNetLibLayout.WarningShown";

        private static readonly string[] s_LegacyNetManagerFileNames =
        {
            "NetManager.Socket.cs",
            "NetManager.PacketPool.cs",
            "NetManager.HashSet.cs"
        };

        private static readonly Regex s_CombinedNetManagerDeclaration = new Regex(
            @"\bpublic\s+(?:sealed\s+)?class\s+NetManager\b",
            RegexOptions.CultureInvariant);

        private static readonly Regex s_PartialNetManagerDeclaration = new Regex(
            @"\bpublic\s+(?:sealed\s+)?partial\s+class\s+NetManager\b",
            RegexOptions.CultureInvariant);

        static PurrNetInstallCompatibility()
        {
            EditorApplication.delayCall += LogMixedLayoutOnce;
        }

        /// <summary>
        /// Returns true when a non-partial combined NetManager is present beside obsolete split
        /// partial files. The supplied paths need only identify which legacy files still exist.
        /// </summary>
        public static bool HasMixedLiteNetLibLayout(
            string netManagerSource,
            IEnumerable<string> existingLegacyFilePaths)
        {
            if (string.IsNullOrWhiteSpace(netManagerSource) ||
                existingLegacyFilePaths == null)
            {
                return false;
            }

            bool isCombined =
                s_CombinedNetManagerDeclaration.IsMatch(netManagerSource) &&
                !s_PartialNetManagerDeclaration.IsMatch(netManagerSource);
            if (!isCombined) return false;

            foreach (string path in existingLegacyFilePaths)
            {
                if (!string.IsNullOrWhiteSpace(path)) return true;
            }

            return false;
        }

        /// <summary>
        /// Finds an incompatible mixed LiteNetLib source layout in the installed PurrNet assets.
        /// </summary>
        public static bool TryGetMixedLiteNetLibLayout(
            out string message,
            out string[] staleAssetPaths)
        {
            message = null;
            staleAssetPaths = Array.Empty<string>();

            string[] candidates = AssetDatabase.FindAssets("NetManager");
            for (int i = 0; i < candidates.Length; i++)
            {
                string assetPath = AssetDatabase.GUIDToAssetPath(candidates[i]);
                if (!assetPath.EndsWith(
                        "/Externals/LiteNetLib/NetManager.cs",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string fullPath = AssetPathToFullPath(assetPath);
                if (!File.Exists(fullPath)) continue;

                string directory = Path.GetDirectoryName(assetPath);
                if (string.IsNullOrEmpty(directory)) continue;

                var staleFiles = new List<string>();
                for (int fileIndex = 0; fileIndex < s_LegacyNetManagerFileNames.Length; fileIndex++)
                {
                    string stalePath = $"{directory}/{s_LegacyNetManagerFileNames[fileIndex]}";
                    if (File.Exists(AssetPathToFullPath(stalePath)))
                    {
                        staleFiles.Add(stalePath);
                    }
                }

                string source;
                try
                {
                    source = File.ReadAllText(fullPath);
                }
                catch (IOException)
                {
                    continue;
                }
                catch (UnauthorizedAccessException)
                {
                    continue;
                }

                if (!HasMixedLiteNetLibLayout(source, staleFiles))
                {
                    continue;
                }

                staleAssetPaths = staleFiles.ToArray();
                message =
                    "PurrNet contains a combined LiteNetLib NetManager.cs together with obsolete " +
                    $"split NetManager file(s): {string.Join(", ", staleAssetPaths)}. This is an " +
                    "in-place upgrade residue and causes CS0260/duplicate declaration errors. " +
                    "Close Unity, remove the complete Assets/PurrNet folder, and reinstall one clean " +
                    "PurrNet version. Do not fix this by adding the partial modifier.";
                return true;
            }

            return false;
        }

        private static string AssetPathToFullPath(string assetPath)
        {
            string projectRoot = Path.GetDirectoryName(Application.dataPath);
            return Path.GetFullPath(Path.Combine(projectRoot ?? string.Empty, assetPath));
        }

        private static void LogMixedLayoutOnce()
        {
            if (SessionState.GetBool(SESSION_WARNING_KEY, false)) return;
            if (!TryGetMixedLiteNetLibLayout(out string message, out _)) return;

            SessionState.SetBool(SESSION_WARNING_KEY, true);
            Debug.LogError($"[GC2 Networking][PurrNet Upgrade] {message}");
        }
    }
}
#endif
