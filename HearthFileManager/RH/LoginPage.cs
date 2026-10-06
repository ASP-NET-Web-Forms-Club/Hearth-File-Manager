using System;
using System.Text;
using System.Web;
using HearthFileManager.engine;

namespace HearthFileManager.RH
{
    public class LoginPage
    {
        public static void HandleRequest()
        {
            if (AppSession.IsLoggedIn) { HttpContext.Current.Response.Redirect("/files", false); ApiHelper.EndResponse(); return; }

            var pt = new PageTemplate { Title = "Sign in", BodyClass = "page-login", ShowSidebar = false };
            pt.ExtraFooterText = StaticAsset.Script("/js/login.js");

            var sb = new StringBuilder();
            sb.Append(pt.GenerateHtmlHeader());
            sb.Append(@"
<div class='login-wrap'>
    <form class='login-card' id='login-form' autocomplete='on'>
        <div class='login-brand'>
            <span class='brand-mark'><i class='fa-solid fa-fire-flame-curved'></i></span>
            <h1>Hearth</h1>
            <p>Sign in to manage your website</p>
        </div>
        <label class='field'>
            <span>Username</span>
            <input type='text' name='username' id='login-username' autocomplete='username' required autofocus />
        </label>
        <label class='field'>
            <span>Password</span>
            <input type='password' name='password' id='login-password' autocomplete='current-password' required />
        </label>
        <div class='form-error' id='login-error'></div>
        <button type='submit' class='btn btn-primary btn-block' id='login-submit'><i class='fa-solid fa-right-to-bracket'></i> Sign in</button>
    </form>
</div>");
            sb.Append(pt.GenerateHtmlFooter());
            PageTemplate.WriteHtml(sb.ToString());
        }
    }
}
