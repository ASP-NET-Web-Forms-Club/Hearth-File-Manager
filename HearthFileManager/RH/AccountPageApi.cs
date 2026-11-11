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
                    case "token-list": Guard.Require(Perm.Api); TokenList(); break;
                    case "token-create": Guard.Mutating(Perm.Api); TokenCreate(); break;
                    case "token-revoke": Guard.Mutating(Perm.Api); TokenRevoke(); break;
                    default: ApiHelper.WriteError($"Unknown action: {action}", 400); break;
                }
            }
            catch (GuardException gx) { ApiHelper.WriteError(gx.Message, gx.StatusCode); }
            catch (Exception ex) { ApiHelper.WriteError("An error occurred: " + ex.Message, 500); }
            ApiHelper.EndResponse();
        }

        // ---- access tokens for the deploy API (/api/v1). Only the owner sees and manages their tokens. ----

        static object TokenDto(obApiToken t) => new { t.Id, t.Name, t.Prefix, t.CanWrite, t.CreatedUtc, t.ExpiresUtc, t.LastUsedUtc, Expired = t.IsExpired };

        static void TokenList()
        {
            var items = new System.Collections.Generic.List<object>();
            foreach (obApiToken t in ApiTokens.ListFor(AppSession.LoginUser.Username)) items.Add(TokenDto(t));
            ApiHelper.WriteJson(new { success = true, message = "Success", items, data = new { Help = ApiV1.HelpText(ApiHelper.GetBaseUrl()) } });
        }

        static void TokenCreate()
        {
            obUser me = AppSession.LoginUser;
            bool canWrite = Req.Form["canWrite"] == "1";
            if (canWrite && !me.Has(Perm.Files)) { ApiHelper.WriteError("You need the \"Manage files\" permission for a read & write token."); return; }
            int days;
            int.TryParse(Req.Form["days"], out days);
            if (days < 0 || days > 3650) days = 0;

            string plain;
            obApiToken t = ApiTokens.Create(me.Username, Req.Form["name"], canWrite, days, out plain);
            // the plain token leaves the server exactly once, here
            ApiHelper.WriteSuccess("Token created", new { Token = plain, Info = TokenDto(t) });
        }

        static void TokenRevoke()
        {
            if (!ApiTokens.Revoke(AppSession.LoginUser.Username, Req.Form["id"] + "")) { ApiHelper.WriteError("Token not found.", 404); return; }
            ApiHelper.WriteSuccess("Token revoked");
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
