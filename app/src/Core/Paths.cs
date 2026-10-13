using System;
using System.IO;

namespace Grappler.Core
{
    /// <summary>
    /// Everything Grappler needs lives inside the project folder. Nothing is
    /// read from PATH, the registry or %APPDATA%, so deleting the folder
    /// really does remove the whole thing.
    /// </summary>
    public static class Paths
    {
        private static readonly string _root = Resolve();

        /// <summary>
        /// Normally the exe sits in the project root next to bin\, but when
        /// it is run straight out of the build folder it does not. Walk up
        /// until bin\yt-dlp.exe turns up.
        /// </summary>
        private static string Resolve()
        {
            DirectoryInfo dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            for (int i = 0; i < 6 && dir != null; i++)
            {
                if (File.Exists(Path.Combine(dir.FullName, "bin\\yt-dlp.exe")))
                {
                    return dir.FullName;
                }
                dir = dir.Parent;
            }
            return AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
        }

        public static string Root      { get { return _root; } }
        public static string BinDir    { get { return Path.Combine(_root, "bin"); } }
        public static string YtDlp     { get { return Path.Combine(_root, "bin\\yt-dlp.exe"); } }
        public static string Ffmpeg    { get { return Path.Combine(_root, "bin\\ffmpeg.exe"); } }
        public static string Deno      { get { return Path.Combine(_root, "bin\\deno.exe"); } }
        public static string ConfFile  { get { return Path.Combine(_root, "config\\yt-dlp.conf"); } }
        public static string ExtensionDir { get { return Path.Combine(_root, "browser-extension"); } }
        public static string HostManifest { get { return Path.Combine(_root, "browser-extension\\host\\com.grappler.host.json"); } }
        public static string AppConf   { get { return Path.Combine(_root, "config\\grappler.conf"); } }

        public static bool IsInstalled
        {
            get { return File.Exists(YtDlp) && File.Exists(ConfFile); }
        }
    }
}
