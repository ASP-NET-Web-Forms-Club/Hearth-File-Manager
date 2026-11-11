using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web;
using System.Web.Configuration;
using HearthFileManager.engine;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace HearthFileManager.RH
{
    /// <summary>
    /// Deploy API for scripts and AI agents: /api/v1/{whoami|list|upload|delete|help}.
    /// Authenticated ONLY by a personal access token (Authorization: Bearer hfm_...). The login cookie and
    /// DevAutoLogin are never consulted, so browsers cannot be tricked into calling it (no CSRF surface).
    /// A token acts as its user: same root folder (FsService.ForUser) and same permissions.
    /// </summary>
    public class ApiV1
    {
        const int MaxListItems = 50000;
        const int MaxDeletePaths = 5000;
        const int MaxWrittenReported = 1000;

        static HttpRequest Req => HttpContext.Current.Request;

        static string Query(string key) => Req.Unvalidated.QueryString[key];

        static bool Flag(string key)
        {
            string v = (Query(key) ?? "").Trim().ToLowerInvariant();
            return v == "1" || v == "true" || v == "yes";
        }

        public static void HandleRequest(string action)
        {
            HttpResponse res = HttpContext.Current.Response;
            res.Cache.SetCacheability(HttpCacheability.NoCache);
            res.Cache.SetNoStore();

            obApiToken tok = null;
            obUser user = null;
            int status = 200;
            string detail = null;
            try
            {
                tok = ApiTokens.Find(ReadToken());
                if (tok == null) throw new GuardException("Missing, unknown or expired access token. Send it in the request header: Authorization: Bearer <token>", 401);
                user = AppConfig.FindUser(tok.Username);
                if (user == null) throw new GuardException("The account of this token no longer exists.", 401);
                if (!user.Has(Perm.Api)) throw new GuardException("This account is not allowed to use the API.", 403);

                switch (action)
                {
                    case "whoami": Method("GET"); WhoAmI(tok, user); break;
                    case "help": Method("GET"); Help(); break;
                    case "list": Method("GET"); detail = List(user); break;
                    case "upload": Method("POST"); Write(tok, user); detail = Upload(tok, user); break;
                    case "delete": Method("POST"); Write(tok, user); detail = Delete(user); break;
                    default: throw new GuardException("Unknown API endpoint: " + action + ". See /api/v1/help", 404);
                }
            }
            catch (GuardException gx) { status = gx.StatusCode; detail = gx.Message; ApiHelper.WriteError(gx.Message, gx.StatusCode); }
            catch (FsException fx) { status = 400; detail = fx.Message; ApiHelper.WriteError(fx.Message, 400); }
            catch (IOException ix) { status = 400; detail = ix.Message; ApiHelper.WriteError("File error: " + ix.Message, 400); }
            catch (UnauthorizedAccessException) { status = 400; detail = "access denied"; ApiHelper.WriteError("Access denied by the server (file may be in use or read-only).", 400); }
            catch (Exception ex) { status = 500; detail = ex.Message; ApiHelper.WriteError("An error occurred: " + ex.Message, 500); }

            ApiTokens.Log(user?.Username ?? tok?.Username, tok, Req.UserHostAddress, action, Query("path"), status, detail);
            ApiHelper.EndResponse();
        }

        static string ReadToken()
        {
            string h = Req.Headers["Authorization"] ?? "";
            if (h.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return h.Substring(7).Trim();
            // some proxies strip the Authorization header
            return (Req.Headers["X-Hearth-Token"] ?? "").Trim();
        }

        static void Method(string expected)
        {
            if (!string.Equals(Req.HttpMethod, expected, StringComparison.OrdinalIgnoreCase))
                throw new GuardException("This endpoint needs a " + expected + " request.", 405);
        }

        /// <summary>Changing files needs a read-write token AND the user's "Manage files" permission.</summary>
        static void Write(obApiToken tok, obUser user)
        {
            if (!tok.CanWrite) throw new GuardException("This token is read-only. Create a read & write token to upload or delete.", 403);
            if (!user.Has(Perm.Files)) throw new GuardException("This account does not have the \"Manage files\" permission.", 403);
        }

        static long MaxRequestBytes()
        {
            try { return ((HttpRuntimeSection)ConfigurationManager.GetSection("system.web/httpRuntime")).MaxRequestLength * 1024L; }
            catch { return 4096 * 1024L; }
        }

        // ------------------------------------------------------------------ endpoints

        static void WhoAmI(obApiToken tok, obUser user)
        {
            ApiHelper.WriteSuccess("Success", new
            {
                User = user.Username,
                TokenName = tok.Name,
                CanWrite = tok.CanWrite && user.Has(Perm.Files),
                tok.ExpiresUtc,
                MaxUploadMb = AppConfig.Get().MaxUploadMb,
                MaxRequestBytes = MaxRequestBytes(),
                ServerTimeUtc = DateTime.UtcNow
            });
        }

        static void Help()
        {
            HttpResponse res = HttpContext.Current.Response;
            res.ContentType = "text/plain; charset=utf-8";
            res.Write(HelpText(ApiHelper.GetBaseUrl()));
        }

        static string List(obUser user)
        {
            FsService fs = FsService.ForUser(user);
            string rel = FsService.NormalizeRel(Query("path"));
            bool recursive = Flag("recursive"), hash = Flag("hash");
            // exclude=uploads,storage/receipts  (comma-separated, and/or the parameter repeated)
            var exclude = new List<string>();
            foreach (string v in Req.Unvalidated.QueryString.GetValues("exclude") ?? new string[0])
                foreach (string p in v.Split(','))
                    if (FsService.NormalizeRel(p).Length > 0) exclude.Add(FsService.NormalizeRel(p));

            List<FsService.obTreeEntry> items = fs.ListTree(rel, recursive, hash, MaxListItems, exclude);
            ApiHelper.WriteJson(new { success = true, message = "Success", data = new { Path = rel, Count = items.Count, Recursive = recursive, Exclude = exclude }, items });
            return items.Count + " item(s)";
        }

        static readonly Regex UploadIdRx = new Regex("^[a-zA-Z0-9]{8,40}$");

        /// <summary>
        /// Body = the zip file (raw bytes, or one multipart file). Small zips: one request.
        /// Large zips: several requests with ?uploadId=&amp;index=&amp;total= ; the last one extracts.
        /// </summary>
        static string Upload(obApiToken tok, obUser user)
        {
            FsService fs = FsService.ForUser(user);
            obConfig cfg = AppConfig.Get();
            string dest = FsService.NormalizeRel(Query("path"));
            if (FsService.IsInRecycle(dest)) throw new FsException("You cannot upload into the recycle bin.");

            int index, total;
            if (!int.TryParse(Query("index") ?? "0", out index) || !int.TryParse(Query("total") ?? "1", out total) || total < 1 || index < 0 || index >= total)
                throw new FsException("Invalid index/total.");
            string uploadId = Query("uploadId");
            if (string.IsNullOrEmpty(uploadId))
            {
                if (total != 1) throw new FsException("uploadId is required when the zip is sent in several parts.");
                uploadId = Guid.NewGuid().ToString("N");
            }
            if (!UploadIdRx.IsMatch(uploadId)) throw new FsException("uploadId must be 8-40 letters or digits.");
            string id = "api" + tok.Id + uploadId; // namespaced per token: tokens cannot touch each other's uploads

            Stream body = Req.Files.Count > 0 ? Req.Files[0].InputStream : Req.InputStream;
            if (body.Length == 0) throw new FsException("No data received. Send the zip file as the request body.");
            fs.AppendChunk(id, index, body, (long)cfg.MaxUploadMb * 1024 * 1024);
            if (index < total - 1)
            {
                ApiHelper.WriteSuccess("Part received", new { Index = index, Total = total });
                return $"part {index + 1}/{total}";
            }

            string part = Path.Combine(fs.TempPath, id + ".part");
            var written = new List<string>();
            int n;
            try
            {
                n = fs.UnzipFile(part, dest, null, default(CancellationToken), (long)cfg.ApiMaxExtractMb * 1024 * 1024, "api", written);
            }
            catch (InvalidDataException) { throw new FsException("The upload is not a valid zip file."); }
            catch (FsException fx)
            {
                if (written.Count == 0) throw;
                throw new FsException(fx.Message + $" ({written.Count} file(s) were already extracted before the error; replaced files are in the recycle bin.)");
            }
            finally { try { File.Delete(part); } catch { } }

            bool truncated = written.Count > MaxWrittenReported;
            if (truncated) written = written.GetRange(0, MaxWrittenReported);
            ApiHelper.WriteSuccess($"Extracted {n} file(s)", new { Path = dest, Files = n, Written = written, WrittenTruncated = truncated });
            return $"extracted {n} file(s)";
        }

        /// <summary>Body = {"paths":["old.php","unused-folder"]}. Items go to the recycle bin, never deleted for good.</summary>
        static string Delete(obUser user)
        {
            FsService fs = FsService.ForUser(user);
            List<string> paths = null;
            string form = Req.Unvalidated.Form["paths"];
            try
            {
                if (!string.IsNullOrEmpty(form)) paths = JsonConvert.DeserializeObject<List<string>>(form);
                else
                {
                    string raw;
                    Req.InputStream.Position = 0;
                    using (var sr = new StreamReader(Req.InputStream, Encoding.UTF8)) raw = sr.ReadToEnd();
                    JToken jt = JToken.Parse(raw);
                    paths = (jt is JArray ? jt : jt["paths"])?.ToObject<List<string>>();
                }
            }
            catch (JsonException) { paths = null; }
            if (paths == null || paths.Count == 0) throw new FsException("Send a JSON body like {\"paths\":[\"old.php\",\"unused-folder\"]}.");
            if (paths.Count > MaxDeletePaths) throw new FsException($"At most {MaxDeletePaths} paths per request.");

            // validate everything first, so a bad path does not leave the job half done
            var rels = new List<string>();
            foreach (string p in paths)
            {
                string rel = FsService.NormalizeRel(p);
                if (rel.Length == 0) throw new FsException("The root folder cannot be deleted.");
                if (FsService.IsInRecycle(rel)) throw new FsException("The recycle bin is not available through the API.");
                fs.Resolve(rel); // throws when outside the root
                rels.Add(rel);
            }

            var deleted = new List<string>();
            var notFound = new List<string>();
            foreach (string rel in rels)
            {
                if (!fs.Exists(rel)) { notFound.Add(rel); continue; } // also covers a file inside a folder deleted earlier in this request
                fs.Delete(rel, "api");
                deleted.Add(rel);
            }
            ApiHelper.WriteSuccess($"Moved {deleted.Count} item(s) to the recycle bin", new { Deleted = deleted, NotFound = notFound });
            return $"deleted {deleted.Count}, not found {notFound.Count}";
        }

        // ------------------------------------------------------------------ instructions

        /// <summary>Plain-text guide for an AI agent or script author. Shown on My Account and at /api/v1/help.</summary>
        public static string HelpText(string baseUrl)
        {
            long maxReq = MaxRequestBytes();
            long chunk = Math.Max(1, maxReq / 2 / 1024 / 1024);
            return $@"HEARTH FILE MANAGER - DEPLOY API (v1)
Base URL: {baseUrl}/api/v1

Use this API to publish website files to the server. Typical job: list the server files,
compare with the local project, zip only the new/changed files, upload the zip.

AUTHENTICATION
  Every request needs the header:   Authorization: Bearer <token>
  The token belongs to one Hearth user and can only reach that user's root folder.
  Read the token from the environment variable HEARTH_TOKEN. Never write it into a script,
  a source file, a URL or a chat message.

PATHS
  All paths are relative to the user's root folder and use ""/"". Empty path = the root.
  Example: ""css/site.css"". Paths with "".."" that leave the root are refused.

RESPONSES
  JSON: {{ ""success"": true|false, ""message"": ""..."", ""data"": {{...}}, ""items"": [...] }}
  HTTP status: 200 ok, 400 bad request, 401 bad/expired token, 403 not allowed, 404, 405.

ENDPOINTS
  GET  /api/v1/whoami
       Checks the token. data: User, TokenName, CanWrite, ExpiresUtc, MaxUploadMb, MaxRequestBytes.

  GET  /api/v1/list?path=<folder>&recursive=1&hash=1&exclude=<folders>
       Lists files and folders. items: Path, IsDir, Size, ModifiedUtc (UTC, ISO 8601), Sha256.
       recursive=1 includes all sub-folders. hash=1 adds the SHA-256 (lower-case hex) of each file.
       Compare by Sha256, not by date: dates change when files are copied or zipped.
       exclude=<folders> skips folders that only matter on the live server (customer uploads, receipts,
       caches). Comma-separated, case-insensitive, ""*"" is a wildcard:
         exclude=uploads            any folder named ""uploads"", at any depth
         exclude=storage/receipts   exactly that folder (path from the root)
         exclude=uploads,cache-*,storage/receipts
       An excluded folder is still listed once, with ""Excluded"": true, but its contents are not.
       Never upload into or delete an excluded folder: you have not seen what is inside it.

  POST /api/v1/upload?path=<target folder>
       Body: a zip file (raw bytes, Content-Type: application/zip).
       The zip is extracted into the target folder, keeping the folder structure inside the zip.
       Missing folders are created. Existing files are replaced; the old copy goes to the recycle bin.
       data: Files (count), Written (paths).
       One request may be at most {maxReq} bytes. For a bigger zip, send it in parts of about {chunk} MB:
         POST /api/v1/upload?path=<folder>&uploadId=<8-40 letters/digits>&index=<0..n-1>&total=<n>
       Send the parts in order. The zip is extracted when the last part arrives.

  POST /api/v1/delete
       Body (JSON): {{ ""paths"": [""old-page.php"", ""unused-folder""] }}
       Moves the items to the recycle bin (the user can restore them in Hearth).
       data: Deleted, NotFound.

  GET  /api/v1/help
       This text.

POWERSHELL EXAMPLE (Windows PowerShell 5.1 or newer)
  $base = '{baseUrl}/api/v1'
  $h = @{{ Authorization = ""Bearer $env:HEARTH_TOKEN"" }}
  # 1. what is on the server?
  $remote = (Invoke-RestMethod ""$base/list?path=&recursive=1&hash=1"" -Headers $h).items
  # 2. zip the changed files into deploy.zip (keep their relative paths inside the zip), then:
  Invoke-RestMethod ""$base/upload?path="" -Method Post -Headers $h -InFile deploy.zip -ContentType 'application/zip'
  # 3. remove files that no longer exist locally
  $body = @{{ paths = @('old-page.php') }} | ConvertTo-Json
  Invoke-RestMethod ""$base/delete"" -Method Post -Headers $h -Body $body -ContentType 'application/json'

RULES FOR AI AGENTS
  - Call /whoami first. If CanWrite is false, you can only list.
  - Upload only what changed. Do not upload secrets, .git folders or local config files.
  - Tell the user which files you are about to upload or delete before doing it.
  - After uploading, list again and confirm the hashes match.
";
        }
    }
}
