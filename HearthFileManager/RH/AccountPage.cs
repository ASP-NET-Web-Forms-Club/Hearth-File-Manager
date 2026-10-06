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
            { Perm.Users, "Manage users" }
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
            pt.ExtraFooterText = StaticAsset.Script("/js/account.js");

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
</div>");
            sb.Append(pt.GenerateHtmlFooter());
            PageTemplate.WriteHtml(sb.ToString());
        }
    }
}
