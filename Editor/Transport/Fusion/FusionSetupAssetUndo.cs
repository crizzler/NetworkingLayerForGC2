using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Arawn.GameCreator2.Networking.Transport.Fusion.Editor
{
    /// <summary>
    /// Extends a normal Unity Undo group to the serialized project files changed by the
    /// Fusion setup wizard. Prefab-content saves and Fusion's JSON project config bypass
    /// Unity's object Undo system, so their exact bytes are restored on Undo/Redo.
    /// </summary>
    [InitializeOnLoad]
    internal static class FusionSetupAssetUndo
    {
        [Serializable]
        private sealed class FileSnapshot
        {
            [SerializeField] private string m_ProjectPath;
            [SerializeField] private bool m_Existed;
            [SerializeField] private bool m_IsDirectory;
            [SerializeField] private string m_Base64;

            public string ProjectPath => m_ProjectPath;
            public bool Existed => m_Existed;
            public bool IsDirectory => m_IsDirectory;

            public static FileSnapshot Capture(string projectPath)
            {
                string normalized = NormalizeProjectPath(projectPath);
                string absolute = ToAbsolutePath(normalized);
                bool isDirectory = Directory.Exists(absolute);
                bool isFile = File.Exists(absolute);
                bool exists = isDirectory || isFile;
                if (!exists)
                {
                    // The transaction explicitly includes generated asset folders. A
                    // path without a file extension is treated as a directory even on
                    // first run, when it does not exist yet.
                    isDirectory = !Path.HasExtension(normalized);
                }
                return new FileSnapshot
                {
                    m_ProjectPath = normalized,
                    m_Existed = exists,
                    m_IsDirectory = isDirectory,
                    m_Base64 = isFile
                        ? Convert.ToBase64String(File.ReadAllBytes(absolute))
                        : string.Empty
                };
            }

            public bool ContentEquals(FileSnapshot other)
            {
                return other != null &&
                       string.Equals(m_ProjectPath, other.m_ProjectPath, StringComparison.Ordinal) &&
                       m_Existed == other.m_Existed &&
                       m_IsDirectory == other.m_IsDirectory &&
                       string.Equals(m_Base64, other.m_Base64, StringComparison.Ordinal);
            }

            public void RestoreContent()
            {
                string absolute = ToAbsolutePath(m_ProjectPath);
                if (m_Existed)
                {
                    if (m_IsDirectory)
                    {
                        Directory.CreateDirectory(absolute);
                        return;
                    }

                    string directory = Path.GetDirectoryName(absolute);
                    if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                    File.WriteAllBytes(absolute, Convert.FromBase64String(m_Base64 ?? string.Empty));
                }
                else if (!m_IsDirectory && File.Exists(absolute))
                {
                    File.Delete(absolute);
                }
            }

            public void RemoveCreatedDirectory()
            {
                if (m_Existed || !m_IsDirectory) return;
                string absolute = ToAbsolutePath(m_ProjectPath);
                if (!Directory.Exists(absolute)) return;

                if (Directory.EnumerateFileSystemEntries(absolute).Any())
                {
                    Debug.LogWarning(
                        $"Fusion setup Undo preserved non-empty generated folder " +
                        $"'{m_ProjectPath}'. Its wizard-created files were still restored.");
                    return;
                }

                Directory.Delete(absolute, false);
            }
        }

        private sealed class UndoMarker : ScriptableObject
        {
            [SerializeField] internal int OperationId;
            [SerializeField] internal bool Applied;
        }

        private sealed class UndoJournal : ScriptableObject
        {
            [SerializeField] internal int OperationId;
            [SerializeField] internal List<FileSnapshot> Before = new();
            [SerializeField] internal List<FileSnapshot> After = new();
        }

        private sealed class Record
        {
            public UndoMarker Marker;
            public UndoJournal Journal;
            public bool LastApplied;
        }

        internal sealed class Transaction
        {
            private readonly Record m_Record;
            private readonly string[] m_Paths;
            private bool m_Finished;

            internal Transaction(IEnumerable<string> projectPaths, string undoName)
            {
                m_Paths = projectPaths
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Select(NormalizeProjectPath)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();

                int operationId = NextOperationId();
                var marker = ScriptableObject.CreateInstance<UndoMarker>();
                marker.hideFlags = HideFlags.HideAndDontSave;
                marker.name = $"Fusion Setup Undo Marker {operationId}";
                marker.OperationId = operationId;
                marker.Applied = false;

                var journal = ScriptableObject.CreateInstance<UndoJournal>();
                journal.hideFlags = HideFlags.HideAndDontSave;
                journal.name = $"Fusion Setup Undo Journal {operationId}";
                journal.OperationId = operationId;
                journal.Before = CaptureAll(m_Paths);

                Undo.RegisterCompleteObjectUndo(marker, undoName);
                marker.Applied = true;

                m_Record = new Record
                {
                    Marker = marker,
                    Journal = journal,
                    LastApplied = true
                };
                s_Records.Add(m_Record);
            }

            public void Commit()
            {
                if (m_Finished) return;
                m_Record.Journal.After = CaptureAll(m_Paths);
                RemoveUnchangedSnapshots(
                    m_Record.Journal.Before,
                    m_Record.Journal.After);
                m_Record.LastApplied = true;
                m_Finished = true;
            }

            public void Rollback()
            {
                if (m_Finished) return;
                RestoreAll(m_Record.Journal.Before);
                s_Records.Remove(m_Record);
                if (m_Record.Marker != null)
                    UnityEngine.Object.DestroyImmediate(m_Record.Marker);
                if (m_Record.Journal != null)
                    UnityEngine.Object.DestroyImmediate(m_Record.Journal);
                m_Finished = true;
            }
        }

        private static readonly List<Record> s_Records = new();
        private static bool s_Restoring;
        private static int s_NextOperationId =
            unchecked((int)(DateTime.UtcNow.Ticks & 0x3fffffff));

        static FusionSetupAssetUndo()
        {
            RecoverSurvivingRecords();
            Undo.undoRedoPerformed -= OnUndoRedoPerformed;
            Undo.undoRedoPerformed += OnUndoRedoPerformed;
        }

        public static Transaction Begin(IEnumerable<string> projectPaths, string undoName)
        {
            return new Transaction(projectPaths, undoName);
        }

        private static int NextOperationId()
        {
            if (++s_NextOperationId <= 0) s_NextOperationId = 1;
            return s_NextOperationId;
        }

        private static List<FileSnapshot> CaptureAll(IEnumerable<string> paths)
        {
            var result = new List<FileSnapshot>();
            foreach (string path in paths)
            {
                result.Add(FileSnapshot.Capture(path));
            }
            return result;
        }

        private static void RemoveUnchangedSnapshots(
            IList<FileSnapshot> before,
            IList<FileSnapshot> after)
        {
            if (before == null || after == null || before.Count != after.Count) return;
            for (int i = before.Count - 1; i >= 0; i--)
            {
                if (!before[i].ContentEquals(after[i])) continue;
                before.RemoveAt(i);
                after.RemoveAt(i);
            }
        }

        private static void RecoverSurvivingRecords()
        {
            UndoMarker[] markers = Resources.FindObjectsOfTypeAll<UndoMarker>();
            UndoJournal[] journals = Resources.FindObjectsOfTypeAll<UndoJournal>();
            foreach (UndoMarker marker in markers)
            {
                if (marker == null) continue;
                UndoJournal journal =
                    journals.FirstOrDefault(item =>
                        item != null && item.OperationId == marker.OperationId);
                if (journal == null) continue;
                s_Records.Add(new Record
                {
                    Marker = marker,
                    Journal = journal,
                    LastApplied = marker.Applied
                });
            }
        }

        private static void OnUndoRedoPerformed()
        {
            if (s_Restoring) return;

            for (int i = s_Records.Count - 1; i >= 0; i--)
            {
                Record record = s_Records[i];
                if (record.Marker == null || record.Journal == null)
                {
                    s_Records.RemoveAt(i);
                    continue;
                }

                bool applied = record.Marker.Applied;
                if (applied == record.LastApplied) continue;
                record.LastApplied = applied;
                RestoreAll(applied ? record.Journal.After : record.Journal.Before);
            }
        }

        private static void RestoreAll(IReadOnlyList<FileSnapshot> snapshots)
        {
            if (snapshots == null || snapshots.Count == 0) return;

            s_Restoring = true;
            try
            {
                AssetDatabase.StartAssetEditing();
                try
                {
                    for (int i = 0; i < snapshots.Count; i++)
                    {
                        snapshots[i]?.RestoreContent();
                    }

                    // Files are removed first. Newly-created folders are then removed from
                    // deepest to shallowest, and only while empty, so Undo never deletes
                    // unrelated assets created after the wizard ran.
                    foreach (FileSnapshot snapshot in snapshots
                                 .Where(item =>
                                     item != null &&
                                     item.IsDirectory &&
                                     !item.Existed)
                                 .OrderByDescending(item => item.ProjectPath.Length))
                    {
                        snapshot.RemoveCreatedDirectory();
                    }
                }
                finally
                {
                    AssetDatabase.StopAssetEditing();
                }

                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            }
            finally
            {
                s_Restoring = false;
            }
        }

        private static string NormalizeProjectPath(string path)
        {
            string normalized = path.Replace('\\', '/').Trim();
            if (!normalized.StartsWith("Assets/", StringComparison.Ordinal) &&
                !string.Equals(normalized, "Assets", StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"Fusion setup Undo only accepts project-relative Assets paths: '{path}'.",
                    nameof(path));
            }
            return normalized;
        }

        private static string ToAbsolutePath(string projectPath)
        {
            string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string absolute = Path.GetFullPath(Path.Combine(projectRoot, projectPath));
            string rootPrefix = projectRoot.TrimEnd(Path.DirectorySeparatorChar) +
                                Path.DirectorySeparatorChar;
            if (!absolute.StartsWith(rootPrefix, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Refusing to restore a path outside the Unity project: '{projectPath}'.");
            }
            return absolute;
        }
    }
}
