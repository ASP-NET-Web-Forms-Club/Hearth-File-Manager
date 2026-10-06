using System;
using System.Text;
using HearthFileManager.engine;

namespace HearthFileManager.RH
{
    public class UsersPage
    {
        public static void HandleRequest()
        {
            if (!Guard.PageRequire(Perm.Users, "users")) return;

            var pt = new PageTemplate { Title = "Users", BodyClass = "page-users", ActiveMenu = "users" };
            pt.ExtraFooterText = PageTemplate.DataIsland("USERS_CONFIG", new
            {
                Me = AppSession.LoginUser.Username,
                Perms = new[]
                {
                    new { Key = Perm.Files, Label = AccountPage.PermLabels[Perm.Files], Hint = "Upload, edit, rename, move, delete, zip. Without it: view and download only." },
                    new { Key = Perm.Ai, Label = AccountPage.PermLabels[Perm.Ai], Hint = "Chat with Gemini to build the website (uses the API key, which may cost money). Includes Manage files." },
                    new { Key = Perm.Settings, Label = AccountPage.PermLabels[Perm.Settings], Hint = "Gemini API key and model, upload limit, website address." },
                    new { Key = Perm.Users, Label = AccountPage.PermLabels[Perm.Users], Hint = "Add and delete users, reset passwords, change permissions." }
                },
                Defaults = Perm.NewUserDefault
            })
                + "\n" + StaticAsset.Script("/js/users.js");

            var sb = new StringBuilder();
            sb.Append(pt.GenerateHtmlHeader());
            sb.Append(@"
<div class='page'>
    <header class='page-header'>
        <h1><i class='fa-solid fa-users'></i> Users</h1>
        <p>Choose what each person may do. Everyone can always change their own password on <a href='/account'>My Account</a>.</p>
    </header>

    <div class='card'>
        <div class='card-head'>
            <h2>Accounts</h2>
            <button type='button' class='btn btn-primary' id='user-add'><i class='fa-solid fa-user-plus'></i><span>Add user</span></button>
        </div>
        <table class='table' id='user-table'>
            <thead><tr><th>Username</th><th>Permissions</th><th>Created</th><th class='col-actions'></th></tr></thead>
            <tbody id='user-rows'></tbody>
        </table>
    </div>
</div>");
            sb.Append(pt.GenerateHtmlFooter());
            PageTemplate.WriteHtml(sb.ToString());
        }
    }
}
