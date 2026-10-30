using System;
using System.Text;
using System.Web;
using HearthFileManager.engine;

namespace HearthFileManager.RH
{
    public class SettingsPage
    {
        public static string MaskKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return "";
            return key.Length <= 8 ? "••••" : "••••••••" + key.Substring(key.Length - 4);
        }

        public static void HandleRequest()
        {
            if (!Guard.PageRequire(Perm.Settings, "settings")) return;
            obConfig cfg = AppConfig.Get();

            var pt = new PageTemplate { Title = "Settings", BodyClass = "page-settings", ActiveMenu = "settings" };
            pt.ExtraFooterText = PageTemplate.DataIsland("SETTINGS", new
            {
                KeyMasked = MaskKey(cfg.GeminiApiKey),
                cfg.GeminiModel,
                cfg.GeminiRpm,
                Fallbacks = string.Join(", ", cfg.GeminiFallbackModels ?? new System.Collections.Generic.List<string>()),
                cfg.MaxUploadMb,
                cfg.SitePreviewUrl,
                FilesWidth = AppConfig.NormalizeFilesWidth(cfg.FilesWidth, false),
                // a full path from config.json is never sent to the browser
                SiteRoot = AppConfig.IsAbsoluteSetting(cfg.SiteRoot) ? AppConfig.DisplayPath(AppConfig.MainRoot()) : cfg.SiteRoot,
                SiteRootLocked = AppConfig.IsAbsoluteSetting(cfg.SiteRoot),
                SiteRootFull = AppConfig.DisplayPath(AppConfig.MainRoot()),
                cfg.DevAutoLogin,
                IsLocal = HttpContext.Current.Request.IsLocal
            }) + "\n" + StaticAsset.Script("/js/settings.js");

            var sb = new StringBuilder();
            sb.Append(pt.GenerateHtmlHeader());
            sb.Append(@"
<div class='page page-width-medium'>
    <header class='page-header'>
        <h1><i class='fa-solid fa-gear'></i> Settings</h1>
    </header>

    <form class='card form-grid' id='settings-form'>
        <h2><i class='fa-solid fa-wand-magic-sparkles'></i> Gemini AI</h2>
        <label class='field'>
            <span>API key</span>
            <input type='password' id='set-key' autocomplete='off' placeholder='Paste a key from Google AI Studio' />
            <small>Current: <b id='set-key-current'>none</b>. Leave empty to keep it. Get a free key at <a href='https://aistudio.google.com/apikey' target='_blank' rel='noopener'>aistudio.google.com/apikey</a>.</small>
        </label>
        <label class='field'>
            <span>Model</span>
            <div class='row'>
                <select id='set-model'></select>
                <button type='button' class='btn' id='set-load-models' title='Reload the list from Google (e.g. after entering a new key)'><i class='fa-solid fa-rotate'></i><span>Refresh list</span></button>
            </div>
            <small>Flash models are free. On the free plan each model allows only a limited number of requests per day; when the limit is reached the builder automatically switches to: <b id='set-fallbacks'></b>.</small>
        </label>
        <label class='field'>
            <span>Requests per minute</span>
            <input type='number' id='set-rpm' min='0' max='100000' />
            <small><b>0 = automatic</b> (recommended): the builder runs at full speed and, if Google says the per-minute limit was reached, remembers that model's limit and slows down. Works for both free and paid plans. Enter a number only to force a lower cap.</small>
        </label>

        <h2><i class='fa-solid fa-globe'></i> Website</h2>
        <label class='field'>
            <span>Main root folder</span>
            <input type='text' id='set-root' placeholder='/App_Data/public' spellcheck='false' />
            <small id='set-root-hint'>The folder Hearth manages: the public website that your second IIS site serves. <b>/App_Data/public</b> or a sub-folder of it, e.g. <b>/App_Data/public/site1</b>; both / and \ work.
            Other locations (e.g. <b>D:\websites\</b>) can only be set by the server administrator in <b>App_Data/config.json</b>.</small>
            <small id='set-root-locked' hidden><i class='fa-solid fa-lock'></i> Set in App_Data/config.json by the server administrator; it can't be changed here.</small>
        </label>
        <label class='field'>
            <span>Public website address</span>
            <input type='url' id='set-site' placeholder='https://www.example.com' />
            <small>Used for the ""View website"" buttons. This site serves the <b>www</b> folder.</small>
        </label>
        <label class='field'>
            <span>Maximum upload size (MB per file)</span>
            <input type='number' id='set-maxupload' min='1' max='4096' />
        </label>
        <label class='field'>
            <span>File manager display width (system default)</span>
            <div class='row'>
                <select id='set-fileswidth-mode'>
                    <option value='100%'>100% (Full width – default)</option>
                    <option value='900px'>Max width 900px</option>
                    <option value='custom'>Custom width (px)…</option>
                </select>
                <input type='number' id='set-fileswidth-custom' min='500' max='3840' step='10' placeholder='e.g. 1200' style='max-width:160px' hidden />
            </div>
            <small>Applies to all users by default. Each user can also override their own display width directly from the toolbar on the <b>My Files</b> page.</small>
        </label>

        <div id='dev-block' hidden>
            <h2><i class='fa-solid fa-code'></i> Developer</h2>
            <label class='check'>
                <input type='checkbox' id='set-devlogin' />
                <span>Auto sign-in for requests from this computer (localhost only). Turn off in production.</span>
            </label>
        </div>

        <div class='form-actions'>
            <button type='submit' class='btn btn-primary'><i class='fa-solid fa-floppy-disk'></i><span>Save settings</span></button>
        </div>
    </form>
</div>");
            sb.Append(pt.GenerateHtmlFooter());
            PageTemplate.WriteHtml(sb.ToString());
        }
    }
}
