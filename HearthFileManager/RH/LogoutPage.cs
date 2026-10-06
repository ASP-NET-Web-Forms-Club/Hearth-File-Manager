using System;
using System.Web;
using HearthFileManager.engine;

namespace HearthFileManager.RH
{
    public class LogoutPage
    {
        public static void HandleRequest()
        {
            AppSession.Logout();
            HttpContext.Current.Response.Redirect("/login", false);
            ApiHelper.EndResponse();
        }
    }
}
