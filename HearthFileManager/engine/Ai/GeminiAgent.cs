using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace HearthFileManager.engine.Ai
{
    public class obAiTurnResult
    {
        public JArray Contents { get; set; }
        public string FinalText { get; set; }
        public List<string> Changed { get; set; } = new List<string>();
        public string UndoId { get; set; }
        public string Error { get; set; }
        public int Requests { get; set; }
        public string Model { get; set; }
    }

    public class GeminiException : Exception
    {
        public GeminiException(string message) : base(message) { }
    }

    /// <summary>
    /// Runs one user turn against the Gemini REST API with function calling:
    /// send → model asks for tools → run tools on wwwroot → send results → … → final text.
    /// HttpContext-free (testable from PowerShell). Progress is reported through a callback.
    /// </summary>
    public class GeminiAgent
    {
        public const string ApiBase = "https://generativelanguage.googleapis.com/v1beta/";
        const int MaxSteps = 40;
        const int OldToolOutputLimit = 1500;

        static readonly HttpClient Http = new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(150);

        static GeminiAgent()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        }

        readonly string _apiKey, _model, _siteUrl;
        readonly FsService _fs;
/// <summary>
        /// Manual requests-per-minute cap from Settings; 0 = automatic. Google exposes no endpoint or
        /// header with the account's limits, so in automatic mode the limiter learns each model's limit
        /// from 429 "per minute" responses (paid tiers simply never hit one).
        /// </summary>
        readonly int _rpm;

        /// <summary>kind: "status" | "tool" | "wait" | "text"</summary>
        public Action<string, string> OnProgress { get; set; } = (k, t) => { };

        /// <summary>Models tried when the main model's free daily quota is used up.</summary>
        public List<string> FallbackModels { get; set; } = new List<string>();

        /// <summary>The model that answered last (after any fallback).</summary>
        public string ModelUsed { get; private set; }

        // model → Pacific-time date on which its daily quota ran out (Google resets at midnight PT)
        static readonly Dictionary<string, string> Exhausted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        static string QuotaDay()
        {
            try { return TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTime.UtcNow, "Pacific Standard Time").ToString("yyyy-MM-dd"); }
            catch { return DateTime.UtcNow.AddHours(-8).ToString("yyyy-MM-dd"); }
        }

        string PickModel()
        {
            var all = new List<string> { _model };
            foreach (string m in FallbackModels ?? new List<string>())
                if (!string.IsNullOrWhiteSpace(m) && !all.Contains(m.Trim())) all.Add(m.Trim());
            string today = QuotaDay();
            lock (Exhausted)
                foreach (string m in all)
                {
                    string day;
                    if (!Exhausted.TryGetValue(m, out day) || day != today) return m;
                }
            return null;
        }

        static void MarkExhausted(string model)
        {
            lock (Exhausted) Exhausted[model] = QuotaDay();
        }

        public GeminiAgent(FsService fs, string apiKey, string model, int rpm, string siteUrl)
        {
            _fs = fs;
            _apiKey = apiKey;
            _model = string.IsNullOrWhiteSpace(model) ? "gemini-flash-latest" : model.Trim();
            _siteUrl = siteUrl ?? "";
            _rpm = rpm;
        }

        public obAiTurnResult RunTurn(JArray history, JArray userParts, string turnId, CancellationToken ct)
        {
            var result = new obAiTurnResult { UndoId = turnId };
            JArray contents = TrimHistory(history ?? new JArray());
            contents.Add(new JObject { ["role"] = "user", ["parts"] = userParts });
            result.Contents = contents;

            var undo = new AiUndo(_fs, turnId);
            var tools = new AiTools(_fs, undo);
            var finalText = new StringBuilder();

            try
            {
                for (int step = 0; step < MaxSteps; step++)
                {
                    ct.ThrowIfCancellationRequested();
                    OnProgress("status", step == 0 ? "Thinking…" : "Working…");
                    JObject resp = Call(contents, ct);
                    result.Requests++;

                    JObject cand = resp["candidates"]?[0] as JObject;
                    JObject content = cand?["content"] as JObject;
                    string finish = (string)cand?["finishReason"];
                    if (content == null || !(content["parts"] is JArray parts) || parts.Count == 0)
                    {
                        if (finish == "MALFORMED_FUNCTION_CALL" && step < MaxSteps - 1) { OnProgress("status", "Retrying…"); continue; }
                        string block = (string)resp["promptFeedback"]?["blockReason"];
                        finalText.Append(block != null ? $"(Gemini declined to answer: {block})" : $"(Gemini returned no answer. Reason: {finish ?? "unknown"})");
                        break;
                    }
                    if (content["role"] == null) content["role"] = "model";
                    contents.Add(content);

                    var calls = new List<JObject>();
                    foreach (JObject part in parts)
                    {
                        if (part["functionCall"] is JObject fc) calls.Add(fc);
                        else if (part["text"] != null && part["thought"] == null)
                        {
                            string t = (string)part["text"];
                            if (!string.IsNullOrWhiteSpace(t)) { finalText.Append(t); OnProgress("text", t); }
                        }
                    }
                    if (calls.Count == 0) break;
                    finalText.Clear(); // text before tool calls is narration; keep only the final answer

                    var responseParts = new JArray();
                    foreach (JObject fc in calls)
                    {
                        ct.ThrowIfCancellationRequested();
                        string name = (string)fc["name"];
                        AiToolResult r = tools.Execute(name, fc["args"] as JObject);
                        OnProgress("tool", r.Summary);
                        var fr = new JObject { ["name"] = name, ["response"] = r.Response };
                        if (fc["id"] != null) fr["id"] = fc["id"];
                        // multimodal function response: images travel inside the functionResponse
                        if (r.InlineParts.Count > 0) fr["parts"] = new JArray(r.InlineParts.ToArray());
                        responseParts.Add(new JObject { ["functionResponse"] = fr });
                    }
                    contents.Add(new JObject { ["role"] = "user", ["parts"] = responseParts });

                    if (step == MaxSteps - 1) finalText.Append("I stopped because this task needed too many steps. Ask me to continue if needed.");
                }
            }
            catch (OperationCanceledException) { result.Error = "Stopped."; }
            catch (GeminiException ex) { result.Error = ex.Message; }
            catch (Exception ex) { result.Error = "Unexpected error: " + ex.Message; }

            if (result.Error != null)
            {
                // Keep history valid for the next turn: drop an unanswered functionCall, then close with a model note.
                JObject last = contents.Count > 0 ? contents[contents.Count - 1] as JObject : null;
                if (last != null && (string)last["role"] == "model" && last.ToString().Contains("\"functionCall\""))
                    contents.RemoveAt(contents.Count - 1);
                contents.Add(new JObject { ["role"] = "model", ["parts"] = new JArray(new JObject { ["text"] = "(The previous task was interrupted: " + result.Error + ")" }) });
            }

            result.FinalText = finalText.ToString().Trim();
            result.Model = ModelUsed;
            // report files, not the folders that were auto-created for them
            foreach (string p in undo.ChangedPaths)
            {
                bool hasChild = false;
                foreach (string q in undo.ChangedPaths) if (q.StartsWith(p + "/", StringComparison.OrdinalIgnoreCase)) { hasChild = true; break; }
                if (!hasChild) result.Changed.Add(p);
            }
            result.Changed.Sort(StringComparer.OrdinalIgnoreCase);
            if (result.Changed.Count == 0) result.UndoId = null;
            AiUndo.Prune(_fs);
            return result;
        }

        // ------------------------------------------------------------------ HTTP

        JObject Call(JArray contents, CancellationToken ct)
        {
            var body = new JObject
            {
                ["systemInstruction"] = new JObject { ["parts"] = new JArray(new JObject { ["text"] = SystemPrompt() }) },
                ["contents"] = contents,
                ["tools"] = new JArray(new JObject { ["functionDeclarations"] = AiTools.Declarations() })
            };
            string json = body.ToString(Formatting.None);
            int timeouts = 0, netErrors = 0;

            for (int attempt = 0; ; attempt++)
            {
                string model = PickModel();
                if (model == null) throw new GeminiException("Today's Gemini limit has been used up for all models. It resets at midnight US Pacific time. Please try again after that.");
                ModelUsed = model;
                RateLimiter limiter = RateLimiter.For(_apiKey + "|" + model, _rpm);
                limiter.WaitTurn(ct, secs => OnProgress("wait", $"Waiting {secs}s for the Gemini speed limit…"));
                var req = new HttpRequestMessage(HttpMethod.Post, $"{ApiBase}models/{Uri.EscapeDataString(model)}:generateContent");
                req.Headers.Add("x-goog-api-key", _apiKey);
                req.Content = new StringContent(json, Encoding.UTF8, "application/json");

                HttpResponseMessage res;
                string text;
                // Gemini occasionally never answers; cap each attempt and retry instead of hanging.
                using (var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    attemptCts.CancelAfter(RequestTimeout);
                    try
                    {
                        res = Http.SendAsync(req, attemptCts.Token).GetAwaiter().GetResult();
                        text = res.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        if (timeouts++ >= 2) throw new GeminiException("Gemini did not respond in time. Please try again in a moment.");
                        OnProgress("wait", "Gemini is taking too long – asking again…");
                        attempt--;
                        continue;
                    }
                    catch (HttpRequestException ex)
                    {
                        if (netErrors++ >= 2) throw new GeminiException("Could not reach Gemini: " + (ex.InnerException ?? ex).Message);
                        SleepWithProgress(5, ct, "Network problem");
                        attempt--;
                        continue;
                    }
                }
                if (res.IsSuccessStatusCode) return JObject.Parse(text);

                int code = (int)res.StatusCode;
                string msg = ErrorMessage(text);
                if ((code == 429 && IsDailyQuota(text)) || code == 404)
                {
                    // this model is used up for today (or retired): move on to the next fallback model
                    MarkExhausted(model);
                    OnProgress("wait", code == 404 ? $"Model {model} is not available – trying another…" : $"Today's limit for {model} is used up – switching model…");
                    attempt--;
                    continue;
                }
                if (code == 429 && attempt < 6)
                {
                    limiter.LearnFromLimit();
                    int wait = RetryDelaySeconds(text, 20 + attempt * 10);
                    SleepWithProgress(wait, ct, "Gemini per-minute limit reached");
                    continue;
                }
                if ((code == 500 || code == 503 || code == 504) && attempt < 6)
                {
                    SleepWithProgress(10 + attempt * 10, ct, "Gemini servers are busy");
                    continue;
                }
                if (code == 400 && msg.IndexOf("signature", StringComparison.OrdinalIgnoreCase) >= 0 && json.Contains("\"thoughtSignature\""))
                {
                    // history came from a different model: its thought signatures are not accepted, so drop them
                    StripSignatures(contents);
                    body["contents"] = contents;
                    json = body.ToString(Formatting.None);
                    attempt--;
                    continue;
                }
                if (code == 400 && msg.IndexOf("API key", StringComparison.OrdinalIgnoreCase) >= 0 || code == 401 || code == 403)
                    throw new GeminiException("The Gemini API key was rejected. Please check it in Settings. (" + msg + ")");
                if (code == 503) throw new GeminiException("Google's Gemini servers are very busy right now. Please try again in a few minutes, or pick another model in Settings.");
                throw new GeminiException($"Gemini error {code}: {msg}");
            }
        }

        void SleepWithProgress(int seconds, CancellationToken ct, string why)
        {
            for (int s = seconds; s > 0; s--)
            {
                if (s == seconds || s % 5 == 0) OnProgress("wait", $"{why} – retrying in {s}s…");
                ct.WaitHandle.WaitOne(1000);
                ct.ThrowIfCancellationRequested();
            }
        }

        static void StripSignatures(JArray contents)
        {
            foreach (JToken sig in new List<JToken>(contents.SelectTokens("$[*].parts[*].thoughtSignature")))
                sig.Parent.Remove();
        }

        static string ErrorMessage(string body)
        {
            try { return (string)JObject.Parse(body)["error"]?["message"] ?? body; }
            catch { return body.Length > 300 ? body.Substring(0, 300) : body; }
        }

        static bool IsDailyQuota(string body) => body.IndexOf("PerDay", StringComparison.OrdinalIgnoreCase) >= 0;

        static int RetryDelaySeconds(string body, int fallback)
        {
            Match m = Regex.Match(body, "\"retryDelay\"\\s*:\\s*\"(\\d+)(?:\\.\\d+)?s\"");
            int s;
            return m.Success && int.TryParse(m.Groups[1].Value, out s) ? Math.Min(s + 1, 90) : fallback;
        }

        /// <summary>Lists models that support generateContent (used by the Settings page).</summary>
        public static List<string> ListModels(string apiKey)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, ApiBase + "models?pageSize=200");
            req.Headers.Add("x-goog-api-key", apiKey);
            HttpResponseMessage res;
            string text;
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
            {
                try
                {
                    res = Http.SendAsync(req, cts.Token).GetAwaiter().GetResult();
                    text = res.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                }
                catch (OperationCanceledException) { throw new GeminiException("Google did not respond. Please try again."); }
            }
            if (!res.IsSuccessStatusCode) throw new GeminiException(ErrorMessage(text));
            var list = new List<string>();
            foreach (JObject m in (JArray)JObject.Parse(text)["models"])
            {
                string name = ((string)m["name"]).Replace("models/", "");
                var methods = m["supportedGenerationMethods"] as JArray;
                if (methods == null || !methods.ToString().Contains("generateContent")) continue;
                if (name.StartsWith("gemini") && !Regex.IsMatch(name, "tts|image|embedding|robotics|computer|transcribe|live|audio")) list.Add(name);
            }
            list.Sort(StringComparer.OrdinalIgnoreCase);
            return list;
        }

        // ------------------------------------------------------------------ history

        /// <summary>Shrinks old tool outputs and drops old images so long chats stay small and cheap.</summary>
        public static JArray TrimHistory(JArray history)
        {
            var trimmed = (JArray)history.DeepClone();
            foreach (JToken c in trimmed)
            {
                if (!(c["parts"] is JArray parts)) continue;
                for (int i = 0; i < parts.Count; i++)
                {
                    JObject p = parts[i] as JObject;
                    if (p == null) continue;
                    if (p["functionResponse"] is JObject fr)
                    {
                        if (fr["response"] != null && fr["response"].ToString(Formatting.None).Length > OldToolOutputLimit)
                            fr["response"] = new JObject { ["ok"] = true, ["note"] = "Old tool output removed to save space. Read the file again if needed." };
                        if (fr["parts"] != null)
                        {
                            fr.Remove("parts");
                            if (fr["response"] is JObject resp) resp["note_images"] = "Images from earlier are no longer attached.";
                        }
                    }
                    else if (p["inlineData"] != null)
                        parts[i] = new JObject { ["text"] = "[image from earlier – no longer attached]" };
                }
            }
            return trimmed;
        }

        string SystemPrompt()
        {
            string site = string.IsNullOrEmpty(_siteUrl) ? "" : $" Its public address is {_siteUrl}.";
            return $@"You are Hearth, a friendly website builder assistant. You edit a real, live website by calling tools.
Today is {DateTime.Now:yyyy-MM-dd}.

WEBSITE LAYOUT (all tool paths are relative to the website root):
- The root folder IS the public website, served by IIS with PHP. 'index.php' or 'index.html' in the root is the home page.{site}
- App_Data/ = private folder for SQLite databases (IIS never serves it). From a PHP file in the root use:
  new PDO('sqlite:' . __DIR__ . '/App_Data/site.db');  — from deeper folders adjust the path, e.g. __DIR__ . '/../App_Data/site.db'.
- web.config in the root is the IIS configuration. It blocks downloads of App_Data and database files; keep those rules when you edit it.
- The recycle bin is managed by the system; you cannot access it.

HOW TO WORK:
- Before changing an existing site, look first: list_files and read the relevant files. Never guess file contents.
- Use replace_in_file for small changes to existing files; use write_file for new files or full rewrites.
- Batch reads: read several files in one read_files call. Keep the number of tool calls low – each call is one API request and the plan has limits.
- Build clean, modern, responsive (mobile-friendly) pages using plain HTML, CSS and a little JavaScript. Use PHP only when server logic is needed (forms, database). Put shared CSS in css/ and images in images/.
- Use relative links (e.g. 'css/style.css', 'about.html'), never absolute paths to this server's disk.
- PHP: always escape output with htmlspecialchars, use prepared statements for SQL, validate form input.
- Do not delete or overwrite files the user did not ask about. Deleting moves files to the recycle bin.
- Changes go live immediately, and the user can press Undo.

TALKING TO THE USER:
- The user is not technical. Reply briefly in plain, friendly language: say what you changed and what they will see. No code in replies unless they ask for it.
- If the request is unclear, ask one short question before making big changes.
- Reply in the same language the user writes in.";
        }
    }

    /// <summary>
    /// Sliding-window limiter per API key + model. Limit = manual cap if set (> 0), otherwise the
    /// limit learned from Google's 429 responses, otherwise unlimited.
    /// </summary>
    public class RateLimiter
    {
        static readonly Dictionary<string, RateLimiter> All = new Dictionary<string, RateLimiter>();
        readonly Queue<DateTime> _sent = new Queue<DateTime>();
        int _rpm = int.MaxValue;
        int _learned = int.MaxValue;
        DateTime _learnedAt;

        public static RateLimiter For(string key, int manualRpm)
        {
            lock (All)
            {
                RateLimiter r;
                if (!All.TryGetValue(key ?? "", out r)) { r = new RateLimiter(); All[key ?? ""] = r; }
                // a learned limit expires after a day so an upgraded plan is picked up automatically
                if (r._learned != int.MaxValue && DateTime.UtcNow - r._learnedAt > TimeSpan.FromHours(24)) r._learned = int.MaxValue;
                r._rpm = manualRpm > 0 ? manualRpm : r._learned;
                return r;
            }
        }

        /// <summary>Google said "too many per minute": what got through in the last minute is the limit.</summary>
        public void LearnFromLimit()
        {
            lock (_sent)
            {
                DateTime now = DateTime.UtcNow;
                while (_sent.Count > 0 && (now - _sent.Peek()).TotalSeconds >= 60) _sent.Dequeue();
                // the request that just failed was counted too, so subtract it
                int ok = Math.Max(1, _sent.Count - 1);
                _learned = Math.Min(_learned, ok);
                _learnedAt = now;
                _rpm = Math.Min(_rpm, _learned);
            }
        }

        public void WaitTurn(CancellationToken ct, Action<int> onWait)
        {
            while (true)
            {
                int waitMs;
                lock (_sent)
                {
                    DateTime now = DateTime.UtcNow;
                    while (_sent.Count > 0 && (now - _sent.Peek()).TotalSeconds >= 60) _sent.Dequeue();
                    if (_sent.Count < _rpm) { _sent.Enqueue(now); return; }
                    waitMs = (int)(60000 - (now - _sent.Peek()).TotalMilliseconds) + 250;
                }
                onWait(Math.Max(1, waitMs / 1000));
                ct.WaitHandle.WaitOne(Math.Min(waitMs, 5000));
                ct.ThrowIfCancellationRequested();
            }
        }
    }
}
