using System;
using System.Web;
using Newtonsoft.Json;

namespace System
{
    public static class ApiHelper
    {
        static HttpResponse Response => HttpContext.Current.Response;
        static HttpRequest Request => HttpContext.Current.Request;

        public static string GetBaseUrl()
        {
            Uri url = Request.Url;
            return $"{url.Scheme}://{url.Host}{(url.IsDefaultPort ? "" : ":" + url.Port)}";
        }

        public static void WriteJson(object obj)
        {
            Response.ContentType = "application/json";
            Response.Write(JsonConvert.SerializeObject(obj));
        }

        public static void WriteSuccess(string message = "Success", object data = null)
        {
            WriteJson(new { success = true, message, data });
        }

        public static void WriteError(string message, int statusCode = 400)
        {
            Response.StatusCode = statusCode;
            WriteJson(new { success = false, message });
        }

        public static void EndResponse()
        {
            Response.TrySkipIisCustomErrors = true;
            try { Response.Flush(); } catch { }
            Response.SuppressContent = true;
            HttpContext.Current.ApplicationInstance.CompleteRequest();
        }
    }
}