using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Grappler.Core
{
    public sealed class ProbeResult
    {
        public string Title { get; set; }
        public string Uploader { get; set; }
        public string Duration { get; set; }
        public string Resolution { get; set; }
        public string ThumbnailUrl { get; set; }

        /// <summary>"Blender Foundation   ·   14:48   ·   1920x1080"</summary>
        public string MetaLine()
        {
            List<string> parts = new List<string>();
            if (!Empty(Uploader)) { parts.Add(Uploader); }
            if (!Empty(Duration)) { parts.Add(Duration); }
            if (!Empty(Resolution)) { parts.Add(Resolution); }
            return string.Join("   ·   ", parts.ToArray());
        }

        // yt-dlp prints the literal "NA" for fields a site does not provide.
        private static bool Empty(string s)
        {
            return string.IsNullOrEmpty(s) || s == "NA" || s == "none";
        }
    }

    public static class YtDlp
    {
        /// <summary>
        /// Machine-readable progress. Fields are @@-separated and the title
        /// goes last, because it is the only one that could plausibly
        /// contain the separator itself.
        /// </summary>
        public const string ProgressTemplate =
            "download:PROG@@%(progress._percent_str)s" +
            "@@%(progress._speed_str)s" +
            "@@%(progress._eta_str)s" +
            "@@%(progress._total_bytes_str,progress._total_bytes_estimate_str)s" +
            "@@%(info.vcodec)s" +
            "@@%(info.title)s";

        // ---------- argument quoting -----------------------------------
        /// <summary>
        /// .NET Framework has no ProcessStartInfo.ArgumentList, so the
        /// command line has to be built by hand - following the exact
        /// backslash/quote rules CommandLineToArgvW uses to take it apart
        /// again. Titles and paths routinely contain spaces and quotes, so
        /// getting this wrong is not a rare edge case.
        /// </summary>
        public static string Quote(string arg)
        {
            if (arg == null) { return "\"\""; }
            if (arg.Length > 0 && arg.IndexOfAny(new char[] { ' ', '\t', '\n', '\v', '"' }) < 0)
            {
                return arg;
            }

            StringBuilder sb = new StringBuilder();
            sb.Append('"');
            for (int i = 0; ; i++)
            {
                int slashes = 0;
                while (i < arg.Length && arg[i] == '\\') { i++; slashes++; }

                if (i == arg.Length)
                {
                    // Trailing backslashes must not escape the closing quote.
                    sb.Append('\\', slashes * 2);
                    break;
                }
                if (arg[i] == '"')
                {
                    sb.Append('\\', slashes * 2 + 1).Append('"');
                }
                else
                {
                    sb.Append('\\', slashes).Append(arg[i]);
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        public static string CommandLine(IList<string> args)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < args.Count; i++)
            {
                if (sb.Length > 0) { sb.Append(' '); }
                sb.Append(Quote(args[i]));
            }
            return sb.ToString();
        }

        // ---------- shared argument blocks ------------------------------
        /// <summary>
        /// YouTube needs a JavaScript runtime to work out its stream URLs.
        /// Without one, the higher-quality formats quietly go missing - so
        /// this is not optional even though nothing errors without it.
        /// </summary>
        public static void AddJsRuntime(List<string> args)
        {
            if (File.Exists(Paths.Deno))
            {
                args.Add("--js-runtimes");
                args.Add("deno:" + Paths.Deno);
            }
        }

        /// <summary>
        /// Sites yt-dlp has no dedicated extractor for (KVS-based ones
        /// especially) increasingly sit behind bot checks that serve a
        /// challenge page instead of the player - which surfaces as
        /// "Unable to extract flashvars". Making the generic extractor
        /// look like a real Chrome gets the actual page. Scoped to
        /// "generic" so YouTube and the other dedicated extractors are
        /// left exactly as they were.
        /// </summary>
        public static void AddImpersonation(List<string> args)
        {
            args.Add("--extractor-args");
            args.Add("generic:impersonate=chrome");
        }

        public static void AddCookies(List<string> args, AppSettings settings)
        {
            AddCookies(args, settings, null);
        }

        /// <summary>
        /// A cookie file handed over by the Brave extension wins over the
        /// browser chosen in Settings: it is the session of the very tab the
        /// link came from, and it needs no browser database decrypting.
        /// </summary>
        public static void AddCookies(List<string> args, AppSettings settings, string cookieFile)
        {
            if (!string.IsNullOrEmpty(cookieFile))
            {
                args.Add("--cookies");
                args.Add(cookieFile);
                return;
            }
            if (settings != null && !string.IsNullOrEmpty(settings.CookieBrowser))
            {
                args.Add("--cookies-from-browser");
                args.Add(settings.CookieBrowser);
            }
        }

        /// <summary>The arguments every real download shares.</summary>
        public static List<string> DownloadBase(AppSettings settings, string cookieFile)
        {
            List<string> args = new List<string>();
            args.Add("--config-location"); args.Add(Paths.ConfFile);
            args.Add("--ffmpeg-location"); args.Add(Paths.BinDir);
            args.Add("-P"); args.Add("home:" + settings.DownloadFolder);
            args.Add("-P"); args.Add("temp:" + settings.TempFolder);
            AddJsRuntime(args);
            AddImpersonation(args);
            AddCookies(args, settings, cookieFile);
            return args;
        }

        public static ProcessStartInfo StartInfo(IList<string> args)
        {
            ProcessStartInfo psi = new ProcessStartInfo(Paths.YtDlp, CommandLine(args));
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;
            psi.WorkingDirectory = Paths.Root;

            // yt-dlp is a frozen Python program, and Python block-buffers
            // stdout whenever it is not attached to a console. Without this
            // the progress arrives in ~8 KB lumps instead of live.
            psi.EnvironmentVariables["PYTHONUNBUFFERED"] = "1";
            return psi;
        }

        // ---------- one-shot capture ------------------------------------
        public static string[] Capture(IList<string> args, int timeoutMs, out int exitCode)
        {
            exitCode = -1;
            List<string> lines = new List<string>();
            try
            {
                using (Process p = Process.Start(StartInfo(args)))
                {
                    // Read stderr on a side thread: a full stderr pipe will
                    // block the child forever if only stdout is drained.
                    System.Threading.Thread drain = new System.Threading.Thread(
                        delegate() { try { p.StandardError.ReadToEnd(); } catch { } });
                    drain.IsBackground = true;
                    drain.Start();

                    string line;
                    while ((line = p.StandardOutput.ReadLine()) != null)
                    {
                        lines.Add(line);
                    }

                    if (!p.WaitForExit(timeoutMs))
                    {
                        ProcessTree.Kill(p);
                        return lines.ToArray();
                    }
                    exitCode = p.ExitCode;
                }
            }
            catch { }
            return lines.ToArray();
        }

        public static string Version()
        {
            if (!File.Exists(Paths.YtDlp)) { return "yt-dlp missing"; }
            int exit;
            string[] lines = Capture(new string[] { "--version" }, 8000, out exit);
            if (lines.Length > 0 && lines[0].Trim().Length > 0)
            {
                return "yt-dlp " + lines[0].Trim();
            }
            return "yt-dlp";
        }

        /// <summary>
        /// One process, five --print lines. Cheaper than asking for the full
        /// JSON dump and avoids needing a JSON parser at all.
        /// </summary>
        public static ProbeResult Probe(string url, AppSettings settings, string cookieFile)
        {
            List<string> args = new List<string>();
            args.Add("--ignore-config");
            args.Add("--no-warnings");
            args.Add("--no-playlist");
            args.Add("--playlist-items"); args.Add("1");
            args.Add("--print"); args.Add("%(title)s");
            args.Add("--print"); args.Add("%(uploader,channel,uploader_id,extractor)s");
            args.Add("--print"); args.Add("%(duration_string,duration)s");
            args.Add("--print"); args.Add("%(resolution)s");
            args.Add("--print"); args.Add("%(thumbnail)s");
            AddJsRuntime(args);
            AddImpersonation(args);
            AddCookies(args, settings, cookieFile);
            args.Add(url);

            int exit;
            string[] lines = Capture(args, 45000, out exit);
            if (exit != 0 || lines.Length == 0) { return null; }

            ProbeResult r = new ProbeResult();
            r.Title = Field(lines, 0);
            r.Uploader = Field(lines, 1);
            // The last fallback in the template is the extractor name, which
            // for unsupported sites is "generic" (or "generic+kvs_fix" with
            // the plugin) - not something to show anyone.
            if (r.Uploader != null && r.Uploader.StartsWith("generic", StringComparison.OrdinalIgnoreCase))
            {
                r.Uploader = null;
            }
            r.Duration = Field(lines, 2);
            r.Resolution = Field(lines, 3);
            r.ThumbnailUrl = Field(lines, 4);
            return r;
        }

        /// <summary>The -F table, for the format picker.</summary>
        public static string[] Formats(string url, AppSettings settings)
        {
            List<string> args = new List<string>();
            args.Add("--ignore-config");
            args.Add("--ffmpeg-location"); args.Add(Paths.BinDir);
            args.Add("--no-playlist");
            args.Add("--color"); args.Add("never");
            args.Add("-F");
            AddJsRuntime(args);
            AddImpersonation(args);
            AddCookies(args, settings);
            args.Add(url);

            int exit;
            return Capture(args, 60000, out exit);
        }

        private static string Field(string[] lines, int index)
        {
            if (index >= lines.Length) { return ""; }
            string value = lines[index].Trim();
            return value == "NA" ? "" : value;
        }
    }

    /// <summary>
    /// yt-dlp spawns ffmpeg as a child. .NET Framework's Process.Kill only
    /// takes the parent, which would leave ffmpeg running and holding the
    /// part-file open, so cancel has to take the whole tree.
    /// </summary>
    public static class ProcessTree
    {
        public static void Kill(Process process)
        {
            if (process == null) { return; }
            int pid;
            try
            {
                if (process.HasExited) { return; }
                pid = process.Id;
            }
            catch { return; }

            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(
                    "taskkill.exe", "/PID " + pid + " /T /F");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (Process killer = Process.Start(psi))
                {
                    killer.WaitForExit(5000);
                }
            }
            catch { }

            try { if (!process.HasExited) { process.Kill(); } }
            catch { }
        }
    }
}
