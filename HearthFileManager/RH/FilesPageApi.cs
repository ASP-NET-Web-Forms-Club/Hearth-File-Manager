using System;
using System.Collections.Generic;
using System.IO;
using System.Web;
using HearthFileManager.engine;
using Newtonsoft.Json;

namespace HearthFileManager.RH
{
    public class FilesPageApi
    {
        static HttpRequest Req => HttpContext.Current.Request;
        static FsService Fs => FsService.ForUser(AppSession.LoginUser);

        public static void HandleRequest()
        {
            string action = (Req["action"] + "").ToLower().Trim();
            try
            {
                switch (action)
                {
                    // GET endpoints (read-only, streamed)
                    case "download": Guard.LoggedIn(); Download(); return;
                    case "raw": Guard.LoggedIn(); Raw(); return;

                    case "list": Guard.LoggedIn(); List(); break;
                    case "read": Guard.LoggedIn(); Read(); break;
                    // everything that changes files needs the "Manage files" permission
                    case "save": Guard.Mutating(Perm.Files); Save(); break;
                    case "mkdir": Guard.Mutating(Perm.Files); MkDir(); break;
                    case "newfile": Guard.Mutating(Perm.Files); NewFile(); break;
                    case "rename": Guard.Mutating(Perm.Files); Rename(); break;
                    case "move": Guard.Mutating(Perm.Files); MoveOrCopy(false); break;
                    case "copy": Guard.Mutating(Perm.Files); MoveOrCopy(true); break;
                    case "delete": Guard.Mutating(Perm.Files); Delete(); break;
                    case "restore": Guard.Mutating(Perm.Files); Restore(); break;
                    case "empty-recycle": Guard.Mutating(Perm.Files); EmptyRecycle(); break;
                    case "zip": Guard.Mutating(Perm.Files); Zip(); break;
                    case "unzip": Guard.Mutating(Perm.Files); Unzip(); break;
                    case "upload-chunk": Guard.Mutating(Perm.Files); UploadChunk(); break;
                    default: ApiHelper.WriteError($"Unknown action: {action}", 400); break;
                }
            }
            catch (GuardException gx) { ApiHelper.WriteError(gx.Message, gx.StatusCode); }
            catch (FsException fx) { ApiHelper.WriteError(fx.Message, 400); }
            catch (IOException ix) { ApiHelper.WriteError("File error: " + ix.Message, 400); }
            catch (UnauthorizedAccessException) { ApiHelper.WriteError("Access denied by the server (file may be in use or read-only).", 400); }
            catch (Exception ex) { ApiHelper.WriteError("An error occurred: " + ex.Message, 500); }
            ApiHelper.EndResponse();
        }

        static string Path => Req.Form["path"] ?? Req.QueryString["path"] ?? "";

        static List<string> Paths()
        {
            string json = Req.Form["paths"] ?? Req.QueryString["paths"];
            if (string.IsNullOrEmpty(json)) return new List<string> { Path };
            return JsonConvert.DeserializeObject<List<string>>(json) ?? new List<string>();
        }

        static void List()
        {
            FsService fs = Fs;
            string rel = FsService.NormalizeRel(Path);
            ApiHelper.WriteJson(new { success = true, message = "Success", data = new { Path = rel, IsRecycle = FsService.IsInRecycle(rel) }, items = fs.List(rel) });
        }

        static void Read()
        {
            string rel = FsService.NormalizeRel(Path);
            string content = Fs.ReadText(rel);
            ApiHelper.WriteJson(new { success = true, message = "Success", data = new { Path = rel, Content = content } });
        }

        static void Save()
        {
            FsService fs = Fs;
            string rel = FsService.NormalizeRel(Path);
            if (FsService.IsInRecycle(rel)) throw new FsException("Restore the file before editing it.");
            if (!File.Exists(fs.Resolve(rel))) throw new FsException("The file no longer exists.");
            // form posts turn every newline into CRLF; keep the file's own style (LF unless it already used CRLF)
            string content = Req.Unvalidated.Form["content"] ?? "";
            string old = File.ReadAllText(fs.Resolve(rel));
            if (!old.Contains("\r\n")) content = content.Replace("\r\n", "\n");
            fs.WriteText(rel, content);
            ApiHelper.WriteSuccess("Saved");
        }

        static void MkDir()
        {
            string rel = Fs.CreateFolder(Path, Req.Form["name"] + "");
            ApiHelper.WriteSuccess("Folder created", new { Path = rel });
        }

        static void NewFile()
        {
            if (FsService.IsInRecycle(Path)) throw new FsException("You cannot create files in the recycle bin.");
            string rel = Fs.CreateFile(Path, Req.Form["name"] + "", "");
            ApiHelper.WriteSuccess("File created", new { Path = rel });
        }

        static void Rename()
        {
            string rel = Fs.Rename(Path, (Req.Form["name"] + "").Trim());
            ApiHelper.WriteSuccess("Renamed", new { Path = rel });
        }

        static void MoveOrCopy(bool copy)
        {
            FsService fs = Fs;
            string dest = FsService.NormalizeRel(Req.Form["dest"]);
            if (FsService.IsInRecycle(dest)) throw new FsException("Use Delete to move items to the recycle bin.");
            if (!Directory.Exists(fs.Resolve(dest))) throw new FsException("Destination folder not found.");
            int n = 0;
            foreach (string p in Paths())
            {
                if (copy) fs.CopyInto(p, dest); else fs.MoveInto(p, dest);
                n++;
            }
            ApiHelper.WriteSuccess($"{(copy ? "Copied" : "Moved")} {n} item(s)");
        }

        static void Delete()
        {
            FsService fs = Fs;
            int n = 0;
            bool permanent = false;
            foreach (string p in Paths())
            {
                permanent |= FsService.IsInRecycle(p);
                fs.Delete(p, "user");
                n++;
            }
            ApiHelper.WriteSuccess(permanent ? $"Permanently deleted {n} item(s)" : $"Moved {n} item(s) to the recycle bin");
        }

        static void Restore()
        {
            FsService fs = Fs;
            var restored = new List<string>();
            foreach (string p in Paths()) restored.Add(fs.Restore(p));
            ApiHelper.WriteSuccess($"Restored {restored.Count} item(s)", restored);
        }

        static void EmptyRecycle()
        {
            int n = Fs.EmptyRecycleBin();
            ApiHelper.WriteSuccess($"Recycle bin emptied ({n} item(s) removed)");
        }

        static void Zip()
        {
            List<string> paths = Paths();
            if (paths.Count == 0) throw new FsException("Nothing selected.");
            string name = (Req.Form["name"] + "").Trim();
            if (name == "") name = (paths.Count == 1 ? FsService.NameOf(paths[0]) : "archive") + ".zip";
            if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) name += ".zip";
            string rel = Fs.Zip(paths, FsService.Combine(FsService.ParentOf(paths[0]), name));
            ApiHelper.WriteSuccess("Created " + FsService.NameOf(rel), new { Path = rel });
        }

        static void Unzip()
        {
            string rel = FsService.NormalizeRel(Path);
            string dest = Req.Form["dest"];
            if (dest == null) dest = FsService.ParentOf(rel);
            int n = Fs.Unzip(rel, dest);
            ApiHelper.WriteSuccess($"Extracted {n} file(s)", new { Path = FsService.NormalizeRel(dest) });
        }

        static void UploadChunk()
        {
            FsService fs = Fs;
            obConfig cfg = AppConfig.Get();
            HttpPostedFile file = Req.Files["chunk"];
            if (file == null) throw new FsException("No data received.");
            int index = int.Parse(Req.Form["index"] ?? "0");
            int total = int.Parse(Req.Form["total"] ?? "1");
            string uploadId = Req.Form["uploadId"];
            string dest = FsService.NormalizeRel(Req.Form["dest"]);
            if (dest == "") throw new FsException("Missing file name.");

            fs.AppendChunk(uploadId, index, file.InputStream, (long)cfg.MaxUploadMb * 1024 * 1024);
            if (index < total - 1) { ApiHelper.WriteSuccess("Chunk received"); return; }

            string rel = fs.FinishUpload(uploadId, dest);
            ApiHelper.WriteSuccess("Uploaded", new { Path = rel });
        }

        // ------------------------------------------------------------------ streaming

        static void Download()
        {
            FsService fs = Fs;
            List<string> paths = Paths();
            HttpResponse res = HttpContext.Current.Response;
            res.BufferOutput = false;

            if (paths.Count == 1 && File.Exists(fs.Resolve(paths[0])))
            {
                string full = fs.Resolve(paths[0]);
                res.ContentType = "application/octet-stream";
                res.AddHeader("Content-Disposition", ContentDisposition("attachment", FsService.NameOf(paths[0])));
                res.AddHeader("Content-Length", new FileInfo(full).Length.ToString());
                res.TransmitFile(full);
            }
            else
            {
                string name = (paths.Count == 1 ? FsService.NameOf(paths[0]) : "download") + ".zip";
                if (name == ".zip") name = "website.zip";
                res.ContentType = "application/zip";
                res.AddHeader("Content-Disposition", ContentDisposition("attachment", name));
                // ZipArchive needs a seekable stream; build in a temp file then transmit
                string tmp = System.IO.Path.Combine(fs.TempPath, "dl-" + Guid.NewGuid().ToString("N") + ".zip");
                using (var f = File.Create(tmp)) fs.ZipToStream(paths, f);
                res.AddHeader("Content-Length", new FileInfo(tmp).Length.ToString());
                res.WriteFile(tmp);
                res.Flush();
                try { File.Delete(tmp); } catch { }
            }
            ApiHelper.EndResponse();
        }

        /// <summary>Inline preview (images, video, pdf) with browser caching via ETag.</summary>
        static void Raw()
        {
            FsService fs = Fs;
            string rel = FsService.NormalizeRel(Path);
            string full = fs.Resolve(rel);
            HttpResponse res = HttpContext.Current.Response;
            if (!File.Exists(full)) { res.StatusCode = 404; ApiHelper.EndResponse(); return; }

            var fi = new FileInfo(full);
            string etag = "\"" + fi.LastWriteTimeUtc.Ticks.ToString("x") + "-" + fi.Length.ToString("x") + "\"";
            res.Cache.SetCacheability(HttpCacheability.Private);
            res.Cache.SetMaxAge(TimeSpan.FromDays(7));
            res.AddHeader("ETag", etag);
            if (Req.Headers["If-None-Match"] == etag) { res.StatusCode = 304; ApiHelper.EndResponse(); return; }

            string mime = FsService.MimeOf(rel);
            res.ContentType = mime;
            // never let uploaded SVG/HTML run script on this origin
            res.AddHeader("Content-Security-Policy", "default-src 'none'; img-src 'self' data:; style-src 'unsafe-inline'; sandbox");
            res.AddHeader("Content-Disposition", ContentDisposition("inline", fi.Name));
            res.TransmitFile(full);
            ApiHelper.EndResponse();
        }

        static string ContentDisposition(string kind, string name)
        {
            string ascii = System.Text.RegularExpressions.Regex.Replace(name, "[^\\x20-\\x7E]|\"", "_");
            return $"{kind}; filename=\"{ascii}\"; filename*=UTF-8''{Uri.EscapeDataString(name)}";
        }
    }
}
