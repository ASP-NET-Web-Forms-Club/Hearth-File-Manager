using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;

namespace HearthFileManager.engine.Ai
{
    /// <summary>Result of one tool call: JSON for functionResponse plus optional inline images.</summary>
    public class AiToolResult
    {
        public JObject Response { get; set; }
        public List<JObject> InlineParts { get; } = new List<JObject>();
        /// <summary>Short human description for the chat activity log.</summary>
        public string Summary { get; set; }
        public bool Changed { get; set; }
    }

    /// <summary>
    /// The MCP-like tools Gemini may call. All paths are relative to the user's website root and the
    /// recycle bin is off-limits; deletes go to the recycle bin. Every mutation is snapshotted for undo.
    /// </summary>
    public class AiTools
    {
        const int MaxReadBytes = 400 * 1024;
        const int MaxWriteBytes = 1024 * 1024;
        const int MaxImageBytes = 4 * 1024 * 1024;

        readonly FsService _fs;
        readonly AiUndo _undo;

        public AiTools(FsService fs, AiUndo undo)
        {
            _fs = fs;
            _undo = undo;
        }

        // ------------------------------------------------------------------ declarations

        public static JArray Declarations()
        {
            return new JArray(
                Fn("list_files", "List files and folders. Paths are relative to the website root, e.g. '' (the root itself), 'images' or 'css'.",
                    Props(P("path", "string", "Folder path, '' for the root"), P("recursive", "boolean", "List all sub-folders too (max 500 entries)"))),
                Fn("search_text", "Search text inside files (case-insensitive). Returns file path, line number and line text.",
                    Props(P("query", "string", "Text or regex to find"), P("path", "string", "Folder to search, default '' (whole website)"),
                          P("file_pattern", "string", "Optional file mask like '*.php'"), P("regex", "boolean", "Treat query as a regular expression")),
                    "query"),
                Fn("read_files", "Read one or more files. Text files return their full content. Images (png, jpg, gif, webp) are shown to you as pictures.",
                    Props(PArr("paths", "File paths to read, e.g. ['index.php','css/style.css']")), "paths"),
                Fn("write_file", "Create a file or completely replace its content. Parent folders are created automatically. Prefer replace_in_file for small changes to big files.",
                    Props(P("path", "string", "File path, e.g. 'index.php' or 'about/index.html'"), P("content", "string", "The full file content")), "path", "content"),
                Fn("replace_in_file", "Replace an exact piece of text in a file. old_text must match exactly (including spaces and line breaks) and be unique unless replace_all is true.",
                    Props(P("path", "string", "File path"), P("old_text", "string", "Exact text to find"), P("new_text", "string", "Replacement text"),
                          P("replace_all", "boolean", "Replace every occurrence")), "path", "old_text", "new_text"),
                Fn("create_folder", "Create a folder (and any missing parent folders).",
                    Props(P("path", "string", "Folder path, e.g. 'images'")), "path"),
                Fn("move_path", "Move or rename a file or folder.",
                    Props(P("from", "string", "Current path"), P("to", "string", "New full path (not just the folder)")), "from", "to"),
                Fn("delete_path", "Delete a file or folder. It is moved to the recycle bin, so the user can restore it.",
                    Props(P("path", "string", "Path to delete")), "path"),
                Fn("sqlite_query", "Run SQL on a SQLite database file inside the private 'App_Data' folder of the website (created if missing). Use it to create tables or inspect data for the PHP website. SELECT returns rows; other statements return affected row counts. Multiple statements separated by ; are allowed.",
                    Props(P("database", "string", "Database file name inside 'App_Data', e.g. 'site.db'"), P("sql", "string", "SQL to run")), "database", "sql")
            );
        }

        static JObject Fn(string name, string desc, JObject props, params string[] required)
        {
            var parameters = new JObject { ["type"] = "object", ["properties"] = props };
            if (required.Length > 0) parameters["required"] = new JArray(required);
            return new JObject { ["name"] = name, ["description"] = desc, ["parameters"] = parameters };
        }

        static JProperty P(string name, string type, string desc) => new JProperty(name, new JObject { ["type"] = type, ["description"] = desc });
        static JProperty PArr(string name, string desc) => new JProperty(name, new JObject { ["type"] = "array", ["items"] = new JObject { ["type"] = "string" }, ["description"] = desc });
        static JObject Props(params JProperty[] p) => new JObject(p);

        // ------------------------------------------------------------------ dispatch

        public AiToolResult Execute(string name, JObject args)
        {
            args = args ?? new JObject();
            try
            {
                switch (name)
                {
                    case "list_files": return ListFiles(args);
                    case "search_text": return SearchText(args);
                    case "read_files": return ReadFiles(args);
                    case "write_file": return WriteFile(args);
                    case "replace_in_file": return ReplaceInFile(args);
                    case "create_folder": return CreateFolder(args);
                    case "move_path": return MovePath(args);
                    case "delete_path": return DeletePath(args);
                    case "sqlite_query": return SqliteQuery(args);
                    default: return Error("Unknown tool: " + name, "Unknown tool " + name);
                }
            }
            catch (FsException ex) { return Error(ex.Message, name + " failed"); }
            catch (Exception ex) { return Error(ex.GetType().Name + ": " + ex.Message, name + " failed"); }
        }

        static AiToolResult Ok(JObject response, string summary, bool changed = false)
        {
            response["ok"] = true;
            return new AiToolResult { Response = response, Summary = summary, Changed = changed };
        }

        static AiToolResult Error(string message, string summary)
        {
            return new AiToolResult { Response = new JObject { ["ok"] = false, ["error"] = message }, Summary = summary + ": " + message };
        }

        static string Str(JObject a, string k) => a[k] == null || a[k].Type == JTokenType.Null ? null : a[k].ToString();
        static bool Bool(JObject a, string k) => a[k] != null && a[k].Type == JTokenType.Boolean && (bool)a[k];

        /// <summary>The AI may not read or write inside the recycle bin directly.</summary>
        string Allowed(string rel)
        {
            rel = FsService.NormalizeRel(rel);
            if (FsService.IsInRecycle(rel)) throw new FsException("The recycle bin is managed by the system and is not accessible.");
            _fs.Resolve(rel);
            return rel;
        }

        // ------------------------------------------------------------------ tools

        AiToolResult ListFiles(JObject a)
        {
            string rel = Allowed(Str(a, "path") ?? "");
            bool recursive = Bool(a, "recursive");
            var items = new JArray();
            Walk(rel, recursive, items);
            return Ok(new JObject { ["path"] = rel, ["items"] = items, ["truncated"] = items.Count >= 500 }, "Looked at " + (rel == "" ? "all folders" : rel));
        }

        void Walk(string rel, bool recursive, JArray items)
        {
            foreach (obFsItem it in _fs.List(rel))
            {
                if (items.Count >= 500) return;
                if (FsService.IsInRecycle(it.Path)) continue;
                items.Add(it.IsDir
                    ? new JObject { ["path"] = it.Path + "/", ["type"] = "folder" }
                    : new JObject { ["path"] = it.Path, ["size"] = it.Size, ["modified"] = it.ModifiedUtc.ToString("yyyy-MM-dd HH:mm") });
                if (recursive && it.IsDir) Walk(it.Path, true, items);
            }
        }

        AiToolResult SearchText(JObject a)
        {
            string query = Str(a, "query");
            string rel = Allowed(Str(a, "path") ?? "");
            var hits = _fs.Search(query, rel, Str(a, "file_pattern"), Bool(a, "regex"), 150);
            var arr = new JArray();
            foreach (var h in hits) arr.Add(new JObject { ["path"] = h.Path, ["line"] = h.Line, ["text"] = h.Text });
            return Ok(new JObject { ["matches"] = arr, ["count"] = arr.Count }, $"Searched for \"{query}\"");
        }

        AiToolResult ReadFiles(JObject a)
        {
            var paths = new List<string>();
            if (a["paths"] is JArray ja) foreach (JToken t in ja) paths.Add(t.ToString());
            else if (Str(a, "path") != null) paths.Add(Str(a, "path"));

            var result = new AiToolResult { Response = new JObject { ["ok"] = true } };
            var files = new JArray();
            int total = 0;
            foreach (string p in paths)
            {
                var f = new JObject { ["path"] = p };
                try
                {
                    string rel = Allowed(p);
                    string full = _fs.Resolve(rel);
                    if (!File.Exists(full)) throw new FsException("File not found");
                    long len = new FileInfo(full).Length;
                    string kind = FsService.KindOf(FsService.ExtOf(rel));
                    if (kind == "image" && FsService.ExtOf(rel) != "svg")
                    {
                        if (len > MaxImageBytes) throw new FsException("Image is too large to view");
                        result.InlineParts.Add(new JObject { ["inlineData"] = new JObject { ["mimeType"] = FsService.MimeOf(rel), ["data"] = Convert.ToBase64String(File.ReadAllBytes(full)) } });
                        f["image"] = "attached below as a picture";
                    }
                    else if (FsService.IsTextFile(rel) || FsService.ExtOf(rel) == "svg")
                    {
                        if (total + len > MaxReadBytes) throw new FsException("Skipped: reading limit reached for this call; read it separately");
                        f["content"] = File.ReadAllText(full, Encoding.UTF8);
                        total += (int)len;
                    }
                    else f["info"] = $"Binary file ({len} bytes), content not shown";
                }
                catch (Exception ex) { f["error"] = ex.Message; }
                files.Add(f);
            }
            result.Response["files"] = files;
            result.Summary = "Read " + string.Join(", ", paths);
            return result;
        }

        AiToolResult WriteFile(JObject a)
        {
            string rel = Allowed(Str(a, "path"));
            string content = Str(a, "content") ?? "";
            if (Encoding.UTF8.GetByteCount(content) > MaxWriteBytes) throw new FsException("Content too large (1 MB max per file).");
            bool existed = _fs.Exists(rel);
            _undo.Snapshot(rel);
            _fs.WriteText(rel, content);
            return Ok(new JObject { ["path"] = rel, ["bytes"] = Encoding.UTF8.GetByteCount(content) }, (existed ? "Updated " : "Created ") + rel, true);
        }

        AiToolResult ReplaceInFile(JObject a)
        {
            string rel = Allowed(Str(a, "path"));
            string oldText = Str(a, "old_text");
            string newText = Str(a, "new_text") ?? "";
            if (string.IsNullOrEmpty(oldText)) throw new FsException("old_text is empty.");
            string text = _fs.ReadText(rel);

            // tolerate CRLF vs LF differences between the model and the file
            if (text.IndexOf(oldText, StringComparison.Ordinal) < 0 && text.Contains("\r\n"))
            {
                oldText = oldText.Replace("\r\n", "\n").Replace("\n", "\r\n");
                newText = newText.Replace("\r\n", "\n").Replace("\n", "\r\n");
            }

            int count = 0, idx = 0;
            while ((idx = text.IndexOf(oldText, idx, StringComparison.Ordinal)) >= 0) { count++; idx += oldText.Length; }
            if (count == 0) throw new FsException("old_text was not found. Read the file again and copy the exact text.");
            bool all = Bool(a, "replace_all");
            if (count > 1 && !all) throw new FsException($"old_text appears {count} times. Include more surrounding text to make it unique, or set replace_all.");

            string updated;
            if (all) updated = text.Replace(oldText, newText);
            else { int at = text.IndexOf(oldText, StringComparison.Ordinal); updated = text.Substring(0, at) + newText + text.Substring(at + oldText.Length); }

            _undo.Snapshot(rel);
            _fs.WriteText(rel, updated);
            return Ok(new JObject { ["path"] = rel, ["replaced"] = all ? count : 1 }, "Edited " + rel, true);
        }

        AiToolResult CreateFolder(JObject a)
        {
            string rel = Allowed(Str(a, "path"));
            if (_fs.Exists(rel)) return Ok(new JObject { ["path"] = rel, ["note"] = "already exists" }, "Folder " + rel + " exists");
            // snapshot the top-most missing ancestor so undo removes everything created
            string top = rel;
            while (FsService.ParentOf(top) != "" && !_fs.Exists(FsService.ParentOf(top))) top = FsService.ParentOf(top);
            _undo.Snapshot(top);
            Directory.CreateDirectory(_fs.Resolve(rel));
            return Ok(new JObject { ["path"] = rel }, "Created folder " + rel, true);
        }

        AiToolResult MovePath(JObject a)
        {
            string from = Allowed(Str(a, "from"));
            string to = Allowed(Str(a, "to"));
            _undo.Snapshot(from);
            _undo.Snapshot(to);
            string dest = _fs.MoveTo(from, to);
            return Ok(new JObject { ["from"] = from, ["to"] = dest }, $"Moved {from} → {dest}", true);
        }

        AiToolResult DeletePath(JObject a)
        {
            string rel = Allowed(Str(a, "path"));
            _undo.Snapshot(rel);
            string bin = _fs.Delete(rel, "gemini");
            return Ok(new JObject { ["path"] = rel, ["recycled_as"] = bin }, "Deleted " + rel + " (moved to recycle bin)", true);
        }

        AiToolResult SqliteQuery(JObject a)
        {
            string db = FsService.NameOf(Str(a, "database") ?? "");
            if (db == "" || db.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new FsException("Invalid database name.");
            string ext = FsService.ExtOf(db);
            if (ext != "db" && ext != "sqlite" && ext != "sqlite3") db += ".db";
            string rel = "App_Data/" + db;
            Directory.CreateDirectory(_fs.Resolve("App_Data"));
            string sql = Str(a, "sql") ?? "";

            string head = sql.TrimStart();
            bool isQuery = head.StartsWith("select", StringComparison.OrdinalIgnoreCase) ||
                           head.StartsWith("pragma", StringComparison.OrdinalIgnoreCase) ||
                           head.StartsWith("with", StringComparison.OrdinalIgnoreCase);
            if (!isQuery) _undo.Snapshot(rel);
            var resp = new JObject { ["database"] = rel };
            using (var conn = new SQLiteConnection($"Data Source={_fs.Resolve(rel)};Version=3;Foreign Keys=True;"))
            {
                conn.Open();
                using (var cmd = new SQLiteCommand(sql, conn))
                {
                    if (isQuery)
                    {
                        var rows = new JArray();
                        using (var r = cmd.ExecuteReader())
                        {
                            while (r.Read() && rows.Count < 200)
                            {
                                var row = new JObject();
                                for (int i = 0; i < r.FieldCount; i++) row[r.GetName(i)] = r.IsDBNull(i) ? null : JToken.FromObject(r.GetValue(i));
                                rows.Add(row);
                            }
                        }
                        resp["rows"] = rows;
                    }
                    else resp["affected_rows"] = cmd.ExecuteNonQuery();
                }
            }
            return Ok(resp, (isQuery ? "Looked up data in " : "Updated database ") + rel, !isQuery);
        }
    }
}
