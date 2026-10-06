using System;
using System.Collections.Concurrent;
using System.Web;
using HearthFileManager.engine;

namespace HearthFileManager.RH
{
    public class LoginPageApi
    {
        // simple brute-force throttle: per IP, 8 failures → 5-minute lockout
        static readonly ConcurrentDictionary<string, Tuple<int, DateTime>> Failures = new ConcurrentDictionary<string, Tuple<int, DateTime>>();

        public static void HandleRequest()
        {
            var req = HttpContext.Current.Request;
            string action = (req["action"] + "").ToLower().Trim();
            try
            {
                switch (action)
                {
                    case "login": Login(); break;
                    default: ApiHelper.WriteError($"Unknown action: {action}", 400); break;
                }
            }
            catch (Exception ex) { ApiHelper.WriteError("An error occurred: " + ex.Message, 500); }
            ApiHelper.EndResponse();
        }

        static void Login()
        {
            var req = HttpContext.Current.Request;
            if (req.HttpMethod != "POST" || req.Headers["X-Hearth"] != "1") { ApiHelper.WriteError("Invalid request", 403); return; }

            string ip = req.UserHostAddress ?? "";
            Tuple<int, DateTime> f;
            if (Failures.TryGetValue(ip, out f) && f.Item1 >= 8 && DateTime.UtcNow - f.Item2 < TimeSpan.FromMinutes(5))
            {
                ApiHelper.WriteError("Too many failed attempts. Please wait 5 minutes and try again.", 429);
                return;
            }

            string username = (req.Form["username"] + "").Trim();
            string password = req.Form["password"] + "";
            if (!AppConfig.VerifyUser(username, password))
            {
                Failures.AddOrUpdate(ip, new Tuple<int, DateTime>(1, DateTime.UtcNow), (k, old) => new Tuple<int, DateTime>(old.Item1 + 1, DateTime.UtcNow));
                ApiHelper.WriteError("Wrong username or password.", 400);
                return;
            }
            Failures.TryRemove(ip, out f);
            AppSession.Login(AppConfig.FindUser(username));
            ApiHelper.WriteSuccess("Welcome back!");
        }
    }
}
