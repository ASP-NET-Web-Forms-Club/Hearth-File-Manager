using System;
using System.Text;
using System.Web;
using Newtonsoft.Json;

namespace HearthFileManager.engine
{
    /// <summary>Master HTML shell: head, modern sidebar, main container, footer scripts.</summary>
    public class PageTemplate
    {
        public string Title { get; set; } = "";
        public string BodyClass { get; set; } = "";
        /// <summary>Sidebar key of the active menu item: files, ai, recycle, users, settings.</summary>
        public string ActiveMenu { get; set; } = "";
        public bool ShowSidebar { get; set; } = true;
        public string ExtraHeaderText { get; set; } = "";
        public string ExtraFooterText { get; set; } = "";

        public string GenerateHtmlHeader()
        {
            string title = HttpUtility.HtmlEncode(string.IsNullOrEmpty(Title) ? "Hearth File Manager" : Title + " · Hearth");
            string css = StaticAsset.Css("/assets/fontawesome/css/all.min.css") + "\n" + StaticAsset.Css("/css/site.css");
            string sidebar = ShowSidebar ? BuildSidebar() : "";
            string shellClass = ShowSidebar ? "app-shell" : "app-shell no-sidebar";

            return $@"<!DOCTYPE html>
<html lang='en'>
<head>
<meta charset='utf-8' />
<meta name='viewport' content='width=device-width, initial-scale=1' />
<title>{title}</title>
<link rel='icon' href='/assets/favicon.svg' type='image/svg+xml' />
{css}
{ExtraHeaderText}
</head>
<body class='{HttpUtility.HtmlAttributeEncode(BodyClass)}'>
<div class='{shellClass}'>
{sidebar}
<main class='app-main'>
";
        }

        public string GenerateHtmlFooter()
        {
            return $@"
</main>
</div>
<div id='toast-host' class='toast-host'></div>
{StaticAsset.Script("/js/site.js")}
{ExtraFooterText}
</body>
</html>";
        }

        string BuildSidebar()
        {
            obUser me = AppSession.LoginUser;
            string username = HttpUtility.HtmlEncode(me != null ? me.Username : "");
            string initial = HttpUtility.HtmlEncode(me != null && me.Username.Length > 0 ? me.Username.Substring(0, 1).ToUpperInvariant() : "?");

            // menu items the user is allowed to use
            string aiItem = me != null && me.Has(Perm.Ai) ? MenuItem("ai", "/ai", "fa-wand-magic-sparkles", "AI Website Builder") : "";
            string usersItem = me != null && me.Has(Perm.Users) ? MenuItem("users", "/users", "fa-users", "Users") : "";
            string settingsItem = me != null && me.Has(Perm.Settings) ? MenuItem("settings", "/settings", "fa-gear", "Settings") : "";
            string accountItem = MenuItem("account", "/account", "fa-user-gear", "My Account");

            var sb = new StringBuilder();
            sb.Append($@"
<button type='button' class='sidebar-toggle' id='sidebar-toggle' aria-label='Open menu'><i class='fa-solid fa-bars'></i></button>
<div class='sidebar-backdrop' id='sidebar-backdrop'></div>
<aside class='sidebar' id='sidebar'>
    <div class='sidebar-brand'>
        <span class='brand-mark'><i class='fa-solid fa-fire-flame-curved'></i></span>
        <span class='brand-text'>Hearth<small>File Manager</small></span>
    </div>
    <nav class='sidebar-nav'>
        <div class='nav-label'>Website</div>
        {MenuItem("files", "/files", "fa-folder-open", "My Files")}
        {aiItem}
        {MenuItem("recycle", "/files#recycle-bin", "fa-trash-can", "Recycle Bin")}
        <div class='nav-label'>Account</div>
        {accountItem}
        {usersItem}
        {settingsItem}
    </nav>
    <div class='sidebar-user'>
        <span class='avatar'>{initial}</span>
        <a class='user-name' href='/account' title='My Account'>{username}</a>
        <a class='btn-icon' href='/logout' title='Sign out'><i class='fa-solid fa-right-from-bracket'></i></a>
    </div>
</aside>");
            return sb.ToString();
        }

        string MenuItem(string key, string href, string icon, string text)
        {
            string active = key == ActiveMenu ? " active" : "";
            return $"<a class='nav-item{active}' data-menu='{key}' href='{href}'><i class='fa-solid {icon}'></i><span>{text}</span></a>";
        }

        /// <summary>Emits a JS global data island. All values cross through JsonConvert.</summary>
        public static string DataIsland(string name, object value)
        {
            var settings = new JsonSerializerSettings { StringEscapeHandling = StringEscapeHandling.EscapeHtml };
            return $"<script>window.{name} = {JsonConvert.SerializeObject(value, settings)};</script>";
        }

        public static void WriteHtml(string html)
        {
            HttpResponse res = HttpContext.Current.Response;
            res.ContentType = "text/html; charset=utf-8";
            res.Write(html);
            ApiHelper.EndResponse();
        }
    }
}
