using System;
using System.Collections.Generic;
using System.Text;
using System.Web;
using HearthFileManager.engine;

namespace HearthFileManager.RH
{
    public class AccountPage
    {
        public static readonly Dictionary<string, string> PermLabels = new Dictionary<string, string>
        {
            { Perm.Files, "Manage files" },
            { Perm.Ai, "Use AI website builder" },
            { Perm.Settings, "Manage settings" },
            { Perm.Users, "Manage users" },
            { Perm.Api, "Use API access tokens" }
        };

        public static void HandleRequest()
        {
            if (!Guard.PageLoggedIn()) return;
            obUser me = AppSession.LoginUser;

            var perms = new StringBuilder();
            foreach (string p in Perm.All)
            {
                bool has = me.Has(p);
                perms.Append($"<li class='{(has ? "yes" : "no")}'><i class='fa-solid {(has ? "fa-circle-check" : "fa-circle-minus")}'></i> {HttpUtility.HtmlEncode(PermLabels[p])}</li>");
            }
            if (!me.Has(Perm.Files)) perms.Append("<li class='note'>Without “Manage files” you can view and download files, but not change them.</li>");

            var pt = new PageTemplate { Title = "My Account", BodyClass = "page-account", ActiveMenu = "account" };
            pt.ExtraFooterText = PageTemplate.DataIsland("ACCOUNT_CONFIG", new { CanApi = me.Has(Perm.Api), CanWrite = me.Has(Perm.Files) })
                + "\n" + StaticAsset.Script("/js/account.js");

            string tokenCard = !me.Has(Perm.Api) ? "" : @"
    <div class='card token-card'>
        <div class='card-head'>
            <h2><i class='fa-solid fa-plug'></i> Access tokens</h2>
            <div class='token-actions'>
                <button type='button' class='btn' id='token-help'><i class='fa-solid fa-robot'></i><span>Copy instructions for AI</span></button>
                <button type='button' class='btn btn-primary' id='token-add'><i class='fa-solid fa-plus'></i><span>New token</span></button>
            </div>
        </div>
        <p class='token-intro'>A token lets a script or an AI agent list and upload your website files through the Hearth API, without your password.
            It can only reach your own folder. Treat it like a password and revoke it when you no longer need it.</p>
        <p class='token-warn' id='token-insecure' hidden><i class='fa-solid fa-triangle-exclamation'></i> This page is not using HTTPS. Tokens sent over plain HTTP can be read by others on the network.</p>
        <table class='table' id='token-table'>
            <thead><tr><th>Name</th><th>Token</th><th>Access</th><th>Expires</th><th>Last used</th><th class='col-actions'></th></tr></thead>
            <tbody id='token-rows'></tbody>
        </table>
    </div>";

            var sb = new StringBuilder();
            sb.Append(pt.GenerateHtmlHeader());
            sb.Append($@"
<div class='page'>
    <header class='page-header'>
        <h1><i class='fa-solid fa-user-gear'></i> My Account</h1>
        <p>Signed in as <b>{HttpUtility.HtmlEncode(me.Username)}</b></p>
    </header>

    <div class='account-grid'>
        <form class='card' id='pw-form' autocomplete='on'>
            <h2><i class='fa-solid fa-key'></i> Change password</h2>
            <input type='text' name='username' value='{HttpUtility.HtmlAttributeEncode(me.Username)}' autocomplete='username' hidden />
            <label class='field'><span>Current password</span><input type='password' id='pw-current' autocomplete='current-password' required /></label>
            <label class='field'><span>New password (min. 8 characters)</span><input type='password' id='pw-new' autocomplete='new-password' minlength='8' required /></label>
            <label class='field'><span>Repeat new password</span><input type='password' id='pw-repeat' autocomplete='new-password' minlength='8' required /></label>
            <div class='form-error' id='pw-error'></div>
            <button type='submit' class='btn btn-primary'><i class='fa-solid fa-floppy-disk'></i><span>Change password</span></button>
        </form>

        <div class='card'>
            <h2><i class='fa-solid fa-shield-halved'></i> My permissions</h2>
            <ul class='perm-list'>{perms}</ul>
        </div>
    </div>
{tokenCard}
</div>");
            sb.Append(pt.GenerateHtmlFooter());
            PageTemplate.WriteHtml(sb.ToString());
        }
    }
}
