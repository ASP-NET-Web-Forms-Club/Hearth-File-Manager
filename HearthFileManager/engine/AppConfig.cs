using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace HearthFileManager.engine
{
    /// <summary>Permission keys stored in obUser.Permissions. "My account" (own password) is always allowed.</summary>
    public static class Perm
    {
        public const string Files = "files";        // upload, edit, rename, move, delete, zip (without it: view & download only)
        public const string Ai = "ai";              // AI website builder (implies Files)
        public const string Settings = "settings";  // Gemini key/model, upload limit, site address
        public const string Users = "users";        // add/delete users, reset passwords, assign permissions

        public static readonly string[] All = { Files, Ai, Settings, Users };
        public static readonly string[] NewUserDefault = { Files, Ai };

        /// <summary>Removes unknown keys, de-duplicates, and adds Files when Ai is present.</summary>
        public static List<string> Normalize(IEnumerable<string> perms)
        {
            var list = new List<string>();
            foreach (string p in perms ?? new string[0])
                if (Array.IndexOf(All, p) >= 0 && !list.Contains(p)) list.Add(p);
            if (list.Contains(Ai) && !list.Contains(Files)) list.Add(Files);
            return list;
        }
    }

    public class obUser
    {
        public string Username { get; set; }
        public string PasswordHash { get; set; }
        public DateTime CreatedUtc { get; set; }
        /// <summary>
        /// The user's own root folder. Empty = the main root. A plain relative value ("alex") is inside the
        /// main root; "/App_Data/public/alex" is relative to the Hearth folder; "D:\sites\alex" is absolute.
        /// </summary>
        public string RootPath { get; set; } = "";
        /// <summary>Granted permission keys (see Perm). null = all permissions (accounts created before permissions existed).</summary>
        public List<string> Permissions { get; set; }

        public bool Has(string perm) => Permissions == null || Perm.Normalize(Permissions).Contains(perm);

        public List<string> EffectivePermissions() => Permissions == null ? new List<string>(Perm.All) : Perm.Normalize(Permissions);
    }

    public class obConfig
    {
        public List<obUser> Users { get; set; } = new List<obUser>();
        public string GeminiApiKey { get; set; } = "";
        // Free tier (AI Studio, Oct 2026): Flash-Lite = 15 RPM / 500 per day, Flash = 5 RPM / 20 per day, per model.
        public string GeminiModel { get; set; } = "gemini-3.5-flash-lite";
        /// <summary>Requests-per-minute cap. 0 = automatic (learned from Google's 429 responses per model).</summary>
        public int GeminiRpm { get; set; } = 0;
        /// <summary>Tried in order when the main model's free daily quota is used up (each model has its own quota).</summary>
        [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
        public List<string> GeminiFallbackModels { get; set; } = new List<string> { "gemini-3.1-flash-lite", "gemini-flash-latest", "gemini-3.7-flash", "gemini-3.6-flash", "gemini-3.5-flash" };
        public int MaxUploadMb { get; set; } = 500;
        /// <summary>
        /// Main root folder that Hearth manages (the public website). "/App_Data/public" is relative to the
        /// Hearth folder; absolute paths such as "D:\websites\" or "C:\inetpub\wwwroot" are allowed.
        /// </summary>
        public string SiteRoot { get; set; } = "/App_Data/public";
        public string SitePreviewUrl { get; set; } = "";
        /// <summary>Only honoured for requests from localhost. Logs in as the first user.</summary>
        public bool DevAutoLogin { get; set; } = false;
    }

    /// <summary>
    /// Loads and saves /App_Data/config.json. HttpContext-free so it can be used from PowerShell tests.
    /// </summary>
    public static class AppConfig
    {
        static readonly object _lock = new object();
        static obConfig _cache;
        static DateTime _cacheStamp;

        /// <summary>Physical path of App_Data. Set by Global.Application_Start (or a test harness).</summary>
        public static string AppDataPath { get; set; }

        public static string ConfigFile => Path.Combine(AppDataPath, "config.json");

        /// <summary>The Hearth application folder (parent of App_Data).</summary>
        public static string AppRoot => Path.GetDirectoryName(Path.GetFullPath(AppDataPath).TrimEnd('\\'));

        /// <summary>Hearth's private folders inside App_Data; a managed root may never point into these.</summary>
        public static readonly string[] PrivateFolders = { "recycle-bin", "ai-undo", "tmp" };

        /// <summary>
        /// Turns a configured path into an absolute folder path. Accepts "/" and "\".
        ///   "D:\sites\x", "\\server\share"  → absolute
        ///   "/App_Data/public", "~/x"        → relative to the Hearth folder
        ///   "alex", "sub/alex"              → relative to relativeBase
        ///   ""                              → relativeBase
        /// </summary>
        public static string ResolvePath(string value, string relativeBase)
        {
            string v = (value ?? "").Trim().Replace('/', '\\');
            if (v.StartsWith("~")) v = v.Substring(1);
            if (v.StartsWith("\\\\\\")) v = v.TrimStart('\\'); // "///x" typo → treat as app-relative
            string full;
            if (v.Length == 0) full = relativeBase;
            else if ((v.Length >= 2 && v[1] == ':') || v.StartsWith("\\\\")) full = v;
            else if (v.StartsWith("\\")) full = Path.Combine(AppRoot, v.TrimStart('\\'));
            else full = Path.Combine(relativeBase, v);
            full = Path.GetFullPath(full);
            return full.Length > 3 ? full.TrimEnd('\\') : full; // keep "C:\"
        }

        /// <summary>Tidies a path setting for storage: absolute → "D:\sites\x", relative → "/App_Data/public/x" or "alex/sub".</summary>
        public static string NormalizePathSetting(string value)
        {
            string v = (value ?? "").Trim();
            if (v.Length == 0) return "";
            bool absolute = (v.Length >= 2 && v[1] == ':') || v.StartsWith("\\\\") || v.StartsWith("//");
            if (absolute)
            {
                v = v.Replace('/', '\\');
                bool unc = v.StartsWith("\\\\");
                string rest = unc ? v.Substring(2) : v;
                while (rest.Contains("\\\\")) rest = rest.Replace("\\\\", "\\");
                v = (unc ? "\\\\" : "") + rest;
                return v.Length > 3 ? v.TrimEnd('\\') : v;
            }
            v = v.Replace('\\', '/');
            while (v.Contains("//")) v = v.Replace("//", "/");
            return v.Length > 1 ? v.TrimEnd('/') : v;
        }

        public const string DefaultSiteRoot = "/App_Data/public";

        public static string MainRoot()
        {
            string v = Get().SiteRoot;
            return ResolvePath(string.IsNullOrWhiteSpace(v) ? DefaultSiteRoot : v, AppRoot);
        }

        public static string UserRoot(obUser u) => ResolvePath(u?.RootPath, MainRoot());

        static bool IsUnder(string path, string folder) =>
            string.Equals(path, folder, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(folder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

        /// <summary>Returns an error message if the folder must not be used as a managed root, else null.</summary>
        public static string RootProblem(string full)
        {
            string appData = Path.GetFullPath(AppDataPath).TrimEnd('\\');
            // the root must not contain Hearth itself (config.json, bin, web.config would become editable)
            if (IsUnder(AppRoot, full)) return "This folder contains the Hearth application itself. Choose a folder such as \"/App_Data/public\" or another folder outside Hearth.";
            foreach (string p in PrivateFolders)
                if (IsUnder(full, Path.Combine(appData, p))) return $"\"App_Data\\{p}\" is used by Hearth itself.";
            if (string.Equals(full, appData, StringComparison.OrdinalIgnoreCase)) return "App_Data itself cannot be the root.";
            return null;
        }

        /// <summary>Paths for display: shown relative to the Hearth folder when inside it ("/App_Data/public/alex").</summary>
        public static string DisplayPath(string full)
        {
            string app = AppRoot.TrimEnd('\\');
            if (full.StartsWith(app + "\\", StringComparison.OrdinalIgnoreCase)) return "/" + full.Substring(app.Length + 1).Replace('\\', '/');
            return full;
        }

        public static obConfig Get()
        {
            lock (_lock)
            {
                DateTime stamp = File.Exists(ConfigFile) ? File.GetLastWriteTimeUtc(ConfigFile) : DateTime.MinValue;
                if (_cache != null && stamp == _cacheStamp) return _cache;

                obConfig cfg = new obConfig();
                if (File.Exists(ConfigFile))
                {
                    JObject jo = JObject.Parse(File.ReadAllText(ConfigFile, Encoding.UTF8));
                    cfg = jo.ToObject<obConfig>() ?? new obConfig();
                    if (cfg.Users == null) cfg.Users = new List<obUser>();

                    // Migrate legacy single-admin format: { AdminUsername, AdminPasswordHash (sha256 hex) }
                    string legacyUser = (string)jo["AdminUsername"];
                    string legacyHash = (string)jo["AdminPasswordHash"];
                    if (cfg.Users.Count == 0 && !string.IsNullOrEmpty(legacyUser))
                    {
                        cfg.Users.Add(new obUser { Username = legacyUser, PasswordHash = "sha256:" + legacyHash, CreatedUtc = DateTime.UtcNow });
                        SaveInternal(cfg);
                        stamp = File.GetLastWriteTimeUtc(ConfigFile);
                    }
                }

                if (cfg.Users.Count == 0)
                {
                    // First run: admin / admin. User is told to change it.
                    cfg.Users.Add(new obUser { Username = "admin", PasswordHash = HashPassword("admin"), CreatedUtc = DateTime.UtcNow });
                    SaveInternal(cfg);
                    stamp = File.GetLastWriteTimeUtc(ConfigFile);
                }

                _cache = cfg;
                _cacheStamp = stamp;
                return cfg;
            }
        }

        /// <summary>Apply a change to the config and persist it atomically.</summary>
        public static void Update(Action<obConfig> change)
        {
            lock (_lock)
            {
                obConfig cfg = Get();
                change(cfg);
                SaveInternal(cfg);
                _cache = cfg;
                _cacheStamp = File.GetLastWriteTimeUtc(ConfigFile);
            }
        }

        static void SaveInternal(obConfig cfg)
        {
            Directory.CreateDirectory(AppDataPath);
            string tmp = ConfigFile + ".tmp";
            File.WriteAllText(tmp, JsonConvert.SerializeObject(cfg, Formatting.Indented), new UTF8Encoding(false));
            if (File.Exists(ConfigFile)) File.Replace(tmp, ConfigFile, null);
            else File.Move(tmp, ConfigFile);
        }

        public static obUser FindUser(string username)
        {
            if (string.IsNullOrEmpty(username)) return null;
            foreach (obUser u in Get().Users)
                if (string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase)) return u;
            return null;
        }

        /// <summary>Checks a password; transparently upgrades legacy sha256 hashes to PBKDF2.</summary>
        public static bool VerifyUser(string username, string password)
        {
            obUser u = FindUser(username);
            if (u == null || password == null) return false;

            string h = u.PasswordHash ?? "";
            if (h.StartsWith("sha256:"))
            {
                bool ok = FixedEquals(Sha256Hex(password), h.Substring(7).ToLowerInvariant());
                if (ok)
                {
                    string name = u.Username;
                    Update(c => { foreach (obUser x in c.Users) if (x.Username == name) x.PasswordHash = HashPassword(password); });
                }
                return ok;
            }
            return VerifyPassword(password, h);
        }

        // ---- password hashing: pbkdf2$<iterations>$<salt b64>$<hash b64> ----

        const int Iterations = 120000;

        public static string HashPassword(string password)
        {
            byte[] salt = new byte[16];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(salt);
            using (var kdf = new Rfc2898DeriveBytes(password, salt, Iterations, HashAlgorithmName.SHA256))
            {
                byte[] hash = kdf.GetBytes(32);
                return $"pbkdf2${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
            }
        }

        public static bool VerifyPassword(string password, string stored)
        {
            string[] p = (stored ?? "").Split('$');
            if (p.Length != 4 || p[0] != "pbkdf2") return false;
            int iter;
            if (!int.TryParse(p[1], out iter)) return false;
            byte[] salt = Convert.FromBase64String(p[2]);
            byte[] expected = Convert.FromBase64String(p[3]);
            using (var kdf = new Rfc2898DeriveBytes(password, salt, iter, HashAlgorithmName.SHA256))
            {
                byte[] actual = kdf.GetBytes(expected.Length);
                int diff = 0;
                for (int i = 0; i < expected.Length; i++) diff |= expected[i] ^ actual[i];
                return diff == 0;
            }
        }

        public static string Sha256Hex(string raw)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(raw));
                StringBuilder sb = new StringBuilder();
                foreach (byte b in bytes) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        static bool FixedEquals(string a, string b)
        {
            if (a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }
    }
}
