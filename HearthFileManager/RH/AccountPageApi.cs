using System;
using System.Web;
using HearthFileManager.engine;

namespace HearthFileManager.RH
{
    public class AccountPageApi
    {
        static HttpRequest Req => HttpContext.Current.Request;

        public static void HandleRequest()
        {
            string action = (Req["action"] + "").ToLower().Trim();
            try
            {
                switch (action)
                {
                    case "change-password": Guard.Mutating(); ChangePassword(); break;
                    default: ApiHelper.WriteError($"Unknown action: {action}", 400); break;
                }
            }
            catch (GuardException gx) { ApiHelper.WriteError(gx.Message, gx.StatusCode); }
            catch (Exception ex) { ApiHelper.WriteError("An error occurred: " + ex.Message, 500); }
            ApiHelper.EndResponse();
        }

        /// <summary>Every user may change their own password, but must prove the current one.</summary>
        static void ChangePassword()
        {
            obUser me = AppSession.LoginUser;
            string current = Req.Unvalidated.Form["current"] ?? "";
            string next = Req.Unvalidated.Form["password"] ?? "";

            if (!AppConfig.VerifyUser(me.Username, current)) { ApiHelper.WriteError("Your current password is not correct."); return; }
            if (next.Length < 8) { ApiHelper.WriteError("The new password must be at least 8 characters."); return; }
            if (next == current) { ApiHelper.WriteError("The new password must be different from the current one."); return; }

            string hash = AppConfig.HashPassword(next);
            AppConfig.Update(c => { foreach (obUser x in c.Users) if (x.Username == me.Username) x.PasswordHash = hash; });
            ApiHelper.WriteSuccess("Password changed");
        }
    }
}
