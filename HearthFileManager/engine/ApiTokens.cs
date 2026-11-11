using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace HearthFileManager.engine
{
    public class obApiToken
    {
        /// <summary>Short public id used to revoke the token and in the audit log.</summary>
        public string Id { get; set; }
        public string Name { get; set; }
        public string Username { get; set; }
        /// <summary>SHA-256 of the token. The token itself is shown once and never stored.</summary>
        public string TokenHash { get; set; }
        /// <summary>First characters of the token, so the user can tell tokens apart.</summary>
        public string Prefix { get; set; }
        /// <summary>false = list only; true = may also upload and delete (the user still needs "Manage files").</summary>
        public bool CanWrite { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime? ExpiresUtc { get; set; }
        public DateTime? LastUsedUtc { get; set; }

        [JsonIgnore]
        public bool IsExpired => ExpiresUtc.HasValue && ExpiresUtc.Value <= DateTime.UtcNow;
    }

    /// <summary>
    /// Personal access tokens for the deploy API (/api/v1). Stored in App_Data/api-tokens.json as
    /// SHA-256 hashes, like sessions.json. A token acts as its user: same root folder, same permissions.
    /// </summary>
    public static class ApiTokens
    {
        public const string TokenPrefix = "hfm_";
        public const int MaxPerUser = 20;

        static readonly object Lock = new object();
        static List<obApiToken> _cache;
        static DateTime _cacheStamp;
        static DateTime _lastUsedSaved = DateTime.MinValue;

        static string TokenFile => Path.Combine(AppConfig.AppDataPath, "api-tokens.json");

        static List<obApiToken> Load()
        {
            lock (Lock)
            {
                DateTime stamp = File.Exists(TokenFile) ? File.GetLastWriteTimeUtc(TokenFile) : DateTime.MinValue;
                if (_cache != null && stamp == _cacheStamp) return _cache;
                var list = new List<obApiToken>();
                if (File.Exists(TokenFile))
                {
                    try { list = JsonConvert.DeserializeObject<List<obApiToken>>(File.ReadAllText(TokenFile, Encoding.UTF8)) ?? new List<obApiToken>(); }
                    catch { list = new List<obApiToken>(); }
                }
                _cache = list;
                _cacheStamp = stamp;
                return list;
            }
        }

        static void Save(List<obApiToken> list)
        {
            string tmp = TokenFile + ".tmp";
            File.WriteAllText(tmp, JsonConvert.SerializeObject(list, Formatting.Indented), new UTF8Encoding(false));
            if (File.Exists(TokenFile)) File.Replace(tmp, TokenFile, null);
            else File.Move(tmp, TokenFile);
            _cache = list;
            _cacheStamp = File.GetLastWriteTimeUtc(TokenFile);
        }

        static string RandomHex(int bytes)
        {
            byte[] raw = new byte[bytes];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(raw);
            return BitConverter.ToString(raw).Replace("-", "").ToLowerInvariant();
        }

        /// <summary>Creates a token. The returned plain token is the only time it is ever available.</summary>
        public static obApiToken Create(string username, string name, bool canWrite, int expiresDays, out string plainToken)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0) throw new GuardException("Give the token a name, e.g. \"Claude on my laptop\".", 400);
            if (name.Length > 60) name = name.Substring(0, 60);

            plainToken = TokenPrefix + RandomHex(32);
            var t = new obApiToken
            {
                Id = RandomHex(6),
                Name = name,
                Username = username,
                TokenHash = AppConfig.Sha256Hex(plainToken),
                Prefix = plainToken.Substring(0, TokenPrefix.Length + 6),
                CanWrite = canWrite,
                CreatedUtc = DateTime.UtcNow,
                ExpiresUtc = expiresDays > 0 ? DateTime.UtcNow.AddDays(expiresDays) : (DateTime?)null
            };
            lock (Lock)
            {
                var list = new List<obApiToken>(Load());
                if (list.FindAll(x => SameUser(x, username)).Count >= MaxPerUser)
                    throw new GuardException($"You already have {MaxPerUser} tokens. Revoke one you no longer use first.", 400);
                list.Add(t);
                Save(list);
            }
            return t;
        }

        public static List<obApiToken> ListFor(string username)
        {
            lock (Lock) return Load().FindAll(x => SameUser(x, username));
        }

        public static bool Revoke(string username, string id)
        {
            lock (Lock)
            {
                var list = new List<obApiToken>(Load());
                int n = list.RemoveAll(x => SameUser(x, username) && x.Id == id);
                if (n > 0) Save(list);
                return n > 0;
            }
        }

        /// <summary>Removes every token of a user (used when the account is deleted).</summary>
        public static void RevokeUser(string username)
        {
            lock (Lock)
            {
                var list = new List<obApiToken>(Load());
                if (list.RemoveAll(x => SameUser(x, username)) > 0) Save(list);
            }
        }

        /// <summary>Looks up a presented token. Returns null when unknown or expired.</summary>
        public static obApiToken Find(string plainToken)
        {
            if (string.IsNullOrEmpty(plainToken) || !plainToken.StartsWith(TokenPrefix) || plainToken.Length > 200) return null;
            string hash = AppConfig.Sha256Hex(plainToken);
            lock (Lock)
            {
                List<obApiToken> list = Load();
                obApiToken t = list.Find(x => x.TokenHash == hash);
                if (t == null || t.IsExpired) return null;

                // "last used" is informational: write it to disk at most once a minute
                t.LastUsedUtc = DateTime.UtcNow;
                if (DateTime.UtcNow - _lastUsedSaved > TimeSpan.FromMinutes(1))
                {
                    _lastUsedSaved = DateTime.UtcNow;
                    try { Save(list); } catch { }
                }
                return t;
            }
        }

        static bool SameUser(obApiToken t, string username) => string.Equals(t.Username, username, StringComparison.OrdinalIgnoreCase);

        // ------------------------------------------------------------------ audit log

        static readonly object LogLock = new object();
        static string LogFile => Path.Combine(AppConfig.AppDataPath, "api-log.jsonl");
        const long LogMaxBytes = 5 * 1024 * 1024;

        /// <summary>Appends one line per API call to App_Data/api-log.jsonl (rotated to api-log.1.jsonl at 5 MB).</summary>
        public static void Log(string username, obApiToken token, string ip, string action, string path, int status, string detail)
        {
            try
            {
                string line = JsonConvert.SerializeObject(new
                {
                    utc = DateTime.UtcNow,
                    user = username,
                    tokenId = token?.Id,
                    tokenName = token?.Name,
                    ip,
                    action,
                    path,
                    status,
                    detail
                });
                lock (LogLock)
                {
                    if (File.Exists(LogFile) && new FileInfo(LogFile).Length > LogMaxBytes)
                    {
                        string old = Path.Combine(AppConfig.AppDataPath, "api-log.1.jsonl");
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(LogFile, old);
                    }
                    File.AppendAllText(LogFile, line + "\n", new UTF8Encoding(false));
                }
            }
            catch { /* logging must never break a request */ }
        }
    }
}
