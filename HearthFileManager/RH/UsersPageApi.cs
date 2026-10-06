using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Web;
using HearthFileManager.engine;
using Newtonsoft.Json;

namespace HearthFileManager.RH
{
    public class UsersPageApi
    {
        static HttpRequest Req => HttpContext.Current.Request;

        public static void HandleRequest()
        {
            string action = (Req["action"] + "").ToLower().Trim();
            try
            {
                switch (action)
                {
                    case "list": Guard.Require(Perm.Users); List(); break;
                    case "add": Guard.Mutating(Perm.Users); Add(); break;
                    case "password": Guard.Mutating(Perm.Users); Password(); break;
                    case "permissions": Guard.Mutating(Perm.Users); Permissions(); break;
                    case "delete": Guard.Mutating(Perm.Users); Delete(); break;
                    default: ApiHelper.WriteError($"Unknown action: {action}", 400); break;
                }
            }
            catch (GuardException gx) { ApiHelper.WriteError(gx.Message, gx.StatusCode); }
            catch (Exception ex) { ApiHelper.WriteError("An error occurred: " + ex.Message, 500); }
            ApiHelper.EndResponse();
        }

        static bool IsMe(string username) => string.Equals(username, AppSession.LoginUser.Username, StringComparison.OrdinalIgnoreCase);

        static void List()
        {
            var items = new List<object>();
            foreach (obUser u in AppConfig.Get().Users)
                items.Add(new { u.Username, u.CreatedUtc, Permissions = u.EffectivePermissions() });
            ApiHelper.WriteJson(new { success = true, message = "Success", items });
        }

        static string ValidPassword()
        {
            string pwd = Req.Unvalidated.Form["password"] ?? "";
            if (pwd.Length < 8) throw new GuardException("The password must be at least 8 characters.", 400);
            return pwd;
        }

        static List<string> PostedPermissions()
        {
            string json = Req.Form["permissions"];
            if (string.IsNullOrEmpty(json)) return new List<string>(Perm.NewUserDefault);
            return Perm.Normalize(JsonConvert.DeserializeObject<List<string>>(json));
        }

        static void Add()
        {
            string username = (Req.Form["username"] + "").Trim();
            if (!Regex.IsMatch(username, "^[A-Za-z0-9._@-]{3,40}$")) { ApiHelper.WriteError("Username: 3–40 letters, numbers, dot, dash, underscore or @."); return; }
            if (AppConfig.FindUser(username) != null) { ApiHelper.WriteError("That username is already taken."); return; }
            string hash = AppConfig.HashPassword(ValidPassword());
            List<string> perms = PostedPermissions();
            AppConfig.Update(c => c.Users.Add(new obUser { Username = username, PasswordHash = hash, CreatedUtc = DateTime.UtcNow, Permissions = perms }));
            ApiHelper.WriteSuccess("User added");
        }

        /// <summary>Admin password reset (no current password needed). The user is signed out everywhere.</summary>
        static void Password()
        {
            obUser u = AppConfig.FindUser(Req.Form["username"] + "");
            if (u == null) { ApiHelper.WriteError("User not found", 404); return; }
            string hash = AppConfig.HashPassword(ValidPassword());
            AppConfig.Update(c => { foreach (obUser x in c.Users) if (x.Username == u.Username) x.PasswordHash = hash; });
            if (!IsMe(u.Username)) AppSession.RevokeUser(u.Username);
            ApiHelper.WriteSuccess("Password changed");
        }

        static void Permissions()
        {
            obUser u = AppConfig.FindUser(Req.Form["username"] + "");
            if (u == null) { ApiHelper.WriteError("User not found", 404); return; }
            List<string> perms = PostedPermissions();

            // lockout protection: you cannot take "Manage users" away from yourself,
            // and at least one account must always keep it
            if (!perms.Contains(Perm.Users))
            {
                if (IsMe(u.Username)) { ApiHelper.WriteError("You cannot remove your own \"Manage users\" permission."); return; }
                if (!AnotherManagerExists(u.Username)) { ApiHelper.WriteError("At least one user must keep the \"Manage users\" permission."); return; }
            }

            AppConfig.Update(c => { foreach (obUser x in c.Users) if (x.Username == u.Username) x.Permissions = perms; });
            ApiHelper.WriteSuccess("Permissions saved for " + u.Username);
        }

        static void Delete()
        {
            string username = Req.Form["username"] + "";
            if (IsMe(username)) { ApiHelper.WriteError("You cannot delete your own account."); return; }
            obUser u = AppConfig.FindUser(username);
            if (u == null) { ApiHelper.WriteError("User not found", 404); return; }
            if (u.Has(Perm.Users) && !AnotherManagerExists(u.Username)) { ApiHelper.WriteError("At least one user must keep the \"Manage users\" permission."); return; }
            AppConfig.Update(c => c.Users.RemoveAll(x => x.Username == u.Username));
            AppSession.RevokeUser(u.Username);
            ApiHelper.WriteSuccess("User deleted");
        }

        static bool AnotherManagerExists(string exceptUsername)
        {
            foreach (obUser x in AppConfig.Get().Users)
                if (!string.Equals(x.Username, exceptUsername, StringComparison.OrdinalIgnoreCase) && x.Has(Perm.Users)) return true;
            return false;
        }
    }
}
