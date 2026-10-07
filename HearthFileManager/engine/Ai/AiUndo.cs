using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;

namespace HearthFileManager.engine.Ai
{
    /// <summary>
    /// Snapshot store for one AI turn. Before the AI first touches a path in a turn, the original
    /// state is copied to App_Data/ai-undo/{turnId}/. Undo restores every touched path.
    /// </summary>
    public class AiUndo
    {
        public const int KeepTurns = 50;

        class Entry
        {
            public string Path { get; set; }
            /// <summary>file, dir or none (did not exist before the turn).</summary>
            public string Kind { get; set; }
            public string Backup { get; set; }
        }

        class Manifest
        {
            public string Root { get; set; }
            public List<Entry> Entries { get; set; }
        }

        readonly FsService _fs;
        readonly string _dir;
        readonly List<Entry> _entries = new List<Entry>();
        readonly HashSet<string> _seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public string TurnId { get; }
        public IReadOnlyCollection<string> ChangedPaths => _seen;

        public static string UndoRoot(FsService fs) => Path.Combine(fs.AppDataPath, "ai-undo");

        public AiUndo(FsService fs, string turnId)
        {
            _fs = fs;
            TurnId = turnId;
            _dir = Path.Combine(UndoRoot(fs), turnId);
        }

        /// <summary>
        /// Call before any mutation of rel. Only the first call per path per turn snapshots.
        /// If parent folders are missing (they will be auto-created), the top-most missing one is
        /// recorded instead so undo removes it entirely.
        /// </summary>
        public void Snapshot(string rel)
        {
            rel = FsService.NormalizeRel(rel);
            string top = rel;
            while (FsService.ParentOf(top) != "" && !_fs.Exists(FsService.ParentOf(top))) top = FsService.ParentOf(top);
            if (top != rel) { SnapshotExact(top); _seen.Add(rel); return; }
            SnapshotExact(rel);
        }

        void SnapshotExact(string rel)
        {
            if (!_seen.Add(rel)) return;
            // If an ancestor folder was already snapshotted (copied, or new and removed on undo), it covers this path.
            foreach (Entry e in _entries)
                if (e.Kind != "file" && rel.StartsWith(e.Path + "/", StringComparison.OrdinalIgnoreCase)) return;

            Directory.CreateDirectory(_dir);
            string full = _fs.Resolve(rel);
            var entry = new Entry { Path = rel, Kind = "none" };
            if (File.Exists(full))
            {
                entry.Kind = "file";
                entry.Backup = _entries.Count + ".bak";
                File.Copy(full, Path.Combine(_dir, entry.Backup), true);
            }
            else if (Directory.Exists(full))
            {
                entry.Kind = "dir";
                entry.Backup = _entries.Count + ".dir";
                FsService.CopyDir(full, Path.Combine(_dir, entry.Backup));
            }
            _entries.Add(entry);
            var manifest = new Manifest { Root = _fs.Root, Entries = _entries };
            File.WriteAllText(Path.Combine(_dir, "manifest.json"), JsonConvert.SerializeObject(manifest, Formatting.Indented), new UTF8Encoding(false));
        }

        /// <summary>Restores all paths touched in a turn. Returns the list of restored paths.</summary>
        public static List<string> Undo(FsService fs, string turnId)
        {
            if (string.IsNullOrEmpty(turnId) || turnId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || turnId.Contains(".."))
                throw new FsException("Invalid undo id.");
            string dir = Path.Combine(UndoRoot(fs), turnId);
            string manifest = Path.Combine(dir, "manifest.json");
            if (!File.Exists(manifest)) throw new FsException("This change can no longer be undone (it was already undone or is too old).");

            var m = JsonConvert.DeserializeObject<Manifest>(File.ReadAllText(manifest, Encoding.UTF8));
            // paths are relative to the root the AI worked in; refuse if this user's root is different
            if (!string.Equals(m.Root, fs.Root, StringComparison.OrdinalIgnoreCase))
                throw new FsException("This change was made in a different root folder, so it can't be undone from here.");
            var entries = m.Entries;
            var restored = new List<string>();
            // reverse order so later snapshots (children) are handled before parents
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                Entry e = entries[i];
                string full = fs.Resolve(e.Path);
                if (Directory.Exists(full)) Directory.Delete(full, true);
                else if (File.Exists(full)) File.Delete(full);

                if (e.Kind == "file")
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(full));
                    File.Copy(Path.Combine(dir, e.Backup), full, true);
                }
                else if (e.Kind == "dir")
                {
                    FsService.CopyDir(Path.Combine(dir, e.Backup), full);
                }
                restored.Add(e.Path);
            }
            Directory.Delete(dir, true);
            return restored;
        }

        public static void Prune(FsService fs)
        {
            string root = UndoRoot(fs);
            if (!Directory.Exists(root)) return;
            var dirs = new List<DirectoryInfo>(new DirectoryInfo(root).GetDirectories());
            dirs.Sort((a, b) => b.CreationTimeUtc.CompareTo(a.CreationTimeUtc));
            for (int i = KeepTurns; i < dirs.Count; i++) { try { dirs[i].Delete(true); } catch { } }
        }
    }
}
