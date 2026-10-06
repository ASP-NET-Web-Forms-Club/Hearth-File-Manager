using System.IO;
using System.Web.Hosting;

namespace System
{
    public static class StaticAsset
    {
        public static string Script(string path) => $"<script src='{path}?v={Version(path)}'></script>";
        public static string Css(string path) => $"<link rel='stylesheet' href='{path}?v={Version(path)}' />";

        static string Version(string path)
        {
            string phys = HostingEnvironment.MapPath("~" + path);
            return (phys != null && File.Exists(phys)) ? File.GetLastWriteTimeUtc(phys).ToString("yyyyMMddHHmmss") : "0";
        }
    }
}