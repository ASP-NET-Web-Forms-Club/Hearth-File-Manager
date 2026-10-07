using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace HearthFileManager.engine
{
    public class FsException : Exception
    {
        public FsException(string message) : base(message) { }
    }

    public class obFsItem
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public bool IsDir { get; set; }
        public long Size { get; set; }
        public DateTime ModifiedUtc { get; set; }
        public string Ext { get; set; }
        public string Kind { get; set; }
        /// <summary>Recycle-bin entries only: where the item came from.</summary>
        public string OriginalPath { get; set; }
        public string DeletedBy { get; set; }
    }

    public class obRecycleEntry
    {
        public string OriginalPath { get; set; }
        public DateTime DeletedUtc { get; set; }
        public string DeletedBy { get; set; }
    }

    /// <summary>
    /// All file-system work for one root folder (the main website root, or a user's own root).
    /// Every path is relative to that root ("index.php", "css/site.css") and goes through Resolve(),
    /// which refuses anything outside it. The recycle bin is private to Hearth (App_Data/recycle-bin)
    /// and appears as the virtual folder "$recycle"; each root only sees its own deleted items.
    /// HttpContext-free: used by the HTTP handlers, the Gemini tools and PowerShell tests.
    /// </summary>
    public class FsService
    {
        /// <summary>Virtual path of the recycle bin (cannot clash with a real folder name in practice).</summary>
        public const string RecycleFolder = "$recycle";

        static readonly object RecycleLock = new object();

        public string Root { get; }
        public string AppDataPath { get; }
        public string RecycleDir => System.IO.Path.Combine(AppDataPath, "recycle-bin");
        string RecycleIndexFile => System.IO.Path.Combine(AppDataPath, "recycle-index.json");
        public string TempPath => System.IO.Path.Combine(AppDataPath, "tmp");

        /// <param name="appDataPath">Hearth's App_Data (recycle bin, undo, temp).</param>
        /// <param name="root">Absolute folder this instance manages.</param>
        public FsService(string appDataPath, string root)
        {
            AppDataPath = System.IO.Path.GetFullPath(appDataPath);
            Root = System.IO.Path.GetFullPath(root).TrimEnd('\\');
            if (Root.Length == 2 && Root[1] == ':') Root += "\\";
            EnsureLayout();
        }

        /// <summary>File service for a signed-in user: their own root, or the main root.</summary>
        public static FsService ForUser(obUser user)
        {
            string root = AppConfig.UserRoot(user);
            string problem = AppConfig.RootProblem(root);
            if (problem != null) throw new FsException("Your root folder is not allowed: " + problem);
            return new FsService(AppConfig.AppDataPath, root);
        }

        public void EnsureLayout()
        {
            Directory.CreateDirectory(RecycleDir);
            Directory.CreateDirectory(TempPath);
            try { Directory.CreateDirectory(Root); } catch { /* reported by CanWrite() */ }
        }

        /// <summary>Checks that the IIS identity can create files in the root. Returns null when OK.</summary>
        public string WriteProblem()
        {
            try
            {
                if (!Directory.Exists(Root)) return "The folder does not exist and could not be created.";
                string probe = System.IO.Path.Combine(Root, ".hearth-write-test-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                return null;
            }
            catch (Exception ex) { return ex.Message; }
        }

        static bool IsUnder(string path, string folder) =>
            string.Equals(path, folder, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(folder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

        // ------------------------------------------------------------------ paths

        public static string NormalizeRel(string rel)
        {
            rel = (rel ?? "").Replace('\\', '/').Trim();
            while (rel.Contains("//")) rel = rel.Replace("//", "/");
            return rel.Trim('/');
        }

        /// <summary>Relative path → absolute path, guaranteed to be inside Root (or inside the recycle bin for "$recycle/...").</summary>
        public string Resolve(string rel)
        {
            rel = NormalizeRel(rel);
            if (rel.IndexOf(':') >= 0 || rel.IndexOf('\0') >= 0) throw new FsException("Invalid path: " + rel);

            string baseDir = Root, sub = rel;
            if (IsInRecycle(rel))
            {
                baseDir = RecycleDir;
                sub = rel.Length > RecycleFolder.Length ? rel.Substring(RecycleFolder.Length + 1) : "";
                // only this root's own deleted items are reachable
                string top = sub.Split('/')[0];
                if (top.Length > 0 && !OwnsRecycleItem(top)) throw new FsException("Not found in your recycle bin: " + top);
            }
            string full = System.IO.Path.GetFullPath(System.IO.Path.Combine(baseDir, sub.Replace('/', '\\')));
            if (!IsUnder(full, baseDir))
                throw new FsException("Path is outside the website folder: " + rel);
            return full.Length > 3 ? full.TrimEnd('\\') : full;
        }

        public string ToRel(string full)
        {
            if (IsUnder(full, RecycleDir))
                return full.Length <= RecycleDir.Length ? RecycleFolder : RecycleFolder + "/" + full.Substring(RecycleDir.Length + 1).Replace('\\', '/');
            string root = Root.TrimEnd('\\');
            if (full.Length <= root.Length) return "";
            return full.Substring(root.Length + 1).Replace('\\', '/');
        }

        /// <summary>The root itself and the recycle bin folder cannot be renamed, moved or deleted.</summary>
        public static bool IsSystemFolder(string rel)
        {
            rel = NormalizeRel(rel);
            return rel.Length == 0 || rel.Equals(RecycleFolder, StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsInRecycle(string rel)
        {
            rel = NormalizeRel(rel);
            return rel.Equals(RecycleFolder, StringComparison.OrdinalIgnoreCase) ||
                   rel.StartsWith(RecycleFolder + "/", StringComparison.OrdinalIgnoreCase);
        }

        static void ValidateName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new FsException("Name cannot be empty.");
            if (name == "." || name == ".." || name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)
                throw new FsException("The name contains characters that are not allowed: " + name);
        }

        void GuardSystem(string rel, string verb)
        {
            if (IsSystemFolder(rel))
                throw new FsException(NormalizeRel(rel).Length == 0 ? $"The root folder cannot be {verb}." : $"The recycle bin cannot be {verb}.");
        }

        // ------------------------------------------------------------------ listing

        public static string KindOf(string ext)
        {
            switch (ext)
            {
                case "jpg": case "jpeg": case "png": case "gif": case "webp": case "svg": case "bmp": case "ico": case "avif": return "image";
                case "html": case "htm": case "php": case "css": case "js": case "json": case "xml": case "sql": case "ts": case "mjs": return "code";
                case "txt": case "md": case "csv": case "log": case "ini": case "htaccess": case "config": case "yml": case "yaml": case "env": return "text";
                case "zip": case "rar": case "7z": case "gz": case "tar": return "archive";
                case "pdf": case "doc": case "docx": case "xls": case "xlsx": case "ppt": case "pptx": return "doc";
                case "mp3": case "wav": case "ogg": case "m4a": return "audio";
                case "mp4": case "webm": case "mov": case "avi": return "video";
                case "db": case "sqlite": case "sqlite3": return "database";
                case "woff": case "woff2": case "ttf": case "otf": case "eot": return "font";
                default: return "other";
            }
        }

        public static string ExtOf(string name)
        {
            int i = name.LastIndexOf('.');
            return i >= 0 ? name.Substring(i + 1).ToLowerInvariant() : "";
        }

        public static bool IsTextFile(string name)
        {
            string k = KindOf(ExtOf(name));
            return k == "code" || k == "text" || ExtOf(name) == "" || name.StartsWith(".");
        }

        public List<obFsItem> List(string rel)
        {
            string full = Resolve(rel);
            if (!Directory.Exists(full)) throw new FsException("Folder not found: " + NormalizeRel(rel));

            bool recycle = IsInRecycle(rel) && NormalizeRel(rel).Equals(RecycleFolder, StringComparison.OrdinalIgnoreCase);
            Dictionary<string, obRecycleEntry> idx = recycle ? LoadRecycleIndex() : null;

            var list = new List<obFsItem>();
            var di = new DirectoryInfo(full);
            // at the top of the recycle bin, show only items deleted from inside this root
            foreach (DirectoryInfo d in di.GetDirectories())
                if (idx == null || idx.ContainsKey(d.Name) && Owns(idx[d.Name])) list.Add(Describe(d, idx));
            foreach (FileInfo f in di.GetFiles())
                if (idx == null || idx.ContainsKey(f.Name) && Owns(idx[f.Name])) list.Add(Describe(f, idx));
            list.Sort((a, b) => a.IsDir != b.IsDir ? (a.IsDir ? -1 : 1) : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return list;
        }

        obFsItem Describe(FileSystemInfo fsi, Dictionary<string, obRecycleEntry> idx)
        {
            bool isDir = fsi is DirectoryInfo;
            string ext = isDir ? "" : ExtOf(fsi.Name);
            var item = new obFsItem
            {
                Name = fsi.Name,
                Path = ToRel(fsi.FullName),
                IsDir = isDir,
                Size = isDir ? 0 : ((FileInfo)fsi).Length,
                ModifiedUtc = fsi.LastWriteTimeUtc,
                Ext = ext,
                Kind = isDir ? "folder" : KindOf(ext)
            };
            obRecycleEntry e;
            if (idx != null && idx.TryGetValue(fsi.Name, out e)) { item.OriginalPath = ToRel(e.OriginalPath); item.DeletedBy = e.DeletedBy; }
            return item;
        }

        public bool Exists(string rel)
        {
            string full = Resolve(rel);
            return File.Exists(full) || Directory.Exists(full);
        }

        // ------------------------------------------------------------------ create / read / write

        public string CreateFolder(string parentRel, string name)
        {
            ValidateName(name);
            string rel = Combine(parentRel, name);
            string full = Resolve(rel);
            if (File.Exists(full) || Directory.Exists(full)) throw new FsException($"'{name}' already exists.");
            Directory.CreateDirectory(full);
            return rel;
        }

        public string CreateFile(string parentRel, string name, string content)
        {
            ValidateName(name);
            string rel = Combine(parentRel, name);
            string full = Resolve(rel);
            if (File.Exists(full) || Directory.Exists(full)) throw new FsException($"'{name}' already exists.");
            WriteText(rel, content ?? "");
            return rel;
        }

        public string ReadText(string rel, int maxBytes = 2 * 1024 * 1024)
        {
            string full = Resolve(rel);
            if (!File.Exists(full)) throw new FsException("File not found: " + NormalizeRel(rel));
            if (new FileInfo(full).Length > maxBytes) throw new FsException("File is too large to open as text: " + NormalizeRel(rel));
            return File.ReadAllText(full, Encoding.UTF8);
        }

        /// <summary>Writes UTF-8 (no BOM), creating parent folders. Keeps a BOM if the file already had one.</summary>
        public void WriteText(string rel, string content)
        {
            string full = Resolve(rel);
            if (Directory.Exists(full)) throw new FsException("A folder already exists with that name: " + NormalizeRel(rel));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full));
            bool bom = false;
            if (File.Exists(full))
            {
                using (var fs = File.OpenRead(full))
                {
                    byte[] b = new byte[3];
                    bom = fs.Read(b, 0, 3) == 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF;
                }
            }
            File.WriteAllText(full, content ?? "", new UTF8Encoding(bom));
        }

        public static string Combine(string a, string b)
        {
            a = NormalizeRel(a); b = NormalizeRel(b);
            if (a.Length == 0) return b;
            if (b.Length == 0) return a;
            return a + "/" + b;
        }

        public static string ParentOf(string rel)
        {
            rel = NormalizeRel(rel);
            int i = rel.LastIndexOf('/');
            return i < 0 ? "" : rel.Substring(0, i);
        }

        public static string NameOf(string rel)
        {
            rel = NormalizeRel(rel);
            int i = rel.LastIndexOf('/');
            return i < 0 ? rel : rel.Substring(i + 1);
        }

        // ------------------------------------------------------------------ rename / move / copy

        public string Rename(string rel, string newName)
        {
            GuardSystem(rel, "renamed");
            ValidateName(newName);
            string src = Resolve(rel);
            string destRel = Combine(ParentOf(rel), newName);
            string dest = Resolve(destRel);
            if (string.Equals(src, dest, StringComparison.Ordinal)) return destRel;
            bool caseOnly = string.Equals(src, dest, StringComparison.OrdinalIgnoreCase);
            if (!caseOnly && (File.Exists(dest) || Directory.Exists(dest))) throw new FsException($"'{newName}' already exists.");
            MoveRaw(src, dest, caseOnly);
            return destRel;
        }

        /// <summary>Moves a file or folder to an exact destination path (not into a folder).</summary>
        public string MoveTo(string rel, string destRel, bool overwrite = false)
        {
            GuardSystem(rel, "moved");
            string src = Resolve(rel);
            string dest = Resolve(destRel);
            if (!File.Exists(src) && !Directory.Exists(src)) throw new FsException("Not found: " + NormalizeRel(rel));
            if (Directory.Exists(src) && (dest + "\\").StartsWith(src + "\\", StringComparison.OrdinalIgnoreCase))
                throw new FsException("A folder cannot be moved into itself.");
            if (File.Exists(dest) || Directory.Exists(dest))
            {
                if (!overwrite) throw new FsException($"'{NormalizeRel(destRel)}' already exists.");
                Delete(destRel, "user");
            }
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dest));
            MoveRaw(src, dest, false);
            return NormalizeRel(destRel);
        }

        public string MoveInto(string rel, string folderRel)
        {
            return MoveTo(rel, Combine(folderRel, NameOf(rel)));
        }

        public string CopyInto(string rel, string folderRel)
        {
            string src = Resolve(rel);
            string destRel = UniqueName(Combine(folderRel, NameOf(rel)));
            string dest = Resolve(destRel);
            if (Directory.Exists(src))
            {
                if ((dest + "\\").StartsWith(src + "\\", StringComparison.OrdinalIgnoreCase)) throw new FsException("A folder cannot be copied into itself.");
                CopyDir(src, dest);
            }
            else if (File.Exists(src)) File.Copy(src, dest);
            else throw new FsException("Not found: " + NormalizeRel(rel));
            return destRel;
        }

        static void MoveRaw(string src, string dest, bool caseOnly)
        {
            if (Directory.Exists(src))
            {
                if (caseOnly) { string tmp = src + ".~mv" + Guid.NewGuid().ToString("N").Substring(0, 6); Directory.Move(src, tmp); Directory.Move(tmp, dest); }
                else Directory.Move(src, dest);
            }
            else if (File.Exists(src)) File.Move(src, dest);
            else throw new FsException("Not found.");
        }

        public static void CopyDir(string src, string dest)
        {
            Directory.CreateDirectory(dest);
            foreach (string f in Directory.GetFiles(src)) File.Copy(f, System.IO.Path.Combine(dest, System.IO.Path.GetFileName(f)), true);
            foreach (string d in Directory.GetDirectories(src)) CopyDir(d, System.IO.Path.Combine(dest, System.IO.Path.GetFileName(d)));
        }

        /// <summary>"a/b.txt" → "a/b (1).txt" if taken.</summary>
        public string UniqueName(string rel)
        {
            if (!Exists(rel)) return NormalizeRel(rel);
            string parent = ParentOf(rel), name = NameOf(rel);
            string ext = System.IO.Path.GetExtension(name), stem = System.IO.Path.GetFileNameWithoutExtension(name);
            if (Directory.Exists(Resolve(rel))) { ext = ""; stem = name; }
            for (int i = 1; ; i++)
            {
                string cand = Combine(parent, $"{stem} ({i}){ext}");
                if (!Exists(cand)) return cand;
            }
        }

        // ------------------------------------------------------------------ recycle bin

        /// <summary>
        /// Moves an item to recycle-bin as "path_flattened.{actor}.{timestamp}".
        /// Items already in the recycle bin are deleted permanently.
        /// </summary>
        public string Delete(string rel, string actor)
        {
            GuardSystem(rel, "deleted");
            string full = Resolve(rel);
            if (!File.Exists(full) && !Directory.Exists(full)) throw new FsException("Not found: " + NormalizeRel(rel));

            if (IsInRecycle(rel))
            {
                if (ParentOf(rel) != RecycleFolder) throw new FsException("Delete the whole item from the recycle bin, or restore it first.");
                if (Directory.Exists(full)) Directory.Delete(full, true); else File.Delete(full);
                lock (RecycleLock)
                {
                    var idx = LoadRecycleIndex();
                    if (idx.Remove(NameOf(rel))) SaveRecycleIndex(idx);
                }
                return null;
            }

            string flat = NormalizeRel(rel).Replace('/', '_');
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string binName = $"{flat}.{actor}.{stamp}";
            lock (RecycleLock)
            {
                string target = System.IO.Path.Combine(RecycleDir, binName);
                int n = 1;
                while (File.Exists(target) || Directory.Exists(target)) target = System.IO.Path.Combine(RecycleDir, $"{binName}-{n++}");
                MoveRaw(full, target, false);

                var idx = LoadRecycleIndex();
                // absolute original path: the recycle bin is shared by all roots
                idx[System.IO.Path.GetFileName(target)] = new obRecycleEntry { OriginalPath = full, DeletedUtc = DateTime.UtcNow, DeletedBy = actor };
                SaveRecycleIndex(idx);
                return ToRel(target);
            }
        }

        /// <summary>Restores a recycle-bin item to its original place (or a unique sibling name if taken).</summary>
        public string Restore(string binRel)
        {
            binRel = NormalizeRel(binRel);
            if (!IsInRecycle(binRel) || IsSystemFolder(binRel) || ParentOf(binRel) != RecycleFolder)
                throw new FsException("Only items in the recycle bin can be restored.");
            lock (RecycleLock)
            {
                var idx = LoadRecycleIndex();
                obRecycleEntry e;
                if (!idx.TryGetValue(NameOf(binRel), out e) || !Owns(e)) throw new FsException("The original location of this item is unknown. Move it manually instead.");
                string dest = UniqueName(ToRel(e.OriginalPath));
                string destFull = Resolve(dest);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destFull));
                MoveRaw(Resolve(binRel), destFull, false);
                idx.Remove(NameOf(binRel));
                SaveRecycleIndex(idx);
                return dest;
            }
        }

        /// <summary>Permanently deletes this root's items in the recycle bin (other roots' items are kept).</summary>
        public int EmptyRecycleBin()
        {
            int n = 0;
            lock (RecycleLock)
            {
                var idx = LoadRecycleIndex();
                foreach (var kv in new List<KeyValuePair<string, obRecycleEntry>>(idx))
                {
                    if (!Owns(kv.Value)) continue;
                    string p = System.IO.Path.Combine(RecycleDir, kv.Key);
                    if (Directory.Exists(p)) Directory.Delete(p, true);
                    else if (File.Exists(p)) File.Delete(p);
                    idx.Remove(kv.Key);
                    n++;
                }
                SaveRecycleIndex(idx);
            }
            return n;
        }

        /// <summary>A recycle entry belongs to this root when it was deleted from inside it.</summary>
        bool Owns(obRecycleEntry e) => e != null && !string.IsNullOrEmpty(e.OriginalPath) &&
                                       System.IO.Path.IsPathRooted(e.OriginalPath) && IsUnder(e.OriginalPath, Root);

        bool OwnsRecycleItem(string name)
        {
            obRecycleEntry e;
            return LoadRecycleIndex().TryGetValue(name, out e) && Owns(e);
        }

        Dictionary<string, obRecycleEntry> LoadRecycleIndex()
        {
            if (!File.Exists(RecycleIndexFile)) return new Dictionary<string, obRecycleEntry>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var d = JsonConvert.DeserializeObject<Dictionary<string, obRecycleEntry>>(File.ReadAllText(RecycleIndexFile, Encoding.UTF8));
                return new Dictionary<string, obRecycleEntry>(d ?? new Dictionary<string, obRecycleEntry>(), StringComparer.OrdinalIgnoreCase);
            }
            catch { return new Dictionary<string, obRecycleEntry>(StringComparer.OrdinalIgnoreCase); }
        }

        void SaveRecycleIndex(Dictionary<string, obRecycleEntry> idx)
        {
            File.WriteAllText(RecycleIndexFile, JsonConvert.SerializeObject(idx, Formatting.Indented), new UTF8Encoding(false));
        }

        // ------------------------------------------------------------------ zip

        /// <summary>Zips the given items (files/folders) into destZipRel.</summary>
        public string Zip(IList<string> rels, string destZipRel)
        {
            if (rels.Count == 0) throw new FsException("Nothing selected.");
            destZipRel = UniqueName(destZipRel);
            string zipFull = Resolve(destZipRel);
            using (var zip = ZipFile.Open(zipFull, ZipArchiveMode.Create))
            {
                foreach (string rel in rels)
                {
                    string full = Resolve(rel);
                    if (string.Equals(full, zipFull, StringComparison.OrdinalIgnoreCase)) continue;
                    if (File.Exists(full)) zip.CreateEntryFromFile(full, NameOf(rel), CompressionLevel.Optimal);
                    else if (Directory.Exists(full)) AddDirToZip(zip, full, NameOf(rel), zipFull);
                    else throw new FsException("Not found: " + rel);
                }
            }
            return destZipRel;
        }

        /// <summary>Writes a zip of the given items to a stream (used for multi-file download).</summary>
        public void ZipToStream(IList<string> rels, Stream output)
        {
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
            {
                foreach (string rel in rels)
                {
                    string full = Resolve(rel);
                    if (File.Exists(full)) zip.CreateEntryFromFile(full, NameOf(rel), CompressionLevel.Fastest);
                    else if (Directory.Exists(full)) AddDirToZip(zip, full, NameOf(rel), null);
                }
            }
        }

        static void AddDirToZip(ZipArchive zip, string dir, string prefix, string skipFile)
        {
            string[] files = Directory.GetFiles(dir);
            string[] dirs = Directory.GetDirectories(dir);
            if (files.Length == 0 && dirs.Length == 0) zip.CreateEntry(prefix + "/");
            foreach (string f in files)
            {
                if (skipFile != null && string.Equals(f, skipFile, StringComparison.OrdinalIgnoreCase)) continue;
                zip.CreateEntryFromFile(f, prefix + "/" + System.IO.Path.GetFileName(f), CompressionLevel.Optimal);
            }
            foreach (string d in dirs) AddDirToZip(zip, d, prefix + "/" + System.IO.Path.GetFileName(d), skipFile);
        }

        /// <summary>Extracts into destFolderRel. Existing files are overwritten (old copy goes to recycle bin). Zip-slip safe.</summary>
        public int Unzip(string zipRel, string destFolderRel)
        {
            string zipFull = Resolve(zipRel);
            if (!File.Exists(zipFull)) throw new FsException("Zip file not found: " + zipRel);
            string destFull = Resolve(destFolderRel);
            Directory.CreateDirectory(destFull);

            int count = 0;
            using (var zip = ZipFile.OpenRead(zipFull))
            {
                foreach (ZipArchiveEntry entry in zip.Entries)
                {
                    string entryRel = Combine(destFolderRel, entry.FullName);
                    string target = Resolve(entryRel); // throws on ../ escape
                    if (entry.FullName.EndsWith("/") || entry.FullName.EndsWith("\\")) { Directory.CreateDirectory(target); continue; }
                    if (IsSystemFolder(entryRel)) continue;
                    if (Directory.Exists(target)) throw new FsException("A folder blocks extraction of " + entry.FullName);
                    if (File.Exists(target)) Delete(entryRel, "unzip");
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target));
                    entry.ExtractToFile(target, false);
                    count++;
                }
            }
            return count;
        }

        // ------------------------------------------------------------------ chunked upload

        static readonly Regex UploadIdRx = new Regex("^[a-zA-Z0-9]{8,64}$");

        string ChunkFile(string uploadId)
        {
            if (!UploadIdRx.IsMatch(uploadId ?? "")) throw new FsException("Invalid upload id.");
            return System.IO.Path.Combine(TempPath, uploadId + ".part");
        }

        /// <summary>Appends chunk #index. Chunks must arrive in order; index 0 starts a fresh file.</summary>
        public long AppendChunk(string uploadId, int index, Stream data, long maxBytes)
        {
            string part = ChunkFile(uploadId);
            if (index == 0 && File.Exists(part)) File.Delete(part);
            if (index > 0 && !File.Exists(part)) throw new FsException("Upload was interrupted. Please upload the file again.");
            using (var fs = new FileStream(part, index == 0 ? FileMode.Create : FileMode.Append, FileAccess.Write))
            {
                data.CopyTo(fs);
                if (fs.Length > maxBytes) { fs.Close(); File.Delete(part); throw new FsException($"File is larger than the {maxBytes / 1024 / 1024} MB limit."); }
                return fs.Length;
            }
        }

        /// <summary>Moves the finished upload into place. If a file exists there, it goes to the recycle bin first.</summary>
        public string FinishUpload(string uploadId, string destRel)
        {
            string part = ChunkFile(uploadId);
            if (!File.Exists(part)) throw new FsException("Upload data missing.");
            foreach (string seg in NormalizeRel(destRel).Split('/')) ValidateName(seg);
            if (IsInRecycle(destRel)) throw new FsException("You cannot upload into the recycle bin.");
            string dest = Resolve(destRel);
            if (Directory.Exists(dest)) throw new FsException("A folder already exists with that name.");
            if (File.Exists(dest)) Delete(destRel, "replaced");
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dest));
            File.Move(part, dest);
            return NormalizeRel(destRel);
        }

        public void CleanupTemp(TimeSpan olderThan)
        {
            foreach (string f in Directory.GetFiles(TempPath))
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(f) > olderThan) { try { File.Delete(f); } catch { } }
        }

        // ------------------------------------------------------------------ search

        public class obSearchHit
        {
            public string Path { get; set; }
            public int Line { get; set; }
            public string Text { get; set; }
        }

        /// <summary>Text search across text files (skips the recycle bin).</summary>
        public List<obSearchHit> Search(string query, string folderRel, string filePattern, bool regex, int maxHits = 200)
        {
            var hits = new List<obSearchHit>();
            if (string.IsNullOrEmpty(query)) return hits;
            Regex rx = regex ? new Regex(query, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2)) : null;
            string start = Resolve(folderRel);
            string pattern = string.IsNullOrWhiteSpace(filePattern) ? "*" : filePattern.Trim();

            foreach (string f in Directory.EnumerateFiles(start, pattern, SearchOption.AllDirectories))
            {
                string rel = ToRel(f);
                if (IsInRecycle(rel) || !IsTextFile(f) || new FileInfo(f).Length > 2 * 1024 * 1024) continue;
                int ln = 0;
                foreach (string line in File.ReadLines(f, Encoding.UTF8))
                {
                    ln++;
                    bool match = rx != null ? rx.IsMatch(line) : line.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!match) continue;
                    hits.Add(new obSearchHit { Path = rel, Line = ln, Text = line.Length > 240 ? line.Substring(0, 240) + "…" : line.Trim() });
                    if (hits.Count >= maxHits) return hits;
                }
            }
            return hits;
        }

        public static string MimeOf(string name)
        {
            switch (ExtOf(name))
            {
                case "jpg": case "jpeg": return "image/jpeg";
                case "png": return "image/png";
                case "gif": return "image/gif";
                case "webp": return "image/webp";
                case "svg": return "image/svg+xml";
                case "ico": return "image/x-icon";
                case "bmp": return "image/bmp";
                case "avif": return "image/avif";
                case "pdf": return "application/pdf";
                case "mp4": return "video/mp4";
                case "webm": return "video/webm";
                case "mp3": return "audio/mpeg";
                case "wav": return "audio/wav";
                case "txt": case "md": case "log": case "csv": return "text/plain; charset=utf-8";
                default: return "application/octet-stream";
            }
        }
    }
}
