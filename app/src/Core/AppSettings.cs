using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Grappler.Core
{
    /// <summary>
    /// Everything the Settings dialog changes, kept in config\grappler.conf
    /// in the same one-option-per-line style as yt-dlp.conf. Deliberately
    /// not JSON: it is meant to stay readable and hand-editable.
    ///
    /// The file is written on every close and is not part of the
    /// repository, so nobody's own folder names ever end up in it.
    /// </summary>
    public sealed class AppSettings
    {
        public string DownloadFolder { get; set; }
        public int MaxParallel { get; set; }

        /// <summary>Which quality a fetched video starts out with - see PreferredQualities.</summary>
        public int DefaultQuality { get; set; }

        public string CookieBrowser { get; set; }

        public const string CloseAsk = "ask";
        public const string CloseToTray = "tray";
        public const string CloseQuits = "quit";

        /// <summary>What the window's close button does: CloseAsk, CloseToTray or CloseQuits.</summary>
        public string CloseAction { get; set; }

        /// <summary>
        /// Labels for DefaultQuality, in order. FormatChoices.DefaultIndex
        /// maps each onto the closest option a given video really has.
        /// </summary>
        public static readonly string[] PreferredQualities = new string[]
        {
            "Best available", "1080p", "720p", "480p", "Audio - MP3", "Audio - original"
        };

        public AppSettings()
        {
            DownloadFolder = DefaultDownloadFolder;
            MaxParallel = 3;
            DefaultQuality = 0;
            CookieBrowser = "";
            CloseAction = CloseAsk;
        }

        public static string DefaultDownloadFolder
        {
            get { return Path.Combine(Paths.Root, "downloads"); }
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
            Dictionary<string, string> conf = ReadConf();

            string folder;
            if (conf.TryGetValue("folder", out folder) && folder.Length > 0) { s.DownloadFolder = folder; }

            s.MaxParallel = Clamp(GetInt(conf, "parallel", 3), 1, 8);
            s.DefaultQuality = Clamp(GetInt(conf, "quality", 0), 0, PreferredQualities.Length - 1);

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
            try
            {
                // The default folder is stored as blank, so moving the whole
                // Grappler folder keeps downloads next to it.
                string folder = string.Equals(
                    Path.GetFullPath(DownloadFolder).TrimEnd('\\'), DefaultDownloadFolder,
                    StringComparison.OrdinalIgnoreCase) ? "" : DownloadFolder;

                StringBuilder sb = new StringBuilder();
                sb.AppendLine("# ==========================================================");
                sb.AppendLine("#  Grappler - app settings");
                sb.AppendLine("# ==========================================================");
                sb.AppendLine("#  Written by the Settings dialog. Safe to edit by hand.");
                sb.AppendLine();
                sb.AppendLine("# Where finished downloads go. Blank = the downloads folder");
                sb.AppendLine("# next to Grappler.");
                sb.AppendLine("folder = " + folder);
                sb.AppendLine();
                sb.AppendLine("# How many downloads run at once (1-8).");
                sb.AppendLine("parallel = " + MaxParallel.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine();
                sb.AppendLine("# Quality picked for you once a video's details are in:");
                sb.AppendLine("# 0 best, 1 1080p, 2 720p, 3 480p, 4 MP3, 5 original audio.");
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
