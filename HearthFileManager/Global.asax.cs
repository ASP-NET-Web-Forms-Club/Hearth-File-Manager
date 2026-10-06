using System;
using System.Web;
using System.Web.Hosting;
using HearthFileManager.engine;

namespace HearthFileManager
{
    public class Global : HttpApplication
    {
        protected void Application_Start(object sender, EventArgs e)
        {
            AppConfig.AppDataPath = HostingEnvironment.MapPath("~/App_Data");
            var fs = new FsService(AppConfig.AppDataPath);
            fs.CleanupTemp(TimeSpan.FromDays(1));
            AppConfig.Get(); // migrates/creates config.json
        }

        protected void Application_BeginRequest(object sender, EventArgs e)
        {
            string path = Request.Path.ToLower().Trim().TrimEnd('/');

            // static assets: let IIS serve them without session work
            if (path.StartsWith("/css/") || path.StartsWith("/js/") || path.StartsWith("/assets/")) return;

            AppSession.TryRestoreFromCookie();

            switch (path)
            {
                case "":
                case "/home":         Response.Redirect("/files", false); ApiHelper.EndResponse(); return;
                case "/login":        RH.LoginPage.HandleRequest(); return;
                case "/loginapi":     RH.LoginPageApi.HandleRequest(); return;
                case "/logout":       RH.LogoutPage.HandleRequest(); return;
                case "/files":        RH.FilesPage.HandleRequest(); return;
                case "/fileapi":      RH.FilesPageApi.HandleRequest(); return;
                case "/ai":           RH.AiPage.HandleRequest(); return;
                case "/aiapi":        RH.AiPageApi.HandleRequest(); return;
                case "/users":        RH.UsersPage.HandleRequest(); return;
                case "/userapi":      RH.UsersPageApi.HandleRequest(); return;
                case "/settings":     RH.SettingsPage.HandleRequest(); return;
                case "/settingsapi":  RH.SettingsPageApi.HandleRequest(); return;
                case "/account":      RH.AccountPage.HandleRequest(); return;
                case "/accountapi":   RH.AccountPageApi.HandleRequest(); return;
            }
        }
    }
}
