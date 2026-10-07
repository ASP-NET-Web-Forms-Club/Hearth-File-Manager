using System;
using System.Web;
using HearthFileManager.engine;
using HearthFileManager.engine.Ai;

namespace HearthFileManager.RH
{
    public class SettingsPageApi
    {
        static HttpRequest Req => HttpContext.Current.Request;

        public static void HandleRequest()
        {
            string action = (Req["action"] + "").ToLower().Trim();
            try
            {
                switch (action)
                {
                    case "save": Guard.Mutating(Perm.Settings); Save(); break;
                    case "models": Guard.Mutating(Perm.Settings); Models(); break;
                    default: ApiHelper.WriteError($"Unknown action: {action}", 400); break;
                }
            }
            catch (GuardException gx) { ApiHelper.WriteError(gx.Message, gx.StatusCode); }
            catch (GeminiException gx) { ApiHelper.WriteError(gx.Message, 400); }
            catch (Exception ex) { ApiHelper.WriteError("An error occurred: " + ex.Message, 500); }
            ApiHelper.EndResponse();
        }

        static int Int(string name, int min, int max, int fallback)
        {
            int v;
            if (!int.TryParse(Req.Form[name], out v)) return fallback;
            return Math.Max(min, Math.Min(max, v));
        }

        static void Save()
        {
            string key = (Req.Form["key"] ?? "").Trim();
            string model = (Req.Form["model"] ?? "").Trim();
            string site = (Req.Form["site"] ?? "").Trim().TrimEnd('/');
            if (site != "" && !Uri.IsWellFormedUriString(site, UriKind.Absolute)) { ApiHelper.WriteError("The website address must start with http:// or https://"); return; }
            bool isLocal = Req.IsLocal;

            // main root folder: validate before saving
            string root = AppConfig.NormalizePathSetting(Req.Form["root"]);
            if (root == "") root = "/App_Data/public";
            string rootFull;
            try { rootFull = AppConfig.ResolvePath(root, AppConfig.AppRoot); }
            catch (Exception ex) { ApiHelper.WriteError("Invalid root folder: " + ex.Message); return; }
            string problem = AppConfig.RootProblem(rootFull);
            if (problem != null) { ApiHelper.WriteError(problem); return; }
            string writeProblem = new FsService(AppConfig.AppDataPath, rootFull).WriteProblem();
            bool devLogin = Req.Form["devlogin"] == "1";

            AppConfig.Update(c =>
            {
                if (key != "") c.GeminiApiKey = key;
                if (model != "") c.GeminiModel = model;
                c.GeminiRpm = Int("rpm", 0, 100000, c.GeminiRpm);
                c.MaxUploadMb = Int("maxupload", 1, 4096, c.MaxUploadMb);
                c.SitePreviewUrl = site;
                c.SiteRoot = root;
                if (isLocal) c.DevAutoLogin = devLogin;
            });
            string msg = writeProblem == null ? "Settings saved"
                : "Settings saved, but Hearth cannot write to " + rootFull + " (" + writeProblem + "). Give the IIS app pool identity Modify permission on it.";
            ApiHelper.WriteSuccess(msg, new { KeyMasked = SettingsPage.MaskKey(AppConfig.Get().GeminiApiKey), SiteRootFull = rootFull });
        }

        static void Models()
        {
            string key = (Req.Form["key"] ?? "").Trim();
            if (key == "") key = AppConfig.Get().GeminiApiKey;
            if (string.IsNullOrEmpty(key)) { ApiHelper.WriteError("Enter an API key first."); return; }
            ApiHelper.WriteJson(new { success = true, message = "Success", items = GeminiAgent.ListModels(key) });
        }
    }
}
