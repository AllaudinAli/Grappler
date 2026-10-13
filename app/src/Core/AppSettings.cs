using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Grappler.Core
{
    /// <summary>
    /// Settings live in two files on purpose.
    ///
    /// The download folder is read from - and written back to -
    /// config\settings.ps1, because Download.bat reads the same line.
    /// Changing the folder in Grappler therefore changes it for the
    /// terminal menu too, instead of the two quietly disagreeing.
    ///
    /// Everything else is Grappler-only and lives in config\grappler.conf,
    /// in the same one-option-per-line style as yt-dlp.conf. Deliberately
    /// not JSON: .NET Framework has no JSON parser worth referencing here,
    /// and a hand-rolled one would be more code than this whole class.
    /// </summary>
    public sealed class AppSettings
    {
        public string DownloadFolder { get; set; }
        public int MaxParallel { get; set; }
        public int DefaultQuality { get; set; }
        public string CookieBrowser { get; set; }

        public const string CloseAsk = "ask";
        public const string CloseToTray = "tray";
        public const string CloseQuits = "quit";

        /// <summary>What the window's close button does: CloseAsk, CloseToTray or CloseQuits.</summary>
        public string CloseAction { get; set; }

        public AppSettings()
        {
            DownloadFolder = Path.Combine(Paths.Root, "downloads");
            MaxParallel = 3;
            DefaultQuality = 0;
            CookieBrowser = "";
            CloseAction = CloseAsk;
        }

        public string TempFolder
        {
            get { return Path.Combine(DownloadFolder, ".incomplete"); }
        }

        public string ArchiveFile
        {
            get { return Path.Combine(DownloadFolder, ".downloaded-archive.txt"); }
        }

        // ---------- load ------------------------------------------------
        public static AppSettings Load()
        {
            AppSettings s = new AppSettings();

            string folder = ReadDownloadFolder();
            if (!string.IsNullOrEmpty(folder)) { s.DownloadFolder = folder; }

            Dictionary<string, string> conf = ReadConf();
            s.MaxParallel = Clamp(GetInt(conf, "parallel", 3), 1, 8);
            s.DefaultQuality = Clamp(GetInt(conf, "quality", 0), 0, QualityPreset.All.Length - 1);
            string cookies;
            if (conf.TryGetValue("cookies", out cookies)) { s.CookieBrowser = cookies; }
            string onClose;
            if (conf.TryGetValue("on_close", out onClose) &&
                (onClose == CloseToTray || onClose == CloseQuits))
            {
                s.CloseAction = onClose;
            }

            return s;
        }

        // Deliberately not RegexOptions.Compiled: this runs twice in a
        // session, and emitting IL for it costs more startup time than
        // interpreting it ever costs at runtime.
        private static readonly Regex FolderLine = new Regex(
            @"^\s*\$DownloadFolder\s*=\s*(['""])(?<path>.*?)\1\s*$",
            RegexOptions.Multiline);

        private static string ReadDownloadFolder()
        {
            try
            {
                if (!File.Exists(Paths.SettingsPs1)) { return null; }
                Match m = FolderLine.Match(File.ReadAllText(Paths.SettingsPs1));
                return m.Success ? m.Groups["path"].Value : null;
            }
            catch { return null; }
        }

        private static Dictionary<string, string> ReadConf()
        {
            Dictionary<string, string> map =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(Paths.AppConf)) { return map; }
                foreach (string raw in File.ReadAllLines(Paths.AppConf))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#') { continue; }
                    int eq = line.IndexOf('=');
                    if (eq <= 0) { continue; }
                    map[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                }
            }
            catch { }
            return map;
        }

        private static int GetInt(Dictionary<string, string> map, string key, int fallback)
        {
            string text;
            int value;
            if (map.TryGetValue(key, out text) &&
                int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            {
                return value;
            }
            return fallback;
        }

        private static int Clamp(int value, int low, int high)
        {
            if (value < low) { return low; }
            if (value > high) { return high; }
            return value;
        }

        // ---------- save ------------------------------------------------
        public void Save()
        {
            WriteDownloadFolder();
            WriteConf();
        }

        /// <summary>
        /// Rewrites just the one assignment and leaves every comment in
        /// settings.ps1 intact - that file is meant to stay readable and
        /// hand-editable.
        /// </summary>
        private void WriteDownloadFolder()
        {
            try
            {
                // The default is written as '' rather than as a full path,
                // so the shipped settings.ps1 stays unchanged - and free of
                // anyone's own folder names - until a folder is really chosen.
                string folder = string.Equals(
                    Path.GetFullPath(DownloadFolder).TrimEnd('\\'),
                    Path.Combine(Paths.Root, "downloads"),
                    StringComparison.OrdinalIgnoreCase) ? "" : DownloadFolder;
                string quoted = "$DownloadFolder = '" +
                                folder.Replace("'", "''") + "'";

                if (File.Exists(Paths.SettingsPs1))
                {
                    string text = File.ReadAllText(Paths.SettingsPs1);
                    if (FolderLine.IsMatch(text))
                    {
                        File.WriteAllText(Paths.SettingsPs1,
                            FolderLine.Replace(text, quoted.Replace("$", "$$")));
                        return;
                    }
                    File.WriteAllText(Paths.SettingsPs1,
                        text.TrimEnd() + Environment.NewLine + Environment.NewLine + quoted + Environment.NewLine);
                    return;
                }

                File.WriteAllText(Paths.SettingsPs1, quoted + Environment.NewLine);
            }
            catch { }
        }

        private void WriteConf()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("# ==========================================================");
                sb.AppendLine("#  Grappler - app settings");
                sb.AppendLine("# ==========================================================");
                sb.AppendLine("#  Written by the Settings dialog. Safe to edit by hand.");
                sb.AppendLine("#  The download folder is NOT here - it lives in");
                sb.AppendLine("#  settings.ps1, so Download.bat sees the same value.");
                sb.AppendLine();
                sb.AppendLine("# How many downloads run at once (1-8).");
                sb.AppendLine("parallel = " + MaxParallel.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine();
                sb.AppendLine("# Which quality is selected when the app opens.");
                sb.AppendLine("quality = " + DefaultQuality.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine();
                sb.AppendLine("# Borrow cookies from a browser for sign-in / age-restricted");
                sb.AppendLine("# videos. One of: chrome firefox edge brave chromium opera");
                sb.AppendLine("# vivaldi - or blank for none. Close the browser first.");
                sb.AppendLine("cookies = " + (CookieBrowser == null ? "" : CookieBrowser));
                sb.AppendLine();
                sb.AppendLine("# What closing the window does: ask, tray (keep running");
                sb.AppendLine("# in the system tray) or quit.");
                sb.AppendLine("on_close = " + (CloseAction ?? CloseAsk));

                string dir = Path.GetDirectoryName(Paths.AppConf);
                if (!Directory.Exists(dir)) { Directory.CreateDirectory(dir); }
                File.WriteAllText(Paths.AppConf, sb.ToString());
            }
            catch { }
        }

        public void EnsureFolders()
        {
            try
            {
                if (!Directory.Exists(DownloadFolder)) { Directory.CreateDirectory(DownloadFolder); }
                if (!Directory.Exists(TempFolder)) { Directory.CreateDirectory(TempFolder); }
            }
            catch { }
        }
    }
}
