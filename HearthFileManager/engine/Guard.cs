using System;
using System.Text;
using System.Web;

namespace HearthFileManager.engine
{
    public class GuardException : Exception
    {
        public int StatusCode { get; }
        public GuardException(string message, int statusCode) : base(message) { StatusCode = statusCode; }
    }

    public static class Guard
    {
        /// <summary>For page handlers: redirects to /login and returns false when not signed in.</summary>
        public static bool PageLoggedIn()
        {
            if (AppSession.IsLoggedIn) return true;
            HttpContext.Current.Response.Redirect("/login", false);
            ApiHelper.EndResponse();
            return false;
        }

        /// <summary>For page handlers: signed in AND has the permission; otherwise renders a "no access" page.</summary>
        public static bool PageRequire(string perm, string activeMenu)
        {
            if (!PageLoggedIn()) return false;
            if (AppSession.LoginUser.Has(perm)) return true;

            HttpContext.Current.Response.StatusCode = 403;
            var pt = new PageTemplate { Title = "No access", BodyClass = "page-noaccess", ActiveMenu = activeMenu };
            var sb = new StringBuilder();
            sb.Append(pt.GenerateHtmlHeader());
            sb.Append(@"
<div class='page'>
    <div class='card empty'>
        <i class='fa-solid fa-lock'></i>
        <h2 style='justify-content:center'>You don't have access to this page</h2>
        <p>Ask a user who can manage users to give you this permission.</p>
        <p><a class='btn' href='/files'><i class='fa-solid fa-folder-open'></i> Back to My Files</a></p>
    </div>
</div>");
            sb.Append(pt.GenerateHtmlFooter());
            PageTemplate.WriteHtml(sb.ToString());
            return false;
        }

        public static void LoggedIn()
        {
            if (!AppSession.IsLoggedIn) throw new GuardException("Your session has ended. Please sign in again.", 401);
        }

        /// <summary>Signed in and holds the permission (403 otherwise).</summary>
        public static void Require(string perm)
        {
            LoggedIn();
            if (!AppSession.LoginUser.Has(perm))
                throw new GuardException("You don't have permission to do this.", 403);
        }

        /// <summary>
        /// Mutating calls must be POST and carry the X-Hearth header set by site.js (Api scope).
        /// Cross-site forms cannot set custom headers, so this blocks CSRF on top of SameSite=Lax.
        /// </summary>
        public static void Mutating()
        {
            LoggedIn();
            HttpRequest req = HttpContext.Current.Request;
            if (req.HttpMethod != "POST" || req.Headers["X-Hearth"] != "1")
                throw new GuardException("Invalid request", 403);
        }

        /// <summary>Mutating call that also needs a permission.</summary>
        public static void Mutating(string perm)
        {
            Mutating();
            Require(perm);
        }
    }
}
