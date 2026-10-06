using System;
using System.Web;
using HearthFileManager.engine;
using HearthFileManager.engine.Ai;
using Newtonsoft.Json.Linq;

namespace HearthFileManager.RH
{
    public class AiPageApi
    {
        static HttpRequest Req => HttpContext.Current.Request;

        public static void HandleRequest()
        {
            string action = (Req["action"] + "").ToLower().Trim();
            try
            {
                switch (action)
                {
                    case "start": Guard.Mutating(Perm.Ai); Start(); break;
                    case "poll": Guard.Require(Perm.Ai); Poll(); break;
                    case "stop": Guard.Mutating(Perm.Ai); Stop(); break;
                    case "undo": Guard.Mutating(Perm.Ai); Undo(); break;
                    default: ApiHelper.WriteError($"Unknown action: {action}", 400); break;
                }
            }
            catch (GuardException gx) { ApiHelper.WriteError(gx.Message, gx.StatusCode); }
            catch (FsException fx) { ApiHelper.WriteError(fx.Message, 400); }
            catch (Exception ex) { ApiHelper.WriteError("An error occurred: " + ex.Message, 500); }
            ApiHelper.EndResponse();
        }

        /// <summary>
        /// Form: history = Gemini "contents" JSON array kept in the browser (IndexedDB),
        ///       message = text, images = JSON array of { MimeType, Data(base64) }.
        /// </summary>
        static void Start()
        {
            obConfig cfg = AppConfig.Get();
            if (string.IsNullOrWhiteSpace(cfg.GeminiApiKey)) throw new FsException("No Gemini API key yet. Add one on the Settings page.");

            var form = Req.Unvalidated.Form;
            string message = (form["message"] ?? "").Trim();
            JArray history = string.IsNullOrEmpty(form["history"]) ? new JArray() : JArray.Parse(form["history"]);
            JArray images = string.IsNullOrEmpty(form["images"]) ? new JArray() : JArray.Parse(form["images"]);
            if (message == "" && images.Count == 0) throw new FsException("Please type a message.");
            if (images.Count > 4) throw new FsException("Attach up to 4 pictures at a time.");

            var parts = new JArray();
            foreach (JObject img in images)
            {
                string mime = (string)img["MimeType"] ?? "";
                if (!mime.StartsWith("image/")) continue;
                parts.Add(new JObject { ["inlineData"] = new JObject { ["mimeType"] = mime, ["data"] = (string)img["Data"] } });
            }
            if (message != "") parts.Add(new JObject { ["text"] = message });

            var agent = new GeminiAgent(new FsService(AppConfig.AppDataPath), cfg.GeminiApiKey, cfg.GeminiModel, cfg.GeminiRpm, cfg.SitePreviewUrl);
            agent.FallbackModels = cfg.GeminiFallbackModels;
            AiTask task = AiTaskManager.Start(AppSession.LoginUser.Username, agent, history, parts);
            ApiHelper.WriteSuccess("Started", new { TaskId = task.Id });
        }

        static void Poll()
        {
            AiTask task = AiTaskManager.Get(Req["taskId"]);
            if (task == null) throw new FsException("This request is no longer running (the server may have restarted). Please send your message again.");
            int since;
            int.TryParse(Req["since"], out since);

            var events = task.EventsSince(since);
            object result = null;
            if (task.Done && task.Result != null)
            {
                obAiTurnResult r = task.Result;
                result = new
                {
                    r.FinalText,
                    r.Error,
                    r.Changed,
                    r.UndoId,
                    r.Requests,
                    r.Model,
                    Contents = r.Contents
                };
            }
            ApiHelper.WriteJson(new { success = true, message = "Success", data = new { task.Done, Next = since + events.Count, Events = events, Result = result } });
        }

        static void Stop()
        {
            AiTask task = AiTaskManager.Get(Req.Form["taskId"]);
            if (task != null) task.Cancel.Cancel();
            ApiHelper.WriteSuccess("Stopping…");
        }

        static void Undo()
        {
            var restored = AiUndo.Undo(new FsService(AppConfig.AppDataPath), Req.Form["undoId"]);
            ApiHelper.WriteSuccess($"Undone. {restored.Count} item(s) restored.", restored);
        }
    }
}
