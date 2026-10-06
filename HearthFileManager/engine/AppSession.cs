using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Web;
using Newtonsoft.Json;

namespace HearthFileManager.engine
{
    /// <summary>
    /// Custom session: "hfm" cookie token → RAM dictionary (fast path) → App_Data/sessions.json
    /// (SHA-256 of token) so logins survive app-pool recycles. Never uses HttpContext.Session.
    /// </summary>
    public static class AppSession
    {
        const string CookieName = "hfm";
        static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);

        class StoredSession
        {
            public string TokenHash { get; set; }
            public string Username { get; set; }
            public DateTime ExpiresUtc { get; set; }
        }

        static readonly ConcurrentDictionary<string, StoredSession> Ram = new ConcurrentDictionary<string, StoredSession>();
        static readonly object FileLock = new object();
        static string SessionFile => Path.Combine(AppConfig.AppDataPath, "sessions.json");

        const string ItemKey = "hfm.user";

        public static bool IsLoggedIn => LoginUser != null;

        public static obUser LoginUser => HttpContext.Current.Items[ItemKey] as obUser;

        /// <summary>Runs at BeginRequest: resolves the cookie into HttpContext.Items.</summary>
        public static void TryRestoreFromCookie()
        {
            HttpContext ctx = HttpContext.Current;
            HttpCookie c = ctx.Request.Cookies[CookieName];
            string token = c?.Value;

            if (!string.IsNullOrEmpty(token))
            {
                string hash = AppConfig.Sha256Hex(token);
                StoredSession s;
                if (!Ram.TryGetValue(hash, out s))
                {
                    s = LoadAll().Find(x => x.TokenHash == hash);
                    if (s != null) Ram[hash] = s;
                }

                if (s != null && s.ExpiresUtc > DateTime.UtcNow)
                {
                    obUser u = AppConfig.FindUser(s.Username);
                    if (u != null)
                    {
                        ctx.Items[ItemKey] = u;
                        // roll expiry forward only when < 1/12 of the window remains
                        if (s.ExpiresUtc - DateTime.UtcNow < TimeSpan.FromTicks(Lifetime.Ticks / 12))
                        {
                            s.ExpiresUtc = DateTime.UtcNow.Add(Lifetime);
                            Persist(s, false);
                            SetCookie(token, s.ExpiresUtc);
                        }
                        return;
                    }
                }
            }

            // Developer convenience: auto-login for localhost requests when enabled in config.json
            obConfig cfg = AppConfig.Get();
            // Host must also be localhost: behind a local reverse proxy / Cloudflare Tunnel, internet traffic is "IsLocal" too.
            string host = ctx.Request.Url.Host;
            bool localHost = host == "localhost" || host == "127.0.0.1" || host == "[::1]";
            if (cfg.DevAutoLogin && ctx.Request.IsLocal && localHost && cfg.Users.Count > 0)
                ctx.Items[ItemKey] = cfg.Users[0];
        }

        public static void Login(obUser user)
        {
            byte[] raw = new byte[32];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(raw);
            string token = BitConverter.ToString(raw).Replace("-", "").ToLowerInvariant();

            var s = new StoredSession { TokenHash = AppConfig.Sha256Hex(token), Username = user.Username, ExpiresUtc = DateTime.UtcNow.Add(Lifetime) };
            Ram[s.TokenHash] = s;
            Persist(s, false);
            SetCookie(token, s.ExpiresUtc);
            HttpContext.Current.Items[ItemKey] = user;
        }

        public static void Logout()
        {
            HttpContext ctx = HttpContext.Current;
            HttpCookie c = ctx.Request.Cookies[CookieName];
            if (c != null && !string.IsNullOrEmpty(c.Value))
            {
                string hash = AppConfig.Sha256Hex(c.Value);
                StoredSession s;
                Ram.TryRemove(hash, out s);
                Persist(new StoredSession { TokenHash = hash }, true);
            }
            SetCookie("", DateTime.UtcNow.AddDays(-1));
            ctx.Items.Remove(ItemKey);
        }

        /// <summary>Removes every session of a user (used when the account is deleted or its password changes).</summary>
        public static void RevokeUser(string username)
        {
            foreach (var kv in Ram)
                if (string.Equals(kv.Value.Username, username, StringComparison.OrdinalIgnoreCase))
                {
                    StoredSession s;
                    Ram.TryRemove(kv.Key, out s);
                }
            lock (FileLock)
            {
                List<StoredSession> all = LoadAll();
                all.RemoveAll(x => string.Equals(x.Username, username, StringComparison.OrdinalIgnoreCase));
                SaveAll(all);
            }
        }

        static void SetCookie(string value, DateTime expires)
        {
            HttpContext ctx = HttpContext.Current;
            ctx.Response.Cookies.Add(new HttpCookie(CookieName, value)
            {
                HttpOnly = true,
                Path = "/",
                Secure = ctx.Request.IsSecureConnection,
                SameSite = SameSiteMode.Lax,
                Expires = expires
            });
        }

        static void Persist(StoredSession s, bool remove)
        {
            lock (FileLock)
            {
                List<StoredSession> all = LoadAll();
                all.RemoveAll(x => x.TokenHash == s.TokenHash || x.ExpiresUtc < DateTime.UtcNow);
                if (!remove) all.Add(s);
                SaveAll(all);
            }
        }

        static List<StoredSession> LoadAll()
        {
            lock (FileLock)
            {
                if (!File.Exists(SessionFile)) return new List<StoredSession>();
                try { return JsonConvert.DeserializeObject<List<StoredSession>>(File.ReadAllText(SessionFile, Encoding.UTF8)) ?? new List<StoredSession>(); }
                catch { return new List<StoredSession>(); }
            }
        }

        static void SaveAll(List<StoredSession> all)
        {
            File.WriteAllText(SessionFile, JsonConvert.SerializeObject(all, Formatting.Indented), new UTF8Encoding(false));
        }
    }
}
